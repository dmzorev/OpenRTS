namespace OpenRTS.Core.Navigation;

/// <summary>Physical adapter for the current full-cell terrain, not a route cost or occupancy map.</summary>
internal static class TerrainGeometry
{
    internal static bool Clear(NavigationGrid grid, WorldPosition from, WorldPosition to, int radius, Locomotor? locomotor = null) =>
        Clear(grid, from, to, radius == 0 ? BodyShape.None : BodyShape.Circle(radius), locomotor);
    internal static bool Clear(NavigationGrid grid, WorldPosition from, WorldPosition to, BodyShape body, Locomotor? locomotor = null)
    {
        int width = grid.Width * grid.CellSize, height = grid.Height * grid.CellSize;
        if (Math.Min(from.X, to.X) < body.HalfWidth || Math.Min(from.Y, to.Y) < body.HalfHeight ||
            Math.Max(from.X, to.X) > width - body.HalfWidth || Math.Max(from.Y, to.Y) > height - body.HalfHeight) return false;
        int size = grid.CellSize, reachX = (body.HalfWidth + size - 1) / size, reachY = (body.HalfHeight + size - 1) / size;
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
            if (locomotor is not null && Math.Abs((long)grid.SurfaceAt(current).Elevation - grid.SurfaceAt(previous).Elevation) > locomotor.MaximumStep) return false;
            previous = current;
            for (int cy = Math.Max(0, y - reachY); cy <= Math.Min(grid.Height - 1, y + reachY); cy++)
                for (int cx = Math.Max(0, x - reachX); cx <= Math.Min(grid.Width - 1, x + reachX); cx++)
                {
                    int cell = cy * grid.Width + cx;
                    if ((grid.IsBlocked(cell) || locomotor is not null && (!locomotor.At(grid, cell).Traverse || Math.Abs((long)grid.SurfaceAt(cell).Elevation - grid.SurfaceAt(current).Elevation) > locomotor.MaximumStep)) &&
                        BodyGeometry.SweptBox(from, to, new(cx * size, cy * size, (cx + 1) * size, (cy + 1) * size), body)) return false;
                    foreach (int obstacle in grid.ObstaclesAt(cell)) if (BodyGeometry.SweptBox(from, to, grid.Obstacle(obstacle), body)) return false;
                }
            if (x == endX && y == endY) return true;
            if (x != endX && (y == endY || tx * ay <= ty * ax)) { x += sx; tx += size; }
            else { y += sy; ty += size; }
        }
    }

    internal static bool CanStop(NavigationGrid grid, WorldPosition point, BodyShape body, Locomotor profile)
    {
        if (!Clear(grid, point, point, body, profile)) return false;
        int size = grid.CellSize;
        for (int y = Math.Max(0, (point.Y - body.HalfHeight) / size); y <= Math.Min(grid.Height - 1, (point.Y + body.HalfHeight) / size); y++)
            for (int x = Math.Max(0, (point.X - body.HalfWidth) / size); x <= Math.Min(grid.Width - 1, (point.X + body.HalfWidth) / size); x++)
                if (!profile.At(grid, y * grid.Width + x).CanStop && BodyGeometry.SweptBox(point, point, new(x * size, y * size, (x + 1) * size, (y + 1) * size), body)) return false;
        return true;
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
