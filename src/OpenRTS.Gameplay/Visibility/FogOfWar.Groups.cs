namespace OpenRTS.Core.Visibility;

public sealed partial class FogOfWar
{
    private readonly record struct Strip(int Row, int Left, int Right);
    private sealed class VisionGroup
    {
        public List<VisionSource> Members = [], Pending = [];
        public Strip[] Strips = [];
        public int X, Y;
        public bool Seen, Individual, Sorted;
        public int RigidTicks;
        public readonly Dictionary<(int X, int Y), Strip[]> Cache = [];
    }
    private readonly Dictionary<(uint Owner, uint Block), VisionGroup> _groups = [];
    private readonly List<(uint Owner, uint Block)> _removedGroups = [];
    private readonly List<Strip> _stripWork = [];
    private int _cachedStrips, _activeStrips;
    private const int MaximumCachedStrips = 1024 * 1024;

    private void UpdateGroups(ReadOnlySpan<VisionSource> sources, CancellationToken token)
    {
        foreach (var g in _groups.Values) { g.Pending.Clear(); g.Seen = false; g.Sorted = true; }
        _next.Clear();
        foreach (var s in sources)
        {
            if (s.Radius < 0 || s.Radius > WorldPosition.MaximumExtent || s.X < 0 || s.X > Width || s.Y < 0 || s.Y > Depth || s.Id.Value == 0)
                throw new ArgumentException("Invalid vision source.");
            if (s.Owner == 0 || s.Radius == 0) continue;
            if (!_slots.ContainsKey(s.Owner) || !_next.TryAdd(s.Id, s)) throw new ArgumentException("Unknown owner or duplicate vision source.");
            // Stable membership survives translation and native packed removal, without geometric assumptions.
            var key = (s.Owner, s.Id.Value / 64);
            if (!_groups.TryGetValue(key, out var g)) _groups.Add(key, g = new());
            if (g.Pending.Count > 0 && g.Pending[^1].Id.Value > s.Id.Value) g.Sorted = false;
            g.Pending.Add(s); g.Seen = true;
        }
        _removedGroups.Clear();
        foreach (var (key, g) in _groups)
        {
            token.ThrowIfCancellationRequested();
            if (!g.Seen)
            {
                if (g.Individual) foreach (var s in g.Members) Change(s, default, false, token);
                else { ApplyStrips(g.Strips, g.X, g.Y, [], 0, 0, key.Owner, -1); _activeStrips -= g.Strips.Length; }
                ClearCache(g); _removedGroups.Add(key); continue;
            }
            if (!g.Sorted) g.Pending.Sort(static (a,b) => a.Id.Value.CompareTo(b.Id.Value));
            var anchor = g.Pending[0]; int dx = 0, dy = 0;
            bool translated = g.Pending.Count == g.Members.Count;
            if (translated)
            {
                dx = anchor.X - g.Members[0].X; dy = anchor.Y - g.Members[0].Y;
                for (int i = 0; i < g.Pending.Count; i++)
                {
                    var a = g.Members[i]; var b = g.Pending[i];
                    if (a.Id != b.Id || a.Radius != b.Radius || b.X - a.X != dx || b.Y - a.Y != dy) { translated = false; break; }
                }
                if (translated && dx == 0 && dy == 0) continue;
            }
            g.RigidTicks = translated ? Math.Min(2, g.RigidTicks + 1) : 0;
            if (!translated) ClearCache(g);
            long rows = 0; bool interior = true;
            foreach (var s in g.Pending)
            {
                rows += Math.Min(Rows, 2L * s.Radius / CellSize + 4);
                interior &= (long)s.X - s.Radius >= 0 && (long)s.Y - s.Radius >= 0 &&
                    (long)s.X + s.Radius < (long)(Width / CellSize) * CellSize && (long)s.Y + s.Radius < (long)(Depth / CellSize) * CellSize;
            }
            // Keep very large or isolated circles on the bounded-memory incremental path.
            bool individual = g.Pending.Count < 4 || rows > 65536 || g.RigidTicks < 2;
            int x = anchor.X / CellSize, y = anchor.Y / CellSize;
            Strip[] strips = [];
            if (!individual)
            {
                var phase = (anchor.X % CellSize, anchor.Y % CellSize);
                if (interior && g.Cache.TryGetValue(phase, out var cached)) { strips = cached; GroupCacheHits++; }
                else
                {
                    strips = BuildStrips(g.Pending, x, y, token);
                    if (interior && g.Cache.Count < 16 && _cachedStrips + strips.Length <= MaximumCachedStrips)
                    { g.Cache.Add(phase, strips); _cachedStrips += strips.Length; }
                }
                if (_activeStrips - g.Strips.Length + strips.Length > MaximumCachedStrips) individual = true;
            }
            if (individual)
            {
                if (!g.Individual) { ApplyStrips(g.Strips, g.X, g.Y, [], 0, 0, key.Owner, -1); _activeStrips -= g.Strips.Length; g.Strips = []; }
                if (g.Individual)
                    foreach (var old in g.Members)
                    {
                        if (!_next.TryGetValue(old.Id, out var next) || next.Owner != old.Owner) Change(old, default, false, token);
                        else if (old != next) Change(old, next, true, token);
                    }
                int previous = 0;
                foreach (var s in g.Pending)
                {
                    while (previous < g.Members.Count && g.Members[previous].Id.Value < s.Id.Value) previous++;
                    if (!g.Individual || previous == g.Members.Count || g.Members[previous].Id != s.Id) Change(default, s, false, token);
                }
            }
            else
            {
                if (g.Individual) foreach (var old in g.Members) Change(old, default, false, token);
                else ApplyStrips(g.Strips, g.X, g.Y, strips, x, y, key.Owner, -1);
                ApplyStrips(strips, x, y, g.Individual ? [] : g.Strips, g.X, g.Y, key.Owner, 1);
                _activeStrips += strips.Length - g.Strips.Length;
                g.Strips = strips; g.X = x; g.Y = y;
            }
            g.Individual = individual;
            (g.Members, g.Pending) = (g.Pending, g.Members);
        }
        foreach (var key in _removedGroups) _groups.Remove(key);
        FinishCoverage();
    }
    private void ClearCache(VisionGroup g)
    {
        foreach (var strips in g.Cache.Values) _cachedStrips -= strips.Length;
        g.Cache.Clear();
    }
    private Strip[] BuildStrips(List<VisionSource> members, int originX, int originY, CancellationToken token)
    {
        _stripWork.Clear();
        foreach (var s in members)
        {
            RasterizedSources++;
            int first = (int)Math.Max(0, ((long)s.Y - s.Radius) / CellSize - 1);
            int last = (int)Math.Min(Rows - 1, ((long)s.Y + s.Radius) / CellSize + 1);
            for (int y = first; y <= last; y++)
            {
                token.ThrowIfCancellationRequested();
                var interval = Interval(s, y);
                if (interval.L <= interval.R) _stripWork.Add(new(y-originY, interval.L-originX, interval.R-originX));
            }
        }
        _stripWork.Sort(static (a,b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Left.CompareTo(b.Left));
        int count = 0;
        for (int i = 0; i < _stripWork.Count; i++)
        {
            var next = _stripWork[i];
            if (count > 0 && _stripWork[count-1] is var last && last.Row == next.Row && next.Left <= last.Right+1)
                _stripWork[count-1] = last with { Right = Math.Max(last.Right, next.Right) };
            else _stripWork[count++] = next;
        }
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stripWork)[..count].ToArray();
    }
    private void ApplyStrips(Strip[] a, int ax, int ay, Strip[] b, int bx, int by, uint owner, int delta)
    {
        int j = 0;
        foreach (var strip in a)
        {
            int row = strip.Row+ay, left = strip.Left+ax, right = strip.Right+ax;
            while (j < b.Length && (b[j].Row+by < row || b[j].Row+by == row && b[j].Right+bx < left)) j++;
            int cursor = left;
            for (int k = j; k < b.Length && b[k].Row+by == row && b[k].Left+bx <= right; k++)
            {
                Difference((cursor, Math.Min(right,b[k].Left+bx-1)), (0,-1), owner,row,delta);
                cursor = Math.Max(cursor,b[k].Right+bx+1);
                if (cursor > right) break;
            }
            Difference((cursor,right),(0,-1),owner,row,delta);
        }
    }
}
