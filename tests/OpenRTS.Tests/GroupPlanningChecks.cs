using OpenRTS.Core;
using OpenRTS.Content;
using static NavigationChecks;

internal static class GroupPlanningChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("1000 crowded members all clear the diagnostic wall and fill the destination", () =>
        {
            var catalog = UnitCatalog.Load([new("unit", "unit: {Health: {HP: 100}}")]);
            var world = NavigationScenario.Create(catalog, 1000);
            world.Step(NavigationScenario.Commands(1000, 1));
            int failures = 0;
            for (int i = 1; i < 32768 && world.MovingCount > 0; i++)
            {
                world.Step();
                foreach (var value in world.Events) if (value.Kind == WorldEventKind.NoRoute) failures++;
            }
            NavigationChecks.Check(world.MovingCount == 0 && failures == 0, $"Crowded wall test stopped with {world.MovingCount} pending orders and {failures} failures");
            var grid = world.NavigationGrid!;
            var expected = Enumerable.Range(0, 1000).Select(i => grid.Center(grid.Index(new(grid.Width - 32 + i % 32, grid.Height / 2 - 15 + i / 32)))).ToHashSet();
            NavigationChecks.Check(expected.SetEquals(Poses(world).Select(p => p.Current)), "The compact destination was not actually filled");
        });
        test("A nearly filled compact area accepts an outside member without a physical permutation cycle", () =>
        {
            var grid = new NavigationGrid(11, 11);
            GridCell[] starts = [new(4, 4), new(5, 4), new(6, 4), new(4, 5), new(6, 5), new(4, 6), new(5, 6), new(6, 6), new(2, 5)];
            var world = World(grid, starts, speed: 128);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(5, 5))))]);
            for (int i = 0; i < 500 && world.MovingCount > 0; i++) { Separated(world); world.Step(); }
            NavigationChecks.Check(world.MovingCount == 0, "A compact destination permutation remained stuck");
            var expected = Enumerable.Range(0, 9).Select(i => grid.Center(grid.Index(new(4 + i % 3, 4 + i / 3)))).ToHashSet();
            NavigationChecks.Check(expected.SetEquals(Poses(world).Select(p => p.Current)), "Local reassignment changed the destination set");
        });
        test("Free same-direction rows advance as a convoy when another row is blocked", () =>
        {
            var grid = new NavigationGrid(16, 10);
            var world = World(grid, Enumerable.Range(0, 9).Select(i => new GridCell(1 + i % 3, 2 + i / 3)).ToArray(), speed: 128);
            var before = Poses(world).ToDictionary(p => p.Id, p => p.Current);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(8, 3)))),
                new(1, 0, 2, CommandKind.Move, new(9), before[new(9)])]);
            foreach (var pose in Poses(world).Where(p => p.Id.Value <= 6))
                NavigationChecks.Check(pose.Current.X == before[pose.Id].X + 128 && pose.Current.Y == before[pose.Id].Y, "Only the front unit moved in a free convoy");
            Separated(world);
        });
        test("A follower does not pull a queued forward-moving unit into a side pocket", () =>
        {
            var free = Enumerable.Range(0, 7).Select(x => new GridCell(x, 1)).Append(new(4, 0)).ToHashSet();
            var grid = new NavigationGrid(7, 3, from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 7)
                where !free.Contains(new(x, y)) select new GridCell(x, y));
            var world = World(grid, [new(1, 1), new(4, 1), new(5, 1)], speed: 256);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(6, 1)))),
                new(1, 0, 2, CommandKind.Move, new(2), grid.Center(grid.Index(new(5, 1))))]);
            int yields = 0;
            for (int i = 0; i < 100; i++) { yields += world.NavigationYields; Separated(world); world.Step(); }
            NavigationChecks.Check(yields == 0 && Poses(world).Single(p => p.Id.Value == 2).Current.Y == grid.Center(grid.Index(new(4, 1))).Y && Poses(world).Single(p => p.Id.Value == 2).Current.X <= grid.Center(grid.Index(new(4, 1))).X + 512, "A rear unit displaced its queued leader");
        });
        test("An idle blocker leaves its own goal to yield and then returns", () =>
        {
            var free = Enumerable.Range(0, 7).Select(x => new GridCell(x, 1)).Append(new(3, 0)).ToHashSet();
            var grid = new NavigationGrid(7, 3, from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 7)
                where !free.Contains(new(x, y)) select new GridCell(x, y));
            var world = World(grid, [new(1, 1), new(3, 1)], speed: 256);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(5, 1))))]);
            int yields = 0;
            for (int i = 0; i < 160; i++) { yields += world.NavigationYields; Separated(world); world.Step(); }
            var poses = Poses(world).OrderBy(p => p.Id.Value).ToArray();
            NavigationChecks.Check(yields > 0 && world.MovingCount == 0 && poses[0].Current == grid.Center(grid.Index(new(5, 1))) && poses[1].Current == grid.Center(grid.Index(new(3, 1))), "An idle unit canceled its forced yield at its old goal");
        });
        test("Diagnostic wall group completes its compact destination", () =>
        {
            var catalog = UnitCatalog.Load([new("unit", "unit: {Health: {HP: 100}}")]);
            var world = NavigationScenario.Create(catalog, 36);
            world.Step(NavigationScenario.Commands(36, 1));
            for (int i = 1; i < 1400 && world.MovingCount > 0; i++) world.Step();
            NavigationChecks.Check(world.MovingCount == 0, "Diagnostic crowd remained stuck: " + world.MovingCount);
            var grid = world.NavigationGrid!;
            var expected = Enumerable.Range(0, 36).Select(i => grid.Center(grid.Index(new(grid.Width - 10 + i % 6, grid.Height / 2 - 2 + i / 6)))).ToHashSet();
            NavigationChecks.Check(expected.SetEquals(Poses(world).Select(p => p.Current)), "Stopping due to failure was mistaken for arrival");
        });
        test("A scattered crowd gathers at its center instead of preserving its original offsets", () =>
        {
            var grid = new NavigationGrid(81, 81);
            GridCell[] starts = Enumerable.Range(0, 9).Select(i => new GridCell(10 + i % 3 * 30, 10 + i / 3 * 30)).ToArray();
            var world = World(grid, starts, speed: 256);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(40, 40))))]);
            for (int tick = 0; tick < 1500 && world.MovingCount > 0; tick++) { Separated(world); world.Step(); }
            NavigationChecks.Check(world.MovingCount == 0, "Crowd failed to gather");
            foreach (var pose in Poses(world))
            {
                int i = (int)pose.Id.Value - 1;
                NavigationChecks.Check(pose.Current == grid.Center(grid.Index(new(39 + i % 3, 39 + i / 3))), "Gathering retained old extent or reversed spatial assignment");
            }
        });
        test("Compact regular groups keep spatial order and synchronized progress in eight directions", () =>
        {
            var grid = new NavigationGrid(31, 31);
            foreach (var delta in new GridCell[] { new(7, 0), new(-7, 0), new(0, 7), new(0, -7), new(7, 5), new(-7, 5), new(7, -5), new(-7, -5) })
            {
                var world = World(grid, Enumerable.Range(0, 9).Select(i => new GridCell(14 + i % 3, 14 + i / 3)).ToArray(), speed: 128);
                var initial = Poses(world).ToDictionary(p => p.Id, p => p.Current);
                world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(15 + delta.X, 15 + delta.Y))))]);
                var reference = new SimulationWorld([new("unit", 100)], grid.Width * grid.CellSize, grid.Height * grid.CellSize, speedPerTick: 128);
                reference.AddInitialUnit(0, initial[new(1)]);
                reference.Step([new(1, 0, 1, CommandKind.Move, new(1), initial[new(1)] with { X = initial[new(1)].X + delta.X * 1024, Y = initial[new(1)].Y + delta.Y * 1024 })]);
                for (int tick = 1; tick <= 128; tick++)
                {
                    var poses = Poses(world); var first = poses[0];
                    int dx = first.Current.X - initial[first.Id].X, dy = first.Current.Y - initial[first.Id].Y;
                    foreach (var pose in poses)
                    {
                        NavigationChecks.Check(pose.Current.X - initial[pose.Id].X == dx && pose.Current.Y - initial[pose.Id].Y == dy, "Members moved at different pace or swapped slots");
                        NavigationChecks.Check(delta.X == 0 || Math.Sign(dx) == Math.Sign(delta.X) || dx == 0, "Backwards X movement");
                        NavigationChecks.Check(delta.Y == 0 || Math.Sign(dy) == Math.Sign(delta.Y) || dy == 0, "Backwards Y movement");
                    }
                    Separated(world);
                    NavigationChecks.Check(first.Current == Poses(reference)[0].Current && world.MovingCount == reference.MovingCount * 9, "Common translation differs from normalized direct motion");
                    if (world.MovingCount == 0) break;
                    world.Step(); reference.Step();
                }
                NavigationChecks.Check(world.MovingCount == 0, "Common translation failed to finish");
                foreach (var pose in Poses(world)) NavigationChecks.Check(pose.Current == initial[pose.Id] with { X = initial[pose.Id].X + delta.X * 1024, Y = initial[pose.Id].Y + delta.Y * 1024 }, "Wrong assigned endpoint");
            }
        });
        test("10000 dense members advance together without backward gathering or cached allocations", () =>
        {
            var grid = new NavigationGrid(180, 150);
            var world = World(grid, Enumerable.Range(0, 10000).Select(i => new GridCell(i % 100 + 4, i / 100 + 4)).ToArray(), speed: 128);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(103, 53))))]);
            for (int i = 0; i < 24; i++) world.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++) world.Step();
            NavigationChecks.Check(GC.GetAllocatedBytesForCurrentThread() - bytes == 0, "Synchronized translation allocates per tick");
            foreach (var pose in Poses(world))
            {
                int i = (int)pose.Id.Value - 1;
                NavigationChecks.Check(pose.Current.X == (i % 100 + 4) * 1024 + 512 + 41 * 128 && pose.Current.Y == (i / 100 + 4) * 1024 + 512, "Dense units waited or gathered before moving");
            }
            NavigationChecks.Check(world.NavigationWaitingCount == 0, "A dense row waited for its own moving neighbor");
            for (int tick = 41; tick < 400; tick++) world.Step();
            NavigationChecks.Check(world.MovingCount == 0 && world.Tick == 400, "10000 dense units did not all arrive at the common travel time");
        });
        test("A compact destination remains complete after a one-cell passage with local reassignment", () =>
        {
            var grid = new NavigationGrid(19, 11, Enumerable.Range(0, 11).Where(y => y != 5).Select(y => new GridCell(9, y)));
            GridCell[] starts = Enumerable.Range(0, 9).Select(i => new GridCell(2 + i % 3, 4 + i / 3)).ToArray();
            var a = World(grid, starts, speed: 256); var b = World(grid, starts, workers: 4, speed: 256, reverse: true);
            WorldCommand[] command = [new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(15, 5))))];
            a.Step(command); b.Step(command); bool deformed = false;
            for (int i = 0; i < 1600 && (a.MovingCount > 0 || b.MovingCount > 0); i++)
            {
                deformed |= !a.GroupTranslating;
                NavigationChecks.Check(a.Report() == b.Report(), "Shared route depends on execution settings");
                Separated(a); Separated(b); a.Step(); b.Step();
            }
            NavigationChecks.Check(deformed && a.MovingCount == 0 && b.MovingCount == 0, "Group failed to deform/finish");
            var expected = Enumerable.Range(0, 9).Select(i => grid.Center(grid.Index(new(14 + i % 3, 4 + i / 3)))).ToHashSet();
            NavigationChecks.Check(expected.SetEquals(Poses(a).Select(p => p.Current)), "The compact destination changed after passage");
        });
        test("Mid-segment retarget, destruction and spawn protect exact body occupancy", () =>
        {
            var grid = new NavigationGrid(15, 12); var a = World(grid, [new(2, 2), new(3, 2), new(2, 3), new(3, 3)], speed: 128);
            a.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(10, 2))))]);
            a.Step([new(2, 0, 1, CommandKind.Destroy, new(2)),
                new(2, 0, 2, CommandKind.BlockCell, Position: grid.Center(grid.Index(new(2, 2)))),
                new(2, 0, 3, CommandKind.Spawn, Position: Poses(a).Single(p => p.Id.Value == 1).Current)]);
            NavigationChecks.Check(a.Events.ToArray().Count(e => e.Kind == WorldEventKind.CellOccupied) == 2, "Destroying a neighbor lost the remaining body occupancy");
            a.Step([new(3, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(5, 9))))]);
            for (int i = 0; i < 160 && a.MovingCount > 0; i++) { Separated(a); a.Step(); }
            NavigationChecks.Check(a.MovingCount == 0 && a.Count == 3, "Membership changes froze a synchronized edge");
        });
        test("Boundary placement stays compact and blocked anchors project explicitly", () =>
        {
            var grid = new NavigationGrid(8, 8, [new(7, 7)]); var world = World(grid, [new(1, 1), new(2, 1), new(1, 2), new(2, 2)], speed: 1024);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(7, 7))))]);
            NavigationChecks.Check(world.Events.Length == 1 && world.Events[0].Kind == WorldEventKind.DestinationAdjusted && world.GroupSlotCount == 4, "Blocked anchor was not explicitly corrected");
            world.Step([new(2, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(7, 1))))]);
            for (int i = 0; i < 8 && world.MovingCount > 0; i++) { Separated(world); world.Step(); }
            var poses = Poses(world).OrderBy(p => p.Id.Value).ToArray();
            NavigationChecks.Check(poses[1].Current.X - poses[0].Current.X == 1024 && poses[2].Current.Y - poses[0].Current.Y == 1024 && poses[1].Current.X == 7 * 1024 + 512, "Independent clamping collapsed or flipped slots");
        });
    }
}
