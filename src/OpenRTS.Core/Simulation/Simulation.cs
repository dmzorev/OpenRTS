using System.Runtime.InteropServices;
using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

/// <summary>Single-owner simulation with game-defined commands, events and tick rules.</summary>
public abstract class Simulation<TCommand, TEvent> : IDisposable where TCommand : struct, ISimulationCommand
{
    private readonly CommandBatch<TCommand> _commands = new();
    private readonly Action<TCommand> _validateCommand;
    private readonly HashSet<EntityId> _reservedIds = [];
    private World? _storage;
    private bool _disposed, _stepping;
    private uint _nextId = 1;

    // Derived membership. Structural changes go through TrackEntity and DeleteEntity.
    protected Entity[] _entityHandles;
    protected readonly Dictionary<EntityId, int> _indices;
    protected readonly List<TEvent> _events = [];
    protected DeterministicRandom _random;
    protected bool _failed;
    protected uint NextEntityId => _nextId;
    protected CancellationToken StepCancellation { get; private set; }

    public int Width { get; }
    public int Depth { get; }
    public int TickRate { get; }
    public long Tick { get; private set; }
    public int Count { get; private set; }
    public ReadOnlySpan<TEvent> Events => CollectionsMarshal.AsSpan(_events);
    public ReadOnlySpan<TCommand> AppliedCommands => _commands.Applied;

    protected Simulation(int width, int depth, ulong seed = 1, int initialCapacity = 0, int tickRate = 25)
    {
        if (width < 1 || width > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 1 || depth > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(depth));
        if (tickRate < 1 || tickRate > 1000) throw new ArgumentOutOfRangeException(nameof(tickRate));
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        Width = width;
        Depth = depth;
        TickRate = tickRate;
        _entityHandles = new Entity[initialCapacity];
        _indices = new(initialCapacity);
        _random = new(seed);
        _validateCommand = ValidateCommand;
    }

    public void Step(ReadOnlySpan<TCommand> commands = default, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot be advanced.");
        if (_stepping) throw new InvalidOperationException("A simulation step cannot be nested.");
        cancellationToken.ThrowIfCancellationRequested();
        _stepping = true;
        try
        {
            long nextTick = checked(Tick + 1);
            _commands.Replace(commands, nextTick, _validateCommand);
            try
            {
                _events.Clear();
                PrepareStep();
                Tick = nextTick;
                StepCancellation = cancellationToken;
                foreach (var command in AppliedCommands) ApplyCommand(command);
                if (_storage is not null)
                    foreach (ref var spatial in _storage.Query<SpatialState>()) spatial.Previous = spatial.Position;
                Update(cancellationToken);
            }
            catch { _failed = true; throw; }
        }
        finally { _stepping = false; }
    }

    protected abstract void ValidateCommand(TCommand command);
    protected virtual void PrepareStep() { }
    protected abstract void ApplyCommand(TCommand command);
    protected abstract void Update(CancellationToken cancellationToken);
    public abstract SimulationReport Report();

    protected World? ExistingStorage => _storage;
    /// <summary>Native component storage for the owner thread. Use the membership operations for entity lifetime.</summary>
    protected World Storage { get { ThrowIfDisposed(); return _storage ??= new(); } }

    protected EntityId AllocateEntityId()
    {
        ThrowIfDisposed();
        if (_nextId == uint.MaxValue) throw new InvalidOperationException("Entity ID space exhausted.");
        var id = new EntityId(_nextId++);
        _reservedIds.Add(id);
        return id;
    }

    protected void ReleaseEntityId(EntityId id) => _reservedIds.Remove(id);

    /// <summary>Places a spatial entity within world bounds. A game may check terrain and occupancy before placement.</summary>
    protected Entity PlaceEntity(WorldPosition position, int type = 0)
    {
        ValidatePosition(position);
        var id = AllocateEntityId();
        var entity = Storage.Create(new EntityIdentity(id, type), new SpatialState(position));
        TrackEntity(entity);
        return entity;
    }

    /// <summary>Register a live entity created in Storage with a fresh or previously reserved identity.</summary>
    protected void TrackEntity(Entity entity)
    {
        ThrowIfDisposed();
        if (!entity.IsAlive || !ReferenceEquals(entity.World, _storage)) throw new ArgumentException("Entity must belong to this simulation.", nameof(entity));
        var id = entity.Get<EntityIdentity>().Id;
        if (!_reservedIds.Contains(id)) throw new ArgumentException("Entity ID must be reserved by this simulation.", nameof(entity));
        if (Count == _entityHandles.Length) Array.Resize(ref _entityHandles, checked(Math.Max(16, Count * 2)));
        _indices.Add(id, Count);
        _entityHandles[Count++] = entity;
        _reservedIds.Remove(id);
    }

    protected bool TryGetEntity(EntityId id, out Entity entity)
    {
        ThrowIfDisposed();
        if (_indices.TryGetValue(id, out int index)) { entity = _entityHandles[index]; return true; }
        entity = default;
        return false;
    }

    protected void DeleteEntity(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        var entity = _entityHandles[index];
        var id = entity.Get<EntityIdentity>().Id;
        entity.Delete();
        _indices.Remove(id);
        Count--;
        if (index != Count)
        {
            _entityHandles[index] = _entityHandles[Count];
            _indices[_entityHandles[index].Get<EntityIdentity>().Id] = index;
        }
        _entityHandles[Count] = default;
    }

    protected void ValidatePosition(WorldPosition position)
    {
        if (position.X < 0 || position.X > Width || position.Y < 0 || position.Y > Depth ||
            Math.Abs((long)position.Elevation) > WorldPosition.MaximumExtent)
            throw new ArgumentOutOfRangeException(nameof(position), "Position is outside world bounds.");
    }

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        if (_stepping) throw new InvalidOperationException("Cannot dispose a simulation during its step.");
        _storage?.Dispose();
        _storage = null;
        _disposed = true;
    }
}
