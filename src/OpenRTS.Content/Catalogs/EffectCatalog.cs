using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using DeclarativeContent;

namespace OpenRTS.Content;

public sealed record EffectStyle(double[] Color, double Size, double[] ImpactColor, double ImpactSize, int DurationTicks, double BeamWidth);

/// <summary>Adapter-only presentation keyed by semantic weapon ID; excluded from gameplay hashes.</summary>
public sealed class EffectCatalog
{
    public ReadOnlyDictionary<string, EffectStyle> Weapons { get; }
    private EffectCatalog(Dictionary<string, EffectStyle> weapons) => Weapons = new(weapons);
    public static EffectCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>().Register<Options>("Effect", (_, _, o) => o, required: true);
        registry.ValidateTemplates(definitions);
        var result = new Dictionary<string, EffectStyle>(StringComparer.Ordinal);
        foreach (var (id, node) in definitions.Concrete)
        {
            var o = registry.Compile(id, node).Create(id).Single<Options>();
            if (o.Color.Length != 3 || o.ImpactColor.Length != 3 || o.Color.Concat(o.ImpactColor).Any(v => !double.IsFinite(v) || v < 0 || v > 1)) throw node.Error(id + ".Effect", "Colors require three finite values in 0..1.");
            result.Add(id, new(o.Color, o.Size, o.ImpactColor, o.ImpactSize, o.DurationTicks, o.BeamWidth));
        }
        return new(result);
    }
    private sealed class Options
    {
        public double[] Color { get; set; } = [1, 0.8, 0.2];
        public double[] ImpactColor { get; set; } = [1, 0.4, 0.1];
        [Range(0.01, 4)] public double Size { get; set; } = 0.15;
        [Range(0.01, 16)] public double ImpactSize { get; set; } = 0.7;
        [Range(1, 1000)] public int DurationTicks { get; set; } = 6;
        [Range(0.01, 1)] public double BeamWidth { get; set; } = 0.04;
    }
}
