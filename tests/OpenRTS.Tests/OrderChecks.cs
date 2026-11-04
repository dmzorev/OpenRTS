using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class OrderChecks
{
    private static UnitDefinition Fighter(string id = "f", int damage = 20, int windup = 2) =>
        new(id, 100, ["idle", "move", "fire"], weapon: new(2048, 5120, damage, 12, windup));
    private static SimulationWorld Battle(bool navigation = true, int workers = 1) => new([Fighter(), new("dummy", 60)], 24576, 16384,
        navigationGrid: navigation ? new(24, 16) : null, navigationWorkers: workers);
    private static EntityPose Pose(SimulationWorld world, uint id) => Poses(world).Single(p => p.Id.Value == id);
    public static void Run(Action<string, Action> test)
    {
        test("Orders: a tank clicks water and reaches its own shoreline, not a disconnected island", () =>
        {
            var grid = new NavigationGrid(12, 7, terrain: Enumerable.Range(0, 84).Select(i => i % 12 is >= 5 and <= 8 ? (byte)1 : (byte)0));
            var profile = new Locomotor("land", [TerrainRule.Ground, TerrainRule.Blocked]);
            var resolver = new DestinationResolver(grid, profile);
            NavigationChecks.Check(resolver.HasStoppingTerrain(new(1536, 3584)), "Cold component hint lost legal stopping terrain");
            NavigationChecks.Check(resolver.Resolve(new(1536, 3584), new(8704, 3584), out var shore) && shore.X < 5 * 1024 && shore.Y == 3584 && resolver.CanStop(shore), "Resolver chose inaccessible opposite shore");
            var world = new SimulationWorld([new("tank", 100, locomotor: profile)], 12288, 7168, navigationGrid: grid);
            world.AddInitialUnit(0, new(1536, 3584)); world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(8704, 3584))]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.DestinationAdjusted), "Adjusted order had no event");
            for (int i = 0; i < 60; i++) world.Step();
            NavigationChecks.Check(Pose(world, 1).Current == shore && world.MovingCount == 0, "Tank did not reach shore");
        });
        test("Orders: selected recipients leave unselected orders intact", () =>
        {
            var world = new SimulationWorld([new("u", 100)], 16384, 12288, navigationGrid: new(16, 12));
            for (int i = 0; i < 4; i++) world.AddInitialUnit(0, new(2048 + i * 2048, 2048));
            world.Step([new(1, 0, 1, CommandKind.MoveSelected, Position: new(10240, 8192), Recipients: new([new(1), new(2)]))]);
            NavigationChecks.Check(Pose(world, 1).Current != Pose(world, 1).Previous && Pose(world, 3).Current == Pose(world, 3).Previous, "Command escaped its recipient set");
            var destination = Pose(world, 1).Order.Destination;
            world.Step([new(2, 0, 1, CommandKind.MoveSelected, Position: new(4096, 8192), Recipients: new([new(3), new(4)]))]);
            NavigationChecks.Check(Pose(world, 1).Order.Destination == destination, "Second group's order changed the first group's goal");
            world.Step([new(3, 0, 1, CommandKind.Move, Position: new(1024, 1024))]);
            NavigationChecks.Check(world.Events.ToArray().Single().Kind == WorldEventKind.MissingEntity && Pose(world, 1).Order.Destination == destination,
                "A missing recipient silently became an order for the whole world");
        });
        test("Orders: immutable recipients, FIFO and valid replacement behave identically through Session", () =>
        {
            EntityId[] ids = [new(1)]; var recipients = new EntitySet(ids); ids[0] = new(99);
            SimulationWorld Create() { var w = new SimulationWorld([new("u", 100)], 16384, 12288, navigationGrid: new(16, 12)); w.AddInitialUnit(0, new(2048, 2048)); return w; }
            using var session = new SimulationSession(Create, startPaused: true);
            session.Issue(new(UnitActionKind.Move, recipients, new(4096, 2048)));
            session.Issue(new(UnitActionKind.Move, recipients, new(4096, 4096), Queued: true));
            session.StepOnce(); Wait(() => session.Frames.AcquireLatest() is { } frame && Tick(frame) == 1);
            var log = session.GetCommandLog(); var direct = Create(); direct.Step(log);
            using var snapshot = session.Frames.AcquireLatest()!;
            NavigationChecks.Check(snapshot.Poses[0].Order.QueuedCount == 1 && snapshot.Poses[0] == Pose(direct, 1), "UI-independent action and canonical command differ");
            for (int i = 0; i < 50; i++) direct.Step();
            NavigationChecks.Check(Pose(direct, 1).Current == new WorldPosition(4096, 4096), "Queued waypoint did not execute");
            static long Tick(FrameExchange.FrameLease lease) { using (lease) return lease.Tick; }
            static void Wait(Func<bool> ready) { if (!SpinWait.SpinUntil(ready, 5000)) throw new Exception("Session did not publish"); }
        });
        test("Orders: invalid replacement preserves the pending queue, bounded to 32", () =>
        {
            var world = new SimulationWorld([new("u", 100)], 16384, 12288); world.AddInitialUnit(0, new(1024, 1024));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(15000, 1024))]);
            WorldCommand[] queued = Enumerable.Range(1, 33).Select(i => new WorldCommand(2, 0, i, CommandKind.Move, new(1), new(15000, 4096), Queued: true)).ToArray();
            world.Step(queued);
            NavigationChecks.Check(Pose(world, 1).Order.QueuedCount == 32 && world.Events.ToArray().Single().Kind == WorldEventKind.QueueFull, "Queue is unbounded");
            world.Step([new(3, 0, 1, CommandKind.AttackMove, new(1), new(15000, 1024))]);
            NavigationChecks.Check(Pose(world, 1).Order.QueuedCount == 32 && world.Events.ToArray().Single().Kind == WorldEventKind.InvalidOrder, "Invalid replacement erased queued orders");
            world.Step([new(4, 0, 1, CommandKind.Stop, new(1))]);
            NavigationChecks.Check(Pose(world, 1).Order.QueuedCount == 0 && world.MovingCount == 0, "Stop did not replace and clear the queue");
        });
        test("Orders: AttackMove engages an enemy and resumes its original destination", () =>
        {
            var world = Battle(); world.AddInitialUnit(0, new(2048, 4096), 1); world.AddInitialUnit(1, new(8192, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackMove, new(1), new(18432, 4096))]);
            int shots = 0;
            for (int i = 0; i < 220; i++) { shots += world.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired); world.Step(); }
            NavigationChecks.Check(shots == 3 && world.Count == 1 && Pose(world, 1).Current == new WorldPosition(18432, 4096), "AttackMove failed to engage/resume");
        });
        test("Orders: moving cancels windup and never resets a fired weapon's cooldown", () =>
        {
            var world = Battle(false); world.AddInitialUnit(0, new(2048, 4096), 1); world.AddInitialUnit(1, new(3584, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            world.Step([new(2, 0, 1, CommandKind.Move, new(1), new(2048, 8192))]);
            NavigationChecks.Check(Pose(world, 2).Health == 60, "Move failed to cancel windup");
            world.Step([new(3, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]); world.Step(); world.Step();
            long ready = Pose(world, 1).Order.ReadyTick; NavigationChecks.Check(ready > world.Tick && Pose(world, 2).Health == 40, "Weapon did not fire after windup");
            world.Step([new(6, 0, 1, CommandKind.Move, new(1), new(2048, 8192))]);
            world.Step([new(7, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(Pose(world, 1).Order.ReadyTick == ready && Pose(world, 2).Health == 40, "Reissuing orders reset cooldown");
        });
        test("Orders: Hold fires in place and simultaneous lethal hits resolve together", () =>
        {
            var world = new SimulationWorld([Fighter(damage: 100, windup: 0)], 16384, 12288, navigationGrid: new(16, 12));
            world.AddInitialUnit(0, new(2048, 4096), 1); world.AddInitialUnit(0, new(3584, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.HoldPosition, new(1)), new(1, 0, 2, CommandKind.HoldPosition, new(2))]);
            NavigationChecks.Check(world.Count == 0 && world.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired) == 2, "ID order prevented simultaneous retaliation");
        });
        test("Orders: acquisition ignores hidden nearer enemies and combat respects walls", () =>
        {
            var grid = new NavigationGrid(16, 12, [new(3, 3)]);
            var world = new SimulationWorld([Fighter(), new("dummy", 100)], 16384, 12288, navigationGrid: grid);
            world.AddInitialUnit(0, new(2560, 3584), 1); world.AddInitialUnit(1, new(4608, 3584), 2); world.AddInitialUnit(1, new(2560, 5632), 2);
            world.Step([new(1, 0, 1, CommandKind.HoldPosition, new(1))]);
            NavigationChecks.Check(Pose(world, 1).Order.Target.Value == 3, "A wall-hidden nearer target suppressed visible acquisition");
            for (int i = 0; i < 5; i++) world.Step();
            NavigationChecks.Check(Pose(world, 2).Health == 100 && Pose(world, 3).Health < 100, "Hitscan passed through static terrain");
        });
        test("Orders: queued targets are revalidated after terrain changes", () =>
        {
            var grid = new NavigationGrid(12, 8); var world = new SimulationWorld([new("u", 100)], 12288, 8192, navigationGrid: grid);
            world.AddInitialUnit(0, new(1536, 2560));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(3584, 2560)), new(1, 0, 2, CommandKind.Move, new(1), new(7680, 2560), Queued: true)]);
            world.Step([new(2, 0, 1, CommandKind.BlockCell, Position: new(7680, 2560))]);
            bool adjusted = false;
            for (int i = 0; i < 80; i++) { world.Step(); adjusted |= world.Events.ToArray().Any(e => e.Kind == WorldEventKind.DestinationAdjusted); }
            NavigationChecks.Check(adjusted && Pose(world, 1).Current != new WorldPosition(7680, 2560) && world.MovingCount == 0, "Queue used stale destination validation");
        });
        test("Orders: missing queued attack targets are skipped without losing later waypoints", () =>
        {
            var world = Battle(); world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(4096, 6144), 2);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(4096, 2048)),
                new(1, 0, 2, CommandKind.AttackTarget, new(1), TargetEntity: new(2), Queued: true),
                new(1, 0, 3, CommandKind.Move, new(1), new(6144, 2048), Queued: true)]);
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(2))]);
            bool rejected = false;
            for (int i = 0; i < 50; i++) { world.Step(); rejected |= world.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder); }
            NavigationChecks.Check(rejected && Pose(world, 1).Current == new WorldPosition(6144, 2048), "Destroyed target stalled/erased the queue");
        });
        test("Orders: Hold leaves transit-only ground before acquiring and firing", () =>
        {
            var bridge = new Locomotor("bridge", [TerrainRule.Ground, new(true, false, 1024, 1024)]);
            var free = new Locomotor("free", [TerrainRule.Ground, TerrainRule.Ground]);
            var grid = new NavigationGrid(7, 5, terrain: Enumerable.Range(0, 35).Select(i => i % 7 == 3 ? (byte)1 : (byte)0));
            var world = new SimulationWorld([new("f", 100, ["idle", "move", "fire"], locomotor: bridge, weapon: new(2048, 5120, 10, 12)), new("d", 100, locomotor: free)],
                7168, 5120, navigationGrid: grid);
            world.AddInitialUnit(0, new(1536, 2560), 1); world.AddInitialUnit(1, new(4096, 2560), 2);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(5632, 2560))]); for (int i = 1; i < 13; i++) world.Step();
            var prior = Pose(world, 1).Current;
            world.Step([new(14, 0, 1, CommandKind.HoldPosition, new(1))]);
            NavigationChecks.Check(Pose(world, 1).Current.X < prior.X && !world.Events.ToArray().Any(e => e.Kind == WorldEventKind.WeaponFired), "Hold engaged while still on a transit-only sample");
            for (int i = 0; i < 15; i++) world.Step();
            NavigationChecks.Check(bridge.Rules[grid.TerrainAt(grid.CellAt(Pose(world, 1).Current))].CanStop && Pose(world, 2).Health < 100, "Hold failed to engage from legal ground");
        });
        test("Orders: independent groups never exchange each other's final goals", () =>
        {
            var world = new SimulationWorld([new("u", 100)], 12288, 5120, navigationGrid: new(12, 5));
            foreach (var p in new WorldPosition[] { new(2560, 1536), new(2560, 3584), new(7680, 1536), new(7680, 3584) }) world.AddInitialUnit(0, p);
            world.Step([new(1, 0, 1, CommandKind.MoveSelected, Position: new(9728, 2560), Recipients: new([new(1), new(2)])),
                new(1, 0, 2, CommandKind.MoveSelected, Position: new(1536, 2560), Recipients: new([new(3), new(4)]))]);
            for (int i = 0; i < 160; i++)
            {
                NavigationChecks.Check(Pose(world, 1).Order.Destination.X >= 9728 && Pose(world, 2).Order.Destination.X >= 9728 &&
                    Pose(world, 3).Order.Destination.X <= 2560 && Pose(world, 4).Order.Destination.X <= 2560, "Separate cohorts swapped destinations");
                Separated(world); world.Step();
            }
        });
        test("Orders: selections spanning disconnected islands resolve on both islands", () =>
        {
            var grid = new NavigationGrid(12, 7, Enumerable.Range(0, 7).Select(y => new GridCell(5, y)));
            var world = new SimulationWorld([new("u", 100)], 12288, 7168, navigationGrid: grid);
            world.AddInitialUnit(0, new(1536, 3584)); world.AddInitialUnit(0, new(9728, 3584));
            world.Step([new(1, 0, 1, CommandKind.MoveSelected, Position: new(5632, 3584), Recipients: new([new(1), new(2)]))]);
            NavigationChecks.Check(Pose(world, 1).Order.Destination.X < 5120 && Pose(world, 2).Order.Destination.X > 6144, "Disconnected selection was ignored or crossed a wall");
            for (int i = 0; i < 60; i++) world.Step(); NavigationChecks.Check(world.MovingCount == 0, "Disconnected cohorts did not finish");
        });
        test("Orders: combat and packed removals preserve sequential/parallel tick hashes", () =>
        {
            SimulationWorld Create(int workers, bool reverse)
            {
                var world = new SimulationWorld([Fighter()], 24576, 16384, navigationGrid: new(24, 16), navigationWorkers: workers, reverseNavigationJobs: reverse);
                for (int i = 0; i < 6; i++) world.AddInitialUnit(0, new((i < 3 ? 4096 : 18432), (i % 3 + 2) * 2048), i < 3 ? 1u : 2u);
                return world;
            }
            var a = Create(1, false); var b = Create(4, true);
            for (int tick = 1; tick <= 180; tick++)
            {
                WorldCommand[] commands = tick == 1 ? [new(1, 0, 1, CommandKind.AttackMove, Position: new(11264, 6144), Recipients: new([new(1), new(2), new(3)])),
                    new(1, 0, 2, CommandKind.AttackMove, Position: new(11264, 6144), Recipients: new([new(4), new(5), new(6)]))] : [];
                a.Step(commands); b.Step(commands); NavigationChecks.Check(a.Report() == b.Report(), "Combat changed with workers/packed removal");
            }
            NavigationChecks.Check(a.Count < 6, "Battle fixture never engaged");
        });
        test("Orders: multi-recipient Move and AttackMove work without a navigation host", () =>
        {
            var world = Battle(false); world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(0, new(4096, 2048), 1);
            world.Step([new(1, 0, 1, CommandKind.MoveSelected, Position: new(8192, 2048), Recipients: new([new(1), new(2)]))]);
            world.Step([new(2, 0, 1, CommandKind.AttackMove, Position: new(8192, 4096), Recipients: new([new(1), new(2)]))]);
            NavigationChecks.Check(world.MovingCount == 2, "Action dispatcher required Godot or a navigation layer");
        });
        test("Orders: an enemy blocker never yields as an allied unit", () =>
        {
            var free = Enumerable.Range(0, 7).Select(x => new GridCell(x, 1)).Append(new(3, 0)).ToHashSet();
            var grid = new NavigationGrid(7, 3, from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 7)
                where !free.Contains(new(x, y)) select new GridCell(x, y));
            var world = new SimulationWorld([new("u", 100)], 7168, 3072, navigationGrid: grid, speedPerTick: 256);
            world.AddInitialUnit(0, grid.Center(grid.Index(new(1, 1))), 1); var enemy = grid.Center(grid.Index(new(3, 1)));
            world.AddInitialUnit(0, enemy, 2);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(6, 1))))]);
            for (int i = 0; i < 100; i++) { world.Step(); NavigationChecks.Check(Pose(world, 2).Current == enemy && world.NavigationYields == 0, "Enemy was asked to move aside"); }
        });
    }
}
