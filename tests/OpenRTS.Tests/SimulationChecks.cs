using System.Diagnostics;
using OpenRTS.Core;

internal static class SimulationChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static SimulationWorld World(int count = 1, int speed = 128)
    {
        var world = new SimulationWorld([new("unit", 100)], WorldPosition.MaximumExtent,
            WorldPosition.MaximumExtent, seed: 123, initialCapacity: count, speedPerTick: speed);
        for (int i = 0; i < count; i++) world.AddInitialUnit(0, new(i * 1024, 0, 512));
        return world;
    }

    private static EntityPose[] Poses(SimulationWorld world)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses;
    }

    private static SimulationReport Completed(SimulationSession session) =>
        session.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    private static void Until(Func<bool> predicate)
    {
        long start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(5)) throw new TimeoutException("Condition was not reached.");
            Thread.Sleep(1);
        }
    }

    public static void Run(Action<string, Action> test)
    {
        test("Integer movement arrives exactly and keeps elevation", () =>
        {
            using var world = World();
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(1024, 0))]);
            Check(Poses(world)[0].Current == new WorldPosition(128, 0, 512), "First fixed step");
            for (int i = 0; i < 20; i++) world.Step();
            Check(Poses(world)[0].Current == new WorldPosition(1024, 0, 512), "Exact arrival without overshoot");
        });
        test("Minimum speed does not stall diagonally", () =>
        {
            using var world = World(speed: 1);
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(3, 3))]);
            for (int i = 0; i < 10; i++) world.Step();
            Check(Poses(world)[0].Current == new WorldPosition(3, 3, 512), "Diagonal arrival");
        });
        test("Large-coordinate movement stays bounded and respects step length", () =>
        {
            using var world = World(speed: 1 << 20);
            var previous = Poses(world)[0].Current;
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(WorldPosition.MaximumExtent, WorldPosition.MaximumExtent))]);
            for (int i = 0; i < 20; i++)
            {
                var point = Poses(world)[0].Current;
                long dx = (long)point.X - previous.X, dy = (long)point.Y - previous.Y;
                Check(dx >= 0 && dy >= 0 && dx * dx + dy * dy <= (long)world.SpeedPerTick * world.SpeedPerTick, "Movement bound");
                previous = point;
                world.Step();
            }
        });
        test("Command order is issuer/sequence, independent of input order", () =>
        {
            using var first = World(); using var second = World();
            WorldCommand[] commands = [new(1, 1, 2, CommandKind.MoveAll, Position: new(0, 1024)),
                new(1, 0, 99, CommandKind.MoveAll, Position: new(1024, 0))];
            first.Step(commands); second.Step(commands.Reverse().ToArray());
            Check(first.Report() == second.Report(), "Canonical order");
            Check(Poses(first)[0].Current == new WorldPosition(0, 128, 512), "Later issuer wins conflict");
        });
        test("Invalid command batches do not advance authoritative state", () =>
        {
            using var world = World(); var before = world.Report();
            Reject<ArgumentException>(() => world.Step([new(2, 0, 1, CommandKind.MoveAll)]));
            Reject<ArgumentException>(() => world.Step([new(1, 0, 1, CommandKind.MoveAll), new(1, 0, 1, CommandKind.MoveAll)]));
            Reject<ArgumentOutOfRangeException>(() => world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(-1, 0))]));
            Reject<ArgumentException>(() => world.Step([new(1, 0, 1, (CommandKind)999)]));
            Check(before == world.Report(), "Rejected batch mutated world");
        });
        test("Packed removal preserves stable IDs and self-contained events", () =>
        {
            using var world = World(2);
            world.Step([new(1, 0, 1, CommandKind.Destroy, new(1)), new(1, 0, 2, CommandKind.Spawn, Position: new(3000, 4000))]);
            Check(Poses(world).Select(p => p.Id.Value).SequenceEqual([2u, 3u]), "IDs must not be reused");
            Check(world.Events[0].Kind == WorldEventKind.Destroyed && world.Events[0].Position.Elevation == 512, "Destroy event retains position");
            Check(world.Events[1].Kind == WorldEventKind.Spawned && world.Events[1].Position == new WorldPosition(3000, 4000), "Spawn event payload");
            world.Step([new(2, 0, 1, CommandKind.Move, new(1), new(0, 0))]);
            Check(world.Events[0].Kind == WorldEventKind.MissingEntity && world.Count == 2, "Missing entity is deterministic rejection");
            Reject<InvalidOperationException>(() => world.AddInitialUnit(0, new(0, 0)));
        });
        test("SplitMix64 uses known vectors and validates bounds", () =>
        {
            var random = new DeterministicRandom(0);
            Check(random.NextUInt64() == 0xE220A8397B1DCDAFUL, "First vector");
            Check(random.NextUInt64() == 0x6E789E6AA1B965F4UL, "Second vector");
            for (int i = 0; i < 1000; i++) Check(random.NextInt(7) is >= 0 and < 7, "Bounded sample");
            Reject<ArgumentOutOfRangeException>(() => random.NextInt(0));
        });
        test("Random targets and state hash do not depend on packed storage order", () =>
        {
            using var first = World(4); using var second = World(4);
            first.Step([new(1, 0, 1, CommandKind.Destroy, new(1)), new(1, 0, 2, CommandKind.Destroy, new(2))]);
            second.Step([new(1, 0, 1, CommandKind.Destroy, new(2)), new(1, 0, 2, CommandKind.Destroy, new(1))]);
            Check(!Poses(first).Select(p => p.Id).SequenceEqual(Poses(second).Select(p => p.Id)), "Fixture must have different packed order");
            Check(first.Report() == second.Report(), "Equivalent state hashes differ");
            WorldCommand[] scatter = [new(2, 0, 1, CommandKind.Scatter, Position: new(100000, 100000))];
            first.Step(scatter); second.Step(scatter);
            Check(first.Report() == second.Report(), "Packed order affected RNG");
        });
        test("Steady movement of 10000 entities allocates no managed memory in Step", () =>
        {
            using var world = World(10000);
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(30000000, 30000000))]);
            for (int i = 0; i < 10; i++) world.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++) world.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(bytes == 0, $"Unexpected tick allocations: {bytes}");
        });
        test("Leases protect published frames and saturated exchange drops only frames", () =>
        {
            using var world = World(); var exchange = new FrameExchange();
            Check(exchange.TryPublish(world), "Initial publication");
            using var first = exchange.AcquireLatest()!;
            var original = first.Poses[0];
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(10000, 0))]); exchange.TryPublish(world);
            using var second = exchange.AcquireLatest()!;
            world.Step(); exchange.TryPublish(world);
            using var third = exchange.AcquireLatest()!;
            world.Step();
            Check(!exchange.TryPublish(world), "Pinned slots cannot be overwritten");
            Check(first.Tick == 0 && first.Poses[0] == original, "Reader data changed");
            first.Dispose(); first.Dispose();
            Check(exchange.TryPublish(world), "Released slot was not reused");
            Reject<ObjectDisposedException>(() => { _ = first.Tick; });
            Check(second.Tick == 1 && third.Tick == 2, "Other leases changed");
        });
        test("Worker matches direct execution and final frame survives reader delays", () =>
        {
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.Scatter, Position: new(20000000, 20000000))];
            using var direct = World(10000); direct.Step(commands);
            while (direct.Tick < 64) direct.Step();
            using var session = new SimulationSession(() => World(10000), commands, 64, realtime: false);
            int observations = 0;
            Until(() =>
            {
                using var frame = session.Frames.AcquireLatest();
                if (frame is not null)
                {
                    var first = frame.Poses[0]; var last = frame.Poses[^1]; long tick = frame.Tick;
                    Thread.SpinWait(10000);
                    Check(frame.Tick == tick && frame.Poses[0] == first && frame.Poses[^1] == last, "Torn frame");
                    observations++;
                }
                return session.Completion.IsCompleted;
            });
            Check(Completed(session) == direct.Report(), "Worker/direct state mismatch");
            using var final = session.Frames.AcquireLatest()!;
            Check(final.Tick == 64 && observations > 0, "Final publication");
        });
        test("Real-time pacing changes elapsed time, not state", () =>
        {
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.MoveAll, Position: new(10000, 10000))];
            using var paced = new SimulationSession(() => World(), commands, 4, realtime: true);
            using var fast = new SimulationSession(() => World(), commands, 4, realtime: false);
            Check(Completed(paced) == Completed(fast), "Pacing changed simulation");
        });
        test("Events survive skipped frames and backlog overflow is explicit", () =>
        {
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.Spawn, Position: new(50, 50)), new(2, 0, 2, CommandKind.Destroy, new(2))];
            using var session = new SimulationSession(() => World(), commands, 16, realtime: false);
            Completed(session);
            var events = new List<WorldEvent>();
            while (session.TryReadEvent(out var value)) events.Add(value);
            Check(events.Count == 2 && events[0].Tick == 1 && events[1].Tick == 2, "Lost events");
            using var overflow = new SimulationSession(() => World(), commands, 16, realtime: false, maximumEventBacklog: 1);
            Reject<InvalidOperationException>(() => Completed(overflow));
        });
        test("Worker faults reach caller and worker can be stopped and restarted", () =>
        {
            using (var broken = new SimulationSession(() => throw new InvalidOperationException("factory failed"), endTick: 1))
                Reject<InvalidOperationException>(() => Completed(broken));
            for (int i = 0; i < 3; i++)
            {
                using var session = new SimulationSession(() => World(100));
                session.Submit(CommandKind.MoveAll, new(50000, 50000));
                Until(() => session.GetCommandLog().Length > 0);
                session.Dispose(); session.Dispose();
                var report = Completed(session);
                using var direct = World(100); var journal = session.GetCommandLog();
                while (direct.Tick < report.Tick)
                    direct.Step(journal.Where(c => c.Tick == direct.Tick + 1).ToArray());
                Check(report == direct.Report(), "Recorded live input did not replay");
                Reject<ObjectDisposedException>(() => session.Submit(CommandKind.MoveAll));
            }
        });
    }
}
