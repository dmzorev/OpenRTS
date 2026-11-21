using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class ConfigurationChecks
{
    private static void Check(bool value, string message) => NavigationChecks.Check(value, message);

    public static void Run(Action<string, Action> test)
    {
        test("Configuration: recursive directories have stable order, deduplicate paths and share inheritance", () =>
        {
            InDirectory((root, write) =>
            {
                write("rules/z/tank.yml", "tank: {Inherits: ^unit, Health: {HP: 200}}\n");
                write("rules/a.yaml", "^unit: {Health: {HP: 100}}\nscout: {Inherits: ^unit}\n");
                write("rules/notes.txt", "not yaml");
                write("game.yaml", "Version: 1\nTitle: Test\nRules: [rules, rules/./a.yaml]\n");
                var manifest = GameManifest.Load(ContentFiles.FromDirectory(root));
                Check(manifest.Rules.Select(source => source.Name).SequenceEqual(["rules/a.yaml", "rules/z/tank.yml"]), "Directory order or deduplication");
                var expected = UnitCatalog.Load([new("combined.yaml", "^unit: {Health: {HP: 100}}\nscout: {Inherits: ^unit}\ntank: {Inherits: ^unit, Health: {HP: 200}}\n")]);
                Check(manifest.LoadUnits().Summary == expected.Summary, "Moving definitions across directories changed gameplay");
                write("rules/z/duplicate.yaml", "scout: {Health: {HP: 1}}\n");
                ExpectContent(() => GameManifest.Load(Path.Combine(root, "game.yaml")).LoadUnits(), "rules/z/duplicate.yaml");
            });
        });
        test("Configuration: virtual filesystem enumeration order does not affect expansion", () =>
        {
            var files = new ContentFiles(path => path, path => path switch
            {
                "rules" => ["rules/z.yaml", "rules/nested", "rules/a.yml", "rules/image.png"],
                "rules/nested" => ["rules/nested/b.yaml"],
                _ => null
            });
            Check(files.Expand(["rules", "rules/a.yml"]).Select(source => source.Name)
                .SequenceEqual(["rules/a.yml", "rules/nested/b.yaml", "rules/z.yaml"]), "Virtual filesystem order leaked");
        });
        test("Configuration: manifest errors keep source context", () =>
        {
            InDirectory((root, write) =>
            {
                write("unit.yaml", "unit: {Health: {HP: 1}}\n");
                Directory.CreateDirectory(Path.Combine(root, "empty"));
                foreach (string text in new[]
                {
                    "Version: 2\nTitle: Test\nRules: [unit.yaml]",
                    "Version: 1\nTitle: Test\nRulse: [unit.yaml]",
                    "Version: 1\nTitle: Test\nRules: unit.yaml",
                    "Version: 1\nTitle: Test\nRules: []",
                    "Version: 1\nTitle: Test\nRules: [missing.yaml]",
                    "Version: 1\nTitle: Test\nRules: [empty]",
                    "Version: 1\nTitle: Test\nRules: [../outside.yaml]",
                    "Version: 1\nTitle: Test\nRules: &files [unit.yaml]"
                })
                {
                    write("game.yaml", text);
                    ExpectContent(() => GameManifest.Load(Path.Combine(root, "game.yaml")), "game.yaml:");
                }
            });
        });
        test("Configuration: settings reject invalid fields, ranges and menu defaults", () =>
        {
            foreach (string text in new[]
            {
                "Simulation: {TickRate: 0}", "Simulation: {SpeedPerTick: 1025}",
                "Simulation: {Tickrate: 25}", "Unknown: {}", "Match: {DefaultMap: ../outside.yaml}",
                "SensorRanges: {Radar: {Owners: [Enemy]}}",
                "SensorRanges: {Radar: {Mode: Sometimes}}",
                "SensorRanges: {Radar: {Color: invalid}}",
                "SensorRanges: {Radar: {Color: ''}}",
                "SensorRanges: {Radar: {Owner: Own}}",
                "Presentation: {Renderer: Canvas2D}",
                "Presentation: {Camera: Invalid}",
                "Match: {Maps: [{Path: a.yaml}]}",
                "Match: {Maps: [{Path: a.yaml, Title: A}]}",
                "Match: {DefaultMap: a.yaml, Maps: [{Path: a.yaml, Title: A}, {Path: a.yaml, Title: B}]}"
            }) ExpectContent(() => GameSettings.Load([new("settings.yaml", text)]), "settings.yaml:");
        });
        test("Configuration: sensor range YAML controls defaults and owner policy", () =>
        {
            var settings = GameSettings.Load([new("ranges.yaml", """
                SensorRanges:
                  Jam: {Mode: Always, Color: "#ff000080", Owners: [Own, Neutral]}
                  Radar: {Mode: Hidden, Owners: []}
                """)]).SensorRanges;
            Check(settings.Jam.Mode == SensorRangeMode.Always && settings.Jam.Color == "#ff000080" &&
                settings.Jam.Owners.SequenceEqual([SensorRangeOwner.Own, SensorRangeOwner.Neutral]), "Custom range defaults lost");
            Check(settings.Radar.Owners.Length == 0 && settings.Radar.Mode == SensorRangeMode.Hidden,
                "Explicitly empty policy must prohibit all owners");
            Check(settings.Stealth.Mode == SensorRangeMode.Selected &&
                settings.Stealth.Owners.SequenceEqual([SensorRangeOwner.Own, SensorRangeOwner.Allies]), "Safe defaults changed");
            var manifest = GameManifest.Load("sandbox/game.yaml");
            Check(manifest.Settings.SensorRanges.Jam.Color == "#ffb840a6" &&
                manifest.Settings.SensorRanges.Radar.Mode == SensorRangeMode.Selected, "Sandbox YAML defaults not loaded");
        });
        test("Configuration: sandbox manifest loads live catalogs and feeds map world settings", () =>
        {
            var manifest = GameManifest.Load("sandbox/game.yaml");
            var catalog = manifest.LoadUnits();
            var files = manifest.Files;
            manifest.LoadModels().ValidateUnits(catalog);
            var effects = EffectCatalog.Load(manifest.Effects);
            Check(catalog.Units.SelectMany(unit => unit.Weapons).All(mount => effects.Weapons.ContainsKey(mount.Weapon.Id)),
                "A live weapon has no effect definition");
            var match = MapScenario.Load(catalog, files.Read, manifest.Settings.Match.DefaultMap,
                defaultStartSetPath: manifest.Settings.Match.DefaultStartSet);
            var settings = GameSettings.Load([new("custom.yaml", "Simulation: {TickRate: 40, SpeedPerTick: 64}")]).Simulation;
            using var world = match.CreateWorld(new(), speedPerTick: settings.SpeedPerTick, tickRate: settings.TickRate);
            using var original = match.CreateWorld();
            Check(world.TickRate == 40 && world.SpeedPerTick == 64 && original.TickRate == 25 && original.SpeedPerTick == 128, "Settings did not reach world");
            Check(world.Report().StateHash != original.Report().StateHash, "World settings are absent from state hash");
            var fixedMatch = MapScenario.Load(catalog, files.Read, "content/maps/fixture.yaml", defaultStartSetPath: "unused.yaml");
            Check(fixedMatch.UnitCount > 0, "Default starting set was forced on a fixed map");
        });
        test("Configuration: isometric mod applies its model overrides to its game catalog", () =>
        {
            var standard = GameManifest.Load("sandbox/game.yaml");
            var isometric = GameManifest.Load("sandbox/isometric-game.yaml");
            Check(standard.Settings.Presentation is { Renderer: GameRenderer.Godot3D, Camera: GameCamera.Default } &&
                isometric.Settings.Presentation is { Renderer: GameRenderer.Godot3D, Camera: GameCamera.Isometric }, "Presentation profile was ignored");
            var units = isometric.LoadUnits();
            var models = isometric.LoadModels();
            models.ValidateUnits(units);
            Check(isometric.Settings.Presentation.ModelOverrides.ContainsKey("tank"), "The isometric tank override was omitted");
            foreach (var (id, replacement) in isometric.Settings.Presentation.ModelOverrides)
                Check(ReferenceEquals(models.Models[id].Parts, models.Models[replacement].Parts), "Model override was ignored: " + id);
            Check(!standard.LoadModels().Models.ContainsKey("iso-tank"), "Mod definitions leaked into the standard profile");
        });
    }

    private static void ExpectContent(Action action, string context)
    {
        try { action(); }
        catch (ContentException error)
        {
            Check(error.Message.Contains(context, StringComparison.Ordinal), "Missing diagnostic context: " + error.Message);
            return;
        }
        throw new Exception("Expected ContentException: " + context);
    }

    private static void InDirectory(Action<string, Action<string, string>> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "openrts-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root, (path, text) =>
            {
                string full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            });
        }
        finally { Directory.Delete(root, true); }
    }
}
