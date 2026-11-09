using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class EcsMigrationChecks
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var scenario in new[] { "direct", "navigation", "combat", "buildings", "production" })
            test("ECS baseline: " + scenario, () =>
            {
                var units = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                var buildings = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units.yaml")),
                    new("buildings", LegacyContent("sandbox/content/buildings.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                using var world = scenario switch
                {
                    "direct" => DiagnosticScenario.Create(units, 100),
                    "navigation" => NavigationScenario.Create(units, 100, 1),
                    "combat" => ShowcaseScenario.Create(units),
                    _ => BuildingScenario.Create(buildings)
                };
                int ticks = scenario == "production" ? 160 : scenario == "navigation" ? 32 : 24;
                WorldCommand[] commands = scenario switch
                {
                    "direct" => DiagnosticScenario.Commands(100, ticks),
                    "navigation" => NavigationScenario.Commands(100, ticks),
                    "combat" => ShowcaseScenario.VerificationCommands(units),
                    "production" => ProductionScenario.Commands(buildings),
                    _ => BuildingScenario.Commands(buildings)
                };
                using var trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var frames = new FrameExchange();
                for (int tick = 0; tick <= ticks; tick++)
                {
                    if (tick != 0) world.Step(commands.Where(c => c.Tick == tick).ToArray());
                    frames.TryPublish(world);
                    using var frame = frames.AcquireLatest()!;
                    trace.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        State = world.Report(), Commands = world.AppliedCommands.ToArray(), Events = world.Events.ToArray(),
                        Entities = frame.Poses.ToArray(), Weapons = frame.Weapons.ToArray(),
                        Projectiles = frame.Projectiles.ToArray(), Tasks = frame.Tasks.ToArray(), Players = frame.Players.ToArray(), Projects = frame.ConstructionProjects.ToArray()
                    }, TraceOptions));
                }
                string actual = Convert.ToHexString(trace.GetHashAndReset());
                NavigationChecks.Check(actual == Expected[scenario], $"{scenario} trace changed: {actual}");
            });
    }

    // Preserve the pre-fog mechanical baseline; positive vision radii have their own fingerprint tests.
    private static string LegacyContent(string path) => System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), @"(?m)^  Vision:.*\r?\n", "");

    // Format 28/catalog 13 with the higher aviation profiles. Normalized ground traces still match 3ae082f.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "8C1F6760F09485F80DC743124CB606EDE6C4BE8613713C01525803C98A7EF682",
        ["navigation"] = "094E28BF5F2FE380BCADB5B2F438B9BC0406FD69B1FCF3F8DDFA450D2439187E",
        ["combat"] = "5145E06628186DED27756A7C1842375E611A44083FA09B660F61908BFFBF7692",
        ["buildings"] = "49F633653BB408E0D31F9FD269B33C2B3AC871F7088FB9C549A3C664B8EF53BE",
        ["production"] = "849694E8162F694FC255D9F704FE9A9B6DC06A5B98BABB40ED0036306F1F44F0"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
