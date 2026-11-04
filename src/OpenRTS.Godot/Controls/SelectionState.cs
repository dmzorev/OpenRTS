using OpenRTS.Core;

namespace OpenRTS.Godot.Controls;

public enum SelectionChange { Replace, Add, Toggle }

/// <summary>Local player state, independent of authoritative orders and simulation hashes.</summary>
public sealed class SelectionState
{
    private readonly HashSet<EntityId> _ids = [];
    private readonly Dictionary<int, EntityId[]> _groups = new();
    private EntityId[] _sorted = [];
    private long _sortedRevision = -1;
    public ReadOnlySpan<EntityId> Ids
    {
        get { if (_sortedRevision != Revision) { _sorted = _ids.OrderBy(id => id.Value).ToArray(); _sortedRevision = Revision; } return _sorted; }
    }
    public int Count => _ids.Count;
    public long Revision { get; private set; }
    public EntityId Primary => Ids.Length == 0 ? default : Ids[0];
    public bool Contains(EntityId id) => _ids.Contains(id);
    public EntityId[] Snapshot() => Ids.ToArray();
    public void Change(IEnumerable<EntityId> ids, SelectionChange change)
    {
        if (change == SelectionChange.Replace) _ids.Clear();
        foreach (var id in ids)
        {
            if (id.Value == 0) continue;
            if (change != SelectionChange.Toggle || !_ids.Remove(id)) _ids.Add(id);
        }
        Revision++;
    }
    public void Prune(Func<EntityId, bool> exists) { if (_ids.RemoveWhere(id => !exists(id)) != 0) Revision++; }
    public void StoreGroup(int number) => _groups[number] = Snapshot();
    public void RecallGroup(int number, bool additive, Func<EntityId, bool> exists)
    { if (_groups.TryGetValue(number, out var ids)) Change(ids.Where(exists), additive ? SelectionChange.Add : SelectionChange.Replace); }
    public void Reset() { _ids.Clear(); _groups.Clear(); Revision++; }
}
