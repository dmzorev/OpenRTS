using System.Collections.ObjectModel;

namespace OpenRTS.Core;

public sealed record PassengerDefinition
{
    public string Type { get; }
    public int Slots { get; }

    public PassengerDefinition(string type, int slots = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (slots is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(slots));
        Type = type;
        Slots = slots;
    }
}

public sealed record TransportDefinition
{
    public ReadOnlyCollection<string> Accepts { get; }
    public int Slots { get; }
    public bool FireFromInside { get; }
    public int UnloadDistance { get; }

    public TransportDefinition(IEnumerable<string> accepts, int slots, bool fireFromInside = false, int unloadDistance = -1)
    {
        ArgumentNullException.ThrowIfNull(accepts);
        var types = accepts.ToArray();
        if (types.Length == 0 || types.Any(string.IsNullOrWhiteSpace) || types.Distinct(StringComparer.Ordinal).Count() != types.Length)
            throw new ArgumentException("Transport needs distinct passenger types.", nameof(accepts));
        if (slots is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(slots));
        if (unloadDistance < -1) throw new ArgumentOutOfRangeException(nameof(unloadDistance));
        Accepts = Array.AsReadOnly(types.Order(StringComparer.Ordinal).ToArray());
        Slots = slots;
        FireFromInside = fireFromInside;
        UnloadDistance = unloadDistance;
    }
}
