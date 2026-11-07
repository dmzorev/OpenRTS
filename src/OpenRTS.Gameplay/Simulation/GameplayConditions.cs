using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

/// <summary>Read-only gameplay facts, computed from authoritative simulation state.</summary>
[Flags]
public enum GameplayConditions : byte
{
    None = 0,
    LowPower = 1,
    Constructing = 2
}

public sealed partial class SimulationWorld
{
    private GameplayConditions ReadLocalConditions(Frent.Entity entity) =>
        IsUnderConstruction(entity) ? GameplayConditions.Constructing : GameplayConditions.None;

    /// <summary>Read the current facts for a live entity on the simulation owner thread.</summary>
    public bool TryGetConditions(EntityId id, out GameplayConditions conditions)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        conditions = GameplayConditions.None;
        if (!TryGetEntity(id, out var entity)) return false;
        conditions = ReadLocalConditions(entity);
        if ((conditions & GameplayConditions.Constructing) == 0)
            conditions |= ReadConditions(TypeOf(entity), entity.Get<OwnershipState>().Owner);
        return true;
    }
}
