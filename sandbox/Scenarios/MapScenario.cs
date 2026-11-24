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
	public const int MaximumPlayers = 8;

	private static readonly string[] PlayerNames =
		["Синий", "Красный", "Зелёный", "Золотой", "Бирюзовый", "Фиолетовый", "Оранжевый", "Розовый"];

	public static string PlayerName(int index) => PlayerNames[index];

	public static MapMatch Prepare(GameMap map, StartingSet? startingSet, IReadOnlyList<string> playerSlots, IReadOnlyList<bool>? activePlayers = null)
	{
		if (playerSlots.Count > MaximumPlayers)
			throw new ArgumentException($"The sandbox supports up to {MaximumPlayers} players.");
		if (activePlayers is not null && activePlayers.Count != playerSlots.Count) throw new ArgumentException("Active slot count mismatch.");
		var players = new Dictionary<string, PlayerDefinition>(StringComparer.Ordinal);
		for (int i = 0; i < playerSlots.Count; i++)
			if ((activePlayers is null || activePlayers[i]) && !players.TryAdd(playerSlots[i], new((uint)i + 1, PlayerNames[i])))
				throw new ArgumentException($"Start '{playerSlots[i]}' is assigned more than once.");
		return map.Prepare(players, startingSet);
	}

	public static MapMatch Load(UnitCatalog catalog, Func<string, ContentSource> read, string mapPath, string? startSetPath = null,
		string? defaultStartSetPath = null)
	{
		var input = read(mapPath);
		var map = SceneMapBundle.IsPath(mapPath) ? SceneMapBundle.Read(input).LoadMap(mapPath, read, catalog).Map : GameMap.Load(input, catalog);
		return PrepareDefault(map, read, startSetPath, defaultStartSetPath);
	}

	public static MapMatch PrepareDefault(GameMap map, Func<string, ContentSource> read, string? startSetPath, string? defaultStartSetPath)
	{
		if (!map.IsSkirmish && startSetPath is not null)
			throw new ArgumentException($"Map '{map.Id}' has fixed placements and does not accept a starting set.");
		var startingSet = map.IsSkirmish ? StartingSet.Load(read(startSetPath ?? defaultStartSetPath ?? DefaultStartSet)) : null;
		return Prepare(map, startingSet, map.Slots);
	}
}
