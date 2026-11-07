using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void RebuildMotionIndex()
    {
        _transportIndexDirty = false;
        var motion = _motion ?? _combatIndex; if (motion is null) return;
        motion.Clear(Count);
        if (ExistingStorage is null) return;
        foreach (var (identity, spatial, definition) in ExistingStorage.Query<EntityIdentity, SpatialState, EntityDefinition>())
        {
            if (_storedCount != 0 && IsStored(_entityHandles[_indices[identity.Value.Id]])) continue;
            var type = definition.Value.Spatial!;
            motion.Add(identity.Value.Id, spatial.Value.Position, type.Body, type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight);
        }
    }
    private void EnsureMovementOrder()
    {
        if (!_orderDirty) return;
        if (_movementOrder.Length < Count) Array.Resize(ref _movementOrder, _entityHandles.Length);
        for (int i = 0; i < Count; i++) _movementOrder[i] = i;
        Array.Sort(_movementOrder, 0, Count, _idOrder); _orderDirty = false;
    }

}
