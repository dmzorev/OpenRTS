namespace OpenRTS.Core.Simulation;

public struct SpatialState(WorldPosition position)
{
    public WorldPosition Previous = position;
    public WorldPosition Position = position;
}
