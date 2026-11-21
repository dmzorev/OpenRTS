using OpenRTS.Core.Presentation;
using OpenRTS.Core.Simulation;
namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly record struct ProjectileLaunch(WorldPosition Origin, int Distance, ulong Observers);

    private struct ProjectileState
    {
        public long Id, BornTick;
        public EntityId Source, Target;
        public uint Owner;
        public int UnitType, Mount, Remaining, InitialDistance;
        public WorldPosition Previous, Position, Destination;
        public WeaponDefinition Weapon;
        public HoverEndpoints Hover;
        public int LaunchElevation;
        public ProjectileDeparture Departure;
        public ProjectileLaunch? LaunchAnchor;
        public FacingDirection FanDirection;
    }
    private readonly List<ProjectileState> _projectiles = [];
    private long _nextProjectileId = 1;
    public int ProjectileCount => _projectiles.Count;
    private ProjectileDeparture CaptureDeparture(int source, FacingDirection facing)
    {
        var actor = _entityHandles[source];
        var spatial = actor.Get<SpatialState>();
        var origin = spatial.Position;
        var motion = new FacingDirection(origin.X - spatial.Previous.X, origin.Y - spatial.Previous.Y);
        var body = TypeOf(actor).Flight is null && motion != default ? motion : actor.Get<PresentationState>().BodyFacing;
        return new(Tick, actor.Get<EntityIdentity>().Id, origin, body, facing);
    }

    private WorldPosition ScatterImpact(WorldPosition target, int radius, long projectileId, EntityId source)
    {
        var random = new DeterministicRandom(_random.State ^ (ulong)projectileId ^ ((ulong)source.Value << 32));
        int minX = Math.Max(-radius, -target.X), maxX = Math.Min(radius, Width - 1 - target.X);
        int minY = Math.Max(-radius, -target.Y), maxY = Math.Min(radius, Depth - 1 - target.Y);
        if (minX > maxX || minY > maxY) return target;
        int closestX = Math.Clamp(0, minX, maxX), closestY = Math.Clamp(0, minY, maxY);
        if (closestX * closestX + closestY * closestY > radius * radius) return target;
        int x, y;
        do
        {
            x = minX + random.NextInt(maxX - minX + 1);
            y = minY + random.NextInt(maxY - minY + 1);
        } while (x * x + y * y > radius * radius);
        var point = target with { X = target.X + x, Y = target.Y + y };
        return _baseGrid is { } grid ? point with { Elevation = grid.SurfaceElevationAt(point) + 128 } : point;
    }

    private static FacingDirection FanDirection(WeaponDefinition weapon, int remaining, FacingDirection aim)
    {
        if (weapon.LaunchFan == 0) return default;
        int edge = weapon.Burst - 1;
        int lane = edge - 2 * remaining;
        int offset = weapon.LaunchFan * lane / edge;
        return new(aim.X + (int)((long)-aim.Y * offset / 1000),
            aim.Y + (int)((long)aim.X * offset / 1000));
    }

    private void Launch(int source, int target, int mount, WeaponDefinition weapon, WorldPosition muzzle, WorldPosition destination,
        int weaponUnitType, HoverEndpoints hover, ProjectileDeparture departure, FacingDirection fan, ulong observers)
    {
        var actor = _entityHandles[source];
        _projectiles.Add(new() { Id = checked(_nextProjectileId++), BornTick = Tick, Source = actor.Get<EntityIdentity>().Id,
            Target = target < 0 ? default : _entityHandles[target].Get<EntityIdentity>().Id,
            Owner = actor.Get<OwnershipState>().Owner, UnitType = weaponUnitType, Mount = mount, Previous = muzzle, Position = muzzle,
            Destination = destination, Weapon = weapon, Remaining = weapon.TravelLimit, Hover = hover, LaunchElevation = muzzle.Elevation,
            Departure = departure, FanDirection = fan,
            LaunchAnchor = departure == default ? null :
                new(muzzle, Math.Max(1, ProjectileTravel(muzzle, destination, weapon.SpatialTrajectory)), observers),
            InitialDistance = Math.Max(1, (int)SquareRoot((ulong)DistanceSquared(muzzle, destination))) });
    }

    private static int ProjectileTravel(WorldPosition from, WorldPosition to, bool spatial)
    {
        long squared = ProjectileDistanceSquared(from, to, spatial);
        int distance = (int)SquareRoot((ulong)squared);
        return (long)distance * distance < squared ? distance + 1 : distance;
    }

    private void UpdateProjectiles()
    {
        int write = 0;
        for (int read = 0; read < _projectiles.Count; read++)
        {
            StepCancellation.ThrowIfCancellationRequested();
            var shot = _projectiles[read]; var weapon = shot.Weapon; shot.Previous = shot.Position;
            if (shot.BornTick == Tick) { _projectiles[write++] = shot; continue; }
            if (weapon.Homing && shot.Target != default)
            {
                // shortcut: steering is instantaneous; add turn limits when missiles need inertia.
                if (!_indices.TryGetValue(shot.Target, out int target) || !IsOnMap(_entityHandles[target])) { Expire(shot); continue; }
                var entity = _entityHandles[target];
                var position = entity.Get<SpatialState>().Position;
                shot.Destination = WeaponPoint(ObjectGeometry.ClosestPoint(TypeOf(entity), position, shot.Position) with { Elevation = position.Elevation },
                    weapon, Airborne(entity));
            }
            int budget = Math.Min(shot.Remaining, weapon.ProjectileSpeed);
            bool spatial = weapon.SpatialTrajectory;
            var next = weapon.HasFlightProfile ? ProfileStep(shot, shot.Remaining) :
                spatial ? SpatialStep(shot.Position, shot.Destination, budget) : StepTowards(shot.Position, shot.Destination, budget);
            // Round distance upwards so rounding cannot extend a missile's fuel budget.
            shot.Remaining -= Math.Max(1, ProjectileTravel(shot.Position, next, spatial));
            // shortcut: wall effects use the segment start; calculate exact entry when impact placement matters.
            bool traceTerrain = weapon.HasFlightProfile || TracesCliffRelief(weapon);
            if (!traceTerrain && _baseGrid is not null &&
                !(spatial ? SpatialTerrainClear(shot.Position, next) : TerrainGeometry.Clear(_baseGrid, shot.Position, next, 0)))
            { Hit(shot, default, shot.Position); continue; }
            int fraction = 65537;
            var hit = weapon.Projectile == ProjectileKind.Grenade ? default :
                TraceProjectileHit(weapon, shot.Source, shot.Owner, shot.Position, next, swept: true, out fraction);
            int wall = traceTerrain ? SpatialTerrainHit(shot.Position, next, profile: weapon.HasFlightProfile) : 65537;
            if (wall <= 65536 && wall <= fraction)
            { Hit(shot, default, SpatialInterpolate(shot.Position, next, wall)); continue; }
            if (hit.Value != 0 && fraction <= 65536)
            { Hit(shot, hit, spatial ? SpatialInterpolate(shot.Position, next, fraction) : Interpolate(shot.Position, next, fraction)); continue; }
            shot.Position = next;
            if (shot.Position == shot.Destination) { Hit(shot, default, shot.Position); continue; }
            if (shot.Remaining <= 0) { Expire(shot); continue; }
            _projectiles[write++] = shot;
        }
        if (write < _projectiles.Count) _projectiles.RemoveRange(write, _projectiles.Count - write);
        void Hit(ProjectileState shot, EntityId target, WorldPosition point)
        {
            var hover = target != default && _indices.TryGetValue(target, out int index) && TypeOf(_entityHandles[index]).Flight?.Mode == FlightMode.Hover
                ? HoverEndpoints.Target : HoverEndpoints.None;
            EmitEvent(new(Tick, WorldEventKind.ProjectileImpact, shot.Source, point, shot.UnitType, TargetEntity: target,
                Mount: shot.Mount, SourcePosition: shot.Previous, ProjectileId: shot.Id) { Hover = hover });
            Impact(shot.Weapon, shot.Owner, target, point, shot.Source, shot.UnitType, shot.Mount);
        }
        void Expire(ProjectileState shot) => EmitEvent(new(Tick, WorldEventKind.ProjectileExpired, shot.Source, shot.Position, shot.UnitType, Mount: shot.Mount, ProjectileId: shot.Id));
    }
    private EntityId TraceProjectileHit(WeaponDefinition weapon, EntityId source, uint owner,
        WorldPosition from, WorldPosition to, bool swept, out int fraction)
    {
        EntityId hit = default;
        fraction = 65537;
        (_combatIndex ?? _motion)!.Query(from, to, 0, _combatCandidates);
        foreach (var id in _combatCandidates)
        {
            if (!_indices.TryGetValue(id, out int i)) continue;
            var actor = _entityHandles[i];
            if (!WeaponAccepts(weapon, actor) || id == source || !weapon.FriendlyFire && AreAllied(actor.Get<OwnershipState>().Owner, owner))
                continue;
            var spatial = actor.Get<SpatialState>();
            var previous = swept ? spatial.Previous : spatial.Position;
            var type = TypeOf(actor);
            int t;
            if (weapon.SpatialTrajectory)
                t = SpatialHitFraction(from, to, previous, spatial.Position, type);
            else if (type.Footprint is { } footprint)
            {
                t = 65537;
                foreach (var bounds in footprint.TargetBounds)
                    t = Math.Min(t, BodyGeometry.HitBoxFraction(from, to, bounds.Translate(spatial.Position)));
            }
            else if (type.Body.Kind == BodyKind.Box)
                t = BodyGeometry.HitBoxFraction(new(from.X - previous.X, from.Y - previous.Y),
                    new(to.X - spatial.Position.X, to.Y - spatial.Position.Y), ObjectGeometry.Box(type.Body, default));
            else
                t = HitFraction(from, to, previous, spatial.Position, type.Body.HalfWidth);
            if (t > 65536 || t > fraction || t == fraction && hit != default && id.Value > hit.Value) continue;
            fraction = t;
            hit = id;
        }
        return hit;
    }

    private static WorldPosition Interpolate(WorldPosition a, WorldPosition b, int fraction) =>
        new(a.X + (int)(((long)b.X - a.X) * fraction / 65536), a.Y + (int)(((long)b.Y - a.Y) * fraction / 65536), a.Elevation);
    private static int HitFraction(WorldPosition a0, WorldPosition a1, WorldPosition b0, WorldPosition b1, int radius)
    {
        long x = (long)a0.X - b0.X, y = (long)a0.Y - b0.Y;
        long dx = (long)a1.X - a0.X - ((long)b1.X - b0.X), dy = (long)a1.Y - a0.Y - ((long)b1.Y - b0.Y);
        Int128 length = (Int128)dx * dx + (Int128)dy * dy;
        int closest = length == 0 ? 0 : (int)Int128.Clamp(-((Int128)x * dx + (Int128)y * dy) * 65536 / length, 0, 65536);
        bool Inside(int t)
        {
            Int128 px = (Int128)x * 65536 + (Int128)dx * t, py = (Int128)y * 65536 + (Int128)dy * t;
            return px * px + py * py <= (Int128)radius * radius * 65536 * 65536;
        }
        if (!Inside(closest)) return 65537;
        int low = 0, high = closest;
        while (low < high) { int mid = (low + high) / 2; if (Inside(mid)) high = mid; else low = mid + 1; }
        return low;
    }
    public void CopyProjectilePoses(Span<ProjectilePose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < _projectiles.Count) throw new ArgumentException("Destination is too small.", nameof(destination));
        for (int i = 0; i < _projectiles.Count; i++)
        {
            var shot = _projectiles[i]; var current = shot.Position; var previous = shot.Previous;
            ProjectileLaunchProjection? launchProjection = null;
            if (shot.LaunchAnchor is { } launch)
            {
                int travelled = shot.Weapon.TravelLimit - shot.Remaining;
                int previousTravel = shot.BornTick == Tick ? 0 :
                    travelled - Math.Max(1, ProjectileTravel(previous, current, shot.Weapon.SpatialTrajectory));
                launchProjection = new(launch.Origin, launch.Origin.Elevation - shot.Departure.Origin.Elevation,
                    Weight(previousTravel), Weight(travelled));
                float Weight(int distance)
                {
                    float fraction = Math.Clamp((float)distance / launch.Distance, 0, 1);
                    return 1 - (shot.Weapon.HasFlightProfile ? fraction * fraction * (3 - 2 * fraction) : fraction);
                }
            }
            if (!shot.Weapon.HasFlightProfile && shot.Weapon.Projectile == ProjectileKind.Grenade)
            {
                long distance = shot.InitialDistance;
                current = Arc(current); previous = Arc(previous);
                WorldPosition Arc(WorldPosition point)
                {
                    long remaining = Math.Min(distance, (long)SquareRoot((ulong)DistanceSquared(point, shot.Destination)));
                    return point with { Elevation = point.Elevation + (int)(4 * 1024 * remaining * (distance - remaining) / (distance * distance)) };
                }
            }
            destination[i] = new(shot.Id, shot.UnitType, shot.Mount, previous, current,
                shot.Weapon.HasFlightProfile && (current.X != previous.X || current.Y != previous.Y)
                    ? new(current.X - previous.X, current.Y - previous.Y)
                    : shot.BornTick == Tick && shot.FanDirection != default ? shot.FanDirection :
                        new(shot.Destination.X - shot.Position.X, shot.Destination.Y - shot.Position.Y))
            {
                Projection = shot.Hover == HoverEndpoints.None ? default :
                    new(shot.Hover, shot.LaunchElevation, shot.Destination, shot.Weapon.TravelLimit - shot.Remaining),
                Departure = shot.Departure,
                LaunchProjection = launchProjection,
                LaunchObservers = shot.LaunchAnchor?.Observers ?? 0
            };
        }
    }
    private void WriteProjectiles(BinaryWriter writer)
    {
        writer.Write(_nextProjectileId); writer.Write(_projectiles.Count);
        foreach (var shot in _projectiles)
        {
            writer.Write(shot.Id); writer.Write(shot.BornTick); writer.Write(shot.Source.Value); writer.Write(shot.Target.Value); writer.Write(shot.Owner);
            writer.Write(shot.UnitType); writer.Write(shot.Mount); writer.Write(shot.Remaining); writer.Write(shot.InitialDistance);
            writer.Write(shot.Position.X); writer.Write(shot.Position.Y); writer.Write(shot.Position.Elevation);
            writer.Write(shot.Destination.X); writer.Write(shot.Destination.Y); writer.Write(shot.Destination.Elevation);
            if (shot.Weapon.HasFlightProfile) writer.Write(shot.LaunchElevation);
            if (shot.Weapon.LaunchFan > 0) { writer.Write(shot.FanDirection.X); writer.Write(shot.FanDirection.Y); }
        }
    }
}
