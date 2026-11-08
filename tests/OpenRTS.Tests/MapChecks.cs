using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class MapChecks
{
    public static void Run(Action<string, Action> test)
    {
        static ContentSource Read(string path) => new(path, File.ReadAllText(Path.Combine("sandbox", path)));
        const string fixturePath = "content/maps/fixture.yaml";
        var catalog = UnitCatalog.Load([Read("content/units.yaml"), Read("content/buildings.yaml")],
            LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
        var players = new Dictionary<string, PlayerDefinition> { ["red"] = new(23, "Red"), ["blue"] = new(17, "Blue", 450) };

        test("Maps: fixed ownership and exact positions share terrain with navigation", () =>
        {
            var map = GameMap.Load(Read(fixturePath), catalog);
            using var world = map.Prepare(players).CreateWorld();
            var poses = Poses(world).OrderBy(p => p.Id.Value).ToArray();
            NavigationChecks.Check(poses.Select(p => p.Presentation.Owner).SequenceEqual(new uint[] { 17, 23, 0 }), "Owners were inferred from IDs or list positions");
            NavigationChecks.Check(poses[0].Current == new WorldPosition(4608, 5632) && poses[1].Current == new WorldPosition(15872, 6656), "Map positions were snapped");
            NavigationChecks.Check(map.TerrainAt(0, 0) == "mountains" && map.TerrainAt(4, 5) == "road" &&
                map.Grid.TerrainAt(map.Grid.Index(new(4, 5))) == catalog.Locomotion!.TerrainIndex("road"), "Map terrain axes or indexing are wrong");
        });
        test("Maps: static brushes compose terrain and independent visuals in placement order", () =>
        {
            var original = Read(fixturePath);
            string brush = """
              brushes:
                cliff:
                  rows: ["AB.", "CDA"]
                  palette:
                    A: {terrain: mountains, visual: rock-a}
                    B: {terrain: road, visual: rock-b}
                    C: {visual: rock-c}
                    D: {terrain: grass, visual: rock-d}
                paint:
                  rows: ["A"]
                  palette:
                    A: {visual: overlay}
              placements:
                - {brush: cliff, cell: [2, 2], rotation: 1}
                - {brush: paint, cell: [2, 2]}
            """;
            var map = GameMap.Load(original with { Text = original.Text.Replace("slots: [blue, red]", brush + "\nslots: [blue, red]") }, catalog);
            NavigationChecks.Check(map.TerrainAt(3, 2) == "mountains" && map.VisualAt(3, 2) == "rock-a" &&
                map.TerrainAt(2, 2) == "grass" && map.VisualAt(2, 2) == "overlay" &&
                map.TerrainAt(3, 3) == "road" && map.VisualAt(3, 3) == "rock-b" &&
                map.VisualAt(3, 4) == "grass", "Rotated brush, transparent cell or overlay was compiled incorrectly");
            var invalid = original with { Text = original.Text.Replace("slots: [blue, red]", brush.Replace("[2, 2], rotation: 1", "[19, 11], rotation: 1") + "\nslots: [blue, red]") };
            NavigationChecks.Reject<ContentException>(() => GameMap.Load(invalid, catalog));
        });
        test("Maps: decorative selection is presentation data and invulnerable groves reject attack", () =>
        {
            int grove = catalog.Units.ToList().FindIndex(u => u.Id == "nature-trees");
            NavigationChecks.Check(!catalog.Presentations[grove].Selectable && catalog.Units[grove].Destruction == DestructionPolicy.Invulnerable, "Grove should be non-selectable and invulnerable");
            var original = Read("content/buildings.yaml");
            var selectable = UnitCatalog.Load([Read("content/units.yaml"), original with { Text = original.Text.Replace("States: [destroyed], Selectable: false", "States: [destroyed], Selectable: true") }],
                LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            NavigationChecks.Check(selectable.Summary == catalog.Summary, "Selection changed gameplay fingerprint");
            using var world = new SimulationWorld(catalog.Units, 20 * 1024, 12 * 1024);
            int rifle = catalog.Units.ToList().FindIndex(u => u.Id == "rifleman");
            var attacker = world.AddInitialUnit(rifle, new(4096, 4096), 1);
            var target = world.AddInitialUnit(grove, new(8192, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, attacker, TargetEntity: target)]);
            for (int i = 0; i < 60; i++) world.Step();
            NavigationChecks.Check(Poses(world).Single(p => p.Id == target).Health == catalog.Units[grove].MaxHealth &&
                !world.Events.ToArray().Any(e => e.Kind == WorldEventKind.WeaponFired), "Invulnerable grove was attacked");
        });
        test("Maps: a different start set changes the same map with deterministic player assignment", () =>
        {
            var map = GameMap.Load(Read(MapScenario.DefaultMap), catalog);
            var standard = StartingSet.Load(Read(MapScenario.DefaultStartSet));
            var alternate = StartingSet.Load(Read("content/start-sets/infantry.yaml"));
            using var first = map.Prepare(players, standard).CreateWorld();
            using var second = map.Prepare(players, alternate).CreateWorld(4);
            using var third = map.Prepare(players.Reverse().ToDictionary(), alternate).CreateWorld(1);
            NavigationChecks.Check(first.Count == 14 && second.Count == 14 && Poses(first).Count(p => catalog.Units[p.UnitType].Supply?.Role == SupplyRole.Truck) == 2 && second.Report() == third.Report(), "Starting set or dictionary order changed match setup");
            var poses = Poses(second).OrderBy(p => p.Id.Value).ToArray();
            NavigationChecks.Check(poses[0].Presentation.Owner == 17 && poses[3].Presentation.Owner == 23 &&
                poses[4].Current == new WorldPosition(64512, 41984), "Slots or reversed offsets were resolved incorrectly");
        });
        test("Maps: both city maps support every start with either mode and allow open slots", () =>
        {
            foreach (string path in new[] { MapScenario.CrossroadsMap, MapScenario.RiversideMap })
            {
                var map = GameMap.Load(Read(path), catalog);
                CheckCityStarts(map);
                using var solo = MapScenario.Prepare(map, StartingSet.Load(Read(MapScenario.DefaultStartSet)), [map.Slots[^1]]).CreateWorld();
                var poses = Poses(solo);
                NavigationChecks.Check(solo.PlayerCount == 2 && poses.Count(p => p.Presentation.Owner == 1) == 3 &&
                    poses.Any(p => p.Presentation.Owner == 1 && p.Current == map.StartPosition(map.Slots[^1])),
                    "An unassigned start spawned a player or the assigned start moved.");
                NavigationChecks.Check(poses.Any(p => p.Presentation.Owner == 0 && catalog.Units[p.UnitType].Id.StartsWith("civilian-", StringComparison.Ordinal)),
                    "The map lacks neutral civilian buildings.");
            }

            void CheckCityStarts(GameMap map)
            {
                foreach (string path in new[] { MapScenario.DefaultStartSet, MapScenario.InfantryStartSet })
                {
                    var mode = StartingSet.Load(Read(path));
                    using var world = MapScenario.Prepare(map, mode, map.Slots).CreateWorld();
                    NavigationChecks.Check(world.PlayerCount == 5, "Four configured slots did not spawn four players.");
                }
                var standard = StartingSet.Load(Read(MapScenario.DefaultStartSet));
                NavigationChecks.Reject<ContentException>(() => MapScenario.Prepare(map, standard, []));
                NavigationChecks.Reject<ArgumentException>(() => MapScenario.Prepare(map, standard, [map.Slots[0], map.Slots[0]]));
            }
        });
        test("Maps: malformed files, slot assignments and late placement failures are rejected", () =>
        {
            var source = Read(fixturePath);
            foreach (var (oldText, newText) in new[]
            {
                ("version: 1", "version: 1\nversion: 1"),
                ("version: 1", "version: 1\nunknown: true"),
                ("version: 1", "version: 2"),
                ("size: [20, 12]", "size: [20, 13]"),
                ("G: grass", "G: unknown-terrain"),
                ("type: rifleman", "type: unknown-unit"),
                ("id: red-infantry", "id: blue-infantry"),
                ("owner: red", "owner: missing")
            })
                NavigationChecks.Reject<ContentException>(() => GameMap.Load(source with { Text = source.Text.Replace(oldText, newText) }, catalog));
            var map = GameMap.Load(source, catalog);
            NavigationChecks.Reject<ContentException>(() => map.Prepare(new Dictionary<string, PlayerDefinition> { ["blue"] = players["blue"] }));
            NavigationChecks.Reject<ContentException>(() => map.Prepare(new Dictionary<string, PlayerDefinition> { ["blue"] = players["blue"], ["red"] = players["blue"] }));
            var overlapping = GameMap.Load(source with { Text = source.Text.Replace("[15872, 6656]", "[4608, 5632]") }, catalog).Prepare(players);
            NavigationChecks.Reject<ContentException>(() => overlapping.CreateWorld());
            var stackedFootprints = GameMap.Load(source with
            {
                Text = source.Text + "  - {id: more-trees, type: nature-trees, position: [9728, 4096], owner: neutral}\n"
            }, catalog).Prepare(players);
            NavigationChecks.Reject<ContentException>(() => stackedFootprints.CreateWorld());
            using var retry = map.Prepare(players).CreateWorld();
            NavigationChecks.Check(retry.Count == 3, "A failed load affected the next world");
        });
    }
}
