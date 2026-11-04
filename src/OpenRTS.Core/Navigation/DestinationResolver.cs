using System.Buffers;

namespace OpenRTS.Core.Navigation;

/// <summary>Static reachability and nearest legal stopping points; mobile occupancy is deliberately excluded.</summary>
public sealed class DestinationResolver
{
    public NavigationGrid Grid { get; }
    public Locomotor Profile { get; }
    internal RouteGeometry Geometry { get; }
    public int NodeAt(WorldPosition point, CancellationToken token = default) => Geometry.Anchor(point, token);
    public int ComponentAt(WorldPosition point, CancellationToken token = default) { int n = NodeAt(point, token); return n < 0 ? 0 : Component(n, token); }
    private int[]? _components;
    private bool[] _stoppable = [];
    private readonly Dictionary<(int Component, int X, int Y), WorldPosition?> _cache = new();
    private readonly Queue<(int Component, int X, int Y)> _cacheOrder = new();
    public DestinationResolver(NavigationGrid grid, Locomotor profile, bool exactGeometry = false)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(profile);
        if (profile.Rules.Count <= grid.MaximumTerrain) throw new ArgumentException("Profile does not cover this terrain.");
        Grid = grid; Profile = profile; Geometry = new(grid, profile, exactGeometry);
    }
    public int Component(int cell, CancellationToken token = default)
    {
        if ((uint)cell >= Geometry.Nodes.CellCount) throw new ArgumentOutOfRangeException(nameof(cell));
        token.ThrowIfCancellationRequested();
        if (_components is null)
        {
            var labels = new int[Geometry.Nodes.CellCount]; int component = 0; var stoppable = new List<bool> { false };
            int[] queue = ArrayPool<int>.Shared.Rent(Geometry.Nodes.CellCount);
            try
            {
                for (int seed = 0; seed < labels.Length; seed++)
                {
                    if ((seed & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (labels[seed] != 0 || Blocked(seed)) continue;
                    int head = 0, tail = 1; queue[0] = seed; labels[seed] = ++component;
                    bool hasStop = false;
                    while (head < tail)
                    {
                        if ((head & 4095) == 0) token.ThrowIfCancellationRequested();
                        int current = queue[head++];
                        hasStop |= Geometry.Stop(current);
                        for (int d = 0; d < 4; d++)
                        {
                            int next = Geometry.Neighbor(current, d);
                            if (next < 0 || labels[next] != 0 || !Geometry.Edge(current, next, token)) continue;
                            labels[next] = component; queue[tail++] = next;
                        }
                    }
                    stoppable.Add(hasStop);
                }
            }
            finally { ArrayPool<int>.Shared.Return(queue); }
            _stoppable = stoppable.ToArray(); _components = labels;
        }
        return _components[cell];
        bool Blocked(int c) => !Geometry.Open(c, token);
    }
    public bool CanStop(WorldPosition point) => TerrainGeometry.CanStop(Grid, point, Profile.Body, Profile);
    public bool CanReach(WorldPosition source, WorldPosition destination, CancellationToken token = default)
    {
        if (TerrainGeometry.Clear(Grid, source, destination, Profile.Body, Profile)) return true;
        int component = ComponentAt(source, token);
        return component > 0 && component == ComponentAt(destination, token);
    }
    public bool HasStoppingTerrain(WorldPosition source, CancellationToken token = default)
    { int component = ComponentAt(source, token); return _stoppable[component]; }
    public bool Resolve(WorldPosition source, WorldPosition requested, out WorldPosition result, CancellationToken token = default)
    {
        result = default;
        if (CanStop(requested) && TerrainGeometry.Clear(Grid, source, requested, Profile.Body, Profile)) { result = requested; return true; }
        int component = ComponentAt(source, token);
        if (component == 0 || !_stoppable[component]) return false;
        if (CanStop(requested) && ComponentAt(requested, token) == component) { result = requested; return true; }
        var key = (component, requested.X, requested.Y);
        if (!_cache.TryGetValue(key, out var cached))
        {
            cached = Nearest(Grid, requested, p => CanStop(p) && ComponentAt(p, token) == component, token);
            if (_cacheOrder.Count == 128) _cache.Remove(_cacheOrder.Dequeue());
            _cacheOrder.Enqueue(key); _cache.Add(key, cached);
        }
        if (cached is not { } point) return false;
        result = point with { Elevation = requested.Elevation }; return true;
    }
    internal static WorldPosition? Nearest(NavigationGrid grid, WorldPosition requested, Func<WorldPosition, bool> valid, CancellationToken token, BodyShape? body = null)
    {
        WorldPosition? best = null; long distance = long.MaxValue;
        int size = grid.CellSize, inset = Math.Min(size / 2, Math.Max((body ?? BodyShape.Default).HalfWidth, (body ?? BodyShape.Default).HalfHeight) + 1);
        Span<int> xs = stackalloc int[3], ys = stackalloc int[3];
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            if ((cell & 1023) == 0) token.ThrowIfCancellationRequested();
            int left = cell % grid.Width * size, top = cell / grid.Width * size;
            xs[0] = Math.Clamp(requested.X, left, left + size - 1); ys[0] = Math.Clamp(requested.Y, top, top + size - 1);
            long dx = (long)xs[0] - requested.X, dy = (long)ys[0] - requested.Y;
            if (dx * dx + dy * dy > distance) continue;
            xs[1] = Math.Clamp(requested.X, left + inset, left + size - inset); xs[2] = left + size / 2;
            ys[1] = Math.Clamp(requested.Y, top + inset, top + size - inset); ys[2] = top + size / 2;
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
            {
                var candidate = new WorldPosition(xs[x], ys[y], requested.Elevation);
                dx = (long)candidate.X - requested.X; dy = (long)candidate.Y - requested.Y; long next = dx * dx + dy * dy;
                if (next > distance || next == distance && best is { } old && (candidate.Y > old.Y || candidate.Y == old.Y && candidate.X >= old.X)) continue;
                if (!valid(candidate)) continue;
                best = candidate; distance = next;
            }
        }
        return best;
    }
}
