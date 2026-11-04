using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed record UnitPresentationBinding(string UnitId, string? Model, string Name);

public sealed record CatalogSummary(int DefinitionCount, string Fingerprint);

/// <summary>Loads and validates definitions once, before creating the gameplay world.</summary>
public sealed class UnitCatalog
{
    public ReadOnlyCollection<UnitDefinition> Units { get; }
    public CatalogSummary Summary { get; }
    public ReadOnlyCollection<UnitPresentationBinding> Presentations { get; }

    private UnitCatalog(UnitDefinition[] units, UnitPresentationBinding[] presentations)
    {
        Units = Array.AsReadOnly(units);
        Presentations = Array.AsReadOnly(presentations);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(2); // Canonical definition format version, independent of YAML formatting.
            writer.Write(units.Length);
            foreach (var unit in units)
            {
                writer.Write(unit.Id);
                writer.Write(unit.MaxHealth);
                writer.Write(unit.Animations.Count); foreach (var name in unit.Animations) writer.Write(name);
                writer.Write(unit.States.Count); foreach (var name in unit.States) writer.Write(name);
            }
        }
        Summary = new(units.Length, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    public static UnitCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>()
            .Register<HealthOptions>("Health", (_, _, options) => options, required: true)
            .Register<PresentationOptions>("Presentation", (_, _, options) => options);
        registry.ValidateTemplates(definitions);
        var entries = definitions.Concrete.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var units = new UnitDefinition[entries.Length];
        var bindings = new UnitPresentationBinding[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, definition) = entries[i]; var traits = registry.Compile(id, definition).Create(id);
            var health = traits.Single<HealthOptions>(); var visual = traits.All<PresentationOptions>().SingleOrDefault();
            try { units[i] = new(id, health.HP, visual?.Animations, visual?.States); }
            catch (ArgumentException error) { throw definition.Error(id + ".Presentation", error.Message); }
            bindings[i] = new(id, visual?.Model, visual?.Name ?? id);
        }
        if (units.Length == 0)
            throw new ArgumentException("Каталог должен содержать хотя бы одно конкретное определение юнита.", nameof(sources));
        return new(units, bindings);
    }

    private sealed class PresentationOptions
    {
        [Required] public string Model { get; set; } = "";
        public string? Name { get; set; }
        public string[] Animations { get; set; } = ["idle", "move"];
        public string[] States { get; set; } = [];
    }

    private sealed class HealthOptions
    {
        [Required, Range(1, int.MaxValue)] public int HP { get; set; }
    }
}
