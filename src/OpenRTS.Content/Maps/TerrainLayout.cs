using OpenRTS.Core;

namespace OpenRTS.Content;

/// <summary>A cell in a static brush. Null fields retain the previous value.</summary>
public readonly record struct TerrainBrushCell(int X, int Y, byte? Terrain = null, string? Visual = null);

/// <summary>Builds one static terrain layer in placement order, before the world is created.</summary>
public sealed class TerrainLayout
{
    private readonly byte[] _terrain;
    private readonly string[] _visual;
    public int Width { get; }
    public int Height { get; }

    public TerrainLayout(int width, int height, IEnumerable<byte> terrain, IEnumerable<string> visual)
    {
        if (width < 1 || height < 1 || (long)width * height > NavigationGrid.MaximumCells)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _terrain = terrain.ToArray();
        _visual = visual.ToArray();
        if (_terrain.Length != width * height || _visual.Length != _terrain.Length ||
            _visual.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Terrain and visual layers must cover the map.");
    }

    /// <summary>Applies a rectangular brush atomically. Rotation is clockwise in quarter turns.</summary>
    public void Apply(int x, int y, int width, int height, IEnumerable<TerrainBrushCell> cells, int quarterTurns = 0)
    {
        if (width < 1 || height < 1 || width > Width || height > Height || quarterTurns is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(width));
        var values = cells.ToArray();
        if (values.Any(c => c.X < 0 || c.X >= width || c.Y < 0 || c.Y >= height ||
                            c.Visual is not null && string.IsNullOrWhiteSpace(c.Visual)) ||
            values.Select(c => (c.X, c.Y)).Distinct().Count() != values.Length)
            throw new ArgumentException("Brush cells must be unique and lie inside its dimensions.");
        int resultWidth = quarterTurns % 2 == 0 ? width : height;
        int resultHeight = quarterTurns % 2 == 0 ? height : width;
        if (x < 0 || y < 0 || (long)x + resultWidth > Width || (long)y + resultHeight > Height)
            throw new ArgumentOutOfRangeException(nameof(x), "The brush must fit entirely on the map.");
        foreach (var cell in values)
        {
            var (dx, dy) = quarterTurns switch
            {
                1 => (height - 1 - cell.Y, cell.X),
                2 => (width - 1 - cell.X, height - 1 - cell.Y),
                3 => (cell.Y, width - 1 - cell.X),
                _ => (cell.X, cell.Y)
            };
            int index = (y + dy) * Width + x + dx;
            if (cell.Terrain is { } type) _terrain[index] = type;
            if (cell.Visual is { } visual) _visual[index] = visual;
        }
    }

    public NavigationGrid CreateGrid() => new(Width, Height, terrain: _terrain);
    public byte TerrainAt(int x, int y) => _terrain[Index(x, y)];
    public string VisualAt(int x, int y) => _visual[Index(x, y)];
    public string[] VisualIds() => _visual.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private int Index(int x, int y) =>
        (uint)x < Width && (uint)y < Height ? y * Width + x : throw new ArgumentOutOfRangeException(nameof(x));
}
