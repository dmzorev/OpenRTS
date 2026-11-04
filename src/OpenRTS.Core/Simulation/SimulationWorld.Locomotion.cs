using System.Diagnostics;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private WeightedNavigator? _weighted;
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
        foreach (var type in _types)
            if (type.Locomotor.Rules.Count <= grid.MaximumTerrain) throw new ArgumentException("Unit locomotor does not define every terrain in this grid.");
        if (!grid.HasTerrain && _types.All(t => t.Locomotor.Rules[0] == TerrainRule.Ground)) return;
        var profiles = _types.Select(t => t.Locomotor).GroupBy(p => p.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var first = g.First();
                if (g.Any(p => !p.Rules.SequenceEqual(first.Rules))) throw new ArgumentException("Conflicting locomotors share an ID.");
                return first;
            }).ToArray();
        _profileIndices = _types.Select(t => Array.FindIndex(profiles, p => p.Id == t.Locomotor.Id)).ToArray();
        _weighted = new(grid, profiles, workers, budget, cache, reverse);
    }
    private Locomotor? PhysicalProfile(int index) => _weighted is null ? null : _types[_entities[index].UnitType].Locomotor;
    private bool ClearTerrain(int index, WorldPosition from, WorldPosition to) =>
        TerrainGeometry.Clear(_navigation!.Grid, from, to, MotionIndex.BodyRadius, PhysicalProfile(index));
    private bool StopAllowed(int unitType, WorldPosition position) => _navigation is null ? _types[unitType].Locomotor.Rules[0].CanStop :
        _types[unitType].Locomotor.At(_navigation.Grid, _navigation.Grid.CellAt(position)).CanStop &&
        TerrainGeometry.Clear(_navigation.Grid, position, position, MotionIndex.BodyRadius,
            _weighted is null ? null : _types[unitType].Locomotor);

    private WorldPosition TerrainStep(int index, WorldPosition target)
    {
        ref var entity = ref _entities[index];
        if (_weighted is null) return StepTowards(entity.Position, target);
        int factor = _types[entity.UnitType].Locomotor.At(_navigation!.Grid, _navigation.Grid.CellAt(entity.Position)).Speed;
        int budget = SpeedPerTick * factor + entity.MovementRemainder;
        entity.MovementRemainder = budget % 1024;
        return budget < 1024 ? entity.Position : StepTowards(entity.Position, target, budget / 1024);
    }

    private void PrepareWeightedRoutes(CancellationToken token)
    {
        var grid = _navigation!.Grid; var navigation = _weighted!;
        navigation.SetGrid(grid);
        if (_weightedRequests.Length < Count) Array.Resize(ref _weightedRequests, _entities.Length);
        int requests = 0;
        for (int order = 0; order < Count; order++)
        {
            token.ThrowIfCancellationRequested();
            int i = _movementOrder[order]; ref var entity = ref _entities[i];
            if (!entity.Moving || entity.YieldFor.Value != 0 || _groupRoutes.TryGetValue(entity.Id, out var old) && old.Revision == grid.Revision) continue;
            int profile = _profileIndices[entity.UnitType], start = grid.CellAt(entity.Position);
            if (!StopAllowed(entity.UnitType, entity.Target) || !navigation.CanReach(profile, start, entity.GoalCell, token))
            { RejectRoute(ref entity); continue; }
            var locomotor = _types[entity.UnitType].Locomotor;
            long dx = (long)entity.Target.X - entity.Position.X, dy = (long)entity.Target.Y - entity.Position.Y;
            long length = (long)SquareRoot((ulong)(dx * dx + dy * dy)); if (length * length < dx * dx + dy * dy) length++;
            int cheapest = locomotor.MinimumCost;
            if (ClearTerrain(i, entity.Position, entity.Target) &&
                TerrainCost.Segment(grid, locomotor, entity.Position, entity.Target) <= length * cheapest * 2)
            { _groupRoutes[entity.Id] = new([entity.GoalCell], grid.Revision); continue; }
            if (entity.GroupMember)
            {
                long before = Stopwatch.GetTimestamp();
                if (!_weightedTrees.TryGetValue(profile, out var tree) || !ReferenceEquals(tree.Grid, grid))
                {
                    tree = new(grid, _treeRoot, token, locomotor); _weightedTrees[profile] = tree;
                    GroupRouteBuilds++; GroupRouteVisitedCells += tree.VisitedCells;
                }
                var turns = tree.Route(start, entity.GoalCell, token);
                if (turns is null) RejectRoute(ref entity);
                else _groupRoutes[entity.Id] = new(turns.Length == 0 ? [entity.GoalCell] : turns, grid.Revision);
                GroupRouteBuildMilliseconds += Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            else _weightedRequests[requests++] = new(profile, entity.GoalCell);
        }
        navigation.Prepare(_weightedRequests.AsSpan(0, requests), token);
        for (int i = 0; i < Count; i++)
        {
            token.ThrowIfCancellationRequested(); ref var entity = ref _entities[i];
            if (!entity.Moving || entity.GroupMember || entity.YieldFor.Value != 0 ||
                _groupRoutes.TryGetValue(entity.Id, out var old) && old.Revision == grid.Revision) continue;
            var key = new RouteRequest(_profileIndices[entity.UnitType], entity.GoalCell); int start = grid.CellAt(entity.Position);
            if (!navigation.TryCost(key, start, out long cost)) continue;
            if (cost == long.MaxValue) RejectRoute(ref entity);
            else _groupRoutes[entity.Id] = new(navigation.Path(key, start, token), grid.Revision);
        }
    }
}
