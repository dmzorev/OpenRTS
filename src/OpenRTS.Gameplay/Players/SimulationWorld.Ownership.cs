using OpenRTS.Core.Capture;
using OpenRTS.Core.Economy;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    public const long MaximumCredits = EconomySystem.MaximumCredits;
    public int PlayerCount => _economy.PlayerCount;

    public void CopyPlayers(Span<PlayerPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        _economy.CopyPlayers(destination, ExistingStorage);
    }

    private void ChangeOwner(int index, uint owner, bool upgradeTransition = false)
    {
        var entity = _entityHandles[index];
        var identity = entity.Get<EntityIdentity>();
        uint previous = entity.Get<OwnershipState>().Owner;
        if (previous == owner) return;

        if (!upgradeTransition && entity.Has<UpgradeState>())
        {
            CancelUpgrade(entity, restoreNeutral: false);
            ref var upgrade = ref entity.Get<UpgradeState>();
            if (owner == 0 && upgrade.Completed >= 0 && _types[identity.Type].Upgrades[upgrade.Completed].ClaimNeutral)
                upgrade.Completed = -1;
        }

        _economy.EnsurePlayer(owner);
        _production.CancelForOwnerChange(entity);

        ref var orders = ref entity.Get<OrderState>();
        orders.HasOrder = false;
        orders.PendingOrders?.Clear();
        orders.CombatTarget = default;
        Halt(index);
        CancelWindups(index);
        ref var visual = ref entity.Get<PresentationState>();
        visual.HasAim = false;
        visual.AimEntity = default;

        entity.Get<OwnershipState>().Owner = owner;
        EconomySystem.ResetIncome(entity, _types[identity.Type].Economy, Tick);
        _capture?.OwnerChanged(entity, Tick);
        _economy.InvalidatePower();
        _events.Add(new(Tick, WorldEventKind.OwnerChanged, identity.Id, entity.Get<SpatialState>().Position,
            identity.Type, Owner: owner, PreviousOwner: previous));
    }

    private void WritePlayers(BinaryWriter writer)
    {
        _economy.WritePlayers(writer, ExistingStorage);
        // Keep the report format's per-entity income and capture state together.
        foreach (var entity in _entityHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<EntityIdentity>().Id.Value))
        {
            EconomySystem.WriteIncome(writer, entity);
            CaptureSystem.WriteEntityState(_capture, writer, entity);
        }
    }
}
