using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

/// <summary>Gameplay facts derived from simulation state or backed by independent grants.</summary>
[Flags]
public enum GameplayConditions : byte
{
    None = 0,
    LowPower = 1,
    Constructing = 2,
    WeaponsDisabled = 4
}

public sealed partial class SimulationWorld
{
    private ConditionGrants? _conditionGrants;

    private bool WeaponsDisabled(EntityId id) => _conditionGrants?.Has(id) == true;

    private bool IsConstructing(Frent.Entity entity) => _construction?.IsFoundation(entity) == true || IsTransformationLocked(entity);

    private GameplayConditions ReadLocalConditions(Frent.Entity entity) =>
        IsConstructing(entity) ? GameplayConditions.Constructing : GameplayConditions.None;

    private GameplayConditions ReadConditions(UnitDefinition source, uint owner) =>
        _economy.ReadConditions(source.Economy, owner, ExistingStorage);

    private void ApplyConditionCommand(WorldCommand command)
    {
        if (!_indices.ContainsKey(command.Entity))
        {
            EmitEvent(new(Tick, WorldEventKind.MissingEntity, command.Entity));
            return;
        }
        if (command.Kind == CommandKind.GrantCondition)
        {
            long id = (_conditionGrants ??= new()).Add(command.Entity, Tick, command.DurationTicks);
            EmitEvent(new WorldEvent(Tick, WorldEventKind.ConditionGranted, command.Entity, DurationTicks: command.DurationTicks)
                { Condition = command.Condition, ConditionGrantId = id });
        }
        else if (_conditionGrants?.Revoke(command.Entity, command.ConditionGrantId) == true)
        {
            EmitEvent(new WorldEvent(Tick, WorldEventKind.ConditionRevoked, command.Entity)
                { Condition = GameplayConditions.WeaponsDisabled, ConditionGrantId = command.ConditionGrantId });
        }
        else RejectAction(command.Entity);
    }

    /// <summary>Read the current facts and grants for a live entity on the simulation owner thread.</summary>
    public bool TryGetConditions(EntityId id, out GameplayConditions conditions)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        conditions = GameplayConditions.None;
        if (!TryGetEntity(id, out var entity)) return false;
        conditions = ReadLocalConditions(entity);
        if ((conditions & GameplayConditions.Constructing) == 0)
            conditions |= ReadConditions(TypeOf(entity), entity.Get<OwnershipState>().Owner);
        if (WeaponsDisabled(id)) conditions |= GameplayConditions.WeaponsDisabled;
        return true;
    }
}
