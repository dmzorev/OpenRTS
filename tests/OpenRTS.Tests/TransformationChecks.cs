using OpenRTS.Core;
using static NavigationChecks;

internal static class TransformationChecks
{
    private static readonly WorldPosition Start = new(3072, 4096);
    private static readonly WorldPosition Site = new(8192, 8704);
    private static readonly FootprintDefinition Yard = new(3, 2,
        [new(0, 0, new(false)), new(1, 0, new(false)), new(2, 0, new(false)),
         new(0, 1, new(false)), new(1, 1, new(false)), new(2, 1, new(false))]);

    private static readonly ActorTransformation[] Recipes =
    [
        new("deploy", "mcv", "yard", new("prepare", 2, new FacingDirection(0, 1)), new("unfold", 3), AllowRotation: true),
        new("pack", "yard", "mcv", new("fold", 2)),
        new("convert", "mcv", "scout"),
        new("upgrade", "yard", "advanced")
    ];

    private static SimulationWorld World(int workers = 1, IEndOfTickRule? rule = null) =>
        SimulationWorld.CreateWithMechanics(new()
        {
            Transformations = Recipes,
            EndOfTick = rule is null ? null : new("tests.transform", 1, () => rule)
        },
        [
            new("mcv", 100, ["idle", "move", "prepare"]),
            new("yard", 250, ["idle", "unfold", "fold"], ["constructing", "lowpower"], movable: false, footprint: Yard,
                economy: new(100, 5, 2), constructionRange: 8192,
                products: [new("build", "advanced", "", OnSite: true)]),
            new("scout", 50),
            new("advanced", 300, movable: false, footprint: Yard, build: new(2, kind: ProductionKind.PlaceBuilding)),
            new("gun", 100, ["idle", "fire"], movable: false, weapon: new(2048, 2048, 37, 1000, 0))
        ], 16 * 1024, 16 * 1024, speedPerTick: 256, navigationGrid: new(16, 16),
        navigationWorkers: workers, reverseNavigationJobs: workers > 1);

    private static WorldCommand Order(SimulationWorld world, EntityId id, string action, WorldPosition? position = null, int rotation = 0) =>
        new(world.Tick + 1, 0, 1, CommandKind.Transform, id, position ?? default, Action: action)
        { TransformHere = position is null, QuarterTurns = rotation };

    private static EntityPose Pose(SimulationWorld world, EntityId id) => Poses(world).Single(pose => pose.Id == id);

    private static WorldEvent UntilEvent(SimulationWorld world, WorldEventKind kind, int limit = 120)
    {
        for (int i = 0; i < limit; i++)
        {
            foreach (var value in world.Events)
                if (value.Kind == kind) return value;
            world.Step();
        }
        throw new Exception($"Did not see {kind} by tick {world.Tick}.");
    }

    public static void Run(Action<string, Action> test)
    {
        test("Transformation: drive, clear friendly and neutral bodies, animate and pack with a follow-up move", () =>
        {
            using var world = World();
            var source = world.AddInitialUnit(0, Start, 1);
            var friendly = world.AddInitialUnit(2, Site, 1);
            var neutral = world.AddInitialUnit(2, new(8192, 7808));
            var gun = world.AddInitialUnit(4, new(2048, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, gun, TargetEntity: source)]);
            UntilEvent(world, WorldEventKind.Damaged);
            int health = Pose(world, source).Health;
            NavigationChecks.Check(health is > 0 and < 100, "Source was not damaged.");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Remove, gun), Order(world, source, "deploy", Site, 1) with { Sequence = 2 }]);
            var planned = Pose(world, source).Transformation;
            var grid = world.NavigationGrid!;
            int blocked = grid.Index(new(8, 8));
            NavigationChecks.Check(planned is { Phase: TransformationPhase.Approaching, UnitType: 1, QuarterTurns: 1 } && planned.Position == Site &&
                !grid.IsBlocked(blocked), "Approaching project did not publish the rotated blueprint or blocked terrain too soon.");

            UntilEvent(world, WorldEventKind.AnimationPlayed);
            NavigationChecks.Check(Pose(world, source).Transformation.Phase == TransformationPhase.Preparing &&
                Pose(world, source).Presentation.BodyFacing == new FacingDirection(0, 1) &&
                Pose(world, friendly).Current != Site && Pose(world, neutral).Current != new WorldPosition(8192, 7808) &&
                !world.NavigationGrid!.IsBlocked(blocked), "Preparation ran before clearance or lost its facing.");

            var swap = UntilEvent(world, WorldEventKind.ActorTransformed);
            var building = swap.TargetEntity;
            NavigationChecks.Check(swap.Entity == source && building != source && world.Count == 3 && Poses(world).All(p => p.Id != source) &&
                !world.Events.ToArray().Any(e => e.Kind is WorldEventKind.Destroyed or WorldEventKind.Removed or WorldEventKind.Spawned),
                "Replacement did not publish a single identity transition.");
            var finishing = Pose(world, building);
            NavigationChecks.Check(finishing.Health == health * 250 / 100 && finishing.Presentation.Owner == 1 &&
                finishing.Transformation.Phase == TransformationPhase.Finishing &&
                world.TryGetConditions(building, out var conditions) && conditions.HasFlag(GameplayConditions.Constructing) &&
                world.NavigationGrid!.IsBlocked(blocked) && !grid.IsBlocked(blocked), "Replacement lost health, owner, lock or the terrain revision.");
            var goal = new WorldPosition(11264, 8704);
            world.Step([Order(world, building, "pack", goal)]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.ActionUnavailable), "Finishing yard could transform again.");
            UntilEvent(world, WorldEventKind.TransformationCompleted);
            world.Step([Order(world, building, "pack", goal)]);
            var packed = UntilEvent(world, WorldEventKind.ActorTransformed).TargetEntity;
            NavigationChecks.Check(packed != building && Pose(world, packed).Order.Destination == goal && !world.NavigationGrid!.IsBlocked(blocked),
                "Pack did not release the stamp and give the replacement the move.");
            for (int i = 0; i < 20; i++) world.Step();
            NavigationChecks.Check(Pose(world, packed).Current == goal, "Replacement did not move to the requested point.");
        });

        test("Transformation: terrain stays open in transit and a later building or enemy cancels deployment", () =>
        {
            foreach (bool building in new[] { false, true })
            {
                using var world = World();
                var source = world.AddInitialUnit(0, Start, 1);
                var blocker = world.AddInitialUnit(2, new(11264, 8704), 2);
                var builder = building ? world.AddInitialUnit(1, new(3584, 12288), 1) : default;
                world.Step([Order(world, source, "deploy", Site, 1)]);
                NavigationChecks.Check(Pose(world, source).Transformation.Phase == TransformationPhase.Approaching, "Deploy was not planned.");
                world.Step([building
                    ? new WorldCommand(2, 0, 1, CommandKind.PlanConstruction, builder, Site, Action: "build") { QuarterTurns = 1 }
                    : new(2, 0, 1, CommandKind.Move, blocker, Site)]);
                UntilEvent(world, WorldEventKind.TransformationCancelled);
                NavigationChecks.Check(Pose(world, source).Transformation.Phase == TransformationPhase.None &&
                    Pose(world, source).UnitType == 0 && Pose(world, blocker).Presentation.Owner == 2,
                    "A blocker displaced the original actor or the cancellation left a blueprint.");
                if (building) NavigationChecks.Check(Poses(world).Any(p => p.UnitType == 3), "The plan blocked an ordinary construction project.");
                else NavigationChecks.Check(Pose(world, blocker).Current == Site, "Hostile blocker was prevented from entering or displaced.");
            }
        });

        test("Transformation: stopping and destruction clear pending plans", () =>
        {
            using var world = World();
            var mcv = world.AddInitialUnit(0, Start, 1);
            world.Step([Order(world, mcv, "deploy", Site, 1)]);
            world.Step([new(2, 0, 1, CommandKind.Stop, mcv)]);
            NavigationChecks.Check(Pose(world, mcv).Transformation.Phase == TransformationPhase.None &&
                world.Events.ToArray().Any(e => e.Kind == WorldEventKind.TransformationCancelled), "Stop left a pending deployment.");

            world.Step([Order(world, mcv, "deploy", rotation: 1)]);
            UntilEvent(world, WorldEventKind.AnimationPlayed);
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Destroy, mcv)]);
            NavigationChecks.Check(world.Count == 0 && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Destroyed),
                "Destroy during preparation did not remove the original actor.");
            for (int i = 0; i < 4; i++) world.Step();
            NavigationChecks.Check(world.Count == 0 && world.Events.IsEmpty, "Destroyed actor was replaced after its animation deadline.");
        });

        test("Transformation: script and command support unit and building replacements with deterministic journals", () =>
        {
            var results = new List<(SimulationReport Report, WorldEvent[] Events)>();
            foreach (int workers in new[] { 1, 4 })
            {
                using var world = World(workers, new OnTickRule());
                var mcv = world.AddInitialUnit(0, Start, 1);
                var yard = world.AddInitialUnit(1, new(8704, 8192), 1);
                var events = new List<WorldEvent>();
                world.Step([Order(world, yard, "upgrade")]);
                events.AddRange(world.Events.ToArray());
                world.Step();
                events.AddRange(world.Events.ToArray());
                var replacements = events.Where(e => e.Kind == WorldEventKind.ActorTransformed).ToArray();
                NavigationChecks.Check(replacements.Length == 2 && replacements[0].Entity == yard && replacements[0].UnitType == 3 &&
                    replacements[1].Entity == mcv && replacements[1].UnitType == 2 && world.Count == 2,
                    "Scripted unit conversion or building replacement failed.");
                results.Add((world.Report(), events.ToArray()));
            }
            NavigationChecks.Check(results[0].Report == results[1].Report && results[0].Events.SequenceEqual(results[1].Events),
                "Worker count changed the transition journal.");
        });
    }

    private sealed class OnTickRule : IEndOfTickRule
    {
        public void Advance(GameplayTick tick)
        {
            if (tick.Tick == 1) NavigationChecks.Check(tick.TryTransform(new(1), "convert"), "Script could not schedule transformation.");
        }
        public void WriteState(BinaryWriter writer) { }
    }
}
