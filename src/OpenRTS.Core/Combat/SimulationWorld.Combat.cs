namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly Dictionary<EntityId, long> _damage = new();
    private readonly List<EntityId> _dead = [];
    private bool Enemy(int index, EntityId id, out int target)
    {
        target = -1;
        return _indices.TryGetValue(id, out target) && target != index && _entities[index].Owner != 0 &&
            _entities[target].Owner != 0 && _entities[index].Owner != _entities[target].Owner;
    }
    private bool InWeaponRange(int index, int target, int range) =>
        DistanceSquared(_entities[index].Position, _entities[target].Position) <= (long)range * range &&
        (_navigation is null || TerrainGeometry.Clear(_navigation.Grid, _entities[index].Position, _entities[target].Position, 0));

    private void UpdateWeapons()
    {
        _damage.Clear(); _dead.Clear();
        for (int orderIndex = 0; orderIndex < Count; orderIndex++)
        {
            int i = _movementOrder[orderIndex]; ref var entity = ref _entities[i]; var weapon = _types[entity.UnitType].Weapon;
            if (weapon is null || !Enemy(i, entity.CombatTarget, out int target) || !InWeaponRange(i, target, weapon.Range) || entity.Moving || !StopAllowed(entity.UnitType, entity.Position))
            { entity.ChargingTarget = default; entity.FireAtTick = 0; continue; }
            if (Tick < entity.WeaponReadyTick) continue;
            if (entity.ChargingTarget != entity.CombatTarget)
            { entity.ChargingTarget = entity.CombatTarget; entity.FireAtTick = Tick + weapon.WindupTicks; }
            if (Tick < entity.FireAtTick) continue;
            entity.WeaponReadyTick = Tick + weapon.CooldownTicks; entity.ChargingTarget = default; entity.FireAtTick = 0;
            entity.WeaponAnimation = new(_types[entity.UnitType].AnimationIndex("fire"), Tick, Math.Min(6, weapon.CooldownTicks));
            _events.Add(new(Tick, WorldEventKind.WeaponFired, entity.Id, _entities[target].Position, entity.UnitType,
                entity.WeaponAnimation.Animation, entity.WeaponAnimation.DurationTicks, TargetEntity: entity.CombatTarget));
            _damage.TryGetValue(entity.CombatTarget, out long previous); _damage[entity.CombatTarget] = previous + weapon.Damage;
        }
        if (_damage.Count == 0) return;
        foreach (var (id, damage) in _damage.OrderBy(p => p.Key.Value))
        {
            int i = _indices[id]; ref var entity = ref _entities[i];
            int before = entity.Health; entity.Health = (int)Math.Max(0, entity.Health - damage);
            _events.Add(new(Tick, WorldEventKind.Damaged, id, entity.Position, entity.UnitType, Amount: before - entity.Health));
            if (entity.Health == 0) _dead.Add(id);
        }
        foreach (var id in _dead) DestroyIndex(_indices[id]);
        if (_dead.Count > 0) RebuildMotionIndex();
    }

}
