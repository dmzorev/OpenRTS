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

    public override void _Ready()
    {
        bool verify = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--verify");
        try
        {
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--verify") continue;
                if (arg == "--verify-simulation") { _verifySimulation = true; continue; }
                if (arg == "--realtime") { _pacedVerification = true; continue; }
                if (arg.StartsWith("--count=", StringComparison.Ordinal) && int.TryParse(arg[8..], out int value) && value is >= 1 and <= 1000000) { _count = value; continue; }
                if (arg.StartsWith("--ticks=", StringComparison.Ordinal) && int.TryParse(arg[8..], out value) && value is >= 1 and <= 1000000) { _ticks = value; continue; }
                if (arg.StartsWith("--fps=", StringComparison.Ordinal) && int.TryParse(arg[6..], out value) && value is >= 1 and <= 240) { Engine.MaxFps = value; continue; }
                throw new ArgumentException("Параметры: --verify, --verify-simulation, --realtime, --count=1..1000000 (лимит диагностического стенда), --ticks=1..1000000, --fps=1..240.");
            }
            if (verify && _verifySimulation) throw new ArgumentException("Выберите один режим проверки.");
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
                BuildScene(_count, _catalog);
                StartSession();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            if (verify || _verifySimulation) GetTree().Quit(1);
            else AddOverlay("Ошибка загрузки контента\n" + error.Message);
        }
    }

    private void StartSession()
    {
        _session?.Dispose();
        _session = null;
        var catalog = _catalog ?? throw new InvalidOperationException("Контент не загружен.");
        _session = new SimulationSession(() => DiagnosticScenario.Create(catalog, _count),
            DiagnosticScenario.Commands(_count, _verifySimulation ? _ticks : long.MaxValue),
            _verifySimulation ? _ticks : null, realtime: !_verifySimulation || _pacedVerification);
    }

    public override void _Process(double delta)
    {
        if (_session is null || _view is null || _verificationDone) return;
        try
        {
            bool completed = _session.Completion.IsCompleted;
            if (_session.Completion.IsFaulted) _session.Completion.GetAwaiter().GetResult();
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;
            float alpha = completed ? 1 : (float)Math.Clamp(
                Stopwatch.GetElapsedTime(frame.Timestamp).TotalSeconds * frame.TickRate, 0, 1);
            _view.Apply(frame.Poses, alpha);
            while (_session.TryReadEvent(out _)) { }
            if (_label is not null)
                _label.Text = $"OpenRTS · симуляция {frame.TickRate} Гц\n\n{frame.Poses.Length:N0} объектов · тик {frame.Tick} · {Engine.GetFramesPerSecond()} FPS\n" +
                    "Отдельный поток · целые координаты · MultiMesh\n\nПКМ — приказ всем · Пробел — случайные цели\nR — перезапуск · Esc — выход\n\nПрямое движение без навигации и столкновений.";
            if (_verifySimulation && completed)
            {
                var report = _session.Completion.GetAwaiter().GetResult();
                if (frame.Tick != report.Tick || frame.Poses.Length != report.EntityCount)
                    throw new InvalidOperationException("Не опубликован конечный кадр симуляции.");
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
            if (_verifySimulation) GetTree().Quit(1);
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
                if (key.Keycode == Key.Escape) GetTree().Quit();
                else if (key.Keycode == Key.R) StartSession();
                else if (key.Keycode == Key.Space)
                {
                    int side = DiagnosticScenario.SideCells(_count) * WorldPosition.UnitsPerCell;
                    _session.Submit(CommandKind.Scatter, new(side, side));
                }
            }
            else if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouse && _camera is not null)
            {
                var origin = _camera.ProjectRayOrigin(mouse.Position);
                var direction = _camera.ProjectRayNormal(mouse.Position);
                if (MathF.Abs(direction.Y) < 0.0001f) return;
                float distance = -origin.Y / direction.Y;
                if (distance < 0) return;
                var point = origin + direction * distance;
                int maximum = 4096 * WorldPosition.UnitsPerCell;
                _session.Submit(CommandKind.MoveAll, new((int)Math.Clamp(point.X * 1024, 0, maximum),
                    (int)Math.Clamp(point.Z * 1024, 0, maximum)));
            }
        }
        catch (Exception error) { GD.PushError(error.Message); }
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
        var ground = new MeshInstance3D
        {
            Position = center,
            Mesh = new PlaneMesh { Size = new Vector2(side, side) },
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
