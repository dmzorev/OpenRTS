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
    private SimulationSession? _session;
    private UnitCatalog? _catalog;
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
        try
        {
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--verify") continue;
                if (arg == "--verify-actions") { _verifyActions = _buildings = _showcase = _paused = true; continue; }
                if (arg == "--verify-production") { _productionScenario = _buildings = _showcase = _verifySimulation = true; _ticks = 160; continue; }
                if (arg == "--skirmish") { _skirmish = _buildings = _showcase = true; continue; }
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
                throw new ArgumentException("Параметры: --buildings, --verify-buildings, --verify-actions, --verify-production, --showcase, --verify-presentation, --capture=path, --verify, --verify-simulation, --verify-controls, --realtime, --navigation, --workers=1..64, --reverse-jobs, --count=1..1000000 (лимит диагностического стенда), --ticks=1..1000000, --fps=1..240.");
            }
            if (!_navigation && !_verifySimulation && !_verifyControls && !verify && !Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--count=", StringComparison.Ordinal)) && !_showcase) _skirmish = _buildings = _showcase = true;
            if (_showcase && _navigation) throw new ArgumentException("Полигон и диагностическая навигация — отдельные режимы.");
            if (_showcase && _count != ShowcaseScenario.UnitCount && Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith("--count=", StringComparison.Ordinal)))
                throw new ArgumentException("Полигон содержит 10 юнитов; для нагрузок используйте --navigation --count=... .");
            if (_showcase) _count = _skirmish ? SkirmishScenario.UnitCount : _buildings ? BuildingScenario.UnitCount : ShowcaseScenario.UnitCount;
            if (_verifyBuildings) { _verifySimulation = true; _ticks = BuildingScenario.VerificationTicks; }
            if (_verifyPresentation) { _verifySimulation = true; _ticks = 12; }
            if ((verify ? 1 : 0) + (_verifySimulation ? 1 : 0) + (_verifyControls ? 1 : 0) > 1) throw new ArgumentException("Выберите один режим проверки.");
            using var file = Godot.FileAccess.Open("res://content/units.yaml", Godot.FileAccess.ModeFlags.Read)
                ?? throw new InvalidOperationException("Не удалось открыть content/units.yaml: " + Godot.FileAccess.GetOpenError());
            using var terrain = Godot.FileAccess.Open("res://content/terrain.yaml", Godot.FileAccess.ModeFlags.Read)
                ?? throw new InvalidOperationException("Не удалось открыть terrain.yaml.");
            var unitSources = new System.Collections.Generic.List<ContentSource> { new("content/units.yaml", file.GetAsText()) };
            if (_buildings) unitSources.Add(new("buildings.yaml", System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/buildings.yaml"))));
            _catalog = UnitCatalog.Load(unitSources,
                LocomotionCatalog.Load([new("content/terrain.yaml", terrain.GetAsText())]),
                WeaponCatalog.Load([new("weapons.yaml", System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/weapons.yaml")))]));
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
                if (_showcase)
                {
                    using var models = Godot.FileAccess.Open("res://content/models.yaml", Godot.FileAccess.ModeFlags.Read)
                        ?? throw new InvalidOperationException("Не удалось открыть models.yaml.");
                    var modelSources = new System.Collections.Generic.List<ContentSource> { new("models.yaml", models.GetAsText()) };
                    if (_buildings) modelSources.Add(new("buildings-models.yaml", System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/buildings-models.yaml"))));
                    _modelCatalog = ModelCatalog.Load(modelSources);
                    _sprites = new(_catalog, _modelCatalog, TeamColor);
                    _combatEffects = new(_catalog, EffectCatalog.Load([new("effects.yaml", System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/effects.yaml")))]));
                    AddChild(_combatEffects.Node);
                    AddChild(new TerrainView(_buildings ? BuildingScenario.Width : ShowcaseScenario.Width, _buildings ? BuildingScenario.Height : ShowcaseScenario.Height, _buildings ? 1 : ShowcaseScenario.TileSize, ShowcaseScenario.TerrainIds, _skirmish ? SkirmishScenario.TerrainAt : _buildings ? BuildingScenario.TerrainAt : ShowcaseScenario.TerrainAt, id => GD.Load<Texture2D>("res://resources/terrain/" + id + ".tres")).Node); AddChild(_sprites.Node); CreateSelectionRing();
                }
                else { _view = new WorldView(_catalog.Units[0], _count); AddChild(_view.Node); }
                if (_navigation)
                {
                    _obstacles = new ObstacleView();
                    AddChild(_obstacles.Node);
                }
                BuildScene(_count, _catalog);
                StartSession();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            if (verify || _verifySimulation || _verifyControls) GetTree().Quit(1);
            else AddOverlay("Ошибка загрузки контента\n" + error.Message);
        }
    }

    private void StartSession()
    {
        _session?.Dispose();
        _session = null;
        _leftGoal = false;
        if (_showcase) { _selection.Reset(); _selected = new(1); _armedAction = null; _armedCustom = null; _productionPlacement = null; _placing = false; _panelRevision = -1; _panelTick = -1; _panelDirty = true; _pointerDown = false; }
        _notice = "";
        var catalog = _catalog ?? throw new InvalidOperationException("Контент не загружен.");
        _combatEffects?.Clear(); _sprites?.ClearWrecks();
        _session = new SimulationSession(() => _skirmish ? SkirmishScenario.Create(catalog, _workers) : _buildings ? BuildingScenario.Create(catalog, _workers) : _showcase ? ShowcaseScenario.Create(catalog) : _navigation ? NavigationScenario.Create(catalog, _count, _workers, _reverseJobs) : DiagnosticScenario.Create(catalog, _count),
            _skirmish ? [] : _productionScenario ? ProductionScenario.Commands(catalog) : _buildings ? BuildingScenario.Commands(catalog) : _showcase ? (_verifyPresentation ? ShowcaseScenario.VerificationCommands(catalog) : []) : _navigation ? (_verifySimulation ? NavigationScenario.Commands(_count, _ticks) : [])
                : DiagnosticScenario.Commands(_count, _verifySimulation ? _ticks : long.MaxValue),
            _verifySimulation ? _ticks : null, realtime: !_verifySimulation || _pacedVerification,
            maximumEventBacklog: Math.Max(65536, _count + 1024),
            playbackSpeed: _verifySimulation ? 1 : _playbackSpeed, startPaused: !_verifySimulation && _paused);
    }

    private static Color TeamColor(uint owner) => owner switch { 1 => new(0.14f, 0.52f, 1), 2 => new(1, 0.20f, 0.16f), _ => new(0.72f, 0.78f, 0.80f) };

    public override void _Process(double delta)
    {
        if (!_verifySimulation) PanCamera(delta);
        if (_session is null || _view is null && _sprites is null || _verificationDone) return;
        try
        {
            bool completed = _session.Completion.IsCompleted;
            if (_session.Completion.IsFaulted) _session.Completion.GetAwaiter().GetResult();
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;
            float alpha = completed ? 1 : _session.GetInterpolationAlpha(frame.Tick);
            _view?.Apply(frame.Poses, alpha);
            if (_showcase)
            {
                if (_displayPoses.Length < frame.Poses.Length) Array.Resize(ref _displayPoses, frame.Poses.Length);
                frame.Poses.CopyTo(_displayPoses); _displayCount = frame.Poses.Length;
                RefreshSelection(); UpdateCommandPanel(frame); UpdateSelection(alpha); UpdateRtsCursor(); if (_buildings) UpdateFootprintPreview();
            }
            _displayGrid = frame.Grid;
            _obstacles?.Apply(frame.Grid);
            while (_session.TryReadEvent(out var value))
            {
                _combatEffects?.Accept(value); _sprites?.Accept(value);
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
                    WorldEventKind.QueueFull => "Очередь приказов заполнена (32).",
                    WorldEventKind.NoRoute => "Маршрут или место остановки недоступны.",
                    WorldEventKind.CellOccupied => "Изменение отклонено: область пересекает тело юнита.",
                    WorldEventKind.CellBlocked => "Создание отклонено: клетка непроходима.",
                    _ => _notice
                };
            }
            _sprites?.Apply(frame.Poses, alpha, frame.Tick, frame.TickRate, frame.Weapons);
            _combatEffects?.Apply(frame.Projectiles, alpha, frame.Tick);
            if (_verifySkirmish) { VerifySkirmish(frame); return; }
            if (_verifyActions) { VerifyActions(frame); return; }
            if (_verifyControls) { if (_verifyRtsControls) VerifyRtsControls(frame.Tick); else VerifyControls(frame.Tick); return; }
            if (_showcase && _label is not null) _label.Text = ShowcaseLabel(frame.Tick) + "\n" + string.Join("\n", frame.Players.ToArray().Where(p => p.Id != 0).Select(p => $"{p.Name}: кредиты {p.Credits} · энергия {p.EnergyProduced}/{p.EnergyConsumed}{(p.LowPower ? " · LOWPOWER" : "")}")) + (_skirmish ? "\nSkirmish · управление синим и красным игроками · захват: W или ПКМ инженером" : "");
            else if (_label is not null)
                _label.Text = $"OpenRTS · симуляция {frame.TickRate} Гц · ×{_playbackSpeed:0.###}{(_paused ? " · ПАУЗА" : "")}\n\n{frame.Poses.Length:N0} объектов · тик {frame.Tick} · {Engine.GetFramesPerSecond()} FPS\n" +
                    (_navigation ? $"Навигация · до {_workers} потоков поиска\nПриказ выполняют {frame.MovingCount} · ожидают {frame.NavigationWaitingCount} (очередь {frame.NavigationQueueWaitingCount})\nОбходов {frame.NavigationLocalSearches} · уступаний {frame.NavigationYields} за тик\n\nПКМ — компактная область · Пробел — сменить сторону\nB — препятствие под курсором · R — перезапуск\n\nТочные координаты и свободный угол. Локальные обходы ограничены."
                        : "Отдельный поток · целые координаты · MultiMesh\n\nПКМ — приказ всем · Пробел — случайные цели\nR — перезапуск · Esc — выход\n\nПрямое движение без навигации и столкновений.") + "\nКолесо — масштаб · WASD/стрелки/СКМ — камера\n+/− — темп · P — пауза · N — один тик" + (_notice.Length == 0 ? "" : "\nПоследний отказ: " + _notice);
            if (_productionScenario && completed)
            {
                var production = _session.Completion.GetAwaiter().GetResult();
                if (frame.Tick != production.Tick || frame.Poses.Length != production.EntityCount || frame.Tasks.Length != 3)
                    throw new InvalidOperationException("Final production snapshot was incomplete.");
                _verificationDone = true; GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(production)); GetTree().Quit(); return;
            }
            if (_verifyBuildings && completed) { VerifyBuildings(frame); return; }
            if (_verifyPresentation && completed)
            {
                VerifyPresentation(frame); return;
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
            _session.Dispose();
            _session = null;
            if (_verifySimulation || _verifyControls) GetTree().Quit(1);
            else if (_label is not null) _label.Text = "Ошибка симуляции\n" + error.Message;
        }
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (_verifySimulation || _session is null) return;
        try
        {
            if (input is InputEventKey { Pressed: true, Echo: false } key)
            {
                Key code = key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode;
                if (_showcase && HandleRtsKey(key, code)) { }
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
            else if (input is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                ZoomCamera(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? 0.8f : 1.25f);
            }
            else if (input is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Middle) != 0 && _camera is not null)
            {
                var before = GroundPoint(motion.Position - motion.Relative);
                var after = GroundPoint(motion.Position);
                if (before is { } from && after is { } to) MoveCamera(from - to);
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
        if (_camera is null) return null;
        var origin = _camera.ProjectRayOrigin(mouse);
        var direction = _camera.ProjectRayNormal(mouse);
        if (MathF.Abs(direction.Y) < 0.0001f) return null;
        float distance = -origin.Y / direction.Y;
        return distance < 0 ? null : origin + direction * distance;
    }

    private WorldPosition? MousePoint(Vector2 mouse)
    {
        if (GroundPoint(mouse) is not { } point) return null;
        int width = _displayGrid is { } grid ? grid.Width * grid.CellSize : 4096 * 1024;
        int height = _displayGrid is { } map ? map.Height * map.CellSize : 4096 * 1024;
        return new((int)Math.Clamp(point.X * 1024, 0, width), (int)Math.Clamp(point.Z * 1024, 0, height));
    }

    private void ZoomCamera(Vector2 mouse, float factor)
    {
        if (_camera is null) return;
        var before = GroundPoint(mouse);
        _camera.Size = Math.Clamp(_camera.Size * factor, Math.Min(4, _baseCameraSize), _baseCameraSize * 2);
        if (before is { } from && GroundPoint(mouse) is { } to) MoveCamera(from - to);
    }

    private void PanCamera(double delta)
    {
        if (_camera is null) return;
        int x = (!_showcase && Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right) ? 1 : 0)
            - (!_showcase && Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ? 1 : 0);
        int z = (!_showcase && Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ? 1 : 0)
            - (!_showcase && Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ? 1 : 0);
        var direction = new Vector3(x, 0, z);
        if (direction != Vector3.Zero) MoveCamera(direction.Normalized() * _camera.Size * (float)delta * 0.7f);
    }

    private void MoveCamera(Vector3 offset)
    {
        if (_camera is null || GroundPoint(GetViewport().GetVisibleRect().Size / 2) is not { } center) return;
        offset.Y = 0;
        offset.X = Math.Clamp(center.X + offset.X, 0, _mapWidth) - center.X;
        offset.Z = Math.Clamp(center.Z + offset.Z, 0, _mapDepth) - center.Z;
        _camera.Position += offset;
    }

    public override void _ExitTree()
    {
        if (_showcase) Input.MouseMode = Input.MouseModeEnum.Visible;
        _session?.Dispose();
        _session = null;
    }

    private void BuildScene(int count, UnitCatalog catalog)
    {
        float side = DiagnosticScenario.SideCells(count);
        var center = new Vector3(side / 2, 0, side / 2);
        Vector2 groundSize = new(side, side);
        if (_navigation || _showcase)
        {
            var grid = _skirmish ? SkirmishScenario.Grid(_catalog!.Locomotion!) : _buildings ? BuildingScenario.Grid(_catalog!.Locomotion!) : _showcase ? ShowcaseScenario.Grid(_catalog!.Locomotion) : NavigationScenario.Grid(count);
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
            Size = _buildings ? 64 : _showcase ? 28 : side * 1.18f,
            Position = center + new Vector3(0, side, 0),
            Far = side * 4,
            Current = true
        };
        AddChild(camera);
        camera.RotationDegrees = new(-90, 0, 0);
        _camera = camera;
        _baseCameraSize = camera.Size;
        _mapWidth = groundSize.X; _mapDepth = groundSize.Y;
        _label = AddOverlay("OpenRTS · запуск симуляции…");
        if (_showcase) AddShowcaseFooter();
    }

    private Label AddOverlay(string text)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer { Position = new Vector2(24, 24) };
        layer.AddChild(panel);
        if (_showcase) _rtsPanels.Add(panel);
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
