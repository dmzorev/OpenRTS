namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
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
