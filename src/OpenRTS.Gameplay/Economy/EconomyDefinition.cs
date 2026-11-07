namespace OpenRTS.Core;

public sealed record EconomyDefinition
{
    public int Energy { get; }
    public int Income { get; }
    public int Interval { get; }
    public string LowPowerState { get; }
    public bool DisableWeapons { get; }

    public EconomyDefinition(int energy = 0, int income = 0, int interval = 100, string lowPowerState = "lowpower", bool disableWeapons = true)
    {
        if (energy is < -1000000 or > 1000000 || income is < 0 or > 1000000000 ||
            interval is < 1 or > 1000000 || string.IsNullOrWhiteSpace(lowPowerState))
            throw new ArgumentException("Invalid economy settings.");
        Energy = energy;
        Income = income;
        Interval = interval;
        LowPowerState = lowPowerState;
        DisableWeapons = disableWeapons;
    }
}
