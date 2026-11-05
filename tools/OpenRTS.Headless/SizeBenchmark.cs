using System.Diagnostics;
using System.Text.Json;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

internal static class SizeBenchmark
{
    // Independent lanes isolate body/clearance cost from crowd throughput and combat.
    internal static void Run(int count, int ticks, int workers, bool reverse)
    {
        int columns = (int)Math.Ceiling(Math.Sqrt(count)), rows = (count + columns - 1) / columns;
        int width = columns * 2 + 16, height = rows * 2 + 40;
        UnitDefinition[] types = [new("small", 100, body: BodyShape.Circle(160)), new("medium", 100, body: BodyShape.Circle(384)),
            new("large", 100, body: BodyShape.Circle(640)), new("box", 100, body: BodyShape.Box(640, 320)),
            new("building", 100, ["idle"], movable: false, footprint: new(2, 2, [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]))];
        SimulationWorld Create()
        {
            var w = new SimulationWorld(types, width * 1024, height * 1024, initialCapacity: count + 1,
                navigationGrid: new(width, height), navigationWorkers: workers, reverseNavigationJobs: reverse);
            for (int i = 0; i < count; i++) w.AddInitialUnit(i % 4, new((i % columns * 2 + 4) * 1024, (i / columns * 2 + 4) * 1024));
            return w;
        }
        var commands = Enumerable.Range(0, count).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move, new((uint)i + 1),
            new((i % columns * 2 + 4) * 1024, (i / columns * 2 + 20) * 1024))).ToArray();
        var obstacle = new WorldCommand(20, 0, 1, CommandKind.Spawn, Position: new((columns + 4) * 1024, (rows * 2 + 15) * 1024), UnitType: 4);
        // JIT is warmed on a smaller world with the same shapes; graph construction remains measured cold.
        using var warm = new SimulationWorld(types, 16384, 16384, navigationGrid: new(16, 16, Enumerable.Range(0, 11).Select(x => new GridCell(x, 5))));
        for (int i = 0; i < 4; i++) warm.AddInitialUnit(i, new((i * 2 + 2) * 1024, 2048));
        warm.Step(Enumerable.Range(0, 4).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move, new((uint)i + 1), new((i * 2 + 2) * 1024, 8192))).ToArray());
        for (int i = 0; i < 64; i++) warm.Step(i == 18 ? [new(20, 0, 1, CommandKind.Spawn, Position: new(12288, 12288), UnitType: 4)] : []);
        long started = Stopwatch.GetTimestamp(); using var world = Create(); double initialization = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double[] samples = new double[ticks]; long allocation = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ticks; i++)
        {
            started = Stopwatch.GetTimestamp(); world.Step(i == 0 ? commands : i == 19 ? [obstacle] : []);
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
        double first = samples[0], edit = ticks >= 20 ? samples[19] : 0, maximum = samples.Max();
        double[] steady = samples.Where((_, i) => i != 0 && i != 19).Order().ToArray();
        started = Stopwatch.GetTimestamp(); var poses = new EntityPose[world.Count]; world.CopyPoses(poses);
        double snapshot = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp(); var report = world.Report(); double hashing = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine("OPENRTS_SIZE_BENCHMARK:" + JsonSerializer.Serialize(new {
            Count = count, Ticks = ticks, Workers = workers, WidthCells = width, HeightCells = height, InitializationMilliseconds = initialization,
            FirstTickMilliseconds = first, EditTickMilliseconds = edit, MaximumMilliseconds = maximum,
            SteadyP50Milliseconds = steady.Length == 0 ? 0 : steady[(steady.Length - 1) / 2],
            SteadyP95Milliseconds = steady.Length == 0 ? 0 : steady[(int)Math.Ceiling(steady.Length * .95) - 1],
            MainThreadAllocatedBytes = allocation, SnapshotMilliseconds = snapshot, HashMilliseconds = hashing,
            MovingAtEnd = world.MovingCount, EntitiesAtEnd = world.Count, NoCombatOrRendering = true,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default" }));
        Console.WriteLine("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
    }
}
