namespace OpenRTS.Core.Combat;

public sealed record WeaponDefinition
{
    public int Range { get; }
    public int AcquireRange { get; }
    public int Damage { get; }
    public int CooldownTicks { get; }
    public int WindupTicks { get; }
    public WeaponDefinition(int range, int acquireRange, int damage, int cooldownTicks, int windupTicks = 2)
    {
        if (range is < 512 or > 8192 || acquireRange < range || acquireRange > 12288 || damage is < 1 or > 1000000 ||
            cooldownTicks is < 1 or > 1000000 || windupTicks is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(range));
        Range = range; AcquireRange = acquireRange; Damage = damage; CooldownTicks = cooldownTicks; WindupTicks = windupTicks;
    }
}
