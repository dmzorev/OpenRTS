using System.Text.Json.Serialization;

namespace OpenRTS.Core.Orders;

public enum CommandKind { Move, MoveAll, Scatter, Spawn, Destroy, BlockCell, UnblockCell, MoveGroup, PlayAnimation, SetVisualStates, Aim, ClearAim, ToggleVisualState, AttackMove, AttackTarget, Stop, HoldPosition, MoveSelected, RepairTarget, Remove, UseAbility, SetAutoCast, EnqueueTask, CancelTasks, PlaceProduced, SetRally, CaptureTarget, Enter, Unload, UnloadAt, PlanConstruction, BuildSite, CancelConstruction, GrantCondition, RevokeCondition }

public readonly record struct WorldCommand(long Tick, uint Issuer, long Sequence, CommandKind Kind,
    EntityId Entity = default, WorldPosition Position = default, int UnitType = 0, uint Owner = 0,
    int Animation = 0, int DurationTicks = 1, AnimationTrack Track = AnimationTrack.Weapon, uint States = 0, EntityId TargetEntity = default,
    EntitySet? Recipients = null, bool Queued = false, string? Action = null, string? Queue = null, int Amount = 1, bool Repeat = false, bool Enabled = false, long TaskId = 0) : ISessionCommand<WorldCommand>
{
    /// <summary>Condition to grant. A duration of zero makes the grant indefinite.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GameplayConditions Condition { get; init; }
    /// <summary>Token of the grant to revoke on this entity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ConditionGrantId { get; init; }

    public WorldCommand WithStamp(long tick, uint issuer, long sequence) => this with { Tick = tick, Issuer = issuer, Sequence = sequence };
}

public enum WorldEventKind { Spawned, Destroyed, MissingEntity, NoRoute, CellOccupied, CellBlocked, InvalidPresentation, AnimationPlayed, DestinationAdjusted, InvalidOrder, QueueFull, WeaponFired, Damaged, Repaired, ProjectileImpact, ProjectileExpired, TerrainStranded, Removed, ActionUnavailable, TaskAdded, TaskCancelled, ProductionReady, Produced, ProductionBlocked, RallyChanged, OwnerChanged, EnteredTransport, Unloaded, TransportFull, UnloadBlocked, ConstructionStarted, ConstructionCompleted, ConstructionCancelled, ConditionGranted, ConditionRevoked, CommandDenied }
public readonly record struct WorldEvent(long Tick, WorldEventKind Kind, EntityId Entity,
    WorldPosition Position = default, int UnitType = -1, int Animation = -1, int DurationTicks = 0, AnimationTrack Track = AnimationTrack.Weapon,
    int Amount = 0, EntityId TargetEntity = default, int Mount = 0, WorldPosition SourcePosition = default, long ProjectileId = 0, uint Owner = 0, uint PreviousOwner = 0)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GameplayConditions Condition { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ConditionGrantId { get; init; }
}

