using System.Globalization;
using DeclarativeContent;
using OpenRTS.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace OpenRTS.Content;

internal sealed class MapYaml(ContentSource source)
{
    public YamlNode Root()
    {
        try
        {
            var parser = new Parser(new StringReader(source.Text));
            int depth = 0;
            while (parser.MoveNext())
            {
                var item = parser.Current!;
                if (item is AnchorAlias || item is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                    throw Error(item.Start, "YAML", "Anchors, aliases and tags are not supported.");
                if (item is MappingStart or SequenceStart && ++depth > 8)
                    throw Error(item.Start, "YAML", "Map data is nested too deeply.");
                if (item is MappingEnd or SequenceEnd) depth--;
            }

            var stream = new YamlStream();
            stream.Load(new StringReader(source.Text));
            if (stream.Documents.Count != 1)
                throw Error(Mark.Empty, "YAML", "Expected exactly one YAML document.");
            return stream.Documents[0].RootNode;
        }
        catch (YamlException error)
        {
            throw Error(error.Start, "YAML", error.Message);
        }
    }

    public Dictionary<string, YamlNode> Fields(YamlNode node, string path, params string[] allowed)
    {
        if (node is not YamlMappingNode mapping) throw Error(node, path, "Expected a mapping.");
        var fields = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping.Children)
        {
            string name = Text(key, path);
            if (allowed.Length != 0 && !allowed.Contains(name))
                throw Error(key, path, $"Unknown field '{name}'.");
            if (!fields.TryAdd(name, value))
                throw Error(key, path, $"Duplicate field '{name}'.");
        }
        return fields;
    }

    public YamlNode Required(Dictionary<string, YamlNode> fields, string key, YamlNode parent, string path) =>
        fields.TryGetValue(key, out var node) ? node : throw Error(parent, path, $"Missing field '{key}'.");

    public IReadOnlyList<YamlNode> Items(YamlNode node, string path) =>
        node is YamlSequenceNode sequence ? sequence.Children.ToArray() : throw Error(node, path, "Expected a sequence.");

    public string Text(YamlNode node, string path) =>
        node is YamlScalarNode { Value: { } value } && !string.IsNullOrWhiteSpace(value)
            ? value : throw Error(node, path, "Expected a nonempty scalar.");

    public long Integer(YamlNode node, string path, long min, long max) =>
        long.TryParse(Text(node, path), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            && value >= min && value <= max ? value : throw Error(node, path, $"Expected an integer in [{min}, {max}].");

    public WorldPosition Pair(YamlNode node, string path, int min, int max)
    {
        var items = Items(node, path);
        if (items.Count != 2) throw Error(node, path, "Expected two coordinates.");
        return new((int)Integer(items[0], path + "[0]", min, max), (int)Integer(items[1], path + "[1]", min, max));
    }

    public Source Location(YamlNode node) => new(source.Name, node.Start.Line, node.Start.Column);
    public ContentException Error(YamlNode node, string path, string message) => new(Location(node), path, message);
    private ContentException Error(Mark mark, string path, string message) =>
        new(new(source.Name, mark.Line, mark.Column), path, message);
}
