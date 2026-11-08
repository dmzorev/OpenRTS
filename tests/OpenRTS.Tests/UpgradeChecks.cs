using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class UpgradeChecks
{
    private static EntityPose Pose(SimulationWorld world, EntityId id) => Poses(world).Single(p => p.Id == id);
    private static TaskPose Task(SimulationWorld world)
    {
        var tasks = new TaskPose[world.TaskCount];
        world.CopyTaskPoses(tasks);
        return tasks.Single();
    }

    public static void Run(Action<string, Action> test)
    {
        test("Upgrades: supported mobile conversion locks movement and fire but keeps boarding and unloading", () =>
        {
            var gun = new WeaponDefinition(8192, 8192, 1, 1, 0);
            UnitDefinition[] types =
            [
                new("apc", 100, ["idle", "move", "fire"], ["armed"],
                    weapons: [new("main", gun), new("rocket", gun)], transport: new(["infantry"], 4),
                    upgrades: [new("armed", 0, 6, support: true, replaceWeapons: true, mounts: ["rocket"], passengerFire: true)]),
                new("engineer", 100, constructionRange: 1536),
                new("infantry", 100, ["idle", "move", "fire"], weapons: [new("rifle", gun, FireFromTransport: true)],
                    passenger: new("infantry")),
                new("enemy", 100)
            ];
            using var world = new SimulationWorld(types, 20000, 18000);
            var start = new WorldPosition(8192, 8192);
            var apc = world.AddInitialUnit(0, start, 1);
            var engineer = world.AddInitialUnit(1, new(8192, 9472), 1);
            var infantry = world.AddInitialUnit(2, new(8960, 8192), 1);
            var enemy = world.AddInitialUnit(3, new(12288, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.StartUpgrade, engineer, TargetEntity: apc)]);
            world.Step([
                new(2, 0, 1, CommandKind.Move, apc, new(4096, 4096)),
                new(2, 0, 2, CommandKind.AttackTarget, apc, TargetEntity: enemy),
                new(2, 0, 3, CommandKind.Enter, infantry, TargetEntity: apc)]);
            NavigationChecks.Check(Pose(world, apc).Upgrade.Work == 2 && Pose(world, apc).Current == start &&
                Pose(world, infantry).Carrier == apc && !Fired(world, apc) && !Fired(world, infantry),
                "An upgrading vehicle moved or fired, or blocked boarding");
            world.Step([new(3, 0, 1, CommandKind.Unload, apc)]);
            NavigationChecks.Check(Pose(world, infantry).Carrier == default && Pose(world, apc).Upgrade.Active,
                "Unloading failed during an upgrade");
            world.Step(); world.Step();
            world.Step([new(6, 0, 1, CommandKind.AttackTarget, apc, TargetEntity: enemy)]);
            NavigationChecks.Check(Pose(world, apc).Upgrade.Completed == "armed" && Pose(world, apc).Health == 100,
                "Conversion lost the actor or health");
            bool fired = false, firedInside = false;
            for (int i = 0; i < 8; i++)
            {
                world.Step(i == 0 ? [new(7, 0, 1, CommandKind.Enter, infantry, TargetEntity: apc)] : []);
                firedInside |= Pose(world, infantry).Carrier == apc && Fired(world, infantry);
                foreach (var value in world.Events)
                    if (value.Kind == WorldEventKind.WeaponFired && value.Entity == apc)
                    {
                        NavigationChecks.Check(value.Mount == 1, "Base mount was not replaced");
                        fired = true;
                    }
            }
            NavigationChecks.Check(fired && firedInside, "The enabled mount or passenger did not fire");
        });

        test("Upgrades: an expanded garrison keeps occupants and admits more infantry", () =>
        {
            var footprint = new FootprintDefinition(1, 1, [new(0, 0, new(false))]);
            UnitDefinition[] types =
            [
                new("bunker", 100, states: ["expanded"], movable: false, footprint: footprint,
                    transport: new(["infantry"], 1, true),
                    upgrades: [new("expanded", 0, 3, support: true, @default: true, seats: 2)]),
                new("engineer", 100, constructionRange: 1536),
                new("infantry", 100, passenger: new("infantry"))
            ];
            using var world = new SimulationWorld(types, 20000, 18000);
            var bunker = world.AddInitialUnit(0, new(8704, 8704), 1);
            var engineer = world.AddInitialUnit(1, new(8704, 9984), 1);
            var first = world.AddInitialUnit(2, new(9728, 8704), 1);
            var second = world.AddInitialUnit(2, new(7680, 8704), 1);
            world.Step([
                new(1, 0, 1, CommandKind.Enter, first, TargetEntity: bunker),
                new(1, 0, 2, CommandKind.StartUpgrade, engineer, TargetEntity: bunker)]);
            world.Step([new(2, 0, 1, CommandKind.Enter, second, TargetEntity: bunker)]);
            NavigationChecks.Check(Pose(world, first).Carrier == bunker && Pose(world, second).Carrier == default,
                "The in-progress upgrade changed capacity prematurely");
            world.Step();
            world.Step([new(4, 0, 1, CommandKind.Enter, second, TargetEntity: bunker)]);
            NavigationChecks.Check(Pose(world, first).Carrier == bunker && Pose(world, second).Carrier == bunker,
                "The completed upgrade did not extend capacity or lost an existing passenger");
        });

        test("Upgrades: the factory pauses a live queue and unlocks production on completion", () =>
        {
            UnitDefinition[] types =
            [
                new("factory", 100, states: ["heavy"], movable: false, taskQueues: [new("vehicles")],
                    products: [new("light", "light", "vehicles"), new("tank", "tank", "vehicles")],
                    upgrades: [new("heavy", 100, 3, products: ["tank"])]),
                new("light", 100, build: new(20)),
                new("tank", 100, build: new(20))
            ];
            using var world = new SimulationWorld(types, 20000, 18000, players: [new(1, "Test", 500)]);
            var factory = world.AddInitialUnit(0, new(8192, 8192), 1);
            world.Step([
                new(1, 0, 1, CommandKind.EnqueueTask, factory, Action: "light"),
                new(1, 0, 2, CommandKind.EnqueueTask, factory, Action: "tank")]);
            int work = Task(world).Progress;
            world.Step([new(2, 0, 1, CommandKind.StartUpgrade, factory, TargetEntity: factory)]);
            world.Step();
            NavigationChecks.Check(Task(world).Progress == work && Pose(world, factory).Upgrade.Work == 2,
                "The queue advanced while the factory was upgrading");
            world.Step();
            NavigationChecks.Check(Task(world).Progress > work && Pose(world, factory).Upgrade.Completed == "heavy",
                "Production did not resume after the upgrade");
            world.Step([new(5, 0, 1, CommandKind.EnqueueTask, factory, Action: "tank")]);
            NavigationChecks.Check(world.TaskCount == 2, "The new recipe stayed locked");
        });
    }

    private static bool Fired(SimulationWorld world, EntityId actor) =>
        world.Events.ToArray().Any(e => e.Kind == WorldEventKind.WeaponFired && e.Entity == actor);
}
