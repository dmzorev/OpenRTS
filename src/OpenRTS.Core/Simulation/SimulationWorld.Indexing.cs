using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void RebuildMotionIndex()
    {
        var motion = _motion ?? _combatIndex; if (motion is null) return;
        motion.Clear(_entities.Length);
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i];
            ref var entityOwnership = ref entity.Ownership;
            var type = _types[entity.UnitType];
            var position = entity.StorageEntity.Get<SpatialState>().Position;
            motion.Add(i, entity.Id, position, entityOwnership.Owner, type.Body, type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight);
        }
    }
    private void EnsureMovementOrder()
    {
        if (!_orderDirty) return;
        if (_movementOrder.Length < Count) Array.Resize(ref _movementOrder, _entities.Length);
        for (int i = 0; i < Count; i++) _movementOrder[i] = i;
        Array.Sort(_movementOrder, 0, Count, _idOrder); _orderDirty = false;
    }

}
