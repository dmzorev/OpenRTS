using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

internal static class EcsBenchmark
{
    internal static void Run(int count, int ticks)
    {
        foreach (string workload in new[] { "units", "mixed", "production" })
        {
            Warm(workload, Math.Min(count, 1000));
            long memoryBefore = GC.GetTotalMemory(true), allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            using var world = Create(workload, count, out var commands);
            double initialization = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long initializationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            long retained = GC.GetTotalMemory(true) - memoryBefore;
            var samples = new double[ticks];
            allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < ticks; i++)
            {
                started = Stopwatch.GetTimestamp();
                world.Step(i == 0 ? commands : []);
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            long stepAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            var poses = new EntityPose[world.Count];
            started = Stopwatch.GetTimestamp(); world.CopyPoses(poses);
            double snapshot = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp(); var report = world.Report();
            double hashing = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double[] steady = samples.Skip(1).Order().ToArray();
            Console.WriteLine("OPENRTS_ECS_BENCHMARK:" + JsonSerializer.Serialize(new
            {
                Workload = workload, InitialCount = count, Ticks = ticks, FinalCount = world.Count,
                InitializationMilliseconds = initialization, InitializationAllocatedBytes = initializationAllocation,
                RetainedManagedBytes = retained, FirstTickMilliseconds = samples[0],
                SteadyP50Milliseconds = steady.Length == 0 ? 0 : steady[(steady.Length - 1) / 2],
                SteadyP95Milliseconds = steady.Length == 0 ? 0 : steady[(int)Math.Ceiling(steady.Length * .95) - 1],
                StepAllocatedBytes = stepAllocation, SnapshotMilliseconds = snapshot, HashMilliseconds = hashing,
                State = report, Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ProcessorCount = Environment.ProcessorCount,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default"
            }));
        }
    }

    private static void Warm(string workload, int count)
    {
        using var world = Create(workload, count, out var commands);
        for (int tick = 0; tick < 64; tick++) world.Step(tick == 0 ? commands : []);
        var poses = new EntityPose[world.Count]; world.CopyPoses(poses); world.Report();
    }

    private static SimulationWorld Create(string workload, int count, out WorldCommand[] commands)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        UnitDefinition[] types = [new("unit", 100, build: new(8)),
            new("factory", 100, movable: false, taskQueues: [new("units", exit: new(1024, 0), exitSearchRadius: 0)],
                products: [new("unit", "unit", "units")])];
        var world = new SimulationWorld(types, (side + 4) * 4096, (side + 40) * 4096, initialCapacity: count);
        var producers = new List<EntityId>();
        for (int i = 0; i < count; i++)
        {
            bool producer = workload == "production" || workload == "mixed" && i % 20 == 0;
            var id = world.AddInitialUnit(producer ? 1 : 0, new((i % side + 2) * 4096, (i / side + 2) * 4096), 1);
            if (producer) producers.Add(id);
        }
        var destination = new WorldPosition(world.Width / 2, world.Depth - 1024);
        commands = producers.Count == 0 ? [new(1, 0, 1, CommandKind.MoveAll, Position: destination)] :
            [.. producers.Select((id, i) => new WorldCommand(1, 0, i + 1, CommandKind.EnqueueTask, id, Action: "unit", Repeat: true)),
             new(1, 0, producers.Count + 1, CommandKind.SetRally, Position: destination, Recipients: new(producers))];
        return world;
    }
}
