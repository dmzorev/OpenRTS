using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Direct client-simulated match with a player-host coordinator. World access belongs to one owner thread.</summary>
public sealed partial class StarSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _owner;
    private readonly ConcurrentQueue<WorldCommand> _input = new();
    private readonly ConcurrentQueue<byte[]> _requests = new();
    private readonly ConcurrentQueue<WorldEvent> _events = new();
    private readonly ConcurrentQueue<MatchChat> _chat = new();
    private readonly TaskCompletionSource<SimulationReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<SimulationWorld, uint, IReadOnlyList<WorldCommand>>? _planAi;
    private readonly TimeSpan _timeout;
    private MatchState _state;
    private int _pending, _requestCount, _eventCount, _disposed;
    private long _ticket, _timestamp, _tick;
    private readonly object _chatGate = new();
    private long _chatAt;
    private int _chatSent;
    private NetworkTickSample? _statistics;
    private string _status = "Подключение…";
    public bool IsHost { get; }
    private uint _player;
    public uint Player => Volatile.Read(ref _player);
    private readonly bool _requestSeat;
    public FrameExchange Frames { get; } = new();
    public Task<SimulationReport> Completion => _completion.Task;
    public MatchState State => Volatile.Read(ref _state);
    public string Status => Waiting is { } wait
        ? $"Ожидание игроков {string.Join(", ", wait.Players)} · тик {wait.Tick} · {(int)wait.Milliseconds} мс"
        : Volatile.Read(ref _status);
    private NetworkWaitStatus? _waiting;
    private IReadOnlyList<NetworkPeerLatency> _latencies = Array.AsReadOnly(Array.Empty<NetworkPeerLatency>());
    public NetworkWaitStatus? Waiting => Volatile.Read(ref _waiting);
    public IReadOnlyList<NetworkPeerLatency> Latencies => Volatile.Read(ref _latencies);
    public string Fingerprint { get; }
    public string JournalPath { get; }
    public MatchDescriptor Descriptor => Volatile.Read(ref _descriptor);
    public NetworkTickSample? Statistics => Volatile.Read(ref _statistics);

    public StarSession(bool host, string address, int port, string? fingerprint, MatchDescriptor descriptor,
        Func<SimulationWorld> createWorld, string journalPath, int playerCount = 2, uint player = 2,
        IReadOnlyList<uint>? aiPlayers = null, Func<SimulationWorld, uint, IReadOnlyList<WorldCommand>>? planAi = null,
        TimeSpan? timeout = null, string lobbySettings = "",
        Func<string, (MatchDescriptor Descriptor, SimulationWorld World)>? prepareSettings = null,
        IReadOnlyList<uint>? closedPlayers = null, MatchResumeCredential? resumeCredential = null, MatchAdmissionPolicy? admissionPolicy = null,
        MatchJoinGrant? admission = null, DirectoryIdentity? accountIdentity = null, IReadOnlyList<MatchEndpoint>? endpoints = null, string password = "", bool requestSeat = false)
    {
        if (password.Length > 64 || password.Any(char.IsControl)) throw new ArgumentException("Пароль матча — до 64 символов.");
        if (requestSeat && (host || player == 0 || resumeCredential is not null || admission is not null))
            throw new ArgumentException("Новое место запрашивает только подключающийся игрок.");
        _requestSeat = requestSeat;
        _password = password;
        playerCount = admission?.PlayerCount ?? playerCount;
        if (port is < 1 or > 65535 || playerCount is < 2 or > 8 || !host && player != 0 && (player < 2 || player > playerCount))
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Address is required.");
        _ = DirectMatch.Descriptor(descriptor);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout < TimeSpan.FromMilliseconds(100) || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (closedPlayers?.Any(p => p < 2 || p > playerCount || aiPlayers?.Contains(p) == true) == true) throw new ArgumentException("Invalid closed slots.");
        var ai = aiPlayers?.ToHashSet() ?? [];
        if (ai.Any(p => p < 2 || p > playerCount) || host && ai.Count != 0 && planAi is null)
            throw new ArgumentException("AI slots require a host policy and valid non-host players.");
        if (resumeCredential is not null && (host || resumeCredential.Player != player || resumeCredential.Match == Guid.Empty ||
            resumeCredential.Epoch < 1 || resumeCredential.Token.Length != 64 || !resumeCredential.Token.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid match resume credential.");
        if (host && admission is not null || !host && admissionPolicy is not null || admission is not null &&
            (accountIdentity is null || admission.Player != player || admission.Fingerprint != (fingerprint ?? "").Replace(":", "").Replace(" ", "").ToUpperInvariant() ||
             admission.Expires <= DateTimeOffset.UtcNow || admission.Endpoints is null || admission.Endpoints.Length is < 1 or > 3 || resumeCredential is not null))
            throw new ArgumentException("Invalid catalogue admission.");
        if (endpoints is not null && (host || endpoints.Count is < 1 or > 3)) throw new ArgumentException("Invalid endpoints.");
        _connectionEndpoints = (admission?.Endpoints ?? endpoints?.ToArray())?.ToArray();
        if (_connectionEndpoints is not null) foreach (var endpoint in _connectionEndpoints) DirectoryValidation.Endpoint(endpoint);
        if (admission is not null) foreach (var endpoint in admission.Endpoints) DirectoryValidation.Endpoint(endpoint);
        _admissionGate = admissionPolicy is null ? null : new(admissionPolicy); _joinGrant = admission; _accountIdentity = accountIdentity;
        _resumeCredential = resumeCredential;
        IsHost = host; _player = host ? 1u : player; _descriptor = descriptor; _prepare = prepareSettings; _settings = lobbySettings;
        if (prepareSettings is not null) ValidateSettings(lobbySettings); JournalPath = journalPath; _planAi = planAi;
        _state = new(host ? Guid.NewGuid() : Guid.Empty, 1, 0, MatchPhase.Lobby,
            Enumerable.Range(1, playerCount).Select(p => new MatchSlot((uint)p, closedPlayers?.Contains((uint)p) == true ? SlotController.None : ai.Contains((uint)p) ? SlotController.Ai : SlotController.Human, host && p == 1, false)), "Лобби");
        X509Certificate2? certificate = null;
        if (host)
        {
            certificate = MatchCertificate.Create();
            Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        else
        {
            Fingerprint = (fingerprint ?? "").Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Нужен SHA-256 отпечаток хоста.");
        }
        _owner = new Thread(() => Run(address, port, certificate, createWorld)) { IsBackground = true, Name = "OpenRTS star owner" };
        _owner.Start();
    }
    private void Run(string address, int port, X509Certificate2? certificate, Func<SimulationWorld> createWorld)
    {
        try
        {
            SimulationReport report;
            using (certificate)
            using (var world = createWorld())
            {
                if (world.Tick != 0 || world.TickRate != Descriptor.TickRate) throw new ArgumentException("Expected matching tick-zero world.");
                world.RestrictCommandsToPlayers(); _ownedWorld = world; _initialHash = world.Report().StateHash;
                report = IsHost ? RunHost(address, port, certificate!, world) : RunClient(address, port, world, createWorld);
            }
            _ownedWorld?.Dispose();
            Volatile.Write(ref _status, "Матч завершён"); _completion.TrySetResult(report);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _completion.TrySetCanceled(_stop.Token); }
        catch (Exception e) { Volatile.Write(ref _status, e.Message); _completion.TrySetException(e); }
        finally { _ownedWorld?.Dispose(); }
    }
    public void Ready(long revision) { RequirePlayer(); Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Ready, revision)); }
    public void Start() { RequireHost(); Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Start, State.Revision)); }
    public void Pause(bool paused) { RequirePlayer(); Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Pause, State.Revision, enabled: paused)); }
    public void Leave() => Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Leave, State.Revision));
    public void Stop() { RequireHost(); Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Stop, State.Revision)); }
    public void SetAi(uint player, bool enabled)
    {
        RequireHost();
        if (player < 2 || player > State.Slots.Count || enabled && _planAi is null) throw new ArgumentException("Invalid AI slot/policy.");
        Enqueue(StarProtocol.RequestFrame(StarProtocol.Operation.Ai, State.Revision, player, enabled));
    }
    public void SendChat(string text)
    {
        byte[] frame = StarProtocol.ChatFrame(Player, text);
        lock (_chatGate)
        {
            if (Stopwatch.GetElapsedTime(_chatAt).TotalSeconds >= 1) { _chatAt = Stopwatch.GetTimestamp(); _chatSent = 0; }
            if (_chatSent >= 4) throw new InvalidOperationException("Чат: не более четырёх сообщений в секунду.");
            Enqueue(frame); _chatSent++;
        }
    }
    public bool TryReadChat(out MatchChat message) => _chat.TryDequeue(out message!);
    private void RequirePlayer() { if (Player == 0) throw new InvalidOperationException("Spectators cannot control the match."); }
    private void RequireHost() { if (!IsHost) throw new InvalidOperationException("Only the player-host coordinates this operation."); }
    private void Enqueue(byte[] frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Completion.IsCompleted) throw new InvalidOperationException("Матч завершён.");
        if (Interlocked.Increment(ref _requestCount) > 32) { Interlocked.Decrement(ref _requestCount); throw new InvalidOperationException("Control queue full."); }
        _requests.Enqueue(frame);
    }
    public long Submit(WorldCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var state = State; RequirePlayer();
        if (Completion.IsCompleted || state.Phase is MatchPhase.Lobby or MatchPhase.Finished ||
            state.Slots[(int)Player - 1].Controller != SlotController.Human) throw new InvalidOperationException("Player is not active.");
        _ = Wire.Batch(Guid.Empty, Player, 1, [command.WithStamp(1, Player, 1)]);
        if (Interlocked.Increment(ref _pending) > 256) { Interlocked.Decrement(ref _pending); throw new InvalidOperationException("Input queue full."); }
        _input.Enqueue(command); return Interlocked.Increment(ref _ticket);
    }
    private WorldCommand[] Collect()
    {
        var commands = new List<WorldCommand>(); int count = Volatile.Read(ref _pending), recipients = 0;
        while (commands.Count < count && _input.TryPeek(out var next))
        {
            int size = next.Recipients?.Count ?? 0;
            if (recipients + size > 32768 || !_input.TryDequeue(out next)) break;
            Interlocked.Decrement(ref _pending); recipients += size; commands.Add(next);
        }
        return commands.ToArray();
    }
    private void Publish(SimulationWorld world)
    {
        if (Frames.TryPublish(world)) { Interlocked.Exchange(ref _tick, world.Tick); Interlocked.Exchange(ref _timestamp, Stopwatch.GetTimestamp()); }
        foreach (var e in world.Events)
        {
            if (Interlocked.Increment(ref _eventCount) > 65536) throw new InvalidOperationException("Event backlog exceeded.");
            _events.Enqueue(e);
        }
    }
    public bool TryReadEvent(out WorldEvent e)
    {
        if (!_events.TryDequeue(out e)) return false;
        Interlocked.Decrement(ref _eventCount); return true;
    }
    public float GetInterpolationAlpha(long tick) => State.Phase != MatchPhase.Playing || tick != Interlocked.Read(ref _tick) ? 1 :
        (float)Math.Clamp(Stopwatch.GetElapsedTime(Interlocked.Read(ref _timestamp)).TotalSeconds * Descriptor.TickRate, 0, 1);
    private void Chat(MatchChat message)
    {
        _chat.Enqueue(message);
        while (_chat.Count > 64) _chat.TryDequeue(out _);
    }
    private void SetState(MatchState state)
    {
        Volatile.Write(ref _waiting, null);
        Volatile.Write(ref _state, state);
        Volatile.Write(ref _status, state.Phase switch {
            MatchPhase.Lobby => "Лобби · " + string.Join(" · ", state.Slots.Select(p => $"{p.Player}: {(p.Controller == SlotController.Ai ? "ИИ" : p.Controller == SlotController.None ? "нет ввода" : !p.Connected ? "ожидание" : p.Ready ? "готов" : "подключён" )}")),
            MatchPhase.Paused => "Общая пауза · " + state.Reason,
            MatchPhase.Finished => "Матч завершён", _ => "Игра" });
    }
    private void Idle() { if (_stop.Token.WaitHandle.WaitOne(5)) _stop.Token.ThrowIfCancellationRequested(); }
    private static bool LinkFailure(Exception e) => e is IOException or TimeoutException or SocketException && e is not (InvalidDataException or DesyncException);
    private static bool PeerFailure(Exception e) => LinkFailure(e) || e is InvalidDataException or ArgumentException;
    private static async Task Authenticate(SslStream tls, X509Certificate2? certificate, CancellationToken ct)
    {
        if (certificate is not null) await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
            ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
        else await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "openrts.local", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (!_owner.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Network owner did not stop.");
        _stop.Dispose();
    }
}
