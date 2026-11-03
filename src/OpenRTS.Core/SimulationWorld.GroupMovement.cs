using System.Diagnostics;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private sealed class GroupRoute(int[] cells)
    {
        public readonly int[] Cells = cells;
        public int Cursor;
    }
    private readonly Dictionary<EntityId, GroupRoute> _groupRoutes = new();
    private readonly Dictionary<int, EntityId> _groupReservations = new();
    private SharedRouteTree? _routeTree;
    private bool _bulkAllowed, _routesDirty;
    private int _treeRoot, _groupEdgeCount;
    private EntityId[] _groupEdge = [];
    private bool[] _handled = [];
    private int[] _intents = [], _intentOrder = [];
    private bool[] _canMove = [];
    private int _convoyDirection;
    private readonly int[] _goalCycle = new int[16], _cycleGoals = new int[16];
    private int _arrivalCursor;
    private readonly IComparer<int> _cellOrder;
    public bool GroupTranslating => _bulkAllowed || _groupEdgeCount > 0;
    public int GroupRouteBuilds { get; private set; }
    public int GroupRouteVisitedCells { get; private set; }
    public double GroupRouteBuildMilliseconds { get; private set; }
    public int NavigationGoalExchanges { get; private set; }
    public int NavigationQueueWaitingCount { get; private set; }

    private bool TryOccupant(int cell, out EntityId occupant)
        => _occupied.TryGetValue(cell, out occupant) || _groupReservations.TryGetValue(cell, out occupant);

    private void SetGroupTarget(WorldPosition position)
    {
        var grid = _navigation!.Grid;
        if (Count == 0) { _groupSlots = []; _bulkAllowed = false; return; }
        int anchor = grid.CellAt(position);
        var slots = GroupDestinations.Slots(grid, anchor, Count);
        if (slots is null) { Reject(); return; }
        var members = new GroupDestinations.Member[Count];
        for (int i = 0; i < Count; i++)
            members[i] = new(_entities[i].Id, _entities[i].WaypointCell >= 0 ? _entities[i].WaypointCell : _entities[i].OccupiedCell, i);
        _groupRoutes.Clear(); _routeTree = null; _routesDirty = true; _bulkAllowed = true;
        _treeRoot = slots.OrderBy(cell => Manhattan(grid, cell, anchor)).ThenBy(cell => cell).First();
        // Spatial matching keeps nearby members nearby, without retaining the crowd's original extent or offsets.
        GroupDestinations.Assign(grid, members, slots, (index, goal) =>
        {
            SetTarget(index, grid.Center(goal));
            _entities[index].GroupMember = true;
        });
        Array.Sort(slots); _groupSlots = slots;
        void Reject() => _events.Add(new(Tick, WorldEventKind.NoRoute, default, position));
    }

    private void TryBulkStep()
    {
        if (_groupEdgeCount > 0) { AdvanceBulk(); return; }
        if (!_bulkAllowed) return;
        var grid = _navigation!.Grid;
        int dx = 0, dy = 0, members = 0;
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order];
            ref var entity = ref _entities[i];
            if (!entity.GroupMember) continue;
            if (entity.WaypointCell >= 0 || _handled[i]) return;
            int x = entity.GoalCell % grid.Width - entity.OccupiedCell % grid.Width;
            int y = entity.GoalCell / grid.Width - entity.OccupiedCell / grid.Width;
            if (members++ == 0) { dx = x; dy = y; }
            else if (x != dx || y != dy) { _bulkAllowed = false; return; }
        }
        if (members == 0 || dx == 0 && dy == 0) { _bulkAllowed = false; return; }
        int primary = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dx) : Math.Sign(dy) * grid.Width;
        int secondary = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dy) * grid.Width : Math.Sign(dx);
        int step = Fits(primary) ? primary : Fits(secondary) ? secondary : 0;
        if (step == 0) { _bulkAllowed = false; return; }
        _groupReservations.EnsureCapacity(members);
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order];
            ref var entity = ref _entities[i];
            if (!entity.GroupMember) continue;
            entity.WaypointCell = entity.OccupiedCell + step;
            _groupReservations.Add(entity.WaypointCell, entity.Id);
            _groupEdge[_groupEdgeCount++] = entity.Id;
        }
        AdvanceBulk();
        bool Fits(int direction)
        {
            if (direction == 0) return false;
            for (int i = 0; i < Count; i++)
            {
                ref var entity = ref _entities[i];
                if (!entity.GroupMember) continue;
                int destination = entity.OccupiedCell + direction;
                if ((uint)destination >= (uint)grid.CellCount || grid.IsBlocked(destination) || _vacated.Contains(destination)) return false;
                if (_occupied.TryGetValue(destination, out var occupant) && !_entities[_indices[occupant]].GroupMember) return false;
            }
            return true;
        }
    }

    private void AdvanceBulk()
    {
        var grid = _navigation!.Grid;
        bool complete = true;
        for (int i = 0; i < _groupEdgeCount; i++)
        {
            if (!_indices.TryGetValue(_groupEdge[i], out int index)) continue;
            ref var entity = ref _entities[index];
            _handled[index] = true;
            complete &= Advance(ref entity, grid.Center(entity.WaypointCell, entity.Position.Elevation));
        }
        if (!complete) return;
        // All riders have identical displacement. Remove sources first, then publish the new occupancy.
        for (int i = 0; i < _groupEdgeCount; i++)
            if (_indices.TryGetValue(_groupEdge[i], out int index))
            { _vacated.Add(_entities[index].OccupiedCell); _occupied.Remove(_entities[index].OccupiedCell); }
        for (int i = 0; i < _groupEdgeCount; i++)
            if (_indices.TryGetValue(_groupEdge[i], out int index))
            {
                ref var entity = ref _entities[index];
                entity.OccupiedCell = entity.WaypointCell; entity.WaypointCell = -1;
                _occupied.Add(entity.OccupiedCell, entity.Id);
                entity.Moving = entity.Position != entity.Target;
            }
        _groupEdgeCount = 0; _groupReservations.Clear();
    }

    private void TryConvoyStep()
    {
        if (_bulkAllowed || _groupEdgeCount > 0 || !HasMovingGroup()) return;
        var grid = _navigation!.Grid;
        if (_intents.Length < _entities.Length)
        {
            Array.Resize(ref _intents, _entities.Length); Array.Resize(ref _intentOrder, _entities.Length);
            Array.Resize(ref _canMove, _entities.Length);
        }
        Array.Fill(_intents, -1, 0, Count);
        int count = 0;
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order];
            ref var entity = ref _entities[i];
            if (!entity.GroupMember || !entity.Moving || _handled[i] || entity.WaypointCell >= 0 ||
                entity.YieldFor.Value != 0 || entity.PathCursor < entity.PathLength || entity.OccupiedCell == entity.GoalCell) continue;
            int goal = GroupWaypoint(ref entity), best = Manhattan(grid, entity.OccupiedCell, goal), candidate = -1;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbor = LocalRouteSearch.Neighbor(grid, entity.OccupiedCell, direction);
                if (neighbor < 0 || grid.IsBlocked(neighbor) || _vacated.Contains(neighbor)) continue;
                int value = Manhattan(grid, neighbor, goal);
                if (value < best || value == best && candidate >= 0 && neighbor < candidate)
                { best = value; candidate = neighbor; }
            }
            if (candidate >= 0) { _intents[i] = candidate; _intentOrder[count++] = i; }
        }
        Array.Sort(_intentOrder, 0, count, _cellOrder);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int direction = (_convoyDirection + attempt) % 4;
            int step = direction switch { 0 => -grid.Width, 1 => -1, 2 => 1, _ => grid.Width };
            Array.Clear(_canMove, 0, Count);
            int accepted = 0;
            // Resolve dependencies front-to-back. A chain can share swept cells only at identical displacement.
            for (int order = 0; order < count; order++)
            {
                int i = _intentOrder[step > 0 ? count - 1 - order : order];
                int next = _intents[i];
                if (next - _entities[i].OccupiedCell != step || _groupReservations.ContainsKey(next)) continue;
                if (_occupied.TryGetValue(next, out var owner) && !_canMove[_indices[owner]]) continue;
                _canMove[i] = true; accepted++;
            }
            if (accepted == 0) continue;
            _convoyDirection = (direction + 1) % 4;
            _groupReservations.EnsureCapacity(accepted);
            for (int order = 0; order < Count; order++)
            {
                int i = _movementOrder[order];
                if (!_canMove[i]) continue;
                ref var entity = ref _entities[i];
                entity.WaypointCell = _intents[i]; entity.WaitTicks = 0;
                _groupReservations.Add(entity.WaypointCell, entity.Id);
                _groupEdge[_groupEdgeCount++] = entity.Id;
            }
            AdvanceBulk();
            return;
        }
    }

    private void PrepareGroupRoutes(CancellationToken token)
    {
        if (_bulkAllowed || !HasMovingGroup()) return;
        var grid = _navigation!.Grid;
        long started = Stopwatch.GetTimestamp();
        if (_routeTree is null || !ReferenceEquals(_routeTree.Grid, grid))
        {
            _routeTree = new(grid, _treeRoot, token); _routesDirty = true;
            GroupRouteBuilds++; GroupRouteVisitedCells += _routeTree.VisitedCells;
        }
        if (!_routesDirty) return;
        _groupRoutes.Clear();
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order];
            ref var entity = ref _entities[i];
            if (!entity.GroupMember || !entity.Moving) continue;
            int start = entity.WaypointCell >= 0 ? entity.WaypointCell : entity.OccupiedCell;
            var route = _routeTree.Route(start, entity.GoalCell, token);
            if (route is not null) _groupRoutes[entity.Id] = new(route);
            else
            {
                // Finish an existing reserved edge before stopping on an unreachable new target.
                var rejectedTarget = entity.Target;
                entity.GroupMember = false;
                entity.Target = grid.Center(start, entity.Position.Elevation); entity.GoalCell = start;
                entity.Moving = entity.Position != entity.Target;
                _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, rejectedTarget, entity.UnitType));
            }
        }
        _routesDirty = false;
        GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private bool HasMovingGroup()
    {
        for (int i = 0; i < Count; i++) if (_entities[i].GroupMember && _entities[i].Moving) return true;
        return false;
    }

    private bool IsFollowingQueue(int index, int goal, int distance)
    {
        ref var requester = ref _entities[index];
        if (!requester.GroupMember) return false;
        var grid = _navigation!.Grid;
        bool found = false;
        for (int direction = 0; direction < 4; direction++)
        {
            int next = LocalRouteSearch.Neighbor(grid, requester.OccupiedCell, direction);
            if (next < 0 || grid.IsBlocked(next) || Manhattan(grid, next, goal) >= distance) continue;
            found = true;
            if (_vacated.Contains(next)) continue;
            if (!TryOccupant(next, out var owner)) return false;
            int blockerIndex = _indices[owner];
            ref var blocker = ref _entities[blockerIndex];
            if (!blocker.GroupMember || !blocker.Moving || blocker.YieldFor.Value != 0) return false;
            int step = next - requester.OccupiedCell;
            if (blocker.WaypointCell >= 0)
            {
                bool same = blocker.WaypointCell - blocker.OccupiedCell == step;
                bool flow = _routeTree is not null && _routeTree.Depth(next) < _routeTree.Depth(requester.OccupiedCell) &&
                    _routeTree.Depth(blocker.WaypointCell) < _routeTree.Depth(blocker.OccupiedCell);
                if (!same && !flow) return false;
                continue;
            }
            int target = blocker.PathCursor < blocker.PathLength ? Path(blockerIndex)[blocker.PathCursor] : GroupWaypoint(ref blocker);
            int best = Manhattan(grid, blocker.OccupiedCell, target), candidate = -1;
            for (int d = 0; d < 4; d++)
            {
                int cell = LocalRouteSearch.Neighbor(grid, blocker.OccupiedCell, d);
                if (cell < 0 || grid.IsBlocked(cell)) continue;
                int value = Manhattan(grid, cell, target);
                if (value < best || value == best && candidate >= 0 && cell < candidate) { best = value; candidate = cell; }
            }
            if (candidate < 0) return false;
            bool aligned = candidate - blocker.OccupiedCell == step;
            bool following = _routeTree is not null && _routeTree.Depth(next) < _routeTree.Depth(requester.OccupiedCell) &&
                _routeTree.Depth(candidate) < _routeTree.Depth(blocker.OccupiedCell);
            if (!aligned && !following) return false;
        }
        return found;
    }

    private bool TryExchangeGoals(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entities[requesterIndex];
        ref var blocker = ref _entities[blockerIndex];
        var grid = _navigation!.Grid;
        if (!requester.GroupMember || !blocker.GroupMember || requester.WaitTicks < 16 ||
            blocker.YieldFor.Value != 0 || blocker.WaypointCell >= 0) return false;
        if (blocker.Moving)
        {
            int oldCost = Manhattan(grid, requester.OccupiedCell, requester.GoalCell) + Manhattan(grid, blocker.OccupiedCell, blocker.GoalCell);
            int newCost = Manhattan(grid, requester.OccupiedCell, blocker.GoalCell) + Manhattan(grid, blocker.OccupiedCell, requester.GoalCell);
            if (newCost >= oldCost || !grid.IsRectangleClear(requester.OccupiedCell, blocker.GoalCell)) return false;
        }
        else
        {
            int remaining = Manhattan(grid, blocker.OccupiedCell, requester.GoalCell);
            if (blocker.OccupiedCell != blocker.GoalCell || remaining >= Manhattan(grid, requester.OccupiedCell, requester.GoalCell)) return false;
        }
        if (!grid.IsRectangleClear(blocker.OccupiedCell, requester.GoalCell)) return false;
        // Adjacent goal exchange preserves the endpoint set and does not increase total Manhattan cost.
        int goal = requester.GoalCell;
        requester.GoalCell = blocker.GoalCell; blocker.GoalCell = goal;
        requester.Target = grid.Center(requester.GoalCell, requester.Position.Elevation);
        blocker.Target = grid.Center(blocker.GoalCell, blocker.Position.Elevation);
        requester.WaitTicks = blocker.WaitTicks = 0;
        requester.PathLength = requester.PathCursor = blocker.PathLength = blocker.PathCursor = 0;
        requester.Moving = requester.Position != requester.Target;
        blocker.Moving = blocker.Position != blocker.Target;
        _groupRoutes[requester.Id] = new([requester.GoalCell]);
        _groupRoutes[blocker.Id] = new([blocker.GoalCell]);
        NavigationGoalExchanges++;
        return true;
    }

    private void RepairArrivals()
    {
        if (_bulkAllowed || Count == 0) return;
        int attempts = 0, inspected = 0, start = _arrivalCursor % Count;
        while (inspected < Math.Min(Count, 256) && attempts < 8 && NavigationLocalSearches < LocalSearchBudget)
        {
            int index = _movementOrder[(start + inspected++) % Count];
            ref var entity = ref _entities[index];
            if (!entity.GroupMember || !entity.Moving || entity.WaypointCell >= 0 || entity.YieldFor.Value != 0 ||
                entity.WaitTicks < 16 || !_navigation!.Grid.IsRectangleClear(entity.OccupiedCell, entity.GoalCell)) continue;
            attempts++; NavigationLocalSearches++;
            if (!TryImproveNeighborTargets(index)) TryRepairGoalCycle(index);
        }
        _arrivalCursor = (start + inspected) % Count;
    }

    private bool TryImproveNeighborTargets(int index)
    {
        ref var entity = ref _entities[index];
        var grid = _navigation!.Grid;
        int distance = Manhattan(grid, entity.OccupiedCell, entity.GoalCell);
        for (int direction = 0; direction < 4; direction++)
        {
            int cell = LocalRouteSearch.Neighbor(grid, entity.OccupiedCell, direction);
            if (cell < 0 || Manhattan(grid, cell, entity.GoalCell) >= distance || !_occupied.TryGetValue(cell, out var owner)) continue;
            if (TryExchangeGoals(index, _indices[owner])) return true;
        }
        return false;
    }

    private bool TryRepairGoalCycle(int index)
    {
        int length = 0, current = index;
        bool closed = false;
        var grid = _navigation!.Grid;
        while (length < _goalCycle.Length)
        {
            ref var entity = ref _entities[current];
            if (!entity.GroupMember || !entity.Moving || entity.WaypointCell >= 0 || entity.YieldFor.Value != 0 ||
                !grid.IsRectangleClear(entity.OccupiedCell, entity.GoalCell)) return false;
            for (int i = 0; i < length; i++) if (_goalCycle[i] == current) return false;
            _goalCycle[length] = current; _cycleGoals[length++] = entity.GoalCell;
            int best = Manhattan(grid, entity.OccupiedCell, entity.GoalCell), candidate = -1;
            for (int direction = 0; direction < 4; direction++)
            {
                int cell = LocalRouteSearch.Neighbor(grid, entity.OccupiedCell, direction);
                if (cell < 0 || grid.IsBlocked(cell)) continue;
                int value = Manhattan(grid, cell, entity.GoalCell);
                if (value < best || value == best && candidate >= 0 && cell < candidate) { best = value; candidate = cell; }
            }
            if (candidate < 0 || !_occupied.TryGetValue(candidate, out var owner) || !_indices.TryGetValue(owner, out int next) ||
                _entities[next].OccupiedCell != candidate) return false;
            if (next == index) { closed = true; break; }
            current = next;
        }
        if (!closed || length < 2) return false;
        int oldCost = 0, newCost = 0;
        for (int i = 0; i < length; i++)
        {
            int cell = _entities[_goalCycle[i]].OccupiedCell;
            oldCost += Manhattan(grid, cell, _cycleGoals[i]);
            int goal = _cycleGoals[(i + length - 1) % length];
            if (!grid.IsRectangleClear(cell, goal)) return false;
            newCost += Manhattan(grid, cell, goal);
        }
        if (newCost >= oldCost) return false;
        // Rotate targets around a local dependency cycle: the endpoint set is unchanged and total cost decreases.
        for (int i = 0; i < length; i++)
        {
            ref var entity = ref _entities[_goalCycle[i]];
            entity.GoalCell = _cycleGoals[(i + length - 1) % length];
            entity.Target = grid.Center(entity.GoalCell, entity.Position.Elevation); entity.Moving = entity.Position != entity.Target;
            entity.WaitTicks = entity.PathLength = entity.PathCursor = 0;
            _groupRoutes[entity.Id] = new([entity.GoalCell]);
        }
        NavigationGoalExchanges += length - 1;
        return true;
    }

    private int GroupWaypoint(ref EntityState entity)
    {
        if (!_groupRoutes.TryGetValue(entity.Id, out var route)) return entity.GoalCell;
        int oldCursor = route.Cursor;
        while (route.Cursor < route.Cells.Length && route.Cells[route.Cursor] == entity.OccupiedCell) route.Cursor++;
        if (_navigation!.Grid.IsRectangleClear(entity.OccupiedCell, entity.GoalCell))
            route.Cursor = route.Cells.Length;
        else
            // A local bypass need not touch every old corner: continue from the furthest visible future turn.
            for (int i = route.Cells.Length - 2; i > route.Cursor; i--)
                if (_navigation.Grid.IsRectangleClear(entity.OccupiedCell, route.Cells[i])) { route.Cursor = i; break; }
        if (route.Cursor > oldCursor && entity.YieldFor.Value == 0) entity.PathLength = entity.PathCursor = 0;
        return route.Cursor < route.Cells.Length ? route.Cells[route.Cursor] : entity.GoalCell;
    }

    private static int Manhattan(NavigationGrid grid, int a, int b)
        => Math.Abs(a % grid.Width - b % grid.Width) + Math.Abs(a / grid.Width - b / grid.Width);

    private bool Distance(bool group, int goal, int cell, out int distance)
    {
        if (!group) return _navigation!.TryDistance(goal, cell, out distance);
        distance = _navigation!.Grid.IsBlocked(cell) ? -1 : Manhattan(_navigation.Grid, goal, cell);
        return true;
    }

    private void WriteGroupState(BinaryWriter writer)
    {
        writer.Write(_bulkAllowed); writer.Write(_routesDirty); writer.Write(_treeRoot); writer.Write(_routeTree?.Grid.Revision ?? -1);
        writer.Write(_convoyDirection); writer.Write(_arrivalCursor);
        writer.Write(_groupEdgeCount);
        for (int i = 0; i < _groupEdgeCount; i++) writer.Write(_groupEdge[i].Value);
        foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
        {
            if (_groupRoutes.TryGetValue(entity.Id, out var route) && entity.GroupMember)
            {
                writer.Write(route.Cells.Length - route.Cursor);
                foreach (int cell in route.Cells.AsSpan(route.Cursor)) writer.Write(cell);
            }
            else writer.Write(0);
        }
    }
}
