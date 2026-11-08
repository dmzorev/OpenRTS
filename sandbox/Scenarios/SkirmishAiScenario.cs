using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

internal static class SkirmishAiScenario
{
    public const int DefaultTicks = 1800;
    public const string ProfileId = "infantry-skirmish";

    public static SkirmishAi Create(UnitCatalog catalog, SkirmishAiProfile profile, uint player, bool blockFirstSite = false, string? startSlot = null)
    {
        // These sites belong to sandbox-skirmish and its construction starting set.
        bool west = (startSlot ?? (player == 1 ? "blue" : "red")) switch
        {
            "blue" => true,
            "red" => false,
            _ => throw new ArgumentException("AI1 requires a valley starting slot.", nameof(startSlot))
        };
        WorldPosition[] sites = west
            ? [new(22016, 12288), new(22528, 19968), new(13824, 23552), new(10240, 17920)]
            : [new(59904, 45056), new(59392, 37376), new(68096, 33792), new(71680, 39424)];
        if (blockFirstSite) sites[0] = new(512, 512); // Mountain border.
        return new(player, profile, catalog.Units, sites,
            west ? new(26624, 11264) : new(55296, 46080),
            west ? new(63936, 45056) : new(17984, 12288));
    }

    public static SimulationReport Run(SimulationWorld world, SkirmishAi ai, int ticks,
        Action<SimulationWorld>? onTick = null, CancellationToken cancellationToken = default)
    {
        while (world.Tick < ticks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commands = ai.Plan(world);
            world.Step(commands, cancellationToken);
            onTick?.Invoke(world);
        }
        return world.Report();
    }
}
