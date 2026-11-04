namespace OpenRTS.Core;

internal static class GridNeighbors
{
    internal static int Neighbor(NavigationGrid grid, int cell, int direction) => direction switch
    {
        0 => cell >= grid.Width ? cell - grid.Width : -1,
        1 => cell % grid.Width > 0 ? cell - 1 : -1,
        2 => cell % grid.Width + 1 < grid.Width ? cell + 1 : -1,
        _ => cell + grid.Width < grid.CellCount ? cell + grid.Width : -1
    };
}
