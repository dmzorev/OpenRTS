using Frent;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private struct UpgradeState()
    {
        public int Completed = -1, Project = -1, Work;
        public EntityId Support;
        public uint Payer;
    }

    private readonly SortedSet<uint> _upgradeProjects = [];
    private readonly List<uint> _upgradePass = [];

    private static int CompletedUpgrade(Entity actor) => actor.Has<UpgradeState>() ? actor.Get<UpgradeState>().Completed : -1;
    private static bool IsUpgrading(Entity actor) => actor.Has<UpgradeState>() && actor.Get<UpgradeState>().Work > 0;
    private bool UpgradeImmobilizes(Entity actor) => IsUpgrading(actor) && TypeOf(actor).Movable;

    private void ApplyUpgrade(WorldCommand command)
    {
        if (command.Kind == CommandKind.CancelUpgrade)
        {
            if (TryGetEntity(command.TargetEntity, out var pending) && pending.Has<UpgradeState>() &&
                pending.Get<UpgradeState>() is { Project: >= 0 } state && state.Payer == command.Owner)
            {
                CancelUpgrade(pending, restoreNeutral: true);
                return;
            }
            RejectAction(command.TargetEntity);
            return;
        }

        if (command.Recipients is not null || !_indices.TryGetValue(command.Entity, out int actorIndex) ||
            !_indices.TryGetValue(command.TargetEntity, out int targetIndex))
        { RejectAction(command.Entity); return; }

        var actor = _entityHandles[actorIndex];
        var target = _entityHandles[targetIndex];
        var type = TypeOf(target);
        int variant = type.UpgradeIndex(command.Action ?? type.DefaultUpgrade?.Id);
        uint owner = actor.Get<OwnershipState>().Owner;
        uint previousOwner = target.Get<OwnershipState>().Owner;
        if (variant < 0 || owner == 0 || !IsOnMap(actor) || !IsOnMap(target) || IsConstructing(actor) ||
            IsConstructing(target) || target.Get<UpgradeState>() is { Project: >= 0 } or { Completed: >= 0 })
        { RejectAction(command.TargetEntity); return; }

        var upgrade = type.Upgrades[variant];
        if ((upgrade.ClaimNeutral ? previousOwner != 0 || _transport?.HasPassengers(target) == true ||
            target.Has<MineState>() && target.Get<MineState>().Remaining == 0 : owner != previousOwner) ||
            (upgrade.Support ? actorIndex == targetIndex || !CanSupportUpgrade(actor, target) : actorIndex != targetIndex) ||
            !_economy.Spend(owner, upgrade.Cost))
        { RejectAction(command.TargetEntity); return; }

        CancelTransformation(target.Get<EntityIdentity>().Id);
        ref var project = ref target.Get<UpgradeState>();
        project.Project = variant;
        project.Payer = owner;
        project.Support = upgrade.Support ? command.Entity : default;
        _upgradeProjects.Add(command.TargetEntity.Value);
        if (upgrade.Support)
            IssueOrder(actorIndex, new(UnitActionKind.UpgradeSupport, default, command.TargetEntity), queued: false);
    }

    private bool CanSupportUpgrade(Entity actor, Entity target)
    {
        var type = TypeOf(actor);
        return type.ConstructionRange > 0 && !UpgradeImmobilizes(actor) &&
            !actor.Get<NavigationAgentState>().TerrainStranded && (type.Movable || InUpgradeRange(actor, target));
    }

    private bool InUpgradeRange(Entity actor, Entity target)
    {
        long range = TypeOf(actor).ConstructionRange;
        return ActorDistanceSquared(actor, target) <= range * range;
    }

    private void PrepareUpgrades()
    {
        if (_upgradeProjects.Count == 0) return;
        _upgradePass.Clear();
        _upgradePass.AddRange(_upgradeProjects);
        foreach (uint id in _upgradePass)
        {
            if (!TryGetEntity(new(id), out var target)) continue;
            var state = target.Get<UpgradeState>();
            if (state.Support == default)
            {
                AdvanceUpgrade(target);
                continue;
            }
            if (!TryGetEntity(state.Support, out var support) || !IsOnMap(support) ||
                support.Get<OwnershipState>().Owner != state.Payer || !CanSupportUpgrade(support, target) ||
                support.Get<OrderState>() is not { HasOrder: true, ActiveOrder.Kind: UnitActionKind.UpgradeSupport } orders ||
                orders.ActiveOrder.Target.Value != id)
                CancelUpgrade(target, restoreNeutral: true);
        }
    }

    private void UpdateUpgradeSupport(int index)
    {
        var actor = _entityHandles[index];
        ref var orders = ref actor.Get<OrderState>();
        var identity = actor.Get<EntityIdentity>();
        if (!TryGetEntity(orders.ActiveOrder.Target, out var target) || !target.Has<UpgradeState>() ||
            target.Get<UpgradeState>().Support != identity.Id)
        {
            orders.HasOrder = false;
            orders.OrderPhase = OrderPhase.Idle;
            Halt(index);
            return;
        }

        var position = actor.Get<SpatialState>().Position;
        if (!InUpgradeRange(actor, target) || TypeOf(actor).Movable && !StopAllowed(identity.Type, position))
        {
            if (TypeOf(target).Footprint is not null) ApproachFootprint(index, target);
            else if ((Tick + identity.Id.Value) % 4 == 0 || orders.OrderStarted == Tick)
            {
                var goal = ObjectGeometry.ClosestPoint(TypeOf(target), target.Get<SpatialState>().Position, position);
                if (actor.Get<MotionState>().Target != goal || !actor.Get<MotionState>().Moving) SetTarget(index, goal);
            }
            orders.OrderPhase = OrderPhase.Approaching;
            return;
        }
        Halt(index);
        orders.OrderPhase = OrderPhase.Engaging;
        AdvanceUpgrade(target);
    }

    private void AdvanceUpgrade(Entity target)
    {
        ref var state = ref target.Get<UpgradeState>();
        var identity = target.Get<EntityIdentity>();
        var type = _types[identity.Type];
        var upgrade = type.Upgrades[state.Project];
        if (state.Work++ == 0)
        {
            int index = _indices[identity.Id];
            if (type.Movable)
            {
                ref var orders = ref target.Get<OrderState>();
                if (!orders.HasOrder || orders.ActiveOrder.Kind != UnitActionKind.UnloadAt)
                {
                    orders.HasOrder = false;
                    orders.OrderPhase = OrderPhase.Idle;
                }
                orders.CombatTarget = default;
                Halt(index);
                CancelWindups(index);
            }
            if (upgrade.ClaimNeutral) ChangeOwner(index, state.Payer, upgradeTransition: true);
            EmitUpgradeEvent(target, WorldEventKind.ConstructionStarted, state.Payer);
        }
        if (state.Work < upgrade.Ticks) return;
        uint payer = state.Payer;
        state.Completed = state.Project;
        ClearUpgradeProject(identity.Id, ref state);
        if (upgrade.ClaimNeutral) target.Get<HealthState>().Current = type.MaxHealth;
        EmitUpgradeEvent(target, WorldEventKind.ConstructionCompleted, payer);
    }

    private void CancelUpgrade(Entity target, bool restoreNeutral)
    {
        if (!target.Has<UpgradeState>()) return;
        ref var state = ref target.Get<UpgradeState>();
        if (state.Project < 0) return;
        var identity = target.Get<EntityIdentity>();
        var upgrade = _types[identity.Type].Upgrades[state.Project];
        uint payer = state.Payer;
        bool rollback = restoreNeutral && upgrade.ClaimNeutral && state.Work > 0;
        ClearUpgradeProject(identity.Id, ref state);
        _economy.Credit(payer, upgrade.Cost);
        if (rollback) ChangeOwner(_indices[identity.Id], 0, upgradeTransition: true);
        EmitUpgradeEvent(target, WorldEventKind.ConstructionCancelled, payer);
    }

    private void ClearUpgradeProject(EntityId id, ref UpgradeState state)
    {
        _upgradeProjects.Remove(id.Value);
        state.Project = -1;
        state.Work = 0;
        state.Support = default;
        state.Payer = 0;
    }

    private void EmitUpgradeEvent(Entity actor, WorldEventKind kind, uint payer)
    {
        var identity = actor.Get<EntityIdentity>();
        _events.Add(new(Tick, kind, identity.Id, actor.Get<SpatialState>().Position, identity.Type, Owner: payer));
    }

    private static UpgradePose ReadUpgrade(Entity actor, UnitDefinition type, ref uint visualStates)
    {
        if (type.Upgrades.Count == 0) return default;
        var state = actor.Get<UpgradeState>();
        var completed = state.Completed < 0 ? null : type.Upgrades[state.Completed];
        var project = state.Project < 0 ? null : type.Upgrades[state.Project];
        var visual = state.Work > 0 ? project : completed;
        if (visual is not null) visualStates |= type.StateMask(visual.Id);
        return new(completed?.Id, project?.Id, state.Payer, state.Work, project?.Ticks ?? 0);
    }

    private static void WriteUpgrade(BinaryWriter writer, Entity actor)
    {
        if (!actor.Has<UpgradeState>()) return;
        var state = actor.Get<UpgradeState>();
        writer.Write(state.Completed); writer.Write(state.Project); writer.Write(state.Work);
        writer.Write(state.Support.Value); writer.Write(state.Payer);
    }
}
