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
                const string fixture = "tests/OpenRTS.Tests/Fixtures/ecs-baseline";
                var terrain = LocomotionCatalog.Load([Read("terrain.yaml")]);
                var weapons = WeaponCatalog.Load([Read("weapons.yaml")]);
                var units = UnitCatalog.Load([Read("units.yaml")], terrain, weapons);
                var buildings = UnitCatalog.Load([Read("units.yaml"), Read("buildings.yaml")], terrain, weapons);
                static ContentSource Read(string file) => new(file, File.ReadAllText(Path.Combine(fixture, file)));
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

    // Rebased on 67851b4 with the frozen fixture catalog, independently of the sandbox YAML.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "38110521DF95F075D4BA47714BED2C0CE093D8734DC901A194293EAF44E8243B",
        ["navigation"] = "76C18CCD4847B48A7493DC2AD2F63EF92ED73023F30D9FF2B72661C8A3BBCB6E",
        ["combat"] = "D9A10D04CB0B749316BD4A8C30EA35308E32A1A7320D487C6A51584C4FED9134",
        ["buildings"] = "FB9296B6EEFCD88DB41790F90D2C70A632F8EF3FC4D0B87AAF0726176125507C",
        ["production"] = "BB67C19BEB67524EF220BFA9925B5822FC16B59F5D60C5D77AD95616CC958FE8"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
