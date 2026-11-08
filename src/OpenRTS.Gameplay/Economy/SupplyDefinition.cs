namespace OpenRTS.Core;

public enum SupplyRole { Truck, Mine, Depot }

public sealed record SupplyDefinition
{
    public SupplyRole Role { get; }
    public int Capacity { get; }

    public SupplyDefinition(SupplyRole role, int capacity = 0)
    {
        if (!Enum.IsDefined(role) || capacity is < 0 or > 1000000 ||
            (role == SupplyRole.Depot ? capacity != 0 : capacity == 0))
            throw new ArgumentException("Supply trucks and mines require a positive capacity. Depots have no capacity.");
        Role = role;
        Capacity = capacity;
    }
}

public readonly record struct SupplyPose(int Amount, int Capacity, bool Automatic, EntityId Mine, EntityId Depot,
    EntityId Station, uint ProjectOwner, int ProjectWork, int ProjectRequired);
