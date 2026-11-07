using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Samples;

public static class SkirmishScenario
{
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
