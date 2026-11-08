using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Content;
using DeclarativeContent;

namespace OpenRTS.Samples;

public static class AviationScenario
{
    public const string Map = "content/maps/aviation.yaml";
    public const int Ticks = 600;
    public static MapMatch Load(UnitCatalog catalog, Func<string, ContentSource> read) =>
        GameMap.Load(read(Map), catalog).Prepare(new Dictionary<string, PlayerDefinition>(StringComparer.Ordinal)
        {
            ["blue"] = new(1, "Синий", 12000), ["red"] = new(2, "Красный", 12000)
        });
    public static WorldCommand[] Commands =>
    [
        new(1, 0, 1, CommandKind.Move, new(4), new(48 * 1024, 20 * 1024)),
        new(1, 0, 2, CommandKind.Move, new(5), new(46 * 1024, 36 * 1024)),
        new(1, 0, 3, CommandKind.SetRally, new(3), new(30 * 1024, 25 * 1024)),
        new(2, 0, 1, CommandKind.EnqueueTask, new(2), Action: "fighter", Amount: 4),
        new(2, 0, 2, CommandKind.EnqueueTask, new(3), Action: "tiltrotor"),
        new(180, 0, 1, CommandKind.AttackTarget, new(4), TargetEntity: new(10)),
        new(180, 0, 2, CommandKind.AttackTarget, new(5), TargetEntity: new(10)),
        new(400, 0, 1, CommandKind.ReturnToBase, new(5))
    ];
}
