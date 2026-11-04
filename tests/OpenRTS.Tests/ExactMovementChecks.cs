using OpenRTS.Core;
using static NavigationChecks;

internal static class ExactMovementChecks
{
    private static SimulationWorld Exact(NavigationGrid grid, params WorldPosition[] positions)
    {
        var world = new SimulationWorld([new("unit", 100)], grid.Width * grid.CellSize, grid.Height * grid.CellSize,
            initialCapacity: positions.Length, navigationGrid: grid, speedPerTick: 128);
        foreach (var p in positions) world.AddInitialUnit(0, p);
        return world;
    }
    private static void Finish(SimulationWorld world, int limit = 512)
    {
        for (int i = 0; i < limit && world.MovingCount > 0; i++) { Separated(world); world.Step(); }
        NavigationChecks.Check(world.MovingCount == 0, "Exact movement did not finish");
        Separated(world);
    }
    public static void Run(Action<string, Action> test)
    {
        test("Terrain sample scale is validated and survives immutable revisions", () =>
        {
            NavigationChecks.Reject<ArgumentOutOfRangeException>(() => new NavigationGrid(2, 2, cellSize: 0));
            NavigationChecks.Reject<ArgumentException>(() => Exact(new NavigationGrid(2, 2, cellSize: 513)));
            var grid = new NavigationGrid(3, 3, cellSize: 514);
            var modified = grid.WithBlocked(4, true);
            NavigationChecks.Check(modified.CellSize == 514 && modified.Center(0) == new WorldPosition(257, 257), "Grid revision lost the sampling scale");
            var world = Exact(modified, modified.Center(0));
            NavigationChecks.Check(world.Count == 1, "Minimum supported scale has no center clearance");
        });
        test("Exact spawns and subcell orders preserve arbitrary fixed-point coordinates", () =>
        {
            var grid = new NavigationGrid(4, 4, cellSize: 4096);
            var start = new WorldPosition(1300, 1900); var goal = new WorldPosition(1423, 1967);
            var world = Exact(grid, start);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), goal)]);
            var first = Poses(world)[0];
            NavigationChecks.Check(first.Current.X > start.X && first.Current.Y > start.Y && first.Current != goal, "Order snapped or moved on a cardinal axis");
            NavigationChecks.Check(grid.CellAt(start) == grid.CellAt(goal), "Test must remain within one route cell");
            Finish(world); NavigationChecks.Check(Poses(world)[0].Current == goal, "Endpoint was rounded to a sample center");
            NavigationChecks.Check(world.NavigationBuiltFields == 0, "A visible nearby target unnecessarily built a global field");
        });
        test("Terrain sampling resolution does not quantize unobstructed movement", () =>
        {
            var a = Exact(new NavigationGrid(16, 16), new WorldPosition(1123, 2345));
            var b = Exact(new NavigationGrid(4, 4, cellSize: 4096), new WorldPosition(1123, 2345));
            WorldCommand[] move = [new(1, 0, 1, CommandKind.Move, new(1), new(14678, 9765))];
            a.Step(move); b.Step(move);
            for (int i = 0; i < 180; i++)
            {
                NavigationChecks.Check(Poses(a)[0] == Poses(b)[0], "Route resolution changed a free-space trajectory");
                a.Step(); b.Step();
            }
            NavigationChecks.Check(a.MovingCount == 0 && b.MovingCount == 0, "Resolution comparison did not reach the target");
            NavigationChecks.Check(a.Report() != b.Report(), "Grid scale was omitted from navigation state");
        });
        test("Several bodies can occupy one navigation sample without overlapping", () =>
        {
            var grid = new NavigationGrid(4, 4, cellSize: 4096);
            var world = Exact(grid, new WorldPosition(900, 900), new(1600, 900), new(900, 1600));
            NavigationChecks.Check(Poses(world).Select(p => grid.CellAt(p.Current)).Distinct().Count() == 1, "Initial bodies should share a nav sample");
            NavigationChecks.Reject<ArgumentException>(() => world.AddInitialUnit(0, new(1100, 1000)));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(1900, 2400)),
                new(1, 0, 2, CommandKind.Move, new(2), new(2600, 2400)), new(1, 0, 3, CommandKind.Move, new(3), new(1900, 3100))]);
            Finish(world);
            NavigationChecks.Check(Poses(world).All(p => grid.CellAt(p.Current) == 0), "Bodies should also finish inside the same nav sample");
        });
        test("Non-cardinal and non-diagonal headings have a bounded Euclidean speed and exact arrival", () =>
        {
            var start = new WorldPosition(1234, 1890); var goal = new WorldPosition(11237, 7311);
            var world = Exact(new NavigationGrid(16, 12), start);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), goal)]);
            var p = Poses(world)[0];
            NavigationChecks.Check(p.Current.X != p.Previous.X && p.Current.Y != p.Previous.Y &&
                p.Current.X - p.Previous.X != p.Current.Y - p.Previous.Y, "Heading was restricted to grid directions");
            for (int i = 0; i < 150 && world.MovingCount > 0; i++)
            {
                p = Poses(world)[0]; long x = p.Current.X - p.Previous.X, y = p.Current.Y - p.Previous.Y;
                NavigationChecks.Check(x * x + y * y <= 128L * 128, "Diagonal speed exceeded the speed limit");
                world.Step();
            }
            NavigationChecks.Check(Poses(world)[0].Current == goal, "Rounding prevented exact arrival");
        });
        test("A short retarget reverses immediately without finishing a route-cell transition", () =>
        {
            var start = new WorldPosition(1500, 1800); var world = Exact(new NavigationGrid(12, 8), start);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(10000, 4500))]);
            var previous = Poses(world)[0].Current;
            var goal = new WorldPosition(start.X - 37, start.Y - 19);
            world.Step([new(2, 0, 1, CommandKind.Move, new(1), goal)]);
            NavigationChecks.Check(Poses(world)[0].Current.X < previous.X && Poses(world)[0].Current.Y < previous.Y, "Retarget still finished a cell edge");
            Finish(world); NavigationChecks.Check(Poses(world)[0].Current == goal, "Retarget rounded the destination");
        });
        test("Swept terrain checks reject a diagonal corner gap narrower than a body", () =>
        {
            var grid = new NavigationGrid(2, 2, [new(1, 0), new(0, 1)]);
            var world = Exact(grid, new WorldPosition(512, 512));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(1536, 1536))]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.DestinationAdjusted), "Missing near-side correction");
            Finish(world); NavigationChecks.Check(Poses(world)[0].Current.X < 768 && Poses(world)[0].Current.Y < 768, "A finite body crossed touching obstacle corners");
        });
        test("An angled route around a wall reaches an exact off-center endpoint", () =>
        {
            var grid = new NavigationGrid(9, 7, Enumerable.Range(0, 7).Where(y => y != 3).Select(y => new GridCell(4, y)));
            var world = Exact(grid, new WorldPosition(1234, 1456)); var goal = new WorldPosition(7891, 5678);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), goal)]);
            bool diagonal = false;
            for (int i = 0; i < 512 && world.MovingCount > 0; i++)
            {
                var p = Poses(world)[0]; diagonal |= p.Previous.X != p.Current.X && p.Previous.Y != p.Current.Y;
                for (int t = 0; t <= 32; t++)
                {
                    int x = (int)(((long)p.Previous.X * (32 - t) + (long)p.Current.X * t) / 32);
                    int y = (int)(((long)p.Previous.Y * (32 - t) + (long)p.Current.Y * t) / 32);
                    NavigationChecks.Check(!grid.IsBlocked(grid.CellAt(new(x, y))), "A segment tunneled through terrain");
                }
                world.Step();
            }
            NavigationChecks.Check(diagonal && world.MovingCount == 0 && Poses(world)[0].Current == goal, "The wall route snapped or failed");
        });
        test("A body near terrain or the map edge is rejected even if its center cell is free", () =>
        {
            var grid = new NavigationGrid(6, 6, [new(2, 2)]);
            NavigationChecks.Reject<ArgumentException>(() => Exact(grid, new WorldPosition(240, 1200)));
            NavigationChecks.Reject<ArgumentException>(() => Exact(grid, new WorldPosition(2000, 2500)));
            var world = Exact(grid, new WorldPosition(1200, 1200));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(2000, 2500))]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.DestinationAdjusted), "Invalid body clearance was accepted as a target");
        });
        test("An occupied destination waits without changing terrain reachability", () =>
        {
            var grid = new NavigationGrid(8, 3); var world = Exact(grid, new WorldPosition(1500, 1500), new(6500, 1500));
            var nav = new GridNavigator(grid); int goal = grid.CellAt(new(6500, 1500));
            nav.Prepare([goal]); NavigationChecks.Check(nav.TryDistance(goal, grid.CellAt(new(1500, 1500)), out int distance) && distance >= 0, "Terrain route missing");
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(6500, 1500))]);
            for (int i = 0; i < 160; i++)
            {
                NavigationChecks.Check(!world.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute), "Temporary body produced a terrain failure");
                Separated(world); world.Step();
            }
            NavigationChecks.Check(world.MovingCount > 0 && ReferenceEquals(world.NavigationGrid, grid), "Body occupancy changed terrain connectivity");
        });
        test("Off-center group clicks produce exact compact destinations", () =>
        {
            var grid = new NavigationGrid(18, 14);
            var starts = Enumerable.Range(0, 9).Select(i => new WorldPosition((2 + i % 3) * 1024 + 613, (2 + i / 3) * 1024 + 477)).ToArray();
            var world = Exact(grid, starts); var click = new WorldPosition(12 * 1024 + 613, 8 * 1024 + 477);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: click)]); Finish(world);
            var expected = Enumerable.Range(0, 9).Select(i => new WorldPosition((11 + i % 3) * 1024 + 613, (7 + i / 3) * 1024 + 477));
            NavigationChecks.Check(expected.ToHashSet().SetEquals(Poses(world).Select(p => p.Current)), "The group click was rounded to terrain centers");
        });
        test("Compact packing and group trajectories are independent of terrain sample size", () =>
        {
            var starts = Enumerable.Range(0, 25).Select(i => new WorldPosition((1 + i % 5) * 1024 + 512, (1 + i / 5) * 1024 + 512)).ToArray();
            var a = Exact(new NavigationGrid(16, 16), starts);
            var b = Exact(new NavigationGrid(4, 4, cellSize: 4096), starts);
            WorldCommand[] command = [new(1, 0, 1, CommandKind.MoveGroup, Position: new(10 * 1024 + 512, 10 * 1024 + 512))];
            a.Step(command); b.Step(command);
            NavigationChecks.Check(b.GroupSlotCount == 25 && b.GroupSlotCount > b.NavigationGrid!.CellCount, "Packing is still limited to one destination per nav sample");
            for (int i = 0; i < 200; i++)
            {
                NavigationChecks.Check(Poses(a).SequenceEqual(Poses(b)), "Changing route resolution spread out the compact destination or quantized motion");
                Separated(b); a.Step(); b.Step();
            }
            NavigationChecks.Check(a.MovingCount == 0 && b.MovingCount == 0, "Coarse-grid group failed to arrive");
        });
        test("Crossing swept trajectories cannot both be accepted despite safe endpoints", () =>
        {
            var grid = new NavigationGrid(6, 6);
            var world = new SimulationWorld([new("u", 100)], 6144, 6144, navigationGrid: grid, speedPerTick: 1024);
            world.AddInitialUnit(0, new(1200, 1700)); world.AddInitialUnit(0, new(1700, 1200));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(2200, 1700)),
                new(1, 0, 2, CommandKind.Move, new(2), new(1700, 2200))]);
            var poses = Poses(world);
            NavigationChecks.Check(!(poses[0].Current == new WorldPosition(2200, 1700) && poses[1].Current == new WorldPosition(1700, 2200)), "Only endpoints were checked for collision");
            Separated(world);
        });
        test("Terrain edits invalidate exact group goals with body clearance outside their goal sample", () =>
        {
            var world = Exact(new NavigationGrid(8, 4), new WorldPosition(512, 1536));
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: new(2948, 1536))]);
            world.Step([new(2, 0, 1, CommandKind.BlockCell, Position: new(3584, 1536))]);
            NavigationChecks.Check(world.NavigationGrid!.IsBlocked(world.NavigationGrid.Index(new(3, 1))), "Terrain edit was not applied");
            NavigationChecks.Check(world.MovingCount == 0 && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute), "Group kept an exact endpoint overlapping new terrain");
        });
        test("An interrupted yield times out en route and restores the original order", () =>
        {
            var free = Enumerable.Range(0, 7).Select(x => new GridCell(x, 1)).Append(new(5, 0)).ToHashSet();
            var grid = new NavigationGrid(7, 3, from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 7)
                where !free.Contains(new(x, y)) select new GridCell(x, y));
            var world = World(grid, [new(2, 1), new(3, 1)], speed: 256);
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), grid.Center(grid.Index(new(6, 1)))),
                new(1, 0, 2, CommandKind.Move, new(2), grid.Center(grid.Index(new(0, 1))))]);
            for (int i = 0; i < 64 && world.NavigationYields == 0; i++) world.Step();
            NavigationChecks.Check(world.NavigationYields > 0, "Scenario did not initiate the yield");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Destroy, new(1)),
                new(world.Tick + 1, 0, 2, CommandKind.BlockCell, Position: grid.Center(grid.Index(new(5, 0))))]);
            NavigationChecks.Check(world.NavigationGrid!.IsBlocked(grid.Index(new(5, 0))), "The future yield target was not blocked");
            Finish(world, 400);
            NavigationChecks.Check(Poses(world)[0].Current == grid.Center(grid.Index(new(0, 1))), "Expired yield did not restore the original exact target");
        });
        test("Local group target exchanges preserve each actor's independent elevation", () =>
        {
            var grid = new NavigationGrid(50, 44, Enumerable.Range(0, 44).Where(y => y < 20 || y >= 24).Select(y => new GridCell(22, y)));
            var starts = Enumerable.Range(0, 36).Select(i => grid.Center(grid.Index(new(4 + i % 6, 4 + i / 6)), i * 101)).ToArray();
            var world = Exact(grid, starts);
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: grid.Center(grid.Index(new(42, 22))))]);
            int exchanges = 0;
            for (int i = 0; i < 1400 && world.MovingCount > 0; i++)
            {
                exchanges += world.NavigationGoalExchanges;
                foreach (var pose in Poses(world)) NavigationChecks.Check(pose.Current.Elevation == starts[pose.Id.Value - 1].Elevation, "A ground target exchange teleported elevation");
                world.Step();
            }
            NavigationChecks.Check(exchanges > 0 && world.MovingCount == 0, "The scenario must execute local exchanges and finish");
            foreach (var pose in Poses(world)) NavigationChecks.Check(pose.Current.Elevation == starts[pose.Id.Value - 1].Elevation, "Final elevation changed");
        });
    }
}
