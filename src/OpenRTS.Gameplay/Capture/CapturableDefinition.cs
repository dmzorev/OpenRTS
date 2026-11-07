namespace OpenRTS.Core;

public sealed record CapturableDefinition
{
    public int? Ticks { get; }
    public CapturableDefinition(int? ticks = null)
    {
        if (ticks is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(ticks));
        Ticks = ticks;
    }
}
