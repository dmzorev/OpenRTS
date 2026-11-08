using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core.Capture;

internal sealed class TimedCapture : ICaptureMechanic
{
    private struct CapturableState
    {
        public long ChangedTick;
        public SortedDictionary<uint, SideProgress>? Sides;
    }

    private readonly record struct SideProgress(int Work, long LastContribution);
    private readonly List<Entity> _activeTargets = [];
    private readonly List<uint> _decayingSides = [];

    public void Initialize(Entity entity, UnitDefinition type)
    {
        if (type.Capturable is not null) entity.Add(new CapturableState());
    }

    public bool CanStart(CaptureParticipant actor, CaptureParticipant target) =>
        actor.Type.Abilities.Any(a => a.Kind == AbilityKind.Capture) && target.Entity.Has<CapturableState>();

    public CaptureStep Advance(CaptureAttempt attempt)
    {
        var actor = attempt.Actor;
        var target = attempt.Target;
        int range = actor.Type.Abilities.First(a => a.Kind == AbilityKind.Capture).Range;
        if (attempt.DistanceSquared > (long)range * range) return CaptureStep.Approach;

        ref var state = ref target.Entity.Get<CapturableState>();
        if (state.ChangedTick == attempt.Tick) return CaptureStep.Engage;
        if (state.Sides is null)
        {
            state.Sides = new();
            _activeTargets.Add(target.Entity);
        }
        int work = state.Sides.GetValueOrDefault(actor.Owner).Work + 1;
        state.Sides[actor.Owner] = new(work, attempt.Tick);
        return work >= Duration(target.Type) ? CaptureStep.Complete : CaptureStep.Engage;
    }

    public void EndOrders(long tick)
    {
        for (int i = _activeTargets.Count - 1; i >= 0; i--)
        {
            var entity = _activeTargets[i];
            if (entity.IsAlive)
            {
                ref var state = ref entity.Get<CapturableState>();
                var sides = state.Sides!;
                _decayingSides.Clear();
                foreach (var (owner, progress) in sides)
                    if (progress.LastContribution != tick) _decayingSides.Add(owner);
                foreach (uint owner in _decayingSides)
                {
                    var progress = sides[owner];
                    if (progress.Work == 1) sides.Remove(owner);
                    else sides[owner] = progress with { Work = progress.Work - 1 };
                }
                if (sides.Count != 0) continue;
                state.Sides = null;
            }
            _activeTargets[i] = _activeTargets[^1];
            _activeTargets.RemoveAt(_activeTargets.Count - 1);
        }
    }

    public void Reset(Entity entity) { }

    public void OwnerChanged(Entity entity, long tick)
    {
        if (!entity.Has<CapturableState>()) return;
        ref var state = ref entity.Get<CapturableState>();
        state.ChangedTick = tick;
        state.Sides?.Clear();
    }

    public CaptureProgress GetProgress(Entity actor, CaptureParticipant target)
    {
        if (!target.Entity.Has<CapturableState>()) return default;
        uint owner = actor.Get<OwnershipState>().Owner;
        var progress = target.Entity.Get<CapturableState>().Sides?.GetValueOrDefault(owner);
        return new(progress?.Work ?? 0, Duration(target.Type));
    }

    private static int Duration(UnitDefinition type) => type.Capturable?.Ticks ?? type.MaxHealth;

    public void WriteEntityState(BinaryWriter writer, Entity entity)
    {
        writer.Write(entity.Has<CapturableState>());
        if (entity.Has<CapturableState>())
        {
            var state = entity.Get<CapturableState>();
            writer.Write(state.ChangedTick);
            writer.Write(state.Sides?.Count ?? 0);
            if (state.Sides is not null)
                foreach (var (owner, progress) in state.Sides)
                {
                    writer.Write(owner);
                    writer.Write(progress.Work);
                }
        }
        writer.Write(false);
    }
}
