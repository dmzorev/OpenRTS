using System;
using DeclarativeContent;
using Godot;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private System.Collections.Generic.Dictionary<string, GameMap> _setupMaps = new();
    private System.Collections.Generic.Dictionary<string, StartingSet> _setupSets = new();
    private MatchSetupOptions? _configuredOptions;
    private string? _pendingLobbySettings;
    private MatchSetupScreen? _matchSetup;
    private bool _startedFromSetup, _verifyMatchSetup;
    private bool _verifyAiMatch;
    private string? _aiProfileId;
    private SkirmishAiProfile? _opponentProfile;
    private SkirmishAi? _opponent;
    private string[]? _opponentStarts;

    private double _registryRetry;
    private void ListenForGameLists()
    {
        _registryRetry = 5;
        try
        {
            _lanRegistry = new(() => System.Threading.Volatile.Read(ref _advertisement)?.Invoke());
            if (_matchSetup is not null) _matchSetup.LocalRegistry = _lanRegistry;
        }
        catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse) { /* Another local instance serves this machine. */ }
        catch (System.Net.Sockets.SocketException e) { GD.PushWarning("LAN registry: " + e.Message); }
    }

    private void OpenMatchSetup(UnitCatalog catalog)
    {
        GameMap Map(string path) => SceneMapBundle.IsPath(path) ? LoadSceneMap(path, catalog) : GameMap.Load(ReadMapSource(path), catalog);
        StartingSet Set(string path) => StartingSet.Load(ReadMapSource(path));
        var settings = _manifest.Settings.Match;
        var mapChoices = settings.Maps.Length == 0 ? [new MatchChoice { Path = settings.DefaultMap, Title = settings.DefaultMap }] : settings.Maps;
        var setChoices = settings.StartingSets.Length == 0 ? [new MatchChoice { Path = settings.DefaultStartSet, Title = settings.DefaultStartSet }] : settings.StartingSets;
        var availableMaps = new List<(MatchChoice Choice, GameMap Map)>();
        foreach (var choice in mapChoices)
        {
            try { availableMaps.Add((choice, Map(choice.Path))); }
            catch (Exception error) when ((SceneMapBundle.IsPath(choice.Path) || choice.Path.EndsWith(".ortmap.json", StringComparison.OrdinalIgnoreCase)) &&
                error is IOException or InvalidDataException or ContentException or System.Text.Json.JsonException)
            {
                GD.PushWarning($"Карта {choice.Title} недоступна. {error.Message}");
            }
        }
        if (_networkVerifyMapPath is { } verificationPath)
        {
            var map = Map(verificationPath);
            availableMaps.RemoveAll(item => item.Map.Id == map.Id);
            availableMaps.Add((new MatchChoice { Path = verificationPath, Title = map.Id }, map));
            _networkVerifyMapId = map.Id;
        }
        if (availableMaps.Count == 0) throw new InvalidDataException("Нет доступных карт матча.");
        var maps = availableMaps.Select(item => (item.Choice.Title, item.Choice.Description, item.Map)).ToArray();
        if (maps.Any(choice => !choice.Item3.IsSkirmish)) throw new ArgumentException("Экран настройки принимает только skirmish-карты.");
        var modes = setChoices.Select(choice => (choice.Title, Set(choice.Path))).ToArray();
        _setupMaps = maps.ToDictionary(m => m.Item3.Id, m => m.Item3);
        _setupSets = modes.ToDictionary(m => m.Item2.Id, m => m.Item2);
        try { _networkAdvertiser ??= new OpenRTS.Network.LanGames(() => System.Threading.Volatile.Read(ref _advertisement)?.Invoke()); }
        catch (System.Net.Sockets.SocketException error) { GD.PushWarning("LAN discovery: " + error.Message); }
        ListenForGameLists();
        _matchSetup = new() { LocalRegistry = _lanRegistry };
        AddChild(_matchSetup);
        _matchSetup.Build(TerminalTheme(), catalog.Locomotion!, maps, modes,
            availableMaps.FindIndex(item => item.Choice.Path == settings.DefaultMap),
            Array.FindIndex(setChoices, choice => choice.Path == settings.DefaultStartSet), _manifest.Settings.Simulation.SpeedPerTick, _fogHoldTicks, catalog.Summary.Fingerprint);
        _matchSetup.Launch += BeginMatchFromSetup;
        _matchSetup.ReplayRequested += BeginReplay;
        _matchSetup.ConfigurationChanged += () => _pendingLobbySettings = _matchSetup.Options.Serialize();
        _matchSetup.CancelNetwork += () => { if (_session?.Network is not null || _session?.Replay is not null) MatchSetupFailed(new OperationCanceledException("Подключение отменено."), true); };
        if (_verifyReplayPath is not null) { _ = VerifyReplayFlow(); }
        else if (_openReplayPath is not null) { _matchSetup.OpenReplayPage(); BeginReplay(_openReplayPath); }
        else if (_verifyMenu) { _ = VerifyMenuFlow(); }
        else if (_verifyNetwork) SelectNetworkVerificationMatch();
        else if (_verifyAiMatch) SelectAiVerificationMatch();
        else if (_verifySceneMapMenu) SelectSceneMapVerification();
        else if (_verifyMatchSetup) SelectVerificationMatch();
    }

    private void BeginMatchFromSetup(GameMap map, StartingSet set, string[] slots, bool aiOpponent)
    {
        if (_session is not null) return;
        try
        {
            _networkMode = _matchSetup!.NetworkMode;
            if (_networkMode != 0 && slots.Length < 2)
                throw new ArgumentException("Прямая игра требует хотя бы двух игровых сторон.");
            _networkAddress = _matchSetup.NetworkAddress; _networkPort = _matchSetup.NetworkPort;
            _networkPin = _matchSetup.NetworkPin; _networkDelay = _matchSetup.NetworkInputDelay;
            _networkPlayer = _matchSetup.NetworkPlayer; _networkAiPlayers = _matchSetup.NetworkAiPlayers;
            _fogViewer = _networkMode == 2 ? _networkPlayer : 1u;
            _matchFogMode = _matchSetup!.SelectedFog;
            _fogStyle = _matchSetup.SelectedFogStyle;
            _radarStyle = _matchSetup.SelectedRadarStyle;
            _matchPlayers = slots.Length;
            _configuredOptions = _matchSetup.Options;
            _mapMatch = PrepareMap(_configuredOptions);
            bool canPlanAi = aiOpponent || _networkMode != 0;
            _opponentProfile = canPlanAi ? SkirmishAiCatalog.Load(ReadMapSource(_manifest.Settings.Match.AiConfig), _mapMatch.Map.Catalog, _aiProfileId ?? _manifest.Settings.Match.AiProfile) : null;
            _opponentStarts = canPlanAi ? slots : null;
            _count = _mapMatch.UnitCount;
            _matchSetup!.SetLaunching(true, "Подготовка поля боя…");
            StartSession();
        }
        catch (Exception error) when (error is ContentException or ArgumentException or InvalidDataException or IOException or System.Security.Cryptography.CryptographicException or OpenRTS.Network.MatchDirectoryException)
        {
            MatchSetupFailed(error, canRetry: true);
        }
    }

    private void UpdateMatchSetup()
    {
        if (_session is null) return;
        if (_session.Completion.IsCompleted)
        {
            try { _session.Completion.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                MatchSetupFailed(error, canRetry: true);
                return;
            }
        }

        if (_session.Network is { } network)
        {
            if (network.IsHost && _pendingLobbySettings is { } pending)
            {
                try { network.ConfigureLobby(pending, MatchSetupOptions.Parse(pending).Seats.Select(s => s.Controller).ToArray()); _pendingLobbySettings = null; }
                catch (InvalidOperationException) { }
            }
            if (_publications.Count != 0) _matchSetup!.ShowDirectoryPublication(string.Join("\n", _publications.Select(p => p.Publication.Status).Distinct()));
            _matchSetup!.SetNetworkStatus(NetworkStatusText(network), network.Fingerprint);
            _matchSetup.UpdateNetworkLobby(network);
            if (_verifyNetwork && network.State.Match != Guid.Empty && network.State.Phase == OpenRTS.Network.MatchPhase.Lobby)
            {
                VerifyDirectoryCapacities(network);
                if (network.Player != 0 && _networkReadyRevision != network.State.Revision) { network.Ready(network.State.Revision); _networkReadyRevision = network.State.Revision; }
                if (network.IsHost && network.State.Spectators == _networkVerifySpectators && network.State.Slots.All(p => p.Controller != OpenRTS.Network.SlotController.Human || p.Connected && p.Ready)) network.Start();
            }
        }
        using var frame = _session.Frames.AcquireLatest();
        if (frame is null) return;
        try
        {
            if (_session.Network is { } joined)
            {
                _configuredOptions = MatchSetupOptions.Parse(joined.LobbySettings);
                _mapMatch = PrepareMap(_configuredOptions); _matchFogMode = (OpenRTS.Core.Visibility.FogMode)_configuredOptions.Fog;
                _fogViewer = joined.Player; _networkPlayer = joined.Player;
                _opponentStarts = _configuredOptions.PlayerSlots(_mapMatch.Map);
            }
            _startedFromSetup = true;
            BuildBattleScene(_mapMatch?.Map.Catalog ?? _catalog!);
            AddNetworkStatus(); AddReplayControls();
            _focusStartingBase = true;
            _matchSetup!.Hide();
            _matchSetup.QueueFree();
            _matchSetup = null;
            _session.SetPlayback(_playbackSpeed, _paused || _verifyMatchSetup);
        }
        catch (Exception error)
        {
            MatchSetupFailed(error, canRetry: false);
        }
    }

    private MapMatch PrepareMap(MatchSetupOptions options)
    {
        if (!_setupMaps.TryGetValue(options.Map, out var map))
            throw new InvalidDataException($"Не установлена версия карты '{options.Map}' с отпечатком {options.MapFingerprint}.");
        if (!_setupSets.TryGetValue(options.Mode, out var set))
            throw new InvalidDataException($"Не установлена версия стартового набора '{options.Mode}' с отпечатком {options.StartingSetFingerprint}.");
        return options.Prepare(map, set);
    }
    private SimulationWorld CreateConfiguredWorld(MapMatch map, MatchSetupOptions options)
    {
        return options.CreateWorld(map, _workers, _networkMode != 0 || options.Local || options.Seats.Any(s => s.Controller == OpenRTS.Network.SlotController.Ai));
    }

    private void MatchSetupFailed(Exception error, bool canRetry)
    {
        _session?.Dispose();
        ClosePublications();
        System.Threading.Volatile.Write(ref _advertisement, null);
        _session = null;
        _pendingLobbySettings = null;
        _networkMode = 0;
        _mapMatch = null;
        _opponentProfile = null;
        _opponent = null;
        _opponentStarts = null;
        bool verification = _verifyMatchSetup || _verifyNetwork || _verifyReplayPath is not null;
        if (verification || !canRetry) GD.PushError(error.ToString());
        if (verification) GetTree().Quit(1);
        else _matchSetup?.SetLaunching(!canRetry, "Матч не запущен. " + error.Message);
    }
}
