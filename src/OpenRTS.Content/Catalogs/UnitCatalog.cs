using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using DeclarativeContent;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Content;

public sealed record UnitPresentationBinding(string UnitId, string? Model, string Name);

public sealed record CatalogSummary(int DefinitionCount, string Fingerprint);

/// <summary>Loads and validates definitions once, before creating the gameplay world.</summary>
public sealed class UnitCatalog
{
    public ReadOnlyCollection<UnitDefinition> Units { get; }
    public CatalogSummary Summary { get; }
    public ReadOnlyCollection<UnitPresentationBinding> Presentations { get; }
    public LocomotionCatalog? Locomotion { get; }

    private UnitCatalog(UnitDefinition[] units, UnitPresentationBinding[] presentations, LocomotionCatalog? locomotion)
    {
        Units = Array.AsReadOnly(units);
        Locomotion = locomotion;
        Presentations = Array.AsReadOnly(presentations);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(4); // Canonical definition format version, independent of YAML formatting.
            writer.Write(locomotion?.Terrains.Count ?? 0);
            if (locomotion is not null) foreach (var terrain in locomotion.Terrains) writer.Write(terrain);
            writer.Write(units.Length);
            foreach (var unit in units)
            {
                writer.Write(unit.Id);
                writer.Write(unit.MaxHealth);
                writer.Write(unit.Weapon is not null);
                if (unit.Weapon is { } weapon) { writer.Write(weapon.Range); writer.Write(weapon.AcquireRange); writer.Write(weapon.Damage); writer.Write(weapon.CooldownTicks); writer.Write(weapon.WindupTicks); }
                writer.Write(unit.Locomotor.Id); writer.Write(unit.Locomotor.Rules.Count);
                foreach (var rule in unit.Locomotor.Rules)
                { writer.Write(rule.Traverse); writer.Write(rule.CanStop); writer.Write(rule.Speed); writer.Write(rule.Cost); }
                writer.Write(unit.Animations.Count); foreach (var name in unit.Animations) writer.Write(name);
                writer.Write(unit.States.Count); foreach (var name in unit.States) writer.Write(name);
            }
        }
        Summary = new(units.Length, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    public static UnitCatalog Load(IEnumerable<ContentSource> sources, LocomotionCatalog? locomotion = null)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>()
            .Register<HealthOptions>("Health", (_, _, options) => options, required: true)
            .Register<MovementOptions>("Movement", (_, _, options) => options)
            .Register<WeaponOptions>("Weapon", (_, _, options) => options)
            .Register<PresentationOptions>("Presentation", (_, _, options) => options);
        registry.ValidateTemplates(definitions);
        var entries = definitions.Concrete.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var units = new UnitDefinition[entries.Length];
        var bindings = new UnitPresentationBinding[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, definition) = entries[i]; var traits = registry.Compile(id, definition).Create(id);
            var health = traits.Single<HealthOptions>(); var visual = traits.All<PresentationOptions>().SingleOrDefault();
            var movement = traits.All<MovementOptions>().SingleOrDefault(); Locomotor? profile = null;
            if (movement is not null && (locomotion is null || !locomotion.Profiles.TryGetValue(movement.Locomotor, out profile)))
                throw definition.Error(id + ".Movement.Locomotor", "Unknown locomotor: " + movement.Locomotor);
            var weapon = traits.All<WeaponOptions>().SingleOrDefault();
            try { units[i] = new(id, health.HP, visual?.Animations, visual?.States, profile,
                weapon is null ? null : new WeaponDefinition(weapon.Range, weapon.AcquireRange, weapon.Damage, weapon.CooldownTicks, weapon.WindupTicks)); }
            catch (ArgumentException error) { throw definition.Error(id + ".Presentation", error.Message); }
            bindings[i] = new(id, visual?.Model, visual?.Name ?? id);
        }
        if (units.Length == 0)
            throw new ArgumentException("Каталог должен содержать хотя бы одно конкретное определение юнита.", nameof(sources));
        return new(units, bindings, locomotion);
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
    private sealed class WeaponOptions
    {
        public int Range { get; set; } = 4096;
        public int AcquireRange { get; set; } = 8192;
        public int Damage { get; set; } = 10;
        public int CooldownTicks { get; set; } = 20;
        public int WindupTicks { get; set; } = 2;
    }
    private sealed class MovementOptions { [Required] public string Locomotor { get; set; } = ""; }
}
