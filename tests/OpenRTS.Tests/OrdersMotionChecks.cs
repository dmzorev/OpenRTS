using OpenRTS.Core;
using static NavigationChecks;

internal static class OrdersMotionChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Orders/motion ECS: FIFO and fractional movement survive archetype removal, growth and slot reuse", () =>
        {
            SimulationWorld Create()
            {
                var slow = new Locomotor("slow", [new(true, true, 333, 1024)]);
                var world = new SimulationWorld([new("u", 100, locomotor: slow),
                    new("producer", 100, locomotor: slow, taskQueues: [new("q")])],
                    65536, 16384, initialCapacity: 4, speedPerTick: 7);
                world.AddInitialUnit(0, new(1024, 1024));
                world.AddInitialUnit(0, new(2048, 1024));
                world.AddInitialUnit(0, new(1024, 2048));
                world.AddInitialUnit(1, new(4096, 2048));
                return world;
            }
            using var control = Create(); using var changed = Create();
            WorldCommand[] orders = [new(1, 0, 1, CommandKind.Move, new(2), new(8192, 1024)),
                new(1, 0, 2, CommandKind.Move, new(2), new(8192, 4096), Queued: true),
                new(1, 0, 3, CommandKind.Move, new(3), new(1073, 2048)),
                new(1, 0, 4, CommandKind.Move, new(3), new(1073, 2105), Queued: true),
                new(1, 0, 5, CommandKind.Move, new(4), new(4145, 2048))];
            control.Step(orders); changed.Step(orders);
            WorldCommand[] mutations = [new(2, 0, 1, CommandKind.Destroy, new(2)),
                .. Enumerable.Range(0, 80).Select(i => new WorldCommand(2, 0, i + 2, CommandKind.Spawn,
                    Position: new(8192 + i * 512, 8192)))];
            for (int tick = 2; tick <= 80; tick++)
            {
                control.Step(); changed.Step(tick == 2 ? mutations : []);
                var expected = Poses(control); var actual = Poses(changed);
                foreach (uint id in new uint[] { 3, 4 })
                    NavigationChecks.Check(actual.Single(p => p.Id.Value == id) == expected.Single(p => p.Id.Value == id), "Storage movement detached an active order, queue or fractional remainder");
                NavigationChecks.Check(actual.Where(p => p.Id.Value >= 5).All(p => p.Current == p.Previous && p.Order.QueuedCount == 0 && p.Order.Phase == OrderPhase.Idle), "New native slots inherited a removed actor's orders or movement");
            }
            NavigationChecks.Check(Poses(changed).Single(p => p.Id.Value == 3).Current == new WorldPosition(1073, 2105) && changed.MovingCount == 0, "FIFO or multi-archetype direct query did not finish");
        });
    }
}
