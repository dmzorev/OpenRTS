using DeclarativeContent;

namespace OpenRTS.Content;

/// <summary>Explicit source lists for a game; parsing and directory expansion happen before catalog compilation.</summary>
public sealed class GameManifest
{
    public ContentFiles Files { get; }
    public string Title { get; }
    public IReadOnlyList<ContentSource> Rules { get; }
    public IReadOnlyList<ContentSource> Terrain { get; }
    public IReadOnlyList<ContentSource> Weapons { get; }
    public IReadOnlyList<ContentSource> Models { get; }
    public IReadOnlyList<ContentSource> Effects { get; }
    public GameSettings Settings { get; }

    private GameManifest(ContentFiles files, string title, IReadOnlyList<ContentSource> rules,
        IReadOnlyList<ContentSource> terrain, IReadOnlyList<ContentSource> weapons,
        IReadOnlyList<ContentSource> models, IReadOnlyList<ContentSource> effects, GameSettings settings)
    {
        Files = files; Title = title; Rules = rules; Terrain = terrain; Weapons = weapons;
        Models = models; Effects = effects; Settings = settings;
    }

    public static GameManifest Load(string filename)
    {
        string full = Path.GetFullPath(filename);
        return Load(ContentFiles.FromDirectory(Path.GetDirectoryName(full)!), Path.GetFileName(full));
    }

    public static GameManifest Load(ContentFiles files, string filename = "game.yaml")
    {
        var source = files.Read(filename);
        var yaml = new MapYaml(source);
        var root = yaml.Root();
        var fields = yaml.Fields(root, filename, "Version", "Title", "Rules", "Terrain", "Weapons", "Models", "Effects", "Settings");
        yaml.Integer(yaml.Required(fields, "Version", root, filename), "Version", 1, 1);
        string title = yaml.Text(yaml.Required(fields, "Title", root, filename), "Title");
        IReadOnlyList<ContentSource> Sources(string key, bool required = false)
        {
            if (!fields.TryGetValue(key, out var node))
            {
                if (required) throw yaml.Error(root, key, "Missing source list.");
                return Array.Empty<ContentSource>();
            }
            var items = yaml.Items(node, key);
            if (items.Count == 0) throw yaml.Error(node, key, "Source list must not be empty; omit optional sections instead.");
            var result = new List<ContentSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                try
                {
                    string path = Path.Combine(Path.GetDirectoryName(filename) ?? "", yaml.Text(item, key));
                    foreach (var file in files.Expand([path]))
                        if (seen.Add(file.Name)) result.Add(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { throw yaml.Error(item, key, error.Message); }
            }
            return result.AsReadOnly();
        }
        return new(files, title, Sources("Rules", true), Sources("Terrain"), Sources("Weapons"),
            Sources("Models"), Sources("Effects"), GameSettings.Load(Sources("Settings")));
    }

    public UnitCatalog LoadUnits(IEnumerable<ContentSource>? rules = null, IEnumerable<ContentSource>? weapons = null) =>
        UnitCatalog.Load(rules ?? Rules, Terrain.Count == 0 ? null : LocomotionCatalog.Load(Terrain),
            Weapons.Count == 0 && weapons is null ? null : WeaponCatalog.Load(weapons ?? Weapons));

    public ModelCatalog LoadModels() => ModelCatalog.Load(Models, Settings.Presentation.ModelOverrides,
        Settings.Presentation.Camera == GameCamera.Isometric ? 0.75f : 1f);
}
