using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed class StartingSet
{
    internal sealed record Entry(string Type, WorldPosition Offset, Source Source);
    internal IReadOnlyList<Entry> Units { get; }
    public string Id { get; }
    public long Credits { get; }

    private StartingSet(string id, long credits, Entry[] units)
    {
        Id = id;
        Credits = credits;
        Units = units;
    }

    public static StartingSet Load(ContentSource source)
    {
        var yaml = new MapYaml(source);
        var root = yaml.Root();
        var fields = yaml.Fields(root, source.Name, "version", "id", "credits", "units");
        yaml.Integer(yaml.Required(fields, "version", root, source.Name), "version", 1, 1);
        string id = yaml.Text(yaml.Required(fields, "id", root, source.Name), "id");
        long credits = yaml.Integer(yaml.Required(fields, "credits", root, id), "credits", 0, SimulationWorld.MaximumCredits);
        var list = yaml.Required(fields, "units", root, id);
        var units = yaml.Items(list, "units");
        if (units.Count == 0) throw yaml.Error(list, "units", "A starting set needs at least one unit.");
        return new(id, credits, units.Select((node, i) =>
        {
            string path = $"units[{i}]";
            var entry = yaml.Fields(node, path, "type", "offset");
            string type = yaml.Text(yaml.Required(entry, "type", node, path), path + ".type");
            var offset = yaml.Pair(yaml.Required(entry, "offset", node, path), path + ".offset",
                -WorldPosition.MaximumExtent, WorldPosition.MaximumExtent);
            return new Entry(type, offset, yaml.Location(node));
        }).ToArray());
    }
}
