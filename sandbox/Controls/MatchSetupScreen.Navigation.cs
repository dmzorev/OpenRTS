using System;
using Godot;
using OpenRTS.Network;

public sealed partial class MatchSetupScreen
{
    private readonly OptionButton _joinRole = new() { Name = "JoinRole" };
    private readonly LineEdit _gameName = new() { Text = "Игра OpenRTS", MaxLength = 64, PlaceholderText = "Название игры", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public string NetworkGameName => _gameName.Text.Trim().Length == 0 ? "Игра OpenRTS" : _gameName.Text.Trim();
    private LanGames? _browser;
    private VBoxContainer _gameRows = null!;
    private Label _gameStatus = null!;
    private double _refreshAt;
    private readonly LineEdit _roomPassword = new() { Name = "CreatePassword", Secret = true, MaxLength = 64, PlaceholderText = "Пароль (по желанию)" };
    private readonly LineEdit _passwordEntry = new() { Name = "JoinPassword", Secret = true, MaxLength = 64, PlaceholderText = "Пароль матча" };
    private readonly ConfirmationDialog _passwordDialog = new() { Name = "JoinPasswordDialog", Title = "Вход в игру", OkButtonText = "Войти" };
    private Action? _afterPassword;
    private string _joinPassword = "";
    public string MatchPassword => _joinPassword;
    private readonly LineEdit _mapFilter = new() { Name = "MapFilter", PlaceholderText = "Найти карту…" };
    private GridContainer _mapTiles = null!;

    private VBoxContainer Page(string key, string heading)
    {
        var scroll = new ScrollContainer { Name = key + "Page", FollowFocus = true }; _root.AddChild(scroll); scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 28);
        var center = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; scroll.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new(840, 0) }; center.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", Sandbox.TerminalBox(new("#101712ee"), new("#697657"), 20));
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 14); panel.AddChild(box);
        var top = new HBoxContainer(); box.AddChild(top);
        top.AddChild(Button("‹ Назад", () => Back(key)));
        var label = new Label { Text = heading, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; label.AddThemeFontSizeOverride("font_size", 25); top.AddChild(label);
        top.AddChild(new Label { Text = "OPENRTS", Modulate = new("#b5bc93") });
        box.AddChild(new HSeparator()); _pages[key] = scroll; return box;
    }
    private static Button Button(string text, Action pressed, string? name = null)
    {
        var button = new Button { Text = text, CustomMinimumSize = new(0, 36) }; if (name is not null) button.Name = name;
        button.Pressed += pressed; return button;
    }
    private static void Field(VBoxContainer parent, string label, Control control)
    { parent.AddChild(new Label { Text = label, Modulate = new("#b6c2a2") }); parent.AddChild(control); }
    private void ShowPage(string key)
    {
        _page = key;
        foreach (var pair in _pages) pair.Value.Visible = pair.Key == key;
        if (key == "replays") RefreshReplays();
        if (key == "browser")
        {
            try { _browser ??= new LanGames(); BrowseDirectory(); } catch (System.Net.Sockets.SocketException error) { _gameStatus.Text = "Поиск недоступен: " + error.Message; }
        }
    }
    private void Back(string key)
    {
        if (key == "create") { _pendingBrowserAction = null; _directoryCancellation?.Cancel(); ShowPage("browser"); return; }
        if (key == "maps" || key == "advanced") { ShowPage("lobby"); return; }
        if (key == "lobby") { if (NetworkMode != 0 && (_busy || _networkSession is not null)) { CancelNetwork?.Invoke(); _networkSession = null; _busy = false; } ShowPage(NetworkMode == 0 ? "main" : "browser"); return; }
        if (key == "replays" && _busy) CancelNetwork?.Invoke();
        _pendingBrowserAction = null; _directoryCancellation?.Cancel();
        ShowPage("main");
    }
    private void BuildMain()
    {
        var layer = new MarginContainer { Name = "MainMenu" }; _root.AddChild(layer); layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 58);
        var body = new HBoxContainer(); layer.AddChild(body);
        var column = new VBoxContainer { CustomMinimumSize = new(300, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; body.AddChild(column);
        column.AddChild(new Label { Text = "СТРАТЕГИЧЕСКОЕ КОМАНДОВАНИЕ", Modulate = new("#c9bb8b") });
        var title = new Label { Text = "OPENRTS" }; title.AddThemeFontSizeOverride("font_size", 58); column.AddChild(title);
        column.AddChild(new Label { Text = "Выберите поле боя", Modulate = new("#b8c2aa") }); column.AddChild(new HSeparator());
        column.AddChild(Button("Skirmish", OpenSkirmish, "MainSkirmish"));
        column.AddChild(Button("Multiplayer", () => ShowPage("browser"), "MainMultiplayer"));
        column.AddChild(Button("Повторы", () => ShowPage("replays"), "MainReplays"));
        column.AddChild(Button("Настройки", () => ShowPage("settings"), "MainSettings"));
        column.AddChild(Button("Выход", () => GetTree().Quit(), "MainExit"));
        var credit = new Label { Text = "ПЕРЕКРЁСТОК\nТактическая карта", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, HorizontalAlignment = HorizontalAlignment.Right, Modulate = new("#8d9c81") }; body.AddChild(credit);
        _pages["main"] = layer;
    }
    private void BuildLobby()
    {
        var box = Page("lobby", "Лобби");
        var heading = new HBoxContainer(); box.AddChild(heading); _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; heading.AddChild(_title); heading.AddChild(_viewers);
        var body = new HBoxContainer(); body.AddThemeConstantOverride("separation", 20); box.AddChild(body);
        box.AddChild(_publicationStatus);
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; body.AddChild(left);
        left.AddChild(_players);
        var fill = new Control { CustomMinimumSize = new(0, 150), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; left.AddChild(fill);
        left.AddChild(new Label { Text = "Одинаковый альянс — союзники. Фракция пока одна: OpenRTS.", Modulate = new("#9ba98e") });
        left.AddChild(_status);
        var right = new VBoxContainer { CustomMinimumSize = new(260, 0) }; body.AddChild(right);
        right.AddChild(_preview); _preview.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && Editable) ShowPage("maps"); };
        _mapButton = Button("Карта", () => ShowPage("maps"), "ChooseMap"); right.AddChild(_mapButton); right.AddChild(_details);
        Field(right, "Стартовый набор", _modes); Field(right, "Видимость карты", _fog); Field(right, "Темп игры", _speed);
        right.AddChild(Button("Расширенные настройки…", () => ShowPage("advanced"), "AdvancedOptions"));
        box.AddChild(new HSeparator());
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; box.AddChild(footer);
        footer.AddChild(_readyNetwork); footer.AddChild(_startNetwork); footer.AddChild(_launch);
        _launch.AddThemeStyleboxOverride("normal", Sandbox.TerminalBox(new("#423b24"), new("#bdac73"), 9));
        _advanced = Page("advanced", "Параметры матча");
        Field(_advanced, "Буфер сетевого ввода · тики", _delay);
        _advanced.AddChild(new Label { Text = "Больше буфер — устойчивее соединение, дольше отклик команд.", Modulate = new("#9ba98e") });
        _advanced.AddChild(Button("Вернуться в лобби", () => ShowPage("lobby")));
    }
    private void BuildBrowser()
    {
        LoadSources();
        var box = Page("browser", "Сетевая игра");
        var address = new HBoxContainer(); box.AddChild(address); address.AddChild(_source);
        address.AddChild(Button("Соединить", BrowseDirectory, "ConnectSource"));
        _source.TextSubmitted += _ => BrowseDirectory();
        box.AddChild(_directoryStatus);
        var scroll = new ScrollContainer { CustomMinimumSize = new(0, 220) }; box.AddChild(scroll);
        _gameRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_gameRows);
        _gameStatus = new Label { Modulate = new("#b5bc93") }; box.AddChild(_gameStatus);
        var actions = new HBoxContainer(); box.AddChild(actions);
        actions.AddChild(Button("Обновить", BrowseDirectory, "RefreshGames"));
        actions.AddChild(Button("Найти в LAN", FindLocalGames, "FindLan"));
        actions.AddChild(Button("Создать игру", () => { _roomPassword.Clear(); ShowPage("create"); }, "CreateGame"));

        var create = Page("create", "Создать игру"); Field(create, "Название", _gameName);
        Field(create, "Пароль", _roomPassword); create.AddChild(Button("Создать", CreateNetworkGame, "ConfirmCreate"));
        _root.AddChild(_passwordDialog); _passwordDialog.AddChild(_passwordEntry);
        _passwordDialog.Confirmed += () => { _joinPassword = _passwordEntry.Text; _passwordEntry.Clear(); var action = _afterPassword; _afterPassword = null; action?.Invoke(); };
        _passwordDialog.Canceled += () => { _afterPassword = null; _passwordEntry.Clear(); };
    }
    private void BeginNetworkLobby(bool host)
    {
        _network.Select(host ? 1 : 2); _busy = false; _networkSession = null; _shownState = null; _lastSettings = "";
        if (host) _address.Text = "0.0.0.0";
        ShowPage("lobby"); RefreshControls(); LaunchMatch();
    }
    private void EnterLobby(string address, int port, string pin, uint player)
    {
        _pendingBrowserAction = null; _directoryCancellation?.Cancel();
        RequestSeat = player != 0;
        _address.Text = address; _port.Value = port; _pin.Text = pin; _joinRole.Select(player == 0 ? 1 : 0);
        if (player != 0) _networkPlayer.Value = player;
        BeginNetworkLobby(false);
    }
    private void AddGameRow(string title, string map, int players, int open, int spectators, bool locked, string status,
        bool joinable, bool watchable, Action<bool> enter, string? joinName = null, string? watchName = null)
    {
        var row = new HBoxContainer(); _gameRows.AddChild(row);
        string mapName = _mapChoices.FirstOrDefault(m => m.Map.Id == map).Title ?? map;
        row.AddChild(new Label { Text = $"{title}{(locked ? " · пароль" : "")}   ·   {mapName}\n{status}   ·   Игроки {players}   ·   Места {Math.Max(0, open)}   ·   Зрители {spectators}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        void Join(bool watch)
        {
            if (!locked) { _joinPassword = ""; enter(watch); return; }
            _afterPassword = () => enter(watch); _passwordEntry.Clear(); _passwordDialog.PopupCentered(new(330, 110)); _passwordEntry.GrabFocus();
        }
        var join = Button("Войти", () => Join(false), joinName); join.Disabled = !joinable || open <= 0; row.AddChild(join);
        var watch = Button("Смотреть", () => Join(true), watchName); watch.Disabled = !watchable; row.AddChild(watch);
    }
    private static string PhaseLabel(MatchPhase phase) => phase switch { MatchPhase.Lobby => "В лобби", MatchPhase.Paused => "Пауза", _ => "Игра идёт" };
    private void RefreshGames()
    {
        foreach (var child in _gameRows.GetChildren()) { _gameRows.RemoveChild(child); child.QueueFree(); }
        if (_browser is not null) LocalRegistry?.Remember(_browser.Entries);
        AddDirectoryGames();
        var listed = _results.SelectMany(r => r.Games).Where(m => m.Expires > DateTimeOffset.UtcNow).Select(m => m.Game.Match).ToHashSet();
        var fromAddress = _results.SelectMany(r => r.LiveEntries).Select(e => e.Game).ToArray();
        var lan = fromAddress.Concat(_nearby.SelectMany(r => r.LiveEntries).Select(e => e.Game)).Concat(_browser?.Games ?? []).Concat(LocalRegistry?.LocalGames ?? [])
            .DistinctBy(g => (g.Fingerprint, g.Port)).Where(g => !listed.Contains(g.Match));
        foreach (var game in lan)
        {
            bool compatible = game.Capacity == 4 && game.Protocol == StarSession.ProtocolVersion &&
                (game.Rules is null || game.Rules == MatchSetupOptions.Rules) && (game.Content is null || game.Content == _directoryContent);
            AddGameRow(game.Title, game.Map, game.Players, game.OpenSlots, game.Spectators, game.PasswordRequired, compatible ? PhaseLabel(game.Phase) : "Несовместимая версия игры или контента",
                compatible && game.Phase == MatchPhase.Lobby, compatible && game.Spectators < 8 && game.Phase is MatchPhase.Lobby or MatchPhase.Paused, watch => {
                    SelectedEndpoints = null;
                    EnterLobby(game.Address, game.Port, game.Fingerprint, watch ? 0u : 2u);
                }, fromAddress.Contains(game) ? "AddressJoin" : "LanJoin", "LanWatch");
        }
        int count = _gameRows.GetChildCount();
        var idle = _browser?.IdlePeers ?? [];
        _gameStatus.Text = count > 0 ? $"Найдено игр: {count}" : idle.Length == 0 ? "Игр пока нет" : $"Клиенты на связи · игр нет ({string.Join(", ", idle.Take(4))})";
    }
    private void BuildMaps()
    {
        var box = Page("maps", "Выбор карты"); box.AddChild(_mapFilter); _mapTiles = new GridContainer { Columns = 3 }; _mapTiles.AddThemeConstantOverride("h_separation", 16); _mapTiles.AddThemeConstantOverride("v_separation", 14); box.AddChild(_mapTiles);
        _mapFilter.TextChanged += _ => PopulateMaps(); PopulateMaps();
    }
    private void PopulateMaps()
    {
        foreach (var child in _mapTiles.GetChildren()) { _mapTiles.RemoveChild(child); child.QueueFree(); }
        for (int i = 0; i < _mapChoices.Length; i++)
        {
            int index = i; var map = _mapChoices[i]; if (!(map.Title + " " + map.Description).Contains(_mapFilter.Text, StringComparison.OrdinalIgnoreCase)) continue;
            var panel = new PanelContainer { CustomMinimumSize = new(310, 0) }; panel.AddThemeStyleboxOverride("panel", Sandbox.TerminalBox(new("#172018"), new("#647453"), 9)); _mapTiles.AddChild(panel);
            var tile = new VBoxContainer(); panel.AddChild(tile);
            var preview = new MatchSetupPreview { CustomMinimumSize = new(280, 180), MouseFilter = Control.MouseFilterEnum.Ignore }; tile.AddChild(preview); preview.SetMap(map.Map, _terrain);
            tile.AddChild(new Label { Text = $"{map.Title}\n{map.Map.Slots.Count} игрока · {map.Map.Grid.Width} × {map.Map.Grid.Height}" });
            tile.AddChild(new Label { Text = map.Description, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(260, 48) });
            var select = Button("Выбрать", () => { if (!Editable) return; _maps.Select(index); _maps.EmitSignal(OptionButton.SignalName.ItemSelected, index); ShowPage("lobby"); }, "SelectMap" + index); select.Disabled = !Editable || _networkSession?.State.Slots.Any(s => s.Connected && s.Player > map.Map.Slots.Count) == true; tile.AddChild(select);
        }
    }
    private void BuildSettings()
    {
        var box = Page("settings", "Настройки");
        var fullscreen = new CheckButton { Text = "Полноэкранный режим", ButtonPressed = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen };
        fullscreen.Toggled += enabled => DisplayServer.WindowSetMode(enabled ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed); box.AddChild(fullscreen);
        Field(box, "Отображение тумана", _fogStyle); Field(box, "Отображение радарных контактов", _radarStyle);
        box.AddChild(Button("Сохранить", () => { var config = new ConfigFile(); config.SetValue("view", "fog", _fogStyle.Selected); config.SetValue("view", "radar", _radarStyle.Selected); config.Save("user://menu.cfg"); ShowPage("main"); }));
        var saved = new ConfigFile(); if (saved.Load("user://menu.cfg") == Error.Ok) { _fogStyle.Select(Math.Clamp((int)saved.GetValue("view", "fog", 1), 0, 2)); _radarStyle.Select(Math.Clamp((int)saved.GetValue("view", "radar", 1), 0, 2)); }
    }
    public override void _Process(double delta)
    {
        if (_page == "browser" && !_working && _results.Length != 0 && (_directoryRefresh -= delta) <= 0) BrowseDirectory();
        if (_mapTiles is not null) _mapTiles.Columns = GetViewport().GetVisibleRect().Size.X < 1100 ? 2 : 3;
        if (_page == "browser" && (_refreshAt -= delta) <= 0) { _refreshAt = 1; RefreshGames(); }
    }
    public override void _ExitTree() { _pendingBrowserAction = null; _directoryCancellation?.Cancel(); _browser?.Dispose(); }
}
