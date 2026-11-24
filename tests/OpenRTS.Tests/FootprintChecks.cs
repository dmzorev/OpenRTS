using OpenRTS.Core;
using DeclarativeContent;
using OpenRTS.Content;
using static NavigationChecks;

internal static class FootprintChecks
{
    private static FootprintDefinition Stamp(int width, int height, CellOverride rule, bool targetable = true) =>
        new(width, height, from y in Enumerable.Range(0, height) from x in Enumerable.Range(0, width) select new FootprintCell(x, y, rule, targetable));
    private static UnitDefinition Building(string id, FootprintDefinition stamp, int health = 100) => new(id, health, ["idle"], movable: false, footprint: stamp, armor: ArmorKind.Building);
    private static UnitDefinition Mover(string id = "unit", int radius = 128, Locomotor? profile = null) => new(id, 100, locomotor: profile, body: BodyShape.Circle(radius));
    private static EntityPose Pose(SimulationWorld world, uint id) => Poses(world).Single(p => p.Id.Value == id);
    private static void Move(SimulationWorld world, uint id, WorldPosition goal) => world.Step([new(world.Tick + 1, 0, 1, CommandKind.Move, new(id), goal)]);
    private static void Finish(SimulationWorld world, int limit = 400) { for (int i = 0; i < limit && world.MovingCount > 0; i++) world.Step(); }
    public static void Run(Action<string, Action> test)
    {
        test("N2: shape and footprint declarations validate physical dimensions and semantics", () =>
        {
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("u", 100, footprint: Stamp(1, 1, new(false))));
            NavigationChecks.Reject<ArgumentOutOfRangeException>(() => BodyShape.Circle(0));
            NavigationChecks.Reject<ArgumentException>(() => new FootprintDefinition(1, 1, [new(1, 0, new(false))]));
            NavigationChecks.Reject<ArgumentException>(() => new FootprintDefinition(1, 1, [new(0, 0, new(false, true))]));
            var b = Building("b", Stamp(2, 2, new(false))); NavigationChecks.Check(b.MoveAnimation == b.IdleAnimation && b.Body.Kind == BodyKind.None, "Building requires a fake movement animation or body");
            var units = UnitCatalog.Load([new("u", "u: {Health: {HP: 10}, Footprint: {Rows: ['##', '#+'], Types: {'#': {Traverse: false}, '+': {Traverse: true, CanStop: false}}}}")]);
            NavigationChecks.Check(units.Units[0].Footprint!.Cells.Count == 4 && !units.Units[0].Movable, "Footprint YAML not compiled");
            NavigationChecks.Reject<ContentException>(() => UnitCatalog.Load([new("u", "u: {Health: {HP: 1}, Footprint: {Rows: ['z']}}") ]));
        });
        test("N2: a cursor query from blocked geometry returns no stopping terrain without indexing an absent component", () =>
        {
            var resolver = new DestinationResolver(new(4, 4, [new GridCell(1, 1)]), Locomotor.Default, exactGeometry: true);
            NavigationChecks.Check(!resolver.HasStoppingTerrain(new(1536, 1536)), "Blocked origin has a stopping component");
            NavigationChecks.Check(resolver.HasStoppingTerrain(new(512, 512)), "A valid origin lost its stopping component");
        });
        test("N2: overlay removal preserves lower layers and stable placement priority after packed removal", () =>
        {
            var profile = new Locomotor("p", [TerrainRule.Ground, TerrainRule.Ground, TerrainRule.Blocked]);
            using var world = new SimulationWorld([new("m", 100, locomotor: profile), Building("forest", Stamp(2, 2, new(true, true, 1))), Building("rock", Stamp(2, 2, new(false, false, 2)))], 12288, 8192, navigationGrid: new(12, 8));
            world.AddInitialUnit(0, new(1024, 1024)); world.AddInitialUnit(1, new(6144, 4096)); world.AddInitialUnit(2, new(6144, 4096));
            int c = world.NavigationGrid!.CellAt(new(6144, 4096));
            world.Step([new(1, 0, 1, CommandKind.Destroy, new(1))]);
            NavigationChecks.Check(world.NavigationGrid.TerrainAt(c) == 2 && world.NavigationGrid.IsBlocked(c), "Packed storage changed top overlay");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(3))]);
            NavigationChecks.Check(world.NavigationGrid.TerrainAt(c) == 1 && !world.NavigationGrid.IsBlocked(c), "Removing top did not restore lower");
            world.Step([new(3, 0, 1, CommandKind.Destroy, new(2))]);
            NavigationChecks.Check(world.NavigationGrid.TerrainAt(c) == 0 && !world.NavigationGrid.IsBlocked(c), "Removing last did not restore base");
        });
        test("N2: removing a hidden lower overlay never resurrects it", () =>
        {
            var p = new Locomotor("p", [TerrainRule.Ground, TerrainRule.Ground, TerrainRule.Ground]);
            using var world = new SimulationWorld([new("u", 10, locomotor: p), Building("a", Stamp(2, 2, new(true, true, 1))), Building("b", Stamp(2, 2, new(true, true, 2)))], 8192, 8192, navigationGrid: new(8, 8));
            world.AddInitialUnit(1, new(4096, 4096)); world.AddInitialUnit(2, new(4096, 4096)); int c = world.NavigationGrid!.CellAt(new(4096, 4096)); int revision = world.NavigationGrid.Revision;
            world.Step([new(1, 0, 1, CommandKind.Destroy, new(1))]);
            NavigationChecks.Check(world.NavigationGrid.TerrainAt(c) == 2 && world.NavigationGrid.Revision == revision, "Hidden removal invalidated or erased upper layer");
            world.Step([new(2, 0, 1, CommandKind.Destroy, new(2))]); NavigationChecks.Check(world.NavigationGrid.TerrainAt(c) == 0, "Deleted lower layer was resurrected");
        });
        test("N2: hidden base edits are hashed and survive removal of a covering object", () =>
        {
            SimulationWorld Create() { var w = new SimulationWorld([Building("cover", Stamp(2, 2, new(true, true)))], 8192, 8192, navigationGrid: new(8, 8)); w.AddInitialUnit(0, new(4096, 4096)); return w; }
            using var a = Create(); using var b = Create(); var point = new WorldPosition(4096, 4096); int c = a.NavigationGrid!.CellAt(point);
            a.Step([new(1, 0, 1, CommandKind.BlockCell, Position: point)]); b.Step();
            NavigationChecks.Check(a.NavigationGrid.Revision == b.NavigationGrid!.Revision && !a.NavigationGrid.IsBlocked(c) && a.Report() != b.Report(), "Hidden base state was not authoritative");
            a.Step([new(2, 0, 1, CommandKind.Destroy, new(1))]); b.Step([new(2, 0, 1, CommandKind.Destroy, new(1))]);
            NavigationChecks.Check(a.NavigationGrid.IsBlocked(c) && !b.NavigationGrid.IsBlocked(c), "Base edit under override was discarded");
        });
        test("N2: passable footprints have no blocking center circle and transit cells forbid stopping", () =>
        {
            using var world = new SimulationWorld([Mover(), Building("yard", Stamp(2, 2, new(true, false)))], 12288, 8192, navigationGrid: new(12, 8));
            world.AddInitialUnit(1, new(6144, 4096)); world.AddInitialUnit(0, new(2048, 4096));
            Move(world, 2, new(10240, 4096)); Finish(world);
            NavigationChecks.Check(Pose(world, 2).Current == new WorldPosition(10240, 4096), "Passable building blocked its own center");
            Move(world, 2, new(6144, 4096)); var goal = Pose(world, 2).Order.Destination; Finish(world);
            NavigationChecks.Check(world.Events.ToArray().All(e => e.Kind != WorldEventKind.NoRoute) && Pose(world, 2).Current == goal && (goal.X < 5 * 1024 || goal.X > 7 * 1024 || goal.Y < 3 * 1024 || goal.Y > 5 * 1024), "Transit goal was not projected outside the full body");
        });
        test("N2: placing a blocking footprint after route calculation forces a safe detour", () =>
        {
            using var world = new SimulationWorld([Mover(), Building("wall", Stamp(2, 4, new(false, false)))], 16384, 10240, navigationGrid: new(16, 10));
            world.AddInitialUnit(0, new(2048, 4096)); Move(world, 1, new(14336, 4096)); for (int t = 0; t < 4; t++) world.Step();
            world.Step([new(6, 0, 1, CommandKind.Spawn, Position: new(8192, 4096), UnitType: 1)]); bool detour = false;
            for (int t = 0; t < 300 && world.MovingCount > 0; t++) { world.Step(); var p = Pose(world, 1).Current; detour |= p.Y < 1900 || p.Y > 6300; }
            NavigationChecks.Check(world.Count == 2 && detour && world.MovingCount == 0 && Pose(world, 1).Current == new WorldPosition(14336, 4096), "Dynamic footprint left a stale route or stopped the order");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Destroy, new(2))]); NavigationChecks.Check(!world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(new(8192, 4096))), "Deleted obstacle remained in navigation");
        });
        test("N2: unsafe placement is rejected atomically without trapping an existing unit", () =>
        {
            using var world = new SimulationWorld([Mover(), Building("b", Stamp(2, 2, new(false, false)))], 8192, 8192, navigationGrid: new(8, 8));
            world.AddInitialUnit(0, new(4096, 4096)); int revision = world.NavigationGrid!.Revision;
            world.Step([new(1, 0, 1, CommandKind.Spawn, Position: new(4096, 4096), UnitType: 1)]);
            NavigationChecks.Check(world.Count == 1 && world.NavigationGrid.Revision == revision && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.CellBlocked), "Unsafe placement partially changed the map");
        });
        test("N2: forced platform removal restores terrain and strands rather than teleports a unit", () =>
        {
            var profile = new Locomotor("tank", [TerrainRule.Ground, TerrainRule.Blocked]);
            var grid = new NavigationGrid(12, 8, terrain: Enumerable.Range(0, 96).Select(c => c % 12 is >= 5 and <= 6 && c / 12 is >= 3 and <= 4 ? (byte)1 : (byte)0));
            using var world = new SimulationWorld([Mover(profile: profile), Building("platform", Stamp(2, 2, new(true, true, 0)))], 12288, 8192, navigationGrid: grid);
            world.AddInitialUnit(1, new(6144, 4096)); world.AddInitialUnit(0, new(6144, 4096));
            world.Step([new(1, 0, 1, CommandKind.Destroy, new(1))]);
            NavigationChecks.Check(Pose(world, 2).TerrainStranded && Pose(world, 2).Current == new WorldPosition(6144, 4096) && world.NavigationGrid!.TerrainAt(grid.CellAt(new(6144, 4096))) == 1, "Forced removal did not restore water/stranded state");
            world.Step([new(2, 0, 1, CommandKind.Spawn, Position: new(6144, 4096), UnitType: 1)]);
            NavigationChecks.Check(!Pose(world, 2).TerrainStranded, "Replaced platform failed to restore movement"); Move(world, 2, new(9216, 4096)); Finish(world);
            NavigationChecks.Check(Pose(world, 2).Current.X == 9216, "Rescued unit cannot move");
        });
        test("N2: circle and box clearance distinguish a narrow passage", () =>
        {
            var grid = new NavigationGrid(16, 10, Enumerable.Range(0, 10).Where(y => y != 5).Select(y => new GridCell(8, y)));
            WorldPosition Run(BodyShape body) { using var w = new SimulationWorld([new("u", 100, body: body)], 16384, 10240, navigationGrid: grid); w.AddInitialUnit(0, new(2048, 5632)); Move(w, 1, new(14336, 5632)); Finish(w); return Pose(w, 1).Current; }
            NavigationChecks.Check(Run(BodyShape.Circle(128)).X == 14336 && Run(BodyShape.Circle(640)).X < 8192 && Run(BodyShape.Box(640, 192)).X == 14336, "Clearance ignored physical shape");
        });
        test("N2: partial geometry and exact route lattice agree across terrain sampling sizes", () =>
        {
            WorldBox[] boxes = [new(8192, 0, 8704, 4096), new(8192, 4608, 8704, 10240)];
            SimulationWorld Create(int size) { var w = new SimulationWorld([Mover(radius: 160)], 16384, 10240, navigationGrid: new(16384 / size, 10240 / size, cellSize: size, obstacles: boxes)); w.AddInitialUnit(0, new(2048, 2560)); Move(w, 1, new(14336, 2560)); return w; }
            using var a = Create(1024); using var b = Create(512);
            for (int t = 0; t < 350 && (a.MovingCount > 0 || b.MovingCount > 0); t++) { NavigationChecks.Check(Pose(a, 1).Current == Pose(b, 1).Current, "Terrain sampling changed geometry/path"); a.Step(); b.Step(); }
            NavigationChecks.Check(a.MovingCount == 0 && Pose(a, 1).Current.X == 14336 && Pose(b, 1).Current.X == 14336, "Partial-cell passage was lost");
        });
        test("N2: rounded terrain corners no longer use square circle dilation", () =>
        {
            using var world = new SimulationWorld([Mover(radius: 256)], 8192, 8192, navigationGrid: new(8, 8, [new(3, 3)]));
            world.AddInitialUnit(0, new(3072 - 190, 3072 - 190)); NavigationChecks.Check(world.Count == 1, "Valid point outside rounded corner was rejected");
            NavigationChecks.Reject<ArgumentException>(() => world.AddInitialUnit(0, new(3072 - 180, 3072 - 180)));
        });
        test("N2: locomotor slope, step and depth limits constrain geometry and elevation follows the surface", () =>
        {
            foreach (string kind in new[] { "slope", "step", "depth" })
            {
                var surface = Enumerable.Range(0, 128).Select(c => c % 16 == 7 ? kind switch { "slope" => new SurfaceSample(Slope: 2000), "step" => new SurfaceSample(Elevation: 1024), _ => new SurfaceSample(Depth: 512) } : default).ToArray();
                var profile = new Locomotor(kind, [TerrainRule.Ground], maximumSlope: 1000, maximumStep: 128, maximumDepth: 256);
                using var w = new SimulationWorld([Mover(profile: profile)], 16384, 8192, navigationGrid: new(16, 8, surface: surface)); w.AddInitialUnit(0, new(2048, 4096)); Move(w, 1, new(14336, 4096)); Finish(w);
                NavigationChecks.Check(Pose(w, 1).Current.X < 7168, "Ignored " + kind + " limit");
            }
            var ramp = new NavigationGrid(12, 8, surface: Enumerable.Range(0, 96).Select(c => new SurfaceSample(Elevation: c % 12 * 64)));
            using var raised = new SimulationWorld([Mover()], 12288, 8192, navigationGrid: ramp); raised.AddInitialUnit(0, new(2048, 4096, 128)); Move(raised, 1, new(10240, 4096)); Finish(raised);
            NavigationChecks.Check(raised.MovingCount == 0 && Pose(raised, 1).Current.Elevation == 640, "Ground elevation broke waypoint completion");
        });
        test("N2: ramp planes keep a large body grounded and a shared cliff edge blocks it", () =>
        {
            const int size = WorldPosition.UnitsPerCell, width = 12, depth = 6, rise = 128;
            var samples = Enumerable.Range(0, width * depth).Select(cell =>
            {
                int x = cell % width;
                return x < 4 ? new SurfaceSample(x * rise + rise / 2, Slope: rise) :
                    new SurfaceSample(x < 8 ? 4 * rise : size);
            }).ToArray();
            var slopes = Enumerable.Range(0, samples.Length).Select(cell => cell % width < 4 ? new SurfaceRise(rise) : default);
            var grid = new NavigationGrid(width, depth, surface: samples, rises: slopes);
            var profile = new Locomotor("ramp", [TerrainRule.Ground], maximumSlope: rise, maximumStep: 0);
            using var world = new SimulationWorld([Mover(radius: 384, profile: profile)], width * size, depth * size, navigationGrid: grid);
            var start = new WorldPosition(size + 377, 3 * size + 128, -500);
            world.AddInitialUnit(0, start);
            NavigationChecks.Check(Pose(world, 1).Current.Elevation == start.X / 8, "Spawn did not project onto the ramp");
            var goal = new WorldPosition(6 * size + 311, start.Y, 4 * rise);
            Move(world, 1, goal);
            int previous = start.X / 8;
            while (world.MovingCount > 0 && world.Tick < 100)
            {
                var current = Pose(world, 1).Current;
                NavigationChecks.Check(current.Elevation == grid.SurfaceElevationAt(current) &&
                    current.Elevation - previous is >= 0 and <= 16, "Ramp movement lost its continuous support");
                previous = current.Elevation;
                world.Step();
            }
            NavigationChecks.Check(Pose(world, 1).Current == goal, "A zero-step profile failed to follow connected ramp cells");
            Move(world, 1, new(9 * size + 128, start.Y, size));
            Finish(world);
            NavigationChecks.Check(Pose(world, 1).Current is { X: < 8 * size - 384, Elevation: 4 * rise }, "Body crossed or touched the cliff");
            var wide = new DestinationResolver(grid, new Locomotor("wide", [TerrainRule.Ground], BodyShape.Box(size, 384), maximumStep: 0));
            NavigationChecks.Check(!wide.CanStop(new(9 * size, start.Y)) && wide.CanStop(new(9 * size + 1, start.Y)),
                "A body tangent to the cliff from the high side was not kept clear");

            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true);
            int editedCell = grid.Index(new(0, 0));
            grid.WithBlocked(editedCell, true).WriteRuntimeState(writer);
            state.Position = 0;
            using var reader = new BinaryReader(state);
            var restored = grid.ReadRuntimeState(reader);
            NavigationChecks.Check(restored.IsBlocked(editedCell) && restored.SurfaceElevationAt(start) == grid.SurfaceElevationAt(start),
                "Runtime grid restore lost the immutable ramp geometry");
        });
        test("Diagonal ramp: zero-step locomotor ascends and descends at exact XY", () =>
        {
            const int size = WorldPosition.UnitsPerCell, side = 10, rise = 128;
            var samples = new SurfaceSample[side * side];
            var rises = new SurfaceRise[samples.Length];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int i = y * side + x;
                    samples[i] = new(Height(x) + Height(y));
                    rises[i] = new(AxisRise(x), AxisRise(y));
                }
            var grid = new NavigationGrid(side, side, surface: samples, rises: rises);
            var profile = new Locomotor("diagonal", [TerrainRule.Ground], maximumSlope: 182, maximumStep: 0);
            var steep = new DestinationResolver(grid, new Locomotor("steep", [TerrainRule.Ground], maximumSlope: 181));
            NavigationChecks.Check(!steep.CanStop(new(4 * size + 512, 4 * size + 512)), "Steep diagonal slope accepted a weaker locomotor.");
            using var world = new SimulationWorld([Mover(profile: profile)], side * size, side * size, navigationGrid: grid);
            var low = new WorldPosition(1501, 1713);
            var high = new WorldPosition(8451, 8687, 10 * rise);
            world.AddInitialUnit(0, low);
            Traverse(high);
            Traverse(low);

            void Traverse(WorldPosition goal)
            {
                Move(world, 1, goal);
                bool onDiagonal = false;
                for (int t = 0; t < 200; t++)
                {
                    var pose = Pose(world, 1);
                    NavigationChecks.Check(pose.Current.Elevation == grid.SurfaceElevationAt(pose.Current) &&
                        Math.Abs(pose.Current.Elevation - pose.Previous.Elevation) <= 24, "Diagonal ramp caused a height jump.");
                    var plane = grid.SurfaceRiseAt(grid.CellAt(pose.Current));
                    onDiagonal |= plane is { X: not 0, Y: not 0 };
                    if (world.MovingCount == 0) break;
                    world.Step();
                }
                NavigationChecks.Check(Pose(world, 1).Current == goal && onDiagonal, "Unit did not reach the precise goal via the diagonal slope.");
            }

            static int AxisRise(int cell) => cell is >= 2 and < 7 ? rise : 0;
            static int Height(int cell) => Math.Clamp(cell - 2, 0, 5) * rise + AxisRise(cell) / 2;
        });
        test("Diagonal cliff: a traversable section between two cliff faces preserves body clearance", () =>
        {
            var grid = new NavigationGrid(2, 1, surface: [new(32), new(64)], rises: [new(64, 256), default]);
            foreach (var body in new[] { BodyShape.Circle(128), BodyShape.Box(128, 128) })
            {
                var profile = new Locomotor("step", [TerrainRule.Ground], body, maximumStep: 64);
                var resolver = new DestinationResolver(grid, profile);
                NavigationChecks.Check(resolver.CanStop(new(1024, 512)) && resolver.CanReach(new(512, 512), new(1536, 512)),
                    "A shallow crossing between the cliffs is blocked.");
                NavigationChecks.Check(!resolver.CanStop(new(1024, 160)) && !resolver.CanStop(new(1024, 864)) &&
                    !resolver.CanStop(new(1024, 350)), "Cliffs or swept body clearance were ignored.");
                NavigationChecks.Check(!resolver.CanStop(new(1024 - body.HalfWidth, 160)) &&
                    resolver.CanStop(new(1023 - body.HalfWidth, 160)), "Cliff clearance did not distinguish tangency from a gap.");
                using var world = new SimulationWorld([new("unit", 100, body: body, locomotor: profile)], 2048, 1024, navigationGrid: grid);
                world.AddInitialUnit(0, new(500, 512));
                var goal = new WorldPosition(1513, 512, 64);
                Move(world, 1, goal);
                Finish(world);
                NavigationChecks.Check(Pose(world, 1).Current == goal, "The route follower could not move through the shallow crossing.");
            }
        });
        test("Diagonal cliff halves: height, clearance and step limit apply within one cell", () =>
        {
            const int size = WorldPosition.UnitsPerCell;
            var grid = new NavigationGrid(1, 1, surface: [default],
                cuts: new Dictionary<int, SurfaceCut> { [0] = new(SurfaceCorner.NorthEast, size) });
            var high = new WorldPosition(768, 256, size);
            var low = new WorldPosition(256, 768);
            NavigationChecks.Check(grid.SurfaceElevationAt(high) == size && grid.SurfaceElevationAt(low) == 0 &&
                grid.SurfaceElevationAt(new WorldPosition(512, 512)) == size, "A triangle or diagonal tie used the wrong height.");
            foreach (var body in new[] { BodyShape.Circle(128), BodyShape.Box(128, 64) })
            {
                var strict = new Locomotor("strict", [TerrainRule.Ground], body, maximumStep: size - 1);
                var resolver = new DestinationResolver(grid, strict);
                NavigationChecks.Check(resolver.CanStop(high) && resolver.CanStop(low) && !resolver.CanReach(high, low) &&
                    !resolver.CanStop(new(560, 512)), "One triangle was blocked or a swept body crossed the wall.");

                var stepping = new Locomotor("step", [TerrainRule.Ground], body, maximumStep: size);
                using var world = new SimulationWorld([new("unit", 100, body: body, locomotor: stepping)], size, size, navigationGrid: grid);
                world.AddInitialUnit(0, high with { Elevation = -1 });
                NavigationChecks.Check(Pose(world, 1).Current == high, "Spawn did not select the upper triangle.");
                Move(world, 1, low);
                Finish(world);
                NavigationChecks.Check(Pose(world, 1).Current == low, "A locomotor with sufficient step could not cross the wall.");
            }
        });
        test("Split terrain: both body shapes respect half-cell traversal and stopping", () =>
        {
            var grid = new NavigationGrid(1, 1, surface: [default],
                cuts: new Dictionary<int, SurfaceCut> { [0] = new(SurfaceCorner.NorthEast, 0, UpperTerrain: 1) });
            var land = new WorldPosition(256, 768);
            var water = new WorldPosition(768, 256);
            var overlapping = new WorldPosition(496, 528);
            NavigationChecks.Check(grid.TerrainAt(land) == 0 && grid.TerrainAt(water) == 1 && grid.MaximumTerrain == 1,
                "The terrain palette does not include both halves.");
            foreach (var body in new[] { BodyShape.Circle(64), BodyShape.Box(64, 32) })
            {
                var landOnly = new DestinationResolver(grid, new Locomotor("land", [TerrainRule.Ground, TerrainRule.Blocked], body));
                var waterOnly = new DestinationResolver(grid, new Locomotor("water", [TerrainRule.Blocked, TerrainRule.Ground], body));
                var noStop = new DestinationResolver(grid,
                    new Locomotor("cross", [TerrainRule.Ground, TerrainRule.Ground with { CanStop = false }], body));
                NavigationChecks.Check(landOnly.CanStop(land) && !landOnly.CanStop(overlapping) && !landOnly.CanReach(land, water) &&
                    waterOnly.CanStop(water) && !waterOnly.CanStop(land) && noStop.CanReach(land, water) &&
                    !noStop.CanStop(overlapping), "Traverse or CanStop ignored a forbidden triangle under the body.");
            }

            var painted = grid.Compose(new Dictionary<int, CellOverride> { [0] = new(Terrain: 0) }, grid);
            var landProfile = new Locomotor("land-building", [TerrainRule.Ground, TerrainRule.Blocked]);
            var footprint = Stamp(1, 1, new(false));
            NavigationChecks.Check(!footprint.AllowsConstruction(grid, new(512, 512), landProfile) &&
                footprint.AllowsConstruction(painted, new(512, 512), landProfile),
                "A full-cell footprint did not check both triangles for supporting terrain.");
            NavigationChecks.Check(grid.TerrainAt(water) == 1 && painted.TerrainAt(water) == 0 && painted.Revision == 1,
                "Painting the original lower terrain did not override the upper triangle.");
            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state, System.Text.Encoding.UTF8, leaveOpen: true);
            painted.WriteRuntimeState(writer);
            state.Position = 0;
            using var reader = new BinaryReader(state);
            var restored = grid.ReadRuntimeState(reader);
            NavigationChecks.Check(restored.TerrainAt(water) == 0 && grid.Compose(new Dictionary<int, CellOverride>(), restored)
                .TerrainAt(water) == 1, "Runtime restore or overlay removal lost a triangle's terrain.");
        });
        test("Split terrain: speed uses the center and route price charges the crossed half", () =>
        {
            int StepX(WorldPosition start, WorldPosition goal)
            {
                var grid = new NavigationGrid(1, 1, surface: [default],
                    cuts: new Dictionary<int, SurfaceCut> { [0] = new(SurfaceCorner.NorthEast, 0, UpperTerrain: 1) });
                var profile = new Locomotor("speed", [TerrainRule.Ground, TerrainRule.Ground with { Speed = 512 }]);
                using var world = new SimulationWorld([Mover(radius: 64, profile: profile)], 1024, 1024, navigationGrid: grid);
                world.AddInitialUnit(0, start);
                Move(world, 1, goal);
                return Pose(world, 1).Current.X - start.X;
            }
            NavigationChecks.Check(StepX(new(192, 768), new(576, 768)) == 128 &&
                StepX(new(448, 192), new(896, 192)) == 64, "Terrains in the same cell did not give distinct movement speeds.");

            int TimeOnUpper(int upperCost)
            {
                var grid = new NavigationGrid(4, 4, surface: new SurfaceSample[16],
                    cuts: new Dictionary<int, SurfaceCut> { [5] = new(SurfaceCorner.NorthEast, 0, UpperTerrain: 1) });
                var profile = new Locomotor("price", [TerrainRule.Ground, TerrainRule.Ground with { Cost = upperCost }]);
                using var world = new SimulationWorld([Mover(radius: 64, profile: profile)], 4096, 4096, navigationGrid: grid);
                var goal = new WorldPosition(1584, 1552);
                world.AddInitialUnit(0, new(2600, 1152));
                Move(world, 1, goal);
                int upperTicks = 0;
                for (int tick = 0; tick < 80; tick++)
                {
                    if (grid.TerrainAt(Pose(world, 1).Current) == 1) upperTicks++;
                    if (world.MovingCount == 0) break;
                    world.Step();
                }
                NavigationChecks.Check(Pose(world, 1).Current == goal, "The mixed-terrain route missed its exact goal.");
                return upperTicks;
            }
            NavigationChecks.Check(TimeOnUpper(65536) < TimeOnUpper(1024), "The route did not avoid the expensive triangle.");
        });
        test("N2: weapon range reaches the nearest building cell rather than its center", () =>
        {
            var gun = new UnitDefinition("gun", 100, ["idle", "move", "fire"], weapon: new(5120, 5120, 20, 100, 0), body: BodyShape.Circle(512));
            using var world = new SimulationWorld([gun, Building("b", Stamp(6, 2, new(false, false)))], 16384, 8192, navigationGrid: new(16, 8));
            world.AddInitialUnit(0, new(4096, 4096), 1); world.AddInitialUnit(1, new(12288, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(Pose(world, 2).Health == 80 && world.MovingCount == 0, "Large target was out of range by center or blocked its own LoS");
            var shot = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired); NavigationChecks.Check(shot.Position.X == 9216 && shot.SourcePosition.X == 4608, "Aim/muzzle points ignored edges");
        });
        test("N2: a projectile hits the front of a large footprint found outside the center query", () =>
        {
            var gun = new UnitDefinition("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 20, 100, 0, projectile: ProjectileKind.Cannon, projectileSpeed: 512));
            using var world = new SimulationWorld([gun, Building("b", Stamp(6, 2, new(false, false)))], 16384, 8192, navigationGrid: new(16, 8));
            world.AddInitialUnit(0, new(1024, 4096), 1); world.AddInitialUnit(1, new(12288, 4096), 2); world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            WorldPosition impact = default; for (int t = 0; t < 20; t++) { world.Step(); foreach (var e in world.Events) if (e.Kind == WorldEventKind.ProjectileImpact) impact = e.Position; }
            NavigationChecks.Check(Pose(world, 2).Health == 80 && impact.X == 9216, "Large footprint was missed or hit only at center");
        });
        test("N2: splash intersects body extents and multiple footprint cells cause one damage application", () =>
        {
            var gun = new UnitDefinition("g", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 20, 100, 0, projectile: ProjectileKind.Grenade, projectileSpeed: 4096, splashRadius: 1200));
            using var world = new SimulationWorld([gun, Building("b", Stamp(6, 2, new(false, false))), Mover("large", 512)], 16384, 10240);
            world.AddInitialUnit(0, new(1024, 4096), 1); world.AddInitialUnit(1, new(12288, 4096), 2); world.AddInitialUnit(2, new(8192, 5120), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]); for (int t = 0; t < 3; t++) world.Step();
            NavigationChecks.Check(Pose(world, 2).Health == 80 && Pose(world, 3).Health == 80, "Splash ignored extent or multiplied per-cell damage");
        });
        test("N2: lethal damage removes the building stamp while visual wrecks stay adapter-owned", () =>
        {
            var gun = new UnitDefinition("g", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 1000, 100, 0));
            using var world = new SimulationWorld([gun, Building("b", Stamp(2, 2, new(false, false)))], 12288, 8192, navigationGrid: new(12, 8));
            world.AddInitialUnit(0, new(2048, 4096), 1); world.AddInitialUnit(1, new(6144, 4096), 2);
            world.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            NavigationChecks.Check(world.Count == 1 && !world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(new(6144, 4096))) && world.Events.ToArray().Any(e => e.Kind == WorldEventKind.Destroyed && e.Owner == 2), "Death retained navigation state or lost wreck ownership");
        });
        test("N2: forest stamps change locomotor access on road and removal restores it", () =>
        {
            var infantry = new Locomotor("i", [TerrainRule.Ground, TerrainRule.Ground]); var tank = new Locomotor("t", [TerrainRule.Ground, TerrainRule.Blocked]);
            using var world = new SimulationWorld([Mover("i", profile: infantry), Mover("t", profile: tank), Building("trees", Stamp(2, 8, new(true, true, 1)))], 16384, 8192, navigationGrid: new(16, 8));
            world.AddInitialUnit(2, new(8192, 4096)); world.AddInitialUnit(0, new(2048, 2048)); world.AddInitialUnit(1, new(2048, 6144));
            world.Step([new(1, 0, 1, CommandKind.Move, new(2), new(14336, 2048)), new(1, 0, 2, CommandKind.Move, new(3), new(14336, 6144))]); Finish(world);
            NavigationChecks.Check(Pose(world, 2).Current.X == 14336 && Pose(world, 3).Current.X < 7168, "Forest override ignored locomotor");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Destroy, new(1))]); Move(world, 3, new(14336, 6144)); Finish(world);
            NavigationChecks.Check(Pose(world, 3).Current.X == 14336, "Terrain did not restore after neutral removal");
        });
        test("N2: different circle radii remain separated over complete swept moves", () =>
        {
            using var world = new SimulationWorld([Mover("small", 160), Mover("large", 640)], 16384, 10240, speedPerTick: 256, navigationGrid: new(16, 10));
            world.AddInitialUnit(0, new(2048, 4096)); world.AddInitialUnit(1, new(8192, 4096));
            world.Step([new(1, 0, 1, CommandKind.Move, new(1), new(10240, 4096)), new(1, 0, 2, CommandKind.Move, new(2), new(2048, 4096))]);
            for (int tick = 0; tick < 250 && world.MovingCount > 0; tick++)
            {
                var a = Pose(world, 1); var b = Pose(world, 2);
                decimal x = a.Previous.X - b.Previous.X, y = a.Previous.Y - b.Previous.Y;
                decimal dx = a.Current.X - a.Previous.X - (b.Current.X - b.Previous.X), dy = a.Current.Y - a.Previous.Y - (b.Current.Y - b.Previous.Y);
                decimal length = dx * dx + dy * dy, t = length == 0 ? 0 : Math.Clamp(-(x * dx + y * dy) / length, 0, 1);
                NavigationChecks.Check((x + dx * t) * (x + dx * t) + (y + dy * t) * (y + dy * t) >= 800 * 800, "Mixed radii overlapped during interpolation"); world.Step();
            }
            NavigationChecks.Check(world.MovingCount == 0, "Mixed circle traffic did not finish");
        });
        test("N2: large group destinations use body-dependent spacing", () =>
        {
            using var world = new SimulationWorld([Mover(radius: 640)], 20480, 14336, navigationGrid: new(20, 14));
            for (int i = 0; i < 4; i++) world.AddInitialUnit(0, new((2 + i % 2 * 3) * 1024, (2 + i / 2 * 3) * 1024));
            world.Step([new(1, 0, 1, CommandKind.MoveGroup, Position: new(10240, 8192))]); Finish(world, 300);
            var poses = Poses(world); NavigationChecks.Check(world.MovingCount == 0, "Oversized bodies were packed into impossible goals");
            for (int i = 0; i < poses.Length; i++) for (int j = i + 1; j < poses.Length; j++)
            { long x = poses[i].Current.X - poses[j].Current.X, y = poses[i].Current.Y - poses[j].Current.Y; NavigationChecks.Check(x * x + y * y >= 1280L * 1280, "Group slots overlap large bodies"); }
        });
        test("N2: dynamic clearance fields and overlays remain canonical across workers", () =>
        {
            SimulationWorld Create(int workers)
            {
                var w = new SimulationWorld([Mover("s", 128), Mover("l", 384), Building("wall", Stamp(2, 4, new(false, false)))], 20480, 10240,
                    navigationGrid: new(20, 10), navigationWorkers: workers, reverseNavigationJobs: workers > 1);
                w.AddInitialUnit(0, new(2048, 2048)); w.AddInitialUnit(1, new(2048, 6144)); return w;
            }
            using var a = Create(1); using var b = Create(4);
            for (int tick = 1; tick <= 160; tick++)
            {
                WorldCommand[] commands = tick switch {
                    1 => [new(1, 0, 1, CommandKind.Move, new(1), new(18432, 2048)), new(1, 0, 2, CommandKind.Move, new(2), new(18432, 6144))],
                    3 => [new(3, 0, 1, CommandKind.Spawn, Position: new(8192, 4096), UnitType: 2)],
                    14 => [new(14, 0, 1, CommandKind.Destroy, new(3))],
                    20 => [new(20, 0, 1, CommandKind.Spawn, Position: new(12288, 4096), UnitType: 2)],
                    40 => [new(40, 0, 1, CommandKind.Remove, new(4))], _ => [] };
                a.Step(commands); b.Step(commands); NavigationChecks.Check(a.Report() == b.Report(), "Worker completion order changed N2 state");
            }
            NavigationChecks.Check(a.MovingCount == 0 && a.Count == 2, "Dynamic worker scenario did not finish");
        });
        test("N2: published terrain is immutable while stacked objects change", () =>
        {
            var p = new Locomotor("p", [TerrainRule.Ground, TerrainRule.Ground]);
            using var world = new SimulationWorld([Mover(profile: p), Building("forest", Stamp(2, 2, new(true, true, 1)))], 12288, 8192, navigationGrid: new(12, 8));
            world.AddInitialUnit(0, new(2048, 2048)); var exchange = new FrameExchange(); exchange.TryPublish(world); using var old = exchange.AcquireLatest()!;
            int c = old.Grid!.CellAt(new(6144, 4096)); world.Step([new(1, 0, 1, CommandKind.Spawn, Position: new(6144, 4096), UnitType: 1)]);
            exchange.TryPublish(world); using var stamped = exchange.AcquireLatest()!;
            world.Step([new(2, 0, 1, CommandKind.Remove, new(2))]); exchange.TryPublish(world); using var current = exchange.AcquireLatest()!;
            NavigationChecks.Check(old.Grid.TerrainAt(c) == 0 && stamped.Grid!.TerrainAt(c) == 1 && current.Grid!.TerrainAt(c) == 0, "Grid snapshots were mutated by overlay removal");
        });
    }
}
