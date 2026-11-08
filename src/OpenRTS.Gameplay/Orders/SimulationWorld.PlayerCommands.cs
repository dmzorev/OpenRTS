namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private bool _restrictPlayerCommands;

    /// <summary>For matches where the trusted host binds nonzero issuers to player IDs. Issuer zero is reserved for scripts.</summary>
    public void RestrictCommandsToPlayers()
    {
        ThrowIfDisposed();
        if (Tick != 0) throw new InvalidOperationException("Configure command authority before the first tick.");
        _restrictPlayerCommands = true;
    }

    private bool CanApplyPlayerCommand(WorldCommand command)
    {
        bool Owns(EntityId id) => TryGetGameplayOwner(id, out uint owner) && owner == command.Issuer;
        if (command.Kind is CommandKind.CancelConstruction or CommandKind.CancelUpgrade)
            return command.Recipients is null && command.Owner == command.Issuer && command.TargetEntity != default;
        if (command.Kind is CommandKind.Aim or CommandKind.ClearAim or CommandKind.Transform)
            return command.Recipients is null && Owns(command.Entity);

        if (command.Kind is not (CommandKind.Move or CommandKind.MoveSelected or CommandKind.MoveGroup or
            CommandKind.AttackMove or CommandKind.AttackTarget or CommandKind.Stop or CommandKind.HoldPosition or
            CommandKind.StartUpgrade or CommandKind.RepairTarget or CommandKind.CaptureTarget or CommandKind.UseAbility or CommandKind.SetAutoCast or
            CommandKind.EnqueueTask or CommandKind.CancelTasks or CommandKind.PlaceProduced or CommandKind.SetRally or
            CommandKind.Land or CommandKind.ReturnToBase or CommandKind.Enter or CommandKind.Unload or CommandKind.UnloadAt or CommandKind.PlanConstruction or CommandKind.BuildSite))
            return false;
        if (command.Recipients is not { } recipients) return Owns(command.Entity);
        foreach (var id in recipients.Ids)
            if (!Owns(id)) return false;
        return true;
    }
}
