using OpenRTS.Core.Capture;
using OpenRTS.Core.Construction;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Production;
using OpenRTS.Core.Transport;

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
    public static GameplayRegistration<ICaptureMechanic> BuiltInCapture { get; } = new("openrts.capture", 2, static () => new TimedCapture());
    public static GameplayRegistration<ITransportMechanic> BuiltInTransport { get; } = new("openrts.transport", 1, static () => new StandardTransport());
    public static GameplayRegistration<IConstructionMechanic> BuiltInConstruction { get; } = new("openrts.construction", 1, static () => new StandardConstruction());
    public GameplayRegistration<ICaptureMechanic>? Capture { get; init; } = BuiltInCapture;
    public GameplayRegistration<ITransportMechanic>? Transport { get; init; } = BuiltInTransport;
    public GameplayRegistration<IConstructionMechanic>? Construction { get; init; } = BuiltInConstruction;
    public GameplayRegistration<IEndOfTickRule>? EndOfTick { get; init; }
    public ProductionRateRule? ProductionRate { get; init; }
    public IReadOnlyList<ActorTransformation> Transformations { get; init; } = [];
    /// <summary>Optional distance fields for unweighted individual routes that need a detour.</summary>
    /// <remarks>The factory must create a pathfinder with the same initial grid instance as the world.</remarks>
    public GameplayRegistration<IGridPathfinder>? GridPathfinder { get; init; }
    /// <summary>Optional ranking rule for legal targets during automatic acquisition.</summary>
    public GameplayRegistration<ICombatTargetRule>? CombatTargeting { get; init; }
}

/// <summary>Runs after the built-in phases and owns its deterministic state.</summary>
public interface IEndOfTickRule
{
    void Advance(GameplayTick tick);
    void WriteState(BinaryWriter writer);
}

/// <summary>Access valid during the rule's Advance call.</summary>
public readonly ref struct GameplayTick
{
    private readonly SimulationWorld _world;
    internal GameplayTick(SimulationWorld world) => _world = world;
    public long Tick => _world.Tick;
    /// <summary>Finish reading before calling operations that may append events.</summary>
    public ReadOnlySpan<WorldEvent> Events => _world.Events;
    public bool TryGetOwner(EntityId entity, out uint owner) => _world.TryGetGameplayOwner(entity, out owner);
    public void CopySupplySources(List<SupplySourcePose> destination) => _world.CopySupplySources(destination);
    public int ReplenishSupply(EntityId source, int amount) => _world.ReplenishSupply(source, amount);
    public bool TrySpawnSupplySource(int unitType, WorldPosition position, out EntityId created) =>
        _world.TrySpawnSupplySource(unitType, position, out created);
    /// <summary>Schedules the same named transition used by player commands.</summary>
    public bool TryTransform(EntityId source, string action, WorldPosition? position = null, int quarterTurns = 0) =>
        _world.TryStartTransformation(source, action, position, quarterTurns);
    public bool TrySpawnAtExit(EntityId source, int unitType, int radius, out EntityId created) =>
        _world.TrySpawnAtExit(source, unitType, radius, out created);
}

public sealed partial class SimulationWorld
{
    private void WriteMechanics(BinaryWriter writer)
    {
        WriteTransformations(writer);
        writer.Write(27); // Includes footprint orientation.
        if (_capture is not null && ReferenceEquals(_mechanics.Capture, GameplayMechanics.BuiltInCapture))
        {
            writer.Write(24);
            GameplayMechanics.BuiltInCapture.WriteIdentity(writer);
        }
        if (_conditionGrants is { } grants)
        {
            writer.Write(23);
            grants.WriteState(writer);
        }
        if (_mechanics.CombatTargeting is { } targeting)
        {
            // The targeting header wraps the existing mechanics format.
            writer.Write(22);
            targeting.WriteIdentity(writer);
            _combatTargetRule!.WriteConfiguration(writer);
        }
        bool customTransportOrConstruction = !ReferenceEquals(_mechanics.Transport, GameplayMechanics.BuiltInTransport) ||
            !ReferenceEquals(_mechanics.Construction, GameplayMechanics.BuiltInConstruction);
        bool customNavigation = _mechanics.GridPathfinder is not null;
        bool custom = customNavigation || customTransportOrConstruction ||
            !ReferenceEquals(_mechanics.Capture, GameplayMechanics.BuiltInCapture) || _endOfTick is not null;
        int format = _navigation is null ? 10 : 14;
        if (customNavigation) format = 21;
        else if (customTransportOrConstruction) format = _navigation is null ? 19 : 20;
        else if (custom) format = _navigation is null ? 17 : 18;
        else if (_mechanics.ProductionRate is not null) format = _navigation is null ? 15 : 16;
        writer.Write(format);
        if (custom) writer.Write(_mechanics.ProductionRate is not null);
        _mechanics.ProductionRate?.WriteState(writer);
        if (!custom) return;

        writer.Write(_mechanics.Capture is not null);
        _mechanics.Capture?.WriteIdentity(writer);
        _capture?.WriteState(writer);
        writer.Write(_endOfTick is not null);
        _mechanics.EndOfTick?.WriteIdentity(writer);
        _endOfTick?.WriteState(writer);
        if (!customTransportOrConstruction && !customNavigation) return;

        writer.Write(_mechanics.Transport is not null);
        _mechanics.Transport?.WriteIdentity(writer);
        _transport?.WriteState(writer);
        writer.Write(_mechanics.Construction is not null);
        _mechanics.Construction?.WriteIdentity(writer);
        _construction?.WriteState(writer);
        if (_mechanics.GridPathfinder is { } pathfinder)
        {
            pathfinder.WriteIdentity(writer);
            _navigation!.Grid.WriteState(writer);
        }
    }
}
