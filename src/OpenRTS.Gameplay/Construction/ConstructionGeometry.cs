namespace OpenRTS.Core.Construction;

internal static class ConstructionGeometry
{
    internal static bool Overlaps(FootprintDefinition first, WorldPosition a, FootprintDefinition second, WorldPosition b)
    {
        if (Math.Abs(a.X - b.X) >= first.HalfWidth + second.HalfWidth ||
            Math.Abs(a.Y - b.Y) >= first.HalfHeight + second.HalfHeight) return false;
        foreach (var firstCell in first.Cells)
        {
            var left = first.Bounds(firstCell, a);
            foreach (var secondCell in second.Cells)
            {
                var right = second.Bounds(secondCell, b);
                if (left.MinX < right.MaxX && right.MinX < left.MaxX && left.MinY < right.MaxY && right.MinY < left.MaxY) return true;
            }
        }
        return false;
    }

    internal static bool Touches(FootprintDefinition footprint, WorldPosition site, WorldPosition from, WorldPosition to, BodyShape body)
    {
        var bounds = new WorldBox(site.X - footprint.HalfWidth, site.Y - footprint.HalfHeight, site.X + footprint.HalfWidth, site.Y + footprint.HalfHeight);
        if (!BodyGeometry.SweptBox(from, to, bounds, body)) return false;
        foreach (var cell in footprint.Cells)
            if (BodyGeometry.SweptBox(from, to, footprint.Bounds(cell, site), body)) return true;
        return false;
    }

    internal static IEnumerable<WorldPosition> Exits(FootprintDefinition footprint, WorldPosition site, WorldPosition from, BodyShape body, int width, int depth)
        => Exits(footprint.HalfWidth, footprint.HalfHeight, site, from, body, width, depth);

    internal static IEnumerable<WorldPosition> Exits(int halfWidth, int halfHeight, WorldPosition site, WorldPosition from, BodyShape body, int width, int depth)
    {
        int left = site.X - halfWidth - body.HalfWidth - 1;
        int right = site.X + halfWidth + body.HalfWidth + 1;
        int top = site.Y - halfHeight - body.HalfHeight - 1;
        int bottom = site.Y + halfHeight + body.HalfHeight + 1;
        int x = Math.Clamp(from.X, left, right), y = Math.Clamp(from.Y, top, bottom);
        WorldPosition[] candidates = [new(left, y), new(right, y), new(x, top), new(x, bottom)];
        return candidates.Where(p => p.X >= body.HalfWidth && p.Y >= body.HalfHeight &&
            p.X <= width - body.HalfWidth && p.Y <= depth - body.HalfHeight);
    }
}
