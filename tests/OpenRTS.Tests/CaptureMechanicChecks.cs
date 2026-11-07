using Frent;
using OpenRTS.Core;
using OpenRTS.Core.Capture;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

internal static class CaptureMechanicChecks
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var kind in new[] { CommandKind.CaptureTarget, CommandKind.UseAbility })
        test($"Capture mechanics: replacement, queued {kind} and a separate scoring rule", () =>
        {
            var scoreA = new HoldScore(new(2));
            var scoreB = new HoldScore(new(2));
            var capture = new GameplayRegistration<ICaptureMechanic>("test.capture", 1, static () => new BreachCapture());
            using var a = World(capture, scoreA, 1);
            using var b = World(capture, scoreB, 4);
            using var builtIn = World(GameplayMechanics.BuiltInCapture, null, 1);
            Check(a.Report() != builtIn.Report(), "Rule registration is missing from the hash");

            Advance([
                new(1, 0, 1, CommandKind.EnqueueTask, new(2), Action: "make"),
                new(1, 0, 2, CommandKind.Move, new(1), new(1280, 1024)),
                new(1, 0, 3, kind, new(1), TargetEntity: new(2), Queued: true, Action: "capture")]);
            Check(Pose(a, 1).Order.QueuedCount == 1, "Capture replaced the active move");
            Advance();
            Advance([new(3, 0, 1, CommandKind.AttackTarget, new(4), TargetEntity: new(2))]);
            var order = Pose(a, 1).Order;
            Check(order.Kind == UnitActionKind.Capture && order.CaptureProgress == 1 && order.CaptureDuration == 3,
                "Custom capture progress was not published");
            Check(Pose(a, 2).Health == 50, "The breach did not damage the objective after capture advanced");
            Advance();
            Check(Pose(a, 2).Presentation.Owner == 1 && Pose(builtIn, 2).Presentation.Owner == 2,
                "Capture did not use the selected implementation");
            Check(a.TaskCount == 0 && Player(a, 2) is { Credits: 94, Energy: 100 } && Player(a, 1).Energy == -20,
                "Ownership transfer bypassed queue refunds or power transfer");
            Check(scoreA.Blue == 1 && scoreA.Red == 3 && scoreB.Blue == 1 && scoreB.Red == 3,
                "Scoring did not see the new owner in the same tick");
            Advance([new(5, 0, 1, CommandKind.Destroy, new(2))]);
            Check(scoreA.Blue == 1 && scoreA.Red == 3, "A destroyed objective still scores");

            void Advance(WorldCommand[]? commands = null)
            {
                a.Step(commands);
                b.Step(commands);
                builtIn.Step(commands);
                Check(a.Report() == b.Report() && a.Events.SequenceEqual(b.Events), "Custom mechanics differ by worker count");
            }
        });

        test("Capture mechanics: disabled capture rejects dependent definitions before play", () =>
        {
            var options = new GameplayMechanics { Capture = null };
            using var world = SimulationWorld.CreateWithMechanics(options, [new("unit", 100)], 8192, 8192);
            world.AddInitialUnit(0, new(1024, 1024), 1);
            world.Step([new(1, 0, 1, CommandKind.CaptureTarget, new(1), TargetEntity: new(1))]);
            Check(world.Count == 1 && world.Events[0].Kind == WorldEventKind.InvalidOrder, "Disabled capture accepted a command");
            foreach (var type in Types().Take(2))
            {
                try
                {
                    using var invalid = SimulationWorld.CreateWithMechanics(options, [type], 8192, 8192);
                }
                catch (ArgumentException error) when (error.ParamName == "mechanics") { continue; }
                throw new InvalidOperationException("Capture-dependent definition was accepted with Capture disabled.");
            }
        });
    }

    private static UnitDefinition[] Types() =>
    [
        new("engineer", 100, abilities: [new("capture", "", Kind: AbilityKind.Capture, Range: 4096)]),
        new("factory", 100, ["idle"], ["lowpower"], movable: false, capturable: new(8), economy: new(-20),
            taskQueues: [new("q", payment: PaymentPolicy.Upfront)], products: [new("make", "unit", "q")]),
        new("unit", 100, build: new(10, 20)),
        new("plant", 100, ["idle"], ["lowpower"], movable: false, economy: new(100)),
        new("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 50, 1000, 0))
    ];

    private static SimulationWorld World(GameplayRegistration<ICaptureMechanic> capture, HoldScore? score, int workers)
    {
        var options = new GameplayMechanics { Capture = capture,
            EndOfTick = score is null ? null : new("test.hold-score", 1, () => score) };
        var world = SimulationWorld.CreateWithMechanics(options, Types(), 16384, 16384, navigationGrid: new(16, 16),
            navigationWorkers: workers, players: [new(1, "blue"), new(2, "red", 100)]);
        world.AddInitialUnit(0, new(1024, 1024), 1);
        world.AddInitialUnit(1, new(3072, 1024), 2);
        world.AddInitialUnit(3, new(8192, 8192), 2);
        world.AddInitialUnit(4, new(3072, 3072), 1);
        return world;
    }

    private static EntityPose Pose(SimulationWorld world, uint id)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses.Single(p => p.Id.Value == id);
    }

    private static PlayerPose Player(SimulationWorld world, uint id)
    {
        var poses = new PlayerPose[world.PlayerCount];
        world.CopyPlayers(poses);
        return poses.Single(p => p.Id == id);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BreachCapture : ICaptureMechanic
    {
        private struct Breach { public int Progress, Required; }

        public void Initialize(Entity entity, UnitDefinition type)
        {
            if (type.Abilities.Any(a => a.Kind == AbilityKind.Capture)) entity.Add(new Breach());
        }

        public bool CanStart(CaptureParticipant actor, CaptureParticipant target) =>
            actor.Entity.Has<Breach>() && target.Type.Capturable is not null;

        public CaptureStep Advance(CaptureAttempt attempt)
        {
            int range = attempt.Actor.Type.Abilities.First(a => a.Kind == AbilityKind.Capture).Range;
            if (attempt.DistanceSquared > (long)range * range)
            {
                Reset(attempt.Actor.Entity);
                return CaptureStep.Approach;
            }
            ref var state = ref attempt.Actor.Entity.Get<Breach>();
            state.Required = attempt.Target.Health <= attempt.Target.Type.MaxHealth / 2 ? 2 : 3;
            return ++state.Progress >= state.Required ? CaptureStep.Complete : CaptureStep.Engage;
        }

        public void Reset(Entity entity)
        {
            if (entity.Has<Breach>()) entity.Get<Breach>() = default;
        }

        public void OwnerChanged(Entity entity, long tick) => Reset(entity);
        public CaptureProgress GetProgress(Entity actor, UnitDefinition targetType) => actor.Has<Breach>()
            ? new(actor.Get<Breach>().Progress, actor.Get<Breach>().Required) : default;

        public void WriteEntityState(BinaryWriter writer, Entity entity)
        {
            bool present = entity.Has<Breach>();
            writer.Write(present);
            if (!present) return;
            writer.Write(entity.Get<Breach>().Progress);
            writer.Write(entity.Get<Breach>().Required);
        }
    }

    private sealed class HoldScore(EntityId objective) : IEndOfTickRule
    {
        public int Blue { get; private set; }
        public int Red { get; private set; }

        public void Advance(GameplayTick tick)
        {
            if (!tick.TryGetOwner(objective, out uint owner)) return;
            if (owner == 1) Blue++;
            if (owner == 2) Red++;
        }

        public void WriteState(BinaryWriter writer)
        {
            writer.Write(objective.Value);
            writer.Write(Blue);
            writer.Write(Red);
        }
    }
}
