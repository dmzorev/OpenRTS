using System.Runtime.InteropServices;

namespace OpenRTS.Core.Navigation;

// Global terrain routes. The world owns targets, route cursors and physical movement.
internal sealed class TerrainRoutePlanner
{
    private readonly IGridPathfinder _pointPaths;
    private readonly WeightedNavigator? _weighted;
    private readonly int[] _profileIndices = [];
    private DestinationResolver? _plainResolver;
    private SharedRouteTree? _groupTree;
    private readonly Dictionary<int, SharedRouteTree> _weightedTrees = new();
    private int _groupRoot;

    internal NavigationGrid Grid { get; private set; }
    internal bool UsesWeightedRoutes => _weighted is not null;
    internal int BuiltFields => _weighted?.Builds ?? _pointPaths.LastBuildCount;
    internal int VisitedCells => _weighted?.Visited ?? _pointPaths.LastVisitedCells;
    internal double BuildMilliseconds => _weighted?.Milliseconds ?? _pointPaths.LastBuildMilliseconds;
    internal int GroupBuilds { get; private set; }
    internal int GroupVisitedCells { get; private set; }

    internal TerrainRoutePlanner(NavigationGrid grid, UnitDefinition[] types, int workers, int budget, int cache, bool reverse,
        GameplayRegistration<IGridPathfinder>? registration)
    {
        Grid = grid;
        var builtIn = registration is null ? new GridNavigator(grid, workers, budget, cache, reverse) : null;
        int maximum = Math.Max(grid.MaximumTerrain, types.SelectMany(t => t.Footprint?.Cells ?? []).Max(c => (int?)c.Navigation.Terrain) ?? 0);
        bool useClearance = grid.HasGeometry || grid.CellSize < 514 || types.Any(t => t.Flight is null && t.Footprint is null &&
            (t.Body != BodyShape.Default || t.Locomotor.MaximumStep != int.MaxValue ||
                t.Locomotor.MaximumSlope != int.MaxValue || t.Locomotor.MaximumDepth != int.MaxValue));
        foreach (var type in types)
            if (type.Flight is null && type.Footprint is null && type.Locomotor.Rules.Count <= maximum)
                throw new ArgumentException("Unit locomotor does not define every terrain in this grid.");

        if (useClearance || maximum != 0 || grid.HasTerrain || types.Any(t => t.Flight is null && t.Locomotor.Rules[0] != TerrainRule.Ground))
        {
            var profiles = types.Where(t => t.Footprint is null && t.Flight is null).Select(t => t.Locomotor)
                .GroupBy(p => p.NavigationKey, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var first = g.First();
                    if (g.Any(p => !p.Rules.SequenceEqual(first.Rules) || p.MaximumDepth != first.MaximumDepth ||
                        p.MaximumSlope != first.MaximumSlope || p.MaximumStep != first.MaximumStep))
                        throw new ArgumentException("Conflicting locomotors share an ID.");
                    return first;
                }).ToArray();
            if (profiles.Length > 0)
            {
                if (registration is not null)
                    throw new ArgumentException("A registered grid pathfinder requires unweighted point navigation.", nameof(registration));
                _profileIndices = types.Select(t => t.Flight is not null ? -1 : Math.Max(0, Array.FindIndex(profiles, p => p.NavigationKey == t.Locomotor.NavigationKey))).ToArray();
                _weighted = new(grid, profiles, workers, budget, cache, reverse, useClearance);
            }
        }
        _pointPaths = builtIn ?? registration!.Create();
        if (!ReferenceEquals(_pointPaths.Grid, grid))
            throw new ArgumentException("A registered grid pathfinder must start with the world's navigation grid.", nameof(registration));
    }

    internal void SetGrid(NavigationGrid grid)
    {
        _pointPaths.SetGrid(grid);
        if (!ReferenceEquals(_pointPaths.Grid, grid))
            throw new InvalidOperationException("The grid pathfinder did not adopt the new grid revision.");
        _weighted?.SetGrid(grid);
        Grid = grid;
        _plainResolver = null;
        ResetGroup(_groupRoot);
    }

    internal int Profile(int unitType) => _profileIndices[unitType];

    internal DestinationResolver Resolver(int unitType, Locomotor locomotor) => _weighted is not null
        ? _weighted.Resolver(Profile(unitType))
        : _plainResolver ??= new(Grid, locomotor);

    internal void PreparePoints(ReadOnlySpan<int> goals, CancellationToken token) => _pointPaths.Prepare(goals, token);

    // False means the field is pending. A null result from a ready field means unreachable.
    internal bool TryPointRoute(int start, int goal, WorldPosition target, out WorldPosition[]? points, CancellationToken token)
    {
        points = null;
        if (!_pointPaths.TryDistance(goal, start, out int distance)) return false;
        if (distance < 0) return true;
        if (distance >= Grid.CellCount) throw new InvalidOperationException("Invalid terrain distance.");
        var turns = new List<int> { start };
        int cell = start, lastDirection = 0;
        while (cell != goal)
        {
            if ((distance & 1023) == 0) token.ThrowIfCancellationRequested();
            int next = -1;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbor = GridNeighbors.Neighbor(Grid, cell, direction);
                if (neighbor >= 0 && _pointPaths.TryDistance(goal, neighbor, out int value) && value == distance - 1 &&
                    (next < 0 || neighbor < next)) next = neighbor;
            }
            if (next < 0 || distance <= 0) throw new InvalidOperationException("Terrain distance field has no descending edge.");
            int step = next - cell;
            if (cell != start && step != lastDirection) turns.Add(cell);
            cell = next;
            lastDirection = step;
            distance--;
        }
        if (distance != 0) throw new InvalidOperationException("Terrain distance at the goal must be zero.");
        turns.Add(goal);
        points = PointRoute(CollectionsMarshal.AsSpan(turns), target);
        return true;
    }

    internal void PrepareWeighted(ReadOnlySpan<RouteRequest> requests, CancellationToken token) => _weighted!.Prepare(requests, token);

    internal bool CanReach(int profile, int start, int goal, CancellationToken token) => _weighted!.CanReach(profile, start, goal, token);

    internal bool TryWeightedRoute(RouteRequest request, int start, WorldPosition target, out WorldPosition[]? points, CancellationToken token)
    {
        points = null;
        if (!_weighted!.TryCost(request, start, out long cost)) return false;
        if (cost != long.MaxValue) points = WeightedRoute(_weighted.Path(request, start, token), target);
        return true;
    }

    internal void ResetGroup(int root)
    {
        _groupRoot = root;
        _groupTree = null;
        _weightedTrees.Clear();
    }

    internal void ResetGroupMetrics() => GroupBuilds = GroupVisitedCells = 0;

    internal WorldPosition[]? GroupRoute(int start, int goal, WorldPosition target, CancellationToken token)
    {
        if (_groupTree is null)
        {
            _groupTree = new(Grid, _groupRoot, token);
            GroupBuilds++;
            GroupVisitedCells += _groupTree.VisitedCells;
        }
        var turns = _groupTree.Route(start, goal, token);
        return turns is null ? null : PointRoute(turns, target);
    }

    internal WorldPosition[]? WeightedGroupRoute(int profile, DestinationResolver resolver, Locomotor locomotor,
        int start, int goal, WorldPosition target, CancellationToken token)
    {
        if (!_weightedTrees.TryGetValue(profile, out var tree))
        {
            int root = resolver.NodeAt(Grid.Center(_groupRoot), token);
            tree = new(Grid, root >= 0 ? root : start, token, locomotor, _weighted!.Geometry(profile));
            _weightedTrees.Add(profile, tree);
            GroupBuilds++;
            GroupVisitedCells += tree.VisitedCells;
        }
        var turns = tree.Route(start, goal, token);
        return turns is null ? null : WeightedRoute(turns, target);
    }

    private WorldPosition[] PointRoute(ReadOnlySpan<int> cells, WorldPosition target)
    {
        var points = new WorldPosition[Math.Max(1, cells.Length)];
        for (int i = 0; i < points.Length - 1; i++) points[i] = Grid.Center(cells[i], target.Elevation);
        points[^1] = target;
        return points;
    }

    private WorldPosition[] WeightedRoute(int[] cells, WorldPosition target)
    {
        var points = new WorldPosition[Math.Max(1, cells.Length)];
        for (int i = 0; i < points.Length - 1; i++) points[i] = _weighted!.Point(cells[i]) with { Elevation = target.Elevation };
        points[^1] = target;
        return points;
    }

    internal void WriteState(BinaryWriter writer)
    {
        _pointPaths.WriteState(writer);
        writer.Write(UsesWeightedRoutes);
        _weighted?.WriteState(writer);
    }

    internal void WriteGroupState(BinaryWriter writer)
    {
        writer.Write(_groupRoot);
        writer.Write(_groupTree?.Grid.Revision ?? -1);
        writer.Write(_weightedTrees.Count);
        foreach (var pair in _weightedTrees.OrderBy(p => p.Key))
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.Grid.Revision);
        }
    }
}
