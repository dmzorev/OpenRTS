using Frent;
using OpenRTS.Core.Production;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    public int TaskCount
    {
        get
        {
            ThrowIfDisposed();
            return _production.TaskCount(ExistingStorage);
        }
    }

    public void CopyTaskPoses(Span<TaskPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        _production.CopyTaskPoses(destination, ExistingStorage);
    }

    bool IProductionWorld.TryGetEntity(EntityId id, out Entity entity) => TryGetEntity(id, out entity);
    Entity IProductionWorld.GetEntity(EntityId id) => _entityHandles[_indices[id]];
    bool IProductionWorld.IsConstructing(Entity actor) => IsConstructing(actor);
    GameplayConditions IProductionWorld.ReadConditions(UnitDefinition type, uint owner) => ReadConditions(type, owner);
    void IProductionWorld.RebuildMotionIndex() => RebuildMotionIndex();

    bool IProductionWorld.TryFindExitPlacement(int type, WorldPosition origin, int radius, out WorldPosition exit) =>
        TryFindExitPlacement(type, origin, radius, out exit);

    bool IProductionWorld.TryPlaceBuilding(int type, WorldPosition position, uint owner, out EntityId created)
    {
        created = default;
        if (!PrepareFootprint(type, position, out var grid)) return false;
        created = Spawn(type, position, owner);
        if (grid is not null) CommitGrid(grid);
        return true;
    }

    EntityId IProductionWorld.SpawnUnit(int type, WorldPosition position, uint owner) => Spawn(type, position, owner);

    void IProductionWorld.PlayExitAnimation(EntityId actor, int animation, int duration) =>
        ApplyPresentation(_indices[actor], new(Tick, 0, 1, CommandKind.PlayAnimation, actor,
            Animation: animation, DurationTicks: duration, Track: AnimationTrack.Body));

    void IProductionWorld.IssueRallyOrder(EntityId actor, WorldPosition point) =>
        IssueOrder(_indices[actor], new(UnitActionKind.Move, point), false);

    void IProductionWorld.Emit(WorldEvent value) => _events.Add(value);
}
