using DeclarativeContent;
using OpenRTS.AI;
using OpenRTS.Core;

namespace OpenRTS.Content;

public static class SkirmishAiCatalog
{
    public static SkirmishAiProfile Load(ContentSource source, UnitCatalog catalog, string id)
    {
        var definitions = DefinitionSet.Load([source]);
        if (!definitions.All.TryGetValue(id, out var node) || id.StartsWith('^'))
            throw new ArgumentException($"AI profile '{id}' is missing from {source.Name}.");
        var data = Options.Bind<ProfileOptions>(node, id);
        var types = catalog.Units.Select((type, index) => (type.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
        ContentException Error(string field, string message) => node.Error(id + "." + field, message);
        int Type(string name, string field) => types.TryGetValue(name, out int index) ? index : throw Error(field, $"Unknown unit type '{name}'.");

        if (data.Version is not (1 or 2)) throw Error("Version", "Expected version 1 or 2.");
        if (data.Version == 1 && data.Sergeants.Count != 0 || data.Version == 2 && data.Group.Count != 0)
            throw Error("Version", "Use Group in version 1 or Sergeants in version 2.");
        if (data.CreditReserve < 0 || data.MacroIntervalTicks is < 1 or > 10000 || data.TacticsIntervalTicks is < 1 or > 10000 ||
            data.BlockedGoalTicks <= Math.Max(data.MacroIntervalTicks, data.TacticsIntervalTicks) || data.BlockedGoalTicks > 1000000)
            throw Error("Timing", "Expected nonnegative reserve, positive intervals and a bounded timeout longer than either interval.");

        int engineer = Type(data.Capture.Unit, "Capture.Unit");
        if (!catalog.Units[engineer].Movable || !catalog.Units[engineer].Abilities.Any(a => a.Id == data.Capture.Action && a.Kind == AbilityKind.Capture))
            throw Error("Capture.Action", "A mobile unit with the named capture ability is required.");

        AiRecipe ProfileRecipe(string name, ProductionKind kind)
        {
            string field = "Recipes." + name;
            if (!data.Recipes.TryGetValue(name, out var recipe)) throw Error(field, "A recipe is required.");
            return Recipe(name, kind, field, recipe);
        }

        AiRecipe Recipe(string name, ProductionKind kind, string field, RecipeOptions recipe)
        {
            int product = Type(name, field);
            int producer = Type(recipe.Producer, field + ".Producer");
            var definition = catalog.Units[producer];
            var action = definition.Products.FirstOrDefault(a => a.Id == recipe.Action);
            if (action is null || action.Unit != name)
                throw Error(field + ".Action", "Expected a recipe producing the declared unit.");
            int queue = definition.TaskQueues.ToList().FindIndex(q => q.Id == action.Queue);
            var build = action.Override ?? catalog.Units[product].Build;
            if (build is null || build.Kind != kind || build.Ticks < 2 ||
                (action.OnSite ? kind != ProductionKind.PlaceBuilding : queue < 0))
                throw Error(field, "Expected on-site construction or a queue of the specified kind, with at least two ticks of work.");
            return new(producer, product, queue, action.Id, build.Cost, action.OnSite);
        }

        if (data.Buildings.Count is < 1 or > 16) throw Error("Buildings", "Declare 1..16 building types.");
        foreach (string name in data.BuildingPriorities.Keys)
            if (!data.Buildings.ContainsKey(name)) throw Error("BuildingPriorities." + name, "Priority requires a building target.");
        var buildings = data.Buildings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            if (p.Value is < 1 or > 16) throw Error("Buildings." + p.Key, "Expected a target in 1..16.");
            return new AiBuilding(ProfileRecipe(p.Key, ProductionKind.PlaceBuilding), p.Value, data.BuildingPriorities.GetValueOrDefault(p.Key));
        }).ToArray();
        var opening = data.Opening.Select(name => Type(name, "Opening")).ToArray();
        if (opening.Length != opening.Distinct().Count() || opening.Any(type => buildings.All(b => b.Recipe.ProductType != type)))
            throw Error("Opening", "Each opening step must name a distinct building from Buildings.");
        AiSergeant CompileSergeant(string name, string field, SergeantStrategy strategy, SergeantStrategy? underThreat,
            Dictionary<string, MemberOptions> roster, Func<string, MemberOptions, AiRecipe> compile)
        {
            if (roster.Count is < 1 or > 16) throw Error(field, "Declare 1..16 member types.");
            var members = roster.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            {
                var recipe = compile(p.Key, p.Value);
                var unit = catalog.Units[recipe.ProductType];
                if (p.Value.Min < 0 || p.Value.Target < Math.Max(1, p.Value.Min) || p.Value.Target > 32 ||
                    !unit.Movable || recipe.ProductType == engineer || p.Key == data.Supply?.Truck ||
                    buildings.Any(b => b.Recipe.OnSite && b.Recipe.ProducerType == recipe.ProductType))
                    throw Error(field + "." + p.Key,
                        "Expected a mobile unit outside the profile's capture, supply and construction roles with 0 <= Min <= Target <= 32.");
                return new AiMember(recipe, p.Value.Min, p.Value.Target);
            }).ToArray();
            if (members.All(m => m.Minimum == 0) || members.Sum(m => m.Target) > 32)
                throw Error(field, "The group requires a positive minimum and a total target of at most 32.");
            if ((strategy != SergeantStrategy.Patrol || underThreat is SergeantStrategy.Assault or SergeantStrategy.Guard) &&
                !members.Any(m => m.Minimum > 0 && catalog.Units[m.Recipe.ProductType].Weapons.Count > 0))
                throw Error(field, "Assault and Guard require an armed member with a positive minimum.");
            return new(name, strategy, members);
        }

        AiSergeantRule[] sergeants;
        if (data.Version == 1)
        {
            var legacy = CompileSergeant("combat", "Group", SergeantStrategy.Assault, null, data.Group,
                (name, member) => member.Recipe is null ? ProfileRecipe(name, ProductionKind.Unit) :
                    throw Error("Group." + name, "Version 1 defines production in Recipes."));
            sergeants = [new(legacy, 1, 1, 0, 0, 0, 0, null)];
        }
        else
        {
            if (data.Sergeants.Count is < 1 or > 16) throw Error("Sergeants", "Reference 1..16 sergeant definitions.");
            sergeants = data.Sergeants.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            {
                string field = "Sergeants." + p.Key;
                if (p.Key.StartsWith('^') || !definitions.All.TryGetValue(p.Key, out var definitionNode))
                    throw Error(field, "Unknown concrete sergeant definition.");
                var definition = Options.Bind<SergeantOptions>(definitionNode, p.Key);
                var rule = p.Value;
                if (rule.Min < 0 || rule.Max < Math.Max(1, rule.Min) || rule.Max > 32 || rule.EarliestTick < 0 ||
                    rule.StartCredits < 0 || rule.RetainCredits < 0 || rule.RetainCredits > rule.StartCredits)
                    throw Error(field, "Expected 0 <= Min <= Max <= 32 (Max positive), nonnegative delay and 0 <= RetainCredits <= StartCredits.");
                var strategy = definition.Strategy ?? throw definitionNode.Error(p.Key + ".Strategy", "A named strategy is required.");
                var sergeant = CompileSergeant(p.Key, field + ".Members", strategy, rule.UnderThreatStrategy, definition.Members,
                    (name, member) => Recipe(name, ProductionKind.Unit, field + ".Members." + name,
                        member.Recipe ?? throw Error(field + ".Members." + name, "A recipe is required.")));
                return new AiSergeantRule(sergeant, rule.Min, rule.Max, rule.Priority, rule.EarliestTick,
                    rule.StartCredits, rule.RetainCredits, rule.UnderThreatStrategy);
            }).ToArray();
            if (sergeants.Sum(rule => rule.Max) > 32) throw Error("Sergeants", "The profile supports at most 32 slots.");
        }
        AiSupply? supply = null;
        if (data.Supply is { } logistics)
        {
            var truck = ProfileRecipe(logistics.Truck, ProductionKind.Unit);
            var definition = catalog.Units[truck.ProductType];
            if (logistics.Target is < 1 or > 8 ||
                definition.Supply?.Role != SupplyRole.Truck || !definition.Abilities.Any(a => a.Kind == AbilityKind.Harvest && a.AutoCast) ||
                catalog.Units[truck.ProducerType].Supply?.Role != SupplyRole.Depot)
                throw Error("Supply", "Expected 1..8 automatic supply trucks from a depot, outside the sergeant rosters.");
            if (logistics.ImproveMines && catalog.Units[engineer].ConstructionRange == 0)
                throw Error("Supply.ImproveMines", "Mine upgrades require an engineer with construction range.");
            supply = new(truck, logistics.Target, logistics.ImproveMines);
        }
        AiRecipe? replacement = data.Recipes.ContainsKey(data.Capture.Unit) ? ProfileRecipe(data.Capture.Unit, ProductionKind.Unit) : null;
        foreach (string name in data.Recipes.Keys)
            if (!data.Buildings.ContainsKey(name) && !data.Group.ContainsKey(name) && name != data.Capture.Unit && name != data.Supply?.Truck)
                throw Error("Recipes." + name, "Unused recipe.");
        return new(id, opening, buildings, sergeants, engineer, data.Capture.Action,
            data.CreditReserve, data.MacroIntervalTicks, data.TacticsIntervalTicks, data.BlockedGoalTicks, supply, replacement);
    }

    private sealed class ProfileOptions
    {
        public int Version { get; set; }
        public string[] Opening { get; set; } = [];
        public Dictionary<string, int> Buildings { get; set; } = [];
        public Dictionary<string, int> BuildingPriorities { get; set; } = [];
        public Dictionary<string, MemberOptions> Group { get; set; } = [];
        public Dictionary<string, SergeantRuleOptions> Sergeants { get; set; } = [];
        public CaptureOptions Capture { get; set; } = new();
        public SupplyOptions? Supply { get; set; }
        public Dictionary<string, RecipeOptions> Recipes { get; set; } = [];
        public int CreditReserve { get; set; }
        public int MacroIntervalTicks { get; set; } = 25;
        public int TacticsIntervalTicks { get; set; } = 5;
        public int BlockedGoalTicks { get; set; } = 250;
    }

    private sealed class SergeantOptions
    {
        public SergeantStrategy? Strategy { get; set; }
        public Dictionary<string, MemberOptions> Members { get; set; } = [];
    }

    private sealed class SergeantRuleOptions
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public int Priority { get; set; }
        public int EarliestTick { get; set; }
        public int StartCredits { get; set; }
        public int RetainCredits { get; set; }
        public SergeantStrategy? UnderThreatStrategy { get; set; }
    }

    private sealed class MemberOptions
    {
        public int Min { get; set; }
        public int Target { get; set; }
        public RecipeOptions? Recipe { get; set; }
    }
    private sealed class CaptureOptions { public string Unit { get; set; } = ""; public string Action { get; set; } = ""; }
    private sealed class SupplyOptions { public string Truck { get; set; } = ""; public int Target { get; set; } public bool ImproveMines { get; set; } }
    private sealed class RecipeOptions { public string Producer { get; set; } = ""; public string Action { get; set; } = ""; }
}
