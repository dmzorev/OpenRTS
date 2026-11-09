using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class AviationChecks
{
    private static UnitDefinition Aircraft(FlightDefinition? flight = null, WeaponDefinition? weapon = null, string id = "aircraft") =>
        new(id, 200, ["idle", "move", "fire"], armor: ArmorKind.Mechanical,
            flight: flight ?? new(), weapons: weapon is null ? [] : [new("main", weapon, FireWhileMoving: true)]);
    private static UnitDefinition Base(int slots = 1, bool runway = false) => new("base", 1000, movable: false,
        footprint: new(8, 8, from y in Enumerable.Range(0, 8) from x in Enumerable.Range(0, 8) select new FootprintCell(x, y, new(false, false), true)),
        airbase: new(Enumerable.Range(0, slots).Select(i => new WorldPosition((i % 2 == 0 ? -1 : 1) * 2048, i / 2 * 2048)), reloadTicks: 2,
            runwayStart: runway ? new(0, 2048) : null, runwayEnd: runway ? new(0, -2048) : null, keepsParking: runway));
    private static EntityPose Pose(SimulationWorld w, EntityId id) => Poses(w).Single(p => p.Id == id);
    private static void Step(SimulationWorld w, int ticks) { for (int i = 0; i < ticks; i++) w.Step(); }
    public static void Run(Action<string, Action> test)
    {
        test("Aviation: categories are extensible, canonical and validated before use", () =>
        {
            var a = new TargetCategories(["submarine", "water", "custom.drone"]);
            NavigationChecks.Check(a.Intersects(new(["custom.drone"])), "A game-defined category was lost");
            NavigationChecks.Reject<ArgumentException>(() => new TargetCategories(["air", "air"]));
            var selective = new WeaponDefinition(4096, 4096, 10, 1, targets: new(["air"]), excludedTargets: new(["helicopter"]));
            NavigationChecks.Check(selective.CanTarget(new(["air", "airplane"])) && !selective.CanTarget(new(["air", "helicopter"])), "Category exclusion did not override inclusion");
            NavigationChecks.Reject<ArgumentException>(() => new FlightDefinition(speed: 0));
            var c = UnitCatalog.Load([new("air", "u: {Health: {HP: 100}, Aircraft: {Categories: [air, helicopter]}, Targetable: {Categories: [ground, vehicle]}}")]);
            NavigationChecks.Check(c.Units.Single().Flight!.Categories.Names.SequenceEqual(["air", "helicopter"]), "YAML flight categories");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("air", "u: {Health: {HP: 100}, Aircraft: {Speed: 0}}") ]));
        });
        test("Aviation: hover crosses blocked terrain without blocking the ground or changing identity", () =>
        {
            var blocked = Enumerable.Range(0, 24).Select(y => new GridCell(12, y));
            using var w = new SimulationWorld([Aircraft(), new("ground", 100)], 24576, 24576, navigationGrid: new(24, 24, blocked));
            var air = w.AddInitialUnit(0, new(4096, 8192, 1536), 1);
            var ground = w.AddInitialUnit(1, new(4096, 8192), 1);
            w.Step([new(1, 0, 1, CommandKind.Move, air, new(20480, 8192)), new(1, 0, 2, CommandKind.Move, ground, new(8192, 8192))]);
            Step(w, 150);
            NavigationChecks.Check(Pose(w, air).Current == new WorldPosition(20480, 8192, 1536), "Aircraft followed the ground path or failed to hover");
            NavigationChecks.Check(Pose(w, ground).Current.X == 8192 && w.Count == 2, "Aircraft blocked a ground actor");
        });
        test("Aviation: landing waits for an occupied body and can be cancelled without teleporting", () =>
        {
            using var w = new SimulationWorld([Aircraft(), new("ground", 100)], 24576, 24576, navigationGrid: new(24, 24));
            var air = w.AddInitialUnit(0, new(8192, 8192, 1536), 1);
            w.AddInitialUnit(1, new(8192, 8192), 1);
            w.Step([new(1, 0, 1, CommandKind.Land, air, new(8192, 8192))]); Step(w, 40);
            NavigationChecks.Check(Pose(w, air).Current.Elevation >= 512, "Landed inside another body");
            int height = Pose(w, air).Current.Elevation;
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Move, air, new(16384, 8192))]);
            NavigationChecks.Check(Math.Abs(Pose(w, air).Current.Elevation - height) <= 96, "Cancel teleported aircraft vertically");
            Step(w, 100); NavigationChecks.Check(Pose(w, air).Current.X == 16384, "Cancelled landing remained active");
        });
        test("Aviation: landed VTOL takes off and lands with the same ID and HP", () =>
        {
            using var w = new SimulationWorld([Aircraft()], 24576, 24576, navigationGrid: new(24, 24));
            var air = w.AddInitialUnit(0, new(4096, 4096), 1);
            w.Step([new(1, 0, 1, CommandKind.Move, air, new(8192, 8192))]); Step(w, 90);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Land, air, new(8192, 8192))]); Step(w, 50);
            NavigationChecks.Check(Pose(w, air).Current == new WorldPosition(8192, 8192) && Pose(w, air).Health == 200 && w.Count == 1, "Landing changed actor or failed to touch down");
        });
        test("Aviation: ground-only fire rejects flying targets, AA hits with height", () =>
        {
            var aa = new WeaponDefinition(8192, 8192, 25, 2, 0, projectile: ProjectileKind.Missile, projectileSpeed: 512,
                targets: new(["air", "helicopter"]), spatialTrajectory: true);
            using var w = new SimulationWorld([new("rifle", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 50, 2, 0)),
                new("aa", 100, ["idle", "move", "fire"], weapon: aa), Aircraft()], 24576, 24576);
            var rifle = w.AddInitialUnit(0, new(4096, 8192), 1);
            var launcher = w.AddInitialUnit(1, new(4096, 12288), 1);
            var air = w.AddInitialUnit(2, new(8192, 8192, 1536), 2);
            w.Step([new(1, 0, 1, CommandKind.AttackTarget, rifle, TargetEntity: air), new(1, 0, 2, CommandKind.AttackTarget, launcher, TargetEntity: air)]);
            NavigationChecks.Check(w.Events.ToArray().Any(e => e.Kind == WorldEventKind.InvalidOrder && e.Entity == rifle), "Ground gun accepted air target");
            Step(w, 20); NavigationChecks.Check(!Poses(w).Any(p => p.Id == air) || Pose(w, air).Health < 200, "AA never hit elevated target");
        });
        test("Aviation: a descending missile does not hit a ground actor under the aircraft", () =>
        {
            var missile = new WeaponDefinition(8192, 8192, 60, 1000, 0, projectile: ProjectileKind.Missile, projectileSpeed: 512,
                targets: new(["ground", "air"]), spatialTrajectory: true);
            using var w = new SimulationWorld([Aircraft(weapon: missile), Aircraft(id: "target-air"), new("ground", 100)], 24576, 24576);
            var source = w.AddInitialUnit(0, new(4096, 8192, 1536), 1);
            var target = w.AddInitialUnit(1, new(8192, 8192, 1536), 2);
            var ground = w.AddInitialUnit(2, new(6144, 8192), 2);
            w.Step([new(1, 0, 1, CommandKind.AttackTarget, source, TargetEntity: target)]); Step(w, 30);
            NavigationChecks.Check(Pose(w, ground).Health == 100 && Pose(w, target).Health < 200, "Missile collided with a projected ground body");
        });
        test("Aviation: forward flight keeps moving after Stop and completes waypoints", () =>
        {
            using var w = new SimulationWorld([Aircraft(new(FlightMode.ForwardFlight))], 65536, 65536);
            var air = w.AddInitialUnit(0, new(32768, 32768, 1536), 1);
            w.Step([new(1, 0, 1, CommandKind.Move, air, new(32768, 24576))]); Step(w, 80);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Stop, air)]);
            var p = Pose(w, air).Current; w.Step();
            NavigationChecks.Check(Pose(w, air).Current != p && Pose(w, air).Flight.Phase == FlightPhase.Loitering, "Stop froze the plane");
        });
        test("Aviation: service reserves separate slots and resumes an attack after rearming", () =>
        {
            var gun = new WeaponDefinition(4096, 8192, 1, 1, 0, targets: new(["ground"]), spatialTrajectory: true);
            using var w = new SimulationWorld([Base(2), Aircraft(new(ammo: 2), gun), new("target", 10000)], 49152, 49152);
            var b = w.AddInitialUnit(0, new(16384, 16384), 1);
            var a = w.AddInitialUnit(1, new(24576, 16384, 1536), 1);
            var c = w.AddInitialUnit(1, new(24576, 24576, 1536), 1);
            w.AddInitialUnit(2, new(28672, 16384), 2);
            w.Step([new(1, 0, 1, CommandKind.ReturnToBase, a), new(1, 0, 2, CommandKind.ReturnToBase, c)]);
            Step(w, 180);
            NavigationChecks.Check(Pose(w, a).Flight.Airbase == b && Pose(w, c).Flight.Airbase == b && Pose(w, a).Flight.Slot != Pose(w, c).Flight.Slot, "Aircraft shared one slot or failed to land");
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.AttackTarget, a, TargetEntity: new(4))]);
            int shots = 0; bool serviced = false;
            for (int i = 0; i < 500; i++)
            {
                w.Step(); shots += w.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired && e.Entity == a);
                serviced |= Pose(w, a).Flight.Phase == FlightPhase.Servicing;
            }
            NavigationChecks.Check(shots > 2 && serviced, $"Reload did not resume attack: shots={shots}, service={serviced}");
        });
        test("Aviation: losing a service base releases its aircraft without deleting it", () =>
        {
            using var w = new SimulationWorld([Base(), Aircraft()], 32768, 32768);
            var b = w.AddInitialUnit(0, new(16384, 16384), 1); var a = w.AddInitialUnit(1, new(24576, 16384, 1536), 1);
            w.Step([new(1, 0, 1, CommandKind.ReturnToBase, a)]); Step(w, 10);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Destroy, b)]); Step(w, 15);
            NavigationChecks.Check(Pose(w, a).Flight.Airbase == default && w.Count == 1, "Stale assignment survived base destruction");
        });
        test("Aviation: workers and frame consumers do not change flight or service results", () =>
        {
            string Run(int workers)
            {
                using var w = new SimulationWorld([Aircraft(), new("ground", 100)], 32768, 32768, navigationGrid: new(32, 32), navigationWorkers: workers);
                var a = w.AddInitialUnit(0, new(8192, 8192, 1536), 1); var b = w.AddInitialUnit(1, new(8192, 8192), 1);
                var frames = new FrameExchange(); frames.TryPublish(w); using var old = frames.AcquireLatest()!;
                w.Step([new(1, 0, 1, CommandKind.MoveSelected, Recipients: new([a, b]), Position: new(24576, 24576))]); Step(w, 160);
                NavigationChecks.Check(old.Poses[0].Current == new WorldPosition(8192, 8192, 1536), "Held frame mutated");
                return w.Report().StateHash;
            }
            NavigationChecks.Check(Run(1) == Run(4), "Flight diverged across workers");
        });
        test("Aviation: four parking spaces cap production and serialize runway departures", () =>
        {
            var template = Base(4, true);
            var producer = new UnitDefinition("base", 1000, movable: false, footprint: template.Footprint, airbase: template.Airbase,
                taskQueues: [new("aircraft")], products: [new("make", "fighter", "aircraft")]);
            var plane = new UnitDefinition("fighter", 200, flight: new(FlightMode.ForwardFlight, needsRunway: true), build: new(1));
            using var w = new SimulationWorld([producer, plane], 65536, 65536, navigationGrid: new(64, 64));
            var b = w.AddInitialUnit(0, new(24576, 24576), 1);
            w.Step([new(1, 0, 1, CommandKind.EnqueueTask, b, Action: "make", Amount: 5)]); Step(w, 8);
            NavigationChecks.Check(w.Count == 5 && w.TaskCount == 1, "Fifth plane bypassed parking capacity or ready task was lost");
            NavigationChecks.Check(Poses(w).Where(p => p.Id != b).Select(p => p.Flight.Slot).Distinct().Count() == 4, "Production overlapped parking slots");
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.MoveSelected, Recipients: new([new(2), new(3)]), Position: new(49152, 16384))]);
            for (int i = 0; i < 180; i++)
            {
                w.Step();
                NavigationChecks.Check(Poses(w).Count(p => p.Flight.Phase is FlightPhase.Taxiing or FlightPhase.RunwayTakeoff) <= 1, "Two aircraft used the runway at once");
            }
            NavigationChecks.Check(Pose(w, new(2)).Current.Elevation > 512 && Pose(w, new(3)).Current.Elevation > 512 && w.Count == 5, $"Runway failed: {Pose(w, new(2)).Current} {Pose(w, new(2)).Flight}; {Pose(w, new(3)).Current} {Pose(w, new(3)).Flight}; count={w.Count}");
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Destroy, new(2))]);
            NavigationChecks.Check(w.Count == 5 && w.TaskCount == 0, "A released parking slot did not unblock production");
        });
        test("Aviation: returning runway aircraft reaches its parking slot", () =>
        {
            using var w = new SimulationWorld([Base(4, true), Aircraft(new(FlightMode.ForwardFlight, needsRunway: true))], 65536, 65536);
            var b = w.AddInitialUnit(0, new(24576, 24576), 1);
            var a = w.AddInitialUnit(1, new(49152, 32768, 1536), 1);
            w.Step([new(1, 0, 1, CommandKind.ReturnToBase, a)]); Step(w, 900);
            NavigationChecks.Check(Pose(w, a).Flight.Phase == FlightPhase.Landed && Pose(w, a).Flight.Airbase == b && Pose(w, a).Current.Elevation == 0,
                $"Runway return remained in {Pose(w, a).Flight.Phase} at {Pose(w, a).Current}");
        });
        test("Aviation: high aircraft align with a runway whose approach meets the map edge", () =>
        {
            var field = new UnitDefinition("edge-field", 1000, movable: false, footprint: Base().Footprint,
                airbase: new([new(-2048, 0)], runwayStart: new(0, -3072), runwayEnd: new(0, 2048), keepsParking: true));
            var flight = new FlightDefinition(FlightMode.ForwardFlight, speed: 320, minimumSpeed: 192, turnRate: 20,
                cruiseAltitude: 5120, verticalSpeed: 160, needsRunway: true);
            using var world = new SimulationWorld([field, Aircraft(flight)], 32768, 24576);
            world.AddInitialUnit(0, new(4096, 4096), 1);
            var parking = new WorldPosition(2048, 4096);
            var aircraft = world.AddInitialUnit(1, parking, 1);
            world.Step([new(1, 0, 1, CommandKind.Move, aircraft, new(20000, 17000))]);

            bool tookOff = false;
            for (int i = 0; i < 150 && Pose(world, aircraft).Current.Elevation < flight.CruiseAltitude; i++)
            {
                var before = Pose(world, aircraft);
                world.Step();
                var after = Pose(world, aircraft);
                if (before.Flight.Phase != FlightPhase.RunwayTakeoff) continue;
                tookOff = true;
                NavigationChecks.Check(after.Current.X == before.Current.X && after.Current.Y > before.Current.Y && after.Presentation.BodyFacing.Y > 0,
                    "Takeoff moved off-axis, hovered or showed the wrong facing");
            }
            NavigationChecks.Check(tookOff && Pose(world, aircraft).Current.Elevation == flight.CruiseAltitude, "The plane did not climb to cruise altitude");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.ReturnToBase, aircraft)]);

            bool descended = false;
            for (int i = 0; i < 400 && Pose(world, aircraft).Flight.Phase != FlightPhase.Landed; i++)
            {
                var before = Pose(world, aircraft);
                world.Step();
                var after = Pose(world, aircraft);
                if (before.Flight.Phase != FlightPhase.RunwayLanding) continue;
                descended = true;
                NavigationChecks.Check(after.Current.X == 4096 && after.Current.Y > before.Current.Y && after.Presentation.BodyFacing.Y > 0,
                    "The final approach moved off-axis, hovered or showed the wrong facing");
                NavigationChecks.Check(before.Current.Elevation - after.Current.Elevation <= flight.VerticalSpeed, "Descent exceeded the vertical speed limit");
            }
            NavigationChecks.Check(descended && Pose(world, aircraft).Current == parking && Pose(world, aircraft).Flight.Phase == FlightPhase.Landed,
                $"The plane did not land at the edge runway: {Pose(world, aircraft).Flight} at {Pose(world, aircraft).Current}");
        });
        test("Aviation: 1000 aircraft have bounded avoidance and no steady tick allocations", () =>
        {
            using var w = new SimulationWorld([Aircraft()], 131072, 131072, initialCapacity: 1000);
            for (int i = 0; i < 1000; i++) w.AddInitialUnit(0, new(4096 + i % 40 * 2048, 4096 + i / 40 * 2048, 1536), 1);
            w.Step([new(1, 0, 1, CommandKind.MoveAll, Position: new(114688, 65536))]);
            Step(w, 64);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Step(w, 32);
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            NavigationChecks.Check(allocation == 0 && w.Count == 1000, $"Steady flight allocated {allocation} bytes");
        });
        test("Aviation: a transport lands before unloading and keeps passengers until touchdown", () =>
        {
            var carrier = new UnitDefinition("carrier", 200, flight: new(), transport: new(["infantry"], 4, unloadDistance: 768));
            using var w = new SimulationWorld([carrier, new("passenger", 100, passenger: new("infantry"))], 32768, 32768, navigationGrid: new(32, 32));
            var a = w.AddInitialUnit(0, new(8192, 8192), 1); var passenger = w.AddInitialUnit(1, new(8960, 8192), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: a)]);
            NavigationChecks.Check(Pose(w, passenger).Carrier == a, "Initial boarding failed");
            w.Step([new(2, 0, 1, CommandKind.Move, a, new(16384, 8192))]); Step(w, 80);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.UnloadAt, a, new(16384, 8192))]);
            NavigationChecks.Check(Pose(w, passenger).Carrier == a, "Passenger appeared in mid-air");
            Step(w, 60);
            NavigationChecks.Check(Pose(w, passenger).Carrier == default && Pose(w, passenger).Current.Elevation == 0 && Pose(w, a).Current.Elevation == 0,
                "Air transport did not land before unloading");
        });
        test("Aviation: rejected ground spawn preserves IDs while airborne spawn may overlap ground", () =>
        {
            using var w = new SimulationWorld([Aircraft(), new("ground", 100)], 16384, 16384, navigationGrid: new(16, 16));
            w.AddInitialUnit(1, new(8192, 8192), 1);
            NavigationChecks.Reject<ArgumentException>(() => w.AddInitialUnit(0, new(8192, 8192), 1));
            var a = w.AddInitialUnit(0, new(8192, 8192, 1536), 1);
            NavigationChecks.Check(a.Value == 2 && w.Count == 2, "Rejected placement consumed an ID or air overlap was refused");
        });
        test("Aviation: unused aircraft definitions do not change ground navigation geometry", () =>
        {
            var ground = new UnitDefinition("ground", 100);
            var plane = new UnitDefinition("plane", 100, body: BodyShape.Circle(320), flight: new());
            var grid = new NavigationGrid(24, 24, Enumerable.Range(0, 20).Select(y => new GridCell(12, y)));
            using var first = new SimulationWorld([ground], 24576, 24576, navigationGrid: grid);
            using var second = new SimulationWorld([ground, plane], 24576, 24576, navigationGrid: grid);
            var a = first.AddInitialUnit(0, new(4096, 8192), 1); var b = second.AddInitialUnit(0, new(4096, 8192), 1);
            var command = new WorldCommand(1, 0, 1, CommandKind.Move, a, new(20480, 8192));
            first.Step([command]); second.Step([command]);
            for (int i = 0; i < 180; i++)
            {
                first.Step(); second.Step();
                NavigationChecks.Check(Pose(first, a).Current == Pose(second, b).Current, "An air body changed the ground clearance graph");
            }
        });
        test("Aviation: unsupported variable-height flight surfaces reject before consuming identity", () =>
        {
            var grid = new NavigationGrid(16, 16, surface: Enumerable.Range(0, 256).Select(i => new SurfaceSample(i == 255 ? 2048 : 0)));
            using var w = new SimulationWorld([Aircraft(), new("ground", 100)], 16384, 16384, navigationGrid: grid);
            NavigationChecks.Reject<ArgumentException>(() => w.AddInitialUnit(0, new(8192, 8192, 1536), 1));
            NavigationChecks.Check(w.AddInitialUnit(1, new(8192, 8192), 1).Value == 1, "Unsupported flight mutated identity allocation");
        });
    }
}
