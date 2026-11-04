namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void RebuildMotionIndex()
    {
        var motion = _motion ?? _combatIndex; if (motion is null) return;
        motion.Clear(_entities.Length);
        for (int i = 0; i < Count; i++) motion.Add(i, _entities[i].Id, _entities[i].Position, _entities[i].Owner);
    }
    private void EnsureMovementOrder()
    {
        if (!_orderDirty) return;
        if (_movementOrder.Length < Count) Array.Resize(ref _movementOrder, _entities.Length);
        for (int i = 0; i < Count; i++) _movementOrder[i] = i;
        Array.Sort(_movementOrder, 0, Count, _idOrder); _orderDirty = false;
    }

}
