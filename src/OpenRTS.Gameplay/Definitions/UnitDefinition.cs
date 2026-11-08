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
    public int ConstructionRange => Definition.ConstructionRange;
    public SupplyDefinition? Supply => Definition.Supply;
    public EconomyDefinition? Economy => Definition.Economy;
    public CapturableDefinition? Capturable => Definition.Capturable;
    public PassengerDefinition? Passenger => Definition.Passenger;
    public TransportDefinition? Transport => Definition.Transport;
    public DestructionPolicy Destruction => Definition.Health!.Destruction;
    internal bool DisableWeaponsOnLowPower { get; }
    internal uint LowPowerVisualMask { get; }
    internal uint WeaponDisableVisualMask { get; }

    public UnitDefinition(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Health is null || definition.Spatial is null || definition.Presentation is null)
            throw new ArgumentException("The current unit simulation requires health, spatial and semantic presentation capabilities.", nameof(definition));
        Definition = definition;
        int weaponDisableState = definition.Presentation.States.IndexOf("weapons-disabled");
        if (weaponDisableState >= 0) WeaponDisableVisualMask = 1u << weaponDisableState;
        if (definition.Economy is { } economy)
        {
            DisableWeaponsOnLowPower = economy.DisableWeapons;
            LowPowerVisualMask = definition.Presentation.StateMask(economy.LowPowerState);
        }
        IdleAnimation = definition.Presentation.IdleAnimation;
        MoveAnimation = definition.Presentation.MoveAnimation;
        if (!Movable && MoveAnimation < 0) MoveAnimation = IdleAnimation;
        if (IdleAnimation < 0 || MoveAnimation < 0) throw new ArgumentException("Units require idle and move semantic animations.", nameof(definition));
    }

    public UnitDefinition(string id, int maxHealth, IEnumerable<string>? animations = null, IEnumerable<string>? states = null, Locomotor? locomotor = null, WeaponDefinition? weapon = null, IEnumerable<WeaponMountDefinition>? weapons = null, ArmorKind armor = ArmorKind.Living, bool movable = true, BodyShape? body = null, FootprintDefinition? footprint = null, IEnumerable<AbilityDefinition>? abilities = null, IEnumerable<TaskQueueDefinition>? taskQueues = null, IEnumerable<ProductionDefinition>? products = null, BuildDefinition? build = null, EconomyDefinition? economy = null, CapturableDefinition? capturable = null, DestructionPolicy destruction = DestructionPolicy.Destroy, PassengerDefinition? passenger = null, TransportDefinition? transport = null, int constructionRange = 0, SupplyDefinition? supply = null)
        : this(Compose(id, maxHealth, animations, states, locomotor, weapon, weapons, armor, movable, body, footprint, abilities, taskQueues, products, build, economy, capturable, destruction, passenger, transport, constructionRange, supply)) { }

    private static EntityDefinition Compose(string id, int maxHealth, IEnumerable<string>? animations, IEnumerable<string>? states,
        Locomotor? locomotor, WeaponDefinition? weapon, IEnumerable<WeaponMountDefinition>? weapons, ArmorKind armor, bool movable,
        BodyShape? body, FootprintDefinition? footprint, IEnumerable<AbilityDefinition>? abilities,
        IEnumerable<TaskQueueDefinition>? taskQueues, IEnumerable<ProductionDefinition>? products, BuildDefinition? build, EconomyDefinition? economy, CapturableDefinition? capturable, DestructionPolicy destruction, PassengerDefinition? passenger, TransportDefinition? transport, int constructionRange, SupplyDefinition? supply)
    {
        if (weapon is not null && weapons is not null) throw new ArgumentException("Use either a single weapon or mounts.");
        return new(id, new(maxHealth, armor, destruction), new(locomotor, movable, body, footprint), new(animations, states),
            new(weapons ?? (weapon is null ? [] : [new WeaponMountDefinition("main", weapon)]), abilities), new(taskQueues, products), build, economy, capturable, passenger, transport, constructionRange, supply);
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(Id); writer.Write(ConstructionRange); writer.Write((int)Destruction); writer.Write(Economy is not null);
        if (Economy is { } e) { writer.Write(e.Energy); writer.Write(e.Income); writer.Write(e.Interval); writer.Write(e.LowPowerState); writer.Write(e.DisableWeapons); }
        writer.Write(Capturable is not null); if (Capturable is { } c) writer.Write(c.Ticks ?? 0);
        writer.Write(Passenger is not null);
        if (Passenger is { } passenger) { writer.Write(passenger.Type); writer.Write(passenger.Slots); }
        writer.Write(Transport is not null);
        if (Transport is { } transport)
        {
            writer.Write(transport.Slots); writer.Write(transport.FireFromInside); writer.Write(transport.UnloadDistance);
            writer.Write(transport.Accepts.Count);
            foreach (var type in transport.Accepts) writer.Write(type);
        }
        writer.Write(MaxHealth); Locomotor.WriteState(writer); writer.Write((int)Armor); writer.Write(Movable);
        writer.Write(Footprint is not null); Footprint?.WriteState(writer);
        writer.Write(Weapons.Count);
        foreach (var mount in Weapons) { writer.Write(mount.Id); writer.Write(mount.Animation); writer.Write(mount.FireWhileMoving); writer.Write(mount.FireFromTransport); mount.Weapon.WriteState(writer); }
        writer.Write(Build is not null); Build?.Write(writer);
        writer.Write(Abilities.Count); foreach (var a in Abilities) { writer.Write(a.Id); writer.Write(a.Mount); writer.Write(a.GroundTarget); writer.Write(a.AutoCast); writer.Write((int)a.Kind); writer.Write(a.Range); }
        writer.Write(TaskQueues.Count); foreach (var q in TaskQueues) { writer.Write(q.Id); writer.Write(q.Capacity); writer.Write((int)q.Payment); writer.Write(q.Exit.X); writer.Write(q.Exit.Y); writer.Write(q.ExitSearchRadius); writer.Write(q.WorkingState is not null); if (q.WorkingState is not null) writer.Write(q.WorkingState); writer.Write(q.ExitAnimation is not null); if (q.ExitAnimation is not null) writer.Write(q.ExitAnimation); writer.Write(q.ExitAnimationTicks); }
        writer.Write(Products.Count); foreach (var p in Products) { writer.Write(p.Id); writer.Write(p.Unit); writer.Write(p.Queue); writer.Write(p.Override is not null); p.Override?.Write(writer); writer.Write(p.OnSite); }
        writer.Write(Animations.Count); foreach (var name in Animations) writer.Write(name);
        writer.Write(States.Count); foreach (var name in States) writer.Write(name);
        writer.Write(Supply is not null);
        if (Supply is { } supply)
        {
            writer.Write((int)supply.Role);
            writer.Write(supply.Capacity);
        }
    }
    public int AnimationIndex(string name) => Definition.Presentation!.AnimationIndex(name);
    public uint StateMask(string name) => Definition.Presentation!.StateMask(name);
    public uint AllowedStateMask => Definition.Presentation!.AllowedStateMask;
}
