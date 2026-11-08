using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Core.Construction;

internal interface IConstructionWorld
{
    long Tick { get; }
    int Width { get; }
    int Depth { get; }

    IEnumerable<Entity> Recipients(WorldCommand command);
    bool TryGetEntity(EntityId id, out Entity entity);
    UnitDefinition TypeOf(int type);
    UnitDefinition TypeOf(Entity entity);
    bool TryGetRecipe(Entity builder, string? action, out int target, [NotNullWhen(true)] out BuildDefinition? recipe);
    bool IsOnMap(Entity entity);
    GameplayConditions ReadConditions(Entity actor);

    bool CanQueueOrder(Entity actor);
    void IssueOrder(EntityId actor, UnitOrder order, bool queued);
    void Halt(EntityId actor);
    bool StopAllowed(int type, WorldPosition position);
    bool TrySetTarget(EntityId actor, WorldPosition position);
    bool TryEvacuate(EntityId actor, WorldPosition position, bool preserveOrder);

    WorldPosition GroundPosition(WorldPosition position);
    bool SiteTerrainAllows(UnitDefinition type, WorldPosition position);
    void QuerySite(WorldPosition from, WorldPosition to, List<EntityId> occupants);
    bool PrepareFootprint(UnitDefinition type, WorldPosition position, out NavigationGrid? candidate);
    void CommitFootprint(NavigationGrid candidate);

    EntityId ReserveEntityId();
    void ReleaseEntityId(EntityId id);
    bool CanAfford(uint owner, long cost);
    bool Spend(uint owner, long cost);
    Entity SpawnFoundation(EntityId id, int type, WorldPosition position, uint owner, int health, int quarterTurns, Action<Entity> initialize);
    void CompleteFoundation(Entity entity, Action<Entity> clearFoundation);
    void Emit(WorldEvent value);
}
