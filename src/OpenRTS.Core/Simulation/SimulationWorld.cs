using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core.Production;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

/// <summary>Single-owner world. Hosts exchange commands and copied poses, never its mutable storage.</summary>
public sealed partial class SimulationWorld : IDisposable
{
    private struct EntityState
    {
        public EntityId Id;
        // shortcut: keep handles in packed state until E4 moves hot components into unit storage.
        public Frent.Entity StorageEntity;
        public int UnitType;
        public WorldPosition Target;
        // shortcut: remaining order/navigation/combat code uses these live ECS views until its E4 slice.
        public readonly int Health => StorageEntity.Get<HealthState>().Current;
        public readonly WorldPosition Position => StorageEntity.Get<SpatialState>().Position;
        public readonly WorldPosition Previous => StorageEntity.Get<SpatialState>().Previous;
        public bool Moving, TerrainStranded;
        public int GoalCell;
        public int MovementRemainder;
        public UnitOrder ActiveOrder;
        public bool HasOrder;
        public OrderPhase OrderPhase;
        public Queue<UnitOrder>? PendingOrders;
        public WeaponState[] Weapons;
        public EntityId CombatTarget;
        public long WeaponReadyTick, OrderStarted, OrderGroup;
        public bool GroupMember;
        public int WaitTicks, PathLength, PathCursor;
        public EntityId YieldFor;
        public long YieldDeadline;
        public long LastProgressTick;
        public WorldPosition LocalTarget;
        public uint Owner, VisualStates;
        public FacingDirection BodyFacing, PreviousBodyFacing, WeaponFacing, PreviousWeaponFacing;
        public AnimationPlayback BodyAnimation, WeaponAnimation;
        public bool BodyAnimationOverride, HasAim;
        public WorldPosition AimPoint;
        public EntityId AimEntity;
    }

    private readonly UnitDefinition[] _types;
    private EntityState[] _entities;
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
    public int MovingCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Count; i++) if (_entities[i].Moving) count++;
            return count;
        }
    }
    public ReadOnlySpan<WorldEvent> Events => CollectionsMarshal.AsSpan(_events);
    public ReadOnlySpan<WorldCommand> AppliedCommands => _commands.AsSpan(0, _commandCount);

    public SimulationWorld(IEnumerable<UnitDefinition> unitTypes, int width, int depth, ulong seed = 1,
        int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128,
        NavigationGrid? navigationGrid = null, int navigationWorkers = 1, int fieldsPerTick = 2,
        int cachedFields = 8, bool reverseNavigationJobs = false)
    {
        if (width < 1 || width > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 1 || depth > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(depth));
        if (tickRate < 1 || tickRate > 1000) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (speedPerTick < 1 || speedPerTick > 1 << 20) throw new ArgumentOutOfRangeException(nameof(speedPerTick));
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _types = unitTypes.ToArray();
        if (_types.Length == 0 || _types.Any(t => t is null) || _types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != _types.Length)
            throw new ArgumentException("Unit definitions must be nonempty, nonnull and uniquely named.", nameof(unitTypes));
        InitializeProduction();
        Width = width;
        Depth = depth;
        TickRate = tickRate;
        SpeedPerTick = speedPerTick;
        _entities = new EntityState[initialCapacity];
        _indices = new(initialCapacity);
        _random = new(seed);
        _idOrder = Comparer<int>.Create((a, b) => _entities[a].Id.Value.CompareTo(_entities[b].Id.Value));
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
            _localPaths = new WorldPosition[checked(initialCapacity * LocalMotionSearch.PathCapacity)];
        }
        if (_motion is null && _types.Any(t => t.Weapon is not null || t.Footprint is not null || t.TaskQueues.Count > 0)) _combatIndex = new(speedPerTick);
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
            if (command.Kind is CommandKind.Move or CommandKind.MoveAll or CommandKind.MoveGroup or CommandKind.Scatter or CommandKind.Spawn or CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.Aim or CommandKind.AttackMove or CommandKind.MoveSelected or CommandKind.UseAbility or CommandKind.PlaceProduced or CommandKind.SetRally)
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
            for (int i = 0; i < Count; i++)
            {
                ref var entity = ref _entities[i];
                foreach (ref var mount in entity.Weapons.AsSpan()) mount.PreviousFacing = mount.Facing;
                entity.PreviousBodyFacing = entity.BodyFacing; entity.PreviousWeaponFacing = entity.WeaponFacing;
            }
            UpdateOrders();
            if (_navigation is not null) UpdateNavigation(cancellationToken);
            else for (int i = 0; i < Count; i++)
                if (_entities[i].Moving && Advance(ref _entities[i], _entities[i].Target)) _entities[i].Moving = false;
            UpdateWeapons();
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
            ref var entity = ref _entities[i];
            ref var spatial = ref entity.StorageEntity.Get<SpatialState>();
            int health = entity.StorageEntity.Get<HealthState>().Current;
            TryGetProducer(i, out var producer);
            uint states = entity.VisualStates;
            if (producer is not null) for (int q = 0; q < producer.Queues.Length; q++)
                if (producer.Queues[q] is { Count: > 0 } tasks && !tasks[0].Ready && _types[entity.UnitType].TaskQueues[q].WorkingState is { } state)
                    states |= _types[entity.UnitType].StateMask(state);
            destination[i] = new(entity.Id, entity.UnitType, spatial.Previous, spatial.Position,
                new(entity.Owner, entity.PreviousBodyFacing, entity.BodyFacing, entity.PreviousWeaponFacing, entity.WeaponFacing,
                    entity.BodyAnimation, entity.WeaponAnimation, states),
                new(entity.ActiveOrder.Kind, entity.OrderPhase, entity.Target, entity.CombatTarget, entity.PendingOrders?.Count ?? 0, entity.WeaponReadyTick), health, weaponOffset, entity.Weapons.Length, entity.TerrainStranded, producer?.HasRally ?? false, producer?.Rally ?? default);
            weaponOffset += entity.Weapons.Length;
        }
    }

    public SimulationReport Report()
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world has no completed state.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_navigation is null ? 7 : 11);
            writer.Write(Tick); writer.Write(Width); writer.Write(Depth);
            writer.Write(TickRate); writer.Write(SpeedPerTick); writer.Write(_nextId); writer.Write(_random.State);
            writer.Write(_types.Length);
            foreach (var type in _types)
            {
                type.WriteState(writer);
            }
            writer.Write(_orderGroupSequence); writer.Write(Count);
            foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
            {
                var position = entity.StorageEntity.Get<SpatialState>().Position;
                writer.Write(entity.Id.Value); writer.Write(entity.UnitType); writer.Write(entity.Health);
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Elevation);
                writer.Write(entity.Target.X); writer.Write(entity.Target.Y); writer.Write(entity.Target.Elevation);
                writer.Write(entity.Moving); writer.Write(entity.TerrainStranded);
                writer.Write(entity.MovementRemainder);
                WritePresentation(writer, entity); WriteOrders(writer, entity); WriteWeapons(writer, entity);
                if (_navigation is not null)
                {
                    writer.Write(entity.GoalCell); writer.Write(entity.GroupMember); writer.Write(entity.WaitTicks);
                    writer.Write(entity.YieldFor.Value); writer.Write(entity.YieldDeadline);
                    writer.Write(entity.LastProgressTick);
                    writer.Write(entity.LocalTarget.X); writer.Write(entity.LocalTarget.Y);
                    writer.Write(entity.PathLength - entity.PathCursor);
                    int index = _indices[entity.Id];
                    foreach (var point in Path(index)[entity.PathCursor..entity.PathLength])
                    { writer.Write(point.X); writer.Write(point.Y); }
                }
            }
            WriteProjectiles(writer); WriteProduction(writer);
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
        if (command.Kind is CommandKind.Move or CommandKind.MoveGroup or CommandKind.MoveSelected or CommandKind.AttackMove or CommandKind.AttackTarget or CommandKind.RepairTarget or CommandKind.Stop or CommandKind.HoldPosition)
        {
            var members = Recipients(command);
            if (command.Kind is CommandKind.Move or CommandKind.MoveSelected or CommandKind.MoveGroup)
            { foreach (int i in members) if (!_types[_entities[i].UnitType].Movable && TryGetProducer(i, out _)) SetRally(i, command.Position);
                members = members.Where(i => _types[_entities[i].UnitType].Movable || !TryGetProducer(i, out _)).ToArray(); }
            if (members.Length == 0) return;
            var action = command.Kind switch { CommandKind.AttackMove => UnitActionKind.AttackMove, CommandKind.AttackTarget => UnitActionKind.Attack, CommandKind.RepairTarget => UnitActionKind.Repair,
                CommandKind.Stop => UnitActionKind.Stop, CommandKind.HoldPosition => UnitActionKind.HoldPosition, _ => UnitActionKind.Move };
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
                var random = new DeterministicRandom(seed ^ _entities[i].Id.Value);
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
        var id = _entities[index].Id;
        if (_entities[index].StorageEntity.Has<ProducerState>()) _producersDirty = true;
        bool stamped = _footprints.Remove(id);
        _events.Add(new(Tick, wreck ? WorldEventKind.Destroyed : WorldEventKind.Removed, id, _entities[index].Position, _entities[index].UnitType, Owner: _entities[index].Owner));
        _entities[index].StorageEntity.Delete();
        _groupRoutes.Remove(id); _indices.Remove(id); _orderDirty = true; Count--;
        if (index != Count)
        {
            _entities[index] = _entities[Count];
            if (_navigation is not null) Path(Count).CopyTo(Path(index));
            _indices[_entities[index].Id] = index;
        }
        _entities[Count] = default;
        if (stamped && _baseGrid is not null) CommitGrid(ComposeFootprints(_baseGrid));
    }

    private bool SetTarget(int index, WorldPosition position)
    {
        ref var entity = ref _entities[index];
        if (!_types[entity.UnitType].Movable && position != entity.Position)
        { _events.Add(new(Tick, WorldEventKind.InvalidOrder, entity.Id)); return false; }
        if (_navigation is not null)
        {
            if (!Resolver(entity.UnitType).Resolve(entity.Position, position, out var resolved, _stepCancellation))
            { _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, position, entity.UnitType)); return false; }
            if (resolved != position) _events.Add(new(Tick, WorldEventKind.DestinationAdjusted, entity.Id, resolved, entity.UnitType));
            position = resolved;
        }
        else if (!StopAllowed(entity.UnitType, position))
        { _events.Add(new(Tick, WorldEventKind.NoRoute, entity.Id, position, entity.UnitType)); return false; }
        entity.GroupMember = false; entity.OrderGroup = 0; entity.MovementRemainder = 0;
        entity.WaitTicks = entity.PathLength = entity.PathCursor = 0;
        entity.YieldFor = default; entity.YieldDeadline = 0; entity.LocalTarget = default;
        _groupRoutes.Remove(entity.Id);
        entity.Target = GroundPosition(position, entity.Position.Elevation);
        if (_navigation is not null)
        {
            entity.GoalCell = _navigation.Grid.CellAt(position);
        }
        entity.Moving = entity.Position != entity.Target;
        return true;
    }

    private EntityId Spawn(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
    {
        if (_nextId == uint.MaxValue) throw new InvalidOperationException("Entity ID space exhausted.");
        if (Count == _entities.Length)
        {
            Array.Resize(ref _entities, checked(Math.Max(16, Count * 2)));
            if (_navigation is not null) Array.Resize(ref _localPaths, checked(_entities.Length * LocalMotionSearch.PathCapacity));
        }
        var id = new EntityId(_nextId);
        var state = new EntityState { Id = id, UnitType = unitType,
            Target = position, Owner = owner, VisualStates = states,
            Weapons = _types[unitType].Weapons.Select(_ => new WeaponState { Facing = FacingDirection.North, PreviousFacing = FacingDirection.North, Animation = AnimationPlayback.None }).ToArray(),
            BodyFacing = FacingDirection.North, PreviousBodyFacing = FacingDirection.North,
            WeaponFacing = FacingDirection.North, PreviousWeaponFacing = FacingDirection.North,
            BodyAnimation = new(_types[unitType].IdleAnimation, Tick, 0), WeaponAnimation = AnimationPlayback.None,
            GoalCell = _navigation?.Grid.CellAt(position) ?? -1 };
        var type = _types[unitType];
        _unitStorage ??= new();
        state.StorageEntity = type.TaskQueues.Count == 0
            ? _unitStorage.Create(new UnitIdentity { Id = id }, type.Definition, new SpatialState(position), new HealthState(type.MaxHealth))
            : _unitStorage.Create(new UnitIdentity { Id = id }, type.Definition, new SpatialState(position), new HealthState(type.MaxHealth), new ProducerState(type.TaskQueues.Count));
        _entities[Count] = state;
        _nextId++;
        (_motion ?? _combatIndex)?.Add(Count, id, position, owner, type.Body, type.Footprint?.HalfWidth ?? type.Body.HalfWidth, type.Footprint?.HalfHeight ?? type.Body.HalfHeight);
        if (type.Footprint is not null) _footprints.Add(id);
        _indices.Add(id, Count++);
        if (type.TaskQueues.Count > 0) _producersDirty = true;
        for (int a = 0; a < type.Abilities.Count; a++) _entities[_indices[id]].Weapons[AbilityMount(unitType, a)].AutoCast = type.Abilities[a].AutoCast;
        _orderDirty = true;
        return id;
    }

    private bool Advance(ref EntityState entity, WorldPosition target)
    {
        ref var spatial = ref entity.StorageEntity.Get<SpatialState>();
        int factor = _types[entity.UnitType].Locomotor.Rules[0].Speed;
        if (factor == 1024) spatial.Position = StepTowards(spatial.Position, target);
        else
        {
            int budget = SpeedPerTick * factor + entity.MovementRemainder;
            entity.MovementRemainder = budget % 1024;
            if (budget >= 1024) spatial.Position = StepTowards(spatial.Position, target, budget / 1024);
        }
        return spatial.Position == target;
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
