using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class ConstructionWorkflowChecks
{
    private static readonly FootprintDefinition Stamp = new(2, 2,
        [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);
    private static UnitDefinition[] Types(bool auto = false) =>
    [
        new("engineer", 100, constructionRange: 1536, abilities: [new("assist", "", AutoCast: auto, Kind: AbilityKind.Build)],
            products: [new("build", "site", "", OnSite: true)]),
        new("site", 100, movable: false, footprint: Stamp, build: new(30, 50, ProductionKind.PlaceBuilding))
    ];
    private static EntityPose Pose(SimulationWorld w, EntityId id) => Poses(w).Single(p => p.Id == id);
    private static ConstructionProjectPose[] Projects(SimulationWorld w)
    { var projects = new ConstructionProjectPose[w.ConstructionProjectCount]; w.CopyConstructionProjects(projects); return projects; }
    private static long Credits(SimulationWorld w)
    { var players = new PlayerPose[w.PlayerCount]; w.CopyPlayers(players); return players.Single(p => p.Id == 1).Credits; }

    public static void Run(Action<string, Action> test)
    {
        test("Workflow: blueprint payment is atomic, refunded on cancellation and configurable", () =>
        {
            using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24), players: [new(1, "Blue", 70)]);
            var engineer = w.AddInitialUnit(0, new(2048, 2048), 1);
            w.AddInitialUnit(1, new(20480, 8192), 1);
            w.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, new(8192, 8192), Action: "build", Queued: true)]);
            var first = Projects(w).Single();
            NavigationChecks.Check(Credits(w) == 20 && first.UnpaidCost == 0, "The visible blueprint is not fully paid");
            w.Step([new(2, 0, 1, CommandKind.PlanConstruction, engineer, new(16384, 8192), Action: "build", Queued: true)]);
            NavigationChecks.Check(Projects(w).SequenceEqual([first]) && Credits(w) == 20 && Pose(w, engineer).Order.QueuedCount == 0,
                "Insufficient funds mutated the project or queue");
            w.Step([new(3, 0, 1, CommandKind.CancelConstruction, Owner: 1, TargetEntity: first.Id)]);
            NavigationChecks.Check(Credits(w) == 70 && Projects(w).Length == 0, "Cancelling a paid blueprint did not refund its price");
            w.Step([new(4, 0, 1, CommandKind.PlanConstruction, engineer, new(21504, 8192), Action: "build")]);
            NavigationChecks.Check(Credits(w) == 70 && Projects(w).Length == 0,
                "A footprint overlapping an existing structure was accepted and charged");
            using var legacy = SimulationWorld.CreateWithMechanics(new() { ConstructionPayment = ConstructionPaymentPolicy.Foundation },
                Types(), 32768, 24576, navigationGrid: new(32, 24), players: [new(1, "Blue", 70)]);
            var other = legacy.AddInitialUnit(0, new(2048, 2048), 1);
            legacy.Step([new(1, 0, 1, CommandKind.PlanConstruction, other, new(8192, 8192), Action: "build")]);
            NavigationChecks.Check(Credits(legacy) == 70 && Projects(legacy).Single().UnpaidCost == 50, "Foundation payment override was ignored");
        });

        test("Workflow: five queued sites survive another engineer clearing and assisting sites two and three", () =>
        {
            SimulationReport? reference = null;
            foreach (int workers in new[] { 1, 4 })
            {
                using var w = new SimulationWorld(Types(), 32768, 24576, speedPerTick: 256, navigationGrid: new(32, 24),
                    navigationWorkers: workers, reverseNavigationJobs: workers > 1, players: [new(1, "Blue", 1000)]);
                var first = w.AddInitialUnit(0, new(8192, 11264), 1);
                var second = w.AddInitialUnit(0, new(12288, 11264), 1);
                WorldPosition[] sites = [new(8192, 8192), new(8192, 11264), new(8192, 14336), new(11264, 14336), new(14336, 14336)];
                w.Step(sites.Select((p, i) => new WorldCommand(1, 0, i + 1, CommandKind.PlanConstruction, first, p,
                    Action: "build", Queued: true)).ToArray());
                var projects = Projects(w);
                var secondSite = projects.Single(p => p.Position == sites[1]).Id;
                var thirdSite = projects.Single(p => p.Position == sites[2]).Id;
                w.Step([new(2, 0, 1, CommandKind.BuildSite, second, TargetEntity: secondSite),
                    new(2, 0, 2, CommandKind.BuildSite, second, TargetEntity: thirdSite, Queued: true)]);
                bool assisted = false;
                for (int i = 0; i < 400; i++)
                {
                    w.Step();
                    assisted |= Pose(w, first).Order.Target == secondSite && Poses(w).Any(p => p.Id == secondSite && p.Construction.Active);
                }
                NavigationChecks.Check(assisted && Projects(w).Length == 0 && Poses(w).Count(p => p.UnitType == 1 && !p.Construction.Active) == 5 &&
                    Credits(w) == 750, "Clearance erased a queued site, failed to join ongoing work, or charged twice");
                var report = w.Report(); NavigationChecks.Check(reference is null || reference == report, "Cooperative construction differs by workers"); reference = report;
            }
        });

        test("Workflow: idle auto builders approach nearby projects outside their working radius", () =>
        {
            using var w = new SimulationWorld(Types(auto: true), 32768, 24576, speedPerTick: 256,
                navigationGrid: new(32, 24), players: [new(1, "Blue", 500)]);
            var first = w.AddInitialUnit(0, new(2048, 2048), 1);
            var helper = w.AddInitialUnit(0, new(8192, 14336), 1);
            w.Step([new(1, 0, 1, CommandKind.PlanConstruction, first, new(8192, 8192), Action: "build"),
                new(1, 0, 2, CommandKind.Stop, first)]);
            bool joined = false;
            for (int i = 0; i < 150; i++) { w.Step(); joined |= Pose(w, helper).Order.Kind == UnitActionKind.Build && Pose(w, helper).Order.Phase != OrderPhase.Idle; }
            NavigationChecks.Check(joined && Projects(w).Length == 0 && Poses(w).Any(p => p.UnitType == 1 && !p.Construction.Active),
                "An idle nearby engineer did not approach and complete the blueprint");
        });

        test("Workflow: the real airfield clears its center and builds with occupied cardinal exits", () =>
        {
            var catalog = GameManifest.Load("sandbox/game.yaml").LoadUnits();
            int engineerType = catalog.Units.ToList().FindIndex(t => t.Id == "construction-engineer");
            int airfield = catalog.Units.ToList().FindIndex(t => t.Id == "airfield");
            using var w = new SimulationWorld(catalog.Units, 65536, 65536, speedPerTick: 128,
                navigationGrid: new(64, 64), players: [new(1, "Blue", 10000)]);
            var center = new WorldPosition(32768, 32768);
            var engineer = w.AddInitialUnit(engineerType, center, 1);
            var footprint = catalog.Units[airfield].Footprint!;
            var obstacles = new List<EntityId>();
            foreach (var point in new WorldPosition[] { new(center.X - footprint.HalfWidth - 257, center.Y),
                new(center.X + footprint.HalfWidth + 257, center.Y), new(center.X, center.Y - footprint.HalfHeight - 257),
                new(center.X, center.Y + footprint.HalfHeight + 257) }) obstacles.Add(w.AddInitialUnit(engineerType, point, 1));
            var commands = obstacles.Select((id, i) => new WorldCommand(1, 0, i + 2, CommandKind.SetAutoCast, id, Action: "build", Enabled: false)).ToList();
            commands.Insert(0, new(1, 0, 1, CommandKind.PlanConstruction, engineer, center, Action: "site-airfield"));
            w.Step(commands.ToArray());
            var project = Projects(w).Single().Id;
            int starts = 0;
            for (int i = 0; i < 600; i++) { w.Step(); starts += w.Events.ToArray().Count(e => e.Entity == project && e.Kind == WorldEventKind.ConstructionStarted); }
            NavigationChecks.Check(starts == 1 && Projects(w).Length == 0 && !Pose(w, project).Construction.Active &&
                Pose(w, engineer).Current != center && Credits(w) == 8200, "The large airfield trapped its builder or never completed");
        });

        test("Workflow: a neutral mine blueprint persists without a builder and another engineer resumes it", () =>
        {
            var upgrade = new UpgradeDefinition("owned", 100, 30, support: true, @default: true, claimNeutral: true);
            UnitDefinition[] types = [new("mine", 100, states: ["owned"], movable: false, footprint: Stamp,
                destruction: DestructionPolicy.Neutralize, supply: new(SupplyRole.Mine, 1000), upgrades: [upgrade]), new("engineer", 100, constructionRange: 1536)];
            using var w = new SimulationWorld(types, 32768, 24576, speedPerTick: 256,
                navigationGrid: new(32, 24), players: [new(1, "Blue", 500), new(2, "Red", 500)]);
            w.RestrictCommandsToPlayers();
            var mine = w.AddInitialUnit(0, new(12288, 12288));
            var first = w.AddInitialUnit(1, new(10240, 12288), 1);
            var second = w.AddInitialUnit(1, new(12288, 10240), 1);
            w.Step([new(1, 1, 1, CommandKind.StartUpgrade, mine, TargetEntity: mine, Owner: 1)]);
            for (int i = 0; i < 8; i++) w.Step();
            NavigationChecks.Check(Pose(w, mine).Upgrade is { Project: "owned", Work: 0, Payer: 1 } && Credits(w) == 400,
                "A neutral blueprint required a ready engineer or charged more than once");
            w.Step([new(w.Tick + 1, 1, 1, CommandKind.BuildSite, first, TargetEntity: mine)]);
            for (int i = 0; i < 5; i++) w.Step();
            int work = Pose(w, mine).Upgrade.Work;
            NavigationChecks.Check(work > 0, "An engineer could not start the mine blueprint");
            w.Step([new(w.Tick + 1, 1, 1, CommandKind.Stop, first)]);
            for (int i = 0; i < 5; i++) w.Step();
            NavigationChecks.Check(Pose(w, mine).Upgrade.Work == work && Credits(w) == 400, "Stopping cancelled, refunded, or advanced the project");
            w.Step([new(w.Tick + 1, 1, 1, CommandKind.StartUpgrade, second, TargetEntity: mine)]);
            for (int i = 0; i < 50; i++) w.Step();
            NavigationChecks.Check(Pose(w, mine).Upgrade.Completed == "owned" && Pose(w, mine).Presentation.Owner == 1 && Credits(w) == 400,
                "The replacement engineer could not continue paid upgrade work");
        });

        test("Workflow: completed barracks clear neutral exit blockers and never park recruits inside", () =>
        {
            var catalog = GameManifest.Load("sandbox/game.yaml").LoadUnits();
            int barracks = catalog.Units.ToList().FindIndex(t => t.Id == "barracks");
            int rifle = catalog.Units.ToList().FindIndex(t => t.Id == "rifleman");
            using var w = new SimulationWorld(catalog.Units, 32768, 24576, speedPerTick: 256,
                navigationGrid: new(32, 24), players: [new(1, "Blue", 10000)]);
            var center = new WorldPosition(12288, 12800);
            var engineer = w.AddInitialUnit(catalog.Units.ToList().FindIndex(t => t.Id == "construction-engineer"), new(4096, 12800), 1);
            w.AddInitialUnit(catalog.Units.ToList().FindIndex(t => t.Id == "power-plant"), new(25088, 8192), 1);
            var blocker = w.AddInitialUnit(rifle, new(12288, 14848));
            w.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, center, Action: "site-barracks")]);
            var producer = Projects(w).Single().Id;
            for (int i = 0; i < 350; i++) w.Step();
            NavigationChecks.Check(!Pose(w, producer).Construction.Active && Pose(w, blocker).Current != new WorldPosition(12288, 14848),
                "Completing the building did not clear its neutral exit blocker");
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.EnqueueTask, producer, Action: "rifleman", Amount: 12)]);
            for (int i = 0; i < 1400; i++) w.Step();
            var infantry = Poses(w).Where(p => p.UnitType == rifle).ToArray();
            NavigationChecks.Check(w.TaskCount == 0 && infantry.Length == 13 && Pose(w, blocker).Current != new WorldPosition(12288, 14848),
                "Ready recruits did not clear an occupied exit");
            var footprint = catalog.Units[barracks].Footprint!;
            foreach (var p in infantry)
            {
                var body = catalog.Units[rifle].Body;
                NavigationChecks.Check(footprint.Cells.All(cell => footprint.Bounds(cell, center).DistanceSquared(p.Current) > (long)body.HalfWidth * body.HalfWidth),
                    "A recruit remained parked inside the production footprint");
            }
        });

        test("Workflow: neutral upgrade buttons have resource data and an idle engineer automatically completes the paid project", () =>
        {
            UnitDefinition[] types = [new("mine", 100, states: ["owned"], movable: false, footprint: Stamp,
                destruction: DestructionPolicy.Neutralize, supply: new(SupplyRole.Mine, 1000), sensors: new(),
                upgrades: [new("owned", 100, 6, support: true, @default: true, claimNeutral: true)]),
                new("engineer", 100, constructionRange: 1536, abilities: [new("assist", "", AutoCast: true, Kind: AbilityKind.Build)])];
            using var w = new SimulationWorld(types, 32768, 24576, speedPerTick: 256,
                navigationGrid: new(32, 24), players: [new(1, "Blue", 500), new(2, "Red", 500)]);
            w.RestrictCommandsToPlayers();
            var mine = w.AddInitialUnit(0, new(12288, 12288));
            var engineer = w.AddInitialUnit(1, new(8192, 12288), 1);
            var frames = new FrameExchange(); frames.TryPublish(w);
            using (var visible = frames.AcquireLatest(1)!)
                NavigationChecks.Check(visible.Poses.ToArray().Single(p => p.Id == mine).Supply.Amount == 1000,
                    "A visible neutral mine looked empty to its upgrade button");
            w.Step([new(1, 1, 1, CommandKind.StartUpgrade, mine, TargetEntity: mine, Owner: 1)]);
            frames.TryPublish(w);
            using (var own = frames.AcquireLatest(1)!)
            using (var enemy = frames.AcquireLatest(2)!)
                NavigationChecks.Check(own.Poses.ToArray().Single(p => p.Id == mine).Upgrade.Project == "owned" &&
                    enemy.Poses.ToArray().Single(p => p.Id == mine).Upgrade.Project is null,
                    "The payer lost its neutral project or another player saw the blueprint");
            for (int i = 0; i < 120; i++) w.Step();
            NavigationChecks.Check(Pose(w, mine).Upgrade.Completed == "owned" && Credits(w) == 400 &&
                Pose(w, engineer).Current != new WorldPosition(8192, 12288), "The idle engineer did not automatically approach its upgrade project");
        });

        test("Workflow: a manual ability cannot leave a unit parked on a no-stop exit lane", () =>
        {
            var lane = new FootprintDefinition(2, 2, [new(0, 0, new(true, false), false), new(1, 0, new(true, false), false),
                new(0, 1, new(true, false), false), new(1, 1, new(true, false), false)]);
            var gun = new WeaponDefinition(2048, 2048, 1, 100, 0, projectile: ProjectileKind.Grenade, splashRadius: 256);
            UnitDefinition[] types = [new("lane", 100, movable: false, body: BodyShape.None, footprint: lane),
                new("soldier", 100, ["idle", "move", "fire"], weapon: gun,
                    abilities: [new("grenade", "main", GroundTarget: true)])];
            using var w = new SimulationWorld(types, 16384, 16384, speedPerTick: 256, navigationGrid: new(16, 16));
            w.AddInitialUnit(0, new(8192, 8192));
            var soldier = w.AddInitialUnit(1, new(4096, 8192), 1);
            w.Step([new(1, 0, 1, CommandKind.Move, soldier, new(12288, 8192))]);
            for (int i = 0; i < 12; i++) w.Step();
            var resolver = new DestinationResolver(w.NavigationGrid!, types[1].Locomotor);
            var at = Pose(w, soldier).Current;
            NavigationChecks.Check(!resolver.CanStop(at), "The fixture did not put the moving soldier in its exit lane");
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.UseAbility, soldier, at with { X = at.X + 512 }, Action: "grenade")]);
            NavigationChecks.Check(w.MovingCount == 1 && resolver.CanStop(Pose(w, soldier).Order.Destination),
                "Ability completion left the soldier parked in a no-stop lane");
            for (int i = 0; i < 40; i++) w.Step();
            NavigationChecks.Check(resolver.CanStop(Pose(w, soldier).Current), "The soldier failed to reach a permitted stopping area");
        });
    }
}
