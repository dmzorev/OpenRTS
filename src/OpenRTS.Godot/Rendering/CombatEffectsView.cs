using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

/// <summary>Projectile snapshots and independent event effects, batched without per-shot nodes.</summary>
public sealed class CombatEffectsView
{
    private readonly UnitCatalog _units;
    private readonly EffectCatalog _effects;
    private readonly List<WorldEvent> _events = [];
    private readonly ColoredMeshBatch _rounds = new(new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 8, Rings = 4 }, "Projectiles");
    private readonly ColoredMeshBatch _beams = new(new BoxMesh { Size = Vector3.One }, "Tracers");
    private readonly ColoredMeshBatch _impacts = new(new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 12, Rings = 4 }, "Impacts");
    public Node3D Node { get; } = new() { Name = "CombatEffects" };
    public int ProjectileCount { get; private set; }
    public int ActiveEffects => _events.Count;
    public CombatEffectsView(UnitCatalog units, EffectCatalog effects)
    {
        _units = units; _effects = effects;
        foreach (var type in units.Units) foreach (var mount in type.Weapons)
            if (!effects.Weapons.ContainsKey(mount.Weapon.Id)) throw new ArgumentException("Missing effect: " + mount.Weapon.Id);
        Node.AddChild(_rounds.Node); Node.AddChild(_beams.Node); Node.AddChild(_impacts.Node);
    }
    public void Accept(WorldEvent value)
    { if (value.UnitType >= 0 && value.Kind is (WorldEventKind.WeaponFired or WorldEventKind.ProjectileImpact)) _events.Add(value); }
    public void Clear() => _events.Clear();
    public void Apply(ReadOnlySpan<ProjectilePose> shots, float alpha, long tick, float flightSurface = 0, FrameExchange.FrameLease? observation = null)
    {
        _rounds.Clear(); _beams.Clear(); _impacts.Clear(); ProjectileCount = shots.Length;
        double time = tick + Math.Clamp(alpha, 0, 1);
        foreach (var shot in shots)
        {
            var style = Style(shot.UnitType, shot.Mount);
            var current = Position(shot.Current);
            var previous = Position(shot.Previous);
            float heading = MathF.Atan2(shot.Facing.X, shot.Facing.Y);
            var projection = shot.Projection;
            if (projection.Hover != HoverEndpoints.None)
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
            var point = previous.Lerp(current, alpha); float size = (float)style.Size;
            _rounds.Add(point, new(size, size, size * 1.7f), heading, Color(style.Color));
            Beam(previous, point, (float)style.BeamWidth, Color(style.Color, 0.5f));
        }
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
                var source = Position(value.SourcePosition);
                source.Z -= Lift(value.SourcePosition.Elevation, HoverEndpoints.Source, value.Hover, flightSurface);
                if (weapon.Projectile == ProjectileKind.Hitscan)
                {
                    var target = Position(value.Position);
                    target.Z -= Lift(value.Position.Elevation, HoverEndpoints.Target, value.Hover, flightSurface);
                    Beam(source, target, (float)style.BeamWidth, Color(style.Color, 1 - phase));
                }
                _impacts.Add(source, new(0.25f * (1 - phase), 0.05f, 0.25f * (1 - phase)), 0, Color(style.Color, 1 - phase));
            }
            else
            {
                float size = (float)style.ImpactSize * (0.3f + phase);
                var target = Position(value.Position);
                target.Z -= Lift(value.Position.Elevation, HoverEndpoints.Target, value.Hover, flightSurface);
                _impacts.Add(target, new(size * 2, 0.08f, size * 2), 0, Color(style.ImpactColor, 1 - phase));
            }
        }
        if (write < _events.Count) _events.RemoveRange(write, _events.Count - write);
        _rounds.Publish(); _beams.Publish(); _impacts.Publish();
    }
    private EffectStyle Style(int unit, int mount) => _effects.Weapons[_units.Units[unit].Weapons[mount].Weapon.Id];
    private void Beam(Vector3 from, Vector3 to, float width, Color color)
    {
        var delta = to - from;
        float length = new Vector2(delta.X, delta.Z).Length();
        if (length < 0.003f) return;
        _beams.Add((from + to) / 2, new(width, width, length), MathF.Atan2(delta.X, delta.Z), color);
    }
    private static float Lift(int elevation, HoverEndpoints endpoint, HoverEndpoints hover, float flightSurface) =>
        (hover & endpoint) != 0 ? SpriteWorldView.HoverLift(elevation / 1024f, flightSurface) : 0;
    private static Vector3 Position(WorldPosition p) => new(p.X / 1024f, p.Elevation / 1024f + SpriteWorldView.OverlayHeight, p.Y / 1024f);
    private static Color Color(double[] c, float alpha = 1) => new((float)c[0], (float)c[1], (float)c[2], alpha);
}
