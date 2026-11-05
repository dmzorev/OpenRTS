using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

public static class SkirmishScenario
{
    public const int UnitCount = 12;
    public static string TerrainAt(int x, int y)
    {
        if (x is 0 or 79 || y is 0 or 55) return "mountains";
        if (x is >= 35 and <= 43 && y is >= 33 and <= 39) return "water";
        return y is 14 or 28 or 40 || x is 16 or 40 or 64 ? "road" : "grass";
    }
    public static NavigationGrid Grid(LocomotionCatalog catalog) => new(80, 56, terrain:
        from y in Enumerable.Range(0, 56) from x in Enumerable.Range(0, 80) select catalog.TerrainIndex(TerrainAt(x, y)));
    public static SimulationWorld Create(UnitCatalog catalog, int workers = 1)
    {
        var world = new SimulationWorld(catalog.Units, 81920, 57344, navigationGrid: Grid(catalog.Locomotion!), navigationWorkers: workers,
            players: [new(1, "Синий", 5000), new(2, "Красный", 5000)]);
        try
        {
            void Add(string id, int x2, int y2, uint owner = 0) => world.AddInitialUnit(catalog.Units.ToList().FindIndex(u => u.Id == id), new(x2 * 512, y2 * 512), owner);
            Add("construction-yard", 27, 24, 1); Add("engineer", 34, 30, 1);
            Add("construction-yard", 133, 88, 2); Add("engineer", 126, 82, 2);
            Add("mine", 65, 48); Add("mine", 93, 56); Add("oil-derrick", 79, 37); Add("oil-derrick", 55, 77);
            Add("nature-trees", 53, 32); Add("nature-cliff", 105, 71); Add("nature-rocks", 84, 84); Add("nature-debris", 39, 56);
            return world;
        }
        catch { world.Dispose(); throw; }
    }
    // Finite headless/replay journal; the interactive map starts without scripted orders.
    public static WorldCommand[] Commands() =>
    [
        new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "power"),
        new(1, 0, 2, CommandKind.UseAbility, new(2), TargetEntity: new(5), Action: "capture"),
        new(1, 0, 3, CommandKind.UseAbility, new(4), TargetEntity: new(8), Action: "capture"),
        new(130, 0, 1, CommandKind.PlaceProduced, new(1), new(22016, 12288), Action: "power", TaskId: 1),
        new(131, 0, 1, CommandKind.EnqueueTask, new(1), Action: "barracks"),
        new(260, 0, 1, CommandKind.PlaceProduced, new(1), new(23552, 19968), Action: "barracks", TaskId: 2)
    ];
}
