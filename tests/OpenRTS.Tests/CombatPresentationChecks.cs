using OpenRTS.Core;
using static NavigationChecks;

internal static class CombatPresentationChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Combat/presentation ECS: burst, autocast, animation and owner survive removal, growth and pinned frames", () =>
        {
            var gun = new WeaponDefinition(2048, 2048, 1, 9, 2, burst: 3);
            UnitDefinition[] types = [new("gun", 10000, ["idle", "move", "fire", "work"], ["damaged"], weapon: gun, abilities: [new("burst", "main", AutoCast: true)]),
                new("target", 10000), new("factory", 10000, ["idle", "move", "fire", "work"], ["damaged"], weapon: gun,
                    abilities: [new("burst", "main", AutoCast: true)], taskQueues: [new("q")])];
            SimulationWorld Create()
            {
                var w = new SimulationWorld(types, 65536, 24576, initialCapacity: 6);
                w.AddInitialUnit(0, new(1024, 1024)); w.AddInitialUnit(0, new(1024, 12288), 1);
                w.AddInitialUnit(1, new(3072, 4096), 2); w.AddInitialUnit(1, new(3072, 8192), 2);
                w.AddInitialUnit(0, new(2048, 4096), 1); w.AddInitialUnit(2, new(2048, 8192), 1);
                return w;
            }
            using var control = Create(); using var changed = Create();
            WorldCommand[] initial = [new(1, 0, 1, CommandKind.HoldPosition, new(5)), new(1, 0, 2, CommandKind.HoldPosition, new(6)),
                new(1, 0, 3, CommandKind.PlayAnimation, new(5), Animation: types[0].AnimationIndex("work"), DurationTicks: 20),
                new(1, 0, 4, CommandKind.SetVisualStates, new(5), States: 1),
                new(1, 0, 5, CommandKind.SetAutoCast, new(2), Action: "burst", Enabled: false),
                new(1, 0, 6, CommandKind.PlayAnimation, new(2), Animation: types[0].AnimationIndex("work"), DurationTicks: 30)];
            control.Step(initial); changed.Step(initial);
            for (int tick = 2; tick <= 4; tick++) { control.Step(); changed.Step(); }
            var frames = new FrameExchange(); frames.TryPublish(changed); using var frozen = frames.AcquireLatest()!;
            var oldPoses = frozen.Poses.ToArray(); var oldWeapons = frozen.Weapons.ToArray();
            NavigationChecks.Check(oldWeapons.Single(p => p.Entity.Value == 5).BurstRemaining > 0, "Fixture did not enter a partial burst");
            int shots = 0;
            for (int tick = 5; tick <= 60; tick++)
            {
                WorldCommand[] common = tick == 12 ? [new(tick, 0, 1, CommandKind.SetAutoCast, new(5), Action: "burst", Enabled: false)] : [];
                WorldCommand[] mutations = tick == 5 ? [new(tick, 0, 1, CommandKind.Destroy, new(2)),
                    .. Enumerable.Range(0, 80).Select(i => new WorldCommand(tick, 0, i + 2, CommandKind.Spawn,
                        Position: new(8192 + i * 512, 16384), Owner: 3))] : common;
                control.Step(common); changed.Step(mutations);
                shots += changed.Events.ToArray().Count(e => e.Kind == WorldEventKind.WeaponFired && e.Entity.Value is 5 or 6);
                var expected = Poses(control); var actual = Poses(changed);
                var expectedWeapons = Weapons(control); var actualWeapons = Weapons(changed);
                foreach (uint id in new uint[] { 3, 4, 5, 6 })
                    NavigationChecks.Check((actual.Single(p => p.Id.Value == id) with { WeaponOffset = 0 }) ==
                        (expected.Single(p => p.Id.Value == id) with { WeaponOffset = 0 }), "Storage movement changed health, ownership, aiming or animation");
                NavigationChecks.Check(actualWeapons.Where(p => p.Entity.Value is 5 or 6).OrderBy(p => p.Entity.Value).SequenceEqual(
                    expectedWeapons.Where(p => p.Entity.Value is 5 or 6).OrderBy(p => p.Entity.Value)), "Burst, cooldown, facing or autocast detached from its actor");
                NavigationChecks.Check(actual.Where(p => p.Id.Value >= 7).All(p => p.Presentation.Owner == 3 && p.Presentation.States == 0 && p.Presentation.Body.Animation == types[0].IdleAnimation) &&
                    actualWeapons.Where(p => p.Entity.Value >= 7).All(p => p.AutoCast && !p.Busy && p.ReadyTick == 0 && p.BurstRemaining == 0 && p.Target.Value == 0), "New slots inherited combat or presentation state");
            }
            NavigationChecks.Check(shots > 0 && Weapons(changed).Single(p => p.Entity.Value == 5).AutoCast == false && Weapons(changed).Single(p => p.Entity.Value == 6).AutoCast, "Actors shared weapon arrays or stopped firing");
            changed.Dispose();
            NavigationChecks.Check(frozen.Poses.SequenceEqual(oldPoses) && frozen.Weapons.SequenceEqual(oldWeapons), "Native lifetime changed a pinned frame");
        });
    }

    private static WeaponPose[] Weapons(SimulationWorld world)
    {
        var poses = new WeaponPose[world.WeaponCount]; world.CopyWeaponPoses(poses); return poses;
    }
}
