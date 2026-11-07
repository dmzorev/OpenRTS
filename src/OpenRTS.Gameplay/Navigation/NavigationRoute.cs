namespace OpenRTS.Core.Navigation;

internal sealed class NavigationRoute(WorldPosition[] points, int revision)
{
    public readonly WorldPosition[] Points = points;
    public readonly int Revision = revision;
    public int Cursor;
    public WorldPosition Waypoint;
    public bool HasWaypoint;
    public long[]? CostPrefix;
}
