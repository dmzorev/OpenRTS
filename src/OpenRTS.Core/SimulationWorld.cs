using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenRTS.Core;

/// <summary>Single-owner world. Hosts exchange commands and copied poses, never its mutable storage.</summary>
public sealed partial class SimulationWorld
{
    private struct EntityState
    {
        public EntityId Id;
        public int UnitType, Health;
        public WorldPosition Previous, Position, Target;
        public bool Moving;
        public int GoalCell;
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
    public int NavigationBuiltFields => _navigation?.LastBuildCount ?? 0;
    public int NavigationVisitedCells => _navigation?.LastVisitedCells ?? 0;
    public double NavigationBuildMilliseconds => _navigation?.LastBuildMilliseconds ?? 0;
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
            if (navigationGrid.CellSize < MotionIndex.BodyRadius * 2 + 2)
                throw new ArgumentException("Terrain samples must leave clearance for the current body radius.", nameof(navigationGrid));
            if (width != (long)navigationGrid.Width * navigationGrid.CellSize || depth != (long)navigationGrid.Height * navigationGrid.CellSize)
                throw new ArgumentException("World bounds must match the navigation grid.", nameof(navigationGrid));
            _navigation = new(navigationGrid, navigationWorkers, fieldsPerTick, cachedFields, reverseNavigationJobs);
            _motion = new(speedPerTick);
            _motion.Clear(initialCapacity);
            _localPaths = new WorldPosition[checked(initialCapacity * LocalMotionSearch.PathCapacity)];
        }
    }

    public EntityId AddInitialUnit(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
    {
        if (Tick != 0) throw new InvalidOperationException("After initialization, entities are created through commands.");
        ValidatePosition(position);
        ValidateType(unitType);
        if (_navigation is not null)
        {
            if (!TerrainGeometry.Clear(_navigation.Grid, position, position, MotionIndex.BodyRadius) || !_motion!.ClearPlacement(position, out _))
                throw new ArgumentException("Initial navigation placement is blocked or occupied.", nameof(position));
        }
        if ((states & ~_types[unitType].AllowedStateMask) != 0) throw new ArgumentOutOfRangeException(nameof(states));
        return Spawn(unitType, position, owner, states);
    }

    public void Step(ReadOnlySpan<WorldCommand> commands = default, CancellationToken cancellationToken = default)
    {
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
            if (command.Kind is CommandKind.Move or CommandKind.MoveAll or CommandKind.MoveGroup or CommandKind.Scatter or CommandKind.Spawn or CommandKind.BlockCell or CommandKind.UnblockCell or CommandKind.Aim)
                ValidatePosition(command.Position);
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
        foreach (var command in AppliedCommands) Apply(command);

        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i];
            entity.Previous = entity.Position;
            entity.PreviousBodyFacing = entity.BodyFacing; entity.PreviousWeaponFacing = entity.WeaponFacing;
        }
        if (_navigation is not null)
        {
            try { UpdateNavigation(cancellationToken); }
            catch { _failed = true; throw; }
        }
        else for (int i = 0; i < Count; i++)
            if (_entities[i].Moving && Advance(ref _entities[i], _entities[i].Target)) _entities[i].Moving = false;
        UpdatePresentation();
    }

    public void CopyPoses(Span<EntityPose> destination)
    {
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < Count) throw new ArgumentException("Destination is too small.", nameof(destination));
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i];
            destination[i] = new(entity.Id, entity.UnitType, entity.Previous, entity.Position,
                new(entity.Owner, entity.PreviousBodyFacing, entity.BodyFacing, entity.PreviousWeaponFacing, entity.WeaponFacing,
                    entity.BodyAnimation, entity.WeaponAnimation, entity.VisualStates));
        }
    }

    public SimulationReport Report()
    {
        if (_failed) throw new InvalidOperationException("An interrupted world has no completed state.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_navigation is null ? 2 : 6);
            writer.Write(Tick); writer.Write(Width); writer.Write(Depth);
            writer.Write(TickRate); writer.Write(SpeedPerTick); writer.Write(_nextId); writer.Write(_random.State);
            writer.Write(_types.Length);
            foreach (var type in _types)
            {
                writer.Write(type.Id); writer.Write(type.MaxHealth);
                writer.Write(type.Animations.Count); foreach (var name in type.Animations) writer.Write(name);
                writer.Write(type.States.Count); foreach (var name in type.States) writer.Write(name);
            }
            writer.Write(Count);
            foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
            {
                writer.Write(entity.Id.Value); writer.Write(entity.UnitType); writer.Write(entity.Health);
                writer.Write(entity.Position.X); writer.Write(entity.Position.Y); writer.Write(entity.Position.Elevation);
                writer.Write(entity.Target.X); writer.Write(entity.Target.Y); writer.Write(entity.Target.Elevation);
                writer.Write(entity.Moving);
                WritePresentation(writer, entity);
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
            _navigation?.WriteState(writer);
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
        if (command.Kind == CommandKind.MoveGroup) { SetGroupTarget(command.Position); return; }
        if (command.Kind is CommandKind.BlockCell or CommandKind.UnblockCell)
        {
            int cell = _navigation!.Grid.CellAt(command.Position);
            if (command.Kind == CommandKind.BlockCell && _motion!.OverlapsCell(_navigation.Grid, cell, Count, out var occupant))
                _events.Add(new(Tick, WorldEventKind.CellOccupied, occupant, _navigation.Grid.Center(cell)));
            else
            {
                var previous = _navigation.Grid;
                _navigation.SetBlocked(cell, command.Kind == CommandKind.BlockCell);
                if (!ReferenceEquals(previous, _navigation.Grid)) _bulkAllowed = false;
            }
            return;
        }
        if (command.Kind == CommandKind.Spawn)
        {
            if (_navigation is not null)
            {
                bool terrain = TerrainGeometry.Clear(_navigation.Grid, command.Position, command.Position, MotionIndex.BodyRadius);
                bool free = _motion!.ClearPlacement(command.Position, out _);
                if (!terrain || !free)
                {
                    _events.Add(new(Tick, !free ? WorldEventKind.CellOccupied : WorldEventKind.CellBlocked,
                        default, command.Position, command.UnitType));
                    return;
                }
            }
            if ((command.States & ~_types[command.UnitType].AllowedStateMask) != 0)
            { _events.Add(new(Tick, WorldEventKind.InvalidPresentation, default, command.Position, command.UnitType)); return; }
            var id = Spawn(command.UnitType, command.Position, command.Owner, command.States);
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
                SetTarget(i, target);
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
        if (command.Kind == CommandKind.Move) SetTarget(index, command.Position);
        else
        {
            _events.Add(new(Tick, WorldEventKind.Destroyed, command.Entity, _entities[index].Position, _entities[index].UnitType));
            if (_navigation is not null)
            {
                _groupRoutes.Remove(command.Entity);
            }
            _indices.Remove(command.Entity);
            _orderDirty = true;
            Count--;
            if (index != Count)
            {
                _entities[index] = _entities[Count];
                if (_navigation is not null) Path(Count).CopyTo(Path(index));
                _indices[_entities[index].Id] = index;
            }
            _entities[Count] = default;
            RebuildMotionIndex();
        }
    }

    private void SetTarget(int index, WorldPosition position)
    {
        ref var entity = ref _entities[index];
        entity.GroupMember = false;
        entity.WaitTicks = entity.PathLength = entity.PathCursor = 0;
        entity.YieldFor = default; entity.YieldDeadline = 0; entity.LocalTarget = default;
        _groupRoutes.Remove(entity.Id);
        entity.Target = position with { Elevation = entity.Position.Elevation };
        if (_navigation is not null)
        {
            entity.GoalCell = _navigation.Grid.CellAt(position);
        }
        entity.Moving = entity.Position != entity.Target;
    }

    private EntityId Spawn(int unitType, WorldPosition position, uint owner = 0, uint states = 0)
    {
        if (_nextId == uint.MaxValue) throw new InvalidOperationException("Entity ID space exhausted.");
        if (Count == _entities.Length)
        {
            Array.Resize(ref _entities, checked(Math.Max(16, Count * 2)));
            if (_navigation is not null) Array.Resize(ref _localPaths, checked(_entities.Length * LocalMotionSearch.PathCapacity));
        }
        var id = new EntityId(_nextId++);
        _entities[Count] = new() { Id = id, UnitType = unitType, Health = _types[unitType].MaxHealth,
            Previous = position, Position = position, Target = position, Owner = owner, VisualStates = states,
            BodyFacing = FacingDirection.North, PreviousBodyFacing = FacingDirection.North,
            WeaponFacing = FacingDirection.North, PreviousWeaponFacing = FacingDirection.North,
            BodyAnimation = new(_types[unitType].IdleAnimation, Tick, 0), WeaponAnimation = AnimationPlayback.None,
            GoalCell = _navigation?.Grid.CellAt(position) ?? -1 };
        _motion?.Add(Count, id, position);
        _indices.Add(id, Count++);
        _orderDirty = true;
        return id;
    }

    private bool Advance(ref EntityState entity, WorldPosition target)
    {
        entity.Position = StepTowards(entity.Position, target);
        return entity.Position == target;
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

    private static ulong SquareRoot(ulong value)
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
