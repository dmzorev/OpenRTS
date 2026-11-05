using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

/// <summary>Footprint/size and presentation laboratory; no economy or real production.</summary>
public static class BuildingScenario
{
    public const int Width = 80, Height = 56, UnitCount = 21, VerificationTicks = 24;
    public static readonly string[] Placeable = ["power-plant", "barracks", "factory", "headquarters", "construction-yard", "mine", "oil-derrick", "nature-trees", "nature-rocks", "nature-cliff", "nature-debris", "bunker-cannon", "bunker-mixed"];
    public static string TerrainAt(int x, int y)
    {
        if (x >= 62 && y >= 37 && y < 51) return "water";
        if (y is 11 or 12 or 26 or 27 or 42 || x is 14 or 26 or 38 or 53 or 66) return "road";
        return "grass";
    }
    public static NavigationGrid Grid(LocomotionCatalog catalog) => new(Width, Height, terrain:
        from y in Enumerable.Range(0, Height) from x in Enumerable.Range(0, Width) select catalog.TerrainIndex(TerrainAt(x, y)));
    public static SimulationWorld Create(UnitCatalog catalog, int workers = 1)
    {
        var world = new SimulationWorld(catalog.Units, Width * 1024, Height * 1024, navigationGrid: Grid(catalog.Locomotion!), navigationWorkers: workers);
        try
        {
            void Add(string name, int twiceX, int twiceY, uint owner = 1, bool working = false, bool damaged = false)
            {
                int type = catalog.Units.ToList().FindIndex(u => u.Id == name); if (type < 0) throw new ArgumentException("Missing building fixture type: " + name);
                uint states = (working ? catalog.Units[type].StateMask("working") : 0) | (damaged ? catalog.Units[type].StateMask("damaged") : 0);
                world.AddInitialUnit(type, new(twiceX * 512, twiceY * 512), owner, states);
            }
            Add("power-plant", 65, 24, working: true); Add("barracks", 84, 25);
            Add("factory", 107, 24, working: true); Add("headquarters", 132, 25, working: true);
            Add("construction-yard", 29, 52, working: true); Add("mine", 53, 52, working: true); Add("oil-derrick", 77, 53, working: true);
            Add("nature-trees", 103, 56, 0); Add("nature-rocks", 120, 56, 0); Add("nature-cliff", 137, 57, 0); Add("nature-debris", 151, 56, 0);
            Add("light-infantry", 16, 64); Add("medium-apc", 22, 64); Add("large-tank", 30, 64); Add("box-vehicle", 38, 64); Add("engineer", 46, 64);
            Add("power-plant", 23, 84); Add("power-plant", 43, 84, damaged: true); Add("power-plant", 63, 84, working: true);
            Add("power-plant", 83, 84, working: true, damaged: true); Add("power-plant", 103, 84);
            return world;
        }
        catch { world.Dispose(); throw; }
    }
    public static WorldCommand[] Commands(UnitCatalog catalog)
    {
        int Type(string id) => catalog.Units.ToList().FindIndex(u => u.Id == id);
        return [new(1, 0, 1, CommandKind.Destroy, new(21)),
            new(1, 0, 2, CommandKind.PlayAnimation, new(5), Animation: catalog.Units[Type("construction-yard")].AnimationIndex("construct"), DurationTicks: 35, Track: OpenRTS.Core.Presentation.AnimationTrack.Body),
            new(1, 0, 3, CommandKind.PlayAnimation, new(2), Animation: catalog.Units[Type("barracks")].AnimationIndex("door"), DurationTicks: 40),
            new(1, 0, 4, CommandKind.Move, new(12), new(42 * 1024, 12544))];
    }
}
