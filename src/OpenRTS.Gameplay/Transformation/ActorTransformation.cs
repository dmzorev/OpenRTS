namespace OpenRTS.Core;

/// <summary>A named transition between two unit definitions, registered by the game.</summary>
public sealed record ActorTransformation(string Id, string Source, string Target,
    TransformationAnimation? Before = null, TransformationAnimation? After = null, bool AllowRotation = false);

/// <summary>A body clip and its simulation duration. Facing, if set, is applied when the clip starts.</summary>
public sealed record TransformationAnimation(string Clip, int Ticks, FacingDirection? Facing = null);

public enum TransformationPhase { None, Approaching, Clearing, Preparing, Finishing }

/// <summary>Planned result and current phase, published with the source or replacement actor.</summary>
public readonly record struct TransformationPose(TransformationPhase Phase, int UnitType,
    WorldPosition Position, int QuarterTurns, long UntilTick);
