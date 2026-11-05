using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private struct UnitIdentity { public EntityId Id; }

    // Unit storage only. Other domains may use independent ECS stores or ordinary data structures.
    private Frent.World? _unitStorage;
    private bool _disposed, _producersDirty;
    private EntityId[] _producerOrder = [];

    private bool TryGetProducer(int index, [NotNullWhen(true)] out ProducerState? producer)
    {
        producer = null;
        var entity = _entities[index].StorageEntity;
        if (!entity.Has<ProducerState>()) return false;
        producer = entity.Get<ProducerState>();
        return true;
    }

    private ProducerState Producer(EntityId id) => _entities[_indices[id]].StorageEntity.Get<ProducerState>();

    private EntityId[] ProducerOrder()
    {
        if (!_producersDirty) return _producerOrder;
        var ids = new List<EntityId>();
        if (_unitStorage is not null)
            foreach (var (identity, _) in _unitStorage.Query<UnitIdentity, ProducerState>()) ids.Add(identity.Value.Id);
        ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        _producerOrder = ids.ToArray();
        _producersDirty = false;
        return _producerOrder;
    }

    private void CapturePreviousPositions()
    {
        if (_unitStorage is null) return;
        foreach (ref var spatial in _unitStorage.Query<SpatialState>())
            spatial.Previous = spatial.Position;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Release storage on the world owner after all jobs have joined. Published frames remain valid.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _unitStorage?.Dispose();
        _unitStorage = null;
        _disposed = true;
    }
}
