using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

/// <summary>Projectile snapshots and independent event effects, batched without per-shot nodes.</summary>
public sealed partial class CombatEffectsView
{
    private readonly UnitCatalog _units;
    private readonly EffectCatalog _effects;
    private readonly ModelDefinition?[] _models;
    private readonly List<WorldEvent> _events = [];
    private readonly Dictionary<EntityId, EntityPose> _workSources = [];
    private readonly Dictionary<string, (ColoredMeshBatch Batch, float Aspect)> _projectileSprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColoredMeshBatch> _projectileModels = new(StringComparer.Ordinal);
    private readonly ColoredMeshBatch _rounds = new(new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 8, Rings = 4 }, "Projectiles");
    private readonly ColoredMeshBatch _beams = new(new BoxMesh { Size = Vector3.One }, "Tracers");
    private readonly ColoredMeshBatch _impacts = new(new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 12, Rings = 4 }, "Impacts");
    private Vector3 _overlayOffset = Vector3.Up * SpriteWorldView.OverlayHeight;
    public Node3D Node { get; } = new() { Name = "CombatEffects" };
    public int ProjectileCount { get; private set; }
    public int ActiveEffects => _events.Count;
    internal void AlignDepthWithCamera(Camera3D camera)
    {
        var back = camera.Basis.Z;
        _overlayOffset = back * (SpriteWorldView.OverlayHeight / back.Y);
    }

    public CombatEffectsView(UnitCatalog units, EffectCatalog effects, ModelCatalog? models = null)
    {
        _units = units; _effects = effects;
        _models = units.Presentations.Select(binding => binding.Model is { } id ? models?.Models.GetValueOrDefault(id) : null).ToArray();
        foreach (var type in units.Units) foreach (var mount in type.Weapons)
        {
            if (!effects.Weapons.TryGetValue(mount.Weapon.Id, out var style)) throw new ArgumentException("Missing effect: " + mount.Weapon.Id);
            if (style.ProjectileModel is { } modelPath && !_projectileModels.ContainsKey(modelPath))
                AddProjectileModel(modelPath, (float)style.Size);
            if (style.ProjectileTexture is not { } path || _projectileSprites.ContainsKey(path)) continue;
            var texture = GD.Load<Texture2D>(path) ?? throw new ArgumentException("Missing projectile texture: " + path);
            var material = new StandardMaterial3D
            {
                AlbedoTexture = texture, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            var batch = new ColoredMeshBatch(new PlaneMesh { Size = Vector2.One }, "ProjectileSprites", material);
            _projectileSprites.Add(path, (batch, (float)texture.GetWidth() / texture.GetHeight()));
            Node.AddChild(batch.Node);
        }
        if (_projectileModels.Count > 0)
        {
            AddProjectileLight(new Vector3(-54, -38, 0), 0.9f);
            AddProjectileLight(new Vector3(-29, 136, 0), 0.45f);
        }
        _hasMountedRockets = effects.Weapons.Values.Any(style => style.MountedRocket is not null);
        Node.AddChild(_rounds.Node); Node.AddChild(_beams.Node); Node.AddChild(_impacts.Node); Node.AddChild(_smoke.Node);
    }
    public void Accept(WorldEvent value)
    { if (value.UnitType >= 0 && value.Kind is (WorldEventKind.WeaponFired or WorldEventKind.ProjectileImpact)) _events.Add(value); }
    public void Clear()
    {
        _events.Clear();
        _puffs.Clear();
        _lastSmokeTick = -1;
    }
    public void Apply(ReadOnlySpan<ProjectilePose> shots, float alpha, long tick, float flightSurface = 0,
        FrameExchange.FrameLease? observation = null, bool reliefProjection = false,
        ReadOnlySpan<EntityPose> actors = default, ReadOnlySpan<WeaponPose> weapons = default, Vector3? cameraBack = null)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        _rounds.Clear(); _beams.Clear(); _impacts.Clear(); ProjectileCount = shots.Length;
        foreach (var sprite in _projectileSprites.Values) sprite.Batch.Clear();
        foreach (var batch in _projectileModels.Values) batch.Clear();
        if (tick < _lastSmokeTick) _puffs.Clear();
        bool emitSmoke = tick != _lastSmokeTick;
        _lastSmokeTick = tick;
        double time = tick + alpha;
        var eye = cameraBack ?? Vector3.Up;
        if (_hasMountedRockets) ApplyMountedRockets(actors, weapons, tick, alpha, eye);
        foreach (var shot in shots)
        {
            var weapon = _units.Units[shot.UnitType].Weapons[shot.Mount].Weapon;
            var style = _effects.Weapons[weapon.Id];
            var current = Position(shot.Current);
            var previous = Position(shot.Previous);
            float heading = MathF.Atan2(shot.Facing.X, shot.Facing.Y);
            var projection = shot.Projection;
            if (!reliefProjection && projection.Hover != HoverEndpoints.None)
            {
                var destination = Position(projection.Destination);
                float sourceLift = Lift(projection.LaunchElevation, HoverEndpoints.Source, projection.Hover, flightSurface);
                float targetLift = Lift(projection.Destination.Elevation, HoverEndpoints.Target, projection.Hover, flightSurface);
                float travelled = projection.Travelled / 1024f;
                float previousTravel = Math.Max(0, travelled - previous.DistanceTo(current));
                previous.Z -= PathLift(previous, previousTravel);
                current.Z -= PathLift(current, travelled);
                var aim = destination - previous.Lerp(current, alpha);
                heading = MathF.Atan2(aim.X, aim.Z - targetLift);

                float PathLift(Vector3 point, float distance) => Mathf.Lerp(sourceLift, targetLift,
                    distance > 0 ? distance / (distance + point.DistanceTo(destination)) : 0);
            }
            var forward = previous.DirectionTo(current);
            if (forward == Vector3.Zero) forward = new(MathF.Sin(heading), 0, MathF.Cos(heading));
            bool handThrown = weapon.Projectile == ProjectileKind.Grenade && LaunchAnimation(shot.UnitType, shot.Mount)?.LaunchLocal is not null;
            if (shot.LaunchProjection is { PreviousWeight: > 0 } anchor &&
                LaunchVisual(shot.UnitType, shot.Mount, shot.Departure, style, forward) is { } launch)
            {
                // Offset the published samples from the muzzle without inserting a separate launch leg.
                var offset = anchor.Origin is { } origin ? launch.Center - Position(origin) :
                    Vector3.Up * (launch.Center.Y - _overlayOffset.Y - anchor.HeightAboveActor / 1024f);
                previous += offset * anchor.PreviousWeight;
                current += offset * anchor.CurrentWeight;
                var tangent = previous.DirectionTo(current);
                if (tangent != Vector3.Zero)
                    forward = anchor.Origin.HasValue && tick == shot.Departure.Tick + 1 && style.MountedRocket is not null
                        ? launch.Direction.Slerp(tangent, alpha) : tangent;
                else if (anchor.Origin.HasValue) forward = launch.Direction;
            }
            var point = previous.Lerp(current, alpha);
            float size = (float)style.Size;
            if (style.ProjectileModel is not null || style.ProjectileTexture is not null) ProjectileVisual(style, point, forward, eye);
            else _rounds.Add(point, new(size, size, handThrown ? size : size * 1.7f), PitchBasis(forward, eye), Color(style.Color));
            if (weapon.Projectile == ProjectileKind.Missile)
            {
                if (emitSmoke) EmitSmoke(point - forward * size, style, time);
            }
            else if (!handThrown) Beam(previous, point, (float)style.BeamWidth, Color(style.Color, 0.5f));
        }
        PublishSmoke(time);
        _workSources.Clear();
        bool sourcesLoaded = false;
        int write = 0;
        for (int read = 0; read < _events.Count; read++)
        {
            var value = _events[read];
            if (observation is { FogCellSize: > 0 } fog)
            {
                var point = value.Kind == WorldEventKind.WeaponFired ? value.SourcePosition : value.Position;
                if (point.X < 0 || point.X >= fog.FogWidth || point.Y < 0 || point.Y >= fog.FogDepth ||
                    fog.FogStates[point.Y / fog.FogCellSize * fog.FogColumns + point.X / fog.FogCellSize] != OpenRTS.Core.Visibility.CellVisibility.Visible) continue;
            }
            var style = Style(value.UnitType, value.Mount);
            double age = time - value.Tick; if (age >= style.DurationTicks) continue;
            _events[write++] = value; if (age < 0) continue;
            float phase = (float)(age / style.DurationTicks); var weapon = _units.Units[value.UnitType].Weapons[value.Mount].Weapon;
            if (value.Kind == WorldEventKind.WeaponFired)
            {
                if (weapon.Projectile == ProjectileKind.Grenade &&
                    LaunchAnimation(value.UnitType, value.Mount)?.LaunchLocal is not null)
                    continue;
                var sourcePoint = value.SourcePosition;
                if (weapon.Effect == WeaponEffect.Repair && observation is not null)
                {
                    if (!sourcesLoaded)
                    {
                        foreach (var pose in observation.Poses) _workSources[pose.Id] = pose;
                        sourcesLoaded = true;
                    }
                    if (_workSources.TryGetValue(value.Entity, out var actor) && actor.Flight.Airborne)
                        sourcePoint = WorkSource(actor, value.Position, alpha);
                }
                var source = Position(sourcePoint);
                if (value.Departure.Tick > 0 && LaunchVisual(value.UnitType, value.Mount, value.Departure, style, Vector3.Forward) is { } launch)
                    source = style.MountedRocket is null ? launch.Center : launch.Center - launch.Direction * (float)style.Size;
                if (!reliefProjection) source.Z -= Lift(sourcePoint.Elevation, HoverEndpoints.Source, value.Hover, flightSurface);
                if (weapon.Projectile == ProjectileKind.Hitscan)
                {
                    var target = Position(value.Position);
                    if (!reliefProjection) target.Z -= Lift(value.Position.Elevation, HoverEndpoints.Target, value.Hover, flightSurface);
                    Beam(source, target, (float)style.BeamWidth, Color(style.Color, 1 - phase));
                }
                _impacts.Add(source, new(0.25f * (1 - phase), 0.05f, 0.25f * (1 - phase)), 0, Color(style.Color, 1 - phase));
            }
            else
            {
                float size = (float)style.ImpactSize * (0.3f + phase);
                var target = Position(value.Position);
                if (!reliefProjection) target.Z -= Lift(value.Position.Elevation, HoverEndpoints.Target, value.Hover, flightSurface);
                _impacts.Add(target, new(size * 2, 0.08f, size * 2), 0, Color(style.ImpactColor, 1 - phase));
            }
        }
        if (write < _events.Count) _events.RemoveRange(write, _events.Count - write);
        _rounds.Publish(); _beams.Publish(); _impacts.Publish();
        foreach (var sprite in _projectileSprites.Values) sprite.Batch.Publish();
        foreach (var batch in _projectileModels.Values) batch.Publish();
    }
    internal WorldPosition WorkSource(in EntityPose actor, WorldPosition target, float alpha)
    {
        var previous = actor.Previous; var current = actor.Current;
        var point = new WorldPosition((int)Mathf.Lerp(previous.X, current.X, alpha), (int)Mathf.Lerp(previous.Y, current.Y, alpha),
            (int)Mathf.Lerp(previous.Elevation, current.Elevation, alpha));
        return ObjectGeometry.ClosestPoint(_units.Units[actor.UnitType], point, target) with { Elevation = point.Elevation };
    }
    private EffectStyle Style(int unit, int mount) => _effects.Weapons[_units.Units[unit].Weapons[mount].Weapon.Id];
    private void Beam(Vector3 from, Vector3 to, float width, Color color)
    {
        var delta = to - from;
        float length = delta.Length();
        if (length < 0.003f) return;
        var direction = delta / length;
        var up = Math.Abs(direction.Y) < 0.99f ? Vector3.Up : Vector3.Right;
        _beams.Add((from + to) / 2, new(width, width, length), Basis.LookingAt(direction, up), color);
    }
    private static float Lift(int elevation, HoverEndpoints endpoint, HoverEndpoints hover, float flightSurface) =>
        (hover & endpoint) != 0 ? SpriteWorldView.HoverLift(elevation / 1024f, flightSurface) : 0;
    private Vector3 Position(WorldPosition p) => new Vector3(p.X, p.Elevation, p.Y) / 1024f + _overlayOffset;
    private static Color Color(double[] c, float alpha = 1) => new((float)c[0], (float)c[1], (float)c[2], alpha);
}
