using OpenRTS.Core.Orders;

namespace OpenRTS.Core.Visibility;

/// <summary>Audience captured at emission, independent of later frames and entity removal.</summary>
public sealed record FogEventVisibility(IReadOnlyList<uint> Players, ulong Audience, ulong Entities, ulong Targets)
{
    public bool Equals(FogEventVisibility? other) => other is not null && Audience == other.Audience && Entities == other.Entities &&
        Targets == other.Targets && Players.SequenceEqual(other.Players);
    public override int GetHashCode() => HashCode.Combine(Audience, Entities, Targets);

    public WorldEvent? ForViewer(WorldEvent value, uint viewer)
    {
        int slot = -1;
        for (int i = 0; i < Players.Count; i++) if (Players[i] == viewer) { slot = i; break; }
        if (slot < 0 || (Audience & (1ul << slot)) == 0) return null;
        bool entity = (Entities & (1ul << slot)) != 0, target = (Targets & (1ul << slot)) != 0;
        return value with { Entity = entity ? value.Entity : default, UnitType = entity ? value.UnitType : -1,
            TargetEntity = target ? value.TargetEntity : default, SourcePosition = entity ? value.SourcePosition : default,
            Hover = entity && target ? value.Hover : default, Visibility = null };
    }
}
