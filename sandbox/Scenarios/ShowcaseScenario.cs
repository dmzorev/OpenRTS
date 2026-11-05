using OpenRTS.Core;

using OpenRTS.Content;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

/// <summary>Presentation fixture; terrain types are visual here, not locomotor rules.</summary>
public static class ShowcaseScenario
{
    public const int Width = 48, Height = 32, TileSize = 4, UnitCount = 20;
    public static readonly string[] TerrainIds = ["road", "grass", "mud", "water", "forest", "swamp", "mountains"];
    public static string TerrainAt(int x, int y)
    {
        if (x >= 10 && y == 0 || x == 11 && y == 7) return "mountains";
        if (x >= 10 && y >= 1 && y <= 4) return "water";
        if (x == 9 && y >= 1 && y <= 4) return "swamp";
        if (y == 3 || x == 5) return "road";
        if (x < 3 && y < 2) return "forest";
        if (x >= 2 && x <= 4 && y >= 6) return "mud";
        return "grass";
    }
    public static NavigationGrid Grid(LocomotionCatalog? locomotion = null) => new(Width, Height,
        from y in Enumerable.Range(0, Height) from x in Enumerable.Range(0, Width)
        where TerrainAt(x / TileSize, y / TileSize) == "mountains" select new GridCell(x, y), terrain: locomotion is null ? null :
            from y in Enumerable.Range(0, Height) from x in Enumerable.Range(0, Width)
            select locomotion.TerrainIndex(TerrainAt(x / TileSize, y / TileSize)));
    public static SimulationWorld Create(UnitCatalog catalog)
    {
        var world = new SimulationWorld(catalog.Units, Width * 1024, Height * 1024,
            initialCapacity: UnitCount, navigationGrid: Grid(catalog.Locomotion));
        string[] order = ["rifleman", "rocketeer", "apc-mg", "apc-rocket", "tank"];
        for (int team = 0; team < 2; team++) for (int i = 0; i < order.Length; i++)
        {
            int type = catalog.Units.ToList().FindIndex(u => u.Id == order[i]);
            if (type < 0) throw new ArgumentException("Showcase unit is missing: " + order[i]);
            int x = team == 0 && i == 3 ? 42 : 7 + i * 6, y = team == 0 ? 12 : 23;
            uint states = team == 0 && i == 3 ? catalog.Units[type].StateMask("swimming") : 0;
            world.AddInitialUnit(type, new(x * 1024, y * 1024), (uint)(team + 1), states);
        }
        string[] extras = ["rifleman", "engineer", "tank-mixed", "bunker-cannon", "bunker-mixed"];
        for (int team = 0; team < 2; team++) for (int i = 0; i < extras.Length; i++)
        {
            int type = catalog.Units.ToList().FindIndex(u => u.Id == extras[i]);
            world.AddInitialUnit(type, new((18 + i * 4) * 1024, (team == 0 ? 6 : 25) * 1024), (uint)(team + 1));
        }
        return world;
    }
    public static WorldCommand[] VerificationCommands(UnitCatalog catalog)
    {
        int apc = catalog.Units.ToList().FindIndex(u => u.Id == "apc-mg"); var type = catalog.Units[apc];
        return [new(1, 0, 1, CommandKind.Move, new(3), new(21 * 1024, 16 * 1024)),
            new(1, 0, 2, CommandKind.Aim, new(3), TargetEntity: new(8)),
            new(1, 0, 3, CommandKind.SetVisualStates, new(3), States: type.StateMask("damaged") | type.StateMask("swimming")),
            new(1, 0, 4, CommandKind.PlayAnimation, new(3), Animation: type.AnimationIndex("fire"), DurationTicks: 60)];
    }
}
