using DeclarativeContent;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class SkirmishAiChecks
{
    public static void Run(Action<string, Action> test)
    {
        static ContentSource Read(string path) => new(path, File.ReadAllText(Path.Combine("sandbox", path)));
        var catalog = UnitCatalog.Load([Read("content/units.yaml"), Read("content/buildings.yaml")],
            LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
        var source = Read("content/skirmish-ai.yaml");
        var profile = SkirmishAiCatalog.Load(source, catalog, SkirmishAiScenario.ProfileId);

        test("AI2 compiles shared sergeants with profile-specific rules", () =>
        {
            NavigationChecks.Reject<ContentException>(() => SkirmishAiCatalog.Load(
                source with { Text = source.Text.Replace("Action: site-barracks", "Action: site-factory") }, catalog, profile.Id));
            NavigationChecks.Reject<ContentException>(() => SkirmishAiCatalog.Load(
                source with { Text = source.Text.Replace("Target: 5", "Target: 2") }, catalog, profile.Id));
            var smaller = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("Target: 5", "Target: 3") }, catalog, profile.Id);
            NavigationChecks.Check(smaller.Sergeants.Single(r => r.Definition.Id == "rifle-section").Definition.Members.Single().Target == 3, "Group target did not come from YAML.");
            var infantry = profile.Sergeants.Single(r => r.Definition.Id == "rifle-section");
            var mechanized = SkirmishAiCatalog.Load(source, catalog, SkirmishAiScenario.MechanizedProfileId)
                .Sergeants.Single(r => r.Definition.Id == "rifle-section");
            NavigationChecks.Check(infantry.Definition.Members.SequenceEqual(mechanized.Definition.Members) &&
                infantry.Max > mechanized.Max && infantry.Priority > mechanized.Priority, "The shared roster must have independent general rules.");
            foreach (var (before, after) in new[]
            {
                ("Strategy: Guard", "Strategy: Unknown"),
                ("rifle-guard: {Min:", "undefined-sergeant: {Min:"),
                ("Min: 1, Max: 2", "Min: 3, Max: 2"),
                ("RetainCredits: 1800", "RetainCredits: 4300")
            })
                NavigationChecks.Reject<ContentException>(() => SkirmishAiCatalog.Load(source with { Text = source.Text.Replace(before, after) }, catalog, profile.Id));
            var legacy = SkirmishAiCatalog.Load(new("legacy.yaml", """
                legacy:
                  Version: 1
                  Buildings: {barracks: 1}
                  Group: {rifleman: {Min: 3, Target: 5}}
                  Capture: {Unit: construction-engineer, Action: capture}
                  Recipes:
                    barracks: {Producer: construction-engineer, Action: site-barracks}
                    rifleman: {Producer: barracks, Action: rifleman}
                """), catalog, "legacy");
            NavigationChecks.Check(legacy.Sergeants is [{ Min: 1, Max: 1, Definition.Strategy: SergeantStrategy.Assault }],
                "Version 1 did not compile to a single persistent assault sergeant.");
        });

        test("AI2 keeps a guard independent and rebuilds a wiped assault group", () =>
        {
            const uint player = 1;
            var fixture = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("Min: 1, Max: 2", "Min: 1, Max: 1")
                .Replace("Target: 5", "Target: 3").Replace("Target: 2, Recipe: {Producer: barracks", "Target: 1, Recipe: {Producer: barracks")
                .Replace("EarliestTick: 900", "EarliestTick: 25") }, catalog, profile.Id);
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var world = map.CreateWorld(4, restrictPlayerCommands: true);
            int rifle = fixture.Sergeants[0].Definition.Members.Single().Recipe.ProductType;
            var assault = new[] { world.AddInitialUnit(rifle, new(23552, 18432), player),
                world.AddInitialUnit(rifle, new(24576, 18432), player), world.AddInitialUnit(rifle, new(25600, 18432), player) };
            var guard = world.AddInitialUnit(rifle, new(24064, 12288), player);
            var ai = new SkirmishAi(player, fixture, catalog.Units, map.Map.StartPosition("blue"), new(24064, 18432), new(64000, 18432));
            var opening = ai.Plan(world);
            NavigationChecks.Check(opening.Single(c => c.Kind == CommandKind.AttackMove).Recipients!.Ids.SequenceEqual(assault) &&
                !opening.Any(c => c.Recipients is { } group && group.Ids.Contains(guard)), "The guard activated before its configured tick.");
            world.Step(opening);
            SkirmishAiScenario.Run(world, ai, 26);
            NavigationChecks.Check(world.AppliedCommands.ToArray().Any(c => c.Kind == CommandKind.HoldPosition && c.Recipients is { Count: 1 } group &&
                group.Ids[0] == guard), "The new guard did not keep its own orders.");
            world.Step([.. assault.Select((id, i) => new WorldCommand(27, 0, i + 1, CommandKind.Destroy, id)), .. ai.Plan(world)]);
            bool empty = false, ordered = false, rebuilt = false;
            SkirmishAiScenario.Run(world, ai, 850, state =>
            {
                empty |= ai.Diagnostics.Contains("rifle-section#1 Assault/waiting:0", StringComparison.Ordinal);
                ordered |= state.AppliedCommands.ToArray().Any(c => c.Action == "rifleman" && c.Kind == CommandKind.EnqueueTask);
                foreach (var command in state.AppliedCommands)
                    if (command.Kind == CommandKind.AttackMove)
                    {
                        NavigationChecks.Check(command.Recipients is { Count: 3 } attackers &&
                            !attackers.Ids.Contains(guard) && attackers.Ids.ToArray().All(id => !assault.Contains(id)),
                            "The recovered assault group used the guard or lost its minimum.");
                        rebuilt = true;
                    }
            });
            NavigationChecks.Check(empty && ordered && rebuilt, "The empty sergeant did not request replacements and lead them back into combat.");
        });

        test("AI2 switches strategy on threat and releases a discretionary squad after spending", () =>
        {
            const uint player = 1;
            var fixture = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("Min: 1, Max: 2", "Min: 1, Max: 1")
                .Replace("Target: 5", "Target: 3").Replace("Truck: supply-truck, Target: 2", "Truck: supply-truck, Target: 1")
                .Replace("Min: 1, Max: 1, Priority: 50, EarliestTick: 900",
                    "Min: 0, Max: 1, Priority: 50, StartCredits: 4700, RetainCredits: 4650")
                .Replace("MacroIntervalTicks: 25", "MacroIntervalTicks: 5") }, catalog, profile.Id);
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var world = map.CreateWorld();
            int Type(string id) => catalog.Units.ToList().FindIndex(u => u.Id == id);
            world.AddInitialUnit(Type("power-plant"), new(10752, 8192), player);
            var barracks = world.AddInitialUnit(Type("barracks"), new(10240, 17920), player);
            int rifle = Type("rifleman");
            var assault = new[] { world.AddInitialUnit(rifle, new(23552, 18432), player),
                world.AddInitialUnit(rifle, new(24576, 18432), player), world.AddInitialUnit(rifle, new(25600, 18432), player) };
            var guard = world.AddInitialUnit(rifle, new(24064, 12288), player);
            var ai = new SkirmishAi(player, fixture, catalog.Units, map.Map.StartPosition("blue"), new(24064, 18432), new(64000, 18432));
            var opening = ai.Plan(world);
            NavigationChecks.Check(opening.Any(c => c.Kind == CommandKind.AttackMove) && ai.Diagnostics.Contains("rifle-guard#1 Guard", StringComparison.Ordinal),
                "The extra squad was not enabled at the starting credit threshold.");
            world.Step([.. opening,
                new(1, 0, 1, CommandKind.EnqueueTask, barracks, Action: "rifleman"),
                new(1, 0, 2, CommandKind.Spawn, Position: new(35584, 18432), UnitType: rifle, Owner: 2)]);
            SkirmishAiScenario.Run(world, ai, 5);
            var changed = ai.Plan(world);
            NavigationChecks.Check(changed.Any(c => c.Kind == CommandKind.Stop && c.Recipients is { Count: 1 } group && group.Ids[0] == guard) &&
                !ai.Diagnostics.Contains("rifle-guard#1", StringComparison.Ordinal), "Spending did not deactivate the extra squad and clear its orders.");
            NavigationChecks.Check(ai.Diagnostics.Contains("rifle-section#1 Guard", StringComparison.Ordinal) &&
                changed.Any(c => (c.Kind is CommandKind.HoldPosition or CommandKind.MoveSelected) && c.Recipients is { } group &&
                    group.Ids.SequenceEqual(assault)), "The threat did not reprofile the original squad with its existing members.");
        });

        test("AI2 counts queued recruits once across separate unarmed patrols", () =>
        {
            const uint player = 1;
            var units = Read("content/units.yaml");
            var definitions = UnitCatalog.Load([units with { Text = units.Text.Replace(
                    "  Armaments:\n    Mounts:\n      main: { Weapon: missile, FireFromTransport: true }\n", "") },
                Read("content/buildings.yaml")], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            var fixture = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("Strategy: Assault", "Strategy: Patrol")
                .Replace("Strategy: Guard", "Strategy: Patrol").Replace(", UnderThreatStrategy: Guard", "")
                .Replace("rifleman", "rocketeer").Replace("Min: 3, Target: 5", "Min: 1, Target: 1")
                .Replace("Target: 2, Recipe: {Producer: barracks", "Target: 1, Recipe: {Producer: barracks")
                .Replace("Min: 1, Max: 2", "Min: 1, Max: 1").Replace("EarliestTick: 900", "EarliestTick: 0")
                .Replace("MacroIntervalTicks: 25", "MacroIntervalTicks: 1") }, definitions, profile.Id);
            var map = MapScenario.Load(definitions, Read, MapScenario.DefaultMap);
            using var world = map.CreateWorld();
            int Type(string id) => definitions.Units.ToList().FindIndex(u => u.Id == id);
            NavigationChecks.Check(definitions.Units[Type("rocketeer")].Weapons.Count == 0, "The patrol fixture must be unarmed.");
            world.AddInitialUnit(Type("power-plant"), new(10752, 8192), player);
            var first = world.AddInitialUnit(Type("barracks"), new(10240, 17920), player);
            var second = world.AddInitialUnit(Type("barracks"), new(20480, 8704), player);
            var ai = new SkirmishAi(player, fixture, definitions.Units, map.Map.StartPosition("blue"), new(24064, 18432), new(64000, 18432));
            var firstOrder = ai.Plan(world).First(c => c.Action == "rocketeer");
            NavigationChecks.Check(firstOrder.Entity == first, "The first patrol did not use the first producer.");
            world.Step([firstOrder]);
            var remaining = ai.Plan(world).Where(c => c.Action == "rocketeer").ToArray();
            NavigationChecks.Check(remaining is [{ Entity: var producer }] && producer == second, "One queued recruit covered both patrols.");
            world.Step(remaining);
            var moving = new HashSet<EntityId>();
            SkirmishAiScenario.Run(world, ai, 140, state =>
            {
                foreach (var command in state.AppliedCommands)
                {
                    NavigationChecks.Check(command.Kind != CommandKind.AttackMove && command.Action != "rocketeer", "Patrols attacked or ordered surplus units.");
                    if (command.Kind == CommandKind.MoveSelected && command.Recipients is { Count: 1 } patrol)
                        moving.Add(patrol.Ids[0]);
                }
            });
            NavigationChecks.Check(moving.Count == 2, "The trained unarmed units did not patrol independently.");
        });

        test("AI2 opens on both sides, skips an invalid candidate and captures income", () =>
        {
            for (uint player = 1; player <= 2; player++)
            {
                var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
                using var world = map.CreateWorld();
                int sorties = 0;
                var search = new InvalidFirstSearch();
                long? firstPower = null, firstBarracks = null;
                bool captured = false, movedInCombat = false;
                var ai = SkirmishAiScenario.Create(catalog, profile, map.Map, player, siteSearch: search);
                SkirmishAiScenario.Run(world, ai, 2500, state =>
                {
                    var own = Poses(state).Where(p => p.Presentation.Owner == player).ToArray();
                    if (own.Any(p => catalog.Units[p.UnitType].Id == "power-plant")) firstPower ??= state.Tick;
                    if (own.Any(p => catalog.Units[p.UnitType].Id == "barracks")) firstBarracks ??= state.Tick;
                    foreach (var value in state.Events)
                    {
                        if (value.Kind == WorldEventKind.OwnerChanged && value.Owner == player) captured = true;
                    }
                    foreach (var command in state.AppliedCommands)
                        if (command.Kind == CommandKind.AttackMove)
                        {
                            sorties++;
                            NavigationChecks.Check(command.Recipients is { Count: >= 3 }, "The sortie ignored the minimum group size.");
                        }
                    movedInCombat |= own.Any(p => p.Order.Kind == UnitActionKind.AttackMove && p.Current != p.Previous);
                });
                NavigationChecks.Check(firstPower.HasValue && firstBarracks > firstPower && search.OfferedInvalid,
                    $"Player {player} did not complete the opening after an invalid site candidate.");
                NavigationChecks.Check(captured && sorties > 0 && movedInCombat, $"Player {player} did not capture and march.");
            }
        });

        test("AI0 generates the same commands across workers and replays them without AI", () =>
        {
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var single = map.CreateWorld(1);
            using var parallel = map.CreateWorld(4);
            using var replay = map.CreateWorld(1);
            var first = SkirmishAiScenario.Create(catalog, profile, map.Map, 2);
            var second = SkirmishAiScenario.Create(catalog, profile, map.Map, 2);
            for (int tick = 1; tick <= SkirmishAiScenario.DefaultTicks; tick++)
            {
                var a = first.Plan(single);
                var b = second.Plan(parallel);
                NavigationChecks.Check(a.Length == b.Length && a.Zip(b).All(pair =>
                    (pair.First with { Recipients = null }) == (pair.Second with { Recipients = null }) &&
                    (pair.First.Recipients?.Ids.ToArray() ?? []).SequenceEqual(pair.Second.Recipients?.Ids.ToArray() ?? [])),
                    $"AI command journal diverged at tick {tick}.");
                single.Step(a);
                parallel.Step(b);
                replay.Step(single.AppliedCommands);
                NavigationChecks.Check(single.Events.SequenceEqual(parallel.Events) && single.Events.SequenceEqual(replay.Events),
                    $"AI events diverged at tick {tick}.");
                NavigationChecks.Check(single.Report() == parallel.Report() && single.Report() == replay.Report(), $"AI replay diverged at tick {tick}.");
            }
        });

        test("AI0 holds its base when armed enemies are nearby", () =>
        {
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var world = map.CreateWorld();
            int rifleman = catalog.Units.ToList().FindIndex(u => u.Id == "rifleman");
            world.AddInitialUnit(rifleman, new(33792, 17408), owner: 2);
            var ai = SkirmishAiScenario.Create(catalog, profile, map.Map, 1);
            bool holding = false;
            SkirmishAiScenario.Run(world, ai, 1000, state =>
            {
                foreach (var command in state.AppliedCommands)
                    NavigationChecks.Check(command.Kind != CommandKind.AttackMove, "Group attacked instead of defending the base.");
                holding |= Poses(state).Any(p => p.Presentation.Owner == 1 && p.Order.Kind == UnitActionKind.HoldPosition);
            });
            NavigationChecks.Check(holding, "Group did not hold the assembly point.");
        });

        test("AI2 reserves the unpaid blueprint before ordering a replacement truck", () =>
        {
            var start = Read(MapScenario.DefaultStartSet);
            var map = MapScenario.Load(catalog, path => path == MapScenario.DefaultStartSet
                ? start with { Text = string.Join('\n', start.Text.Replace("credits: 5000", "credits: 1000").Split('\n')
                    .Where(line => !line.Contains("type: supply-truck", StringComparison.Ordinal))) }
                : Read(path), MapScenario.DefaultMap);
            var frequent = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("MacroIntervalTicks: 25", "MacroIntervalTicks: 1") }, catalog, profile.Id);
            using var world = map.CreateWorld();
            var ai = SkirmishAiScenario.Create(catalog, frequent, map.Map, 2);
            world.Step(ai.Plan(world));
            var projects = new ConstructionProjectPose[world.ConstructionProjectCount];
            world.CopyConstructionProjects(projects);
            NavigationChecks.Check(projects.Single() is { Owner: 2, UnpaidCost: 500 }, "The blueprint did not expose its actual unpaid cost.");
            var players = new PlayerPose[world.PlayerCount];
            world.CopyPlayers(players);
            NavigationChecks.Check(players.Single(p => p.Id == 2).Credits == 1000 && !ai.Plan(world).Any(c => c.Action == "truck"),
                "The bot committed credits needed to start the unpaid building.");
        });

        test("AI2 reserves deferred queue balances and releases them after cancellation", () =>
        {
            const uint player = 2;
            foreach (var payment in new[] { PaymentPolicy.Progressive, PaymentPolicy.OnCompletion })
            {
                var buildings = Read("content/buildings.yaml");
                var edited = buildings.Text.Replace("Capacity: 20,\n          Payment: Upfront", $"Capacity: 20,\n          Payment: {payment}");
                var definitions = UnitCatalog.Load([Read("content/units.yaml"), buildings with { Text = edited }],
                    LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                int Type(string id) => definitions.Units.ToList().FindIndex(u => u.Id == id);
                int barracksType = Type("barracks"), powerType = Type("power-plant");
                NavigationChecks.Check(definitions.Units[barracksType].TaskQueues.Single().Payment == payment,
                    "Fixture did not load the intended payment policy.");
                var frequent = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("MacroIntervalTicks: 25", "MacroIntervalTicks: 1") }, definitions, profile.Id);
                int rifleCost = frequent.Sergeants.Single(r => r.Definition.Id == "rifle-section").Definition.Members.Single().Recipe.Cost;
                int truckCost = frequent.Supply!.Truck.Cost;
                var start = Read(MapScenario.DefaultStartSet);
                foreach (int surplus in new[] { -1, 0 })
                {
                    int initial = frequent.CreditReserve + rifleCost + truckCost + surplus;
                    var match = MapScenario.Load(definitions, path => path == MapScenario.DefaultStartSet
                        ? start with { Text = start.Text.Replace("credits: 5000", $"credits: {initial}") } : Read(path), MapScenario.DefaultMap);
                    using var world = match.CreateWorld();
                    world.AddInitialUnit(powerType, new(59904, 45056), player);
                    var barracks = world.AddInitialUnit(barracksType, new(59392, 37376), player);
                    var ai = SkirmishAiScenario.Create(definitions, frequent, match.Map, player);
                    ai.Plan(world);
                    world.Step([new(1, 0, 1, CommandKind.EnqueueTask, barracks, Action: "rifleman")]);
                    var tasks = new TaskPose[world.TaskCount];
                    var players = new PlayerPose[world.PlayerCount];
                    world.CopyTaskPoses(tasks);
                    world.CopyPlayers(players);
                    var task = tasks.Single();
                    long paid = initial - players.Single(p => p.Id == player).Credits;
                    NavigationChecks.Check(task.Payer == player && task.UnpaidCost == rifleCost - paid &&
                        (payment == PaymentPolicy.Progressive ? paid > 0 : paid == 0), $"{payment} published the wrong payment balance.");
                    var planned = ai.Plan(world);
                    NavigationChecks.Check(planned.Any(c => c.Action == "truck") == (surplus == 0),
                        $"{payment} did not respect the exact credit boundary.");
                    if (surplus == 0) continue;
                    world.Step([new(2, 0, 1, CommandKind.CancelTasks, barracks, Action: "rifleman", TaskId: task.Id)]);
                    NavigationChecks.Check(world.TaskCount == 0 && ai.Plan(world).Any(c => c.Action == "truck"),
                        $"{payment} kept reserving a cancelled task.");
                }
            }
        });

        test("AI2 builds a factory and deploys a separate group of progressively paid vehicles", () =>
        {
            var mechanized = SkirmishAiCatalog.Load(source, catalog, SkirmishAiScenario.MechanizedProfileId);
            int vehicle = mechanized.Sergeants.Single(r => r.Definition.Id == "apc-section").Definition.Members.Single().Recipe.ProductType;
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            for (uint player = 1; player <= 2; player++)
            {
                using var world = map.CreateWorld(player == 1 ? 1 : 4, restrictPlayerCommands: true);
                var ai = SkirmishAiScenario.Create(catalog, mechanized, map.Map, player);
                var produced = new HashSet<EntityId>();
                bool deployed = false;
                SkirmishAiScenario.Run(world, ai, 3200, state =>
                {
                    foreach (var e in state.Events)
                        if (e.Kind == WorldEventKind.Produced)
                        {
                            var unit = Poses(state).SingleOrDefault(p => p.Id == e.TargetEntity);
                            if (unit.UnitType == vehicle && unit.Presentation.Owner == player) produced.Add(unit.Id);
                        }
                    foreach (var command in state.AppliedCommands)
                        if (command.Kind == CommandKind.AttackMove && command.Recipients is { } recipients)
                            foreach (var id in recipients.Ids) deployed |= produced.Contains(id);
                });
                NavigationChecks.Check(produced.Count >= 2 && deployed, $"Player {player} did not produce and deploy two vehicles.");
            }
        });

        test("AI2 recovers its crew and freight, resumes the blueprint and upgrades a serviced mine", () =>
        {
            const uint player = 2;
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var world = map.CreateWorld(4, restrictPlayerCommands: true);
            var start = Poses(world).Where(p => p.Presentation.Owner == player).ToArray();
            var builder = start.Single(p => p.UnitType == profile.EngineerType).Id;
            var truck = start.Single(p => p.UnitType == profile.Supply!.Truck.ProductType).Id;
            var ai = SkirmishAiScenario.Create(catalog, profile, map.Map, player);
            world.Step(ai.Plan(world));
            var projects = new ConstructionProjectPose[world.ConstructionProjectCount];
            world.CopyConstructionProjects(projects);
            var site = projects.Single().Id;
            world.Step([new(2, 0, 1, CommandKind.Destroy, builder), new(2, 0, 2, CommandKind.Destroy, truck), .. ai.Plan(world)]);
            bool resumed = false, delivered = false, capturedOil = false;
            long previousCredits = 5000;
            var players = new PlayerPose[world.PlayerCount];
            SkirmishAiScenario.Run(world, ai, 3200, state =>
            {
                foreach (var command in state.AppliedCommands)
                    if (command.Kind == CommandKind.BuildSite && command.TargetEntity == site && command.Entity != builder) resumed = true;
                foreach (var e in state.Events)
                    if (e.Kind == WorldEventKind.OwnerChanged && e.Owner == player && catalog.Units[e.UnitType].Economy is { Income: > 0 }) capturedOil = true;
                state.CopyPlayers(players);
                long credits = players.Single(p => p.Id == player).Credits;
                delivered |= !capturedOil && credits > previousCredits;
                previousCredits = credits;
            });
            var own = Poses(world).Where(p => p.Presentation.Owner == player).ToArray();
            NavigationChecks.Check(resumed && own.Any(p => p.Id == site && !p.Construction.Active), "The replacement engineer did not resume the original blueprint.");
            NavigationChecks.Check(own.Count(p => p.UnitType == profile.Supply!.Truck.ProductType) == profile.Supply!.Target && delivered,
                "The bot did not replace the truck and earn freight credits.");
            NavigationChecks.Check(capturedOil && own.Any(p => catalog.Units[p.UnitType].Supply?.Role == SupplyRole.Mine && p.Upgrade.Completed is not null),
                "The engineer did not capture oil and finish a mine upgrade.");
        });

        test("AI1 session plans on each simulation tick even when publication drops frames", () =>
        {
            const int ticks = 350;
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            var ai = SkirmishAiScenario.Create(catalog, profile, map.Map, 2);
            var observations = new List<long>();
            using var session = new SimulationSession<SimulationWorld, WorldCommand, WorldEvent>(
                () => map.CreateWorld(4, restrictPlayerCommands: true), endTick: ticks, realtime: false,
                tryPublish: world => world.Tick is 0 or ticks,
                collectCommands: world => { observations.Add(world.Tick); return ai.Plan(world); });
            var report = session.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var log = session.GetCommandLog();
            NavigationChecks.Check(observations.SequenceEqual(Enumerable.Range(0, ticks).Select(i => (long)i)) &&
                log.Any(c => c.Kind == CommandKind.PlanConstruction), "The collector missed ticks without published frames.");
            using var replay = map.CreateWorld(1, restrictPlayerCommands: true);
            NavigationChecks.Check(DiagnosticScenario.Run(replay, ticks, log) == report, "Session journal did not replay in direct headless execution.");
        });

        test("AI1 checks ownership at application after a same-tick ownership change", () =>
        {
            var infantry = new UnitDefinition("infantry", 100, passenger: new("infantry", 1));
            var footprint = new FootprintDefinition(2, 2, [new(0, 0, new(false, false)), new(1, 0, new(false, false)),
                new(0, 1, new(false, false)), new(1, 1, new(false, false))]);
            var building = new UnitDefinition("house", 500, movable: false, footprint: footprint, transport: new(["infantry"], 1));
            using var world = new SimulationWorld([infantry, building], 16384, 16384, navigationGrid: new NavigationGrid(16, 16));
            world.RestrictCommandsToPlayers();
            var house = world.AddInitialUnit(1, new(5120, 5120));
            var occupant = world.AddInitialUnit(0, new(3584, 5120), 2);
            var enemy = world.AddInitialUnit(0, new(11000, 9000), 1);
            world.Step([new(1, 2, 1, CommandKind.Enter, occupant, TargetEntity: house)]);
            NavigationChecks.Check(Poses(world).Single(p => p.Id == house).Presentation.Owner == 2, "Test did not occupy the neutral building.");
            world.Step([
                new(2, 2, 1, CommandKind.Unload, house),
                new(2, 2, 2, CommandKind.Aim, house, new(8000, 8000)),
                new(2, 2, 3, CommandKind.MoveSelected, Position: new(10000, 6000), Recipients: new([occupant, enemy])),
                new(2, 2, 4, CommandKind.Spawn, Position: new(15000, 15000), Owner: 2)
            ]);
            var events = world.Events.ToArray();
            NavigationChecks.Check(events.Any(e => e.Kind == WorldEventKind.OwnerChanged && e.Entity == house && e.Owner == 0) &&
                events.Count(e => e.Kind == WorldEventKind.CommandDenied) == 3 && world.Count == 3 &&
                Poses(world).Single(p => p.Id == occupant).Order.Phase == OrderPhase.Idle,
                "A stale, mixed or privileged command passed player authority.");
        });
    }
    private sealed class InvalidFirstSearch : OpenRTS.AI.IBuildingSiteSearch
    {
        public bool OfferedInvalid { get; private set; }

        public IEnumerable<OpenRTS.AI.BuildingSite> Candidates(NavigationGrid grid, WorldPosition center, UnitDefinition building)
        {
            OfferedInvalid = true;
            yield return new(new(512, 512), 0);
            foreach (var site in new OpenRTS.AI.CompactBuildingSiteSearch().Candidates(grid, center, building)) yield return site;
        }
    }
}
