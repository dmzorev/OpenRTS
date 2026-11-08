using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int LocalSearchBudget = 64;
    private readonly LocalMotionSearch _localSearch = new();
    private Span<WorldPosition> Path(int index) => _entityHandles[index].Get<NavigationAgentState>().LocalPath.AsSpan();
    public int NavigationLocalSearches { get; private set; }
    public int NavigationYields { get; private set; }

    private void UpdateNavigation(CancellationToken token)
    {
        var grid = _navigation!.Grid;
        if (_navigationGoals.Length < Count) Array.Resize(ref _navigationGoals, _entityHandles.Length);
        EnsureMovementOrder();
        if (_handled.Length < _entityHandles.Length)
        { Array.Resize(ref _handled, _entityHandles.Length); Array.Resize(ref _cohort, _entityHandles.Length); Array.Resize(ref _cohortIds, _entityHandles.Length); }
        Array.Clear(_handled, 0, Count);
        NavigationWaitingCount = NavigationLocalSearches = NavigationYields = NavigationGoalExchanges = NavigationQueueWaitingCount = 0;
        _navigation.ResetGroupMetrics();
        GroupRouteBuildMilliseconds = 0; GroupTranslating = false;
        TryBulkStep();
        if (_navigation.UsesWeightedRoutes) PrepareWeightedRoutes(token);
        else { PrepareGroupRoutes(token); PreparePointRoutes(token); }
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int index = _movementOrder[(order + (int)(Tick % Count)) % Count];
            ref var entity = ref _entityHandles[index];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || _handled[index]) continue;
            var entityIdentity = entity.Get<EntityIdentity>();
            var type = _types[entityIdentity.Type];
            WorldPosition waypoint;
            if (entityNavigation.YieldFor.Value != 0)
            {
                if (Tick >= entityNavigation.YieldDeadline)
                {
                    entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0;
                    entityNavigation.PathLength = entityNavigation.PathCursor = 0; entityNavigation.Route = null;
                    entityMotion.Moving = entitySpatial.Position != entityMotion.Target;
                    continue;
                }
                bool waiting = _indices.TryGetValue(entityNavigation.YieldFor, out int requester) && _entityHandles[requester].Get<MotionState>().Moving &&
                    DistanceSquared(entitySpatial.Position, _entityHandles[requester].Get<SpatialState>().Position) < 1536L * 1536 && Tick < entityNavigation.YieldDeadline;
                if (entitySpatial.Position == entityNavigation.LocalTarget)
                {
                    if (waiting) { NavigationWaitingCount++; continue; }
                    entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; entityNavigation.Route = null;
                    entityMotion.Moving = entitySpatial.Position != entityMotion.Target;
                    continue;
                }
                waypoint = entityNavigation.PathCursor < entityNavigation.PathLength ? Path(index)[entityNavigation.PathCursor] : entityNavigation.LocalTarget;
            }
            else
            {
                if (entitySpatial.Position == entityMotion.Target) { entityMotion.Moving = false; entityNavigation.WaitTicks = 0; continue; }
                if (!TryWaypoint(index, out waypoint)) { NavigationWaitingCount++; continue; }
                if (entityNavigation.PathCursor < entityNavigation.PathLength) waypoint = Path(index)[entityNavigation.PathCursor];
            }
            waypoint = GroundPosition(waypoint, entitySpatial.Position.Elevation);
            var next = TerrainStep(entitySpatial.Position, ref entityMotion, type, waypoint);
            if (next == entitySpatial.Position) continue;
            bool terrain = ClearTerrain(type, entitySpatial.Position, next);
            EntityId blockedBy = default;
            var body = terrain ? _motion!.Resolve(entityIdentity.Id) : default;
            if (terrain && _motion!.ClearMove(body, next, out blockedBy))
            {
                entitySpatial.Position = next; entityNavigation.LastProgressTick = Tick;
                _motion.SetEnd(body, next); entityNavigation.WaitTicks = 0;
                if (entityNavigation.YieldFor.Value == 0 && next == entityMotion.Target) entityMotion.Moving = false;
                if (next == waypoint && entityNavigation.PathCursor < entityNavigation.PathLength) entityNavigation.PathCursor++;
                if (entityNavigation.PathCursor == entityNavigation.PathLength && entityNavigation.PathLength > 0 && entityNavigation.Route is { } route)
                { entityNavigation.PathLength = entityNavigation.PathCursor = 0; route.HasWaypoint = false; }
                continue;
            }
            int blocker = blockedBy.Value != 0 && _indices.TryGetValue(blockedBy, out int blockedIndex) ? blockedIndex : -1;
            entityNavigation.WaitTicks = Math.Min(1024, entityNavigation.WaitTicks + 1);
            if (entityNavigation.YieldFor.Value == 0 && entityNavigation.WaitTicks >= 4 && entityNavigation.PathCursor == entityNavigation.PathLength &&
                entityNavigation.Route is { } blockedRoute)
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
            else if (entityNavigation.WaitTicks >= 4 && (Tick + entityIdentity.Id.Value) % 8 == 0 && NavigationLocalSearches < LocalSearchBudget)
            {
                NavigationLocalSearches++;
                var horizon = StepTowards(entitySpatial.Position, waypoint, 2048);
                entityNavigation.PathLength = _localSearch.Find(grid, _motion!, entityIdentity.Id, entitySpatial.Position, horizon, Path(index), locomotor: PhysicalProfile(index)); entityNavigation.PathCursor = 0;
                if (entityNavigation.PathLength == 0 && blocker >= 0 && NavigationLocalSearches < LocalSearchBudget) TryYield(index, blocker);
            }
            if (!terrain && entityNavigation.YieldFor.Value == 0)
            {
                // Steering can leave the original visible segment; rebuild from the actual position.
                entityNavigation.Route = null;
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
            ref var entity = ref _entityHandles[i];
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                entityNavigation.Route is { } existing && existing.Revision == grid.Revision) continue;
            if (!ClearTerrain(i, entityMotion.Target, entityMotion.Target)) { RejectRoute(ref entity); continue; }
            if (ClearTerrain(i, entity.Get<SpatialState>().Position, entityMotion.Target))
                entityNavigation.Route = new([entityMotion.Target], grid.Revision);
            else _navigationGoals[goals++] = entityNavigation.GoalCell;
        }
        _navigation.PreparePoints(_navigationGoals.AsSpan(0, goals), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ref var entity = ref _entityHandles[i];
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                entityNavigation.Route is { } existing && existing.Revision == grid.Revision) continue;
            int start = grid.CellAt(entity.Get<SpatialState>().Position);
            if (!_navigation.TryPointRoute(start, entityNavigation.GoalCell, entityMotion.Target, out var points, token)) continue;
            if (points is null) RejectRoute(ref entity);
            else entityNavigation.Route = new(points, grid.Revision);
        }
    }

    private bool TryWaypoint(int index, out WorldPosition waypoint)
    {
        ref var entity = ref _entityHandles[index];
        ref var entitySpatial = ref entity.Get<SpatialState>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>(); var grid = _navigation!.Grid;
        waypoint = default;
        if (entityNavigation.Route is not { } route || route.Revision != grid.Revision) return false;
        if (route.HasWaypoint && entitySpatial.Position != route.Waypoint) { waypoint = route.Waypoint; return true; }
        if (route.HasWaypoint) route.Cursor++;
        bool weighted = _navigation.UsesWeightedRoutes;
        if (weighted)
        {
            route.CostPrefix ??= new long[route.Points.Length];
            var previous = entitySpatial.Position; long cost = 0;
            for (int p = route.Cursor; p < route.Points.Length; p++)
            {
                var point = GroundPosition(route.Points[p], entitySpatial.Position.Elevation);
                long segment = TerrainCost.Segment(grid, PhysicalProfile(index)!, previous, point);
                cost = segment == long.MaxValue || cost > long.MaxValue - segment ? long.MaxValue : cost + segment;
                route.CostPrefix[p] = cost; previous = point;
            }
        }
        for (int i = route.Points.Length - 1; i >= route.Cursor; i--)
        {
            var target = GroundPosition(route.Points[i], entitySpatial.Position.Elevation);
            if (!ClearTerrain(index, entitySpatial.Position, target)) continue;
            if (weighted && TerrainCost.Segment(grid, PhysicalProfile(index)!, entitySpatial.Position, target) > route.CostPrefix![i]) continue;
            route.Cursor = i; route.Waypoint = waypoint = target; route.HasWaypoint = true; return true;
        }
        // A conservative start-cell center is also a useful recovery after a local detour.
        var center = GroundPosition(grid.Center(grid.CellAt(entitySpatial.Position)), entitySpatial.Position.Elevation);
        if (entitySpatial.Position != center && ClearTerrain(index, entitySpatial.Position, center))
        { route.Waypoint = waypoint = center; route.HasWaypoint = false; return true; }
        entityNavigation.Route = null;
        return false;
    }

    private bool IsFollowing(int requesterIndex, int blockerIndex)
    {
        ref var a = ref _entityHandles[requesterIndex];
        ref var aSpatial = ref a.Get<SpatialState>();
        ref var aMotion = ref a.Get<MotionState>();
        ref var b = ref _entityHandles[blockerIndex];
        ref var bSpatial = ref b.Get<SpatialState>();
        ref var bMotion = ref b.Get<MotionState>();
        ref var bNavigation = ref b.Get<NavigationAgentState>();
        if (!bMotion.Moving || bNavigation.YieldFor.Value != 0 || bSpatial.Position == bMotion.Target || Tick - bNavigation.LastProgressTick > 8) return false;
        long ax = (long)aMotion.Target.X - aSpatial.Position.X, ay = (long)aMotion.Target.Y - aSpatial.Position.Y;
        long bx = (long)bMotion.Target.X - bSpatial.Position.X, by = (long)bMotion.Target.Y - bSpatial.Position.Y;
        Int128 cross = (Int128)ax * by - (Int128)ay * bx;
        return ax * bx + ay * by > 0 && cross * cross * 16 <= ((Int128)ax * ax + (Int128)ay * ay) * ((Int128)bx * bx + (Int128)by * by) &&
            DistanceSquared(bSpatial.Position, bMotion.Target) < DistanceSquared(aSpatial.Position, bMotion.Target);
    }

    private void TryYield(int requesterIndex, int blockerIndex)
    {
        ref var requester = ref _entityHandles[requesterIndex];
        var requesterIdentity = requester.Get<EntityIdentity>();
        ref var requesterOwnership = ref requester.Get<OwnershipState>();
        ref var requesterNavigation = ref requester.Get<NavigationAgentState>();
        ref var requesterMotion = ref requester.Get<MotionState>();
        ref var blocker = ref _entityHandles[blockerIndex];
        ref var blockerSpatial = ref blocker.Get<SpatialState>();
        var blockerIdentity = blocker.Get<EntityIdentity>();
        ref var blockerOwnership = ref blocker.Get<OwnershipState>();
        ref var blockerNavigation = ref blocker.Get<NavigationAgentState>();
        ref var blockerMotion = ref blocker.Get<MotionState>();
        if (!_types[blockerIdentity.Type].Movable || UpgradeImmobilizes(blocker) || IsTransformationLocked(blocker) || requesterOwnership.Owner != blockerOwnership.Owner || blockerNavigation.YieldFor.Value != 0 || IsFollowing(requesterIndex, blockerIndex) ||
            !requesterNavigation.GroupMember && requesterMotion.Target == blockerSpatial.Position) return;
        if (blockerMotion.Moving && blockerIdentity.Id.Value < requesterIdentity.Id.Value) return;
        NavigationLocalSearches++;
        int length = _localSearch.Find(_navigation!.Grid, _motion!, blocker.Get<EntityIdentity>().Id, blockerSpatial.Position, blockerMotion.Target,
            Path(blockerIndex), requester.Get<SpatialState>().Position, PhysicalProfile(blockerIndex), _types[requesterIdentity.Type].Body);
        if (length == 0) return;
        blockerNavigation.YieldFor = requesterIdentity.Id; blockerNavigation.YieldDeadline = Tick + 128;
        blockerNavigation.PathLength = length; blockerNavigation.PathCursor = 0; blockerNavigation.LocalTarget = Path(blockerIndex)[length - 1];
        blockerMotion.Moving = true; blockerNavigation.WaitTicks = 0;
        blockerNavigation.Route = null; NavigationYields++;
    }

    private void RejectRoute(ref Frent.Entity entity)
    {
        var entityIdentity = entity.Get<EntityIdentity>();
        ref var entityMotion = ref entity.Get<MotionState>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        entityMotion.Moving = false; entityNavigation.GroupMember = false; entity.Get<NavigationAgentState>().Route = null;
        entityNavigation.WaitTicks = entityNavigation.PathLength = entityNavigation.PathCursor = 0;
        entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; entityNavigation.LocalTarget = default;
        _events.Add(new(Tick, WorldEventKind.NoRoute, entityIdentity.Id, entityMotion.Target, entityIdentity.Type));
    }

    private static long DistanceSquared(WorldPosition a, WorldPosition b)
    {
        long dx = (long)a.X - b.X, dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
