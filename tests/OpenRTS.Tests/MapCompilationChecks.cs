using System.Text.Json.Nodes;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

internal static class MapCompilationChecks
{
    public static void Run(Action<string, Action> test)
    {
        var manifest = GameManifest.Load("sandbox/game.yaml");
        var catalog = manifest.LoadUnits();
        static ContentSource Read(string name) => new(name, File.ReadAllText("sandbox/" + name));
        var source = MapSource.FromM1(Read("content/maps/fixture.yaml"), catalog);
        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") };
        static MapSource Copy(MapSource s, MapLayer? terrain = null, MapLayer? visual = null,
            IReadOnlyList<MapSlotSource>? slots = null, IReadOnlyList<MapObjectSource>? objects = null) =>
            new(s.Id, s.IsSkirmish, s.Width, s.Height, terrain ?? s.Terrain, visual ?? s.Visual,
                slots ?? s.Slots, objects ?? s.Objects, s.CellSize);

        test("Maps compiled: v2 initial HP, local health variants, and rotated footprints survive read and world initialization", () =>
        {
            var variant = new MapHealthVariant("veteran", 180);
            MapObjectSource[] objects =
            [
                new("full", "rifleman", new(1024, 1024), "blue"),
                new("wounded", "rifleman", new(3072, 1024), "blue", InitialHealth: 35),
                new("veteran", "rifleman", new(5120, 1024), "blue", HealthVariant: variant),
                new("second-veteran", "rifleman", new(7168, 1024), "red", InitialHealth: 90, HealthVariant: variant),
                new("rotated", "power-plant", new(4096, 5632), "neutral", QuarterTurns: 1)
            ];
            var grass = new MapLayer(["grass"], new int[144]);
            var input = new MapSource("initial-properties", false, 12, 12, grass, grass, source.Slots, objects);
            var compiled = MapCompiler.Compile(MapSource.Read(new("v2 source", input.Write())), catalog);
            var restored = MapCompiler.Read(new("v2 artifact", compiled.Write()), catalog);
            using var world = compiled.Map.Prepare(players).CreateWorld(1);
            using var otherWorld = restored.Map.Prepare(players).CreateWorld(4);
            var poses = new EntityPose[objects.Length];
            world.CopyPoses(poses);
            NavigationChecks.Check(world.Report() == otherWorld.Report() && poses.Select(p => p.Health).Take(4).SequenceEqual(new[] { 100, 35, 180, 90 }),
                "Initial HP changed on serialization or across workers.");
            var effective = restored.Map.Catalog;
            int veteran = poses[2].UnitType;
            NavigationChecks.Check(poses[0].UnitType == poses[1].UnitType && veteran == poses[3].UnitType && veteran == catalog.Units.Count &&
                effective.Units.Take(veteran).SequenceEqual(catalog.Units) && effective.Units[veteran].MaxHealth == 180 &&
                effective.Presentations[veteran].Model == catalog.Presentations[poses[0].UnitType].Model &&
                compiled.ContentFingerprint == catalog.Summary.Fingerprint && compiled.EffectiveContentFingerprint != compiled.ContentFingerprint,
                "Variant did not preserve catalog indices, isolate base health or inherit presentation.");
            var grid = world.NavigationGrid!;
            NavigationChecks.Check(poses[4].QuarterTurns == 1 && poses[4].Presentation.BodyFacing == FacingDirection.FromQuarterTurns(1) &&
                grid.IsBlocked(grid.Index(new(3, 4))) && grid.IsBlocked(grid.Index(new(4, 6))) && !grid.IsBlocked(grid.Index(new(5, 5))),
                "Rotated footprint or facing was not initialized.");
            MapSource Replace(int index, MapObjectSource value) =>
                Copy(input, objects: objects.Select((ob, i) => i == index ? value : ob).ToArray());
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Replace(1, objects[1] with { InitialHealth = 101 }), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Replace(1, objects[1] with { InitialHealth = 0 }), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Replace(1, objects[1] with { QuarterTurns = 1 }), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Replace(3, objects[3] with { HealthVariant = variant with { MaxHealth = 190 } }), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Replace(3, objects[3] with { Type = "tank" }), catalog));
            var invalid = MapCompiler.Compile(Replace(4, objects[4] with { QuarterTurns = 0 }), catalog);
            NavigationChecks.Reject<ContentException>(() => invalid.Map.Prepare(players).CreateWorld());
            var updatedHp = MapCompiler.Compile(Replace(1, objects[1] with { InitialHealth = 36 }), catalog);
            using var altered = updatedHp.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(updatedHp.GameplayFingerprint != compiled.GameplayFingerprint &&
                updatedHp.EffectiveContentFingerprint == compiled.EffectiveContentFingerprint &&
                altered.Report().StateHash != world.Report().StateHash, "Initial HP must participate in gameplay and tick-zero identity.");
            var bad = JsonNode.Parse(compiled.Write())!;
            bad["effectiveContentFingerprint"] = compiled.ContentFingerprint;
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("v2 bad effective fingerprint", bad.ToJsonString()), catalog));
        });

        test("Maps compiled: v3 diagonal relief preserves exact start and object heights", () =>
        {
            var grass = new MapLayer(["grass"], new int[64]);
            var cells = Enumerable.Range(0, 64).Select(i => new MapSurfaceCell(1920 + i % 8 * 128 - i / 8 * 256, 128, -256)).ToArray();
            MapSource Source(MapSurfaceCell[] surface) => new("relief", true, 8, 8, grass, grass,
                [new("west", new(1536, 1792))], [new("scout", "rifleman", new(5632, 2432), "neutral", InitialHealth: 35)], surface: surface);
            var compiled = MapCompiler.Compile(MapSource.Read(new("relief", Source(cells).Write())), catalog);
            var restored = MapCompiler.Read(new("relief artifact", compiled.Write()), catalog);
            var set = StartingSet.Load(new("relief starts",
                "version: 1\nid: scout\ncredits: 0\nunits: [{ type: rifleman, offset: [0, 512] }]"));
            using var world = restored.Map.Prepare(new Dictionary<string, PlayerDefinition> { ["west"] = new(1, "West") }, set).CreateWorld();
            var poses = new EntityPose[2];
            world.CopyPoses(poses);
            NavigationChecks.Check(restored.Source.Surface!.SequenceEqual(cells) &&
                restored.Map.Grid.SurfaceAt(0).Slope == 287 &&
                restored.Map.StartPosition("west") == new WorldPosition(1536, 1792, 1728) &&
                poses[0].Current == new WorldPosition(1536, 2304, 1600) &&
                poses[1].Current == new WorldPosition(5632, 2432, 2080) && poses[1].Health == 35,
                "Surface, exact start offsets or v2-compatible initial HP were lost.");

            var tampered = JsonNode.Parse(compiled.Write())!;
            tampered["map"]!["surface"]![0]![0] = 1921;
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("tampered surface", tampered.ToJsonString()), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(cells[..^1]), catalog));
            cells[0] = new(1048576, RiseX: 256, RiseY: 256);
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(cells), catalog));
        });

        test("Maps compiled: v4 diagonal cuts affect identity and reject invalid surfaces", () =>
        {
            var grass = new MapLayer(["grass"], new int[4]);
            var cells = new MapSurfaceCell[4];
            MapSource Source() => new("cuts", false, 2, 2, grass, grass, [], [], surface: cells);
            var flat = MapCompiler.Compile(Source(), catalog);
            cells[0] = new(0, Cut: new(OpenRTS.Core.Navigation.SurfaceCorner.NorthWest, 1024));
            var cut = MapCompiler.Compile(Source(), catalog);
            cells[0] = cells[0] with { Cut = cells[0].Cut with { UpperCorner = OpenRTS.Core.Navigation.SurfaceCorner.SouthEast } };
            var opposite = MapCompiler.Compile(Source(), catalog);
            NavigationChecks.Check(flat.GameplayFingerprint != cut.GameplayFingerprint &&
                cut.GameplayFingerprint != opposite.GameplayFingerprint &&
                MapCompiler.Read(new("cut map", cut.Write()), catalog).Write() == cut.Write(),
                "A diagonal cut or its orientation was lost in the map identity.");
            cells[0] = cells[0] with { RiseX = 128 };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(), catalog));
            cells[0] = new(1024, Cut: cells[0].Cut);
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(), catalog));
        });

        test("Maps compiled: v5 split terrain round-trips and keeps upper artwork outside gameplay identity", () =>
        {
            var grass = new MapLayer(["grass"], new int[4]);
            var cells = new MapSurfaceCell[4];
            MapSource Source() => new("shore", false, 2, 2, grass, grass, [], [], surface: cells);
            cells[0] = new(0, Cut: new(OpenRTS.Core.Navigation.SurfaceCorner.NorthEast, 0), UpperTerrainId: "water", UpperVisualId: "water");
            var shore = MapCompiler.Compile(MapSource.Read(new("shore source", Source().Write())), catalog);
            var restored = MapCompiler.Read(new("shore compiled", shore.Write()), catalog);
            NavigationChecks.Check(restored.Write() == shore.Write() && restored.Map.UpperVisualAt(0, 0) == "water" &&
                restored.Map.TerrainIds.Contains("water"), "Split ground or its upper artwork did not round-trip.");
            cells[0] = cells[0] with { UpperVisualId = "river" };
            var art = MapCompiler.Compile(Source(), catalog);
            cells[0] = cells[0] with { UpperTerrainId = "mud" };
            var gameplay = MapCompiler.Compile(Source(), catalog);
            NavigationChecks.Check(shore.GameplayFingerprint == art.GameplayFingerprint && shore.DataFingerprint != art.DataFingerprint &&
                art.GameplayFingerprint != gameplay.GameplayFingerprint, "The two upper IDs contributed to the wrong fingerprints.");
            cells[0] = cells[0] with { UpperTerrainId = "unknown" };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(), catalog));
            cells[0] = cells[0] with { UpperTerrainId = "grass" };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(), catalog));
            cells[0] = cells[0] with { UpperTerrainId = "water", Cut = default };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Source(), catalog));
        });

        test("Maps compiled: live YAML and compiled maps agree on every journal tick", () =>
        {
            foreach (string path in new[] { "fixture", "skirmish", "crossroads", "riverside-station" })
            {
                var input = Read($"content/maps/{path}.yaml");
                var legacy = GameMap.Load(input, catalog);
                var compiled = MapCompiler.Compile(MapSource.Read(new("source.mapsource.json", MapSource.FromM1(input, catalog).Write())), catalog);
                var loaded = GameMap.Load(new("test.ortmap.json", compiled.Write()), catalog);
                var assignments = legacy.Slots.Select((id, i) => (id, player: new PlayerDefinition((uint)i + 1, "Player " + (i + 1))))
                    .ToDictionary(p => p.id, p => p.player);
                var set = legacy.IsSkirmish ? StartingSet.Load(Read(MapScenario.DefaultStartSet)) : null;
                using var first = legacy.Prepare(assignments, set, 77).CreateWorld(1);
                using var second = loaded.Prepare(assignments.Reverse().ToDictionary(), set, 77).CreateWorld(4);
                for (int tick = 0; tick <= 32; tick++)
                {
                    NavigationChecks.Check(first.Report() == second.Report(), $"Compiled map diverged at {path}/{tick}.");
                    if (tick == 32) break;
                    WorldCommand[] commands = tick == 0 ? [new(1, 0, 1, CommandKind.MoveAll, Position: new(12288, 6144))] : [];
                    first.Step(commands); second.Step(commands);
                }
                NavigationChecks.Check(loaded.Grid.Width == legacy.Grid.Width && loaded.VisualIds.SequenceEqual(legacy.VisualIds), "Map layers changed.");
                for (int y = 0; y < legacy.Grid.Height; y++) for (int x = 0; x < legacy.Grid.Width; x++)
                    NavigationChecks.Check(legacy.TerrainAt(x, y) == loaded.TerrainAt(x, y) && legacy.VisualAt(x, y) == loaded.VisualAt(x, y), "Cell data changed.");
            }
        });

        test("Maps compiled: canonical palettes and slots ignore authoring layout but retain placement order", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            static MapLayer Reverse(MapLayer layer) => new(layer.Palette.Reverse().ToArray(), layer.Cells.Select(i => layer.Palette.Count - 1 - i).ToArray());
            var equivalent = MapCompiler.Compile(Copy(source, Reverse(source.Terrain), Reverse(source.Visual), source.Slots.Reverse().ToArray()), catalog);
            NavigationChecks.Check(compiled.Write() == equivalent.Write(), "Palette or slot list order changed canonical output.");
            var reordered = MapCompiler.Compile(Copy(source, objects: source.Objects.Reverse().ToArray()), catalog);
            NavigationChecks.Check(compiled.GameplayFingerprint != reordered.GameplayFingerprint, "Object order was erased from identity.");
            using var first = compiled.Map.Prepare(players).CreateWorld();
            using var second = reordered.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(first.Report().StateHash != second.Report().StateHash, "The test did not exercise spawn order.");
            NavigationChecks.Check(MapCompiler.Compile(MapSource.Read(new("formatted", JsonNode.Parse(source.Write())!.ToJsonString(new() { WriteIndented = true }))), catalog).Write() == compiled.Write(), "Formatting changed compilation.");
        });

        test("Maps compiled: visuals change integrity but not gameplay identity or simulation", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            var changed = MapCompiler.Compile(Copy(source, visual: new(source.Visual.Palette.Select(p => "art-" + p).ToArray(), source.Visual.Cells)), catalog);
            NavigationChecks.Check(compiled.GameplayFingerprint == changed.GameplayFingerprint && compiled.DataFingerprint != changed.DataFingerprint, "Visual and gameplay identity were mixed.");
            using var first = compiled.Map.Prepare(players).CreateWorld();
            using var second = changed.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(first.Report() == second.Report(), "Visual data changed the world.");
            var tampered = JsonNode.Parse(compiled.Write())!;
            tampered["map"]!["visual"]!["palette"]![0] = "different-art";
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("tampered", tampered.ToJsonString()), catalog));
            var changedCatalog = manifest.LoadUnits(manifest.Rules.Append(new ContentSource("extra-unit.yaml",
                "compilation-test-extra-unit: {Health: {HP: 1}}")));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("wrong-content", compiled.Write()), changedCatalog));
        });

        test("Maps compiled: invalid input, unsupported geometry, duplicate keys and damaged artifacts fail closed", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            foreach (var mutate in new Action<JsonNode>[]
            {
                n => n["geometry"] = "heightfield",
                n => n["version"] = 2,
                n => n["compilerVersion"] = 2,
                n => n["extra"] = true,
                n => n["map"]!["cellSize"] = 512,
                n => n["map"]!["size"]![0] = int.MaxValue,
                n => n["map"]!["terrain"]!["cells"]![0] = -1,
                n => n["map"]!["terrain"]!["palette"]![0] = "missing-terrain",
                n => n["map"]!["objects"]![0]!["type"] = "missing-type",
                n => n["map"]!["objects"]![0]!["owner"] = "missing-owner",
                n => n["map"]!["objects"]![1]!["id"] = n["map"]!["objects"]![0]!["id"]!.GetValue<string>(),
                n => n["map"]!["objects"]![0]!["position"] = new JsonArray(1, 2, 3),
                n => n["map"]!["objects"]![0]!["position"]![0] = 1.5,
                n => n["map"]!["id"] = new string('x', MapCompiler.MaximumTextBytes + 1),
                n => n["map"]!["slots"] = null,
                n => n["gameplayFingerprint"] = new string('0', 64)
            })
            {
                var node = JsonNode.Parse(compiled.Write())!; mutate(node);
                NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("invalid", node.ToJsonString()), catalog));
            }
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("duplicate", compiled.Write().Replace("\"version\":1", "\"version\":1,\"version\":1")), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("trailing", compiled.Write() + " {}"), catalog));
            var raised = source.Objects.ToArray(); raised[0] = raised[0] with { Position = new(4608, 5632, 1) };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Copy(source, objects: raised), catalog));
        });

        test("Maps compiled: source ownership, late placement failure and cancellation preserve callers and files", () =>
        {
            var palette = source.Terrain.Palette.ToArray(); var cells = source.Terrain.Cells.ToArray();
            var layer = new MapLayer(palette, cells); palette[0] = "changed"; cells[0] = -1;
            var owned = MapCompiler.Compile(Copy(source, terrain: layer), catalog);
            NavigationChecks.Check(owned.GameplayFingerprint == MapCompiler.Compile(source, catalog).GameplayFingerprint, "Source retained mutable caller arrays.");
            var objects = source.Objects.ToArray(); objects[1] = objects[1] with { Position = objects[0].Position };
            var overlap = MapCompiler.Compile(Copy(source, objects: objects), catalog);
            NavigationChecks.Reject<ContentException>(() => overlap.Map.Prepare(players).CreateWorld());
            using var retry = owned.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(retry.Count == 3, "Rejected world leaked into the next initialization.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            NavigationChecks.Reject<OperationCanceledException>(() => MapCompiler.Compile(source, catalog, cancelled.Token));
            string path = Path.Combine(Path.GetTempPath(), "openrts-map-" + Guid.NewGuid().ToString("N") + ".ortmap.json");
            try
            {
                MapCompiler.Save(owned, path); string before = File.ReadAllText(path);
                NavigationChecks.Reject<OperationCanceledException>(() => MapCompiler.Save(owned, path, cancelled.Token));
                NavigationChecks.Check(File.ReadAllText(path) == before && MapCompiler.Read(new(path, before), catalog).DataFingerprint == owned.DataFingerprint, "Cancelled save replaced the installed artifact.");
            }
            finally { File.Delete(path); }
            NavigationChecks.Check(!typeof(MapCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name!.Contains("Godot") || a.Name == "OpenRTS.Network"), "Map compiler acquired a host dependency.");
        });

        test("Maps compiled: tiny independent fixture and rejected start configuration remain separate contracts", () =>
        {
            var tiny = MapCompiler.Compile(MapSource.Read(Read("content/maps/compilation-fixture.mapsource.json")), catalog);
            using var world = tiny.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(world.Count == 2 && world.Width == 4096 && world.Depth == 3072, "Independent fixture was not usable.");
            var skirmish = MapCompiler.Compile(MapSource.FromM1(Read(MapScenario.DefaultMap), catalog), catalog).Map;
            var invalid = StartingSet.Load(new("invalid-start.yaml", "version: 1\nid: invalid\ncredits: 0\nunits:\n  - {type: rifleman, offset: [536870912, 0]}\n"));
            NavigationChecks.Reject<ContentException>(() => skirmish.Prepare(players, invalid));
            using var valid = skirmish.Prepare(players, StartingSet.Load(Read(MapScenario.DefaultStartSet))).CreateWorld();
            NavigationChecks.Check(valid.Count > 2, "A rejected configuration invalidated the reusable map.");
        });
    }
}
