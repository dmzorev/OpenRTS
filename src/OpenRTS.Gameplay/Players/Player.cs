namespace OpenRTS.Core;

public sealed record PlayerDefinition(uint Id, string Name, long Credits = 0);
public readonly record struct PlayerPose(uint Id, string Name, long Credits, long EnergyProduced, long EnergyConsumed)
{
    public long Energy => EnergyProduced - EnergyConsumed;
    public bool LowPower => Energy < 0;
}
public enum DestructionPolicy { Destroy, Invulnerable, Neutralize }
