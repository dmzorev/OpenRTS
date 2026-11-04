using OpenRTS.Core;
using static NavigationChecks;

internal static class GroupNavigationChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("A dense starting group completes its spatially assigned destinations", () =>
        {
            var grid = new NavigationGrid(30, 20);
            var world = World(grid, Enumerable.Range(0, 36).Select(i => new GridCell(i % 6, i / 6 + 7)).ToArray(), speed: 256);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(23, 10))))]);
            for (int i = 0; i < 2000 && world.MovingCount > 0; i++) { Separated(world); world.Step(); }
            NavigationChecks.Check(world.MovingCount == 0, $"Dense group still has {world.MovingCount} active orders");
            Separated(world);
            NavigationChecks.Check(Poses(world).All(p => p.Current == grid.Center(grid.Index(new((int)(p.Id.Value - 1) % 6 + 21, (int)(p.Id.Value - 1) / 6 + 8)))), "Original relative slots changed");
        });
        test("A group occupies distinct destinations and all members finish", () =>
        {
            var grid = new NavigationGrid(15, 11);
            var starts = Enumerable.Range(0, 9).Select(i => new GridCell(i % 3, i / 3 + 4)).ToArray();
            var a = World(grid, starts, speed: 256);
            var b = World(grid, starts, workers: 4, speed: 256, reverse: true);
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(11, 5))))];
            a.Step(commands); b.Step(commands);
            for (int i = 0; i < 500; i++)
            {
                Separated(a); Separated(b);
                NavigationChecks.Check(a.Report() == b.Report(), "Group depends on executor");
                a.Step(); b.Step();
            }
            NavigationChecks.Check(a.MovingCount == 0 && a.GroupSlotCount == starts.Length, "Group did not settle");
            NavigationChecks.Check(Poses(a).All(p => p.Current.X >= 7 * 1024), "Group finished outside its destination region");
            NavigationChecks.Check(Poses(a).Select(p => p.Current).Distinct().Count() == starts.Length, "Destination positions overlap");
        });
        test("A group clears a one-cell opening without individual global fields", () =>
        {
            var grid = new NavigationGrid(15, 9, Enumerable.Range(0, 9).Where(y => y != 4).Select(y => new GridCell(7, y)));
            var world = World(grid, Enumerable.Range(0, 7).Select(y => new GridCell(1, y + 1)).ToArray(), speed: 256);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(12, 4))))]);
            for (int i = 0; i < 1000; i++) { Separated(world); NavigationChecks.Check(world.NavigationBuiltFields <= 1, "Per-member global fields"); world.Step(); }
            NavigationChecks.Check(world.MovingCount == 0 && Poses(world).All(p => p.Current.X > 7 * 1024), "Group did not clear passage");
        });
        test("Local detours pass opposing units in an open lane", () =>
        {
            var grid = new NavigationGrid(7, 3); var world = World(grid, [new(1, 1), new(5, 1)], speed: 256);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(5, 1)))),
                new(1, 0, 2, CommandKind.Move, new(2), grid.Center(grid.Index(new(1, 1))))]);
            int searches = 0;
            for (int i = 0; i < 200; i++) { searches += world.NavigationLocalSearches; Separated(world); world.Step(); }
            NavigationChecks.Check(searches > 0 && world.MovingCount == 0, "Opposing units remained stuck");
        });
        test("A blocker retreats to a side pocket and both units resume their orders", () =>
        {
            var free = Enumerable.Range(0, 7).Select(x => new GridCell(x, 1)).Append(new(5, 0)).ToHashSet();
            var grid = new NavigationGrid(7, 3, from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 7)
                where !free.Contains(new(x, y)) select new GridCell(x, y));
            var a = World(grid, [new(2, 1), new(3, 1)], speed: 256);
            var b = World(grid, [new(2, 1), new(3, 1)], workers: 4, speed: 256, reverse: true);
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(6, 1)))),
                new(1, 0, 2, CommandKind.Move, new(2), grid.Center(grid.Index(new(0, 1))))];
            a.Step(commands); b.Step(commands); int yields = 0;
            for (int i = 0; i < 300; i++)
            {
                yields += a.NavigationYields;
                NavigationChecks.Check(a.NavigationLocalSearches <= 64 && a.Report() == b.Report(), "Yield budget or determinism");
                Separated(a); Separated(b); a.Step(); b.Step();
            }
            NavigationChecks.Check(yields > 0 && a.MovingCount == 0, "No retreat through the pocket");
        });
        test("A corridor without passing space waits safely", () =>
        {
            var grid = new NavigationGrid(5, 1); var world = World(grid, [new(1, 0), new(2, 0)], speed: 256);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(4)), new(1, 0, 2, CommandKind.Move, new(2), grid.Center(0))]);
            var before = Poses(world);
            for (int i = 0; i < 100; i++) { world.Step(); Separated(world); }
            NavigationChecks.Check(Poses(world).Select(p => p.Current).SequenceEqual(before.Select(p => p.Current)) && world.MovingCount == 2, "Impossible swap was forced");
        });
        test("Group goals project atomically and legacy direct group commands reject before advancing", () =>
        {
            var grid = new NavigationGrid(7, 3, Enumerable.Range(0, 3).Select(y => new GridCell(4, y)));
            var world = World(grid, [new(0, 0), new(1, 0), new(2, 0), new(0, 1), new(1, 1), new(2, 1), new(0, 2)]);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(6, 1))))]);
            NavigationChecks.Check(world.GroupSlotCount == 7 && world.Events.Length == 1 && world.Events[0].Kind == WorldEventKind.DestinationAdjusted, "Unreachable anchor was not corrected to available space");
            world.Step([new(2, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(4, 1))))]);
            NavigationChecks.Check(world.Events.Length == 1 && world.Events[0].Kind == WorldEventKind.DestinationAdjusted && world.GroupSlotCount == 7, "Blocked anchor did not keep a legal packed destination");
            var direct = new SimulationWorld([new("unit", 100)], 10240, 10240);
            NavigationChecks.Reject<ArgumentException>(() => direct.Step([new(1, 0, 1, CommandKind.MoveGroup)]));
            NavigationChecks.Check(direct.Tick == 0, "Invalid group advanced direct world");
        });
        test("Group retarget, terrain changes, spawn and packed removal remain deterministic", () =>
        {
            var grid = new NavigationGrid(12, 8);
            GridCell[] starts = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
            var a = World(grid, starts, speed: 128); var b = World(grid, starts, workers: 4, speed: 128, reverse: true);
            for (int tick = 1; tick <= 256; tick++)
            {
                WorldCommand[] commands = tick switch
                {
                    1 => [new(tick, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(9, 5))))],
                    3 => [new(tick, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(0, 7))))],
                    5 => [new(tick, 0, 1, CommandKind.Destroy, new(2)), new(tick, 0, 2, CommandKind.Spawn, Position: grid.Center(grid.Index(new(11, 7))))],
                    7 => [new(tick, 0, 1, CommandKind.BlockCell, Position: grid.Center(grid.Index(new(9, 5))))],
                    9 => [new(tick, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(4, 6))))],
                    _ => []
                };
                a.Step(commands); b.Step(commands); Separated(a); Separated(b);
                NavigationChecks.Check(a.Report() == b.Report(), "Mixed group commands desynchronized");
            }
            NavigationChecks.Check(a.MovingCount == 0, "Retargeted group failed to settle");
        });
        test("Packed removal copies active detours independently of storage order", () =>
        {
            var grid = new NavigationGrid(7, 3);
            GridCell[] starts = [new(0, 0), new(6, 2), new(1, 1), new(5, 1)];
            var a = World(grid, starts, speed: 128); var b = World(grid, starts, workers: 4, speed: 128, reverse: true);
            WorldCommand[] move = [new(1, 0, 1, CommandKind.Move, new(3), grid.Center(grid.Index(new(5, 1)))),
                new(1, 0, 2, CommandKind.Move, new(4), grid.Center(grid.Index(new(1, 1))))];
            a.Step(move); b.Step(move);
            for (int tick = 2; tick <= 200; tick++)
            {
                a.Step(tick == 24 ? [new(tick, 0, 1, CommandKind.Destroy, new(1)), new(tick, 0, 2, CommandKind.Destroy, new(2))] : []);
                b.Step(tick == 24 ? [new(tick, 0, 1, CommandKind.Destroy, new(2)), new(tick, 0, 2, CommandKind.Destroy, new(1))] : []);
                NavigationChecks.Check(a.Report() == b.Report(), "Packed active paths differ");
                Separated(a); Separated(b);
            }
            NavigationChecks.Check(a.MovingCount == 0, "Detour lost after packed removal");
        });
        test("Groups reach their own shoreline and public goals remain cell-bounded", () =>
        {
            var grid = new NavigationGrid(9, 3, Enumerable.Range(0, 3).Select(y => new GridCell(4, y)));
            var world = World(grid, [new(0, 1), new(1, 1)]);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(7, 1))))]);
            int failures = world.Events.ToArray().Count(e => e.Kind == WorldEventKind.DestinationAdjusted);
            for (int i = 0; i < 8; i++) { world.Step(); failures += world.Events.ToArray().Count(e => e.Kind == WorldEventKind.DestinationAdjusted); }
            NavigationChecks.Check(world.MovingCount == 0 && failures == 1 && Poses(world).All(p => p.Current.X < 4096), "Members failed to reach their own shoreline");
            var navigation = new GridNavigator(grid);
            NavigationChecks.Reject<ArgumentOutOfRangeException>(() => navigation.Prepare([grid.CellCount]));
        });
    }
}
