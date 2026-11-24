using System;
using System.Collections.Generic;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Network;

public sealed partial class MatchSetupScreen : CanvasLayer
{
    public event Action? CancelNetwork;
    public event Action? ConfigurationChanged;
    public event Action<GameMap, StartingSet, string[], bool>? Launch;
    private readonly OptionButton _network = new() { Name = "NetworkMode" };
    private readonly LineEdit _address = new() { Name = "NetworkAddress", Text = "127.0.0.1", PlaceholderText = "Адрес хоста" };
    private readonly SpinBox _port = new() { Name = "NetworkPort", MinValue = 1, MaxValue = 65535, Value = 17771 };
    private readonly LineEdit _pin = new() { Name = "NetworkPin", PlaceholderText = "SHA-256 отпечаток хоста" };
    private readonly SpinBox _networkPlayer = new() { Name = "NetworkPlayer", MinValue = 2, MaxValue = MapScenario.MaximumPlayers, Value = 2, CustomMinimumSize = new(110, 36), Prefix = "Место: " };
    private readonly SpinBox _networkAi = new() { Name = "NetworkAi", MinValue = 0, MaxValue = MapScenario.MaximumPlayers - 1, Value = 0 };
    private readonly SpinBox _delay = new() { Name = "NetworkDelay", MinValue = 1, MaxValue = 16, Value = 6 };
    private readonly OptionButton _maps = new() { Name = "Maps" }, _modes = new() { Name = "Modes" }, _count = new() { Name = "PlayerCount" };
    private readonly OptionButton _fog = new() { Name = "Fog" }, _fogStyle = new() { Name = "FogStyle" }, _radarStyle = new() { Name = "RadarStyle" };
    private readonly OptionButton _speed = new() { Name = "GameSpeed" };
    private readonly CheckButton _opponent = new() { Name = "AiOpponent", Text = "Играть против ИИ" };
    private readonly HBoxContainer _playerCountRow = new();
    private readonly GridContainer _players = new() { Name = "Players", Columns = 6, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly List<OptionButton> _starts = [], _roles = [], _teams = [];
    private readonly List<Label> _readyLabels = [];
    private readonly MatchSeat[] _seats = Enumerable.Range(0, MapScenario.MaximumPlayers)
        .Select(i => new MatchSeat(i < 2 ? SlotController.Human : SlotController.None, i)).ToArray();
    private readonly Label _details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart }, _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _title = new(), _viewers = new();
    private readonly Button _launch = new() { Name = "Launch", Text = "Начать матч" }, _readyNetwork = new() { Name = "NetworkReady", Text = "Готов" }, _startNetwork = new() { Name = "NetworkStart", Text = "Запустить матч" };
    private readonly MatchSetupPreview _preview = new() { Name = "SelectedMap", CustomMinimumSize = new(260, 180), TextureFilter = Control.TextureFilterEnum.Nearest, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
    private readonly MatchSetupPreview _background = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(0.85f, 0.9f, 0.8f) };
    private readonly Dictionary<string, Control> _pages = new();
    private Control _root = null!;
    private VBoxContainer _advanced = null!;
    private Button _mapButton = null!;
    private string _page = "main", _lastSettings = "";
    private bool _applying, _busy;
    private int _simulationSpeed = 128, _retentionTicks;
    private StarSession? _networkSession;
    private MatchState? _shownState;
    private (string Title, string Description, GameMap Map)[] _mapChoices = [];
    private (string Title, StartingSet Set)[] _modeChoices = [];
    private LocomotionCatalog _terrain = null!;
    public int NetworkMode => _network.Selected;
    public string NetworkAddress => _address.Text.Trim();
    public int NetworkPort => (int)_port.Value;
    public string NetworkPin => _pin.Text;
    public int NetworkInputDelay => (int)_delay.Value;
    public uint NetworkPlayer => _networkSession?.Player ?? (_joinRole.Selected == 1 ? 0 : (uint)_networkPlayer.Value);
    public int NetworkCapacity { get; private set; } = MapScenario.MaximumPlayers;
    public uint[] NetworkAiPlayers => _seats.Select((s, i) => (s, i)).Where(p => p.s.Controller == SlotController.Ai).Select(p => (uint)p.i + 1).ToArray();
    public uint[] ClosedPlayers => _seats.Select((s, i) => (s, i)).Where(p => p.s.Controller == SlotController.None).Select(p => (uint)p.i + 1).ToArray();
    public SlotController[] Controllers => _seats.Select(s => s.Controller).ToArray();
    public OpenRTS.Core.Visibility.FogMode SelectedFog => (OpenRTS.Core.Visibility.FogMode)_fog.Selected;
    public OpenRTS.Godot.Rendering.FogDisplayStyle SelectedFogStyle => (OpenRTS.Godot.Rendering.FogDisplayStyle)_fogStyle.Selected;
    public OpenRTS.Godot.Rendering.ContactDisplayStyle SelectedRadarStyle => (OpenRTS.Godot.Rendering.ContactDisplayStyle)_radarStyle.Selected;
    public MatchSetupOptions Options => new MatchSetupOptions(CurrentMap.Id, _modeChoices[_modes.Selected].Set.Id, _fog.Selected, (int)_delay.Value,
        new[] { 15, 25, 40 }[_speed.Selected], _seats.Take(RecipeSeatCount).ToArray(), _simulationSpeed, _retentionTicks).Bind(CurrentMap, _modeChoices[_modes.Selected].Set);
    private GameMap CurrentMap => _mapChoices[_maps.Selected].Map;
    private int Capacity => Math.Min(NetworkMode == 0 ? MapScenario.MaximumPlayers : NetworkCapacity, CurrentMap.Slots.Count);
    private int RecipeSeatCount => NetworkMode == 0
        ? (Capacity > MatchSetupOptions.LegacySeatCount ? MapScenario.MaximumPlayers : MatchSetupOptions.LegacySeatCount)
        : NetworkCapacity;
    private bool Editable => NetworkMode != 2 && (!_busy || NetworkMode == 1 && _networkSession?.State.Phase == MatchPhase.Lobby);

    public void Build(Theme theme, LocomotionCatalog terrain, (string Title, string Description, GameMap Map)[] maps,
        (string Title, StartingSet Set)[] modes, int defaultMap = 0, int defaultMode = 0, int speedPerTick = 128, int fogHoldTicks = 0, string contentFingerprint = "")
    {
        _directoryContent = contentFingerprint;
        Name = "MatchSetup"; Layer = 10; _simulationSpeed = speedPerTick; _retentionTicks = fogHoldTicks; _mapChoices = maps; _modeChoices = modes; _terrain = terrain;
        _root = new Control { Theme = theme }; AddChild(_root); _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_background); _background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); _background.SetMap(maps[Math.Min(1, maps.Length - 1)].Map, terrain);
        var shade = new ColorRect { Color = new Color(0.025f, 0.04f, 0.035f, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(shade); shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _network.AddItem("Skirmish"); _network.AddItem("Host"); _network.AddItem("Join");
        foreach (var item in maps) _maps.AddItem(item.Title); foreach (var item in modes) _modes.AddItem(item.Title);
        _maps.Select(Math.Max(0, defaultMap)); _modes.Select(Math.Max(0, defaultMode));
        foreach (string s in new[] { "Скрыта", "Разведана", "Видима" }) _fog.AddItem(s);
        foreach (string s in new[] { "Точная сетка", "Круги обзора", "Круги и сетка" }) _fogStyle.AddItem(s); _fogStyle.Select(1);
        foreach (string s in new[] { "Иконки", "Модели зданий", "Все модели" }) _radarStyle.AddItem(s); _radarStyle.Select(1);
        foreach (string s in new[] { "Медленно · 15", "Обычно · 25", "Быстро · 40" }) _speed.AddItem(s); _speed.Select(1);
        _joinRole.AddItem("Игрок"); _joinRole.AddItem("Зритель");
        // Hidden selectors keep diagnostic scenarios on the same public controls as the shell.
        foreach (Control c in new Control[] { _network, _maps, _networkAi, _opponent, _port, _pin, _networkPlayer, _joinRole, _address }) { _root.AddChild(c); c.Hide(); }
        BuildMain(); BuildBrowser(); BuildLobby(); BuildMaps(); BuildSettings(); BuildReplays();
        _maps.ItemSelected += _ => { SelectMap(); Changed(); };
        _count.ItemSelected += _ => {
            bool addAi = NetworkMode == 0 && Capacity > MatchSetupOptions.LegacySeatCount && _modeChoices[_modes.Selected].Set.Id == "supply";
            int last = Math.Min(_count.Selected, Capacity - 1);
            for (int i = 1; i < _seats.Length; i++)
            {
                var controller = SlotController.None;
                if (i <= last)
                {
                    controller = _seats[i].Controller;
                    if (!addAi) controller = SlotController.Human;
                    else if (controller == SlotController.None) controller = SlotController.Ai;
                }
                _seats[i] = _seats[i] with { Controller = controller };
            }
            RebuildPlayers(); Changed();
        };
        _network.ItemSelected += _ => { ShowPage("lobby"); RefreshControls(); };
        _modes.ItemSelected += _ => { if (_modeChoices[_modes.Selected].Set.Id != "supply") for (int i = 1; i < _seats.Length; i++) if (_seats[i].Controller == SlotController.Ai) _seats[i] = _seats[i] with { Controller = SlotController.Human }; RebuildPlayers(); Changed(); };
        _fog.ItemSelected += _ => { RefreshControls(); Changed(); }; _speed.ItemSelected += _ => Changed(); _delay.ValueChanged += _ => Changed();
        _networkAi.ValueChanged += value => { for (int i = 1; i < Capacity; i++) _seats[i] = _seats[i] with { Controller = i >= Capacity - (int)value ? SlotController.Ai : SlotController.Human }; RebuildPlayers(); Changed(); };
        _opponent.Toggled += enabled => { _seats[1] = _seats[1] with { Controller = enabled ? SlotController.Ai : SlotController.Human }; RebuildPlayers(); };
        _launch.Pressed += LaunchMatch; _readyNetwork.Pressed += () => _networkSession?.Ready(_networkSession.State.Revision); _startNetwork.Pressed += () => _networkSession?.Start();
        SelectMap(); ShowPage("main");
    }
    private void LaunchMatch() => Launch?.Invoke(CurrentMap, _modeChoices[_modes.Selected].Set, AssignedSlots(), _seats.Any(s => s.Controller == SlotController.Ai));
    private string[] AssignedSlots() => Options.PlayerSlots(CurrentMap);
    public void OpenSkirmish()
    {
        _network.Select(0); _busy = false;
        if (_modeChoices[_modes.Selected].Set.Id == "supply") _seats[1] = _seats[1] with { Controller = SlotController.Ai };
        RebuildPlayers(); ShowPage("lobby");
    }
    private void SelectMap()
    {
        int capacity = Capacity;
        _count.Clear(); for (int i = 1; i <= capacity; i++) _count.AddItem(i.ToString());
        for (int i = 0; i < _seats.Length; i++) _seats[i] = _seats[i] with { Start = i % capacity, Controller = i >= capacity ? SlotController.None : _seats[i].Controller };
        _details.Text = $"{_mapChoices[_maps.Selected].Title}   ·   {CurrentMap.Grid.Width} × {CurrentMap.Grid.Height}\n{_mapChoices[_maps.Selected].Description}";
        _mapButton.Text = _mapChoices[_maps.Selected].Title + "  ↗";
        _preview.SetMap(CurrentMap, _terrain); RebuildPlayers();
    }
    private void Changed() { if (!_applying && Editable && _networkSession is { IsHost: true }) ConfigurationChanged?.Invoke(); }
    private void RebuildPlayers()
    {
        foreach (var child in _players.GetChildren()) { _players.RemoveChild(child); child.QueueFree(); }
        _starts.Clear(); _roles.Clear(); _teams.Clear(); _readyLabels.Clear();
        foreach (string label in new[] { "СТОРОНА", "УЧАСТНИК", "ФРАКЦИЯ", "АЛЬЯНС", "СТАРТ", "ГОТОВ" }) _players.AddChild(new Label { Text = label });
        for (int i = 0; i < Capacity; i++)
        {
            int row = i;
            var name = new Label { Text = MapScenario.PlayerName(i), CustomMinimumSize = new(82, 36), VerticalAlignment = VerticalAlignment.Center };
            name.AddThemeColorOverride("font_color", Sandbox.TeamMarkerColor((uint)i + 1)); _players.AddChild(name);
            var role = new OptionButton { Name = "Role" + (i + 1), CustomMinimumSize = new(110, 36) };
            role.AddItem(i == 0 ? "Игрок · Host" : NetworkMode == 0 || _networkSession?.State.Slots[i].Connected == true ? "Игрок " + (i + 1) : "Открыто", (int)SlotController.Human);
            role.AddItem("ИИ · Skirmish", (int)SlotController.Ai); role.AddItem("Закрыто", (int)SlotController.None);
            role.Select((int)_seats[i].Controller); _players.AddChild(role); _roles.Add(role);
            var faction = new OptionButton { CustomMinimumSize = new(78, 36), Disabled = true }; faction.AddItem("OpenRTS"); _players.AddChild(faction);
            var team = new OptionButton { Name = "Team" + (i + 1), CustomMinimumSize = new(64, 36), TooltipText = "Одинаковый альянс: без атак друг на друга. Обзор и управление раздельные." };
            team.AddItem("Нет"); for (int t = 1; t <= MapScenario.MaximumPlayers; t++) team.AddItem(t.ToString()); team.Select((int)_seats[i].Team); _players.AddChild(team); _teams.Add(team);
            var start = new OptionButton { Name = "Start" + (i + 1), CustomMinimumSize = new(58, 36) };
            for (int t = 0; t < CurrentMap.Slots.Count; t++) start.AddItem((t + 1).ToString("00")); start.Select(_seats[i].Start); _players.AddChild(start); _starts.Add(start);
            var ready = new Label { HorizontalAlignment = HorizontalAlignment.Center }; _players.AddChild(ready); _readyLabels.Add(ready);
            role.ItemSelected += value => { _seats[row] = _seats[row] with { Controller = (SlotController)value }; RebuildPlayers(); Changed(); };
            team.ItemSelected += value => { _seats[row] = _seats[row] with { Team = (uint)value }; Changed(); };
            start.ItemSelected += value => {
                int old = _seats[row].Start;
                int other = Array.FindIndex(_seats, 0, Capacity, s => s.Start == (int)value);
                if (other >= 0 && other != row) { _seats[other] = _seats[other] with { Start = old }; if (other < _starts.Count) _starts[other].Select(old); }
                _seats[row] = _seats[row] with { Start = (int)value }; _preview.SetAssignments(_seats.Take(Capacity).Select(s => s.Start).ToArray()); Changed();
            };
        }
        _count.Select(_seats.Take(Capacity).Count(s => s.Controller != SlotController.None) - 1);
        _preview.SetAssignments(_seats.Take(Capacity).Select(s => s.Start).ToArray()); RefreshControls();
    }
    private void RefreshControls()
    {
        _publicationStatus.Visible = NetworkMode == 1;
        _playerCountRow.Visible = NetworkMode == 0 && Capacity > MatchSetupOptions.LegacySeatCount;
        _opponent.Disabled = NetworkMode != 0 || _modeChoices[_modes.Selected].Set.Id != "supply";
        _opponent.SetPressedNoSignal(_seats[1].Controller == SlotController.Ai);
        _title.Text = NetworkMode == 0 ? "Skirmish" : NetworkMode == 1 ? "Лобби · Host" : NetworkPlayer == 0 ? "Лобби · Зритель" : "Лобби · Игрок " + NetworkPlayer;
        _maps.Disabled = _modes.Disabled = _count.Disabled = _fog.Disabled = _speed.Disabled = _mapButton.Disabled = !Editable;
        _delay.Editable = Editable; _fogStyle.Disabled = _fog.Selected == 2;
        for (int i = 0; i < _roles.Count; i++)
        {
            bool connected = _networkSession?.State.Slots[i].Connected == true;
            _roles[i].Disabled = !Editable || i == 0 || connected;
            _roles[i].SetItemDisabled(1, _modeChoices[_modes.Selected].Set.Id != "supply");
            _starts[i].Disabled = _teams[i].Disabled = !Editable || _seats[i].Controller == SlotController.None;
            _readyLabels[i].Text = _seats[i].Controller == SlotController.None ? "—" : _seats[i].Controller == SlotController.Ai ? "✓" : NetworkMode == 0 ? "•" : _networkSession?.State.Slots[i].Ready == true ? "✓" : "…";
        }
        _launch.Visible = NetworkMode == 0 || _networkSession is null;
        _launch.Disabled = _busy;
        _readyNetwork.Visible = NetworkMode != 0 && _networkSession is not null && NetworkPlayer != 0 || NetworkMode == 1 && _networkSession is not null;
        _startNetwork.Visible = _networkSession?.IsHost == true;
        if (_networkSession is { } network)
        {
            _readyNetwork.Disabled = network.State.Match == Guid.Empty || network.State.Slots[(int)network.Player - 1 < 0 ? 0 : (int)network.Player - 1].Ready;
            _startNetwork.Disabled = network.State.Slots.Any(s => s.Controller == SlotController.Human && (!s.Connected || !s.Ready));
        }
    }
    public void ApplyOptions(string data)
    {
        var options = MatchSetupOptions.Parse(data); _simulationSpeed = options.SpeedPerTick; _retentionTicks = options.FogHoldTicks; _applying = true;
        try
        {
            int map = Array.FindIndex(_mapChoices, m => m.Map.Id == options.Map), mode = Array.FindIndex(_modeChoices, m => m.Set.Id == options.Mode);
            if (map < 0 || mode < 0) throw new ArgumentException("Карта или стартовый набор не установлены.");
            bool changedMap = _maps.Selected != map; _maps.Select(map); _modes.Select(mode);
            if (changedMap) SelectMap();
            _fog.Select(options.Fog); _delay.Value = options.Delay; _speed.Select(Array.IndexOf(new[] { 15, 25, 40 }, options.TickRate));
            Array.Copy(options.Seats, _seats, options.Seats.Length);
            RebuildPlayers();
        }
        finally { _applying = false; }
    }
    public void SetNetworkStatus(string status, string fingerprint) { if (NetworkMode == 1) _pin.Text = fingerprint; _status.Text = status; }
    public void UpdateNetworkLobby(StarSession network)
    {
        _networkSession = network;
        if (network.LobbySettings != "" && network.LobbySettings != _lastSettings) { _lastSettings = network.LobbySettings; ApplyOptions(_lastSettings); }
        if (!ReferenceEquals(_shownState, network.State))
        {
            _shownState = network.State;
            for (int i = 0; i < network.State.Slots.Count; i++) _seats[i] = _seats[i] with { Controller = network.State.Slots[i].Controller };
            _viewers.Text = $"Наблюдатели: {network.State.Spectators}" + (NetworkMode == 2 ? "   ·   Настройки задаёт Host" : ""); RebuildPlayers();
        }
        RefreshControls();
    }
    public void SetLaunching(bool busy, string message) { _busy = busy; _status.Text = message; if (_page == "replays") _replayMessage.Text = message; RefreshControls(); }
}
