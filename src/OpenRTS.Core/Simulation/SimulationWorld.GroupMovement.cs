using System.Diagnostics;

using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private sealed class GroupRoute(WorldPosition[] points, int revision)
    {
        public readonly WorldPosition[] Points = points;
        public readonly int Revision = revision;
        public int Cursor;
        public WorldPosition Waypoint;
        public bool HasWaypoint;
        public long[]? CostPrefix;
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

    private static WorldPosition[] RoutePoints(int[] cells, NavigationGrid grid, WorldPosition goal)
    {
        if (cells.Length == 0) return [goal];
        var points = cells.Select(c => grid.Center(c, goal.Elevation)).ToArray(); points[^1] = goal; return points;
    }
    private WorldPosition[] WeightedPoints(int[] cells, WorldPosition goal)
    {
        if (cells.Length == 0) return [goal];
        var points = cells.Select(c => _weighted!.Point(c) with { Elevation = goal.Elevation }).ToArray(); points[^1] = goal; return points;
    }
    private void SetGroupTarget(WorldPosition position, int[] members, UnitActionKind action = UnitActionKind.Move, bool queued = false)
    {
        bool Eligible(int i) => _types[_entities[i].UnitType].Movable && (action != UnitActionKind.AttackMove || OrderWeapon(i) is not null);
        foreach (int i in members) if (!Eligible(i)) _events.Add(new(Tick, WorldEventKind.InvalidOrder, _entities[i].Id));
        members = members.Where(Eligible).ToArray();
        var grid = _navigation!.Grid;
        if (members.Length == 0) return;
        var assignments = members.Select(i => new GroupDestinations.Member(_entities[i].Id, _entities[i].Position, i)).ToArray();
        WorldPosition[]? slots;
        if (queued && members.Any(i => _entities[i].Orders.HasOrder))
        {
            int pitch = Math.Max(1024, members.Max(i => Math.Max(_types[_entities[i].UnitType].Body.HalfWidth, _types[_entities[i].UnitType].Body.HalfHeight)) * 2 + 2);
            int columns = (int)Math.Ceiling(Math.Sqrt(members.Length));
            slots = Enumerable.Range(0, members.Length).Select(i => new WorldPosition(
                Math.Clamp(position.X + (i % columns - (columns - 1) / 2) * pitch, 0, Width),
                Math.Clamp(position.Y + (i / columns - ((members.Length + columns - 1) / columns - 1) / 2) * pitch, 0, Depth))).ToArray();
            long cohort = checked(++_orderGroupSequence);
            GroupDestinations.Assign(assignments, slots, (index, goal) => IssueOrder(index, new(action, goal, Group: true, Cohort: cohort), true));
            return;
        }
        var components = new Dictionary<Locomotor, (DestinationResolver Resolver, int Component)>();
        foreach (int i in members)
        {
            var profile = _types[_entities[i].UnitType].Locomotor; var resolver = Resolver(_entities[i].UnitType);
            int component = resolver.ComponentAt(_entities[i].Position, _stepCancellation);
            if (component == 0 || components.TryGetValue(profile, out var prior) && prior.Component != component) { SplitOrReject(); return; }
            components[profile] = (resolver, component);
        }
        bool Valid(WorldPosition p) => components.Values.All(c => c.Resolver.CanStop(p) && c.Resolver.ComponentAt(p, _stepCancellation) == c.Component);
        var anchor = Valid(position) ? position : DestinationResolver.Nearest(grid, position, Valid, _stepCancellation);
        if (anchor is not { } destination) { SplitOrReject(); return; }
        slots = GroupDestinations.Slots(grid, destination, members.Length, components.Keys.ToArray(), Valid);
        if (slots is null) { Reject(); return; }
        if (destination != position) _events.Add(new(Tick, WorldEventKind.DestinationAdjusted, default, destination));
        _routeTree = null; _weightedTrees.Clear(); _bulkAllowed = _weighted is null;
        _treeRoot = grid.CellAt(slots.OrderBy(point => DistanceSquared(point, destination)).ThenBy(point => point.Y).ThenBy(point => point.X).First());
        long group = checked(++_orderGroupSequence);
        GroupDestinations.Assign(assignments, slots, (index, goal) => IssueOrder(index, new(action, goal, Group: true, Cohort: group), queued));
        Array.Sort(slots, (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)); _groupSlots = slots;
        void SplitOrReject()
        {
            var parts = members.GroupBy(i => (_types[_entities[i].UnitType].Locomotor.Id, Component: Resolver(_entities[i].UnitType).Component(grid.CellAt(_entities[i].Position), _stepCancellation)))
                .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.Component).Select(g => g.ToArray()).ToArray();
            if (parts.Length == 1) { Reject(); return; }
            foreach (var part in parts) SetGroupTarget(position, part, action, queued);
        }
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
            ref var entityNavigation = ref entity.Navigation;
            ref var entityMotion = ref entity.Motion;
            if (!entityNavigation.GroupMember) continue;
            if (entityNavigation.YieldFor.Value != 0) { _bulkAllowed = false; return; }
            int x = entityMotion.Target.X - entity.Position.X, y = entityMotion.Target.Y - entity.Position.Y;
            if (members++ == 0) { dx = x; dy = y; }
            else if (x != dx || y != dy) { _bulkAllowed = false; return; }
            _cohort[i] = true;
        }
        if (members == 0 || dx == 0 && dy == 0) { _bulkAllowed = false; return; }
        // This is a temporary common velocity, not a maintained formation or a route-cell reservation.
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order]; if (!_cohort[i]) continue;
            ref var entity = ref _entities[i];
            ref var entityMotion = ref entity.Motion;
            var next = StepTowards(entity.Position, entityMotion.Target);
            if (!TerrainGeometry.Clear(grid, entity.Position, next, MotionIndex.BodyRadius) ||
                !_motion!.ClearMove(i, next, out _, _cohort)) { _bulkAllowed = false; return; }
        }
        GroupTranslating = true;
        for (int i = 0; i < Count; i++) if (_cohort[i])
        {
            ref var entity = ref _entities[i];
            ref var entityMotion = ref entity.Motion;
            ref var entityNavigation = ref entity.Navigation;
            ref var spatial = ref entity.StorageEntity.Get<SpatialState>();
            spatial.Position = StepTowards(spatial.Position, entityMotion.Target);
            entityNavigation.LastProgressTick = Tick;
            entityMotion.Moving = spatial.Position != entityMotion.Target; entityNavigation.WaitTicks = 0;
            _motion!.SetEnd(i, spatial.Position); _handled[i] = true;
        }
    }

    private void PrepareGroupRoutes(CancellationToken token)
    {
        if (_bulkAllowed) return;
        var grid = _navigation!.Grid;
        bool needed = false;
        for (int i = 0; i < Count; i++)
            if (_entities[i].Navigation.GroupMember && _entities[i].Motion.Moving &&
                (!_groupRoutes.TryGetValue(_entities[i].Id, out var route) || route.Revision != grid.Revision)) { needed = true; break; }
        if (!needed) return;
        long started = Stopwatch.GetTimestamp();
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _entities[i];
            ref var entityNavigation = ref entity.Navigation;
            ref var entityMotion = ref entity.Motion;
            if (!entityNavigation.GroupMember || !entityMotion.Moving || _groupRoutes.TryGetValue(entity.Id, out var existing) && existing.Revision == grid.Revision) continue;
            if (!TerrainGeometry.Clear(grid, entityMotion.Target, entityMotion.Target, MotionIndex.BodyRadius)) { RejectRoute(ref entity); continue; }
            if (TerrainGeometry.Clear(grid, entity.Position, entityMotion.Target, MotionIndex.BodyRadius))
                _groupRoutes[entity.Id] = new([entityMotion.Target], grid.Revision);
            else
            {
                if (_routeTree is null || !ReferenceEquals(_routeTree.Grid, grid))
                {
                    _routeTree = new(grid, _treeRoot, token);
                    GroupRouteBuilds++; GroupRouteVisitedCells += _routeTree.VisitedCells;
                }
                var route = _routeTree.Route(grid.CellAt(entity.Position), entityNavigation.GoalCell, token);
                if (route is null) RejectRoute(ref entity);
                else _groupRoutes[entity.Id] = new(RoutePoints(route, grid, entityMotion.Target), grid.Revision);
            }
        }
        GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private bool TryExchangeGoals(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entities[requesterIndex];
        ref var requesterNavigation = ref requester.Navigation;
        ref var requesterMotion = ref requester.Motion;
        ref var requesterOrders = ref requester.Orders;
        ref var blocker = ref _entities[blockerIndex];
        ref var blockerNavigation = ref blocker.Navigation;
        ref var blockerMotion = ref blocker.Motion;
        ref var blockerOrders = ref blocker.Orders;
        var grid = _navigation!.Grid;
        if (!requesterNavigation.GroupMember || !blockerNavigation.GroupMember || requester.Owner != blocker.Owner || requesterNavigation.OrderGroup != blockerNavigation.OrderGroup || blockerNavigation.YieldFor.Value != 0 || requesterNavigation.WaitTicks < 16) return false;
        long oldCost = DistanceSquared(requester.Position, requesterMotion.Target) + DistanceSquared(blocker.Position, blockerMotion.Target);
        long newCost = DistanceSquared(requester.Position, blockerMotion.Target) + DistanceSquared(blocker.Position, requesterMotion.Target);
        if (newCost >= oldCost || !StopAllowed(requester.UnitType, blockerMotion.Target) || !ClearTerrain(requesterIndex, requester.Position, blockerMotion.Target) ||
            !StopAllowed(blocker.UnitType, requesterMotion.Target) || !ClearTerrain(blockerIndex, blocker.Position, requesterMotion.Target)) return false;
        var oldTarget = requesterMotion.Target;
        requesterMotion.Target = blockerMotion.Target with { Elevation = requester.Position.Elevation };
        blockerMotion.Target = oldTarget with { Elevation = blocker.Position.Elevation };
        (requesterNavigation.GoalCell, blockerNavigation.GoalCell) = (blockerNavigation.GoalCell, requesterNavigation.GoalCell);
        requesterOrders.ActiveOrder = requesterOrders.ActiveOrder with { Position = requesterMotion.Target };
        blockerOrders.ActiveOrder = blockerOrders.ActiveOrder with { Position = blockerMotion.Target };
        requesterNavigation.WaitTicks = blockerNavigation.WaitTicks = 0;
        requesterNavigation.PathLength = requesterNavigation.PathCursor = blockerNavigation.PathLength = blockerNavigation.PathCursor = 0;
        requesterMotion.Moving = requester.Position != requesterMotion.Target; blockerMotion.Moving = blocker.Position != blockerMotion.Target;
        _groupRoutes.Remove(requester.Id); _groupRoutes.Remove(blocker.Id);
        NavigationGoalExchanges++;
        return true;
    }

    private void WriteGroupState(BinaryWriter writer)
    {
        writer.Write(MotionIndex.BodyRadius); writer.Write(_bulkAllowed);
        writer.Write(_treeRoot); writer.Write(_routeTree?.Grid.Revision ?? -1);
        writer.Write(_weightedTrees.Count);
        foreach (var pair in _weightedTrees.OrderBy(p => p.Key)) { writer.Write(pair.Key); writer.Write(pair.Value.Grid.Revision); }
        foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
        {
            if (_groupRoutes.TryGetValue(entity.Id, out var route))
            {
                writer.Write(route.Revision); writer.Write(route.Points.Length - route.Cursor);
                foreach (var point in route.Points.AsSpan(route.Cursor)) { writer.Write(point.X); writer.Write(point.Y); }
                writer.Write(route.HasWaypoint); writer.Write(route.Waypoint.X); writer.Write(route.Waypoint.Y);
            }
            else writer.Write(-1);
        }
    }
}
