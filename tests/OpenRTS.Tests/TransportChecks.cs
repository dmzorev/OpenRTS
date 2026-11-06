using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

internal static class TransportChecks
{
    private static readonly PassengerDefinition Infantry = new("infantry");

    public static void Run(Action<string, Action> test)
    {
        test("Transport: ten infantry board six then four using queued Enter", () =>
        {
            foreach (int workers in new[] { 1, 4 })
            {
                using var world = new SimulationWorld([new("infantry", 100, passenger: Infantry),
                    new("apc", 250, armor: ArmorKind.Mechanical, transport: new(["infantry"], 6, unloadDistance: 768))],
                    32 * 1024, 24 * 1024, navigationGrid: new NavigationGrid(32, 24), navigationWorkers: workers);
                var first = world.AddInitialUnit(1, new(12 * 1024, 9 * 1024), 1);
                var second = world.AddInitialUnit(1, new(20 * 1024, 9 * 1024), 1);
                var infantry = Enumerable.Range(0, 10).Select(i => world.AddInitialUnit(0, new(4 * 1024 + i % 2 * 640, 7 * 1024 + i / 2 * 640), 1)).ToArray();
                var members = new EntitySet(infantry);
                world.Step([new(1, 1, 1, CommandKind.Enter, TargetEntity: first, Recipients: members),
                    new(1, 1, 2, CommandKind.Enter, TargetEntity: second, Recipients: members, Queued: true)]);
                Advance(world, 240);
                var loaded = Poses(world);
                NavigationChecks.Check(loaded.Count(p => p.Carrier == first) == 6 && loaded.Count(p => p.Carrier == second) == 4, "Overflow did not board the second APC");
                var destination = new WorldPosition(25 * 1024, 15 * 1024);
                world.Step([new(world.Tick + 1, 1, 1, CommandKind.UnloadAt, Position: destination, Recipients: new([first, second]))]);
                NavigationChecks.Check(Poses(world).Count(p => p.Carrier.Value != 0) == 10, "APCs unloaded before driving");
                Advance(world, 240);
                NavigationChecks.Check(Poses(world).All(p => p.Carrier.Value == 0), "Multi-transport unload did not unload every passenger");
                NavigationChecks.Check(Poses(world).Where(p => p.Id == first || p.Id == second).All(p => Math.Abs(p.Current.X - destination.X) < 1024 &&
                    Math.Abs(p.Current.Y - destination.Y) < 1024), "APCs did not unload near the requested point");
            }
        });
        test("Transport: weighted slots, targeted unload, neutral ownership and static rally", () =>
        {
            var heavy = new UnitDefinition("heavy", 100, passenger: new("infantry", 2));
            var footprint = new FootprintDefinition(2, 2, [new(0, 0, new(false, false)), new(1, 0, new(false, false)),
                new(0, 1, new(false, false)), new(1, 1, new(false, false))]);
            var building = new UnitDefinition("house", 500, states: ["garrisoned"], movable: false,
                armor: ArmorKind.Building, footprint: footprint, transport: new(["infantry"], 3));
            using var world = new SimulationWorld([heavy, building], 16384, 16384, navigationGrid: new NavigationGrid(16, 16));
            var center = new WorldPosition(5120, 5120);
            var house = world.AddInitialUnit(1, center);
            var a = world.AddInitialUnit(0, center with { X = center.X - 1536 }, 2);
            var b = world.AddInitialUnit(0, center with { X = center.X + 1536 }, 2);
            world.Step([new(1, 2, 1, CommandKind.Enter, TargetEntity: house, Recipients: new([a, b]))]);
            var poses = Poses(world);
            NavigationChecks.Check(poses.Single(p => p.Id == house).Presentation is { Owner: 2, States: 1 } &&
                poses.Single(p => p.Id == a).Carrier == house && poses.Single(p => p.Id == b).Carrier.Value == 0, "Slots or garrison ownership incorrect");
            world.Step([new(2, 2, 1, CommandKind.Unload, house, TargetEntity: a)]);
            NavigationChecks.Check(Poses(world).Single(p => p.Id == house).Presentation is { Owner: 0, States: 0 }, "Empty house did not return to neutral");
            world.Step([new(3, 2, 1, CommandKind.Enter, b, TargetEntity: house)]);
            var goal = new WorldPosition(10000, 6000);
            world.Step([new(4, 2, 1, CommandKind.UnloadAt, house, Position: goal)]);
            poses = Poses(world);
            NavigationChecks.Check(poses.Single(p => p.Id == b) is { Carrier.Value: 0 } exited && exited.Order.Destination == goal &&
                poses.Single(p => p.Id == house).Current == center, "Static unload did not dispatch the passenger");
        });
        test("Transport: firing ports filter mounts and sniper damage spills independently of hull damage", () =>
        {
            foreach (bool spill in new[] { false, true })
            {
                var rifle = new WeaponDefinition(4096, 4096, 4, 20, 0, id: "rifle");
                var forbidden = new WeaponDefinition(4096, 4096, 400, 20, 0, id: "forbidden");
                var sniper = new WeaponDefinition(4096, 4096, 1, 20, 0, mechanicalModifier: 0, passengerDamage: 150, passengerDamageSpill: spill);
                using var world = new SimulationWorld([
                    new("infantry", 100, animations: ["idle", "move", "fire"], weapons: [new("rifle", rifle, FireFromTransport: true), new("forbidden", forbidden)], passenger: Infantry),
                    new("apc", 250, armor: ArmorKind.Mechanical, transport: new(["infantry"], 6, fireFromInside: true)),
                    new("sniper", 1000, animations: ["idle", "move", "fire"], weapon: sniper)], 16000, 16000);
                var carrier = world.AddInitialUnit(1, new(5000, 5000), 1);
                var a = world.AddInitialUnit(0, new(4300, 5000), 1);
                var b = world.AddInitialUnit(0, new(5700, 5000), 1);
                var enemy = world.AddInitialUnit(2, new(8500, 5000), 2);
                world.Step([new(1, 1, 1, CommandKind.Enter, TargetEntity: carrier, Recipients: new([a, b]))]);
                world.Step([new(2, 2, 1, CommandKind.AttackTarget, enemy, TargetEntity: carrier)]);
                var poses = Poses(world);
                NavigationChecks.Check(poses.All(p => p.Id != a) && poses.Single(p => p.Id == b).Health == (spill ? 50 : 100) &&
                    poses.Single(p => p.Id == carrier).Health == 250, "Sniper did not damage cargo independently");
                Advance(world, 8);
                NavigationChecks.Check(Poses(world).Single(p => p.Id == enemy).Health is < 1000 and >= 980, "Only allowed weapons may fire from inside");
                world.Step([new(world.Tick + 1, 1, 1, CommandKind.Destroy, carrier)]);
                NavigationChecks.Check(Poses(world).All(p => p.Id != b), "Destroyed transport left orphan cargo");
            }
        });
    }

    private static EntityPose[] Poses(SimulationWorld world)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses;
    }

    private static void Advance(SimulationWorld world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.Step();
    }
}
