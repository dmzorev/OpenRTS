namespace OpenRTS.Core.Navigation;

internal struct NavigationAgentState(int goalCell, bool hasNavigation)
{
    public bool TerrainStranded, GroupMember;
    public int GoalCell = goalCell;
    public long OrderGroup;
    public int WaitTicks, PathLength, PathCursor;
    public EntityId YieldFor;
    public long YieldDeadline, LastProgressTick;
    public WorldPosition LocalTarget;
    public NavigationRoute? Route;
    public WorldPosition[]? LocalPath = hasNavigation ? new WorldPosition[LocalMotionSearch.PathCapacity] : null;
}
