using System.Buffers;

namespace OpenRTS.Core;

/// <summary>A terrain BFS forest reused to connect many fixed destinations, without N global searches.</summary>
internal sealed class SharedRouteTree
{
    public NavigationGrid Grid { get; }
    public int VisitedCells { get; private set; }
    internal int Depth(int cell) => _depth[cell];
    private readonly int[] _parent, _depth;
    private readonly List<int> _forward = [], _backward = [];

    internal SharedRouteTree(NavigationGrid grid, int root, CancellationToken token)
    {
        Grid = grid;
        _parent = new int[grid.CellCount]; _depth = new int[grid.CellCount];
        Array.Fill(_depth, -1);
        int[] queue = ArrayPool<int>.Shared.Rent(grid.CellCount);
        try
        {
            Flood(root);
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                if ((cell & 4095) == 0) token.ThrowIfCancellationRequested();
                if (_depth[cell] < 0 && !grid.IsBlocked(cell)) Flood(cell);
            }
            void Flood(int seed)
            {
                if (grid.IsBlocked(seed)) return;
                int head = 0, tail = 1; queue[0] = seed; _depth[seed] = 0; _parent[seed] = -1;
                while (head < tail)
                {
                    if ((head & 4095) == 0) token.ThrowIfCancellationRequested();
                    int cell = queue[head++];
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int next = LocalRouteSearch.Neighbor(grid, cell, direction);
                        if (next < 0 || _depth[next] >= 0 || grid.IsBlocked(next)) continue;
                        _depth[next] = _depth[cell] + 1; _parent[next] = cell; queue[tail++] = next;
                    }
                }
                VisitedCells += tail;
            }
        }
        finally { ArrayPool<int>.Shared.Return(queue); }
    }

    internal int[]? Route(int start, int goal, CancellationToken token)
    {
        if (Grid.IsBlocked(start) || Grid.IsBlocked(goal)) return null;
        if (start == goal) return [];
        int sx = start % Grid.Width, sy = start / Grid.Width, gx = goal % Grid.Width, gy = goal / Grid.Width;
        int turn = sy * Grid.Width + gx;
        if (Clear(start, turn) && Clear(turn, goal)) return turn == start || turn == goal ? [goal] : [turn, goal];
        turn = gy * Grid.Width + sx;
        if (Clear(start, turn) && Clear(turn, goal)) return turn == start || turn == goal ? [goal] : [turn, goal];
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
