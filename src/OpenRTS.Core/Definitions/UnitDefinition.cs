using System.Collections.ObjectModel;

namespace OpenRTS.Core;

/// <summary>Spatial-unit view over a compiled recipe, retaining the existing host/content API.</summary>
public sealed record UnitDefinition
{
    public EntityDefinition Definition { get; }
    public string Id => Definition.Id;
    public int MaxHealth => Definition.Health!.MaxHealth;
    public ArmorKind Armor => Definition.Health!.Armor;
    public ReadOnlyCollection<string> Animations => Definition.Presentation!.Animations;
    public ReadOnlyCollection<string> States => Definition.Presentation!.States;
    public int IdleAnimation { get; }
    public int MoveAnimation { get; }
    public Locomotor Locomotor => Definition.Spatial!.Locomotor;
    public bool Movable => Definition.Spatial!.Movable;
    public BodyShape Body => Definition.Spatial!.Body;
    public FootprintDefinition? Footprint => Definition.Spatial!.Footprint;
    public ReadOnlyCollection<WeaponMountDefinition> Weapons => (Definition.Armament ?? ArmamentDefinition.Empty).Weapons;
    public WeaponDefinition? Weapon => Weapons.Count == 0 ? null : Weapons[0].Weapon;
    public ReadOnlyCollection<AbilityDefinition> Abilities => (Definition.Armament ?? ArmamentDefinition.Empty).Abilities;
    public ReadOnlyCollection<TaskQueueDefinition> TaskQueues => (Definition.Production ?? ProducerDefinition.Empty).Queues;
    public ReadOnlyCollection<ProductionDefinition> Products => (Definition.Production ?? ProducerDefinition.Empty).Products;
    public BuildDefinition? Build => Definition.Build;

    public UnitDefinition(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Health is null || definition.Spatial is null || definition.Presentation is null)
            throw new ArgumentException("The current unit simulation requires health, spatial and semantic presentation capabilities.", nameof(definition));
        Definition = definition;
        IdleAnimation = definition.Presentation.IdleAnimation;
        MoveAnimation = definition.Presentation.MoveAnimation;
        if (!Movable && MoveAnimation < 0) MoveAnimation = IdleAnimation;
        if (IdleAnimation < 0 || MoveAnimation < 0) throw new ArgumentException("Units require idle and move semantic animations.", nameof(definition));
    }

    public UnitDefinition(string id, int maxHealth, IEnumerable<string>? animations = null, IEnumerable<string>? states = null, Locomotor? locomotor = null, WeaponDefinition? weapon = null, IEnumerable<WeaponMountDefinition>? weapons = null, ArmorKind armor = ArmorKind.Living, bool movable = true, BodyShape? body = null, FootprintDefinition? footprint = null, IEnumerable<AbilityDefinition>? abilities = null, IEnumerable<TaskQueueDefinition>? taskQueues = null, IEnumerable<ProductionDefinition>? products = null, BuildDefinition? build = null)
        : this(Compose(id, maxHealth, animations, states, locomotor, weapon, weapons, armor, movable, body, footprint, abilities, taskQueues, products, build)) { }

    private static EntityDefinition Compose(string id, int maxHealth, IEnumerable<string>? animations, IEnumerable<string>? states,
        Locomotor? locomotor, WeaponDefinition? weapon, IEnumerable<WeaponMountDefinition>? weapons, ArmorKind armor, bool movable,
        BodyShape? body, FootprintDefinition? footprint, IEnumerable<AbilityDefinition>? abilities,
        IEnumerable<TaskQueueDefinition>? taskQueues, IEnumerable<ProductionDefinition>? products, BuildDefinition? build)
    {
        if (weapon is not null && weapons is not null) throw new ArgumentException("Use either a single weapon or mounts.");
        return new(id, new(maxHealth, armor), new(locomotor, movable, body, footprint), new(animations, states),
            new(weapons ?? (weapon is null ? [] : [new WeaponMountDefinition("main", weapon)]), abilities), new(taskQueues, products), build);
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
    public int AnimationIndex(string name) => Definition.Presentation!.AnimationIndex(name);
    public uint StateMask(string name) => Definition.Presentation!.StateMask(name);
    public uint AllowedStateMask => Definition.Presentation!.AllowedStateMask;
}
