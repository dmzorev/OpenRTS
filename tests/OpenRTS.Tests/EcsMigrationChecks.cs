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
                        Projectiles = frame.Projectiles.ToArray(), Tasks = frame.Tasks.ToArray(), Players = frame.Players.ToArray(), Projects = frame.ConstructionProjects.ToArray()
                    }, TraceOptions));
                }
                string actual = Convert.ToHexString(trace.GetHashAndReset());
                NavigationChecks.Check(actual == Expected[scenario], $"{scenario} trace changed: {actual}");
            });
    }

    // Rebased for supply state in frame poses and the new skirmish building and production content.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "5FFFFEBC309F1BF9A49738880F3AA4E07D1C1A0E0546A7103EBFC6462DA9AE32",
        ["navigation"] = "22AC8D239C505915827BF9A15939BFB6190AA3DCF6632886DD14647A13EA5E8E",
        ["combat"] = "06E77A5442272575F5EE71E049CBF1DD8EF62230DEEA12985BFB5AB92E69C182",
        ["buildings"] = "3853E82F5ADD895E8DDD51B27CD64A213CEFE0462F785AB669AF7A203A278EC2",
        ["production"] = "AAE6232D73CABD180775C332EEE3EDE4FCB89A446D73D52854D9741CDF70362D"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
