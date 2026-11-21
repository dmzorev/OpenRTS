using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class AirBoardingChecks
{
    private static readonly WorldPosition Center = new(16384, 12288);
    private static EntityPose Pose(SimulationWorld w, EntityId id) => Poses(w).Single(p => p.Id == id);
    private static void Step(SimulationWorld w, int ticks) { for (int i = 0; i < ticks; i++) w.Step(); }
    private static long Squared(WorldPosition a, WorldPosition b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
    private static UnitDefinition[] Types(int capacity = 4) =>
    [
        new("carrier", 300, body: BodyShape.Circle(320), flight: new(cruiseAltitude: 1536), transport: new(["infantry"], capacity)),
        new("infantry", 100, passenger: new("infantry")),
        new("obstacle", 100, movable: false, footprint: new(2, 2,
            [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]), destruction: DestructionPolicy.Invulnerable)
    ];

    public static void Run(Action<string, Action> test)
    {
        test("Air boarding: real heavy transport and combat tiltrotor land for a selected passenger", () =>
        {
            var catalog = GameManifest.Load("sandbox/game.yaml").LoadUnits();
            foreach (string name in new[] { "heavy-air-transport", "tiltrotor" })
            {
                SimulationReport? reference = null;
                foreach (int workers in new[] { 1, 4 })
                {
                    using var w = new SimulationWorld(catalog.Units, 32768, 24576, navigationGrid: new(32, 24),
                        navigationWorkers: workers, reverseNavigationJobs: workers > 1, players: [new(1, "Blue")]);
                    w.RestrictCommandsToPlayers();
                    int hostType = catalog.Units.ToList().FindIndex(t => t.Id == name);
                    int passengerType = catalog.Units.ToList().FindIndex(t => t.Id == (name == "tiltrotor" ? "rifleman" : "tank"));
                    var carrier = w.AddInitialUnit(hostType, Center with { Elevation = 3584 }, 1);
                    var passenger = w.AddInitialUnit(passengerType, new(8192, 8192), 1);
                    w.Step([new(1, 1, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]);
                    NavigationChecks.Check(Pose(w, passenger).Carrier == default && Pose(w, carrier).Flight.Airborne,
                        "A passenger boarded before touchdown");
                    var waiting = Pose(w, passenger).Order.Destination;
                    NavigationChecks.Check(ObjectGeometry.DistanceSquared(catalog.Units[passengerType], waiting,
                        catalog.Units[hostType], Center, out _, out _) > 0, "The passenger's waiting point blocks the landing body");
                    Step(w, 300);
                    NavigationChecks.Check(Pose(w, passenger).Carrier == carrier && Pose(w, carrier).Flight.Phase == FlightPhase.Landed,
                        $"{name} failed to land and board its passenger");
                    var report = w.Report(); NavigationChecks.Check(reference is null || reference == report, "Pickup differs by worker count"); reference = report;
                }
            }
        });

        test("Air boarding: an occupied projection chooses the nearest free side toward the passenger", () =>
        {
            using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24));
            w.AddInitialUnit(2, Center);
            var carrier = w.AddInitialUnit(0, Center with { Elevation = 1536 }, 1);
            var passenger = w.AddInitialUnit(1, Center with { Y = 6144 }, 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]); Step(w, 300);
            var landed = Pose(w, carrier);
            NavigationChecks.Check(Pose(w, passenger).Carrier == carrier && landed.Current.X == Center.X && landed.Current.Y < Center.Y,
                "The landing search ignored its passenger-facing tie break");
        });

        test("Air boarding: the landing site must be reachable by the passenger across static components", () =>
        {
            using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24,
                Enumerable.Range(0, 24).Select(y => new GridCell(16, y))));
            var carrier = w.AddInitialUnit(0, new(20480, 12288, 1536), 1);
            var passenger = w.AddInitialUnit(1, new(8192, 12288), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]); Step(w, 300);
            NavigationChecks.Check(Pose(w, passenger).Carrier == carrier && Pose(w, carrier).Current.X < 16384,
                "The aircraft landed in a component its passenger cannot reach");
        });

        test("Air boarding: a newly blocked landing is replanned without teleporting", () =>
        {
            using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24));
            var carrier = w.AddInitialUnit(0, Center with { Elevation = 1536 }, 1);
            var passenger = w.AddInitialUnit(1, new(8192, 12288), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]);
            w.Step([new(2, 0, 1, CommandKind.Spawn, UnitType: 2, Position: Center)]);
            var previous = Pose(w, carrier).Current;
            for (int i = 0; i < 300; i++)
            {
                w.Step(); var at = Pose(w, carrier).Current;
                NavigationChecks.Check(Math.Abs(at.Elevation - previous.Elevation) <= 96 && Squared(at, previous) <= 160L * 160,
                    "Replanning teleported its aircraft"); previous = at;
            }
            NavigationChecks.Check(Pose(w, passenger).Carrier == carrier && Pose(w, carrier).Current.X < Center.X,
                "A blocked landing never retried on the passenger side");
        });

        test("Air boarding: no free site waits and retries after terrain opens", () =>
        {
            var blocked = from y in Enumerable.Range(0, 16) from x in Enumerable.Range(0, 16)
                          where (x, y) != (6, 6) select new GridCell(x, y);
            using var w = new SimulationWorld(Types(), 16384, 16384, navigationGrid: new(16, 16, blocked));
            var carrier = w.AddInitialUnit(0, new(8192, 8192, 1536), 1);
            var passenger = w.AddInitialUnit(1, new(6656, 6656), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]); Step(w, 9);
            NavigationChecks.Check(Pose(w, passenger).Order is { Kind: UnitActionKind.Enter, Phase: OrderPhase.Waiting } &&
                Pose(w, carrier).Flight.Airborne, "No landing site lost the pickup order or forced a landing");
            w.Step(Enumerable.Range(7, 3).Select((x, i) => new WorldCommand(w.Tick + 1, 0, i + 1, CommandKind.UnblockCell,
                Position: new(x * 1024 + 512, 6656))).ToArray());
            Step(w, 160);
            NavigationChecks.Check(Pose(w, passenger).Carrier == carrier, "Opening a reachable landing site did not resume pickup");
        });

        test("Air boarding: stopping a passenger or moving its transport cancels an automatic pickup", () =>
        {
            foreach (var (moveCarrier, sameTick) in new[] { (false, false), (true, false), (true, true) })
            {
                using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24));
                var carrier = w.AddInitialUnit(0, Center with { Elevation = 1536 }, 1);
                var passenger = w.AddInitialUnit(1, new(8192, 12288), 1);
                var enter = new WorldCommand(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier);
                if (sameTick) w.Step([enter, new(1, 0, 2, CommandKind.Move, carrier, new(24576, 12288))]);
                else
                {
                    w.Step([enter]);
                    w.Step([moveCarrier ? new(2, 0, 1, CommandKind.Move, carrier, new(24576, 12288)) :
                        new(2, 0, 1, CommandKind.Stop, passenger)]);
                }
                Step(w, 160);
                NavigationChecks.Check(Pose(w, passenger).Carrier == default && Pose(w, carrier).Current.Elevation == 1536 &&
                    (!moveCarrier || Pose(w, carrier).Current.X == 24576), "An obsolete pickup overrode the new player order");
            }
        });

        test("Air boarding: another waiting passenger can take over a cancelled landing request", () =>
        {
            using var w = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24));
            var carrier = w.AddInitialUnit(0, Center with { Elevation = 1536 }, 1);
            var first = w.AddInitialUnit(1, new(8192, 12288), 1);
            var second = w.AddInitialUnit(1, new(24576, 12288), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, Recipients: new([first, second]), TargetEntity: carrier)]);
            w.Step([new(2, 0, 1, CommandKind.Stop, first)]); Step(w, 300);
            NavigationChecks.Check(Pose(w, first).Carrier == default && Pose(w, second).Carrier == carrier,
                "Cancelling one passenger lost another passenger's request");
        });

        test("Air boarding: moving a carrier cancels pickup even before a landing site is found", () =>
        {
            var blocked = from y in Enumerable.Range(0, 16) from x in Enumerable.Range(0, 16)
                          where (x, y) != (6, 6) select new GridCell(x, y);
            using var w = new SimulationWorld(Types(), 16384, 16384, navigationGrid: new(16, 16, blocked));
            var carrier = w.AddInitialUnit(0, new(8192, 8192, 1536), 1);
            var passenger = w.AddInitialUnit(1, new(6656, 6656), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]);
            w.Step([new(2, 0, 1, CommandKind.Move, carrier, new(12288, 12288))]); Step(w, 80);
            NavigationChecks.Check(Pose(w, passenger).Order.Phase == OrderPhase.Idle && Pose(w, carrier).Current.X == 12288,
                "A failed site search left an Enter request capable of overriding a new carrier order");
        });

        test("Air boarding: full or foreign aircraft cannot be forced to land by Enter", () =>
        {
            using (var foreign = new SimulationWorld(Types(), 32768, 24576, navigationGrid: new(32, 24)))
            {
                var carrier = foreign.AddInitialUnit(0, Center with { Elevation = 1536 }, 2);
                var passenger = foreign.AddInitialUnit(1, new(8192, 12288), 1);
                foreign.Step([new(1, 1, 1, CommandKind.Enter, passenger, TargetEntity: carrier)]); Step(foreign, 40);
                NavigationChecks.Check(Pose(foreign, carrier).Flight.Airborne && Pose(foreign, passenger).Carrier == default,
                    "An enemy pickup request changed the aircraft's flight");
            }
            using var w = new SimulationWorld(Types(capacity: 1), 32768, 24576, navigationGrid: new(32, 24));
            var host = w.AddInitialUnit(0, Center, 1);
            var loaded = w.AddInitialUnit(1, Center with { X = Center.X - 768 }, 1);
            var extra = w.AddInitialUnit(1, new(8192, 12288), 1);
            w.Step([new(1, 0, 1, CommandKind.Enter, loaded, TargetEntity: host)]);
            NavigationChecks.Check(Pose(w, loaded).Carrier == host, "The full transport fixture did not load");
            w.Step([new(2, 0, 1, CommandKind.Move, host, new(24576, 12288))]); Step(w, 100);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.Enter, extra, TargetEntity: host)]);
            NavigationChecks.Check(w.Events.ToArray().Any(e => e.Kind == WorldEventKind.TransportFull), "A full aircraft did not reject the extra passenger");
            Step(w, 40);
            NavigationChecks.Check(Pose(w, host).Flight.Airborne && Pose(w, loaded).Carrier == host && Pose(w, extra).Carrier == default,
                "A full aircraft started an unnecessary landing or lost cargo");
        });
    }
}
