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

/// <summary>Live two-player TLS session; UI reads frames and submits bounded intents, never owns the world.</summary>
public sealed class DirectSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _owner;
    private readonly ConcurrentQueue<WorldCommand> _input = new();
    private readonly ConcurrentQueue<WorldEvent> _events = new();
    private readonly TaskCompletionSource<SimulationReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending, _eventCount, _leave, _disposed;
    private long _sequence, _timestamp, _tick;
    private NetworkTickSample? _statistics;
    public NetworkTickSample? Statistics => Volatile.Read(ref _statistics);
    private string _status = "Подключение…";
    public uint Player { get; }
    public FrameExchange Frames { get; } = new();
    public Task<SimulationReport> Completion => _completion.Task;
    public string Status => Volatile.Read(ref _status);
    public string Fingerprint { get; }
    public string JournalPath { get; }
    public MatchDescriptor Descriptor { get; }
    private readonly int _tickRate;

    public DirectSession(bool host, string address, int port, string? fingerprint, MatchDescriptor descriptor,
        Func<SimulationWorld> createWorld, string journalPath)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (descriptor.EndTick != 0) throw new ArgumentException("Live sessions use EndTick=0.");
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Address is required.");
        _ = DirectMatch.Descriptor(descriptor);
        X509Certificate2? certificate = null;
        if (host)
        {
            certificate = MatchCertificate.Create();
            Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        else
        {
            Fingerprint = (fingerprint ?? "").Replace(":", "").Replace(" ", "").ToUpperInvariant();
            if (Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Нужен SHA-256 отпечаток сертификата хоста (64 hex символа).");
        }
        Descriptor = descriptor;
        Player = host ? 1u : 2u; _tickRate = descriptor.TickRate; JournalPath = journalPath;
        _owner = new Thread(() => Run(host, address, port, certificate, descriptor, createWorld)) { IsBackground = true, Name = "OpenRTS network owner" };
        _owner.Start();
    }
    private void Run(bool host, string address, int port, X509Certificate2? certificate,
        MatchDescriptor descriptor, Func<SimulationWorld> createWorld)
    {
        try
        {
            using var cert = certificate;
            using var socket = new TcpClient();
            TcpClient peer = socket;
            SimulationReport report;
            using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connectDeadline.CancelAfter(TimeSpan.FromMinutes(1));
                if (host)
                {
                    var listener = new TcpListener(IPAddress.Parse(address), port);
                    listener.Start(); Volatile.Write(ref _status, "Ожидание подключения. Передайте сопернику отпечаток сертификата.");
                    try { peer = listener.AcceptTcpClientAsync(connectDeadline.Token).AsTask().GetAwaiter().GetResult(); }
                    finally { listener.Stop(); }
                }
                else socket.ConnectAsync(address, port, connectDeadline.Token).AsTask().GetAwaiter().GetResult();
                using var accepted = peer;
                using var tls = new SslStream(peer.GetStream(), false, host ? null : (_, remote, _, _) =>
                    remote is not null && Convert.ToHexString(SHA256.HashData(remote.GetRawCertData())) == Fingerprint);
                Volatile.Write(ref _status, "Проверка TLS…");
                if (host) tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, connectDeadline.Token).GetAwaiter().GetResult();
                else tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "openrts.local",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, connectDeadline.Token).GetAwaiter().GetResult();
                using var journal = File.Create(JournalPath);
                // Socket/TLS/world lifetime stays on this owner. The connect deadline does not limit the match.
                report = DirectMatch.Run(peer, Player, descriptor, createWorld, Collect, journal, _stop.Token,
                    afterTick: Publish, realtime: true, status: text => Volatile.Write(ref _status, text), transport: tls,
                    leaveRequested: () => Volatile.Read(ref _leave) != 0, observe: sample => Volatile.Write(ref _statistics, sample));
            }
            Volatile.Write(ref _status, "Матч завершён"); _completion.TrySetResult(report);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _completion.TrySetCanceled(_stop.Token); }
        catch (Exception e) { Volatile.Write(ref _status, e.Message); _completion.TrySetException(e); }
    }
    public long Submit(WorldCommand command)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Completion.IsCompleted || Volatile.Read(ref _leave) != 0) throw new InvalidOperationException("Матч завершён.");
        _ = Wire.Batch(Guid.Empty, Player, 1, [command.WithStamp(1, Player, 1)]);
        if (Interlocked.Increment(ref _pending) > 256) { Interlocked.Decrement(ref _pending); throw new InvalidOperationException("Очередь сетевого ввода заполнена."); }
        _input.Enqueue(command); return Interlocked.Increment(ref _sequence);
    }
    private IReadOnlyList<WorldCommand> Collect(long tick)
    {
        var commands = new List<WorldCommand>(); int count = Volatile.Read(ref _pending), recipients = 0;
        while (commands.Count < count && _input.TryPeek(out var next))
        {
            int size = next.Recipients?.Count ?? 0;
            if (recipients + size > 32768) break;
            if (!_input.TryDequeue(out next)) break;
            Interlocked.Decrement(ref _pending); recipients += size; commands.Add(next);
        }
        return commands;
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
    public float GetInterpolationAlpha(long tick) => tick != Interlocked.Read(ref _tick) ? 1 :
        (float)Math.Clamp((Stopwatch.GetTimestamp() - Interlocked.Read(ref _timestamp)) * (double)_tickRate / Stopwatch.Frequency, 0, 1);
    public void Leave() => Interlocked.Exchange(ref _leave, 1);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (!_owner.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Network owner did not stop.");
        _stop.Dispose();
    }
}
