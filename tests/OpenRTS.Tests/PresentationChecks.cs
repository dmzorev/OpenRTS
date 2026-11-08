using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class PresentationChecks
{
    private static UnitDefinition Unit() => new("unit", 100, ["idle", "move", "fire", "deploy"], ["damaged", "swimming"]);
    public static void Run(Action<string, Action> test)
    {
        test("Walking follows actual displacement and stops on arrival or Stop", () =>
        {
            var type = Unit(); using var world = new SimulationWorld([type], 10000, 10000);
            world.AddInitialUnit(0, new(1000, 1000));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(1256, 1000))]);
            NavigationChecks.Check(Poses(world)[0].Presentation.Body.Animation == type.MoveAnimation, "Moving unit did not walk");
            world.Step(); world.Step();
            var idle = Poses(world)[0];
            NavigationChecks.Check(idle.Previous == idle.Current && idle.Presentation.Body.Animation == type.IdleAnimation, "Arrival left the walking cycle active");
            var grid = new NavigationGrid(5, 5, Enumerable.Range(0, 5).Select(y => new GridCell(2, y)));
            using var blocked = new SimulationWorld([type], 5120, 5120, navigationGrid: grid);
            blocked.AddInitialUnit(0, grid.Center(grid.Index(new(1, 2))));
            blocked.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(3, 2))))]);
            blocked.Step([new(2, 0, 1, CommandKind.Stop, new(1))]);
            var waiting = Poses(blocked)[0];
            NavigationChecks.Check(waiting.Previous == waiting.Current && waiting.Presentation.Body.Animation == type.IdleAnimation, "Stationary unit walked because it had a move order");
        });
        test("Presentation capabilities are immutable, canonical and bounded", () =>
        {
            string[] names = ["move", "fire", "idle"];
            var type = new UnitDefinition("u", 100, names, ["swimming", "damaged"]); names[0] = "broken";
            NavigationChecks.Check(type.Animations.SequenceEqual(["fire", "idle", "move"]) && type.States.SequenceEqual(["damaged", "swimming"]), "Capabilities changed or were not canonicalized");
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("u", 100, ["idle", "idle", "move"]));
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("u", 100, ["fire"]));
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("u", 100, states: Enumerable.Range(0, 33).Select(i => "s" + i)));
            var full = new UnitDefinition("u", 100, states: Enumerable.Range(0, 32).Select(i => "s" + i));
            NavigationChecks.Check(full.AllowedStateMask == uint.MaxValue, "The last state bit was lost");
            var clip = new AnimationPlayback(0, 10, 3);
            NavigationChecks.Check(!clip.IsActive(9) && clip.IsActive(10) && clip.IsActive(12) && !clip.IsActive(13), "Finite animation has wrong tick bounds");
        });
        test("Adapter model bindings do not enter gameplay definitions or state hashes", () =>
        {
            UnitCatalog Load(string model, string animations) => UnitCatalog.Load([new("u", $"u: {{Health: {{HP: 100}}, Presentation: {{Model: {model}, Animations: [{animations}], States: [damaged]}}}}")]);
            var a = Load("sprite-model", "fire, idle, move"); var b = Load("rig-model", "move, idle, fire");
            NavigationChecks.Check(a.Summary == b.Summary && a.Presentations[0].Model != b.Presentations[0].Model, "Adapter choice changed gameplay fingerprint");
            using var first = new SimulationWorld(a.Units, 10000, 10000); using var second = new SimulationWorld(b.Units, 10000, 10000);
            first.AddInitialUnit(0, new(1000, 1000)); second.AddInitialUnit(0, new(1000, 1000));
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.PlayAnimation, new(1), Animation: 0, DurationTicks: 8)];
            first.Step(commands); second.Step(commands);
            NavigationChecks.Check(first.Report() == second.Report(), "Model binding leaked into the simulation");
        });
        test("Movement, independent weapon aim, firing and two states coexist", () =>
        {
            var type = Unit(); using var world = new SimulationWorld([type], 10000, 10000);
            world.AddInitialUnit(0, new(2000, 2000), owner: 1); world.AddInitialUnit(0, new(2000, 6000), owner: 2);
            uint states = type.StateMask("damaged") | type.StateMask("swimming");
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(6000, 2000)),
                new(1, 0, 2, CommandKind.Aim, new(1), TargetEntity: new(2)),
                new(1, 0, 3, CommandKind.SetVisualStates, new(1), States: states),
                new(1, 0, 4, CommandKind.PlayAnimation, new(1), Animation: type.AnimationIndex("fire"), DurationTicks: 4)]);
            var pose = Poses(world)[0];
            NavigationChecks.Check(pose.Presentation.Owner == 1 && pose.Presentation.States == states &&
                pose.Presentation.Body.Animation == type.MoveAnimation && pose.Presentation.Weapon.Animation == type.AnimationIndex("fire") &&
                pose.Presentation.BodyFacing.X > 0 && pose.Presentation.BodyFacing.Y == 0 && pose.Presentation.WeaponFacing.Y > 0, "Concurrent presentation was flattened into one exclusive state");
            NavigationChecks.Check(world.Events.ToArray().Single().Kind == WorldEventKind.AnimationPlayed, "One-shot cue was not independently delivered");
            for (int tick = 2; tick <= 5; tick++) world.Step();
            pose = Poses(world)[0];
            NavigationChecks.Check(pose.Presentation.Weapon.Animation == -1 && pose.Presentation.States == states && pose.Presentation.Body.StartTick == 1, "Finite track did not expire independently of states/loop phase");
        });
        test("Body and weapon animation tracks expire independently", () =>
        {
            var type = Unit(); using var world = new SimulationWorld([type], 10000, 10000);
            world.AddInitialUnit(0, new(1000, 1000));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(8000, 1000)),
                new(1, 0, 2, CommandKind.PlayAnimation, new(1), Animation: type.AnimationIndex("deploy"), DurationTicks: 3, Track: AnimationTrack.Body),
                new(1, 0, 3, CommandKind.PlayAnimation, new(1), Animation: type.AnimationIndex("fire"), DurationTicks: 5)]);
            world.Step(); world.Step();
            NavigationChecks.Check(Poses(world)[0].Presentation.Body.Animation == type.AnimationIndex("deploy"), "Body override expired early");
            world.Step(); var pose = Poses(world)[0];
            NavigationChecks.Check(pose.Presentation.Body.Animation == type.MoveAnimation && pose.Presentation.Weapon.Animation == type.AnimationIndex("fire"), "Finishing body action reset the weapon track");
            world.Step(); world.Step(); NavigationChecks.Check(Poses(world)[0].Presentation.Weapon.Animation == -1, "Weapon override persisted past duration");
        });
        test("Queued toggles, invalid cues and destroyed aim targets are deterministic", () =>
        {
            var type = Unit(); using var world = new SimulationWorld([type], 10000, 10000);
            world.AddInitialUnit(0, new(1000, 1000)); world.AddInitialUnit(0, new(4000, 4000));
            var before = world.Report();
            NavigationChecks.Reject<ArgumentException>(() => world.Step([new(1, 0, 1, CommandKind.PlayAnimation, new(1), DurationTicks: 0)]));
            NavigationChecks.Check(world.Report() == before, "Invalid duration advanced the world");
            world.Step([new(1, 0, 1, CommandKind.ToggleVisualState, new(1), States: type.StateMask("damaged")),
                new(1, 0, 2, CommandKind.ToggleVisualState, new(1), States: type.StateMask("damaged")),
                new(1, 0, 3, CommandKind.PlayAnimation, new(1), Animation: 99),
                new(1, 0, 4, CommandKind.SetVisualStates, new(1), States: 128),
                new(1, 0, 5, CommandKind.Aim, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(Poses(world)[0].Presentation.States == 0 && world.Events.ToArray().Count(e => e.Kind == WorldEventKind.InvalidPresentation) == 2, "Invalid capabilities mutated state or toggles used stale snapshots");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(2))]);
            NavigationChecks.Check(Poses(world)[0].Presentation.WeaponFacing == Poses(world)[0].Presentation.BodyFacing, "Deleted target left stale aim");
        });
        test("Skipped frames retain loop phase and pinned presentation snapshots", () =>
        {
            var type = Unit(); using var world = new SimulationWorld([type], 10000, 10000); world.AddInitialUnit(0, new(1000, 1000));
            var exchange = new FrameExchange();
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(8000, 1000)),
                new(1, 0, 2, CommandKind.PlayAnimation, new(1), Animation: type.AnimationIndex("fire"), DurationTicks: 3)]);
            exchange.TryPublish(world); using var old = exchange.AcquireLatest()!;
            for (int i = 0; i < 10; i++) world.Step();
            exchange.TryPublish(world); using var current = exchange.AcquireLatest()!;
            NavigationChecks.Check(old.Poses[0].Presentation.Weapon.Animation == type.AnimationIndex("fire") && current.Poses[0].Presentation.Weapon.Animation == -1 &&
                current.Poses[0].Presentation.Body.StartTick == 1, "Frame skip restarted animation or mutated pinned data");
        });
        test("Layered model content validates references, cycles, dimensions and semantics", () =>
        {
            var units = UnitCatalog.Load([new("units", File.ReadAllText("sandbox/content/units.yaml"))],
                LocomotionCatalog.Load([new("terrain", File.ReadAllText("sandbox/content/terrain.yaml"))]), LocomotionChecks.Weapons());
            var models = ModelCatalog.Load([new("models", File.ReadAllText("sandbox/content/models.yaml"))]); models.ValidateUnits(units);
            NavigationChecks.Check(units.Units.Any(u => u.Id == "sniper") && models.Models.Count == 11 && models.Models["tank"].Parts[1].Parent == 0, "Showcase declarations are incomplete");
            ModelCatalog Load(string value) => ModelCatalog.Load([new("invalid-model", value)]);
            NavigationChecks.Reject<ContentException>(() => Load("m: {Model: {Parts: {body: {Resource: 'res://x', Parent: body}}}}"));
            NavigationChecks.Reject<ContentException>(() => Load("m: {Model: {Parts: {body: {Resource: 'res://x', Size: [1]}}}}"));
            NavigationChecks.Reject<ContentException>(() => Load("m: {Model: {Parts: {body: {Resource: 'res://x'}}, Animations: {idle: {Sequence: stand, Parts: [missing]}}}}"));
            var missing = UnitCatalog.Load([new("u", "u: {Health: {HP: 1}, Presentation: {Model: absent}}")]);
            NavigationChecks.Reject<ArgumentException>(() => models.ValidateUnits(missing));
        });
    }
}
