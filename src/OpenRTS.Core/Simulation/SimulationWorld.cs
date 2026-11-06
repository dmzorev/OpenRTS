using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

/// <summary>Single-owner world. Hosts exchange commands and copied poses, never its mutable storage.</summary>
public sealed partial class SimulationWorld : IDisposable
{
    private readonly UnitDefinition[] _types;
    // Derived dense handle list, independent of Frent rows and spatial-index slots.
    private Frent.Entity[] _unitHandles;
    private readonly Dictionary<EntityId, int> _indices;
    private readonly List<WorldEvent> _events = [];
    private WorldCommand[] _commands = [];
    private int _commandCount;
    private uint _nextId = 1;
    private DeterministicRandom _random;
    private readonly GridNavigator? _navigation;
    private readonly MotionIndex? _motion;
    private readonly MotionIndex? _combatIndex;
    private CancellationToken _stepCancellation;
    private long _orderGroupSequence;
    private int[] _movementOrder = [], _navigationGoals = [];
    private bool _orderDirty = true, _failed;
    private readonly IComparer<int> _idOrder;

    public int Width { get; }
    public int Depth { get; }
    public int TickRate { get; }
    public int SpeedPerTick { get; }
    public long Tick { get; private set; }
    public int Count { get; private set; }
    public NavigationGrid? NavigationGrid => _navigation?.Grid;
    public int NavigationWaitingCount { get; private set; }
    public int NavigationBuiltFields => _weighted?.Builds ?? _navigation?.LastBuildCount ?? 0;
    public int NavigationVisitedCells => _weighted?.Visited ?? _navigation?.LastVisitedCells ?? 0;
    public double NavigationBuildMilliseconds => _weighted?.Milliseconds ?? _navigation?.LastBuildMilliseconds ?? 0;
    public int MovingCount => CountMoving();
    public ReadOnlySpan<WorldEvent> Events => CollectionsMarshal.AsSpan(_events);
    public ReadOnlySpan<WorldCommand> AppliedCommands => _commands.AsSpan(0, _commandCount);

    public SimulationWorld(IEnumerable<UnitDefinition> unitTypes, int width, int depth, ulong seed = 1,
        int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128,
        NavigationGrid? navigationGrid = null, int navigationWorkers = 1, int fieldsPerTick = 2,
        int cachedFields = 8, bool reverseNavigationJobs = false, IEnumerable<PlayerDefinition>? players = null)
    {
        if (width < 1 || width > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 1 || depth > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(depth));
        if (tickRate < 1 || tickRate > 1000) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (speedPerTick < 1 || speedPerTick > 1 << 20) throw new ArgumentOutOfRangeException(nameof(speedPerTick));
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _types = unitTypes.ToArray();
        if (_types.Length == 0 || _types.Any(t => t is null) || _types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != _types.Length)
            throw new ArgumentException("Unit definitions must be nonempty, nonnull and uniquely named.", nameof(unitTypes));
        InitializePlayers(players);
        InitializeProduction();
        Width = width;
        Depth = depth;
        TickRate = tickRate;
        SpeedPerTick = speedPerTick;
        _unitHandles = new Frent.Entity[initialCapacity];
        _indices = new(initialCapacity);
        _random = new(seed);
        _idOrder = Comparer<int>.Create((a, b) => _unitHandles[a].Get<UnitIdentity>().Id.Value.CompareTo(_unitHandles[b].Get<UnitIdentity>().Id.Value));
        if (navigationGrid is not null)
        {
            if (speedPerTick > 1024) throw new ArgumentOutOfRangeException(nameof(speedPerTick), "The current local motion budget allows at most 1024 world units per tick.");
            if (width != (long)navigationGrid.Width * navigationGrid.CellSize || depth != (long)navigationGrid.Height * navigationGrid.CellSize)
                throw new ArgumentException("World bounds must match the navigation grid.", nameof(navigationGrid));
            _baseGrid = navigationGrid;
            _navigation = new(navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs);
            InitializeLocomotion(navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs);
            _motion = new(speedPerTick);
            _motion.Clear(initialCapacity);
        }
        if (_motion is null && _types.Any(t => t.Weapon is not null || t.Footprint is not null || t.TaskQueues.Count > 0 || t.Transport is not null)) _combatIndex = new(speedPerTick);
    }

    public EntityId AddInitialUnit(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
    {
        ThrowIfDisposed();
        if (Tick != 0) throw new InvalidOperationException("After initialization, entities are created through commands.");
        ValidatePosition(position);
        ValidateType(unitType);
        if (_types[unitType].Footprint is null && !StopAllowed(unitType, position)) throw new ArgumentException("Initial terrain does not permit stopping.", nameof(position));
        if (!PrepareFootprint(unitType, position, out var footprintGrid)) throw new ArgumentException("Footprint placement is out of bounds, misaligned or would trap an existing body.", nameof(position));
        if (_navigation is not null && _types[unitType].Footprint is null)
        {
            if (!_motion!.ClearPlacement(position, out _, _types[unitType].Body))
                throw new ArgumentException("Initial navigation placement is blocked or occupied.", nameof(position));
        }
        if ((states & ~_types[unitType].AllowedStateMask) != 0) throw new ArgumentOutOfRangeException(nameof(states));
        try
        {
            var id = Spawn(unitType, position, owner, states);
            if (footprintGrid is not null) CommitGrid(footprintGrid);
            return id;
        }
        catch { _failed = true; throw; }
    }

    public void Step(ReadOnlySpan<WorldCommand> commands = default, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot be advanced.");
        cancellationToken.ThrowIfCancellationRequested();
        long nextTick = checked(Tick + 1);
        if (_commands.Length < commands.Length) Array.Resize(ref _commands, commands.Length);
        commands.CopyTo(_commands);
        Array.Sort(_commands, 0, commands.Length, CommandOrder.Instance);
        for (int i = 0; i < commands.Length; i++)
        {
            var command = _commands[i];
            if (command.Tick != nextTick || command.Sequence <= 0 || !Enum.IsDefined(command.Kind))
                throw new ArgumentException("Commands must belong to the next tick and have a positive sequence and a known kind.", nameof(commands));
            if (i > 0 && CommandOrder.Instance.Compare(_commands[i - 1], command) == 0)
                throw new ArgumentException("Duplicate issuer/sequence in command batch.", nameof(commands));
            if (command.Kind is CommandKind.Move or CommandKind.MoveAll or CommandKind.MoveGroup or CommandKind.Scatter or CommandKind.Spawn or CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.Aim or CommandKind.AttackMove or CommandKind.MoveSelected or CommandKind.UseAbility or CommandKind.PlaceProduced or CommandKind.SetRally or CommandKind.UnloadAt)
                ValidatePosition(command.Position);
            if (command.Kind is CommandKind.UseAbility or CommandKind.SetAutoCast or CommandKind.EnqueueTask or CommandKind.CancelTasks or CommandKind.PlaceProduced)
                if (string.IsNullOrWhiteSpace(command.Action) || command.Action.Length > 128 || command.Amount < 1 || command.Kind == CommandKind.EnqueueTask && command.Amount > 256 || command.TaskId < 0)
                    throw new ArgumentException("Custom actions require a valid ID, amount and task identity.", nameof(commands));
            if (command.Kind == CommandKind.Scatter && (command.Position.X == 0 || command.Position.Y == 0))
                throw new ArgumentException("Scatter area must have positive extents.", nameof(commands));
            if (command.Kind == CommandKind.Spawn) ValidateType(command.UnitType);
            if (command.Kind == CommandKind.PlayAnimation && (command.Animation < 0 || command.DurationTicks < 1 ||
                command.DurationTicks > 1000000 || !Enum.IsDefined(command.Track)))
                throw new ArgumentException("Animation commands require a valid track, nonnegative ID and duration in 1..1000000 ticks.", nameof(commands));
            if ((command.Kind is CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.MoveGroup) && _navigation is null)
                throw new ArgumentException("This world has no navigation layer.", nameof(commands));
        }

        _events.Clear();
        RebuildMotionIndex();
        _commandCount = commands.Length;
        Tick = nextTick;
        _stepCancellation = cancellationToken;
        try { foreach (var command in AppliedCommands) Apply(command); }
        catch { _failed = true; throw; }

        try
        {
            CapturePreviousPositions();
            CapturePreviousFacings();
            UpdateOrders();
            if (_transportIndexDirty) RebuildMotionIndex();
            if (_navigation is not null) UpdateNavigation(cancellationToken);
            else UpdateDirectMovement();
            UpdateWeapons();
            UpdateIncome();
            UpdateProduction();
            UpdatePresentation();
        }
        catch { _failed = true; throw; }
    }

    public void CopyPoses(Span<EntityPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < Count) throw new ArgumentException("Destination is too small.", nameof(destination));
        int weaponOffset = 0;
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _unitHandles[i];
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityPresentation = ref entity.Get<PresentationState>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            ref var entityCombat = ref entity.Get<CombatState>();
            ref var entityOrders = ref entity.Get<OrderState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            ref var entityNavigation = ref entity.Get<NavigationAgentState>();
            ref var spatial = ref entity.Get<SpatialState>();
            int health = entity.Get<HealthState>().Current;
            TryGetProducer(i, out var producer);
            uint states = entityPresentation.VisualStates;
            if (entity.Has<TransportState>() && TypeOf(entity).States.Contains("garrisoned"))
            {
                uint mask = TypeOf(entity).StateMask("garrisoned");
                states = HasPassengers(entity) ? states | mask : states & ~mask;
            }
            var carrier = CarrierOf(entity);
            if (carrier.Value != 0) spatial = ref _unitHandles[_indices[carrier]].Get<SpatialState>();
            if (_types[entityIdentity.UnitType].Economy is { } power && LowPower(entityOwnership.Owner)) states |= _types[entityIdentity.UnitType].StateMask(power.LowPowerState);
            if (producer is not null) for (int q = 0; q < producer.Queues.Length; q++)
                if (producer.Queues[q] is { Count: > 0 } tasks && !tasks[0].Ready && _types[entityIdentity.UnitType].TaskQueues[q].WorkingState is { } state)
                    states |= _types[entityIdentity.UnitType].StateMask(state);
            int captureProgress = 0, captureDuration = 0;
            if (entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Capture && entity.Has<CaptureState>() && _indices.TryGetValue(entityOrders.ActiveOrder.Target, out int captureTarget))
            { captureProgress = entity.Get<CaptureState>().Progress; var targetType = _types[_unitHandles[captureTarget].Get<UnitIdentity>().UnitType]; captureDuration = targetType.Capturable?.Ticks ?? targetType.MaxHealth; }
            destination[i] = new(entityIdentity.Id, entityIdentity.UnitType, spatial.Previous, spatial.Position,
                new(entityOwnership.Owner, entityPresentation.PreviousBodyFacing, entityPresentation.BodyFacing, entityPresentation.PreviousWeaponFacing, entityPresentation.WeaponFacing,
                    entityPresentation.BodyAnimation, entityPresentation.WeaponAnimation, states),
                new(entityOrders.ActiveOrder.Kind, entityOrders.OrderPhase, entityMotion.Target, entityOrders.CombatTarget, entityOrders.PendingOrders?.Count ?? 0, entityCombat.WeaponReadyTick, captureProgress, captureDuration), health, weaponOffset, entityCombat.Weapons.Length, entityNavigation.TerrainStranded, producer?.HasRally ?? false, producer?.Rally ?? default, carrier);
            weaponOffset += entityCombat.Weapons.Length;
        }
    }

    public SimulationReport Report()
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world has no completed state.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_navigation is null ? 9 : 13);
            writer.Write(Tick); writer.Write(Width); writer.Write(Depth);
            writer.Write(TickRate); writer.Write(SpeedPerTick); writer.Write(_nextId); writer.Write(_random.State);
            writer.Write(_types.Length);
            foreach (var type in _types)
            {
                type.WriteState(writer);
            }
            writer.Write(_orderGroupSequence); writer.Write(Count);
            foreach (var entity in _unitHandles.AsSpan(0, Count).ToArray().OrderBy(e => e.Get<UnitIdentity>().Id.Value))
            {
                var entityIdentity = entity.Get<UnitIdentity>();
                ref var entityMotion = ref entity.Get<MotionState>();
                ref var entityNavigation = ref entity.Get<NavigationAgentState>();
                var position = entity.Get<SpatialState>().Position;
                writer.Write(entityIdentity.Id.Value); writer.Write(entityIdentity.UnitType); writer.Write(entity.Get<HealthState>().Current);
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Elevation);
                writer.Write(entityMotion.Target.X); writer.Write(entityMotion.Target.Y); writer.Write(entityMotion.Target.Elevation);
                writer.Write(entityMotion.Moving); writer.Write(entityNavigation.TerrainStranded);
                writer.Write(entityMotion.MovementRemainder);
                WritePresentation(writer, entity); WriteOrders(writer, entity); WriteWeapons(writer, entity); WriteTransport(writer, entity);
                if (_navigation is not null)
                {
                    writer.Write(entityNavigation.GoalCell); writer.Write(entityNavigation.GroupMember); writer.Write(entityNavigation.WaitTicks);
                    writer.Write(entityNavigation.YieldFor.Value); writer.Write(entityNavigation.YieldDeadline);
                    writer.Write(entityNavigation.LastProgressTick);
                    writer.Write(entityNavigation.LocalTarget.X); writer.Write(entityNavigation.LocalTarget.Y);
                    writer.Write(entityNavigation.PathLength - entityNavigation.PathCursor);
                    int index = _indices[entityIdentity.Id];
                    foreach (var point in Path(index)[entityNavigation.PathCursor..entityNavigation.PathLength])
                    { writer.Write(point.X); writer.Write(point.Y); }
                }
            }
            WriteProjectiles(writer); WriteProduction(writer);
            WritePlayers(writer);
            writer.Write(_baseGrid is not null); _baseGrid?.WriteState(writer);
            _navigation?.WriteState(writer);
            writer.Write(_weighted is not null); _weighted?.WriteState(writer);
            if (_navigation is not null)
            {
                writer.Write(_groupSlots.Length);
                foreach (var point in _groupSlots) { writer.Write(point.X); writer.Write(point.Y); }
                WriteGroupState(writer);
            }
        }
        return new(Tick, Count, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    private void Apply(WorldCommand command)
    {
        if (command.Kind is CommandKind.UseAbility or CommandKind.SetAutoCast) { ApplyAbility(command); return; }
        if (command.Kind is CommandKind.EnqueueTask or CommandKind.CancelTasks or CommandKind.PlaceProduced or CommandKind.SetRally) { ApplyProduction(command); return; }
        if (command.Kind == CommandKind.Unload)
        {
            foreach (int member in Recipients(command)) Unload(member, command.TargetEntity);
            return;
        }
        if (command.Kind is CommandKind.Move or CommandKind.MoveGroup or CommandKind.MoveSelected or CommandKind.AttackMove or CommandKind.AttackTarget or CommandKind.RepairTarget or CommandKind.CaptureTarget or CommandKind.Enter or CommandKind.UnloadAt or CommandKind.Stop or CommandKind.HoldPosition)
        {
            var members = Recipients(command);
            if (command.Kind is CommandKind.Move or CommandKind.MoveSelected or CommandKind.MoveGroup)
            { foreach (int i in members) if (!_types[_unitHandles[i].Get<UnitIdentity>().UnitType].Movable && TryGetProducer(i, out _)) SetRally(i, command.Position);
                members = members.Where(i => _types[_unitHandles[i].Get<UnitIdentity>().UnitType].Movable || !TryGetProducer(i, out _)).ToArray(); }
            if (members.Length == 0) return;
            var action = command.Kind switch { CommandKind.AttackMove => UnitActionKind.AttackMove, CommandKind.AttackTarget => UnitActionKind.Attack, CommandKind.RepairTarget => UnitActionKind.Repair, CommandKind.CaptureTarget => UnitActionKind.Capture,
                CommandKind.Enter => UnitActionKind.Enter, CommandKind.UnloadAt => UnitActionKind.UnloadAt, CommandKind.Stop => UnitActionKind.Stop, CommandKind.HoldPosition => UnitActionKind.HoldPosition, _ => UnitActionKind.Move };
            if (_navigation is not null && (command.Kind is CommandKind.MoveGroup or CommandKind.MoveSelected || command.Kind == CommandKind.AttackMove && members.Length > 1))
                SetGroupTarget(command.Position, members, action, command.Queued);
            else foreach (int member in members) IssueOrder(member, new(action, command.Position, command.TargetEntity), command.Queued);
            return;
        }
        if (command.Kind is CommandKind.BlockCell or CommandKind.UnblockCell)
        {
            SetBaseBlocked(_navigation!.Grid.CellAt(command.Position), command.Kind == CommandKind.BlockCell);
            return;
        }
        if (command.Kind == CommandKind.Spawn)
        {
            bool footprint = _types[command.UnitType].Footprint is not null;
            bool terrain = footprint || StopAllowed(command.UnitType, command.Position);
            bool free = footprint || _motion is null || _motion.ClearPlacement(command.Position, out _, _types[command.UnitType].Body);
            bool stamp = PrepareFootprint(command.UnitType, command.Position, out var footprintGrid);
            terrain &= stamp;
            if (!terrain || !free)
            {
                _events.Add(new(Tick, !free ? WorldEventKind.CellOccupied : WorldEventKind.CellBlocked,
                    default, command.Position, command.UnitType));
                return;
            }
            if ((command.States & ~_types[command.UnitType].AllowedStateMask) != 0)
            { _events.Add(new(Tick, WorldEventKind.InvalidPresentation, default, command.Position, command.UnitType)); return; }
            var id = Spawn(command.UnitType, command.Position, command.Owner, command.States);
            if (footprintGrid is not null) CommitGrid(footprintGrid);
            _events.Add(new(Tick, WorldEventKind.Spawned, id, command.Position, command.UnitType));
            return;
        }
        if (command.Kind is CommandKind.MoveAll or CommandKind.Scatter)
        {
            ulong seed = command.Kind == CommandKind.Scatter ? _random.NextUInt64() : 0;
            for (int i = 0; i < Count; i++)
            {
                // Per-entity streams make this independent of packed storage and future task scheduling.
                var random = new DeterministicRandom(seed ^ _unitHandles[i].Get<UnitIdentity>().Id.Value);
                var target = command.Kind == CommandKind.Scatter
                    ? new WorldPosition(random.NextInt(command.Position.X), random.NextInt(command.Position.Y)) : command.Position;
                IssueOrder(i, new(UnitActionKind.Move, target), false);
            }
            return;
        }
        if (!_indices.TryGetValue(command.Entity, out int index))
        {
            _events.Add(new(Tick, WorldEventKind.MissingEntity, command.Entity));
            return;
        }
        if (command.Kind is CommandKind.PlayAnimation or CommandKind.SetVisualStates or CommandKind.ToggleVisualState or CommandKind.Aim or CommandKind.ClearAim)
        { ApplyPresentation(index, command); return; }
        DestroyIndex(index, command.Kind != CommandKind.Remove); RebuildMotionIndex();
    }

    private void DestroyIndex(int index, bool wreck = true)
    {
        var actor = _unitHandles[index];
        var id = actor.Get<UnitIdentity>().Id;
        if (IsStored(actor)) { DetachPassenger(actor); wreck = false; }
        RemovePassengers(actor);
        index = _indices[id];
        if (_unitHandles[index].Has<ProducerState>()) _producersDirty = true;
        bool stamped = _footprints.Remove(id);
        _events.Add(new(Tick, wreck ? WorldEventKind.Destroyed : WorldEventKind.Removed, id, _unitHandles[index].Get<SpatialState>().Position, _unitHandles[index].Get<UnitIdentity>().UnitType, Owner: _unitHandles[index].Get<OwnershipState>().Owner));
        _powerDirty = true;
        _unitHandles[index].Delete();
        _indices.Remove(id); _orderDirty = true; Count--;
        if (index != Count)
        {
            _unitHandles[index] = _unitHandles[Count];
            _indices[_unitHandles[index].Get<UnitIdentity>().Id] = index;
        }
        _unitHandles[Count] = default;
        if (stamped && _baseGrid is not null) CommitGrid(ComposeFootprints(_baseGrid));
    }

    private bool SetTarget(int index, WorldPosition position)
    {
        ref var entity = ref _unitHandles[index];
        ref var entitySpatial = ref entity.Get<SpatialState>();
        var entityIdentity = entity.Get<UnitIdentity>();
        ref var entityNavigation = ref entity.Get<NavigationAgentState>();
        ref var entityMotion = ref entity.Get<MotionState>();
        if (!_types[entityIdentity.UnitType].Movable && position != entitySpatial.Position)
        { _events.Add(new(Tick, WorldEventKind.InvalidOrder, entityIdentity.Id)); return false; }
        if (_navigation is not null)
        {
            if (!Resolver(entityIdentity.UnitType).Resolve(entitySpatial.Position, position, out var resolved, _stepCancellation))
            { _events.Add(new(Tick, WorldEventKind.NoRoute, entityIdentity.Id, position, entityIdentity.UnitType)); return false; }
            if (resolved != position) _events.Add(new(Tick, WorldEventKind.DestinationAdjusted, entityIdentity.Id, resolved, entityIdentity.UnitType));
            position = resolved;
        }
        else if (!StopAllowed(entityIdentity.UnitType, position))
        { _events.Add(new(Tick, WorldEventKind.NoRoute, entityIdentity.Id, position, entityIdentity.UnitType)); return false; }
        entityNavigation.GroupMember = false; entityNavigation.OrderGroup = 0; entityMotion.MovementRemainder = 0;
        entityNavigation.WaitTicks = entityNavigation.PathLength = entityNavigation.PathCursor = 0;
        entityNavigation.YieldFor = default; entityNavigation.YieldDeadline = 0; entityNavigation.LocalTarget = default;
        entityNavigation.Route = null;
        entityMotion.Target = GroundPosition(position, entitySpatial.Position.Elevation);
        if (_navigation is not null)
        {
            entityNavigation.GoalCell = _navigation.Grid.CellAt(position);
        }
        entityMotion.Moving = entitySpatial.Position != entityMotion.Target;
        return true;
    }

    private EntityId Spawn(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
    {
        if (_nextId == uint.MaxValue) throw new InvalidOperationException("Entity ID space exhausted.");
        if (Count == _unitHandles.Length)
        {
            Array.Resize(ref _unitHandles, checked(Math.Max(16, Count * 2)));
        }
        var id = new EntityId(_nextId);
        Frent.Entity state;
        var type = _types[unitType];
        _unitStorage ??= new();
        state = type.TaskQueues.Count == 0
            ? _unitStorage.Create(new UnitIdentity(id, unitType), type.Definition, new SpatialState(position), new HealthState(type.MaxHealth), new OrderState(), new MotionState(position), new NavigationAgentState(_navigation?.Grid.CellAt(position) ?? -1, _navigation is not null), new CombatState(type), new PresentationState(type.IdleAnimation, Tick, states), new OwnershipState(owner))
            : _unitStorage.Create(new UnitIdentity(id, unitType), type.Definition, new SpatialState(position), new HealthState(type.MaxHealth), new OrderState(), new MotionState(position), new NavigationAgentState(_navigation?.Grid.CellAt(position) ?? -1, _navigation is not null), new CombatState(type), new PresentationState(type.IdleAnimation, Tick, states), new OwnershipState(owner), new ProducerState(type.TaskQueues.Count));
        if (type.Passenger is not null) state.Add(new PassengerState());
        if (type.Transport is not null) state.Add(new TransportState());
        Player(owner);
        if (type.Economy?.Income > 0) state.Add(new IncomeState(checked(Tick + type.Economy.Interval)));
        if (type.Capturable is not null) state.Add(new CapturableState());
        if (type.Abilities.Any(a => a.Kind == AbilityKind.Capture)) state.Add(new CaptureState());
        _powerDirty = true;
        _unitHandles[Count] = state;
        _nextId++;
        (_motion ?? _combatIndex)?.Add(id, position, type.Body, type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight);
        if (type.Footprint is not null) _footprints.Add(id);
        _indices.Add(id, Count++);
        if (type.TaskQueues.Count > 0) _producersDirty = true;
        for (int a = 0; a < type.Abilities.Count; a++) if (type.Abilities[a].Kind == AbilityKind.Weapon) _unitHandles[_indices[id]].Get<CombatState>().Weapons[AbilityMount(unitType, a)].AutoCast = type.Abilities[a].AutoCast;
        _orderDirty = true;
        return id;
    }

    private WorldPosition StepTowards(WorldPosition position, WorldPosition target) => StepTowards(position, target, SpeedPerTick);

    private static WorldPosition StepTowards(WorldPosition position, WorldPosition target, int speed)
    {
        long dx = (long)target.X - position.X, dy = (long)target.Y - position.Y;
        ulong squared = (ulong)(dx * dx + dy * dy);
        if (squared <= (ulong)speed * (ulong)speed) return target;
        long distance = (long)SquareRoot(squared);
        if ((ulong)(distance * distance) < squared) distance++;
        int stepX = (int)(dx * speed / distance), stepY = (int)(dy * speed / distance);
        if (stepX == 0 && stepY == 0)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) stepX = Math.Sign(dx); else stepY = Math.Sign(dy);
        }
        return new(checked(position.X + stepX), checked(position.Y + stepY), position.Elevation);
    }

    private void ValidateType(int type)
    {
        if ((uint)type >= (uint)_types.Length) throw new ArgumentOutOfRangeException(nameof(type));
    }

    private void ValidatePosition(WorldPosition position)
    {
        if (position.X < 0 || position.X > Width || position.Y < 0 || position.Y > Depth ||
            Math.Abs((long)position.Elevation) > WorldPosition.MaximumExtent)
            throw new ArgumentOutOfRangeException(nameof(position), "Position is outside world bounds.");
    }

    internal static ulong SquareRoot(ulong value)
    {
        ulong result = 0, bit = 1UL << 62;
        while (bit > value) bit >>= 2;
        while (bit != 0)
        {
            if (value >= result + bit) { value -= result + bit; result = (result >> 1) + bit; }
            else result >>= 1;
            bit >>= 2;
        }
        return result;
    }

    private sealed class CommandOrder : IComparer<WorldCommand>
    {
        public static readonly CommandOrder Instance = new();
        public int Compare(WorldCommand a, WorldCommand b)
        {
            int issuer = a.Issuer.CompareTo(b.Issuer);
            return issuer != 0 ? issuer : a.Sequence.CompareTo(b.Sequence);
        }
    }
}
