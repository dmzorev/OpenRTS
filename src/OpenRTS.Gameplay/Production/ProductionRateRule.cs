namespace OpenRTS.Core.Production;

/// <summary>Half-ticks of queue work contributed by one simulation tick.</summary>
public enum ProductionRate : byte { Paused = 0, Half = 1, Normal = 2 }

/// <summary>A game-defined queue rate resolved for both power states at registration.</summary>
public sealed class ProductionRateRule
{
    public string Id { get; }
    public int Version { get; }
    public ProductionRate Powered { get; }
    public ProductionRate LowPower { get; }

    /// <param name="id">Stable rule ID chosen by the game.</param>
    /// <param name="version">Version of the rule's behavior.</param>
    /// <param name="selectRate">Called once for each value of the low-power condition.</param>
    public ProductionRateRule(string id, int version, Func<bool, ProductionRate> selectRate)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("A production rate rule needs a valid ID.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(selectRate);
        Id = id;
        Version = version;
        Powered = selectRate(false);
        LowPower = selectRate(true);
        if (!Enum.IsDefined(Powered) || !Enum.IsDefined(LowPower))
            throw new ArgumentException("Invalid production rate.", nameof(selectRate));
    }

    internal ProductionRate Rate(bool lowPower) => lowPower ? LowPower : Powered;

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Id);
        writer.Write(Version);
        writer.Write((byte)Powered);
        writer.Write((byte)LowPower);
    }
}
