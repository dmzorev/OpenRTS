using System.Numerics;

namespace OpenRTS.Core.Visibility;

public enum FogMode { Hidden, Explored, Visible }
public enum CellVisibility : byte { Unknown, Explored, Visible }
public readonly record struct VisionSource(EntityId Id, uint Owner, int X, int Y, int Radius);

/// <summary>Single-owner, incremental circular coverage. Samples are centers of clipped cells.</summary>
public sealed partial class FogOfWar
{
    public int Width { get; }
    public int Depth { get; }
    public int CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int CellCount => Columns * Rows;
    public IReadOnlyList<uint> Players { get; }
    public bool OptimizeGroups { get; }
    public FogMode Mode { get; }
    public int HoldTicks { get; }
    private long _tick;
    private readonly long[] _visibleUntil;
    private readonly bool[] _wasCovered;
    public int RasterizedSources { get; private set; }
    public int GroupCacheHits { get; private set; }
    private readonly uint[] _players;
    private readonly Dictionary<uint, int> _slots;
    private readonly int[] _coverage;
    private readonly byte[] _explored;
    private readonly bool[] _dirty;
    private readonly List<int> _touched = [];
    private Dictionary<EntityId, VisionSource> _sources = [], _next = [];

    public FogOfWar(int width, int depth, IEnumerable<uint> players, int cellSize = 1024, bool optimizeGroups = false, FogMode mode = FogMode.Hidden, int holdTicks = 0)
    {
        if (width <= 0 || width > WorldPosition.MaximumExtent || depth <= 0 || depth > WorldPosition.MaximumExtent || cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!Enum.IsDefined(mode) || mode == FogMode.Visible) throw new ArgumentException("Visible mode does not need fog fields.");
        if (holdTicks is < 0 or > 250) throw new ArgumentOutOfRangeException(nameof(holdTicks));
        HoldTicks = holdTicks;
        Mode = mode; OptimizeGroups = optimizeGroups;
        Width = width; Depth = depth; CellSize = cellSize;
        Columns = (int)(((long)width + cellSize - 1) / cellSize); Rows = (int)(((long)depth + cellSize - 1) / cellSize);
        _players = players.Order().ToArray();
        if (_players.Length is < 1 or > 16 || _players.Any(p => p == 0) || _players.Distinct().Count() != _players.Length)
            throw new ArgumentException("Fog needs 1..16 distinct nonneutral players.", nameof(players));
        long cells = (long)Columns * Rows * _players.Length;
        // Bound the dense coverage, history and dirty storage before allocating any of them.
        int bytesPerCell = 4 + (mode == FogMode.Hidden ? 1 : 0) + (mode == FogMode.Hidden || holdTicks > 0 ? 1 : 0) + (holdTicks > 0 ? 9 : 0);
        if (cells * bytesPerCell > 192L * 1024 * 1024) throw new ArgumentException("Fog exceeds the 192 MiB field budget.");
        _coverage = new int[(int)cells]; _explored = mode == FogMode.Hidden ? new byte[(int)cells] : []; _dirty = mode == FogMode.Hidden || holdTicks > 0 ? new bool[(int)cells] : [];
        _visibleUntil = holdTicks > 0 ? new long[(int)cells] : []; _wasCovered = holdTicks > 0 ? new bool[(int)cells] : [];
        _slots = _players.Select((id, slot) => (id, slot)).ToDictionary(p => p.id, p => p.slot);
        Players = Array.AsReadOnly(_players);
    }

    /// <summary>Exclusive expiration: a cell lost at t is retained on t..t+HoldTicks-1.</summary>
    public void AdvanceTo(long tick)
    {
        if (tick < _tick) throw new ArgumentOutOfRangeException(nameof(tick));
        _tick = tick;
    }
    private bool Covered(int i) => _coverage[i] > 0 || HoldTicks > 0 && _visibleUntil[i] > _tick;
    internal void CopyStrength(Span<byte> destination)
    {
        if (destination.Length < _coverage.Length) throw new ArgumentException("Fog strength destination too short.");
        for (int i = 0; i < _coverage.Length; i++)
            destination[i] = _coverage[i] > 0 ? (byte)255 : HoldTicks > 0 && _visibleUntil[i] > _tick
                ? (byte)((_visibleUntil[i] - _tick) * 255 / (HoldTicks + 1)) : (byte)0;
    }

    public int PlayerSlot(uint player) => _slots.GetValueOrDefault(player, -1);
    public CellVisibility At(uint player, int cell)
    {
        if ((uint)cell >= CellCount || !_slots.TryGetValue(player, out int slot)) return CellVisibility.Unknown;
        int i = slot * CellCount + cell;
        return Covered(i) ? CellVisibility.Visible : Mode == FogMode.Explored || _explored[i] != 0 ? CellVisibility.Explored : CellVisibility.Unknown;
    }
    public CellVisibility At(uint player, WorldPosition p) => At(player, CellAt(p));
    public int CellAt(WorldPosition p) => p.X < 0 || p.X >= Width || p.Y < 0 || p.Y >= Depth ? -1 : p.Y / CellSize * Columns + p.X / CellSize;
    /// <summary>Number of effective contributors (merged groups when optimized), not necessarily individual actors.</summary>
    public int Coverage(uint player, int cell) => (uint)cell < CellCount && _slots.TryGetValue(player, out int slot) ? _coverage[slot * CellCount + cell] : 0;

    public void Update(ReadOnlySpan<VisionSource> sources, CancellationToken cancellationToken = default)
    {
        RasterizedSources = GroupCacheHits = 0;
        if (OptimizeGroups) { UpdateGroups(sources, cancellationToken); return; }
        _next.Clear();
        foreach (var s in sources)
        {
            if (s.Radius < 0 || s.Radius > WorldPosition.MaximumExtent || s.X < 0 || s.X > Width || s.Y < 0 || s.Y > Depth || s.Id.Value == 0)
                throw new ArgumentException("Invalid vision source.");
            if (s.Owner == 0 || s.Radius == 0) continue;
            if (!_slots.ContainsKey(s.Owner) || !_next.TryAdd(s.Id, s)) throw new ArgumentException("Unknown owner or duplicate vision source.");
        }
        foreach (var (id, old) in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_next.TryGetValue(id, out var next)) Change(old, default, false, cancellationToken);
            else if (old != next)
            {
                if (old.Owner == next.Owner) Change(old, next, true, cancellationToken);
                else { Change(old, default, false, cancellationToken); Change(default, next, false, cancellationToken); }
            }
        }
        foreach (var (id, next) in _next)
            if (!_sources.ContainsKey(id)) Change(default, next, false, cancellationToken);
        FinishCoverage();
        (_sources, _next) = (_next, _sources);
    }

    private void FinishCoverage()
    {
        foreach (int i in _touched)
        {
            if (Mode == FogMode.Hidden && _coverage[i] > 0) _explored[i] = 1;
            if (HoldTicks > 0)
            {
                if (_coverage[i] > 0) _visibleUntil[i] = 0;
                else if (_wasCovered[i]) _visibleUntil[i] = checked(_tick + HoldTicks);
            }
            _dirty[i] = false;
        }
        _touched.Clear();
    }
    private void Change(VisionSource old, VisionSource next, bool sameOwner, CancellationToken token)
    {
        RasterizedSources++;
        int first = Rows, last = -1;
        void Include(VisionSource s)
        {
            if (s.Radius == 0) return;
            first = Math.Min(first, (int)Math.Max(0, ((long)s.Y - s.Radius) / CellSize - 1));
            last = Math.Max(last, (int)Math.Min(Rows - 1, ((long)s.Y + s.Radius) / CellSize + 1));
        }
        Include(old); Include(next);
        for (int y = first; y <= last; y++)
        {
            token.ThrowIfCancellationRequested();
            var a = Interval(old, y); var b = Interval(next, y);
            if (old.Radius > 0) Difference(a, sameOwner ? b : (0, -1), old.Owner, y, -1);
            if (next.Radius > 0) Difference(b, sameOwner ? a : (0, -1), next.Owner, y, 1);
        }
    }
    private void Difference((int L, int R) a, (int L, int R) b, uint owner, int row, int delta)
    {
        if (a.L > a.R) return;
        if (b.L > b.R) { Segment(a.L, a.R); return; }
        Segment(a.L, Math.Min(a.R, b.L - 1)); Segment(Math.Max(a.L, b.R + 1), a.R);
        void Segment(int l, int r)
        {
            int offset = _slots[owner] * CellCount + row * Columns;
            for (int x = l; x <= r; x++)
            {
                int i = offset + x;
                if ((Mode == FogMode.Hidden || HoldTicks > 0) && !_dirty[i])
                {
                    _dirty[i] = true; _touched.Add(i);
                    if (HoldTicks > 0) _wasCovered[i] = _coverage[i] > 0;
                }
                _coverage[i] = checked(_coverage[i] + delta);
                if (_coverage[i] < 0) throw new InvalidOperationException("Negative vision coverage.");
            }
        }
    }
    private long Center2(int column) => (long)column * CellSize + Math.Min((long)(column + 1) * CellSize, Width);
    private (int L, int R) Interval(VisionSource s, int row)
    {
        if (s.Radius == 0) return (0, -1);
        long dy = (long)row * CellSize + Math.Min((long)(row + 1) * CellSize, Depth) - 2L * s.Y;
        // WorldPosition.MaximumExtent bounds doubled coordinates to 2^30 and squares to 2^60.
        long q = (2L * s.Radius) * (2L * s.Radius) - dy * dy;
        if (q < 0) return (0, -1);
        long root = 0;
        if (q > 0)
        {
            root = 1L << ((BitOperations.Log2((ulong)q) + 2) / 2);
            while (true) { long next = (root + q / root) / 2; if (next >= root) break; root = next; }
        }
        long left = 2L * s.X - root, right = 2L * s.X + root;
        int Lower(long limit)
        {
            long offset = limit - CellSize, stride = 2L * CellSize;
            long column = offset >= 0 ? (offset + stride - 1) / stride : offset / stride;
            int result = (int)Math.Clamp(column, 0, Columns - 1);
            return result == Columns - 1 && Center2(result) < limit ? Columns : result;
        }
        return (Lower(left), Lower(right + 1) - 1);
    }

    internal void CopyStates(Span<CellVisibility> destination)
    {
        if (destination.Length < _coverage.Length) throw new ArgumentException("Fog destination too short.");
        for (int i = 0; i < _coverage.Length; i++) destination[i] = Covered(i) ? CellVisibility.Visible : Mode == FogMode.Explored || _explored[i] != 0 ? CellVisibility.Explored : CellVisibility.Unknown;
    }
    internal void WriteState(BinaryWriter writer)
    {
        if (HoldTicks > 0)
        {
            writer.Write(3); writer.Write(HoldTicks);
            for (int i = 0; i < _visibleUntil.Length; i++) writer.Write(_visibleUntil[i] > _tick ? _visibleUntil[i] : 0L);
        }
        writer.Write(Mode == FogMode.Hidden ? 1 : 2);
        if (Mode != FogMode.Hidden) writer.Write((int)Mode);
        writer.Write(CellSize); writer.Write(_players.Length);
        foreach (uint player in _players) writer.Write(player);
        writer.Write(_explored.Length); writer.Write(_explored);
    }
}
