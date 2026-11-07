using OpenRTS.Core;
using OpenRTS.Core.Production;
using OpenRTS.Content;
using DeclarativeContent;
using static NavigationChecks;

internal static class PlayerChecks
{
    private static PlayerPose Player(SimulationWorld w, uint id) { var p = new PlayerPose[w.PlayerCount]; w.CopyPlayers(p); return p.Single(p => p.Id == id); }
    private static UnitDefinition Building(string id, int energy = 0, int income = 0, int interval = 2, CapturableDefinition? capture = null, DestructionPolicy destruction = DestructionPolicy.Destroy, int hp = 100)
        => new(id, hp, ["idle", "fire"], ["lowpower"], movable: false, economy: new(energy, income, interval), capturable: capture, destruction: destruction);
    public static void Run(Action<string, Action> test)
    {
        test("Players: neutral, power, income ownership and frozen resource frames", () =>
        {
            using var w = new SimulationWorld([Building("plant", 100), Building("consumer", -120), Building("deposit", income: 7)], 20000, 16000,
                players: [new(1, "blue", 100), new(2, "red", 90)]);
            w.AddInitialUnit(0, new(1024, 1024), 1); w.AddInitialUnit(1, new(4096, 1024), 1);
            w.AddInitialUnit(2, new(8192, 1024), 2); w.AddInitialUnit(2, new(12288, 1024));
            NavigationChecks.Check(Player(w, 0).Credits == 0 && Player(w, 1).Energy == -20, "Players/power not initialized");
            w.Step(); var exchange = new FrameExchange(); exchange.TryPublish(w); using var frozen = exchange.AcquireLatest()!;
            w.Step(); NavigationChecks.Check(Player(w, 2).Credits == 97 && Player(w, 0).Credits == 0 && frozen.Players.ToArray().Single(p => p.Id == 2).Credits == 90, "Income or resource snapshot ownership failed");
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Presentation.States == 1, "Building has no lowpower state");
            w.Step([new(3, 0, 1, CommandKind.Destroy, new(2))]);
            NavigationChecks.Check(Player(w, 1).Energy == 100 && !Player(w, 1).LowPower, "Removed building still consumes energy");
        });
        foreach (var policy in Enum.GetValues<PaymentPolicy>())
        test($"Players: {policy} charges production once, pauses without funds and refunds cancellation", () =>
        {
            UnitDefinition[] types = [new("unit", 100, build: new(4, 10)), new("factory", 100, movable: false,
                taskQueues: [new("q", payment: policy, exit: new(0, 2048))], products: [new("make", "unit", "q")])];
            using var w = new SimulationWorld(types, 20000, 16000, players: [new(1, "blue", 10)]);
            w.AddInitialUnit(1, new(4096, 4096), 1);
            w.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]);
            w.Step(); w.Step(); w.Step();
            NavigationChecks.Check(w.Count == 2 && Player(w, 1).Credits == 0, "Production payment/output mismatch");
            w.Step([new(5, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]);
            for (int i = 0; i < 8; i++) w.Step();
            NavigationChecks.Check(w.Count == 2 && Player(w, 1).Credits == 0, "Production bypassed credit shortage");
            if (policy == PaymentPolicy.Upfront) NavigationChecks.Check(w.TaskCount == 0, "Unaffordable upfront order consumed queue space");
            using var cancel = new SimulationWorld(types, 20000, 16000, players: [new(1, "blue", 10)]);
            cancel.AddInitialUnit(1, new(4096, 4096), 1);
            cancel.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]);
            cancel.Step([new(2, 0, 1, CommandKind.CancelTasks, new(1), Action: "make")]);
            NavigationChecks.Check(cancel.TaskCount == 0 && Player(cancel, 1).Credits == (policy == PaymentPolicy.OnCompletion ? 10 : 8), "Cancellation did not retain the consumed quarter of cost");
        });
        test("Players: lowpower halves production and disables weapons until supply returns", () =>
        {
            var gun = new WeaponDefinition(8192, 8192, 10, 2, 0);
            UnitDefinition[] types = [new("unit", 100, build: new(2)), new("factory", 100, ["idle", "fire"], ["lowpower"], weapon: gun, movable: false,
                taskQueues: [new("q", exit: new(0, 2048))], products: [new("make", "unit", "q")], economy: new(-10)), Building("plant", 20)];
            using var w = new SimulationWorld(types, 20000, 16000); w.AddInitialUnit(1, new(4096, 4096), 1); w.AddInitialUnit(0, new(6144, 4096), 2);
            w.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]); w.Step();
            NavigationChecks.Check(w.Count == 2 && Poses(w).Single(p => p.Id.Value == 2).Health == 100, "Lowpower did not pause weapons or slow production");
            w.Step(); w.Step(); NavigationChecks.Check(w.Count == 3, "Half-speed production did not finish on fourth tick");
            w.Step([new(5, 0, 1, CommandKind.Spawn, Position: new(10000, 10000), UnitType: 2, Owner: 1)]);
            NavigationChecks.Check(!Player(w, 1).LowPower && Poses(w).Single(p => p.Id.Value == 2).Health < 100, "Power recovery did not restore weapons");
        });
        foreach (var policy in Enum.GetValues<PaymentPolicy>())
        test($"Players: registered production rate pauses {policy} work and resumes without charging twice", () =>
        {
            int rateSelections = 0;
            var rule = new ProductionRateRule("test.pause-lowpower", 1, lowPower =>
            {
                rateSelections++;
                return lowPower ? ProductionRate.Paused : ProductionRate.Normal;
            });
            UnitDefinition[] types = [new("unit", 100, build: new(4, 20)), new("factory", 100, ["idle"], ["lowpower"], movable: false,
                taskQueues: [new("q", payment: policy, exit: new(0, 2048))], products: [new("make", "unit", "q")], economy: new(-10)), Building("plant", 20)];
            using var a = World(1, rule);
            using var b = World(4, rule);
            using var builtIn = World(1, null);
            var halfSpeed = new ProductionRateRule(rule.Id, rule.Version, lowPower => lowPower ? ProductionRate.Half : ProductionRate.Normal);
            using var differentRule = World(1, halfSpeed);
            NavigationChecks.Check(a.Report() != builtIn.Report() && a.Report() != differentRule.Report(), "Report omits rule configuration");

            int remaining = policy switch { PaymentPolicy.Upfront => 20, PaymentPolicy.Progressive => 35, _ => 40 };
            uint visual = types[1].StateMask("lowpower");
            Advance([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "make")]);
            NavigationChecks.Check(Task(a).Progress == 1 && Player(a, 1).Credits == remaining, "Wrong progress before power loss");
            Advance([new(2, 0, 1, CommandKind.Destroy, new(2))]);
            Advance([new(3, 0, 1, CommandKind.SetVisualStates, new(1))]);
            Advance();
            NavigationChecks.Check(Task(a).Progress == 1 && Player(a, 1).Credits == remaining, "Power loss changed paid progress");
            NavigationChecks.Check(a.TryGetConditions(new(1), out var facts) && facts == GameplayConditions.LowPower &&
                !a.TryGetConditions(new(2), out _) && Poses(a).Single().Presentation.States == visual, "Low power fact or projection is stale");

            Advance([new(5, 0, 1, CommandKind.Spawn, Position: new(8192, 8192), UnitType: 2, Owner: 1),
                new(5, 0, 2, CommandKind.SetVisualStates, new(1), States: visual)]);
            NavigationChecks.Check(Task(a).Progress == 2 && a.TryGetConditions(new(1), out facts) && facts == GameplayConditions.None,
                "Visual state affected production or power recovery was deferred");
            Advance();
            NavigationChecks.Check(Task(a).Progress == 3, "Production resumed at the wrong rate");
            Advance();
            NavigationChecks.Check(a.TaskCount == 0 && a.Count == 3 && Player(a, 1).Credits == 20 &&
                a.Events.ToArray().Count(e => e.Kind == WorldEventKind.Produced) == 1, "Queue did not pay and release exactly once");
            NavigationChecks.Check(rateSelections == 2, "Production executed the registration callback during a tick");

            SimulationWorld World(int workers, ProductionRateRule? rate)
            {
                var world = new SimulationWorld(rate, types, 16384, 16384, navigationGrid: new(16, 16), navigationWorkers: workers,
                    players: [new(1, "blue", 40)]);
                world.AddInitialUnit(1, new(4096, 4096), 1);
                world.AddInitialUnit(2, new(8192, 8192), 1);
                return world;
            }
            void Advance(WorldCommand[]? commands = null)
            {
                a.Step(commands);
                b.Step(commands);
                NavigationChecks.Check(a.Report() == b.Report() && a.Events.SequenceEqual(b.Events), "Custom rule differs by worker count");
            }
            static TaskPose Task(SimulationWorld world)
            {
                var tasks = new TaskPose[world.TaskCount];
                world.CopyTaskPoses(tasks);
                return tasks.Single();
            }
        });
        test("Players: default capture time, neutralization, recapture and income timers", () =>
        {
            UnitDefinition[] types = [new("engineer", 100, abilities: [new("capture", "", Kind: AbilityKind.Capture, Range: 2048)]),
                Building("deposit", income: 9, capture: new(), destruction: DestructionPolicy.Neutralize, hp: 3),
                new("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 10, 1000, 0))];
            using var w = new SimulationWorld(types, 20000, 16000); w.AddInitialUnit(0, new(1024, 1024), 1); w.AddInitialUnit(1, new(2048, 1024));
            w.Step([new(1, 0, 1, CommandKind.UseAbility, new(1), TargetEntity: new(2), Action: "capture")]); w.Step();
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Presentation.Owner == 0 && Poses(w).Single(p => p.Id.Value == 1).Order.CaptureProgress == 2, "Capture completed too early or no progress published");
            w.Step(); NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Presentation.Owner == 1, "MaxHP default capture duration failed");
            w.Step(); w.Step(); NavigationChecks.Check(Player(w, 1).Credits == 9, "Captured deposit did not reset and pay income timer");
            w.Step([new(6, 0, 1, CommandKind.Spawn, Position: new(6144, 1024), UnitType: 2, Owner: 2), new(6, 0, 2, CommandKind.AttackTarget, new(3), TargetEntity: new(2))]);
            var deposit = Poses(w).Single(p => p.Id.Value == 2);
            NavigationChecks.Check(deposit.Health == 1 && deposit.Presentation.Owner == 0 && !w.Events.ToArray().Any(e => e.Kind == WorldEventKind.Destroyed && e.Entity.Value == 2), "Lethal hit removed a neutralizable object");
            w.Step([new(7, 0, 1, CommandKind.UseAbility, new(1), TargetEntity: new(2), Action: "capture")]); w.Step(); w.Step();
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Presentation.Owner == 1 && Poses(w).Single(p => p.Id.Value == 2).Health == 1, "Recapture healed or lost the object");
        });
        test("Players: invulnerable props reject direct attacks and splash damage", () =>
        {
            using var w = new SimulationWorld([new("prop", 100, destruction: DestructionPolicy.Invulnerable),
                new("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 100, 1000, 0, projectile: ProjectileKind.Grenade, projectileSpeed: 4096, splashRadius: 2048), abilities: [new("grenade", "main", true, AutoCast: true)]), new("target", 200)], 20000, 16000);
            w.AddInitialUnit(1, new(1024, 1024), 1); w.AddInitialUnit(0, new(4096, 1024), 2); w.AddInitialUnit(2, new(4096, 2048), 2);
            w.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(w.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder), "Unattackable prop accepted attack");
            w.Step([new(2, 0, 1, CommandKind.UseAbility, new(1), Position: new(4096, 2048), Action: "grenade")]); w.Step(); w.Step();
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Health == 100 && Poses(w).Single(p => p.Id.Value == 3).Health < 200, "Invulnerable prop took splash damage or fixture missed");
        });
        test("Players: capture cancels old queues, refunds their payer and transfers power atomically", () =>
        {
            UnitDefinition[] types = [new("engineer", 100, abilities: [new("capture", "", Kind: AbilityKind.Capture, Range: 4096)]),
                new("factory", 100, ["idle"], ["lowpower"], movable: false, taskQueues: [new("q")], products: [new("make", "unit", "q")],
                    economy: new(-20), capturable: new(1)), new("unit", 100, build: new(10, 20)), Building("plant", 100)];
            using var w = new SimulationWorld(types, 20000, 16000, players: [new(1, "blue"), new(2, "red", 100)]);
            w.AddInitialUnit(0, new(1024, 1024), 1); w.AddInitialUnit(1, new(3072, 1024), 2); w.AddInitialUnit(3, new(6144, 6144), 2);
            w.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(2), Action: "make"), new(1, 0, 2, CommandKind.SetRally, new(2), Position: new(8192, 8192))]);
            w.Step([new(2, 0, 1, CommandKind.UseAbility, new(1), TargetEntity: new(2), Action: "capture")]);
            var factory = Poses(w).Single(p => p.Id.Value == 2);
            NavigationChecks.Check(factory.Presentation.Owner == 1 && !factory.HasRally && w.TaskCount == 0 && Player(w, 2).Credits == 98, "Old tasks/rally/payment survived capture");
            NavigationChecks.Check(Player(w, 1).Energy == -20 && Player(w, 2).Energy == 100 && factory.Presentation.States == 1, "Power did not transfer with the building");
        });
        test("Players: neutralized footprints remain solid and captured ruins can be repaired", () =>
        {
            var footprint = new FootprintDefinition(2, 2, [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);
            UnitDefinition[] types = [new("engineer", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 10, 2, 0, effect: WeaponEffect.Repair),
                    abilities: [new("capture", "", Kind: AbilityKind.Capture), new("repair", "main")]),
                new("deposit", 3, ["idle"], movable: false, footprint: footprint, capturable: new(1), destruction: DestructionPolicy.Neutralize),
                new("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 10, 1000, 0))];
            using var w = new SimulationWorld(types, 16384, 8192, navigationGrid: new(16, 8));
            w.AddInitialUnit(0, new(2048, 4096), 1); w.AddInitialUnit(1, new(4096, 4096), 1); w.AddInitialUnit(2, new(8192, 4096), 2);
            w.Step([new(1, 0, 1, CommandKind.AttackTarget, new(3), TargetEntity: new(2))]);
            NavigationChecks.Check(w.Count == 3 && w.NavigationGrid!.IsBlocked(w.NavigationGrid.Index(new(3, 3))) && Poses(w).Single(p => p.Id.Value == 2).Health == 1, "Neutralization removed the stamp or body");
            w.Step([new(2, 0, 1, CommandKind.UseAbility, new(1), TargetEntity: new(2), Action: "capture")]);
            w.Step([new(3, 0, 1, CommandKind.UseAbility, new(1), TargetEntity: new(2), Action: "repair")]);
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Presentation.Owner == 1 && Poses(w).Single(p => p.Id.Value == 2).Health == 3, "Captured ruin did not accept ally repair");
        });
        test("Players: skirmish journal captures distant resources and builds for the correct player", () =>
        {
            var catalog = UnitCatalog.Load([new("units", File.ReadAllText("sandbox/content/units.yaml")), new("buildings", File.ReadAllText("sandbox/content/buildings.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            using var a = SkirmishScenario.Create(catalog, 1); using var b = SkirmishScenario.Create(catalog, 4);
            var commands = SkirmishScenario.Commands();
            for (int tick = 1; tick <= 1800; tick++)
            {
                var batch = commands.Where(c => c.Tick == tick).ToArray(); a.Step(batch); b.Step(batch);
                if (tick % 100 == 0) NavigationChecks.Check(a.Report() == b.Report(), "Skirmish differs by workers");
            }
            var poses = Poses(a);
            NavigationChecks.Check(poses.Single(p => p.Id.Value == 5).Presentation.Owner == 1 && poses.Single(p => p.Id.Value == 8).Presentation.Owner == 2, "Engineers did not reach and capture distant resources");
            NavigationChecks.Check(a.Count == 14 && Player(a, 1).Credits > 4000 && Player(a, 2).Credits > 5000 && Player(a, 1).Energy == 80, "Skirmish production/income/power did not complete");
        });
        test("Players: content rejects invalid capture, economy and neutralization definitions", () =>
        {
            foreach (string yaml in new[] { "x: {Health: {HP: 10, Destruction: Neutralize}}", "x: {Health: {HP: 10}, Capturable: {Ticks: 0}}", "x: {Health: {HP: 10}, Economy: {Energy: -10}}", "x: {Health: {HP: 10}, Actions: {Definitions: {capture: {Capture: true, AutoCast: true}}}}" })
                NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("invalid", yaml)]));
        });
    }
}
