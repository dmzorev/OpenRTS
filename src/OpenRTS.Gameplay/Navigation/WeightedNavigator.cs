using System.Diagnostics;

namespace OpenRTS.Core.Navigation;

public readonly record struct RouteRequest(int Profile, int Goal);

/// <summary>Profile-keyed reverse Dijkstra fields, deterministic FIFO publication and static components.</summary>
public sealed class WeightedNavigator
{
    private sealed class Field { internal RouteRequest Key = new(-1, -1); internal long[] Costs = []; public int Visited; }
    private readonly Locomotor[] _profiles;
    private readonly bool _exact;
    internal RouteGeometry Geometry(int profile) => Resolver(profile).Geometry;
    internal WorldPosition Point(int cell) => _exact ? new WorldPosition(cell % _routeWidth * RouteGeometry.ExactStep + RouteGeometry.ExactStep / 2, cell / _routeWidth * RouteGeometry.ExactStep + RouteGeometry.ExactStep / 2) : Grid.Center(cell);
    private readonly int _routeCount, _routeWidth;
    private readonly Field[] _fields;
    private readonly Dictionary<RouteRequest, int> _cached = new();
    private readonly Dictionary<int, DestinationResolver> _components = new();
    private readonly List<RouteRequest> _requests = [];
    private readonly HashSet<RouteRequest> _unique = [];
    private readonly RouteRequest[] _jobs;
    private readonly int[] _slots;
    private readonly int _workers;
    private readonly bool _reverse;
    private int _cursor, _replacement;
    public NavigationGrid Grid { get; private set; }
    public int Builds { get; private set; }
    public int Visited { get; private set; }
    public double Milliseconds { get; private set; }

    public WeightedNavigator(NavigationGrid grid, Locomotor[] profiles, int workers, int budget, int cache, bool reverse, bool exactGeometry = false)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(profiles);
        if (workers is < 1 or > 64 || budget is < 1 or > 64 || cache is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (profiles.Length is < 1 or > 16 || profiles.Any(p => p is null || p.Rules.Count <= grid.MaximumTerrain))
            throw new ArgumentException("Provide 1..16 profiles covering every terrain.", nameof(profiles));
        // Components together are bounded to 64 MiB; fields to 128 MiB, except one maximum-size field.
        _exact = exactGeometry;
        var nodes = new RouteGeometry(grid, profiles[0], exactGeometry).Nodes; _routeCount = nodes.CellCount; _routeWidth = nodes.Width;
        if ((long)_routeCount * profiles.Length > NavigationGrid.MaximumCells)
            throw new ArgumentException("N1 terrain/profile combination exceeds its component memory budget.");
        Grid = grid; _profiles = profiles.ToArray(); _workers = workers; _reverse = reverse;
        int capacity = Math.Min(cache, Math.Max(1, NavigationGrid.MaximumCells / _routeCount));
        _fields = Enumerable.Range(0, capacity).Select(_ => new Field()).ToArray();
        _jobs = new RouteRequest[Math.Min(budget, capacity)]; _slots = new int[_jobs.Length];
    }

    public void SetGrid(NavigationGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Width != Grid.Width || grid.Height != Grid.Height || grid.CellSize != Grid.CellSize || _profiles.Any(p => p.Rules.Count <= grid.MaximumTerrain))
            throw new ArgumentException("Replacement grid must preserve dimensions and profile coverage.", nameof(grid));
        if (ReferenceEquals(grid, Grid)) return;
        Grid = grid; _cached.Clear(); _components.Clear(); _cursor = _replacement = 0;
        foreach (var field in _fields) field.Key = new(-1, -1);
    }

    public DestinationResolver Resolver(int profile)
    {
        if ((uint)profile >= (uint)_profiles.Length) throw new ArgumentOutOfRangeException(nameof(profile));
        if (!_components.TryGetValue(profile, out var resolver))
        { resolver = new(Grid, _profiles[profile], _exact); _components.Add(profile, resolver); }
        return resolver;
    }
    public bool CanReach(int profile, int a, int b, CancellationToken token = default)
    {
        Validate(new(profile, b)); ValidateCell(a);
        var resolver = Resolver(profile); int component = resolver.Component(a, token);
        return component > 0 && resolver.Component(b, token) == component;
    }

    public void Prepare(ReadOnlySpan<RouteRequest> requests, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); _requests.Clear(); _unique.Clear();
        foreach (var request in requests) { Validate(request); if (_unique.Add(request)) _requests.Add(request); }
        _requests.Sort((a, b) => a.Profile != b.Profile ? a.Profile.CompareTo(b.Profile) : a.Goal.CompareTo(b.Goal));
        Builds = Visited = 0; Milliseconds = 0;
        if (_requests.Count == 0) return;
        _cursor %= _requests.Count;
        for (int n = 0; n < _requests.Count && Builds < _jobs.Length; n++)
        {
            var key = _requests[(_cursor + n) % _requests.Count];
            if (!_cached.ContainsKey(key)) _jobs[Builds++] = key;
        }
        if (Builds == 0) return;
        _cursor = (_requests.IndexOf(_jobs[Builds - 1]) + 1) % _requests.Count;
        for (int j = 0; j < Builds; j++)
        {
            _slots[j] = _replacement; _replacement = (_replacement + 1) % _fields.Length;
            var field = _fields[_slots[j]];
            _cached.Remove(field.Key); field.Key = new(-1, -1);
            if (field.Costs.Length == 0) field.Costs = new long[_routeCount];
        }
        for (int j = 0; j < Builds; j++) Geometry(_jobs[j].Profile).Prepare(token);
        long start = Stopwatch.GetTimestamp();
        void Run(int job)
        {
            int j = _reverse ? Builds - job - 1 : job;
            _fields[_slots[j]].Visited = Build(Geometry(_jobs[j].Profile), _jobs[j].Goal, _fields[_slots[j]].Costs, token);
        }
        if (_workers == 1) for (int j = 0; j < Builds; j++) Run(j);
        else Parallel.For(0, Builds, new ParallelOptions { MaxDegreeOfParallelism = _workers, CancellationToken = token }, Run);
        Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        for (int j = 0; j < Builds; j++)
        {
            var field = _fields[_slots[j]]; field.Key = _jobs[j];
            _cached.Add(field.Key, _slots[j]); Visited += field.Visited;
        }
    }

    public bool TryCost(RouteRequest key, int cell, out long cost)
    {
        Validate(key); ValidateCell(cell);
        if (_cached.TryGetValue(key, out int slot)) { cost = _fields[slot].Costs[cell]; return true; }
        cost = long.MaxValue; return false;
    }

    internal int[] Path(RouteRequest key, int start, CancellationToken token)
    {
        var turns = new List<int> { start }; int cell = start, direction = 0, length = 0;
        var costs = _fields[_cached[key]].Costs; var graph = Geometry(key.Profile);
        while (cell != key.Goal)
        {
            if ((length++ & 1023) == 0) token.ThrowIfCancellationRequested();
            int next = -1;
            for (int d = 0; d < 4; d++)
            {
                int n = graph.Neighbor(cell, d);
                if (n >= 0 && costs[n] != long.MaxValue && costs[n] + graph.Cost(cell, n) == costs[cell] && (next < 0 || n < next)) next = n;
            }
            if (next < 0) throw new InvalidOperationException("Weighted field has no descending edge.");
            if (cell != start && next - cell != direction) turns.Add(cell);
            direction = next - cell; cell = next;
        }
        turns.Add(key.Goal); return turns.ToArray();
    }

    internal static int Build(NavigationGrid grid, Locomotor profile, int goal, long[] costs, CancellationToken token)
    => Build(new RouteGeometry(grid, profile), goal, costs, token);
    private static int Build(RouteGeometry graph, int goal, long[] costs, CancellationToken token)
    {
        Array.Fill(costs, long.MaxValue); token.ThrowIfCancellationRequested();
        if (!graph.Open(goal, token)) return 0;
        var queue = new PriorityQueue<int, (long Cost, int Cell)>();
        costs[goal] = 0; queue.Enqueue(goal, (0, goal)); int visited = 0;
        while (queue.TryDequeue(out int cell, out var priority))
        {
            if ((visited & 4095) == 0) token.ThrowIfCancellationRequested();
            if (priority.Cost != costs[cell]) continue;
            visited++;
            for (int d = 0; d < 4; d++)
            {
                int next = graph.Neighbor(cell, d);
                if (next < 0 || !graph.Edge(cell, next, token)) continue;
                long cost = priority.Cost + graph.Cost(cell, next);
                if (cost >= costs[next]) continue;
                costs[next] = cost; queue.Enqueue(next, (cost, next));
            }
        }
        return visited;
    }
    private void ValidateCell(int cell)
    { if ((uint)cell >= (uint)_routeCount) throw new ArgumentOutOfRangeException(nameof(cell)); }
    private void Validate(RouteRequest key)
    { ValidateCell(key.Goal); if ((uint)key.Profile >= (uint)_profiles.Length) throw new ArgumentOutOfRangeException(nameof(key)); }
    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(_jobs.Length); writer.Write(_fields.Length); writer.Write(_cursor); writer.Write(_replacement);
        foreach (var field in _fields) { writer.Write(field.Key.Profile); writer.Write(field.Key.Goal); }
    }
}
