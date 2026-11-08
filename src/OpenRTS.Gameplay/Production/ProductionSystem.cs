using System.Diagnostics.CodeAnalysis;
using Frent;
using OpenRTS.Core.Economy;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core.Production;

internal sealed class ProductionSystem(IProductionWorld world, UnitDefinition[] types, BuildRecipes recipes,
    EconomySystem economy, ProductionRateRule? rateRule, object storageGate)
{
    private long _nextTaskId = 1;
    private bool _producerOrderDirty;
    private EntityId[] _producerOrder = [];

    internal static ProducerState? CreateProducer(UnitDefinition type) =>
        type.TaskQueues.Count > 0 ? new(type.TaskQueues.Count) : null;

    internal void InvalidateProducerOrder() => _producerOrderDirty = true;

    internal void BeforeRemove(Entity entity)
    {
        if (entity.Has<ProducerState>()) InvalidateProducerOrder();
    }

    private EntityId[] ProducerOrder(World? storage)
    {
        if (!_producerOrderDirty) return _producerOrder;
        var ids = new List<EntityId>();
        if (storage is not null)
        {
            lock (storageGate)
            {
                foreach (var (identity, _) in storage.Query<EntityIdentity, ProducerState>())
                    ids.Add(identity.Value.Id);
            }
        }
        ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        _producerOrder = ids.ToArray();
        _producerOrderDirty = false;
        return _producerOrder;
    }

    private static bool TryGetProducer(Entity entity, [NotNullWhen(true)] out ProducerState? producer)
    {
        producer = entity.Has<ProducerState>() ? entity.Get<ProducerState>() : null;
        return producer is not null;
    }

    private void Refund(ProductionTask task, BuildDefinition build)
    {
        long consumed = (long)build.Cost * Math.Min(task.Progress, build.Ticks) / build.Ticks;
        economy.Credit(task.Payer, Math.Max(0, task.Paid - consumed));
    }

    internal void CancelForOwnerChange(Entity actor)
    {
        if (!TryGetProducer(actor, out var producer)) return;
        int type = actor.Get<EntityIdentity>().Type;
        foreach (var queue in producer.Queues)
        {
            foreach (var task in queue) Refund(task, recipes[type, task.Product].Build);
            queue.Clear();
        }
        producer.HasRally = false;
    }

    internal bool TryHandleMoveAsRally(Entity actor, WorldPosition point)
    {
        var identity = actor.Get<EntityIdentity>();
        if (types[identity.Type].Movable || !TryGetProducer(actor, out var producer)) return false;
        SetRally(actor, producer, point);
        return true;
    }

    private void SetRally(Entity actor, ProducerState? producer, WorldPosition point)
    {
        var id = actor.Get<EntityIdentity>().Id;
        if (world.IsConstructing(actor) || producer is null)
        {
            RejectAction(id);
            return;
        }
        producer.HasRally = true;
        producer.Rally = point;
        world.Emit(new(world.Tick, WorldEventKind.RallyChanged, id, point));
    }

    internal void Apply(WorldCommand command, ReadOnlySpan<Entity> recipients)
    {
        switch (command.Kind)
        {
            case CommandKind.SetRally:
                foreach (var actor in recipients)
                {
                    TryGetProducer(actor, out var producer);
                    SetRally(actor, producer, command.Position);
                }
                break;
            case CommandKind.EnqueueTask:
                Enqueue(command, recipients);
                break;
            case CommandKind.CancelTasks:
                Cancel(command, recipients);
                break;
            case CommandKind.PlaceProduced:
                Place(command, recipients);
                break;
        }
    }

    private static int QueueIndex(UnitDefinition type, string id)
    {
        for (int q = 0; q < type.TaskQueues.Count; q++)
            if (type.TaskQueues[q].Id == id) return q;
        return -1;
    }

    private void Enqueue(WorldCommand command, ReadOnlySpan<Entity> recipients)
    {
        for (int n = 0; n < command.Amount; n++)
        {
            Entity chosen = default;
            ProducerState? producer = null;
            int product = -1, queue = -1, length = int.MaxValue;
            foreach (var actor in recipients)
            {
                int typeIndex = actor.Get<EntityIdentity>().Type;
                uint owner = actor.Get<OwnershipState>().Owner;
                var type = types[typeIndex];
                int p = recipes.FindProduct(typeIndex, command.Action);
                if (p < 0 || type.Products[p].OnSite || !world.ProductEnabled(actor, p) || owner == 0 ||
                    world.IsConstructing(actor) || !TryGetProducer(actor, out var state)) continue;
                int q = QueueIndex(type, type.Products[p].Queue);
                int size = state.Queues[q].Count;
                var settings = type.TaskQueues[q];
                if (size >= settings.Capacity || size >= length) continue;
                if (settings.Payment == PaymentPolicy.Upfront && !economy.CanAfford(owner, recipes[typeIndex, p].Build.Cost)) continue;
                chosen = actor;
                producer = state;
                product = p;
                queue = q;
                length = size;
            }
            if (producer is null)
            {
                RejectAction();
                break;
            }
            var identity = chosen.Get<EntityIdentity>();
            uint payer = chosen.Get<OwnershipState>().Owner;
            var recipe = recipes[identity.Type, product];
            bool upfront = types[identity.Type].TaskQueues[queue].Payment == PaymentPolicy.Upfront;
            if (upfront && !economy.Spend(payer, recipe.Build.Cost))
            {
                RejectAction(identity.Id);
                break;
            }
            producer.Queues[queue].Add(new(checked(_nextTaskId++), product, command.Repeat)
            {
                Payer = payer,
                Paid = upfront ? recipe.Build.Cost : 0
            });
            world.Emit(new(world.Tick, WorldEventKind.TaskAdded, identity.Id, UnitType: recipe.UnitType));
        }
    }

    private void Cancel(WorldCommand command, ReadOnlySpan<Entity> recipients)
    {
        // Cancel newest matching entries first, retaining progress of the active task when possible.
        var matching = new List<(EntityIdentity Producer, List<ProductionTask> Queue, ProductionTask Task)>();
        foreach (var actor in recipients)
        {
            if (!TryGetProducer(actor, out var producer)) continue;
            var identity = actor.Get<EntityIdentity>();
            var type = types[identity.Type];
            for (int q = 0; q < producer.Queues.Length; q++)
            {
                if (command.Queue is not null && type.TaskQueues[q].Id != command.Queue) continue;
                var tasks = producer.Queues[q];
                foreach (var task in tasks)
                    if (type.Products[task.Product].Id == command.Action && task.Repeat == command.Repeat &&
                        (command.TaskId == 0 || task.Id == command.TaskId))
                        matching.Add((identity, tasks, task));
            }
        }
        foreach (var item in matching.OrderByDescending(item => item.Task.Id).Take(command.Amount))
        {
            Refund(item.Task, recipes[item.Producer.Type, item.Task.Product].Build);
            item.Queue.Remove(item.Task);
            world.Emit(new(world.Tick, WorldEventKind.TaskCancelled, item.Producer.Id));
        }
    }

    private void Place(WorldCommand command, ReadOnlySpan<Entity> recipients)
    {
        foreach (var actor in recipients)
        {
            if (world.IsUpgrading(actor) || !TryGetProducer(actor, out var producer)) continue;
            var identity = actor.Get<EntityIdentity>();
            var type = types[identity.Type];
            foreach (var tasks in producer.Queues)
            {
                if (tasks.Count == 0) continue;
                var task = tasks[0];
                if (!task.Ready || command.TaskId != task.Id || type.Products[task.Product].Id != command.Action) continue;
                var recipe = recipes[identity.Type, task.Product];
                if (recipe.Build.Kind != ProductionKind.PlaceBuilding ||
                    ObjectGeometry.DistanceSquared(type, actor.Get<SpatialState>().Position, types[recipe.UnitType], command.Position, out _, out _) >
                    (long)recipe.Build.PlacementRadius * recipe.Build.PlacementRadius ||
                    !world.TryPlaceBuilding(recipe.UnitType, command.Position, actor.Get<OwnershipState>().Owner, out var created))
                {
                    RejectAction(identity.Id);
                    return;
                }
                FinishTask(actor, tasks, task, created);
                return;
            }
        }
        RejectAction();
    }

    private void RejectAction(EntityId id = default) => world.Emit(new(world.Tick, WorldEventKind.ActionUnavailable, id));

    private void FinishTask(Entity actor, List<ProductionTask> tasks, ProductionTask task, EntityId created)
    {
        tasks.RemoveAt(0);
        if (task.Repeat)
            tasks.Add(new(checked(_nextTaskId++), task.Product, true) { Payer = actor.Get<OwnershipState>().Owner });
        world.Emit(new(world.Tick, WorldEventKind.Produced, actor.Get<EntityIdentity>().Id, TargetEntity: created));
    }

    internal void Advance(World? storage, CancellationToken cancellationToken)
    {
        // Close the ECS query before processing. Every spawn must be visible to the next producer.
        bool rebuilt = false;
        int completed = 0;
        foreach (var id in ProducerOrder(storage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!world.TryGetEntity(id, out var actor) || world.IsConstructing(actor) || world.IsUpgrading(actor)) continue;
            int typeIndex = actor.Get<EntityIdentity>().Type;
            var type = types[typeIndex];
            var producer = actor.Get<ProducerState>();
            uint owner = actor.Get<OwnershipState>().Owner;
            if (owner == 0) continue;
            for (int q = 0; q < producer.Queues.Length; q++)
            {
                var tasks = producer.Queues[q];
                if (tasks.Count == 0) continue;
                var task = tasks[0];
                var recipe = recipes[typeIndex, task.Product];
                var settings = type.TaskQueues[q];
                if (!task.Ready && !AdvanceTask(id, task, recipe.Build, settings.Payment, type, owner)) continue;
                if (recipe.Build.Kind == ProductionKind.PlaceBuilding || completed == 32) continue;
                if (!rebuilt)
                {
                    world.RebuildMotionIndex();
                    rebuilt = true;
                }
                var from = actor.Get<SpatialState>().Position;
                var exit = new WorldPosition(Math.Clamp(from.X + settings.Exit.X, 0, world.Width),
                    Math.Clamp(from.Y + settings.Exit.Y, 0, world.Depth));
                if (!world.TryFindExitPlacement(recipe.UnitType, exit, settings.ExitSearchRadius, out var position))
                {
                    if (!task.Blocked) world.Emit(new(world.Tick, WorldEventKind.ProductionBlocked, id));
                    task.Blocked = true;
                    continue;
                }
                var spawned = world.SpawnUnit(recipe.UnitType, position, actor.Get<OwnershipState>().Owner);
                completed++;
                if (settings.ExitAnimation is { } animation)
                    world.PlayExitAnimation(id, type.AnimationIndex(animation), settings.ExitAnimationTicks);
                if (producer.HasRally) world.IssueRallyOrder(spawned, producer.Rally);
                FinishTask(actor, tasks, task, spawned);
            }
        }
    }

    private bool AdvanceTask(EntityId id, ProductionTask task, BuildDefinition build, PaymentPolicy payment, UnitDefinition type, uint owner)
    {
        bool lowPower = (world.ReadConditions(type, owner) & GameplayConditions.LowPower) != 0;
        var rate = rateRule?.Rate(lowPower) ?? (lowPower ? ProductionRate.Half : ProductionRate.Normal);
        if (rate == ProductionRate.Paused) return false;
        if (payment == PaymentPolicy.Upfront && task.Paid < build.Cost)
        {
            if (!economy.Spend(owner, build.Cost - task.Paid)) return false;
            task.Paid = build.Cost;
        }
        int work = task.Remainder + (int)rate;
        int progress = Math.Min(build.Ticks, task.Progress + work / 2);
        int due = payment switch
        {
            PaymentPolicy.Progressive => (int)(((long)build.Cost * progress + build.Ticks - 1) / build.Ticks),
            PaymentPolicy.OnCompletion when progress == build.Ticks => build.Cost,
            _ => task.Paid
        };
        if (!economy.Spend(owner, due - task.Paid)) return false;
        task.Paid = due;
        task.Progress = progress;
        task.Remainder = work % 2;
        if (task.Progress < build.Ticks) return false;
        task.Ready = true;
        world.Emit(new(world.Tick, WorldEventKind.ProductionReady, id));
        return true;
    }

    internal static (bool HasRally, WorldPosition Rally) Project(Entity actor, UnitDefinition type, ref uint states)
    {
        if (!TryGetProducer(actor, out var producer)) return default;
        for (int q = 0; q < producer.Queues.Length; q++)
            if (producer.Queues[q] is { Count: > 0 } tasks && !tasks[0].Ready && type.TaskQueues[q].WorkingState is { } state)
                states |= type.StateMask(state);
        return (producer.HasRally, producer.Rally);
    }

    internal int TaskCount(World? storage)
    {
        int count = 0;
        foreach (var id in ProducerOrder(storage))
            foreach (var tasks in world.GetEntity(id).Get<ProducerState>().Queues)
                count += tasks.Count;
        return count;
    }

    internal void CopyTaskPoses(Span<TaskPose> destination, World? storage)
    {
        if (destination.Length < TaskCount(storage)) throw new ArgumentException("Destination is too small.");
        int at = 0;
        foreach (var id in ProducerOrder(storage))
        {
            var actor = world.GetEntity(id);
            int type = actor.Get<EntityIdentity>().Type;
            var queues = actor.Get<ProducerState>().Queues;
            for (int q = 0; q < queues.Length; q++)
                foreach (var task in queues[q])
                {
                    var recipe = recipes[type, task.Product];
                    destination[at++] = new(id, q, task.Id, task.Product, recipe.UnitType, task.Progress,
                        recipe.Build.Ticks, recipe.Build.Cost, task.Repeat, task.Ready)
                    { Payer = task.Payer, UnpaidCost = recipe.Build.Cost - task.Paid };
                }
        }
    }

    internal void WriteState(BinaryWriter writer, World? storage)
    {
        var order = ProducerOrder(storage);
        writer.Write(_nextTaskId);
        writer.Write(order.Length);
        foreach (var id in order)
        {
            var producer = world.GetEntity(id).Get<ProducerState>();
            writer.Write(id.Value);
            writer.Write(producer.HasRally);
            writer.Write(producer.Rally.X);
            writer.Write(producer.Rally.Y);
            writer.Write(producer.Rally.Elevation);
            writer.Write(producer.Queues.Length);
            foreach (var tasks in producer.Queues)
            {
                writer.Write(tasks.Count);
                foreach (var task in tasks)
                {
                    writer.Write(task.Id);
                    writer.Write(task.Product);
                    writer.Write(task.Progress);
                    writer.Write(task.Repeat);
                    writer.Write(task.Ready);
                    writer.Write(task.Blocked);
                    writer.Write(task.Payer);
                    writer.Write(task.Paid);
                    writer.Write(task.Remainder);
                }
            }
        }
    }
}
