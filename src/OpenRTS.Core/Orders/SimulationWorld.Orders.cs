namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private const int MaximumQueuedOrders = 32;

    private int[] Recipients(WorldCommand command)
    {
        if (command.Recipients is null)
        {
            if (command.Entity.Value == 0 && command.Kind == CommandKind.MoveGroup)
                return Enumerable.Range(0, Count).OrderBy(i => _entities[i].Id.Value).ToArray();
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
        ref var entity = ref _entities[index];
        if (queued && entity.HasOrder)
        {
            entity.PendingOrders ??= new();
            if (entity.PendingOrders.Count == MaximumQueuedOrders) _events.Add(new(Tick, WorldEventKind.QueueFull, entity.Id));
            else entity.PendingOrders.Enqueue(order);
            return;
        }
        if (StartOrder(index, order) && !queued) entity.PendingOrders?.Clear();
    }

    private bool StartOrder(int index, UnitOrder order)
    {
        ref var entity = ref _entities[index];
        bool repair = order.Kind == UnitActionKind.Repair;
        var weapon = OrderWeapon(index, repair, order.Target);
        if (order.Kind is UnitActionKind.Move or UnitActionKind.AttackMove && !_types[entity.UnitType].Movable ||
            order.Kind is UnitActionKind.Attack or UnitActionKind.AttackMove or UnitActionKind.Repair && weapon is null ||
            order.Kind is UnitActionKind.Attack or UnitActionKind.Repair && !ValidTarget(index, order.Target, weapon!, out _))
        { _events.Add(new(Tick, WorldEventKind.InvalidOrder, entity.Id)); return false; }
        if (order.Kind is UnitActionKind.Move or UnitActionKind.AttackMove)
        { if (!SetTarget(index, order.Position)) return false; entity.GroupMember = order.Group; entity.OrderGroup = order.Cohort; }
        else if (order.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition)
        { if (!SetTarget(index, entity.Position)) return false; }
        else Halt(index);
        if (entity.CombatTarget.Value != 0 || order.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition)
        { entity.HasAim = false; entity.AimEntity = default; }
        entity.CombatTarget = default; CancelWindups(index);
        entity.ActiveOrder = order; entity.HasOrder = order.Kind != UnitActionKind.Stop || entity.Moving;
        entity.OrderStarted = Tick;
        entity.OrderPhase = order.Kind == UnitActionKind.HoldPosition ? OrderPhase.Holding : entity.Moving ? OrderPhase.Moving : OrderPhase.Idle;
        return true;
    }

    private void Halt(int index)
    {
        ref var entity = ref _entities[index];
        entity.Moving = false; entity.Target = entity.Position; entity.GroupMember = false;
        entity.PathLength = entity.PathCursor = entity.WaitTicks = 0; entity.YieldFor = default; entity.YieldDeadline = 0;
        _groupRoutes.Remove(entity.Id);
    }
    private void UpdateOrders()
    {
        EnsureMovementOrder();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            int i = _movementOrder[orderIndex]; ref var entity = ref _entities[i];
            if (entity.HasOrder && !entity.Moving && entity.CombatTarget.Value == 0 && entity.ActiveOrder.Kind is UnitActionKind.Move or UnitActionKind.AttackMove or UnitActionKind.Stop)
                entity.HasOrder = false;
            while (!entity.HasOrder && entity.PendingOrders is { Count: > 0 }) StartOrder(i, entity.PendingOrders.Dequeue());
            if (!entity.HasOrder && entity.CombatTarget.Value == 0) entity.OrderPhase = OrderPhase.Idle;
            bool repair = entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.Repair;
            var weapon = OrderWeapon(i, repair, entity.HasOrder ? entity.ActiveOrder.Target : entity.CombatTarget);
            if (entity.Moving && entity.HasOrder && entity.ActiveOrder.Kind is UnitActionKind.Stop or UnitActionKind.HoldPosition) continue;
            if (weapon is null || entity.Owner == 0 || entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.Move) continue;
            bool attack = entity.HasOrder && entity.ActiveOrder.Kind is UnitActionKind.Attack or UnitActionKind.Repair;
            bool aggressive = entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.AttackMove;
            int range = aggressive ? weapon.AcquireRange : weapon.Range;
            if (attack) entity.CombatTarget = entity.ActiveOrder.Target;
            if (!ValidTarget(i, entity.CombatTarget, weapon, out int target) || !attack && !InWeaponRange(i, target, range))
            {
                if (entity.CombatTarget.Value != 0)
                {
                    entity.CombatTarget = default; entity.HasAim = false; entity.AimEntity = default;
                    if (attack) { entity.HasOrder = false; Halt(i); entity.OrderPhase = OrderPhase.Idle; continue; }
                    if (aggressive)
                    { SetTarget(i, entity.ActiveOrder.Position); entity.GroupMember = entity.ActiveOrder.Group; entity.OrderGroup = entity.ActiveOrder.Cohort; entity.OrderPhase = entity.Moving ? OrderPhase.Moving : OrderPhase.Idle; }
                    else entity.OrderPhase = entity.HasOrder ? OrderPhase.Holding : OrderPhase.Idle;
                }
                if ((Tick + entity.Id.Value) % 4 != 0 && entity.OrderStarted != Tick) continue;
                target = AcquireWeaponTarget(i, weapon, range);
                if (target < 0 || !InWeaponRange(i, target, range)) continue;
                entity.CombatTarget = _entities[target].Id;
            }
            entity.HasAim = true; entity.AimEntity = entity.CombatTarget;
            if (InWeaponRange(i, target, weapon.Range) && StopAllowed(entity.UnitType, entity.Position)) { Halt(i); entity.OrderPhase = OrderPhase.Engaging; }
            else if ((aggressive || attack) && _types[entity.UnitType].Movable)
            {
                if (!entity.Moving || (Tick + entity.Id.Value) % 4 == 0)
                {
                    var goal = _entities[target].Position;
                    if (entity.Target != goal) SetTarget(i, goal);
                }
                entity.OrderPhase = entity.Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
            }
            else entity.OrderPhase = OrderPhase.Waiting;
        }
    }

    private static void WriteOrders(BinaryWriter writer, EntityState entity)
    {
        writer.Write(entity.OrderGroup); writer.Write(entity.HasOrder); Write(entity.ActiveOrder); writer.Write((int)entity.OrderPhase); writer.Write(entity.OrderStarted);
        writer.Write(entity.CombatTarget.Value);
        writer.Write(entity.PendingOrders?.Count ?? 0);
        if (entity.PendingOrders is not null) foreach (var order in entity.PendingOrders) Write(order);
        void Write(UnitOrder order) { writer.Write((int)order.Kind); writer.Write(order.Position.X); writer.Write(order.Position.Y); writer.Write(order.Target.Value); writer.Write(order.Group); writer.Write(order.Cohort); }
    }
}
