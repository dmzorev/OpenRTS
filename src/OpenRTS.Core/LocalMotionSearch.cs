namespace OpenRTS.Core;

/// <summary>Bounded short search in world-space samples, independent of the terrain grid resolution.</summary>
internal sealed class LocalMotionSearch
{
    internal const int PathCapacity = 16;
    private const int Radius = 16, Side = Radius * 2 + 1, Sample = MotionIndex.BodyRadius;
    private readonly int[] _queue = new int[Side * Side], _parents = new int[Side * Side], _depth = new int[Side * Side];

    internal int Find(NavigationGrid grid, MotionIndex bodies, int index, WorldPosition start, WorldPosition goal,
        Span<WorldPosition> path, WorldPosition? yieldFor = null)
    {
        Array.Fill(_parents, -2);
        int origin = Radius * Side + Radius, head = 0, tail = 1;
        _queue[0] = origin; _parents[origin] = -1; _depth[origin] = 0;
        while (head < tail)
        {
            int node = _queue[head++]; var point = Position(node);
            bool found;
            if (yieldFor is { } requester)
            {
                long dx = (long)start.X - requester.X, dy = (long)start.Y - requester.Y;
                Int128 cross = ((Int128)point.X - start.X) * dy - ((Int128)point.Y - start.Y) * dx;
                found = cross * cross >= (Int128)MotionIndex.BodyRadius * 2 * MotionIndex.BodyRadius * 2 * (dx * dx + dy * dy);
            }
            else found = TerrainGeometry.Clear(grid, point, goal, MotionIndex.BodyRadius) && bodies.ClearSegment(index, point, goal);
            if (node != origin && found && _depth[node] < PathCapacity)
            {
                int length = _depth[node], cursor = node;
                for (int i = length - 1; i >= 0; i--) { path[i] = Position(cursor); cursor = _parents[cursor]; }
                if (yieldFor is null && point != goal) path[length++] = goal;
                return length;
            }
            if (_depth[node] >= PathCapacity - 1) continue;
            // Cardinal samples first, then diagonals. Every edge still has an exact swept-body check.
            for (int direction = 0; direction < 8; direction++)
            {
                int x = node % Side + (direction switch { 1 or 4 or 6 => -1, 2 or 5 or 7 => 1, _ => 0 });
                int y = node / Side + (direction switch { 0 or 4 or 5 => -1, 3 or 6 or 7 => 1, _ => 0 });
                if ((uint)x >= Side || (uint)y >= Side) continue;
                int next = y * Side + x;
                if (_parents[next] != -2) continue;
                var target = Position(next);
                if (!TerrainGeometry.Clear(grid, point, target, MotionIndex.BodyRadius) || !bodies.ClearSegment(index, point, target)) continue;
                _parents[next] = node; _depth[next] = _depth[node] + 1; _queue[tail++] = next;
            }
        }
        return 0;
        WorldPosition Position(int node) => new(start.X + (node % Side - Radius) * Sample,
            start.Y + (node / Side - Radius) * Sample, start.Elevation);
    }
}
