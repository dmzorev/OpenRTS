using OpenRTS.Core;

internal static class ConstructionChecks
{
    private static readonly WorldPosition Site = new(8192, 8192);
    private static readonly FootprintDefinition Stamp = new(2, 2,
        [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);

    private static UnitDefinition[] Types() =>
    [
        new("engineer", 100, constructionRange: 1536, products:
            [new("power", "plant", "", OnSite: true), new("other", "other", "", OnSite: true)]),
        new("plant", 100, ["idle", "fire"], ["lowpower", "constructing"], movable: false, footprint: Stamp,
            weapon: new(3072, 3072, 10, 1000, 0), economy: new(100, 5, 2),
            build: new(6, 50, ProductionKind.PlaceBuilding, initialHealthPercent: 30)),
        new("yard", 100, movable: false, constructionRange: 8192,
            products: [new("power", "plant", "", OnSite: true)]),
        new("enemy", 100, ["idle", "move", "fire"], weapon: new(3072, 3072, 7, 1000, 0)),
        new("other", 100, movable: false, footprint: Stamp, build: new(4, 25, ProductionKind.PlaceBuilding))
    ];

    private static SimulationWorld World(int workers = 1) =>
        new(Types(), 24 * 1024, 16 * 1024, speedPerTick: 256, navigationGrid: new(24, 16),
            navigationWorkers: workers, reverseNavigationJobs: workers > 1, players: [new(1, "Blue", 500), new(2, "Red", 500)]);

    private static EntityPose Pose(SimulationWorld world, EntityId id)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses.Single(p => p.Id == id);
    }

    private static ConstructionProjectPose[] Projects(SimulationWorld world)
    {
        var poses = new ConstructionProjectPose[world.ConstructionProjectCount];
        world.CopyConstructionProjects(poses);
        return poses;
    }

    private static PlayerPose Player(SimulationWorld world, uint owner)
    {
        var poses = new PlayerPose[world.PlayerCount];
        world.CopyPlayers(poses);
        return poses.Single(p => p.Id == owner);
    }

    public static void Run(Action<string, Action> test)
    {
        test("Construction: readiness controls weapons, production and boarding independently of visual states", () =>
        {
            var factory = new UnitDefinition("factory", 100, ["idle", "fire"], ["constructing", "lowpower"], movable: false,
                footprint: Stamp, weapon: new(3072, 3072, 1, 1000, 0), economy: new(100, 5, 2),
                taskQueues: [new("train")], products: [new("train", "worker", "train")],
                transport: new(["worker"], 1), build: new(2, kind: ProductionKind.PlaceBuilding));
            using var world = new SimulationWorld([
                new("yard", 100, movable: false, constructionRange: 8192, products: [new("build", "factory", "", OnSite: true)]),
                factory, new("worker", 100, passenger: new("worker"), constructionRange: 1536, build: new(50)), new("enemy", 100)],
                24 * 1024, 16 * 1024, navigationGrid: new(24, 16), players: [new(1, "Blue", 500)]);
            var yard = world.AddInitialUnit(0, new(4096, 8192), 1);
            var worker = world.AddInitialUnit(2, new(6656, 8192), 1);
            var enemy = world.AddInitialUnit(3, new(11264, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "build")]);
            var site = new EntityId(4);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var pinned = frames.AcquireLatest()!;
            NavigationChecks.Check(world.TryGetConditions(site, out var conditions) && conditions == GameplayConditions.Constructing,
                "The foundation did not expose its readiness condition");

            world.Step([
                new(2, 0, 1, CommandKind.SetVisualStates, site, States: 0),
                new(2, 0, 2, CommandKind.EnqueueTask, site, Action: "train"),
                new(2, 0, 3, CommandKind.Enter, worker, TargetEntity: site),
                new(2, 0, 4, CommandKind.Destroy, yard)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 0 && Pose(world, worker).Carrier.Value == 0 &&
                Pose(world, enemy).Health == 100 && world.TaskCount == 0 && Player(world, 1).Energy == 0,
                "An unfinished foundation became active or continued without its builder");
            NavigationChecks.Check(world.TryGetConditions(site, out conditions) && conditions.HasFlag(GameplayConditions.Constructing),
                "A visual command cleared a gameplay condition");
            world.Step([new(3, 0, 1, CommandKind.BuildSite, worker, TargetEntity: site)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 2, "A replacement builder could not resume after packed removal");
            world.Step([new(4, 0, 1, CommandKind.SetVisualStates, site, States: factory.StateMask("constructing"))]);
            NavigationChecks.Check(world.TryGetConditions(site, out conditions) && conditions == GameplayConditions.None &&
                !Pose(world, site).Construction.Active && Pose(world, enemy).Health == 100 && Player(world, 1).Energy == 100,
                "Completion did not update readiness and power after the weapon phase");
            world.Step([new(5, 0, 1, CommandKind.Enter, worker, TargetEntity: site),
                new(5, 0, 2, CommandKind.EnqueueTask, site, Action: "train"),
                new(5, 0, 3, CommandKind.AttackTarget, site, TargetEntity: enemy)]);
            NavigationChecks.Check(Pose(world, worker).Carrier == site && world.TaskCount == 1 && Pose(world, enemy).Health < 100 &&
                world.TryGetConditions(site, out conditions) && conditions == GameplayConditions.None,
                "The completed factory did not enable consumers independently of its visual state");
            NavigationChecks.Check(pinned.Poses.ToArray().Single(p => p.Id == site).Construction.Active,
                "Completion changed the pinned foundation frame");
        });

        test("Construction: project overlap and cancellation are owner-scoped and leave navigation untouched", () =>
        {
            using var world = World();
            var blue = world.AddInitialUnit(0, new(2048, 2048), 1);
            var red = world.AddInitialUnit(0, new(16384, 12288), 2);
            var grid = world.NavigationGrid;
            world.Step([
                new(1, 0, 1, CommandKind.PlanConstruction, blue, Site, Action: "power"),
                new(1, 0, 2, CommandKind.PlanConstruction, red, Site, Action: "power")]);
            var original = Projects(world);
            NavigationChecks.Check(original.Length == 2 && world.Count == 2 && ReferenceEquals(grid, world.NavigationGrid), "Ghosts occupied cells or removed an enemy plan");
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var pinned = frames.AcquireLatest()!;
            world.Step([
                new(2, 0, 1, CommandKind.CancelConstruction, Owner: 2, TargetEntity: original[0].Id),
                new(2, 0, 2, CommandKind.PlanConstruction, blue, Site, Action: "other", Queued: true),
                new(2, 0, 3, CommandKind.Stop, blue)]);
            var plans = Projects(world);
            NavigationChecks.Check(plans.Length == 2 && plans[0].Id != original[0].Id && plans[0].UnitType == 4 && plans[1] == original[1], "Replacement or owner validation failed");
            NavigationChecks.Check(pinned.ConstructionProjects.ToArray().SequenceEqual(original), "Pinned project frame changed");
            world.Step([new(3, 0, 1, CommandKind.CancelConstruction, Owner: 1, TargetEntity: plans[0].Id)]);
            NavigationChecks.Check(Projects(world).SequenceEqual([original[1]]) && Player(world, 1).Credits == 500, "Cancelling an unstarted project spent money");
        });

        test("Construction: a foundation starts at configured health, pauses, retains damage and accepts two builders", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            var engineer = world.AddInitialUnit(0, new(8192, 6144), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            var id = new EntityId(3);
            var start = Pose(world, id);
            NavigationChecks.Check(start.Health == 30 && start.Construction == new ConstructionProgress(0, 12) && Projects(world).Length == 0,
                "Initial foundation health or progress is wrong");
            NavigationChecks.Check(Player(world, 1).Credits == 450 && Player(world, 1).Energy == 0 && world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(Site)),
                "Starting work did not charge once, stamp the site, or leave power disabled");

            var enemy = new EntityId(4);
            world.Step([
                new(2, 0, 1, CommandKind.Stop, yard),
                new(2, 0, 2, CommandKind.Spawn, Position: new(10240, 8192), UnitType: 3, Owner: 2),
                new(2, 0, 3, CommandKind.AttackTarget, enemy, TargetEntity: id)]);
            NavigationChecks.Check(Pose(world, id).Health == 23 && Pose(world, id).Construction.Work == 0 && Pose(world, enemy).Health == 100,
                "Paused foundation fired, progressed, or did not take damage");
            world.Step([new(3, 0, 1, CommandKind.BuildSite, TargetEntity: id, Recipients: new([yard, engineer]))]);
            NavigationChecks.Check(Pose(world, id).Construction.Work == 4, "Builders did not combine their work");
            world.Step();
            world.Step();
            var done = Pose(world, id);
            NavigationChecks.Check(!done.Construction.Active && done.Health == 93 && Player(world, 1).Energy == 100 && Player(world, 1).Credits == 450,
                "Completion lost combat damage or activated economy early");
            world.Step();
            world.Step();
            NavigationChecks.Check(Player(world, 1).Credits == 455, "Income timer did not start after completion");
        });

        test("Construction: idle allies help automatically and manual orders take priority", () =>
        {
            AbilityDefinition build = new("build", "", AutoCast: true, Kind: AbilityKind.Build);
            UnitDefinition[] types =
            [
                new("starter", 100, movable: false, constructionRange: 8192,
                    products: [new("power", "plant", "", OnSite: true)]),
                new("yard", 100, movable: false, constructionRange: 8192, abilities: [build]),
                new("engineer", 100, constructionRange: 1536, abilities: [build]),
                new("plant", 100, movable: false, footprint: Stamp, build: new(10, 0, ProductionKind.PlaceBuilding))
            ];
            using var world = new SimulationWorld(types, 24000, 16000);
            var starter = world.AddInitialUnit(0, new(4096, 4096), 1);
            var yard = world.AddInitialUnit(1, new(4096, 6144), 1);
            var engineer = world.AddInitialUnit(2, new(6400, 8192), 1);
            world.AddInitialUnit(2, new(9984, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, starter, Site, Action: "power")]);
            var site = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.ConstructionStarted).Entity;
            world.Step();
            NavigationChecks.Check(Pose(world, site).Construction.Work == 6, "Both idle allies should contribute and the enemy should not");
            world.Step([
                new(3, 0, 1, CommandKind.HoldPosition, engineer),
                new(3, 0, 2, CommandKind.SetAutoCast, yard, Action: "build", Enabled: false)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 8 && !Pose(world, yard).AutoBuild,
                "Hold or disabled autocast did not suspend construction help");
            world.Step([new(4, 0, 1, CommandKind.UseAbility, yard, TargetEntity: site, Action: "build")]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 12, "Manual construction did not work with autocast off");
        });

        test("Construction: multiple queued sites finish in order with worker parity", () =>
        {
            using var sequential = World();
            using var parallel = World(4);
            foreach (var world in new[] { sequential, parallel })
            {
                world.AddInitialUnit(0, new(2048, 4096), 1);
                world.AddInitialUnit(0, new(2048, 5120), 1);
            }
            var group = new EntitySet([new(1), new(2)]);
            WorldCommand[] journal =
            [
                new(1, 0, 1, CommandKind.PlanConstruction, Position: Site, Recipients: group, Action: "power", Queued: true),
                new(1, 0, 2, CommandKind.PlanConstruction, Position: new(12288, 8192), Recipients: group, Action: "power", Queued: true),
                new(1, 0, 3, CommandKind.PlanConstruction, Position: new(16384, 8192), Recipients: group, Action: "other", Queued: true)
            ];
            var completed = new List<uint>();
            for (int tick = 1; tick <= 200; tick++)
            {
                sequential.Step(tick == 1 ? journal : []);
                parallel.Step(tick == 1 ? journal : []);
                NavigationChecks.Check(sequential.Events.SequenceEqual(parallel.Events), "Construction event order differs by worker scheduling");
                foreach (var e in sequential.Events) if (e.Kind == WorldEventKind.ConstructionCompleted) completed.Add(e.Entity.Value);
            }
            NavigationChecks.Check(completed.SequenceEqual(new uint[] { 3, 4, 5 }) && Projects(sequential).Length == 0,
                $"Shift queue did not complete in order: {string.Join(", ", completed)}");
            NavigationChecks.Check(sequential.Report() == parallel.Report(), "Construction differs by navigation workers");
        });

        test("Construction: Shift between orders appends behind earlier yard placements", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            world.Step([
                new(1, 0, 1, CommandKind.PlanConstruction, yard, new(7168, 8192), Queued: true, Action: "power"),
                new(1, 0, 2, CommandKind.PlanConstruction, yard, new(10240, 8192), Queued: true, Action: "power")]);
            var completed = new List<uint>();
            for (int tick = 2; tick <= 30; tick++)
            {
                world.Step(tick == 9
                    ? [new(9, 0, 1, CommandKind.PlanConstruction, yard, new(4096, 12288), Queued: true, Action: "power")]
                    : []);
                foreach (var e in world.Events)
                    if (e.Kind == WorldEventKind.ConstructionCompleted) completed.Add(e.Entity.Value);
            }
            NavigationChecks.Check(completed.SequenceEqual(new uint[] { 2, 3, 4 }),
                "The new Shift placement overtook an earlier pending yard order");
        });

        test("Construction: friendly and neutral units evacuate, but enemy or immobile obstacles cancel", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            var neutral = world.AddInitialUnit(0, Site);
            var ally = world.AddInitialUnit(0, new(8704, 8704), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            NavigationChecks.Check(Projects(world).Single().Clearing && world.Count == 3 && !world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(Site)),
                "Foundation trapped the occupants during clearance");
            for (int i = 0; i < 20; i++) world.Step();
            NavigationChecks.Check(world.Count == 4 && Projects(world).Length == 0 && Pose(world, neutral).Current != Site && Pose(world, ally).Current != new WorldPosition(8704, 8704),
                "Friendly/neutral evacuation did not clear the footprint");

            foreach (int blocker in new[] { 0, 4 })
            {
                using var blocked = World();
                blocked.AddInitialUnit(2, new(4096, 8192), 1);
                blocked.AddInitialUnit(blocker, Site, blocker == 0 ? 2u : 1u);
                blocked.Step([new(1, 0, 1, CommandKind.PlanConstruction, new(1), Site, Action: "power")]);
                NavigationChecks.Check(blocked.Count == 2 && Projects(blocked).Length == 0 && Player(blocked, 1).Credits == 500 &&
                    blocked.Events.ToArray().Any(e => e.Kind == WorldEventKind.ConstructionCancelled),
                    "Blocked foundation did not cancel cleanly");
            }
        });

        test("Construction: fixed builders cannot plan outside their radius", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(2048, 2048), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            NavigationChecks.Check(Projects(world).Length == 0 && world.Count == 1 && Player(world, 1).Credits == 500, "Out-of-range yard accepted a project");
        });
    }
}
