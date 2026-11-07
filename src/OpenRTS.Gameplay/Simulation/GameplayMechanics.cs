using OpenRTS.Core.Capture;
using OpenRTS.Core.Production;

namespace OpenRTS.Core;

/// <summary>A versioned factory for one implementation per simulation world.</summary>
public sealed class GameplayRegistration<T> where T : class
{
    private readonly Func<T> _factory;
    public string Id { get; }
    public int Version { get; }

    public GameplayRegistration(string id, int version, Func<T> factory)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("A gameplay registration needs a valid ID.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(factory);
        Id = id;
        Version = version;
        _factory = factory;
    }

    internal T Create() => _factory() ?? throw new InvalidOperationException($"Gameplay factory '{Id}' returned null.");

    internal void WriteIdentity(BinaryWriter writer)
    {
        writer.Write(Id);
        writer.Write(Version);
    }
}

public sealed class GameplayMechanics
{
    public static GameplayRegistration<ICaptureMechanic> BuiltInCapture { get; } = new("openrts.capture", 1, static () => new TimedCapture());
    public GameplayRegistration<ICaptureMechanic>? Capture { get; init; } = BuiltInCapture;
    public GameplayRegistration<IEndOfTickRule>? EndOfTick { get; init; }
    public ProductionRateRule? ProductionRate { get; init; }
}

/// <summary>Runs after the built-in phases and owns its deterministic state.</summary>
public interface IEndOfTickRule
{
    void Advance(GameplayTick tick);
    void WriteState(BinaryWriter writer);
}

/// <summary>Read-only access valid during the rule's Advance call.</summary>
public readonly ref struct GameplayTick
{
    private readonly SimulationWorld _world;
    internal GameplayTick(SimulationWorld world) => _world = world;
    public long Tick => _world.Tick;
    public bool TryGetOwner(EntityId entity, out uint owner) => _world.TryGetGameplayOwner(entity, out owner);
}

public sealed partial class SimulationWorld
{
    private void WriteMechanics(BinaryWriter writer)
    {
        bool custom = !ReferenceEquals(_mechanics.Capture, GameplayMechanics.BuiltInCapture) || _endOfTick is not null;
        int format = _navigation is null ? 10 : 14;
        if (custom) format = _navigation is null ? 17 : 18;
        else if (_productionRate is not null) format = _navigation is null ? 15 : 16;
        writer.Write(format);
        if (custom) writer.Write(_productionRate is not null);
        _productionRate?.WriteState(writer);
        if (!custom) return;

        writer.Write(_mechanics.Capture is not null);
        _mechanics.Capture?.WriteIdentity(writer);
        _capture?.WriteState(writer);
        writer.Write(_endOfTick is not null);
        _mechanics.EndOfTick?.WriteIdentity(writer);
        _endOfTick?.WriteState(writer);
    }
}
