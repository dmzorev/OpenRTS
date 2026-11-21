using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using DeclarativeContent;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

try
{
    var path = "";
    string? mapPath = null, startSetPath = null, aiConfig = null, aiProfile = null, modPath = null, compileMapPath = null;
    LocalMapPlaytest? localPlaytest = null;
    bool fogRetentionBenchmark = false, fogGroupsBenchmark = false, fogBenchmark = false, fog = false, skirmishAi = false, ticksSpecified = false, aviation = false;
    uint? aiPlayer = null;
    bool simulate = false, benchmark = false, hasPath = false, navigation = false, reverseJobs = false, showcase = false, buildings = false, sizeBenchmark = false, production = false, ecsBenchmark = false, skirmish = false;
    int count = 10000, ticks = DiagnosticScenario.DefaultTicks, workers = 4;
    foreach (string argument in args)
    {
        if (argument == "--fog-retention-benchmark") fogRetentionBenchmark = true;
        else if (argument == "--fog-groups-benchmark") fogGroupsBenchmark = true;
        else if (argument == "--fog-benchmark") fogBenchmark = true;
        else if (argument == "--fog") { fog = simulate = true; }
        else if (argument == "--aviation") { aviation = simulate = true; mapPath = AviationScenario.Map; }
        else if (argument == "--skirmish") { skirmish = simulate = true; }
        else if (argument == "--skirmish-ai") { skirmishAi = simulate = true; }
        else if (argument.StartsWith("--mod=", StringComparison.Ordinal) && argument.Length > 6) modPath = argument[6..];
        else if (argument.StartsWith("--ai-player=", StringComparison.Ordinal) && uint.TryParse(argument[12..], out uint player) && player is >= 1 and <= MapScenario.MaximumPlayers) aiPlayer = player;
        else if (argument.StartsWith("--ai-config=", StringComparison.Ordinal) && argument.Length > 12) aiConfig = argument[12..];
        else if (argument.StartsWith("--ai-profile=", StringComparison.Ordinal) && argument.Length > 13) aiProfile = argument[13..];
        else if (argument.StartsWith("--map=", StringComparison.Ordinal) && argument.Length > 6) { mapPath = argument[6..]; simulate = true; }
        else if (argument.StartsWith("--local-playtest=", StringComparison.Ordinal)) localPlaytest = LocalMapPlaytest.Read(argument[17..]);
        else if (argument.StartsWith("--compile-map=", StringComparison.Ordinal) && argument.Length > 14) compileMapPath = argument[14..];
        else if (argument.StartsWith("--start-set=", StringComparison.Ordinal) && argument.Length > 12) { startSetPath = argument[12..]; simulate = true; }
        else if (argument == "--ecs-benchmark") ecsBenchmark = true;
        else if (argument == "--size-benchmark") sizeBenchmark = true;
        else if (argument == "--simulate") simulate = true;
        else if (argument == "--benchmark") { simulate = true; benchmark = true; }
        else if (argument == "--production") { simulate = true; buildings = true; production = true; }
        else if (argument == "--buildings") { simulate = true; buildings = true; }
        else if (argument == "--showcase") { simulate = true; showcase = true; }
        else if (argument == "--navigation") { simulate = true; navigation = true; }
        else if (argument == "--reverse-jobs") reverseJobs = true;
        else if (argument.StartsWith("--count=", StringComparison.Ordinal) && int.TryParse(argument[8..], out int value) && value is >= 1 and <= 1000000) count = value;
        else if (argument.StartsWith("--ticks=", StringComparison.Ordinal) && int.TryParse(argument[8..], out value) && value is >= 1 and <= 1000000) { ticks = value; ticksSpecified = true; }
        else if (argument.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(argument[10..], out value) && value is >= 1 and <= 64) workers = value;
        else if (!argument.StartsWith("--", StringComparison.Ordinal) && !hasPath) { path = argument; hasPath = true; }
        else throw new ArgumentException("Usage: OpenRTS.Headless [units.yaml] [--mod=game.yaml] [--simulate|--benchmark|--fog-retention-benchmark|--ecs-benchmark|--size-benchmark|--showcase|--buildings|--production] [--navigation] [--skirmish|--skirmish-ai|--map=path] [--ai-player=1..4] [--ai-config=path] [--ai-profile=id] [--start-set=path] [--compile-map=output.ortmap.json] [--workers=1..64] [--reverse-jobs] [--count=1..1000000] [--ticks=1..1000000]");
    }
    if (localPlaytest is not null && (mapPath is null || hasPath || startSetPath is not null || compileMapPath is not null ||
        fog || aviation || skirmish || skirmishAi))
        throw new ArgumentException("--local-playtest requires its compiled map and game manifest and uses its validated starting set.");
    if (skirmishAi && skirmish)
        throw new ArgumentException("Select either the scripted skirmish or the AI skirmish.");
    if (!skirmishAi && (aiConfig is not null || aiPlayer.HasValue || aiProfile is not null))
        throw new ArgumentException("--ai-player, --ai-config and --ai-profile require --skirmish-ai.");
    if (aviation && !ticksSpecified) ticks = AviationScenario.Ticks;
    if (skirmishAi && !ticksSpecified) ticks = SkirmishAiScenario.DefaultTicks;
    bool mapRequested = skirmish || skirmishAi || mapPath is not null || startSetPath is not null || compileMapPath is not null;
    if (mapRequested && (buildings || showcase || navigation || benchmark || ecsBenchmark || sizeBenchmark || skirmish && mapPath is not null))
        throw new ArgumentException("Select one map or diagnostic scenario.");
    if (fogRetentionBenchmark) { FogRetentionBenchmark.Run(count,ticks); return 0; }
    if (fogGroupsBenchmark) { FogGroupBenchmark.Run(count, ticks); return 0; }
    if (fogBenchmark) { FogBenchmark.Run(count, ticks); return 0; }
    if (ecsBenchmark) { EcsBenchmark.Run(count, ticks); return 0; }
    if (sizeBenchmark) { SizeBenchmark.Run(count, ticks, workers, reverseJobs); return 0; }
    if (buildings && (showcase || navigation || benchmark)) throw new ArgumentException("Buildings is a separate fixture.");
    if (showcase && (navigation || benchmark)) throw new ArgumentException("Showcase is a presentation fixture; use navigation/benchmark separately.");
    if (hasPath && modPath is not null) throw new ArgumentException("Choose a legacy units file or --mod, not both.");
    var manifest = hasPath ? null : GameManifest.Load(modPath ?? (buildings || mapRequested ? "sandbox/game.yaml" : "sandbox/diagnostic-game.yaml"));
    var settings = manifest?.Settings ?? GameSettings.Load([]);
    UnitCatalog catalog;
    if (manifest is not null)
        catalog = manifest.LoadUnits(fog ? manifest.Rules.Select(source => source with { Text = FogScenario.AddVision(source.Text) }) : null);
    else
    {
        // Preserve the positional units.yaml entry point for existing consumers.
        var terrainPath = Path.Combine(Path.GetDirectoryName(path)!, "terrain.yaml");
        var weaponPath = Path.Combine(Path.GetDirectoryName(path)!, "weapons.yaml");
        var sources = new List<ContentSource> { new(path, fog ? FogScenario.AddVision(File.ReadAllText(path)) : File.ReadAllText(path)) };
        if (buildings || mapRequested) { var buildingPath = Path.Combine(Path.GetDirectoryName(path)!, "buildings.yaml"); sources.Add(new(buildingPath, File.ReadAllText(buildingPath))); }
        catalog = UnitCatalog.Load(sources,
            File.Exists(terrainPath) ? LocomotionCatalog.Load([new(terrainPath, File.ReadAllText(terrainPath))]) : null,
            File.Exists(weaponPath) ? WeaponCatalog.Load([new(weaponPath, File.ReadAllText(weaponPath))]) : null);
    }
    Console.WriteLine("OPENRTS_CONTENT:" + JsonSerializer.Serialize(catalog.Summary));
    ContentSource ReadMapSource(string file)
    {
        if (manifest is not null && !Path.IsPathRooted(file)) return manifest.Files.Read(file);
        string fullPath = Path.Combine("sandbox", file);
        return new(file, File.ReadAllText(fullPath));
    }
    if (compileMapPath is not null)
    {
        if (!compileMapPath.EndsWith(".ortmap.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Compiled map output must end with .ortmap.json; source files are not overwritten.");
        string inputPath = mapPath ?? settings.Match.DefaultMap;
        if (SceneMapBundle.IsPath(inputPath))
            throw new ArgumentException("Use the Godot scene-map build command to publish a scene map. Headless reads published bundles through --map.");
        var input = ReadMapSource(inputPath);
        var source = input.Name.EndsWith(".mapsource.json", StringComparison.OrdinalIgnoreCase)
            ? MapSource.Read(input) : MapSource.FromM1(input, catalog);
        var compiled = MapCompiler.Compile(source, catalog);
        MapCompiler.Save(compiled, compileMapPath);
        Console.WriteLine("OPENRTS_MAP:" + JsonSerializer.Serialize(new { compiled.Map.Id, compiled.GameplayFingerprint,
            compiled.DataFingerprint, compiled.ContentFingerprint, Path = Path.GetFullPath(compileMapPath) }));
        return 0;
    }
    if (simulate)
    {
        var map = localPlaytest is not null ? localPlaytest.Load(mapPath!, ReadMapSource, catalog, manifest!)
            : aviation ? AviationScenario.Load(catalog, ReadMapSource)
            : mapRequested ? MapScenario.Load(catalog, ReadMapSource, mapPath ?? settings.Match.DefaultMap, startSetPath, settings.Match.DefaultStartSet)
            : null;
        SimulationWorld Create() => fog ? FogScenario.Create(catalog) : map is not null ? map.CreateWorld(new(), speedPerTick: settings.Simulation.SpeedPerTick, workers: workers, tickRate: settings.Simulation.TickRate) : buildings ? BuildingScenario.Create(catalog, workers) : showcase ? ShowcaseScenario.Create(catalog) : navigation ? NavigationScenario.Create(catalog, count, workers, reverseJobs) : DiagnosticScenario.Create(catalog, count);
        using var world = Create();
        var commands = fog ? [] : skirmish && startSetPath is null ? SkirmishScenario.Commands() : aviation ? AviationScenario.Commands : map is not null ? [] : production ? ProductionScenario.Commands(catalog) : buildings ? BuildingScenario.Commands(catalog) : showcase ? ShowcaseScenario.VerificationCommands(catalog) : navigation ? NavigationScenario.Commands(count, ticks) : benchmark
            ? new WorldCommand[] { new(1, 0, 1, CommandKind.MoveAll, Position: new(world.Width, world.Depth)) }
            : DiagnosticScenario.Commands(count, ticks);
        SimulationReport report;
        if (skirmishAi)
        {
            uint player = aiPlayer ?? 2;
            string config = aiConfig ?? (manifest is null ? Path.Combine(Path.GetDirectoryName(path)!, "skirmish-ai.yaml") : settings.Match.AiConfig);
            var aiSource = aiConfig is null && manifest is not null ? manifest.Files.Read(config) : new(config, File.ReadAllText(config));
            var profile = SkirmishAiCatalog.Load(aiSource, catalog, aiProfile ?? settings.Match.AiProfile);
            var ai = SkirmishAiScenario.Create(catalog, profile, map!.Map, player);
            int issued = 0, attacks = 0, completedBuildings = 0, trained = 0, captured = 0, trucks = 0, upgradedMines = 0;
            EntityPose[] poses = [];
            report = SkirmishAiScenario.Run(world, ai, ticks, state =>
            {
                issued += state.AppliedCommands.Length;
                foreach (var command in state.AppliedCommands)
                    if (command.Kind == CommandKind.AttackMove) attacks++;
                foreach (var value in state.Events)
                {
                    if (value.Kind == WorldEventKind.OwnerChanged && value.Owner == player &&
                        catalog.Units[value.UnitType].Capturable is not null) captured++;
                    if (value.Kind is WorldEventKind.Produced or WorldEventKind.ConstructionCompleted)
                    {
                        Array.Resize(ref poses, state.Count);
                        state.CopyPoses(poses);
                        var id = value.Kind == WorldEventKind.Produced ? value.TargetEntity : value.Entity;
                        var unit = Array.Find(poses, p => p.Id == id && p.Presentation.Owner == player);
                        if (unit.Id == default) continue;
                        var definition = catalog.Units[unit.UnitType];
                        if (definition.Supply?.Role == SupplyRole.Mine && unit.Upgrade.Completed is not null) upgradedMines++;
                        else if (definition.Supply?.Role == SupplyRole.Truck) trucks++;
                        else if (definition.Footprint is not null && unit.Upgrade.Completed is null) completedBuildings++;
                        else if (profile.Sergeants.Any(rule => rule.Definition.Members.Any(member => member.Recipe.ProductType == unit.UnitType))) trained++;
                    }
                }
            });
            Console.WriteLine("OPENRTS_AI:" + JsonSerializer.Serialize(new
            {
                Player = player, Profile = profile.Id, SkirmishAi.PolicyVersion,
                ProfileFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile))),
                Commands = issued, Attacks = attacks, Buildings = completedBuildings, Trained = trained, Captured = captured,
                SupplyTrucks = trucks, UpgradedMines = upgradedMines, BuildingCandidates = ai.BuildingCandidatesChecked,
                Diagnostics = ai.Diagnostics
            }));
        }
        else if (benchmark)
        {
            // Warm JIT on a separate world; measured ticks exclude initialization, snapshots and state hashing.
            using var warmup = Create();
            DiagnosticScenario.Run(warmup, 64, commands);
            var samples = new double[ticks];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int cursor = 0;
            long fieldsBuilt = 0, visitedCells = 0, localSearches = 0, yields = 0, groupTrees = 0, groupVisited = 0, exchanges = 0;
            double groupMilliseconds = 0;
            long? completedAtTick = null;
            long failedRoutes = 0;
            long lastOrderTick = commands.Length == 0 ? 0 : commands[^1].Tick;
            double buildsMilliseconds = 0;
            for (int i = 0; i < ticks; i++)
            {
                int start = cursor;
                while (cursor < commands.Length && commands[cursor].Tick == world.Tick + 1) cursor++;
                long before = Stopwatch.GetTimestamp();
                world.Step(commands.AsSpan(start, cursor - start));
                samples[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                fieldsBuilt += world.NavigationBuiltFields;
                visitedCells += world.NavigationVisitedCells;
                buildsMilliseconds += world.NavigationBuildMilliseconds;
                localSearches += world.NavigationLocalSearches;
                yields += world.NavigationYields;
                groupTrees += world.GroupRouteBuilds; groupVisited += world.GroupRouteVisitedCells;
                groupMilliseconds += world.GroupRouteBuildMilliseconds;
                exchanges += world.NavigationGoalExchanges;
                foreach (var value in world.Events) if (value.Kind == WorldEventKind.NoRoute) failedRoutes++;
                if (completedAtTick is null && failedRoutes == 0 && world.Tick >= lastOrderTick && world.MovingCount == 0) completedAtTick = world.Tick;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double firstTickMilliseconds = samples[0];
            Array.Sort(samples);
            Console.WriteLine("OPENRTS_BENCHMARK:" + JsonSerializer.Serialize(new
            {
                Workload = navigation ? "world-space compact assignment, exact any-angle motion, swept circles, shared terrain routes and bounded local search, no combat/rendering" : "direct movement, no navigation/combat/rendering",
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
                Count = count, Ticks = ticks, Workers = navigation ? workers : 1,
                MapWidthCells = world.Width / 1024, MapHeightCells = world.Depth / 1024,
                TickRate = world.TickRate, P50Milliseconds = samples[(ticks - 1) / 2],
                P95Milliseconds = samples[(int)Math.Ceiling(ticks * 0.95) - 1], AllocatedBytes = allocated,
                FirstTickMilliseconds = firstTickMilliseconds, MaximumTickMilliseconds = samples[^1],
                MovingAtEnd = world.MovingCount, WaitingAtEnd = world.NavigationWaitingCount,
                QueueWaitingAtEnd = world.NavigationQueueWaitingCount,
                FieldsBuilt = fieldsBuilt, VisitedCells = visitedCells, BuildsMilliseconds = buildsMilliseconds,
                GroupSlots = world.GroupSlotCount, LocalSearches = localSearches, Yields = yields,
                GroupTreesBuilt = groupTrees, GroupTreeVisitedCells = groupVisited, GroupRoutePreparationMilliseconds = groupMilliseconds,
                GoalExchanges = exchanges,
                GroupTranslatingAtEnd = world.GroupTranslating, CompletedAtTick = completedAtTick, FailedRoutes = failedRoutes
            }));
            report = world.Report();
        }
        else report = DiagnosticScenario.Run(world, ticks, commands);
        Console.WriteLine("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
    }
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ContentException or InvalidDataException or JsonException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
