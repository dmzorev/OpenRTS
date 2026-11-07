namespace OpenRTS.Core.Navigation;

/// <summary>Target geometry is a union of footprint cells, independent of their movement permissions.</summary>
public static class ObjectGeometry
{
    public static WorldPosition ClosestPoint(UnitDefinition type, WorldPosition position, WorldPosition from)
    {
        if (type.Footprint is not { } footprint) return BodyGeometry.ClosestPoint(position, type.Body, from);
        var closest = position; long distance = long.MaxValue;
        for (int i = 0; i < footprint.TargetBounds.Count; i++)
        {
            var candidate = footprint.TargetBounds[i].Translate(position).Closest(from);
            long squared = Squared(candidate, from);
            if (squared < distance) { closest = candidate; distance = squared; }
        }
        return closest;
    }
    public static bool WithinRadius(UnitDefinition type, WorldPosition position, WorldPosition center, int radius)
    {
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (type.Footprint is { } footprint)
        {
            for (int i = 0; i < footprint.TargetBounds.Count; i++) if (footprint.TargetBounds[i].Translate(position).DistanceSquared(center) <= (long)radius * radius) return true;
            return false;
        }
        if (type.Body.Kind == BodyKind.Box) return Box(type.Body, position).DistanceSquared(center) <= (long)radius * radius;
        long reach = (long)radius + type.Body.HalfWidth; return Squared(position, center) <= reach * reach;
    }
    public static long DistanceSquared(UnitDefinition a, WorldPosition ap, UnitDefinition b, WorldPosition bp, out WorldPosition from, out WorldPosition to)
    {
        from = ap; to = bp; long best = long.MaxValue;
        int ac = a.Footprint?.TargetBounds.Count ?? 1, bc = b.Footprint?.TargetBounds.Count ?? 1;
        for (int ai = 0; ai < ac; ai++) for (int bi = 0; bi < bc; bi++)
        {
            bool aBox = a.Footprint is not null || a.Body.Kind == BodyKind.Box;
            bool bBox = b.Footprint is not null || b.Body.Kind == BodyKind.Box;
            WorldBox ab = a.Footprint is { } af ? af.TargetBounds[ai].Translate(ap) : Box(a.Body, ap);
            WorldBox bb = b.Footprint is { } bf ? bf.TargetBounds[bi].Translate(bp) : Box(b.Body, bp);
            WorldPosition x, y;
            if (aBox && bBox)
            { x = ab.Closest(new((bb.MinX + bb.MaxX) / 2, (bb.MinY + bb.MaxY) / 2)); y = bb.Closest(x); x = ab.Closest(y); }
            else if (aBox) { x = ab.Closest(bp); y = BodyGeometry.ClosestPoint(bp, b.Body, x); }
            else if (bBox) { y = bb.Closest(ap); x = BodyGeometry.ClosestPoint(ap, a.Body, y); }
            else { y = BodyGeometry.ClosestPoint(bp, b.Body, ap); x = BodyGeometry.ClosestPoint(ap, a.Body, y); }
            long d = Squared(x, y); if (d >= best) continue;
            best = d; from = x with { Elevation = ap.Elevation }; to = y with { Elevation = bp.Elevation };
        }
        return best;
    }
    internal static WorldBox Box(BodyShape shape, WorldPosition position) => new(position.X - shape.HalfWidth, position.Y - shape.HalfHeight, position.X + shape.HalfWidth, position.Y + shape.HalfHeight);
    internal static long Squared(WorldPosition a, WorldPosition b) { long x = (long)a.X - b.X, y = (long)a.Y - b.Y; return x * x + y * y; }
}
