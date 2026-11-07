using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void ApplyPresentation(int index, WorldCommand command)
    {
        ref var entity = ref _unitHandles[index];
        var entityIdentity = entity.Get<UnitIdentity>();
        ref var entityPresentation = ref entity.Get<PresentationState>();
        var type = _types[entityIdentity.UnitType];
        if (command.Kind is CommandKind.SetVisualStates or CommandKind.ToggleVisualState)
        {
            if ((command.States & ~type.AllowedStateMask) != 0) { Reject(); return; }
            entityPresentation.VisualStates = command.Kind == CommandKind.ToggleVisualState ? entityPresentation.VisualStates ^ command.States : command.States;
        }
        else if (command.Kind == CommandKind.PlayAnimation)
        {
            if ((uint)command.Animation >= (uint)type.Animations.Count) { Reject(); return; }
            var playback = new AnimationPlayback(command.Animation, Tick, command.DurationTicks);
            if (command.Track == AnimationTrack.Body) { entityPresentation.BodyAnimation = playback; entityPresentation.BodyAnimationOverride = true; }
            else entityPresentation.WeaponAnimation = playback;
            _events.Add(new(Tick, WorldEventKind.AnimationPlayed, entityIdentity.Id, entity.Get<SpatialState>().Position, entityIdentity.UnitType,
                command.Animation, command.DurationTicks, command.Track));
        }
        else if (command.Kind == CommandKind.Aim)
        {
            if (command.TargetEntity.Value != 0 && !_indices.ContainsKey(command.TargetEntity))
            { _events.Add(new(Tick, WorldEventKind.MissingEntity, command.TargetEntity)); return; }
            entityPresentation.HasAim = true; entityPresentation.AimPoint = command.Position; entityPresentation.AimEntity = command.TargetEntity;
        }
        else { entityPresentation.HasAim = false; entityPresentation.AimEntity = default; entityPresentation.AimPoint = default; }
        void Reject() => _events.Add(new(Tick, WorldEventKind.InvalidPresentation, command.Entity));
    }

    private void UpdatePresentation()
    {
        if (_unitStorage is null) return;
        foreach (var (presentation, combat, motion, position, definition) in
            _unitStorage.Query<PresentationState, CombatState, MotionState, SpatialState, EntityDefinition>())
        {
            ref var entityPresentation = ref presentation.Value;
            ref var entityCombat = ref combat.Value;
            ref var entityMotion = ref motion.Value;
            ref var spatial = ref position.Value;
            var type = definition.Value.Presentation!;
            int dx = spatial.Position.X - spatial.Previous.X, dy = spatial.Position.Y - spatial.Previous.Y;
            if (dx != 0 || dy != 0) entityPresentation.BodyFacing = new(dx, dy);
            if (entityPresentation.BodyAnimationOverride && !entityPresentation.BodyAnimation.IsActive(Tick)) entityPresentation.BodyAnimationOverride = false;
            if (!entityPresentation.BodyAnimationOverride)
            {
                int animation = (dx != 0 || dy != 0) && type.MoveAnimation >= 0 ? type.MoveAnimation : type.IdleAnimation;
                if (entityPresentation.BodyAnimation.Animation != animation || entityPresentation.BodyAnimation.DurationTicks != 0)
                    entityPresentation.BodyAnimation = new(animation, Tick, 0);
            }
            if (!entityPresentation.WeaponAnimation.IsActive(Tick)) entityPresentation.WeaponAnimation = AnimationPlayback.None;
            var aim = entityMotion.Target;
            if (entityPresentation.HasAim)
            {
                if (entityPresentation.AimEntity.Value != 0)
                {
                    if (_indices.TryGetValue(entityPresentation.AimEntity, out int target)) aim = _unitHandles[target].Get<SpatialState>().Position;
                    else { entityPresentation.HasAim = false; entityPresentation.AimEntity = default; entityPresentation.AimPoint = default; }
                }
                else aim = entityPresentation.AimPoint;
            }
            if (entityPresentation.HasAim || entityMotion.Moving)
            {
                dx = aim.X - spatial.Position.X; dy = aim.Y - spatial.Position.Y;
                if (dx != 0 || dy != 0) entityPresentation.WeaponFacing = new(dx, dy);
            }
            else entityPresentation.WeaponFacing = entityPresentation.BodyFacing;
            foreach (ref var mount in entityCombat.Weapons.AsSpan()) if (mount.Target.Value == 0) mount.Facing = entityPresentation.WeaponFacing;
        }
    }

    private static void WritePresentation(BinaryWriter writer, Frent.Entity entity)
    {
        ref var entityOwnership = ref entity.Get<OwnershipState>();
        ref var entityPresentation = ref entity.Get<PresentationState>();
        writer.Write(entityOwnership.Owner); writer.Write(entityPresentation.VisualStates);
        writer.Write(entityPresentation.BodyFacing.X); writer.Write(entityPresentation.BodyFacing.Y);
        writer.Write(entityPresentation.WeaponFacing.X); writer.Write(entityPresentation.WeaponFacing.Y);
        Write(entityPresentation.BodyAnimation); Write(entityPresentation.WeaponAnimation); writer.Write(entityPresentation.BodyAnimationOverride);
        writer.Write(entityPresentation.HasAim); writer.Write(entityPresentation.AimEntity.Value); writer.Write(entityPresentation.AimPoint.X); writer.Write(entityPresentation.AimPoint.Y);
        void Write(AnimationPlayback value) { writer.Write(value.Animation); writer.Write(value.StartTick); writer.Write(value.DurationTicks); }
    }
}
