using System.Text.Json;
using System.Diagnostics;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

try
{
    var path = "sandbox/content/units.yaml";
    bool simulate = false, benchmark = false, hasPath = false, navigation = false, reverseJobs = false;
    int count = 10000, ticks = DiagnosticScenario.DefaultTicks, workers = 4;
    foreach (string argument in args)
    {
        if (argument == "--simulate") simulate = true;
        else if (argument == "--benchmark") { simulate = true; benchmark = true; }
        else if (argument == "--navigation") { simulate = true; navigation = true; }
        else if (argument == "--reverse-jobs") reverseJobs = true;
        else if (argument.StartsWith("--count=", StringComparison.Ordinal) && int.TryParse(argument[8..], out int value) && value is >= 1 and <= 1000000) count = value;
        else if (argument.StartsWith("--ticks=", StringComparison.Ordinal) && int.TryParse(argument[8..], out value) && value is >= 1 and <= 1000000) ticks = value;
        else if (argument.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(argument[10..], out value) && value is >= 1 and <= 64) workers = value;
        else if (!argument.StartsWith("--", StringComparison.Ordinal) && !hasPath) { path = argument; hasPath = true; }
        else throw new ArgumentException("Usage: OpenRTS.Headless [units.yaml] [--simulate|--benchmark] [--navigation] [--workers=1..64] [--reverse-jobs] [--count=1..1000000] [--ticks=1..1000000]");
    }
    var catalog = UnitCatalog.Load([new ContentSource(path, File.ReadAllText(path))]);
    Console.WriteLine("OPENRTS_CONTENT:" + JsonSerializer.Serialize(catalog.Summary));
    if (simulate)
    {
        SimulationWorld Create() => navigation ? NavigationScenario.Create(catalog, count, workers, reverseJobs) : DiagnosticScenario.Create(catalog, count);
        var world = Create();
        var commands = navigation ? NavigationScenario.Commands(count, ticks) : benchmark
            ? new WorldCommand[] { new(1, 0, 1, CommandKind.MoveAll, Position: new(world.Width, world.Depth)) }
            : DiagnosticScenario.Commands(count, ticks);
        SimulationReport report;
        if (benchmark)
        {
            // Warm JIT on a separate world; measured ticks exclude initialization, snapshots and state hashing.
            var warmup = Create();
            DiagnosticScenario.Run(warmup, 64, commands);
            var samples = new double[ticks];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int cursor = 0;
            long fieldsBuilt = 0, visitedCells = 0;
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
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double firstTickMilliseconds = samples[0];
            Array.Sort(samples);
            Console.WriteLine("OPENRTS_BENCHMARK:" + JsonSerializer.Serialize(new
            {
                Workload = navigation ? "shared BFS and cell reservations, no combat/rendering" : "direct movement, no navigation/combat/rendering",
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
                Count = count, Ticks = ticks, Workers = navigation ? workers : 1,
                MapWidthCells = world.Width / 1024, MapHeightCells = world.Depth / 1024,
                TickRate = world.TickRate, P50Milliseconds = samples[(ticks - 1) / 2],
                P95Milliseconds = samples[(int)Math.Ceiling(ticks * 0.95) - 1], AllocatedBytes = allocated,
                FirstTickMilliseconds = firstTickMilliseconds, MaximumTickMilliseconds = samples[^1],
                MovingAtEnd = world.MovingCount, WaitingAtEnd = world.NavigationWaitingCount,
                FieldsBuilt = fieldsBuilt, VisitedCells = visitedCells, BuildsMilliseconds = buildsMilliseconds
            }));
            report = world.Report();
        }
        else report = DiagnosticScenario.Run(world, ticks, commands);
        Console.WriteLine("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
    }
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ContentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
