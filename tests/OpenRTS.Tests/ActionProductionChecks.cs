using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class ActionProductionChecks
{
    private static FootprintDefinition Stamp() => new(2, 2, [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);
    private static SimulationWorld Factory(int workers = 1, bool reverse = false, int capacity = 20, NavigationGrid? grid = null, WorldPosition? exit = null, int search = 2048, int duration = 3)
    {
        UnitDefinition[] types = [new("factory", 500, ["idle"], movable: false, footprint: Stamp(), states: ["working"],
            taskQueues: [new("units", capacity, exit: exit ?? new(0, 2048), exitSearchRadius: search, workingState: "working"), new("buildings", capacity, workingState: "working")],
            products: [new("u", "unit", "units"), new("b", "building", "buildings")]),
            new("unit", 100, body: BodyShape.Circle(128), build: new(duration, 20)),
            new("building", 500, ["idle"], movable: false, footprint: Stamp(), build: new(3, 80, ProductionKind.PlaceBuilding))];
        var w = new SimulationWorld(types, 20480, 16384, navigationGrid: grid ?? new(20, 16), navigationWorkers: workers, reverseNavigationJobs: reverse, players: [new(1, "blue", 10000), new(2, "red", 10000)]);
        w.AddInitialUnit(0, new(4096, 4096), 1); return w;
    }
    private static TaskPose[] Tasks(SimulationWorld w) { var result = new TaskPose[w.TaskCount]; w.CopyTaskPoses(result); return result; }
    private static WorldCommand Enqueue(SimulationWorld w, string action = "u", int amount = 1, bool repeat = false, EntitySet? recipients = null) => new(w.Tick + 1, 0, 1, CommandKind.EnqueueTask, new(1), Action: action, Amount: amount, Repeat: repeat, Recipients: recipients);
    private static SimulationWorld Grenades(int count = 1)
    {
        var grenade = new WeaponDefinition(8192, 8192, 10, 100, 0, projectile: ProjectileKind.Grenade, splashRadius: 1000, projectileSpeed: 4096);
        var unit = new UnitDefinition("u", 100, ["idle", "move", "fire"], weapon: grenade, abilities: [new("grenade", "main", true)]);
        var w = new SimulationWorld([unit], 20000, 12000);
        for (int i = 0; i < count; i++) w.AddInitialUnit(0, new(1000 + i * 600, 2000), 1);
        w.AddInitialUnit(0, new(5000, 6000), 2); return w;
    }
    public static void Run(Action<string, Action> test)
    {
        test("Actions: YAML separates UI bindings from gameplay and inherits/overrides build costs", () =>
        {
            string yaml = "u: {Health: {HP: 10}, Buildable: {Ticks: 10, Cost: 7}}\nf: {Health: {HP: 10}, TaskQueues: {Queues: {production: {Capacity: 5}}}, Actions: {Buttons: {u: {Unit: u, Cost: 3, Label: Train, Hotkey: A}}}}";
            var a = UnitCatalog.Load([new("a", yaml)]); var b = UnitCatalog.Load([new("b", yaml.Replace("Train, Hotkey: A", "Build, Hotkey: Q"))]);
            NavigationChecks.Check(a.Summary == b.Summary && a.Units[0].Products[0].Override!.Ticks == 10 && a.Units[0].Products[0].Override!.Cost == 3 && a.Presentations[0].Actions![0].Hotkey == "A", "UI entered gameplay fingerprint or defaults failed");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("bad", yaml.Replace("Unit: u", "Unit: missing"))]));
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("bad", yaml.Replace("Capacity: 5", "Capacity: 0"))]));
        });
        test("Actions: twenty selected units cast once and pick the first ready stable ID", () =>
        {
            using var w = Grenades(20); var ids = new EntitySet(Enumerable.Range(1, 20).Select(i => new EntityId((uint)i)));
            w.Step([new(1, 0, 1, CommandKind.UseAbility, Position: new(6000, 4000), Recipients: ids, Action: "grenade")]);
            NavigationChecks.Check(w.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired) == 1 && w.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired).Entity.Value == 1, "Manual action fired all units");
            w.Step([new(2, 0, 1, CommandKind.UseAbility, Position: new(6000, 4000), Recipients: ids, Action: "grenade")]);
            NavigationChecks.Check(w.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired).Entity.Value == 2, "Cooldown was not used to choose next caster");
        });
        test("Actions: ground grenades land on a fixed point and inactive abilities never auto-fire", () =>
        {
            using var w = Grenades(); for (int i = 0; i < 8; i++) w.Step(); NavigationChecks.Check(w.ProjectileCount == 0, "Disabled ability became an ordinary weapon");
            w.Step([new(9, 0, 1, CommandKind.UseAbility, new(1), new(5000, 6000), Action: "grenade")]);
            w.Step(); w.Step(); NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Health == 90, "Ground grenade did not splash a target");
            w.Step([new(12, 0, 1, CommandKind.UseAbility, new(1), new(5000, 6000), Action: "grenade")]); NavigationChecks.Check(w.Events.ToArray().Any(e => e.Kind == WorldEventKind.ActionUnavailable), "Cooldown was reset by a repeated order");
        });
        test("Actions: autocast toggles are per unit and authoritative", () =>
        {
            using var w = Grenades(); w.Step([new(1, 0, 1, CommandKind.SetAutoCast, new(1), Action: "grenade", Enabled: true)]);
            int fired = w.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired);
            for (int i = 0; i < 8; i++) { w.Step(); fired += w.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired); }
            NavigationChecks.Check(fired == 1, "Enabled grenade did not acquire normally");
            var poses = new WeaponPose[w.WeaponCount]; w.CopyWeaponPoses(poses); NavigationChecks.Check(poses.Single(p => p.Entity.Value == 1).AutoCast && !poses.Single(p => p.Entity.Value == 2).AutoCast, "Toggle leaked across owners");
        });
        test("Actions: repair autocast heals only damaged allied mechanical targets", () =>
        {
            var heal = new UnitDefinition("repair", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 10, 10, 0, effect: WeaponEffect.Repair, livingModifier: 0), abilities: [new("repair", "main")]);
            var enemy = new UnitDefinition("enemy", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 5, 100, 0));
            using var w = new SimulationWorld([heal, new("vehicle", 100, armor: ArmorKind.Mechanical), enemy], 16000, 16000);
            w.AddInitialUnit(0, new(1000, 2000), 1); w.AddInitialUnit(1, new(3000, 2000), 1); w.AddInitialUnit(2, new(7000, 2000), 2);
            w.Step([new(1, 0, 1, CommandKind.AttackTarget, new(3), TargetEntity: new(2))]);
            w.Step([new(2, 0, 1, CommandKind.SetAutoCast, new(1), Action: "repair", Enabled: true)]);
            for (int i = 0; i < 8; i++) w.Step();
            NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Health == 100, "Repair failed to acquire a damaged ally");
        });
        test("Actions: typed ability input crosses the live session boundary with one caster", () =>
        {
            using var session = new SimulationSession(() => Grenades(2), [], realtime: true, startPaused: true);
            session.Issue(new(UnitActionKind.Ability, new([new(1), new(2)]), new(5000, 6000), Action: "grenade")); session.StepOnce();
            NavigationChecks.Check(SpinWait.SpinUntil(() => { using var f = session.Frames.AcquireLatest(); return f?.Tick == 1; }, TimeSpan.FromSeconds(5)), "Live ability tick did not publish");
            using var frame = session.Frames.AcquireLatest()!;
            NavigationChecks.Check(frame.Weapons.ToArray().Count(w => w.ReadyTick > 1) == 1 && session.GetCommandLog().Single().Action == "grenade", "Typed input lost its action ID or cast on multiple units");
        });
        test("Production: a multi-building move sets each rally without reporting a missing mobile route", () =>
        {
            using var w = Factory(); w.AddInitialUnit(0, new(8192, 4096), 1);
            w.Step([new(1, 0, 1, CommandKind.MoveSelected, Position: new(16000, 12000), Recipients: new([new(1), new(2)]))]);
            NavigationChecks.Check(Poses(w).All(p => p.HasRally) && !w.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute), "Rally selection tried to route an empty mobile group");
        });
        test("Production: removing a lower packed producer preserves the surviving producer queue", () =>
        {
            using var w = Factory(); w.AddInitialUnit(0, new(8192, 4096), 1);
            w.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(2), Action: "u", Repeat: true)]);
            w.Step([new(2, 0, 1, CommandKind.Destroy, new(1))]); w.Step();
            NavigationChecks.Check(w.Count == 2 && Tasks(w).Single().Producer.Value == 2 && w.Events.ToArray().Any(e => e.Kind == WorldEventKind.Produced && e.Entity.Value == 2), "Packed removal detached the queue from its producer");
        });
        test("Production: active tasks publish the configured working state without retaining it after completion", () =>
        {
            using var w = Factory(); w.Step([Enqueue(w)]); NavigationChecks.Check(Poses(w)[0].Presentation.States != 0, "Work state was not published");
            w.Step(); w.Step(); NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 1).Presentation.States == 0, "Finished work retained its derived state");
        });
        test("Production: recipe overrides enforce placement range, preserve price and play semantic exit clips", () =>
        {
            var producer = new UnitDefinition("f", 100, ["idle", "door"], movable: false, footprint: Stamp(), taskQueues: [new("q", exit: new(0, 2048), exitAnimation: "door")],
                products: [new("b", "b", "q", new(1, 7, ProductionKind.PlaceBuilding, 1024)), new("u", "u", "q")]);
            using var w = new SimulationWorld([producer, new("b", 100, ["idle"], movable: false, footprint: Stamp(), build: new(50, 80, ProductionKind.PlaceBuilding)), new("u", 100, build: new(1))], 20480, 16384, navigationGrid: new(20, 16), players: [new(1, "blue", 1000)]);
            w.AddInitialUnit(0, new(4096, 4096), 1); w.Step([new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "b")]);
            var task = Tasks(w).Single(); NavigationChecks.Check(task.Cost == 7 && task.Duration == 1 && task.Ready, "Recipe defaults did not override the target");
            w.Step([new(2, 0, 1, CommandKind.PlaceProduced, new(1), new(16384, 4096), Action: "b", TaskId: task.Id)]);
            NavigationChecks.Check(w.TaskCount == 1, "Out of range placement consumed the permit");
            w.Step([new(3, 0, 1, CommandKind.PlaceProduced, new(1), new(7168, 4096), Action: "b", TaskId: task.Id)]);
            NavigationChecks.Check(w.TaskCount == 0 && w.Count == 2, "Valid boundary range was rejected");
            w.Step([new(4, 0, 1, CommandKind.EnqueueTask, new(1), Action: "u")]);
            NavigationChecks.Check(w.Events.ToArray().Any(e => e.Kind == WorldEventKind.AnimationPlayed && e.Animation == producer.AnimationIndex("door")), "Exit did not play the declared semantic animation");
        });
        test("Production: batch5 distributes by shortest queue and stable producer ID", () =>
        {
            using var w = Factory(); w.AddInitialUnit(0, new(8192, 4096), 1); var ids = new EntitySet([new(2), new(1)]);
            w.Step([Enqueue(w, amount: 5, recipients: ids)]); var tasks = Tasks(w);
            NavigationChecks.Check(tasks.Count(t => t.Producer.Value == 1) == 3 && tasks.Count(t => t.Producer.Value == 2) == 2, "Batch did not distribute 3/2 deterministically");
        });
        test("Production: independent queues progress together and buildings wait for explicit placement", () =>
        {
            using var w = Factory(); w.Step([Enqueue(w), new(1, 0, 2, CommandKind.EnqueueTask, new(1), Action: "b")]); w.Step(); w.Step();
            NavigationChecks.Check(w.Count == 2 && Tasks(w) is [{ Ready: true, UnitType: 2 }], "Queues did not work in parallel or building spawned before placement");
            var task = Tasks(w)[0]; w.Step([new(4, 0, 1, CommandKind.PlaceProduced, new(1), new(1, 1), Action: "b", TaskId: task.Id)]);
            NavigationChecks.Check(w.TaskCount == 1 && w.Count == 2, "Invalid placement consumed the ready task");
            w.Step([new WorldCommand(5, 0, 1, CommandKind.PlaceProduced, new(1), new(12288, 12288), Action: "b", TaskId: task.Id)
                { QuarterTurns = 2 }]);
            NavigationChecks.Check(w.Count == 3 && w.TaskCount == 0 &&
                w.NavigationGrid!.IsBlocked(w.NavigationGrid.CellAt(new(12288, 12288))) &&
                Poses(w).Single(p => p.Id.Value == 3).QuarterTurns == 2, "Ready building lost its placement or rotation");
        });
        test("Production: rotated factory spawns units at its rotated exit", () =>
        {
            using var world = Factory(search: 0, duration: 1);
            world.Step([
                new WorldCommand(1, 0, 1, CommandKind.Spawn, Position: new(12288, 8192), UnitType: 0, Owner: 1) { QuarterTurns = 1 },
                new(1, 0, 2, CommandKind.EnqueueTask, new(2), Action: "u")]);
            NavigationChecks.Check(Poses(world).Single(p => p.Id.Value == 3).Current == new WorldPosition(10240, 8192),
                "Factory exit was not rotated from south to west");
        });
        test("Production: repeated tasks renew at the tail and stack cancellation is bounded", () =>
        {
            using var w = Factory(); w.Step([Enqueue(w, amount: 2, repeat: true)]); long first = Tasks(w)[0].Id; w.Step(); w.Step();
            var tasks = Tasks(w); NavigationChecks.Check(tasks.Length == 2 && tasks[0].Id != first && tasks[1].Id > tasks[0].Id, "Repeat lost its place or was not renewed");
            w.Step([new(4, 0, 1, CommandKind.CancelTasks, new(1), Action: "u", Repeat: true, Amount: 1)]); NavigationChecks.Check(w.TaskCount == 1, "Single cancel removed the stack");
            w.Step([new(5, 0, 1, CommandKind.CancelTasks, new(1), Action: "u", Repeat: true, Amount: int.MaxValue)]); NavigationChecks.Check(w.TaskCount == 0, "Cancel all failed");
        });
        test("Production: blocked exits retain the completed task and retry without overlap", () =>
        {
            var grid = new NavigationGrid(20, 16, from y in Enumerable.Range(0, 16) from x in Enumerable.Range(0, 20) select new GridCell(x, y));
            using var w = Factory(grid: grid, exit: new(512, 2560), search: 0, duration: 1); w.Step([Enqueue(w)]);
            NavigationChecks.Check(w.Count == 1 && Tasks(w)[0].Ready && w.Events.ToArray().Any(e => e.Kind == WorldEventKind.ProductionBlocked), "Blocked production was discarded");
            w.Step([new(2, 0, 1, CommandKind.UnblockCell, Position: new(4608, 6656))]);
            NavigationChecks.Check(w.Count == 2 && w.TaskCount == 0 && Poses(w).Single(p => p.Id.Value == 2).Current == new WorldPosition(4608, 6656), "Factory did not retry the released exit");
        });
        test("Production: queue caps, rally, destruction and published task leases stay valid", () =>
        {
            using var w = Factory(capacity: 2); w.Step([Enqueue(w, amount: 5), new(1, 0, 2, CommandKind.SetRally, new(1), new(16000, 12000))]);
            NavigationChecks.Check(w.TaskCount == 2, "Queue exceeded capacity"); var frames = new FrameExchange(); frames.TryPublish(w); var old = frames.AcquireLatest()!;
            w.Step(); w.Step(); NavigationChecks.Check(Poses(w).Single(p => p.Id.Value == 2).Order.Destination == new WorldPosition(16000, 12000), "Produced unit missed rally");
            w.Step([new(4, 0, 1, CommandKind.Destroy, new(1))]); NavigationChecks.Check(w.TaskCount == 0 && old.Tasks.Length == 2, "Death leaked production or mutated a pinned lease");
            old.Dispose(); NavigationChecks.Reject<ObjectDisposedException>(() => { _ = old.Tasks.Length; });
        });
        test("Production: task and ability commands replay unchanged through a threaded session", () =>
        {
            WorldCommand[] script = [new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "u", Amount: 2, Repeat: true),
                new(2, 0, 1, CommandKind.SetRally, new(1), new(16000, 12000)),
                new(3, 0, 1, CommandKind.CancelTasks, new(1), Action: "u", Repeat: true)];
            using var direct = Factory(); var expected = DiagnosticScenario.Run(direct, 20, script);
            using var session = new SimulationSession(() => Factory(4, true), script, 20, realtime: false);
            NavigationChecks.Check(session.Completion.GetAwaiter().GetResult() == expected && session.GetCommandLog().SequenceEqual(script), "Task journal changed through the host boundary");
            using var frame = session.Frames.AcquireLatest()!; NavigationChecks.Check(frame.Tasks.Length == direct.TaskCount, "Task snapshot was dropped");
        });
        test("Production: workers and packed removals preserve the complete task state", () =>
        {
            using var a = Factory(); using var b = Factory(4, true);
            for (int tick = 1; tick <= 20; tick++)
            {
                WorldCommand[] commands = tick == 1 ? [Enqueue(a, amount: 5, repeat: true)] : [];
                a.Step(commands); b.Step(commands); NavigationChecks.Check(a.Report() == b.Report(), "Workers changed production state");
            }
        });
    }
}
