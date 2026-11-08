using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Construction;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    /// <summary>Owner-thread check of static access for an on-site builder. Mobile clearance is decided by construction.</summary>
    public bool CanAccessBuildingSite(EntityId builder, int productType, WorldPosition position, int quarterTurns,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_navigation is null || !TryGetEntity(builder, out var actor) || !IsOnMap(actor)) return false;
        var type = TypeOf(actor);
        var target = _types[productType].WithRotation(quarterTurns);
        if (type.ConstructionRange <= 0 || target.Footprint is not { } footprint) return false;
        var from = actor.Get<SpatialState>().Position;
        long range = (long)type.ConstructionRange * type.ConstructionRange;
        long distance = type.Movable ? ObjectGeometry.DistanceSquared(type, from, target, position, out _, out _) :
            ObjectGeometry.Squared(from, position);
        if (distance <= range) return true;
        if (!type.Movable) return false;
        var resolver = Resolver(actor.Get<EntityIdentity>().Type);
        foreach (var point in ConstructionGeometry.Exits(footprint, position, from, type.Body, Width, Depth))
            if (resolver.CanStop(point) && resolver.CanReach(from, point, cancellationToken)) return true;
        return false;
    }

    /// <summary>Checks the ready-placement radius from the same recipe used when the command is applied.</summary>
    public bool IsProducedBuildingInRange(EntityId producer, string action, WorldPosition position, int quarterTurns)
    {
        ThrowIfDisposed();
        if (!TryGetEntity(producer, out var actor) || !IsOnMap(actor)) return false;
        var identity = actor.Get<EntityIdentity>();
        int product = _buildRecipes.FindProduct(identity.Type, action);
        if (product < 0) return false;
        var recipe = _buildRecipes[identity.Type, product];
        long radius = recipe.Build.PlacementRadius;
        return ObjectGeometry.DistanceSquared(TypeOf(actor), actor.Get<SpatialState>().Position,
            _types[recipe.UnitType].WithRotation(quarterTurns), position, out _, out _) <= radius * radius;
    }
}
