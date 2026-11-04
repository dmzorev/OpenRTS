namespace OpenRTS.Core.Navigation;

internal static class TerrainCost
{
    // Doubled integral of the centerline's price. Q32 intersections avoid floating-point route decisions.
    internal static long Segment(NavigationGrid grid, Locomotor profile, WorldPosition a, WorldPosition b)
    {
        long dx = (long)b.X - a.X, dy = (long)b.Y - a.Y;
        if (dx == 0 && dy == 0) return 0;
        long length = (long)SimulationWorld.SquareRoot((ulong)(dx * dx + dy * dy));
        if (length * length < dx * dx + dy * dy) length++;
        const long one = 1L << 32;
        int size = grid.CellSize, x = Math.Min(a.X / size, grid.Width - 1), y = Math.Min(a.Y / size, grid.Height - 1);
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long t = 0; Int128 sum = 0;
        while (t < one)
        {
            long tx = dx == 0 ? one : ((sx > 0 ? (long)(x + 1) * size - a.X : (long)a.X - x * size) * one / Math.Abs(dx));
            long ty = dy == 0 ? one : ((sy > 0 ? (long)(y + 1) * size - a.Y : (long)a.Y - y * size) * one / Math.Abs(dy));
            long next = Math.Min(one, Math.Min(tx, ty));
            var rule = profile.At(grid, y * grid.Width + x);
            if (grid.IsBlocked(y * grid.Width + x) || !rule.Traverse) return long.MaxValue;
            sum += (Int128)(next - t) * length * rule.Cost * 2;
            if (next == one) break;
            if (tx == next) x += sx;
            if (ty == next) y += sy;
            t = next;
        }
        return checked((long)((sum + one - 1) / one));
    }

    internal static long Edge(NavigationGrid grid, Locomotor profile, int a, int b)
        => profile.At(grid, a).Cost + profile.At(grid, b).Cost;
}
