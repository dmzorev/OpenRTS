using System.Collections.ObjectModel;

namespace OpenRTS.Core;

public sealed record HealthDefinition
{
    public int MaxHealth { get; }
    public ArmorKind Armor { get; }
    public HealthDefinition(int maxHealth, ArmorKind armor = ArmorKind.Living)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHealth);
        if (!Enum.IsDefined(armor)) throw new ArgumentException("Unknown armor class.", nameof(armor));
        MaxHealth = maxHealth; Armor = armor;
    }
}

public sealed record SpatialDefinition
{
    public Locomotor Locomotor { get; }
    public bool Movable { get; }
    public BodyShape Body => Locomotor.Body;
    public FootprintDefinition? Footprint { get; }
    public SpatialDefinition(Locomotor? locomotor = null, bool movable = true, BodyShape? body = null, FootprintDefinition? footprint = null)
    {
        if (footprint is not null && movable) throw new ArgumentException("Footprints belong to immobile objects.", nameof(footprint));
        Locomotor = (locomotor ?? Locomotor.Default).WithBody(footprint is not null ? BodyShape.None : body ?? locomotor?.Body ?? BodyShape.Default);
        Movable = movable; Footprint = footprint;
    }
}

public sealed record PresentationDefinition
{
    public ReadOnlyCollection<string> Animations { get; }
    public ReadOnlyCollection<string> States { get; }
    public int IdleAnimation => AnimationIndex("idle");
    public int MoveAnimation => AnimationIndex("move");
    public uint AllowedStateMask => States.Count == 32 ? uint.MaxValue : (1u << States.Count) - 1;
    public PresentationDefinition(IEnumerable<string>? animations = null, IEnumerable<string>? states = null)
    {
        Animations = Names(animations ?? ["idle", "move"], 64);
        States = Names(states ?? [], 32);
    }
    public int AnimationIndex(string name) => Animations.IndexOf(name);
    public uint StateMask(string name)
    {
        int index = States.IndexOf(name);
        if (index < 0) throw new ArgumentException("Unknown semantic state: " + name, nameof(name));
        return 1u << index;
    }
    private static ReadOnlyCollection<string> Names(IEnumerable<string> source, int maximum)
    {
        var names = source.ToArray();
        if (names.Length > maximum || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Semantic names must be nonempty, unique and within their channel budget.", nameof(source));
        Array.Sort(names, StringComparer.Ordinal);
        return Array.AsReadOnly(names);
    }
}

public sealed record ArmamentDefinition
{
    internal static ArmamentDefinition Empty { get; } = new();
    public ReadOnlyCollection<WeaponMountDefinition> Weapons { get; }
    public ReadOnlyCollection<AbilityDefinition> Abilities { get; }
    public ArmamentDefinition(IEnumerable<WeaponMountDefinition>? weapons = null, IEnumerable<AbilityDefinition>? abilities = null)
    {
        var mounts = (weapons ?? []).ToArray();
        if (mounts.Length > 8 || mounts.Any(m => m is null || m.Weapon is null || string.IsNullOrWhiteSpace(m.Id)) || mounts.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != mounts.Length)
            throw new ArgumentException("Entities allow up to eight uniquely named weapon mounts.");
        var skills = (abilities ?? []).ToArray();
        if (skills.Any(a => a is null)) throw new ArgumentException("Abilities cannot be null.");
        Array.Sort(skills, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        if (skills.Length > 8 || skills.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 128 || !mounts.Any(m => m.Id == a.Mount)) ||
            skills.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != skills.Length || skills.Select(a => a.Mount).Distinct(StringComparer.Ordinal).Count() != skills.Length)
            throw new ArgumentException("Abilities require unique IDs, known distinct mounts and bounded counts.");
        foreach (var a in skills)
        {
            var w = mounts.Single(m => m.Id == a.Mount).Weapon;
            if (a.GroundTarget && (w.Effect == WeaponEffect.Repair || w.Projectile == ProjectileKind.Missile || w.SplashRadius == 0))
                throw new ArgumentException("Ground abilities require splash and cannot repair or home.");
        }
        Weapons = Array.AsReadOnly(mounts); Abilities = Array.AsReadOnly(skills);
    }
}

public sealed record ProducerDefinition
{
    internal static ProducerDefinition Empty { get; } = new();
    public ReadOnlyCollection<TaskQueueDefinition> Queues { get; }
    public ReadOnlyCollection<ProductionDefinition> Products { get; }
    public ProducerDefinition(IEnumerable<TaskQueueDefinition>? queues = null, IEnumerable<ProductionDefinition>? products = null)
    {
        var tasks = (queues ?? []).ToArray(); var recipes = (products ?? []).ToArray();
        if (tasks.Any(q => q is null) || recipes.Any(p => p is null)) throw new ArgumentException("Queues/products cannot be null.");
        Array.Sort(tasks, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id)); Array.Sort(recipes, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        if (tasks.Length > 8 || recipes.Length > 64 || tasks.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != tasks.Length ||
            recipes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != recipes.Length ||
            recipes.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || string.IsNullOrWhiteSpace(p.Unit) || !tasks.Any(q => q.Id == p.Queue)))
            throw new ArgumentException("Production requires unique action/queue IDs, known queues and bounded counts.");
        Queues = Array.AsReadOnly(tasks); Products = Array.AsReadOnly(recipes);
    }
}
