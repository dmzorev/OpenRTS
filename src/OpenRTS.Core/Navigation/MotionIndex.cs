namespace OpenRTS.Core.Navigation;

/// <summary>Multi-bin spatial index; broad extents and physical collision shapes are independent.</summary>
internal sealed class MotionIndex(int maximumStep)
{
    internal const int BodyRadius = 256;
    private const int BinSize = 1024;
    private struct Body { internal EntityId Id; internal WorldPosition Start, End; internal BodyShape Shape; }
    private readonly record struct Entry(int Body, int Next);
    private Body[] _bodies = [];
    private Entry[] _entries = [];
    private int[] _seen = [];
    private int _entryCount, _stamp;
    private readonly Dictionary<long, int> _heads = new();
    private readonly List<int> _candidates = [];

    internal void Clear(int capacity)
    {
        _heads.Clear(); _heads.EnsureCapacity(capacity); _entryCount = 0;
        if (_bodies.Length < capacity) { Array.Resize(ref _bodies, Math.Max(capacity, 16)); Array.Resize(ref _seen, _bodies.Length); }
    }
    internal void Add(int index, EntityId id, WorldPosition position, uint owner = 0, BodyShape? shape = null, int broadX = -1, int broadY = -1)
    {
        if (index >= _bodies.Length) { Array.Resize(ref _bodies, Math.Max(index + 1, _bodies.Length * 2)); Array.Resize(ref _seen, _bodies.Length); }
        var body = shape ?? BodyShape.Default;
        if (broadX < 0) broadX = body.HalfWidth; if (broadY < 0) broadY = body.HalfHeight;
        _bodies[index] = new() { Id = id, Start = position, End = position, Shape = body };
        for (int y = Floor(position.Y - broadY); y <= Floor(position.Y + broadY); y++)
            for (int x = Floor(position.X - broadX); x <= Floor(position.X + broadX); x++)
            {
                if (_entries.Length == _entryCount) Array.Resize(ref _entries, Math.Max(64, _entries.Length * 2));
                long key = Key(x, y); int next = _heads.TryGetValue(key, out int head) ? head : -1;
                _entries[_entryCount] = new(index, next); _heads[key] = _entryCount++;
            }
    }
    internal void SetEnd(int index, WorldPosition position) => _bodies[index].End = position;
    internal BodyShape Shape(int index) => _bodies[index].Shape;
    internal void Query(WorldPosition from, WorldPosition to, int radius, List<int> result)
    {
        result.Clear(); int padding = radius + maximumStep;
        if (_stamp == int.MaxValue) { Array.Clear(_seen); _stamp = 0; } _stamp++;
        for (int y = Floor(Math.Min(from.Y, to.Y) - padding); y <= Floor(Math.Max(from.Y, to.Y) + padding); y++)
            for (int x = Floor(Math.Min(from.X, to.X) - padding); x <= Floor(Math.Max(from.X, to.X) + padding); x++)
            {
                if (!_heads.TryGetValue(Key(x, y), out int next)) continue;
                for (; next >= 0; next = _entries[next].Next)
                {
                    int index = _entries[next].Body;
                    if (_seen[index] == _stamp) continue;
                    _seen[index] = _stamp; result.Add(index);
                }
            }
    }
    internal bool ClearSegment(int index, WorldPosition from, WorldPosition to)
    {
        var shape = _bodies[index].Shape; Query(from, to, Math.Max(shape.HalfWidth, shape.HalfHeight), _candidates);
        foreach (int other in _candidates)
            if (other != index && BodyGeometry.Overlap(from, to, shape, _bodies[other].End, _bodies[other].End, _bodies[other].Shape)) return false;
        return true;
    }
    internal bool ClearMove(int index, WorldPosition destination, out int blocker, bool[]? cohort = null)
    {
        var actor = _bodies[index]; Query(actor.Start, destination, Math.Max(actor.Shape.HalfWidth, actor.Shape.HalfHeight), _candidates); blocker = -1;
        foreach (int other in _candidates)
        {
            if (other == index || cohort is not null && cohort[other]) continue;
            ref var body = ref _bodies[other];
            if (BodyGeometry.Overlap(actor.Start, destination, actor.Shape, body.Start, body.End, body.Shape) &&
                (blocker < 0 || body.Id.Value < _bodies[blocker].Id.Value)) blocker = other;
        }
        return blocker < 0;
    }
    internal bool ClearPlacement(WorldPosition position, out int blocker, BodyShape? shape = null)
    {
        var actor = shape ?? BodyShape.Default;
        Query(position, position, Math.Max(actor.HalfWidth, actor.HalfHeight), _candidates); blocker = -1;
        foreach (int other in _candidates)
            if (BodyGeometry.Overlap(position, position, actor, _bodies[other].End, _bodies[other].End, _bodies[other].Shape) &&
                (blocker < 0 || _bodies[other].Id.Value < _bodies[blocker].Id.Value)) blocker = other;
        return blocker < 0;
    }
    internal bool OverlapsCell(NavigationGrid grid, int cell, int count, out EntityId occupant)
    {
        int x = cell % grid.Width * grid.CellSize, y = cell / grid.Width * grid.CellSize; var box = new WorldBox(x, y, x + grid.CellSize, y + grid.CellSize);
        Query(new(x, y), new(x + grid.CellSize, y + grid.CellSize), 0, _candidates); occupant = default;
        foreach (int i in _candidates)
            if (_bodies[i].Shape.Kind != BodyKind.None && BodyGeometry.SweptBox(_bodies[i].End, _bodies[i].End, box, _bodies[i].Shape) &&
                (occupant.Value == 0 || _bodies[i].Id.Value < occupant.Value)) occupant = _bodies[i].Id;
        return occupant.Value != 0;
    }
    private static int Floor(int value) => value >= 0 ? value / BinSize : (value - BinSize + 1) / BinSize;
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
