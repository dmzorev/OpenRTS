using System.Buffers;

namespace OpenRTS.Core.Navigation;

/// <summary>A terrain BFS forest reused to connect many fixed destinations, without N global searches.</summary>
internal sealed class SharedRouteTree
{
    public NavigationGrid Grid { get; }
    public int VisitedCells { get; private set; }
    internal int Depth(int cell) => _depth[cell];
    private readonly int[] _parent, _depth;
    private readonly Locomotor? _profile;
    private readonly RouteGeometry _geometry;
    private readonly List<int> _forward = [], _backward = [];

    internal SharedRouteTree(NavigationGrid grid, int root, CancellationToken token, Locomotor? profile = null, RouteGeometry? geometry = null)
    {
        Grid = grid; _profile = profile; _geometry = geometry ?? new(grid, profile ?? Locomotor.Default);
        var nodes = _geometry.Nodes;
        _parent = new int[nodes.CellCount]; _depth = new int[nodes.CellCount];
        Array.Fill(_depth, -1);
        int[] queue = ArrayPool<int>.Shared.Rent(nodes.CellCount);
        long[]? costs = profile is null ? null : ArrayPool<long>.Shared.Rent(nodes.CellCount);
        if (costs is not null) Array.Fill(costs, long.MaxValue, 0, nodes.CellCount);
        try
        {
            Flood(root);
            for (int cell = 0; cell < nodes.CellCount; cell++)
            {
                if ((cell & 4095) == 0) token.ThrowIfCancellationRequested();
                if (_depth[cell] < 0 && !Blocked(cell)) Flood(cell);
            }
            bool Blocked(int cell) => !_geometry.Open(cell, token);
            void FloodWeighted(int seed)
            {
                var pending = new PriorityQueue<int, (long Cost, int Cell)>();
                costs![seed] = 0; _parent[seed] = -1; pending.Enqueue(seed, (0, seed));
                while (pending.TryDequeue(out int cell, out var priority))
                {
                    if ((VisitedCells & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (costs[cell] != priority.Cost) continue;
                    _depth[cell] = _parent[cell] < 0 ? 0 : _depth[_parent[cell]] + 1; VisitedCells++;
                    for (int d = 0; d < 4; d++)
                    {
                        int next = _geometry.Neighbor(cell, d);
                        if (next < 0 || !_geometry.Edge(cell, next, token)) continue;
                        long cost = priority.Cost + _geometry.Cost(cell, next);
                        if (cost >= costs[next]) continue;
                        costs[next] = cost; _parent[next] = cell; pending.Enqueue(next, (cost, next));
                    }
                }
            }
            void Flood(int seed)
            {
                if (Blocked(seed)) return;
                if (profile is not null) { FloodWeighted(seed); return; }
                int head = 0, tail = 1; queue[0] = seed; _depth[seed] = 0; _parent[seed] = -1;
                while (head < tail)
                {
                    if ((head & 4095) == 0) token.ThrowIfCancellationRequested();
                    int cell = queue[head++];
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int next = _geometry.Neighbor(cell, direction);
                        if (next < 0 || _depth[next] >= 0 || !_geometry.Edge(cell, next, token)) continue;
                        _depth[next] = _depth[cell] + 1; _parent[next] = cell; queue[tail++] = next;
                    }
                }
                VisitedCells += tail;
            }
        }
        finally { ArrayPool<int>.Shared.Return(queue); if (costs is not null) ArrayPool<long>.Shared.Return(costs); }
    }

    internal int[]? Route(int start, int goal, CancellationToken token)
    {
        if (!_geometry.Open(start, token) || !_geometry.Open(goal, token)) return null;
        if (start == goal) return [];
        int sx = start % _geometry.Nodes.Width, sy = start / _geometry.Nodes.Width, gx = goal % _geometry.Nodes.Width, gy = goal / _geometry.Nodes.Width;
        int turn = sy * _geometry.Nodes.Width + gx;
        if (_profile is null && Clear(start, turn) && Clear(turn, goal)) return turn == start || turn == goal ? [goal] : [turn, goal];
        turn = gy * _geometry.Nodes.Width + sx;
        if (_profile is null && Clear(start, turn) && Clear(turn, goal)) return turn == start || turn == goal ? [goal] : [turn, goal];
        _forward.Clear(); _backward.Clear();
        int a = start, b = goal, visited = 0;
        while (a != b)
        {
            if ((visited++ & 4095) == 0) token.ThrowIfCancellationRequested();
            if (_depth[a] >= _depth[b])
            {
                a = _parent[a];
                if (a < 0) return null;
                _forward.Add(a);
            }
            else
            {
                _backward.Add(b); b = _parent[b];
                if (b < 0) return null;
            }
        }
        for (int i = _backward.Count - 1; i >= 0; i--) _forward.Add(_backward[i]);
        var turns = new List<int>();
        int previous = start, lastDirection = 0;
        foreach (int cell in _forward)
        {
            int direction = cell - previous;
            if (previous != start && direction != lastDirection) turns.Add(previous);
            previous = cell; lastDirection = direction;
        }
        turns.Add(goal);
        return turns.ToArray();
    }

    private bool Clear(int from, int to)
    {
        int step = from / Grid.Width == to / Grid.Width ? Math.Sign(to - from) : Math.Sign(to - from) * Grid.Width;
        if (step == 0) return !Grid.IsBlocked(from);
        for (int cell = from; ; cell += step)
        {
            if (Grid.IsBlocked(cell)) return false;
            if (cell == to) return true;
        }
    }
}
