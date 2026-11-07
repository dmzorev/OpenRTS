namespace OpenRTS.Core.Combat;

internal struct WeaponState
{
    public EntityId Target, ChargingTarget;
    public long ReadyTick, FireAtTick;
    public int BurstRemaining;
    public bool AutoCast;
    public FacingDirection PreviousFacing, Facing;
    public AnimationPlayback Animation;
}

internal struct CombatState(UnitDefinition definition)
{
    public WeaponState[] Weapons = definition.Weapons.Select(_ => new WeaponState
    {
        Facing = FacingDirection.North, PreviousFacing = FacingDirection.North,
        Animation = AnimationPlayback.None
    }).ToArray();
    public long WeaponReadyTick;
}
