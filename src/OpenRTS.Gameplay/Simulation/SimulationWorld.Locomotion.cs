using OpenRTS.Core.Simulation;
using System.Diagnostics;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private WeightedNavigator? _weighted;
    private bool _useClearance;
    private DestinationResolver? _plainResolver;
    private DestinationResolver Resolver(int unitType)
    {
        var grid = _navigation!.Grid;
        if (_weighted is not null) { _weighted.SetGrid(grid); return _weighted.Resolver(_profileIndices[unitType]); }
        if (_plainResolver is null || !ReferenceEquals(_plainResolver.Grid, grid)) _plainResolver = new(grid, _types[unitType].Locomotor);
        return _plainResolver;
    }
    private int[] _profileIndices = [];
    private RouteRequest[] _weightedRequests = [];
    private readonly Dictionary<int, SharedRouteTree> _weightedTrees = new();

    private void InitializeLocomotion(NavigationGrid grid, int workers, int budget, int cache, bool reverse)
    {
        int maximum = Math.Max(grid.MaximumTerrain, _types.SelectMany(t => t.Footprint?.Cells ?? []).Max(c => (int?)c.Navigation.Terrain) ?? 0);
        _useClearance = grid.HasGeometry || grid.CellSize < 514 || _types.Any(t => t.Footprint is null && (t.Body != BodyShape.Default || t.Locomotor.MaximumStep != int.MaxValue || t.Locomotor.MaximumSlope != int.MaxValue || t.Locomotor.MaximumDepth != int.MaxValue));
        foreach (var type in _types)
            if (type.Footprint is null && type.Locomotor.Rules.Count <= maximum) throw new ArgumentException("Unit locomotor does not define every terrain in this grid.");
        if (!_useClearance && maximum == 0 && !grid.HasTerrain && _types.All(t => t.Locomotor.Rules[0] == TerrainRule.Ground)) return;
        var profiles = _types.Where(t => t.Footprint is null).Select(t => t.Locomotor).GroupBy(p => p.NavigationKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var first = g.First();
                if (g.Any(p => !p.Rules.SequenceEqual(first.Rules) || p.MaximumDepth != first.MaximumDepth || p.MaximumSlope != first.MaximumSlope || p.MaximumStep != first.MaximumStep)) throw new ArgumentException("Conflicting locomotors share an ID.");
                return first;
            }).ToArray();
        if (profiles.Length == 0) return;
        _profileIndices = _types.Select(t => Math.Max(0, Array.FindIndex(profiles, p => p.NavigationKey == t.Locomotor.NavigationKey))).ToArray();
        _weighted = new(grid, profiles, workers, budget, cache, reverse, _useClearance);
    }
    private Locomotor? PhysicalProfile(int index) => _weighted is null ? null : _types[_unitHandles[index].Get<UnitIdentity>().UnitType].Locomotor;
    private bool ClearTerrain(int index, WorldPosition from, WorldPosition to) =>
        ClearTerrain(_types[_unitHandles[index].Get<UnitIdentity>().UnitType], from, to);
    private bool ClearTerrain(UnitDefinition type, WorldPosition from, WorldPosition to) =>
        TerrainGeometry.Clear(_navigation!.Grid, from, to, type.Body, _weighted is null ? null : type.Locomotor) && SiteMovementClear(from, to, type.Body);
    private bool StopAllowed(int unitType, WorldPosition position) =>
        SiteStopClear(position, _types[unitType].Body) &&
        (_navigation is null ? _types[unitType].Locomotor.Rules[0].CanStop :
            TerrainGeometry.CanStop(_navigation.Grid, position, _types[unitType].Body, _types[unitType].Locomotor));

    private WorldPosition GroundPosition(WorldPosition point, int fallback) => point with { Elevation = _navigation?.Grid.HasSurface == true ? _navigation.Grid.SurfaceAt(_navigation.Grid.CellAt(point)).Elevation : fallback };

    private WorldPosition TerrainStep(WorldPosition position, ref MotionState motion, UnitDefinition type, WorldPosition target)
    {
        if (_weighted is null) return StepTowards(position, target);
        int factor = type.Locomotor.At(_navigation!.Grid, _navigation.Grid.CellAt(position)).Speed;
        int budget = SpeedPerTick * factor + motion.MovementRemainder;
        motion.MovementRemainder = budget % 1024;
        var result = budget < 1024 ? position : StepTowards(position, target, budget / 1024);
        return _navigation.Grid.HasSurface ? result with { Elevation = _navigation.Grid.SurfaceAt(_navigation.Grid.CellAt(result)).Elevation } : result;
    }

    private void PrepareWeightedRoutes(CancellationToken token)
    {
        var grid = _navigation!.Grid; var navigation = _weighted!;
        navigation.SetGrid(grid);
        if (_weightedRequests.Length < Count) Array.Resize(ref _weightedRequests, _unitHandles.Length);
        int requests = 0;
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _unitHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.YieldFor.Value != 0 || entityNavigation.Route is { } old && old.Revision == grid.Revision) continue;
            int profile = _profileIndices[entityIdentity.UnitType]; var resolver = navigation.Resolver(profile);
            if (!StopAllowed(entityIdentity.UnitType, entityMotion.Target)) { RejectRoute(ref entity); continue; }
            var locomotor = _types[entityIdentity.UnitType].Locomotor;
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
                if (!_weightedTrees.TryGetValue(profile, out var tree) || !ReferenceEquals(tree.Grid, grid))
                {
                    tree = new(grid, resolver.NodeAt(grid.Center(_treeRoot), token) is var root && root >= 0 ? root : start, token, locomotor, navigation.Geometry(profile)); _weightedTrees[profile] = tree;
                    GroupRouteBuilds++; GroupRouteVisitedCells += tree.VisitedCells;
                }
                var turns = tree.Route(start, goal, token);
                if (turns is null) RejectRoute(ref entity);
                else entityNavigation.Route = new(WeightedPoints(turns, entityMotion.Target), grid.Revision);
                GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            else _weightedRequests[requests++] = new(profile, goal);
        }
        navigation.Prepare(_weightedRequests.AsSpan(0, requests), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested(); ref var entity = ref _unitHandles[i];
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            if (!entityMotion.Moving || entityNavigation.GroupMember || entityNavigation.YieldFor.Value != 0 ||
                entityNavigation.Route is { } old && old.Revision == grid.Revision) continue;
            int profile = _profileIndices[entityIdentity.UnitType]; var resolver = navigation.Resolver(profile);
            int goal = resolver.NodeAt(entityMotion.Target, token), start = resolver.NodeAt(entity.Get<SpatialState>().Position, token);
            if (goal < 0 || start < 0) { RejectRoute(ref entity); continue; }
            var key = new RouteRequest(profile, goal);
            if (!navigation.TryCost(key, start, out long cost)) continue;
            if (cost == long.MaxValue) RejectRoute(ref entity);
            else entityNavigation.Route = new(WeightedPoints(navigation.Path(key, start, token), entityMotion.Target), grid.Revision);
        }
    }
}
