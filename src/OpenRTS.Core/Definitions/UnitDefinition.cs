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

    public UnitDefinition(string id, int maxHealth, IEnumerable<string>? animations = null, IEnumerable<string>? states = null, Locomotor? locomotor = null, WeaponDefinition? weapon = null, IEnumerable<WeaponMountDefinition>? weapons = null, ArmorKind armor = ArmorKind.Living, bool movable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHealth);
        Id = id; MaxHealth = maxHealth;
        Locomotor = locomotor ?? Locomotor.Default;
        if (weapon is not null && weapons is not null) throw new ArgumentException("Use either a single weapon or mounts.");
        var mounts = weapons?.ToArray() ?? (weapon is null ? [] : [new WeaponMountDefinition("main", weapon)]);
        if (mounts.Length > 8 || mounts.Any(m => m is null || m.Weapon is null || string.IsNullOrWhiteSpace(m.Id)) || mounts.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != mounts.Length || !Enum.IsDefined(armor))
            throw new ArgumentException("Units allow up to eight uniquely named weapon mounts and a known armor class.");
        Weapons = Array.AsReadOnly(mounts); Armor = armor; Movable = movable;
        Animations = Names(animations ?? ["idle", "move"], 64);
        States = Names(states ?? [], 32);
        IdleAnimation = AnimationIndex("idle"); MoveAnimation = AnimationIndex("move");
        if (IdleAnimation < 0 || MoveAnimation < 0) throw new ArgumentException("Units require idle and move semantic animations.", nameof(animations));
        if (Weapons.Any(m => AnimationIndex(m.Animation) < 0)) throw new ArgumentException("Weapon mounts must reference declared semantic animations.", nameof(animations));
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(Id); writer.Write(MaxHealth); Locomotor.WriteState(writer); writer.Write((int)Armor); writer.Write(Movable);
        writer.Write(Weapons.Count);
        foreach (var mount in Weapons) { writer.Write(mount.Id); writer.Write(mount.Animation); writer.Write(mount.FireWhileMoving); mount.Weapon.WriteState(writer); }
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
