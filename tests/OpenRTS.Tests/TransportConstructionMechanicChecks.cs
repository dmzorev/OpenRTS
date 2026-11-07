using Frent;
using OpenRTS.Core;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Core.Transport;

internal static class TransportConstructionMechanicChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Mechanic registration: external transport owns boarding and cargo", () =>
        {
            var first = RunTransport(1, 1);
            var parallel = RunTransport(4, 1);
            var revised = RunTransport(1, 2);
            Check(first.Reports.SequenceEqual(parallel.Reports) && first.Events.SequenceEqual(parallel.Events),
                "Worker scheduling changed custom transport");
            Check(first.Reports.Zip(revised.Reports).All(pair => pair.First != pair.Second) && first.Events.SequenceEqual(revised.Events),
                "Transport version did not distinguish otherwise equal runs");
        });
        test("Mechanic registration: external foundation preserves damage and activates economy", () =>
        {
            var first = RunConstruction(1, 1);
            var parallel = RunConstruction(4, 1);
            var revised = RunConstruction(1, 2);
            Check(first.Reports.SequenceEqual(parallel.Reports) && first.Events.SequenceEqual(parallel.Events),
                "Worker scheduling changed custom construction");
            Check(first.Reports.Zip(revised.Reports).All(pair => pair.First != pair.Second) && first.Events.SequenceEqual(revised.Events),
                "Construction version did not distinguish otherwise equal runs");
        });
        test("Mechanic registration: disabled slots reject dependent definitions", () =>
        {
            var mechanics = new GameplayMechanics { Transport = null, Construction = null };
            using var plain = SimulationWorld.CreateWithMechanics(mechanics, [new("plain", 10)], 16384, 16384);
            using var defaults = new SimulationWorld([new("plain", 10)], 16384, 16384);
            Check(plain.Report() != defaults.Report(), "Disabled slots did not enter the hash");
            Reject([new("scout", 10, passenger: new("scout"))], "transport");
            Reject([new("bunker", 10, movable: false, transport: new(["scout"], 1))], "transport");
            Reject([new("builder", 10, constructionRange: 1024)], "construction");

            void Reject(UnitDefinition[] types, string expected)
            {
                try { using var invalid = SimulationWorld.CreateWithMechanics(mechanics, types, 16384, 16384); }
                catch (ArgumentException error) when (error.ParamName == "mechanics" && error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
                throw new InvalidOperationException($"Disabled {expected} accepted dependent definitions");
            }
        });
    }

    private static (List<SimulationReport> Reports, List<WorldEvent> Events) RunTransport(int workers, int version)
    {
        var mechanics = new GameplayMechanics { Transport = new("example.single-seat", version, static () => new SingleSeat()) };
        using var world = SimulationWorld.CreateWithMechanics(mechanics,
            [new("scout", 100, passenger: new("scout")),
                new("post", 100, states: ["garrisoned"], movable: false, transport: new(["scout"], 2))],
            16384, 16384, navigationGrid: new(16, 16), navigationWorkers: workers, reverseNavigationJobs: workers > 1);
        var carrier = world.AddInitialUnit(1, new(8192, 8192));
        var first = world.AddInitialUnit(0, new(6656, 8192), 1);
        var second = world.AddInitialUnit(0, new(9728, 8192), 1);
        var reports = new List<SimulationReport>();
        var events = new List<WorldEvent>();
        Step(world, reports, events, [
            new(1, 0, 1, CommandKind.Enter, first, TargetEntity: carrier),
            new(1, 0, 2, CommandKind.Enter, second, TargetEntity: carrier)]);
        Check(Pose(world, first).Carrier == carrier && Pose(world, second).Carrier.Value == 0 &&
            Pose(world, carrier).Presentation is { Owner: 1, States: 1 } &&
            events.Any(e => e.Kind == WorldEventKind.TransportFull && e.Entity == second), "Custom range, capacity or neutral claim was ignored");

        Step(world, reports, events, [new(2, 0, 1, CommandKind.Unload, carrier, TargetEntity: first)]);
        Check(Pose(world, first).Carrier.Value == 0 && Pose(world, carrier).Presentation is { Owner: 0, States: 0 },
            "Custom cargo did not unload or return the empty post to neutral");
        Step(world, reports, events, [new(3, 0, 1, CommandKind.Enter, second, TargetEntity: carrier)]);
        Check(Pose(world, second).Carrier == carrier, "The custom seat did not reopen");
        Step(world, reports, events, [new(4, 0, 1, CommandKind.Destroy, carrier)]);
        Check(world.Count == 1 && Pose(world, first).Id == first &&
            events.Any(e => e.Tick == 4 && e.Kind == WorldEventKind.Removed && e.Entity == second) &&
            !events.Any(e => e.Tick == 4 && e.Kind == WorldEventKind.OwnerChanged), "Destroying a custom carrier mishandled its passenger");
        return (reports, events);
    }

    private static (List<SimulationReport> Reports, List<WorldEvent> Events) RunConstruction(int workers, int version)
    {
        var mechanics = new GameplayMechanics { Construction = new("example.prefabricated", version, static () => new Prefabricated()) };
        var footprint = new FootprintDefinition(2, 2,
            [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);
        using var world = SimulationWorld.CreateWithMechanics(mechanics,
            [new("yard", 100, movable: false, constructionRange: 8192, products: [new("plant", "plant", "", OnSite: true)]),
                new("plant", 100, states: ["lowpower", "constructing"], movable: false, footprint: footprint,
                    build: new(9, 50, ProductionKind.PlaceBuilding, initialHealthPercent: 25), economy: new(100, 5, 2)),
                new("enemy", 100, ["idle", "move", "fire"], weapon: new(3072, 3072, 7, 1000, 0))],
            16384, 16384, navigationGrid: new(16, 16), navigationWorkers: workers, reverseNavigationJobs: workers > 1,
            players: [new(1, "Blue", 500), new(2, "Red")]);
        var yard = world.AddInitialUnit(0, new(4096, 8192), 1);
        var enemy = world.AddInitialUnit(2, new(11264, 8192), 2);
        var site = new EntityId(3);
        var position = new WorldPosition(8192, 8192);
        var reports = new List<SimulationReport>();
        var events = new List<WorldEvent>();
        Step(world, reports, events, [new(1, 0, 1, CommandKind.PlanConstruction, yard, position, Action: "plant")]);
        Check(Pose(world, site).Health == 40 && Pose(world, site).Construction == new ConstructionProgress(0, 2) &&
            world.TryGetConditions(site, out var conditions) && conditions == GameplayConditions.Constructing &&
            Player(world) is { Credits: 450, Energy: 0 } && world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(position)),
            "The custom foundation did not use the normal placement, payment and readiness path");
        Step(world, reports, events, [new(2, 0, 1, CommandKind.AttackTarget, enemy, TargetEntity: site)]);
        Check(Pose(world, site).Health == 53 && Pose(world, site).Construction == new ConstructionProgress(1, 2),
            "Custom progress did not incorporate combat damage");
        Step(world, reports, events);
        Check(Pose(world, site).Health == 73 && !Pose(world, site).Construction.Active &&
            world.TryGetConditions(site, out conditions) && conditions == GameplayConditions.None && Player(world) is { Credits: 450, Energy: 100 } &&
            events.Any(e => e.Kind == WorldEventKind.ConstructionCompleted && e.Entity == site && e.Tick == 3),
            "Custom completion did not activate the original entity at the expected tick");
        Step(world, reports, events);
        Check(Player(world).Credits == 450, "Income started before its interval");
        Step(world, reports, events);
        Check(Player(world).Credits == 455, "Income timer was not installed by custom completion");
        return (reports, events);
    }

    private static void Step(SimulationWorld world, List<SimulationReport> reports, List<WorldEvent> events, ReadOnlySpan<WorldCommand> commands = default)
    {
        world.Step(commands);
        reports.Add(world.Report());
        foreach (var item in world.Events) events.Add(item);
    }

    private static EntityPose Pose(SimulationWorld world, EntityId id)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses.Single(pose => pose.Id == id);
    }

    private static PlayerPose Player(SimulationWorld world)
    {
        var players = new PlayerPose[world.PlayerCount];
        world.CopyPlayers(players);
        return players.Single(player => player.Id == 1);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SingleSeat : ITransportMechanic
    {
        private struct Rider { public EntityId Carrier; }
        private sealed class Seat
        {
            public readonly EntityId[] Occupant = new EntityId[1];
            public bool Claimed;
        }

        public void Initialize(Entity entity, UnitDefinition type)
        {
            if (type.Transport is not null) entity.Add(new Seat());
            if (type.Passenger is not null) entity.Add(new Rider());
        }

        public EntityId CarrierOf(Entity passenger) => passenger.Has<Rider>() ? passenger.Get<Rider>().Carrier : default;

        public ReadOnlySpan<EntityId> Passengers(Entity carrier)
        {
            if (!carrier.Has<Seat>()) return [];
            var occupant = carrier.Get<Seat>().Occupant;
            return occupant[0].Value == 0 ? [] : occupant;
        }

        public bool Accepts(TransportParticipant passenger, TransportParticipant carrier) => passenger.Type.Passenger!.Type == "scout";
        public int BoardingRange(TransportParticipant passenger, TransportParticipant carrier) => 1536;
        public bool HasRoom(TransportParticipant passenger, TransportParticipant carrier) => Passengers(carrier.Entity).IsEmpty;

        public void Board(TransportParticipant passenger, TransportParticipant carrier, bool claimedNeutral)
        {
            passenger.Entity.Get<Rider>().Carrier = carrier.Id;
            var seat = carrier.Entity.Get<Seat>();
            seat.Occupant[0] = passenger.Id;
            seat.Claimed = claimedNeutral;
        }

        public bool Detach(TransportParticipant passenger, TransportParticipant carrier)
        {
            passenger.Entity.Get<Rider>().Carrier = default;
            var seat = carrier.Entity.Get<Seat>();
            seat.Occupant[0] = default;
            bool wasClaimed = seat.Claimed;
            seat.Claimed = false;
            return wasClaimed;
        }

        public void SuppressNeutralReturn(Entity carrier) => carrier.Get<Seat>().Claimed = false;

        public void WriteEntityState(BinaryWriter writer, Entity entity)
        {
            bool hasSeat = entity.Has<Seat>();
            writer.Write(hasSeat);
            if (!hasSeat) return;
            var seat = entity.Get<Seat>();
            writer.Write(seat.Occupant[0].Value);
            writer.Write(seat.Claimed);
        }
    }

    private sealed class Prefabricated : IConstructionMechanic
    {
        private struct AssemblyState(int required, long startedAt)
        {
            public readonly int Required = required;
            public readonly long StartedAt = startedAt;
            public int Work;
        }

        public FoundationStart PlanFoundation(UnitDefinition type, BuildDefinition recipe) => new(40, 2);
        public void Initialize(Entity foundation, FoundationStart start, long tick) => foundation.Add(new AssemblyState(start.RequiredWork, tick));
        public bool IsFoundation(Entity entity) => entity.Has<AssemblyState>();
        public ConstructionProgress GetProgress(Entity foundation) => new(foundation.Get<AssemblyState>().Work, foundation.Get<AssemblyState>().Required);

        public FoundationStep Advance(FoundationWork work)
        {
            ref var state = ref work.Foundation.Get<AssemblyState>();
            if (work.Tick == state.StartedAt) return new(work.Health, false);
            state.Work++;
            return new(Math.Min(work.Type.MaxHealth, work.Health + 20), state.Work == state.Required);
        }

        public void Complete(Entity foundation) => foundation.Remove<AssemblyState>();

        public void WriteFoundationState(BinaryWriter writer, Entity foundation)
        {
            var state = foundation.Get<AssemblyState>();
            writer.Write(state.Required);
            writer.Write(state.StartedAt);
            writer.Write(state.Work);
        }
    }
}
