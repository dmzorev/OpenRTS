using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed class GameMap
{
    private const string Neutral = "neutral";
    private sealed record Placed(string Id, int Type, WorldPosition Position, string Owner, Source Source, int QuarterTurns, int? InitialHealth);
    private sealed record Slot(WorldPosition Position, bool Reverse);
    private readonly UnitCatalog _catalog;
    private readonly Dictionary<string, int> _types;
    private readonly Dictionary<string, Slot> _slots;
    private readonly Placed[] _objects;
    private readonly Source _source;
    private readonly TerrainLayout _layout;
    private readonly IReadOnlyList<MapSurfaceCell>? _surface;
    public UnitCatalog Catalog => _catalog;
    public string Id { get; }
    public string GameplayFingerprint { get; }
    public bool IsSkirmish { get; }
    public NavigationGrid Grid { get; }
    public IReadOnlyList<string> Slots { get; }
    public IReadOnlyList<string> TerrainIds { get; }
    public IReadOnlyList<string> VisualIds { get; }
    public IEnumerable<WorldPosition> ObjectPositions => _objects.Select(obj => obj.Position);

    internal GameMap(MapSource data, UnitCatalog catalog, string gameplayFingerprint)
    {
        Id = data.Id; IsSkirmish = data.IsSkirmish; _catalog = catalog; _source = data.Origin;
        GameplayFingerprint = gameplayFingerprint;
        _types = catalog.Units.Select((type, index) => (type.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);
        var terrainIds = data.Terrain.Palette.Select(id => (byte)catalog.Locomotion!.Terrains.IndexOf(id)).ToArray();
        _layout = new(data.Width, data.Height, data.Terrain.Cells.Select(i => terrainIds[i]), data.Visual.Cells.Select(i => data.Visual.Palette[i]));
        _surface = data.Surface;
        Grid = _layout.CreateGrid(_surface, catalog.Locomotion);
        _slots = data.Slots.ToDictionary(s => s.Id, s => new Slot(s.Position is { } point ? OnGround(point) : default, s.Reverse), StringComparer.Ordinal);
        Slots = Array.AsReadOnly(_slots.Keys.Order(StringComparer.Ordinal).ToArray());
        _objects = data.Objects.Select(o => new Placed(o.Id, _types[UnitCatalog.MapTypeId(data.Id, o)], OnGround(o.Position), o.Owner, o.Origin ?? data.Origin, o.QuarterTurns, o.InitialHealth)).ToArray();
        var terrainNames = terrainIds.Order().Select(i => catalog.Locomotion!.Terrains[i]);
        TerrainIds = Array.AsReadOnly((Grid.HasSplitTerrain
            ? terrainNames.Concat(_surface!.Select(cell => cell.UpperTerrainId).OfType<string>()).Distinct(StringComparer.Ordinal)
            : terrainNames).ToArray());
        VisualIds = Array.AsReadOnly(_layout.VisualIds().Concat(_surface?.Select(cell => cell.UpperVisualId).OfType<string>() ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public string VisualAt(int x, int y) => _layout.VisualAt(x, y);
    public string? UpperVisualAt(int x, int y) => _surface?[Grid.Index(new(x, y))].UpperVisualId;

    private WorldPosition OnGround(WorldPosition point) => Grid.HasSurface ? point with { Elevation = Grid.SurfaceElevationAt(point) } : point;

    public string TerrainAt(int x, int y) => _catalog.Locomotion!.Terrains[Grid.TerrainAt(Grid.Index(new(x, y)))];

    public WorldPosition StartPosition(string slot)
    {
        if (!IsSkirmish) throw new InvalidOperationException("Fixed placements have no start positions.");
        return _slots[slot].Position;
    }

    public static GameMap Load(ContentSource source, UnitCatalog catalog, IEnumerable<ContentSource>? brushSources = null) =>
        source.Name.EndsWith(".ortmap.json", StringComparison.OrdinalIgnoreCase)
            ? MapCompiler.Read(source, catalog).Map
            : MapCompiler.Compile(source.Name.EndsWith(".mapsource.json", StringComparison.OrdinalIgnoreCase)
                ? MapSource.Read(source) : MapSource.FromM1(source, catalog, brushSources), catalog).Map;

    public MapMatch Prepare(IReadOnlyDictionary<string, PlayerDefinition> assignments, StartingSet? startingSet = null, ulong seed = 1)
    {
        if (IsSkirmish != (startingSet is not null))
            throw new ContentException(_source, Id, IsSkirmish ? "Select a starting set." : "Fixture maps do not use starting sets.");
        var assigned = new Dictionary<string, PlayerDefinition>(assignments, StringComparer.Ordinal);
        if (IsSkirmish && assigned.Count == 0)
            throw new ContentException(_source, Id, "Assign at least one player to a start.");
        foreach (string slot in assigned.Keys)
            if (!_slots.ContainsKey(slot)) throw new ContentException(_source, Id, $"Unknown player slot '{slot}'.");

        var players = new List<PlayerDefinition>();
        var playerIds = new HashSet<uint>();
        var placements = new List<MapMatch.Placement>();
        foreach (string slot in Slots)
        {
            string path = $"{Id}.{slot}";
            if (!assigned.TryGetValue(slot, out var player))
            {
                if (IsSkirmish) continue;
                throw new ContentException(_source, path, "Missing player assignment.");
            }
            if (player.Id == 0 || !playerIds.Add(player.Id) || string.IsNullOrWhiteSpace(player.Name)
                || player.Credits is < 0 or > SimulationWorld.MaximumCredits)
                throw new ContentException(_source, path, "Expected a unique nonzero player ID, a name and bounded credits.");
            players.Add(startingSet is null ? player : player with { Credits = startingSet.Credits });

            if (startingSet is null) continue;
            var anchor = _slots[slot];
            for (int i = 0; i < startingSet.Units.Count; i++)
            {
                var unit = startingSet.Units[i];
                string label = $"{path}/{startingSet.Id}.units[{i}]";
                if (!_types.TryGetValue(unit.Type, out int type))
                    throw new ContentException(unit.Source, label, $"Unknown unit '{unit.Type}'.");
                var point = unit.At(anchor.Position, anchor.Reverse);
                if (point.X < 0 || point.X >= Grid.Width * Grid.CellSize || point.Y < 0 || point.Y >= Grid.Height * Grid.CellSize)
                    throw new ContentException(unit.Source, label, $"Unit '{unit.Type}' at ({point.X}, {point.Y}) is outside the map.");
                placements.Add(new(label, type, OnGround(point), player.Id, unit.Source, IsStart: true));
            }
        }

        // Starting entities keep their IDs in the default skirmish journal and HUD checks.
        foreach (var obj in _objects)
            placements.Add(new($"{Id}.objects.{obj.Id}", obj.Type, obj.Position, obj.Owner == Neutral ? 0 : assigned[obj.Owner].Id, obj.Source, QuarterTurns: obj.QuarterTurns, InitialHealth: obj.InitialHealth));

        return new(this, _catalog, players.ToArray(), placements.ToArray(), seed);
    }
}
