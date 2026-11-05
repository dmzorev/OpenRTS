namespace OpenRTS.Core;

/// <summary>Immutable, typed recipe. Capabilities are optional; this is not an actor's runtime state.</summary>
public sealed record EntityDefinition
{
    public string Id { get; }
    public HealthDefinition? Health { get; }
    public SpatialDefinition? Spatial { get; }
    public PresentationDefinition? Presentation { get; }
    public ArmamentDefinition? Armament { get; }
    public ProducerDefinition? Production { get; }
    public BuildDefinition? Build { get; }
    public EconomyDefinition? Economy { get; }
    public CapturableDefinition? Capturable { get; }

    public EntityDefinition(string id, HealthDefinition? health = null, SpatialDefinition? spatial = null,
        PresentationDefinition? presentation = null, ArmamentDefinition? armament = null,
        ProducerDefinition? production = null, BuildDefinition? build = null, EconomyDefinition? economy = null, CapturableDefinition? capturable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Economy = economy; Capturable = capturable;
        if (economy is not null && (spatial is null || spatial.Movable || presentation is null || !presentation.States.Contains(economy.LowPowerState))) throw new ArgumentException("Economy requires an immobile entity and declared lowpower state.");
        if (capturable is not null && (spatial is null || spatial.Movable || health is null || health.Destruction == DestructionPolicy.Invulnerable)) throw new ArgumentException("Capturable requires an immobile attackable entity with health.");
        if (health?.Destruction == DestructionPolicy.Neutralize && capturable is null) throw new ArgumentException("Neutralize requires Capturable.");
        Id = id; Health = health; Spatial = spatial; Presentation = presentation; Build = build;
        Armament = armament is not null && (armament.Weapons.Count > 0 || armament.Abilities.Count > 0) ? armament : null;
        Production = production?.Queues.Count > 0 ? production : null;
        if (Armament is not null)
        {
            if (spatial is null) throw new ArgumentException("Armaments require spatial capability.");
            if (presentation is null || Armament.Weapons.Any(m => presentation.AnimationIndex(m.Animation) < 0))
                throw new ArgumentException("Weapon mounts must reference declared semantic animations.");
        }
        if (Production is not null)
        {
            if (Production.Products.Count > 0 && spatial is null) throw new ArgumentException("Physical production requires spatial capability.");
            if (Production.Queues.Any(q => q.WorkingState is not null && (presentation is null || !presentation.States.Contains(q.WorkingState))))
                throw new ArgumentException("Queue working state must be declared by the entity.");
            if (Production.Queues.Any(q => q.ExitAnimation is not null && (presentation is null || presentation.AnimationIndex(q.ExitAnimation) < 0)))
                throw new ArgumentException("Queue exit animation must be declared by the entity.");
        }
        if (Armament is not null && Production is not null && Armament.Abilities.Any(a => Production.Products.Any(p => p.Id == a.Id)))
            throw new ArgumentException("Ability and production action IDs must be distinct.");
    }
}
