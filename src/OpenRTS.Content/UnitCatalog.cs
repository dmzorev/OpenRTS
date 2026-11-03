using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed record CatalogSummary(int DefinitionCount, string Fingerprint);

/// <summary>Loads and validates definitions once, before creating the gameplay world.</summary>
public sealed class UnitCatalog
{
    public ReadOnlyCollection<UnitDefinition> Units { get; }
    public CatalogSummary Summary { get; }

    private UnitCatalog(UnitDefinition[] units)
    {
        Units = Array.AsReadOnly(units);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1); // Canonical definition format version, independent of YAML formatting.
            writer.Write(units.Length);
            foreach (var unit in units)
            {
                writer.Write(unit.Id);
                writer.Write(unit.MaxHealth);
            }
        }
        Summary = new(units.Length, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    public static UnitCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>()
            .Register<HealthOptions>("Health", (id, _, options) => new UnitDefinition(id, options.HP), required: true);
        registry.ValidateTemplates(definitions);
        var units = definitions.Concrete.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => registry.Compile(pair.Key, pair.Value).Create(pair.Key).Single<UnitDefinition>()).ToArray();
        if (units.Length == 0)
            throw new ArgumentException("Каталог должен содержать хотя бы одно конкретное определение юнита.", nameof(sources));
        return new(units);
    }

    private sealed class HealthOptions
    {
        [Required, Range(1, int.MaxValue)] public int HP { get; set; }
    }
}
