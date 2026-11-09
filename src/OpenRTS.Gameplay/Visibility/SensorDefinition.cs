namespace OpenRTS.Core.Visibility;

public enum SensorDisplay : byte { Contact, Identified, Model }

[Flags]
public enum LiveObservationFields : byte
{
    None = 0,
    Health = 1,
    States = 2,
    All = Health | States
}

/// <summary>A counter required on a channel, optionally only while standing on a terrain ID.</summary>
public sealed record ConcealmentDefinition
{
    public string Channel { get; }
    public string Counter { get; }
    public byte? Terrain { get; }

    public ConcealmentDefinition(string channel, string counter, byte? terrain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(counter);
        Channel = channel;
        Counter = counter;
        Terrain = terrain;
    }
}

public sealed class DetectorDefinition
{
    public string Channel { get; }
    public string Signature { get; }
    public int Range { get; }
    public SensorDisplay Display { get; }
    public LiveObservationFields Fields { get; }
    public IReadOnlyList<string> Counters { get; }

    public DetectorDefinition(string channel, string signature, int range, SensorDisplay display = SensorDisplay.Contact,
        IEnumerable<string>? counters = null, LiveObservationFields fields = LiveObservationFields.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (range is <= 0 or > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(range));
        if (!Enum.IsDefined(display)) throw new ArgumentOutOfRangeException(nameof(display));
        if (display == SensorDisplay.Model && channel != SensorDefinition.Optical)
            throw new ArgumentException("Live models currently require the optical channel.", nameof(display));
        if ((fields & ~LiveObservationFields.All) != 0) throw new ArgumentOutOfRangeException(nameof(fields));
        Channel = channel;
        Signature = signature;
        Range = range;
        Display = display;
        Fields = fields;
        Counters = SensorDefinition.Names(counters ?? []);
    }
}

/// <summary>Optional signatures and detectors for a spatial entity recipe.</summary>
public sealed class SensorDefinition
{
    public const string Visual = "visual";
    public const string Optical = "optical";
    public IReadOnlyList<string> Signatures { get; }
    public IReadOnlyList<ConcealmentDefinition> Concealments { get; }
    public IReadOnlyList<DetectorDefinition> Detectors { get; }

    public SensorDefinition(IEnumerable<string>? signatures = null, IEnumerable<ConcealmentDefinition>? concealments = null,
        IEnumerable<DetectorDefinition>? detectors = null)
    {
        Signatures = Names(signatures ?? [Visual]);
        Concealments = Array.AsReadOnly((concealments ?? []).ToArray());
        Detectors = Array.AsReadOnly((detectors ?? []).ToArray());
        if (Concealments.Any(c => c is null) || Detectors.Any(d => d is null))
            throw new ArgumentException("Sensor recipes cannot contain null entries.");
    }

    internal static IReadOnlyList<string> Names(IEnumerable<string> names)
    {
        var result = names.Order(StringComparer.Ordinal).ToArray();
        if (result.Any(string.IsNullOrWhiteSpace) || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Sensor names must be nonempty and unique.");
        return Array.AsReadOnly(result);
    }

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(Signatures.Count);
        foreach (string name in Signatures) writer.Write(name);
        writer.Write(Concealments.Count);
        foreach (var c in Concealments)
        {
            writer.Write(c.Channel);
            writer.Write(c.Counter);
            writer.Write(c.Terrain.HasValue);
            if (c.Terrain is byte terrain) writer.Write(terrain);
        }
        writer.Write(Detectors.Count);
        foreach (var d in Detectors)
        {
            writer.Write(d.Channel);
            writer.Write(d.Signature);
            writer.Write(d.Range);
            writer.Write((byte)d.Display);
            writer.Write((byte)d.Fields);
            writer.Write(d.Counters.Count);
            foreach (string counter in d.Counters) writer.Write(counter);
        }
    }
}

/// <summary>Compact facts of a current contact, with a player-local ID and an optional type.</summary>
public readonly record struct ContactKnowledge(long Id, WorldPosition Position, int? UnitType, long ObservedTick, long IdentifiedTick);

/// <summary>Frame projection. Optional measurements are sampled from the live entity at publication.</summary>
public readonly record struct ContactPose(ContactKnowledge Knowledge, int? Health, uint? States);
