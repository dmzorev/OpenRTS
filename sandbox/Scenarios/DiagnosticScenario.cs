using OpenRTS.Core;

using OpenRTS.Content;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

/// <summary>Shared host fixture, deliberately outside the simulation's gameplay API.</summary>
public static class DiagnosticScenario
{
    public const int DefaultTicks = 64;
    public static int SideCells(int count) => (int)Math.Ceiling(Math.Sqrt(count)) + 8;

    public static SimulationWorld Create(UnitCatalog catalog, int count)
    {
        int[] movableTypes = Enumerable.Range(0, catalog.Units.Count).Where(t => catalog.Units[t].Movable).ToArray();
        if (movableTypes.Length == 0) throw new ArgumentException("The movement fixture requires a movable unit.", nameof(catalog));
        if (count < 1 || count > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        var world = new SimulationWorld(catalog.Units, 4096 * WorldPosition.UnitsPerCell,
            4096 * WorldPosition.UnitsPerCell, initialCapacity: count, seed: 12345);
        try
        {
            for (int i = 0; i < count; i++)
                world.AddInitialUnit(movableTypes[i % movableTypes.Length], new((i % columns + 4) * WorldPosition.UnitsPerCell,
                    (i / columns + 4) * WorldPosition.UnitsPerCell));
            return world;
        }
        catch { world.Dispose(); throw; }
    }

    public static WorldCommand[] Commands(int count, long ticks)
    {
        int side = SideCells(count) * WorldPosition.UnitsPerCell;
        return new WorldCommand[]
        {
            new(1, 0, 1, CommandKind.Scatter, Position: new(side, side)),
            new(21, 0, 2, CommandKind.MoveAll, Position: new(side * 3 / 4, side / 4)),
            new(41, 0, 3, CommandKind.Scatter, Position: new(side, side))
        }.Where(command => command.Tick <= ticks).ToArray();
    }

    public static SimulationReport Run(SimulationWorld world, long ticks, WorldCommand[] commands)
    {
        if (ticks < world.Tick) throw new ArgumentOutOfRangeException(nameof(ticks));
        var ordered = commands.OrderBy(c => c.Tick).ToArray();
        int cursor = 0;
        while (world.Tick < ticks)
        {
            int start = cursor;
            while (cursor < ordered.Length && ordered[cursor].Tick == world.Tick + 1) cursor++;
            world.Step(ordered.AsSpan(start, cursor - start));
        }
        return world.Report();
    }
}
