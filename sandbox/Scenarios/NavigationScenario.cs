using OpenRTS.Core;

using OpenRTS.Content;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

/// <summary>Mass orders on a fully allocated navigation layer, with a wall and a four-cell opening.</summary>
public static class NavigationScenario
{
    public static NavigationGrid Grid(int count)
    {
        if (count < 1 || count > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int width = columns * 3 + 32, height = columns * 2 + 32, wall = columns + 16;
        return new NavigationGrid(width, height, Enumerable.Range(0, height)
            .Where(y => y < height / 2 - 2 || y >= height / 2 + 2).Select(y => new GridCell(wall, y)));
    }

    public static SimulationWorld Create(UnitCatalog catalog, int count, int workers = 4, bool reverseJobs = false)
    {
        int[] movableTypes = Enumerable.Range(0, catalog.Units.Count).Where(t => catalog.Units[t].Movable).ToArray();
        if (movableTypes.Length == 0) throw new ArgumentException("The movement fixture requires a movable unit.", nameof(catalog));
        var grid = Grid(count);
        var world = new SimulationWorld(catalog.Units, grid.Width * 1024, grid.Height * 1024, seed: 12345,
            initialCapacity: count, navigationGrid: grid, navigationWorkers: workers, fieldsPerTick: 4,
            reverseNavigationJobs: reverseJobs);
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            world.AddInitialUnit(movableTypes[i % movableTypes.Length], grid.Center(grid.Index(new(i % columns + 4, i / columns + 4))));
        return world;
    }

    public static WorldCommand[] Commands(int count, long ticks)
    {
        var grid = Grid(count);
        return [new(1, 0, 1, CommandKind.MoveGroup,
            Position: grid.Center(grid.Index(new(grid.Width - 8, grid.Height / 2))))];
    }
}
