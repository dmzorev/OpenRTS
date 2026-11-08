using Frent;

namespace OpenRTS.Core.Transport;

public readonly record struct TransportParticipant(Entity Entity, EntityId Id, UnitDefinition Type, uint Owner, TransportDefinition? EffectiveTransport = null)
{
    public TransportDefinition? Transport => EffectiveTransport ?? Type.Transport;
}

/// <summary>World-local boarding policy and cargo storage. The host owns movement, placement, and ownership.</summary>
public interface ITransportMechanic
{
    // Only Initialize may change ECS structure. It initializes empty cargo and no carrier for new entities.
    void Initialize(Entity entity, UnitDefinition type);
    EntityId CarrierOf(Entity passenger);
    // Consume the ordered view before the next mutation of cargo.
    ReadOnlySpan<EntityId> Passengers(Entity carrier);
    bool Accepts(TransportParticipant passenger, TransportParticipant carrier);
    int BoardingRange(TransportParticipant passenger, TransportParticipant carrier);
    bool HasRoom(TransportParticipant passenger, TransportParticipant carrier);
    // After successful admission, record the passenger in both directions exactly once. Owner snapshots are from before the claim.
    void Board(TransportParticipant passenger, TransportParticipant carrier, bool claimedNeutral);
    // Remove both directions and return whether the empty carrier should return to neutral.
    bool Detach(TransportParticipant passenger, TransportParticipant carrier);
    // Clear neutral-return state before the host removes the entire cargo of a destroyed or disabled carrier.
    void SuppressNeutralReturn(Entity carrier);
    // The host writes the carrier ID. Write additional canonical, self-delimiting state here.
    void WriteEntityState(BinaryWriter writer, Entity entity);
    void WriteState(BinaryWriter writer) { }
}
