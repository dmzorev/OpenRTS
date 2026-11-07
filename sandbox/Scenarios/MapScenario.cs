using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

public static class MapScenario
{
    public const string DefaultMap = "content/maps/skirmish.yaml";
    public const string DefaultStartSet = "content/start-sets/construction.yaml";

    public static MapMatch Load(UnitCatalog catalog, Func<string, ContentSource> read, string mapPath, string? startSetPath = null)
    {
        var map = GameMap.Load(read(mapPath), catalog);
        if (map.Slots.Count > 2)
            throw new ArgumentException($"Map '{map.Id}' declares {map.Slots.Count} slots. The sandbox supports up to two players.");
        if (!map.IsSkirmish && startSetPath is not null)
            throw new ArgumentException($"Map '{map.Id}' has fixed placements and does not accept a starting set.");

        var players = new Dictionary<string, PlayerDefinition>(StringComparer.Ordinal);
        for (int i = 0; i < map.Slots.Count; i++)
            players.Add(map.Slots[i], new((uint)i + 1, i == 0 ? "Синий" : "Красный"));

        return map.Prepare(players, map.IsSkirmish ? StartingSet.Load(read(startSetPath ?? DefaultStartSet)) : null);
    }
}
