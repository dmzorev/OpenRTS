using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenRTS.Core;

/// <summary>Single-owner world. Hosts exchange commands and copied poses, never its mutable storage.</summary>
public sealed class SimulationWorld
{
    private struct EntityState
    {
        public EntityId Id;
        public int UnitType, Health;
        public WorldPosition Previous, Position, Target;
        public bool Moving;
    }

    private readonly UnitDefinition[] _types;
    private EntityState[] _entities;
    private readonly Dictionary<EntityId, int> _indices;
    private readonly List<WorldEvent> _events = [];
    private WorldCommand[] _commands = [];
    private int _commandCount;
    private uint _nextId = 1;
    private DeterministicRandom _random;

    public int Width { get; }
    public int Depth { get; }
    public int TickRate { get; }
    public int SpeedPerTick { get; }
    public long Tick { get; private set; }
    public int Count { get; private set; }
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
        int initialCapacity = 0, int tickRate = 25, int speedPerTick = 128)
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
    }

    public EntityId AddInitialUnit(int unitType, WorldPosition position)
    {
        if (Tick != 0) throw new InvalidOperationException("After initialization, entities are created through commands.");
        ValidatePosition(position);
        ValidateType(unitType);
        return Spawn(unitType, position);
    }

    public void Step(ReadOnlySpan<WorldCommand> commands = default)
    {
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
            if (command.Kind is CommandKind.Move or CommandKind.MoveAll or CommandKind.Scatter or CommandKind.Spawn)
                ValidatePosition(command.Position);
            if (command.Kind == CommandKind.Scatter && (command.Position.X == 0 || command.Position.Y == 0))
                throw new ArgumentException("Scatter area must have positive extents.", nameof(commands));
            if (command.Kind == CommandKind.Spawn) ValidateType(command.UnitType);
        }

        _events.Clear();
        _commandCount = commands.Length;
        Tick = nextTick;
        foreach (var command in AppliedCommands) Apply(command);

        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i];
            entity.Previous = entity.Position;
            if (!entity.Moving) continue;
            long dx = (long)entity.Target.X - entity.Position.X;
            long dy = (long)entity.Target.Y - entity.Position.Y;
            ulong squared = (ulong)(dx * dx + dy * dy);
            if (squared <= (ulong)SpeedPerTick * (ulong)SpeedPerTick)
            {
                entity.Position = entity.Target;
                entity.Moving = false;
                continue;
            }
            long distance = (long)SquareRoot(squared);
            if ((ulong)(distance * distance) < squared) distance++;
            int stepX = (int)(dx * SpeedPerTick / distance);
            int stepY = (int)(dy * SpeedPerTick / distance);
            if (stepX == 0 && stepY == 0)
            {
                if (Math.Abs(dx) >= Math.Abs(dy)) stepX = Math.Sign(dx);
                else stepY = Math.Sign(dy);
            }
            entity.Position = new(checked(entity.Position.X + stepX), checked(entity.Position.Y + stepY), entity.Position.Elevation);
        }
    }

    public void CopyPoses(Span<EntityPose> destination)
    {
        if (destination.Length < Count) throw new ArgumentException("Destination is too small.", nameof(destination));
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i];
            destination[i] = new(entity.Id, entity.UnitType, entity.Previous, entity.Position);
        }
    }

    public SimulationReport Report()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(Tick); writer.Write(Width); writer.Write(Depth);
            writer.Write(TickRate); writer.Write(SpeedPerTick); writer.Write(_nextId); writer.Write(_random.State);
            writer.Write(_types.Length);
            foreach (var type in _types) { writer.Write(type.Id); writer.Write(type.MaxHealth); }
            writer.Write(Count);
            foreach (var entity in _entities.AsSpan(0, Count).ToArray().OrderBy(e => e.Id.Value))
            {
                writer.Write(entity.Id.Value); writer.Write(entity.UnitType); writer.Write(entity.Health);
                writer.Write(entity.Position.X); writer.Write(entity.Position.Y); writer.Write(entity.Position.Elevation);
                writer.Write(entity.Target.X); writer.Write(entity.Target.Y); writer.Write(entity.Target.Elevation);
                writer.Write(entity.Moving);
            }
        }
        return new(Tick, Count, Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))));
    }

    private void Apply(WorldCommand command)
    {
        if (command.Kind == CommandKind.Spawn)
        {
            var id = Spawn(command.UnitType, command.Position);
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
        if (command.Kind == CommandKind.Move) SetTarget(index, command.Position);
        else
        {
            _events.Add(new(Tick, WorldEventKind.Destroyed, command.Entity, _entities[index].Position, _entities[index].UnitType));
            _indices.Remove(command.Entity);
            Count--;
            if (index != Count)
            {
                _entities[index] = _entities[Count];
                _indices[_entities[index].Id] = index;
            }
            _entities[Count] = default;
        }
    }

    private void SetTarget(int index, WorldPosition position)
    {
        ref var entity = ref _entities[index];
        entity.Target = position with { Elevation = entity.Position.Elevation };
        entity.Moving = entity.Position != entity.Target;
    }

    private EntityId Spawn(int unitType, WorldPosition position)
    {
        if (_nextId == uint.MaxValue) throw new InvalidOperationException("Entity ID space exhausted.");
        if (Count == _entities.Length) Array.Resize(ref _entities, checked(Math.Max(16, Count * 2)));
        var id = new EntityId(_nextId++);
        _entities[Count] = new() { Id = id, UnitType = unitType, Health = _types[unitType].MaxHealth,
            Previous = position, Position = position, Target = position };
        _indices.Add(id, Count++);
        return id;
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
