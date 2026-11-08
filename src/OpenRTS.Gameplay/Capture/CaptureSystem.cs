using Frent;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core.Capture;

internal interface ICaptureWorld
{
    bool TryGetEntity(EntityId id, out Entity entity);
    UnitDefinition TypeOf(Entity entity);
    bool HasPassengers(Entity entity);
    void IssueOrder(EntityId actor, UnitOrder order, bool queued);
    void SetMovementTarget(EntityId actor, WorldPosition position);
    void Halt(EntityId actor);
    void ChangeOwner(EntityId target, uint owner);
}

internal sealed class CaptureSystem(ICaptureWorld world, ICaptureMechanic mechanic)
{
    private readonly ICaptureMechanic _mechanic = mechanic;

    internal void Initialize(Entity entity, UnitDefinition type) => _mechanic.Initialize(entity, type);
    internal void Reset(Entity entity) => _mechanic.Reset(entity);
    internal void OwnerChanged(Entity entity, long tick) => _mechanic.OwnerChanged(entity, tick);

    private CaptureParticipant ParticipantOf(Entity entity) => new(entity, world.TypeOf(entity),
        entity.Get<OwnershipState>().Owner, entity.Get<HealthState>().Current);

    private bool TryGetParticipants(Entity entity, EntityId id, out CaptureParticipant actor, out CaptureParticipant target)
    {
        actor = target = default;
        if (!world.TryGetEntity(id, out var targetEntity)) return false;
        actor = ParticipantOf(entity);
        if (actor.Owner == 0 || actor.Owner == targetEntity.Get<OwnershipState>().Owner || world.HasPassengers(targetEntity)) return false;
        target = ParticipantOf(targetEntity);
        return _mechanic.CanStart(actor, target);
    }

    internal bool CanStart(Entity entity, EntityId id) => TryGetParticipants(entity, id, out _, out _);

    internal bool TryApplyAbility(Entity entity, AbilityDefinition ability, WorldCommand command)
    {
        if (ability.Kind != AbilityKind.Capture || command.Kind == CommandKind.SetAutoCast || !CanStart(entity, command.TargetEntity))
            return false;
        world.IssueOrder(entity.Get<EntityIdentity>().Id, new(UnitActionKind.Capture, default, command.TargetEntity), command.Queued);
        return true;
    }

    internal void EndOrders(long tick) => _mechanic.EndOrders(tick);

    internal void Advance(Entity entity, long tick)
    {
        var actorId = entity.Get<EntityIdentity>().Id;
        ref var order = ref entity.Get<OrderState>();
        var targetId = order.ActiveOrder.Target;
        if (!TryGetParticipants(entity, targetId, out var actor, out var target))
        {
            _mechanic.Reset(entity);
            order.HasOrder = false;
            world.Halt(actorId);
            return;
        }

        var position = entity.Get<SpatialState>().Position;
        var targetPosition = target.Entity.Get<SpatialState>().Position;
        long distance = ObjectGeometry.DistanceSquared(actor.Type, position, target.Type, targetPosition, out _, out _);
        var step = _mechanic.Advance(new(actor, target, tick, distance));
        switch (step)
        {
            case CaptureStep.Approach:
                if (!entity.Get<MotionState>().Moving || target.Type.Movable && (tick + actorId.Value) % 4 == 0)
                    world.SetMovementTarget(actorId, ObjectGeometry.ClosestPoint(target.Type, targetPosition, position));
                order.OrderPhase = OrderPhase.Approaching;
                break;
            case CaptureStep.Engage:
                world.Halt(actorId);
                order.OrderPhase = OrderPhase.Engaging;
                break;
            case CaptureStep.Complete:
                world.Halt(actorId);
                order.OrderPhase = OrderPhase.Engaging;
                world.ChangeOwner(targetId, actor.Owner);
                _mechanic.Reset(entity);
                order.HasOrder = false;
                order.OrderPhase = OrderPhase.Idle;
                break;
            default:
                throw new InvalidOperationException("Capture returned an unknown step.");
        }
    }

    internal CaptureProgress GetProgress(Entity entity, in OrderState order)
    {
        if (!order.HasOrder || order.ActiveOrder.Kind != UnitActionKind.Capture ||
            !world.TryGetEntity(order.ActiveOrder.Target, out var target)) return default;
        return _mechanic.GetProgress(entity, ParticipantOf(target));
    }

    internal CaptureTargetProgress GetTargetProgress(Entity entity, UnitDefinition type) => _mechanic.GetTargetProgress(entity, type);

    internal void WriteState(BinaryWriter writer) => _mechanic.WriteState(writer);

    internal static void WriteEntityState(CaptureSystem? system, BinaryWriter writer, Entity entity)
    {
        if (system is not null) system._mechanic.WriteEntityState(writer, entity);
        else
        {
            writer.Write(false);
            writer.Write(false);
        }
    }
}
