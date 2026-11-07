namespace OpenRTS.Core.Simulation;

internal struct MotionState(WorldPosition target)
{
    public WorldPosition Target = target;
    public bool Moving;
    public int MovementRemainder;
}
