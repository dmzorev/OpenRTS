namespace OpenRTS.Core.Navigation;

/// <summary>Bounded short search in world-space samples, independent of the terrain grid resolution.</summary>
internal sealed class LocalMotionSearch
{
    internal const int PathCapacity = 16;
    private const int Radius = 16, Side = Radius * 2 + 1, Sample = MotionIndex.BodyRadius;
    private readonly int[] _queue = new int[Side * Side], _parents = new int[Side * Side], _depth = new int[Side * Side];
    private readonly long[] _costs = new long[Side * Side];

    internal int Find(NavigationGrid grid, MotionIndex bodies, EntityId id, WorldPosition start, WorldPosition goal,
        Span<WorldPosition> path, WorldPosition? yieldFor = null, Locomotor? locomotor = null, BodyShape? requesterBody = null)
    {
        var token = bodies.Resolve(id);
        var body = bodies.Shape(token);
        Array.Fill(_parents, -2);
        int origin = Radius * Side + Radius, head = 0, tail = 1;
        _queue[0] = origin; _parents[origin] = -1; _depth[origin] = 0;
        PriorityQueue<int, (long Cost, int Node)>? pending = null;
        if (locomotor is not null)
        { Array.Fill(_costs, long.MaxValue); _costs[origin] = 0; pending = new(); pending.Enqueue(origin, (0, origin)); }
        while (pending is null ? head < tail : pending.Count > 0)
        {
            int node;
            if (pending is null) node = _queue[head++];
            else
            {
                pending.TryDequeue(out node, out var priority);
                if (priority.Cost != _costs[node]) continue;
            }
            var point = Position(node);
            bool found;
            if (yieldFor is { } requester)
            {
                long dx = (long)start.X - requester.X, dy = (long)start.Y - requester.Y;
                Int128 cross = ((Int128)point.X - start.X) * dy - ((Int128)point.Y - start.Y) * dx;
                int clearance = Math.Max(body.HalfWidth, body.HalfHeight) + Math.Max((requesterBody ?? BodyShape.Default).HalfWidth, (requesterBody ?? BodyShape.Default).HalfHeight);
                found = cross * cross >= (Int128)clearance * clearance * (dx * dx + dy * dy);
            }
            else found = TerrainGeometry.Clear(grid, point, goal, body, locomotor) && bodies.ClearSegment(token, point, goal);
            if (node != origin && found && _depth[node] < PathCapacity &&
                (yieldFor is null || locomotor is null || locomotor.At(grid, grid.CellAt(point)).CanStop))
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
                if (pending is null && _parents[next] != -2) continue;
                var target = Position(next);
                if (!TerrainGeometry.Clear(grid, point, target, body, locomotor) || !bodies.ClearSegment(token, point, target)) continue;
                if (pending is not null)
                {
                    long edge = TerrainCost.Segment(grid, locomotor!, point, target);
                    if (edge == long.MaxValue) continue;
                    long cost = _costs[node] + edge;
                    if (cost >= _costs[next]) continue;
                    _costs[next] = cost; pending.Enqueue(next, (cost, next));
                }
                else _queue[tail++] = next;
                _parents[next] = node; _depth[next] = _depth[node] + 1;
            }
        }
        return 0;
        WorldPosition Position(int node) => new(start.X + (node % Side - Radius) * Sample,
            start.Y + (node / Side - Radius) * Sample, start.Elevation);
    }
}
