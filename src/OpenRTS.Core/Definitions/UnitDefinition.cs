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
    public ReadOnlyCollection<WeaponMountDefinition> Weapons { get; }
    public WeaponDefinition? Weapon => Weapons.Count == 0 ? null : Weapons[0].Weapon;
    public ArmorKind Armor { get; }
    public bool Movable { get; }
    public BodyShape Body => Locomotor.Body;
    public FootprintDefinition? Footprint { get; }
    public ReadOnlyCollection<AbilityDefinition> Abilities { get; }
    public ReadOnlyCollection<TaskQueueDefinition> TaskQueues { get; }
    public ReadOnlyCollection<ProductionDefinition> Products { get; }
    public BuildDefinition? Build { get; }

    public UnitDefinition(string id, int maxHealth, IEnumerable<string>? animations = null, IEnumerable<string>? states = null, Locomotor? locomotor = null, WeaponDefinition? weapon = null, IEnumerable<WeaponMountDefinition>? weapons = null, ArmorKind armor = ArmorKind.Living, bool movable = true, BodyShape? body = null, FootprintDefinition? footprint = null, IEnumerable<AbilityDefinition>? abilities = null, IEnumerable<TaskQueueDefinition>? taskQueues = null, IEnumerable<ProductionDefinition>? products = null, BuildDefinition? build = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHealth);
        Id = id; MaxHealth = maxHealth;
        if (footprint is not null && movable) throw new ArgumentException("Footprints belong to immobile objects.", nameof(footprint));
        Footprint = footprint;
        Locomotor = (locomotor ?? Locomotor.Default).WithBody(footprint is not null ? BodyShape.None : body ?? locomotor?.Body ?? BodyShape.Default);
        if (weapon is not null && weapons is not null) throw new ArgumentException("Use either a single weapon or mounts.");
        var mounts = weapons?.ToArray() ?? (weapon is null ? [] : [new WeaponMountDefinition("main", weapon)]);
        if (mounts.Length > 8 || mounts.Any(m => m is null || m.Weapon is null || string.IsNullOrWhiteSpace(m.Id)) || mounts.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != mounts.Length || !Enum.IsDefined(armor))
            throw new ArgumentException("Units allow up to eight uniquely named weapon mounts and a known armor class.");
        Weapons = Array.AsReadOnly(mounts); Armor = armor; Movable = movable;
        var skills = (abilities ?? []).ToArray();
        var queues = (taskQueues ?? []).ToArray();
        var recipes = (products ?? []).ToArray();
        if (skills.Any(a => a is null) || queues.Any(q => q is null) || recipes.Any(p => p is null)) throw new ArgumentException("Actions/queues cannot be null.");
        Array.Sort(skills, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id)); Array.Sort(queues, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id)); Array.Sort(recipes, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        if (skills.Length > 8 || queues.Length > 8 || recipes.Length > 64 || skills.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 128 || !mounts.Any(m => m.Id == a.Mount)) ||
            skills.Select(a => a.Id).Distinct().Count() != skills.Length || skills.Select(a => a.Mount).Distinct().Count() != skills.Length ||
            queues.Select(q => q.Id).Distinct().Count() != queues.Length || recipes.Select(p => p.Id).Distinct().Count() != recipes.Length ||
            recipes.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 128 || string.IsNullOrWhiteSpace(p.Unit) || !queues.Any(q => q.Id == p.Queue)) || skills.Any(a => recipes.Any(p => p.Id == a.Id)))
            throw new ArgumentException("Actions require unique IDs, known mounts/queues and bounded counts.");
        foreach (var a in skills)
        { var w = mounts.Single(m => m.Id == a.Mount).Weapon; if (a.GroundTarget && (w.Effect == WeaponEffect.Repair || w.Projectile == ProjectileKind.Missile || w.SplashRadius == 0)) throw new ArgumentException("Ground abilities require splash and cannot repair or home."); }
        Abilities = Array.AsReadOnly(skills); TaskQueues = Array.AsReadOnly(queues); Products = Array.AsReadOnly(recipes); Build = build;
        Animations = Names(animations ?? ["idle", "move"], 64);
        States = Names(states ?? [], 32);
        if (queues.Any(q => q.WorkingState is not null && !States.Contains(q.WorkingState))) throw new ArgumentException("Queue working state must be declared by the unit.");
        if (queues.Any(q => q.ExitAnimation is not null && !Animations.Contains(q.ExitAnimation))) throw new ArgumentException("Queue exit animation must be declared by the unit.");
        IdleAnimation = AnimationIndex("idle"); MoveAnimation = AnimationIndex("move");
        if (!movable && MoveAnimation < 0) MoveAnimation = IdleAnimation;
        if (IdleAnimation < 0 || MoveAnimation < 0) throw new ArgumentException("Units require idle and move semantic animations.", nameof(animations));
        if (Weapons.Any(m => AnimationIndex(m.Animation) < 0)) throw new ArgumentException("Weapon mounts must reference declared semantic animations.", nameof(animations));
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(Id); writer.Write(MaxHealth); Locomotor.WriteState(writer); writer.Write((int)Armor); writer.Write(Movable);
        writer.Write(Footprint is not null); Footprint?.WriteState(writer);
        writer.Write(Weapons.Count);
        foreach (var mount in Weapons) { writer.Write(mount.Id); writer.Write(mount.Animation); writer.Write(mount.FireWhileMoving); mount.Weapon.WriteState(writer); }
        writer.Write(Build is not null); Build?.Write(writer);
        writer.Write(Abilities.Count); foreach (var a in Abilities) { writer.Write(a.Id); writer.Write(a.Mount); writer.Write(a.GroundTarget); writer.Write(a.AutoCast); }
        writer.Write(TaskQueues.Count); foreach (var q in TaskQueues) { writer.Write(q.Id); writer.Write(q.Capacity); writer.Write((int)q.Payment); writer.Write(q.Exit.X); writer.Write(q.Exit.Y); writer.Write(q.ExitSearchRadius); writer.Write(q.WorkingState is not null); if (q.WorkingState is not null) writer.Write(q.WorkingState); writer.Write(q.ExitAnimation is not null); if (q.ExitAnimation is not null) writer.Write(q.ExitAnimation); writer.Write(q.ExitAnimationTicks); }
        writer.Write(Products.Count); foreach (var p in Products) { writer.Write(p.Id); writer.Write(p.Unit); writer.Write(p.Queue); writer.Write(p.Override is not null); p.Override?.Write(writer); }
        writer.Write(Animations.Count); foreach (var name in Animations) writer.Write(name);
        writer.Write(States.Count); foreach (var name in States) writer.Write(name);
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
