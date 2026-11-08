namespace OpenRTS.Core;

public enum SupplyRole { Truck, Mine, Depot }

public sealed record SupplyDefinition
{
    public SupplyRole Role { get; }
    public int Capacity { get; }
    public int InitialAmount { get; }
    public bool RemoveWhenEmpty { get; }

    public SupplyDefinition(SupplyRole role, int capacity = 0, int? initialAmount = null, bool removeWhenEmpty = false)
    {
        if (!Enum.IsDefined(role) || capacity is < 0 or > 1000000 ||
            (role == SupplyRole.Depot ? capacity != 0 : capacity == 0))
            throw new ArgumentException("Supply trucks and mines require a positive capacity. Depots have no capacity.");
        Role = role;
        Capacity = capacity;
        if ((initialAmount.HasValue || removeWhenEmpty) && role != SupplyRole.Mine)
            throw new ArgumentException("Only mines can set a starting stock or disappear when empty.");
        if (initialAmount is { } amount && (amount < 1 || amount > capacity))
            throw new ArgumentOutOfRangeException(nameof(initialAmount));
        InitialAmount = initialAmount ?? capacity;
        RemoveWhenEmpty = removeWhenEmpty;
    }
}

public readonly record struct SupplyPose(int Amount, int Capacity, bool Automatic, EntityId Mine, EntityId Depot,
    EntityId Station);

public readonly record struct SupplySourcePose(EntityId Id, int UnitType, WorldPosition Position, int Amount, int Capacity);
