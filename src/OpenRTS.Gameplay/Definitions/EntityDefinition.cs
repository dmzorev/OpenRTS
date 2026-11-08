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
    public PassengerDefinition? Passenger { get; }
    public TransportDefinition? Transport { get; }
    public int ConstructionRange { get; }
    public SupplyDefinition? Supply { get; }

    public EntityDefinition(string id, HealthDefinition? health = null, SpatialDefinition? spatial = null,
        PresentationDefinition? presentation = null, ArmamentDefinition? armament = null,
        ProducerDefinition? production = null, BuildDefinition? build = null, EconomyDefinition? economy = null, CapturableDefinition? capturable = null, PassengerDefinition? passenger = null, TransportDefinition? transport = null, int constructionRange = 0, SupplyDefinition? supply = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (constructionRange is < 0 or > WorldPosition.MaximumExtent ||
            (constructionRange > 0 && spatial is null) ||
            (constructionRange == 0 && (production?.Products.Any(p => p.OnSite) == true ||
                armament?.Abilities.Any(a => a.Kind == AbilityKind.Build) == true)))
            throw new ArgumentException("On-site construction requires a spatial builder and a positive range.");
        ConstructionRange = constructionRange;
        Supply = supply;
        if (supply is not null && (spatial is null || health is null ||
            (supply.Role == SupplyRole.Truck ? !spatial.Movable || passenger is not null :
                spatial.Footprint is null) ||
            supply.Role == SupplyRole.Mine && (capturable is not null || health.Destruction != DestructionPolicy.Neutralize)))
            throw new ArgumentException("Invalid spatial, capture or health capability for supply.");
        if (armament?.Abilities.Any(a => a.Kind == AbilityKind.Harvest) == true && supply?.Role != SupplyRole.Truck ||
            armament?.Abilities.Any(a => a.Kind == AbilityKind.ClaimMine) == true && constructionRange == 0)
            throw new ArgumentException("Supply abilities require the corresponding supply or construction capability.");
        Economy = economy; Capturable = capturable; Passenger = passenger; Transport = transport;
        if (passenger is not null && (spatial is null || !spatial.Movable || spatial.Footprint is not null || health is null || health.Destruction != DestructionPolicy.Destroy || production?.Queues.Count > 0))
            throw new ArgumentException("Passenger requires a movable unit without a footprint or production.");
        if (transport is not null && (health is null || spatial is null || !spatial.Movable && transport.UnloadDistance != -1))
            throw new ArgumentException("Transport requires health and spatial capability; static transports unload in place.");
        if (economy is not null && (spatial is null || spatial.Movable || presentation is null || !presentation.States.Contains(economy.LowPowerState))) throw new ArgumentException("Economy requires an immobile entity and declared lowpower state.");
        if (capturable is not null && (spatial is null || spatial.Movable || health is null || health.Destruction == DestructionPolicy.Invulnerable)) throw new ArgumentException("Capturable requires an immobile attackable entity with health.");
        if (health?.Destruction == DestructionPolicy.Neutralize && capturable is null && supply?.Role != SupplyRole.Mine) throw new ArgumentException("Neutralize requires Capturable.");
        Id = id; Health = health; Spatial = spatial; Presentation = presentation; Build = build;
        Armament = armament is not null && (armament.Weapons.Count > 0 || armament.Abilities.Count > 0) ? armament : null;
        Production = production is not null && (production.Queues.Count > 0 || production.Products.Count > 0) ? production : null;
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
