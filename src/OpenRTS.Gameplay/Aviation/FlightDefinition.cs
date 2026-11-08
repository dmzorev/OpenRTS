using System.Collections.ObjectModel;
using OpenRTS.Core.Combat;

namespace OpenRTS.Core;

public enum FlightMode { Hover, ForwardFlight }
public enum FlightPhase { Landed, TakingOff, Flying, Hovering, Loitering, Approaching, Landing, Servicing, Taxiing, RunwayTakeoff, RunwayLanding }

public sealed record FlightDefinition
{
    public FlightMode Mode { get; }
    public int Speed { get; }
    public int MinimumSpeed { get; }
    public int Acceleration { get; }
    public int TurnRate { get; }
    public int CruiseAltitude { get; }
    public int VerticalSpeed { get; }
    public int HalfHeight { get; }
    public int Ammo { get; }
    public string ServiceType { get; }
    public TargetCategories Categories { get; }
    public bool NeedsRunway { get; }
    public FlightDefinition(FlightMode mode = FlightMode.Hover, int speed = 160, int minimumSpeed = 80,
        int acceleration = 16, int turnRate = 16, int cruiseAltitude = 1536, int verticalSpeed = 96,
        int halfHeight = 192, int ammo = 0, string serviceType = "aircraft", TargetCategories? categories = null, bool needsRunway = false)
    {
        if (!Enum.IsDefined(mode) || speed is < 1 or > 1024 || minimumSpeed < 1 || minimumSpeed > speed ||
            acceleration is < 1 or > 1024 || turnRate is < 1 or > 128 || cruiseAltitude is < 512 or > 16384 ||
            verticalSpeed is < 1 or > 1024 || halfHeight is < 1 or > 256 || ammo is < 0 or > 100000 ||
            string.IsNullOrWhiteSpace(serviceType) || serviceType.Length > 128 || needsRunway && mode != FlightMode.ForwardFlight)
            throw new ArgumentException("Invalid aircraft speed, altitude, turn rate, ammunition or service settings.");
        Mode = mode; Speed = speed; MinimumSpeed = minimumSpeed; Acceleration = acceleration; TurnRate = turnRate;
        CruiseAltitude = cruiseAltitude; VerticalSpeed = verticalSpeed; HalfHeight = halfHeight; Ammo = ammo;
        ServiceType = serviceType; Categories = categories ?? new(["air"]); NeedsRunway = needsRunway;
    }
    internal void Write(BinaryWriter w)
    {
        w.Write((int)Mode); w.Write(Speed); w.Write(MinimumSpeed); w.Write(Acceleration); w.Write(TurnRate);
        w.Write(CruiseAltitude); w.Write(VerticalSpeed); w.Write(HalfHeight); w.Write(Ammo); w.Write(ServiceType);
        Categories.Write(w); w.Write(NeedsRunway);
    }
}

/// <summary>Offsets are world units relative to the provider. Runway points run from start to end on takeoff.</summary>
public sealed record AirbaseDefinition
{
    public ReadOnlyCollection<WorldPosition> Slots { get; }
    public ReadOnlyCollection<string> Accepts { get; }
    public int ReloadTicks { get; }
    public int RepairPerTick { get; }
    public WorldPosition? RunwayStart { get; }
    public WorldPosition? RunwayEnd { get; }
    public bool KeepsParking { get; }
    public AirbaseDefinition(IEnumerable<WorldPosition> slots, IEnumerable<string>? accepts = null, int reloadTicks = 15,
        int repairPerTick = 2, WorldPosition? runwayStart = null, WorldPosition? runwayEnd = null, bool keepsParking = false)
    {
        var points = slots.ToArray(); var types = (accepts ?? ["aircraft"]).Order(StringComparer.Ordinal).ToArray();
        if (points.Length is < 1 or > 16 || points.Distinct().Count() != points.Length ||
            points.Any(p => Math.Abs((long)p.X) > 8192 || Math.Abs((long)p.Y) > 8192 || p.Elevation != 0) ||
            types.Length == 0 || types.Any(string.IsNullOrWhiteSpace) || types.Distinct(StringComparer.Ordinal).Count() != types.Length ||
            reloadTicks is < 1 or > 100000 || repairPerTick is < 0 or > 10000 ||
            runwayStart.HasValue != runwayEnd.HasValue || runwayStart.HasValue &&
            (runwayStart == runwayEnd || runwayStart.Value.Elevation != 0 || runwayEnd!.Value.Elevation != 0 ||
             Math.Abs((long)runwayStart.Value.X) > 8192 || Math.Abs((long)runwayStart.Value.Y) > 8192 ||
             Math.Abs((long)runwayEnd.Value.X) > 8192 || Math.Abs((long)runwayEnd.Value.Y) > 8192))
            throw new ArgumentException("Invalid airbase slots, services or runway.");
        Slots = Array.AsReadOnly(points); Accepts = Array.AsReadOnly(types); ReloadTicks = reloadTicks;
        RepairPerTick = repairPerTick; RunwayStart = runwayStart; RunwayEnd = runwayEnd; KeepsParking = keepsParking;
    }
    internal void Write(BinaryWriter w)
    {
        w.Write(Slots.Count); foreach (var p in Slots) { w.Write(p.X); w.Write(p.Y); }
        w.Write(Accepts.Count); foreach (var a in Accepts) w.Write(a);
        w.Write(ReloadTicks); w.Write(RepairPerTick); w.Write(KeepsParking); w.Write(RunwayStart.HasValue);
        if (RunwayStart is { } start) { w.Write(start.X); w.Write(start.Y); w.Write(RunwayEnd!.Value.X); w.Write(RunwayEnd.Value.Y); }
    }
}
