using Frent;

namespace OpenRTS.Core.Capture;

/// <summary>Live actor and read-only game facts. Native handles are for the mechanic's own components on the world owner thread.</summary>
public readonly record struct CaptureParticipant(Entity Entity, UnitDefinition Type, uint Owner, int Health);

public readonly record struct CaptureAttempt(CaptureParticipant Actor, CaptureParticipant Target, long Tick, long DistanceSquared);
public readonly record struct CaptureProgress(int Progress, int Duration);
public readonly record struct CaptureTargetProgress(uint Owner, int Progress, int Duration);
public enum CaptureStep { Approach, Engage, Complete }

/// <summary>A world-local capture implementation. The host owns movement, the order queue and ownership changes.</summary>
public interface ICaptureMechanic
{
    // Initialize may add its own components. During orders and ownership changes only modify existing components.
    void Initialize(Entity entity, UnitDefinition type);
    // This check can run before queueing, on activation, and again while an order is running.
    bool CanStart(CaptureParticipant actor, CaptureParticipant target);
    CaptureStep Advance(CaptureAttempt attempt);
    void Reset(Entity entity);
    void OwnerChanged(Entity entity, long tick);
    void EndOrders(long tick) { }
    CaptureProgress GetProgress(Entity actor, UnitDefinition targetType) => default;
    CaptureProgress GetProgress(Entity actor, CaptureParticipant target) => GetProgress(actor, target.Type);
    CaptureTargetProgress GetTargetProgress(Entity target, UnitDefinition type) => default;
    // Report callbacks must only read state and write canonical, self-delimiting data.
    void WriteEntityState(BinaryWriter writer, Entity entity);
    void WriteState(BinaryWriter writer) { }
}
