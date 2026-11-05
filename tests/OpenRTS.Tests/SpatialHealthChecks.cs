using OpenRTS.Core;

internal static class SpatialHealthChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Spatial/health ECS: archetype removal and slot reuse preserve moving damaged actors and frozen frames", () =>
        {
            UnitDefinition[] types = [new("unit", 100, body: BodyShape.Circle(128), build: new(1)),
                new("shooter", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 25, 100, 0), abilities: [new("shoot", "main")]),
                new("factory", 300, movable: false, taskQueues: [new("q", exit: new(0, 2048), exitSearchRadius: 0)], products: [new("train", "unit", "q")])];
            using var world = new SimulationWorld(types, 65536, 24576, initialCapacity: 4);
            world.AddInitialUnit(1, new(4096, 1024), 2);
            world.AddInitialUnit(0, new(1024, 1024), 1);
            world.AddInitialUnit(0, new(2048, 1024), 1);
            world.AddInitialUnit(2, new(4096, 4096), 1);
            world.Step([new(1, 0, 1, CommandKind.Move, new(2), new(4096, 1024)),
                new(1, 0, 2, CommandKind.Move, new(3), new(2048, 4096)),
                new(1, 0, 3, CommandKind.UseAbility, new(1), TargetEntity: new(3), Action: "shoot")]);
            var frames = new FrameExchange(); frames.TryPublish(world); using var old = frames.AcquireLatest()!;
            var damaged = old.Poses.ToArray().Single(p => p.Id.Value == 3);
            NavigationChecks.Check(damaged.Health == 75 && damaged.Previous == new WorldPosition(2048, 1024) && damaged.Current == new WorldPosition(2048, 1152), "Initial ECS movement/damage failed");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(2)), new(2, 0, 2, CommandKind.Spawn, Position: new(6144, 1024), UnitType: 0, Owner: 1)]);
            var poses = NavigationChecks.Poses(world);
            var survivor = poses.Single(p => p.Id.Value == 3);
            NavigationChecks.Check(survivor.Health == 75 && survivor.Previous == damaged.Current && survivor.Current == new WorldPosition(2048, 1280), "Different legacy/ECS packed removals detached spatial or health state");
            NavigationChecks.Check(poses.Single(p => p.Id.Value == 4).Current == new WorldPosition(4096, 4096) && poses.Single(p => p.Id.Value == 5).Health == 100, "Slot reuse inherited a removed entity's state");
            NavigationChecks.Check(world.Events.ToArray().Single(e => e.Kind == WorldEventKind.Destroyed).Position == new WorldPosition(1152, 1024), "Destruction read spatial after deleting its component");
            world.Step([new(3, 0, 1, CommandKind.EnqueueTask, new(4), Action: "train")]);
            NavigationChecks.Check(NavigationChecks.Poses(world).Single(p => p.Id.Value == 6).Current == new WorldPosition(4096, 6144), "Production used a detached factory position");
            world.Step(Enumerable.Range(0, 80).Select(i => new WorldCommand(4, 0, i + 1, CommandKind.Spawn, Position: new(8192 + i * 512, 8192), UnitType: 0, Owner: 1)).ToArray());
            survivor = NavigationChecks.Poses(world).Single(p => p.Id.Value == 3);
            NavigationChecks.Check(survivor.Health == 75 && survivor.Current == new WorldPosition(2048, 1536), "Archetype capacity growth lost component state");
            world.Dispose();
            NavigationChecks.Check(old.Poses.ToArray().Single(p => p.Id.Value == 3) == damaged, "Native storage lifetime changed a pinned pose");
        });
        test("Spatial/health ECS: command spawns initialize previous position at the new tick", () =>
        {
            using var world = new SimulationWorld([new("u", 100)], 12000, 12000);
            world.Step([new(1, 0, 1, CommandKind.Spawn, Position: new(4096, 4096), Owner: 1), new(1, 0, 2, CommandKind.Move, new(1), new(8192, 4096))]);
            var pose = NavigationChecks.Poses(world).Single();
            NavigationChecks.Check(pose.Health == 100 && pose.Previous == new WorldPosition(4096, 4096) && pose.Current == new WorldPosition(4224, 4096), "Spawn component initialization or previous-position query used stale/default rows");
        });
    }
}
