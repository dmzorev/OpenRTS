namespace OpenRTS.Core;

public enum ProductionKind { Unit, PlaceBuilding }
public sealed record BuildDefinition
{
    public int Ticks { get; }
    public int Cost { get; }
    public ProductionKind Kind { get; }
    public int PlacementRadius { get; }
    public int InitialHealthPercent { get; }
    public BuildDefinition(int ticks = 100, int cost = 0, ProductionKind kind = ProductionKind.Unit, int placementRadius = 102400, int initialHealthPercent = 20)
    {
        if (ticks is < 1 or > 1000000 || cost is < 0 or > 1000000000 || !Enum.IsDefined(kind) || placementRadius is < 0 or > WorldPosition.MaximumExtent || initialHealthPercent is < 1 or > 100) throw new ArgumentException("Invalid build duration, cost, mode or placement radius.");
        Ticks = ticks; Cost = cost; Kind = kind; PlacementRadius = placementRadius; InitialHealthPercent = initialHealthPercent;
    }
    internal void Write(BinaryWriter w) { w.Write(Ticks); w.Write(Cost); w.Write((int)Kind); w.Write(PlacementRadius); w.Write(InitialHealthPercent); }
}

public sealed record ProductionDefinition(string Id, string Unit, string Queue, BuildDefinition? Override = null, bool OnSite = false);
