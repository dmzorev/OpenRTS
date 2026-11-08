using Frent;

namespace OpenRTS.Core.Production;

internal interface IProductionWorld
{
    long Tick { get; }
    int Width { get; }
    int Depth { get; }
    bool TryGetEntity(EntityId id, out Entity entity);
    Entity GetEntity(EntityId id);
    bool IsConstructing(Entity actor);
    bool IsUpgrading(Entity actor);
    bool ProductEnabled(Entity actor, int product);
    GameplayConditions ReadConditions(UnitDefinition type, uint owner);
    void RebuildMotionIndex();
    bool TryFindExitPlacement(int type, WorldPosition origin, int radius, out WorldPosition exit);
    UnitDefinition TypeOf(Entity actor);
    int RotationOf(Entity actor);
    bool TryPlaceBuilding(int type, WorldPosition position, uint owner, int quarterTurns, out EntityId created);
    EntityId SpawnUnit(int type, WorldPosition position, uint owner);
    void PlayExitAnimation(EntityId actor, int animation, int duration);
    void IssueRallyOrder(EntityId actor, WorldPosition point);
    void Emit(WorldEvent value);
}
