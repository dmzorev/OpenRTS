using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Network;
using YamlDotNet.Serialization;

public sealed partial class MatchSetupScreen
{
    public sealed class DiscoverySettings
    {
        public string[] Servers { get; set; } = [];
        public string? AdvertiseHost { get; set; }
    }
    private DiscoverySettings _discovery = new();
    private readonly LineEdit _source = new() { Name = "ServerAddress", MaxLength = 256,
        PlaceholderText = "IP или имя сервера", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _directoryStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _publicationStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private sealed record SearchResult(DirectoryMatch[] Games, LanRegistry.Entry[] Lan, string Status)
    {
        private readonly long _received = Stopwatch.GetTimestamp();
        public IEnumerable<LanRegistry.Entry> LiveEntries => Lan.Select(e => e with {
            RemainingMs = e.RemainingMs - (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(_received).TotalMilliseconds)
        }).Where(e => e.RemainingMs > 0);
    }
    private SearchResult[] _results = [];
    private SearchResult[] _nearby = [];
    public MatchEndpoint[]? SelectedEndpoints { get; private set; }
    public bool RequestSeat { get; private set; }
    private string _directoryContent = "";
    private CancellationTokenSource? _directoryCancellation;
    internal LanRegistry? LocalRegistry { get; set; }
    private bool _working;
    private double _directoryRefresh;
    public string? AdvertiseHost => _discovery.AdvertiseHost;
    // NetworkLobby owns fresh publication clients independently of the browser lifetime.
    public (string Url, string? Peer)[] PublicationSources()
    {
        var sources = new List<(string, string?)>();
        foreach (var source in SourceAddresses())
        {
            try { sources.Add((CatalogueUrl(source), Uri.CheckHostName(source) != UriHostNameType.Unknown ? source : null)); }
            catch (ArgumentException) { /* The browser displays the address error. LAN remains available. */ }
        }
        return sources.Distinct().ToArray();
    }
    public void ShowDirectoryPublication(string status) => _publicationStatus.Text = status;
    private void LoadSources()
    {
        string path = Godot.FileAccess.FileExists("user://network.yaml") ? "user://network.yaml" : "res://network.yaml";
        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read) ?? throw new IOException("Файл недоступен.");
            var settings = new DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<DiscoverySettings>(file.GetAsText());
            if (settings?.Servers is null || settings.Servers.Length > 16 || settings.Servers.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256))
                throw new InvalidDataException("Ожидается список Servers до 16 адресов.");
            _discovery = settings;
        }
        catch (Exception e) { _directoryStatus.Text = "network.yaml · " + e.Message; }
    }
    private string[] SourceAddresses() => _discovery.Servers.Append(_source.Text).Select(s => s.Trim())
        .Where(s => s.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string CatalogueUrl(string source)
    {
        if (!source.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate("https://" + source, UriKind.Absolute, out var uri)) throw new ArgumentException("Введите IP или имя сервера.");
            source = uri.IsDefaultPort ? new UriBuilder(uri) { Port = 17773 }.Uri.AbsoluteUri : uri.AbsoluteUri;
        }
        return MatchDirectoryClient.ServiceAddress(source).AbsoluteUri;
    }

    private async Task<SearchResult> Search(string source, CancellationToken ct)
    {
        LanRegistry.Entry[] lan = [];
        if (Uri.CheckHostName(source) != UriHostNameType.Unknown)
        {
            try
            {
                lan = await LanRegistry.FindAsync(source, ct);
                return new([], lan, $"{source} · связь установлена · игр {lan.Length}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is SocketException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException) { }
        }
        try
        {
            using var client = new MatchDirectoryClient(CatalogueUrl(source), "Игрок");
            var games = await client.FindAsync(MatchSetupOptions.Rules, _directoryContent, ct);
            return new(games, lan, $"{source} · связь установлена · игр {games.Length + lan.Length}");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new([], lan, $"{source} · нет связи ({e.Message})");
        }
    }

    private Action? _pendingBrowserAction;
    private async void RunBrowser(Func<CancellationToken, Task> action)
    {
        if (_working) { _directoryCancellation?.Cancel(); _pendingBrowserAction = () => RunBrowser(action); return; }
        _working = true;
        using var cancel = new CancellationTokenSource();
        _directoryCancellation = cancel;
        try { await action(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e) { if (!cancel.IsCancellationRequested) _directoryStatus.Text = e.Message; }
        finally
        {
            _directoryCancellation = null; _working = false; _directoryRefresh = 8;
            var pending = _pendingBrowserAction; _pendingBrowserAction = null; pending?.Invoke();
        }
    }

    private void BrowseDirectory()
    {
        var sources = SourceAddresses(); _browser?.Refresh();
        if (sources.Length == 0) { _directoryCancellation?.Cancel(); _pendingBrowserAction = null; _results = []; _directoryStatus.Text = ""; RefreshGames(); return; }
        RunBrowser(async ct => {
            _directoryStatus.Text = "Поиск игр…";
            var results = await Task.WhenAll(sources.Select(s => Search(s, ct))); ct.ThrowIfCancellationRequested();
            foreach (var result in results)
            {
                LocalRegistry?.Remember(result.LiveEntries);
                foreach (var match in result.Games)
                {
                    var game = match.Game;
                    var endpoint = game.Endpoints.FirstOrDefault(e => e.Kind == MatchEndpointKind.Direct);
                    if (endpoint is null || !game.HostAdmission || game.Private) continue;
                    int ttl = (int)Math.Clamp((match.Expires - DateTimeOffset.UtcNow).TotalMilliseconds, 0, 12000);
                    LocalRegistry?.Remember([new(new(endpoint.Address, endpoint.Port, game.Title, game.Map, game.Players,
                        game.AvailablePlayers.Length, game.Spectators, game.Fingerprint, game.AvailablePlayers, game.PasswordRequired,
                        game.Match, game.PlayerCount, game.Protocol, game.Rules, game.ContentHash, game.Phase), ttl, false)]);
                }
            }
            _results = results; _directoryStatus.Text = string.Join("\n", results.Select(r => r.Status));
            RefreshGames();
        });
    }
    private void FindLocalGames()
    {
        if (_browser is not { } browser) return;
        RunBrowser(async ct => {
            _directoryStatus.Text = "Поиск в локальной сети…"; browser.Refresh();
            await Task.Delay(400, ct);
            var addresses = browser.IdlePeers.Select(ip => ip.ToString()).Concat(browser.Games.Select(g => g.Address))
                .Distinct().Take(32).ToArray();
            var results = new List<SearchResult>();
            foreach (var batch in addresses.Chunk(8))
            {
                var replies = await Task.WhenAll(batch.Select(async address => {
                    try { return await LanRegistry.FindAsync(address, ct); }
                    catch (Exception e) when (!ct.IsCancellationRequested && e is SocketException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException) { return []; }
                }));
                ct.ThrowIfCancellationRequested();
                foreach (var entries in replies)
                {
                    LocalRegistry?.Remember(entries);
                    results.Add(new([], entries, ""));
                }
            }
            _nearby = results.ToArray();
            _directoryStatus.Text = addresses.Length == 0 ? "Клиенты OpenRTS пока не ответили" : $"Клиенты OpenRTS на связи: {addresses.Length}";
            RefreshGames();
        });
    }
    private void CreateNetworkGame()
    {
        _pendingBrowserAction = null; _directoryCancellation?.Cancel();
        _publicationStatus.Text = "";
        _joinPassword = _roomPassword.Text; SelectedEndpoints = null; RequestSeat = false;
        BeginNetworkLobby(true);
    }

    private void AddDirectoryGames()
    {
        foreach (var match in _results.SelectMany(r => r.Games).Where(m => m.Expires > DateTimeOffset.UtcNow).OrderByDescending(m => m.Game.Revision).DistinctBy(m => m.Game.Match))
        {
            var game = match.Game;
            bool supported = game.PlayerCount == 4;
            AddGameRow(game.Title, game.Map, game.Players, game.AvailablePlayers.Length - match.ReservedPlayers, game.Spectators, game.PasswordRequired,
                supported ? PhaseLabel(game.Phase) : "Для этой игры нужен клиент на " + game.PlayerCount + " сторон",
                supported && game.HostAdmission && game.Phase == MatchPhase.Lobby, supported && game.HostAdmission && game.Phase is MatchPhase.Lobby or MatchPhase.Paused,
                watch => {
                    var local = _browser?.Games.FirstOrDefault(g => g.Match == game.Match && g.Fingerprint == game.Fingerprint);
                    SelectedEndpoints = local is null ? game.Endpoints : game.Endpoints.Prepend(new(MatchEndpointKind.Direct, local.Address, local.Port)).Distinct().Take(3).ToArray();
                    var endpoint = SelectedEndpoints.OrderBy(e => e.Kind).First();
                    EnterLobby(endpoint.Address, endpoint.Port, game.Fingerprint, watch ? 0u : 2u);
                },
                supported ? "DirectoryJoinMatch" : "DirectoryUnsupportedJoin" + game.PlayerCount,
                supported ? "DirectoryWatchMatch" : "DirectoryUnsupportedWatch" + game.PlayerCount);
        }
    }
}
