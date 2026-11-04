using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot;

/// <summary>Semantic animation snapshots are mapped to SpriteFrames and batched layered quads.</summary>
public sealed class SpriteWorldView
{
    private sealed record Sequence(SpriteBatch[] Frames, double[] Ends, double Speed, bool Loop)
    {
        internal SpriteBatch At(AnimationPlayback playback, double time, int tickRate)
        {
            double elapsed = Math.Max(0, time - playback.StartTick), total = Ends[^1];
            double value = playback.DurationTicks > 0 ? Math.Min(0.999999, elapsed / playback.DurationTicks) * total
                : Loop ? elapsed * Speed / tickRate % total : Math.Min(total - 0.000001, elapsed * Speed / tickRate);
            int i = 0; while (i < Ends.Length - 1 && value >= Ends[i]) i++;
            return Frames[i];
        }
    }
    private sealed record Part(ModelPart Definition, Sequence?[] Sequences, ModelAnimation?[] Animations);
    private sealed record Model(UnitDefinition Unit, Part[] Parts, ModelState[] States);
    private readonly Model[] _models;
    private readonly List<SpriteBatch> _batches = [];
    private readonly Dictionary<(ulong, Rect2), SpriteBatch> _textures = new();
    public Node3D Node { get; } = new() { Name = "SpriteUnits" };
    public int BatchCount => _batches.Count;
    public int SpriteCount { get; private set; }
    public int Capacity => _batches.Sum(batch => batch.Capacity);
    public int MaximumParts { get; }

    public SpriteWorldView(UnitCatalog units, ModelCatalog models)
    {
        models.ValidateUnits(units);
        _models = new Model[units.Units.Count];
        for (int i = 0; i < _models.Length; i++)
        {
            var type = units.Units[i]; var binding = units.Presentations[i];
            if (binding.Model is null) throw new ArgumentException("Sprite rendering requires a Model binding for " + type.Id);
            var definition = models.Models[binding.Model]; var parts = new Part[definition.Parts.Count];
            for (int p = 0; p < parts.Length; p++)
            {
                var part = definition.Parts[p];
                var frames = GD.Load<SpriteFrames>(part.Resource) ?? throw new ArgumentException("Missing SpriteFrames: " + part.Resource);
                var sequences = new Sequence?[type.Animations.Count]; var animations = new ModelAnimation?[sequences.Length];
                for (int a = 0; a < sequences.Length; a++)
                {
                    var animation = definition.Animations[type.Animations[a]];
                    if (!animation.Parts.Contains(part.Id)) continue;
                    var name = new StringName(animation.Sequence);
                    if (!frames.HasAnimation(name) || frames.GetFrameCount(name) == 0) throw new ArgumentException($"{part.Resource}: missing sequence {name}.");
                    int sourceCount = frames.GetFrameCount(name); var loop = frames.GetAnimationLoopMode(name);
                    int count = loop == SpriteFrames.LoopMode.Pingpong && sourceCount > 1 ? sourceCount * 2 - 2 : sourceCount;
                    var textures = new SpriteBatch[count]; var ends = new double[count];
                    double total = 0;
                    for (int frame = 0; frame < count; frame++)
                    {
                        int sourceFrame = frame < sourceCount ? frame : sourceCount * 2 - 2 - frame;
                        var texture = frames.GetFrameTexture(name, sourceFrame) ?? throw new ArgumentException("Animation frame has no texture.");
                        textures[frame] = Batch(texture);
                        double duration = frames.GetFrameDuration(name, sourceFrame);
                        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("Invalid frame duration: " + part.Resource);
                        total += duration; ends[frame] = total;
                    }
                    double speed = frames.GetAnimationSpeed(name);
                    if (total <= 0 || speed <= 0) throw new ArgumentException("Invalid sequence timing: " + part.Resource);
                    sequences[a] = new(textures, ends, speed, loop != SpriteFrames.LoopMode.None); animations[a] = animation;
                }
                if (sequences[type.IdleAnimation] is null) throw new ArgumentException($"{definition.Id}.{part.Id}: idle must bind every part.");
                parts[p] = new(part, sequences, animations);
            }
            _models[i] = new(type, parts, type.States.Select(name => definition.States[name]).ToArray());
            MaximumParts = Math.Max(MaximumParts, parts.Length);
        }
    }

    private SpriteBatch Batch(Texture2D texture)
    {
        var resolved = SpriteBatch.Resolve(texture); var key = (resolved.Texture.GetInstanceId(), resolved.Region);
        if (_textures.TryGetValue(key, out var batch)) return batch;
        batch = new(resolved.Texture, resolved.Region, true); batch.Node.Name = "Sprite_" + resolved.Texture.ResourcePath.GetFile().GetBaseName();
        _textures.Add(key, batch); _batches.Add(batch); Node.AddChild(batch.Node); return batch;
    }

    public void Apply(ReadOnlySpan<EntityPose> poses, float alpha, long tick, int tickRate)
    {
        alpha = Math.Clamp(alpha, 0, 1); double time = tick == 0 ? 0 : tick + alpha; SpriteCount = 0;
        foreach (var batch in _batches) batch.Clear();
        Span<Vector2> pivots = stackalloc Vector2[8];
        Span<float> angles = stackalloc float[8];
        foreach (var pose in poses)
        {
            var model = _models[pose.UnitType]; var view = pose.Presentation;
            Vector2 origin = new(Lerp(pose.Previous.X, pose.Current.X, alpha), Lerp(pose.Previous.Y, pose.Current.Y, alpha));
            float elevation = Lerp(pose.Previous.Elevation, pose.Current.Elevation, alpha);
            float body = Angle(view.PreviousBodyFacing, view.BodyFacing, alpha), weapon = Angle(view.PreviousWeaponFacing, view.WeaponFacing, alpha);
            Color tint = Colors.White; float scale = 1;
            for (int state = 0; state < model.States.Length; state++) if ((view.States & (1u << state)) != 0)
            {
                var style = model.States[state]; tint *= new Color((float)style.Red, (float)style.Green, (float)style.Blue, (float)style.Opacity); scale *= (float)style.Scale;
            }
            for (int p = 0; p < model.Parts.Length; p++)
            {
                var runtime = model.Parts[p]; var part = runtime.Definition;
                float angle = part.Facing == ModelFacing.Body ? body : weapon;
                Vector2 mount = part.Parent < 0 ? origin : pivots[part.Parent];
                mount += Rotate(new((float)part.OffsetX * scale, (float)part.OffsetY * scale), part.Parent < 0 ? body : angles[part.Parent]);
                angles[p] = angle;
                var playback = view.Body;
                int animation = playback.Animation;
                if (animation < 0 || animation >= runtime.Sequences.Length || runtime.Sequences[animation] is null)
                { animation = model.Unit.IdleAnimation; playback = new(animation, 0, 0); }
                if (view.Weapon.Animation >= 0 && view.Weapon.Animation < runtime.Sequences.Length &&
                    view.Weapon.IsActive(tick) && runtime.Sequences[view.Weapon.Animation] is not null)
                { playback = view.Weapon; animation = playback.Animation; }
                var effect = runtime.Animations[animation]!;
                float progress = playback.DurationTicks > 0 ? (float)Math.Clamp((time - playback.StartTick) / playback.DurationTicks, 0, 1) : 0;
                float pulse = playback.DurationTicks > 0 ? MathF.Sin(MathF.PI * Math.Clamp(progress * 3, 0, 1)) : 0;
                float bob = (float)effect.Bob * MathF.Sin((float)(time - playback.StartTick) * MathF.Tau * 3 / tickRate);
                mount += Rotate(new(bob, (float)effect.Recoil * pulse), angle);
                pivots[p] = mount;
                Vector2 size = new((float)part.Width * scale, (float)part.Height * scale);
                Vector2 center = mount + Rotate(new((0.5f - (float)part.PivotX) * size.X, (0.5f - (float)part.PivotY) * size.Y), angle);
                // shortcut: two demo owner colors; load faction palettes when a game defines them.
                var team = view.Owner switch { 1 => new Color(0.14f, 0.52f, 1), 2 => new Color(1, 0.20f, 0.16f), _ => new Color(0.72f, 0.78f, 0.80f) };
                team.A = pulse * (float)effect.Flash;
                runtime.Sequences[animation]!.At(playback, time, tickRate).Add(new(center.X, elevation + 0.035f + part.Layer * 0.015f, center.Y), angle, size, tint, team);
                SpriteCount++;
            }
        }
        foreach (var batch in _batches) batch.Publish();
    }

    public static float Angle(FacingDirection previous, FacingDirection current, float alpha)
    {
        float from = MathF.Atan2(previous.X, -previous.Y), to = MathF.Atan2(current.X, -current.Y);
        return from + Mathf.Wrap(to - from, -MathF.PI, MathF.PI) * alpha;
    }
    private static Vector2 Rotate(Vector2 point, float angle) => new(point.X * MathF.Cos(angle) - point.Y * MathF.Sin(angle), point.X * MathF.Sin(angle) + point.Y * MathF.Cos(angle));
    private static float Lerp(int previous, int current, float alpha) => (previous + ((long)current - previous) * alpha) / 1024;
}
