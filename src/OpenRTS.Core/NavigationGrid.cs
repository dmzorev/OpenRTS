namespace OpenRTS.Core;

public readonly record struct GridCell(int X, int Y);

/// <summary>Immutable one-cell navigation layer. Readers can safely share it with workers and views.</summary>
public sealed class NavigationGrid
{
    public const int MaximumCells = 16 * 1024 * 1024;
    private readonly uint[] _blocked;
    public int Width { get; }
    public int Height { get; }
    public int CellCount => Width * Height;
    public int Revision { get; }

    public NavigationGrid(int width, int height, IEnumerable<GridCell>? blocked = null)
    {
        if (width < 1 || width > WorldPosition.MaximumExtent / 1024) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1 || height > WorldPosition.MaximumExtent / 1024 || (long)width * height > MaximumCells)
            throw new ArgumentOutOfRangeException(nameof(height), "Navigation layer exceeds its cell budget.");
        Width = width;
        Height = height;
        _blocked = new uint[(CellCount + 31) / 32];
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
        Revision = checked(previous.Revision + 1);
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
        if (position.X < 0 || position.X > (long)Width * 1024 || position.Y < 0 || position.Y > (long)Height * 1024)
            throw new ArgumentOutOfRangeException(nameof(position));
        return Math.Min(position.Y / 1024, Height - 1) * Width + Math.Min(position.X / 1024, Width - 1);
    }

    public WorldPosition Center(int index, int elevation = 0)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return new(index % Width * 1024 + 512, index / Width * 1024 + 512, elevation);
    }

    public bool IsBlocked(int index)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_blocked[index >> 5] & (1u << (index & 31))) != 0;
    }

    public NavigationGrid WithBlocked(int index, bool blocked)
    {
        if (IsBlocked(index) == blocked) return this;
        return new(this, index, blocked);
    }

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(Revision);
        foreach (uint bits in _blocked) writer.Write(bits);
    }
}
