using System;
using System.Collections.Generic;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

public sealed partial class MatchSetupScreen : CanvasLayer
{
    private readonly OptionButton _maps = new() { Name = "Maps" };
    private readonly OptionButton _fog = new() { Name = "Fog", TooltipText = "Скрыта: требуется разведка. Разведана: известна вся местность, объекты только в обзоре. Видима: все объекты видны." };
    private readonly OptionButton _fogStyle = new() { Name = "FogStyle" };
    public OpenRTS.Core.Visibility.FogMode SelectedFog => (OpenRTS.Core.Visibility.FogMode)_fog.Selected;
    public OpenRTS.Godot.Rendering.FogDisplayStyle SelectedFogStyle => (OpenRTS.Godot.Rendering.FogDisplayStyle)_fogStyle.Selected;
    private readonly OptionButton _modes = new() { Name = "Modes" };
    private readonly OptionButton _count = new() { Name = "PlayerCount" };
    private readonly CheckButton _opponent = new() { Name = "AiOpponent", Text = "Играть против ИИ" };
    private readonly GridContainer _players = new() { Name = "Players", Columns = 2 };
    private readonly List<OptionButton> _starts = [];
    private readonly Label _details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Button _launch = new() { Name = "Launch", Text = "НАЧАТЬ МАТЧ" };
    private readonly MatchSetupPreview _preview = new()
    {
        CustomMinimumSize = new(382, 288), TextureFilter = Control.TextureFilterEnum.Nearest
    };
    private (string Title, string Description, GameMap Map)[] _mapChoices = [];
    private (string Title, StartingSet Set)[] _modeChoices = [];
    private LocomotionCatalog _terrain = null!;

    public event Action<GameMap, StartingSet, string[], bool>? Launch;

    public void Build(Theme theme, LocomotionCatalog terrain,
        (string Title, string Description, GameMap Map)[] maps, (string Title, StartingSet Set)[] modes)
    {
        Name = "MatchSetup";
        Layer = 10;
        _mapChoices = maps;
        _modeChoices = modes;
        _terrain = terrain;
        var backdrop = new ColorRect { Color = new("#0b100c"), Theme = theme };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var scroll = new ScrollContainer { FollowFocus = true };
        backdrop.AddChild(scroll);
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 16);
        var center = new CenterContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        scroll.AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Sandbox.TerminalBox(new("#131911"), new("#71815a"), 20));
        center.AddChild(panel);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 14);
        panel.AddChild(layout);
        layout.AddChild(Sandbox.TerminalHeading("OPENRTS   /   ЛОКАЛЬНАЯ ИГРА"));
        var title = new Label { Text = "Настройка матча" };
        title.AddThemeFontSizeOverride("font_size", 28);
        layout.AddChild(title);

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 18);
        layout.AddChild(body);
        var mapColumn = new VBoxContainer();
        body.AddChild(mapColumn);
        mapColumn.AddChild(Sandbox.TerminalHeading("КАРТА И СТАРТОВЫЕ ТОЧКИ"));
        mapColumn.AddChild(_preview);
        mapColumn.AddChild(_details);

        var setup = new VBoxContainer { CustomMinimumSize = new(284, 0) };
        setup.AddThemeConstantOverride("separation", 9);
        body.AddChild(setup);
        AddField(setup, "Карта", _maps);
        AddField(setup, "Режим игры", _modes);
        AddField(setup, "Количество игроков", _count);
        _fog.AddItem("Скрыта · нужна разведка"); _fog.AddItem("Разведана · только текущий обзор"); _fog.AddItem("Видима · без тумана");
        _fogStyle.AddItem("Точная сетка"); _fogStyle.AddItem("Круги обзора"); _fogStyle.AddItem("Круги и границы сетки"); _fogStyle.Select(1);
        AddField(setup, "Туман войны", _fog); AddField(setup, "Отображение тумана · F9 в игре", _fogStyle);
        _fog.ItemSelected += _ => _fogStyle.Disabled = SelectedFog == OpenRTS.Core.Visibility.FogMode.Visible;
        setup.AddChild(_opponent);
        setup.AddChild(Sandbox.TerminalHeading("СТАРТОВЫЕ ПОЗИЦИИ"));
        _players.AddThemeConstantOverride("h_separation", 12);
        _players.AddThemeConstantOverride("v_separation", 5);
        setup.AddChild(_players);

        layout.AddChild(new HSeparator());
        layout.AddChild(new Label { Text = "ИИ доступен в режиме «Снабжение» с двумя игроками." });
        layout.AddChild(_status);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        layout.AddChild(buttons);
        var quit = new Button { Text = "Выход" };
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(quit);
        _launch.CustomMinimumSize = new(172, 38);
        _launch.AddThemeStyleboxOverride("normal", Sandbox.TerminalBox(new("#40321a"), new("#af8445"), 8));
        buttons.AddChild(_launch);

        foreach (var map in maps) _maps.AddItem(map.Title);
        foreach (var mode in modes) _modes.AddItem(mode.Title);
        _maps.ItemSelected += _ => SelectMap(PlayerCount);
        _modes.ItemSelected += _ => { UpdateOpponent(); _status.Text = ""; };
        _count.ItemSelected += _ => UpdatePlayers();
        _launch.Pressed += () => Launch?.Invoke(CurrentMap, _modeChoices[_modes.Selected].Set, AssignedSlots(), _opponent.ButtonPressed && !_opponent.Disabled);
        SelectMap(2);
        _maps.GrabFocus();
    }

    private static void AddField(VBoxContainer column, string label, OptionButton input)
    {
        column.AddChild(new Label { Text = label });
        column.AddChild(input);
    }

    private GameMap CurrentMap => _mapChoices[_maps.Selected].Map;
    private int PlayerCount => _count.Selected + 1;

    private void SelectMap(int previousCount)
    {
        int capacity = Math.Min(CurrentMap.Slots.Count, MapScenario.MaximumPlayers);
        _count.Clear();
        for (int i = 1; i <= capacity; i++) _count.AddItem(i.ToString());
        _count.Select(Math.Clamp(previousCount, 1, capacity) - 1);
        _starts.Clear();
        var entry = _mapChoices[_maps.Selected];
        _details.Text = $"{entry.Map.Grid.Width} × {entry.Map.Grid.Height} · до {capacity} игроков\n{entry.Description}";
        _preview.SetMap(entry.Map, _terrain);
        UpdatePlayers();
    }

    private void UpdatePlayers()
    {
        var selected = _starts.Select(row => row.Selected).Take(PlayerCount).ToList();
        while (selected.Count < PlayerCount)
            selected.Add(Enumerable.Range(0, CurrentMap.Slots.Count).First(slot => !selected.Contains(slot)));

        foreach (var child in _players.GetChildren())
        {
            _players.RemoveChild(child);
            child.QueueFree();
        }
        _starts.Clear();
        for (int i = 0; i < PlayerCount; i++)
        {
            int player = i;
            var label = new Label { Text = MapScenario.PlayerName(i), CustomMinimumSize = new(68, 0) };
            label.AddThemeColorOverride("font_color", Sandbox.TeamMarkerColor((uint)i + 1));
            _players.AddChild(label);
            var select = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            for (int slot = 0; slot < CurrentMap.Slots.Count; slot++)
                select.AddItem($"Точка {slot + 1:00}");
            select.Select(selected[i]);
            select.ItemSelected += slot =>
            {
                int old = selected[player];
                int other = selected.IndexOf((int)slot);
                if (other >= 0 && other != player)
                {
                    selected[other] = old;
                    _starts[other].Select(old);
                }
                selected[player] = (int)slot;
                _preview.SetAssignments(selected);
                _status.Text = "";
            };
            _players.AddChild(select);
            _starts.Add(select);
        }
        _preview.SetAssignments(selected);
        UpdateOpponent();
        _status.Text = "";
    }

    private string[] AssignedSlots() => _starts.Select(row => CurrentMap.Slots[row.Selected]).ToArray();

    private bool CanUseOpponent => CurrentMap.IsSkirmish && _modeChoices[_modes.Selected].Set.Id == "supply" && PlayerCount == 2;

    private void UpdateOpponent()
    {
        _opponent.Disabled = !CanUseOpponent;
        if (_opponent.Disabled) _opponent.SetPressedNoSignal(false);
    }

    public void SetLaunching(bool busy, string message)
    {
        _maps.Disabled = _modes.Disabled = _count.Disabled = _fog.Disabled = _launch.Disabled = busy;
        _fogStyle.Disabled = busy || SelectedFog == OpenRTS.Core.Visibility.FogMode.Visible;
        _opponent.Disabled = busy || !CanUseOpponent;
        foreach (var start in _starts) start.Disabled = busy;
        _status.Text = message;
    }
}
