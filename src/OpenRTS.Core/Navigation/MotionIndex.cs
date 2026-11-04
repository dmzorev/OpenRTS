namespace OpenRTS.Core.Navigation;

/// <summary>Reusable broad phase in world units; navigation cells have no exclusive owner.</summary>
internal sealed class MotionIndex(int maximumStep)
{
    internal const int BodyRadius = 256;
    private const int BinSize = 1024;
    private struct Body { internal EntityId Id; internal WorldPosition Start, End; internal int Next; internal uint Owner; }
    private Body[] _bodies = [];
    private readonly Dictionary<long, int> _heads = new();

    internal void Clear(int capacity)
    {
        _heads.Clear();
        _heads.EnsureCapacity(capacity);
        if (_bodies.Length < capacity) Array.Resize(ref _bodies, Math.Max(capacity, 16));
    }

    internal void Add(int index, EntityId id, WorldPosition position, uint owner = 0)
    {
        if (index >= _bodies.Length) Array.Resize(ref _bodies, Math.Max(index + 1, _bodies.Length * 2));
        long key = Key(position.X / BinSize, position.Y / BinSize);
        int next = _heads.TryGetValue(key, out int head) ? head : -1;
        _bodies[index] = new() { Id = id, Start = position, End = position, Next = next, Owner = owner };
        _heads[key] = index;
    }

    internal void SetEnd(int index, WorldPosition position) => _bodies[index].End = position;

    internal int NearestEnemy(int index, int range, Func<int, int, int, bool>? valid = null)
    {
        var actor = _bodies[index]; int best = -1; long distance = (long)range * range;
        for (int y = Floor(actor.Start.Y - range); y <= Floor(actor.Start.Y + range); y++)
            for (int x = Floor(actor.Start.X - range); x <= Floor(actor.Start.X + range); x++)
            {
                if (!_heads.TryGetValue(Key(x, y), out int next)) continue;
                for (; next >= 0; next = _bodies[next].Next)
                {
                    var other = _bodies[next]; if (other.Owner == 0 || other.Owner == actor.Owner) continue;
                    long dx = (long)actor.Start.X - other.Start.X, dy = (long)actor.Start.Y - other.Start.Y, squared = dx * dx + dy * dy;
                    if (squared > distance || squared == distance && best >= 0 && other.Id.Value >= _bodies[best].Id.Value) continue;
                    if (valid is not null && !valid(index, next, range)) continue;
                    best = next; distance = squared;
                }
            }
        return best;
    }

    internal bool ClearSegment(int index, WorldPosition from, WorldPosition to)
    {
        int padding = BodyRadius * 2 + maximumStep;
        for (int y = Floor(Math.Min(from.Y, to.Y) - padding); y <= Floor(Math.Max(from.Y, to.Y) + padding); y++)
            for (int x = Floor(Math.Min(from.X, to.X) - padding); x <= Floor(Math.Max(from.X, to.X) + padding); x++)
            {
                if (!_heads.TryGetValue(Key(x, y), out int current)) continue;
                for (; current >= 0; current = _bodies[current].Next)
                    if (current != index && Overlap(from, to, _bodies[current].End, _bodies[current].End)) return false;
            }
        return true;
    }

    internal bool ClearMove(int index, WorldPosition destination, out int blocker, bool[]? cohort = null)
    {
        var start = _bodies[index].Start;
        int padding = BodyRadius * 2 + maximumStep;
        int minX = Floor(Math.Min(start.X, destination.X) - padding), maxX = Floor(Math.Max(start.X, destination.X) + padding);
        int minY = Floor(Math.Min(start.Y, destination.Y) - padding), maxY = Floor(Math.Max(start.Y, destination.Y) + padding);
        blocker = -1;
        for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
        {
            if (!_heads.TryGetValue(Key(x, y), out int current)) continue;
            for (; current >= 0; current = _bodies[current].Next)
            {
                if (current == index || cohort is not null && cohort[current]) continue;
                ref var other = ref _bodies[current];
                if (!Overlap(start, destination, other.Start, other.End)) continue;
                if (blocker < 0 || other.Id.Value < _bodies[blocker].Id.Value) blocker = current;
            }
        }
        return blocker < 0;
    }

    internal bool ClearPlacement(WorldPosition position, out int blocker)
    {
        blocker = -1;
        int padding = BodyRadius * 2 + maximumStep;
        for (int y = Floor(position.Y - padding); y <= Floor(position.Y + padding); y++)
            for (int x = Floor(position.X - padding); x <= Floor(position.X + padding); x++)
            {
                if (!_heads.TryGetValue(Key(x, y), out int current)) continue;
                for (; current >= 0; current = _bodies[current].Next)
                    if (Overlap(position, position, _bodies[current].End, _bodies[current].End) &&
                        (blocker < 0 || _bodies[current].Id.Value < _bodies[blocker].Id.Value)) blocker = current;
            }
        return blocker < 0;
    }

    internal bool OverlapsCell(NavigationGrid grid, int cell, int count, out EntityId occupant)
    {
        // Terrain edits are rare; test exact bodies instead of treating a route cell as occupied.
        int x = cell % grid.Width * grid.CellSize, y = cell / grid.Width * grid.CellSize;
        occupant = default;
        for (int i = 0; i < count; i++)
            if (TerrainGeometry.IntersectsBox(_bodies[i].End, _bodies[i].End,
                x - BodyRadius, y - BodyRadius, x + grid.CellSize + BodyRadius, y + grid.CellSize + BodyRadius) &&
                (occupant.Value == 0 || _bodies[i].Id.Value < occupant.Value)) occupant = _bodies[i].Id;
        return occupant.Value != 0;
    }

    private static bool Overlap(WorldPosition a0, WorldPosition a1, WorldPosition b0, WorldPosition b1)
    {
        long x = (long)a0.X - b0.X, y = (long)a0.Y - b0.Y;
        long dx = ((long)a1.X - a0.X) - ((long)b1.X - b0.X), dy = ((long)a1.Y - a0.Y) - ((long)b1.Y - b0.Y);
        Int128 length = (Int128)dx * dx + (Int128)dy * dy;
        Int128 dot = -((Int128)x * dx + (Int128)y * dy);
        const long diameterSquared = BodyRadius * 2L * BodyRadius * 2L;
        if (dot <= 0 || length == 0) return (Int128)x * x + (Int128)y * y < diameterSquared;
        if (dot >= length) return (Int128)(x + dx) * (x + dx) + (Int128)(y + dy) * (y + dy) < diameterSquared;
        return ((Int128)x * x + (Int128)y * y - diameterSquared) * length < dot * dot;
    }

    private static int Floor(int value) => value >= 0 ? value / BinSize : (value - BinSize + 1) / BinSize;
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
