namespace OpenRTS.Core;

/// <summary>Bounded occupancy-aware search. Its scratch storage is reused by the world owner.</summary>
internal sealed class LocalRouteSearch
{
    internal const int PathCapacity = 16;
    private const int Radius = 8, Side = Radius * 2 + 1;
    private readonly int[] _queue = new int[Side * Side], _parents = new int[Side * Side], _depth = new int[Side * Side];

    internal int Find(NavigationGrid grid, int start, GridNavigator navigation, int goal, int distance,
        Dictionary<int, EntityId> occupied, HashSet<int> vacated, Span<int> path, int requester = -1, Dictionary<int, EntityId>? reserved = null, bool manhattan = false)
    {
        Array.Fill(_parents, -2);
        int sx = start % grid.Width, sy = start / grid.Width;
        int origin = Radius * Side + Radius, head = 0, tail = 1;
        _queue[0] = start; _parents[origin] = -1; _depth[origin] = 0;
        while (head < tail)
        {
            int cell = _queue[head++], x = cell % grid.Width, y = cell / grid.Width;
            int local = (y - sy + Radius) * Side + x - sx + Radius;
            bool found = requester >= 0
                ? (long)(x - sx) * (sy - requester / grid.Width) != (long)(y - sy) * (sx - requester % grid.Width)
                : manhattan ? Math.Abs(x - goal % grid.Width) + Math.Abs(y - goal / grid.Width) < distance
                : navigation.TryDistance(goal, cell, out int value) && value >= 0 && value < distance;
            if (cell != start && found)
            {
                int length = _depth[local], cursor = local;
                for (int i = length - 1; i >= 0; i--)
                {
                    path[i] = (cursor / Side - Radius + sy) * grid.Width + cursor % Side - Radius + sx;
                    cursor = _parents[cursor];
                }
                return length;
            }
            if (_depth[local] == PathCapacity) continue;
            for (int direction = 0; direction < 4; direction++)
            {
                int neighbor = Neighbor(grid, cell, direction);
                if (neighbor < 0 || grid.IsBlocked(neighbor) || occupied.ContainsKey(neighbor) || reserved is not null && reserved.ContainsKey(neighbor) || vacated.Contains(neighbor)) continue;
                int nx = neighbor % grid.Width - sx + Radius, ny = neighbor / grid.Width - sy + Radius;
                if ((uint)nx >= Side || (uint)ny >= Side) continue;
                int next = ny * Side + nx;
                if (_parents[next] != -2) continue;
                _parents[next] = local; _depth[next] = _depth[local] + 1;
                _queue[tail++] = neighbor;
            }
        }
        return 0;
    }

    internal static int Neighbor(NavigationGrid grid, int cell, int direction) => direction switch
    {
        0 => cell >= grid.Width ? cell - grid.Width : -1,
        1 => cell % grid.Width > 0 ? cell - 1 : -1,
        2 => cell % grid.Width + 1 < grid.Width ? cell + 1 : -1,
        _ => cell + grid.Width < grid.CellCount ? cell + grid.Width : -1
    };
}
