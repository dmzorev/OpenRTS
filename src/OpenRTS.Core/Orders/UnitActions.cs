namespace OpenRTS.Core.Orders;

/// <summary>Immutable recipients can safely cross the session/journal boundary.</summary>
public sealed class EntitySet
{
    private readonly EntityId[] _ids;
    public ReadOnlySpan<EntityId> Ids => _ids;
    public int Count => _ids.Length;
    public EntitySet(IEnumerable<EntityId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _ids = ids.Distinct().OrderBy(id => id.Value).ToArray();
        if (_ids.Length is < 1 or > 1000000 || _ids[0].Value == 0) throw new ArgumentException("Recipients must contain 1..1000000 valid IDs.", nameof(ids));
    }
}
public enum UnitActionKind { Move, AttackMove, Attack, Stop, HoldPosition }
public enum OrderPhase { Idle, Moving, Approaching, Engaging, Holding, Waiting }
public readonly record struct UnitAction(UnitActionKind Kind, EntitySet Recipients, WorldPosition Position = default,
    EntityId Target = default, bool Queued = false);
public readonly record struct UnitOrder(UnitActionKind Kind, WorldPosition Position, EntityId Target = default, bool Group = false, long Cohort = 0);
public readonly record struct OrderSnapshot(UnitActionKind Kind, OrderPhase Phase, WorldPosition Destination, EntityId Target,
    int QueuedCount, long ReadyTick);

