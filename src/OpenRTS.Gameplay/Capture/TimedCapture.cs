using Frent;

namespace OpenRTS.Core.Capture;

internal sealed class TimedCapture : ICaptureMechanic
{
    private struct CapturableState { public long ChangedTick; }
    private struct CaptureState { public int Progress; public uint Owner; }

    public void Initialize(Entity entity, UnitDefinition type)
    {
        if (type.Capturable is not null) entity.Add(new CapturableState());
        if (type.Abilities.Any(a => a.Kind == AbilityKind.Capture)) entity.Add(new CaptureState());
    }

    public bool CanStart(CaptureParticipant actor, CaptureParticipant target) =>
        actor.Entity.Has<CaptureState>() && target.Entity.Has<CapturableState>();

    public CaptureStep Advance(CaptureAttempt attempt)
    {
        var actor = attempt.Actor;
        var target = attempt.Target;
        ref var state = ref actor.Entity.Get<CaptureState>();
        if (state.Owner != target.Owner) { state.Progress = 0; state.Owner = target.Owner; }
        int range = actor.Type.Abilities.First(a => a.Kind == AbilityKind.Capture).Range;
        if (attempt.DistanceSquared > (long)range * range)
        {
            state.Progress = 0;
            return CaptureStep.Approach;
        }
        if (target.Entity.Get<CapturableState>().ChangedTick == attempt.Tick)
        {
            state.Progress = 0;
            return CaptureStep.Engage;
        }
        return ++state.Progress >= Duration(target.Type) ? CaptureStep.Complete : CaptureStep.Engage;
    }

    public void Reset(Entity entity)
    {
        if (entity.Has<CaptureState>()) entity.Get<CaptureState>().Progress = 0;
    }

    public void OwnerChanged(Entity entity, long tick)
    {
        Reset(entity);
        if (entity.Has<CapturableState>()) entity.Get<CapturableState>().ChangedTick = tick;
    }

    public CaptureProgress GetProgress(Entity actor, UnitDefinition targetType) => actor.Has<CaptureState>()
        ? new(actor.Get<CaptureState>().Progress, Duration(targetType)) : default;

    private static int Duration(UnitDefinition type) => type.Capturable?.Ticks ?? type.MaxHealth;

    public void WriteEntityState(BinaryWriter writer, Entity entity)
    {
        writer.Write(entity.Has<CapturableState>());
        if (entity.Has<CapturableState>()) writer.Write(entity.Get<CapturableState>().ChangedTick);
        writer.Write(entity.Has<CaptureState>());
        if (entity.Has<CaptureState>())
        {
            var state = entity.Get<CaptureState>();
            writer.Write(state.Progress);
            writer.Write(state.Owner);
        }
    }
}
