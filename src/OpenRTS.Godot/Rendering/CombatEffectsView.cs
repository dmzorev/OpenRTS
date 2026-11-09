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
    { if (value.Kind is WorldEventKind.WeaponFired or WorldEventKind.ProjectileImpact) _events.Add(value); }
    public void Clear() => _events.Clear();
    public void Apply(ReadOnlySpan<ProjectilePose> shots, float alpha, long tick)
    {
        _rounds.Clear(); _beams.Clear(); _impacts.Clear(); ProjectileCount = shots.Length;
        double time = tick + Math.Clamp(alpha, 0, 1);
        foreach (var shot in shots)
        {
            var style = Style(shot.UnitType, shot.Mount); var current = Position(shot.Current); var previous = Position(shot.Previous);
            var point = previous.Lerp(current, alpha); float size = (float)style.Size;
            _rounds.Add(point, new(size, size, size * 1.7f), MathF.Atan2(shot.Facing.X, shot.Facing.Y), Color(style.Color));
            Beam(previous, point, (float)style.BeamWidth, Color(style.Color, 0.5f));
        }
        int write = 0;
        for (int read = 0; read < _events.Count; read++)
        {
            var value = _events[read]; var style = Style(value.UnitType, value.Mount);
            double age = time - value.Tick; if (age >= style.DurationTicks) continue;
            _events[write++] = value; if (age < 0) continue;
            float phase = (float)(age / style.DurationTicks); var weapon = _units.Units[value.UnitType].Weapons[value.Mount].Weapon;
            if (value.Kind == WorldEventKind.WeaponFired)
            {
                if (weapon.Projectile == ProjectileKind.Hitscan) Beam(Position(value.SourcePosition), Position(value.Position), (float)style.BeamWidth, Color(style.Color, 1 - phase));
                _impacts.Add(Position(value.SourcePosition), new(0.25f * (1 - phase), 0.05f, 0.25f * (1 - phase)), 0, Color(style.Color, 1 - phase));
            }
            else
            {
                float size = (float)style.ImpactSize * (0.3f + phase);
                _impacts.Add(Position(value.Position), new(size * 2, 0.08f, size * 2), 0, Color(style.ImpactColor, 1 - phase));
            }
        }
        if (write < _events.Count) _events.RemoveRange(write, _events.Count - write);
        _rounds.Publish(); _beams.Publish(); _impacts.Publish();
    }
    private EffectStyle Style(int unit, int mount) => _effects.Weapons[_units.Units[unit].Weapons[mount].Weapon.Id];
    private void Beam(Vector3 from, Vector3 to, float width, Color color)
    {
        var delta = to - from; if (delta.LengthSquared() < 0.00001f) return;
        _beams.Add((from + to) / 2, new(width, width, delta.Length()), MathF.Atan2(delta.X, delta.Z), color);
    }
    private static Vector3 Position(WorldPosition p) => new(p.X / 1024f, p.Elevation / 1024f + SpriteWorldView.OverlayHeight, p.Y / 1024f);
    private static Color Color(double[] c, float alpha = 1) => new((float)c[0], (float)c[1], (float)c[2], alpha);
}
