namespace OpenRTS.Core.Presentation;

internal struct PresentationState(int idleAnimation, long tick, uint states)
{
    public uint VisualStates = states;
    public FacingDirection BodyFacing = FacingDirection.North, PreviousBodyFacing = FacingDirection.North;
    public FacingDirection WeaponFacing = FacingDirection.North, PreviousWeaponFacing = FacingDirection.North;
    public AnimationPlayback BodyAnimation = new(idleAnimation, tick, 0), WeaponAnimation = AnimationPlayback.None;
    public bool BodyAnimationOverride, HasAim;
    public WorldPosition AimPoint;
    public EntityId AimEntity;
}
