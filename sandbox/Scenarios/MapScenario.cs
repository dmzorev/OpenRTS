using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

public static class MapScenario
{
    public const string DefaultMap = "content/maps/skirmish.yaml";
    public const string DefaultStartSet = "content/start-sets/supply.yaml";
    public const string InfantryStartSet = "content/start-sets/infantry.yaml";
    public const string CrossroadsMap = "content/maps/crossroads.yaml";
    public const string RiversideMap = "content/maps/riverside-station.yaml";
    public const int MaximumPlayers = 4;

    private static readonly string[] PlayerNames = ["Синий", "Красный", "Зелёный", "Золотой"];

    public static string PlayerName(int index) => PlayerNames[index];

    public static MapMatch Prepare(GameMap map, StartingSet? startingSet, IReadOnlyList<string> playerSlots)
    {
        if (playerSlots.Count > MaximumPlayers)
            throw new ArgumentException($"The sandbox supports up to {MaximumPlayers} players.");
        var players = new Dictionary<string, PlayerDefinition>(StringComparer.Ordinal);
        for (int i = 0; i < playerSlots.Count; i++)
            if (!players.TryAdd(playerSlots[i], new((uint)i + 1, PlayerNames[i])))
                throw new ArgumentException($"Start '{playerSlots[i]}' is assigned more than once.");
        return map.Prepare(players, startingSet);
    }

    public static MapMatch Load(UnitCatalog catalog, Func<string, ContentSource> read, string mapPath, string? startSetPath = null)
    {
        var map = GameMap.Load(read(mapPath), catalog);
        if (!map.IsSkirmish && startSetPath is not null)
            throw new ArgumentException($"Map '{map.Id}' has fixed placements and does not accept a starting set.");
        var startingSet = map.IsSkirmish ? StartingSet.Load(read(startSetPath ?? DefaultStartSet)) : null;
        return Prepare(map, startingSet, map.Slots);
    }
}
