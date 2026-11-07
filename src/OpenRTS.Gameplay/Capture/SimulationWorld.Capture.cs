using Frent;
using OpenRTS.Core.Capture;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly ICaptureMechanic? _capture;

    private CaptureParticipant CaptureParticipantOf(Entity entity) => new(entity, TypeOf(entity),
        entity.Get<OwnershipState>().Owner, entity.Get<HealthState>().Current);

    private bool TryCaptureParticipants(int index, EntityId id, out int targetIndex,
        out CaptureParticipant actor, out CaptureParticipant target)
    {
        actor = target = default;
        targetIndex = -1;
        if (_capture is null || !_indices.TryGetValue(id, out targetIndex)) return false;
        actor = CaptureParticipantOf(_entityHandles[index]);
        var targetEntity = _entityHandles[targetIndex];
        if (actor.Owner == 0 || actor.Owner == targetEntity.Get<OwnershipState>().Owner || HasPassengers(targetEntity)) return false;
        target = CaptureParticipantOf(targetEntity);
        return _capture.CanStart(actor, target);
    }

    private bool CanStartCapture(int index, EntityId id) => TryCaptureParticipants(index, id, out _, out _, out _);

    private bool TryApplyNonWeaponAbility(int index, AbilityDefinition ability, WorldCommand command)
    {
        if (ability.Kind != AbilityKind.Capture || command.Kind == CommandKind.SetAutoCast || !CanStartCapture(index, command.TargetEntity))
            return false;
        IssueOrder(index, new(UnitActionKind.Capture, default, command.TargetEntity), command.Queued);
        return true;
    }

    private void UpdateCapture(int index)
    {
        var entity = _entityHandles[index];
        ref var order = ref entity.Get<OrderState>();
        if (!TryCaptureParticipants(index, order.ActiveOrder.Target, out int targetIndex, out var actor, out var target))
        {
            _capture?.Reset(entity);
            order.HasOrder = false;
            Halt(index);
            return;
        }

        long distance = ObjectGeometry.DistanceSquared(actor.Type, entity.Get<SpatialState>().Position,
            target.Type, target.Entity.Get<SpatialState>().Position, out _, out _);
        var step = _capture!.Advance(new(actor, target, Tick, distance));
        switch (step)
        {
            case CaptureStep.Approach:
                if (!entity.Get<MotionState>().Moving || (Tick + entity.Get<EntityIdentity>().Id.Value) % 4 == 0)
                    SetTarget(index, AimPoint(index, targetIndex));
                order.OrderPhase = OrderPhase.Approaching;
                break;
            case CaptureStep.Engage:
                Halt(index);
                order.OrderPhase = OrderPhase.Engaging;
                break;
            case CaptureStep.Complete:
                Halt(index);
                order.OrderPhase = OrderPhase.Engaging;
                ChangeOwner(targetIndex, actor.Owner);
                _capture.Reset(entity);
                order.HasOrder = false;
                order.OrderPhase = OrderPhase.Idle;
                break;
            default:
                throw new InvalidOperationException("Capture returned an unknown step.");
        }
    }

    private CaptureProgress GetCaptureProgress(Entity entity, in OrderState order)
    {
        if (_capture is null || !order.HasOrder || order.ActiveOrder.Kind != UnitActionKind.Capture ||
            !_indices.TryGetValue(order.ActiveOrder.Target, out int target)) return default;
        return _capture.GetProgress(entity, TypeOf(_entityHandles[target]));
    }

    private void WriteCaptureState(BinaryWriter writer, Entity entity)
    {
        if (_capture is not null) _capture.WriteEntityState(writer, entity);
        else
        {
            writer.Write(false);
            writer.Write(false);
        }
    }
}
