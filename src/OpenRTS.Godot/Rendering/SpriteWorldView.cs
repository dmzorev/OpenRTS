using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

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
    private sealed record Part(ModelPart Definition, Sequence?[] Sequences, ModelAnimation?[] Animations, int Mount, uint Visible, uint Hidden, uint SpinWhen, uint LoopWhen, int LoopAnimation);
    private sealed record Model(UnitDefinition Unit, Part[] Parts, ModelState[] States, uint Damaged, uint Destroyed, uint NearlyDestroyed);
    private readonly Model[] _models;
    private readonly SortedDictionary<EntityId, EntityPose> _wrecks = new(Comparer<EntityId>.Create((a, b) => a.Value.CompareTo(b.Value)));
    private EntityPose[] _ordered = [];
    private readonly IComparer<EntityPose> _poseOrder = Comparer<EntityPose>.Create((a, b) => a.Id.Value.CompareTo(b.Id.Value));
    private readonly Dictionary<EntityId, float> _depthRanks = new();
    private readonly Dictionary<long, List<(WorldBox Box, float Depth)>> _depthRegions = new();
    private ulong _depthSignature;
    private readonly HashSet<EntityId> _liveIds = [];
    public int WreckCount => _wrecks.Count;
    public void ClearWrecks() { _wrecks.Clear(); _depthSignature = 0; }
    public void Accept(WorldEvent value)
    {
        if (value.Kind == WorldEventKind.Removed) { _wrecks.Remove(value.Entity); return; }
        if (value.Kind != WorldEventKind.Destroyed || value.UnitType < 0 || _models[value.UnitType].Destroyed == 0) return;
        var model = _models[value.UnitType]; var north = FacingDirection.North;
        _wrecks[value.Entity] = new(value.Entity, value.UnitType, value.Position, value.Position,
            new(value.Owner, north, north, north, north, new(model.Unit.IdleAnimation, value.Tick, 0), AnimationPlayback.None, model.Destroyed));
    }
    private readonly List<SpriteBatch> _batches = [];
    private readonly Dictionary<(ulong, Rect2), SpriteBatch> _textures = new();
    public Node3D Node { get; } = new() { Name = "SpriteUnits" };
    public int BatchCount => _batches.Count;
    public int SpriteCount { get; private set; }
    public int Capacity => _batches.Sum(batch => batch.Capacity);
    public int MaximumParts { get; }

    private readonly Func<uint, Color> _ownerColor;

    public SpriteWorldView(UnitCatalog units, ModelCatalog models, Func<uint, Color>? ownerColor = null)
    {
        _ownerColor = ownerColor ?? (_ => Colors.White);
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
                uint Mask(IEnumerable<string>? names) { uint mask = 0; if (names is not null) foreach (string name in names) mask |= type.StateMask(name); return mask; }
                uint State(string? name) => name is null ? 0 : type.StateMask(name);
                parts[p] = new(part, sequences, animations, part.Mount is null ? -1 : type.Weapons.ToList().FindIndex(m => m.Id == part.Mount),
                    Mask(part.VisibleWhen), Mask(part.HiddenWhen), State(part.SpinWhen), State(part.LoopWhen), part.LoopAnimation is null ? -1 : type.AnimationIndex(part.LoopAnimation));
            }
            _models[i] = new(type, parts, type.States.Select(name => definition.States[name]).ToArray(), definition.DamagedState is null ? 0 : type.StateMask(definition.DamagedState), definition.DestroyedState is null ? 0 : type.StateMask(definition.DestroyedState), type.States.Contains("nearly-destroyed") ? type.StateMask("nearly-destroyed") : 0);
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

    private readonly HashSet<EntityId> _ghostIds = new();

    public void Apply(ReadOnlySpan<EntityPose> poses, float alpha, long tick, int tickRate, ReadOnlySpan<WeaponPose> weapons = default, ReadOnlySpan<ConstructionProjectPose> projects = default)
    {
        alpha = Math.Clamp(alpha, 0, 1); double time = tick == 0 ? 0 : tick + alpha; SpriteCount = 0;
        foreach (var batch in _batches) batch.Clear();
        Span<Vector2> pivots = stackalloc Vector2[8];
        Span<float> angles = stackalloc float[8];
        int count = poses.Length + _wrecks.Count + projects.Length;
        if (_ordered.Length < count) Array.Resize(ref _ordered, Math.Max(count, _ordered.Length * 2));
        int at = 0;
        foreach (var pose in poses) if (pose.Carrier.Value == 0) _ordered[at++] = pose;
        if (_wrecks.Count > 0)
        {
            _liveIds.Clear(); foreach (var pose in poses) _liveIds.Add(pose.Id);
            foreach (var wreck in _wrecks.Values) if (wreck.Presentation.Body.StartTick <= tick && !_liveIds.Contains(wreck.Id)) _ordered[at++] = wreck;
        }
        _ghostIds.Clear();
        foreach (var project in projects)
        {
            _ghostIds.Add(project.Id);
            var facing = FacingDirection.North;
            var presentation = new EntityPresentation(project.Owner, facing, facing, facing, facing,
                new(_models[project.UnitType].Unit.IdleAnimation, tick, 0), AnimationPlayback.None, 0);
            _ordered[at++] = new(project.Id, project.UnitType, project.Position, project.Position, presentation);
        }
        count = at;
        bool sorted = true; for (int i = 1; i < count; i++) if (_ordered[i - 1].Id.Value > _ordered[i].Id.Value) { sorted = false; break; }
        if (!sorted) Array.Sort(_ordered, 0, count, _poseOrder);
        PrepareDepth(_ordered.AsSpan(0, count));
        foreach (var pose in _ordered.AsSpan(0, count))
        {
            var model = _models[pose.UnitType]; var view = pose.Presentation;
            if (model.Damaged != 0 && pose.Health > 0 && (long)pose.Health * 2 <= model.Unit.MaxHealth) view = view with { States = view.States | model.Damaged };
            if (model.NearlyDestroyed != 0 && pose.Health > 0 && (long)pose.Health * 4 <= model.Unit.MaxHealth) view = view with { States = view.States | model.NearlyDestroyed };
            float depth = model.Unit.Footprint is not null ? _depthRanks[pose.Id] : DepthAt(pose.Current) + (_depthRanks.Count > 0 ? 0.0001f : 0);
            float layerStep = _depthRanks.Count > 0 ? 0.00005f : 0.015f;
            Vector2 origin = new(Lerp(pose.Previous.X, pose.Current.X, alpha), Lerp(pose.Previous.Y, pose.Current.Y, alpha));
            float elevation = Lerp(pose.Previous.Elevation, pose.Current.Elevation, alpha);
            float body = Angle(view.PreviousBodyFacing, view.BodyFacing, alpha), weapon = Angle(view.PreviousWeaponFacing, view.WeaponFacing, alpha);
            bool ghost = _ghostIds.Contains(pose.Id);
            Color tint = ghost ? new(0.7f, 0.9f, 1, 0.42f) : Colors.White;
            float scale = 1, stateFlash = 0;
            for (int state = 0; state < model.States.Length; state++) if ((view.States & (1u << state)) != 0)
            {
                var style = model.States[state]; stateFlash = Math.Max(stateFlash, (float)style.Pulse * (0.5f + 0.5f * MathF.Sin((float)time * MathF.Tau * 2 / tickRate))); tint *= new Color((float)style.Red, (float)style.Green, (float)style.Blue, (float)style.Opacity); scale *= (float)style.Scale;
            }
            for (int p = 0; p < model.Parts.Length; p++)
            {
                var runtime = model.Parts[p]; var part = runtime.Definition;
                if ((view.States & runtime.Visible) != runtime.Visible || (view.States & runtime.Hidden) != 0) { pivots[p] = origin; angles[p] = body; continue; }
                float angle = part.Facing == ModelFacing.Body ? body : weapon;
                if (!ghost && part.Facing == ModelFacing.Weapon && runtime.Mount >= 0 && pose.WeaponOffset + runtime.Mount < weapons.Length)
                {
                    var aim = weapons[pose.WeaponOffset + runtime.Mount];
                    angle = Angle(aim.PreviousFacing, aim.Facing, alpha);
                }
                if (part.Spin != 0 && (runtime.SpinWhen == 0 || (view.States & runtime.SpinWhen) != 0)) angle += (float)(time / tickRate * part.Spin * Math.PI / 180);
                Vector2 mount = part.Parent < 0 ? origin : pivots[part.Parent];
                mount += Rotate(new((float)part.OffsetX * scale, (float)part.OffsetY * scale), part.Parent < 0 ? body : angles[part.Parent]);
                angles[p] = angle;
                var playback = view.Body;
                int animation = playback.Animation;
                if (animation < 0 || animation >= runtime.Sequences.Length || runtime.Sequences[animation] is null)
                { animation = model.Unit.IdleAnimation; playback = new(animation, 0, 0); }
                if (runtime.LoopAnimation >= 0 && (view.States & runtime.LoopWhen) != 0 && runtime.Sequences[runtime.LoopAnimation] is not null && (animation == model.Unit.IdleAnimation || animation == model.Unit.MoveAnimation))
                { animation = runtime.LoopAnimation; playback = new(animation, 0, 0); }
                if (view.Weapon.Animation >= 0 && view.Weapon.Animation < runtime.Sequences.Length &&
                    view.Weapon.IsActive(tick) && runtime.Sequences[view.Weapon.Animation] is not null)
                { playback = view.Weapon; animation = playback.Animation; }
                float extraFlash = 0, extraRecoil = 0;
                // shortcut: a shared part has one base clip; use rig mixing for layered skeletal animation.
                if (pose.WeaponCount > 0 && pose.WeaponOffset + pose.WeaponCount <= weapons.Length)
                    foreach (var channel in weapons.Slice(pose.WeaponOffset, pose.WeaponCount))
                    {
                        var clip = channel.Animation;
                        if (!clip.IsActive(tick) || clip.Animation < 0 || clip.Animation >= runtime.Sequences.Length || runtime.Sequences[clip.Animation] is null) continue;
                        float phase = (float)Math.Clamp((time - clip.StartTick) / clip.DurationTicks, 0, 1);
                        float kick = MathF.Sin(MathF.PI * Math.Clamp(phase * 3, 0, 1));
                        var style = runtime.Animations[clip.Animation]!;
                        extraFlash = Math.Max(extraFlash, kick * (float)style.Flash); extraRecoil += kick * (float)style.Recoil;
                        playback = clip; animation = clip.Animation;
                    }
                var effect = runtime.Animations[animation]!;
                float progress = playback.DurationTicks > 0 ? (float)Math.Clamp((time - playback.StartTick) / playback.DurationTicks, 0, 1) : 0;
                float pulse = playback.DurationTicks > 0 ? MathF.Sin(MathF.PI * Math.Clamp(progress * 3, 0, 1)) : 0;
                float bob = (float)effect.Bob * MathF.Sin((float)(time - playback.StartTick) * MathF.Tau * 3 / tickRate);
                mount += Rotate(new(bob + (float)effect.SlideX * MathF.Sin((float)(time - playback.StartTick) / tickRate * MathF.Tau),
                    Math.Max((float)effect.Recoil * pulse, extraRecoil) + (float)effect.SlideY * MathF.Sin((float)(time - playback.StartTick) / tickRate * MathF.Tau)), angle);
                pivots[p] = mount;
                float buildScale = playback.DurationTicks > 0 ? Mathf.Lerp((float)effect.ScaleFrom, 1, progress) : 1;
                Vector2 size = new((float)part.Width * scale * buildScale, (float)part.Height * scale * buildScale);
                var partTint = tint; if (playback.DurationTicks > 0) partTint.A *= Mathf.Lerp((float)effect.OpacityFrom, 1, progress);
                Vector2 center = mount + Rotate(new((0.5f - (float)part.PivotX) * size.X, (0.5f - (float)part.PivotY) * size.Y), angle);
                var team = _ownerColor(view.Owner);
                team.A = Math.Max(stateFlash, Math.Max(pulse * (float)effect.Flash, extraFlash));
                runtime.Sequences[animation]!.At(playback, time, tickRate).Add(new(center.X, elevation + 0.035f + depth + part.Layer * layerStep, center.Y), angle, size, partTint, team);
                SpriteCount++;
            }
        }
        foreach (var batch in _batches) batch.Publish();
    }

    private static long DepthKey(int x, int y) => ((long)x << 32) | (uint)y;
    private void PrepareDepth(ReadOnlySpan<EntityPose> poses)
    {
        ulong signature = 14695981039346656037;
        foreach (var pose in poses) if (_models[pose.UnitType].Unit.Footprint is not null)
        { signature = (signature ^ pose.Id.Value) * 1099511628211; signature = (signature ^ (uint)pose.Current.X) * 1099511628211; signature = (signature ^ (uint)pose.Current.Y) * 1099511628211; }
        if (signature == _depthSignature) return; _depthSignature = signature; _depthRanks.Clear(); _depthRegions.Clear();
        int rank = 0;
        foreach (var pose in poses)
        {
            var footprint = _models[pose.UnitType].Unit.Footprint; if (footprint is null) continue;
            float depth = ++rank * 0.001f; _depthRanks.Add(pose.Id, depth);
            foreach (var cell in footprint.Cells)
            {
                var box = footprint.Bounds(cell, pose.Current);
                for (int y = box.MinY / 1024; y <= (box.MaxY - 1) / 1024; y++) for (int x = box.MinX / 1024; x <= (box.MaxX - 1) / 1024; x++)
                { long key = DepthKey(x, y); if (!_depthRegions.TryGetValue(key, out var list)) _depthRegions.Add(key, list = []); list.Add((box, depth)); }
            }
        }
    }
    private float DepthAt(WorldPosition point)
    {
        if (!_depthRegions.TryGetValue(DepthKey(point.X / 1024, point.Y / 1024), out var list)) return 0;
        for (int i = list.Count - 1; i >= 0; i--)
        { var (b, depth) = list[i]; if (point.X >= b.MinX && point.X < b.MaxX && point.Y >= b.MinY && point.Y < b.MaxY) return depth; }
        return 0;
    }

    public static float Angle(FacingDirection previous, FacingDirection current, float alpha)
    {
        float from = MathF.Atan2(previous.X, -previous.Y), to = MathF.Atan2(current.X, -current.Y);
        return from + Mathf.Wrap(to - from, -MathF.PI, MathF.PI) * alpha;
    }
    private static Vector2 Rotate(Vector2 point, float angle) => new(point.X * MathF.Cos(angle) - point.Y * MathF.Sin(angle), point.X * MathF.Sin(angle) + point.Y * MathF.Cos(angle));
    private static float Lerp(int previous, int current, float alpha) => (previous + ((long)current - previous) * alpha) / 1024;
}
