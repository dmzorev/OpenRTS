using OpenRTS.Core.Simulation;
namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int MaximumQueuedOrders = 32;

    private int[] Recipients(WorldCommand command)
    {
        if (command.Recipients is null)
        {
            if (command.Entity.Value == 0 && command.Kind == CommandKind.MoveGroup)
                return Enumerable.Range(0, Count).OrderBy(i => _unitHandles[i].Get<UnitIdentity>().Id.Value).ToArray();
            if (_indices.TryGetValue(command.Entity, out int index)) return [index];
            _events.Add(new(Tick, WorldEventKind.MissingEntity, command.Entity)); return [];
        }
        var indices = new List<int>(command.Recipients.Count);
        foreach (var id in command.Recipients.Ids)
            if (_indices.TryGetValue(id, out int index)) indices.Add(index);
            else _events.Add(new(Tick, WorldEventKind.MissingEntity, id));
        return indices.ToArray();
    }

    private void IssueOrder(int index, UnitOrder order, bool queued)
    {
        ref var entity = ref _unitHandles[index];
        var entityIdentity = entity.Get<UnitIdentity>();
        ref var entityOrders = ref entity.Get<OrderState>();
        if (queued && entityOrders.HasOrder)
        {
            entityOrders.PendingOrders ??= new();
            if (entityOrders.PendingOrders.Count == MaximumQueuedOrders) _events.Add(new(Tick, WorldEventKind.QueueFull, entityIdentity.Id));
            else entityOrders.PendingOrders.Enqueue(order);
            return;
        }
        if (StartOrder(index, order) && !queued) entityOrders.PendingOrders?.Clear();
    }

    private bool StartOrder(int index, UnitOrder order)
    {
        ref var entity = ref _unitHandles[index];
        var entityIdentity = entity.Get<UnitIdentity>();
        ref var entityPresentation = ref entity.Get<PresentationState>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        ref var entityOrders = ref entity.Get<OrderState>();
        ref var entityMotion = ref entity.Get<MotionState>();
        bool repair = order.Kind == UnitActionKind.Repair;
        var weapon = OrderWeapon(index, repair, order.Target);
        if (order.Kind is UnitActionKind.Move or UnitActionKind.AttackMove && !_types[entityIdentity.UnitType].Movable ||
            order.Kind is UnitActionKind.Attack or UnitActionKind.AttackMove or UnitActionKind.Repair && weapon is null ||
            order.Kind is UnitActionKind.Attack or UnitActionKind.Repair && !ValidTarget(index, order.Target, weapon!, out _))
        { _events.Add(new(Tick, WorldEventKind.InvalidOrder, entityIdentity.Id)); return false; }
        if (order.Kind is UnitActionKind.Move or UnitActionKind.AttackMove)
        { if (!SetTarget(index, order.Position)) return false; entityNavigation.GroupMember = order.Group; entityNavigation.OrderGroup = order.Cohort; }
        else if (order.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition)
        { if (!_types[entityIdentity.UnitType].Movable) Halt(index); else if (!SetTarget(index, entity.Get<SpatialState>().Position)) return false; }
        else Halt(index);
        if (entityOrders.CombatTarget.Value != 0 || order.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition)
        { entityPresentation.HasAim = false; entityPresentation.AimEntity = default; }
        entityOrders.CombatTarget = default; CancelWindups(index);
        entityOrders.ActiveOrder = order; entityOrders.HasOrder = order.Kind != UnitActionKind.Stop || entityMotion.Moving;
        entityOrders.OrderStarted = Tick;
        entityOrders.OrderPhase = order.Kind == UnitActionKind.HoldPosition ? OrderPhase.Holding : entityMotion.Moving ? OrderPhase.Moving : OrderPhase.Idle;
        return true;
    }

    private void Halt(int index)
    {
        ref var entity = ref _unitHandles[index];
        ref var entityMotion = ref entity.Get<MotionState>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        entityMotion.Moving = false; entityMotion.Target = entity.Get<SpatialState>().Position; entityNavigation.GroupMember = false;
        entityNavigation.PathLength = entityNavigation.PathCursor = entityNavigation.WaitTicks = 0; entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0;
        entityNavigation.Route = null;
    }
    private void UpdateOrders()
    {
        EnsureMovementOrder();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            int i = _movementOrder[orderIndex]; ref var entity = ref _unitHandles[i];
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var entityOrders = ref entity.Get<OrderState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            if (entityNavigation.TerrainStranded) { entityOrders.OrderPhase = OrderPhase.Waiting; continue; }
            if (entityOrders.HasOrder && !entityMotion.Moving && entityOrders.CombatTarget.Value == 0 && entityOrders.ActiveOrder.Kind is UnitActionKind.Move or UnitActionKind.AttackMove or UnitActionKind.Stop)
                entityOrders.HasOrder = false;
            while (!entityOrders.HasOrder && entityOrders.PendingOrders is { Count: > 0 }) StartOrder(i, entityOrders.PendingOrders.Dequeue());
            if (!entityOrders.HasOrder && entityOrders.CombatTarget.Value == 0) entityOrders.OrderPhase = OrderPhase.Idle;
            if (entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Ability) { UpdateAbilityOrder(i); continue; }
            bool repair = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Repair;
            var weapon = OrderWeapon(i, repair, entityOrders.HasOrder ? entityOrders.ActiveOrder.Target : entityOrders.CombatTarget);
            if (entityMotion.Moving && entityOrders.HasOrder && entityOrders.ActiveOrder.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition) continue;
            if (weapon is null || entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Move) continue;
            if (entity.Get<OwnershipState>().Owner == 0) continue;
            ref var entityPresentation = ref entity.Get<PresentationState>();
            bool attack = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind is UnitActionKind.Attack or UnitActionKind.Repair;
            bool aggressive = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.AttackMove;
            int range = aggressive ? weapon.AcquireRange : weapon.Range;
            if (attack) entityOrders.CombatTarget = entityOrders.ActiveOrder.Target;
            if (!ValidTarget(i, entityOrders.CombatTarget, weapon, out int target) || !attack && !InWeaponRange(i, target, range))
            {
                if (entityOrders.CombatTarget.Value != 0)
                {
                    entityOrders.CombatTarget = default; entityPresentation.HasAim = false; entityPresentation.AimEntity = default;
                    if (attack) { entityOrders.HasOrder = false; Halt(i); entityOrders.OrderPhase = OrderPhase.Idle; continue; }
                    if (aggressive)
                    { SetTarget(i, entityOrders.ActiveOrder.Position); entityNavigation.GroupMember = entityOrders.ActiveOrder.Group; entityNavigation.OrderGroup = entityOrders.ActiveOrder.Cohort; entityOrders.OrderPhase = entityMotion.Moving ? OrderPhase.Moving : OrderPhase.Idle; }
                    else entityOrders.OrderPhase = entityOrders.HasOrder ? OrderPhase.Holding : OrderPhase.Idle;
                }
                if ((Tick + entityIdentity.Id.Value) % 4 != 0 && entityOrders.OrderStarted != Tick) continue;
                target = AcquireWeaponTarget(i, weapon, range);
                if (target < 0 || !InWeaponRange(i, target, range)) continue;
                entityOrders.CombatTarget = _unitHandles[target].Get<UnitIdentity>().Id;
            }
            entityPresentation.HasAim = true; entityPresentation.AimEntity = entityOrders.CombatTarget;
            if (InWeaponRange(i, target, weapon.Range) && (_types[entityIdentity.UnitType].Footprint is not null || StopAllowed(entityIdentity.UnitType, entity.Get<SpatialState>().Position))) { Halt(i); entityOrders.OrderPhase = OrderPhase.Engaging; }
            else if ((aggressive || attack) && _types[entityIdentity.UnitType].Movable)
            {
                if (!entityMotion.Moving || (Tick + entityIdentity.Id.Value) % 4 == 0)
                {
                    var goal = AimPoint(i, target);
                    if (entityMotion.Target != goal) SetTarget(i, goal);
                }
                entityOrders.OrderPhase = entityMotion.Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
            }
            else entityOrders.OrderPhase = OrderPhase.Waiting;
        }
    }

    private static void WriteOrders(BinaryWriter writer, Frent.Entity entity)
    {
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        ref var entityOrders = ref entity.Get<OrderState>();
        writer.Write(entityNavigation.OrderGroup); writer.Write(entityOrders.HasOrder); Write(entityOrders.ActiveOrder); writer.Write((int)entityOrders.OrderPhase); writer.Write(entityOrders.OrderStarted);
        writer.Write(entityOrders.CombatTarget.Value);
        writer.Write(entityOrders.PendingOrders?.Count ?? 0);
        if (entityOrders.PendingOrders is not null) foreach (var order in entityOrders.PendingOrders) Write(order);
        void Write(UnitOrder order) { writer.Write((int)order.Kind); writer.Write(order.Position.X); writer.Write(order.Position.Y); writer.Write(order.Position.Elevation); writer.Write(order.Target.Value); writer.Write(order.Group); writer.Write(order.Cohort); writer.Write(order.Ability); }
    }
}
