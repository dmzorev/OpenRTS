using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using DeclarativeContent;

namespace OpenRTS.Content;

public enum ModelFacing { Body, Weapon }
public sealed record ModelPart(string Id, int Parent, string Resource, ModelFacing Facing,
    double Width, double Height, double PivotX, double PivotY, double OffsetX, double OffsetY, int Layer, string? Mount = null);
public sealed record ModelAnimation(string Sequence, ReadOnlyCollection<string> Parts, double Bob, double Recoil, double Flash);
public sealed record ModelState(double Red, double Green, double Blue, double Scale, double Opacity);
public sealed record ModelDefinition(string Id, ReadOnlyCollection<ModelPart> Parts,
    ReadOnlyDictionary<string, ModelAnimation> Animations, ReadOnlyDictionary<string, ModelState> States);

/// <summary>Adapter declarations, loaded separately from UnitCatalog; never used by World.Step.</summary>
public sealed class ModelCatalog
{
    public ReadOnlyDictionary<string, ModelDefinition> Models { get; }
    private ModelCatalog(Dictionary<string, ModelDefinition> models) => Models = new(models);
    public static ModelCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>().Register<ModelOptions>("Model", (_, _, options) => options, required: true);
        registry.ValidateTemplates(definitions);
        var models = new Dictionary<string, ModelDefinition>(StringComparer.Ordinal);
        foreach (var (id, node) in definitions.Concrete.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var options = registry.Compile(id, node).Create(id).Single<ModelOptions>();
            if (options.Kind != "Sprite") throw node.Error(id + ".Model.Kind", "This adapter currently supports Sprite models.");
            if (options.Parts.Count is < 1 or > 8) throw node.Error(id + ".Model.Parts", "A model requires 1..8 parts.");
            var ordered = new List<ModelPart>(); var pending = options.Parts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
            while (pending.Count > 0)
            {
                bool progress = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    var (name, part) = pending[i];
                    if (string.IsNullOrWhiteSpace(name) || !part.Resource.StartsWith("res://", StringComparison.Ordinal))
                        throw node.Error(id + ".Model.Parts." + name, "Part ID and res:// resource are required.");
                    if (part.Parent is not null && !options.Parts.ContainsKey(part.Parent)) throw node.Error(id + ".Model.Parts." + name, "Unknown parent: " + part.Parent);
                    int parent = part.Parent is null ? -1 : ordered.FindIndex(p => p.Id == part.Parent);
                    if (part.Parent is not null && parent < 0) continue;
                    ValidateVector(part.Size, 0.01, 32, "Size"); ValidateVector(part.Pivot, 0, 1, "Pivot"); ValidateVector(part.Offset, -32, 32, "Offset");
                    ordered.Add(new(name, parent, part.Resource, part.Facing, part.Size[0], part.Size[1], part.Pivot[0], part.Pivot[1], part.Offset[0], part.Offset[1], part.Layer, part.Mount));
                    pending.RemoveAt(i--); progress = true;
                }
                if (!progress) throw node.Error(id + ".Model.Parts", "Part parent cycle.");
            }
            var animations = new Dictionary<string, ModelAnimation>(StringComparer.Ordinal);
            foreach (var (name, animation) in options.Animations)
            {
                if (string.IsNullOrWhiteSpace(name) || animation.Parts.Length == 0 || animation.Parts.Distinct().Count() != animation.Parts.Length ||
                    animation.Parts.Any(p => !options.Parts.ContainsKey(p))) throw node.Error(id + ".Model.Animations." + name, "Animation references missing or duplicate parts.");
                animations.Add(name, new(animation.Sequence, Array.AsReadOnly(animation.Parts), animation.Bob, animation.Recoil, animation.Flash));
            }
            var states = new Dictionary<string, ModelState>(StringComparer.Ordinal);
            foreach (var (name, state) in options.States)
            {
                if (string.IsNullOrWhiteSpace(name) || state.Tint.Length != 3 || state.Tint.Any(v => v < 0 || v > 2))
                    throw node.Error(id + ".Model.States." + name, "State tint requires three values in 0..2.");
                states.Add(name, new(state.Tint[0], state.Tint[1], state.Tint[2], state.Scale, state.Opacity));
            }
            models.Add(id, new(id, Array.AsReadOnly(ordered.ToArray()), new(animations), new(states)));
            void ValidateVector(double[] value, double min, double max, string name)
            {
                if (value.Length != 2 || value.Any(v => v < min || v > max)) throw node.Error(id + ".Model.Parts." + name, $"Requires two values in {min}..{max}.");
            }
        }
        return new(models);
    }

    public void ValidateUnits(UnitCatalog units)
    {
        for (int i = 0; i < units.Units.Count; i++)
        {
            var binding = units.Presentations[i]; if (binding.Model is null) continue;
            if (!Models.TryGetValue(binding.Model, out var model)) throw new ArgumentException($"Unit {binding.UnitId}: unknown model {binding.Model}.");
            foreach (var part in model.Parts)
                if (part.Mount is not null && !units.Units[i].Weapons.Any(m => m.Id == part.Mount)) throw new ArgumentException($"Unit {binding.UnitId}: unknown mount {part.Mount}.");
            foreach (string animation in units.Units[i].Animations)
                if (!model.Animations.ContainsKey(animation)) throw new ArgumentException($"Unit {binding.UnitId}: model lacks animation {animation}.");
            foreach (string state in units.Units[i].States)
                if (!model.States.ContainsKey(state)) throw new ArgumentException($"Unit {binding.UnitId}: model lacks state {state}.");
        }
    }

    private sealed class ModelOptions
    {
        public string Kind { get; set; } = "Sprite";
        public Dictionary<string, PartOptions> Parts { get; set; } = new();
        public Dictionary<string, AnimationOptions> Animations { get; set; } = new();
        public Dictionary<string, StateOptions> States { get; set; } = new();
    }
    private sealed class PartOptions
    {
        [Required] public string Resource { get; set; } = "";
        public string? Parent { get; set; }
        public string? Mount { get; set; }
        public ModelFacing Facing { get; set; }
        public double[] Size { get; set; } = [1, 1];
        public double[] Pivot { get; set; } = [0.5, 0.5];
        public double[] Offset { get; set; } = [0, 0];
        [Range(0, 16)] public int Layer { get; set; }
    }
    private sealed class AnimationOptions
    {
        [Required] public string Sequence { get; set; } = "";
        public string[] Parts { get; set; } = [];
        [Range(0.0, 1.0)] public double Bob { get; set; }
        [Range(0.0, 1.0)] public double Recoil { get; set; }
        [Range(0.0, 1.0)] public double Flash { get; set; }
    }
    private sealed class StateOptions
    {
        public double[] Tint { get; set; } = [1, 1, 1];
        [Range(0.1, 2.0)] public double Scale { get; set; } = 1;
        [Range(0.0, 1.0)] public double Opacity { get; set; } = 1;
    }
}
