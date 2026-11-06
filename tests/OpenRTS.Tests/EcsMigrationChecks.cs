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
                var units = LocomotionChecks.Units();
                var buildings = UnitCatalog.Load([new("units", File.ReadAllText("sandbox/content/units.yaml")),
                    new("buildings", File.ReadAllText("sandbox/content/buildings.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
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
                        Projectiles = frame.Projectiles.ToArray(), Tasks = frame.Tasks.ToArray(), Players = frame.Players.ToArray()
                    }, TraceOptions));
                }
                string actual = Convert.ToHexString(trace.GetHashAndReset());
                NavigationChecks.Check(actual == Expected[scenario], $"{scenario} trace changed: {actual}");
            });
    }

    // Rebased for transport definitions and state, plus the Carrier field in frame poses.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "F9318DBBC4B538E6A686D8B8553C5FA098AD5C67FF1544A3184232A1AACBD9D2",
        ["navigation"] = "9FB39C6528A62AC7A73908CD5CE14B6B0D6581E9506C5DB30937FF1972746811",
        ["combat"] = "1F51FC99DD46F26BC3D40DC69E6A23FC3F48EA61287CE6DD097A39423841B376",
        ["buildings"] = "4F4215CF9564B3BD90BCC531D60023DE3F82B4616E7A981047017FBD6192BFE6",
        ["production"] = "F9ACED5ED32C905EF40EE65754C81EE7BEC1E5AB42405A2E140851FFA40DFFDE"
    };

    private static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
