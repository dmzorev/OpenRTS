namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private int AbilityIndex(int type, string? id) { for (int a = 0; a < _types[type].Abilities.Count; a++) if (_types[type].Abilities[a].Id == id) return a; return -1; }
    private int AbilityMount(int type, int ability) { for (int m = 0; m < _types[type].Weapons.Count; m++) if (_types[type].Weapons[m].Id == _types[type].Abilities[ability].Mount) return m; throw new InvalidOperationException("Missing ability mount."); }
    private int MountAbility(int type, int mount)
    { for (int a = 0; a < _types[type].Abilities.Count; a++) if (_types[type].Abilities[a].Mount == _types[type].Weapons[mount].Id) return a; return -1; }
    private void ApplyAbility(WorldCommand command)
    {
        bool toggling = command.Kind == CommandKind.SetAutoCast;
        foreach (int i in Recipients(command))
        {
            ref var e = ref _entities[i];
            ref var eOwnership = ref e.Ownership;
            ref var eCombat = ref e.Combat;
            ref var eOrders = ref e.Orders;
            ref var eNavigation = ref e.Navigation;
            int ability = AbilityIndex(e.UnitType, command.Action);
            if (ability < 0 || eOwnership.Owner == 0) continue;
            int mount = AbilityMount(e.UnitType, ability); ref var state = ref eCombat.Weapons[mount];
            if (toggling) { state.AutoCast = command.Enabled; continue; }
            var definition = _types[e.UnitType].Abilities[ability]; var weapon = _types[e.UnitType].Weapons[mount].Weapon;
            if (state.ReadyTick > Tick || state.FireAtTick > 0 || eOrders.HasOrder && eOrders.ActiveOrder.Kind == UnitActionKind.Ability || eNavigation.TerrainStranded) continue;
            bool target = ValidTarget(i, command.TargetEntity, weapon, out _);
            if (!target && (!definition.GroundTarget || command.TargetEntity.Value != 0)) continue;
            IssueOrder(i, new(UnitActionKind.Ability, command.Position, command.TargetEntity, Ability: ability), command.Queued);
            return;
        }
        if (!toggling) RejectAction();
    }
    private bool AbilityInRange(int index, UnitOrder order, WeaponDefinition weapon)
    {
        if (order.Target.Value != 0) return _indices.TryGetValue(order.Target, out int target) && InWeaponRange(index, target, weapon.Range);
        var e = _entities[index]; var closest = ObjectGeometry.ClosestPoint(_types[e.UnitType], e.Position, order.Position);
        return DistanceSquared(closest, order.Position) <= (long)weapon.Range * weapon.Range && (_baseGrid is null || TerrainGeometry.Clear(_baseGrid, closest, order.Position, 0));
    }
    private void UpdateAbilityOrder(int i)
    {
        ref var e = ref _entities[i];
        ref var ePresentation = ref e.Presentation;
        ref var eOrders = ref e.Orders;
        ref var eMotion = ref e.Motion;
        var order = eOrders.ActiveOrder; int mount = AbilityMount(e.UnitType, order.Ability); var weapon = _types[e.UnitType].Weapons[mount].Weapon;
        if (order.Target.Value != 0 && !ValidTarget(i, order.Target, weapon, out _)) { eOrders.HasOrder = false; Halt(i); return; }
        var aim = order.Target.Value != 0 ? _entities[_indices[order.Target]].Position : order.Position;
        ePresentation.HasAim = true; ePresentation.AimEntity = order.Target; ePresentation.AimPoint = aim;
        if (AbilityInRange(i, order, weapon)) { Halt(i); eOrders.OrderPhase = OrderPhase.Engaging; }
        else if (_types[e.UnitType].Movable)
        { if (!eMotion.Moving || (Tick + e.Id.Value) % 4 == 0) SetTarget(i, aim); eOrders.OrderPhase = eMotion.Moving ? OrderPhase.Approaching : OrderPhase.Waiting; }
        else eOrders.OrderPhase = OrderPhase.Waiting;
    }
}
