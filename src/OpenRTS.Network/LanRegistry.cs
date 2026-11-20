using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenRTS.Network;

/// <summary>A small directory on the discovery port (TCP). Only original registrations may be copied one hop.</summary>
public sealed class LanRegistry : IDisposable
{
    private const int Lifetime = 12_000, Limit = 32;
    public sealed record Entry(LanGame Game, int RemainingMs, bool Copy);
    private sealed record Stored(LanGame Game, long Until, bool Copy);
    private readonly Dictionary<(string Address, int Port), Stored> _entries = [];
    private readonly TcpListener _listener;
    private readonly Func<LanGame?> _ownGame;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public LanRegistry(Func<LanGame?> ownGame, int port = LanGames.DiscoveryPort)
    {
        _ownGame = ownGame;
        _listener = new(IPAddress.Any, port);
        _listener.Start(16);
        _worker = Task.Run(Serve);
    }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Remember(IEnumerable<Entry> entries)
    {
        lock (_entries)
            foreach (var entry in entries)
                if (!entry.Copy)
                {
                    var game = entry.Game;
                    if (IPAddress.TryParse(game.Address, out var ip) && IPAddress.IsLoopback(ip)) game = game with { Address = "" };
                    Store(game, entry.RemainingMs, true);
                }
    }
    private void Prune()
    {
        long now = Environment.TickCount64;
        foreach (var pair in _entries.Where(p => p.Value.Until <= now).ToArray()) _entries.Remove(pair.Key);
    }
    private bool Store(LanGame game, int ttl, bool copy)
    {
        Prune();
        var key = (game.Address, game.Port);
        if (_entries.TryGetValue(key, out var previous))
        {
            if (copy && !previous.Copy) return false;
        }
        else if (_entries.Count >= Limit) return false;
        _entries[key] = new(game, Environment.TickCount64 + Math.Clamp(ttl, 0, Lifetime), copy);
        return true;
    }
    public IEnumerable<LanGame> LocalGames => Snapshot().Select(e =>
        e.Game.Address.Length == 0 ? e.Game with { Address = IPAddress.Loopback.ToString() } : e.Game);
    private Entry[] Snapshot()
    {
        lock (_entries)
        {
            Prune();
            var games = new List<Entry>();
            var own = _ownGame();
            if (own is not null) games.Add(new(own with { Address = "" }, Lifetime, false));
            long now = Environment.TickCount64;
            foreach (var stored in _entries.Values)
                if (stored.Game.Match != own?.Match && stored.Until > now)
                    games.Add(new(stored.Game, (int)(stored.Until - now), stored.Copy));
            return games.ToArray();
        }
    }
    private async Task Serve()
    {
        var active = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                active.RemoveAll(task => task.IsCompletedSuccessfully);
                if (active.Count == 8) { var done = await Task.WhenAny(active); await done; active.Remove(done); }
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                active.Add(Reply(client));
            }
        }
        catch (Exception e) when (_stop.IsCancellationRequested && e is SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { await Task.WhenAll(active); }
    }
    private static bool NetworkFailure(Exception error) => error is SocketException or IOException or InvalidDataException
        or OperationCanceledException or JsonException or ObjectDisposedException;
    private async Task Reply(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var stream = client.GetStream(); var ct = deadline.Token;
                var request = await Wire.Receive(stream, ct, 2048);
                if (request.AsSpan().SequenceEqual("OpenRTS-list-1"u8))
                    await Wire.Send(stream, JsonSerializer.SerializeToUtf8Bytes(Snapshot()), ct);
                else if (request.AsSpan().StartsWith("OpenRTS-register-1 "u8))
                {
                    var game = JsonSerializer.Deserialize<LanGame>(request.AsSpan("OpenRTS-register-1 "u8.Length));
                    LanGames.Validate(game);
                    var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    bool stored;
                    lock (_entries) stored = Store(game! with { Address = IPAddress.IsLoopback(address) ? "" : address.ToString() }, Lifetime, false);
                    await Wire.Send(stream, stored ? "OK"u8.ToArray() : "FULL"u8.ToArray(), ct);
                }
            }
            catch (Exception e) when (NetworkFailure(e)) { }
        }
    }

    private static async Task<(byte[] Reply, IPAddress Address)> Request(string host, int port, byte[] payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(host, port, deadline.Token);
        var stream = client.GetStream(); await Wire.Send(stream, payload, deadline.Token);
        return (await Wire.Receive(stream, deadline.Token), ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
    }
    public static async Task<Entry[]> FindAsync(string host, CancellationToken ct, int port = LanGames.DiscoveryPort)
    {
        var (bytes, address) = await Request(host, port, "OpenRTS-list-1"u8.ToArray(), ct);
        var entries = JsonSerializer.Deserialize<Entry[]>(bytes);
        if (entries is null || entries.Length > Limit + 1) throw new InvalidDataException("Некорректный список игр.");
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry is null || entry.RemainingMs is < 0 or > Lifetime) throw new InvalidDataException("Некорректный срок объявления.");
            LanGames.Validate(entry.Game);
            string ip = entry.Game.Address;
            if (ip == "") ip = address.ToString();
            else if (string.IsNullOrWhiteSpace(ip) || Uri.CheckHostName(ip) == UriHostNameType.Unknown ||
                     IPAddress.TryParse(ip, out var remote) && (remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any) ||
                     IPAddress.IsLoopback(remote) && !IPAddress.IsLoopback(address))) throw new InvalidDataException("Некорректный адрес игры.");
            entries[i] = entry with { Game = entry.Game with { Address = ip } };
        }
        return entries;
    }
    public static async Task RegisterAsync(string host, LanGame game, CancellationToken ct, int port = LanGames.DiscoveryPort)
    {
        byte[] payload = [.. "OpenRTS-register-1 "u8, .. JsonSerializer.SerializeToUtf8Bytes(game)];
        var (reply, _) = await Request(host, port, payload, ct);
        if (!reply.AsSpan().SequenceEqual("OK"u8)) throw new InvalidDataException("Регистрация отклонена.");
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop();
        try { _worker.GetAwaiter().GetResult(); } finally { _stop.Dispose(); }
    }
}
