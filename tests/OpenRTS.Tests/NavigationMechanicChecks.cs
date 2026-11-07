using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class NavigationMechanicChecks
{
    private const int Wall = 8, NorthGap = 2, SouthGap = 9;

    public static void Run(Action<string, Action> test)
    {
        test("Navigation registration: alternate field, pending route, grid revision and worker parity", () =>
        {
            var grid = new NavigationGrid(16, 12, Enumerable.Range(0, 12)
                .Where(y => y is not (NorthGap or SouthGap)).Select(y => new GridCell(Wall, y)));
            var start = grid.Center(grid.Index(new(4, 4)));
            var goal = grid.Center(grid.Index(new(12, 4)));
            var corner = grid.Center(0);
            var first = new SouthPassageFields(grid, 1);
            var parallel = new SouthPassageFields(grid, 4);
            using var builtIn = Create(grid, new(), start);
            using var a = Create(grid, Mechanics(() => first), start);
            using var b = Create(grid, Mechanics(() => parallel), start);
            using var revised = Create(grid, Mechanics(() => new SouthPassageFields(grid, 1), version: 2), start);
            NavigationChecks.Check(a.Report() != builtIn.Report() && a.Report() != revised.Report(), "The report omits the pathfinder registration");

            int builtInCrossing = -1, customCrossing = -1;
            for (int tick = 1; tick <= 64; tick++)
            {
                WorldCommand[] commands = tick switch
                {
                    1 => [new(1, 0, 1, CommandKind.Move, new(1), goal)],
                    3 => [new(3, 0, 1, CommandKind.BlockCell, Position: corner)],
                    _ => []
                };
                builtIn.Step(commands);
                a.Step(commands);
                b.Step(commands);
                var stock = Poses(builtIn).Single().Current;
                var custom = Poses(a).Single().Current;
                if (tick == 1) NavigationChecks.Check(custom == start && a.NavigationWaitingCount == 1, "A pending field rejected or moved the unit");
                if (stock.X >= Wall * grid.CellSize && builtInCrossing < 0) builtInCrossing = stock.Y / grid.CellSize;
                if (custom.X >= Wall * grid.CellSize && customCrossing < 0) customCrossing = custom.Y / grid.CellSize;
                NavigationChecks.Check(!a.NavigationGrid!.IsBlocked(a.NavigationGrid.CellAt(custom)), "The unit entered a blocked cell");
                NavigationChecks.Check(a.Report() == b.Report() && a.Events.SequenceEqual(b.Events), "Registered fields differ by worker count");
            }
            NavigationChecks.Check(builtInCrossing == NorthGap && customCrossing == SouthGap && Poses(a).Single().Current == goal,
                "The follower did not consume the registered field's detour");
            NavigationChecks.Check(first.GridChanges == 1 && parallel.GridChanges == 1 && ReferenceEquals(first.Grid, a.NavigationGrid) &&
                first.Grid.IsBlocked(0), "The pathfinder did not receive the committed effective grid");
        });

        test("Navigation registration: incompatible worlds fail during creation", () =>
        {
            var grid = new NavigationGrid(8, 8);
            var start = grid.Center(0);
            int calls = 0;
            var mechanics = Mechanics(() => { calls++; return new SouthPassageFields(grid, 1); });
            NavigationChecks.Reject<ArgumentException>(() => { using var _ = SimulationWorld.CreateWithMechanics(mechanics, [new("unit", 100)], 8192, 8192); });
            var weighted = new NavigationGrid(8, 8, terrain: new byte[64]);
            NavigationChecks.Reject<ArgumentException>(() => { using var _ = Create(weighted, mechanics, start); });
            NavigationChecks.Check(calls == 0, "An incompatible world invoked the pathfinder factory");
            NavigationChecks.Reject<ArgumentException>(() => { using var _ = Create(grid, Mechanics(() => new GridNavigator(new(8, 8))), start); });
        });
    }

    private static GameplayMechanics Mechanics(Func<IGridPathfinder> create, int version = 1) =>
        new() { GridPathfinder = new("example.south-passage-fields", version, create) };

    private static SimulationWorld Create(NavigationGrid grid, GameplayMechanics mechanics, WorldPosition start)
    {
        var world = SimulationWorld.CreateWithMechanics(mechanics, [new("unit", 100)],
            grid.Width * grid.CellSize, grid.Height * grid.CellSize, speedPerTick: 512, navigationGrid: grid);
        world.AddInitialUnit(0, start);
        return world;
    }

    private sealed class SouthPassageFields(NavigationGrid grid, int workers) : IGridPathfinder
    {
        private readonly GridNavigator _fields = new(WithoutNorthPassage(grid), workers, reverseJobs: workers > 1);
        private bool _delayed;
        public int GridChanges { get; private set; }
        public NavigationGrid Grid { get; private set; } = grid;
        public int LastBuildCount => _fields.LastBuildCount;
        public int LastVisitedCells => _fields.LastVisitedCells;
        public double LastBuildMilliseconds => _fields.LastBuildMilliseconds;

        public void SetGrid(NavigationGrid next)
        {
            Grid = next;
            _fields.SetGrid(WithoutNorthPassage(next));
            GridChanges++;
        }

        public void Prepare(ReadOnlySpan<int> goals, CancellationToken cancellationToken)
        {
            if (!_delayed && !goals.IsEmpty)
            {
                _delayed = true;
                goals = [];
            }
            _fields.Prepare(goals, cancellationToken);
        }

        public bool TryDistance(int goal, int cell, out int distance) => _fields.TryDistance(goal, cell, out distance);

        public void WriteState(BinaryWriter writer)
        {
            writer.Write(_delayed);
            _fields.WriteState(writer);
        }

        private static NavigationGrid WithoutNorthPassage(NavigationGrid source) =>
            source.WithBlocked(source.Index(new(Wall, NorthGap)), true);
    }
}
