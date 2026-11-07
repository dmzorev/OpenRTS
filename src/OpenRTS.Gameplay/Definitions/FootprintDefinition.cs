using System.Collections.ObjectModel;

namespace OpenRTS.Core.Navigation;

public readonly record struct FootprintCell(int X, int Y, CellOverride Navigation, bool Targetable = true);

/// <summary>World-aligned ground cells, centered on the entity. No render sizes or resources.</summary>
public sealed class FootprintDefinition
{
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public ReadOnlyCollection<FootprintCell> Cells { get; }
    public ReadOnlyCollection<WorldBox> TargetBounds { get; }
    public int HalfWidth => Width * CellSize / 2;
    public int HalfHeight => Height * CellSize / 2;
    public FootprintDefinition(int width, int height, IEnumerable<FootprintCell> cells, int cellSize = 1024)
    {
        if (width is < 1 or > 32 || height is < 1 or > 32 || cellSize is < 128 or > 4096 || (cellSize & 1) != 0) throw new ArgumentOutOfRangeException(nameof(width));
        var values = cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToArray();
        if (values.Length == 0 || values.Any(c => (uint)c.X >= width || (uint)c.Y >= height || c.Navigation.Traverse == false && c.Navigation.CanStop == true) || values.Select(c => (c.X, c.Y)).Distinct().Count() != values.Length)
            throw new ArgumentException("Footprint needs unique in-bounds cells and consistent movement permissions.", nameof(cells));
        Width = width; Height = height; CellSize = cellSize; Cells = Array.AsReadOnly(values);
        var boxes = new List<WorldBox>();
        for (int y = 0; y < height; y++) for (int x = 0; x < width;)
        {
            if (!values.Any(c => c.X == x && c.Y == y && c.Targetable)) { x++; continue; }
            int start = x++;
            while (x < width && values.Any(c => c.X == x && c.Y == y && c.Targetable)) x++;
            var box = new WorldBox(start * cellSize - HalfWidth, y * cellSize - HalfHeight, x * cellSize - HalfWidth, (y + 1) * cellSize - HalfHeight);
            int prior = boxes.FindIndex(b => b.MinX == box.MinX && b.MaxX == box.MaxX && b.MaxY == box.MinY);
            if (prior < 0) boxes.Add(box); else boxes[prior] = boxes[prior] with { MaxY = box.MaxY };
        }
        TargetBounds = Array.AsReadOnly(boxes.ToArray());
    }
    public WorldBox Bounds(FootprintCell cell, WorldPosition position) => new(position.X - HalfWidth + cell.X * CellSize, position.Y - HalfHeight + cell.Y * CellSize,
        position.X - HalfWidth + (cell.X + 1) * CellSize, position.Y - HalfHeight + (cell.Y + 1) * CellSize);
    public bool AllowsConstruction(NavigationGrid grid, WorldPosition position, Locomotor profile)
    {
        int size = grid.CellSize;
        int left = position.X - HalfWidth, top = position.Y - HalfHeight;
        if (left < 0 || top < 0 || position.X + HalfWidth > (long)grid.Width * size ||
            position.Y + HalfHeight > (long)grid.Height * size ||
            left % size != 0 || top % size != 0 || CellSize % size != 0) return false;
        foreach (var cell in Cells)
        {
            var box = Bounds(cell, position);
            for (int y = box.MinY / size; y < box.MaxY / size; y++)
                for (int x = box.MinX / size; x < box.MaxX / size; x++)
                {
                    int index = y * grid.Width + x;
                    int terrain = grid.TerrainAt(index);
                    bool canStop = terrain < profile.Rules.Count ? profile.At(grid, index).CanStop :
                        ReferenceEquals(profile, Locomotor.Default) || profile.Rules.Count == 1 && profile.Rules[0] == TerrainRule.Ground;
                    if (grid.IsBlocked(index) || !grid.CanStop(index) || !canStop || grid.ObstaclesAt(index).Length != 0) return false;
                }
        }
        return true;
    }
    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(Cells.Count);
        foreach (var cell in Cells)
        {
            writer.Write(cell.X); writer.Write(cell.Y); writer.Write(cell.Targetable);
            writer.Write(cell.Navigation.Traverse.HasValue); if (cell.Navigation.Traverse is { } t) writer.Write(t);
            writer.Write(cell.Navigation.CanStop.HasValue); if (cell.Navigation.CanStop is { } s) writer.Write(s);
            writer.Write(cell.Navigation.Terrain.HasValue); if (cell.Navigation.Terrain is { } r) writer.Write(r);
        }
    }
}
