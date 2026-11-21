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
        test("Weapons: MinRange blocks targets inside the minimum and allows the exact boundary", () =>
        {
            const int origin = 2048, minimum = 2048;
            var weapon = new WeaponDefinition(4096, 4096, 25, 100, 2, minRange: minimum);
            int boundary = origin + minimum + 2 * BodyShape.Default.HalfWidth;
            foreach (bool explicitAttack in new[] { false, true })
            {
                using var world = new SimulationWorld([Armed("gun", weapon, movable: !explicitAttack), new("target", 100)], 16384, 8192);
                world.AddInitialUnit(0, new(origin, 4096), 1);
                var target = world.AddInitialUnit(1, new(boundary - 1, 4096), 2);
                world.Step(explicitAttack ? [Attack(world)] : []);
                for (int tick = 0; tick < 4; tick++) world.Step();
                NavigationChecks.Check(Pose(world, 2).Health == 100 && Pose(world, 1).Current.X == origin &&
                    Mounts(world)[0].WindupEndTick == 0, "A target in the dead zone started a windup or made the gun approach.");
                world.Step([new(6, 0, 2, CommandKind.Move, target, new(boundary, 4096))]);
                for (int tick = 0; tick < 8; tick++) world.Step();
                NavigationChecks.Check(Pose(world, 2).Health == 75, "The weapon should fire at exactly MinRange.");
            }
        });
        test("Weapons: an attack order withdraws from MinRange and Hold Position cancels the withdrawal", () =>
        {
            const int minimum = 3072;
            var weapon = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Cannon,
                trajectory: ProjectileTrajectory.Arc, minRange: minimum);
            var start = new WorldPosition(5632, 4608);
            var enemy = start with { X = start.X + 1024 };
            SimulationWorld Create(NavigationGrid? grid = null)
            {
                var world = new SimulationWorld([Armed("artillery", weapon), new("target", 100)],
                    16384, 10240, navigationGrid: grid);
                world.AddInitialUnit(0, start, 1);
                world.AddInitialUnit(1, enemy, 2);
                return world;
            }

            foreach (var grid in new NavigationGrid?[] { null, new(16, 10, [new(4, 3), new(4, 4), new(4, 5)]) })
            {
                using var world = Create(grid);
                world.Step([Attack(world)]);
                using var restored = SimulationWorld.RestoreSnapshot(world.CaptureSnapshot(), () => Create(grid));
                for (int tick = 0; tick < 200 && Pose(world, 2).Health == 100; tick++)
                {
                    world.Step();
                    restored.Step();
                    NavigationChecks.Check(world.Report() == restored.Report(), "Restoring the retreat changed the next tick.");
                }
                var position = Pose(world, 1).Current;
                long dx = position.X - enemy.X, dy = position.Y - enemy.Y;
                long edge = minimum + 2L * BodyShape.Default.HalfWidth;
                NavigationChecks.Check(Pose(world, 2).Health == 75 && dx * dx + dy * dy >= edge * edge,
                    "The artillery should move to a reachable firing position and fire.");
            }

            var blocked = from y in Enumerable.Range(0, 10) from x in Enumerable.Range(0, 16)
                where y != 4 || x is not (5 or 6)
                select new GridCell(x, y);
            using var trapped = Create(new(16, 10, blocked));
            trapped.Step([Attack(trapped)]);
            for (int tick = 0; tick < 16; tick++) trapped.Step();
            NavigationChecks.Check(Pose(trapped, 1).Current == start && Pose(trapped, 2).Health == 100,
                "There was no reachable firing position in the enclosure.");

            using var held = Create();
            held.Step([new(1, 0, 1, CommandKind.HoldPosition, new(1))]);
            for (int tick = 0; tick < 12; tick++) held.Step();
            NavigationChecks.Check(Pose(held, 1).Current == start && Pose(held, 2).Health == 100, "Hold Position caused a withdrawal.");
            held.Step([Attack(held)]);
            var stopped = Pose(held, 1).Current;
            NavigationChecks.Check(stopped != start, "Attack should start the withdrawal.");
            held.Step([new(held.Tick + 1, 0, 2, CommandKind.HoldPosition, new(1))]);
            for (int tick = 0; tick < 25; tick++) held.Step();
            NavigationChecks.Check(Pose(held, 1).Current == stopped && Pose(held, 2).Health == 100,
                "Hold Position should cancel a withdrawal in progress.");
        });
        test("Weapons: MinRange applies to manual ground-target abilities", () =>
        {
            const int minimum = 2048;
            var weapon = new WeaponDefinition(4096, 4096, 25, 100, 0, projectile: ProjectileKind.Cannon,
                splashRadius: 512, trajectory: ProjectileTrajectory.Arc, minRange: minimum);
            foreach (bool atBoundary in new[] { false, true })
            {
                var gun = new UnitDefinition("gun", 100, ["idle", "move", "fire"], weapon: weapon,
                    body: BodyShape.None, abilities: [new("bombard", "main", GroundTarget: true)]);
                using var world = new SimulationWorld([gun], 16384, 8192);
                var start = new WorldPosition(4096, 4096);
                var entity = world.AddInitialUnit(0, start, 1);
                var aim = start with { X = start.X + minimum - (atBoundary ? 0 : 1) };
                world.Step([new(1, 0, 1, CommandKind.UseAbility, entity, aim, Action: "bombard")]);
                for (int tick = 0; tick < 4; tick++) world.Step();
                NavigationChecks.Check(world.ProjectileCount == (atBoundary ? 1 : 0) && Pose(world, 1).Current == start,
                    "The ground-target ability should honor the minimum firing distance.");
            }
        });
        test("Weapons: Light armor can override damage and otherwise uses the Mechanical modifier", () =>
        {
            WeaponDefinition Load(string fields) => WeaponCatalog.Load([new("light", $$"""
                rifle:
                  Weapon: {MinRange: 512, Damage: 100, ReloadTicks: 100, WindupTicks: 0, Versus: {Mechanical: 10, Building: 5{{fields}} } }
                """)]).Weapons["rifle"];
            var fallback = Load("");
            var weapon = Load(", Light: 35");
            NavigationChecks.Check(weapon.MinRange == 512 && fallback.Modifier(ArmorKind.Light) == 10 && weapon.Modifier(ArmorKind.Light) == 35,
                "Light must use its own modifier or fall back to Mechanical.");
            NavigationChecks.Reject<ArgumentException>(() => new WeaponDefinition(2048, 4096, 10, 10, minRange: 2049));
            NavigationChecks.Reject<ArgumentException>(() => new WeaponDefinition(2048, 4096, 10, 10, minRange: -1));
            foreach (var (armor, health) in new[] { (ArmorKind.Light, 65), (ArmorKind.Mechanical, 90) })
            {
                using var world = new SimulationWorld([Armed("rifle", weapon), new("target", 100, armor: armor)], 8192, 8192);
                world.AddInitialUnit(0, new(2048, 4096), 1);
                world.AddInitialUnit(1, new(4096, 4096), 2);
                world.Step([Attack(world)]);
                NavigationChecks.Check(Pose(world, 2).Health == health, "Damage did not use the configured armor modifier.");
            }
        });
        test("Weapons: artillery scatter and salvo fan keep their sampled destinations through snapshot and target removal", () =>
        {
            const int radius = 680;
            var weapon = new WeaponDefinition(16000, 16000, 25, 1000, 0, projectile: ProjectileKind.Missile,
                projectileSpeed: 440, burst: 4, burstIntervalTicks: 2, trajectory: ProjectileTrajectory.Ballistic,
                homing: false, scatterRadius: radius, launchFan: 500);
            var target = new WorldPosition(12288, 8192);
            SimulationWorld Create()
            {
                var world = new SimulationWorld([Armed("salvo", weapon, movable: false), new("target", 100)], 16384, 16384);
                world.AddInitialUnit(0, new(2048, 8192), 1);
                world.AddInitialUnit(1, target, 2);
                return world;
            }
            using var world = Create();
            var points = new List<WorldPosition>();
            var lanes = new List<int>();
            var impacts = new List<WorldPosition>();
            var shots = new ProjectilePose[4];
            world.Step([Attack(world)]);
            using var restored = SimulationWorld.RestoreSnapshot(world.CaptureSnapshot(), Create);
            for (int tick = 1; tick <= 90; tick++)
            {
                foreach (var e in world.Events)
                {
                    if (e.Kind == WorldEventKind.WeaponFired) points.Add(e.Position);
                    if (e.Kind == WorldEventKind.ProjectileImpact) impacts.Add(e.Position);
                }
                if (tick is 2 or 4 or 6 or 8)
                {
                    world.CopyProjectilePoses(shots);
                    var shot = shots[world.ProjectileCount - 1];
                    lanes.Add(shot.Current.Y - shot.Previous.Y);
                }
                WorldCommand[] commands = tick == 8 ? [new(9, 0, 2, CommandKind.Remove, new(2))] : [];
                world.Step(commands); restored.Step(commands);
                NavigationChecks.Check(world.Report() == restored.Report(), "The salvo diverged after recovery.");
            }
            NavigationChecks.Check(lanes.Count == 4 && lanes.Zip(lanes.Skip(1)).All(pair => pair.First < pair.Second) &&
                lanes[0] < 0 && lanes[^1] > 0, "The first flight steps did not form a fan.");
            NavigationChecks.Check(points.Distinct().Count() == 4 && points.All(p =>
                (p.X - target.X) * (p.X - target.X) + (p.Y - target.Y) * (p.Y - target.Y) <= radius * radius),
                "The salvo did not select four distinct points inside the spread disk.");
            NavigationChecks.Check(impacts.Count == 4 && impacts.ToHashSet().SetEquals(points),
                "A missile stopped following its original sampled point after the target was removed.");
        });
        test("Weapons: Trajectory chooses direct or planar flight and supports legacy YAML", () =>
        {
            WeaponDefinition Load(string fields) => WeaponCatalog.Load([new("test weapon", $"test: {{Weapon: {{{fields}}}}}")]).Weapons["test"];
            NavigationChecks.Check(Load("Trajectory: Direct").Trajectory == ProjectileTrajectory.Direct &&
                State(Load("Trajectory: Direct")).SequenceEqual(State(Load("SpatialTrajectory: true"))) &&
                State(Load("Trajectory: Planar")).SequenceEqual(State(Load("SpatialTrajectory: false"))) &&
                Load("").Trajectory == ProjectileTrajectory.Planar, "Trajectory did not normalize to the existing weapon modes.");
            NavigationChecks.Reject<ContentException>(() => Load("Trajectory: Direct, SpatialTrajectory: false"));
            NavigationChecks.Reject<ContentException>(() => Load("Trajectory: Arc"));
            var ballistic = Load("Projectile: Missile, Trajectory: Ballistic, TrajectoryHeight: 3072, Homing: false");
            NavigationChecks.Check(ballistic.TrajectoryHeight == 3072 && !ballistic.Homing && ballistic.SpatialTrajectory,
                "Curved trajectory settings did not survive YAML.");
            NavigationChecks.Reject<ContentException>(() => Load("Projectile: Cannon, Trajectory: Cruise, Homing: true"));

            static byte[] State(WeaponDefinition weapon)
            {
                using var bytes = new MemoryStream();
                using var writer = new BinaryWriter(bytes);
                weapon.WriteState(writer);
                return bytes.ToArray();
            }
        });
        test("Weapons: long-range artillery acquires and hits at the configured range", () =>
        {
            const int range = 80 * WorldPosition.UnitsPerCell;
            WeaponDefinition Load(int distance, string limits = "") => WeaponCatalog.Load([new("long range", $$"""
                howitzer:
                  Weapon: {Projectile: Cannon, Trajectory: Arc, Range: {{distance}}, Speed: 2048, Damage: 25, ReloadTicks: 300, WindupTicks: 0{{limits}} }
                """)]).Weapons["howitzer"];
            var weapon = Load(range);
            NavigationChecks.Check(weapon.AcquireRange == range && weapon.TravelLimit == 2 * range &&
                Load(4096).TravelLimit == 16384 && Load(4096).AcquireRange == 8192,
                "Long-range defaults should expand while existing default values stay compatible.");
            NavigationChecks.Reject<ContentException>(() => Load(WeaponDefinition.MaximumRange + 1));
            NavigationChecks.Reject<ContentException>(() => Load(range, $", AcquireRange: {range - 1}"));
            NavigationChecks.Reject<ContentException>(() => Load(range, $", TravelLimit: {range - 1}"));
            NavigationChecks.Check(Load(WeaponDefinition.MaximumRange).TravelLimit == WeaponDefinition.MaximumTravelLimit,
                "The world-scale range limit must have a valid default travel budget.");

            using var world = new SimulationWorld([Armed("howitzer", weapon, movable: false), new("target", 100)], 128 * 1024, 8192);
            const int originX = 2048;
            world.AddInitialUnit(0, new(originX, 4096), 1);
            int targetX = originX + range + 2 * BodyShape.Default.HalfWidth;
            world.AddInitialUnit(1, new(targetX + 1, 4096), 2);
            for (int tick = 0; tick < 8; tick++) world.Step();
            NavigationChecks.Check(world.ProjectileCount == 0, "A target beyond the configured range should not be acquired.");
            world.Step([new(world.Tick + 1, 0, 2, CommandKind.Move, new(2), new(targetX, 4096))]);
            bool hit = false;
            for (int tick = 0; tick < 90; tick++)
            {
                world.Step();
                hit |= world.Events.ToArray().Any(e => e.Kind == WorldEventKind.ProjectileImpact && e.TargetEntity == new EntityId(2));
            }
            NavigationChecks.Check(hit && Pose(world, 2).Health == 75, "Artillery should acquire a target at its configured range and have enough travel for the arc.");
        });
        test("Weapons: flight profiles have different apexes, hit targets and survive snapshot restore", () =>
        {
            long arcApex = Sample(ProjectileTrajectory.Arc).ApexTick;
            var ballistic = Sample(ProjectileTrajectory.Ballistic);
            var cruise = Sample(ProjectileTrajectory.Cruise);
            NavigationChecks.Check(ballistic.ApexTick < arcApex && cruise.ShelfTicks >= 5,
                $"Unexpected profiles: arc apex {arcApex}, ballistic apex {ballistic}, cruise shelf {cruise}.");

            static (long ApexTick, int ShelfTicks) Sample(ProjectileTrajectory trajectory)
            {
                const int height = 2048;
                var weapon = new WeaponDefinition(8192, 8192, 25, 1000, 0, projectile: ProjectileKind.Missile,
                    projectileSpeed: 256, trajectory: trajectory, trajectoryHeight: height, homing: false);
                SimulationWorld Create()
                {
                    var world = new SimulationWorld([Armed("shooter", weapon, movable: false), new("target", 100)], 16384, 16384);
                    world.AddInitialUnit(0, new(2048, 4096), 1);
                    world.AddInitialUnit(1, new(8192, 4096), 2);
                    return world;
                }
                using var world = Create();
                world.Step([Attack(world)]);
                var poses = new ProjectilePose[1];
                SimulationWorld? restored = null;
                int peak = 0, shelf = 0;
                long apex = 0;
                try
                {
                    while (world.ProjectileCount > 0 && world.Tick < 80)
                    {
                        world.CopyProjectilePoses(poses);
                        int z = poses[0].Current.Elevation;
                        if (z > peak) { peak = z; apex = world.Tick; }
                        if (z == height + 128) shelf++;
                        if (world.Tick == 6) restored = SimulationWorld.RestoreSnapshot(world.CaptureSnapshot(), Create);
                        world.Step();
                        if (restored is not null)
                        {
                            restored.Step();
                            NavigationChecks.Check(restored.Report() == world.Report(), "Curved flight diverged after snapshot restore.");
                        }
                    }
                }
                finally { restored?.Dispose(); }
                NavigationChecks.Check(peak >= 2000 && Pose(world, 2).Health == 75 && world.ProjectileCount == 0,
                    $"{trajectory} did not climb and hit its target.");
                return (apex, shelf);
            }
        });
        test("Weapons: a curved shot can clear terrain or impact the wall during the same flight profile", () =>
        {
            CheckHeight(4096, hitsTarget: true);
            CheckHeight(512, hitsTarget: false);
            CheckHeight(1700, hitsTarget: true, slope: true, y: 4224);
            CheckHeight(1700, hitsTarget: false, slope: true, y: 4992);

            static void CheckHeight(int height, bool hitsTarget, bool slope = false, int y = 4096)
            {
                var grid = new NavigationGrid(16, 8,
                    surface: Enumerable.Range(0, 128).Select(i => new SurfaceSample(i % 16 == 5 ? slope ? 2048 : 3072 : 0)),
                    rises: slope ? Enumerable.Range(0, 128).Select(i => i % 16 == 5 ? new SurfaceRise(0, 3072) : default) : null);
                var weapon = new WeaponDefinition(8192, 8192, 25, 1000, 0, projectile: ProjectileKind.Cannon,
                    projectileSpeed: 256, trajectory: ProjectileTrajectory.Arc, trajectoryHeight: height);
                using var world = new SimulationWorld([Armed("howitzer", weapon, movable: false), new("target", 100)], 16384, 8192,
                    navigationGrid: grid);
                world.AddInitialUnit(0, new(2048, y), 1);
                world.AddInitialUnit(1, new(8192, y), 2);
                world.Step([Attack(world)]);
                WorldEvent? impact = null;
                while (world.ProjectileCount > 0 && world.Tick < 90)
                {
                    world.Step();
                    foreach (var e in world.Events) if (e.Kind == WorldEventKind.ProjectileImpact) impact = e;
                }
                NavigationChecks.Check(impact.HasValue && (hitsTarget
                    ? impact.Value.TargetEntity == new EntityId(2) && Pose(world, 2).Health == 75
                    : impact.Value.TargetEntity == default && Math.Abs(impact.Value.Position.X - 5120) <= 1 && Pose(world, 2).Health == 100),
                    $"Unexpected curved projectile impact at height {height}, slope={slope}, y={y}: {impact}.");
            }
        });
        test("Weapons: guided ballistic and cruise missiles follow a moving target, unguided rounds keep their point", () =>
        {
            foreach (var trajectory in new[] { ProjectileTrajectory.Ballistic, ProjectileTrajectory.Cruise })
            {
                NavigationChecks.Check(Fire(trajectory, true) == 75 && Fire(trajectory, false) == 100,
                    $"{trajectory} did not distinguish guided from fixed aim.");
            }

            static int Fire(ProjectileTrajectory trajectory, bool homing)
            {
                var weapon = new WeaponDefinition(8192, 8192, 25, 1000, 0, projectile: ProjectileKind.Missile,
                    projectileSpeed: 256, travelLimit: 24000, trajectory: trajectory, homing: homing);
                using var world = new SimulationWorld([Armed("launcher", weapon, movable: false), new("target", 100)], 16384, 16384);
                world.AddInitialUnit(0, new(2048, 4096), 1);
                world.AddInitialUnit(1, new(8192, 4096), 2);
                world.Step([Attack(world)]);
                world.Step([new(2, 0, 2, CommandKind.Move, new(2), new(8192, 7168))]);
                while (world.ProjectileCount > 0 && world.Tick < 110) world.Step();
                return Pose(world, 2).Health;
            }
        });
        test("Weapons: slow spatial projectiles advance within their budget and hit diagonal targets", () =>
        {
            foreach (var offset in new WorldPosition[] { new(1, 1, 1), new(256, 256, 256), new(-256, -256, 256) })
            {
                var weapon = new WeaponDefinition(4096, 4096, 10, 1000000, 0, projectile: ProjectileKind.Cannon,
                    projectileSpeed: 1, spatialTrajectory: true);
                using var world = new SimulationWorld([
                    new("shooter", 100, ["idle", "move", "fire"], body: BodyShape.None, weapon: weapon),
                    new("target", 100, body: BodyShape.None)], 16384, 16384);
                var start = new WorldPosition(2048, 2048);
                var shooter = world.AddInitialUnit(0, start, 1);
                var target = world.AddInitialUnit(1, new(start.X + offset.X, start.Y + offset.Y, offset.Elevation), 2);
                world.Step([new(1, 0, 1, CommandKind.AttackTarget, shooter, TargetEntity: target)]);
                var shots = new ProjectilePose[1];
                var previous = start;
                bool hit = false;
                for (int i = 0; i < 800 && !hit; i++)
                {
                    world.Step();
                    WorldPosition current;
                    if (world.ProjectileCount != 0)
                    {
                        world.CopyProjectilePoses(shots);
                        current = shots[0].Current;
                        NavigationChecks.Check(current != previous, "A spatial projectile spent fuel without moving");
                    }
                    else
                    {
                        var impact = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.ProjectileImpact);
                        current = impact.Position;
                        hit = impact.TargetEntity == target;
                    }
                    long x = (long)current.X - previous.X, y = (long)current.Y - previous.Y, z = (long)current.Elevation - previous.Elevation;
                    NavigationChecks.Check(x * x + y * y + z * z <= 1, "Rounding moved a spatial projectile beyond its one-unit budget");
                    previous = current;
                }
                NavigationChecks.Check(hit && Pose(world, target.Value).Health == 90, "A slow spatial projectile failed to reach its diagonal target");
            }
        });

        test("Weapons: named YAML mounts validate references and canonical gameplay fingerprints", () =>
        {
            var catalog = LocomotionChecks.Units();
            NavigationChecks.Check(catalog.Units.Single(u => u.Id == "tank").Weapons.Count == 2 && catalog.Units.Single(u => u.Id == "engineer").Weapon!.Effect == WeaponEffect.Repair, "Missing sample mounts");
            var weapons = LocomotionChecks.Weapons();
            var fixture = LocomotionChecks.Fixture("weapons.yaml");
            var altered = WeaponCatalog.Load([fixture with { Text = fixture.Text.Replace("Burst: 3", "Burst: 4", StringComparison.Ordinal) }]);
            NavigationChecks.Check(catalog.Summary != LocomotionChecks.Units(weapons: altered).Summary, "Burst excluded from fingerprint");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("u", "u: {Health: {HP: 1}, Armaments: {Mounts: {a: {Weapon: absent}}}}")], weapons: weapons));
            NavigationChecks.Reject<ContentException>(() => WeaponCatalog.Load([new("w", "w: {Weapon: {Burst: 0}}") ]));
            NavigationChecks.Reject<ContentException>(() => WeaponCatalog.Load([new("w", "w: {Weapon: {Effect: Repair, Projectile: Missile}}") ]));
            NavigationChecks.Reject<ArgumentException>(() => Armed("bad", new(2048, 2048, 10, 2), armor: (ArmorKind)99));
        });
        test("Weapons: factory repairs automatically beyond engineer range", () =>
        {
            var catalog = UnitCatalog.Load([LocomotionChecks.Fixture("repair.yaml")], weapons: LocomotionChecks.Weapons());
            var factory = catalog.Units.Single(u => u.Id == "factory");
            var engineer = catalog.Units.Single(u => u.Id == "engineer");
            NavigationChecks.Check(factory.Weapon!.Range > engineer.Weapon!.Range &&
                engineer.Abilities.Single(a => a.Id == "repair").AutoCast, "Repair fixture must have distinct ranges");

            var enemy = Armed("enemy", new(8192, 8192, 60, 1000, 0));
            var target = new UnitDefinition("vehicle", 100, armor: ArmorKind.Mechanical);
            using var world = new SimulationWorld([factory, enemy, target], 24000, 16000);
            world.AddInitialUnit(0, new(7680, 8192), 1);
            world.AddInitialUnit(2, new(1536, 8192), 1);
            world.AddInitialUnit(1, new(1536, 12288), 2);
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
