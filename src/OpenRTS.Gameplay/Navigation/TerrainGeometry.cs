namespace OpenRTS.Core.Navigation;

/// <summary>Physical adapter for the current full-cell terrain, not a route cost or occupancy map.</summary>
internal static class TerrainGeometry
{
    internal static bool Clear(NavigationGrid grid, WorldPosition from, WorldPosition to, int radius, Locomotor? locomotor = null) =>
        Clear(grid, from, to, radius == 0 ? BodyShape.None : BodyShape.Circle(radius), locomotor);
    internal static bool Clear(NavigationGrid grid, WorldPosition from, WorldPosition to, BodyShape body, Locomotor? locomotor = null)
    {
        int width = grid.Width * grid.CellSize, height = grid.Height * grid.CellSize;
        int minX = Math.Min(from.X, to.X), minY = Math.Min(from.Y, to.Y);
        int maxX = Math.Max(from.X, to.X), maxY = Math.Max(from.Y, to.Y);
        if (minX < body.HalfWidth || minY < body.HalfHeight ||
            maxX > width - body.HalfWidth || maxY > height - body.HalfHeight) return false;
        int size = grid.CellSize;
        bool graded = (grid.HasSlopedSurface || grid.HasSurfaceCuts) && locomotor is not null;
        var sweep = new WorldBox(minX - body.HalfWidth, minY - body.HalfHeight, maxX + body.HalfWidth, maxY + body.HalfHeight);
        // Include edges owned by the cell just beyond a tangent on a grid line.
        int reachX = (body.HalfWidth + size - (graded ? 0 : 1)) / size;
        int reachY = (body.HalfHeight + size - (graded ? 0 : 1)) / size;
        int x = Math.Min(from.X / size, grid.Width - 1), y = Math.Min(from.Y / size, grid.Height - 1);
        int endX = Math.Min(to.X / size, grid.Width - 1), endY = Math.Min(to.Y / size, grid.Height - 1);
        long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long ax = Math.Abs(dx), ay = Math.Abs(dy);
        long tx = sx > 0 ? (long)(x + 1) * size - from.X : from.X - (long)x * size;
        long ty = sy > 0 ? (long)(y + 1) * size - from.Y : from.Y - (long)y * size;
        int previous = y * grid.Width + x;
        while (true)
        {
            int current = y * grid.Width + x;
            if (!graded && locomotor is not null && Math.Abs((long)grid.SurfaceAt(current).Elevation - grid.SurfaceAt(previous).Elevation) > locomotor.MaximumStep) return false;
            previous = current;
            for (int cy = Math.Max(0, y - reachY); cy <= Math.Min(grid.Height - 1, y + reachY); cy++)
                for (int cx = Math.Max(0, x - reachX); cx <= Math.Min(grid.Width - 1, x + reachX); cx++)
                {
                    int cell = cy * grid.Width + cx;
                    var bounds = new WorldBox(cx * size, cy * size, (cx + 1) * size, (cy + 1) * size);
                    if ((grid.IsBlocked(cell) || locomotor is not null && !graded &&
                        Math.Abs((long)grid.SurfaceAt(cell).Elevation - grid.SurfaceAt(current).Elevation) > locomotor.MaximumStep) &&
                        BodyGeometry.SweptBox(from, to, bounds, body)) return false;
                    if (locomotor is not null && TouchesRestrictedTerrain(grid, cell, bounds, from, to, body, locomotor, stopping: false))
                        return false;
                    // A tangent still touches the cliff, including edges owned by neighboring cells.
                    if (graded && sweep.MinX <= bounds.MaxX && sweep.MaxX >= bounds.MinX &&
                        sweep.MinY <= bounds.MaxY && sweep.MaxY >= bounds.MinY &&
                        TouchesCliff(grid, cx, cy, cell, bounds, sweep, locomotor!.MaximumStep, from, to, body)) return false;
                    foreach (int obstacle in grid.ObstaclesAt(cell)) if (BodyGeometry.SweptBox(from, to, grid.Obstacle(obstacle), body)) return false;
                }
            if (x == endX && y == endY) return true;
            if (x != endX && (y == endY || tx * ay <= ty * ax)) { x += sx; tx += size; }
            else { y += sy; ty += size; }
        }
    }

    private static bool TouchesCliff(NavigationGrid grid, int x, int y, int cell, WorldBox bounds, WorldBox sweep,
        int maximumStep, WorldPosition from, WorldPosition to, BodyShape body)
    {
        int size = grid.CellSize, left = bounds.MinX, top = bounds.MinY, right = bounds.MaxX, bottom = bounds.MaxY;
        var cut = grid.SurfaceCutAt(cell);
        if (cut != default && cut.UpperElevation - grid.SurfaceAt(cell).Elevation > maximumStep &&
            BodyGeometry.SweptSegment(from, to, new(left, cut.DescendingDiagonal ? top : bottom),
                new(right, cut.DescendingDiagonal ? bottom : top), body)) return true;

        // A flat edge can meet a ramp at the same height. Test the shared edge, keeping the full body clear of a cliff.
        return sweep.MaxX >= right && x + 1 < grid.Width && Blocks(cell + 1, right, top, right, bottom) ||
            sweep.MaxY >= bottom && y + 1 < grid.Height && Blocks(cell + grid.Width, left, bottom, right, bottom);

        bool Blocks(int neighbor, int x0, int y0, int x1, int y1)
        {
            int start = grid.SurfaceElevationAtEdge(cell, x0, y0, x1, y1) - grid.SurfaceElevationAtEdge(neighbor, x0, y0, x1, y1);
            int end = grid.SurfaceElevationAtEdge(cell, x1, y1, x0, y0) - grid.SurfaceElevationAtEdge(neighbor, x1, y1, x0, y0);
            return BlocksAbove(start, end) || BlocksAbove(-start, -end);

            bool BlocksAbove(int a, int b)
            {
                if (a <= maximumStep && b <= maximumStep) return false;
                int low = 0, high = size;
                // Round toward the traversable part to keep clearance at fractional wall endpoints.
                if (a <= maximumStep) low = (int)((long)size * (maximumStep - a) / (b - a));
                else if (b <= maximumStep) high = size - (int)((long)size * (maximumStep - b) / (a - b));
                return BodyGeometry.SweptBox(from, to, x0 == x1
                    ? new(x0, y0 + low, x1, y0 + high)
                    : new(x0 + low, y0, x0 + high, y1), body);
            }
        }
    }

    internal static bool CanStop(NavigationGrid grid, WorldPosition point, BodyShape body, Locomotor profile)
    {
        if (!Clear(grid, point, point, body, profile)) return false;
        int size = grid.CellSize;
        int tangent = grid.HasSplitTerrain ? 1 : 0;
        for (int y = Math.Max(0, (point.Y - body.HalfHeight - tangent) / size); y <= Math.Min(grid.Height - 1, (point.Y + body.HalfHeight) / size); y++)
            for (int x = Math.Max(0, (point.X - body.HalfWidth - tangent) / size); x <= Math.Min(grid.Width - 1, (point.X + body.HalfWidth) / size); x++)
                if (TouchesRestrictedTerrain(grid, y * grid.Width + x, new(x * size, y * size, (x + 1) * size, (y + 1) * size),
                    point, point, body, profile, stopping: true)) return false;
        return true;
    }

    private static bool TouchesRestrictedTerrain(NavigationGrid grid, int cell, WorldBox bounds, WorldPosition from,
        WorldPosition to, BodyShape body, Locomotor profile, bool stopping)
    {
        var lower = profile.At(grid, cell);
        bool lowerAllowed = stopping ? lower.CanStop : lower.Traverse;
        if (!grid.HasSplitTerrain || grid.SurfaceCutAt(cell) is not { UpperTerrain: not null } cut)
            return !lowerAllowed && BodyGeometry.SweptBox(from, to, bounds, body);
        var upper = profile.At(grid, cell, upper: true);
        bool upperAllowed = stopping ? upper.CanStop : upper.Traverse;
        if (lowerAllowed == upperAllowed) return !lowerAllowed && BodyGeometry.SweptBox(from, to, bounds, body);
        if (!BodyGeometry.SweptBox(from, to, bounds, body)) return false;
        // The two right angles lie at opposite corners.
        var forbidden = !upperAllowed ? cut.UpperCorner : (SurfaceCorner)(((int)cut.UpperCorner + 1) % 4 + 1);
        return BodyGeometry.SweptTriangle(from, to, bounds, forbidden, body);
    }

    internal static bool IntersectsBox(WorldPosition a, WorldPosition b, int minX, int minY, int maxX, int maxY)
    {
        if (Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX ||
            Math.Max(a.Y, b.Y) < minY || Math.Min(a.Y, b.Y) > maxY) return false;
        if (Inside(a) || Inside(b)) return true;
        return Crosses(new(minX, minY), new(maxX, minY)) || Crosses(new(maxX, minY), new(maxX, maxY)) ||
            Crosses(new(maxX, maxY), new(minX, maxY)) || Crosses(new(minX, maxY), new(minX, minY));
        bool Inside(WorldPosition p) => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        bool Crosses(WorldPosition c, WorldPosition d)
        {
            long abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
            return Math.Sign(abC) * Math.Sign(abD) <= 0 && Math.Sign(cdA) * Math.Sign(cdB) <= 0;
        }
    }

    private static long Cross(WorldPosition a, WorldPosition b, WorldPosition c)
        => ((long)b.X - a.X) * ((long)c.Y - a.Y) - ((long)b.Y - a.Y) * ((long)c.X - a.X);
}
