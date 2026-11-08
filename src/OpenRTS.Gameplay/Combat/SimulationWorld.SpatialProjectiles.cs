using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private static long ProjectileDistanceSquared(WorldPosition a, WorldPosition b, bool spatial)
    {
        long z = spatial ? (long)a.Elevation - b.Elevation : 0;
        return DistanceSquared(a, b) + z * z;
    }
    private static WorldPosition SpatialStep(WorldPosition a, WorldPosition b, int speed)
    {
        long length = (long)SquareRoot((ulong)ProjectileDistanceSquared(a, b, true));
        if (length <= speed) return b;
        return new(a.X + (int)(((long)b.X - a.X) * speed / length), a.Y + (int)(((long)b.Y - a.Y) * speed / length),
            a.Elevation + (int)(((long)b.Elevation - a.Elevation) * speed / length));
    }
    private static WorldPosition SpatialInterpolate(WorldPosition a, WorldPosition b, int t) =>
        new(a.X + (int)(((long)b.X - a.X) * t / 65536), a.Y + (int)(((long)b.Y - a.Y) * t / 65536),
            a.Elevation + (int)(((long)b.Elevation - a.Elevation) * t / 65536));

    private static bool ClipAxis(long value, long delta, long minimum, long maximum, ref int low, ref int high)
    {
        if (delta == 0) return value >= minimum && value <= maximum;
        long a = (minimum - value) * 65536, b = (maximum - value) * 65536;
        if (delta < 0) { (a, b) = (-b, -a); delta = -delta; }
        static long Floor(long n, long d) => n >= 0 ? n / d : -((-n + d - 1) / d);
        long entry = -Floor(-a, delta), exit = Floor(b, delta);
        if (entry > high || exit < low) return false;
        low = (int)Math.Max(low, entry); high = (int)Math.Min(high, exit);
        return low <= high;
    }

    private static int SpatialBoxHit(WorldPosition a, WorldPosition b, WorldBox box, int minimumZ, int maximumZ)
    {
        int low = 0, high = 65536;
        return ClipAxis(a.X, (long)b.X - a.X, box.MinX, box.MaxX, ref low, ref high) &&
            ClipAxis(a.Y, (long)b.Y - a.Y, box.MinY, box.MaxY, ref low, ref high) &&
            ClipAxis(a.Elevation, (long)b.Elevation - a.Elevation, minimumZ, maximumZ, ref low, ref high) ? low : 65537;
    }

    private static int SpatialHitFraction(WorldPosition a, WorldPosition b, WorldPosition previous, WorldPosition current, UnitDefinition type)
    {
        int height = type.Flight?.HalfHeight ?? 256;
        var from = new WorldPosition(a.X - previous.X, a.Y - previous.Y, a.Elevation - previous.Elevation);
        var to = new WorldPosition(b.X - current.X, b.Y - current.Y, b.Elevation - current.Elevation);
        if (type.Footprint is { } footprint)
        {
            int first = 65537;
            foreach (var bounds in footprint.TargetBounds) first = Math.Min(first, SpatialBoxHit(from, to, bounds, -height, height));
            return first;
        }
        if (type.Body.Kind == BodyKind.Box) return SpatialBoxHit(from, to, ObjectGeometry.Box(type.Body, default), -height, height);
        int low = HitFraction(from, to, default, default, type.Body.HalfWidth);
        if (low > 65536) return low;
        int high = 65536 - HitFraction(to, from, default, default, type.Body.HalfWidth);
        return ClipAxis(from.Elevation, (long)to.Elevation - from.Elevation, -height, height, ref low, ref high) ? low : 65537;
    }

    private bool SpatialTerrainClear(WorldPosition from, WorldPosition to)
    {
        if (_baseGrid is not { } grid) return true;
        int size = grid.CellSize;
        if (Math.Min(from.X, to.X) < 0 || Math.Min(from.Y, to.Y) < 0 || Math.Max(from.X, to.X) >= Width || Math.Max(from.Y, to.Y) >= Depth) return false;
        int x = from.X / size, y = from.Y / size, ex = to.X / size, ey = to.Y / size;
        long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long ax = Math.Abs(dx), ay = Math.Abs(dy);
        long tx = sx > 0 ? (long)(x + 1) * size - from.X : from.X - (long)x * size;
        long ty = sy > 0 ? (long)(y + 1) * size - from.Y : from.Y - (long)y * size;
        while (true)
        {
            if (!ClearCell(x, y)) return false;
            if (x == ex && y == ey) return true;
            if (x != ex && y != ey && tx * ay == ty * ax && !ClearCell(x, y + sy)) return false;
            if (x != ex && (y == ey || tx * ay <= ty * ax)) { x += sx; tx += size; }
            else { y += sy; ty += size; }
        }
        bool ClearCell(int cx, int cy)
        {
            int cell = cy * grid.Width + cx;
            var bounds = new WorldBox(cx * size, cy * size, (cx + 1) * size, (cy + 1) * size);
            int surface = grid.SurfaceAt(cell).Elevation;
            // shortcut: binary terrain blockers have a 512-unit height; author obstacle heights when a game needs tall air obstacles.
            int top = surface + (grid.IsBlocked(cell) ? 512 : -1);
            if (SpatialBoxHit(from, to, bounds, -WorldPosition.MaximumExtent, top) <= 65536) return false;
            foreach (int obstacle in grid.ObstaclesAt(cell))
                if (SpatialBoxHit(from, to, grid.Obstacle(obstacle), surface, surface + 512) <= 65536) return false;
            return true;
        }
    }
}
