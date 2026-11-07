namespace OpenRTS.Core.Navigation;

public readonly record struct CellOverride(bool? Traverse = null, bool? CanStop = null, byte? Terrain = null);
public readonly record struct GridCell(int X, int Y);
public readonly record struct SurfaceSample(int Elevation = 0, int Slope = 0, int Depth = 0);

/// <summary>Immutable terrain sampling layer, independent of exact body positions and collision bins.</summary>
public sealed class NavigationGrid
{
    public const int MaximumCells = 16 * 1024 * 1024;
    private readonly uint[] _blocked;
    private readonly byte[]? _terrain;
    private readonly uint[]? _noStop;
    private readonly SurfaceSample[]? _surface;
    private readonly WorldBox[] _obstacles;
    private readonly Dictionary<int, int[]> _obstacleCells;
    public bool HasGeometry => _surface is not null || _obstacles.Length > 0;
    public bool HasSurface => _surface is not null;
    public IReadOnlyList<WorldBox> Obstacles => Array.AsReadOnly(_obstacles);
    public bool HasTerrain => _terrain is not null;
    public int MaximumTerrain { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public int CellCount => Width * Height;
    public int Revision { get; }

    public NavigationGrid(int width, int height, IEnumerable<GridCell>? blocked = null, int cellSize = WorldPosition.UnitsPerCell,
        IEnumerable<byte>? terrain = null, IEnumerable<SurfaceSample>? surface = null, IEnumerable<WorldBox>? obstacles = null)
    {
        if (cellSize < 1 || cellSize > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (width < 1 || width > WorldPosition.MaximumExtent / cellSize) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1 || height > WorldPosition.MaximumExtent / cellSize || (long)width * height > MaximumCells)
            throw new ArgumentOutOfRangeException(nameof(height), "Navigation layer exceeds its cell budget.");
        Width = width;
        Height = height;
        CellSize = cellSize;
        _blocked = new uint[(CellCount + 31) / 32];
        if (terrain is not null)
        {
            _terrain = terrain.ToArray();
            if (_terrain.Length != CellCount) throw new ArgumentException("Terrain count must match the grid.", nameof(terrain));
            MaximumTerrain = _terrain.Max();
        }
        _surface = surface?.ToArray();
        if (_surface is not null && (_surface.Length != CellCount || _surface.Any(v => v.Elevation is < -1048576 or > 1048576 || v.Slope < 0 || v.Depth < 0))) throw new ArgumentException("Invalid surface samples.", nameof(surface));
        _obstacles = obstacles?.Distinct().OrderBy(b => b.MinY).ThenBy(b => b.MinX).ThenBy(b => b.MaxY).ThenBy(b => b.MaxX).ToArray() ?? [];
        if (_obstacles.Length > 65536 || _obstacles.Any(b => b.MinX < 0 || b.MinY < 0 || b.MaxX > Width * CellSize || b.MaxY > Height * CellSize || b.MinX >= b.MaxX || b.MinY >= b.MaxY)) throw new ArgumentException("Invalid partial obstacles.", nameof(obstacles));
        var obstacleCells = new Dictionary<int, List<int>>();
        for (int i = 0; i < _obstacles.Length; i++)
        {
            var box = _obstacles[i];
            for (int y = box.MinY / CellSize; y <= Math.Min(Height - 1, box.MaxY / CellSize); y++)
                for (int x = box.MinX / CellSize; x <= Math.Min(Width - 1, box.MaxX / CellSize); x++)
                { int c = y * Width + x; if (!obstacleCells.TryGetValue(c, out var list)) obstacleCells.Add(c, list = []); list.Add(i); }
        }
        _obstacleCells = obstacleCells.ToDictionary(p => p.Key, p => p.Value.ToArray());
        foreach (var cell in blocked ?? [])
        {
            int index = Index(cell);
            _blocked[index >> 5] |= 1u << (index & 31);
        }
    }

    private NavigationGrid(NavigationGrid previous, int cell, bool blocked)
    {
        Width = previous.Width;
        Height = previous.Height;
        CellSize = previous.CellSize;
        Revision = checked(previous.Revision + 1);
        _terrain = previous._terrain; MaximumTerrain = previous.MaximumTerrain; _noStop = previous._noStop;
        _surface = previous._surface; _obstacles = previous._obstacles; _obstacleCells = previous._obstacleCells;
        _blocked = (uint[])previous._blocked.Clone();
        uint bit = 1u << (cell & 31);
        if (blocked) _blocked[cell >> 5] |= bit;
        else _blocked[cell >> 5] &= ~bit;
    }

    private NavigationGrid(NavigationGrid basis, uint[] blocked, byte[]? terrain, uint[]? noStop, int revision)
    {
        Width = basis.Width; Height = basis.Height; CellSize = basis.CellSize; Revision = revision;
        _blocked = blocked; _terrain = terrain; _noStop = noStop; MaximumTerrain = terrain?.Max() ?? 0;
        _surface = basis._surface; _obstacles = basis._obstacles; _obstacleCells = basis._obstacleCells;
    }
    /// <summary>Overlays valid cell indices on this base grid and compares against the current revision of the same grid.</summary>
    public NavigationGrid Compose(IReadOnlyDictionary<int, CellOverride> patches, NavigationGrid current)
    {
        var blocked = (uint[])_blocked.Clone(); var stop = _noStop is null ? new uint[blocked.Length] : (uint[])_noStop.Clone();
        byte[]? terrain = _terrain is null ? null : (byte[])_terrain.Clone();
        foreach (var (cell, patch) in patches)
        {
            uint bit = 1u << (cell & 31);
            if (patch.Traverse is { } traverse) { if (traverse) blocked[cell >> 5] &= ~bit; else blocked[cell >> 5] |= bit; }
            if (patch.CanStop is { } canStop) { if (canStop) stop[cell >> 5] &= ~bit; else stop[cell >> 5] |= bit; }
            if (patch.Terrain is { } type) { terrain ??= new byte[CellCount]; terrain[cell] = type; }
        }
        if (_terrain is null && terrain is not null && !terrain.Any(t => t != 0)) terrain = null;
        uint[]? finalStop = stop.Any(t => t != 0) ? stop : null;
        if (blocked.AsSpan().SequenceEqual(current._blocked) && Equal(terrain, current._terrain) && Equal(finalStop, current._noStop)) return current;
        return new(this, blocked, terrain, finalStop, checked(current.Revision + 1));
        static bool Equal<T>(T[]? a, T[]? b) where T : IEquatable<T> => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    }
    public bool CanStop(int cell) => !IsBlocked(cell) && (_noStop is null || (_noStop[cell >> 5] & (1u << (cell & 31))) == 0);
    public SurfaceSample SurfaceAt(int cell) => _surface?[cell] ?? default;
    public ReadOnlySpan<int> ObstaclesAt(int cell) => _obstacleCells.TryGetValue(cell, out var values) ? values : [];
    public WorldBox Obstacle(int index) => _obstacles[index];

    public int Index(GridCell cell)
    {
        if ((uint)cell.X >= (uint)Width || (uint)cell.Y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(cell));
        return cell.Y * Width + cell.X;
    }

    public int CellAt(WorldPosition position)
    {
        if (position.X < 0 || position.X > (long)Width * CellSize || position.Y < 0 || position.Y > (long)Height * CellSize)
            throw new ArgumentOutOfRangeException(nameof(position));
        return Math.Min(position.Y / CellSize, Height - 1) * Width + Math.Min(position.X / CellSize, Width - 1);
    }

    public WorldPosition Center(int index, int elevation = 0)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return new(index % Width * CellSize + CellSize / 2, index / Width * CellSize + CellSize / 2, elevation);
    }

    public bool IsBlocked(int index)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_blocked[index >> 5] & (1u << (index & 31))) != 0;
    }

    public byte TerrainAt(int index)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _terrain?[index] ?? 0;
    }

    public NavigationGrid WithBlocked(int index, bool blocked)
    {
        if (IsBlocked(index) == blocked) return this;
        return new(this, index, blocked);
    }

    public bool IsRectangleClear(int a, int b)
    {
        int minX = Math.Min(a % Width, b % Width), maxX = Math.Max(a % Width, b % Width);
        int minY = Math.Min(a / Width, b / Width), maxY = Math.Max(a / Width, b / Width);
        for (int y = minY; y <= maxY; y++)
        {
            int first = y * Width + minX, last = y * Width + maxX;
            int left = first >> 5, right = last >> 5;
            uint leftMask = uint.MaxValue << (first & 31), rightMask = uint.MaxValue >> (31 - (last & 31));
            if (left == right) { if ((_blocked[left] & leftMask & rightMask) != 0) return false; }
            else
            {
                if ((_blocked[left] & leftMask) != 0 || (_blocked[right] & rightMask) != 0) return false;
                for (int word = left + 1; word < right; word++) if (_blocked[word] != 0) return false;
            }
        }
        return true;
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(Revision);
        foreach (uint bits in _blocked) writer.Write(bits);
        writer.Write(HasTerrain);
        if (_terrain is not null) writer.Write(_terrain);
        writer.Write(_noStop is not null); if (_noStop is not null) foreach (uint bits in _noStop) writer.Write(bits);
        writer.Write(_surface is not null); if (_surface is not null) foreach (var sample in _surface) { writer.Write(sample.Elevation); writer.Write(sample.Slope); writer.Write(sample.Depth); }
        writer.Write(_obstacles.Length); foreach (var box in _obstacles) { writer.Write(box.MinX); writer.Write(box.MinY); writer.Write(box.MaxX); writer.Write(box.MaxY); }
    }
}
