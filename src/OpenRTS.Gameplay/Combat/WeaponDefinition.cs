using System.Text.Json.Serialization;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core.Combat;

public enum ArmorKind { Living, Light, Mechanical, Building }
public enum ProjectileKind { Hitscan, Cannon, Missile, Grenade }
public enum ProjectileTrajectory { Planar, Direct, Arc, Ballistic, Cruise }
public enum WeaponEffect { Damage, Repair }

/// <summary>Gameplay only. Distances are fixed world units, durations are simulation ticks, modifiers are percent.</summary>
public sealed record WeaponDefinition
{
    public const int MaximumRange = WorldPosition.MaximumExtent;
    public const int MaximumTravelLimit = 2 * MaximumRange;
    public string Id { get; }
    public TargetCategories Targets { get; }
    public TargetCategories? ExcludedTargets { get; }
    public ProjectileTrajectory Trajectory { get; }
    public bool SpatialTrajectory => Trajectory != ProjectileTrajectory.Planar;
    public bool HasFlightProfile => Trajectory is ProjectileTrajectory.Arc or ProjectileTrajectory.Ballistic or ProjectileTrajectory.Cruise;
    public int TrajectoryHeight { get; }
    public bool Homing { get; }
    public int ScatterRadius { get; }
    public int LaunchFan { get; }
    public SensorDisplay MinimumContact { get; }
    public int Range { get; }
    public int MinRange { get; }
    public int AcquireRange { get; }
    public int Damage { get; }
    public int CooldownTicks { get; }
    public int WindupTicks { get; }
    public int Burst { get; }
    public int BurstIntervalTicks { get; }
    public int AnimationTicks { get; }
    public ProjectileKind Projectile { get; }
    public WeaponEffect Effect { get; }
    public int ProjectileSpeed { get; }
    public int TravelLimit { get; }
    public int SplashRadius { get; }
    public int LivingModifier { get; }
    public int MechanicalModifier { get; }
    public int LightModifier { get; }
    public int BuildingModifier { get; }
    public bool FriendlyFire { get; }
    public int PassengerDamage { get; }
    public bool PassengerDamageSpill { get; }
    public WeaponDefinition(int range, int acquireRange, int damage, int cooldownTicks, int windupTicks = 2,
        string id = "weapon", ProjectileKind projectile = ProjectileKind.Hitscan, WeaponEffect effect = WeaponEffect.Damage,
        int projectileSpeed = 256, int? travelLimit = null, int splashRadius = 0,
        int burst = 1, int burstIntervalTicks = 2, int animationTicks = 4,
        int livingModifier = 100, int mechanicalModifier = 100, int buildingModifier = 100, bool friendlyFire = false, int passengerDamage = 0, bool passengerDamageSpill = false, TargetCategories? targets = null, bool spatialTrajectory = false, TargetCategories? excludedTargets = null,
        SensorDisplay minimumContact = SensorDisplay.Model, ProjectileTrajectory? trajectory = null,
        int? trajectoryHeight = null, bool? homing = null, int scatterRadius = 0, int launchFan = 0,
        int minRange = 0, int? lightModifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Targets = targets ?? TargetCategories.Ground; ExcludedTargets = excludedTargets;
        Trajectory = trajectory ?? (spatialTrajectory ? ProjectileTrajectory.Direct : ProjectileTrajectory.Planar);
        TrajectoryHeight = trajectoryHeight ?? (Trajectory switch
        {
            ProjectileTrajectory.Arc => 2048,
            ProjectileTrajectory.Ballistic => 4096,
            ProjectileTrajectory.Cruise => 1536,
            _ => 0,
        });
        Homing = homing ?? projectile == ProjectileKind.Missile;
        if (scatterRadius is < 0 or > 8192 || scatterRadius > 0 && (!HasFlightProfile || Homing))
            throw new ArgumentOutOfRangeException(nameof(scatterRadius), "ScatterRadius requires an unguided flight profile and a radius in 0..8192.");
        if (launchFan is < 0 or > 1000 || launchFan > 0 && (Trajectory != ProjectileTrajectory.Ballistic || projectile != ProjectileKind.Missile || Homing || burst < 2))
            throw new ArgumentOutOfRangeException(nameof(launchFan), "LaunchFan requires a ballistic missile burst without homing and a spread in 0..1000.");
        ScatterRadius = scatterRadius; LaunchFan = launchFan;
        MinimumContact = minimumContact;
        if (range is < 512 or > MaximumRange) throw new ArgumentOutOfRangeException(nameof(range), $"Weapon range must be in 512..{MaximumRange}.");
        if (minRange < 0 || minRange > range)
            throw new ArgumentOutOfRangeException(nameof(minRange), $"Minimum range must be in 0..{range}.");
        if (acquireRange < range || acquireRange > MaximumRange)
            throw new ArgumentOutOfRangeException(nameof(acquireRange), $"Acquisition range must be in {range}..{MaximumRange}.");
        int travel = travelLimit ?? Math.Max(16384, 2 * range);
        if (travel < range || travel > MaximumTravelLimit)
            throw new ArgumentOutOfRangeException(nameof(travelLimit), $"Projectile travel limit must be in {range}..{MaximumTravelLimit}.");
        if (damage is < 0 or > 1000000 || damage == 0 && passengerDamage == 0 ||
            cooldownTicks is < 1 or > 1000000 || windupTicks is < 0 or > 1000 || burst is < 1 or > 64 ||
            burstIntervalTicks is < 1 or > 10000 || animationTicks is < 1 or > 1000 ||
            projectileSpeed is < 1 or > 4096 || splashRadius is < 0 or > 8192 ||
            passengerDamage is < 0 or > 1000000 || passengerDamageSpill && passengerDamage == 0 ||
            !Enum.IsDefined(projectile) || !Enum.IsDefined(Trajectory) || spatialTrajectory && Trajectory != ProjectileTrajectory.Direct ||
            Homing && projectile != ProjectileKind.Missile ||
            (HasFlightProfile ? projectile == ProjectileKind.Hitscan || TrajectoryHeight is < 1 or > 16384 : trajectoryHeight.HasValue) ||
            !Enum.IsDefined(effect) || !Enum.IsDefined(minimumContact) ||
            livingModifier is < 0 or > 1000 || mechanicalModifier is < 0 or > 1000 || buildingModifier is < 0 or > 1000 || lightModifier is < 0 or > 1000 ||
            effect == WeaponEffect.Repair && (projectile != ProjectileKind.Hitscan || splashRadius != 0 || friendlyFire || passengerDamage != 0 || minimumContact != SensorDisplay.Model))
            throw new ArgumentException("Invalid weapon range, timing, projectile or effect parameters.");
        Id = id; Range = range; MinRange = minRange; AcquireRange = acquireRange; Damage = damage; CooldownTicks = cooldownTicks; WindupTicks = windupTicks;
        Projectile = projectile; Effect = effect; ProjectileSpeed = projectileSpeed; TravelLimit = travel; SplashRadius = splashRadius;
        Burst = burst; BurstIntervalTicks = burstIntervalTicks; AnimationTicks = animationTicks;
        LivingModifier = livingModifier; MechanicalModifier = mechanicalModifier; BuildingModifier = buildingModifier; FriendlyFire = friendlyFire; PassengerDamage = passengerDamage; PassengerDamageSpill = passengerDamageSpill;
        LightModifier = lightModifier ?? mechanicalModifier;
    }
    public int Modifier(ArmorKind armor) => armor switch
    {
        ArmorKind.Living => LivingModifier,
        ArmorKind.Light => LightModifier,
        ArmorKind.Mechanical => MechanicalModifier,
        _ => BuildingModifier
    };
    internal bool BelowMinimum(long distanceSquared) => distanceSquared < (long)MinRange * MinRange;
    public bool CanTarget(TargetCategories categories) => Targets.Intersects(categories) && ExcludedTargets?.Intersects(categories) != true;
    public bool CanAttackContact(SensorDisplay level) => Effect == WeaponEffect.Damage && level >= MinimumContact;
    public void WriteState(BinaryWriter writer)
    {
        if (MinRange != 0 || LightModifier != MechanicalModifier) { writer.Write(-34); writer.Write(MinRange); writer.Write(LightModifier); }
        if (ScatterRadius != 0 || LaunchFan != 0) { writer.Write(-33); writer.Write(ScatterRadius); writer.Write(LaunchFan); }
        if (MinimumContact != SensorDisplay.Model) { writer.Write(-31); writer.Write((byte)MinimumContact); }
        if (HasFlightProfile || Homing != (Projectile == ProjectileKind.Missile))
        { writer.Write(-32); writer.Write((byte)Trajectory); writer.Write(TrajectoryHeight); writer.Write(Homing); }
        Targets.Write(writer); writer.Write(ExcludedTargets is not null); ExcludedTargets?.Write(writer); writer.Write(SpatialTrajectory);
        writer.Write(Id); writer.Write(Range); writer.Write(AcquireRange); writer.Write(Damage); writer.Write(CooldownTicks); writer.Write(WindupTicks);
        writer.Write((int)Projectile); writer.Write((int)Effect); writer.Write(ProjectileSpeed); writer.Write(TravelLimit); writer.Write(SplashRadius);
        writer.Write(Burst); writer.Write(BurstIntervalTicks); writer.Write(AnimationTicks); writer.Write(LivingModifier); writer.Write(MechanicalModifier); writer.Write(BuildingModifier); writer.Write(FriendlyFire); writer.Write(PassengerDamage); writer.Write(PassengerDamageSpill);
    }
}

public sealed record WeaponMountDefinition(string Id, WeaponDefinition Weapon, string Animation = "fire", bool FireWhileMoving = false, bool FireFromTransport = false);
public readonly record struct WeaponPose(EntityId Entity, int Mount, FacingDirection PreviousFacing, FacingDirection Facing,
    AnimationPlayback Animation, long ReadyTick, int BurstRemaining, EntityId Target, bool AutoCast = false, bool Busy = false)
{
    [JsonIgnore] public long WindupEndTick { get; init; }
    [JsonIgnore] public bool Reloading { get; init; }
}
[Flags]
public enum HoverEndpoints { None = 0, Source = 1, Target = 2 }

/// <summary>Physical flight samples used to project the visual path between two sprite anchors.</summary>
public readonly record struct ProjectileProjection(HoverEndpoints Hover, int LaunchElevation, WorldPosition Destination, int Travelled);
/// <summary>Visual launch projection. Origin is available to observers who saw the shot fired.</summary>
public readonly record struct ProjectileLaunchProjection(WorldPosition? Origin, int HeightAboveActor, float PreviousWeight, float CurrentWeight);

/// <summary>Actor pose at the instant of firing, used to locate its visual muzzle.</summary>
public readonly record struct ProjectileDeparture(long Tick, EntityId Source, WorldPosition Origin,
    FacingDirection BodyFacing, FacingDirection MountFacing);

public readonly record struct ProjectilePose(long Id, int UnitType, int Mount, WorldPosition Previous, WorldPosition Current, FacingDirection Facing)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProjectileProjection Projection { get; init; }
    [JsonIgnore] public ProjectileLaunchProjection? LaunchProjection { get; init; }
    internal ulong LaunchObservers { get; init; }
    [JsonIgnore] public ProjectileDeparture Departure { get; init; }
}
