using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Transport;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    // Orders, spatial indexing and ordinary targeting share this presence check.
    private bool IsOnMap(Entity entity) => _transport?.IsStored(entity) != true;

    IEnumerable<Entity> ITransportWorld.Recipients(WorldCommand command) => Recipients(command).Select(index => _entityHandles[index]);
    bool ITransportWorld.TryGetEntity(EntityId id, out Entity entity) => TryGetEntity(id, out entity);
    Entity ITransportWorld.GetEntity(EntityId id) => _entityHandles[_indices[id]];
    UnitDefinition ITransportWorld.TypeOf(Entity entity) => TypeOf(entity);
    TransportDefinition? ITransportWorld.TransportOf(Entity entity) => TypeOf(entity).TransportFor(CompletedUpgrade(entity));
    bool ITransportWorld.IsUnderConstruction(Entity entity) => (ReadLocalConditions(entity) & GameplayConditions.Constructing) != 0;
    bool ITransportWorld.CanBoardTransport(Entity carrier) => !IsAircraft(carrier) || carrier.Get<FlightState>().Phase == FlightPhase.Landed;
    bool ITransportWorld.PrepareUnloadLanding(Entity carrier, WorldPosition destination)
    {
        if (!IsAircraft(carrier) || carrier.Get<FlightState>().Phase == FlightPhase.Landed) return true;
        var f = carrier.Get<FlightState>();
        f.LandRequested = true; f.LandingPoint = GroundPosition(destination, 0);
        carrier.Get<MotionState>().Moving = true;
        return false;
    }

    void ITransportWorld.IssueOrder(EntityId actor, UnitOrder order, bool queued) => IssueOrder(_indices[actor], order, queued);
    bool ITransportWorld.TrySetTarget(EntityId actor, WorldPosition position) => SetTarget(_indices[actor], position);
    void ITransportWorld.Halt(EntityId actor) => Halt(_indices[actor]);
    void ITransportWorld.ChangeOwner(EntityId actor, uint owner) => ChangeOwner(_indices[actor], owner);
    void ITransportWorld.RemoveEntity(EntityId actor) => DestroyIndex(_indices[actor], wreck: false);

    void ITransportWorld.EnsureMotionIndex() => EnsureMotionIndex();
    bool ITransportWorld.TryFindExitPlacement(int type, WorldPosition origin, int radius, out WorldPosition exit, UnitDefinition carrier) =>
        TryFindExitPlacement(type, origin, radius, out exit, carrier);

    void ITransportWorld.PassengerBoarded(EntityId actor)
    {
        CancelWindups(_indices[actor]);
        _motionIndexDirty = true;
    }

    void ITransportWorld.PlacePassenger(EntityId actor, WorldPosition exit)
    {
        int index = _indices[actor];
        var entity = _entityHandles[index];
        entity.Get<SpatialState>() = new(exit);
        entity.Get<NavigationAgentState>().TerrainStranded = false;
        Halt(index);
        (_motion ?? _combatIndex)?.Add(actor, exit, TypeOf(entity).Body);
        CancelWindups(index);
    }

    void ITransportWorld.Emit(WorldEvent value) => EmitEvent(value);
}
