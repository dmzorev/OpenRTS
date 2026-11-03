using OpenRTS.Core;

namespace OpenRTS.Content;

/// <summary>Mass orders on a fully allocated navigation layer, with a wall and a four-cell opening.</summary>
public static class NavigationScenario
{
    public static NavigationGrid Grid(int count)
    {
        if (count < 1 || count > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int width = columns * 2 + 32, height = columns + 32, wall = columns + 16;
        return new NavigationGrid(width, height, Enumerable.Range(0, height)
            .Where(y => y < height / 2 - 2 || y >= height / 2 + 2).Select(y => new GridCell(wall, y)));
    }

    public static SimulationWorld Create(UnitCatalog catalog, int count, int workers = 4, bool reverseJobs = false)
    {
        var grid = Grid(count);
        var world = new SimulationWorld(catalog.Units, grid.Width * 1024, grid.Height * 1024, seed: 12345,
            initialCapacity: count, navigationGrid: grid, navigationWorkers: workers, fieldsPerTick: 4,
            reverseNavigationJobs: reverseJobs);
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            world.AddInitialUnit(i % catalog.Units.Count, grid.Center(grid.Index(new(i % columns + 4, i / columns + 4))));
        return world;
    }

    public static WorldCommand[] Commands(int count, long ticks)
    {
        var grid = Grid(count);
        // Four shared goals test actual parallel field construction instead of 10000 private searches.
        return Enumerable.Range(0, count).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move,
            new((uint)i + 1), grid.Center(grid.Index(new(grid.Width - 5, grid.Height / 2 - 6 + i % 4 * 4)))))
            .Concat(ticks >= 41 ? new[] { new WorldCommand(41, 0, count + 1, CommandKind.MoveAll,
                Position: grid.Center(grid.Index(new(grid.Width - 8, grid.Height / 2)))) } : []).ToArray();
    }
}
