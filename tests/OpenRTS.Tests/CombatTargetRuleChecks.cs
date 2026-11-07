using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;

internal static class CombatTargetRuleChecks
{
    private static readonly EntityId Shooter = new(1), Nearest = new(2), Wounded = new(3), Ignored = new(5);
    private const int FragileType = 4;

    internal static void Run(Action<string, Action> test)
    {
        foreach (var kind in new[] { CommandKind.AttackMove, CommandKind.Move })
            test($"Combat targeting: registered priorities during {kind} preserve legality, locks and explicit orders", () =>
            {
                using var builtIn = World(new(), 1);
                using var a = World(Mechanics(), 1);
                using var b = World(Mechanics(), 4);
                using var revised = World(Mechanics(version: 2), 1);
                using var configured = World(Mechanics(weakestFirst: false), 1);
                var initial = a.Report();
                NavigationChecks.Check(initial == b.Report() && initial != revised.Report() &&
                    initial != configured.Report() && initial != builtIn.Report(),
                    "The hash must include rule identity, version and settings, independent of worker count");

                WorldCommand[] move = [new(1, 0, 1, kind, Shooter, new(9216, 4608))];
                builtIn.Step(move);
                Advance(move);
                NavigationChecks.Check(Shot(builtIn).TargetEntity == Nearest && Shot(a).TargetEntity == Wounded,
                    "The rule did not choose the weakest legal target or break the tie by entity ID");

                Advance([new(2, 0, 1, CommandKind.Spawn, Position: new(5632, 4608), UnitType: FragileType, Owner: 2)]);
                NavigationChecks.Check(Shot(a).TargetEntity == Wounded, "Automatic scoring replaced a still-valid target");

                Advance([new(3, 0, 1, CommandKind.AttackTarget, Shooter, TargetEntity: Ignored)]);
                NavigationChecks.Check(Shot(a).TargetEntity == Ignored &&
                    a.Events.ToArray().Select(e => e.Kind).SequenceEqual([WorldEventKind.WeaponFired, WorldEventKind.Damaged, WorldEventKind.Destroyed]) &&
                    !NavigationChecks.Poses(a).Any(pose => pose.Id == Ignored),
                    "Explicit attacks must bypass scoring and use the normal damage and death phases");

                void Advance(WorldCommand[] commands)
                {
                    a.Step(commands);
                    b.Step(commands);
                    NavigationChecks.Check(a.Report() == b.Report() && a.Events.SequenceEqual(b.Events),
                        "Registered targeting depends on worker count");
                }
            });
    }

    private static WorldEvent Shot(SimulationWorld world) =>
        world.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired && e.Entity == Shooter);

    private static GameplayMechanics Mechanics(int version = 1, bool weakestFirst = true) =>
        new() { CombatTargeting = new("example.health-priority", version, () => new HealthPriority(weakestFirst)) };

    private static SimulationWorld World(GameplayMechanics mechanics, int workers)
    {
        var weapon = new WeaponDefinition(4096, 6144, 10, 1, 0);
        UnitDefinition[] types =
        [
            new("shooter", 100, ["idle", "move", "fire"], weapons: [new("main", weapon, FireWhileMoving: true)]),
            new("durable", 100),
            new("wounded", 30),
            new("ignored", 5),
            new("fragile", 1),
            new("invulnerable", 1, destruction: DestructionPolicy.Invulnerable)
        ];
        var world = SimulationWorld.CreateWithMechanics(mechanics, types, 16384, 12288,
            navigationGrid: new(16, 12, [new(2, 4)]), navigationWorkers: workers, reverseNavigationJobs: workers > 1);
        world.AddInitialUnit(0, new(4608, 4608), 1);
        world.AddInitialUnit(1, new(6656, 4608), 2);
        world.AddInitialUnit(2, new(6656, 6656), 2);
        world.AddInitialUnit(2, new(6656, 2560), 2);
        world.AddInitialUnit(3, new(7680, 4608), 2);
        world.AddInitialUnit(FragileType, new(3584, 4608), 1);
        world.AddInitialUnit(FragileType, new(4608, 5632));
        world.AddInitialUnit(5, new(4608, 3584), 2);
        world.AddInitialUnit(FragileType, new(1536, 4608), 2); // Behind the wall.
        world.AddInitialUnit(FragileType, new(10752, 8704), 2); // Outside acquire range.
        return world;
    }

    private sealed class HealthPriority(bool weakestFirst) : ICombatTargetRule
    {
        public long? Score(in CombatTargetQuery query, in CombatTargetCandidate candidate) =>
            candidate.Type.Id == "ignored" ? null : weakestFirst ? -(long)candidate.Health : candidate.Health;

        public void WriteConfiguration(BinaryWriter writer) => writer.Write(weakestFirst);
    }
}
