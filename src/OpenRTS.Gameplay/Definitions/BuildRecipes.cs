namespace OpenRTS.Core;

internal readonly record struct BuildRecipe(int UnitType, BuildDefinition Build);

// Queue production and on-site construction resolve the same product definitions.
internal sealed class BuildRecipes
{
    private readonly UnitDefinition[] _types;
    private readonly BuildRecipe[][] _recipes;

    internal BuildRecipes(UnitDefinition[] types)
    {
        _types = types;
        _recipes = new BuildRecipe[types.Length][];
        for (int type = 0; type < types.Length; type++)
        {
            var products = types[type].Products;
            var recipes = _recipes[type] = new BuildRecipe[products.Count];
            for (int product = 0; product < products.Count; product++)
            {
                var definition = products[product];
                int target = Array.FindIndex(types, unit => unit.Id == definition.Unit);
                if (target < 0 || types[target].Build is null)
                    throw new ArgumentException("Production requires a known buildable unit: " + definition.Unit);
                var build = definition.Override ?? types[target].Build!;
                if ((build.Kind == ProductionKind.PlaceBuilding) != (types[target].Footprint is not null) ||
                    definition.OnSite && (build.Kind != ProductionKind.PlaceBuilding || types[target].Destruction == DestructionPolicy.Invulnerable))
                    throw new ArgumentException("PlaceBuilding requires a footprint; Unit production cannot spawn footprints.");
                recipes[product] = new(target, build);
            }
        }
    }

    internal BuildRecipe this[int type, int product] => _recipes[type][product];

    internal int FindProduct(int type, string? id)
    {
        var products = _types[type].Products;
        for (int i = 0; i < products.Count; i++)
            if (products[i].Id == id) return i;
        return -1;
    }
}
