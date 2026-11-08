using DeclarativeContent;
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

        test("AI0 rejects invalid recipes and accepts a changed group size", () =>
        {
            NavigationChecks.Reject<ContentException>(() => SkirmishAiCatalog.Load(
                source with { Text = source.Text.Replace("Action: barracks", "Action: factory") }, catalog, profile.Id));
            NavigationChecks.Reject<ContentException>(() => SkirmishAiCatalog.Load(
                source with { Text = source.Text.Replace("Target: 5", "Target: 2") }, catalog, profile.Id));
            var smaller = SkirmishAiCatalog.Load(source with { Text = source.Text.Replace("Target: 5", "Target: 3") }, catalog, profile.Id);
            NavigationChecks.Check(smaller.Group.Single().Target == 3, "Group target did not come from YAML.");
        });

        test("AI0 opens on both sides, retries a rejected placement and captures income", () =>
        {
            for (uint player = 1; player <= 2; player++)
            {
                var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
                using var world = map.CreateWorld();
                int invalidPlacements = 0, sorties = 0;
                long? firstPower = null, firstBarracks = null;
                bool captured = false, movedInCombat = false;
                var ai = SkirmishAiScenario.Create(catalog, profile, player, blockFirstSite: true);
                SkirmishAiScenario.Run(world, ai, SkirmishAiScenario.DefaultTicks, state =>
                {
                    var own = Poses(state).Where(p => p.Presentation.Owner == player).ToArray();
                    if (own.Any(p => catalog.Units[p.UnitType].Id == "power-plant")) firstPower ??= state.Tick;
                    if (own.Any(p => catalog.Units[p.UnitType].Id == "barracks")) firstBarracks ??= state.Tick;
                    foreach (var value in state.Events)
                    {
                        if (value.Kind == WorldEventKind.OwnerChanged && value.Owner == player) captured = true;
                        if (value.Kind == WorldEventKind.ActionUnavailable && state.AppliedCommands.Length > 0 &&
                            state.AppliedCommands[0].Kind == CommandKind.PlaceProduced) invalidPlacements++;
                    }
                    foreach (var command in state.AppliedCommands)
                        if (command.Kind == CommandKind.AttackMove)
                        {
                            sorties++;
                            NavigationChecks.Check(command.Recipients is { Count: >= 3 }, "The sortie ignored the minimum group size.");
                        }
                    movedInCombat |= own.Any(p => p.Order.Kind == UnitActionKind.AttackMove && p.Current != p.Previous);
                });
                NavigationChecks.Check(firstPower.HasValue && firstBarracks > firstPower && invalidPlacements > 0,
                    $"Player {player} did not complete the opening after a rejected placement.");
                NavigationChecks.Check(captured && sorties > 0 && movedInCombat, $"Player {player} did not capture and march.");
            }
        });

        test("AI0 generates the same commands across workers and replays them without AI", () =>
        {
            var map = MapScenario.Load(catalog, Read, MapScenario.DefaultMap);
            using var single = map.CreateWorld(1);
            using var parallel = map.CreateWorld(4);
            using var replay = map.CreateWorld(1);
            var first = SkirmishAiScenario.Create(catalog, profile, 2);
            var second = SkirmishAiScenario.Create(catalog, profile, 2);
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
            world.AddInitialUnit(rifleman, new(37888, 11264), owner: 2);
            var ai = SkirmishAiScenario.Create(catalog, profile, 1);
            bool holding = false;
            SkirmishAiScenario.Run(world, ai, 1000, state =>
            {
                foreach (var command in state.AppliedCommands)
                    NavigationChecks.Check(command.Kind != CommandKind.AttackMove, "Group attacked instead of defending the base.");
                holding |= Poses(state).Any(p => p.Presentation.Owner == 1 && p.Order.Kind == UnitActionKind.HoldPosition);
            });
            NavigationChecks.Check(holding, "Group did not hold the assembly point.");
        });
    }
}
