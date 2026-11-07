namespace OpenRTS.Core;

// The pilot grants WeaponsDisabled. Each token owns its own lifetime.
internal sealed class ConditionGrants
{
    private readonly record struct Grant(EntityId Entity, long ExpiresAt);

    private readonly Dictionary<long, Grant> _grants = new();
    private readonly Dictionary<EntityId, HashSet<long>> _byEntity = new();
    private readonly SortedSet<(long Tick, long Id)> _expirations = new();
    private long _nextId = 1;

    internal bool Has(EntityId entity) => _byEntity.ContainsKey(entity);

    internal long Add(EntityId entity, long tick, int duration)
    {
        long expiry = duration == 0 ? 0 : checked(tick + duration);
        long id = checked(_nextId++);
        if (!_byEntity.TryGetValue(entity, out var ids)) _byEntity.Add(entity, ids = new());
        ids.Add(id);
        _grants.Add(id, new(entity, expiry));
        if (expiry != 0) _expirations.Add((expiry, id));
        return id;
    }

    internal bool Revoke(EntityId entity, long id)
    {
        if (!_grants.TryGetValue(id, out var grant) || grant.Entity != entity) return false;
        Remove(id, grant);
        return true;
    }

    internal void Expire(long tick)
    {
        while (_expirations.Count > 0)
        {
            var (expiry, id) = _expirations.Min;
            if (expiry > tick) break;
            Remove(id, _grants[id]);
        }
    }

    internal void RemoveEntity(EntityId entity)
    {
        if (!_byEntity.Remove(entity, out var ids)) return;
        foreach (long id in ids)
        {
            var grant = _grants[id];
            RemoveToken(id, grant.ExpiresAt);
        }
    }

    private void Remove(long id, Grant grant)
    {
        RemoveToken(id, grant.ExpiresAt);
        var ids = _byEntity[grant.Entity];
        ids.Remove(id);
        if (ids.Count == 0) _byEntity.Remove(grant.Entity);
    }

    private void RemoveToken(long id, long expiry)
    {
        _grants.Remove(id);
        if (expiry != 0) _expirations.Remove((expiry, id));
    }

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(_nextId);
        writer.Write(_grants.Count);
        foreach (var (id, grant) in _grants.OrderBy(entry => entry.Key))
        {
            writer.Write(id);
            writer.Write(grant.Entity.Value);
            writer.Write(grant.ExpiresAt);
        }
    }
}
