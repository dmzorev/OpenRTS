namespace DeclarativeContent;

public sealed record TraitInstance(string Key, object Value);
public sealed class TraitSet(IEnumerable<TraitInstance> traits)
{
    public IReadOnlyList<TraitInstance> Entries { get; } = Array.AsReadOnly(traits.ToArray());
    public IEnumerable<T> All<T>() => Entries.Select(t => t.Value).OfType<T>();
    public T Single<T>() => All<T>().Single();
}

public sealed class TraitPlan<TContext>(IEnumerable<Func<TContext, TraitInstance>> factories)
{
    private readonly Func<TContext, TraitInstance>[] _factories = factories.ToArray();
    public TraitSet Create(TContext context) => new(_factories.Select(f => f(context)));
}

/// <summary>The host explicitly registers supported names, parameters, factories and dependencies.</summary>
public sealed class TraitRegistry<TContext>
{
    private sealed record Registration(Type OptionsType, bool Multiple, bool Required, string[] Requires,
        Action<ContentNode, string> ValidatePartial, Func<ContentNode, string, string, Func<TContext, TraitInstance>> Compile);
    private readonly Dictionary<string, Registration> _types = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Type> OptionTypes => _types.ToDictionary(x => x.Key, x => x.Value.OptionsType);

    public TraitRegistry<TContext> Register<TOptions>(string name, Func<TContext, string, TOptions, object> create,
        bool multiple = false, bool required = false, params string[] requires) where TOptions : new()
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('@')) throw new ArgumentException("Invalid trait name", nameof(name));
        _types.Add(name, new(typeof(TOptions), multiple, required, requires,
            (node, path) => Options.Bind<TOptions>(node, path, partial: true), (node, path, key) =>
        {
            Options.Bind<TOptions>(node, path);
            return context => new(key, create(context, key, Options.Bind<TOptions>(node, path)));
        }));
        return this;
    }

    public void ValidateTemplates(DefinitionSet definitions)
    {
        foreach (var (id, node) in definitions.All.Where(x => x.Key.StartsWith('^')))
            foreach (var (key, trait) in node.Map(id))
            {
                var parts = key.Split('@');
                if (parts.Length > 2 || parts.Length == 2 && parts[1].Length == 0 || !_types.TryGetValue(parts[0], out _))
                    throw trait.Error($"{id}.{key}", "Неизвестный или некорректный трейт шаблона.");
                _types[parts[0]].ValidatePartial(trait, $"{id}.{key}");
            }
    }

    public TraitPlan<TContext> Compile(string id, ContentNode definition)
    {
        var entries = definition.Map(id).Select(pair =>
        {
            var parts = pair.Key.Split('@'); var name = parts[0];
            if (parts.Length > 2 || parts.Length == 2 && parts[1].Length == 0)
                throw pair.Value.Error($"{id}.{pair.Key}", "Ожидалось Trait или Trait@instance.");
            if (!_types.TryGetValue(name, out var type)) throw pair.Value.Error($"{id}.{pair.Key}",
                $"Неизвестный трейт. Допустимы: {string.Join(", ", _types.Keys)}.");
            return (Key: pair.Key, Name: name, Node: pair.Value, Type: type);
        }).ToList();
        foreach (var (name, type) in _types)
        {
            var count = entries.Count(e => e.Name == name);
            if (type.Required && count == 0) throw definition.Error(id, $"Требуется трейт {name}.");
            if (!type.Multiple && count > 1) throw definition.Error(id, $"Трейт {name} допускает только один экземпляр.");
        }
        foreach (var e in entries)
            foreach (var dependency in e.Type.Requires)
                if (!entries.Any(x => x.Name == dependency)) throw e.Node.Error($"{id}.{e.Key}", $"Требуется трейт {dependency}.");
        var factories = new List<Func<TContext, TraitInstance>>();
        while (entries.Count > 0)
        {
            var ready = entries.Where(e => e.Type.Requires.All(d => entries.All(x => x.Name != d))).ToArray();
            if (ready.Length == 0) throw definition.Error(id, $"Цикл зависимостей трейтов: {string.Join(", ", entries.Select(e => e.Key))}.");
            foreach (var e in ready) { factories.Add(e.Type.Compile(e.Node, $"{id}.{e.Key}", e.Key)); entries.Remove(e); }
        }
        return new(factories);
    }
}
