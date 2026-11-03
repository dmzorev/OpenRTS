using System.Collections.ObjectModel;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DeclarativeContent;

public sealed record Source(string File, long Line, long Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

public sealed class ContentException(Source source, string path, string message)
    : Exception($"{source} [{path}]: {message}");

/// <summary>A read-only syntax tree. Each value keeps its origin through inheritance.</summary>
public sealed record ContentNode(Source Source, string? Scalar = null,
    IReadOnlyDictionary<string, ContentNode>? Fields = null, IReadOnlyList<ContentNode>? Items = null)
{
    public IReadOnlyDictionary<string, ContentNode> Map(string path) => Fields ??
        throw new ContentException(Source, path, "Ожидался блок ключ: значение.");
    public ContentException Error(string path, string message) => new(Source, path, message);
    internal static ContentNode Mapping(Source source, IDictionary<string, ContentNode> fields) =>
        new(source, Fields: new ReadOnlyDictionary<string, ContentNode>(fields));
}

public sealed record ContentSource(string Name, string Text);

public sealed class DefinitionSet
{
    public IReadOnlyDictionary<string, ContentNode> All { get; }
    public IEnumerable<KeyValuePair<string, ContentNode>> Concrete => All.Where(x => !x.Key.StartsWith('^'));
    private DefinitionSet(Dictionary<string, ContentNode> resolved) => All = new ReadOnlyDictionary<string, ContentNode>(resolved);

    public static DefinitionSet Load(IEnumerable<ContentSource> sources)
    {
        var raw = new Dictionary<string, ContentNode>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var root = Parse(source);
            foreach (var (id, node) in root.Map(source.Name))
            {
                if (string.IsNullOrWhiteSpace(id) || id == "^" || id.StartsWith('-'))
                    throw node.Error(id, "Недопустимый идентификатор определения.");
                node.Map(id);
                if (!raw.TryAdd(id, node))
                    throw node.Error(id, $"Повторное определение; первое находится в {raw[id].Source}. Используйте Inherits и новый ID.");
            }
        }
        var resolved = new Dictionary<string, ContentNode>(StringComparer.Ordinal);
        var visiting = new List<string>();
        ContentNode Resolve(string id, ContentNode reference)
        {
            if (resolved.TryGetValue(id, out var cached)) return cached;
            if (!raw.TryGetValue(id, out var node)) throw reference.Error(id, "Родитель не найден.");
            if (visiting.Contains(id)) throw reference.Error(id, $"Цикл наследования: {string.Join(" → ", visiting.Append(id))}.");
            if (visiting.Count >= 64) throw reference.Error(id, "Превышена глубина наследования (64).");
            visiting.Add(id);
            ContentNode merged = ContentNode.Mapping(node.Source, new Dictionary<string, ContentNode>(StringComparer.Ordinal));
            // Parent declarations are evaluated first, in source order. Local fields always win.
            foreach (var (key, value) in node.Map(id).Where(x => IsInheritance(x.Key)))
            {
                if (value.Fields is not null || value.Items is not null || string.IsNullOrWhiteSpace(value.Scalar))
                    throw value.Error($"{id}.{key}", "Inherits ожидает один ID родителя.");
                merged = Merge(merged, Resolve(value.Scalar, value), id);
            }
            merged = Merge(merged, ContentNode.Mapping(node.Source,
                node.Map(id).Where(x => !IsInheritance(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)), id);
            visiting.RemoveAt(visiting.Count - 1);
            resolved.Add(id, merged);
            return merged;
        }
        foreach (var (id, node) in raw) Resolve(id, node);
        return new(resolved);
    }

    private static bool IsInheritance(string key) => key == "Inherits" || key.StartsWith("Inherits@", StringComparison.Ordinal);

    private static ContentNode Merge(ContentNode basis, ContentNode overlay, string path)
    {
        if (overlay.Fields is null) return overlay;
        var result = basis.Fields is null ? new Dictionary<string, ContentNode>(StringComparer.Ordinal) :
            new Dictionary<string, ContentNode>(basis.Fields, StringComparer.Ordinal);
        foreach (var (key, value) in overlay.Fields)
        {
            if (IsInheritance(key)) throw value.Error(path, "Inherits допускается только в корне определения.");
            if (key.StartsWith('-'))
            {
                if (value.Fields is not null || value.Items is not null || !string.IsNullOrEmpty(value.Scalar))
                    throw value.Error($"{path}.{key}", "Удаление должно иметь пустое значение: -Имя:");
                if (!result.Remove(key[1..])) throw value.Error($"{path}.{key}", "Удаляемый блок отсутствует; проверьте имя и @instance.");
            }
            else result[key] = Merge(result.GetValueOrDefault(key) ?? ContentNode.Mapping(value.Source,
                new Dictionary<string, ContentNode>()), value, $"{path}.{key}");
        }
        return ContentNode.Mapping(overlay.Source, result);
    }

    private static ContentNode Parse(ContentSource source)
    {
        try
        {
            // Inheritance has one explicit mechanism. Aliases/tags cannot bypass validation or create cycles.
            var parser = new Parser(new StringReader(source.Text));
            while (parser.MoveNext())
            {
                var e = parser.Current;
                if (e is AnchorAlias || e is NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
                    throw new ContentException(new(source.Name, e.Start.Line, e.Start.Column), "YAML", "Anchors, aliases и tags не поддерживаются; используйте Inherits.");
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(source.Text));
            if (stream.Documents.Count != 1)
                throw new ContentException(new(source.Name, 1, 1), "YAML", "Ожидался ровно один YAML-документ.");
            ContentNode Convert(YamlNode node, int depth)
            {
                var at = new Source(source.Name, node.Start.Line, node.Start.Column);
                if (depth > 64) throw new ContentException(at, "YAML", "Превышена глубина блоков (64).");
                if (node is YamlScalarNode scalar) return new(at, Scalar: scalar.Value);
                if (node is YamlSequenceNode sequence) return new(at, Items: Array.AsReadOnly(sequence.Children.Select(x => Convert(x, depth + 1)).ToArray()));
                if (node is YamlMappingNode mapping)
                {
                    var fields = new Dictionary<string, ContentNode>(StringComparer.Ordinal);
                    foreach (var (k, v) in mapping.Children)
                    {
                        if (k is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                            throw new ContentException(at, "YAML", "Ключ должен быть непустой строкой.");
                        fields.Add(key.Value, Convert(v, depth + 1));
                    }
                    return ContentNode.Mapping(at, fields);
                }
                throw new ContentException(at, "YAML", "Неизвестный узел.");
            }
            return Convert(stream.Documents[0].RootNode, 0);
        }
        catch (YamlException e)
        {
            throw new ContentException(new(source.Name, e.Start.Line, e.Start.Column), "YAML", e.Message);
        }
    }
}
