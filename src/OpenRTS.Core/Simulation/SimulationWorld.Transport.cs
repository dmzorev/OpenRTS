using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private struct PassengerState { public EntityId Carrier; }

    private sealed class TransportState
    {
        public readonly List<EntityId> Occupants = [];
        public int UsedSlots;
        public bool ClaimedNeutral;
    }

    private const int BoardingRange = 512;
    private bool _transportIndexDirty;
    private int _storedCount;
    private static EntityId CarrierOf(Frent.Entity actor) => actor.Has<PassengerState>() ? actor.Get<PassengerState>().Carrier : default;
    private static bool IsStored(Frent.Entity actor) => CarrierOf(actor).Value != 0;
    private static bool HasPassengers(Frent.Entity actor) => actor.Has<TransportState>() && actor.Get<TransportState>().Occupants.Count != 0;
    private UnitDefinition TypeOf(Frent.Entity actor) => _types[actor.Get<UnitIdentity>().UnitType];

    private bool CanEnter(int index, EntityId id, out int target)
    {
        target = -1;
        var actor = _unitHandles[index];
        if (TypeOf(actor).Passenger is not { } passenger || HasPassengers(actor) ||
            !_indices.TryGetValue(id, out target) || index == target) return false;
        var carrier = _unitHandles[target];
        if (IsStored(carrier) || TypeOf(carrier).Transport is not { } transport ||
            !transport.Accepts.Contains(passenger.Type) || passenger.Slots > transport.Slots) return false;
        uint owner = actor.Get<OwnershipState>().Owner, host = carrier.Get<OwnershipState>().Owner;
        return owner != 0 && (owner == host || host == 0 && !TypeOf(carrier).Movable);
    }

    private void UpdateEnter(int index, UnitOrder order)
    {
        var actor = _unitHandles[index];
        var identity = actor.Get<UnitIdentity>();
        if (!CanEnter(index, order.Target, out int target))
        {
            FinishTransportOrder(index);
            _events.Add(new(Tick, WorldEventKind.InvalidOrder, identity.Id, TargetEntity: order.Target));
            return;
        }

        var carrier = _unitHandles[target];
        var carrierPosition = carrier.Get<SpatialState>().Position;
        var position = actor.Get<SpatialState>().Position;
        ref var orders = ref actor.Get<OrderState>();
        if (ObjectGeometry.DistanceSquared(TypeOf(actor), position, TypeOf(carrier), carrierPosition, out _, out _) > (long)BoardingRange * BoardingRange)
        {
            bool newGoal = orders.OrderPhase != OrderPhase.Approaching ||
                orders.ActiveOrder.Position != carrierPosition && (!actor.Get<MotionState>().Moving || (Tick + identity.Id.Value) % 8 == 0);
            if (newGoal)
            {
                // Aim at the nearest face; terrain resolution chooses a legal stop beside a footprint.
                var approach = ObjectGeometry.ClosestPoint(TypeOf(carrier), carrierPosition, position);
                orders.ActiveOrder = order with { Position = carrierPosition };
                if (!SetTarget(index, approach)) { FinishTransportOrder(index); return; }
                orders.OrderPhase = OrderPhase.Approaching;
            }
            if (!actor.Get<MotionState>().Moving) FinishTransportOrder(index);
            return;
        }

        FinishTransportOrder(index);
        var cargo = carrier.Get<TransportState>();
        int slots = TypeOf(actor).Passenger!.Slots;
        if (cargo.UsedSlots + slots > TypeOf(carrier).Transport!.Slots)
        {
            _events.Add(new(Tick, WorldEventKind.TransportFull, identity.Id, TargetEntity: order.Target));
            return;
        }

        uint owner = actor.Get<OwnershipState>().Owner;
        if (carrier.Get<OwnershipState>().Owner == 0)
        {
            ChangeOwner(target, owner);
            cargo.ClaimedNeutral = true;
        }
        orders.PendingOrders?.Clear();
        actor.Get<PassengerState>().Carrier = order.Target;
        cargo.Occupants.Add(identity.Id);
        _storedCount++;
        cargo.UsedSlots += slots;
        CancelWindups(index);
        _transportIndexDirty = true;
        _events.Add(new(Tick, WorldEventKind.EnteredTransport, identity.Id, carrierPosition, identity.UnitType, TargetEntity: order.Target, Owner: owner));
    }

    private void FinishTransportOrder(int index)
    {
        ref var orders = ref _unitHandles[index].Get<OrderState>();
        orders.HasOrder = false;
        orders.OrderPhase = OrderPhase.Idle;
        Halt(index);
    }

    private void UpdateUnload(int index, WorldPosition destination)
    {
        var carrier = _unitHandles[index];
        int distance = TypeOf(carrier).Transport!.UnloadDistance;
        if (!HasPassengers(carrier)) { FinishTransportOrder(index); return; }
        if (distance < 0 || DistanceSquared(carrier.Get<SpatialState>().Position, destination) <= (long)distance * distance)
        {
            FinishTransportOrder(index);
            Unload(index, default, destination);
        }
        else if (!carrier.Get<MotionState>().Moving)
        {
            FinishTransportOrder(index);
            _events.Add(new(Tick, WorldEventKind.NoRoute, carrier.Get<UnitIdentity>().Id, destination));
        }
    }

    private void Unload(int index, EntityId passenger, WorldPosition? destination = null)
    {
        var carrier = _unitHandles[index];
        if (IsStored(carrier) || !HasPassengers(carrier)) return;
        if (_transportIndexDirty) RebuildMotionIndex();
        var cargo = carrier.Get<TransportState>();
        var position = carrier.Get<SpatialState>().Position;
        var type = TypeOf(carrier);
        int radius = Math.Max(type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight) + 3072;
        for (int i = 0; i < cargo.Occupants.Count;)
        {
            var id = cargo.Occupants[i];
            if (passenger.Value != 0 && id != passenger) { i++; continue; }
            int actorIndex = _indices[id];
            var actor = _unitHandles[actorIndex];
            int unitType = actor.Get<UnitIdentity>().UnitType;
            if (!FindProductionExit(unitType, position, radius, out var exit, index))
            {
                _events.Add(new(Tick, WorldEventKind.UnloadBlocked, id, position, unitType, TargetEntity: carrier.Get<UnitIdentity>().Id));
                i++;
                continue;
            }
            DetachPassenger(actor);
            actor.Get<SpatialState>() = new(exit);
            actor.Get<NavigationAgentState>().TerrainStranded = false;
            Halt(actorIndex);
            (_motion ?? _combatIndex)?.Add(id, exit, TypeOf(actor).Body);
            CancelWindups(actorIndex);
            if (destination is { } goal) IssueOrder(actorIndex, new(UnitActionKind.Move, goal), false);
            _events.Add(new(Tick, WorldEventKind.Unloaded, id, exit, unitType, TargetEntity: carrier.Get<UnitIdentity>().Id, Owner: actor.Get<OwnershipState>().Owner));
        }
    }

    private void DetachPassenger(Frent.Entity actor)
    {
        var id = CarrierOf(actor);
        if (id.Value == 0) return;
        actor.Get<PassengerState>().Carrier = default;
        _storedCount--;
        int index = _indices[id];
        var cargo = _unitHandles[index].Get<TransportState>();
        cargo.Occupants.Remove(actor.Get<UnitIdentity>().Id);
        cargo.UsedSlots -= TypeOf(actor).Passenger!.Slots;
        if (cargo.Occupants.Count == 0 && cargo.ClaimedNeutral)
        {
            cargo.ClaimedNeutral = false;
            ChangeOwner(index, 0);
        }
    }

    private void RemovePassengers(Frent.Entity carrier)
    {
        if (!HasPassengers(carrier)) return;
        var cargo = carrier.Get<TransportState>();
        cargo.ClaimedNeutral = false;
        while (cargo.Occupants.Count > 0) DestroyIndex(_indices[cargo.Occupants[^1]], wreck: false);
    }

    private static void WriteTransport(BinaryWriter writer, Frent.Entity actor)
    {
        writer.Write(CarrierOf(actor).Value);
        if (!actor.Has<TransportState>()) return;
        var cargo = actor.Get<TransportState>();
        writer.Write(cargo.ClaimedNeutral);
        writer.Write(cargo.Occupants.Count);
        foreach (var id in cargo.Occupants) writer.Write(id.Value);
    }
}
