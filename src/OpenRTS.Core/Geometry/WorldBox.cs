namespace OpenRTS.Core.Navigation;

public readonly record struct WorldBox(int MinX, int MinY, int MaxX, int MaxY)
{
    public WorldPosition Closest(WorldPosition point) => new(Math.Clamp(point.X, MinX, MaxX), Math.Clamp(point.Y, MinY, MaxY), point.Elevation);
    public long DistanceSquared(WorldPosition point)
    { var p = Closest(point); long x = (long)point.X - p.X, y = (long)point.Y - p.Y; return x * x + y * y; }
    public WorldBox Translate(WorldPosition point) => new(checked(MinX + point.X), checked(MinY + point.Y), checked(MaxX + point.X), checked(MaxY + point.Y));
}
