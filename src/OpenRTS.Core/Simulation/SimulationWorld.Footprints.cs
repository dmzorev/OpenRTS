using OpenRTS.Core.Simulation;
namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private NavigationGrid? _baseGrid;
    // Derived index of immobile stamps; packed entity storage never defines their priority.
    private readonly SortedSet<EntityId> _footprints = new(Comparer<EntityId>.Create((a, b) => a.Value.CompareTo(b.Value)));
    private readonly Dictionary<int, CellOverride> _cellOverrides = new();

    private bool FootprintFits(FootprintDefinition footprint, WorldPosition position)
    {
        int left = position.X - footprint.HalfWidth, top = position.Y - footprint.HalfHeight;
        if (left < 0 || top < 0 || position.X + footprint.HalfWidth > Width || position.Y + footprint.HalfHeight > Depth) return false;
        return _baseGrid is null || left % _baseGrid.CellSize == 0 && top % _baseGrid.CellSize == 0 && footprint.CellSize % _baseGrid.CellSize == 0;
    }
    private NavigationGrid ComposeFootprints(NavigationGrid basis, FootprintDefinition? extra = null, WorldPosition position = default)
    {
        _cellOverrides.Clear();
        foreach (var id in _footprints)
        {
            ref var entity = ref _unitHandles[_indices[id]];
            var entityIdentity = entity.Get<UnitIdentity>();
            Stamp(_types[entityIdentity.UnitType].Footprint!, entity.Get<SpatialState>().Position);
        }
        if (extra is not null) Stamp(extra, position);
        return basis.Compose(_cellOverrides, _navigation!.Grid);
        void Stamp(FootprintDefinition footprint, WorldPosition center)
        {
            foreach (var cell in footprint.Cells)
            {
                var box = footprint.Bounds(cell, center); var next = cell.Navigation;
                for (int y = box.MinY / basis.CellSize; y < box.MaxY / basis.CellSize; y++)
                    for (int x = box.MinX / basis.CellSize; x < box.MaxX / basis.CellSize; x++)
                    {
                        int index = y * basis.Width + x; _cellOverrides.TryGetValue(index, out var lower);
                        _cellOverrides[index] = new(next.Traverse ?? lower.Traverse, next.CanStop ?? lower.CanStop, next.Terrain ?? lower.Terrain);
                    }
            }
        }
    }
    private bool SafeGrid(NavigationGrid candidate, out EntityId occupant)
    {
        occupant = default;
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _unitHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityMotion = ref entity.Get<MotionState>();
            var type = _types[entityIdentity.UnitType]; if (type.Footprint is not null) continue;
            bool clear = TerrainGeometry.Clear(candidate, entitySpatial.Position, entitySpatial.Position, type.Body, type.Locomotor);
            bool newNoStop = !TerrainGeometry.CanStop(candidate, entitySpatial.Position, type.Body, type.Locomotor) &&
                TerrainGeometry.CanStop(_navigation!.Grid, entitySpatial.Position, type.Body, type.Locomotor) && !entityMotion.Moving;
            if ((!clear || newNoStop) && (occupant.Value == 0 || entityIdentity.Id.Value < occupant.Value)) occupant = entityIdentity.Id;
        }
        return occupant.Value == 0;
    }
    private bool PrepareFootprint(int unitType, WorldPosition position, out NavigationGrid? candidate)
    {
        candidate = null; var footprint = _types[unitType].Footprint;
        if (footprint is null) return true;
        if (!FootprintFits(footprint, position)) return false;
        if (_baseGrid is null) return true;
        candidate = ComposeFootprints(_baseGrid, footprint, position);
        return SafeGrid(candidate, out _);
    }
    private void CommitGrid(NavigationGrid candidate)
    {
        if (ReferenceEquals(candidate, _navigation!.Grid)) return;
        _navigation.SetGrid(candidate); _weighted?.SetGrid(candidate); _plainResolver = null; _routeTree = null; _weightedTrees.Clear(); _bulkAllowed = false;
        // Local paths and exact destinations are also invalidated, not only shared global fields.
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _unitHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityOrders = ref entity.Get<OrderState>();
            entityNavigation.PathLength = entityNavigation.PathCursor = 0;
            entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0;
            var type = _types[entityIdentity.UnitType]; if (type.Footprint is not null) continue;
            bool wasStranded = entityNavigation.TerrainStranded;
            entityNavigation.TerrainStranded = !TerrainGeometry.Clear(candidate, entitySpatial.Position, entitySpatial.Position, type.Body, type.Locomotor);
            if (entityNavigation.TerrainStranded)
            {
                entityMotion.Moving = false; entityOrders.OrderPhase = OrderPhase.Waiting;
                if (!wasStranded) _events.Add(new(Tick, WorldEventKind.TerrainStranded, entityIdentity.Id, entitySpatial.Position, entityIdentity.UnitType));
            }
            else if ((wasStranded || entityMotion.Moving) && type.Movable)
            {
                var goal = entityMotion.Target;
                if (!StopAllowed(entityIdentity.UnitType, goal)) SetTarget(i, goal);
                else entityMotion.Moving = entitySpatial.Position != goal;
            }
        }
    }
    private void SetBaseBlocked(int cell, bool blocked)
    {
        var candidateBase = _baseGrid!.WithBlocked(cell, blocked);
        if (ReferenceEquals(candidateBase, _baseGrid)) return;
        var candidate = ComposeFootprints(candidateBase);
        if (!SafeGrid(candidate, out var occupant))
        { _events.Add(new(Tick, WorldEventKind.CellOccupied, occupant, _navigation!.Grid.Center(cell))); return; }
        _baseGrid = candidateBase; CommitGrid(candidate);
    }
}
