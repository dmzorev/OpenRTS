using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

internal static class SkirmishAiScenario
{
    public const int DefaultTicks = 1800;
    public const string ProfileId = "infantry-skirmish";
    public const string MechanizedProfileId = "mechanized-skirmish";

    public static SkirmishAi Create(UnitCatalog catalog, SkirmishAiProfile profile, GameMap map, uint player,
        IReadOnlyList<string>? assignedSlots = null, IBuildingSiteSearch? siteSearch = null)
    {
        var slots = assignedSlots ?? map.Slots;
        if (!map.IsSkirmish || player == 0 || player > slots.Count || slots.Count < 2)
            throw new ArgumentException("AI requires a skirmish map with at least two assigned starts.", nameof(map));

        var center = map.StartPosition(slots[(int)player - 1]);
        var enemy = slots.Where((_, index) => index != (int)player - 1).Select(map.StartPosition)
            .OrderBy(p => Distance(center, p)).First();
        int distance = Math.Max(Math.Abs(enemy.X - center.X), Math.Abs(enemy.Y - center.Y));
        int rallyDistance = Math.Min(10 * map.Grid.CellSize, distance / 3);
        int attackDistance = Math.Min(4 * map.Grid.CellSize, distance / 4);
        return new(player, profile, catalog.Units, center, Toward(center, enemy, rallyDistance),
            Toward(enemy, center, attackDistance), siteSearch);

        static long Distance(WorldPosition a, WorldPosition b)
        {
            long x = (long)a.X - b.X, y = (long)a.Y - b.Y;
            return x * x + y * y;
        }

        WorldPosition Toward(WorldPosition from, WorldPosition to, int step) => distance == 0 ? from :
            new(from.X + (int)((long)(to.X - from.X) * step / distance),
                from.Y + (int)((long)(to.Y - from.Y) * step / distance));
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
