namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void ApplyPresentation(int index, WorldCommand command)
    {
        ref var entity = ref _entities[index]; var type = _types[entity.UnitType];
        if (command.Kind is CommandKind.SetVisualStates or CommandKind.ToggleVisualState)
        {
            if ((command.States & ~type.AllowedStateMask) != 0) { Reject(); return; }
            entity.VisualStates = command.Kind == CommandKind.ToggleVisualState ? entity.VisualStates ^ command.States : command.States;
        }
        else if (command.Kind == CommandKind.PlayAnimation)
        {
            if ((uint)command.Animation >= (uint)type.Animations.Count) { Reject(); return; }
            var playback = new AnimationPlayback(command.Animation, Tick, command.DurationTicks);
            if (command.Track == AnimationTrack.Body) { entity.BodyAnimation = playback; entity.BodyAnimationOverride = true; }
            else entity.WeaponAnimation = playback;
            _events.Add(new(Tick, WorldEventKind.AnimationPlayed, entity.Id, entity.Position, entity.UnitType,
                command.Animation, command.DurationTicks, command.Track));
        }
        else if (command.Kind == CommandKind.Aim)
        {
            if (command.TargetEntity.Value != 0 && !_indices.ContainsKey(command.TargetEntity))
            { _events.Add(new(Tick, WorldEventKind.MissingEntity, command.TargetEntity)); return; }
            entity.HasAim = true; entity.AimPoint = command.Position; entity.AimEntity = command.TargetEntity;
        }
        else { entity.HasAim = false; entity.AimEntity = default; entity.AimPoint = default; }
        void Reject() => _events.Add(new(Tick, WorldEventKind.InvalidPresentation, command.Entity));
    }

    private void UpdatePresentation()
    {
        for (int i = 0; i < Count; i++)
        {
            ref var entity = ref _entities[i]; var type = _types[entity.UnitType];
            int dx = entity.Position.X - entity.Previous.X, dy = entity.Position.Y - entity.Previous.Y;
            if (dx != 0 || dy != 0) entity.BodyFacing = new(dx, dy);
            if (entity.BodyAnimationOverride && !entity.BodyAnimation.IsActive(Tick)) entity.BodyAnimationOverride = false;
            if (!entity.BodyAnimationOverride)
            {
                int animation = dx != 0 || dy != 0 ? type.MoveAnimation : type.IdleAnimation;
                if (entity.BodyAnimation.Animation != animation || entity.BodyAnimation.DurationTicks != 0)
                    entity.BodyAnimation = new(animation, Tick, 0);
            }
            if (!entity.WeaponAnimation.IsActive(Tick)) entity.WeaponAnimation = AnimationPlayback.None;
            var aim = entity.Target;
            if (entity.HasAim)
            {
                if (entity.AimEntity.Value != 0)
                {
                    if (_indices.TryGetValue(entity.AimEntity, out int target)) aim = _entities[target].Position;
                    else { entity.HasAim = false; entity.AimEntity = default; entity.AimPoint = default; }
                }
                else aim = entity.AimPoint;
            }
            if (entity.HasAim || entity.Moving)
            {
                dx = aim.X - entity.Position.X; dy = aim.Y - entity.Position.Y;
                if (dx != 0 || dy != 0) entity.WeaponFacing = new(dx, dy);
            }
            else entity.WeaponFacing = entity.BodyFacing;
            foreach (ref var mount in entity.Weapons.AsSpan()) if (mount.Target.Value == 0) mount.Facing = entity.WeaponFacing;
        }
    }

    private static void WritePresentation(BinaryWriter writer, EntityState entity)
    {
        writer.Write(entity.Owner); writer.Write(entity.VisualStates);
        writer.Write(entity.BodyFacing.X); writer.Write(entity.BodyFacing.Y);
        writer.Write(entity.WeaponFacing.X); writer.Write(entity.WeaponFacing.Y);
        Write(entity.BodyAnimation); Write(entity.WeaponAnimation); writer.Write(entity.BodyAnimationOverride);
        writer.Write(entity.HasAim); writer.Write(entity.AimEntity.Value); writer.Write(entity.AimPoint.X); writer.Write(entity.AimPoint.Y);
        void Write(AnimationPlayback value) { writer.Write(value.Animation); writer.Write(value.StartTick); writer.Write(value.DurationTicks); }
    }
}
