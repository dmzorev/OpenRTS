using System;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private static readonly Color TerminalInk = new("#dce6c8");
    private static readonly Color TerminalAmber = new("#efbb60");
    private static readonly Color TerminalOlive = new("#59654b");
    private const float SidebarWidth = 336, FooterHeight = 252;
    private const string TerminalSettingsPath = "user://terminal.cfg";
    private PanelContainer? _terminal, _diagnostics;
    private BoxContainer? _terminalLayout;
    private GridContainer? _terminalOrders, _terminalActions;
    private TerminalMinimap? _minimap;
    private Label? _terminalResources, _terminalSelection, _terminalNotice;
    private TextureRect? _terminalPortrait;
    private ProgressBar? _terminalHealth;
    private Control? _matchMenu;
    private Button? _layoutButton;
    private bool _rightHud = true, _edgeScroll = true, _allHealth, _pauseBeforeMenu, _focusStartingBase;
    private float _edgeSpeed = 0.7f;

    private Color ActiveButtonColor => _terminal is null ? new(0.5f, 0.85f, 1) : TerminalAmber;
    private bool DiagnosticsVisible => _terminal is null || _diagnostics?.Visible == true;
    private bool OverRtsPanel(Vector2 pixel) => _rtsPanels.Exists(panel => panel.IsVisibleInTree() && panel.GetGlobalRect().HasPoint(pixel));

    private static StyleBoxFlat TerminalBox(Color background, Color border, int padding = 6) => new()
    {
        BgColor = background, BorderColor = border,
        BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = padding, ContentMarginRight = padding, ContentMarginTop = padding, ContentMarginBottom = padding
    };

    private static Theme TerminalTheme()
    {
        var theme = new Theme { DefaultFontSize = 13 };
        theme.SetStylebox("panel", "PanelContainer", TerminalBox(new("#131911"), TerminalOlive, 8));
        theme.SetStylebox("normal", "Button", TerminalBox(new("#21291c"), TerminalOlive));
        theme.SetStylebox("hover", "Button", TerminalBox(new("#353d26"), TerminalInk));
        theme.SetStylebox("pressed", "Button", TerminalBox(new("#393220"), TerminalAmber));
        theme.SetStylebox("disabled", "Button", TerminalBox(new("#181d16"), new("#343e2f")));
        theme.SetColor("font_color", "Button", TerminalInk);
        theme.SetColor("font_disabled_color", "Button", new("#85907a"));
        theme.SetColor("font_color", "Label", TerminalInk);
        theme.SetStylebox("background", "ProgressBar", TerminalBox(new("#0d130d"), new("#394733"), 0));
        theme.SetStylebox("fill", "ProgressBar", TerminalBox(new("#80a653"), new("#80a653"), 0));
        return theme;
    }

    private static Label TerminalHeading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeColorOverride("font_color", TerminalAmber);
        heading.AddThemeFontSizeOverride("font_size", 13);
        return heading;
    }

    private static Texture2D TerminalIcon(Texture2D texture)
    {
        // Replace the sprite's team marker with the terminal's neutral palette once per unit type.
        using var image = texture.GetImage();
        if (image is null || image.IsEmpty()) return texture;
        if (image.IsCompressed() && image.Decompress() != Error.Ok) return texture;
        image.Convert(Image.Format.Rgba8);
        var paint = new Color("#788c56");
        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
        {
            var pixel = image.GetPixel(x, y);
            float peak = Mathf.Max(pixel.R, pixel.B);
            float mask = Mathf.SmoothStep(0.12f, 0.35f, (Mathf.Min(pixel.R, pixel.B) - pixel.G) / Mathf.Max(peak, 0.001f));
            if (pixel.A <= 0 || mask <= 0) continue;
            var tint = paint * Mathf.Lerp(0.52f, 1.08f, peak);
            tint.A = pixel.A;
            image.SetPixel(x, y, pixel.Lerp(tint, mask));
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Button TerminalButton(string text, string tooltip, Action action)
    {
        var button = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += action;
        return button;
    }

    private void AddTerminalHud()
    {
        var settings = new ConfigFile();
        if (!_verifySkirmish && settings.Load(TerminalSettingsPath) == Error.Ok)
        {
            _rightHud = (bool)settings.GetValue("hud", "right", true);
            _edgeScroll = (bool)settings.GetValue("camera", "edge_scroll", true);
            _edgeSpeed = Mathf.Clamp((float)settings.GetValue("camera", "edge_speed", 0.7f), 0.2f, 1.4f);
            _allHealth = (bool)settings.GetValue("hud", "all_health", false);
        }

        var layer = new CanvasLayer { Layer = 2 };
        AddChild(layer);
        _terminal = new PanelContainer { Name = "MilitaryTerminal", Theme = TerminalTheme() };
        _terminal.AddThemeStyleboxOverride("panel", TerminalBox(new("#10150f"), new("#71815a"), 6));
        layer.AddChild(_terminal);
        _rtsPanels.Add(_terminal);
        _terminalLayout = new BoxContainer { Name = "Layout" };
        _terminalLayout.AddThemeConstantOverride("separation", 6);
        _terminal.AddChild(_terminalLayout);

        var radar = AddSection(_terminalLayout, "01   ТАКТИЧЕСКАЯ КАРТА");
        _minimap = new TerminalMinimap { CustomMinimumSize = new(0, 112), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _minimap.TooltipText = "ЛКМ или перетаскивание · переместить камеру\nПолный обзор карты";
        _minimap.Navigate += CenterCamera;
        radar.AddChild(_minimap);
        _terminalResources = new Label { Text = "Кредиты · Энергия", TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        radar.AddChild(_terminalResources);
        var nav = new HBoxContainer();
        radar.AddChild(nav);
        _layoutButton = TerminalButton("", "Переключить расположение панели (F10)", ToggleTerminalLayout);
        nav.AddChild(_layoutButton);
        nav.AddChild(TerminalButton("Меню", "Меню матча (Esc)", ToggleMatchMenu));

        var selection = AddSection(_terminalLayout, "02   ВЫБРАННЫЕ ОБЪЕКТЫ");
        var card = new HBoxContainer();
        selection.AddChild(card);
        _terminalPortrait = new TextureRect { CustomMinimumSize = new(56, 45), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        card.AddChild(_terminalPortrait);
        var details = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddChild(details);
        _terminalSelection = new Label { Text = "Выберите объект", TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        details.AddChild(_terminalSelection);
        _terminalHealth = new ProgressBar { CustomMinimumSize = new(0, 6), ShowPercentage = false, Visible = false };
        details.AddChild(_terminalHealth);
        AddTypeStrip(selection);
        AddSelectionExtras(selection);
        _terminalNotice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        selection.AddChild(_terminalNotice);

        var commandPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _terminalLayout.AddChild(commandPanel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        commandPanel.AddChild(scroll);
        var commands = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(commands);
        commands.AddChild(TerminalHeading("03   ПРОИЗВОДСТВО И СПОСОБНОСТИ"));
        _terminalActions = new GridContainer();
        _customRow = _terminalActions;
        commands.AddChild(_terminalActions);
        AddTaskStrip(commands);
        commands.AddChild(TerminalHeading("04   ПРИКАЗЫ"));
        _terminalOrders = new GridContainer();
        commands.AddChild(_terminalOrders);
        AddOrderButtons(_terminalOrders);

        if (_buildings) AddBuildingTools(commands);
        ConfigureTerminalLayout();
        AddMatchMenu();
    }

    private static VBoxContainer AddSection(Container parent, string heading)
    {
        var panel = new PanelContainer();
        parent.AddChild(panel);
        var column = new VBoxContainer();
        panel.AddChild(column);
        column.AddChild(TerminalHeading(heading));
        return column;
    }

    private void ConfigureTerminalLayout()
    {
        _terminal!.SetAnchorsAndOffsetsPreset(_rightHud ? Control.LayoutPreset.RightWide : Control.LayoutPreset.BottomWide);
        _terminal.OffsetLeft = _rightHud ? -SidebarWidth : 0;
        _terminal.OffsetTop = _rightHud ? 0 : -FooterHeight;
        _terminalLayout!.Vertical = _rightHud;
        _terminalLayout.GetChild<Control>(0).CustomMinimumSize = _rightHud ? new(0, 244) : new(224, 0);
        _terminalLayout.GetChild<Control>(1).CustomMinimumSize = _rightHud ? Vector2.Zero : new(242, 0);
        _terminalOrders!.Columns = _rightHud ? 3 : 6;
        _terminalActions!.Columns = _rightHud ? 4 : 8;
        _layoutButton!.Text = _rightHud ? "Панель вниз" : "Панель вправо";
    }

    private void ToggleTerminalLayout()
    {
        var center = GroundPoint(Playfield().GetCenter());
        _rightHud = !_rightHud;
        ConfigureTerminalLayout();
        if (center is { } point) CenterCamera(new(point.X, point.Z));
        SaveTerminalSettings();
    }

    private void SaveTerminalSettings()
    {
        if (_verifySkirmish) return;
        var settings = new ConfigFile();
        settings.SetValue("hud", "right", _rightHud);
        settings.SetValue("hud", "all_health", _allHealth);
        settings.SetValue("camera", "edge_scroll", _edgeScroll);
        settings.SetValue("camera", "edge_speed", _edgeSpeed);
        if (settings.Save(TerminalSettingsPath) != Error.Ok) GD.PushWarning("Не удалось сохранить настройки HUD.");
    }

    private void AddMatchMenu()
    {
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        var modal = new ColorRect { Color = new(0, 0, 0, 0.65f), Visible = false, Theme = _terminal!.Theme };
        layer.AddChild(modal);
        modal.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _matchMenu = modal;
        _rtsPanels.Add(modal);

        var center = new CenterContainer();
        modal.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var menu = AddSection(center, "OPENRTS   /   МЕНЮ МАТЧА");
        menu.CustomMinimumSize = new(280, 0);
        menu.AddChild(TerminalButton("Продолжить", "Esc", ToggleMatchMenu));
        menu.AddChild(TerminalButton("Сменить расположение панели", "F10", ToggleTerminalLayout));
        var edge = new CheckButton { Text = "Прокрутка у края", ButtonPressed = _edgeScroll, FocusMode = Control.FocusModeEnum.None };
        edge.Toggled += enabled => { _edgeScroll = enabled; SaveTerminalSettings(); };
        menu.AddChild(edge);
        menu.AddChild(new Label { Text = "Скорость прокрутки у края" });
        var speed = new HSlider { MinValue = 0.2, MaxValue = 1.4, Step = 0.1, Value = _edgeSpeed, FocusMode = Control.FocusModeEnum.None };
        speed.ValueChanged += value => { _edgeSpeed = (float)value; SaveTerminalSettings(); };
        menu.AddChild(speed);
        var health = new CheckButton { Text = "Показывать здоровье всех объектов", ButtonPressed = _allHealth, FocusMode = Control.FocusModeEnum.None };
        health.Toggled += enabled => { _allHealth = enabled; SaveTerminalSettings(); };
        menu.AddChild(health);
        menu.AddChild(TerminalButton("Консоль разработчика", "F12", () => { ToggleMatchMenu(); _diagnostics!.Visible = !_diagnostics.Visible; }));
        menu.AddChild(TerminalButton("Перезапустить матч", "Сбросить текущий матч", () => { ToggleMatchMenu(); StartSession(); }));
        menu.AddChild(TerminalButton("Выйти", "Закрыть OpenRTS", () => GetTree().Quit()));
    }

    private void ToggleMatchMenu()
    {
        if (_session is null) return;
        if (!_matchMenu!.Visible)
        {
            _pauseBeforeMenu = _paused;
            _paused = true;
            _pointerDown = false;
            _rtsOverlay!.Marquee = null;
        }
        else _paused = _pauseBeforeMenu;
        _matchMenu.Visible = !_matchMenu.Visible;
        _session.SetPlayback(_playbackSpeed, _paused);
    }

    private Rect2 Playfield()
    {
        var field = GetViewport().GetVisibleRect();
        if (_terminal is not null)
            field.Size -= _rightHud ? new Vector2(SidebarWidth, 0) : new Vector2(0, FooterHeight);
        return field;
    }

    private void CenterCamera(Vector2 world)
    {
        if (GroundPoint(Playfield().GetCenter()) is { } center)
            MoveCamera(new(world.X - center.X, 0, world.Y - center.Z));
    }

    private void UpdateTerminal(FrameExchange.FrameLease frame, float alpha)
    {
        if (_terminal is null) return;
        if (_focusStartingBase && Selected() is { } startBase)
        {
            _focusStartingBase = false;
            _camera!.Size = _baseCameraSize;
            CenterCamera(new Vector2(startBase.Current.X, startBase.Current.Y) / WorldPosition.UnitsPerCell);
        }
        var field = Playfield();
        if (_displayBaseGrid is { } grid && GroundPoint(field.Position) is { } start && GroundPoint(field.End) is { } end)
            _minimap!.UpdateMap(grid, _catalog!.Locomotion!, _displayPoses.AsSpan(0, _displayCount), frame.Tick,
                new Rect2(start.X, start.Z, end.X - start.X, end.Z - start.Z));

        foreach (var player in frame.Players)
            if (player.Id == _constructionViewer)
            {
                _terminalResources!.Text = $"{player.Name}  ·  {player.Credits:N0} кр.  ·  ⚡ {player.EnergyProduced}/{player.EnergyConsumed}";
                _terminalResources.TooltipText = $"{player.Name}\nКредиты: {player.Credits:N0}\nЭнергия: {player.EnergyProduced} / {player.EnergyConsumed}";
                _terminalResources.Modulate = player.LowPower ? TerminalAmber : Colors.White;
                break;
            }
        _terminalNotice!.Text = _paused ? "Пауза" : _notice.Length > 0 ? _notice
            : _sitePlacement is not null || _placing ? "Выберите место строительства · Esc отмена"
            : HasTargetingMode() && !_pointerDown ? "Укажите цель · Esc отмена" : "";
        _terminalNotice.TooltipText = _terminalNotice.Text;

        var pose = Selected();
        if (pose is { } selected)
        {
            string name = _catalog!.Presentations[selected.UnitType].Name;
            int max = _catalog.Units[selected.UnitType].MaxHealth;
            _terminalPortrait!.Texture = UnitIcon(selected.UnitType);
            _terminalSelection!.Text = $"{name}  ×{_selection.Count}\n{selected.Health}/{max} HP" + ConstructionStatus();
            _terminalSelection.TooltipText = _terminalSelection.Text;
            _terminalHealth!.MaxValue = max;
            _terminalHealth.Value = selected.Health;
        }
        else
        {
            _terminalPortrait!.Texture = null;
            _terminalSelection!.Text = _selectedProject.Value == 0 ? "Выберите объект" : "Проект строительства";
            _terminalSelection.TooltipText = "";
        }
        _terminalHealth!.Visible = pose is not null;
        UpdateHealthBars(alpha, field);
    }

    private void UpdateHealthBars(float alpha, Rect2 field)
    {
        var bars = _rtsOverlay!.HealthBars;
        bars.Clear();
        for (int i = 0; i < _displayCount; i++)
        {
            var pose = _displayPoses[i];
            var type = _catalog!.Units[pose.UnitType];
            bool selected = _selection.Contains(pose.Id);
            if (!selected && !_allHealth && pose.Health >= type.MaxHealth) continue;
            float x = Mathf.Lerp(pose.Previous.X, pose.Current.X, alpha) / 1024;
            float z = Mathf.Lerp(pose.Previous.Y, pose.Current.Y, alpha) / 1024;
            float y = Mathf.Lerp(pose.Previous.Elevation, pose.Current.Elevation, alpha) / 1024;
            float depth = (type.Footprint?.HalfHeight ?? type.Body.HalfHeight) / 1024f;
            var point = _camera!.UnprojectPosition(new(x, y, z - depth));
            float width = type.Footprint is null ? 26 : 44;
            var rect = new Rect2(point.X - width / 2, point.Y - 10, width, 5);
            if (field.Encloses(rect)) bars.Add((rect, Mathf.Clamp(pose.Health / (float)type.MaxHealth, 0, 1)));
        }
    }
}
