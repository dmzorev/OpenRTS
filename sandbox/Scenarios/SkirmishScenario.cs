using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

public static class SkirmishScenario
{
    // Finite headless/replay journal; the interactive map starts without scripted orders.
    public static WorldCommand[] Commands() =>
    [
        new(1, 0, 1, CommandKind.PlanConstruction, new(2), new(22016, 12288), Action: "site-power", Queued: true),
        new(1, 0, 2, CommandKind.PlanConstruction, new(2), new(22528, 19968), Action: "site-barracks", Queued: true),
        new(1, 0, 3, CommandKind.UseAbility, new(5), TargetEntity: new(10), Action: "capture"),
        new(1, 0, 4, CommandKind.UseAbility, new(2), TargetEntity: new(9), Action: "capture", Queued: true)
    ];
}
