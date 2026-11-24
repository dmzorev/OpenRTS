using Godot;
using OpenRTS.Content;
using OpenRTS.Core.Orders;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;

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
    private sealed record Part(ModelPart Definition, Sequence[]?[] Sequences, Sequence[]?[]? Windups, ModelAnimation?[] Animations, int Mount, uint Visible, uint Hidden, uint SpinWhen, uint LoopWhen, int LoopAnimation, int WorkAnimation);
    private sealed record Model(UnitDefinition Unit, Part[] Parts, ModelState[] States, uint Damaged, uint Destroyed, uint NearlyDestroyed,
        ModelDrawLayer DrawLayer, int DrawLayers, double SortOffset, int PartLayerCount, float Scale);
    private readonly record struct DrawItem(int Index, int Layer, double Depth, uint Id) : IComparable<DrawItem>
    {
        public int CompareTo(DrawItem other)
        {
            int layer = Layer.CompareTo(other.Layer);
            if (layer != 0) return layer;
            int depth = Depth.CompareTo(other.Depth);
            return depth != 0 ? depth : Id.CompareTo(other.Id);
        }
    }
    private const int ModelLayers = 3, PreviewLayer = 3, AirLayer = 4, LayerCount = AirLayer + 1;
    private const float LayerStep = 0.4f, LayerSpan = 0.32f, ShadowHeight = 0.38f;
    private const float HoverLiftScale = 0.45f;
    private const float SpriteBaseHeight = 0.04f;
    public const float GroundMarkerHeight = SpriteBaseHeight / 2;
    public const float OverlayHeight = 2f;
    private readonly Model[] _models;
    private readonly WorkFlightVisual _workFlights;
    private double _workTime;
    private readonly SortedDictionary<EntityId, EntityPose> _wrecks = new(Comparer<EntityId>.Create((a, b) => a.Value.CompareTo(b.Value)));
    private EntityPose[] _ordered = [];
    private readonly IComparer<EntityPose> _poseOrder = Comparer<EntityPose>.Create((a, b) => a.Id.Value.CompareTo(b.Id.Value));
    private DrawItem[] _drawOrder = [];
    private (float Height, float Stride)[] _drawDepths = [];
    private readonly HashSet<EntityId> _liveIds = [];
    public int WreckCount => _wrecks.Count;
    public void ClearWrecks() => _wrecks.Clear();
    public void Accept(WorldEvent value)
    {
        if (value.Kind == WorldEventKind.Removed) { _wrecks.Remove(value.Entity); return; }
        if (value.Kind != WorldEventKind.Destroyed || value.UnitType < 0 || _models[value.UnitType].Destroyed == 0) return;
        var model = _models[value.UnitType]; var facing = FacingDirection.FromQuarterTurns(value.QuarterTurns);
        _wrecks[value.Entity] = new(value.Entity, value.UnitType, value.Position, value.Position,
            new(value.Owner, facing, facing, facing, facing, new(model.Unit.IdleAnimation, value.Tick, 0), AnimationPlayback.None, model.Destroyed))
        { QuarterTurns = value.QuarterTurns };
    }
    private readonly List<SpriteBatch> _batches = [];
    private readonly Dictionary<SpriteBatch, SpriteBatch> _translucentBatches = [];
    private readonly Dictionary<(ulong, Rect2), SpriteBatch> _textures = new();
    public Node3D Node { get; } = new() { Name = "SpriteUnits" };
    public int BatchCount => _batches.Count;
    public int SpriteCount { get; private set; }
    public int Capacity => _batches.Sum(batch => batch.Capacity);
    public int MaximumParts { get; }

    private readonly Func<uint, Color> _ownerColor;
    private Vector3 _depthAxis = Vector3.Up;
    private Vector2 _sortAxis = Vector2.Down;
    private float _flightSurface;
    private NavigationGrid? _reliefSurface;
    internal float FlightSurface => _flightSurface;

    internal void AlignDepthWithCamera(Camera3D camera)
    {
        var back = camera.Basis.Z;
        // Moving along an orthographic view ray preserves the projected anchor.
        _depthAxis = back / back.Y;
        _sortAxis = new Vector2(back.X, back.Z).Normalized();
    }

    public void SetFlightSurface(NavigationGrid? grid, bool reliefProjection = false)
    {
        _flightSurface = grid?.HasSurface == true ? grid.SurfaceAt(0).Elevation / 1024f : 0;
        _reliefSurface = reliefProjection ? grid : null;
    }

    /// <summary>Interpolated sprite anchor, including the screen lift of a hovering aircraft.</summary>
    public Vector3 DisplayPosition(in EntityPose pose, float alpha = 1)
    {
        var point = Interpolate(pose, alpha);
        point.Z -= HoverLift(_models[pose.UnitType].Unit, point.Y);
        return point;
    }

    /// <summary>Screen-space selection extent of the root sprite, independent of the physical body and camera zoom.</summary>
    public float PickRadius(in EntityPose pose, Camera3D camera, float alpha = 1)
    {
        var position = DisplayPosition(pose, alpha);
        var center = camera.UnprojectPosition(position);
        float angle = _workFlights.Heading(pose, _workTime, Angle(pose.Presentation.PreviousBodyFacing, pose.Presentation.BodyFacing, alpha));
        float squared = 24 * 24;
        var model = _models[pose.UnitType];
        foreach (var part in model.Parts)
        {
            var root = part.Definition;
            if (root.Parent >= 0) continue;
            for (int y = 0; y <= 1; y++) for (int x = 0; x <= 1; x++)
            {
                var corner = Rotate(new((float)((x - root.PivotX) * root.Width), (float)((y - root.PivotY) * root.Height)),
                    angle + Mathf.DegToRad((float)root.SpriteRotation)) + Rotate(new((float)root.OffsetX, (float)root.OffsetY), angle);
                squared = Math.Max(squared, center.DistanceSquaredTo(camera.UnprojectPosition(position + new Vector3(corner.X, 0, corner.Y) * model.Scale)));
            }
        }
        return MathF.Sqrt(squared) + 4;
    }

    public SpriteWorldView(UnitCatalog units, ModelCatalog models, Func<uint, Color>? ownerColor = null, Func<string, SpriteFrames>? framesFor = null)
    {
        try
        {
            _ownerColor = ownerColor ?? (_ => Colors.White);
            framesFor ??= GD.Load<SpriteFrames>;
            _workFlights = new(units.Units.Select(p => p.Flight?.WorkRoaming ?? false).ToArray());
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
                    var frames = framesFor(part.Resource) ?? throw new ArgumentException("Missing SpriteFrames: " + part.Resource);
                    var sequences = new Sequence[]?[type.Animations.Count]; var animations = new ModelAnimation?[sequences.Length];
                    Sequence[]?[]? windups = null;
                    for (int a = 0; a < sequences.Length; a++)
                    {
                        var animation = definition.Animations[type.Animations[a]];
                        if (!animation.Parts.Contains(part.Id)) continue;
                        sequences[a] = LoadDirections(frames, animation.Sequence, part);
                        if (animation.WindupSequence is { } windup)
                        {
                            windups ??= new Sequence[]?[sequences.Length];
                            windups[a] = LoadDirections(frames, windup, part);
                        }
                        animations[a] = animation;
                    }
                    if (sequences[type.IdleAnimation] is null) throw new ArgumentException($"{definition.Id}.{part.Id}: idle must bind every part.");
                    uint Mask(IEnumerable<string>? names) { uint mask = 0; if (names is not null) foreach (string name in names) mask |= type.StateMask(name); return mask; }
                    uint State(string? name) => name is null ? 0 : type.StateMask(name);
                    parts[p] = new(part, sequences, windups, animations, part.Mount is null ? -1 : type.Weapons.ToList().FindIndex(m => m.Id == part.Mount),
                        Mask(part.VisibleWhen), Mask(part.HiddenWhen), State(part.SpinWhen), State(part.LoopWhen),
                        part.LoopAnimation is null ? -1 : type.AnimationIndex(part.LoopAnimation),
                        part.AimAtConstruction ? type.AnimationIndex("produce") : -1);
                }
                int drawLayers = 0;
                foreach (var part in definition.Parts) drawLayers |= 1 << (int)(part.DrawLayer ?? definition.DrawLayer);
                _models[i] = new(type, parts, type.States.Select(name => definition.States[name]).ToArray(), definition.DamagedState is null ? 0 : type.StateMask(definition.DamagedState), definition.DestroyedState is null ? 0 : type.StateMask(definition.DestroyedState), type.States.Contains("nearly-destroyed") ? type.StateMask("nearly-destroyed") : 0,
                    definition.DrawLayer, drawLayers, definition.SortOffset,
                    definition.Parts.Max(part => Math.Max(part.Layer, part.FarLayer ?? 0)) + 1, models.ScaleFor(type));
                MaximumParts = Math.Max(MaximumParts, parts.Length);
            }
        }
        catch { Node.Free(); throw; }
    }

    private Sequence[] LoadDirections(SpriteFrames frames, string sequence, ModelPart part)
    {
        using var name = new StringName(sequence);
        if (part.Facings < 2 || frames.HasAnimation(name)) return [LoadSequence(frames, name, part.Resource)];
        var directions = new Sequence[part.Facings];
        for (int facing = 0; facing < directions.Length; facing++)
        {
            using var variant = new StringName(sequence + "-" + facing.ToString(System.Globalization.CultureInfo.InvariantCulture));
            directions[facing] = LoadSequence(frames, variant, part.Resource);
        }
        return directions;
    }

    private Sequence LoadSequence(SpriteFrames frames, StringName name, string resource)
    {
        if (!frames.HasAnimation(name) || frames.GetFrameCount(name) == 0) throw new ArgumentException($"{resource}: missing sequence {name}.");
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
            if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("Invalid frame duration: " + resource);
            total += duration; ends[frame] = total;
        }
        double speed = frames.GetAnimationSpeed(name);
        if (total <= 0 || speed <= 0) throw new ArgumentException("Invalid sequence timing: " + resource);
        return new(textures, ends, speed, loop != SpriteFrames.LoopMode.None);
    }

    private SpriteBatch Batch(Texture2D texture)
    {
        var resolved = SpriteBatch.Resolve(texture); var key = (resolved.Texture.GetInstanceId(), resolved.Region);
        if (_textures.TryGetValue(key, out var batch)) return batch;
        batch = new(resolved.Texture, resolved.Region, true); batch.Node.Name = "Sprite_" + resolved.Texture.ResourcePath.GetFile().GetBaseName();
        _textures.Add(key, batch); _batches.Add(batch); Node.AddChild(batch.Node); return batch;
    }

    private SpriteBatch TintedBatch(SpriteBatch frame, Color tint)
    {
        if (tint.A >= 0.999f) return frame;
        if (_translucentBatches.TryGetValue(frame, out var batch)) return batch;
        batch = frame.Translucent();
        _translucentBatches.Add(frame, batch); _batches.Add(batch); Node.AddChild(batch.Node);
        return batch;
    }

    private readonly HashSet<EntityId> _ghostIds = new();

    public void Apply(ReadOnlySpan<EntityPose> poses, float alpha, long tick, int tickRate, ReadOnlySpan<WeaponPose> weapons = default, ReadOnlySpan<ConstructionProjectPose> projects = default, ConstructionProjectPose? preview = null,
        ReadOnlySpan<ContactPose> contacts = default, ContactDisplayStyle contactStyle = ContactDisplayStyle.BuildingModels)
    {
        alpha = Math.Clamp(alpha, 0, 1); double time = tick == 0 ? 0 : tick + alpha; SpriteCount = 0;
        _workTime = time;
        _workFlights.Update(poses, tick);
        foreach (var batch in _batches) batch.Clear();
        Span<Vector2> pivots = stackalloc Vector2[8];
        Span<float> angles = stackalloc float[8];
        int count = poses.Length + _wrecks.Count + projects.Length + (preview.HasValue ? 1 : 0);
        foreach (var contact in contacts) if (ContactDisplay.ShowsModel(contact.Knowledge, contactStyle)) count++;
        if (_ordered.Length < count) Array.Resize(ref _ordered, Math.Max(count, _ordered.Length * 2));
        int at = 0;
        foreach (var pose in poses) if (pose.Carrier.Value == 0) _ordered[at++] = pose;
        if (_wrecks.Count > 0)
        {
            _liveIds.Clear(); foreach (var pose in poses) _liveIds.Add(pose.Id);
            foreach (var wreck in _wrecks.Values) if (wreck.Presentation.Body.StartTick <= tick && !_liveIds.Contains(wreck.Id)) _ordered[at++] = wreck;
        }
        _ghostIds.Clear();
        uint ghostId = uint.MaxValue;
        if (projects.Length > 0 || preview.HasValue || contacts.Length > 0)
        {
            _liveIds.Clear();
            for (int i = 0; i < at; i++) _liveIds.Add(_ordered[i].Id);
            foreach (var project in projects) _liveIds.Add(project.Id);
            foreach (var project in projects) AppendProject(project);
            if (preview is { } hovered) AppendProject(hovered);
            foreach (var contact in contacts)
                if (ContactDisplay.ShowsModel(contact.Knowledge, contactStyle) && contact.Knowledge.UnitType is int type)
                    AppendProject(new(default, 0, type, contact.Knowledge.Position, false) { QuarterTurns = contact.QuarterTurns });
        }
        void AppendProject(ConstructionProjectPose project)
        {
            var type = _models[project.UnitType].Unit;
            var id = project.Id;
            uint states = 0;
            if (project.Upgrade is { } variant) states = type.StateMask(variant);
            if (project.Upgrade is not null || id == default)
                do { id = new(ghostId--); } while (!_liveIds.Add(id));
            _ghostIds.Add(id);
            var facing = project.Facing == default ? FacingDirection.FromQuarterTurns(project.QuarterTurns) : project.Facing;
            var presentation = new EntityPresentation(project.Owner, facing, facing, facing, facing,
                new(type.IdleAnimation, tick, 0), AnimationPlayback.None, states);
            _ordered[at++] = new(id, project.UnitType, project.Position, project.Position, presentation) { QuarterTurns = project.QuarterTurns };
        }
        bool idOrdered = true;
        for (int i = 1; i < at; i++)
            if (_ordered[i - 1].Id.Value > _ordered[i].Id.Value) { idOrdered = false; break; }
        if (!idOrdered) Array.Sort(_ordered, 0, at, _poseOrder);
        if (_drawDepths.Length < at * LayerCount)
        {
            Array.Resize(ref _drawOrder, _ordered.Length * ModelLayers);
            Array.Resize(ref _drawDepths, _ordered.Length * LayerCount);
        }
        Span<int> layerCounts = stackalloc int[LayerCount];
        layerCounts.Clear();
        bool sorted = true;
        int drawCount = 0;
        for (int i = 0; i < at; i++)
        {
            var pose = _ordered[i];
            var model = _models[pose.UnitType];
            int mask = _ghostIds.Contains(pose.Id) ? 1 << PreviewLayer : pose.Flight.Airborne ? 1 << AirLayer : model.DrawLayers;
            double x = pose.Previous.X + ((long)pose.Current.X - pose.Previous.X) * (double)alpha;
            double y = pose.Previous.Y + ((long)pose.Current.Y - pose.Previous.Y) * (double)alpha;
            double depth = (x * _sortAxis.X + y * _sortAxis.Y) / WorldPosition.UnitsPerCell + model.SortOffset;
            while (mask != 0)
            {
                int layer = System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                _drawOrder[drawCount] = new(i, layer, depth, pose.Id.Value);
                layerCounts[layer]++;
                if (drawCount > 0 && _drawOrder[drawCount - 1].CompareTo(_drawOrder[drawCount]) > 0) sorted = false;
                drawCount++;
            }
        }
        if (!sorted) Array.Sort(_drawOrder, 0, drawCount);
        Span<int> ranks = stackalloc int[LayerCount];
        ranks.Clear();
        foreach (var item in _drawOrder.AsSpan(0, drawCount))
        {
            float stride = LayerSpan / layerCounts[item.Layer];
            _drawDepths[item.Index * LayerCount + item.Layer] = (SpriteBaseHeight + item.Layer * LayerStep + ranks[item.Layer]++ * stride, stride);
        }
        for (int i = 0; i < at; i++)
        {
            var pose = _ordered[i];
            var model = _models[pose.UnitType]; var view = pose.Presentation;
            if (model.Damaged != 0 && pose.Health > 0 && (long)pose.Health * 2 <= model.Unit.MaxHealth) view = view with { States = view.States | model.Damaged };
            if (model.NearlyDestroyed != 0 && pose.Health > 0 && (long)pose.Health * 4 <= model.Unit.MaxHealth) view = view with { States = view.States | model.NearlyDestroyed };
            bool ghost = _ghostIds.Contains(pose.Id);
            var point = Interpolate(pose, alpha);
            float elevation = point.Y;
            float surface = _reliefSurface is { } grid && model.Unit.Flight is not null
                ? grid.SurfaceElevationAt(new((int)(point.X * 1024), (int)(point.Z * 1024))) / 1024f : _flightSurface;
            float height = Math.Max(0, elevation - surface);
            float lift = HoverLift(model.Unit, elevation);
            Vector2 origin = new(point.X, point.Z - lift);
            float body = _workFlights.Heading(pose, time, Angle(view.PreviousBodyFacing, view.BodyFacing, alpha)), weapon = Angle(view.PreviousWeaponFacing, view.WeaponFacing, alpha);
            int construct = pose.Upgrade.Active ? model.Unit.AnimationIndex("construct") : -1;
            bool moving = view.Body.Animation == model.Unit.MoveAnimation ||
                pose.Order is { Kind: UnitActionKind.Move or UnitActionKind.AttackMove,
                    Phase: OrderPhase.Moving or OrderPhase.Approaching or OrderPhase.Waiting or OrderPhase.Withdrawing };
            Color tint = ghost ? new(0.7f, 0.9f, 1, 0.42f) : pose.Cloaked ? new(0.62f, 0.7f, 0.76f, 0.42f) : Colors.White;
            float scale = model.Scale, stateFlash = 0;
            for (int state = 0; state < model.States.Length; state++) if ((view.States & (1u << state)) != 0)
            {
                var style = model.States[state]; stateFlash = Math.Max(stateFlash, (float)style.Pulse * (0.5f + 0.5f * MathF.Sin((float)time * MathF.Tau * 2 / tickRate))); tint *= new Color((float)style.Red, (float)style.Green, (float)style.Blue, (float)style.Opacity); scale *= (float)style.Scale;
            }
            for (int p = 0; p < model.Parts.Length; p++)
            {
                var runtime = model.Parts[p]; var part = runtime.Definition;
                if ((view.States & runtime.Visible) != runtime.Visible || (view.States & runtime.Hidden) != 0) { pivots[p] = origin; angles[p] = body; continue; }
                var playback = construct >= 0 ? new AnimationPlayback(construct, tick - pose.Upgrade.Work + 1, pose.Upgrade.Required) : view.Body;
                int animation = playback.Animation;
                if (animation < 0 || animation >= runtime.Sequences.Length || runtime.Sequences[animation] is null)
                { animation = model.Unit.IdleAnimation; playback = new(animation, 0, 0); }
                if (runtime.LoopAnimation >= 0 && (view.States & runtime.LoopWhen) != 0 && runtime.Sequences[runtime.LoopAnimation] is not null && (animation == model.Unit.IdleAnimation || animation == model.Unit.MoveAnimation))
                { animation = runtime.LoopAnimation; playback = new(animation, 0, 0); }
                if (construct < 0 && view.Weapon.Animation >= 0 && view.Weapon.Animation < runtime.Sequences.Length &&
                    view.Weapon.IsActive(tick) && runtime.Sequences[view.Weapon.Animation] is not null &&
                    runtime.Windups?[view.Weapon.Animation] is null)
                { playback = view.Weapon; animation = playback.Animation; }
                float extraFlash = 0, extraRecoil = 0;
                Sequence[]? windingUp = null;
                WeaponPose? stagedChannel = null;
                // shortcut: a shared part has one base clip; use rig mixing for layered skeletal animation.
                if (construct < 0 && pose.WeaponCount > 0 && pose.WeaponOffset + pose.WeaponCount <= weapons.Length)
                    foreach (var channel in weapons.Slice(pose.WeaponOffset, pose.WeaponCount))
                    {
                        var clip = channel.Animation;
                        if (runtime.Windups is { } windups)
                        {
                            var weaponMount = model.Unit.Weapons[channel.Mount];
                            int staged = model.Unit.AnimationIndex(weaponMount.Animation);
                            if (staged >= 0 && windups[staged] is { } windup)
                            {
                                int duration = weaponMount.Weapon.WindupTicks;
                                if (!moving && duration > 0 && channel.WindupEndTick > tick)
                                {
                                    animation = staged;
                                    playback = new(staged, channel.WindupEndTick - duration, duration);
                                    windingUp = windup;
                                    stagedChannel = channel;
                                    break;
                                }
                                if (clip.Animation == staged && clip.IsActive(tick))
                                {
                                    if (moving || view.Body.StartTick > clip.StartTick) continue;
                                    animation = staged;
                                    playback = clip;
                                    stagedChannel = channel;
                                    break;
                                }
                            }
                        }
                        if (!clip.IsActive(tick) || clip.Animation < 0 || clip.Animation >= runtime.Sequences.Length || runtime.Sequences[clip.Animation] is null) continue;
                        float phase = (float)Math.Clamp((time - clip.StartTick) / clip.DurationTicks, 0, 1);
                        float kick = MathF.Sin(MathF.PI * Math.Clamp(phase * 3, 0, 1));
                        var style = runtime.Animations[clip.Animation]!;
                        extraFlash = Math.Max(extraFlash, kick * (float)style.Flash); extraRecoil += kick * (float)style.Recoil;
                        playback = clip; animation = clip.Animation;
                    }
                float? constructionAngle = part.AimAtConstruction && !ghost && construct < 0
                    ? ConstructionAngle(pose, alpha, at) : null;
                if (constructionAngle.HasValue && runtime.WorkAnimation >= 0 && runtime.Sequences[runtime.WorkAnimation] is not null)
                {
                    animation = runtime.WorkAnimation;
                    playback = new(animation, 0, 0);
                }
                var effect = runtime.Animations[animation]!;
                var facing = effect.Facing ?? part.Facing;
                float angle = facing == ModelFacing.Body ? body : weapon;
                if (facing == ModelFacing.Weapon && stagedChannel is { } stagedAim)
                    angle = Angle(stagedAim.Facing, stagedAim.Facing, 1);
                else if (!ghost && facing == ModelFacing.Weapon && runtime.Mount >= 0 && pose.WeaponOffset + runtime.Mount < weapons.Length)
                {
                    var aim = weapons[pose.WeaponOffset + runtime.Mount];
                    bool firing = effect.Facing == ModelFacing.Weapon && part.MuzzleLocal.HasValue &&
                        aim.Animation.Animation == animation && aim.Animation.IsActive(tick);
                    angle = part.AimAtConstruction && aim.Target == default && !aim.Animation.IsActive(tick)
                        ? body + MathF.PI : Angle(aim.PreviousFacing, aim.Facing, firing ? 1 : alpha);
                }
                else if (part.AimAtConstruction) angle = body + MathF.PI;
                angle = constructionAngle ?? angle;
                if (part.Spin != 0 && (runtime.SpinWhen == 0 || (view.States & runtime.SpinWhen) != 0))
                {
                    double seconds = time / tickRate;
                    if (part.EmitterSpin)
                    {
                        var emitter = pose.Emitter;
                        double elapsed = Math.Max(0, time - emitter.DeployStartTick);
                        double delay = emitter.DeployTicks;
                        double integrated = delay > 0 && elapsed < delay ? elapsed * elapsed / (2 * delay) : elapsed - delay / 2;
                        seconds = !emitter.HasStealthRotor || emitter.Moving || ghost ? 0 : integrated / tickRate;
                    }
                    angle += (float)(seconds * part.Spin * Math.PI / 180);
                }
                int direction = 0;
                if (part.Facings > 1)
                {
                    float step = MathF.Tau / part.Facings;
                    direction = (int)MathF.Floor(Mathf.PosMod(angle, MathF.Tau) / step + 0.5f) % part.Facings;
                    angle = direction * step;
                }
                Vector2 mount = part.Parent < 0 ? origin : pivots[part.Parent];
                var parentOffset = Rotate(new((float)part.OffsetX * scale, (float)part.OffsetY * scale),
                    part.Parent < 0 ? body : angles[part.Parent]);
                mount += parentOffset;
                angles[p] = angle;
                float drawAngle = (part.Facings == 0 ? angle : 0) + Mathf.DegToRad((float)part.SpriteRotation);
                float progress = playback.DurationTicks > 0 ? (float)Math.Clamp((time - playback.StartTick) / playback.DurationTicks, 0, 1) : 0;
                float pulse = playback.DurationTicks > 0 ? MathF.Sin(MathF.PI * Math.Clamp(progress * 3, 0, 1)) : 0;
                float bob = (float)effect.Bob * MathF.Sin((float)(time - playback.StartTick) * MathF.Tau * 3 / tickRate);
                mount += Rotate(new(bob + (float)effect.SlideX * MathF.Sin((float)(time - playback.StartTick) / tickRate * MathF.Tau),
                    Math.Max((float)effect.Recoil * pulse, extraRecoil) + (float)effect.SlideY * MathF.Sin((float)(time - playback.StartTick) / tickRate * MathF.Tau)), angle) * model.Scale;
                pivots[p] = mount;
                float buildScale = playback.DurationTicks > 0 ? Mathf.Lerp((float)effect.ScaleFrom, 1, progress) : 1;
                Vector2 size = new((float)part.Width * scale * buildScale, (float)part.Height * scale * buildScale);
                // Directional rotor frames already include the height of the fixed mounting point.
                if (part.EmitterSpin && part.Facings == 0)
                {
                    var emitter = pose.Emitter;
                    float deploy = emitter.Moving || !emitter.HasStealthRotor ? 0 :
                        emitter.DeployTicks == 0 ? 1 : (float)Math.Clamp((time - emitter.DeployStartTick) / emitter.DeployTicks, 0, 1);
                    size *= Mathf.Lerp(0.6f, 1f, deploy);
                }
                var partTint = tint; if (playback.DurationTicks > 0) partTint.A *= Mathf.Lerp((float)effect.OpacityFrom, 1, progress);
                Vector2 center = mount + Rotate(new((0.5f - (float)part.PivotX) * size.X, (0.5f - (float)part.PivotY) * size.Y), drawAngle);
                var team = _ownerColor(view.Owner);
                team.A = Math.Max(stateFlash, Math.Max(pulse * (float)effect.Flash, extraFlash));
                var directions = windingUp ?? runtime.Sequences[animation]!;
                var frame = directions[directions.Length == 1 ? 0 : direction].At(playback, time, tickRate);
                if (model.Unit.Flight is { } flight && part.Parent < 0 && height > 0.25f && !ghost)
                {
                    float shadowOffset = flight.Mode == FlightMode.Hover ? 0 : height * 0.28f;
                    var shadow = new Color(0, 0, 0, 0.3f * tint.A);
                    float shadowHeight = _reliefSurface is null ? ShadowHeight : surface + ShadowHeight;
                    TintedBatch(frame, shadow).Add(new(center.X + shadowOffset, shadowHeight, center.Y + lift + shadowOffset * 0.65f),
                        drawAngle, size * 0.9f, shadow, Colors.Transparent);
                    SpriteCount++;
                }
                int layer = ghost ? PreviewLayer : pose.Flight.Airborne ? AirLayer : (int)(part.DrawLayer ?? model.DrawLayer);
                var (depth, stride) = _drawDepths[i * LayerCount + layer];
                int partLayer = part.FarLayer is int farLayer && parentOffset.Dot(new(_depthAxis.X, _depthAxis.Z)) < 0
                    ? farLayer : part.Layer;
                var position = new Vector3(center.X, elevation, center.Y) + _depthAxis * (depth + partLayer * (stride / model.PartLayerCount));
                TintedBatch(frame, partTint).Add(position, drawAngle, size, partTint, team);
                SpriteCount++;
            }
        }
        foreach (var batch in _batches) batch.Publish();
    }

    private float? ConstructionAngle(in EntityPose pose, float alpha, int count)
    {
        if (pose.Order.Target == default || pose.Order.Phase != OrderPhase.Engaging ||
            pose.Order.Kind is not (UnitActionKind.Build or UnitActionKind.UpgradeSupport)) return null;
        int index = Array.BinarySearch(_ordered, 0, count, pose with { Id = pose.Order.Target }, _poseOrder);
        if (index < 0) return null;
        var offset = Interpolate(_ordered[index], alpha) - Interpolate(pose, alpha);
        return offset.X == 0 && offset.Z == 0 ? null : MathF.Atan2(offset.X, -offset.Z);
    }

    public static float Angle(FacingDirection previous, FacingDirection current, float alpha)
    {
        float from = MathF.Atan2(previous.X, -previous.Y), to = MathF.Atan2(current.X, -current.Y);
        return from + Mathf.Wrap(to - from, -MathF.PI, MathF.PI) * alpha;
    }
    private static Vector2 Rotate(Vector2 point, float angle) => new(point.X * MathF.Cos(angle) - point.Y * MathF.Sin(angle), point.X * MathF.Sin(angle) + point.Y * MathF.Cos(angle));
    private float HoverLift(UnitDefinition unit, float elevation) => _reliefSurface is null && unit.Flight?.Mode == FlightMode.Hover
        ? HoverLift(elevation, _flightSurface) : 0;
    internal static float HoverLift(float elevation, float surface) => Math.Max(0, elevation - surface) * HoverLiftScale;
    private static Vector3 Interpolate(in EntityPose pose, float alpha) => new(
        Lerp(pose.Previous.X, pose.Current.X, alpha), Lerp(pose.Previous.Elevation, pose.Current.Elevation, alpha), Lerp(pose.Previous.Y, pose.Current.Y, alpha));
    private static float Lerp(int previous, int current, float alpha) => (previous + ((long)current - previous) * alpha) / 1024;
}
