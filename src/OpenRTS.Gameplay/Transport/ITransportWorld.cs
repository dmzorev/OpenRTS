using Frent;
using OpenRTS.Core.Orders;

namespace OpenRTS.Core.Transport;

internal interface ITransportWorld
{
    long Tick { get; }
    IEnumerable<Entity> Recipients(WorldCommand command);
    bool TryGetEntity(EntityId id, out Entity entity);
    Entity GetEntity(EntityId id);
    UnitDefinition TypeOf(Entity entity);
    bool IsUnderConstruction(Entity entity);

    void IssueOrder(EntityId actor, UnitOrder order, bool queued);
    bool TrySetTarget(EntityId actor, WorldPosition position);
    void Halt(EntityId actor);
    void ChangeOwner(EntityId actor, uint owner);
    void RemoveEntity(EntityId actor);

    void EnsureMotionIndex();
    bool TryFindExitPlacement(int type, WorldPosition origin, int radius, out WorldPosition exit, UnitDefinition carrier);
    void PassengerBoarded(EntityId actor);
    void PlacePassenger(EntityId actor, WorldPosition exit);
    void Emit(WorldEvent value);
}
