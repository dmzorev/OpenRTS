namespace OpenRTS.Core;

public readonly record struct GridCell(int X, int Y);

/// <summary>Immutable terrain sampling layer, independent of exact body positions and collision bins.</summary>
public sealed class NavigationGrid
{
    public const int MaximumCells = 16 * 1024 * 1024;
    private readonly uint[] _blocked;
    private readonly byte[]? _terrain;
    public bool HasTerrain => _terrain is not null;
    public int MaximumTerrain { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public int CellCount => Width * Height;
    public int Revision { get; }

    public NavigationGrid(int width, int height, IEnumerable<GridCell>? blocked = null, int cellSize = WorldPosition.UnitsPerCell,
        IEnumerable<byte>? terrain = null)
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
        _terrain = previous._terrain; MaximumTerrain = previous.MaximumTerrain;
        _blocked = (uint[])previous._blocked.Clone();
        uint bit = 1u << (cell & 31);
        if (blocked) _blocked[cell >> 5] |= bit;
        else _blocked[cell >> 5] &= ~bit;
    }

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

    internal bool IsRectangleClear(int a, int b)
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

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(Revision);
        foreach (uint bits in _blocked) writer.Write(bits);
        writer.Write(HasTerrain);
        if (_terrain is not null) writer.Write(_terrain);
    }
}
