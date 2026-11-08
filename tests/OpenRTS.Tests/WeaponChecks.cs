using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class WeaponChecks
{
    private static UnitDefinition Armed(string id, WeaponDefinition weapon, bool moving = false, ArmorKind armor = ArmorKind.Living, bool movable = true) =>
        new(id, 1000, ["idle", "move", "fire", "repair", "missiles"], weapons: [new("main", weapon, weapon.Effect == WeaponEffect.Repair ? "repair" : "fire", moving)], armor: armor, movable: movable);
    private static EntityPose Pose(SimulationWorld world, uint id) => Poses(world).Single(p => p.Id.Value == id);
    private static WorldCommand Attack(SimulationWorld world, uint source = 1, uint target = 2) => new(world.Tick + 1, 0, 1, CommandKind.AttackTarget, new(source), TargetEntity: new(target));
    private static WeaponPose[] Mounts(SimulationWorld world) { var poses = new WeaponPose[world.WeaponCount]; world.CopyWeaponPoses(poses); return poses; }
    public static void Run(Action<string, Action> test)
    {
        test("Weapons: named YAML mounts validate references and canonical gameplay fingerprints", () =>
        {
            var catalog = LocomotionChecks.Units();
            NavigationChecks.Check(catalog.Units.Single(u => u.Id == "tank-mixed").Weapons.Count == 2 && catalog.Units.Single(u => u.Id == "engineer").Weapon!.Effect == WeaponEffect.Repair, "Missing sample mounts");
            var weapons = LocomotionChecks.Weapons();
            var altered = WeaponCatalog.Load([new("w", File.ReadAllText("sandbox/content/weapons.yaml").Replace("Burst: 3", "Burst: 4"))]);
            NavigationChecks.Check(catalog.Summary != UnitCatalog.Load([new("u", File.ReadAllText("sandbox/content/units.yaml"))], LocomotionChecks.Catalog(), altered).Summary, "Burst excluded from fingerprint");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("u", "u: {Health: {HP: 1}, Armaments: {Mounts: {a: {Weapon: absent}}}}")], weapons: weapons));
            NavigationChecks.Reject<ContentException>(() => WeaponCatalog.Load([new("w", "w: {Weapon: {Burst: 0}}") ]));
            NavigationChecks.Reject<ContentException>(() => WeaponCatalog.Load([new("w", "w: {Weapon: {Effect: Repair, Projectile: Missile}}") ]));
            NavigationChecks.Reject<ArgumentException>(() => Armed("bad", new(2048, 2048, 10, 2), armor: (ArmorKind)99));
        });
        test("Weapons: factory repairs automatically beyond engineer range", () =>
        {
            var catalog = UnitCatalog.Load([
                new("units", File.ReadAllText("sandbox/content/units.yaml")),
                new("buildings", File.ReadAllText("sandbox/content/buildings.yaml"))
            ], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            var factory = catalog.Units.Single(u => u.Id == "factory");
            var engineer = catalog.Units.Single(u => u.Id == "engineer");
            var power = catalog.Units.Single(u => u.Id == "power-plant");
            NavigationChecks.Check(engineer.Abilities.Single(a => a.Id == "repair").AutoCast, "Engineer repair should default to autocast");

            var enemy = Armed("enemy", new(8192, 8192, 60, 1000, 0));
            var target = new UnitDefinition("vehicle", 100, armor: ArmorKind.Mechanical);
            using var world = new SimulationWorld([factory, enemy, target, power, .. catalog.Units.Where(u => u != factory && u != power)], 24000, 16000);
            world.AddInitialUnit(0, new(7680, 8192), 1);
            world.AddInitialUnit(2, new(1536, 8192), 1);
            world.AddInitialUnit(1, new(1536, 12288), 2);
            world.AddInitialUnit(3, new(15872, 4096), 1);
            world.Step([Attack(world, 3, 2)]);
            NavigationChecks.Check(Pose(world, 2).Health == 40, "The enemy should damage the vehicle before it gets repaired");
            for (int tick = 0; tick < 16; tick++) world.Step();
            NavigationChecks.Check(Pose(world, 2).Health > 40 && Mounts(world).Single(p => p.Entity.Value == 1).AutoCast,
                "Factory should repair an ally 3.5 cells beyond its footprint without an order");
        });
        test("Weapons: burst intervals and reload survive interruption without replenishing shots", () =>
        {
            var weapon = new WeaponDefinition(4096, 4096, 1, 10, 0, burst: 3, burstIntervalTicks: 2);
            using var world = new SimulationWorld([Armed("u", weapon), new("target", 1000)], 16384, 16384);
            world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 2);
            var fired = new List<long>();
            for (int t = 1; t <= 16; t++)
            {
                world.Step(t == 1 ? [Attack(world)] : []);
                foreach (var e in world.Events) if (e.Kind == WorldEventKind.WeaponFired) fired.Add(e.Tick);
            }
            NavigationChecks.Check(fired.SequenceEqual(new long[] {1, 3, 5, 15}), "Burst/reload cadence differs: " + string.Join(',', fired));
            var before = Mounts(world)[0];
            world.Step([new(17, 0, 1, CommandKind.Move, new(1), new(8192, 2048))]);
            NavigationChecks.Check(Mounts(world)[0].BurstRemaining == before.BurstRemaining && Mounts(world)[0].ReadyTick == before.ReadyTick, "Move replenished burst or reset reload");
        });
        test("Weapons: cannon is delayed and a moving target can dodge the fixed impact point", () =>
        {
            var cannon = new WeaponDefinition(8192, 8192, 40, 100, 0, projectile: ProjectileKind.Cannon, projectileSpeed: 256);
            SimulationWorld Create() { var w = new SimulationWorld([Armed("gun", cannon), new("target", 100)], 16384, 16384); w.AddInitialUnit(0, new(2048, 4096), 1); w.AddInitialUnit(1, new(7168, 4096), 2); return w; }
            using var still = Create(); still.Step([Attack(still)]); NavigationChecks.Check(Pose(still, 2).Health == 100 && still.ProjectileCount == 1, "Cannon became hitscan");
            for (int t = 0; t < 25; t++) still.Step(); NavigationChecks.Check(Pose(still, 2).Health == 60, "Stationary target escaped cannon");
            using var dodge = Create(); dodge.Step([Attack(dodge)]); dodge.Step([new(2, 0, 1, CommandKind.Move, new(2), new(7168, 12000))]);
            for (int t = 0; t < 25; t++) dodge.Step(); NavigationChecks.Check(Pose(dodge, 2).Health == 100 && dodge.ProjectileCount == 0, "Cannon followed target or guaranteed damage");
        });
        test("Weapons: fast cannon collision chooses the first intercepted body instead of the ordered target", () =>
        {
            using var world = new SimulationWorld([Armed("gun", new(8192, 8192, 20, 100, 0, projectile: ProjectileKind.Cannon, projectileSpeed: 4096)), new("body", 100)], 16384, 8192);
            world.AddInitialUnit(0, new(1024, 2048), 1); world.AddInitialUnit(1, new(7168, 2048), 2); world.AddInitialUnit(1, new(3072, 2048), 2);
            world.Step([Attack(world)]); world.Step();
            NavigationChecks.Check(Pose(world, 3).Health == 80 && Pose(world, 2).Health == 100, "Fast shot tunneled or damaged its target by ID");
        });
        test("Weapons: swept collision detects a crossing target and static edits stop a shot", () =>
        {
            var gun = Armed("gun", new(8192, 8192, 20, 1000, 0, projectile: ProjectileKind.Cannon, projectileSpeed: 4096));
            using var world = new SimulationWorld([gun, new("body", 100)], 16384, 8192, speedPerTick: 4096);
            world.AddInitialUnit(0, new(1024, 4096), 1); world.AddInitialUnit(1, new(9000, 4096), 2); world.AddInitialUnit(1, new(3072, 2048), 2);
            world.Step([Attack(world)]); world.Step([new(2, 0, 1, CommandKind.Move, new(3), new(3072, 6144))]);
            NavigationChecks.Check(Pose(world, 3).Health == 80 && Pose(world, 2).Health == 100, "Relative swept collision missed a crossing body");
            using var blocked = new SimulationWorld([gun, new("body", 100)], 16384, 8192, navigationGrid: new(16, 8));
            blocked.AddInitialUnit(0, new(1024, 2048), 1); blocked.AddInitialUnit(1, new(7168, 2048), 2); blocked.Step([Attack(blocked)]);
            blocked.Step([new(2, 0, 1, CommandKind.BlockCell, Position: new(3072, 2048))]);
            NavigationChecks.Check(blocked.ProjectileCount == 0 && Pose(blocked, 2).Health == 100, "Projectile crossed new static terrain");
        });
        test("Weapons: missile follows a maneuvering target and has a bounded travel budget", () =>
        {
            var missile = new WeaponDefinition(8192, 8192, 30, 1000, 0, projectile: ProjectileKind.Missile, projectileSpeed: 256, travelLimit: 8192);
            using var world = new SimulationWorld([Armed("launcher", missile), new("body", 100)], 32768, 16384);
            world.AddInitialUnit(0, new(1024, 4096), 1); world.AddInitialUnit(1, new(4096, 4096), 2);
            world.Step([Attack(world)]); world.Step([new(2, 0, 1, CommandKind.Move, new(2), new(4096, 8192))]);
            for (int t = 0; t < 35; t++) world.Step(); NavigationChecks.Check(Pose(world, 2).Health == 70, "Homing missile missed a slower moving target");
            using var escape = new SimulationWorld([Armed("launcher", missile), new("body", 100)], 32768, 16384, speedPerTick: 512);
            escape.AddInitialUnit(0, new(1024, 4096), 1); escape.AddInitialUnit(1, new(7168, 4096), 2); escape.Step([Attack(escape)]);
            escape.Step([new(2, 0, 1, CommandKind.Move, new(2), new(30000, 4096))]); bool expired = false;
            for (int t = 0; t < 40; t++) { escape.Step(); expired |= escape.Events.ToArray().Any(e => e.Kind == WorldEventKind.ProjectileExpired); }
            NavigationChecks.Check(expired && escape.ProjectileCount == 0 && Pose(escape, 2).Health == 100, "Missile chased beyond fuel limit");
        });
        test("Weapons: launched shots survive their source and homing handles destroyed targets", () =>
        {
            using var world = new SimulationWorld([Armed("gun", new(8192, 8192, 30, 1000, 0, projectile: ProjectileKind.Cannon)), new("body", 100)], 16384, 8192);
            world.AddInitialUnit(0, new(1024, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 2); world.Step([Attack(world)]);
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(1))]); for (int t = 0; t < 16; t++) world.Step();
            NavigationChecks.Check(Pose(world, 2).Health == 70, "Destroyed shooter erased a launched projectile");
            using var missile = new SimulationWorld([Armed("gun", new(8192, 8192, 30, 1000, 0, projectile: ProjectileKind.Missile)), new("body", 100)], 16384, 8192);
            missile.AddInitialUnit(0, new(1024, 2048), 1); missile.AddInitialUnit(1, new(4096, 2048), 2); missile.Step([Attack(missile)]);
            missile.Step([new(2, 0, 1, CommandKind.Destroy, new(2))]); NavigationChecks.Check(missile.ProjectileCount == 0, "Homing retained a missing target");
        });
        test("Weapons: armor matrix and circular grenade splash affect eligible targets only", () =>
        {
            var grenade = new WeaponDefinition(8192, 8192, 20, 1000, 0, projectile: ProjectileKind.Grenade, projectileSpeed: 1024,
                splashRadius: 1200, livingModifier: 150, mechanicalModifier: 20, buildingModifier: 200);
            using var world = new SimulationWorld([Armed("g", grenade), new("living", 100), new("machine", 100, armor: ArmorKind.Mechanical), new("building", 100, armor: ArmorKind.Building)], 16384, 8192);
            world.AddInitialUnit(0, new(1024, 2048), 1); world.AddInitialUnit(1, new(6144, 2048), 2);
            world.AddInitialUnit(2, new(6144, 2600), 2); world.AddInitialUnit(3, new(7000, 2048), 2);
            world.AddInitialUnit(1, new(6144, 1500), 1); world.AddInitialUnit(1, new(7500, 2048), 2);
            world.Step([Attack(world)]); for (int t = 0; t < 6; t++) world.Step();
            NavigationChecks.Check(Pose(world, 2).Health == 70 && Pose(world, 3).Health == 96 && Pose(world, 4).Health == 60 && Pose(world, 5).Health == 100 && Pose(world, 6).Health == 100, "AoE armor, radius or ally filtering differs");
        });
        test("Weapons: repair rejects enemies/living, heals allies, clamps HP and completes", () =>
        {
            var repair = new WeaponDefinition(2048, 2048, 25, 2, 0, effect: WeaponEffect.Repair, livingModifier: 0);
            var gun = new WeaponDefinition(4096, 4096, 60, 1000, 0);
            using var world = new SimulationWorld([Armed("engineer", repair), new("machine", 100, armor: ArmorKind.Mechanical), Armed("enemy", gun), new("living", 100)], 16384, 8192);
            world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 1); world.AddInitialUnit(2, new(7168, 2048), 2); world.AddInitialUnit(3, new(2048, 4096), 1);
            world.Step([Attack(world, 3, 2)]); NavigationChecks.Check(Pose(world, 2).Health == 40, "Damage fixture failed");
            world.Step([new(2, 0, 1, CommandKind.RepairTarget, new(1), TargetEntity: new(3))]); NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder), "Repair accepted an enemy");
            world.Step([new(3, 0, 1, CommandKind.RepairTarget, new(1), TargetEntity: new(4))]); NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder), "Repair accepted living armor");
            world.Step([new(4, 0, 1, CommandKind.RepairTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(Pose(world, 2).Health == 65 && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Repaired), "Ally repair failed");
            for (int t = 0; t < 6; t++) world.Step(); NavigationChecks.Check(Pose(world, 2).Health == 100 && Pose(world, 1).Order.Phase == OrderPhase.Idle, "Repair exceeded HP or did not finish");
        });
        test("Weapons: simultaneous lethal damage cannot be undone by repair", () =>
        {
            using var world = new SimulationWorld([Armed("repair", new(4096, 4096, 1000, 1, 0, effect: WeaponEffect.Repair)), new("ally", 200, armor: ArmorKind.Mechanical), Armed("enemy", new(4096, 4096, 100, 1, 0))], 16384, 8192);
            world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 1); world.AddInitialUnit(2, new(6144, 2048), 2);
            world.Step([Attack(world, 3, 2)]); world.Step([new(2, 0, 1, CommandKind.RepairTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(world.Count == 2 && !world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Repaired), "Healing resurrected a lethal hit");
        });
        test("Weapons: independent mounts fire while maneuvering and stationary mounts cancel windup", () =>
        {
            var type = new UnitDefinition("dual", 1000, ["idle", "move", "fire", "missiles"], weapons: [
                new("cannon", new(8192, 8192, 1, 5, 0), "fire", true), new("rocket", new(8192, 8192, 1, 11, 0), "missiles", true)]);
            using var world = new SimulationWorld([type, new("target", 1000)], 16384, 16384);
            world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 2);
            int[] shots = new int[2];
            for (int t = 1; t <= 13; t++)
            {
                world.Step(t == 1 ? [new(1, 0, 1, CommandKind.Move, new(1), new(2048, 12000))] : []);
                foreach (var e in world.Events) if (e.Kind == WorldEventKind.WeaponFired) shots[e.Mount]++;
            }
            NavigationChecks.Check(shots.SequenceEqual(new[] {3, 2}) && world.MovingCount == 1 && Mounts(world)[0].ReadyTick != Mounts(world)[1].ReadyTick, "Mount clocks shared state or Move blocked firing");
            using var pedestrian = new SimulationWorld([Armed("soldier", new(8192, 8192, 10, 20, 3)), new("t", 1000)], 16384, 16384);
            pedestrian.AddInitialUnit(0, new(2048, 2048), 1); pedestrian.AddInitialUnit(1, new(4096, 2048), 2); pedestrian.Step([Attack(pedestrian)]);
            pedestrian.Step([new(2, 0, 1, CommandKind.Move, new(1), new(2048, 12000))]); for (int t = 0; t < 5; t++) pedestrian.Step();
            NavigationChecks.Check(Pose(pedestrian, 2).Health == 1000, "Moving stationary mount fired");
        });
        test("Weapons: splash friendly fire is explicit and does not allow hostile repair", () =>
        {
            var grenade = new WeaponDefinition(4096, 4096, 10, 100, 0, projectile: ProjectileKind.Grenade, projectileSpeed: 4096, splashRadius: 1024, friendlyFire: true);
            using var world = new SimulationWorld([Armed("g", grenade), new("t", 100)], 16384, 8192);
            world.AddInitialUnit(0, new(1024, 2048), 1); world.AddInitialUnit(1, new(4096, 2048), 2); world.AddInitialUnit(1, new(4096, 2600), 1);
            world.Step([Attack(world)]); world.Step();
            NavigationChecks.Check(Pose(world, 2).Health == 90 && Pose(world, 3).Health == 90, "Explicit friendly fire failed");
        });
        test("Weapons: immobile units reject move/group/yield and attack without chasing", () =>
        {
            var onlyBuildings = UnitCatalog.Load([new("u", "u: {Health: {HP: 1}, Movement: {Locomotor: infantry, Enabled: false}}")], LocomotionChecks.Catalog());
            NavigationChecks.Reject<ArgumentException>(() => DiagnosticScenario.Create(onlyBuildings, 1));
            NavigationChecks.Reject<ArgumentException>(() => NavigationScenario.Create(onlyBuildings, 1));
            using var world = new SimulationWorld([Armed("bunker", new(2048, 4096, 1, 10, 0), movable: false), new("unit", 100)], 16384, 8192, navigationGrid: new(16, 8));
            world.AddInitialUnit(0, new(4096, 4096), 1); world.AddInitialUnit(1, new(6144, 4096), 2);
            var position = Pose(world, 1).Current; world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(8192, 4096))]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder) && Pose(world, 1).Current == position, "Bunker moved");
            world.Step([Attack(world)]); NavigationChecks.Check(Pose(world, 2).Health == 99, "Bunker could not attack");
            world.Step([new(3, 0, 1, CommandKind.MoveGroup, Position: new(12000, 4096))]);
            for (int t = 0; t < 20; t++) world.Step(); NavigationChecks.Check(Pose(world, 1).Current == position, "Group/yield moved bunker");
        });
        test("Weapons: pinned weapon/projectile frames survive packed removal and skip publication", () =>
        {
            using var world = new SimulationWorld([Armed("u", new(8192, 8192, 10, 1000, 0, projectile: ProjectileKind.Missile)), new("t", 100)], 16384, 8192);
            world.AddInitialUnit(0, new(1024, 2048), 1); world.AddInitialUnit(1, new(6144, 2048), 2); world.Step([Attack(world)]);
            var exchange = new FrameExchange(); exchange.TryPublish(world); using var old = exchange.AcquireLatest()!;
            var original = old.Projectiles[0]; var mount = old.Weapons[0];
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(1))]); exchange.TryPublish(world); using var current = exchange.AcquireLatest()!;
            NavigationChecks.Check(old.Projectiles[0] == original && old.Weapons[0] == mount && current.Weapons.Length == 0 && current.Projectiles.Length == 1, "Pinned combat frame mutated or packed removal lost projectile");
            current.Dispose(); NavigationChecks.Reject<ObjectDisposedException>(() => { _ = current.Projectiles.Length; });
        });
        test("Weapons: canonical battle matches workers and reversed navigation scheduling", () =>
        {
            string Run(int workers)
            {
                using var world = new SimulationWorld([Armed("gun", new(4096, 6144, 7, 7, 1, projectile: ProjectileKind.Missile, burst: 2)), new("body", 100)], 16384, 8192, navigationGrid: new(16, 8), navigationWorkers: workers, reverseNavigationJobs: workers > 1);
                world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(1, new(6144, 2048), 2);
                for (int t = 1; t <= 80; t++) world.Step(t == 1 ? [Attack(world)] : []);
                return world.Report().StateHash;
            }
            NavigationChecks.Check(Run(1) == Run(4), "Battle hash depends on worker execution");
        });
        test("Weapons: 10000 active shooters use spatial targets and measured combat ticks", () =>
        {
            using var world = new SimulationWorld([Armed("u", new(2048, 2048, 1, 3, 0))], 1253376, 327680, initialCapacity: 10000);
            for (int i = 0; i < 5000; i++)
            { int x = (i % 100) * 12288 + 2048, y = (i / 100) * 6144 + 2048; world.AddInitialUnit(0, new(x, y), 1); world.AddInitialUnit(0, new(x + 1024, y), 2); }
            for (int t = 0; t < 8; t++) world.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew(); int fired = 0;
            for (int t = 0; t < 16; t++) { world.Step(); foreach (var e in world.Events) if (e.Kind == WorldEventKind.WeaponFired) fired++; }
            timer.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            NavigationChecks.Check(fired > 40000 && world.Count == 10000, "Large battle failed to acquire spatial targets");
            Console.WriteLine("OPENRTS_COMBAT_SAMPLE:" + System.Text.Json.JsonSerializer.Serialize(new { Entities = world.Count, Ticks = 16, Shots = fired, StepMilliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = bytes, State = world.Report() }));
        });
        test("Weapons: 10000 live projectiles retain bounded spatial collision work", () =>
        {
            using var world = new SimulationWorld([Armed("u", new(2048, 2048, 1, 1000, 0, projectile: ProjectileKind.Missile, projectileSpeed: 16))], 1253376, 327680, initialCapacity: 10000);
            for (int i = 0; i < 5000; i++)
            { int x = (i % 100) * 12288 + 2048, y = (i / 100) * 6144 + 2048; world.AddInitialUnit(0, new(x, y), 1); world.AddInitialUnit(0, new(x + 1024, y), 2); }
            for (int t = 0; t < 8; t++) world.Step();
            var timer = new System.Diagnostics.Stopwatch(); long bytes = GC.GetAllocatedBytesForCurrentThread(); timer.Start();
            for (int t = 0; t < 16; t++) world.Step();
            timer.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            NavigationChecks.Check(world.ProjectileCount == 10000, "Long-flight salvo lost projectiles");
            Console.WriteLine("OPENRTS_PROJECTILE_SAMPLE:" + System.Text.Json.JsonSerializer.Serialize(new { Entities = world.Count, Projectiles = world.ProjectileCount, Ticks = 16, StepMilliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = bytes, State = world.Report() }));
        });
    }
}
