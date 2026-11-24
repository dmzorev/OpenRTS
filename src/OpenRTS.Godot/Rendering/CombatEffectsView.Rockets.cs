using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

public sealed partial class CombatEffectsView
{
    private const int MaxSmokePuffs = 4096;
    private const uint ProjectileLightingLayer = 1u << 19;
    private readonly record struct SmokePuff(Vector3 Position, float Size, double Start, int Duration);

    private readonly bool _hasMountedRockets;
    private readonly List<SmokePuff> _puffs = [];
    private readonly ColoredMeshBatch _smoke = SmokeBatch();
    private long _lastSmokeTick = -1;

    private void ApplyMountedRockets(ReadOnlySpan<EntityPose> actors, ReadOnlySpan<WeaponPose> weapons, long tick, float alpha, Vector3 eye)
    {
        foreach (var actor in actors)
        {
            var mounts = _units.Units[actor.UnitType].Weapons;
            foreach (var channel in weapons.Slice(actor.WeaponOffset, actor.WeaponCount))
            {
                if (channel.Reloading) continue;
                var weapon = mounts[channel.Mount].Weapon;
                var style = _effects.Weapons[weapon.Id];
                if (style.MountedRocket is not { } mount) continue;
                float phase = channel.WindupEndTick > 0 && weapon.WindupTicks > 0
                    ? Mathf.SmoothStep(0, 1, 1 - (channel.WindupEndTick - tick - alpha) / weapon.WindupTicks) : 0;
                var view = actor.Presentation;
                var (direction, center) = MountedRocket(actor.UnitType, channel.Mount, mount,
                    Position(actor.Previous).Lerp(Position(actor.Current), alpha),
                    SpriteWorldView.Angle(view.PreviousBodyFacing, view.BodyFacing, alpha),
                    SpriteWorldView.Angle(channel.PreviousFacing, channel.Facing, alpha), phase, (float)style.Size);
                ProjectileVisual(style, center, direction, eye);
            }
        }
    }

    private (Vector3 Direction, Vector3 Center) MountedRocket(int unit, int mountIndex, MountedRocketStyle style,
        Vector3 position, float body, float mount, float phase, float halfLength)
    {
        float yaw = style.FollowsBody ? body : mount;
        // A mounted projectile pivots around its tail until departure.
        var socket = MountVisual(unit, mountIndex, body, yaw, spatialOnly: true);
        if (socket is { } local) yaw = local.Angle;
        var horizontal = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var tailOffset = socket is { } anchor ? anchor.Point :
            new Vector3(MathF.Sin(body), 0, -MathF.Cos(body)) * (float)style.ChassisOffset +
            horizontal * (float)style.TailOffset + Vector3.Up * (float)style.Height;
        float pitch = Mathf.DegToRad((float)style.LaunchPitch * phase);
        var direction = horizontal * MathF.Cos(pitch) + Vector3.Up * MathF.Sin(pitch);
        return (direction, position + tailOffset * ModelScale(unit) + direction * halfLength);
    }

    internal (Vector3 Direction, Vector3 Center)? LaunchVisual(int unit, int mount, ProjectileDeparture launch, EffectStyle style, Vector3 direction)
    {
        float body = SpriteWorldView.Angle(launch.BodyFacing, launch.BodyFacing, 1);
        float weapon = SpriteWorldView.Angle(launch.MountFacing, launch.MountFacing, 1);
        var origin = Position(launch.Origin);
        if (style.MountedRocket is { } rocket)
            return MountedRocket(unit, mount, rocket, origin, body, weapon, 1, (float)style.Size);
        if (LaunchAnimation(unit, mount) is { LaunchLocal: { } local } animation)
        {
            var part = _models[unit]!.Parts.First(p => animation.Parts.Contains(p.Id));
            float angle = (animation.Facing ?? part.Facing) == ModelFacing.Body ? body : weapon;
            if (part.Facings > 1)
            {
                float step = MathF.Tau / part.Facings;
                angle = MathF.Floor(Mathf.PosMod(angle, MathF.Tau) / step + 0.5f) * step;
            }
            var offset = new Vector2((float)local.X, (float)local.Z).Rotated(angle);
            return (direction, origin + new Vector3(offset.X, (float)local.Y, offset.Y) * ModelScale(unit));
        }
        var socket = MountVisual(unit, mount, body, weapon, spatialOnly: false);
        return socket is { } anchor ? (direction, origin + anchor.Point * ModelScale(unit)) : null;
    }

    private float ModelScale(int unit) => _modelCatalog?.ScaleFor(_units.Units[unit]) ?? 1f;

    private ModelAnimation? LaunchAnimation(int unit, int mount) =>
        _models[unit]?.Animations.GetValueOrDefault(_units.Units[unit].Weapons[mount].Animation);

    private (Vector3 Point, float Angle)? MountVisual(int unit, int mount, float body, float weapon, bool spatialOnly)
    {
        if (_models[unit] is not { } model) return null;
        var mountId = _units.Units[unit].Weapons[mount].Id;
        Span<Vector2> pivots = stackalloc Vector2[model.Parts.Count];
        Span<float> angles = stackalloc float[model.Parts.Count];
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var part = model.Parts[i];
            var facing = part.Mount == mountId && !spatialOnly ? LaunchAnimation(unit, mount)?.Facing ?? part.Facing : part.Facing;
            float angle = facing == ModelFacing.Body ? body : weapon;
            if (part.Facings > 1)
            {
                float step = MathF.Tau / part.Facings;
                angle = MathF.Floor(Mathf.PosMod(angle, MathF.Tau) / step + 0.5f) * step;
            }
            angles[i] = angle;
            var offset = new Vector2((float)part.OffsetX, (float)part.OffsetY);
            pivots[i] = part.Parent < 0 ? offset.Rotated(body) : pivots[part.Parent] + offset.Rotated(angles[part.Parent]);
            if (part.Mount != mountId) continue;
            if (part.MuzzleLocal is { } localMuzzle)
            {
                var tip = pivots[i] + new Vector2((float)localMuzzle.X, (float)localMuzzle.Z).Rotated(angle);
                return (new Vector3(tip.X, (float)localMuzzle.Y, tip.Y), angle);
            }
            if (spatialOnly || part.Muzzle is not { } point) continue;
            var local = new Vector2((float)((point.X - part.PivotX) * part.Width), (float)((point.Y - part.PivotY) * part.Height));
            float drawAngle = (part.Facings == 0 ? angle : 0) + Mathf.DegToRad((float)part.SpriteRotation);
            var muzzle = pivots[i] + local.Rotated(drawAngle);
            return (new Vector3(muzzle.X, 0, muzzle.Y), angle);
        }
        return null;
    }

    private void AddProjectileModel(string path)
    {
        var asset = GD.Load<PackedScene>(path) ?? throw new ArgumentException("Missing projectile model: " + path);
        var root = asset.Instantiate<Node3D>();
        try
        {
            var instances = root.FindChildren("*", "MeshInstance3D", recursive: true, owned: false);
            var instance = root as MeshInstance3D ?? (instances.Count == 1 ? (MeshInstance3D)instances[0] : null);
            if (instance?.Mesh is not { } mesh || instances.Count > 1 ||
                !instance.Transform.IsEqualApprox(Transform3D.Identity) || !root.Transform.IsEqualApprox(Transform3D.Identity))
                throw new ArgumentException("Projectile model requires one identity-transformed mesh: " + path);
            var bounds = mesh.GetAabb();
            float halfLength = -bounds.Position.Z;
            if (halfLength <= 0 || MathF.Abs(bounds.End.Z - halfLength) > 0.025f)
                throw new ArgumentException("Projectile model must be centered along its length: " + path);
            var batch = new ColoredMeshBatch(mesh, path.GetFile().GetBaseName(), preserveMaterials: true);
            batch.Node.Layers = ProjectileLightingLayer;
            _projectileModels.Add(path, (batch, halfLength));
            Node.AddChild(batch.Node);
        }
        finally { root.Free(); }
    }

    private void AddProjectileLight(Vector3 rotation, float energy) => Node.AddChild(new DirectionalLight3D
    {
        RotationDegrees = rotation, LightEnergy = energy, LightCullMask = ProjectileLightingLayer
    });

    private void ProjectileVisual(EffectStyle style, Vector3 point, Vector3 direction, Vector3 eye)
    {
        if (style.ProjectileModel is { } model)
        {
            var (mesh, halfLength) = _projectileModels[model];
            mesh.Add(point, Vector3.One * ((float)style.Size / halfLength), PitchBasis(direction, Vector3.Up), Colors.White);
            return;
        }
        var (batch, aspect) = _projectileSprites[style.ProjectileTexture!];
        float length = 2 * (float)style.Size;
        batch.Add(point, new(length * aspect, 1, length), PitchBasis(direction, eye), Colors.White);
    }

    private static Basis PitchBasis(Vector3 flight, Vector3 eye)
    {
        if (flight == Vector3.Zero) flight = Vector3.Forward;
        var right = flight.Cross(Vector3.Up);
        if (MathF.Abs(flight.Y) > 0.001f)
        {
            var screenRight = flight.Cross(eye);
            if (screenRight.LengthSquared() > 0.0001f) right = screenRight;
        }
        right = right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Right;
        return new(right, right.Cross(flight), -flight);
    }

    private static ColoredMeshBatch SmokeBatch()
    {
        var texture = new GradientTexture2D
        {
            Width = 32, Height = 32, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new(0.5f, 0.5f), FillTo = new(1, 0.5f),
            Gradient = new Gradient
            {
                Offsets = [0, 0.45f, 1],
                Colors = [Colors.White, new(1, 1, 1, 0.6f), new(1, 1, 1, 0)]
            }
        };
        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        return new(new QuadMesh { Size = Vector2.One }, "RocketSmoke", material);
    }

    private void EmitSmoke(Vector3 tail, EffectStyle style, double time)
    {
        if (_puffs.Count < MaxSmokePuffs) _puffs.Add(new(tail, (float)style.SmokeSize, time, style.SmokeTicks));
    }

    private void PublishSmoke(double time)
    {
        _smoke.Clear();
        int write = 0;
        for (int read = 0; read < _puffs.Count; read++)
        {
            var puff = _puffs[read];
            float age = (float)((time - puff.Start) / puff.Duration);
            if (age >= 1) continue;
            _puffs[write++] = puff;
            age = Math.Max(0, age);
            float size = puff.Size * (1 + age * 1.9f);
            var color = new Color(0.66f + 0.16f * (1 - age), 0.64f + 0.11f * (1 - age), 0.61f, 0.78f * (1 - age) * (1 - age));
            _smoke.Add(puff.Position + Vector3.Up * (age * puff.Size), Vector3.One * size, 0, color);
        }
        if (write < _puffs.Count) _puffs.RemoveRange(write, _puffs.Count - write);
        _smoke.Publish();
    }
}
