using OpenRTS.Core.Simulation;
namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private int AbilityIndex(int type, string? id) { for (int a = 0; a < _types[type].Abilities.Count; a++) if (_types[type].Abilities[a].Id == id) return a; return -1; }
    private int AbilityMount(int type, int ability) { for (int m = 0; m < _types[type].Weapons.Count; m++) if (_types[type].Weapons[m].Id == _types[type].Abilities[ability].Mount) return m; throw new InvalidOperationException("Missing ability mount."); }
    private int MountAbility(int type, int mount)
    { for (int a = 0; a < _types[type].Abilities.Count; a++) if (_types[type].Abilities[a].Kind == AbilityKind.Weapon && _types[type].Abilities[a].Mount == _types[type].Weapons[mount].Id) return a; return -1; }
    private void ApplyAbility(WorldCommand command)
    {
        bool issuedOrder = false;
        bool toggling = command.Kind == CommandKind.SetAutoCast;
        foreach (int i in Recipients(command))
        {
            ref var e = ref _entityHandles[i];
            var eIdentity = e.Get<EntityIdentity>();
            ref var eOwnership = ref e.Get<OwnershipState>();
            ref var eCombat = ref e.Get<CombatState>();
            ref var eOrders = ref e.Get<OrderState>();
            ref var eNavigation = ref e.Get<NavigationAgentState>();
            int ability = AbilityIndex(eIdentity.Type, command.Action);
            if (ability < 0 || eOwnership.Owner == 0 || UpgradeImmobilizes(e) || (ReadLocalConditions(e) & GameplayConditions.Constructing) != 0) continue;
            var action = _types[eIdentity.Type].Abilities[ability];
            if (action.Kind == AbilityKind.Harvest)
            {
                issuedOrder |= ApplyHarvest(i, command);
                continue;
            }
            if (action.Kind == AbilityKind.Build)
            {
                if (toggling) { eOrders.AutoBuild = command.Enabled; continue; }
                if (_construction?.CanBuild(e, command.TargetEntity) != true) continue;
                IssueOrder(i, new(UnitActionKind.Build, default, command.TargetEntity), command.Queued);
                issuedOrder = true;
                continue;
            }
            if (action.Kind == AbilityKind.Capture)
            {
                issuedOrder |= _capture?.TryApplyAbility(e, action, command) == true;
                continue;
            }
            int mount = AbilityMount(eIdentity.Type, ability);
            if ((TypeOf(e).EnabledMounts(CompletedUpgrade(e)) & (1 << mount)) == 0) continue;
            ref var state = ref eCombat.Weapons[mount];
            if (toggling) { state.AutoCast = command.Enabled; continue; }
            var definition = _types[eIdentity.Type].Abilities[ability]; var weapon = _types[eIdentity.Type].Weapons[mount].Weapon;
            if (state.ReadyTick > Tick || state.FireAtTick > 0 || eOrders.HasOrder && eOrders.ActiveOrder.Kind == UnitActionKind.Ability || eNavigation.TerrainStranded) continue;
            bool target = ValidTarget(i, command.TargetEntity, weapon, out _);
            if (!target && (!definition.GroundTarget || command.TargetEntity.Value != 0)) continue;
            IssueOrder(i, new(UnitActionKind.Ability, command.Position, command.TargetEntity, Ability: ability), command.Queued);
            return;
        }
        if (!toggling && !issuedOrder) RejectAction();
    }
    private bool AbilityInRange(int index, UnitOrder order, WeaponDefinition weapon)
    {
        if (order.Target.Value != 0) return _indices.TryGetValue(order.Target, out int target) && IsOnMap(_entityHandles[target]) && InWeaponRange(index, target, weapon.Range);
        var e = _entityHandles[index];
        var closest = ObjectGeometry.ClosestPoint(TypeOf(e), e.Get<SpatialState>().Position, order.Position);
        return DistanceSquared(closest, order.Position) <= (long)weapon.Range * weapon.Range && (_baseGrid is null || TerrainGeometry.Clear(_baseGrid, closest, order.Position, 0));
    }
    private void UpdateAbilityOrder(int i)
    {
        ref var e = ref _entityHandles[i];
        var eIdentity = e.Get<EntityIdentity>();
        ref var ePresentation = ref e.Get<PresentationState>();
        ref var eOrders = ref e.Get<OrderState>();
        ref var eMotion = ref e.Get<MotionState>();
        var order = eOrders.ActiveOrder; int mount = AbilityMount(eIdentity.Type, order.Ability); var weapon = _types[eIdentity.Type].Weapons[mount].Weapon;
        if (order.Target.Value != 0 && !ValidTarget(i, order.Target, weapon, out _)) { eOrders.HasOrder = false; Halt(i); return; }
        var aim = order.Target.Value != 0 ? _entityHandles[_indices[order.Target]].Get<SpatialState>().Position : order.Position;
        ePresentation.HasAim = true; ePresentation.AimEntity = order.Target; ePresentation.AimPoint = aim;
        if (AbilityInRange(i, order, weapon)) { Halt(i); eOrders.OrderPhase = OrderPhase.Engaging; }
        else if (_types[eIdentity.Type].Movable)
        { if (!eMotion.Moving || (Tick + eIdentity.Id.Value) % 4 == 0) SetTarget(i, aim); eOrders.OrderPhase = eMotion.Moving ? OrderPhase.Approaching : OrderPhase.Waiting; }
        else eOrders.OrderPhase = OrderPhase.Waiting;
    }
}
