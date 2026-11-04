namespace OpenRTS.Core.Navigation;

/// <summary>Physical adapter for the current full-cell terrain, not a route cost or occupancy map.</summary>
internal static class TerrainGeometry
{
    internal static bool Clear(NavigationGrid grid, WorldPosition from, WorldPosition to, int radius, Locomotor? locomotor = null)
    {
        int width = grid.Width * grid.CellSize, height = grid.Height * grid.CellSize;
        if (Math.Min(from.X, to.X) < radius || Math.Min(from.Y, to.Y) < radius ||
            Math.Max(from.X, to.X) > width - radius || Math.Max(from.Y, to.Y) > height - radius) return false;
        int size = grid.CellSize, reach = (radius + size - 1) / size;
        int x = Math.Min(from.X / size, grid.Width - 1), y = Math.Min(from.Y / size, grid.Height - 1);
        int endX = Math.Min(to.X / size, grid.Width - 1), endY = Math.Min(to.Y / size, grid.Height - 1);
        long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long ax = Math.Abs(dx), ay = Math.Abs(dy);
        long tx = sx > 0 ? (long)(x + 1) * size - from.X : from.X - (long)x * size;
        long ty = sy > 0 ? (long)(y + 1) * size - from.Y : from.Y - (long)y * size;
        while (true)
        {
            for (int cy = Math.Max(0, y - reach); cy <= Math.Min(grid.Height - 1, y + reach); cy++)
                for (int cx = Math.Max(0, x - reach); cx <= Math.Min(grid.Width - 1, x + reach); cx++)
                    if ((grid.IsBlocked(cy * grid.Width + cx) || locomotor is not null && !locomotor.At(grid, cy * grid.Width + cx).Traverse) && IntersectsBox(from, to,
                        cx * size - radius, cy * size - radius, (cx + 1) * size + radius, (cy + 1) * size + radius)) return false;
            if (x == endX && y == endY) return true;
            if (x != endX && (y == endY || tx * ay <= ty * ax)) { x += sx; tx += size; }
            else { y += sy; ty += size; }
        }
    }

    // Conservative square dilation of terrain. Rounded obstacle corners can be introduced with shape geometry.
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
