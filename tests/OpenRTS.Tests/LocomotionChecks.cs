using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class LocomotionChecks
{
    internal static LocomotionCatalog Catalog() => LocomotionCatalog.Load([new("terrain", File.ReadAllText("sandbox/content/terrain.yaml"))]);
    internal static UnitCatalog Units() => UnitCatalog.Load([new("units", File.ReadAllText("sandbox/content/units.yaml"))], Catalog());
    private static SimulationWorld World(NavigationGrid grid, Locomotor profile, int speed = 128, int workers = 1) =>
        new([new("unit", 100, locomotor: profile)], grid.Width * grid.CellSize, grid.Height * grid.CellSize,
            navigationGrid: grid, speedPerTick: speed, navigationWorkers: workers);
    public static void Run(Action<string, Action> test)
    {
        test("Terrain catalog validates complete profiles and canonical gameplay fingerprints", () =>
        {
            var catalog = Catalog(); var units = Units();
            NavigationChecks.Check(catalog.Terrains.Count == 7 && catalog.Terrains[0] == "road" && catalog.Profiles.Count == 3, "Missing N1 definitions");
            NavigationChecks.Check(units.Units.Single(u => u.Id == "tank").Locomotor.Id == "tracked", "Movement binding lost");
            var changed = LocomotionCatalog.Load([new("modified", File.ReadAllText("sandbox/content/terrain.yaml").Replace("Speed: 384", "Speed: 383"))]);
            NavigationChecks.Check(units.Summary != UnitCatalog.Load([new("units", File.ReadAllText("sandbox/content/units.yaml"))], changed).Summary, "Speed change did not enter content fingerprint");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("units", "u: {Health: {HP: 1}, Movement: {Locomotor: typo}}")], catalog));
            NavigationChecks.Reject<ContentException>(() => LocomotionCatalog.Load([new("incomplete", "a: {Terrain: {Default: true}}\nb: {Terrain: {}}\np: {Locomotor: {Terrain: {a: {}}}}") ]));
            NavigationChecks.Reject<ArgumentException>(() => new Locomotor("p", [new(true, true, 0, 1024)]));
            NavigationChecks.Reject<ArgumentException>(() => new Locomotor("p", [new(false, true, 0, 0)]));
            NavigationChecks.Reject<ArgumentException>(() => new NavigationGrid(2, 2, terrain: [0]));
        });
        test("Profile components distinguish water, forest, swamp and static edits", () =>
        {
            var catalog = Catalog(); var names = new[] { "infantry", "amphibious", "tracked" };
            foreach (string terrain in new[] { "water", "forest", "swamp", "mountains" })
            {
                byte type = catalog.TerrainIndex(terrain);
                var grid = new NavigationGrid(7, 5, terrain: Enumerable.Range(0, 35).Select(i => i % 7 == 3 ? type : (byte)0));
                var profiles = names.Select(n => catalog.Profiles[n]).ToArray();
                var nav = new WeightedNavigator(grid, profiles, 1, 2, 4, false);
                for (int p = 0; p < profiles.Length; p++)
                    NavigationChecks.Check(nav.CanReach(p, 15, 19) == profiles[p].Rules[type].Traverse, "Wrong profile connectivity: " + names[p] + "/" + terrain);
                if (terrain == "water")
                {
                    var blocked = grid;
                    for (int y = 0; y < 5; y++) blocked = blocked.WithBlocked(y * 7 + 3, true);
                    nav.SetGrid(blocked); NavigationChecks.Check(!nav.CanReach(1, 15, 19), "Component cache survived closed crossing");
                    nav.SetGrid(grid); NavigationChecks.Check(nav.CanReach(1, 15, 19), "Component cache failed to recover");
                }
            }
        });
        test("Reverse weighted fields match an independent Dijkstra oracle for every cell", () =>
        {
            var profile = new Locomotor("oracle", [TerrainRule.Ground, new(true, true, 512, 4096), TerrainRule.Blocked]);
            var random = new Random(461);
            for (int sample = 0; sample < 8; sample++)
            {
                byte[] terrain = Enumerable.Range(0, 63).Select(_ => (byte)random.Next(3)).ToArray(); terrain[31] = 0;
                var grid = new NavigationGrid(9, 7, terrain: terrain);
                var nav = new WeightedNavigator(grid, [profile], 1, 2, 4, false); nav.Prepare([new(0, 31)]);
                var expected = Oracle(grid, profile, 31);
                for (int cell = 0; cell < grid.CellCount; cell++)
                    NavigationChecks.Check(nav.TryCost(new(0, 31), cell, out long value) && value == expected[cell], "Oracle mismatch at " + cell);
            }
            var large = new NavigationGrid(40000, 1);
            var expensive = new Locomotor("large", [new(true, true, 1, 65536)]);
            var longNav = new WeightedNavigator(large, [expensive], 1, 1, 1, false); longNav.Prepare([new(0, 39999)]);
            NavigationChecks.Check(longNav.TryCost(new(0, 39999), 0, out long cost) && cost == 39999L * 131072 && cost > int.MaxValue, "Route price overflowed Int32");
        });
        test("Weighted keys include locomotor and FIFO budgets survive parallel completion order", () =>
        {
            var grid = new NavigationGrid(9, 7, terrain: Enumerable.Range(0, 63).Select(i => (byte)(i % 2)));
            Locomotor[] profiles = [new("a", [TerrainRule.Ground, new(true, true, 512, 4096)]), new("b", [TerrainRule.Ground, new(true, true, 1024, 1024)])];
            var a = new WeightedNavigator(grid, profiles, 1, 2, 3, false);
            var b = new WeightedNavigator(grid, profiles, 4, 2, 3, true);
            RouteRequest[] keys = [new(0, 0), new(1, 0), new(0, 62), new(1, 62)];
            var served = new HashSet<RouteRequest>();
            for (int tick = 0; tick < 8; tick++)
            {
                a.Prepare(keys); b.Prepare(keys);
                NavigationChecks.Check(a.Builds <= 2 && a.Builds == b.Builds, "Field work budget changed");
                foreach (var key in keys)
                {
                    bool found = a.TryCost(key, 31, out long x), other = b.TryCost(key, 31, out long y);
                    NavigationChecks.Check(found == other && x == y, "Parallel field/cache result changed"); if (found) served.Add(key);
                }
            }
            NavigationChecks.Check(served.Count == keys.Length, "A profile request starved");
        });
        test("World follows the cheap detour instead of smoothing through expensive swamp", () =>
        {
            byte[] terrain = new byte[11 * 7];
            for (int x = 3; x <= 7; x++) terrain[3 * 11 + x] = 1;
            var grid = new NavigationGrid(11, 7, terrain: terrain);
            var profile = new Locomotor("detour", [TerrainRule.Ground, new(true, true, 128, 16384)]);
            var world = World(grid, profile); world.AddInitialUnit(0, new(1536, 3584));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(9728, 3584))]);
            for (int tick = 1; tick < 200; tick++)
            {
                var pose = Poses(world)[0];
                NavigationChecks.Check(grid.TerrainAt(grid.CellAt(pose.Current)) == 0, "Smoothing entered the costly shortcut");
                if (world.MovingCount == 0) break;
                world.Step();
            }
            NavigationChecks.Check(Poses(world)[0].Current == new WorldPosition(9728, 3584), "Cheap detour did not arrive");
        });
        test("Physical speed is independent of route price and retains fractional movement", () =>
        {
            var grid = new NavigationGrid(8, 4, terrain: Enumerable.Repeat((byte)0, 32));
            foreach (int factor in new[] { 1024, 768, 512, 1 })
            {
                var profile = new Locomotor("speed", [new(true, true, factor, 1024)]);
                var world = World(grid, profile); world.AddInitialUnit(0, new(1536, 1536));
                world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(6656, 1536))]);
                for (int tick = 1; tick < 8; tick++) world.Step();
                NavigationChecks.Check(Poses(world)[0].Current.X == 1536 + factor, "Fractional speed lost distance: " + factor);
            }
        });
        test("Transit-only ground permits crossing but rejects destinations and spawning", () =>
        {
            var grid = new NavigationGrid(7, 5, terrain: Enumerable.Range(0, 35).Select(i => i % 7 == 3 ? (byte)1 : (byte)0));
            var profile = new Locomotor("bridge", [TerrainRule.Ground, new(true, false, 1024, 1024)]);
            var world = World(grid, profile);
            NavigationChecks.Reject<ArgumentException>(() => world.AddInitialUnit(0, new(3584, 2560)));
            world.AddInitialUnit(0, new(1536, 2560));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(5632, 2560))]);
            for (int i = 0; i < 40; i++) world.Step();
            NavigationChecks.Check(Poses(world)[0].Current.X == 5632, "Transit-only ground became a wall");
            world.Step([new(42, 0, 1, CommandKind.Move, new(1), new(3584, 2560)),
                new(42, 0, 2, CommandKind.Spawn, Position: new(3584, 1536))]);
            NavigationChecks.Check(world.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute) && world.Count == 1 && world.MovingCount == 0, "Forbidden stopping position was accepted");
        });
        test("Profile-aware geometry keeps a tracked body's edge out of water", () =>
        {
            var grid = new NavigationGrid(6, 5, terrain: Enumerable.Range(0, 30).Select(i => i % 6 >= 3 ? (byte)1 : (byte)0));
            var profile = new Locomotor("land", [TerrainRule.Ground, TerrainRule.Blocked]);
            var world = World(grid, profile);
            NavigationChecks.Reject<ArgumentException>(() => world.AddInitialUnit(0, new(3000, 1536)));
            world.AddInitialUnit(0, new(1536, 1536));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(3000, 1536))]);
            NavigationChecks.Check(world.MovingCount == 0 && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute), "Body entered a forbidden terrain edge");
        });
        test("Mixed groups reject incompatible stop areas atomically and preserve worker parity", () =>
        {
            var land = new Locomotor("land", [TerrainRule.Ground, TerrainRule.Blocked]);
            var amphibian = new Locomotor("amphibian", [TerrainRule.Ground, new(true, true, 512, 2048)]);
            var grid = new NavigationGrid(12, 8, terrain: Enumerable.Range(0, 96).Select(i => i % 12 >= 8 ? (byte)1 : (byte)0));
            SimulationWorld Create(int workers) {
                var w = new SimulationWorld([new("land", 100, locomotor: land), new("amphibian", 100, locomotor: amphibian)], 12288, 8192, navigationGrid: grid, navigationWorkers: workers);
                w.AddInitialUnit(0, new(1536, 2560)); w.AddInitialUnit(1, new(1536, 4608)); return w;
            }
            var a = Create(1); var b = Create(4);
            for (int tick = 1; tick <= 80; tick++)
            {
                WorldCommand[] commands = tick == 1 ? [new(1, 0, 1, CommandKind.MoveGroup, Position: new(9728, 3584))] :
                    tick == 2 ? [new(2, 0, 1, CommandKind.MoveGroup, Position: new(6656, 3584))] : [];
                a.Step(commands); b.Step(commands);
                if (tick == 1) NavigationChecks.Check(a.MovingCount == 0 && a.Events.ToArray().Single().Kind == WorldEventKind.NoRoute, "Incompatible group partly changed orders");
                NavigationChecks.Check(a.Report() == b.Report(), "Mixed group changed with worker count");
            }
            NavigationChecks.Check(a.MovingCount == 0, "Compatible mixed group did not finish");
        });
        test("Weighted point orders retain tick hashes with parallel and reversed field jobs", () =>
        {
            var grid = new NavigationGrid(24, 16, terrain: Enumerable.Repeat((byte)1, 384));
            Locomotor[] profiles = [new("a", [TerrainRule.Ground, new(true, true, 768, 2048)]), new("b", [TerrainRule.Ground, new(true, true, 512, 1536)])];
            SimulationWorld Create(int workers, bool reverse)
            {
                var world = new SimulationWorld(profiles.Select((p, i) => new UnitDefinition("u" + i, 100, locomotor: p)),
                    24576, 16384, navigationGrid: grid, navigationWorkers: workers, reverseNavigationJobs: reverse);
                for (int i = 0; i < 8; i++) world.AddInitialUnit(i % 2, new(2560 + i % 4 * 2048, 2560 + i / 4 * 2048));
                return world;
            }
            var a = Create(1, false); var b = Create(4, true);
            for (int tick = 1; tick <= 64; tick++)
            {
                WorldCommand[] commands = tick == 1 ? [new(1, 0, 1, CommandKind.MoveAll, Position: new(20992, 12800))] : [];
                a.Step(commands); b.Step(commands);
                NavigationChecks.Check(a.Report() == b.Report(), "Weighted world/field publication changed with workers");
                if (tick == 1) NavigationChecks.Check(a.NavigationBuiltFields == 2, "Expected one shared field per locomotor");
            }
        });
        test("Stopping on one's current transit cell cannot cancel a valid crossing", () =>
        {
            var grid = new NavigationGrid(7, 5, terrain: Enumerable.Range(0, 35).Select(i => i % 7 == 3 ? (byte)1 : (byte)0));
            var profile = new Locomotor("bridge", [TerrainRule.Ground, new(true, false, 1024, 1024)]);
            var world = World(grid, profile); world.AddInitialUnit(0, new(1536, 2560));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(5632, 2560))]);
            for (int i = 1; i < 13; i++) world.Step();
            var position = Poses(world)[0].Current;
            NavigationChecks.Check(grid.TerrainAt(grid.CellAt(position)) == 1, "Fixture did not reach transit ground");
            world.Step([new(14, 0, 1, CommandKind.Move, new(1), position)]);
            NavigationChecks.Check(world.MovingCount == 1 && Poses(world)[0].Current != position && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.NoRoute), "Illegal endpoint stopped a unit on transit ground");
        });
        test("10000 weighted group members share terrain trees instead of individual global fields", () =>
        {
            var catalog = Units(); var ground = catalog.Locomotion!;
            var grid = new NavigationGrid(240, 132, terrain: Enumerable.Range(0, 240 * 132).Select(i =>
                i % 240 is >= 115 and <= 122 && i / 240 is not (>= 64 and <= 68) ? ground.TerrainIndex("mud") : (byte)0));
            var world = new SimulationWorld(catalog.Units, 240 * 1024, 132 * 1024, initialCapacity: 10000, navigationGrid: grid);
            for (int i = 0; i < 10000; i++) world.AddInitialUnit(i % 5, new((i % 100 + 4) * 1024 + 512, (i / 100 + 4) * 1024 + 512));
            int trees = 0, fields = 0; double elapsed = 0; var samples = new double[8]; long allocated = GC.GetTotalAllocatedBytes(true);
            for (int tick = 1; tick <= 8; tick++)
            {
                var commands = tick == 1 ? new WorldCommand[] { new(1, 0, 1, CommandKind.MoveGroup, Position: new(190 * 1024 + 512, 66 * 1024 + 512)) } : [];
                long start = System.Diagnostics.Stopwatch.GetTimestamp(); world.Step(commands);
                samples[tick - 1] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                elapsed += samples[tick - 1];
                trees += world.GroupRouteBuilds; fields += world.NavigationBuiltFields;
            }
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            NavigationChecks.Check(trees == 3 && fields == 0 && world.MovingCount == 10000, "Weighted group built per-unit global fields or lost members");
            Console.WriteLine("OPENRTS_N1_SAMPLE:" + System.Text.Json.JsonSerializer.Serialize(new { Entities = 10000, Ticks = 8, Trees = trees, Fields = fields, StepMilliseconds = elapsed, TickMilliseconds = samples, AllocatedBytes = allocated }));
        });
    }

    // Deliberately slow O(V²) oracle, independent of the production priority queue/cache/path extraction.
    private static long[] Oracle(NavigationGrid grid, Locomotor profile, int goal)
    {
        var costs = Enumerable.Repeat(long.MaxValue, grid.CellCount).ToArray(); var done = new bool[costs.Length]; costs[goal] = 0;
        while (true)
        {
            int best = -1;
            for (int i = 0; i < costs.Length; i++) if (!done[i] && costs[i] != long.MaxValue && (best < 0 || costs[i] < costs[best])) best = i;
            if (best < 0) return costs;
            done[best] = true;
            for (int next = 0; next < costs.Length; next++)
            {
                if (Math.Abs(best % grid.Width - next % grid.Width) + Math.Abs(best / grid.Width - next / grid.Width) != 1 ||
                    grid.IsBlocked(next) || !profile.Rules[grid.TerrainAt(next)].Traverse) continue;
                long cost = costs[best] + profile.Rules[grid.TerrainAt(best)].Cost + profile.Rules[grid.TerrainAt(next)].Cost;
                costs[next] = Math.Min(costs[next], cost);
            }
        }
    }
}
