using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private bool CanCapture(int index, EntityId id, out int target)
    {
        target = -1; var actor = _entityHandles[index];
        return actor.Get<OwnershipState>().Owner != 0 && actor.Has<CaptureState>() &&
            _indices.TryGetValue(id, out target) && !HasPassengers(_entityHandles[target]) && _types[_entityHandles[target].Get<EntityIdentity>().Type].Capturable is not null &&
            _entityHandles[target].Get<OwnershipState>().Owner != actor.Get<OwnershipState>().Owner;
    }
    private void UpdateCapture(int index)
    {
        var actor = _entityHandles[index]; ref var order = ref actor.Get<OrderState>();
        ref var state = ref actor.Get<CaptureState>();
        if (!CanCapture(index, order.ActiveOrder.Target, out int target))
        { state.Progress = 0; order.HasOrder = false; Halt(index); return; }
        var other = _entityHandles[target]; var otherType = _types[other.Get<EntityIdentity>().Type];
        uint owner = other.Get<OwnershipState>().Owner;
        if (state.Owner != owner) { state.Progress = 0; state.Owner = owner; }
        var skill = _types[actor.Get<EntityIdentity>().Type].Abilities.First(a => a.Kind == AbilityKind.Capture);
        long distance = ObjectGeometry.DistanceSquared(_types[actor.Get<EntityIdentity>().Type], actor.Get<SpatialState>().Position,
            otherType, other.Get<SpatialState>().Position, out _, out _);
        if (distance > (long)skill.Range * skill.Range)
        {
            state.Progress = 0;
            if (!actor.Get<MotionState>().Moving || (Tick + actor.Get<EntityIdentity>().Id.Value) % 4 == 0) SetTarget(index, AimPoint(index, target));
            order.OrderPhase = OrderPhase.Approaching; return;
        }
        Halt(index); order.OrderPhase = OrderPhase.Engaging;
        if (other.Get<CapturableState>().ChangedTick == Tick) { state.Progress = 0; return; }
        if (++state.Progress < (otherType.Capturable!.Ticks ?? otherType.MaxHealth)) return;
        ChangeOwner(target, actor.Get<OwnershipState>().Owner);
        state.Progress = 0; order.HasOrder = false; order.OrderPhase = OrderPhase.Idle;
    }
}
