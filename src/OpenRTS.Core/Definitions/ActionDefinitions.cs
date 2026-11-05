using System.Collections.ObjectModel;

namespace OpenRTS.Core;

public enum AbilityKind { Weapon, Capture }
public sealed record AbilityDefinition(string Id, string Mount, bool GroundTarget = false, bool AutoCast = false, AbilityKind Kind = AbilityKind.Weapon, int Range = 1024);
public enum ProductionKind { Unit, PlaceBuilding }
public enum PaymentPolicy { Upfront, Progressive, OnCompletion }
public sealed record BuildDefinition
{
    public int Ticks { get; }
    public int Cost { get; }
    public ProductionKind Kind { get; }
    public int PlacementRadius { get; }
    public BuildDefinition(int ticks = 100, int cost = 0, ProductionKind kind = ProductionKind.Unit, int placementRadius = 102400)
    {
        if (ticks is < 1 or > 1000000 || cost is < 0 or > 1000000000 || !Enum.IsDefined(kind) || placementRadius is < 0 or > WorldPosition.MaximumExtent) throw new ArgumentException("Invalid build duration, cost, mode or placement radius.");
        Ticks = ticks; Cost = cost; Kind = kind; PlacementRadius = placementRadius;
    }
    internal void Write(BinaryWriter w) { w.Write(Ticks); w.Write(Cost); w.Write((int)Kind); w.Write(PlacementRadius); }
}
public sealed record TaskQueueDefinition
{
    public string Id { get; }
    public int Capacity { get; }
    public PaymentPolicy Payment { get; }
    public WorldPosition Exit { get; }
    public int ExitSearchRadius { get; }
    public string? WorkingState { get; }
    public string? ExitAnimation { get; }
    public int ExitAnimationTicks { get; }
    public TaskQueueDefinition(string id, int capacity = 20, PaymentPolicy payment = PaymentPolicy.Upfront, WorldPosition exit = default, int exitSearchRadius = 2048, string? workingState = null, string? exitAnimation = null, int exitAnimationTicks = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (capacity is < 1 or > 256 || !Enum.IsDefined(payment) || Math.Abs((long)exit.X) > 65536 || Math.Abs((long)exit.Y) > 65536 || exitSearchRadius is < 0 or > 8192 || exitAnimationTicks is < 1 or > 1000000) throw new ArgumentException("Invalid task queue settings.");
        Id = id; Capacity = capacity; Payment = payment; Exit = exit; ExitSearchRadius = exitSearchRadius; WorkingState = workingState; ExitAnimation = exitAnimation; ExitAnimationTicks = exitAnimationTicks;
    }
}
public sealed record ProductionDefinition(string Id, string Unit, string Queue, BuildDefinition? Override = null);
public readonly record struct TaskPose(EntityId Producer, int Queue, long Id, int Product, int UnitType, int Progress, int Duration, int Cost, bool Repeat, bool Ready);
