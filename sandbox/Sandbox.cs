using System;
using System.Diagnostics;
using System.Text.Json;
using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Controls;

public partial class Sandbox : Node3D
{
    internal const string PlaytestReadyLine = "OPENRTS_PLAYTEST_READY";
    private bool _managedPlaytest, _playtestReady;
    private Task<int>? _playtestStop;
    private SandboxSession? _session;
    private bool _networkEnded;
    private bool _fogScenario;
    private bool _sensorScenario;
    private bool _jammingScenario;
    private bool _sensorVisibility;
    private ContactDisplayStyle _radarStyle = ContactDisplayStyle.BuildingModels;
    private bool _verifySensors;
    private int _fogHoldTicks;
    private OpenRTS.Core.Visibility.FogMode _matchFogMode = OpenRTS.Core.Visibility.FogMode.Visible;
    private FogDisplayStyle _fogStyle = FogDisplayStyle.Circles;
    private bool PlayerFog => _fogScenario || _mapMatch is not null &&
        (_sensorVisibility || _matchFogMode != OpenRTS.Core.Visibility.FogMode.Visible);
    private uint _fogViewer = 1;
    private FogWorldView? _fogView;
    private UnitCatalog? _catalog;
    private MapMatch? _mapMatch;
    private WorldView? _view;
    private SpriteWorldView? _sprites;
    private CombatEffectsView? _combatEffects;
    private bool _showcase, _verifyPresentation;
    private bool _verifyActions, _productionScenario;
    private readonly SelectionState _selection = new();
    private EntityId _selected { get => _selection.Primary; set => _selection.Change(value.Value == 0 ? [] : [value], SelectionChange.Replace); }
    private EntityPose[] _displayPoses = [];
    private int _displayCount;
    private MultiMeshInstance3D? _selectionRing;
    private string? _capturePath;
    private Camera3D? _camera;
    private Label? _label;
    private int _count = 10000, _ticks = DiagnosticScenario.DefaultTicks;
    private bool _verifySimulation, _pacedVerification, _verificationDone;
    private bool _verifyControls, _verifyRtsControls;
    private bool _buildings, _verifyBuildings, _skirmish, _verifySkirmish;
    private bool _navigation, _reverseJobs, _leftGoal;
    private int _workers = 4;
    private ObstacleView? _obstacles;
    private NavigationGrid? _displayGrid;
    private string _notice = "";
    private float _baseCameraSize, _mapWidth, _mapDepth;
    private double _playbackSpeed = 1;
    private bool _paused;

    public override void _Ready()
    {
        bool verify = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--verify");
        _managedPlaytest = OS.GetCmdlineUserArgs().Contains("--managed-playtest");
        bool checkBundles = false;
        try
        {
            string? mapPath = null, startSetPath = null, modPath = null;
            LocalMapPlaytest? localPlaytest = null;
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--jamming") { _jammingScenario = _fogScenario = _buildings = _showcase = true; continue; }
                if (arg == "--verify") continue;
                if (arg == "--ramps") { _ramps = _showcase = true; continue; }
                if (arg == "--verify-ramps") { _verifyRamps = _paused = true; continue; }
                if (arg.StartsWith("--lan-test=", StringComparison.Ordinal)) { _networkVerifyLan = arg[11..]; continue; }
                if (arg.StartsWith("--directory-test=", StringComparison.Ordinal)) { _networkVerifyDirectoryUrl = arg[17..]; continue; }
                if (arg.StartsWith("--verify-network=", StringComparison.Ordinal)) { _verifyNetwork = true; _networkVerifyRole = arg[17..]; continue; }
                if (arg.StartsWith("--network-port=", StringComparison.Ordinal)) { _networkVerifyPort = int.Parse(arg[15..]); continue; }
                if (arg.StartsWith("--network-map=", StringComparison.Ordinal)) { _networkVerifyMapPath = arg[14..]; continue; }
                if (arg.StartsWith("--network-pin-file=", StringComparison.Ordinal)) { _networkVerifyPinFile = arg[19..]; continue; }
                if (arg.StartsWith("--verify-replay=", StringComparison.Ordinal)) { _verifyReplayPath = arg[16..]; continue; }
                if (arg.StartsWith("--replay=", StringComparison.Ordinal)) { _openReplayPath = arg[9..]; continue; }
                if (arg.StartsWith("--replay-map=", StringComparison.Ordinal)) { _replayMapPath = arg[13..]; continue; }
                if (arg.StartsWith("--replay-start-set=", StringComparison.Ordinal)) { _replaySetPath = arg[19..]; continue; }
                if (arg == "--record-playtest") { _recordPlaytest = true; continue; }
                if (arg == "--managed-playtest") continue;
                if (arg == "--verify-menu") { _verifyMenu = true; continue; }
                if (arg.StartsWith("--menu-captures=", StringComparison.Ordinal)) { _menuCaptures = arg[16..]; continue; }
                if (arg == "--network-net3") { _networkVerifyNet3 = true; continue; }
                if (arg == "--network-reconnect") { _networkVerifyReconnect = true; continue; }
                if (arg.StartsWith("--network-spectators=", StringComparison.Ordinal)) { _networkVerifySpectators = int.Parse(arg["--network-spectators=".Length..]); if (_networkVerifySpectators is < 0 or > 1) throw new ArgumentException("Verification supports at most one spectator."); continue; }
                if (arg.StartsWith("--network-ui-delay-ms=", StringComparison.Ordinal)) { _networkVerifyUiDelay = int.Parse(arg["--network-ui-delay-ms=".Length..]); if (_networkVerifyUiDelay is < 0 or > 10000) throw new ArgumentException("Invalid verification UI delay."); continue; }
                if (arg.StartsWith("--mod=", StringComparison.Ordinal) && arg.Length > 6) { modPath = arg[6..]; continue; }
                if (arg.StartsWith("--fog-hold=", StringComparison.Ordinal) && int.TryParse(arg[11..], out int hold) && hold is >= 0 and <= 250) { _fogHoldTicks = hold; continue; }
                if (arg == "--verify-fog-match") { _verifyFogMatch = _verifyMatchSetup = true; continue; }
                if (arg == "--verify-fog") { _verifyFog = _fogScenario = _showcase = _verifySimulation = true; _fogStyle = FogDisplayStyle.Compare; _ticks = 160; continue; }
                if (arg == "--fog") { _fogScenario = _showcase = true; _fogStyle = FogDisplayStyle.Compare; continue; }
                if (arg is "--sensors" or "--verify-sensors")
                {
                    _sensorScenario = _fogScenario = _showcase = true;
                    _fogStyle = FogDisplayStyle.Compare;
                    if (arg == "--verify-sensors") { _verifySensors = _verifySimulation = true; _ticks = 6; }
                    continue;
                }
                if (arg == "--aviation") { mapPath = AviationScenario.Map; continue; }
                if (arg == "--verify-aviation") { _verifyAviation = _verifySimulation = true; mapPath = AviationScenario.Map; _ticks = AviationScenario.Ticks; continue; }
                if (arg == "--verify-actions") { _verifyActions = _buildings = _showcase = _paused = true; continue; }
                if (arg == "--verify-production") { _productionScenario = _buildings = _showcase = _verifySimulation = true; _ticks = 160; continue; }
                if (arg.StartsWith("--map=", StringComparison.Ordinal) && arg.Length > 6) { mapPath = arg[6..]; continue; }
                if (arg.StartsWith("--local-playtest=", StringComparison.Ordinal))
                {
                    localPlaytest = LocalMapPlaytest.Read(arg["--local-playtest=".Length..]);
                    continue;
                }
                if (arg.StartsWith("--start-set=", StringComparison.Ordinal) && arg.Length > 12) { startSetPath = arg[12..]; continue; }
                if (arg == "--skirmish") { _skirmish = _buildings = _showcase = true; continue; }
                if (arg == "--check-scene-maps") { checkBundles = true; continue; }
                if (arg == "--verify-initial-properties") { _verifyInitialProperties = _verifySceneMapMenu = _verifyMatchSetup = true; continue; }
                if (arg == "--verify-scene-map-menu") { _verifySceneMapMenu = _verifyMatchSetup = true; continue; }
                if (arg == "--verify-match-setup") { _verifyMatchSetup = true; continue; }
                if (arg == "--verify-ai-match") { _verifyAiMatch = _verifyMatchSetup = true; continue; }
                if (arg.StartsWith("--ai-profile=", StringComparison.Ordinal) && arg.Length > 13) { _aiProfileId = arg[13..]; continue; }
                if (arg == "--verify-skirmish") { _verifySkirmish = _skirmish = _buildings = _showcase = true; _paused = true; continue; }
                if (arg == "--buildings") { _buildings = _showcase = true; continue; }
                if (arg == "--verify-buildings") { _verifyBuildings = _buildings = _showcase = true; _verifySimulation = true; _ticks = BuildingScenario.VerificationTicks; continue; }
                if (arg == "--showcase") { _showcase = true; continue; }
                if (arg == "--verify-presentation") { _verifyPresentation = _showcase = true; continue; }
                if (arg.StartsWith("--capture=", StringComparison.Ordinal)) { _capturePath = arg[10..]; continue; }
                if (arg == "--verify-simulation") { _verifySimulation = true; continue; }
                if (arg == "--verify-rts-controls") { _verifyRtsControls = _verifyControls = _showcase = true; _paused = true; continue; }
                if (arg == "--verify-controls") { _verifyControls = true; _paused = true; continue; }
                if (arg == "--realtime") { _pacedVerification = true; continue; }
                if (arg == "--navigation") { _navigation = true; continue; }
                if (arg == "--reverse-jobs") { _reverseJobs = true; continue; }
                if (arg.StartsWith("--count=", StringComparison.Ordinal) && int.TryParse(arg[8..], out int value) && value is >= 1 and <= 1000000) { _count = value; continue; }
                if (arg.StartsWith("--ticks=", StringComparison.Ordinal) && int.TryParse(arg[8..], out value) && value is >= 1 and <= 1000000) { _ticks = value; continue; }
                if (arg.StartsWith("--fps=", StringComparison.Ordinal) && int.TryParse(arg[6..], out value) && value is >= 1 and <= 240) { Engine.MaxFps = value; continue; }
                if (arg.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(arg[10..], out value) && value is >= 1 and <= 64) { _workers = value; continue; }
                throw new ArgumentException("Параметры: --ramps, --verify-ramps, --mod=path, --sensors, --fog, --fog-hold=0..250, --skirmish, --verify-match-setup, --verify-ai-match, --ai-profile=id, --map=path, --start-set=path, --buildings, --verify-buildings, --verify-actions, --verify-production, --showcase, --verify-presentation, --capture=path, --verify, --verify-simulation, --verify-controls, --realtime, --navigation, --workers=1..64, --reverse-jobs, --count=1..1000000 (лимит диагностического стенда), --ticks=1..1000000, --fps=1..240.");
            }
            if (_verifyRamps && mapPath is null) _ramps = _showcase = true;
            bool replayRequested = _openReplayPath is not null || _verifyReplayPath is not null;
            if (replayRequested && (mapPath is not null || _skirmish || _verifySimulation) ||
                !replayRequested && (_replayMapPath is not null || _replaySetPath is not null))
                throw new ArgumentException("Выберите повтор и при необходимости укажите --replay-map и --replay-start-set.");
            if ((_recordPlaytest || _managedPlaytest) && localPlaytest is null)
                throw new ArgumentException("Запуск playtest требует проверенных параметров из редактора.");
            if (_managedPlaytest) _playtestStop = Task.Run(() =>
            {
                using var input = Console.OpenStandardInput();
                return input.ReadByte();
            });
            if (_networkVerifyMapPath is not null && !_verifyNetwork)
                throw new ArgumentException("--network-map is a network verification option.");
            if (localPlaytest is not null && (mapPath is null || startSetPath is not null || _skirmish || mapPath == AviationScenario.Map))
                throw new ArgumentException("Local playtest requires its compiled map and uses its validated starting set.");
            if (mapPath is not null && (_showcase || _navigation || _verifyControls || verify))
                throw new ArgumentException("--map выбирает отдельный сценарий.");
            if (_ramps && (_buildings || _skirmish || _navigation || _fogScenario || _verifySimulation || _verifyControls || verify))
                throw new ArgumentException("--ramps выбирает отдельный полигон.");
            if (mapPath is not null) _buildings = _showcase = true;
            bool defaultSkirmish = !_navigation && !_verifySimulation && !_verifyControls && !verify && !_showcase &&
                !Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--count=", StringComparison.Ordinal));
            bool showSetup = defaultSkirmish && startSetPath is null;
            if (_aiProfileId is not null && !showSetup)
                throw new ArgumentException("--ai-profile требует запуск через экран настройки.");
            if (_verifyMatchSetup && !showSetup)
                throw new ArgumentException("--verify-match-setup требует запуск через экран настройки.");
            if (defaultSkirmish) _skirmish = _buildings = _showcase = true;
            if (_showcase && _navigation) throw new ArgumentException("Полигон и диагностическая навигация — отдельные режимы.");
            if (_showcase && _count != ShowcaseScenario.UnitCount && Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--count=", StringComparison.Ordinal)))
                throw new ArgumentException("Полигон содержит 10 юнитов; для нагрузок используйте --navigation --count=... .");
            if (startSetPath is not null && (mapPath is null && !_skirmish || _verifySkirmish))
                throw new ArgumentException("--start-set доступен при запуске skirmish.");
            if (_verifyBuildings) { _verifySimulation = true; _ticks = BuildingScenario.VerificationTicks; }
            if (_verifyPresentation) { _verifySimulation = true; _ticks = 12; }
            if ((verify ? 1 : 0) + (_verifySimulation ? 1 : 0) + (_verifyControls ? 1 : 0) > 1) throw new ArgumentException("Выберите один режим проверки.");
            if ((mapPath ?? _replayMapPath) is { } bundleMap && SceneMapBundle.IsPath(bundleMap))
            {
                string bundlePath = Path.IsPathRooted(bundleMap) || bundleMap.StartsWith("res://", StringComparison.Ordinal) ? bundleMap : "res://" + bundleMap;
                modPath ??= SceneMapBundle.Read(ReadFile(bundlePath)).Mod;
            }
            _activeManifestPath = modPath ?? (_buildings || _ramps || checkBundles ? "res://game.yaml" : "res://diagnostic-game.yaml");
            _manifest = LoadManifest(_activeManifestPath);
            LoadRangeDefaults(_manifest.Settings.SensorRanges);
            var unitSources = _manifest.Rules.Select(source =>
            {
                string text = source.Text;
                if (_sensorScenario) text = FogScenario.AddSensorProfiles(text);
                if (_fogScenario && !_jammingScenario) text = FogScenario.AddVision(text);
                return source with { Text = text };
            });
            _catalog = _ramps ? LoadRampCatalog() : _manifest.LoadUnits(unitSources, _sensorScenario ?
                _manifest.Weapons.Select(source => source with { Text = FogScenario.AddRadarWeapons(source.Text) }) : null);
            _installedCatalog = _catalog;
            if (_ramps) _rampScenario = new(_catalog);
            _sensorVisibility = _catalog.Units.Any(unit => unit.Sensors is not null);
            if (_verifySkirmish) _sensorVisibility = false; // This scripted laboratory controls and inspects both players.
            if (checkBundles)
            {
                CheckInstalledSceneMaps(_catalog);
                GetTree().Quit();
                return;
            }
            if (showSetup)
            {
                OpenMatchSetup(_catalog);
                return;
            }
            if (_skirmish || mapPath is not null)
            {
                string selectedPath = mapPath ?? _manifest.Settings.Match.DefaultMap;
                if (localPlaytest is not null)
                {
                    GameMap playable;
                    if (SceneMapBundle.IsPath(selectedPath)) playable = LoadSceneMap(selectedPath, _catalog, localPlaytest);
                    else
                    {
                        var compiled = MapCompiler.Read(ReadMapSource(selectedPath), _catalog);
                        localPlaytest.CheckMap(compiled);
                        playable = compiled.Map;
                    }
                    _mapMatch = localPlaytest.Prepare(playable, _manifest);
                }
                else _mapMatch = SceneMapBundle.IsPath(selectedPath)
                    ? MapScenario.PrepareDefault(LoadSceneMap(selectedPath, _catalog), ReadMapSource, startSetPath, _manifest.Settings.Match.DefaultStartSet)
                    : mapPath == AviationScenario.Map ? AviationScenario.Load(_catalog, ReadMapSource) : MapScenario.Load(_catalog, ReadMapSource, mapPath ?? _manifest.Settings.Match.DefaultMap, startSetPath, _manifest.Settings.Match.DefaultStartSet);
                if (_sensorScenario) _matchFogMode = OpenRTS.Core.Visibility.FogMode.Hidden;
                if (_recordPlaytest) BindLocalRecording(localPlaytest!);
                _skirmish = _mapMatch.Map.IsSkirmish || mapPath == AviationScenario.Map;
                if (_mapMatch.Map.Grid.HasSurface && !_skirmish) _buildings = false;
                if (_verifyRamps && !_mapMatch.Map.Grid.HasSlopedSurface)
                    throw new ArgumentException("--verify-ramps requires a reference map with ramps.");
            }
            if (_showcase) _count = _mapMatch?.UnitCount ?? (_ramps ? RampScenario.UnitCount : _buildings ? BuildingScenario.UnitCount : ShowcaseScenario.UnitCount);
            if (verify)
            {
                var preview = PreviewRenderer.Create(_catalog.Units[0], _count);
                AddChild(preview);
                VerifyPreview(preview, _count);
                GD.Print("OPENRTS_CONTENT:" + JsonSerializer.Serialize(_catalog.Summary));
                GD.Print($"OPENRTS_PREVIEW:{_count}");
                GetTree().Quit();
            }
            else
            {
                BuildBattleScene(_mapMatch?.Map.Catalog ?? _catalog);
                StartSession();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            if (_managedPlaytest || checkBundles || _verifyMenu || _verifyNetwork || _verifyReplayPath is not null || verify || _verifySimulation || _verifyControls || _verifySkirmish || _verifyActions || _verifyMatchSetup || _verifyRamps) GetTree().Quit(1);
            else AddOverlay("Ошибка загрузки контента\n" + error.Message);
        }
    }

    private void BuildBattleScene(UnitCatalog catalog)
    {
        switch (_manifest.Settings.Presentation.Renderer)
        {
            case GameRenderer.Godot3D:
                BuildGodot3DBattleScene(catalog);
                break;
            default:
                throw new InvalidOperationException($"Unsupported renderer: {_manifest.Settings.Presentation.Renderer}.");
        }
    }

    private void BuildGodot3DBattleScene(UnitCatalog catalog)
    {
        _catalog = catalog;
        if (_showcase)
        {
            _modelCatalog = _manifest.LoadModels();
            _sprites = new(catalog, _modelCatalog, TeamColor);
            _combatEffects = new(catalog, EffectCatalog.Load(_manifest.Effects), _modelCatalog);
            AddChild(_combatEffects.Node);
            var entry = InstantiateMapScene(_mapMatch?.Map);
            Texture2D TextureFor(string id) => entry is not null && entry.VisualTextures.TryGetValue(id, out var texture) && texture is not null
                ? texture : GD.Load<Texture2D>("res://resources/terrain/" + id + ".tres");
            var map = _mapMatch?.Map;
            var grid = _rampScenario?.Grid ?? map?.Grid;
            if (grid?.HasSurface == true) AddRampTerrain(grid, map, TextureFor);
            var view = _rampTerrain is not null || entry?.DrawCompiledTerrain == false ? null : map is not null
                ? new TerrainView(map.Grid.Width, map.Grid.Height, 1, map.VisualIds, map.VisualAt, TextureFor)
                : new TerrainView(_buildings ? BuildingScenario.Width : ShowcaseScenario.Width,
                    _buildings ? BuildingScenario.Height : ShowcaseScenario.Height, _buildings ? 1 : ShowcaseScenario.TileSize,
                    ShowcaseScenario.TerrainIds, _buildings ? BuildingScenario.TerrainAt : ShowcaseScenario.TerrainAt, TextureFor);
            if (view is not null) AddChild(view.Node);
            AddChild(_sprites.Node);
            if (PlayerFog) { _fogView = new(); AddChild(_fogView.Node); }
            CreateSelectionRing();
            if (entry is not null) AddChild(entry);
        }
        else { _view = new WorldView(catalog.Units[0], _count); AddChild(_view.Node); }
        if (_navigation)
        {
            _obstacles = new ObstacleView();
            AddChild(_obstacles.Node);
        }
        BuildScene(_count, catalog);
    }

    private void StartSession()
    {
        _session?.Dispose();
        _session = null;
        _leftGoal = false; _networkEnded = false;
        if (_showcase) { _selection.Reset(); _selected = _skirmish ? default : new(1); _armedAction = null; _armedCustom = null; _productionPlacement = null; _placing = false; _panelRevision = -1; _panelTick = -1; _panelDirty = true; _pointerDown = false; }
        _notice = "";
        _focusStartingBase = _terminal is not null;
        ResetPlacementRotation();
        _sitePlacement = null; _selectedProject = default; _visibleProjects = []; _visibleProjectsTick = -1; _constructionViewer = _networkMode != 0 ? _fogViewer : 1;
        var catalog = _mapMatch?.Map.Catalog ?? _catalog ?? throw new InvalidOperationException("Контент не загружен.");
        _combatEffects?.Clear(); _sprites?.ClearWrecks();
        _rtsOverlay?.RadarContacts.Clear();
        var map = _mapMatch;
        _opponent = null;
        if (_networkMode != 0) { StartNetworkSession(map!); return; }
        var offlineAis = _configuredOptions is { } configured && map is not null && _opponentProfile is not null
            ? configured.Seats.Select((seat, i) => (seat, i)).Where(p => p.seat.Controller == OpenRTS.Network.SlotController.Ai)
                .Select(p => SkirmishAiScenario.Create(catalog, _opponentProfile, map.Map, (uint)p.i + 1, _opponentStarts, opponents: configured.Opponents((uint)p.i + 1))).ToArray() : [];
        _opponent = offlineAis.FirstOrDefault();
        var recorder = _recordPlaytest ? CreateLocalRecorder() : null;
        _session = new SandboxSession(new SimulationSession(() => _rampScenario is { } ramps ? ramps.CreateWorld(_workers) : _jammingScenario ? JammingScenario.Create(catalog) : _fogScenario && (!_sensorScenario || map is null) ? FogScenario.Create(catalog, _fogHoldTicks, _sensorScenario) : map is not null ? _configuredOptions is { } selectedOptions ? CreateConfiguredWorld(map, selectedOptions) : map.CreateWorld(new GameplayMechanics { FogCellSize = _matchFogMode == OpenRTS.Core.Visibility.FogMode.Visible ? null : 1024, FogMode = _matchFogMode, FogVisibilityHoldTicks = _fogHoldTicks }, speedPerTick: _manifest.Settings.Simulation.SpeedPerTick, workers: _workers, restrictPlayerCommands: offlineAis.Length != 0, tickRate: _manifest.Settings.Simulation.TickRate) : _buildings ? BuildingScenario.Create(catalog, _workers) : _showcase ? ShowcaseScenario.Create(catalog) : _navigation ? NavigationScenario.Create(catalog, _count, _workers, _reverseJobs) : DiagnosticScenario.Create(catalog, _count),
            _verifySensors ? FogScenario.RadarCommands : _verifyFog ? FogScenario.Commands : _verifyAviation ? AviationScenario.Commands.Where(c => c.Tick <= _ticks).ToArray() : map is not null ? [] : _productionScenario ? ProductionScenario.Commands(catalog) : _buildings ? BuildingScenario.Commands(catalog) : _showcase ? (_verifyPresentation ? ShowcaseScenario.VerificationCommands(catalog) : []) : _navigation ? (_verifySimulation ? NavigationScenario.Commands(_count, _ticks) : [])
                : DiagnosticScenario.Commands(_count, _verifySimulation ? _ticks : long.MaxValue),
            _verifySimulation ? _ticks : null, realtime: !_verifySimulation || _pacedVerification,
            maximumEventBacklog: Math.Max(65536, _count + 1024),
            playbackSpeed: _verifySimulation ? 1 : _playbackSpeed, startPaused: !_verifySimulation && (_paused || _matchSetup is not null),
            collectCommands: offlineAis.Length == 0 ? null : world => offlineAis.SelectMany(ai => ai.Plan(world)).OrderBy(c => c.Issuer).ThenBy(c => c.Sequence).ToArray(),
            validateTurn: recorder is null ? null : recorder.ValidateTurn, observeTick: recorder is null ? null : recorder.ObserveTick), recorder);
    }

    private static Color TeamColor(uint owner) => owner switch
    {
        1 => new(0.14f, 0.52f, 1), 2 => new(1, 0.20f, 0.16f), 3 => new("#66bf53"), 4 => new("#ffc149"),
        5 => new("#39d4c5"), 6 => new("#a688f4"), 7 => new("#fa872d"), 8 => new("#ee8cb9"),
        _ => new(0.72f, 0.78f, 0.80f)
    };

    internal static Color TeamMarkerColor(uint owner) => owner switch
    {
        1 => new("#70baff"), 2 => new("#ff6651"), 0 => new("#c2bea4"), _ => TeamColor(owner)
    };

    public override void _Process(double delta)
    {
        if (_networkAdvertiser is not null && _lanRegistry is null && (_registryRetry -= delta) <= 0) ListenForGameLists();
        if (_playtestStop?.IsCompleted == true)
        {
            try
            {
                _playtestStop.GetAwaiter().GetResult();
                if (_playtestReady && _recordPlaytest) SaveLocalReplay();
                GetTree().Quit();
            }
            catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
            return;
        }
        if (_matchSetup is not null)
        {
            UpdateMatchSetup();
            return;
        }
        if (_verifyNetwork && (_networkVerifyElapsed += delta) > 40) { GD.PushError("Network verification timed out."); GetTree().Quit(1); return; }
        if (!_verifySimulation && !_verifyRamps) PanCamera(delta);
        if (_session is null || _view is null && _sprites is null || _verificationDone) return;
        try
        {
            if (_session.Network is { } network && _networkLabel is not null)
            {
                _networkLabel.Text = $"{(network.Player == 0 ? "Зритель" : "Игрок " + network.Player)} · {NetworkStatusText(network)}" + (_publications.Count == 0 ? "" : " · " + _publications[0].Publication.Status);
                UpdateNetworkChat(network);
            }
            UpdateReplayControls();
            bool completed = _session.Completion.IsCompleted;
            if (_networkMode != 0 && completed && !_networkEnded) { _networkEnded = true; _panelDirty = true; }
            if (_session.Completion.IsFaulted) _session.Completion.GetAwaiter().GetResult();
            using var frame = _session.Frames.AcquireLatest(PlayerFog ? _fogViewer : 0);
            if (frame is null) return;
            VerifyNetworkFrame(frame);
            float alpha = completed ? 1 : _session.GetInterpolationAlpha(frame.Tick);
            _view?.Apply(frame.Poses, alpha);
            _displayGrid = frame.Grid;
            _fogView?.Apply(frame, _fogStyle);
            if (_showcase)
            {
                _displayAlpha = alpha;
                _sprites!.SetFlightSurface(frame.Grid, reliefProjection: _rampTerrain is not null);
                if (_displayPoses.Length < frame.Poses.Length) Array.Resize(ref _displayPoses, frame.Poses.Length);
                _displayCount = 0;
                _storedPoses.Clear();
                _loadedTransports.Clear();
                foreach (var pose in frame.Poses)
                {
                    if (pose.Carrier.Value == 0) _displayPoses[_displayCount++] = pose;
                    else { _storedPoses.Add(pose); _loadedTransports.Add(pose.Carrier); }
                }
                if (_focusStartingBase && _selected.Value == 0)
                    for (int i = 0; i < _displayCount; i++)
                        if (_displayPoses[i].Presentation.Owner == (PlayerFog || _networkMode != 0 ? _fogViewer : 1))
                        {
                            _selected = _displayPoses[i].Id;
                            break;
                        }
                RefreshSelection(); UpdateRadarContacts(frame); UpdateConstructionView(frame); UpdateCommandPanel(frame); UpdateSelection(alpha); UpdateRtsCursor(); if (_buildings) UpdateFootprintPreview();
            }
            _obstacles?.Apply(frame.Grid);
            if (_managedPlaytest && !_playtestReady && (!_recordPlaytest || frame.Tick > 0))
            {
                _playtestReady = true;
                GD.Print(PlaytestReadyLine);
            }
            while (_session.TryReadEvent(out var value))
            {
                if (PlayerFog) { if (value.ForViewer(_fogViewer) is not { } filtered) continue; value = filtered; }
                _combatEffects?.Accept(value); if (!PlayerFog) _sprites?.Accept(value);
                _notice = value.Kind switch
                {
                    WorldEventKind.DestinationAdjusted => "Цель скорректирована к ближайшему достижимому месту.",
                    WorldEventKind.ActionUnavailable => "Действие сейчас недоступно: проверьте цель, кулдаун, очередь и кредиты.",
                    WorldEventKind.ProductionBlocked => "Производство завершено, выход занят. Ожидает свободного места.",
                    WorldEventKind.ProductionReady => "Производство завершено; результат ожидает выпуска или размещения.",
                    WorldEventKind.Produced when _selection.Contains(value.Entity) => "Производство завершено.",
                    WorldEventKind.TaskAdded when _selection.Contains(value.Entity) => "Заказ добавлен в очередь.",
                    WorldEventKind.TaskCancelled when _selection.Contains(value.Entity) => "Задача отменена.",
                    WorldEventKind.InvalidOrder => "Действие недоступно выбранному юниту.",
                    WorldEventKind.CommandDenied when value.Owner == CommandOwner => "Команда отклонена: проверьте владельца юнитов.",
                    WorldEventKind.TransportFull => "В транспорте нет свободных слотов.",
                    WorldEventKind.UnloadBlocked => "Рядом с транспортом нет места для выгрузки.",
                    WorldEventKind.ConstructionCancelled when value.Owner == _constructionViewer => "Проект отменён.",
                    WorldEventKind.ConstructionCompleted when value.Owner == _constructionViewer => "Строительство завершено.",
                    WorldEventKind.QueueFull => "Очередь приказов заполнена (32).",
                    WorldEventKind.NoRoute => "Маршрут или место остановки недоступны.",
                    WorldEventKind.CellOccupied => "Изменение отклонено: область пересекает тело юнита.",
                    WorldEventKind.CellBlocked => "Создание отклонено: клетка непроходима.",
                    _ => _notice
                };
            }
            if (_rtsOverlay is not null) _rtsOverlay.RadarStyle = _radarStyle;
            _sprites?.Apply(frame.Poses, alpha, frame.Tick, frame.TickRate, frame.Weapons, _visibleProjects, BuildingPreview() ?? UpgradePreview(), frame.Contacts, _radarStyle);
            _combatEffects?.Apply(frame.Projectiles, alpha, frame.Tick, _sprites?.FlightSurface ?? 0, PlayerFog ? frame : null,
                reliefProjection: _rampTerrain is not null, actors: frame.Poses, weapons: frame.Weapons, cameraBack: _camera?.Basis.Z);
            UpdateTerminal(frame, alpha);
            if (_jammingScenario && _verifySimulation && completed)
            {
                VerifyJammingVisuals(frame);
                return;
            }
            if (_verifySensors) { VerifySensors(frame); return; }
            if (_verifyFog) { VerifyFog(frame); return; }
            if (_verifyAiMatch) { VerifyAiMatch(frame); return; }
            if (_verifySceneMapMenu) { VerifySceneMapMenu(frame); return; }
            if (_verifyMatchSetup) { VerifyConfiguredMatch(frame); return; }
            if (_verifySkirmish) { VerifySkirmish(frame); return; }
            if (_verifyActions) { VerifyActions(frame); return; }
            if (_verifyControls) { if (_verifyRtsControls) VerifyRtsControls(frame.Tick); else VerifyControls(frame.Tick); return; }
            if (_showcase && DiagnosticsVisible && _label is not null) _label.Text = ShowcaseLabel(frame.Tick) + ConstructionStatus() + "\n" + string.Join("\n", frame.Players.ToArray().Where(p => p.Id != 0).Select(p => $"{p.Name}: кредиты {p.Credits} · энергия {p.EnergyProduced}/{p.EnergyConsumed}{(p.LowPower ? " · LOWPOWER" : "")}")) + (_skirmish ? $"\n{Engine.GetFramesPerSecond()} FPS · P пауза · N шаг · +/− темп · R сброс · F12 скрыть" : "");
            if (DiagnosticsVisible && _label is not null && _opponent is { } ai) _label.Text += "\nИИ · " + ai.Diagnostics;
            else if (!_showcase && _label is not null)
                _label.Text = $"OpenRTS · симуляция {frame.TickRate} Гц · ×{_playbackSpeed:0.###}{(_paused ? " · ПАУЗА" : "")}\n\n{frame.Poses.Length:N0} объектов · тик {frame.Tick} · {Engine.GetFramesPerSecond()} FPS\n" +
                    (_navigation ? $"Навигация · до {_workers} потоков поиска\nПриказ выполняют {frame.MovingCount} · ожидают {frame.NavigationWaitingCount} (очередь {frame.NavigationQueueWaitingCount})\nОбходов {frame.NavigationLocalSearches} · уступаний {frame.NavigationYields} за тик\n\nПКМ — компактная область · Пробел — сменить сторону\nB — препятствие под курсором · R — перезапуск\n\nТочные координаты и свободный угол. Локальные обходы ограничены."
                        : "Отдельный поток · целые координаты · MultiMesh\n\nПКМ — приказ всем · Пробел — случайные цели\nR — перезапуск · Esc — выход\n\nПрямое движение без навигации и столкновений.") + "\nКолесо — масштаб · WASD/стрелки/СКМ — камера\n+/− — темп · P — пауза · N — один тик" + (_notice.Length == 0 ? "" : "\nПоследний отказ: " + _notice);
            if (_verifyRamps) { VerifyRamps(frame); return; }
            if (_productionScenario && completed)
            {
                var production = _session.Completion.GetAwaiter().GetResult();
                if (frame.Tick != production.Tick || frame.Poses.Length != production.EntityCount || frame.Tasks.Length != 3)
                    throw new InvalidOperationException("Final production snapshot was incomplete.");
                _verificationDone = true; GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(production)); GetTree().Quit(); return;
            }
            if (_verifyAviation && completed) { VerifyAviation(frame); return; }
            if (_verifyBuildings && completed) { VerifyBuildings(frame); return; }
            if (_verifyPresentation && completed)
            {
                VerifyPresentation(frame); return;
            }
            if (_verifySimulation && completed && _mapMatch is not null)
            {
                var report = _session.Completion.GetAwaiter().GetResult();
                if (frame.Tick != report.Tick || frame.Poses.Length != report.EntityCount)
                    throw new InvalidOperationException("Не опубликован конечный кадр карты.");
                _verificationDone = true;
                if (_recordPlaytest) SaveLocalReplay();
                GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
                GetTree().Quit();
                return;
            }
            if (_verifySimulation && completed)
            {
                var view = _view ?? throw new InvalidOperationException("Missing diagnostic view.");
                var report = _session.Completion.GetAwaiter().GetResult();
                if (frame.Tick != report.Tick || frame.Poses.Length != report.EntityCount)
                    throw new InvalidOperationException("Не опубликован конечный кадр симуляции.");
                if (_navigation)
                {
                    var grid = frame.Grid ?? throw new InvalidOperationException("Кадр не содержит карту проходимости.");
                    int blocked = 0, lastBlocked = -1;
                    for (int i = 0; i < grid.CellCount; i++) if (grid.IsBlocked(i)) { blocked++; lastBlocked = i; }
                    if (_obstacles!.Node.Multimesh.InstanceCount != blocked)
                        throw new InvalidOperationException("Представление препятствий не соответствует карте.");
                    if (blocked > 0 && DisplayServer.GetName() != "headless")
                    {
                        var center = grid.Center(lastBlocked);
                        var point = _obstacles.Node.Multimesh.GetInstanceTransform(blocked - 1).Origin;
                        if (!point.IsEqualApprox(new Vector3(center.X / 1024f, 0.6f, center.Y / 1024f)))
                            throw new InvalidOperationException("Неверная позиция препятствия.");
                    }
                }
                int capacity = view.Node.Multimesh.InstanceCount;
                view.Apply(ReadOnlySpan<EntityPose>.Empty, 1);
                if (view.Node.Multimesh.VisibleInstanceCount != 0)
                    throw new InvalidOperationException("Пустое состояние не скрыло экземпляры.");
                view.Apply(frame.Poses, 0.5f);
                if (frame.Poses.Length > 0 && DisplayServer.GetName() != "headless")
                {
                    var last = view.Node.Multimesh.GetInstanceTransform(frame.Poses.Length - 1).Origin;
                    var pose = frame.Poses[^1];
                    var expected = new Vector3((pose.Previous.X + pose.Current.X) / 2048f,
                        (pose.Previous.Elevation + pose.Current.Elevation) / 2048f + 0.25f,
                        (pose.Previous.Y + pose.Current.Y) / 2048f);
                    if (!last.IsEqualApprox(expected)) throw new InvalidOperationException($"Неверная позиция представления: {last}, ожидалась {expected}.");
                }
                view.Apply(frame.Poses, 1);
                if (view.Node.Multimesh.InstanceCount != capacity || view.Node.Multimesh.VisibleInstanceCount != frame.Poses.Length)
                    throw new InvalidOperationException("Ёмкость MultiMesh не переиспользуется.");
                GD.Print("OPENRTS_CONTENT:" + JsonSerializer.Serialize(_catalog!.Summary));
                GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
                _verificationDone = true;
                GetTree().Quit();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            bool replayFailed = _session.Replay is not null;
            if (_session.Network is { IsHost: false, ResumeCredential: { } credential }) _networkResume = credential;
            _session.Dispose();
            _session = null;
            bool verifyReconnect = _verifyNetwork && _networkVerifyReconnect && _networkVerifyRole == "join" && !_networkReconnectAttempted &&
                _networkResume is not null && error is System.IO.IOException;
            if (_managedPlaytest || (_verifyNetwork && !verifyReconnect) || _verifyReplayPath is not null || _verifySimulation || _verifyControls || _verifySkirmish || _verifyActions || _verifyMatchSetup || _verifyRamps) GetTree().Quit(1);
            else if (replayFailed) ShowNetworkFailure(error.Message, "Ошибка повтора");
            else if (_networkMode != 0) ShowNetworkFailure(error.Message);
            else if (_label is not null)
            {
                if (_diagnostics is not null) _diagnostics.Show();
                _label.Text = "Ошибка симуляции\n" + error.Message;
            }
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (_verifySimulation || _session is null || _matchSetup is not null) return;
        try
        {
            if (input is InputEventKey { Pressed: true, Echo: false } key)
            {
                Key code = key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode;
                if (HandleReplayKey(code)) return;
                if (_networkMode != 0 && code is Key.R or Key.P or Key.N or Key.Period or Key.Comma or Key.Bracketleft or Key.Bracketright or Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract) return;
                if (PlayerFog && code == Key.F8 && _opponentProfile is null && _networkMode == 0) SwitchFogViewer();
                else if (PlayerFog && code == Key.F9) _fogStyle = (FogDisplayStyle)(((int)_fogStyle + 1) % 3);
                else if (PlayerFog && code == Key.F7) _radarStyle = (ContactDisplayStyle)(((int)_radarStyle + 1) % 3);
                else if (_terminal is not null && code == Key.F10) ToggleTerminalLayout();
                else if (_terminal is not null && code == Key.F11) SetTerminalFullscreen(!_exclusiveFullscreen);
                else if (_matchMenu?.Visible == true) { if (code == Key.Escape) ToggleMatchMenu(); }
                else if (_diagnostics is not null && code == Key.F12) _diagnostics.Visible = !_diagnostics.Visible;
                else if (_terminal is not null && code == Key.Escape && !HasTargetingMode()) ToggleMatchMenu();
                else if (_showcase && HandleRtsKey(key, code)) { }
                else if (_terminal is not null && !DiagnosticsVisible) return;
                else if (code == Key.Escape) GetTree().Quit();
                else if (code == Key.R) StartSession();
                else if (code == Key.P) { _paused = !_paused; _session.SetPlayback(_playbackSpeed, _paused); }
                else if (code == Key.N) { if (!_paused) { _paused = true; _session.SetPlayback(_playbackSpeed, true); } _session.StepOnce(); }
                else if (code is Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract)
                {
                    bool slower = code is Key.Minus or Key.KpSubtract;
                    _playbackSpeed = Math.Clamp(_playbackSpeed * (slower ? 0.5 : 2), 0.125, 8);
                    _session.SetPlayback(_playbackSpeed, _paused);
                }
                else if (_showcase && HandleShowcaseKey(code)) { }
                else if (code == Key.Space)
                {
                    if (_displayGrid is not null)
                    {
                        _leftGoal = !_leftGoal;
                        int cell = _displayGrid.Index(new(_leftGoal ? 4 : _displayGrid.Width - 8, _displayGrid.Height / 2));
                        _session.Submit(CommandKind.MoveGroup, _displayGrid.Center(cell));
                    }
                    else
                    {
                        int side = DiagnosticScenario.SideCells(_count) * WorldPosition.UnitsPerCell;
                        _session.Submit(CommandKind.Scatter, new(side, side));
                    }
                }
                else if (!_showcase && code == Key.B && _displayGrid is not null && MousePoint(GetViewport().GetMousePosition()) is { } target)
                {
                    int cell = _displayGrid.CellAt(target);
                    _session.Submit(_displayGrid.IsBlocked(cell) ? CommandKind.UnblockCell : CommandKind.BlockCell, target);
                }
            }
            else if (_matchMenu?.Visible == true || _terminal is not null && OverRtsPanel(_pointer)) return;
            else if (input is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                ZoomCamera(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? 0.8f : 1.25f);
            }
            else if (input is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Middle) != 0 && _camera is not null)
            {
                PlanarCamera.Drag(_camera, motion.Position, motion.Relative, Playfield().GetCenter(), new(_mapWidth, _mapDepth),
                    RampPick(motion.Position - motion.Relative)?.Elevation / 1024f ?? 0);
                if (_showcase) { _pointer = motion.Position; UpdateRtsCursor(); }
            }
            else if (_showcase && input is InputEventMouseButton click && click.ButtonIndex is MouseButton.Left or MouseButton.Right)
            { HandleShowcaseClick(click); }
            else if (_showcase && input is InputEventMouseMotion pointer) { _pointer = pointer.Position; UpdateRtsCursor(); }
            else if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouse && _camera is not null)
            {
                if (MousePoint(mouse.Position) is { } target) _session.Submit(_navigation ? CommandKind.MoveGroup : CommandKind.MoveAll, target);
            }
        }
        catch (Exception error) { GD.PushError(error.Message); }
    }

    private Vector3? GroundPoint(Vector2 mouse)
    {
        if (_rampTerrain is not null)
            return RampPick(mouse) is { } p ? new Vector3(p.X, p.Elevation, p.Y) / WorldPosition.UnitsPerCell : null;
        return _camera is null ? null : PlanarCamera.GroundPoint(_camera, mouse);
    }

    private WorldPosition? MousePoint(Vector2 mouse)
    {
        if (_rampTerrain is not null) return RampPick(mouse);
        if (GroundPoint(mouse) is not { } point) return null;
        int width = _displayGrid is { } grid ? grid.Width * grid.CellSize : 4096 * 1024;
        int height = _displayGrid is { } map ? map.Height * map.CellSize : 4096 * 1024;
        return new((int)Math.Clamp(point.X * 1024, 0, width), (int)Math.Clamp(point.Z * 1024, 0, height));
    }

    private void ZoomCamera(Vector2 mouse, float factor)
    {
        if (_camera is null) return;
        PlanarCamera.ZoomAt(_camera, mouse, factor, _baseCameraSize, Playfield().GetCenter(), new(_mapWidth, _mapDepth),
            RampPick(mouse)?.Elevation / 1024f ?? 0);
    }

    private void PanCamera(double delta)
    {
        var window = GetWindow();
        if (_camera is null || _matchMenu?.Visible == true || _terminal is not null && !window.HasFocus()) return;
        int x = (!_showcase && Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right) ? 1 : 0)
            - (!_showcase && Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ? 1 : 0);
        int z = (!_showcase && Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ? 1 : 0)
            - (!_showcase && Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ? 1 : 0);
        float speed = 0.7f;
        if (_terminal is not null && _edgeScroll && x == 0 && z == 0 && !_pointerDown && !Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            int id = window.GetWindowId();
            var bounds = new Rect2I(DisplayServer.WindowGetPosition(id), DisplayServer.WindowGetSize(id));
            var edge = WindowEdgeDirection(DisplayServer.MouseGetPosition(), bounds);
            x = edge.X;
            z = edge.Y;
            speed = _edgeSpeed;
        }
        var direction = new Vector3(x, 0, z);
        if (_manifest.Settings.Presentation.Camera == GameCamera.Isometric)
        {
            var right = _camera.Basis.X;
            direction = right * x + right.Cross(Vector3.Up) * z;
        }
        if (direction != Vector3.Zero) MoveCamera(direction.Normalized() * _camera.Size * (float)delta * speed);
    }

    private static Vector2I WindowEdgeDirection(Vector2I mouse, Rect2I bounds)
    {
        const int edge = 12;
        return new(
            mouse.X < bounds.Position.X + edge ? -1 : mouse.X >= bounds.End.X - edge ? 1 : 0,
            mouse.Y < bounds.Position.Y + edge ? -1 : mouse.Y >= bounds.End.Y - edge ? 1 : 0);
    }

    private void MoveCamera(Vector3 offset)
    {
        if (_camera is not null)
            PlanarCamera.Pan(_camera, offset, Playfield().GetCenter(), new(_mapWidth, _mapDepth));
    }

    public override void _ExitTree()
    {
        ClosePublications();
        System.Threading.Volatile.Write(ref _advertisement, null); _lanRegistry?.Dispose(); _lanRegistry = null;
        _networkAdvertiser?.Dispose(); _networkAdvertiser = null;
        if (_showcase) Input.MouseMode = Input.MouseModeEnum.Visible;
        _session?.Dispose();
        _session = null;
        _networkLobby = null;
    }

    private void BuildScene(int count, UnitCatalog catalog)
    {
        float side = DiagnosticScenario.SideCells(count);
        var center = new Vector3(side / 2, 0, side / 2);
        Vector2 groundSize = new(side, side);
        if (_navigation || _showcase)
        {
            var grid = _rampScenario?.Grid ?? _mapMatch?.Map.Grid ?? (_buildings ? BuildingScenario.Grid(catalog.Locomotion!) : _showcase ? ShowcaseScenario.Grid(catalog.Locomotion) : NavigationScenario.Grid(count));
            float sample = grid.CellSize / (float)WorldPosition.UnitsPerCell;
            side = Math.Max(grid.Width, grid.Height) * sample;
            center = new(grid.Width * sample / 2f, 0, grid.Height * sample / 2f);
            groundSize = new(grid.Width * sample, grid.Height * sample);
        }
        var ground = new MeshInstance3D
        {
            Position = center,
            Mesh = new PlaneMesh { Size = groundSize },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.055f, 0.09f, 0.12f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        if (!_showcase) AddChild(ground);
        else ground.QueueFree();
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = _rampTerrain is not null ? side * (26f / 32f) : _skirmish ? 24 : _mapMatch is not null ? side * 1.18f : _buildings ? 64 : _showcase ? 28 : side * 1.18f,
            Position = center + new Vector3(0, side, 0),
            Far = side * 4,
            Current = true
        };
        AddChild(camera);
        bool isometric = _manifest.Settings.Presentation.Camera == GameCamera.Isometric;
        if (isometric)
        {
            camera.RotationDegrees = new(-35.26439f, 45, 0);
            camera.Position = center + camera.Basis.Z * side;
            _sprites?.AlignDepthWithCamera(camera);
            _combatEffects?.AlignDepthWithCamera(camera);
        }
        else if (_rampTerrain is not null)
        {
            camera.RotationDegrees = new(-53, -18, 0);
            camera.Position = center + new Vector3(0, 1, 4.1f) + camera.Basis.Z * side;
        }
        else camera.RotationDegrees = new(-90, 0, 0);
        if (_showcase && !isometric && _rampTerrain is not null)
        {
            // Move the existing overlay lift along the view ray so height does not shift its screen position.
            var offset = (camera.Basis.Z / camera.Basis.Z.Y - Vector3.Up) * SpriteWorldView.OverlayHeight;
            _selectionRing!.Position = offset;
            _combatEffects!.Node.Position = offset;
        }
        _camera = camera;
        _baseCameraSize = camera.Size;
        _mapWidth = groundSize.X; _mapDepth = groundSize.Y;
        _label = AddOverlay("OpenRTS · запуск симуляции…");
        if (_skirmish || _fogScenario) AddTerminalHud();
        else if (_showcase) AddShowcaseFooter();
    }

    private Label AddOverlay(string text)
    {
        var layer = new CanvasLayer { Layer = _skirmish || _fogScenario ? 3 : 1 };
        AddChild(layer);
        var panel = new PanelContainer { Position = new Vector2(24, 24) };
        layer.AddChild(panel);
        if (_showcase) _rtsPanels.Add(panel);
        if ((_skirmish || _fogScenario) && _diagnostics is null) { _diagnostics = panel; panel.Hide(); }
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 18);
        panel.AddChild(margin);
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 18);
        margin.AddChild(label);
        return label;
    }

    private static void VerifyPreview(MultiMeshInstance3D preview, int count)
    {
        if (preview.Multimesh.InstanceCount != count || preview.GetChildCount() != 0)
            throw new InvalidOperationException("Ожидался один MultiMesh без узлов на каждый объект.");
        // The headless dummy renderer does not retain per-instance GPU transforms.
        if (DisplayServer.GetName() == "headless") return;
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        var last = preview.Multimesh.GetInstanceTransform(count - 1).Origin;
        if (last.X != (count - 1) % columns - (columns - 1) / 2f ||
            last.Z != (count - 1) / columns - (columns - 1) / 2f)
            throw new InvalidOperationException($"Последний маркер расположен неверно: {last}.");
    }
}
