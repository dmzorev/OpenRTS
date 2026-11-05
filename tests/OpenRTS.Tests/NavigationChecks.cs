using OpenRTS.Content;
using OpenRTS.Core;

internal static class NavigationChecks
{
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    internal static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static SimulationWorld World(NavigationGrid grid, GridCell[] starts, int workers = 1,
        int speed = 1024, bool reverse = false, int budget = 2, int cache = 8)
    {
        var world = new SimulationWorld([new("unit", 100)], grid.Width * grid.CellSize, grid.Height * grid.CellSize,
            initialCapacity: starts.Length, speedPerTick: speed, navigationGrid: grid,
            navigationWorkers: workers, fieldsPerTick: budget, cachedFields: cache, reverseNavigationJobs: reverse);
        foreach (var start in starts) world.AddInitialUnit(0, grid.Center(grid.Index(start)));
        return world;
    }
    internal static EntityPose[] Poses(SimulationWorld world)
    {
        var poses = new EntityPose[world.Count]; world.CopyPoses(poses); return poses;
    }
    internal static void Separated(SimulationWorld world)
    {
        var poses = Poses(world);
        foreach (var pose in poses)
            Check(!world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(pose.Current)), "Entity entered a blocked cell");
        for (int a = 0; a < poses.Length; a++)
            for (int b = a + 1; b < poses.Length; b++)
                for (int alpha = 0; alpha <= 4; alpha++)
                {
                    long ax = (long)poses[a].Previous.X * (4 - alpha) + (long)poses[a].Current.X * alpha;
                    long ay = (long)poses[a].Previous.Y * (4 - alpha) + (long)poses[a].Current.Y * alpha;
                    long bx = (long)poses[b].Previous.X * (4 - alpha) + (long)poses[b].Current.X * alpha;
                    long by = (long)poses[b].Previous.Y * (4 - alpha) + (long)poses[b].Current.Y * alpha;
                    Check((ax - bx) * (ax - bx) + (ay - by) * (ay - by) >= 2048L * 2048, "Interpolated circular bodies overlap");
                }
    }

    public static void Run(Action<string, Action> test)
    {
        test("Navigation maps are immutable, versioned and bounded", () =>
        {
            var original = new NavigationGrid(8, 5);
            int cell = original.Index(new(3, 2)); var modified = original.WithBlocked(cell, true);
            Check(!original.IsBlocked(cell) && modified.IsBlocked(cell) && modified.Revision == 1, "Immutable map revision");
            Check(ReferenceEquals(modified, modified.WithBlocked(cell, true)), "No-op edit should retain revision");
            Reject<ArgumentOutOfRangeException>(() => original.Index(new(8, 0)));
            Reject<ArgumentOutOfRangeException>(() => new NavigationGrid(8192, 8192));
            Reject<ArgumentException>(() => World(modified, [new(3, 2)]));
            Reject<ArgumentOutOfRangeException>(() => World(original, [new(1, 1)], speed: 1025));
        });
        test("Shared BFS finds shortest cardinal routes around obstacles", () =>
        {
            var grid = new NavigationGrid(3, 3, [new(1, 1)]);
            var nav = new GridNavigator(grid);
            int goal = grid.Index(new(2, 1)); nav.Prepare([goal]);
            Check(nav.TryDistance(goal, grid.Index(new(0, 1)), out int distance) && distance == 4, "Shortest detour must be four steps");
            Check(nav.TryDistance(goal, grid.Index(new(1, 1)), out distance) && distance == -1, "Wall is not traversable");
            int blockedGoal = grid.Index(new(1, 1)); nav.Prepare([blockedGoal]);
            Check(nav.TryDistance(blockedGoal, 0, out distance) && distance == -1, "Blocked goal must be unreachable");
        });
        test("Navigation job budget and rotating request cursor eventually serve distinct goals", () =>
        {
            var grid = new NavigationGrid(12, 8); var nav = new GridNavigator(grid, fieldsPerTick: 2, cachedFields: 3);
            int[] goals = [0, 2, 4, 6, 8, 10]; var served = new HashSet<int>();
            for (int tick = 0; tick < 6; tick++)
            {
                nav.Prepare(goals);
                Check(nav.LastBuildCount <= 2, "Exceeded deterministic build budget");
                foreach (int goal in goals) if (nav.TryDistance(goal, 0, out _)) served.Add(goal);
            }
            Check(served.Count == goals.Length, "A request was starved by cache capacity");
        });
        test("Parallel fields and reversed task order match sequential fields", () =>
        {
            var grid = new NavigationGrid(31, 23, Enumerable.Range(0, 23).Where(y => y != 11).Select(y => new GridCell(15, y)));
            var serial = new GridNavigator(grid, 1, 4, 4); var parallel = new GridNavigator(grid, 4, 4, 4, reverseJobs: true);
            for (int tick = 0; tick < 5; tick++)
            {
                int[] goals = Enumerable.Range(0, 7).Select(i => (i * 97 + tick * 13) % grid.CellCount).ToArray();
                serial.Prepare(goals); parallel.Prepare(goals.Reverse().ToArray());
                Check(serial.LastBuildCount == parallel.LastBuildCount && serial.LastVisitedCells == parallel.LastVisitedCells, "Build set differs");
                foreach (int goal in goals)
                    for (int cell = 0; cell < grid.CellCount; cell++)
                    {
                        bool a = serial.TryDistance(goal, cell, out int da), b = parallel.TryDistance(goal, cell, out int db);
                        Check(a == b && da == db, "Field depends on task order");
                    }
            }
        });
        test("A unit crosses a narrow opening without entering a wall", () =>
        {
            var grid = new NavigationGrid(7, 5, Enumerable.Range(0, 5).Where(y => y != 2).Select(y => new GridCell(3, y)));
            using var world = World(grid, [new(0, 0)], speed: 256);
            var target = grid.Center(grid.Index(new(6, 4)));
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: target)]);
            for (int i = 0; i < 80; i++) { Separated(world); world.Step(); }
            Check(Poses(world)[0].Current == target && world.MovingCount == 0, "Unit failed to arrive through the opening");
        });
        test("A group with distinct destinations queues through a one-cell opening", () =>
        {
            var grid = new NavigationGrid(7, 5, Enumerable.Range(0, 5).Where(y => y != 2).Select(y => new GridCell(3, y)));
            var starts = Enumerable.Range(0, 5).Select(y => new GridCell(0, y)).ToArray();
            using var a = World(grid, starts, 1, 256); using var b = World(grid, starts, 4, 256, true);
            var commands = Enumerable.Range(0, 5).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move,
                new((uint)i + 1), grid.Center(grid.Index(new(6, i))))).ToArray();
            a.Step(commands); b.Step(commands);
            for (int i = 0; i < 300; i++)
            {
                Separated(a); Separated(b);
                Check(a.Report() == b.Report(), "Corridor result depends on executor");
                a.Step(); b.Step();
            }
            Check(a.MovingCount == 0 && b.MovingCount == 0, "Group failed to clear the opening");
            foreach (var pose in Poses(a))
                Check(pose.Current == grid.Center(grid.Index(new(6, (int)pose.Id.Value - 1))), "Wrong group destination");
        });
        test("Unreachable goals project to shore and opening terrain permits a new order", () =>
        {
            var grid = new NavigationGrid(3, 3, Enumerable.Range(0, 3).Select(y => new GridCell(1, y)));
            using var world = World(grid, [new(0, 1)]); var goal = grid.Center(grid.Index(new(2, 1)));
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: goal)]);
            Check(world.Events.Length == 1 && world.Events[0].Kind == WorldEventKind.DestinationAdjusted && Poses(world)[0].Order.Destination.X < 1024, "Missing reachable-shore correction");
            world.Step([new(2, 0, 1, CommandKind.UnblockCell, Position: grid.Center(grid.Index(new(1, 1))))]);
            Check(world.NavigationGrid!.Revision == 1 && grid.IsBlocked(grid.Index(new(1, 1))), "Map snapshots were mutated");
            world.Step([new(3, 0, 1, CommandKind.MoveAll, Position: goal)]); world.Step();
            Check(Poses(world)[0].Current == goal, "Stale unreachable field was reused");
        });
        test("Units cannot occupy the same point or cross through one another", () =>
        {
            var grid = new NavigationGrid(3, 3); using var world = World(grid, [new(0, 1), new(2, 1)]);
            world.Step([new(1, 0, 1, CommandKind.MoveAll, Position: grid.Center(grid.Index(new(1, 1))))]);
            Separated(world);
            Check(world.MovingCount == 1, "Exactly one unit should occupy the common goal");
            for (int i = 0; i < 8; i++) { world.Step(); Separated(world); }
            Check(world.NavigationWaitingCount == 1, "Occupied goal must cause waiting");
            using var headOn = World(grid, [new(0, 1), new(1, 1)]);
            var before = Poses(headOn);
            headOn.Step([new(1, 0, 1, CommandKind.Move, new(1), before[1].Current), new(1, 0, 2, CommandKind.Move, new(2), before[0].Current)]);
            Check(Poses(headOn).Select(p => p.Current).SequenceEqual(before.Select(p => p.Current)), "Head-on cell swap was allowed");
        });
        test("Perpendicular movement through nearby samples stays safe throughout interpolation", () =>
        {
            var grid = new NavigationGrid(3, 3); using var world = World(grid, [new(0, 1), new(0, 0)]);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(0)),
                new(1, 0, 2, CommandKind.Move, new(2), grid.Center(1))]);
            Separated(world);
            for (int i = 0; i < 32; i++) { world.Step(); Separated(world); }
            Check(world.MovingCount == 0, "Safe perpendicular motion failed to finish");
        });
        test("Mid-segment retargeting is immediate and terrain cannot overlap the current body", () =>
        {
            var grid = new NavigationGrid(5, 2); using var world = World(grid, [new(0, 0), new(2, 0)], speed: 128);
            var origin = grid.Center(0);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(4))]);
            world.Step([new(2, 0, 1, CommandKind.Move, new(1), origin),
                new(2, 0, 2, CommandKind.BlockCell, Position: origin),
                new(2, 0, 3, CommandKind.Move, new(2), origin)]);
            Check(world.Events[0].Kind == WorldEventKind.CellOccupied && !world.NavigationGrid!.IsBlocked(0), "Terrain overlapped the body");
            Check(Poses(world).Single(p => p.Id.Value == 1).Current == origin, "A retarget finished an obsolete cell transition");
            for (int i = 0; i < 32; i++) { Separated(world); world.Step(); }
            Check(Poses(world).Single(p => p.Id.Value == 1).Current == origin, "Retargeted unit left its exact goal");
        });
        test("Blocked and occupied spawns are rejected; destroy releases reservations", () =>
        {
            var grid = new NavigationGrid(4, 2, [new(3, 1)]); using var world = World(grid, [new(0, 0)]);
            world.Step([new(1, 0, 1, CommandKind.Spawn, Position: grid.Center(0)),
                new(1, 0, 2, CommandKind.Spawn, Position: grid.Center(grid.Index(new(3, 1))))]);
            Check(world.Count == 1 && world.Events[0].Kind == WorldEventKind.CellOccupied && world.Events[1].Kind == WorldEventKind.CellBlocked, "Invalid spawn accepted");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(1)), new(2, 0, 2, CommandKind.Spawn, Position: grid.Center(0))]);
            Check(world.Count == 1 && Poses(world)[0].Id.Value == 2, "Released cell or ID sequence is incorrect");
        });
        test("Cache readiness and map changes are authoritative, worker count is not", () =>
        {
            var grid = new NavigationGrid(8, 5);
            using var a = World(grid, [new(0, 1)], 1); using var b = World(grid, [new(0, 1)], 4, reverse: true);
            using var differentBudget = World(grid, [new(0, 1)], budget: 1);
            Check(a.Report() == b.Report() && a.Report() != differentBudget.Report(), "Execution settings leaked into hash or work budget was omitted");
            for (int tick = 1; tick <= 20; tick++)
            {
                WorldCommand[] batch = tick == 1 ? [new(1, 0, 1, CommandKind.MoveAll, Position: grid.Center(grid.Index(new(7, 3))))]
                    : tick == 3 ? [new(3, 0, 1, CommandKind.BlockCell, Position: grid.Center(grid.Index(new(6, 2))))] : [];
                a.Step(batch); b.Step(batch); Check(a.Report() == b.Report(), "Dynamic map differs by worker count");
            }
        });
        test("Navigation remains canonical after packed removals", () =>
        {
            var grid = new NavigationGrid(8, 5); GridCell[] starts = [new(0, 1), new(1, 1), new(2, 1), new(3, 1)];
            using var a = World(grid, starts); using var b = World(grid, starts, 4);
            a.Step([new(1, 0, 1, CommandKind.Destroy, new(1)), new(1, 0, 2, CommandKind.Destroy, new(2))]);
            b.Step([new(1, 0, 1, CommandKind.Destroy, new(2)), new(1, 0, 2, CommandKind.Destroy, new(1))]);
            Check(a.Report() == b.Report(), "Equivalent navigation states differ");
            WorldCommand[] move = [new(2, 0, 1, CommandKind.MoveAll, Position: grid.Center(grid.Index(new(7, 3))))];
            a.Step(move); b.Step(move);
            for (int i = 0; i < 20; i++) { Check(a.Report() == b.Report(), "Packed order resolved a conflict"); a.Step(); b.Step(); }
        });
        test("A fully traversed 1024-square navigation layer is worker-independent", () =>
        {
            var grid = new NavigationGrid(1024, 1024);
            int[] goals = [0, 1023, grid.CellCount - 1, grid.CellCount - 1024];
            var a = new GridNavigator(grid, 1, 4, 4); var b = new GridNavigator(grid, 4, 4, 4, true);
            a.Prepare(goals); b.Prepare(goals);
            Check(a.LastVisitedCells == 4 * grid.CellCount && b.LastVisitedCells == a.LastVisitedCells, "Large field was not fully searched");
            foreach (int goal in goals)
            {
                a.TryDistance(goal, grid.CellCount / 2, out int da); b.TryDistance(goal, grid.CellCount / 2, out int db);
                Check(da == db, "Large field mismatch");
            }
        });
        test("Pre-cancelled navigation does not advance a world", () =>
        {
            using var world = World(new NavigationGrid(8, 8), [new(1, 1)]); var before = world.Report();
            using var stop = new CancellationTokenSource(); stop.Cancel();
            Reject<OperationCanceledException>(() => world.Step(cancellationToken: stop.Token));
            Check(world.Report() == before, "Pre-cancelled step changed state");
        });
        test("Immutable terrain snapshots survive edits and parallel sessions stop promptly", () =>
        {
            var grid = new NavigationGrid(8, 8); using var world = World(grid, [new(0, 0)]); var exchange = new FrameExchange();
            exchange.TryPublish(world);
            using var old = exchange.AcquireLatest()!;
            world.Step([new(1, 0, 1, CommandKind.BlockCell, Position: grid.Center(10))]); exchange.TryPublish(world);
            using var current = exchange.AcquireLatest()!;
            Check(!old.Grid!.IsBlocked(10) && current.Grid!.IsBlocked(10), "Reader's terrain snapshot changed");

            using var ready = new ManualResetEventSlim();
            var large = new NavigationGrid(2048, 2048);
            var commands = Enumerable.Range(0, 4).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move,
                new((uint)i + 1), large.Center(large.CellCount - 1 - i))).ToArray();
            using var session = new SimulationSession(() =>
            {
                var result = World(large, [new(0, 0), new(1, 0), new(2, 0), new(3, 0)], 4, budget: 4);
                ready.Set(); return result;
            }, commands, endTick: 100, realtime: false);
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "World factory did not start");
            Thread.Sleep(5);
            session.Dispose();
            Check(session.Completion.IsCompleted, "Worker outlived Dispose");
            if (!session.Completion.IsCanceled) session.Completion.GetAwaiter().GetResult();
        });
        test("10000-unit navigation matches 1/2/4 workers and reverse execution", () =>
        {
            var catalog = UnitCatalog.Load([new("test", "unit: {Health: {HP: 100}}")]);
            using var a = NavigationScenario.Create(catalog, 10000, 1);
            using var b = NavigationScenario.Create(catalog, 10000, 2);
            using var c = NavigationScenario.Create(catalog, 10000, 4, true);
            var commands = NavigationScenario.Commands(10000, 64);
            for (int tick = 1; tick <= 64; tick++)
            {
                var batch = commands.Where(command => command.Tick == tick).ToArray();
                a.Step(batch); b.Step(batch); c.Step(batch);
                if (tick is 1 or 2 or 8 or 41 or 64)
                    Check(a.Report() == b.Report() && a.Report() == c.Report(), $"Navigation desync at tick {tick}");
            }
            // A fixed point order isolates cached movement from group endpoint invalidations.
            a.Step([new(a.Tick + 1, 0, 1, CommandKind.MoveAll, Position: a.NavigationGrid!.Center(a.NavigationGrid.CellCount - 1))]);
            for (int i = 0; i < 8; i++) a.Step();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++) a.Step();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(bytes == 0, $"Cached navigation allocates per tick: {bytes} bytes");
        });
        test("A bulk adjusted-destination event burst is delivered with an explicit host budget", () =>
        {
            const int count = 70000;
            var catalog = UnitCatalog.Load([new("test", "unit: {Health: {HP: 100}}")]);
            var grid = NavigationScenario.Grid(count);
            int wall = (int)Math.Ceiling(Math.Sqrt(count)) + 16;
            WorldCommand[] commands = [new(1, 0, 1, CommandKind.MoveAll, Position: grid.Center(grid.Index(new(wall, 0))))];
            using var session = new SimulationSession(() => NavigationScenario.Create(catalog, count, 4), commands,
                endTick: 1, realtime: false, maximumEventBacklog: count + 8);
            var report = session.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            int delivered = 0;
            while (session.TryReadEvent(out var value))
            {
                Check(value.Kind == WorldEventKind.DestinationAdjusted, "Unexpected bulk event");
                delivered++;
            }
            Check(report.EntityCount == count && delivered == count, "Bulk no-route events were lost");
        });
    }
}
