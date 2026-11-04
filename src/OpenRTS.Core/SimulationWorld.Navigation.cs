namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int LocalSearchBudget = 64;
    private readonly LocalMotionSearch _localSearch = new();
    private WorldPosition[] _localPaths = [];
    private Span<WorldPosition> Path(int index) => _localPaths.AsSpan(index * LocalMotionSearch.PathCapacity, LocalMotionSearch.PathCapacity);
    public int NavigationLocalSearches { get; private set; }
    public int NavigationYields { get; private set; }

    private void RebuildMotionIndex()
    {
        if (_motion is null) return;
        _motion.Clear(_entities.Length);
        for (int i = 0; i < Count; i++) _motion.Add(i, _entities[i].Id, _entities[i].Position);
    }

    private void UpdateNavigation(CancellationToken token)
    {
        var grid = _navigation!.Grid;
        if (_navigationGoals.Length < Count) Array.Resize(ref _navigationGoals, _entities.Length);
        if (_orderDirty)
        {
            if (_movementOrder.Length < Count) Array.Resize(ref _movementOrder, _entities.Length);
            for (int i = 0; i < Count; i++) _movementOrder[i] = i;
            Array.Sort(_movementOrder, 0, Count, _idOrder); _orderDirty = false;
        }
        if (_handled.Length < _entities.Length)
        { Array.Resize(ref _handled, _entities.Length); Array.Resize(ref _cohort, _entities.Length); }
        Array.Clear(_handled, 0, Count);
        NavigationWaitingCount = NavigationLocalSearches = NavigationYields = NavigationGoalExchanges = NavigationQueueWaitingCount = 0;
        GroupRouteBuilds = GroupRouteVisitedCells = 0; GroupRouteBuildMilliseconds = 0; GroupTranslating = false;
        TryBulkStep();
        PrepareGroupRoutes(token);
        PreparePointRoutes(token);
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int index = _movementOrder[(order + (int)(Tick % Count)) % Count];
            ref var entity = ref _entities[index];
            if (!entity.Moving || _handled[index]) continue;
            WorldPosition waypoint;
            if (entity.YieldFor.Value != 0)
            {
                if (Tick >= entity.YieldDeadline)
                {
                    entity.YieldFor = default; entity.YieldDeadline = 0;
                    entity.PathLength = entity.PathCursor = 0; _groupRoutes.Remove(entity.Id);
                    entity.Moving = entity.Position != entity.Target;
                    continue;
                }
                bool waiting = _indices.TryGetValue(entity.YieldFor, out int requester) && _entities[requester].Moving &&
                    DistanceSquared(entity.Position, _entities[requester].Position) < 1536L * 1536 && Tick < entity.YieldDeadline;
                if (entity.Position == entity.LocalTarget)
                {
                    if (waiting) { NavigationWaitingCount++; continue; }
                    entity.YieldFor = default; entity.YieldDeadline = 0; _groupRoutes.Remove(entity.Id);
                    entity.Moving = entity.Position != entity.Target;
                    continue;
                }
                waypoint = entity.PathCursor < entity.PathLength ? Path(index)[entity.PathCursor] : entity.LocalTarget;
            }
            else
            {
                if (entity.Position == entity.Target) { entity.Moving = false; entity.WaitTicks = 0; continue; }
                if (!TryWaypoint(index, out waypoint)) { NavigationWaitingCount++; continue; }
                if (entity.PathCursor < entity.PathLength) waypoint = Path(index)[entity.PathCursor];
            }
            var next = StepTowards(entity.Position, waypoint);
            bool terrain = TerrainGeometry.Clear(grid, entity.Position, next, MotionIndex.BodyRadius);
            int blocker = -1;
            if (terrain && _motion!.ClearMove(index, next, out blocker))
            {
                MoveTo(index, next); entity.WaitTicks = 0;
                if (next == waypoint && entity.PathCursor < entity.PathLength) entity.PathCursor++;
                if (entity.PathCursor == entity.PathLength && entity.PathLength > 0 && _groupRoutes.TryGetValue(entity.Id, out var route))
                { entity.PathLength = entity.PathCursor = 0; route.HasWaypoint = false; }
                continue;
            }
            entity.WaitTicks = Math.Min(1024, entity.WaitTicks + 1);
            if (entity.YieldFor.Value == 0 && entity.WaitTicks >= 4 && entity.PathCursor == entity.PathLength &&
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
            if (entity.PathCursor < entity.PathLength)
            {
                if (entity.WaitTicks >= 8) entity.PathLength = entity.PathCursor = 0;
            }
            else if (entity.WaitTicks >= 4 && (Tick + entity.Id.Value) % 8 == 0 && NavigationLocalSearches < LocalSearchBudget)
            {
                NavigationLocalSearches++;
                var horizon = StepTowards(entity.Position, waypoint, 2048);
                entity.PathLength = _localSearch.Find(grid, _motion!, index, entity.Position, horizon, Path(index)); entity.PathCursor = 0;
                if (entity.PathLength == 0 && blocker >= 0 && NavigationLocalSearches < LocalSearchBudget) TryYield(index, blocker);
            }
            if (!terrain && entity.YieldFor.Value == 0)
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
            if (!entity.Moving || entity.GroupMember || entity.YieldFor.Value != 0 ||
                _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            if (!TerrainGeometry.Clear(grid, entity.Target, entity.Target, MotionIndex.BodyRadius)) { RejectRoute(ref entity); continue; }
            if (TerrainGeometry.Clear(grid, entity.Position, entity.Target, MotionIndex.BodyRadius))
                _groupRoutes[entity.Id] = new([entity.GoalCell], grid.Revision);
            else _navigationGoals[goals++] = entity.GoalCell;
        }
        _navigation.Prepare(_navigationGoals.AsSpan(0, goals), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ref var entity = ref _entities[i];
            if (!entity.Moving || entity.GroupMember || entity.YieldFor.Value != 0 ||
                _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            int start = grid.CellAt(entity.Position);
            if (!_navigation.TryDistance(entity.GoalCell, start, out int distance)) continue;
            if (distance < 0) { RejectRoute(ref entity); continue; }
            var turns = new List<int> { start };
            int cell = start, lastDirection = 0;
            while (cell != entity.GoalCell)
            {
                if ((distance & 1023) == 0) token.ThrowIfCancellationRequested();
                int next = -1;
                for (int direction = 0; direction < 4; direction++)
                {
                    int neighbor = GridNeighbors.Neighbor(grid, cell, direction);
                    if (neighbor >= 0 && _navigation.TryDistance(entity.GoalCell, neighbor, out int value) && value == distance - 1 &&
                        (next < 0 || neighbor < next)) next = neighbor;
                }
                if (next < 0) throw new InvalidOperationException("Terrain distance field has no descending edge.");
                int step = next - cell;
                if (cell != start && step != lastDirection) turns.Add(cell);
                cell = next; lastDirection = step; distance--;
            }
            turns.Add(entity.GoalCell);
            _groupRoutes[entity.Id] = new(turns.ToArray(), grid.Revision);
        }
    }

    private bool TryWaypoint(int index, out WorldPosition waypoint)
    {
        ref var entity = ref _entities[index]; var grid = _navigation!.Grid;
        waypoint = default;
        if (!_groupRoutes.TryGetValue(entity.Id, out var route) || route.Revision != grid.Revision) return false;
        if (route.HasWaypoint && entity.Position != route.Waypoint) { waypoint = route.Waypoint; return true; }
        if (route.HasWaypoint) route.Cursor++;
        for (int i = route.Cells.Length - 1; i >= route.Cursor; i--)
        {
            var target = route.Cells[i] == entity.GoalCell ? entity.Target : grid.Center(route.Cells[i], entity.Position.Elevation);
            if (!TerrainGeometry.Clear(grid, entity.Position, target, MotionIndex.BodyRadius)) continue;
            route.Cursor = i; route.Waypoint = waypoint = target; route.HasWaypoint = true; return true;
        }
        // A conservative start-cell center is also a useful recovery after a local detour.
        var center = grid.Center(grid.CellAt(entity.Position), entity.Position.Elevation);
        if (entity.Position != center && TerrainGeometry.Clear(grid, entity.Position, center, MotionIndex.BodyRadius))
        { route.Waypoint = waypoint = center; route.HasWaypoint = false; return true; }
        _groupRoutes.Remove(entity.Id);
        return false;
    }

    private void MoveTo(int index, WorldPosition next)
    {
        ref var entity = ref _entities[index];
        entity.Position = next; entity.LastProgressTick = Tick; _motion!.SetEnd(index, next);
        if (entity.YieldFor.Value == 0 && entity.Position == entity.Target) { entity.Moving = false; entity.WaitTicks = 0; }
    }

    private bool IsFollowing(int requesterIndex, int blockerIndex)
    {
        ref var a = ref _entities[requesterIndex]; ref var b = ref _entities[blockerIndex];
        if (!b.Moving || b.YieldFor.Value != 0 || b.Position == b.Target || Tick - b.LastProgressTick > 8) return false;
        long ax = (long)a.Target.X - a.Position.X, ay = (long)a.Target.Y - a.Position.Y;
        long bx = (long)b.Target.X - b.Position.X, by = (long)b.Target.Y - b.Position.Y;
        Int128 cross = (Int128)ax * by - (Int128)ay * bx;
        return ax * bx + ay * by > 0 && cross * cross * 16 <= ((Int128)ax * ax + (Int128)ay * ay) * ((Int128)bx * bx + (Int128)by * by) &&
            DistanceSquared(b.Position, b.Target) < DistanceSquared(a.Position, b.Target);
    }

    private void TryYield(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entities[requesterIndex]; ref var blocker = ref _entities[blockerIndex];
        if (blocker.YieldFor.Value != 0 || IsFollowing(requesterIndex, blockerIndex) ||
            !requester.GroupMember && requester.Target == blocker.Position) return;
        if (blocker.Moving && blocker.Id.Value < requester.Id.Value) return;
        NavigationLocalSearches++;
        int length = _localSearch.Find(_navigation!.Grid, _motion!, blockerIndex, blocker.Position, blocker.Target,
            Path(blockerIndex), requester.Position);
        if (length == 0) return;
        blocker.YieldFor = requester.Id; blocker.YieldDeadline = Tick + 128;
        blocker.PathLength = length; blocker.PathCursor = 0; blocker.LocalTarget = Path(blockerIndex)[length - 1];
        blocker.Moving = true; blocker.WaitTicks = 0;
        _groupRoutes.Remove(blocker.Id); NavigationYields++;
    }

    private void RejectRoute(ref EntityState entity)
    {
        entity.Moving = false; entity.GroupMember = false; _groupRoutes.Remove(entity.Id);
        entity.WaitTicks = entity.PathLength = entity.PathCursor = 0;
        entity.YieldFor = default; entity.YieldDeadline = 0; entity.LocalTarget = default;
        _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, entity.Target, entity.UnitType));
    }

    private static long DistanceSquared(WorldPosition a, WorldPosition b)
    {
        long dx = (long)a.X - b.X, dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
