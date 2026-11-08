namespace OpenRTS.Core.Orders;

public enum UnitActionKind { Move, AttackMove, Attack, Stop, HoldPosition, Repair, Ability, Capture, Enter, Unload, UnloadAt, Build, UpgradeSupport, Transform }
public enum OrderPhase { Idle, Moving, Approaching, Engaging, Holding, Waiting }
public readonly record struct UnitAction(UnitActionKind Kind, EntitySet Recipients, WorldPosition Position = default,
    EntityId Target = default, bool Queued = false, string? Action = null);
public readonly record struct UnitOrder(UnitActionKind Kind, WorldPosition Position, EntityId Target = default, bool Group = false, long Cohort = 0, int Ability = -1);
public readonly record struct OrderSnapshot(UnitActionKind Kind, OrderPhase Phase, WorldPosition Destination, EntityId Target,
    int QueuedCount, long ReadyTick, int CaptureProgress = 0, int CaptureDuration = 0);

