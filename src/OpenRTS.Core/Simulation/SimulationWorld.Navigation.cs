using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int LocalSearchBudget = 64;
    private readonly LocalMotionSearch _localSearch = new();
    private Span<WorldPosition> Path(int index) => _entities[index].Navigation.LocalPath.AsSpan();
    public int NavigationLocalSearches { get; private set; }
    public int NavigationYields { get; private set; }

    private void UpdateNavigation(CancellationToken token)
    {
        var grid = _navigation!.Grid;
        if (_navigationGoals.Length < Count) Array.Resize(ref _navigationGoals, _entities.Length);
        EnsureMovementOrder();
        if (_handled.Length < _entities.Length)
        { Array.Resize(ref _handled, _entities.Length); Array.Resize(ref _cohort, _entities.Length); }
        Array.Clear(_handled, 0, Count);
        NavigationWaitingCount = NavigationLocalSearches = NavigationYields = NavigationGoalExchanges = NavigationQueueWaitingCount = 0;
        GroupRouteBuilds = GroupRouteVisitedCells = 0; GroupRouteBuildMilliseconds = 0; GroupTranslating = false;
        TryBulkStep();
        if (_weighted is not null) PrepareWeightedRoutes(token);
        else { PrepareGroupRoutes(token); PreparePointRoutes(token); }
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int index = _movementOrder[(order + (int)(Tick % Count)) % Count];
            ref var entity = ref _entities[index];
            ref var entityMotion = ref entity.Motion;
            ref var entityNavigation = ref entity.Navigation;
            if (!entityMotion.Moving || _handled[index]) continue;
            WorldPosition waypoint;
            if (entityNavigation.YieldFor.Value != 0)
            {
                if (Tick >= entityNavigation.YieldDeadline)
                {
                    entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0;
                    entityNavigation.PathLength = entityNavigation.PathCursor = 0; _groupRoutes.Remove(entity.Id);
                    entityMotion.Moving = entity.Position != entityMotion.Target;
                    continue;
                }
                bool waiting = _indices.TryGetValue(entityNavigation.YieldFor, out int requester) && _entities[requester].Motion.Moving &&
                    DistanceSquared(entity.Position, _entities[requester].Position) < 1536L * 1536 && Tick < entityNavigation.YieldDeadline;
                if (entity.Position == entityNavigation.LocalTarget)
                {
                    if (waiting) { NavigationWaitingCount++; continue; }
                    entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; _groupRoutes.Remove(entity.Id);
                    entityMotion.Moving = entity.Position != entityMotion.Target;
                    continue;
                }
                waypoint = entityNavigation.PathCursor < entityNavigation.PathLength ? Path(index)[entityNavigation.PathCursor] : entityNavigation.LocalTarget;
            }
            else
            {
                if (entity.Position == entityMotion.Target) { entityMotion.Moving = false; entityNavigation.WaitTicks = 0; continue; }
                if (!TryWaypoint(index, out waypoint)) { NavigationWaitingCount++; continue; }
                if (entityNavigation.PathCursor < entityNavigation.PathLength) waypoint = Path(index)[entityNavigation.PathCursor];
            }
            waypoint = GroundPosition(waypoint, entity.Position.Elevation);
            var next = TerrainStep(index, waypoint);
            if (next == entity.Position) continue;
            bool terrain = ClearTerrain(index, entity.Position, next);
            int blocker = -1;
            if (terrain && _motion!.ClearMove(index, next, out blocker))
            {
                MoveTo(index, next); entityNavigation.WaitTicks = 0;
                if (next == waypoint && entityNavigation.PathCursor < entityNavigation.PathLength) entityNavigation.PathCursor++;
                if (entityNavigation.PathCursor == entityNavigation.PathLength && entityNavigation.PathLength > 0 && _groupRoutes.TryGetValue(entity.Id, out var route))
                { entityNavigation.PathLength = entityNavigation.PathCursor = 0; route.HasWaypoint = false; }
                continue;
            }
            entityNavigation.WaitTicks = Math.Min(1024, entityNavigation.WaitTicks + 1);
            if (entityNavigation.YieldFor.Value == 0 && entityNavigation.WaitTicks >= 4 && entityNavigation.PathCursor == entityNavigation.PathLength &&
                _groupRoutes.TryGetValue(entity.Id, out var blockedRoute))
            {
                int oldCursor = blockedRoute.Cursor; blockedRoute.HasWaypoint = false;
                if (TryWaypoint(index, out _) && blockedRoute.Cursor > oldCursor) { NavigationWaitingCount++; continue; }
            }
            if (blocker >= 0 && TryExchangeGoals(index, blocker)) { NavigationWaitingCount++; continue; }
            if (blocker >= 0 && IsFollowing(index, blocker))
            {
                NavigationWaitingCount++; NavigationQueueWaitingCount++;
                // A small gap is enough to continue naturally when the front actor advances next tick.
                continue;
            }
            if (entityNavigation.PathCursor < entityNavigation.PathLength)
            {
                if (entityNavigation.WaitTicks >= 8) entityNavigation.PathLength = entityNavigation.PathCursor = 0;
            }
            else if (entityNavigation.WaitTicks >= 4 && (Tick + entity.Id.Value) % 8 == 0 && NavigationLocalSearches < LocalSearchBudget)
            {
                NavigationLocalSearches++;
                var horizon = StepTowards(entity.Position, waypoint, 2048);
                entityNavigation.PathLength = _localSearch.Find(grid, _motion!, index, entity.Position, horizon, Path(index), locomotor: PhysicalProfile(index)); entityNavigation.PathCursor = 0;
                if (entityNavigation.PathLength == 0 && blocker >= 0 && NavigationLocalSearches < LocalSearchBudget) TryYield(index, blocker);
            }
            if (!terrain && entityNavigation.YieldFor.Value == 0)
            {
                // Steering can leave the original visible segment; rebuild from the actual position.
                _groupRoutes.Remove(entity.Id);
            }
            NavigationWaitingCount++;
        }
    }

    private void PreparePointRoutes(CancellationToken token)
    {
        var grid = _navigation!.Grid;
        int goals = 0;
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ref var entity = ref _entities[i];
            ref var entityMotion = ref entity.Motion;
            ref var entityNavigation = ref entity.Navigation;
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            if (!ClearTerrain(i, entityMotion.Target, entityMotion.Target)) { RejectRoute(ref entity); continue; }
            if (ClearTerrain(i, entity.Position, entityMotion.Target))
                _groupRoutes[entity.Id] = new([entityMotion.Target], grid.Revision);
            else _navigationGoals[goals++] = entityNavigation.GoalCell;
        }
        _navigation.Prepare(_navigationGoals.AsSpan(0, goals), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ref var entity = ref _entities[i];
            ref var entityMotion = ref entity.Motion;
            ref var entityNavigation = ref entity.Navigation;
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            int start = grid.CellAt(entity.Position);
            if (!_navigation.TryDistance(entityNavigation.GoalCell, start, out int distance)) continue;
            if (distance < 0) { RejectRoute(ref entity); continue; }
            var turns = new List<int> { start };
            int cell = start, lastDirection = 0;
            while (cell != entityNavigation.GoalCell)
            {
                if ((distance & 1023) == 0) token.ThrowIfCancellationRequested();
                int next = -1;
                for (int direction = 0; direction < 4; direction++)
                {
                    int neighbor = GridNeighbors.Neighbor(grid, cell, direction);
                    if (neighbor >= 0 && _navigation.TryDistance(entityNavigation.GoalCell, neighbor, out int value) && value == distance - 1 &&
                        (next < 0 || neighbor < next)) next = neighbor;
                }
                if (next < 0) throw new InvalidOperationException("Terrain distance field has no descending edge.");
                int step = next - cell;
                if (cell != start && step != lastDirection) turns.Add(cell);
                cell = next; lastDirection = step; distance--;
            }
            turns.Add(entityNavigation.GoalCell);
            _groupRoutes[entity.Id] = new(RoutePoints(turns.ToArray(), grid, entityMotion.Target), grid.Revision);
        }
    }

    private bool TryWaypoint(int index, out WorldPosition waypoint)
    {
        ref var entity = ref _entities[index]; var grid = _navigation!.Grid;
        waypoint = default;
        if (!_groupRoutes.TryGetValue(entity.Id, out var route) || route.Revision != grid.Revision) return false;
        if (route.HasWaypoint && entity.Position != route.Waypoint) { waypoint = route.Waypoint; return true; }
        if (route.HasWaypoint) route.Cursor++;
        if (_weighted is not null)
        {
            route.CostPrefix ??= new long[route.Points.Length];
            var previous = entity.Position; long cost = 0;
            for (int p = route.Cursor; p < route.Points.Length; p++)
            {
                var point = GroundPosition(route.Points[p], entity.Position.Elevation);
                long segment = TerrainCost.Segment(grid, PhysicalProfile(index)!, previous, point);
                cost = segment == long.MaxValue || cost > long.MaxValue - segment ? long.MaxValue : cost + segment;
                route.CostPrefix[p] = cost; previous = point;
            }
        }
        for (int i = route.Points.Length - 1; i >= route.Cursor; i--)
        {
            var target = GroundPosition(route.Points[i], entity.Position.Elevation);
            if (!ClearTerrain(index, entity.Position, target)) continue;
            if (_weighted is not null && TerrainCost.Segment(grid, PhysicalProfile(index)!, entity.Position, target) > route.CostPrefix![i]) continue;
            route.Cursor = i; route.Waypoint = waypoint = target; route.HasWaypoint = true; return true;
        }
        // A conservative start-cell center is also a useful recovery after a local detour.
        var center = GroundPosition(grid.Center(grid.CellAt(entity.Position)), entity.Position.Elevation);
        if (entity.Position != center && ClearTerrain(index, entity.Position, center))
        { route.Waypoint = waypoint = center; route.HasWaypoint = false; return true; }
        _groupRoutes.Remove(entity.Id);
        return false;
    }

    private void MoveTo(int index, WorldPosition next)
    {
        ref var entity = ref _entities[index];
        ref var entityNavigation = ref entity.Navigation;
        ref var entityMotion = ref entity.Motion;
        ref var spatial = ref entity.StorageEntity.Get<SpatialState>();
        spatial.Position = next; entityNavigation.LastProgressTick = Tick; _motion!.SetEnd(index, next);
        if (entityNavigation.YieldFor.Value == 0 && spatial.Position == entityMotion.Target) { entityMotion.Moving = false; entityNavigation.WaitTicks = 0; }
    }

    private bool IsFollowing(int requesterIndex, int blockerIndex)
    {
        ref var a = ref _entities[requesterIndex];
        ref var aMotion = ref a.Motion;
        ref var b = ref _entities[blockerIndex];
        ref var bMotion = ref b.Motion;
        ref var bNavigation = ref b.Navigation;
        if (!bMotion.Moving || bNavigation.YieldFor.Value != 0 || b.Position == bMotion.Target || Tick - bNavigation.LastProgressTick > 8) return false;
        long ax = (long)aMotion.Target.X - a.Position.X, ay = (long)aMotion.Target.Y - a.Position.Y;
        long bx = (long)bMotion.Target.X - b.Position.X, by = (long)bMotion.Target.Y - b.Position.Y;
        Int128 cross = (Int128)ax * by - (Int128)ay * bx;
        return ax * bx + ay * by > 0 && cross * cross * 16 <= ((Int128)ax * ax + (Int128)ay * ay) * ((Int128)bx * bx + (Int128)by * by) &&
            DistanceSquared(b.Position, bMotion.Target) < DistanceSquared(a.Position, bMotion.Target);
    }

    private void TryYield(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entities[requesterIndex];
        ref var requesterOwnership = ref requester.Ownership;
        ref var requesterNavigation = ref requester.Navigation;
        ref var requesterMotion = ref requester.Motion;
        ref var blocker = ref _entities[blockerIndex];
        ref var blockerOwnership = ref blocker.Ownership;
        ref var blockerNavigation = ref blocker.Navigation;
        ref var blockerMotion = ref blocker.Motion;
        if (!_types[blocker.UnitType].Movable || requesterOwnership.Owner != blockerOwnership.Owner || blockerNavigation.YieldFor.Value != 0 || IsFollowing(requesterIndex, blockerIndex) ||
            !requesterNavigation.GroupMember && requesterMotion.Target == blocker.Position) return;
        if (blockerMotion.Moving && blocker.Id.Value < requester.Id.Value) return;
        NavigationLocalSearches++;
        int length = _localSearch.Find(_navigation!.Grid, _motion!, blockerIndex, blocker.Position, blockerMotion.Target,
            Path(blockerIndex), requester.Position, PhysicalProfile(blockerIndex), _types[requester.UnitType].Body);
        if (length == 0) return;
        blockerNavigation.YieldFor = requester.Id; blockerNavigation.YieldDeadline = Tick + 128;
        blockerNavigation.PathLength = length; blockerNavigation.PathCursor = 0; blockerNavigation.LocalTarget = Path(blockerIndex)[length - 1];
        blockerMotion.Moving = true; blockerNavigation.WaitTicks = 0;
        _groupRoutes.Remove(blocker.Id); NavigationYields++;
    }

    private void RejectRoute(ref EntityState entity)
    {
        ref var entityMotion = ref entity.Motion;
        ref var entityNavigation = ref entity.Navigation;
        entityMotion.Moving = false; entityNavigation.GroupMember = false; _groupRoutes.Remove(entity.Id);
        entityNavigation.WaitTicks = entityNavigation.PathLength = entityNavigation.PathCursor = 0;
        entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; entityNavigation.LocalTarget = default;
        _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, entityMotion.Target, entity.UnitType));
    }

    private static long DistanceSquared(WorldPosition a, WorldPosition b)
    {
        long dx = (long)a.X - b.X, dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
