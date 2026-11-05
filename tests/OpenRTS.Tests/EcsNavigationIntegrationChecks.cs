using OpenRTS.Core;
using static NavigationChecks;

internal static class EcsNavigationIntegrationChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("ECS integration: independent body slots preserve projectile interception and movement after recycling", () =>
        {
            UnitDefinition[] types = [new("producer", 100, movable: false, taskQueues: [new("q")]),
                new("target", 100), new("gun", 100, ["idle", "move", "fire"], weapon:
                    new(8192, 8192, 25, 1000, 0, projectile: ProjectileKind.Cannon, projectileSpeed: 2048))];
            using var world = new SimulationWorld(types, 65536, 24576, initialCapacity: 5, navigationGrid: new(64, 24));
            world.AddInitialUnit(0, new(1024, 1024), 1);
            world.AddInitialUnit(2, new(1024, 4096), 2);
            world.AddInitialUnit(0, new(4096, 4096), 1);
            world.AddInitialUnit(1, new(8192, 4096), 1);
            world.AddInitialUnit(0, new(8192, 10240), 1);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(2), TargetEntity: new(4))]);
            NavigationChecks.Check(world.ProjectileCount == 1, "Fixture did not launch a shell");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(1)),
                .. Enumerable.Range(0, 80).Select(i => new WorldCommand(2, 0, i + 2, CommandKind.Spawn,
                    Position: new(8192 + i * 512, 16384), UnitType: 1))]);
            world.Step();
            var poses = Poses(world);
            NavigationChecks.Check(poses.Single(p => p.Id.Value == 3).Health == 75 && poses.Single(p => p.Id.Value == 4).Health == 100,
                "Spatial candidate identity was confused with a native row or dense handle slot");
            world.Step([new(4, 0, 1, CommandKind.Destroy, new(3)), new(4, 0, 2, CommandKind.Move, new(2), new(4096, 4096))]);
            for (int tick = 5; tick <= 44; tick++) world.Step();
            NavigationChecks.Check(world.MovingCount == 0 && Poses(world).Single(p => p.Id.Value == 2).Current == new WorldPosition(4096, 4096),
                "End updates or route ownership used a recycled body/storage index");
            NavigationChecks.Check(Poses(world).Single(p => p.Id.Value == 4).Health == 100, "A stale body became a projectile target");
        });
    }
}
