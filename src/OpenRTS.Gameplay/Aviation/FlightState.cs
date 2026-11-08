namespace OpenRTS.Core;

internal sealed class FlightState
{
    internal FlightPhase Phase;
    internal int Heading, Speed, Ammo, ReloadWork;
    internal EntityId Base;
    internal int Slot = -1;
    internal bool ReturnRequested, LandRequested, MovementRequested;
    internal bool RunwayReserved, TaxiToParking;
    internal WorldPosition LandingPoint, ExitPoint;
    internal long RequestTick, RetryTick;
    internal void Write(BinaryWriter w)
    {
        w.Write((int)Phase); w.Write(Heading); w.Write(Speed); w.Write(Ammo); w.Write(ReloadWork);
        w.Write(Base.Value); w.Write(Slot); w.Write(ReturnRequested); w.Write(LandRequested);
        w.Write(LandingPoint.X); w.Write(LandingPoint.Y); w.Write(LandingPoint.Elevation);
        w.Write(ExitPoint.X); w.Write(ExitPoint.Y); w.Write(ExitPoint.Elevation);
        w.Write(RequestTick); w.Write(RetryTick); w.Write(RunwayReserved); w.Write(TaxiToParking);
    }
}

public readonly record struct FlightSnapshot(FlightPhase Phase, int Ammo, int Capacity, EntityId Airbase, int Slot, bool Airborne = false);
