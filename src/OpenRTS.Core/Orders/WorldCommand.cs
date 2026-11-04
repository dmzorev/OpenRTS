namespace OpenRTS.Core.Orders;

public enum CommandKind { Move, MoveAll, Scatter, Spawn, Destroy, BlockCell, UnblockCell, MoveGroup, PlayAnimation, SetVisualStates, Aim, ClearAim, ToggleVisualState, AttackMove, AttackTarget, Stop, HoldPosition, MoveSelected, RepairTarget }

public readonly record struct WorldCommand(long Tick, uint Issuer, long Sequence, CommandKind Kind,
    EntityId Entity = default, WorldPosition Position = default, int UnitType = 0, uint Owner = 0,
    int Animation = 0, int DurationTicks = 1, AnimationTrack Track = AnimationTrack.Weapon, uint States = 0, EntityId TargetEntity = default,
    EntitySet? Recipients = null, bool Queued = false);

public enum WorldEventKind { Spawned, Destroyed, MissingEntity, NoRoute, CellOccupied, CellBlocked, InvalidPresentation, AnimationPlayed, DestinationAdjusted, InvalidOrder, QueueFull, WeaponFired, Damaged, Repaired, ProjectileImpact, ProjectileExpired }
public readonly record struct WorldEvent(long Tick, WorldEventKind Kind, EntityId Entity,
    WorldPosition Position = default, int UnitType = -1, int Animation = -1, int DurationTicks = 0, AnimationTrack Track = AnimationTrack.Weapon,
    int Amount = 0, EntityId TargetEntity = default, int Mount = 0, WorldPosition SourcePosition = default, long ProjectileId = 0);

