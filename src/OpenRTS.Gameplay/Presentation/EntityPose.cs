using OpenRTS.Core.Capture;
using System.Text.Json.Serialization;

namespace OpenRTS.Core.Presentation;

public enum AnimationTrack { Body, Weapon }
public readonly record struct FacingDirection(int X, int Y)
{
    public static FacingDirection North => new(0, -1);
    public static FacingDirection FromQuarterTurns(int quarterTurns) => quarterTurns switch
    {
        1 => new(1, 0),
        2 => new(0, 1),
        3 => new(-1, 0),
        _ => North
    };
}
public readonly record struct AnimationPlayback(int Animation, long StartTick, int DurationTicks)
{
    public static AnimationPlayback None => new(-1, 0, 0);
    public bool IsActive(long tick) => Animation >= 0 && tick >= StartTick && (DurationTicks == 0 || tick - StartTick < DurationTicks);
}
public readonly record struct EntityPresentation(uint Owner, FacingDirection PreviousBodyFacing, FacingDirection BodyFacing,
    FacingDirection PreviousWeaponFacing, FacingDirection WeaponFacing, AnimationPlayback Body, AnimationPlayback Weapon, uint States);
public readonly record struct EntityPose(EntityId Id, int UnitType, WorldPosition Previous, WorldPosition Current,
    EntityPresentation Presentation = default, OrderSnapshot Order = default, int Health = 0, int WeaponOffset = 0, int WeaponCount = 0, bool TerrainStranded = false, bool HasRally = false, WorldPosition Rally = default, EntityId Carrier = default, ConstructionProgress Construction = default, bool AutoBuild = false, SupplyPose Supply = default, UpgradePose Upgrade = default, CaptureTargetProgress Capture = default)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int QuarterTurns { get; init; }
}
