using OpenRTS.Core.Simulation;
using System.Diagnostics;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private RouteRequest[] _weightedRequests = [];

    private DestinationResolver Resolver(int unitType) => _navigation!.Resolver(unitType, _types[unitType].Locomotor);

    private Locomotor? PhysicalProfile(int index) => _navigation?.UsesWeightedRoutes != true ? null : _types[_entityHandles[index].Get<EntityIdentity>().Type].Locomotor;
    private bool ClearTerrain(int index, WorldPosition from, WorldPosition to) =>
        ClearTerrain(_types[_entityHandles[index].Get<EntityIdentity>().Type], from, to);
    private bool ClearTerrain(UnitDefinition type, WorldPosition from, WorldPosition to) =>
        TerrainGeometry.Clear(_navigation!.Grid, from, to, type.Body, _navigation.UsesWeightedRoutes ? type.Locomotor : null) &&
        _construction?.SiteMovementClear(from, to, type.Body) != false;
    private bool StopAllowed(int unitType, WorldPosition position) =>
        _types[unitType].Flight is not null || _construction?.SiteStopClear(position, _types[unitType].Body) != false &&
        (_navigation is null ? _types[unitType].Locomotor.Rules[0].CanStop :
            TerrainGeometry.CanStop(_navigation.Grid, position, _types[unitType].Body, _types[unitType].Locomotor));

    private WorldPosition GroundPosition(WorldPosition point, int fallback) => point with { Elevation = _navigation?.Grid.HasSurface == true ? _navigation.Grid.SurfaceAt(_navigation.Grid.CellAt(point)).Elevation : fallback };

    private WorldPosition TerrainStep(WorldPosition position, ref MotionState motion, UnitDefinition type, WorldPosition target)
    {
        if (!_navigation!.UsesWeightedRoutes) return StepTowards(position, target);
        var grid = _navigation.Grid;
        int factor = type.Locomotor.At(grid, grid.CellAt(position)).Speed;
        int budget = SpeedPerTick * factor + motion.MovementRemainder;
        motion.MovementRemainder = budget % 1024;
        var result = budget < 1024 ? position : StepTowards(position, target, budget / 1024);
        return grid.HasSurface ? result with { Elevation = grid.SurfaceAt(grid.CellAt(result)).Elevation } : result;
    }

    private void PrepareWeightedRoutes(CancellationToken token)
    {
        var navigation = _navigation!;
        var grid = navigation.Grid;
        if (_weightedRequests.Length < Count) Array.Resize(ref _weightedRequests, _entityHandles.Length);
        int requests = 0;
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _entityHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<EntityIdentity>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.YieldFor.Value != 0 || entityNavigation.Route is { } old && old.Revision == grid.Revision) continue;
            int profile = navigation.Profile(entityIdentity.Type);
            var resolver = Resolver(entityIdentity.Type);
            if (!StopAllowed(entityIdentity.Type, entityMotion.Target)) { RejectRoute(ref entity); continue; }
            var locomotor = _types[entityIdentity.Type].Locomotor;
            long dx = (long)entityMotion.Target.X - entitySpatial.Position.X, dy = (long)entityMotion.Target.Y - entitySpatial.Position.Y;
            long length = (long)SquareRoot((ulong)(dx * dx + dy * dy)); if (length * length < dx * dx + dy * dy) length++;
            int cheapest = locomotor.MinimumCost;
            if (ClearTerrain(i, entitySpatial.Position, entityMotion.Target) &&
                TerrainCost.Segment(grid, locomotor, entitySpatial.Position, entityMotion.Target) <= length * cheapest * 2)
            { entityNavigation.Route = new([entityMotion.Target], grid.Revision); continue; }
            // An exact clear segment proves reachability without constructing a full-map clearance mask.
            int start = resolver.NodeAt(entitySpatial.Position, token), goal = resolver.NodeAt(entityMotion.Target, token);
            if (start < 0 || goal < 0 || !navigation.CanReach(profile, start, goal, token))
            { RejectRoute(ref entity); continue; }
            if (entityNavigation.GroupMember)
            {
                long before = Stopwatch.GetTimestamp();
                var points = navigation.WeightedGroupRoute(profile, resolver, locomotor, start, goal, entityMotion.Target, token);
                if (points is null) RejectRoute(ref entity);
                else entityNavigation.Route = new(points, grid.Revision);
                GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            else _weightedRequests[requests++] = new(profile, goal);
        }
        navigation.PrepareWeighted(_weightedRequests.AsSpan(0, requests), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested(); ref var entity = ref _entityHandles[i];
            var entityIdentity = entity.Get<EntityIdentity>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                entityNavigation.Route is { } old && old.Revision == grid.Revision) continue;
            int profile = navigation.Profile(entityIdentity.Type);
            var resolver = Resolver(entityIdentity.Type);
            int goal = resolver.NodeAt(entityMotion.Target, token), start = resolver.NodeAt(entity.Get<SpatialState>().Position, token);
            if (goal < 0 || start < 0) { RejectRoute(ref entity); continue; }
            var key = new RouteRequest(profile, goal);
            if (!navigation.TryWeightedRoute(key, start, entityMotion.Target, out var points, token)) continue;
            if (points is null) RejectRoute(ref entity);
            else entityNavigation.Route = new(points, grid.Revision);
        }
    }
}
