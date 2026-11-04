using System.Collections.ObjectModel;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed class LocomotionCatalog
{
    public ReadOnlyCollection<string> Terrains { get; }
    public ReadOnlyDictionary<string, Locomotor> Profiles { get; }
    private LocomotionCatalog(string[] terrains, Dictionary<string, Locomotor> profiles)
    { Terrains = Array.AsReadOnly(terrains); Profiles = new(profiles); }
    public byte TerrainIndex(string name)
    {
        int index = Terrains.IndexOf(name);
        return index < 0 ? throw new ArgumentException("Unknown terrain: " + name) : checked((byte)index);
    }
    public static LocomotionCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>()
            .Register<TerrainOptions>("Terrain", (_, _, options) => options)
            .Register<LocomotorOptions>("Locomotor", (_, _, options) => options);
        registry.ValidateTemplates(definitions);
        var terrains = new List<(string Id, bool Default)>();
        var profiles = new Dictionary<string, LocomotorOptions>(StringComparer.Ordinal);
        foreach (var (id, node) in definitions.Concrete)
        {
            var traits = registry.Compile(id, node).Create(id);
            var terrain = traits.All<TerrainOptions>().SingleOrDefault();
            var profile = traits.All<LocomotorOptions>().SingleOrDefault();
            if ((terrain is null) == (profile is null)) throw node.Error(id, "Declare exactly one Terrain or Locomotor.");
            if (terrain is not null) terrains.Add((id, terrain.Default));
            else profiles.Add(id, profile!);
        }
        if (terrains.Count is < 1 or > 256 || terrains.Count(t => t.Default) != 1)
            throw new ArgumentException("Declare 1..256 terrains and exactly one Default terrain.", nameof(sources));
        var names = terrains.OrderByDescending(t => t.Default).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t => t.Id).ToArray();
        var result = new Dictionary<string, Locomotor>(StringComparer.Ordinal);
        foreach (var (id, profile) in profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var node = definitions.Concrete.Single(p => p.Key == id).Value;
            if (profile.Terrain is null || profile.Terrain.Count != names.Length || profile.Terrain.Keys.Any(k => !names.Contains(k)))
                throw node.Error(id + ".Locomotor.Terrain", "Define exactly one rule for every declared terrain.");
            try
            {
                result.Add(id, new(id, names.Select(name =>
                {
                    var rule = profile.Terrain[name];
                    if (rule is null) throw node.Error(id + ".Locomotor.Terrain." + name, "A terrain rule must be a mapping.");
                    return new TerrainRule(rule.Traverse, rule.CanStop ?? rule.Traverse,
                        rule.Speed ?? (rule.Traverse ? 1024 : 0), rule.Cost ?? (rule.Traverse ? 1024 : 0));
                })));
            }
            catch (ArgumentException error) { throw node.Error(id + ".Locomotor", error.Message); }
        }
        if (result.Count == 0) throw new ArgumentException("Declare at least one Locomotor.", nameof(sources));
        return new(names, result);
    }
    private sealed class TerrainOptions { public bool Default { get; set; } }
    private sealed class LocomotorOptions { public Dictionary<string, RuleOptions> Terrain { get; set; } = new(); }
    private sealed class RuleOptions
    {
        public bool Traverse { get; set; } = true;
        public bool? CanStop { get; set; }
        public int? Speed { get; set; }
        public int? Cost { get; set; }
    }
}
