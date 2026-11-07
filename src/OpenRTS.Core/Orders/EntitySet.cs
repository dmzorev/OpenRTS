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
