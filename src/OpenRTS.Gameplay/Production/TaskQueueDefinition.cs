namespace OpenRTS.Core;

public enum PaymentPolicy { Upfront, Progressive, OnCompletion }
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

public readonly record struct TaskPose(EntityId Producer, int Queue, long Id, int Product, int UnitType, int Progress, int Duration, int Cost, bool Repeat, bool Ready);
