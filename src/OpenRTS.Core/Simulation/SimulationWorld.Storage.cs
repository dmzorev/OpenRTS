using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    // Unit storage only. Other domains may use independent ECS stores or ordinary data structures.
    private Frent.World? _unitStorage;
    private bool _disposed, _producersDirty;
    private EntityId[] _producerOrder = [];

    private bool TryGetProducer(int index, [NotNullWhen(true)] out ProducerState? producer)
    {
        producer = null;
        var entity = _unitHandles[index];
        if (!entity.Has<ProducerState>()) return false;
        producer = entity.Get<ProducerState>();
        return true;
    }

    private ProducerState Producer(EntityId id) => _unitHandles[_indices[id]].Get<ProducerState>();

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

    private void CapturePreviousFacings()
    {
        if (_unitStorage is null) return;
        foreach (var (presentation, combat) in _unitStorage.Query<PresentationState, CombatState>())
        {
            ref var state = ref presentation.Value;
            state.PreviousBodyFacing = state.BodyFacing;
            state.PreviousWeaponFacing = state.WeaponFacing;
            foreach (ref var mount in combat.Value.Weapons.AsSpan()) mount.PreviousFacing = mount.Facing;
        }
    }

    private int CountMoving()
    {
        int count = 0;
        if (_unitStorage is not null)
            foreach (ref var motion in _unitStorage.Query<MotionState>()) if (motion.Moving) count++;
        return count;
    }

    private int CountWeapons()
    {
        int count = 0;
        if (_unitStorage is not null)
            foreach (ref var combat in _unitStorage.Query<CombatState>()) count += combat.Weapons.Length;
        return count;
    }

    private void UpdateDirectMovement()
    {
        if (_unitStorage is null) return;
        foreach (var (spatial, motion, definition) in _unitStorage.Query<SpatialState, MotionState, EntityDefinition>())
        {
            ref var state = ref motion.Value;
            if (!state.Moving) continue;
            int factor = definition.Value.Spatial!.Locomotor.Rules[0].Speed;
            if (factor == 1024) spatial.Value.Position = StepTowards(spatial.Value.Position, state.Target);
            else
            {
                int budget = SpeedPerTick * factor + state.MovementRemainder;
                state.MovementRemainder = budget % 1024;
                if (budget >= 1024) spatial.Value.Position = StepTowards(spatial.Value.Position, state.Target, budget / 1024);
            }
            state.Moving = spatial.Value.Position != state.Target;
        }
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
