using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using System.Text.RegularExpressions;

namespace OpenRTS.Samples;

public static class FogScenario
{
    public static OpenRTS.Core.Orders.WorldCommand[] Commands => [new(1, 0, 1, OpenRTS.Core.Orders.CommandKind.Move, new(1), new(30 * 1024, 12 * 1024))];
    public static string AddVision(string yaml) => Regex.Replace(yaml, @"(?m)^([\w-]+):\s*$", "$0\n  Vision: {Radius: 6144}");
    public static SimulationWorld Create(UnitCatalog catalog)
    {
        var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 }, catalog.Units,
            ShowcaseScenario.Width * 1024, ShowcaseScenario.Height * 1024, navigationGrid: ShowcaseScenario.Grid(catalog.Locomotion),
            players: [new(1, "Blue"), new(2, "Red")]);
        try
        {
            int rifle = catalog.Units.ToList().FindIndex(t => t.Id == "rifleman");
            int tank = catalog.Units.ToList().FindIndex(t => t.Id == "tank");
            int bunker = catalog.Units.ToList().FindIndex(t => t.Id == "bunker-cannon");
            for (int player = 1; player <= 2; player++)
            {
                int x = player == 1 ? 8 : 35;
                world.AddInitialUnit(rifle, new(x * 1024, 12 * 1024), (uint)player);
                world.AddInitialUnit(tank, new((x + (player == 1 ? 3 : -3)) * 1024, 16 * 1024), (uint)player);
                world.AddInitialUnit(bunker, new(x * 1024, 20 * 1024), (uint)player);
            }
            world.SynchronizeVisibility();
            return world;
        }
        catch { world.Dispose(); throw; }
    }
}
