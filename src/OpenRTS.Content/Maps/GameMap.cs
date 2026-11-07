using DeclarativeContent;
using OpenRTS.Core;
using YamlDotNet.RepresentationModel;

namespace OpenRTS.Content;

public sealed class GameMap
{
    private const string Neutral = "neutral";
    private sealed record Placed(string Id, int Type, WorldPosition Position, string Owner, Source Source);
    private sealed record Slot(WorldPosition Position, bool Reverse);
    private readonly UnitCatalog _catalog;
    private readonly Dictionary<string, int> _types;
    private readonly Dictionary<string, Slot> _slots;
    private readonly Placed[] _objects;
    private readonly Source _source;
    public string Id { get; }
    public bool IsSkirmish { get; }
    public NavigationGrid Grid { get; }
    public IReadOnlyList<string> Slots { get; }
    public IReadOnlyList<string> TerrainIds { get; }

    private GameMap(string id, bool skirmish, UnitCatalog catalog, Dictionary<string, int> types, NavigationGrid grid,
        string[] terrainIds, Dictionary<string, Slot> slots, Placed[] objects, Source source)
    {
        Id = id;
        IsSkirmish = skirmish;
        _catalog = catalog;
        _types = types;
        Grid = grid;
        TerrainIds = Array.AsReadOnly(terrainIds);
        _slots = slots;
        Slots = Array.AsReadOnly(slots.Keys.Order(StringComparer.Ordinal).ToArray());
        _objects = objects;
        _source = source;
    }

    public string TerrainAt(int x, int y) => _catalog.Locomotion!.Terrains[Grid.TerrainAt(Grid.Index(new(x, y)))];

    public static GameMap Load(ContentSource source, UnitCatalog catalog)
    {
        var yaml = new MapYaml(source);
        var root = yaml.Root();
        var fields = yaml.Fields(root, source.Name, "version", "id", "kind", "size", "cellSize", "terrain", "objects", "slots", "starts");
        yaml.Integer(yaml.Required(fields, "version", root, source.Name), "version", 1, 1);
        string id = yaml.Text(yaml.Required(fields, "id", root, source.Name), "id");
        var kind = yaml.Required(fields, "kind", root, id);
        bool skirmish = yaml.Text(kind, "kind") switch
        {
            "skirmish" => true,
            "fixture" => false,
            _ => throw yaml.Error(kind, "kind", "Expected 'skirmish' or 'fixture'.")
        };
        string slotField = skirmish ? "starts" : "slots";
        string invalidSlotField = skirmish ? "slots" : "starts";
        if (fields.TryGetValue(invalidSlotField, out var invalid))
            throw yaml.Error(invalid, invalidSlotField, $"Use '{slotField}' for this map kind.");
        const int cellSize = WorldPosition.UnitsPerCell;
        yaml.Integer(yaml.Required(fields, "cellSize", root, id), "cellSize", cellSize, cellSize);
        var sizeNode = yaml.Required(fields, "size", root, id);
        var size = yaml.Pair(sizeNode, "size", 1, WorldPosition.MaximumExtent / cellSize);
        if ((long)size.X * size.Y > NavigationGrid.MaximumCells)
            throw yaml.Error(sizeNode, "size", "Map exceeds the navigation cell budget.");

        var locomotion = catalog.Locomotion ?? throw yaml.Error(root, id, "Maps require a terrain catalog.");
        var terrainNode = yaml.Required(fields, "terrain", root, id);
        var terrain = yaml.Fields(terrainNode, "terrain", "palette", "rows");
        var palette = new Dictionary<char, byte>();
        var paletteNode = yaml.Required(terrain, "palette", terrainNode, "terrain");
        foreach (var (symbol, value) in yaml.Fields(paletteNode, "terrain.palette"))
        {
            if (symbol.Length != 1 || !char.IsAsciiLetter(symbol[0]))
                throw yaml.Error(value, "terrain.palette", $"Expected an ASCII letter as a symbol, got '{symbol}'.");
            string name = yaml.Text(value, "terrain.palette." + symbol);
            int index = locomotion.Terrains.IndexOf(name);
            if (index < 0) throw yaml.Error(value, "terrain.palette." + symbol, $"Unknown terrain '{name}'.");
            palette.Add(symbol[0], (byte)index);
        }

        var rowsNode = yaml.Required(terrain, "rows", terrainNode, "terrain");
        var rows = yaml.Items(rowsNode, "terrain.rows");
        if (rows.Count != size.Y) throw yaml.Error(rowsNode, "terrain.rows", $"Expected {size.Y} rows.");
        var samples = new byte[size.X * size.Y];
        var used = new HashSet<byte>();
        for (int y = 0; y < rows.Count; y++)
        {
            string path = $"terrain.rows[{y}]";
            string row = yaml.Text(rows[y], path);
            if (row.Length != size.X) throw yaml.Error(rows[y], path, $"Expected {size.X} symbols.");
            for (int x = 0; x < row.Length; x++)
            {
                if (!palette.TryGetValue(row[x], out byte value))
                    throw yaml.Error(rows[y], path, $"Unknown symbol '{row[x]}' at x={x}.");
                samples[y * size.X + x] = value;
                used.Add(value);
            }
        }

        WorldPosition Position(YamlNode node, string path)
        {
            var point = yaml.Pair(node, path, 0, WorldPosition.MaximumExtent);
            if (point.X >= size.X * cellSize || point.Y >= size.Y * cellSize)
                throw yaml.Error(node, path, $"Position ({point.X}, {point.Y}) is outside the map.");
            return point;
        }

        var slotNode = yaml.Required(fields, slotField, root, id);
        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var node in yaml.Items(slotNode, slotField))
        {
            string name;
            Slot slot = new(default, false);
            if (skirmish)
            {
                var entry = yaml.Fields(node, slotField, "id", "position", "reverse");
                name = yaml.Text(yaml.Required(entry, "id", node, slotField), slotField + ".id");
                string path = $"{slotField}.{name}";
                var point = Position(yaml.Required(entry, "position", node, path), path + ".position");
                bool reverse = entry.TryGetValue("reverse", out var direction) && (yaml.Text(direction, path + ".reverse") switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw yaml.Error(direction, path + ".reverse", "Expected true or false.")
                });
                slot = new(point, reverse);
            }
            else name = yaml.Text(node, slotField);

            if (name == Neutral || !slots.TryAdd(name, slot))
                throw yaml.Error(node, slotField, $"Reserved or duplicate slot '{name}'.");
        }
        if (skirmish && slots.Count == 0) throw yaml.Error(slotNode, slotField, "Skirmish needs at least one start.");

        var types = catalog.Units.Select((type, index) => (type.Id, index)).ToDictionary(t => t.Id, t => t.index, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var objects = new List<Placed>();
        var objectsNode = yaml.Required(fields, "objects", root, id);
        foreach (var node in yaml.Items(objectsNode, "objects"))
        {
            var entry = yaml.Fields(node, "objects", "id", "type", "position", "owner");
            string name = yaml.Text(yaml.Required(entry, "id", node, "objects"), "objects.id");
            string path = $"objects.{name}";
            if (!ids.Add(name)) throw yaml.Error(node, path, "Duplicate object ID.");
            var typeNode = yaml.Required(entry, "type", node, path);
            string type = yaml.Text(typeNode, path + ".type");
            if (!types.TryGetValue(type, out int index)) throw yaml.Error(typeNode, path + ".type", $"Unknown unit '{type}'.");
            var ownerNode = yaml.Required(entry, "owner", node, path);
            string owner = yaml.Text(ownerNode, path + ".owner");
            if (owner != Neutral && (skirmish || !slots.ContainsKey(owner)))
                throw yaml.Error(ownerNode, path + ".owner", $"Owner '{owner}' is not allowed on this map.");
            objects.Add(new(name, index, Position(yaml.Required(entry, "position", node, path), path + ".position"),
                owner, yaml.Location(node)));
        }

        return new(id, skirmish, catalog, types, new(size.X, size.Y, terrain: samples),
            used.Order().Select(index => locomotion.Terrains[index]).ToArray(), slots, objects.ToArray(), yaml.Location(root));
    }

    public MapMatch Prepare(IReadOnlyDictionary<string, PlayerDefinition> assignments, StartingSet? startingSet = null, ulong seed = 1)
    {
        if (IsSkirmish != (startingSet is not null))
            throw new ContentException(_source, Id, IsSkirmish ? "Select a starting set." : "Fixture maps do not use starting sets.");
        var assigned = new Dictionary<string, PlayerDefinition>(assignments, StringComparer.Ordinal);
        foreach (string slot in assigned.Keys)
            if (!_slots.ContainsKey(slot)) throw new ContentException(_source, Id, $"Unknown player slot '{slot}'.");

        var players = new List<PlayerDefinition>();
        var playerIds = new HashSet<uint>();
        var placements = new List<MapMatch.Placement>();
        foreach (string slot in Slots)
        {
            string path = $"{Id}.{slot}";
            if (!assigned.TryGetValue(slot, out var player))
                throw new ContentException(_source, path, "Missing player assignment.");
            if (player.Id == 0 || !playerIds.Add(player.Id) || string.IsNullOrWhiteSpace(player.Name)
                || player.Credits is < 0 or > SimulationWorld.MaximumCredits)
                throw new ContentException(_source, path, "Expected a unique nonzero player ID, a name and bounded credits.");
            players.Add(startingSet is null ? player : player with { Credits = startingSet.Credits });

            if (startingSet is null) continue;
            var anchor = _slots[slot];
            int sign = anchor.Reverse ? -1 : 1;
            for (int i = 0; i < startingSet.Units.Count; i++)
            {
                var unit = startingSet.Units[i];
                string label = $"{path}/{startingSet.Id}.units[{i}]";
                if (!_types.TryGetValue(unit.Type, out int type))
                    throw new ContentException(unit.Source, label, $"Unknown unit '{unit.Type}'.");
                var point = new WorldPosition(anchor.Position.X + sign * unit.Offset.X, anchor.Position.Y + sign * unit.Offset.Y);
                if (point.X < 0 || point.X >= Grid.Width * Grid.CellSize || point.Y < 0 || point.Y >= Grid.Height * Grid.CellSize)
                    throw new ContentException(unit.Source, label, $"Unit '{unit.Type}' at ({point.X}, {point.Y}) is outside the map.");
                placements.Add(new(label, type, point, player.Id, unit.Source, IsStart: true));
            }
        }

        // Starting entities keep their IDs in the default skirmish journal and HUD checks.
        foreach (var obj in _objects)
            placements.Add(new($"{Id}.objects.{obj.Id}", obj.Type, obj.Position, obj.Owner == Neutral ? 0 : assigned[obj.Owner].Id, obj.Source));

        return new(this, _catalog, players.ToArray(), placements.ToArray(), seed);
    }
}
