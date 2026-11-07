namespace OpenRTS.Core.Navigation;

/// <summary>Multi-bin spatial index; broad extents and physical collision shapes are independent.</summary>
internal sealed class MotionIndex(int maximumStep)
{
    internal readonly record struct BodyToken(int Index, int Generation);
    internal const int BodyRadius = 256;
    private const int BinSize = 1024;
    private struct Body { internal EntityId Id; internal WorldPosition Start, End; internal BodyShape Shape; }
    private readonly record struct Entry(int Body, int Next);
    private Body[] _bodies = [];
    private Entry[] _entries = [];
    private int[] _seen = [];
    private bool[] _cohort = [];
    private int _entryCount, _stamp, _bodyCount, _generation;
    private readonly Dictionary<EntityId, int> _byId = new();
    private readonly Dictionary<long, int> _heads = new();
    private readonly List<int> _candidates = [];

    internal void Clear(int capacity)
    {
        _heads.Clear(); _heads.EnsureCapacity(capacity); _entryCount = _bodyCount = 0;
        _byId.Clear(); _byId.EnsureCapacity(capacity);
        _generation = unchecked(_generation + 1);
        EnsureCapacity(capacity);
    }
    private void EnsureCapacity(int capacity)
    {
        if (_bodies.Length >= capacity) return;
        Array.Resize(ref _bodies, Math.Max(capacity, Math.Max(16, _bodies.Length * 2)));
        Array.Resize(ref _seen, _bodies.Length); Array.Resize(ref _cohort, _bodies.Length);
    }
    internal void Add(EntityId id, WorldPosition position, BodyShape? shape = null, int broadX = -1, int broadY = -1)
    {
        int index = _bodyCount;
        EnsureCapacity(index + 1);
        _byId.Add(id, index); _bodyCount++;
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
    // Tokens expire on Clear/rebuild; they never name a Frent row or a game-handle slot.
    internal BodyToken Resolve(EntityId id) => new(_byId[id], _generation);
    private ref Body BodyAt(BodyToken token)
    {
        System.Diagnostics.Debug.Assert(token.Generation == _generation && (uint)token.Index < _bodyCount);
        return ref _bodies[token.Index];
    }
    internal void SetEnd(EntityId id, WorldPosition position) => SetEnd(Resolve(id), position);
    internal void SetEnd(BodyToken token, WorldPosition position) => BodyAt(token).End = position;
    internal BodyShape Shape(BodyToken token) => BodyAt(token).Shape;
    internal void SetCohort(ReadOnlySpan<EntityId> members)
    {
        Array.Clear(_cohort, 0, _bodyCount);
        foreach (var id in members) _cohort[_byId[id]] = true;
    }
    internal void Query(WorldPosition from, WorldPosition to, int radius, List<EntityId> result)
    {
        QuerySlots(from, to, radius, _candidates); result.Clear();
        foreach (int index in _candidates) result.Add(_bodies[index].Id);
    }
    private void QuerySlots(WorldPosition from, WorldPosition to, int radius, List<int> result)
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
    internal bool ClearSegment(BodyToken token, WorldPosition from, WorldPosition to)
    {
        int index = token.Index;
        var shape = BodyAt(token).Shape; QuerySlots(from, to, Math.Max(shape.HalfWidth, shape.HalfHeight), _candidates);
        foreach (int other in _candidates)
            if (other != index && BodyGeometry.Overlap(from, to, shape, _bodies[other].End, _bodies[other].End, _bodies[other].Shape)) return false;
        return true;
    }
    internal bool ClearMove(EntityId id, WorldPosition destination, out EntityId blocker, bool ignoreCohort = false)
        => ClearMove(Resolve(id), destination, out blocker, ignoreCohort);
    internal bool ClearMove(BodyToken token, WorldPosition destination, out EntityId blocker, bool ignoreCohort = false)
    {
        int index = token.Index; var actor = BodyAt(token);
        QuerySlots(actor.Start, destination, Math.Max(actor.Shape.HalfWidth, actor.Shape.HalfHeight), _candidates); blocker = default;
        foreach (int other in _candidates)
        {
            if (other == index || ignoreCohort && _cohort[other]) continue;
            ref var body = ref _bodies[other];
            if (BodyGeometry.Overlap(actor.Start, destination, actor.Shape, body.Start, body.End, body.Shape) &&
                (blocker.Value == 0 || body.Id.Value < blocker.Value)) blocker = body.Id;
        }
        return blocker.Value == 0;
    }
    internal bool ClearPlacement(WorldPosition position, out EntityId blocker, BodyShape? shape = null)
    {
        var actor = shape ?? BodyShape.Default;
        QuerySlots(position, position, Math.Max(actor.HalfWidth, actor.HalfHeight), _candidates); blocker = default;
        foreach (int other in _candidates)
            if (BodyGeometry.Overlap(position, position, actor, _bodies[other].End, _bodies[other].End, _bodies[other].Shape) &&
                (blocker.Value == 0 || _bodies[other].Id.Value < blocker.Value)) blocker = _bodies[other].Id;
        return blocker.Value == 0;
    }
    private static int Floor(int value) => value >= 0 ? value / BinSize : (value - BinSize + 1) / BinSize;
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
