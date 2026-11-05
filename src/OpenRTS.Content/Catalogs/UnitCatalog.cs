using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using DeclarativeContent;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Content;

public sealed record ActionButtonBinding(string Id, string Label, string? Icon, string Hotkey, string Cursor);
public sealed record UnitPresentationBinding(string UnitId, string? Model, string Name, ReadOnlyCollection<ActionButtonBinding>? Actions = null);

public sealed record CatalogSummary(int DefinitionCount, string Fingerprint);

/// <summary>Loads and validates definitions once, before creating the gameplay world.</summary>
public sealed partial class UnitCatalog
{
    public ReadOnlyCollection<UnitDefinition> Units { get; }
    public ReadOnlyCollection<EntityDefinition> Definitions { get; }
    public CatalogSummary Summary { get; }
    public ReadOnlyCollection<UnitPresentationBinding> Presentations { get; }
    public LocomotionCatalog? Locomotion { get; }

    private UnitCatalog(UnitDefinition[] units, UnitPresentationBinding[] presentations, LocomotionCatalog? locomotion)
    {
        Units = Array.AsReadOnly(units);
        Definitions = Array.AsReadOnly(units.Select(u => u.Definition).ToArray());
        Locomotion = locomotion;
        Presentations = Array.AsReadOnly(presentations);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(7); // Canonical definition format version, independent of YAML formatting.
            writer.Write(locomotion?.Terrains.Count ?? 0);
            if (locomotion is not null) foreach (var terrain in locomotion.Terrains) writer.Write(terrain);
            writer.Write(units.Length);
            foreach (var unit in units)
            {
                unit.WriteState(writer);
            }
        }
        Summary = new(units.Length, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    public static UnitCatalog Load(IEnumerable<ContentSource> sources, LocomotionCatalog? locomotion = null, WeaponCatalog? weapons = null)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>()
            .Register<HealthOptions>("Health", (_, _, options) => options, required: true)
            .Register<MovementOptions>("Movement", (_, _, options) => options)
            .Register<BodyOptions>("Body", (_, _, options) => options)
            .Register<FootprintOptions>("Footprint", (_, _, options) => options)
            .Register<ArmamentOptions>("Armaments", (_, _, options) => options)
            .Register<BuildOptions>("Buildable", (_, _, options) => options)
            .Register<QueueOptions>("TaskQueues", (_, _, options) => options)
            .Register<ActionsOptions>("Actions", (_, _, options) => options)
            .Register<ActionPanelOptions>("ActionPanel", (_, _, options) => options)
            .Register<PresentationOptions>("Presentation", (_, _, options) => options);
        registry.ValidateTemplates(definitions);
        var entries = definitions.Concrete.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var compiled = entries.Select(entry =>
        {
            var traits = registry.Compile(entry.Key, entry.Value).Create(entry.Key);
            try { return (Id: entry.Key, Node: entry.Value, Traits: traits, Build: traits.All<BuildOptions>().SingleOrDefault()?.Compile()); }
            catch (ArgumentException error) { throw entry.Value.Error(entry.Key + ".Buildable", error.Message); }
        }).ToArray();
        var buildDefaults = compiled.ToDictionary(e => e.Id, e => e.Build, StringComparer.Ordinal);
        var units = new UnitDefinition[entries.Length];
        var bindings = new UnitPresentationBinding[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, definition, traits, build) = compiled[i];
            var health = traits.Single<HealthOptions>(); var visual = traits.All<PresentationOptions>().SingleOrDefault();
            var movement = traits.All<MovementOptions>().SingleOrDefault(); Locomotor? profile = null;
            if (movement is not null && (movement.Enabled || movement.Locomotor.Length > 0) && (locomotion is null || !locomotion.Profiles.TryGetValue(movement.Locomotor, out profile)))
                throw definition.Error(id + ".Movement.Locomotor", "Unknown locomotor: " + movement.Locomotor);
            var armaments = traits.All<ArmamentOptions>().SingleOrDefault();
            var mounts = new List<WeaponMountDefinition>();
            if (armaments is not null) foreach (var (name, mount) in armaments.Mounts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (weapons is null || !weapons.Weapons.TryGetValue(mount.Weapon, out var weapon)) throw definition.Error(id + ".Armaments." + name, "Unknown weapon: " + mount.Weapon);
                mounts.Add(new(name, weapon, mount.Animation, mount.FireWhileMoving));
            }
            var body = traits.All<BodyOptions>().SingleOrDefault(); var footprint = traits.All<FootprintOptions>().SingleOrDefault();
            try
            {
                BodyShape? shape = body is null ? null : body.Kind switch { BodyKind.Circle => BodyShape.Circle(body.Radius), BodyKind.Box when body.HalfSize.Length == 2 => BodyShape.Box(body.HalfSize[0], body.HalfSize[1]), _ => throw new ArgumentException("Body requires Circle/Box and valid dimensions.") };
                FootprintDefinition? stamp = null;
                if (footprint is not null)
                {
                    if (footprint.Rows.Length == 0 || footprint.Rows[0].Length == 0 || footprint.Rows.Any(r => r.Length != footprint.Rows[0].Length) || footprint.Types.Keys.Any(k => k.Length != 1 || k == ".")) throw new ArgumentException("Footprint requires equal-width rows, single-character types and '.' for absent cells.");
                    var cells = new List<FootprintCell>();
                    for (int y = 0; y < footprint.Rows.Length; y++) for (int x = 0; x < footprint.Rows[y].Length; x++)
                    {
                        string key = footprint.Rows[y][x].ToString(); if (key == ".") continue;
                        if (!footprint.Types.TryGetValue(key, out var cell)) throw new ArgumentException("Undefined footprint symbol: " + key);
                        byte? terrain = cell.Terrain is null ? null : locomotion?.TerrainIndex(cell.Terrain) ?? throw new ArgumentException("Footprint terrain needs a locomotion catalog.");
                        cells.Add(new(x, y, new(cell.Traverse, cell.CanStop, terrain), cell.Targetable));
                    }
                    stamp = new(footprint.Rows[0].Length, footprint.Rows.Length, cells, footprint.CellSize);
                }
                var queues = (traits.All<QueueOptions>().SingleOrDefault()?.Queues ?? new()).OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => q.Value.Compile(q.Key)).ToArray();
                var (abilities, products, buttons) = CompileActions(traits.All<ActionsOptions>().SingleOrDefault(), traits.All<ActionPanelOptions>().SingleOrDefault(), queues, buildDefaults);
                units[i] = new(id, health.HP, visual?.Animations, visual?.States, profile, weapons: mounts, armor: health.Armor, movable: movement?.Enabled ?? stamp is null, body: shape, footprint: stamp, abilities: abilities, taskQueues: queues, products: products, build: build);
                bindings[i] = new(id, visual?.Model, visual?.Name ?? id, Array.AsReadOnly(buttons));
            }
            catch (ArgumentException error) { throw definition.Error(id, error.Message); }

        }
        if (units.Length == 0)
            throw new ArgumentException("Каталог должен содержать хотя бы одно конкретное определение юнита.", nameof(sources));
        var targets = units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        for (int i = 0; i < units.Length; i++) foreach (var product in units[i].Products)
        {
            var target = targets[product.Unit];
            if (((product.Override ?? target.Build!).Kind == ProductionKind.PlaceBuilding) != (target.Footprint is not null))
                throw compiled[i].Node.Error(units[i].Id + ".Actions." + product.Id, "PlaceBuilding requires a footprint; Unit production cannot spawn footprints.");
        }
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
        public ArmorKind Armor { get; set; }
    }
    private sealed class BodyOptions { public BodyKind Kind { get; set; } = BodyKind.Circle; public int Radius { get; set; } = 256; public int[] HalfSize { get; set; } = [256, 256]; }
    private sealed class FootprintOptions
    {
        public int CellSize { get; set; } = 1024;
        public string[] Rows { get; set; } = [];
        public Dictionary<string, CellOptions> Types { get; set; } = new();
    }
    private sealed class CellOptions
    {
        public bool? Traverse { get; set; }
        public bool? CanStop { get; set; }
        public string? Terrain { get; set; }
        public bool Targetable { get; set; } = true;
    }
    private sealed class ArmamentOptions { public Dictionary<string, MountOptions> Mounts { get; set; } = new(); }
    private sealed class MountOptions
    {
        [Required] public string Weapon { get; set; } = "";
        public string Animation { get; set; } = "fire";
        public bool FireWhileMoving { get; set; }
    }
    private sealed class MovementOptions { public string Locomotor { get; set; } = ""; public bool Enabled { get; set; } = true; }
}
