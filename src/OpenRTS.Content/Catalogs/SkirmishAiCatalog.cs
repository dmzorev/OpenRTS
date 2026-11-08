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

        if (data.Version != 1) throw Error("Version", "Expected version 1.");
        if (data.CreditReserve < 0 || data.MacroIntervalTicks is < 1 or > 10000 || data.TacticsIntervalTicks is < 1 or > 10000 ||
            data.BlockedGoalTicks <= Math.Max(data.MacroIntervalTicks, data.TacticsIntervalTicks) || data.BlockedGoalTicks > 1000000)
            throw Error("Timing", "Expected nonnegative reserve, positive intervals and a bounded timeout longer than either interval.");

        int engineer = Type(data.Capture.Unit, "Capture.Unit");
        if (!catalog.Units[engineer].Movable || !catalog.Units[engineer].Abilities.Any(a => a.Id == data.Capture.Action && a.Kind == AbilityKind.Capture))
            throw Error("Capture.Action", "A mobile unit with the named capture ability is required.");

        AiRecipe Recipe(string name, ProductionKind kind)
        {
            string field = "Recipes." + name;
            int product = Type(name, field);
            if (!data.Recipes.TryGetValue(name, out var recipe)) throw Error(field, "A recipe is required.");
            int producer = Type(recipe.Producer, field + ".Producer");
            var definition = catalog.Units[producer];
            var action = definition.Products.FirstOrDefault(a => a.Id == recipe.Action);
            if (action is null || action.Unit != name || action.OnSite)
                throw Error(field + ".Action", "Expected a queued recipe producing the declared unit.");
            int queue = definition.TaskQueues.ToList().FindIndex(q => q.Id == action.Queue);
            var build = action.Override ?? catalog.Units[product].Build;
            if (queue < 0 || definition.TaskQueues[queue].Payment != PaymentPolicy.Upfront ||
                build is null || build.Kind != kind || build.Ticks < 2)
                throw Error(field, "AI0 needs an Upfront recipe of the expected kind with a duration of at least two ticks.");
            return new(producer, product, queue, action.Id, build.Cost);
        }

        if (data.Buildings.Count is < 1 or > 16 || data.Group.Count is < 1 or > 16)
            throw Error("Buildings", "Declare 1..16 building types and 1..16 combat member types.");
        var buildings = data.Buildings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            if (p.Value is < 1 or > 16) throw Error("Buildings." + p.Key, "Expected a target in 1..16.");
            return new AiBuilding(Recipe(p.Key, ProductionKind.PlaceBuilding), p.Value);
        }).ToArray();
        var opening = data.Opening.Select(name => Type(name, "Opening")).ToArray();
        if (opening.Length != opening.Distinct().Count() || opening.Any(type => buildings.All(b => b.Recipe.ProductType != type)))
            throw Error("Opening", "Each opening step must name a distinct building from Buildings.");
        var group = data.Group.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            var recipe = Recipe(p.Key, ProductionKind.Unit);
            var unit = catalog.Units[recipe.ProductType];
            if (p.Value.Min < 0 || p.Value.Target < Math.Max(1, p.Value.Min) || p.Value.Target > 32 ||
                !unit.Movable || unit.Weapons.Count == 0 || recipe.ProductType == engineer)
                throw Error("Group." + p.Key, "Expected a mobile armed unit outside the capture role, with 0 <= Min <= Target <= 32.");
            return new AiMember(recipe, p.Value.Min, p.Value.Target);
        }).ToArray();
        if (group.All(g => g.Minimum == 0) || group.Sum(g => g.Target) > 32)
            throw Error("Group", "The group requires a positive minimum and a total target of at most 32.");
        foreach (string name in data.Recipes.Keys)
            if (!data.Buildings.ContainsKey(name) && !data.Group.ContainsKey(name))
                throw Error("Recipes." + name, "Unused recipe.");
        return new(id, opening, buildings, group, engineer, data.Capture.Action,
            data.CreditReserve, data.MacroIntervalTicks, data.TacticsIntervalTicks, data.BlockedGoalTicks);
    }

    private sealed class ProfileOptions
    {
        public int Version { get; set; }
        public string[] Opening { get; set; } = [];
        public Dictionary<string, int> Buildings { get; set; } = [];
        public Dictionary<string, MemberOptions> Group { get; set; } = [];
        public CaptureOptions Capture { get; set; } = new();
        public Dictionary<string, RecipeOptions> Recipes { get; set; } = [];
        public int CreditReserve { get; set; }
        public int MacroIntervalTicks { get; set; } = 25;
        public int TacticsIntervalTicks { get; set; } = 5;
        public int BlockedGoalTicks { get; set; } = 250;
    }

    private sealed class MemberOptions { public int Min { get; set; } public int Target { get; set; } }
    private sealed class CaptureOptions { public string Unit { get; set; } = ""; public string Action { get; set; } = ""; }
    private sealed class RecipeOptions { public string Producer { get; set; } = ""; public string Action { get; set; } = ""; }
}
