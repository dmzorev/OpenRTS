using System;
using System.Diagnostics;
using System.Text.Json;
using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Godot;
using OpenRTS.Core;

public partial class Sandbox : Node3D
{
    private SimulationSession? _session;
    private UnitCatalog? _catalog;
    private WorldView? _view;
    private Camera3D? _camera;
    private Label? _label;
    private int _count = 10000, _ticks = DiagnosticScenario.DefaultTicks;
    private bool _verifySimulation, _pacedVerification, _verificationDone;
    private bool _verifyControls;
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
                if (arg == "--verify-simulation") { _verifySimulation = true; continue; }
                if (arg == "--verify-controls") { _verifyControls = true; _paused = true; continue; }
                if (arg == "--realtime") { _pacedVerification = true; continue; }
                if (arg == "--navigation") { _navigation = true; continue; }
                if (arg == "--reverse-jobs") { _reverseJobs = true; continue; }
                if (arg.StartsWith("--count=", StringComparison.Ordinal) && int.TryParse(arg[8..], out int value) && value is >= 1 and <= 1000000) { _count = value; continue; }
                if (arg.StartsWith("--ticks=", StringComparison.Ordinal) && int.TryParse(arg[8..], out value) && value is >= 1 and <= 1000000) { _ticks = value; continue; }
                if (arg.StartsWith("--fps=", StringComparison.Ordinal) && int.TryParse(arg[6..], out value) && value is >= 1 and <= 240) { Engine.MaxFps = value; continue; }
                if (arg.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(arg[10..], out value) && value is >= 1 and <= 64) { _workers = value; continue; }
                throw new ArgumentException("Параметры: --verify, --verify-simulation, --verify-controls, --realtime, --navigation, --workers=1..64, --reverse-jobs, --count=1..1000000 (лимит диагностического стенда), --ticks=1..1000000, --fps=1..240.");
            }
            if ((verify ? 1 : 0) + (_verifySimulation ? 1 : 0) + (_verifyControls ? 1 : 0) > 1) throw new ArgumentException("Выберите один режим проверки.");
            using var file = Godot.FileAccess.Open("res://content/units.yaml", Godot.FileAccess.ModeFlags.Read)
                ?? throw new InvalidOperationException("Не удалось открыть content/units.yaml: " + Godot.FileAccess.GetOpenError());
            _catalog = UnitCatalog.Load([new ContentSource("content/units.yaml", file.GetAsText())]);
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
                _view = new WorldView(_catalog.Units[0], _count);
                AddChild(_view.Node);
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
        _notice = "";
        var catalog = _catalog ?? throw new InvalidOperationException("Контент не загружен.");
        _session = new SimulationSession(() => _navigation ? NavigationScenario.Create(catalog, _count, _workers, _reverseJobs) : DiagnosticScenario.Create(catalog, _count),
            _navigation ? (_verifySimulation ? NavigationScenario.Commands(_count, _ticks) : [])
                : DiagnosticScenario.Commands(_count, _verifySimulation ? _ticks : long.MaxValue),
            _verifySimulation ? _ticks : null, realtime: !_verifySimulation || _pacedVerification,
            maximumEventBacklog: Math.Max(65536, _count + 1024),
            playbackSpeed: _verifySimulation ? 1 : _playbackSpeed, startPaused: !_verifySimulation && _paused);
    }

    public override void _Process(double delta)
    {
        if (!_verifySimulation) PanCamera(delta);
        if (_session is null || _view is null || _verificationDone) return;
        try
        {
            bool completed = _session.Completion.IsCompleted;
            if (_session.Completion.IsFaulted) _session.Completion.GetAwaiter().GetResult();
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;
            if (_verifyControls) { VerifyControls(frame.Tick); return; }
            float alpha = completed ? 1 : _session.GetInterpolationAlpha(frame.Tick);
            _view.Apply(frame.Poses, alpha);
            _displayGrid = frame.Grid;
            _obstacles?.Apply(frame.Grid);
            while (_session.TryReadEvent(out var value))
                _notice = value.Kind switch
                {
                    WorldEventKind.NoRoute => "Нет маршрута или недостаточно связного места для группы.",
                    WorldEventKind.CellOccupied => "Изменение отклонено: клетка занята или зарезервирована.",
                    WorldEventKind.CellBlocked => "Создание отклонено: клетка непроходима.",
                    _ => _notice
                };
            if (_label is not null)
                _label.Text = $"OpenRTS · симуляция {frame.TickRate} Гц · ×{_playbackSpeed:0.###}{(_paused ? " · ПАУЗА" : "")}\n\n{frame.Poses.Length:N0} объектов · тик {frame.Tick} · {Engine.GetFramesPerSecond()} FPS\n" +
                    (_navigation ? $"Навигация · до {_workers} потоков поиска\nПриказ выполняют {frame.MovingCount} · ожидают {frame.NavigationWaitingCount} (очередь {frame.NavigationQueueWaitingCount})\nОбходов {frame.NavigationLocalSearches} · уступаний {frame.NavigationYields} за тик\n\nПКМ — компактная область · Пробел — сменить сторону\nB — препятствие под курсором · R — перезапуск\n\nОдна клетка на юнит. Обходы и уступание ограничены."
                        : "Отдельный поток · целые координаты · MultiMesh\n\nПКМ — приказ всем · Пробел — случайные цели\nR — перезапуск · Esc — выход\n\nПрямое движение без навигации и столкновений.") + "\nКолесо — масштаб · WASD/стрелки/СКМ — камера\n+/− — темп · P — пауза · N — один тик" + (_notice.Length == 0 ? "" : "\nПоследний отказ: " + _notice);
            if (_verifySimulation && completed)
            {
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
                int capacity = _view.Node.Multimesh.InstanceCount;
                _view.Apply(ReadOnlySpan<EntityPose>.Empty, 1);
                if (_view.Node.Multimesh.VisibleInstanceCount != 0)
                    throw new InvalidOperationException("Пустое состояние не скрыло экземпляры.");
                _view.Apply(frame.Poses, 0.5f);
                if (frame.Poses.Length > 0 && DisplayServer.GetName() != "headless")
                {
                    var last = _view.Node.Multimesh.GetInstanceTransform(frame.Poses.Length - 1).Origin;
                    var pose = frame.Poses[^1];
                    var expected = new Vector3((pose.Previous.X + pose.Current.X) / 2048f,
                        (pose.Previous.Elevation + pose.Current.Elevation) / 2048f + 0.25f,
                        (pose.Previous.Y + pose.Current.Y) / 2048f);
                    if (!last.IsEqualApprox(expected)) throw new InvalidOperationException($"Неверная позиция представления: {last}, ожидалась {expected}.");
                }
                _view.Apply(frame.Poses, 1);
                if (_view.Node.Multimesh.InstanceCount != capacity || _view.Node.Multimesh.VisibleInstanceCount != frame.Poses.Length)
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
                if (code == Key.Escape) GetTree().Quit();
                else if (code == Key.R) StartSession();
                else if (code == Key.P) { _paused = !_paused; _session.SetPlayback(_playbackSpeed, _paused); }
                else if (code == Key.N) { if (!_paused) { _paused = true; _session.SetPlayback(_playbackSpeed, true); } _session.StepOnce(); }
                else if (code is Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract)
                {
                    bool slower = code is Key.Minus or Key.KpSubtract;
                    _playbackSpeed = Math.Clamp(_playbackSpeed * (slower ? 0.5 : 2), 0.125, 8);
                    _session.SetPlayback(_playbackSpeed, _paused);
                }
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
                else if (code == Key.B && _displayGrid is not null && MousePoint(GetViewport().GetMousePosition()) is { } target)
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
            }
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
        int width = (_displayGrid?.Width ?? 4096) * 1024, height = (_displayGrid?.Height ?? 4096) * 1024;
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
        int x = (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right) ? 1 : 0)
            - (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ? 1 : 0);
        int z = (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ? 1 : 0)
            - (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ? 1 : 0);
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
        _session?.Dispose();
        _session = null;
    }

    private void BuildScene(int count, UnitCatalog catalog)
    {
        float side = DiagnosticScenario.SideCells(count);
        var center = new Vector3(side / 2, 0, side / 2);
        Vector2 groundSize = new(side, side);
        if (_navigation)
        {
            var grid = NavigationScenario.Grid(count);
            side = Math.Max(grid.Width, grid.Height);
            center = new(grid.Width / 2f, 0, grid.Height / 2f);
            groundSize = new(grid.Width, grid.Height);
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
        AddChild(ground);
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = side * 1.18f,
            Position = center + new Vector3(0, side, side * 0.7f),
            Far = side * 4,
            Current = true
        };
        AddChild(camera);
        camera.LookAt(center);
        _camera = camera;
        _baseCameraSize = camera.Size;
        _mapWidth = groundSize.X; _mapDepth = groundSize.Y;
        _label = AddOverlay("OpenRTS · запуск симуляции…");
    }

    private Label AddOverlay(string text)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer { Position = new Vector2(24, 24) };
        layer.AddChild(panel);
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
