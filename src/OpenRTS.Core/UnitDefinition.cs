using System.Collections.ObjectModel;

namespace OpenRTS.Core;

/// <summary>Gameplay and semantic capabilities; no textures, model resources or adapter types.</summary>
public sealed record UnitDefinition
{
    public string Id { get; }
    public int MaxHealth { get; }
    public ReadOnlyCollection<string> Animations { get; }
    public ReadOnlyCollection<string> States { get; }
    public int IdleAnimation { get; }
    public int MoveAnimation { get; }
    public Locomotor Locomotor { get; }

    public UnitDefinition(string id, int maxHealth, IEnumerable<string>? animations = null, IEnumerable<string>? states = null, Locomotor? locomotor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHealth);
        Id = id; MaxHealth = maxHealth;
        Locomotor = locomotor ?? Locomotor.Default;
        Animations = Names(animations ?? ["idle", "move"], 64);
        States = Names(states ?? [], 32);
        IdleAnimation = AnimationIndex("idle"); MoveAnimation = AnimationIndex("move");
        if (IdleAnimation < 0 || MoveAnimation < 0) throw new ArgumentException("Units require idle and move semantic animations.", nameof(animations));
    }

    public int AnimationIndex(string name) => Animations.IndexOf(name);
    public uint StateMask(string name)
    {
        int index = States.IndexOf(name);
        if (index < 0) throw new ArgumentException("Unknown semantic state: " + name, nameof(name));
        return 1u << index;
    }
    public uint AllowedStateMask => States.Count == 32 ? uint.MaxValue : (1u << States.Count) - 1;

    private static ReadOnlyCollection<string> Names(IEnumerable<string> source, int maximum)
    {
        var names = source.ToArray();
        if (names.Length > maximum || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Semantic names must be nonempty, unique and within their channel budget.", nameof(source));
        Array.Sort(names, StringComparer.Ordinal);
        return Array.AsReadOnly(names);
    }
}
