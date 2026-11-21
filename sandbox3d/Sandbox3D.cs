using OpenRTS.Godot.Rendering;
using Godot;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Presentation;
using OpenRTS.Godot.Controls;

public partial class Sandbox3D : Node3D
{
    private const int CellSize = TerrainScenario.CellSize;
    private static readonly Vector2 MapSize = new(TerrainScenario.Width, TerrainScenario.Depth);

    private readonly SelectionState _selection = new();
    private readonly Dictionary<EntityId, ActorView> _actors = new();
    private readonly HashSet<EntityId> _displayed = [];
    private readonly List<EntityId> _removed = [];
    private SimulationSession? _session;
    private TerrainScenario _scenario = null!;
    private TerrainSurface _terrain = null!;
    private ActorModel[] _models = [];
    private Camera3D _camera = null!;
    private RtsOverlay _overlay = null!;
    private Label _status = null!;
    private MeshInstance3D _goal = null!;
    private Mesh _selectionMesh = null!, _targetMesh = null!;
    private float _baseCameraSize, _goalTime;
    private bool _verify, _failed;
    private Rect2 ViewRect => GetViewport().GetVisibleRect();

    public override void _Ready()
    {
        try
        {
            _verify = OS.GetCmdlineUserArgs().Contains("--verify");
            var terrain = LocomotionCatalog.Load([ReadContent("res://terrain.yaml")]);
            var weapons = WeaponCatalog.Load([ReadContent("res://weapons.yaml")]);
            var catalog = UnitCatalog.Load([ReadContent("res://units.yaml")], terrain, weapons);
            _scenario = new(catalog);
            _terrain = new(_scenario.Grid);
            GetNode<MeshInstance3D>("Ground").Mesh = _terrain.Mesh;
            _models = catalog.Units.Select(LoadModel).ToArray();

            _camera = GetNode<Camera3D>("Camera");
            _baseCameraSize = _camera.Size;
            var window = GetWindow();
            if (_verify && DisplayServer.GetName() == "headless")
                window.Size = new(ProjectSettings.GetSetting("display/window/size/viewport_width").AsInt32(),
                    ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32());
            window.FocusExited += CancelDrag;
            _selectionMesh = RingMesh(new Color("#84efb9"));
            _targetMesh = RingMesh(new Color("#f28b77"));
            _goal = new MeshInstance3D { Mesh = RingMesh(new Color("#ffe184")), Visible = false };
            AddChild(_goal);
            CreateRocketBatch();

            var layer = new CanvasLayer();
            AddChild(layer);
            _overlay = new RtsOverlay { ShowCursor = false };
            layer.AddChild(_overlay);
            var hints = new Label
            {
                Position = new Vector2(16, 12),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Text = "ЛКМ / рамка — выбор    ПКМ — движение или атака    Shift — очередь\n" +
                    "Стрелки / СКМ — камера    Колесо — масштаб    Пробел — к выбору\n" +
                    "M — цель движения    S / H — стоп / удерживать    Ctrl+1–9 — сохранить группу"
            };
            hints.AddThemeConstantOverride("outline_size", 5);
            hints.AddThemeColorOverride("font_outline_color", new Color("#141c1d"));
            layer.AddChild(hints);
            _status = new Label { Position = new Vector2(16, 80), MouseFilter = Control.MouseFilterEnum.Ignore };
            _status.AddThemeConstantOverride("outline_size", 5);
            _status.AddThemeColorOverride("font_outline_color", new Color("#141c1d"));
            layer.AddChild(_status);

            _session = new SimulationSession(_scenario.CreateWorld, startPaused: _verify);
            UpdateStatus();
        }
        catch (Exception error) { Fail(error); }
    }

    private static ContentSource ReadContent(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null) throw new IOException($"Cannot open {path}: {Godot.FileAccess.GetOpenError()}");
        return new(path, file.GetAsText());
    }

    public override void _Process(double delta)
    {
        if (_session is null) return;
        try
        {
            if (_session.Completion.IsCompleted) _session.Completion.GetAwaiter().GetResult();
            if (!_verify) PanCamera(delta);
            if (_goal.Visible && (_goalTime -= (float)delta) <= 0) _goal.Visible = false;
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;

            float alpha = _session.GetInterpolationAlpha(frame.Tick);
            UpdateActors(frame.Poses, frame.Weapons, alpha, frame.Tick);
            UpdateRockets(frame.Projectiles, alpha);
            while (_session.TryReadEvent(out _)) { }
            if (_verify) VerifyControls(frame);
        }
        catch (Exception error) { Fail(error); }
    }

    private void UpdateActors(ReadOnlySpan<EntityPose> poses, ReadOnlySpan<WeaponPose> weapons, float alpha, long tick)
    {
        _displayed.Clear();
        foreach (var pose in poses)
        {
            if (pose.Carrier.Value != 0) continue;
            _displayed.Add(pose.Id);
            if (!_actors.TryGetValue(pose.Id, out var actor))
            {
                var marker = pose.Presentation.Owner == 1 ? _selectionMesh : _targetMesh;
                actor = new ActorView(_models[pose.UnitType], marker, pose.UnitType);
                AddChild(actor.Node);
                _actors.Add(pose.Id, actor);
            }
            var weapon = pose.WeaponCount == 0 ? default : weapons[pose.WeaponOffset];
            actor.Apply(pose, alpha, _terrain.NormalAt(pose.Current), tick, weapon);
        }
        _removed.Clear();
        foreach (var (id, actor) in _actors)
            if (!_displayed.Contains(id))
            {
                actor.Node.QueueFree();
                _removed.Add(id);
            }
        if (_removed.Count == 0) return;
        foreach (var id in _removed) _actors.Remove(id);
        _selection.Prune(_actors.ContainsKey);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        foreach (var (id, view) in _actors) view.SetSelected(_selection.Contains(id));
        _status.Text = _moveArmed ? "Укажите цель движения (ЛКМ)" : $"Выбрано: {_selection.Count}";
    }

    public override void _ExitTree()
    {
        GetWindow().FocusExited -= CancelDrag;
        _session?.Dispose();
        _session = null;
    }

    private void Fail(Exception error)
    {
        GD.PushError(error.ToString());
        _failed = true;
        SetProcess(false);
        GetTree().Quit(1);
    }
}
