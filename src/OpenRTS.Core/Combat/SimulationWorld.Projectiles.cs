using OpenRTS.Core.Simulation;
namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private struct ProjectileState
    {
        public long Id, BornTick;
        public EntityId Source, Target;
        public uint Owner;
        public int UnitType, Mount, Remaining, InitialDistance;
        public WorldPosition Previous, Position, Destination;
        public WeaponDefinition Weapon;
    }
    private readonly List<ProjectileState> _projectiles = [];
    private long _nextProjectileId = 1;
    public int ProjectileCount => _projectiles.Count;
    private void Launch(int source, int target, int mount, WeaponDefinition weapon, WorldPosition? point = null)
    {
        ref var actor = ref _unitHandles[source];
        ref var actorSpatial = ref actor.Get<SpatialState>();
        var actorIdentity = actor.Get<UnitIdentity>();
        ref var actorOwnership = ref actor.Get<OwnershipState>();
        WorldPosition muzzle, destination;
        if (point is { } p) { destination = p; muzzle = ObjectGeometry.ClosestPoint(_types[actorIdentity.UnitType], actorSpatial.Position, p); }
        else ObjectGeometry.DistanceSquared(_types[actorIdentity.UnitType], actorSpatial.Position, _types[_unitHandles[target].Get<UnitIdentity>().UnitType], _unitHandles[target].Get<SpatialState>().Position, out muzzle, out destination);
        _projectiles.Add(new() { Id = checked(_nextProjectileId++), BornTick = Tick, Source = actorIdentity.Id, Target = target < 0 ? default : _unitHandles[target].Get<UnitIdentity>().Id,
            Owner = actorOwnership.Owner, UnitType = actorIdentity.UnitType, Mount = mount, Previous = muzzle, Position = muzzle,
            Destination = destination, Weapon = weapon, Remaining = weapon.TravelLimit,
            InitialDistance = Math.Max(1, (int)SquareRoot((ulong)DistanceSquared(muzzle, destination))) });
    }
    private void UpdateProjectiles()
    {
        int write = 0;
        for (int read = 0; read < _projectiles.Count; read++)
        {
            _stepCancellation.ThrowIfCancellationRequested();
            var shot = _projectiles[read]; var weapon = shot.Weapon; shot.Previous = shot.Position;
            if (shot.BornTick == Tick) { _projectiles[write++] = shot; continue; }
            if (weapon.Projectile == ProjectileKind.Missile)
            {
                // shortcut: steering is instantaneous; add turn limits when missiles need inertia.
                if (!_indices.TryGetValue(shot.Target, out int target)) { Expire(shot); continue; }
                shot.Destination = ObjectGeometry.ClosestPoint(_types[_unitHandles[target].Get<UnitIdentity>().UnitType], _unitHandles[target].Get<SpatialState>().Position, shot.Position);
            }
            int budget = Math.Min(shot.Remaining, weapon.ProjectileSpeed);
            var next = StepTowards(shot.Position, shot.Destination, budget);
            int travel = Math.Max(1, (int)SquareRoot((ulong)DistanceSquared(shot.Position, next)));
            // Round distance upwards so rounding cannot extend a missile's fuel budget.
            if ((long)travel * travel < DistanceSquared(shot.Position, next)) travel++;
            shot.Remaining -= travel;
            // shortcut: wall effects use the segment start; calculate exact entry when impact placement matters.
            if (_baseGrid is not null && !TerrainGeometry.Clear(_baseGrid, shot.Position, next, 0))
            { Hit(shot, default, shot.Position); continue; }
            EntityId hit = default; int fraction = 65537;
            if (weapon.Projectile != ProjectileKind.Grenade)
            {
                (_motion ?? _combatIndex)!.Query(shot.Position, next, 0, _combatCandidates);
                foreach (var candidateId in _combatCandidates)
                {
                    if (!_indices.TryGetValue(candidateId, out int i)) continue;
                    ref var body = ref _unitHandles[i];
                    ref var bodySpatial = ref body.Get<SpatialState>();
                    var bodyIdentity = body.Get<UnitIdentity>();
                    ref var bodyOwnership = ref body.Get<OwnershipState>();
                    if (bodyIdentity.Id == shot.Source || !weapon.FriendlyFire && bodyOwnership.Owner == shot.Owner) continue;
                    int t;
                    var type = _types[bodyIdentity.UnitType];
                    if (type.Footprint is { } footprint)
                    {
                        t = 65537;
                        for (int f = 0; f < footprint.TargetBounds.Count; f++) t = Math.Min(t, BodyGeometry.HitBoxFraction(shot.Position, next, footprint.TargetBounds[f].Translate(bodySpatial.Position)));
                    }
                    else if (type.Body.Kind == BodyKind.Box)
                        t = BodyGeometry.HitBoxFraction(new(shot.Position.X - bodySpatial.Previous.X, shot.Position.Y - bodySpatial.Previous.Y), new(next.X - bodySpatial.Position.X, next.Y - bodySpatial.Position.Y), ObjectGeometry.Box(type.Body, default));
                    else t = HitFraction(shot.Position, next, bodySpatial.Previous, bodySpatial.Position, type.Body.HalfWidth);
                    if (t < fraction || t == fraction && (hit.Value == 0 || bodyIdentity.Id.Value < hit.Value)) { fraction = t; hit = bodyIdentity.Id; }
                }
            }
            if (hit.Value != 0 && fraction <= 65536)
            { Hit(shot, hit, Interpolate(shot.Position, next, fraction)); continue; }
            shot.Position = next;
            if (shot.Position == shot.Destination) { Hit(shot, default, shot.Position); continue; }
            if (shot.Remaining <= 0) { Expire(shot); continue; }
            _projectiles[write++] = shot;
        }
        if (write < _projectiles.Count) _projectiles.RemoveRange(write, _projectiles.Count - write);
        void Hit(ProjectileState shot, EntityId target, WorldPosition point)
        {
            _events.Add(new(Tick, WorldEventKind.ProjectileImpact, shot.Source, point, shot.UnitType, TargetEntity: target,
                Mount: shot.Mount, SourcePosition: shot.Previous, ProjectileId: shot.Id));
            Impact(shot.Weapon, shot.Owner, target, point, shot.Source, shot.UnitType, shot.Mount);
        }
        void Expire(ProjectileState shot) => _events.Add(new(Tick, WorldEventKind.ProjectileExpired, shot.Source, shot.Position, shot.UnitType, Mount: shot.Mount, ProjectileId: shot.Id));
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
            if (shot.Weapon.Projectile == ProjectileKind.Grenade)
            {
                current = Arc(current); previous = Arc(previous);
                WorldPosition Arc(WorldPosition point)
                {
                    long remaining = (long)SquareRoot((ulong)DistanceSquared(point, shot.Destination));
                    long distance = shot.InitialDistance; remaining = Math.Min(remaining, distance);
                    return point with { Elevation = point.Elevation + (int)(4 * 1024 * remaining * (distance - remaining) / (distance * distance)) };
                }
            }
            destination[i] = new(shot.Id, shot.UnitType, shot.Mount, previous, current,
                new(shot.Destination.X - shot.Position.X, shot.Destination.Y - shot.Position.Y));
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
        }
    }
}
