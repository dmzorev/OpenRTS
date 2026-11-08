using OpenRTS.AI;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class BaseConstructionPlannerChecks
{
    private static UnitDefinition[] Types()
    {
        var builder = new UnitDefinition("builder", 100, constructionRange: 1536,
            products: [new("wall", "wall", "", OnSite: true), new("shed", "shed", "", OnSite: true)]);
        UnitDefinition Building(string id, int width) => new(id, 100, movable: false,
            footprint: new(width, 1, Enumerable.Range(0, width).Select(x => new FootprintCell(x, 0, new(false)))),
            build: new(ticks: 5, kind: ProductionKind.PlaceBuilding));
        return [builder, Building("wall", 3), Building("shed", 1)];
    }

    private static WorldPosition At(int x, int y) => new(x * 1024 + 512, y * 1024 + 512);
    private static AiBuilding Goal(int type, string action, int priority = 0) => new(new(0, type, -1, action, 0, true), 1, priority);

    private static BaseConstructionDecision? Decide(BaseConstructionPlanner plan, SimulationWorld world)
    {
        var projects = new ConstructionProjectPose[world.ConstructionProjectCount];
        world.CopyConstructionProjects(projects);
        var tasks = new TaskPose[world.TaskCount];
        world.CopyTaskPoses(tasks);
        return plan.Plan(world, Poses(world), tasks, projects, 0, p => p.Order.Phase == OrderPhase.Idle);
    }

    private sealed class FixedSearch(params BuildingSite[] sites) : IBuildingSiteSearch
    {
        public IEnumerable<BuildingSite> Candidates(NavigationGrid grid, WorldPosition center, UnitDefinition building) => sites;
    }

    public static void Run(Action<string, Action> test)
    {
        test("Base construction honors priority and rotates an asymmetric building to fit", () =>
        {
            var types = Types();
            var grid = new NavigationGrid(10, 10, from y in Enumerable.Range(0, 10)
                from x in Enumerable.Range(0, 10)
                where x != 4 || y < 1 || y > 7
                select new GridCell(x, y));
            using var world = new SimulationWorld(types, 10240, 10240, navigationGrid: grid);
            world.AddInitialUnit(0, At(4, 6), 1);
            var site = At(4, 3);
            var planner = new BaseConstructionPlanner(1, site, types, [Goal(2, "shed"), Goal(1, "wall", 50)],
                strategy: new FixedSearch(new(site, 0), new(site, 1)));
            var command = Decide(planner, world)?.Command ?? throw new Exception("No site in the corridor.");
            NavigationChecks.Check(command.Action == "wall" && command.QuarterTurns == 1 && planner.CandidatesChecked == 2,
                "The planner ignored priority or accepted the orientation across blocked cells.");
            world.Step([command.WithStamp(1, 1, 1)]);
            for (int i = 0; i < 40; i++) world.Step();
            NavigationChecks.Check(Poses(world).Any(p => p.UnitType == 1 && p.QuarterTurns == 1 && !p.Construction.Active),
                "Gameplay did not finish the rotated building at the proposed site.");
        });

        test("Base construction rejects unreachable sites across a terrain barrier", () =>
        {
            var types = Types();
            var grid = new NavigationGrid(20, 12, Enumerable.Range(0, 12).Select(y => new GridCell(9, y)));
            using var world = new SimulationWorld(types, 20480, 12288, navigationGrid: grid);
            world.AddInitialUnit(0, At(3, 5), 1);
            var local = At(6, 5);
            var planner = new BaseConstructionPlanner(1, At(14, 5), types, [Goal(1, "wall")],
                strategy: new FixedSearch(new(At(14, 5), 0), new(local, 0)));
            NavigationChecks.Check(Decide(planner, world)?.Command.Position == local && planner.CandidatesChecked == 2,
                "The planner preferred a valid footprint that the builder could not reach.");
        });

        test("Base construction abandons an unpaid blueprint after a terrain change and finds another site", () =>
        {
            var types = Types();
            using var world = new SimulationWorld(types, 20480, 12288, navigationGrid: new NavigationGrid(20, 12));
            world.AddInitialUnit(0, At(3, 5), 1);
            var first = At(9, 5);
            var next = At(12, 5);
            var planner = new BaseConstructionPlanner(1, first, types, [Goal(1, "wall")],
                strategy: new FixedSearch(new(first, 0), new(next, 0)));
            var command = Decide(planner, world)?.Command ?? throw new Exception("No initial site.");
            world.Step([command.WithStamp(1, 0, 1), new(1, 0, 2, CommandKind.BlockCell, Position: first)]);
            var cancel = Decide(planner, world)?.Command ?? throw new Exception("The blocked blueprint was left active.");
            NavigationChecks.Check(cancel.Kind == CommandKind.CancelConstruction, "A terrain change did not cancel the unpaid blueprint.");
            world.Step([cancel.WithStamp(2, 1, 1)]);
            NavigationChecks.Check(world.ConstructionProjectCount == 0 && Decide(planner, world)?.Command.Position == next,
                "The search repeated the blocked site instead of trying a new one.");
        });

        test("Base construction bounds each search and resumes its cursor", () =>
        {
            var types = Types();
            using var world = new SimulationWorld(types, 20480, 12288, navigationGrid: new NavigationGrid(20, 12));
            world.AddInitialUnit(0, At(3, 5), 1);
            var valid = At(6, 5);
            var candidates = Enumerable.Repeat(new BuildingSite(default, 0), 300).Append(new(valid, 0)).ToArray();
            var planner = new BaseConstructionPlanner(1, valid, types, [Goal(1, "wall")], strategy: new FixedSearch(candidates));
            NavigationChecks.Check(Decide(planner, world) is null && planner.CandidatesChecked == 256,
                "The planner exceeded the per-cycle candidate budget.");
            world.Step();
            NavigationChecks.Check(Decide(planner, world)?.Command.Position == valid && planner.CandidatesChecked == 301,
                "The planner did not continue from the next candidate.");
        });

        test("Base construction places ready buildings within the producer's recipe radius", () =>
        {
            var types = Types();
            types[0] = new("yard", 100, movable: false, taskQueues: [new("buildings")], products:
                [new("wall", "wall", "buildings", new(5, kind: ProductionKind.PlaceBuilding, placementRadius: 4096))]);
            using var world = new SimulationWorld(types, 20480, 12288, navigationGrid: new NavigationGrid(20, 12));
            var yard = world.AddInitialUnit(0, At(3, 5), 1);
            var site = At(6, 5);
            var planner = new BaseConstructionPlanner(1, site, types, [new(new(0, 1, 0, "wall", 0), 1)],
                strategy: new FixedSearch(new(At(16, 5), 0), new(site, 1)));
            var enqueue = Decide(planner, world)?.Command ?? throw new Exception("No order for the missing building.");
            world.Step([enqueue.WithStamp(1, 1, 1)]);
            for (int i = 0; i < 4; i++) world.Step();
            var place = Decide(planner, world)?.Command ?? throw new Exception("No order to place the ready building.");
            NavigationChecks.Check(place.Kind == CommandKind.PlaceProduced && place.Entity == yard && place.TaskId > 0 &&
                place.Position == site && place.QuarterTurns == 1 && planner.CandidatesChecked == 2, "Ready placement ignored the radius or rotation.");
            world.Step([place.WithStamp(6, 1, 1)]);
            NavigationChecks.Check(world.TaskCount == 0 && Poses(world).Any(p => p.UnitType == 1 && p.QuarterTurns == 1),
                "The proposed ready placement failed in Gameplay.");
        });
    }
}
