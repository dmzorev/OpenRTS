using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

/// <summary>Same build/ability journal for headless and Godot, including on-site construction.</summary>
public static class ProductionScenario
{
    public static WorldCommand[] Commands(UnitCatalog catalog) => [.. BuildingScenario.Commands(catalog),
        new(2, 0, 1, CommandKind.EnqueueTask, new(2), Action: "rifleman", Amount: 5),
        new(2, 0, 2, CommandKind.PlanConstruction, new(5), new(19 * 1024 + 512, 21 * 1024), Action: "site-power"),
        new(2, 0, 3, CommandKind.SetRally, new(2), new(47 * 1024, 18 * 1024)),
        new(3, 0, 1, CommandKind.SetAutoCast, new(12), Action: "grenade", Enabled: true),
        new(3, 0, 2, CommandKind.UseAbility, new(12), new(11 * 1024, 32 * 1024), Action: "grenade")];
}
