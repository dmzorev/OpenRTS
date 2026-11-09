using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using System.Text.RegularExpressions;

namespace OpenRTS.Samples;

public static class FogScenario
{
    public static OpenRTS.Core.Orders.WorldCommand[] Commands => [new(1, 0, 1, OpenRTS.Core.Orders.CommandKind.Move, new(1), new(30 * 1024, 12 * 1024))];
    public static string AddVision(string yaml) => Regex.Replace(yaml, @"(?m)^([\w-]+):\s*$", "$0\n  Vision: {Radius: 6144}");
    public static string AddSensorProfiles(string yaml) => Regex.Replace(yaml, @"(?m)^(rifleman|tank|bunker-cannon):$", match =>
        match.Value + "\n  Sensors:\n" + (match.Groups[1].Value switch
        {
            "rifleman" => """
                Signatures: [visual, radar.ground]
                Concealments:
                  - {Counter: forest, Terrain: forest}
            """,
            "tank" => """
                Signatures: [visual, radar.ground]
                Detectors:
                  - {Channel: optical, Signature: visual, Range: 5120, Display: Model, Counters: [forest]}
            """,
            _ => """
                Signatures: [visual]
                Detectors:
                  - {Channel: radar, Signature: radar.ground, Range: 24576}
                  - {Channel: radar, Signature: radar.ground, Range: 12288, Display: Identified}
            """
        }) + "\n");

    public static SimulationWorld Create(UnitCatalog catalog, int holdTicks = 0, bool sensors = false)
    {
        var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024, FogVisibilityHoldTicks = holdTicks }, catalog.Units,
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
                if (sensors)
                {
                    world.AddInitialUnit(rifle, new((player == 1 ? 15 : 10) * 1024, 6 * 1024), (uint)player);
                    world.AddInitialUnit(tank, new((player == 1 ? 18 : 34) * 1024, 14 * 1024), (uint)player);
                    world.AddInitialUnit(bunker, new((player == 1 ? 16 : 35) * 1024, 20 * 1024), (uint)player);
                    continue;
                }
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
