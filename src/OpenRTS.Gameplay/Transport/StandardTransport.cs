using System.Runtime.InteropServices;
using Frent;

namespace OpenRTS.Core.Transport;

internal sealed class StandardTransport : ITransportMechanic
{
    private struct PassengerState { public EntityId Carrier; }

    private sealed class TransportState
    {
        public readonly List<EntityId> Occupants = [];
        public int UsedSlots;
        public bool ClaimedNeutral;
    }

    public void Initialize(Entity entity, UnitDefinition type)
    {
        if (type.Passenger is not null) entity.Add(new PassengerState());
        if (type.Transport is not null) entity.Add(new TransportState());
    }

    public EntityId CarrierOf(Entity passenger) => passenger.Has<PassengerState>() ? passenger.Get<PassengerState>().Carrier : default;

    public ReadOnlySpan<EntityId> Passengers(Entity carrier) => carrier.Has<TransportState>()
        ? CollectionsMarshal.AsSpan(carrier.Get<TransportState>().Occupants) : [];

    public bool Accepts(TransportParticipant passenger, TransportParticipant carrier)
    {
        var seat = passenger.Type.Passenger!;
        var cargo = carrier.Transport!;
        return seat.Slots <= cargo.Slots && cargo.Accepts.Contains(seat.Type);
    }

    public int BoardingRange(TransportParticipant passenger, TransportParticipant carrier) => 512;

    public bool HasRoom(TransportParticipant passenger, TransportParticipant carrier) =>
        carrier.Entity.Get<TransportState>().UsedSlots + passenger.Type.Passenger!.Slots <= carrier.Transport!.Slots;

    public void Board(TransportParticipant passenger, TransportParticipant carrier, bool claimedNeutral)
    {
        var cargo = carrier.Entity.Get<TransportState>();
        cargo.ClaimedNeutral |= claimedNeutral;
        passenger.Entity.Get<PassengerState>().Carrier = carrier.Id;
        cargo.Occupants.Add(passenger.Id);
        cargo.UsedSlots += passenger.Type.Passenger!.Slots;
    }

    public bool Detach(TransportParticipant passenger, TransportParticipant carrier)
    {
        passenger.Entity.Get<PassengerState>().Carrier = default;
        var cargo = carrier.Entity.Get<TransportState>();
        cargo.Occupants.Remove(passenger.Id);
        cargo.UsedSlots -= passenger.Type.Passenger!.Slots;
        if (cargo.Occupants.Count != 0 || !cargo.ClaimedNeutral) return false;
        cargo.ClaimedNeutral = false;
        return true;
    }

    public void SuppressNeutralReturn(Entity carrier) => carrier.Get<TransportState>().ClaimedNeutral = false;

    public void WriteEntityState(BinaryWriter writer, Entity entity)
    {
        if (!entity.Has<TransportState>()) return;
        var cargo = entity.Get<TransportState>();
        writer.Write(cargo.ClaimedNeutral);
        writer.Write(cargo.Occupants.Count);
        foreach (var id in cargo.Occupants) writer.Write(id.Value);
    }
}
