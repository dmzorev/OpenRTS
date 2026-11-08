using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class SupplyChecks
{
    private static readonly FootprintDefinition Dock = new(2, 2,
        [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);

    private static UnitDefinition Truck(int capacity) => new("truck", 100,
        abilities: [new("harvest", "", AutoCast: true, Kind: AbilityKind.Harvest)], supply: new(SupplyRole.Truck, capacity));
    private static UnitDefinition Mine(int capacity) => new("mine", 100, movable: false, footprint: Dock,
        destruction: DestructionPolicy.Neutralize, supply: new(SupplyRole.Mine, capacity));
    private static UnitDefinition Depot() => new("depot", 100, movable: false, footprint: Dock, supply: new(SupplyRole.Depot));

    private static EntityPose Pose(SimulationWorld world, EntityId id) => Poses(world).Single(p => p.Id == id);
    private static long Credits(SimulationWorld world, uint owner)
    {
        var players = new PlayerPose[world.PlayerCount];
        world.CopyPlayers(players);
        return players.Single(p => p.Id == owner).Credits;
    }

    public static void Run(Action<string, Action> test)
    {
        test("Supply: nearby crates fill one trip and disappear, then a partial load returns", () =>
        {
            var crate = UnitCatalog.Load([new("crate.yaml", """
                crate:
                  Health: {HP: 20}
                  Movement: {Enabled: false}
                  Body: {Radius: 128}
                  Supply: {Role: Mine, Capacity: 8, InitialAmount: 3, RemoveWhenEmpty: true}
                """)]).Units[0];
            using var world = new SimulationWorld([Truck(8), crate, Depot()], 24 * 1024, 16 * 1024,
                navigationGrid: new(24, 16), speedPerTick: 512);
            world.AddInitialUnit(2, new(2048, 8192), 1);
            var truck = world.AddInitialUnit(0, new(8192, 8192), 1);
            var first = world.AddInitialUnit(1, new(9216, 8192));
            var second = world.AddInitialUnit(1, new(11264, 8192));
            var distant = world.AddInitialUnit(1, new(18432, 8192));
            NavigationChecks.Check(Pose(world, first).Supply is { Amount: 3, Capacity: 8 }, "Wrong starting stock");
            world.Step([new(1, 0, 1, CommandKind.UseAbility, truck, TargetEntity: first, Action: "harvest")]);
            var removed = new List<EntityId>();
            for (int i = 0; i < 160 && Credits(world, 1) < 6; i++)
            {
                foreach (var value in world.Events)
                    if (value.Kind == WorldEventKind.Removed) removed.Add(value.Entity);
                world.Step();
            }
            NavigationChecks.Check(removed.SequenceEqual([first, second]) && Credits(world, 1) == 6 &&
                Pose(world, distant).Supply.Amount == 3 && Pose(world, truck).Supply.Mine == default,
                "The truck did not collect both nearby crates before unloading, or included the distant crate");
        });

        test("Supply: one truck per mine and depot, four units unloaded per tick", () =>
        {
            using var world = new SimulationWorld([Truck(8), Mine(16), Depot()], 20000, 16000);
            var mine = world.AddInitialUnit(1, new(8192, 8192));
            var otherMine = world.AddInitialUnit(1, new(12288, 4096));
            world.AddInitialUnit(2, new(12288, 8192), 1);
            var first = world.AddInitialUnit(0, new(10240, 8192), 1);
            var second = world.AddInitialUnit(0, new(12288, 6144), 1);
            var waiting = world.AddInitialUnit(0, new(10240, 9216), 1);
            for (int i = 0; i < 8; i++) world.Step();
            NavigationChecks.Check(Pose(world, first).Supply.Amount == 8 && Pose(world, second).Supply.Amount == 8 &&
                Pose(world, waiting).Supply.Amount == 0 && Pose(world, mine).Supply.Amount == 8 && Credits(world, 1) == 0,
                "Loading advanced more than one truck per mine or paid before unloading");
            world.Step();
            NavigationChecks.Check(Credits(world, 1) == 4 && Pose(world, first).Supply.Amount == 4 &&
                Pose(world, second).Supply.Amount == 8 && Pose(world, waiting).Supply.Amount == 1, "Dock queue or unloading rate differs");
            world.Step(); world.Step();
            NavigationChecks.Check(Credits(world, 1) == 12 && Pose(world, second).Supply.Amount == 4, "Second truck did not take its turn");
            world.Step([new(12, 0, 1, CommandKind.HoldPosition, second)]);
            NavigationChecks.Check(Pose(world, second).Supply.Amount == 4 && Pose(world, second).Order.Phase == OrderPhase.Holding,
                "A regular order must suspend unloading");
            for (int i = 0; i < 200; i++) world.Step();
            NavigationChecks.Check(Pose(world, mine).Supply.Amount == 0 && Pose(world, otherMine).Supply.Amount == 0 &&
                Credits(world, 1) == 28 && Pose(world, second).Supply.Amount == 4,
                "Exhaustion or interrupted delivery created or lost resources");
        });

        test("Supply: mine upgrade pauses service on arrival and keeps stock through ownership changes", () =>
        {
            const int upgradeTicks = 150;
            var engineer = new UnitDefinition("engineer", 100, constructionRange: 1536);
            var upgradeableMine = new UnitDefinition("mine", 100, states: ["owned"], movable: false, footprint: Dock,
                destruction: DestructionPolicy.Neutralize, supply: new(SupplyRole.Mine, 300),
                upgrades: [new("owned", 900, upgradeTicks, support: true, claimNeutral: true)]);
            var gun = new UnitDefinition("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 101, 1000, 0));
            using var world = new SimulationWorld([Truck(1000), upgradeableMine, engineer, gun], 24000, 16000,
                speedPerTick: 512, players: [new(1, "Builder", 2000), new(2, "Rival", 0)]);
            var mine = world.AddInitialUnit(1, new(12288, 8192));
            var builder = world.AddInitialUnit(2, new(4096, 8192), 1);
            var enemy = world.AddInitialUnit(0, new(14336, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: mine)]);
            NavigationChecks.Check(Credits(world, 1) == 1100 && Pose(world, mine).Upgrade is { Payer: 1, Work: 0 } && Pose(world, mine).Supply.Amount == 299,
                "The blueprint did not charge immediately or blocked mining before arrival");
            world.Step([new(2, 0, 1, CommandKind.CancelUpgrade, Owner: 1, TargetEntity: mine)]);
            NavigationChecks.Check(Credits(world, 1) == 2000 && Pose(world, mine).Upgrade.Project is null, "Cancelling an untouched blueprint did not refund it");
            world.Step([new(3, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: mine)]);
            for (int i = 0; i < 60 && Pose(world, mine).Upgrade.Work == 0; i++) world.Step();
            var upgrading = Pose(world, mine);
            NavigationChecks.Check(upgrading.Upgrade.Active && upgrading.Presentation.Owner == 1 && upgrading.Supply.Amount < 298,
                "Engineer did not approach, start and claim the mine");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.CancelUpgrade, Owner: 1, TargetEntity: mine)]);
            NavigationChecks.Check(Credits(world, 1) == 2000 && Pose(world, mine).Presentation.Owner == 0 &&
                Pose(world, mine).Upgrade.Project is null, "Cancelling work did not refund it and return the mine to neutral");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: mine)]);
            int stock = Pose(world, mine).Supply.Amount;
            for (int i = 0; i < upgradeTicks; i++) world.Step();
            NavigationChecks.Check(Pose(world, mine).Upgrade.Completed == "owned" && Pose(world, mine).Supply.Amount == stock &&
                Pose(world, enemy).Supply.Amount == 300 - stock, "Upgrade failed, consumed ore or let a rival load");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Spawn, Position: new(12288, 6144), UnitType: 0, Owner: 1)]);
            int remaining = Pose(world, mine).Supply.Amount;
            NavigationChecks.Check(remaining == stock - 2, "An owned mine must load twice as fast");
            world.Step([
                new(world.Tick + 1, 0, 1, CommandKind.SetAutoCast, new(4), Action: "harvest", Enabled: false),
                new(world.Tick + 1, 0, 2, CommandKind.Spawn, Position: new(17408, 8192), UnitType: 3, Owner: 2),
                new(world.Tick + 1, 0, 3, CommandKind.AttackTarget, new(5), TargetEntity: mine)]);
            NavigationChecks.Check(Pose(world, mine).Presentation.Owner == 0 && Pose(world, mine).Supply.Amount == remaining,
                "A lethal hit must restore the neutral mine with the same stock");
        });

        test("Supply: a produced truck travels and pays into its own account on the Valley", () =>
        {
            static ContentSource Read(string path) => new(path, File.ReadAllText(Path.Combine("sandbox", path)));
            var catalog = UnitCatalog.Load([Read("content/units.yaml"), Read("content/buildings.yaml")],
                LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            var oil = catalog.Units.Single(u => u.Id == "oil-derrick");
            var depot = catalog.Units.Single(u => u.Supply?.Role == SupplyRole.Depot);
            NavigationChecks.Check(oil.Build is null && oil.Capturable is not null && oil.Economy?.Income == 30 &&
                depot.ConstructionRange == 0 && depot.Products.Select(p => p.Unit).SequenceEqual(["construction-engineer", "supply-truck"]),
                "Oil or depot content no longer meets the supply contract");
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var a = map.CreateWorld(1);
            using var b = map.CreateWorld(4);
            for (int i = 1; i <= 950; i++)
            {
                WorldCommand[] commands = i switch
                {
                    1 =>
                    [
                        new(1, 0, 1, CommandKind.EnqueueTask, new(1), Action: "truck"),
                        new(1, 0, 2, CommandKind.SetAutoCast, Recipients: new([new(3), new(6)]), Action: "harvest", Enabled: false)
                    ],
                    115 =>
                    [
                        new(115, 0, 1, CommandKind.UseAbility, new(15), TargetEntity: new(7), Action: "harvest"),
                        new(115, 0, 2, CommandKind.UseAbility, new(15), TargetEntity: new(1), Action: "harvest")
                    ],
                    _ => []
                };
                a.Step(commands);
                b.Step(commands);
            }
            var truck = Poses(a).Single(p => p.Id.Value == 15 && catalog.Units[p.UnitType].Supply?.Role == SupplyRole.Truck);
            NavigationChecks.Check(truck.Supply is { Automatic: true, Mine.Value: 7, Depot.Value: 1 } && Credits(a, 1) > 4650 && Credits(a, 2) == 5000 &&
                Poses(a).Any(p => catalog.Units[p.UnitType].Supply?.Role == SupplyRole.Mine && p.Supply.Amount < p.Supply.Capacity),
                $"No delivery reached the correct account: {truck.Current}, {truck.Supply}, credits {Credits(a, 1)}");
            NavigationChecks.Check(a.Report() == b.Report(), "Supply differs across navigation workers");
        });
    }
}
