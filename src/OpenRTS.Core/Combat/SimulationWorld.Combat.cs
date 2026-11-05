using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly Dictionary<EntityId, long> _damage = new(), _healing = new();
    private readonly List<EntityId> _dead = [], _affected = [];
    private readonly List<EntityId> _combatCandidates = [];
    private bool ValidTarget(int index, EntityId id, WeaponDefinition weapon, out int target)
    {
        target = -1;
        if (!_indices.TryGetValue(id, out target) || index == target) return false;
        uint owner = _unitHandles[index].Get<OwnershipState>().Owner;
        if (owner == 0) return false;
        ref var other = ref _unitHandles[target];
        var otherIdentity = other.Get<UnitIdentity>();
        ref var otherOwnership = ref other.Get<OwnershipState>();
        if (_types[otherIdentity.UnitType].Destruction == DestructionPolicy.Invulnerable) return false;
        if (weapon.Modifier(_types[otherIdentity.UnitType].Armor) == 0) return false;
        return weapon.Effect == WeaponEffect.Repair
            ? otherOwnership.Owner == owner && other.Get<HealthState>().Current < _types[otherIdentity.UnitType].MaxHealth
            : otherOwnership.Owner != 0 && otherOwnership.Owner != owner;
    }
    private WeaponDefinition? OrderWeapon(int index, bool repair = false, EntityId target = default)
    {
        WeaponDefinition? best = null, compatible = null;
        var mounts = _types[_unitHandles[index].Get<UnitIdentity>().UnitType].Weapons;
        for (int m = 0; m < mounts.Count; m++)
        {
            var mount = mounts[m];
            if (MountAbility(_unitHandles[index].Get<UnitIdentity>().UnitType, m) >= 0 && !repair && !_unitHandles[index].Get<CombatState>().Weapons[m].AutoCast) continue;
            if ((mount.Weapon.Effect == WeaponEffect.Repair) != repair) continue;
            if (best is null || mount.Weapon.Range < best.Range) best = mount.Weapon;
            if (ValidTarget(index, target, mount.Weapon, out _) && (compatible is null || mount.Weapon.Range < compatible.Range)) compatible = mount.Weapon;
        }
        return compatible ?? best;
    }
    private bool InWeaponRange(int index, int target, int range)
    {
        ref var source = ref _unitHandles[index];
        var sourceIdentity = source.Get<UnitIdentity>(); ref var other = ref _unitHandles[target];
        var otherIdentity = other.Get<UnitIdentity>();
        long distance = ObjectGeometry.DistanceSquared(_types[sourceIdentity.UnitType], source.Get<SpatialState>().Position, _types[otherIdentity.UnitType], other.Get<SpatialState>().Position, out var from, out var to);
        return distance <= (long)range * range && (_baseGrid is null || TerrainGeometry.Clear(_baseGrid, from, to, 0));
    }
    private WorldPosition AimPoint(int source, int target) => ObjectGeometry.ClosestPoint(_types[_unitHandles[target].Get<UnitIdentity>().UnitType], _unitHandles[target].Get<SpatialState>().Position, _unitHandles[source].Get<SpatialState>().Position);

    private int AcquireWeaponTarget(int index, WeaponDefinition weapon, int range = -1)
    {
        if (_unitHandles[index].Get<OwnershipState>().Owner == 0) return -1;
        if (range < 0) range = weapon.Range;
        var position = _unitHandles[index].Get<SpatialState>().Position; var type = _types[_unitHandles[index].Get<UnitIdentity>().UnitType];
        int x = type.Footprint?.HalfWidth ?? type.Body.HalfWidth, y = type.Footprint?.HalfHeight ?? type.Body.HalfHeight;
        (_motion ?? _combatIndex)!.Query(new(position.X - x, position.Y - y), new(position.X + x, position.Y + y), range, _combatCandidates);
        int best = -1; long distance = (long)range * range;
        foreach (var candidateId in _combatCandidates)
        {
            if (!_indices.TryGetValue(candidateId, out int candidate)) continue;
            if (!ValidTarget(index, _unitHandles[candidate].Get<UnitIdentity>().Id, weapon, out _) || !InWeaponRange(index, candidate, range)) continue;
            long d = ObjectGeometry.DistanceSquared(type, position, _types[_unitHandles[candidate].Get<UnitIdentity>().UnitType], _unitHandles[candidate].Get<SpatialState>().Position, out _, out _);
            if (d < distance || d == distance && (best < 0 || _unitHandles[candidate].Get<UnitIdentity>().Id.Value < _unitHandles[best].Get<UnitIdentity>().Id.Value)) { best = candidate; distance = d; }
        }
        return best;
    }
    private void CancelWindups(int index)
    {
        foreach (ref var state in _unitHandles[index].Get<CombatState>().Weapons.AsSpan()) { state.ChargingTarget = default; state.FireAtTick = 0; state.Target = default; }
    }
    private void UpdateWeapons()
    {
        _damage.Clear(); _healing.Clear(); _dead.Clear();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            _stepCancellation.ThrowIfCancellationRequested();
            int i = _movementOrder[orderIndex]; ref var entity = ref _unitHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<UnitIdentity>();
            var type = _types[entityIdentity.UnitType];
            if (type.Weapons.Count == 0) continue;
            ref var entityCombat = ref entity.Get<CombatState>();
            ref var entityPresentation = ref entity.Get<PresentationState>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            ref var entityOrders = ref entity.Get<OrderState>();
            ref var entityMotion = ref entity.Get<MotionState>();
            entityCombat.WeaponReadyTick = 0;
            for (int mountIndex = 0; mountIndex < type.Weapons.Count; mountIndex++)
            {
                var mount = type.Weapons[mountIndex]; var weapon = mount.Weapon; ref var state = ref entityCombat.Weapons[mountIndex];
                if (!state.Animation.IsActive(Tick)) state.Animation = AnimationPlayback.None;
                if (type.Economy is { DisableWeapons: true } && LowPower(entityOwnership.Owner))
                { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; entityCombat.WeaponReadyTick = Math.Max(entityCombat.WeaponReadyTick, state.ReadyTick); continue; }
                bool repairOrder = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Repair;
                bool ordinaryMove = entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Move;
                int ability = MountAbility(entityIdentity.UnitType, mountIndex);
                bool manual = ability >= 0 && entityOrders.HasOrder && entityOrders.ActiveOrder.Kind == UnitActionKind.Ability && entityOrders.ActiveOrder.Ability == ability;
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
                        if (!ValidTarget(i, preferred, weapon, out target) || !InWeaponRange(i, target, weapon.Range))
                        {
                            target = -1;
                            if (!repairOrder && (weapon.Effect == WeaponEffect.Damage || state.AutoCast))
                            {
                                if (ValidTarget(i, state.Target, weapon, out int previous) && InWeaponRange(i, previous, weapon.Range)) target = previous;
                                else if ((Tick + entityIdentity.Id.Value + mountIndex) % 4 == 0 || entityOrders.OrderStarted == Tick) target = AcquireWeaponTarget(i, weapon);
                            }
                        }
                    }
                }
                if (target < 0 && !ground) { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; }
                else
                {
                    state.Target = ground ? default : _unitHandles[target].Get<UnitIdentity>().Id;
                    var aim = ground ? point : AimPoint(i, target); state.Facing = new(aim.X - entitySpatial.Position.X, aim.Y - entitySpatial.Position.Y);
                    if (Tick >= state.ReadyTick)
                    {
                        if (state.FireAtTick == 0 || state.ChargingTarget != state.Target)
                        { state.ChargingTarget = state.Target; state.FireAtTick = Tick + (state.BurstRemaining == 0 ? weapon.WindupTicks : 0); }
                        if (Tick >= state.FireAtTick)
                        {
                            if (state.BurstRemaining == 0) state.BurstRemaining = weapon.Burst;
                            state.BurstRemaining--; state.ReadyTick = Tick + (state.BurstRemaining > 0 ? weapon.BurstIntervalTicks : weapon.CooldownTicks);
                            state.ChargingTarget = default; state.FireAtTick = 0;
                            state.Animation = new(type.AnimationIndex(mount.Animation), Tick, weapon.AnimationTicks);
                            entityPresentation.WeaponAnimation = state.Animation;
                            WorldPosition muzzle, impactPoint;
                            if (ground) { impactPoint = point; muzzle = ObjectGeometry.ClosestPoint(type, entitySpatial.Position, point); }
                            else ObjectGeometry.DistanceSquared(type, entitySpatial.Position, _types[_unitHandles[target].Get<UnitIdentity>().UnitType], _unitHandles[target].Get<SpatialState>().Position, out muzzle, out impactPoint);
                            _events.Add(new(Tick, WorldEventKind.WeaponFired, entityIdentity.Id, impactPoint, entityIdentity.UnitType,
                                state.Animation.Animation, state.Animation.DurationTicks, TargetEntity: state.Target, Mount: mountIndex, SourcePosition: muzzle));
                            if (weapon.Projectile == ProjectileKind.Hitscan) Impact(weapon, entityOwnership.Owner, state.Target, impactPoint, entityIdentity.Id, entityIdentity.UnitType, mountIndex);
                            else Launch(i, target, mountIndex, weapon, ground ? point : null);
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
            ref var entity = ref _unitHandles[i];
            ref var entitySpatial = ref entity.Get<SpatialState>();
            var entityIdentity = entity.Get<UnitIdentity>(); ref var health = ref entity.Get<HealthState>(); int before = health.Current;
            _damage.TryGetValue(id, out long damage); health.Current = (int)Math.Max(0, health.Current - damage);
            if (before != health.Current) _events.Add(new(Tick, WorldEventKind.Damaged, id, entitySpatial.Position, entityIdentity.UnitType, Amount: before - health.Current));
            if (health.Current == 0)
            {
                if (_types[entityIdentity.UnitType].Destruction == DestructionPolicy.Neutralize) { health.Current = 1; ChangeOwner(i, 0); }
                else _dead.Add(id);
                continue;
            }
            before = health.Current; _healing.TryGetValue(id, out long healing); health.Current = (int)Math.Min(_types[entityIdentity.UnitType].MaxHealth, health.Current + healing);
            if (before != health.Current) _events.Add(new(Tick, WorldEventKind.Repaired, id, entitySpatial.Position, entityIdentity.UnitType, Amount: health.Current - before));
        }
        foreach (var id in _dead) DestroyIndex(_indices[id]);
        if (_dead.Count > 0) RebuildMotionIndex();
    }
    private void Impact(WeaponDefinition weapon, uint owner, EntityId target, WorldPosition position, EntityId source, int unitType, int mount)
    {
        if (weapon.SplashRadius == 0) { ApplyEffect(target); return; }
        // shortcut: uniform splash ignores cover; add falloff/occlusion when a weapon requires them.
        (_motion ?? _combatIndex)!.Query(position, position, weapon.SplashRadius, _combatCandidates);
        foreach (var id in _combatCandidates)
            if (_indices.TryGetValue(id, out int i) && ObjectGeometry.WithinRadius(_types[_unitHandles[i].Get<UnitIdentity>().UnitType], _unitHandles[i].Get<SpatialState>().Position, position, weapon.SplashRadius)) ApplyEffect(_unitHandles[i].Get<UnitIdentity>().Id);
        void ApplyEffect(EntityId id)
        {
            if (!_indices.TryGetValue(id, out int i)) return;
            ref var entity = ref _unitHandles[i];
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var entityOwnership = ref entity.Get<OwnershipState>();
            bool repair = weapon.Effect == WeaponEffect.Repair;
            if (repair ? entityOwnership.Owner != owner || id == source : entityOwnership.Owner == 0 || !weapon.FriendlyFire && entityOwnership.Owner == owner) return;
            if (_types[_unitHandles[i].Get<UnitIdentity>().UnitType].Destruction == DestructionPolicy.Invulnerable) return;
            long value = (long)weapon.Damage * weapon.Modifier(_types[entityIdentity.UnitType].Armor) / 100;
            if (value == 0) return;
            var effects = repair ? _healing : _damage;
            effects.TryGetValue(id, out long previous); effects[id] = previous + value;
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
            ref var entity = ref _unitHandles[i];
            var entityIdentity = entity.Get<UnitIdentity>();
            ref var combat = ref entity.Get<CombatState>();
            ref var orders = ref entity.Get<OrderState>();
            for (int mount = 0; mount < combat.Weapons.Length; mount++)
            {
                var state = combat.Weapons[mount];
                destination[offset++] = new(entityIdentity.Id, mount, state.PreviousFacing, state.Facing, state.Animation, state.ReadyTick, state.BurstRemaining, state.Target, state.AutoCast, state.FireAtTick > 0 || orders.HasOrder && orders.ActiveOrder.Kind == UnitActionKind.Ability && AbilityMount(entityIdentity.UnitType, orders.ActiveOrder.Ability) == mount);
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
