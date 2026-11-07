namespace OpenRTS.Core;

public sealed record PlayerDefinition(uint Id, string Name, long Credits = 0);
public readonly record struct PlayerPose(uint Id, string Name, long Credits, long EnergyProduced, long EnergyConsumed)
{
    public long Energy => EnergyProduced - EnergyConsumed;
    public bool LowPower => Energy < 0;
}
internal sealed class PlayerState(PlayerDefinition definition)
{
    public readonly uint Id = definition.Id;
    public readonly string Name = definition.Name;
    public long Credits = definition.Credits, Produced, Consumed;
}
public sealed record EconomyDefinition
{
    public int Energy { get; }
    public int Income { get; }
    public int Interval { get; }
    public string LowPowerState { get; }
    public bool DisableWeapons { get; }
    public EconomyDefinition(int energy = 0, int income = 0, int interval = 100, string lowPowerState = "lowpower", bool disableWeapons = true)
    {
        if (energy is < -1000000 or > 1000000 || income is < 0 or > 1000000000 || interval is < 1 or > 1000000 || string.IsNullOrWhiteSpace(lowPowerState)) throw new ArgumentException("Invalid economy settings.");
        Energy = energy; Income = income; Interval = interval; LowPowerState = lowPowerState; DisableWeapons = disableWeapons;
    }
}
public enum DestructionPolicy { Destroy, Invulnerable, Neutralize }
internal struct IncomeState(long nextTick) { public long NextTick = nextTick; }
