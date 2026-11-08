using OpenRTS.Core.Simulation;
using OpenRTS.Core.Transport;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly Dictionary<EntityId, long> _damage = new(), _healing = new();
    private readonly List<EntityId> _dead = [], _affected = [], _disabledTransports = [];
    private readonly List<EntityId> _combatCandidates = [];
    private bool ValidTarget(int index, EntityId id, WeaponDefinition weapon, out int target)
    {
        target = -1;
        if (!_indices.TryGetValue(id, out target) || index == target || !IsOnMap(_entityHandles[target])) return false;
        uint owner = _entityHandles[index].Get<OwnershipState>().Owner;
        if (owner == 0 || !WeaponAccepts(weapon, _entityHandles[target])) return false;
        ref var other = ref _entityHandles[target];
        var otherIdentity = other.Get<EntityIdentity>();
        ref var otherOwnership = ref other.Get<OwnershipState>();
        if (_types[otherIdentity.Type].Destruction == DestructionPolicy.Invulnerable) return false;
        if ((weapon.Damage == 0 || weapon.Modifier(_types[otherIdentity.Type].Armor) == 0) &&
            (weapon.PassengerDamage == 0 || _transport?.HasPassengers(other) != true)) return false;
        return weapon.Effect == WeaponEffect.Repair
            ? otherOwnership.Owner == owner && other.Get<HealthState>().Current < _types[otherIdentity.Type].MaxHealth
            : otherOwnership.Owner != 0 && otherOwnership.Owner != owner;
    }
    private WeaponDefinition? OrderWeapon(int index, bool repair = false, EntityId target = default)
    {
        WeaponDefinition? best = null, compatible = null;
        var actor = _entityHandles[index];
        if (UpgradeImmobilizes(actor)) return null;
        var type = TypeOf(actor);
        var mounts = type.Weapons;
        int enabled = type.EnabledMounts(CompletedUpgrade(actor));
        for (int m = 0; m < mounts.Count; m++)
        {
            if ((enabled & (1 << m)) == 0) continue;
            var mount = mounts[m];
            if (MountAbility(_entityHandles[index].Get<EntityIdentity>().Type, m) >= 0 && !repair && !_entityHandles[index].Get<CombatState>().Weapons[m].AutoCast) continue;
            if ((mount.Weapon.Effect == WeaponEffect.Repair) != repair) continue;
            if (best is null || mount.Weapon.Range < best.Range) best = mount.Weapon;
            if (ValidTarget(index, target, mount.Weapon, out _) && (compatible is null || mount.Weapon.Range < compatible.Range)) compatible = mount.Weapon;
        }
        var carrier = _entityHandles[index];
        if (!repair && _transport is { } transport)
            foreach (var id in transport.FiringPassengers(carrier))
            {
                var passenger = _entityHandles[_indices[id]];
                var passengerType = TypeOf(passenger);
                int enabledPassengerMounts = passengerType.EnabledMounts(CompletedUpgrade(passenger));
                for (int m = 0; m < passengerType.Weapons.Count; m++)
                {
                    var mount = passengerType.Weapons[m];
                    if ((enabledPassengerMounts & (1 << m)) == 0 || !mount.FireFromTransport ||
                        mount.Weapon.Effect != WeaponEffect.Damage) continue;
                    if (best is null || mount.Weapon.Range < best.Range) best = mount.Weapon;
                    if (ValidTarget(index, target, mount.Weapon, out _) && (compatible is null || mount.Weapon.Range < compatible.Range)) compatible = mount.Weapon;
                }
            }
        return compatible ?? best;
    }
    private bool InWeaponRange(int index, int target, int range) => InWeaponRange(index, target, range, out _);

    private bool InWeaponRange(int index, int target, int range, out long distanceSquared)
    {
        ref var source = ref _entityHandles[index];
        ref var other = ref _entityHandles[target];
        distanceSquared = ObjectGeometry.DistanceSquared(TypeOf(source), source.Get<SpatialState>().Position, TypeOf(other), other.Get<SpatialState>().Position, out var from, out var to);
        bool spatial = IsAircraft(source) || IsAircraft(other);
        long dz = (long)from.Elevation - to.Elevation;
        return distanceSquared <= (long)range * range && (!spatial || distanceSquared + dz * dz <= (long)range * range) &&
            (_baseGrid is null || (spatial ? SpatialTerrainClear(from, to) : TerrainGeometry.Clear(_baseGrid, from, to, 0)));
    }
    private WorldPosition AimPoint(int source, int target) => ObjectGeometry.ClosestPoint(TypeOf(_entityHandles[target]), _entityHandles[target].Get<SpatialState>().Position, _entityHandles[source].Get<SpatialState>().Position);

    private int AcquireWeaponTarget(int index, WeaponDefinition weapon, int range = -1)
    {
        var origin = _entityHandles[index];
        uint owner = origin.Get<OwnershipState>().Owner;
        if (owner == 0) return -1;
        if (range < 0) range = weapon.Range;
        var position = origin.Get<SpatialState>().Position;
        var identity = origin.Get<EntityIdentity>();
        var type = TypeOf(origin);
        int x = type.Footprint?.HalfWidth ?? type.Body.HalfWidth, y = type.Footprint?.HalfHeight ?? type.Body.HalfHeight;
        (_combatIndex ?? _motion)!.Query(new(position.X - x, position.Y - y), new(position.X + x, position.Y + y), range, _combatCandidates);
        var rule = _combatTargetRule;
        var query = new CombatTargetQuery(identity.Id, owner, type, weapon, range);
        int best = -1;
        EntityId bestId = default;
        long bestScore = 0, bestDistance = 0;
        foreach (var candidateId in _combatCandidates)
        {
            if (!ValidTarget(index, candidateId, weapon, out int candidate) ||
                !InWeaponRange(index, candidate, range, out long distance)) continue;
            long score = 0;
            if (rule is not null)
            {
                var actor = _entityHandles[candidate];
                var target = new CombatTargetCandidate(candidateId, actor.Get<OwnershipState>().Owner, TypeOf(actor),
                    actor.Get<HealthState>().Current, distance);
                if (rule.Score(in query, in target) is not { } priority) continue;
                score = priority;
            }
            if (best < 0 || score > bestScore || score == bestScore &&
                (distance < bestDistance || distance == bestDistance && candidateId.Value < bestId.Value))
            {
                best = candidate;
                bestId = candidateId;
                bestScore = score;
                bestDistance = distance;
            }
        }
        return best;
    }
    private void CancelWindups(int index)
    {
        foreach (ref var state in _entityHandles[index].Get<CombatState>().Weapons.AsSpan()) { state.ChargingTarget = default; state.FireAtTick = 0; state.Target = default; }
    }
    private void UpdateWeapons()
    {
        _damage.Clear(); _healing.Clear(); _dead.Clear(); _disabledTransports.Clear();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            StepCancellation.ThrowIfCancellationRequested();
            int i = _movementOrder[orderIndex]; ref var entity = ref _entityHandles[i];
            var entityIdentity = entity.Get<EntityIdentity>();
            var type = TypeOf(entity);
            if (type.Weapons.Count == 0 || (ReadLocalConditions(entity) & GameplayConditions.Constructing) != 0) continue;
            var carrierId = _transport?.CarrierOf(entity) ?? default;
            int origin = carrierId.Value == 0 ? i : _indices[carrierId];
            var carrier = _entityHandles[origin];
            bool stored = origin != i;
            var sourceType = stored ? TypeOf(carrier) : type;
            if (UpgradeImmobilizes(carrier) || stored && !_transport!.AllowsPassengerFire(carrier)) continue;
            int enabled = type.EnabledMounts(CompletedUpgrade(entity));
            ref var entitySpatial = ref carrier.Get<SpatialState>();
            ref var entityCombat = ref entity.Get<CombatState>();
            ref var entityPresentation = ref entity.Get<PresentationState>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            ref var entityOrders = ref carrier.Get<OrderState>();
            ref var entityMotion = ref carrier.Get<MotionState>();
            bool weaponsDisabled = !FlightCanFire(entity) || WeaponsDisabled(entityIdentity.Id) ||
                sourceType.DisableWeaponsOnLowPower && ReadConditions(sourceType, entityOwnership.Owner).HasFlag(GameplayConditions.LowPower);
            entityCombat.WeaponReadyTick = 0;
            for (int mountIndex = 0; mountIndex < type.Weapons.Count; mountIndex++)
            {
                var mount = type.Weapons[mountIndex]; var weapon = mount.Weapon; ref var state = ref entityCombat.Weapons[mountIndex];
                if (IsAircraft(entity) && sourceType.Flight!.Ammo > 0 && carrier.Get<FlightState>().Ammo == 0)
                { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; continue; }
                if ((enabled & (1 << mountIndex)) == 0 ||
                    stored && (!mount.FireFromTransport || weapon.Effect != WeaponEffect.Damage)) continue;
                if (!state.Animation.IsActive(Tick)) state.Animation = AnimationPlayback.None;
                if (weaponsDisabled)
                { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; entityCombat.WeaponReadyTick = Math.Max(entityCombat.WeaponReadyTick, state.ReadyTick); continue; }
                bool repairOrder = !stored && entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Repair;
                bool ordinaryMove = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Move;
                int ability = MountAbility(entityIdentity.Type, mountIndex);
                bool manual = !stored && ability >= 0 && entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Ability && entityOrders.ActiveOrder.Ability == ability;
                bool allowed = manual || ability < 0 || state.AutoCast || repairOrder && weapon.Effect == WeaponEffect.Repair;
                int target = -1; bool ground = false; WorldPosition point = default;
                if (allowed && (!ordinaryMove || mount.FireWhileMoving) && (!entityMotion.Moving || mount.FireWhileMoving) &&
                    (weapon.Effect != WeaponEffect.Repair || repairOrder || manual || state.AutoCast))
                {
                    if (manual)
                    {
                        var order = entityOrders.ActiveOrder;
                        if (AbilityInRange(i, order, weapon))
                        { ground = order.Target.Value == 0; point = order.Position; if (!ground) ValidTarget(i, order.Target, weapon, out target); }
                    }
                    else
                    {
                        var preferred = repairOrder ? entityOrders.ActiveOrder.Target : entityOrders.CombatTarget;
                        if (!ValidTarget(origin, preferred, weapon, out target) || !InWeaponRange(origin, target, weapon.Range))
                        {
                            target = -1;
                            if (!repairOrder && (weapon.Effect == WeaponEffect.Damage || state.AutoCast))
                            {
                                if (ValidTarget(origin, state.Target, weapon, out int previous) && InWeaponRange(origin, previous, weapon.Range)) target = previous;
                                else if ((Tick + entityIdentity.Id.Value + mountIndex) % 4 == 0 || entityOrders.OrderStarted == Tick) target = AcquireWeaponTarget(origin, weapon);
                            }
                        }
                    }
                }
                if (target < 0 && !ground) { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; }
                else
                {
                    state.Target = ground ? default : _entityHandles[target].Get<EntityIdentity>().Id;
                    var aim = ground ? point : AimPoint(origin, target); state.Facing = new(aim.X - entitySpatial.Position.X, aim.Y - entitySpatial.Position.Y);
                    if (Tick >= state.ReadyTick && FlightFiringArc(entity, aim))
                    {
                        if (state.FireAtTick == 0 || state.ChargingTarget != state.Target)
                        { state.ChargingTarget = state.Target; state.FireAtTick = Tick + (state.BurstRemaining == 0 ? weapon.WindupTicks : 0); }
                        if (Tick >= state.FireAtTick)
                        {
                            if (state.BurstRemaining == 0) state.BurstRemaining = weapon.Burst;
                            if (IsAircraft(entity) && sourceType.Flight!.Ammo > 0)
                            {
                                var flight = carrier.Get<FlightState>(); flight.Ammo--;
                                if (flight.Ammo == 0) { flight.ReturnRequested = true; flight.RequestTick = Tick; }
                            }
                            state.BurstRemaining--; state.ReadyTick = Tick + (state.BurstRemaining > 0 ? weapon.BurstIntervalTicks : weapon.CooldownTicks);
                            state.ChargingTarget = default; state.FireAtTick = 0;
                            state.Animation = new(type.AnimationIndex(mount.Animation), Tick, weapon.AnimationTicks);
                            entityPresentation.WeaponAnimation = state.Animation;
                            WorldPosition muzzle, impactPoint;
                            if (ground) { impactPoint = point; muzzle = ObjectGeometry.ClosestPoint(sourceType, entitySpatial.Position, point); }
                            else ObjectGeometry.DistanceSquared(sourceType, entitySpatial.Position, TypeOf(_entityHandles[target]), _entityHandles[target].Get<SpatialState>().Position, out muzzle, out impactPoint);
                            _events.Add(new(Tick, WorldEventKind.WeaponFired, entityIdentity.Id, impactPoint, entityIdentity.Type,
                                state.Animation.Animation, state.Animation.DurationTicks, TargetEntity: state.Target, Mount: mountIndex, SourcePosition: muzzle));
                            if (weapon.Projectile == ProjectileKind.Hitscan) Impact(weapon, entityOwnership.Owner, state.Target, impactPoint, entityIdentity.Id, entityIdentity.Type, mountIndex);
                            else Launch(origin, target, mountIndex, weapon, ground ? point : null, entityIdentity.Type);
                            if (manual && state.BurstRemaining == 0) { entityOrders.HasOrder = false; entityOrders.CombatTarget = default; entityOrders.OrderPhase = OrderPhase.Idle; }
                        }
                    }
                }
                entityCombat.WeaponReadyTick = Math.Max(entityCombat.WeaponReadyTick, state.ReadyTick);
            }
        }
        UpdateProjectiles();
        if (_damage.Count == 0 && _healing.Count == 0) return;
        _affected.Clear(); _affected.AddRange(_damage.Keys);
        foreach (var id in _healing.Keys) if (!_damage.ContainsKey(id)) _affected.Add(id);
        _affected.Sort((a, b) => a.Value.CompareTo(b.Value));
        foreach (var id in _affected)
        {
            if (!_indices.TryGetValue(id, out int i)) continue;
            ref var entity = ref _entityHandles[i];
            var carrierId = _transport?.CarrierOf(entity) ?? default;
            int origin = carrierId.Value == 0 ? i : _indices[carrierId];
            var carrier = _entityHandles[origin];
            ref var entitySpatial = ref carrier.Get<SpatialState>();
            var entityIdentity = entity.Get<EntityIdentity>(); ref var health = ref entity.Get<HealthState>(); int before = health.Current;
            _damage.TryGetValue(id, out long damage); health.Current = (int)Math.Max(0, health.Current - damage);
            if (before != health.Current) _events.Add(new(Tick, WorldEventKind.Damaged, id, entitySpatial.Position, entityIdentity.Type, Amount: before - health.Current));
            if (health.Current == 0)
            {
                if (_types[entityIdentity.Type].Destruction == DestructionPolicy.Neutralize)
                {
                    health.Current = _types[entityIdentity.Type].Supply?.Role == SupplyRole.Mine ? _types[entityIdentity.Type].MaxHealth : 1;
                    if (_transport?.HasPassengers(entity) == true) _disabledTransports.Add(id);
                    ChangeOwner(i, 0); }
                else _dead.Add(id);
                continue;
            }
            before = health.Current; _healing.TryGetValue(id, out long healing); health.Current = (int)Math.Min(_types[entityIdentity.Type].MaxHealth, health.Current + healing);
            if (before != health.Current) _events.Add(new(Tick, WorldEventKind.Repaired, id, entitySpatial.Position, entityIdentity.Type, Amount: health.Current - before));
        }
        foreach (var id in _dead) if (_indices.TryGetValue(id, out int index)) DestroyIndex(index);
        foreach (var id in _disabledTransports) _transport!.RemovePassengers(_entityHandles[_indices[id]]);
        if (_dead.Count > 0 || _disabledTransports.Count > 0) RebuildMotionIndex();
    }
    private void Impact(WeaponDefinition weapon, uint owner, EntityId target, WorldPosition position, EntityId source, int unitType, int mount)
    {
        if (weapon.SplashRadius == 0) { ApplyEffect(target); return; }
        // shortcut: uniform splash ignores cover; add falloff/occlusion when a weapon requires them.
        (_combatIndex ?? _motion)!.Query(position, position, weapon.SplashRadius, _combatCandidates);
        foreach (var id in _combatCandidates)
            if (_indices.TryGetValue(id, out int i) && ObjectGeometry.WithinRadius(TypeOf(_entityHandles[i]), _entityHandles[i].Get<SpatialState>().Position, position, weapon.SplashRadius)) ApplyEffect(_entityHandles[i].Get<EntityIdentity>().Id);
        void ApplyEffect(EntityId id)
        {
            if (!_indices.TryGetValue(id, out int i)) return;
            ref var entity = ref _entityHandles[i];
            var entityIdentity = entity.Get<EntityIdentity>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            bool repair = weapon.Effect == WeaponEffect.Repair;
            if (repair ? entityOwnership.Owner != owner || id == source : entityOwnership.Owner == 0 || !weapon.FriendlyFire && entityOwnership.Owner == owner) return;
            if (!WeaponAccepts(weapon, entity)) return;
            if (weapon.SpatialTrajectory && Math.Abs((long)entity.Get<SpatialState>().Position.Elevation - position.Elevation) >
                weapon.SplashRadius + (TypeOf(entity).Flight?.HalfHeight ?? 256)) return;
            if (_types[_entityHandles[i].Get<EntityIdentity>().Type].Destruction == DestructionPolicy.Invulnerable) return;
            long value = (long)weapon.Damage * weapon.Modifier(_types[entityIdentity.Type].Armor) / 100;
            if (value > 0)
            {
                var effects = repair ? _healing : _damage;
                effects.TryGetValue(id, out long previous);
                effects[id] = previous + value;
            }
            if (weapon.PassengerDamage == 0 || _transport is null) return;
            int remaining = weapon.PassengerDamage;
            foreach (var occupant in _transport.Passengers(entity))
            {
                _damage.TryGetValue(occupant, out long pending);
                int health = _entityHandles[_indices[occupant]].Get<HealthState>().Current;
                int dealt = (int)Math.Min(remaining, Math.Max(0, health - pending));
                if (dealt > 0) _damage[occupant] = pending + dealt;
                remaining -= dealt;
                if (!weapon.PassengerDamageSpill || remaining == 0) break;
            }
        }
    }
    public int WeaponCount => CountWeapons();
    public void CopyWeaponPoses(Span<WeaponPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < WeaponCount) throw new ArgumentException("Destination is too small.", nameof(destination));
        int offset = 0;
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entityHandles[i];
            var entityIdentity = entity.Get<EntityIdentity>();
            ref var combat = ref entity.Get<CombatState>();
            ref var orders = ref entity.Get<OrderState>();
            for (int mount = 0; mount < combat.Weapons.Length; mount++)
            {
                var state = combat.Weapons[mount];
                destination[offset++] = new(entityIdentity.Id, mount, state.PreviousFacing, state.Facing, state.Animation, state.ReadyTick, state.BurstRemaining, state.Target, state.AutoCast, state.FireAtTick > 0 || orders.HasOrder && orders.ActiveOrder.Kind == UnitActionKind.Ability && AbilityMount(entityIdentity.Type, orders.ActiveOrder.Ability) == mount);
            }
        }
    }
    private static void WriteWeapons(BinaryWriter writer, Frent.Entity entity)
    {
        ref var entityCombat = ref entity.Get<CombatState>();
        writer.Write(entityCombat.Weapons.Length);
        foreach (var state in entityCombat.Weapons)
        {
            writer.Write(state.Target.Value); writer.Write(state.ChargingTarget.Value); writer.Write(state.ReadyTick); writer.Write(state.FireAtTick); writer.Write(state.BurstRemaining); writer.Write(state.AutoCast);
            writer.Write(state.Facing.X); writer.Write(state.Facing.Y); writer.Write(state.Animation.Animation); writer.Write(state.Animation.StartTick); writer.Write(state.Animation.DurationTicks);
        }
    }
}
