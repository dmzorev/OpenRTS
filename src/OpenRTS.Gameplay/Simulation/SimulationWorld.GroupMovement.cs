using System.Diagnostics;

using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private SharedRouteTree? _routeTree;
    private bool _bulkAllowed;
    private int _treeRoot;
    private bool[] _handled = [], _cohort = [];
    private EntityId[] _cohortIds = [];
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
        bool Eligible(int i) => _types[_entityHandles[i].Get<EntityIdentity>().Type].Movable && (action != UnitActionKind.AttackMove || OrderWeapon(i) is not null);
        foreach (int i in members) if (!Eligible(i)) _events.Add(new(Tick, WorldEventKind.InvalidOrder, _entityHandles[i].Get<EntityIdentity>().Id));
        members = members.Where(Eligible).ToArray();
        var grid = _navigation!.Grid;
        if (members.Length == 0) return;
        var assignments = members.Select(i => new GroupDestinations.Member(_entityHandles[i].Get<EntityIdentity>().Id, _entityHandles[i].Get<SpatialState>().Position, i)).ToArray();
        WorldPosition[]? slots;
        if (queued && members.Any(i => _entityHandles[i].Get<OrderState>().HasOrder))
        {
            int pitch = Math.Max(1024, members.Max(i => Math.Max(_types[_entityHandles[i].Get<EntityIdentity>().Type].Body.HalfWidth, _types[_entityHandles[i].Get<EntityIdentity>().Type].Body.HalfHeight)) * 2 + 2);
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
            var profile = _types[_entityHandles[i].Get<EntityIdentity>().Type].Locomotor; var resolver = Resolver(_entityHandles[i].Get<EntityIdentity>().Type);
            int component = resolver.ComponentAt(_entityHandles[i].Get<SpatialState>().Position, StepCancellation);
            if (component == 0 || components.TryGetValue(profile, out var prior) && prior.Component != component) { SplitOrReject(); return; }
            components[profile] = (resolver, component);
        }
        bool Valid(WorldPosition p) => components.Values.All(c => c.Resolver.CanStop(p) && c.Resolver.ComponentAt(p, StepCancellation) == c.Component);
        var anchor = Valid(position) ? position : DestinationResolver.Nearest(grid, position, Valid, StepCancellation);
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
            var parts = members.GroupBy(i => (_types[_entityHandles[i].Get<EntityIdentity>().Type].Locomotor.Id, Component: Resolver(_entityHandles[i].Get<EntityIdentity>().Type).Component(grid.CellAt(_entityHandles[i].Get<SpatialState>().Position), StepCancellation)))
                .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.Component).Select(g => g.ToArray()).ToArray();
            if (parts.Length == 1) { Reject(); return; }
            foreach (var part in parts) SetGroupTarget(position, part, action, queued);
        }
        void Reject() => _events.Add(new(Tick, WorldEventKind.NoRoute, default, position));
    }

    private void TryBulkStep()
    {
        if (!_bulkAllowed || _construction?.HasReservations == true) return;
        var grid = _navigation!.Grid;
        int dx = 0, dy = 0, members = 0;
        Array.Clear(_cohort, 0, Count);
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order]; ref var entity = ref _entityHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            if (!entityNavigation.GroupMember) continue;
            if (entityNavigation.YieldFor.Value != 0) { _bulkAllowed = false; return; }
            int x = entityMotion.Target.X - entitySpatial.Position.X, y = entityMotion.Target.Y - entitySpatial.Position.Y;
            if (members++ == 0) { dx = x; dy = y; }
            else if (x != dx || y != dy) { _bulkAllowed = false; return; }
            _cohort[i] = true; _cohortIds[members - 1] = entity.Get<EntityIdentity>().Id;
        }
        if (members == 0 || dx == 0 && dy == 0) { _bulkAllowed = false; return; }
        _motion!.SetCohort(_cohortIds.AsSpan(0, members));
        // This is a temporary common velocity, not a maintained formation or a route-cell reservation.
        for (int order = 0; order < Count; order++)
        {
            int i = _movementOrder[order]; if (!_cohort[i]) continue;
            ref var entity = ref _entityHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            var next = StepTowards(entitySpatial.Position, entityMotion.Target);
            if (!TerrainGeometry.Clear(grid, entitySpatial.Position, next, MotionIndex.BodyRadius) ||
                !_motion!.ClearMove(entity.Get<EntityIdentity>().Id, next, out _, ignoreCohort: true)) { _bulkAllowed = false; return; }
        }
        GroupTranslating = true;
        for (int i = 0; i < Count; i++) if (_cohort[i])
        {
            ref var entity = ref _entityHandles[i];
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var spatial = ref entity.Get<SpatialState>();
            spatial.Position = StepTowards(spatial.Position, entityMotion.Target);
            entityNavigation.LastProgressTick = Tick;
            entityMotion.Moving = spatial.Position != entityMotion.Target; entityNavigation.WaitTicks = 0;
            _motion!.SetEnd(entity.Get<EntityIdentity>().Id, spatial.Position); _handled[i] = true;
        }
    }

    private void PrepareGroupRoutes(CancellationToken token)
    {
        if (_bulkAllowed) return;
        var grid = _navigation!.Grid;
        bool needed = false;
        for (int i = 0; i < Count; i++)
        {
            ref var agent = ref _entityHandles[i].Get<NavigationAgentState>();
            if (agent.GroupMember && _entityHandles[i].Get<MotionState>().Moving &&
                (agent.Route is not { } route || route.Revision != grid.Revision)) { needed = true; break; }
        }
        if (!needed) return;
        long started = Stopwatch.GetTimestamp();
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _entityHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            if (!entityNavigation.GroupMember || !entityMotion.Moving || entityNavigation.Route is { } existing && existing.Revision == grid.Revision) continue;
            if (!TerrainGeometry.Clear(grid, entityMotion.Target, entityMotion.Target, MotionIndex.BodyRadius)) { RejectRoute(ref entity); continue; }
            if (TerrainGeometry.Clear(grid, entitySpatial.Position, entityMotion.Target, MotionIndex.BodyRadius))
                entityNavigation.Route = new([entityMotion.Target], grid.Revision);
            else
            {
                if (_routeTree is null || !ReferenceEquals(_routeTree.Grid, grid))
                {
                    _routeTree = new(grid, _treeRoot, token);
                    GroupRouteBuilds++; GroupRouteVisitedCells += _routeTree.VisitedCells;
                }
                var route = _routeTree.Route(grid.CellAt(entitySpatial.Position), entityNavigation.GoalCell, token);
                if (route is null) RejectRoute(ref entity);
                else entityNavigation.Route = new(RoutePoints(route, grid, entityMotion.Target), grid.Revision);
            }
        }
        GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private bool TryExchangeGoals(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entityHandles[requesterIndex];
        ref var requesterSpatial = ref requester.Get<SpatialState>();
        var requesterIdentity = requester.Get<EntityIdentity>();
        ref var requesterOwnership = ref requester.Get<OwnershipState>();
        ref var requesterNavigation = ref requester.Get<NavigationAgentState>();
        ref var requesterMotion = ref requester.Get<MotionState>();
        ref var requesterOrders = ref requester.Get<OrderState>();
        ref var blocker = ref _entityHandles[blockerIndex];
        ref var blockerSpatial = ref blocker.Get<SpatialState>();
        var blockerIdentity = blocker.Get<EntityIdentity>();
        ref var blockerOwnership = ref blocker.Get<OwnershipState>();
        ref var blockerNavigation = ref blocker.Get<NavigationAgentState>();
        ref var blockerMotion = ref blocker.Get<MotionState>();
        ref var blockerOrders = ref blocker.Get<OrderState>();
        var grid = _navigation!.Grid;
        if (!requesterNavigation.GroupMember || !blockerNavigation.GroupMember || requesterOwnership.Owner != blockerOwnership.Owner || requesterNavigation.OrderGroup != blockerNavigation.OrderGroup || blockerNavigation.YieldFor.Value != 0 || requesterNavigation.WaitTicks < 16) return false;
        long oldCost = DistanceSquared(requesterSpatial.Position, requesterMotion.Target) + DistanceSquared(blockerSpatial.Position, blockerMotion.Target);
        long newCost = DistanceSquared(requesterSpatial.Position, blockerMotion.Target) + DistanceSquared(blockerSpatial.Position, requesterMotion.Target);
        if (newCost >= oldCost || !StopAllowed(requesterIdentity.Type, blockerMotion.Target) || !ClearTerrain(requesterIndex, requesterSpatial.Position, blockerMotion.Target) ||
            !StopAllowed(blockerIdentity.Type, requesterMotion.Target) || !ClearTerrain(blockerIndex, blockerSpatial.Position, requesterMotion.Target)) return false;
        var oldTarget = requesterMotion.Target;
        requesterMotion.Target = blockerMotion.Target with { Elevation = requesterSpatial.Position.Elevation };
        blockerMotion.Target = oldTarget with { Elevation = blockerSpatial.Position.Elevation };
        (requesterNavigation.GoalCell, blockerNavigation.GoalCell) = (blockerNavigation.GoalCell, requesterNavigation.GoalCell);
        requesterOrders.ActiveOrder = requesterOrders.ActiveOrder with { Position = requesterMotion.Target };
        blockerOrders.ActiveOrder = blockerOrders.ActiveOrder with { Position = blockerMotion.Target };
        requesterNavigation.WaitTicks = blockerNavigation.WaitTicks = 0;
        requesterNavigation.PathLength = requesterNavigation.PathCursor = blockerNavigation.PathLength = blockerNavigation.PathCursor = 0;
        requesterMotion.Moving = requesterSpatial.Position != requesterMotion.Target; blockerMotion.Moving = blockerSpatial.Position != blockerMotion.Target;
        requesterNavigation.Route = null; blockerNavigation.Route = null;
        NavigationGoalExchanges++;
        return true;
    }

    private void WriteGroupState(BinaryWriter writer)
    {
        writer.Write(MotionIndex.BodyRadius); writer.Write(_bulkAllowed);
        writer.Write(_treeRoot); writer.Write(_routeTree?.Grid.Revision ?? -1);
        writer.Write(_weightedTrees.Count);
        foreach (var pair in _weightedTrees.OrderBy(p => p.Key)) { writer.Write(pair.Key); writer.Write(pair.Value.Grid.Revision); }
        foreach (var entity in _entityHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<EntityIdentity>().Id.Value))
        {
            if (entity.Get<NavigationAgentState>().Route is { } route)
            {
                writer.Write(route.Revision); writer.Write(route.Points.Length - route.Cursor);
                foreach (var point in route.Points.AsSpan(route.Cursor)) { writer.Write(point.X); writer.Write(point.Y); }
                writer.Write(route.HasWaypoint); writer.Write(route.Waypoint.X); writer.Write(route.Waypoint.Y);
            }
            else writer.Write(-1);
        }
    }
}
