using System.Text.Json;
using OpenRTS.Core;

internal static class ConditionGrantChecks
{
    private static readonly EntityId Shooter = new(1), Enemy = new(2);
    private const GameplayConditions Disabled = GameplayConditions.WeaponsDisabled;

    internal static void Run(Action<string, Action> test)
    {
        test("Conditions: overlapping grants expire independently before commands and replay with frames", () =>
        {
            WorldCommand[] journal =
            [
                new(1, 0, 1, CommandKind.AttackTarget, Shooter, TargetEntity: Enemy),
                new(1, 0, 2, CommandKind.Move, new(3), new(9728, 6656)),
                Grant(2, 1, Shooter, 3),
                Grant(3, 1, Shooter, 4),
                new(4, 0, 1, CommandKind.SetVisualStates, Shooter),
                Revoke(5, 1, Shooter, 1),
                Revoke(6, 1, Enemy, 2),
                Grant(8, 1, Shooter, 0),
                Grant(8, 2, Shooter, 5),
                Revoke(9, 1, Shooter, 3),
                Revoke(10, 1, Shooter, 4),
                new(11, 0, 1, CommandKind.SetVisualStates, Shooter, States: 1)
            ];
            using var a = World(1, withScout: true);
            using var b = World(4, withScout: true);
            var frames = new FrameExchange();
            var events = new List<WorldEvent>();
            var shots = new List<long>();
            for (int tick = 1; tick <= 11; tick++)
            {
                var batch = journal.Where(command => command.Tick == tick).ToArray();
                a.Step(batch);
                b.Step(batch);
                if (tick == 1) NavigationChecks.Check(b.NavigationBuiltFields == 1, "The fixture did not use a route job");
                bool active = tick is >= 2 and <= 6 or 8 or 9;
                NavigationChecks.Check(a.TryGetConditions(Shooter, out var facts) &&
                    facts == (active ? Disabled : GameplayConditions.None), $"Wrong condition on tick {tick}");
                NavigationChecks.Check(frames.TryPublish(b), "Could not publish the condition frame");
                using var frame = frames.AcquireLatest()!;
                var pose = frame.Poses.ToArray().Single(pose => pose.Id == Shooter);
                NavigationChecks.Check(pose.Presentation.States == (active || tick == 11 ? 1u : 0u),
                    $"The visual projection disagrees on tick {tick}");
                NavigationChecks.Check(a.Report() == b.Report() && a.Events.SequenceEqual(b.Events),
                    "Workers or publication changed the result");
                foreach (var item in a.Events)
                {
                    events.Add(item);
                    if (item.Kind == WorldEventKind.WeaponFired) shots.Add(tick);
                }
            }
            NavigationChecks.Check(shots.SequenceEqual(new long[] { 1, 7, 10, 11 }), "Weapons fired inside a disable interval");
            NavigationChecks.Check(events.Where(e => e.Kind == WorldEventKind.ConditionGranted).Select(e => e.ConditionGrantId)
                .SequenceEqual(new long[] { 1, 2, 3, 4 }), "Grant IDs were reused or ordered incorrectly");
            NavigationChecks.Check(events.Where(e => e.Kind == WorldEventKind.ConditionRevoked).Select(e => e.ConditionGrantId)
                .SequenceEqual(new long[] { 3, 4 }), "Revoking an expired or foreign token removed a grant");
            NavigationChecks.Check(events.Where(e => e.Kind == WorldEventKind.ActionUnavailable).Select(e => e.Tick)
                .SequenceEqual(new long[] { 5, 6 }), "Expired tokens must disappear before commands");

            var json = JsonSerializer.Serialize(journal, EcsMigrationChecks.TraceOptions);
            var restored = JsonSerializer.Deserialize<WorldCommand[]>(json, EcsMigrationChecks.TraceOptions)!;
            NavigationChecks.Check(restored.SequenceEqual(journal), "The JSON journal lost condition payloads");
            using var session = new SimulationSession(() => World(4, withScout: true), restored, 11, realtime: false);
            NavigationChecks.Check(session.Completion.GetAwaiter().GetResult() == a.Report() &&
                session.GetCommandLog().SequenceEqual(journal), "Session replay changed the condition journal");
            var replay = new List<WorldEvent>();
            while (session.TryReadEvent(out var item)) replay.Add(item);
            NavigationChecks.Check(replay.SequenceEqual(events), "Session replay changed condition events");
        });

        test("Conditions: rejected commands preserve the tick, removed entities lose grants and tokens never repeat", () =>
        {
            using var a = World(1);
            using var b = World(1);
            var grant = Grant(1, 1, Shooter, 3);
            foreach (var invalid in new[]
            {
                grant with { Condition = GameplayConditions.LowPower },
                grant with { Condition = GameplayConditions.Constructing | Disabled },
                grant with { DurationTicks = -1 },
                grant with { DurationTicks = 1000001 },
                grant with { Recipients = new([Shooter, Enemy]) },
                Revoke(1, 1, Shooter, 0)
            })
                NavigationChecks.Reject<ArgumentException>(() => a.Step([grant with { Sequence = 2 }, invalid]));
            NavigationChecks.Check(a.Tick == 0 && a.Report() == b.Report(), "Invalid grants changed the world");

            a.Step([grant, Grant(1, 2, Shooter, 0)]);
            b.Step([grant with { DurationTicks = 8 }, Grant(1, 2, Shooter, 0)]);
            NavigationChecks.Check(a.TryGetConditions(Shooter, out var facts) && facts == Disabled &&
                b.TryGetConditions(Shooter, out facts) && facts == Disabled && a.Report() != b.Report(),
                "Equal active flags must hash different expirations");

            var remove = new WorldCommand(2, 0, 3, CommandKind.Remove, Shooter);
            a.Step([remove]);
            b.Step([Revoke(2, 1, Shooter, 1), Revoke(2, 2, Shooter, 2), remove]);
            NavigationChecks.Check(!a.TryGetConditions(Shooter, out _) && a.Report() == b.Report(),
                "Removing the entity left grant state behind");

            EntityId replacement = new(3);
            a.Step([new(3, 0, 1, CommandKind.Spawn, Position: new(2048, 2048), Owner: 1),
                Revoke(3, 2, replacement, 2), Grant(3, 3, Shooter, 1), Grant(3, 4, replacement, 1)]);
            NavigationChecks.Check(a.Events.ToArray().Single(e => e.Kind == WorldEventKind.ConditionGranted) is
                { ConditionGrantId: 3 } granted && granted.Entity == replacement, "A stale or missing target consumed a token");
        });
    }

    private static WorldCommand Grant(long tick, long sequence, EntityId entity, int duration) =>
        new(tick, 0, sequence, CommandKind.GrantCondition, entity, DurationTicks: duration) { Condition = Disabled };

    private static WorldCommand Revoke(long tick, long sequence, EntityId entity, long id) =>
        new(tick, 0, sequence, CommandKind.RevokeCondition, entity) { ConditionGrantId = id };

    private static SimulationWorld World(int workers, bool withScout = false)
    {
        var shooter = new UnitDefinition("gun", 100, ["idle", "fire"], ["weapons-disabled"], movable: false,
            weapon: new(4096, 4096, 1, 1, 0));
        var world = new SimulationWorld([shooter, new("target", 100)], 16384, 8192,
            navigationGrid: new(16, 8, [new(6, 6)]), navigationWorkers: workers, reverseNavigationJobs: workers > 1);
        world.AddInitialUnit(0, new(2048, 2048), 1);
        world.AddInitialUnit(1, new(4096, 2048), 2);
        if (withScout) world.AddInitialUnit(1, new(4608, 6656), 1);
        return world;
    }
}
