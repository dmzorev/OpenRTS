using OpenRTS.Core.Presentation;

namespace OpenRTS.Core;

public readonly record struct ConstructionProjectPose(EntityId Id, uint Owner, int UnitType, WorldPosition Position, bool Clearing, string? Upgrade = null, FacingDirection Facing = default);
public readonly record struct ConstructionProgress(int Work, int Required)
{
    public bool Active => Required > 0;
}

internal sealed class ConstructionProject(EntityId id, uint owner, int unitType, WorldPosition position, BuildDefinition recipe)
{
    public EntityId Id { get; } = id;
    public uint Owner { get; } = owner;
    public int UnitType { get; } = unitType;
    public WorldPosition Position { get; } = position;
    public BuildDefinition Recipe { get; } = recipe;
    public long ReservedAt, LastAttempt;
}
