using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenRTS.Network;

public sealed record LanGame(string Address, int Port, string Title, string Map, int Players, int OpenSlots, int Spectators,
    string Fingerprint, uint[]? AvailablePlayers = null, bool PasswordRequired = false, Guid Match = default, int Capacity = 4,
    int Protocol = StarSession.ProtocolVersion, string? Rules = null, string? Content = null, MatchPhase Phase = MatchPhase.Lobby);

/// <summary>Discovery only. A reply describes the responding host's own match.</summary>
public sealed class LanGames : IDisposable
{
    public static LanGame? Advertisement(StarSession host, int port, string title, Func<string> map)
    {
        var state = host.State;
        if (!host.IsHost || host.Completion.IsCompleted || state.Phase == MatchPhase.Finished) return null;
        var available = state.Slots.Where(s => s.Controller == SlotController.Human && !s.Connected).Select(s => s.Player).ToArray();
        return new("", port, title, map(), state.Slots.Count(s => s.Connected), available.Length, state.Spectators,
            host.Fingerprint, available, host.PasswordRequired, state.Match, state.Slots.Count, StarSession.ProtocolVersion,
            host.Descriptor.Rules, host.Descriptor.ContentHash, state.Phase);
    }
    public const int DiscoveryPort = 17772;
    private static readonly byte[] Query = Encoding.ASCII.GetBytes("OpenRTS-LAN-1");
    private static readonly byte[] Empty = Encoding.ASCII.GetBytes("OpenRTS-LAN-EMPTY");
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receive, _query;
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private readonly ConcurrentDictionary<IPEndPoint, (LanGame Game, long Seen)> _games = new();
    private readonly ConcurrentDictionary<IPAddress, long> _idle = new();
    public IPAddress[] IdlePeers => _idle.Where(p => Stopwatch.GetElapsedTime(p.Value).TotalSeconds < 6).Select(p => p.Key).ToArray();
    public LanRegistry.Entry[] Entries => _games.Values.Select(g => new LanRegistry.Entry(g.Game,
        6000 - (int)Math.Min(6000, Stopwatch.GetElapsedTime(g.Seen).TotalMilliseconds), false)).Where(e => e.RemainingMs > 0).ToArray();
    public IReadOnlyList<LanGame> Games => _games.Values.Where(g => Stopwatch.GetElapsedTime(g.Seen).TotalSeconds < 6)
        .OrderBy(g => g.Game.Title, StringComparer.Ordinal).Select(g => g.Game).ToArray();

    public LanGames(Func<LanGame?>? advertise = null, IPAddress? destination = null)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            if (advertise is not null) _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, advertise is null ? 0 : DiscoveryPort));
        }
        catch { _udp.Dispose(); throw; }
        _receive = Task.Run(async () => {
            long rateAt = Stopwatch.GetTimestamp(); int replies = 0;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    if (advertise is not null)
                    {
                        if (!packet.Buffer.AsSpan().SequenceEqual(Query)) continue;
                        if (Stopwatch.GetElapsedTime(rateAt).TotalSeconds >= 1) { rateAt = Stopwatch.GetTimestamp(); replies = 0; }
                        if (++replies > 32) continue;
                        byte[] data = advertise() is { } game ? JsonSerializer.SerializeToUtf8Bytes(game) : Empty;
                        if (data.Length <= 2048) await _udp.SendAsync(data, packet.RemoteEndPoint, _stop.Token);
                    }
                    else if (packet.RemoteEndPoint.Port == DiscoveryPort && packet.Buffer.AsSpan().SequenceEqual(Empty))
                    {
                        foreach (var old in _idle.Where(p => Stopwatch.GetElapsedTime(p.Value).TotalSeconds >= 6)) _idle.TryRemove(old.Key, out _);
                        if (_idle.Count < 64 || _idle.ContainsKey(packet.RemoteEndPoint.Address)) _idle[packet.RemoteEndPoint.Address] = Stopwatch.GetTimestamp();
                    }
                    else if (packet.RemoteEndPoint.Port == DiscoveryPort && Decode(packet) is { } game)
                    {
                        foreach (var old in _games.Where(g => Stopwatch.GetElapsedTime(g.Value.Seen).TotalSeconds >= 6)) _games.TryRemove(old.Key, out _);
                        var key = new IPEndPoint(packet.RemoteEndPoint.Address, game.Port);
                        if (_games.Count < 64 || _games.ContainsKey(key)) _games[key] = (game, Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                catch (SocketException) { await Task.Delay(100); }
                catch (InvalidDataException) { /* Other UDP applications can use the same port. */ }
            }
        });
        _query = advertise is not null ? Task.CompletedTask : Task.Run(async () => {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (var address in destination is null ? BroadcastAddresses() : [destination])
                    {
                        try { await _udp.SendAsync(Query, new IPEndPoint(address, DiscoveryPort), _stop.Token); }
                        catch (SocketException) { /* One inactive interface must not stop the other searches. */ }
                    }
                    await _refresh.WaitAsync(1500, _stop.Token);
                }
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
    }

    public void Refresh() { if (_refresh.CurrentCount == 0) { try { _refresh.Release(); } catch (SemaphoreFullException) { } } }

    private static IPAddress[] BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                         n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                foreach (var entry in iface.GetIPProperties().UnicastAddresses.Where(e => e.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    byte[] host = entry.Address.GetAddressBytes(), mask = entry.IPv4Mask.GetAddressBytes();
                    if (entry.PrefixLength is <= 0 or >= 31) continue;
                    for (int i = 0; i < host.Length; i++) host[i] |= (byte)~mask[i];
                    addresses.Add(new IPAddress(host));
                }
        }
        catch (NetworkInformationException) { /* Limited broadcast is still available. */ }
        return addresses.ToArray();
    }

    // A connected UDP socket accepts replies only from the requested address and discovery port.
    public static async Task<LanGame?> QueryAsync(string host, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, deadline.Token);
        if (addresses.Length == 0) throw new IOException("Нет IPv4-адреса для поиска LAN.");
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(addresses[0], DiscoveryPort);
        await udp.SendAsync(Query, deadline.Token);
        return Decode(await udp.ReceiveAsync(deadline.Token));
    }

    private static LanGame? Decode(UdpReceiveResult packet)
    {
        if (packet.Buffer.AsSpan().SequenceEqual(Empty)) return null;
        if (packet.Buffer.Length > 2048) throw new InvalidDataException();
        LanGame? game;
        try { game = JsonSerializer.Deserialize<LanGame>(packet.Buffer); }
        catch (JsonException e) { throw new InvalidDataException("Неизвестный ответ LAN.", e); }
        Validate(game);
        return game! with { Address = packet.RemoteEndPoint.Address.ToString() };
    }
    internal static void Validate(LanGame? game)
    {
        if (game is null || game.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(game.Title) || string.IsNullOrWhiteSpace(game.Map) ||
            game.Title.Length > 96 || game.Map.Length > 96 || game.Players is < 1 or > 8 || game.OpenSlots is < 0 or > 7 ||
            game.Spectators is < 0 or > 8 || game.Capacity is < 2 or > 8 || game.Rules?.Length > 128 || game.Content?.Length > 128 ||
            !Enum.IsDefined(game.Phase) || game.Phase == MatchPhase.Finished || game.Fingerprint?.Length != 64 || !game.Fingerprint.All(Uri.IsHexDigit) ||
            game.AvailablePlayers is { } available && (available.Length > 7 || available.Any(p => p is < 2 or > 8)))
            throw new InvalidDataException("Некорректная запись LAN.");
    }

    public void Dispose()
    {
        _stop.Cancel(); _udp.Dispose();
        try { Task.WhenAll(_receive, _query).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _refresh.Dispose(); _stop.Dispose();
    }
}
