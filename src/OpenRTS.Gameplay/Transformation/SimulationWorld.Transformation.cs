using Frent;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Economy;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int TransformationStallTicks = 250;
    private sealed record TransformationRecipe(int Target, TransformationClip Before, TransformationClip After, bool AllowRotation);
    private readonly record struct TransformationClip(int Animation, int Ticks, FacingDirection? Facing);

    private sealed class PendingTransformation(TransformationRecipe recipe, WorldPosition position, int turns, WorldPosition? moveAfter)
    {
        internal readonly TransformationRecipe Recipe = recipe;
        internal readonly WorldPosition Position = position;
        internal readonly int Turns = turns;
        internal WorldPosition? MoveAfter = moveAfter;
        internal TransformationPhase Phase = TransformationPhase.Approaching;
        internal long UntilTick, LastProgress, NextClearance;
        internal WorldPosition LastPosition;
    }

    private readonly SortedDictionary<(int Source, string Id), TransformationRecipe> _transformationRecipes;
    private readonly SortedDictionary<uint, PendingTransformation> _transformations = new();
    private readonly List<uint> _transformationPass = [];
    private readonly List<EntityId> _transformationCandidates = [];
    private readonly List<EntityId> _transformationOccupants = [];
    private readonly List<(WorldPosition Position, BodyShape Body)> _transformationExits = [];

    private SortedDictionary<(int, string), TransformationRecipe> CompileTransformations(IReadOnlyList<ActorTransformation> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var recipes = new SortedDictionary<(int, string), TransformationRecipe>(Comparer<(int Source, string Id)>.Create(
            (a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : StringComparer.Ordinal.Compare(a.Id, b.Id)));
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            int source = Array.FindIndex(_types, type => type.Id == definition.Source);
            int target = Array.FindIndex(_types, type => type.Id == definition.Target);
            if (source < 0 || target < 0 || source == target || string.IsNullOrWhiteSpace(definition.Id) || definition.Id.Length > 128)
                throw new ArgumentException("A transformation needs an ID and two distinct, known unit types.", nameof(definitions));
            if (!recipes.TryAdd((source, definition.Id), new(target, Clip(definition.Before, source), Clip(definition.After, target), definition.AllowRotation)))
                throw new ArgumentException($"Duplicate transformation '{definition.Id}' on '{definition.Source}'.", nameof(definitions));
        }
        return recipes;

        TransformationClip Clip(TransformationAnimation? animation, int type)
        {
            if (animation is null) return default;
            int index = animation.Clip is null ? -1 : _types[type].AnimationIndex(animation.Clip);
            if (index < 0 || animation.Ticks is < 1 or > 1000000 ||
                animation.Facing is { } facing && (facing == default || Math.Abs((long)facing.X) > 1024 || Math.Abs((long)facing.Y) > 1024))
                throw new ArgumentException($"Invalid transformation clip for '{_types[type].Id}'.", nameof(definitions));
            return new(index, animation.Ticks, animation.Facing);
        }
    }

    internal bool TryStartTransformation(EntityId id, string action, WorldPosition? requested, int turns)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > 128 || (uint)turns > 3 ||
            requested is { } point && (point.X < 0 || point.X > Width || point.Y < 0 || point.Y > Depth ||
                Math.Abs((long)point.Elevation) > WorldPosition.MaximumExtent) ||
            !TryGetEntity(id, out var actor) || !IsOnMap(actor) || IsConstructing(actor) || !CanLeaveForm(actor))
            return false;

        var identity = actor.Get<EntityIdentity>();
        if (!_transformationRecipes.TryGetValue((identity.Type, action), out var recipe)) return false;
        var source = TypeOf(actor);
        var target = _types[recipe.Target];
        if (turns != 0 && (!recipe.AllowRotation || target.Footprint is null)) return false;
        target = target.WithRotation(turns);
        var from = actor.Get<SpatialState>().Position;
        bool followMove = !source.Movable && target.Movable;
        var position = source.Movable ? requested ?? from : from;
        if (target.Footprint is { } footprint && (requested is null || !source.Movable))
            position = AlignTransformationSite(position, footprint);
        if (!source.Movable && !followMove && requested is { } site && (site.X != position.X || site.Y != position.Y))
            return false;
        if (position.X < 0 || position.X > Width || position.Y < 0 || position.Y > Depth) return false;
        position = GroundPosition(position, from.Elevation);
        if (target.Footprint is not null && !SiteTerrainAllows(target, position)) return false;

        int index = _indices[id];
        if (source.Movable && (!StopAllowed(identity.Type, position) ||
            _navigation is not null && !Resolver(identity.Type).CanReach(from, position, StepCancellation) ||
            !SetTarget(index, position))) return false;
        if (!source.Movable) Halt(index);

        CancelTransformation(id, stop: false);
        _transformations.Add(id.Value, new(recipe, position, turns, followMove ? requested : null)
            { LastProgress = Tick, LastPosition = from });
        ref var orders = ref actor.Get<OrderState>();
        orders.ActiveOrder = new(UnitActionKind.Transform, position);
        orders.HasOrder = true;
        orders.OrderPhase = from == position || !source.Movable ? OrderPhase.Waiting : OrderPhase.Approaching;
        orders.OrderStarted = Tick;
        orders.CombatTarget = default;
        orders.PendingOrders?.Clear();
        ref var presentation = ref actor.Get<PresentationState>();
        presentation.HasAim = false;
        presentation.AimEntity = default;
        CancelWindups(index);
        _capture?.Reset(actor);
        return true;
    }

    private WorldPosition AlignTransformationSite(WorldPosition point, FootprintDefinition footprint)
    {
        int cell = _baseGrid?.CellSize ?? footprint.CellSize;
        return new(Align(point.X, footprint.HalfWidth, Width), Align(point.Y, footprint.HalfHeight, Depth), point.Elevation);

        int Align(int value, int half, int extent)
        {
            if (2 * half > extent) return value;
            int offset = Math.Clamp(value - half, 0, extent - 2 * half);
            return Math.Min((offset + cell / 2) / cell, (extent - 2 * half) / cell) * cell + half;
        }
    }

    private bool CanLeaveForm(Entity actor) =>
        actor.Get<HealthState>().Current > 0 && _transport?.HasPassengers(actor) != true &&
        (!actor.Has<UpgradeState>() || actor.Get<UpgradeState>().Project < 0) &&
        (!actor.Has<ProducerState>() || actor.Get<ProducerState>().Queues.All(queue => queue.Count == 0)) &&
        (!actor.Has<TruckState>() || actor.Get<TruckState>().Cargo == 0);

    private TransformationPose ReadTransformation(EntityId id) =>
        _transformations.Count != 0 && _transformations.TryGetValue(id.Value, out var plan)
            ? new(plan.Phase, plan.Recipe.Target, plan.Position, plan.Turns, plan.UntilTick)
            : default;

    private bool IsTransformationLocked(Entity actor) => _transformations.Count != 0 &&
        _transformations.TryGetValue(actor.Get<EntityIdentity>().Id.Value, out var plan) &&
        plan.Phase is TransformationPhase.Preparing or TransformationPhase.Finishing;

    private void OnTransformationOwnerChanged(EntityId id)
    {
        if (_transformations.TryGetValue(id.Value, out var plan) && plan.Phase == TransformationPhase.Finishing)
            plan.MoveAfter = null;
        else CancelTransformation(id);
    }

    private void CancelTransformation(EntityId id, bool stop = true)
    {
        if (_transformations.Count == 0 || !_transformations.Remove(id.Value, out var plan)) return;
        if (stop && TryGetEntity(id, out var actor) && plan.Phase != TransformationPhase.Finishing)
        {
            Halt(_indices[id]);
            ref var order = ref actor.Get<OrderState>();
            order.HasOrder = false;
            order.OrderPhase = OrderPhase.Idle;
            if (plan.Phase == TransformationPhase.Preparing)
            {
                actor.Get<PresentationState>().BodyAnimationOverride = false;
                _economy.InvalidatePower();
            }
        }
        if (plan.Phase != TransformationPhase.Finishing)
            EmitEvent(new(Tick, WorldEventKind.TransformationCancelled, id, plan.Position, plan.Recipe.Target) { QuarterTurns = plan.Turns });
    }

    private void AdvanceTransformations()
    {
        if (_transformations.Count == 0) return;
        if (_motion is null) RebuildMotionIndex();
        else EnsureMotionIndex();

        _transformationPass.Clear();
        _transformationPass.AddRange(_transformations.Keys);
        foreach (uint key in _transformationPass)
        {
            StepCancellation.ThrowIfCancellationRequested();
            if (!_transformations.TryGetValue(key, out var plan) || plan.Phase == TransformationPhase.Finishing) continue;
            EntityId id = new(key);
            if (!TryGetEntity(id, out var actor) || !IsOnMap(actor) || !CanLeaveForm(actor))
            { CancelTransformation(id); continue; }

            if (actor.Get<OrderState>().ActiveOrder.Kind != UnitActionKind.Transform || !actor.Get<OrderState>().HasOrder)
            { CancelTransformation(id, stop: false); continue; }

            var source = TypeOf(actor);
            var target = _types[plan.Recipe.Target].WithRotation(plan.Turns);
            var from = actor.Get<SpatialState>().Position;
            bool arrived = !source.Movable || from == plan.Position;
            if (from != plan.LastPosition)
            {
                plan.LastPosition = from;
                if (plan.Phase == TransformationPhase.Approaching) plan.LastProgress = Tick;
            }

            if (target.Footprint is { } footprint)
            {
                if (!SiteTerrainAllows(target, plan.Position) ||
                    !CheckTransformationSite(actor, plan, footprint, arrived))
                { CancelTransformation(id); continue; }
                if (_transformationOccupants.Count != 0)
                {
                    if (plan.Phase == TransformationPhase.Preparing || Tick - plan.LastProgress >= TransformationStallTicks)
                        CancelTransformation(id);
                    else ClearTransformationSite(plan, footprint);
                    continue;
                }
            }

            if (!arrived)
            {
                if (!actor.Get<MotionState>().Moving || Tick - plan.LastProgress >= TransformationStallTicks)
                    CancelTransformation(id);
                continue;
            }

            if (plan.Phase == TransformationPhase.Preparing && Tick < plan.UntilTick) continue;
            if (!TryPrepareReplacement(id, source, target, plan.Position, out var candidate))
            { CancelTransformation(id); continue; }

            if (plan.Phase != TransformationPhase.Preparing && plan.Recipe.Before.Ticks > 0)
            {
                Halt(_indices[id]);
                plan.Phase = TransformationPhase.Preparing;
                plan.UntilTick = Tick + plan.Recipe.Before.Ticks;
                _economy.InvalidatePower();
                PlayTransformationClip(id, plan.Recipe.Before);
                continue;
            }
            ReplaceActor(id, actor, target, plan, candidate);
        }
    }

    private bool CheckTransformationSite(Entity actor, PendingTransformation plan, FootprintDefinition footprint, bool arrived)
    {
        var position = actor.Get<SpatialState>().Position;
        var body = TypeOf(actor).Body;
        // A unit in the center may prevent exact arrival. Start clearing once the source reaches the site perimeter.
        bool near = arrived || Math.Abs(position.X - plan.Position.X) <= footprint.HalfWidth + body.HalfWidth + SpeedPerTick &&
            Math.Abs(position.Y - plan.Position.Y) <= footprint.HalfHeight + body.HalfHeight + SpeedPerTick;
        var id = actor.Get<EntityIdentity>().Id;
        uint owner = actor.Get<OwnershipState>().Owner;
        _transformationOccupants.Clear();
        QueryTransformationArea(plan.Position, footprint.HalfWidth, footprint.HalfHeight);
        foreach (var other in _transformationCandidates)
        {
            if (other == id || !TryGetEntity(other, out var blocker)) continue;
            var type = TypeOf(blocker);
            var at = blocker.Get<SpatialState>().Position;
            if (type.Footprint is { } stamp)
            {
                if (ConstructionGeometry.Overlaps(footprint, plan.Position, stamp, at)) return false;
                continue;
            }
            if (!ConstructionGeometry.Touches(footprint, plan.Position, at, at, type.Body)) continue;
            if (!type.Movable) return false;
            if (!near) continue;
            uint blockerOwner = blocker.Get<OwnershipState>().Owner;
            if (blockerOwner != 0 && blockerOwner != owner) return false;
            _transformationOccupants.Add(other);
        }
        return true;
    }

    private void ClearTransformationSite(PendingTransformation plan, FootprintDefinition footprint)
    {
        if (plan.Phase != TransformationPhase.Clearing)
        {
            plan.Phase = TransformationPhase.Clearing;
            plan.LastProgress = Tick;
        }
        if (Tick < plan.NextClearance) return;
        plan.NextClearance = Tick + 8;
        _transformationExits.Clear();
        foreach (var id in _transformationOccupants)
        {
            var actor = _entityHandles[_indices[id]];
            var position = actor.Get<SpatialState>().Position;
            var body = TypeOf(actor).Body;
            var motion = actor.Get<MotionState>();
            if (motion.Moving && actor.Get<NavigationAgentState>().WaitTicks < 8 &&
                !ConstructionGeometry.Touches(footprint, plan.Position, motion.Target, motion.Target, body))
            {
                _transformationExits.Add((motion.Target, body));
                continue;
            }
            foreach (var exit in ConstructionGeometry.Exits(footprint, plan.Position, position, body, Width, Depth)
                .OrderBy(point => ObjectGeometry.Squared(position, point)))
            {
                if (_transformationExits.Any(goal => BodyGeometry.Overlap(exit, exit, body, goal.Position, goal.Position, goal.Body)) ||
                    !TryEvacuate(id, exit, preserveOrder: false)) continue;
                _transformationExits.Add((exit, body));
                break;
            }
        }
    }

    private void QueryTransformationArea(WorldPosition position, int halfWidth, int halfHeight)
    {
        (_motion ?? _combatIndex)!.Query(new(position.X - halfWidth, position.Y - halfHeight),
            new(position.X + halfWidth, position.Y + halfHeight), 0, _transformationCandidates);
        _transformationCandidates.Sort(static (a, b) => a.Value.CompareTo(b.Value));
    }

    private bool TryPrepareReplacement(EntityId sourceId, UnitDefinition source, UnitDefinition target, WorldPosition position,
        out NavigationGrid? candidate)
    {
        candidate = null;
        if (_baseGrid is { } grid && (source.Footprint is not null || target.Footprint is not null))
        {
            candidate = ComposeFootprints(grid, target.Footprint, position, sourceId);
            if (!SafeGrid(candidate, out _, sourceId)) return false;
        }
        if (target.Footprint is not null) return true;

        var body = target.Body;
        if (position.X < body.HalfWidth || position.X > Width - body.HalfWidth ||
            position.Y < body.HalfHeight || position.Y > Depth - body.HalfHeight ||
            _construction?.SiteStopClear(position, body) == false) return false;
        var effective = candidate ?? _navigation?.Grid;
        if (effective is null ? !target.Locomotor.Rules[0].CanStop :
            !TerrainGeometry.CanStop(effective, position, body, target.Locomotor)) return false;

        QueryTransformationArea(position, body.HalfWidth, body.HalfHeight);
        foreach (var id in _transformationCandidates)
        {
            if (id == sourceId || !TryGetEntity(id, out var other)) continue;
            var type = TypeOf(other);
            var at = other.Get<SpatialState>().Position;
            if (type.Footprint is { } footprint
                ? ConstructionGeometry.Touches(footprint, at, position, position, body)
                : BodyGeometry.Overlap(position, position, body, at, at, type.Body)) return false;
        }
        return true;
    }

    private void ReplaceActor(EntityId id, Entity actor, UnitDefinition target, PendingTransformation plan, NavigationGrid? candidate)
    {
        uint owner = actor.Get<OwnershipState>().Owner;
        int hp = (int)Math.Clamp((long)actor.Get<HealthState>().Current * target.MaxHealth / TypeOf(actor).MaxHealth, 1, target.MaxHealth);
        var from = actor.Get<SpatialState>().Position;
        var facing = actor.Get<PresentationState>().BodyFacing;
        var created = AllocateEntityId();
        _transformations.Remove(id.Value);
        DestroyIndex(_indices[id], wreck: false, replacing: true);
        bool finishing = plan.Recipe.After.Ticks > 0;
        if (finishing)
        {
            plan.Phase = TransformationPhase.Finishing;
            plan.UntilTick = Tick + plan.Recipe.After.Ticks;
            _transformations.Add(created.Value, plan);
        }
        Spawn(plan.Recipe.Target, plan.Position, owner, id: created, initialHealth: hp, quarterTurns: plan.Turns);
        if (target.Footprint is null)
        {
            ref var visual = ref _entityHandles[_indices[created]].Get<PresentationState>();
            visual.BodyFacing = visual.PreviousBodyFacing = facing;
        }
        if (candidate is not null) CommitGrid(candidate);
        RebuildMotionIndex();
        EmitEvent(new(Tick, WorldEventKind.ActorTransformed, id, plan.Position, plan.Recipe.Target,
            TargetEntity: created, SourcePosition: from, Owner: owner) { QuarterTurns = plan.Turns });
        if (finishing) PlayTransformationClip(created, plan.Recipe.After);
        else FinishTransformation(created, plan);
    }

    private void PlayTransformationClip(EntityId id, TransformationClip clip)
    {
        int index = _indices[id];
        ApplyPresentation(index, new(Tick, 0, 0, CommandKind.PlayAnimation, id,
            Animation: clip.Animation, DurationTicks: clip.Ticks, Track: AnimationTrack.Body));
    }

    private void ApplyTransformationFacings()
    {
        foreach (var (id, plan) in _transformations)
        {
            var clip = plan.Phase switch
            {
                TransformationPhase.Preparing => plan.Recipe.Before,
                TransformationPhase.Finishing => plan.Recipe.After,
                _ => default
            };
            if (clip.Facing is not { } facing) continue;
            ref var presentation = ref _entityHandles[_indices[new(id)]].Get<PresentationState>();
            presentation.BodyFacing = facing;
            presentation.WeaponFacing = facing;
        }
    }

    private void CompleteTransformations()
    {
        if (_transformations.Count == 0) return;
        _transformationPass.Clear();
        foreach (var (id, plan) in _transformations)
            if (plan.Phase == TransformationPhase.Finishing && Tick >= plan.UntilTick) _transformationPass.Add(id);
        foreach (uint key in _transformationPass)
        {
            var plan = _transformations[key];
            _transformations.Remove(key);
            EntityId id = new(key);
            var actor = _entityHandles[_indices[id]];
            lock (StorageGate) EconomySystem.ActivateIncome(actor, TypeOf(actor).Economy, Tick);
            _economy.InvalidatePower();
            FinishTransformation(id, plan);
        }
    }

    private void FinishTransformation(EntityId id, PendingTransformation plan)
    {
        EmitEvent(new(Tick, WorldEventKind.TransformationCompleted, id, plan.Position, plan.Recipe.Target) { QuarterTurns = plan.Turns });
        if (plan.MoveAfter is { } move) IssueOrder(_indices[id], new(UnitActionKind.Move, move), queued: false);
    }

    private void WriteTransformations(BinaryWriter writer)
    {
        if (_transformationRecipes.Count == 0) return;
        writer.Write(31);
        writer.Write(_transformationRecipes.Count);
        foreach (var (key, recipe) in _transformationRecipes)
        {
            writer.Write(key.Source); writer.Write(key.Id); writer.Write(recipe.Target);
            WriteClip(recipe.Before); WriteClip(recipe.After); writer.Write(recipe.AllowRotation);
        }
        writer.Write(_transformations.Count);
        foreach (var (id, plan) in _transformations)
        {
            writer.Write(id); writer.Write(plan.Recipe.Target); writer.Write(plan.Turns); writer.Write((int)plan.Phase);
            WriteClip(plan.Recipe.Before); WriteClip(plan.Recipe.After);
            writer.Write(plan.UntilTick); writer.Write(plan.LastProgress); writer.Write(plan.NextClearance);
            WritePoint(plan.Position); WritePoint(plan.LastPosition);
            writer.Write(plan.MoveAfter.HasValue);
            if (plan.MoveAfter is { } move) WritePoint(move);
        }

        void WritePoint(WorldPosition point) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Elevation); }
        void WriteClip(TransformationClip clip)
        {
            writer.Write(clip.Animation); writer.Write(clip.Ticks); writer.Write(clip.Facing.HasValue);
            if (clip.Facing is { } facing) { writer.Write(facing.X); writer.Write(facing.Y); }
        }
    }
}
