using OpenRTS.Core.Presentation;
using System.Text.Json.Serialization;

namespace OpenRTS.Core;

public readonly record struct ConstructionProjectPose(EntityId Id, uint Owner, int UnitType, WorldPosition Position, bool Clearing, string? Upgrade = null, FacingDirection Facing = default)
{
    public int UnpaidCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int QuarterTurns { get; init; }
}
public readonly record struct ConstructionProgress(int Work, int Required)
{
    public bool Active => Required > 0;
}

internal sealed class ConstructionProject(EntityId id, uint owner, int unitType, WorldPosition position, BuildDefinition recipe, int quarterTurns)
{
    public EntityId Id { get; } = id;
    public uint Owner { get; } = owner;
    public int UnitType { get; } = unitType;
    public WorldPosition Position { get; } = position;
    public BuildDefinition Recipe { get; } = recipe;
    public int QuarterTurns { get; } = quarterTurns;
    public long ReservedAt, LastAttempt;
}
