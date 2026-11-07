using OpenRTS.Core.Simulation;
using OpenRTS.Core.Production;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly ProductionRateRule? _productionRate;
    private int[][] _productTypes = []; private BuildDefinition[][] _builds = [];
    private long _nextTaskId = 1;

    private static uint ProductionVisualStates(UnitDefinition type, ProducerState? producer)
    {
        if (producer is null) return 0;
        uint states = 0;
        for (int q = 0; q < producer.Queues.Length; q++)
            if (producer.Queues[q] is { Count: > 0 } tasks && !tasks[0].Ready && type.TaskQueues[q].WorkingState is { } state)
                states |= type.StateMask(state);
        return states;
    }

    private void InitializeProduction()
    {
        _productTypes = new int[_types.Length][]; _builds = new BuildDefinition[_types.Length][];
        for (int t = 0; t < _types.Length; t++)
        {
            var products = _types[t].Products; _productTypes[t] = new int[products.Count]; _builds[t] = new BuildDefinition[products.Count];
            for (int p = 0; p < products.Count; p++)
            {
                int target = Array.FindIndex(_types, x => x.Id == products[p].Unit);
                if (target < 0 || _types[target].Build is null) throw new ArgumentException("Production requires a known buildable unit: " + products[p].Unit);
                var build = products[p].Override ?? _types[target].Build!;
                if ((build.Kind == ProductionKind.PlaceBuilding) != (_types[target].Footprint is not null) || products[p].OnSite && (build.Kind != ProductionKind.PlaceBuilding || _types[target].Destruction == DestructionPolicy.Invulnerable)) throw new ArgumentException("PlaceBuilding requires a footprint; Unit production cannot spawn footprints.");
                _productTypes[t][p] = target; _builds[t][p] = build;
            }
        }
    }
    private int ProductIndex(int type, string? id) { for (int p = 0; p < _types[type].Products.Count; p++) if (_types[type].Products[p].Id == id) return p; return -1; }
    private int QueueIndex(int type, string id) { for (int q = 0; q < _types[type].TaskQueues.Count; q++) if (_types[type].TaskQueues[q].Id == id) return q; return -1; }
    private void SetRally(int index, WorldPosition point)
    {
        ref var actor = ref _entityHandles[index];
        var actorIdentity = actor.Get<EntityIdentity>();
        if ((ReadLocalConditions(actor) & GameplayConditions.Constructing) != 0 || !TryGetProducer(index, out var producer)) { RejectAction(actorIdentity.Id); return; }
        producer.HasRally = true; producer.Rally = point; _events.Add(new(Tick, WorldEventKind.RallyChanged, actorIdentity.Id, point));
    }
    private void RejectAction(EntityId id = default) => _events.Add(new(Tick, WorldEventKind.ActionUnavailable, id));
    private void ApplyProduction(WorldCommand command)
    {
        int[] recipients = Recipients(command);
        if (command.Kind == CommandKind.SetRally) { foreach (int i in recipients) SetRally(i, command.Position); return; }
        if (command.Kind == CommandKind.EnqueueTask)
        {
            for (int n = 0; n < command.Amount; n++)
            {
                int chosen = -1, product = -1, queue = -1, length = int.MaxValue;
                foreach (int i in recipients)
                {
                    ref var e = ref _entityHandles[i];
                    var eIdentity = e.Get<EntityIdentity>();
                    ref var eOwnership = ref e.Get<OwnershipState>();
                    int p = ProductIndex(eIdentity.Type, command.Action);
                    if (p < 0 || _types[eIdentity.Type].Products[p].OnSite || eOwnership.Owner == 0 ||
                        (ReadLocalConditions(e) & GameplayConditions.Constructing) != 0 || !TryGetProducer(i, out var state)) continue;
                    int q = QueueIndex(eIdentity.Type, _types[eIdentity.Type].Products[p].Queue), size = state.Queues[q].Count;
                    if (size >= _types[eIdentity.Type].TaskQueues[q].Capacity || size >= length) continue;
                    var settings = _types[eIdentity.Type].TaskQueues[q];
                    if (settings.Payment == PaymentPolicy.Upfront && Player(eOwnership.Owner).Credits < _builds[eIdentity.Type][p].Cost) continue;
                    chosen = i; product = p; queue = q; length = size;
                }
                if (chosen < 0) { RejectAction(); break; }
                var actor = _entityHandles[chosen];
                var actorIdentity = actor.Get<EntityIdentity>();
                uint owner = actor.Get<OwnershipState>().Owner;
                int cost = _builds[actorIdentity.Type][product].Cost;
                bool upfront = _types[actorIdentity.Type].TaskQueues[queue].Payment == PaymentPolicy.Upfront;
                if (upfront && !Spend(owner, cost)) { RejectAction(actorIdentity.Id); break; }
                Producer(actorIdentity.Id).Queues[queue].Add(new(checked(_nextTaskId++), product, command.Repeat) { Payer = owner, Paid = upfront ? cost : 0 });
                _events.Add(new(Tick, WorldEventKind.TaskAdded, actorIdentity.Id, UnitType: _productTypes[actorIdentity.Type][product]));
            }
            return;
        }
        if (command.Kind == CommandKind.CancelTasks)
        {
            // Cancel newest matching entries first, retaining progress of the active task when possible.
            var matching = new List<(long Id, EntityId Producer, int Queue, ProductionTask Task)>();
            foreach (int i in recipients)
            {
                var e = _entityHandles[i];
                var eIdentity = e.Get<EntityIdentity>(); if (!TryGetProducer(i, out var state)) continue;
                for (int q = 0; q < state.Queues.Length; q++)
                    if (command.Queue is null || _types[eIdentity.Type].TaskQueues[q].Id == command.Queue)
                        foreach (var task in state.Queues[q])
                            if (_types[eIdentity.Type].Products[task.Product].Id == command.Action && task.Repeat == command.Repeat && (command.TaskId == 0 || task.Id == command.TaskId)) matching.Add((task.Id, eIdentity.Id, q, task));
            }
            foreach (var item in matching.OrderByDescending(x => x.Id).Take(command.Amount))
            { Refund(item.Task, _builds[_entityHandles[_indices[item.Producer]].Get<EntityIdentity>().Type][item.Task.Product]); Producer(item.Producer).Queues[item.Queue].Remove(item.Task); _events.Add(new(Tick, WorldEventKind.TaskCancelled, item.Producer)); }
            return;
        }
        foreach (int i in recipients)
        {
            var e = _entityHandles[i];
            var eIdentity = e.Get<EntityIdentity>(); if (!TryGetProducer(i, out var state)) continue;
            for (int q = 0; q < state.Queues.Length; q++)
            {
                var tasks = state.Queues[q]; if (tasks.Count == 0) continue; var task = tasks[0];
                if (!task.Ready || command.TaskId != task.Id || _types[eIdentity.Type].Products[task.Product].Id != command.Action) continue;
                int target = _productTypes[eIdentity.Type][task.Product]; var build = _builds[eIdentity.Type][task.Product];
                if (build.Kind != ProductionKind.PlaceBuilding || ObjectGeometry.DistanceSquared(_types[eIdentity.Type], e.Get<SpatialState>().Position, _types[target], command.Position, out _, out _) > (long)build.PlacementRadius * build.PlacementRadius ||
                    !PrepareFootprint(target, command.Position, out var grid)) { RejectAction(eIdentity.Id); return; }
                var id = Spawn(target, command.Position, e.Get<OwnershipState>().Owner); if (grid is not null) CommitGrid(grid);
                FinishTask(eIdentity.Id, q, task, id); return;
            }
        }
        RejectAction();
    }
    private void FinishTask(EntityId producer, int queue, ProductionTask task, EntityId created)
    {
        var tasks = Producer(producer).Queues[queue]; tasks.RemoveAt(0);
        if (task.Repeat) tasks.Add(new(checked(_nextTaskId++), task.Product, true) { Payer = _entityHandles[_indices[producer]].Get<OwnershipState>().Owner });
        _events.Add(new(Tick, WorldEventKind.Produced, producer, TargetEntity: created));
    }
    private void UpdateProduction()
    {
        // The ECS query is closed before processing: every spawn must be visible to the next producer.
        var order = ProducerOrder();
        if (order.Length == 0) return;
        bool rebuilt = false; int completed = 0;
        foreach (var id in order)
        {
            StepCancellation.ThrowIfCancellationRequested();
            if (!_indices.TryGetValue(id, out int i)) continue; var actor = _entityHandles[i];
            if ((ReadLocalConditions(actor) & GameplayConditions.Constructing) != 0) continue;
            var actorIdentity = actor.Get<EntityIdentity>(); var producer = actor.Get<ProducerState>(); var type = _types[actorIdentity.Type];
            uint owner = actor.Get<OwnershipState>().Owner; if (owner == 0) continue;
            for (int q = 0; q < producer.Queues.Length; q++)
            {
                var tasks = producer.Queues[q]; if (tasks.Count == 0) continue;
                var task = tasks[0]; var build = _builds[actorIdentity.Type][task.Product];
                if (!task.Ready)
                {
                    bool lowPower = ReadConditions(type, owner).HasFlag(GameplayConditions.LowPower);
                    var rate = _productionRate?.Rate(lowPower) ?? (lowPower ? ProductionRate.Half : ProductionRate.Normal);
                    if (rate == ProductionRate.Paused) continue;
                    var policy = type.TaskQueues[q].Payment;
                    if (policy == PaymentPolicy.Upfront && task.Paid < build.Cost)
                    { if (!Spend(owner, build.Cost - task.Paid)) continue; task.Paid = build.Cost; }
                    int work = task.Remainder + (int)rate;
                    int progress = Math.Min(build.Ticks, task.Progress + work / 2);
                    int due = policy == PaymentPolicy.Progressive ? (int)(((long)build.Cost * progress + build.Ticks - 1) / build.Ticks) : policy == PaymentPolicy.OnCompletion && progress == build.Ticks ? build.Cost : task.Paid;
                    if (!Spend(owner, due - task.Paid)) continue;
                    task.Paid = due; task.Progress = progress; task.Remainder = work % 2;
                    if (task.Progress < build.Ticks) continue;
                    task.Ready = true; _events.Add(new(Tick, WorldEventKind.ProductionReady, id));
                }
                if (build.Kind == ProductionKind.PlaceBuilding || completed == 32) continue;
                if (!rebuilt) { RebuildMotionIndex(); rebuilt = true; }
                int target = _productTypes[actorIdentity.Type][task.Product]; var settings = type.TaskQueues[q];
                var exit = new WorldPosition(Math.Clamp(actor.Get<SpatialState>().Position.X + settings.Exit.X, 0, Width), Math.Clamp(actor.Get<SpatialState>().Position.Y + settings.Exit.Y, 0, Depth));
                if (!TryFindExitPlacement(target, exit, settings.ExitSearchRadius, out var position))
                { if (!task.Blocked) _events.Add(new(Tick, WorldEventKind.ProductionBlocked, id)); task.Blocked = true; continue; }
                var spawned = Spawn(target, position, actor.Get<OwnershipState>().Owner); completed++;
                if (settings.ExitAnimation is { } animation) ApplyPresentation(_indices[id], new(Tick, 0, 1, CommandKind.PlayAnimation, id,
                    Animation: type.AnimationIndex(animation), DurationTicks: settings.ExitAnimationTicks, Track: AnimationTrack.Body));
                if (producer.HasRally) IssueOrder(_indices[spawned], new(UnitActionKind.Move, producer.Rally), false);
                FinishTask(id, q, task, spawned);
            }
        }
    }
    public int TaskCount
    {
        get
        {
            ThrowIfDisposed();
            int count = 0;
            foreach (var id in ProducerOrder()) foreach (var tasks in Producer(id).Queues) count += tasks.Count;
            return count;
        }
    }
    public void CopyTaskPoses(Span<TaskPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < TaskCount) throw new ArgumentException("Destination is too small."); int at = 0;
        foreach (var id in ProducerOrder())
        {
            var state = Producer(id);
            int t = _entityHandles[_indices[id]].Get<EntityIdentity>().Type; for (int q = 0; q < state.Queues.Length; q++) foreach (var task in state.Queues[q])
            { var build = _builds[t][task.Product]; destination[at++] = new(id, q, task.Id, task.Product, _productTypes[t][task.Product], task.Progress, build.Ticks, build.Cost, task.Repeat, task.Ready); }
        }
    }
    private void WriteProduction(BinaryWriter w)
    {
        var order = ProducerOrder();
        w.Write(_nextTaskId); w.Write(order.Length);
        foreach (var id in order)
        {
            var state = Producer(id);
            w.Write(id.Value); w.Write(state.HasRally); w.Write(state.Rally.X); w.Write(state.Rally.Y); w.Write(state.Rally.Elevation); w.Write(state.Queues.Length);
            foreach (var tasks in state.Queues) { w.Write(tasks.Count); foreach (var t in tasks) { w.Write(t.Id); w.Write(t.Product); w.Write(t.Progress); w.Write(t.Repeat); w.Write(t.Ready); w.Write(t.Blocked); w.Write(t.Payer); w.Write(t.Paid); w.Write(t.Remainder); } }
        }
    }
}
