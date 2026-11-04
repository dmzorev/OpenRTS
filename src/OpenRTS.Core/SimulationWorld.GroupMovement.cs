using System.Diagnostics;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private sealed class GroupRoute(int[] cells, int revision)
    {
        public readonly int[] Cells = cells;
        public readonly int Revision = revision;
        public int Cursor;
        public WorldPosition Waypoint;
        public bool HasWaypoint;
    }
    private readonly Dictionary<EntityId, GroupRoute> _groupRoutes = new();
    private SharedRouteTree? _routeTree;
    private bool _bulkAllowed;
    private int _treeRoot;
    private bool[] _handled = [], _cohort = [];
    private WorldPosition[] _groupSlots = [];
    public bool GroupTranslating { get; private set; }
    public int GroupRouteBuilds { get; private set; }
    public int GroupRouteVisitedCells { get; private set; }
    public double GroupRouteBuildMilliseconds { get; private set; }
    public int NavigationGoalExchanges { get; private set; }
    public int NavigationQueueWaitingCount { get; private set; }
    public int GroupSlotCount => _groupSlots.Length;

    private void SetGroupTarget(WorldPosition position)
    {
        var grid = _navigation!.Grid;
        if (Count == 0) { _groupSlots = []; _bulkAllowed = false; return; }
        var slots = GroupDestinations.Slots(grid, position, Count);
        if (slots is null) { Reject(); return; }
        var members = new GroupDestinations.Member[Count];
        for (int i = 0; i < Count; i++) members[i] = new(_entities[i].Id, _entities[i].Position, i);
        _groupRoutes.Clear(); _routeTree = null; _bulkAllowed = true;
        _treeRoot = grid.CellAt(slots.OrderBy(point => DistanceSquared(point, position)).ThenBy(point => point.Y).ThenBy(point => point.X).First());
        GroupDestinations.Assign(members, slots, (index, goal) =>
        {
            SetTarget(index, goal);
            _entities[index].GroupMember = true;
        });
        Array.Sort(slots, (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)); _groupSlots = slots;
        void Reject() => _events.Add(new(Tick, WorldEventKind.NoRoute, default, position));
    }

    private void TryBulkStep()
    {
        if (!_bulkAllowed) return;
        var grid = _navigation!.Grid;
        int dx = 0, dy = 0, members = 0;
        Array.Clear(_cohort, 0, Count);
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order]; ref var entity = ref _entities[i];
            if (!entity.GroupMember) continue;
            if (entity.YieldFor.Value != 0) { _bulkAllowed = false; return; }
            int x = entity.Target.X - entity.Position.X, y = entity.Target.Y - entity.Position.Y;
            if (members++ == 0) { dx = x; dy = y; }
            else if (x != dx || y != dy) { _bulkAllowed = false; return; }
            _cohort[i] = true;
        }
        if (members == 0 || dx == 0 && dy == 0) { _bulkAllowed = false; return; }
        // This is a temporary common velocity, not a maintained formation or a route-cell reservation.
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order]; if (!_cohort[i]) continue;
            ref var entity = ref _entities[i]; var next = StepTowards(entity.Position, entity.Target);
            if (!TerrainGeometry.Clear(grid, entity.Position, next, MotionIndex.BodyRadius) ||
                !_motion!.ClearMove(i, next, out _, _cohort)) { _bulkAllowed = false; return; }
        }
        GroupTranslating = true;
        for (int i = 0; i < Count; i++) if (_cohort[i])
        {
            ref var entity = ref _entities[i];
            entity.Position = StepTowards(entity.Position, entity.Target);
            entity.LastProgressTick = Tick;
            entity.Moving = entity.Position != entity.Target; entity.WaitTicks = 0;
            _motion!.SetEnd(i, entity.Position); _handled[i] = true;
        }
    }

    private void PrepareGroupRoutes(CancellationToken token)
    {
        if (_bulkAllowed) return;
        var grid = _navigation!.Grid;
        bool needed = false;
        for (int i = 0; i < Count; i++)
            if (_entities[i].GroupMember && _entities[i].Moving &&
                (!_groupRoutes.TryGetValue(_entities[i].Id, out var route) || route.Revision != grid.Revision)) { needed = true; break; }
        if (!needed) return;
        long started = Stopwatch.GetTimestamp();
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _entities[i];
            if (!entity.GroupMember || !entity.Moving || _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            if (!TerrainGeometry.Clear(grid, entity.Target, entity.Target, MotionIndex.BodyRadius)) { RejectRoute(ref entity); continue; }
            if (TerrainGeometry.Clear(grid, entity.Position, entity.Target, MotionIndex.BodyRadius))
                _groupRoutes[entity.Id] = new([entity.GoalCell], grid.Revision);
            else
            {
                if (_routeTree is null || !ReferenceEquals(_routeTree.Grid, grid))
                {
                    _routeTree = new(grid, _treeRoot, token);
                    GroupRouteBuilds++; GroupRouteVisitedCells += _routeTree.VisitedCells;
                }
                var route = _routeTree.Route(grid.CellAt(entity.Position), entity.GoalCell, token);
                if (route is null) RejectRoute(ref entity);
                else _groupRoutes[entity.Id] = new(route.Length == 0 ? [entity.GoalCell] : route, grid.Revision);
            }
        }
        GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private bool TryExchangeGoals(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entities[requesterIndex]; ref var blocker = ref _entities[blockerIndex];
        var grid = _navigation!.Grid;
        if (!requester.GroupMember || !blocker.GroupMember || blocker.YieldFor.Value != 0 || requester.WaitTicks < 16) return false;
        long oldCost = DistanceSquared(requester.Position, requester.Target) + DistanceSquared(blocker.Position, blocker.Target);
        long newCost = DistanceSquared(requester.Position, blocker.Target) + DistanceSquared(blocker.Position, requester.Target);
        if (newCost >= oldCost || !TerrainGeometry.Clear(grid, requester.Position, blocker.Target, MotionIndex.BodyRadius) ||
            !TerrainGeometry.Clear(grid, blocker.Position, requester.Target, MotionIndex.BodyRadius)) return false;
        var oldTarget = requester.Target;
        requester.Target = blocker.Target with { Elevation = requester.Position.Elevation };
        blocker.Target = oldTarget with { Elevation = blocker.Position.Elevation };
        (requester.GoalCell, blocker.GoalCell) = (blocker.GoalCell, requester.GoalCell);
        requester.WaitTicks = blocker.WaitTicks = 0;
        requester.PathLength = requester.PathCursor = blocker.PathLength = blocker.PathCursor = 0;
        requester.Moving = requester.Position != requester.Target; blocker.Moving = blocker.Position != blocker.Target;
        _groupRoutes.Remove(requester.Id); _groupRoutes.Remove(blocker.Id);
        NavigationGoalExchanges++;
        return true;
    }

    private void WriteGroupState(BinaryWriter writer)
    {
        writer.Write(MotionIndex.BodyRadius); writer.Write(_bulkAllowed);
        writer.Write(_treeRoot); writer.Write(_routeTree?.Grid.Revision ?? -1);
        foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
        {
            if (_groupRoutes.TryGetValue(entity.Id, out var route))
            {
                writer.Write(route.Revision); writer.Write(route.Cells.Length - route.Cursor);
                foreach (int cell in route.Cells.AsSpan(route.Cursor)) writer.Write(cell);
                writer.Write(route.HasWaypoint); writer.Write(route.Waypoint.X); writer.Write(route.Waypoint.Y);
            }
            else writer.Write(-1);
        }
    }
}
