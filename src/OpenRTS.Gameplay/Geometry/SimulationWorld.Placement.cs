using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    internal bool TrySpawnAtExit(EntityId source, int unitType, int radius, out EntityId created)
    {
        ValidateType(unitType);
        if (!_types[unitType].Movable) throw new ArgumentException("Exit spawning requires a mobile unit.", nameof(unitType));
        if (radius is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(radius));
        created = default;
        if (!TryGetEntity(source, out var actor) || !IsOnMap(actor) || IsConstructing(actor)) return false;
        uint owner = actor.Get<OwnershipState>().Owner;
        if (!TryFindExitPlacement(unitType, actor.Get<SpatialState>().Position, radius, out var exit, TypeOf(actor))) return false;
        created = SpawnFromRule(unitType, exit, owner);
        return true;
    }

    private EntityId SpawnFromRule(int unitType, WorldPosition position, uint owner)
    {
        var id = Spawn(unitType, position, owner);
        EmitEvent(new(Tick, WorldEventKind.Spawned, id, position, unitType, Owner: owner));
        return id;
    }

    private bool TryFindExitPlacement(int type, WorldPosition exit, int radius, out WorldPosition point, UnitDefinition? origin = null)
    {
        point = default;
        long best = long.MaxValue;
        var definition = _types[type];
        var body = definition.Body;
        var index = _motion ?? _combatIndex;
        int first = -(radius / 256) * 256;
        long radiusSquared = (long)radius * radius;

        // shortcut: bounded 256-unit lattice for exits; add authored lanes when dense factories need exact packing.
        for (int y = first; y <= radius; y += 256)
            for (int x = first; x <= radius; x += 256)
            {
                long distance = (long)x * x + (long)y * y;
                if (distance > radiusSquared || distance >= best) continue;
                var candidate = new WorldPosition(exit.X + x, exit.Y + y, exit.Elevation);
                if (candidate.X < body.HalfWidth || candidate.X > Width - body.HalfWidth ||
                    candidate.Y < body.HalfHeight || candidate.Y > Depth - body.HalfHeight || !StopAllowed(type, candidate)) continue;
                if (origin is not null && ObjectGeometry.DistanceSquared(definition, candidate, origin, exit, out _, out _) == 0) continue;
                if (index is not null && !index.ClearPlacement(candidate, out _, body)) continue;
                best = distance;
                point = GroundPosition(candidate, exit.Elevation);
            }

        return best != long.MaxValue;
    }
}
