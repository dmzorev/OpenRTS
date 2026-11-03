namespace OpenRTS.Core;

/// <summary>Immutable gameplay definition; presentation and mutable entity state live elsewhere.</summary>
public sealed record UnitDefinition
{
    public string Id { get; }
    public int MaxHealth { get; }

    public UnitDefinition(string id, int maxHealth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHealth);
        Id = id;
        MaxHealth = maxHealth;
    }
}
