using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Core.Transport;

internal sealed class TransportSystem(ITransportWorld world, ITransportMechanic mechanic)
{
    private readonly ITransportWorld _world = world;
    private readonly ITransportMechanic _mechanic = mechanic;
    private int _storedCount;
    internal bool HasStoredActors => _storedCount != 0;
    internal EntityId CarrierOf(Entity actor) => HasStoredActors ? _mechanic.CarrierOf(actor) : default;
    internal bool IsStored(Entity actor) => CarrierOf(actor).Value != 0;
    internal bool HasPassengers(Entity actor) => !Passengers(actor).IsEmpty;
    internal ReadOnlySpan<EntityId> Passengers(Entity actor) => HasStoredActors ? _mechanic.Passengers(actor) : [];
    internal bool AllowsPassengerFire(Entity carrier) => _world.TransportOf(carrier) is { FireFromInside: true };
    internal ReadOnlySpan<EntityId> FiringPassengers(Entity carrier) => HasStoredActors && AllowsPassengerFire(carrier) ? Passengers(carrier) : [];

    private TransportParticipant ParticipantOf(Entity entity)
        => new(entity, entity.Get<EntityIdentity>().Id, _world.TypeOf(entity), entity.Get<OwnershipState>().Owner, _world.TransportOf(entity));

    internal void Initialize(Entity actor, UnitDefinition type) => _mechanic.Initialize(actor, type);

    internal static void Apply(TransportSystem? system, ITransportWorld world, WorldCommand command)
    {
        var recipients = world.Recipients(command);
        if (command.Kind == CommandKind.Unload)
        {
            if (system is not null)
                foreach (var carrier in recipients) system.Unload(carrier, command.TargetEntity);
            return;
        }

        var action = command.Kind == CommandKind.Enter ? UnitActionKind.Enter : UnitActionKind.UnloadAt;
        foreach (var actor in recipients)
            world.IssueOrder(actor.Get<EntityIdentity>().Id, new(action, command.Position, command.TargetEntity), command.Queued);
    }

    internal bool CanStartOrder(Entity actor, UnitOrder order) => order.Kind switch
    {
        UnitActionKind.Enter => CanEnter(actor, order.Target, out _),
        UnitActionKind.UnloadAt => HasPassengers(actor),
        _ => true
    };

    internal bool PrepareUnloadOrder(Entity carrier, WorldPosition destination)
    {
        var id = carrier.Get<EntityIdentity>().Id;
        int distance = _world.TypeOf(carrier).Transport!.UnloadDistance;
        if (distance < 0) { _world.Halt(id); return true; }
        return ObjectGeometry.Squared(carrier.Get<SpatialState>().Position, destination) <= (long)distance * distance ||
            _world.TrySetTarget(id, destination);
    }

    private bool CanEnter(Entity actor, EntityId id, out Entity carrier)
    {
        carrier = default;
        if (_world.TypeOf(actor).Passenger is null || HasPassengers(actor) ||
            !_world.TryGetEntity(id, out carrier) || actor.Get<EntityIdentity>().Id == id) return false;
        if (IsStored(carrier) || _world.IsUnderConstruction(carrier) || !_world.CanBoardTransport(carrier) || _world.TypeOf(carrier).Transport is null) return false;
        var passenger = ParticipantOf(actor);
        var host = ParticipantOf(carrier);
        return passenger.Owner != 0 && (passenger.Owner == host.Owner || host.Owner == 0 && !host.Type.Movable) &&
            _mechanic.Accepts(passenger, host);
    }

    internal void UpdateEnter(Entity actor, UnitOrder order)
    {
        var identity = actor.Get<EntityIdentity>();
        if (!CanEnter(actor, order.Target, out var carrier))
        {
            FinishTransportOrder(actor);
            _world.Emit(new(_world.Tick, WorldEventKind.InvalidOrder, identity.Id, TargetEntity: order.Target));
            return;
        }

        var carrierPosition = carrier.Get<SpatialState>().Position;
        var position = actor.Get<SpatialState>().Position;
        var passenger = ParticipantOf(actor);
        var host = ParticipantOf(carrier);
        int range = _mechanic.BoardingRange(passenger, host);
        if (range < 0) throw new InvalidOperationException("Transport boarding range cannot be negative.");
        ref var orders = ref actor.Get<OrderState>();
        if (ObjectGeometry.DistanceSquared(passenger.Type, position, host.Type, carrierPosition, out _, out _) > (long)range * range)
        {
            bool newGoal = orders.OrderPhase != OrderPhase.Approaching ||
                orders.ActiveOrder.Position != carrierPosition && (!actor.Get<MotionState>().Moving || (_world.Tick + identity.Id.Value) % 8 == 0);
            if (newGoal)
            {
                // Aim at the nearest face; terrain resolution chooses a legal stop beside a footprint.
                var approach = ObjectGeometry.ClosestPoint(host.Type, carrierPosition, position);
                orders.ActiveOrder = order with { Position = carrierPosition };
                if (!_world.TrySetTarget(identity.Id, approach)) { FinishTransportOrder(actor); return; }
                orders.OrderPhase = OrderPhase.Approaching;
            }
            if (!actor.Get<MotionState>().Moving) FinishTransportOrder(actor);
            return;
        }

        FinishTransportOrder(actor);
        if (!_mechanic.HasRoom(passenger, host))
        {
            _world.Emit(new(_world.Tick, WorldEventKind.TransportFull, identity.Id, TargetEntity: order.Target));
            return;
        }

        bool claimedNeutral = host.Owner == 0;
        if (claimedNeutral) _world.ChangeOwner(host.Id, passenger.Owner);
        orders.PendingOrders?.Clear();
        _mechanic.Board(passenger, host, claimedNeutral);
        _storedCount++;
        _world.PassengerBoarded(identity.Id);
        _world.Emit(new(_world.Tick, WorldEventKind.EnteredTransport, identity.Id, carrierPosition, identity.Type, TargetEntity: order.Target, Owner: passenger.Owner));
    }

    private void FinishTransportOrder(Entity actor)
    {
        ref var orders = ref actor.Get<OrderState>();
        orders.HasOrder = false;
        orders.OrderPhase = OrderPhase.Idle;
        _world.Halt(actor.Get<EntityIdentity>().Id);
    }

    internal void UpdateUnload(Entity carrier, WorldPosition destination)
    {
        int distance = _world.TypeOf(carrier).Transport!.UnloadDistance;
        if (!HasPassengers(carrier)) { FinishTransportOrder(carrier); return; }
        if (!_world.PrepareUnloadLanding(carrier, destination)) return;
        if (distance < 0 || ObjectGeometry.Squared(carrier.Get<SpatialState>().Position, destination) <= (long)distance * distance)
        {
            FinishTransportOrder(carrier);
            Unload(carrier, default, destination);
        }
        else if (!carrier.Get<MotionState>().Moving)
        {
            FinishTransportOrder(carrier);
            _world.Emit(new(_world.Tick, WorldEventKind.NoRoute, carrier.Get<EntityIdentity>().Id, destination));
        }
    }

    private void Unload(Entity carrier, EntityId passenger, WorldPosition? destination = null)
    {
        if (IsStored(carrier) || !HasPassengers(carrier)) return;
        if (!_world.CanBoardTransport(carrier))
        {
            _world.IssueOrder(carrier.Get<EntityIdentity>().Id, new(UnitActionKind.UnloadAt, destination ?? carrier.Get<SpatialState>().Position), false);
            return;
        }
        _world.EnsureMotionIndex();
        var position = carrier.Get<SpatialState>().Position;
        var type = _world.TypeOf(carrier);
        int radius = Math.Max(type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight) + 3072;
        for (int i = 0; ;)
        {
            var occupants = Passengers(carrier);
            if (i >= occupants.Length) break;
            var id = occupants[i];
            if (passenger.Value != 0 && id != passenger) { i++; continue; }
            var actor = _world.GetEntity(id);
            int unitType = actor.Get<EntityIdentity>().Type;
            if (!_world.TryFindExitPlacement(unitType, position, radius, out var exit, type))
            {
                _world.Emit(new(_world.Tick, WorldEventKind.UnloadBlocked, id, position, unitType, TargetEntity: carrier.Get<EntityIdentity>().Id));
                i++;
                continue;
            }
            DetachPassenger(actor);
            _world.PlacePassenger(id, exit);
            if (destination is { } goal) _world.IssueOrder(id, new(UnitActionKind.Move, goal), false);
            _world.Emit(new(_world.Tick, WorldEventKind.Unloaded, id, exit, unitType, TargetEntity: carrier.Get<EntityIdentity>().Id, Owner: actor.Get<OwnershipState>().Owner));
        }
    }

    private void DetachPassenger(Entity actor)
    {
        var id = CarrierOf(actor);
        if (id.Value == 0) return;
        bool returnNeutral = _mechanic.Detach(ParticipantOf(actor), ParticipantOf(_world.GetEntity(id)));
        _storedCount--;
        if (returnNeutral) _world.ChangeOwner(id, 0);
    }

    internal void RemovePassengers(Entity carrier)
    {
        if (!HasPassengers(carrier)) return;
        _mechanic.SuppressNeutralReturn(carrier);
        var occupants = Passengers(carrier);
        while (!occupants.IsEmpty)
        {
            _world.RemoveEntity(occupants[^1]);
            occupants = Passengers(carrier);
        }
    }

    internal bool BeforeRemove(Entity entity, bool wreck)
    {
        if (IsStored(entity)) { DetachPassenger(entity); wreck = false; }
        RemovePassengers(entity);
        return wreck;
    }

    internal static EntityId Project(TransportSystem? system, Entity entity, UnitDefinition type, ref uint visualStates, out SpatialState spatial)
    {
        if (type.Transport is not null && type.States.Contains("garrisoned"))
        {
            uint mask = type.StateMask("garrisoned");
            visualStates = system?.HasPassengers(entity) == true ? visualStates | mask : visualStates & ~mask;
        }

        var carrier = system?.CarrierOf(entity) ?? default;
        spatial = (carrier.Value != 0 ? system!._world.GetEntity(carrier) : entity).Get<SpatialState>();
        return carrier;
    }

    internal static void WriteEntityState(TransportSystem? system, BinaryWriter writer, Entity actor)
    {
        writer.Write(system?.CarrierOf(actor).Value ?? 0u);
        system?._mechanic.WriteEntityState(writer, actor);
    }

    internal void WriteState(BinaryWriter writer) => _mechanic.WriteState(writer);
}
