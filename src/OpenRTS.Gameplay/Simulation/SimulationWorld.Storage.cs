using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private UnitDefinition TypeOf(Frent.Entity actor) => _types[actor.Get<EntityIdentity>().Type];

    private void CapturePreviousFacings()
    {
        if (ExistingStorage is null) return;
        lock (StorageGate)
        {
            foreach (var (presentation, combat) in ExistingStorage.Query<PresentationState, CombatState>())
            {
                ref var state = ref presentation.Value;
                state.PreviousBodyFacing = state.BodyFacing;
                state.PreviousWeaponFacing = state.WeaponFacing;
                foreach (ref var mount in combat.Value.Weapons.AsSpan()) mount.PreviousFacing = mount.Facing;
            }
        }
    }

    private int CountMoving()
    {
        int count = 0;
        if (ExistingStorage is not null)
            lock (StorageGate)
            {
                foreach (ref var motion in ExistingStorage.Query<MotionState>()) if (motion.Moving) count++;
            }
        return count;
    }

    private int CountWeapons()
    {
        int count = 0;
        if (ExistingStorage is not null)
            lock (StorageGate)
            {
                foreach (ref var combat in ExistingStorage.Query<CombatState>()) count += combat.Weapons.Length;
            }
        return count;
    }

    private void UpdateDirectMovement()
    {
        if (ExistingStorage is null) return;
        lock (StorageGate)
        {
            foreach (var (spatial, motion, definition) in ExistingStorage.Query<SpatialState, MotionState, EntityDefinition>())
            {
                ref var state = ref motion.Value;
                if (!state.Moving) continue;
                int factor = definition.Value.Spatial!.Locomotor.Rules[0].Speed;
                var next = spatial.Value.Position;
                if (factor == 1024) next = StepTowards(next, state.Target);
                else
                {
                    int budget = SpeedPerTick * factor + state.MovementRemainder;
                    state.MovementRemainder = budget % 1024;
                    if (budget >= 1024) next = StepTowards(next, state.Target, budget / 1024);
                }
                if (_construction?.SiteMovementClear(spatial.Value.Position, next, definition.Value.Spatial.Body) != false) spatial.Value.Position = next;
                state.Moving = spatial.Value.Position != state.Target;
            }
        }
    }
}
