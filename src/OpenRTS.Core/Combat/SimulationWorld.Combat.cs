namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private struct WeaponState
    {
        public EntityId Target, ChargingTarget;
        public long ReadyTick, FireAtTick;
        public int BurstRemaining;
        public bool AutoCast;
        public FacingDirection PreviousFacing, Facing;
        public AnimationPlayback Animation;
    }
    private readonly Dictionary<EntityId, long> _damage = new(), _healing = new();
    private readonly List<EntityId> _dead = [], _affected = [];
    private readonly List<int> _combatCandidates = [];
    private bool Enemy(int index, EntityId id, out int target)
    {
        target = -1;
        return _indices.TryGetValue(id, out target) && target != index && _entities[index].Owner != 0 &&
            _entities[target].Owner != 0 && _entities[index].Owner != _entities[target].Owner;
    }
    private bool ValidTarget(int index, EntityId id, WeaponDefinition weapon, out int target)
    {
        target = -1;
        if (!_indices.TryGetValue(id, out target) || index == target || _entities[index].Owner == 0) return false;
        ref var other = ref _entities[target];
        if (weapon.Modifier(_types[other.UnitType].Armor) == 0) return false;
        return weapon.Effect == WeaponEffect.Repair
            ? other.Owner == _entities[index].Owner && other.Health < _types[other.UnitType].MaxHealth
            : other.Owner != 0 && other.Owner != _entities[index].Owner;
    }
    private WeaponDefinition? OrderWeapon(int index, bool repair = false, EntityId target = default)
    {
        WeaponDefinition? best = null, compatible = null;
        var mounts = _types[_entities[index].UnitType].Weapons;
        for (int m = 0; m < mounts.Count; m++)
        {
            var mount = mounts[m];
            if (MountAbility(_entities[index].UnitType, m) >= 0 && !repair && !_entities[index].Weapons[m].AutoCast) continue;
            if ((mount.Weapon.Effect == WeaponEffect.Repair) != repair) continue;
            if (best is null || mount.Weapon.Range < best.Range) best = mount.Weapon;
            if (ValidTarget(index, target, mount.Weapon, out _) && (compatible is null || mount.Weapon.Range < compatible.Range)) compatible = mount.Weapon;
        }
        return compatible ?? best;
    }
    private bool InWeaponRange(int index, int target, int range)
    {
        ref var source = ref _entities[index]; ref var other = ref _entities[target];
        long distance = ObjectGeometry.DistanceSquared(_types[source.UnitType], source.Position, _types[other.UnitType], other.Position, out var from, out var to);
        return distance <= (long)range * range && (_baseGrid is null || TerrainGeometry.Clear(_baseGrid, from, to, 0));
    }
    private WorldPosition AimPoint(int source, int target) => ObjectGeometry.ClosestPoint(_types[_entities[target].UnitType], _entities[target].Position, _entities[source].Position);

    private int AcquireWeaponTarget(int index, WeaponDefinition weapon, int range = -1)
    {
        if (range < 0) range = weapon.Range;
        var position = _entities[index].Position; var type = _types[_entities[index].UnitType];
        int x = type.Footprint?.HalfWidth ?? type.Body.HalfWidth, y = type.Footprint?.HalfHeight ?? type.Body.HalfHeight;
        (_motion ?? _combatIndex)!.Query(new(position.X - x, position.Y - y), new(position.X + x, position.Y + y), range, _combatCandidates);
        int best = -1; long distance = (long)range * range;
        foreach (int candidate in _combatCandidates)
        {
            if (!ValidTarget(index, _entities[candidate].Id, weapon, out _) || !InWeaponRange(index, candidate, range)) continue;
            long d = ObjectGeometry.DistanceSquared(type, position, _types[_entities[candidate].UnitType], _entities[candidate].Position, out _, out _);
            if (d < distance || d == distance && (best < 0 || _entities[candidate].Id.Value < _entities[best].Id.Value)) { best = candidate; distance = d; }
        }
        return best;
    }
    private void CancelWindups(int index)
    {
        foreach (ref var state in _entities[index].Weapons.AsSpan()) { state.ChargingTarget = default; state.FireAtTick = 0; state.Target = default; }
    }
    private void UpdateWeapons()
    {
        _damage.Clear(); _healing.Clear(); _dead.Clear();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            _stepCancellation.ThrowIfCancellationRequested();
            int i = _movementOrder[orderIndex]; ref var entity = ref _entities[i]; var type = _types[entity.UnitType];
            entity.WeaponReadyTick = 0;
            for (int mountIndex = 0; mountIndex < type.Weapons.Count; mountIndex++)
            {
                var mount = type.Weapons[mountIndex]; var weapon = mount.Weapon; ref var state = ref entity.Weapons[mountIndex];
                if (!state.Animation.IsActive(Tick)) state.Animation = AnimationPlayback.None;
                bool repairOrder = entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.Repair;
                bool ordinaryMove = entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.Move;
                int ability = MountAbility(entity.UnitType, mountIndex);
                bool manual = ability >= 0 && entity.HasOrder && entity.ActiveOrder.Kind == UnitActionKind.Ability && entity.ActiveOrder.Ability == ability;
                bool allowed = manual || ability < 0 || state.AutoCast || repairOrder && weapon.Effect == WeaponEffect.Repair;
                int target = -1; bool ground = false; WorldPosition point = default;
                if (allowed && (!ordinaryMove || mount.FireWhileMoving) && (!entity.Moving || mount.FireWhileMoving) &&
                    (weapon.Effect != WeaponEffect.Repair || repairOrder || manual || state.AutoCast))
                {
                    if (manual)
                    {
                        var order = entity.ActiveOrder;
                        if (AbilityInRange(i, order, weapon))
                        { ground = order.Target.Value == 0; point = order.Position; if (!ground) ValidTarget(i, order.Target, weapon, out target); }
                    }
                    else
                    {
                        var preferred = repairOrder ? entity.ActiveOrder.Target : entity.CombatTarget;
                        if (!ValidTarget(i, preferred, weapon, out target) || !InWeaponRange(i, target, weapon.Range))
                        {
                            target = -1;
                            if (!repairOrder && (weapon.Effect == WeaponEffect.Damage || state.AutoCast))
                            {
                                if (ValidTarget(i, state.Target, weapon, out int previous) && InWeaponRange(i, previous, weapon.Range)) target = previous;
                                else if ((Tick + entity.Id.Value + mountIndex) % 4 == 0 || entity.OrderStarted == Tick) target = AcquireWeaponTarget(i, weapon);
                            }
                        }
                    }
                }
                if (target < 0 && !ground) { state.Target = state.ChargingTarget = default; state.FireAtTick = 0; }
                else
                {
                    state.Target = ground ? default : _entities[target].Id;
                    var aim = ground ? point : AimPoint(i, target); state.Facing = new(aim.X - entity.Position.X, aim.Y - entity.Position.Y);
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
                            entity.WeaponAnimation = state.Animation;
                            WorldPosition muzzle, impactPoint;
                            if (ground) { impactPoint = point; muzzle = ObjectGeometry.ClosestPoint(type, entity.Position, point); }
                            else ObjectGeometry.DistanceSquared(type, entity.Position, _types[_entities[target].UnitType], _entities[target].Position, out muzzle, out impactPoint);
                            _events.Add(new(Tick, WorldEventKind.WeaponFired, entity.Id, impactPoint, entity.UnitType,
                                state.Animation.Animation, state.Animation.DurationTicks, TargetEntity: state.Target, Mount: mountIndex, SourcePosition: muzzle));
                            if (weapon.Projectile == ProjectileKind.Hitscan) Impact(weapon, entity.Owner, state.Target, impactPoint, entity.Id, entity.UnitType, mountIndex);
                            else Launch(i, target, mountIndex, weapon, ground ? point : null);
                            if (manual && state.BurstRemaining == 0) { entity.HasOrder = false; entity.CombatTarget = default; entity.OrderPhase = OrderPhase.Idle; }
                        }
                    }
                }
                entity.WeaponReadyTick = Math.Max(entity.WeaponReadyTick, state.ReadyTick);
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
            ref var entity = ref _entities[i]; int before = entity.Health;
            _damage.TryGetValue(id, out long damage); entity.Health = (int)Math.Max(0, entity.Health - damage);
            if (before != entity.Health) _events.Add(new(Tick, WorldEventKind.Damaged, id, entity.Position, entity.UnitType, Amount: before - entity.Health));
            if (entity.Health == 0) { _dead.Add(id); continue; }
            before = entity.Health; _healing.TryGetValue(id, out long healing); entity.Health = (int)Math.Min(_types[entity.UnitType].MaxHealth, entity.Health + healing);
            if (before != entity.Health) _events.Add(new(Tick, WorldEventKind.Repaired, id, entity.Position, entity.UnitType, Amount: entity.Health - before));
        }
        foreach (var id in _dead) DestroyIndex(_indices[id]);
        if (_dead.Count > 0) RebuildMotionIndex();
    }
    private void Impact(WeaponDefinition weapon, uint owner, EntityId target, WorldPosition position, EntityId source, int unitType, int mount)
    {
        if (weapon.SplashRadius == 0) { ApplyEffect(target); return; }
        // shortcut: uniform splash ignores cover; add falloff/occlusion when a weapon requires them.
        (_motion ?? _combatIndex)!.Query(position, position, weapon.SplashRadius, _combatCandidates);
        foreach (int i in _combatCandidates)
            if (ObjectGeometry.WithinRadius(_types[_entities[i].UnitType], _entities[i].Position, position, weapon.SplashRadius)) ApplyEffect(_entities[i].Id);
        void ApplyEffect(EntityId id)
        {
            if (!_indices.TryGetValue(id, out int i)) return;
            ref var entity = ref _entities[i]; bool repair = weapon.Effect == WeaponEffect.Repair;
            if (repair ? entity.Owner != owner || id == source : entity.Owner == 0 || !weapon.FriendlyFire && entity.Owner == owner) return;
            long value = (long)weapon.Damage * weapon.Modifier(_types[entity.UnitType].Armor) / 100;
            if (value == 0) return;
            var effects = repair ? _healing : _damage;
            effects.TryGetValue(id, out long previous); effects[id] = previous + value;
        }
    }
    public int WeaponCount { get { int count = 0; for (int i = 0; i < Count; i++) count += _entities[i].Weapons.Length; return count; } }
    public void CopyWeaponPoses(Span<WeaponPose> destination)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("An interrupted world cannot publish a frame.");
        if (destination.Length < WeaponCount) throw new ArgumentException("Destination is too small.", nameof(destination));
        int offset = 0;
        for (int i = 0; i < Count; i++) for (int mount = 0; mount < _entities[i].Weapons.Length; mount++)
        {
            var state = _entities[i].Weapons[mount];
            destination[offset++] = new(_entities[i].Id, mount, state.PreviousFacing, state.Facing, state.Animation, state.ReadyTick, state.BurstRemaining, state.Target, state.AutoCast, state.FireAtTick > 0 || _entities[i].HasOrder && _entities[i].ActiveOrder.Kind == UnitActionKind.Ability && AbilityMount(_entities[i].UnitType, _entities[i].ActiveOrder.Ability) == mount);
        }
    }
    private static void WriteWeapons(BinaryWriter writer, EntityState entity)
    {
        writer.Write(entity.Weapons.Length);
        foreach (var state in entity.Weapons)
        {
            writer.Write(state.Target.Value); writer.Write(state.ChargingTarget.Value); writer.Write(state.ReadyTick); writer.Write(state.FireAtTick); writer.Write(state.BurstRemaining); writer.Write(state.AutoCast);
            writer.Write(state.Facing.X); writer.Write(state.Facing.Y); writer.Write(state.Animation.Animation); writer.Write(state.Animation.StartTick); writer.Write(state.Animation.DurationTicks);
        }
    }
}
