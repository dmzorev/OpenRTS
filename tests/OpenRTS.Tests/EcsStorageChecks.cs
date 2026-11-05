using OpenRTS.Core;

internal static class EcsStorageChecks
{
    private static UnitDefinition Factory(string id, WorldPosition exit, string product = "unit") =>
        new(id, 100, movable: false, body: BodyShape.Circle(128), taskQueues: [new("q", exit: exit, exitSearchRadius: 0)],
            products: [new("make", product, "q")], build: new(1));
    private static TaskPose[] Tasks(SimulationWorld world) { var tasks = new TaskPose[world.TaskCount]; world.CopyTaskPoses(tasks); return tasks; }

    public static void Run(Action<string, Action> test)
    {
        test("ECS: sequential producer commits protect a shared exit", () =>
        {
            using var world = new SimulationWorld([Factory("left", new(4096, 2048)), Factory("right", new(0, 2048)),
                new("unit", 100, body: BodyShape.Circle(128), build: new(1))], 20000, 16000);
            world.AddInitialUnit(0, new(4096, 4096), 1); world.AddInitialUnit(1, new(8192, 4096), 1);
            world.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make"), new(1, 0, 2, CommandKind.EnqueueTask, new(2), Action: "make")]);
            NavigationChecks.Check(world.Count == 3 && Tasks(world) is [{ Producer.Value: 2, Ready: true }], "Second producer reused an occupied exit");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(3))]);
            NavigationChecks.Check(world.Count == 3 && world.TaskCount == 0 && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Produced && e.Entity.Value == 2 && e.TargetEntity.Value == 4), "Released exit was not visible or game IDs were recycled");
        });
        test("ECS: producer creation changes membership after the current production pass", () =>
        {
            using var world = new SimulationWorld([Factory("factory", new(4096, 0), "factory")], 24000, 12000);
            world.AddInitialUnit(0, new(4096, 4096), 1);
            world.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make", Repeat: true)]);
            NavigationChecks.Check(world.Count == 2 && world.TaskCount == 1, "New producer was lost or processed in its spawning pass");
            world.Step([new(2, 0, 1, CommandKind.EnqueueTask, new(2), Action: "make")]);
            NavigationChecks.Check(world.Count == 3 && Tasks(world) is [{ Producer.Value: 1, Ready: true }], "Structural spawn invalidated production iteration or new membership was not refreshed");
            world.Step([new(3, 0, 1, CommandKind.Destroy, new(1))]);
            NavigationChecks.Check(world.TaskCount == 0 && world.Count == 2, "Producer removal left stale task membership");
        });
        test("ECS: reused storage slots do not inherit queues or rally and publish in domain ID order", () =>
        {
            using var world = new SimulationWorld([Factory("factory", new(4096, 0)), new("unit", 100, build: new(20))], 24000, 12000);
            world.AddInitialUnit(0, new(4096, 4096), 1); world.AddInitialUnit(0, new(8192, 4096), 1);
            world.Step([new(1, 0, 1, CommandKind.SetRally, new(1), new(20000, 10000)), new(1, 0, 2, CommandKind.EnqueueTask, new(1), Action: "make")]);
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(1)), new(2, 0, 2, CommandKind.Spawn, Position: new(4096, 4096), UnitType: 0, Owner: 1)]);
            NavigationChecks.Check(world.TaskCount == 0 && !NavigationChecks.Poses(world).Single(p => p.Id.Value == 3).HasRally, "Reused entity inherited cold state");
            world.Step([new(3, 0, 1, CommandKind.EnqueueTask, new(3), Action: "make"), new(3, 0, 2, CommandKind.EnqueueTask, new(2), Action: "make")]);
            NavigationChecks.Check(Tasks(world).Select(t => t.Producer.Value).SequenceEqual(new uint[] { 2, 3 }), "Task publication used storage order");
        });
        test("ECS: combat deaths remove producers before ready results are released", () =>
        {
            using var world = new SimulationWorld([Factory("factory", new(4096, 0)), new("unit", 100, build: new(1)),
                new("enemy", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 1000, 10, 0))], 20000, 12000);
            world.AddInitialUnit(0, new(4096, 4096), 1); world.AddInitialUnit(2, new(8192, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make"), new(1, 0, 2, CommandKind.AttackTarget, new(2), TargetEntity: new(1))]);
            NavigationChecks.Check(world.Count == 1 && world.TaskCount == 0 && !world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Produced), "Dead producer released its result");
        });
        test("ECS: disposal is idempotent, rejects use and preserves pinned frames", () =>
        {
            var world = new SimulationWorld([Factory("factory", new(4096, 0)), new("unit", 100, build: new(20))], 20000, 12000);
            try
            {
                world.AddInitialUnit(0, new(4096, 4096), 1);
                world.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]);
                var frames = new FrameExchange(); frames.TryPublish(world); using var lease = frames.AcquireLatest()!;
                world.Dispose(); world.Dispose();
                NavigationChecks.Check(lease.Poses.Length == 1 && lease.Tasks.Length == 1, "Disposal invalidated published data");
                NavigationChecks.Reject<ObjectDisposedException>(() => world.Step());
                NavigationChecks.Reject<ObjectDisposedException>(() => world.Report());
                NavigationChecks.Reject<ObjectDisposedException>(() => world.AddInitialUnit(0, new(4096, 4096)));
                NavigationChecks.Reject<ObjectDisposedException>(() => world.CopyTaskPoses([]));
                NavigationChecks.Reject<ObjectDisposedException>(() => world.CopyPoses([]));
                NavigationChecks.Reject<ObjectDisposedException>(() => world.CopyWeaponPoses([]));
                NavigationChecks.Reject<ObjectDisposedException>(() => world.CopyProjectilePoses([]));
            }
            finally { world.Dispose(); }
        });
        test("ECS: unchanged producer membership adds no steady tick allocations", () =>
        {
            using var world = new SimulationWorld([Factory("factory", default), new("unit", 100, build: new(1))], 106496, 14336);
            for (int i = 0; i < 1000; i++) world.AddInitialUnit(0, new((i % 100 + 2) * 1024, (i / 100 + 2) * 1024), 1);
            world.Step(Enumerable.Range(1, 1000).Select(i => new WorldCommand(1, 0, i, CommandKind.EnqueueTask, new((uint)i), Action: "make")).ToArray());
            for (int i = 0; i < 64; i++) world.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) world.Step();
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            NavigationChecks.Check(world.Count == 1000 && world.TaskCount == 1000 && allocation == 0, $"Stable producers allocated {allocation} bytes or unexpectedly spawned");
        });
        test("ECS: session releases its world before success or failure becomes observable", () =>
        {
            SimulationWorld? owned = null;
            SimulationWorld Create()
            {
                owned = new SimulationWorld([Factory("factory", new(4096, 0)), new("unit", 100, build: new(1))], 20000, 12000);
                owned.AddInitialUnit(0, new(4096, 4096), 1); return owned;
            }
            using (var session = new SimulationSession(Create, endTick: 1, realtime: false))
            {
                session.Completion.GetAwaiter().GetResult();
                NavigationChecks.Reject<ObjectDisposedException>(() => owned!.Report());
                using var frame = session.Frames.AcquireLatest()!;
                NavigationChecks.Check(frame.Poses.Length == 1, "Completed session lost its snapshot");
            }
            using (var session = new SimulationSession(Create, [new(1, 0, 1, CommandKind.Move, Position: new(-1, 0))], 1, realtime: false))
            {
                NavigationChecks.Reject<ArgumentOutOfRangeException>(() => session.Completion.GetAwaiter().GetResult());
                NavigationChecks.Reject<ObjectDisposedException>(() => owned!.Report());
            }
        });
    }
}
