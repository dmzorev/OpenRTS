using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Transport;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly ITransportMechanic? _transport;
    private int _storedCount;
    private bool HasStoredActors => _storedCount != 0;
    private EntityId CarrierOf(Entity actor) => HasStoredActors ? _transport!.CarrierOf(actor) : default;
    private bool IsStored(Entity actor) => CarrierOf(actor).Value != 0;
    private bool HasPassengers(Entity actor) => !Passengers(actor).IsEmpty;
    private ReadOnlySpan<EntityId> Passengers(Entity actor) => HasStoredActors ? _transport!.Passengers(actor) : [];
    private static bool AllowsPassengerFire(UnitDefinition type) => type.Transport is { FireFromInside: true };
    private ReadOnlySpan<EntityId> FiringPassengers(Entity carrier) => HasStoredActors && AllowsPassengerFire(TypeOf(carrier)) ? Passengers(carrier) : [];

    private TransportParticipant TransportParticipantOf(Entity entity)
        => new(entity, entity.Get<EntityIdentity>().Id, TypeOf(entity), entity.Get<OwnershipState>().Owner);

    private void ApplyTransport(WorldCommand command)
    {
        if (command.Kind == CommandKind.Unload)
        {
            foreach (int member in Recipients(command)) Unload(member, command.TargetEntity);
            return;
        }

        var action = command.Kind == CommandKind.Enter ? UnitActionKind.Enter : UnitActionKind.UnloadAt;
        foreach (int member in Recipients(command))
            IssueOrder(member, new(action, command.Position, command.TargetEntity), command.Queued);
    }

    private bool CanStartTransportOrder(int index, UnitOrder order) => order.Kind switch
    {
        UnitActionKind.Enter => CanEnter(index, order.Target, out _),
        UnitActionKind.UnloadAt => HasPassengers(_entityHandles[index]),
        _ => true
    };

    private bool PrepareUnloadOrder(int index, WorldPosition destination)
    {
        var carrier = _entityHandles[index];
        int distance = TypeOf(carrier).Transport!.UnloadDistance;
        if (distance < 0) { Halt(index); return true; }
        return DistanceSquared(carrier.Get<SpatialState>().Position, destination) <= (long)distance * distance || SetTarget(index, destination);
    }

    private bool CanEnter(int index, EntityId id, out int target)
    {
        target = -1;
        var actor = _entityHandles[index];
        if (_transport is null || TypeOf(actor).Passenger is null || HasPassengers(actor) ||
            !_indices.TryGetValue(id, out target) || index == target) return false;
        var carrier = _entityHandles[target];
        if (IsStored(carrier) || (ReadLocalConditions(carrier) & GameplayConditions.Constructing) != 0 || TypeOf(carrier).Transport is null) return false;
        var passenger = TransportParticipantOf(actor);
        var host = TransportParticipantOf(carrier);
        return passenger.Owner != 0 && (passenger.Owner == host.Owner || host.Owner == 0 && !host.Type.Movable) &&
            _transport.Accepts(passenger, host);
    }

    private void UpdateEnter(int index, UnitOrder order)
    {
        var actor = _entityHandles[index];
        var identity = actor.Get<EntityIdentity>();
        if (!CanEnter(index, order.Target, out int target))
        {
            FinishTransportOrder(index);
            _events.Add(new(Tick, WorldEventKind.InvalidOrder, identity.Id, TargetEntity: order.Target));
            return;
        }

        var carrier = _entityHandles[target];
        var carrierPosition = carrier.Get<SpatialState>().Position;
        var position = actor.Get<SpatialState>().Position;
        var passenger = TransportParticipantOf(actor);
        var host = TransportParticipantOf(carrier);
        int range = _transport!.BoardingRange(passenger, host);
        if (range < 0) throw new InvalidOperationException("Transport boarding range cannot be negative.");
        ref var orders = ref actor.Get<OrderState>();
        if (ObjectGeometry.DistanceSquared(passenger.Type, position, host.Type, carrierPosition, out _, out _) > (long)range * range)
        {
            bool newGoal = orders.OrderPhase != OrderPhase.Approaching ||
                orders.ActiveOrder.Position != carrierPosition && (!actor.Get<MotionState>().Moving || (Tick + identity.Id.Value) % 8 == 0);
            if (newGoal)
            {
                // Aim at the nearest face; terrain resolution chooses a legal stop beside a footprint.
                var approach = ObjectGeometry.ClosestPoint(host.Type, carrierPosition, position);
                orders.ActiveOrder = order with { Position = carrierPosition };
                if (!SetTarget(index, approach)) { FinishTransportOrder(index); return; }
                orders.OrderPhase = OrderPhase.Approaching;
            }
            if (!actor.Get<MotionState>().Moving) FinishTransportOrder(index);
            return;
        }

        FinishTransportOrder(index);
        if (!_transport.HasRoom(passenger, host))
        {
            _events.Add(new(Tick, WorldEventKind.TransportFull, identity.Id, TargetEntity: order.Target));
            return;
        }

        bool claimedNeutral = host.Owner == 0;
        if (claimedNeutral) ChangeOwner(target, passenger.Owner);
        orders.PendingOrders?.Clear();
        _transport.Board(passenger, host, claimedNeutral);
        _storedCount++;
        CancelWindups(index);
        _motionIndexDirty = true;
        _events.Add(new(Tick, WorldEventKind.EnteredTransport, identity.Id, carrierPosition, identity.Type, TargetEntity: order.Target, Owner: passenger.Owner));
    }

    private void FinishTransportOrder(int index)
    {
        ref var orders = ref _entityHandles[index].Get<OrderState>();
        orders.HasOrder = false;
        orders.OrderPhase = OrderPhase.Idle;
        Halt(index);
    }

    private void UpdateUnload(int index, WorldPosition destination)
    {
        var carrier = _entityHandles[index];
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
            _events.Add(new(Tick, WorldEventKind.NoRoute, carrier.Get<EntityIdentity>().Id, destination));
        }
    }

    private void Unload(int index, EntityId passenger, WorldPosition? destination = null)
    {
        var carrier = _entityHandles[index];
        if (IsStored(carrier) || !HasPassengers(carrier)) return;
        EnsureMotionIndex();
        var position = carrier.Get<SpatialState>().Position;
        var type = TypeOf(carrier);
        int radius = Math.Max(type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight) + 3072;
        for (int i = 0; ;)
        {
            var occupants = Passengers(carrier);
            if (i >= occupants.Length) break;
            var id = occupants[i];
            if (passenger.Value != 0 && id != passenger) { i++; continue; }
            int actorIndex = _indices[id];
            var actor = _entityHandles[actorIndex];
            int unitType = actor.Get<EntityIdentity>().Type;
            if (!TryFindExitPlacement(unitType, position, radius, out var exit, type))
            {
                _events.Add(new(Tick, WorldEventKind.UnloadBlocked, id, position, unitType, TargetEntity: carrier.Get<EntityIdentity>().Id));
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
            _events.Add(new(Tick, WorldEventKind.Unloaded, id, exit, unitType, TargetEntity: carrier.Get<EntityIdentity>().Id, Owner: actor.Get<OwnershipState>().Owner));
        }
    }

    private void DetachPassenger(Entity actor)
    {
        var id = CarrierOf(actor);
        if (id.Value == 0) return;
        int index = _indices[id];
        bool returnNeutral = _transport!.Detach(TransportParticipantOf(actor), TransportParticipantOf(_entityHandles[index]));
        _storedCount--;
        if (returnNeutral) ChangeOwner(index, 0);
    }

    private void RemovePassengers(Entity carrier)
    {
        if (!HasPassengers(carrier)) return;
        _transport!.SuppressNeutralReturn(carrier);
        var occupants = Passengers(carrier);
        while (!occupants.IsEmpty)
        {
            DestroyIndex(_indices[occupants[^1]], wreck: false);
            occupants = Passengers(carrier);
        }
    }

    private bool RemoveFromTransport(Frent.Entity entity, bool wreck)
    {
        if (IsStored(entity)) { DetachPassenger(entity); wreck = false; }
        RemovePassengers(entity);
        return wreck;
    }

    private EntityId ProjectTransport(Frent.Entity entity, UnitDefinition type, ref uint visualStates, out SpatialState spatial)
    {
        if (type.Transport is not null && type.States.Contains("garrisoned"))
        {
            uint mask = type.StateMask("garrisoned");
            visualStates = HasPassengers(entity) ? visualStates | mask : visualStates & ~mask;
        }

        var carrier = CarrierOf(entity);
        spatial = (carrier.Value != 0 ? _entityHandles[_indices[carrier]] : entity).Get<SpatialState>();
        return carrier;
    }

    private void WriteTransport(BinaryWriter writer, Entity actor)
    {
        writer.Write(CarrierOf(actor).Value);
        _transport?.WriteEntityState(writer, actor);
    }
}
