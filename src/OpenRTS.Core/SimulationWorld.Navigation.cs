namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int LocalSearchBudget = 64;
    private readonly LocalRouteSearch _localSearch = new();
    private int[] _localPaths = [], _groupSlots = [];
    public int NavigationLocalSearches { get; private set; }
    public int NavigationYields { get; private set; }
    public int GroupSlotCount => _groupSlots.Length;

    private Span<int> Path(int index) => _localPaths.AsSpan(index * LocalRouteSearch.PathCapacity, LocalRouteSearch.PathCapacity);

    private void UpdateNavigation(CancellationToken cancellationToken)
    {
        var navigation = _navigation!;
        var grid = navigation.Grid;
        if (_navigationGoals.Length < Count) Array.Resize(ref _navigationGoals, _entities.Length);
        if (_orderDirty)
        {
            if (_movementOrder.Length < Count) Array.Resize(ref _movementOrder, _entities.Length);
            for (int i = 0; i < Count; i++) _movementOrder[i] = i;
            Array.Sort(_movementOrder, 0, Count, _idOrder);
            _orderDirty = false;
        }
        if (_handled.Length < _entities.Length)
        { Array.Resize(ref _handled, _entities.Length); Array.Resize(ref _groupEdge, _entities.Length); }
        Array.Clear(_handled, 0, Count);
        int goals = 0;
        for (int i = 0; i < Count; i++)
            if (_entities[i].Moving && !_entities[i].GroupMember) _navigationGoals[goals++] = _entities[i].GoalCell;
        navigation.Prepare(_navigationGoals.AsSpan(0, goals), cancellationToken);
        NavigationWaitingCount = NavigationLocalSearches = NavigationYields = 0;
        NavigationGoalExchanges = 0;
        NavigationQueueWaitingCount = 0;
        GroupRouteBuilds = GroupRouteVisitedCells = 0; GroupRouteBuildMilliseconds = 0;
        TryBulkStep();
        PrepareGroupRoutes(cancellationToken);
        RepairArrivals();
        TryConvoyStep();
        for (int order = 0; order < Count; order++)
        {
            int index = _movementOrder[(order + (int)(Tick % Count)) % Count];
            ref var entity = ref _entities[index];
            if (!entity.Moving || _handled[index]) continue;
            if (entity.GroupMember && _bulkAllowed && entity.WaypointCell < 0) { NavigationWaitingCount++; continue; }
            if (entity.WaypointCell < 0)
            {
                if (entity.YieldFor.Value != 0 && entity.PathCursor == entity.PathLength)
                {
                    bool requesterMoving = _indices.TryGetValue(entity.YieldFor, out int requester) && _entities[requester].Moving;
                    int cell = requesterMoving ? _entities[requester].OccupiedCell : -1;
                    int separation = cell < 0 ? int.MaxValue : Math.Abs(cell % grid.Width - entity.YieldOrigin % grid.Width) + Math.Abs(cell / grid.Width - entity.YieldOrigin / grid.Width);
                    if (requesterMoving && separation <= 2 && Tick < entity.YieldDeadline)
                    { NavigationWaitingCount++; continue; }
                    entity.YieldFor = default; entity.YieldOrigin = 0; entity.YieldDeadline = 0;
                    if (entity.GoalCell == entity.OccupiedCell) { entity.Moving = false; continue; }
                }
                int goal = entity.GroupMember ? GroupWaypoint(ref entity) : entity.GoalCell;
                if (entity.YieldFor.Value == 0 && entity.OccupiedCell == entity.GoalCell) { entity.Moving = false; entity.WaitTicks = 0; continue; }
                int candidate = -1;
                if (entity.PathCursor < entity.PathLength)
                {
                    int next = Path(index)[entity.PathCursor];
                    if (!grid.IsBlocked(next) && !TryOccupant(next, out _) && !_vacated.Contains(next)) candidate = next;
                    else if (++entity.WaitTicks >= 8)
                    {
                        entity.PathLength = entity.PathCursor = 0;
                        // A forced yield may be interrupted by new terrain or another reservation.
                        entity.YieldFor = default; entity.YieldOrigin = 0; entity.YieldDeadline = 0;
                    }
                    if (candidate < 0) { NavigationWaitingCount++; continue; }
                }
                else
                {
                    if (!Distance(entity.GroupMember, goal, entity.OccupiedCell, out int distance))
                    { NavigationWaitingCount++; continue; }
                    if (distance < 0)
                    {
                        entity.Moving = false;
                        entity.GroupMember = false;
                        _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, entity.Target, entity.UnitType));
                        continue;
                    }
                    int best = distance;
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int neighbor = LocalRouteSearch.Neighbor(grid, entity.OccupiedCell, direction);
                        if (neighbor < 0 || TryOccupant(neighbor, out _) || _vacated.Contains(neighbor)) continue;
                        Distance(entity.GroupMember, goal, neighbor, out int value);
                        if (value >= 0 && value < distance && (value < best || value == best && neighbor < candidate))
                        { candidate = neighbor; best = value; }
                    }
                    if (candidate < 0)
                    {
                        if (IsFollowingQueue(index, goal, distance))
                        {
                            entity.WaitTicks = 0;
                            NavigationWaitingCount++; NavigationQueueWaitingCount++;
                            continue;
                        }
                        entity.WaitTicks = Math.Min(1024, entity.WaitTicks + 1);
                        if (entity.WaitTicks >= 4 && (Tick + entity.Id.Value) % 8 == 0 && NavigationLocalSearches < LocalSearchBudget)
                        {
                            NavigationLocalSearches++;
                            entity.PathLength = _localSearch.Find(grid, entity.OccupiedCell, navigation, goal, distance, _occupied, _vacated, Path(index), reserved: _groupReservations, manhattan: entity.GroupMember);
                            entity.PathCursor = 0;
                            if (entity.PathLength > 0) candidate = Path(index)[0];
                            else TryYield(index, goal, distance);
                            if (!entity.Moving) continue;
                        }
                        if (candidate < 0) { NavigationWaitingCount++; continue; }
                    }
                }
                entity.WaypointCell = candidate;
                _occupied.Add(candidate, entity.Id);
                entity.WaitTicks = 0;
            }
            var waypoint = grid.Center(entity.WaypointCell, entity.Position.Elevation);
            if (Advance(ref entity, waypoint))
            {
                _vacated.Add(entity.OccupiedCell);
                _occupied.Remove(entity.OccupiedCell);
                entity.OccupiedCell = entity.WaypointCell;
                entity.WaypointCell = -1;
                if (entity.PathCursor < entity.PathLength) entity.PathCursor++;
                if (entity.YieldFor.Value == 0 && entity.OccupiedCell == entity.GoalCell) entity.Moving = false;
            }
        }
    }

    private void TryYield(int requesterIndex, int goal, int distance)
    {
        var navigation = _navigation!;
        ref var requester = ref _entities[requesterIndex];
        for (int direction = 0; direction < 4 && NavigationLocalSearches < LocalSearchBudget; direction++)
        {
            int cell = LocalRouteSearch.Neighbor(navigation.Grid, requester.OccupiedCell, direction);
            if (cell < 0 || !_occupied.TryGetValue(cell, out var id) || !_indices.TryGetValue(id, out int index)) continue;
            ref var blocker = ref _entities[index];
            if (blocker.GroupMember && _bulkAllowed || blocker.WaypointCell >= 0 || blocker.YieldFor.Value != 0 || !requester.GroupMember && cell == goal) continue;
            if (blocker.Moving)
            {
                int target = blocker.PathCursor < blocker.PathLength ? Path(index)[blocker.PathCursor]
                    : blocker.GroupMember ? GroupWaypoint(ref blocker) : blocker.GoalCell;
                // A follower must not pull a queued unit backwards when that unit is heading away from it.
                if (Manhattan(navigation.Grid, cell, target) < Manhattan(navigation.Grid, requester.OccupiedCell, target)) continue;
                if (requester.GroupMember && blocker.GroupMember && _routeTree is not null)
                {
                    int ahead = _routeTree.Depth(cell), behind = _routeTree.Depth(requester.OccupiedCell);
                    if (ahead < behind || ahead == behind && blocker.Id.Value < requester.Id.Value) continue;
                }
                else if (blocker.Id.Value < requester.Id.Value) continue;
            }
            if (!Distance(requester.GroupMember, goal, cell, out int value) || value < 0 || value >= distance) continue;
            NavigationLocalSearches++;
            if (TryExchangeGoals(requesterIndex, index)) return;
            if (requester.GroupMember && blocker.GroupMember &&
                navigation.Grid.IsRectangleClear(requester.OccupiedCell, requester.GoalCell) &&
                navigation.Grid.IsRectangleClear(blocker.OccupiedCell, blocker.GoalCell)) continue;
            int length = _localSearch.Find(navigation.Grid, cell, navigation, goal, distance, _occupied, _vacated, Path(index), requester.OccupiedCell, _groupReservations, requester.GroupMember);
            if (length == 0) { if (TryExchangeGoals(requesterIndex, index)) return; continue; }
            blocker.PathLength = length; blocker.PathCursor = blocker.WaitTicks = 0;
            blocker.YieldFor = requester.Id; blocker.YieldOrigin = cell;
            blocker.YieldDeadline = checked(Tick + (LocalRouteSearch.PathCapacity * 2 + 4L) * ((1023 + SpeedPerTick) / SpeedPerTick));
            if (blocker.GoalCell < 0) blocker.GoalCell = blocker.OccupiedCell;
            blocker.Moving = true;
            NavigationYields++;
            return;
        }
    }
}
