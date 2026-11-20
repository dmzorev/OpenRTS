namespace OpenRTS.Network;

public enum MatchPhase : byte { Lobby, Playing, Paused, Finished }
public enum SlotController : byte { Human, Ai, None }
public sealed record MatchSlot(uint Player, SlotController Controller, bool Connected, bool Ready);

/// <summary>Immutable coordinator publication. Revision binds Ready to the exact lobby composition.</summary>
public sealed class MatchState
{
    public Guid Match { get; }
    public long Revision { get; }
    public long Tick { get; }
    public MatchPhase Phase { get; }
    public IReadOnlyList<MatchSlot> Slots { get; }
    public string Reason { get; }
    public int Spectators { get; }
    internal MatchState(Guid match, long revision, long tick, MatchPhase phase, IEnumerable<MatchSlot> slots, string reason, int spectators = 0)
    {
        Match = match; Revision = revision; Tick = tick; Phase = phase;
        Slots = Array.AsReadOnly(slots.ToArray()); Reason = reason; Spectators = spectators;
    }
}

public sealed record MatchChat(uint Player, string Text);

internal static class StarProtocol
{
    internal const int Version = 6;
    internal const byte PasswordChallenge = 42, PasswordProof = 43, SeatAssigned = 44;
    internal const uint AnySeat = uint.MaxValue;
    internal const byte Waiting = 39, Pong = 41;
    internal static byte[] Probe(byte kind, long nonce) => Wire.Pack(w => {
        if (kind is not (Ping or Pong) || nonce < 1) throw new InvalidDataException("Invalid latency probe.");
        w.Write(kind); w.Write(nonce);
    });
    internal static long ReadProbe(byte[] bytes, byte kind) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != kind || kind is not (Ping or Pong)) throw new InvalidDataException("Invalid latency probe.");
        long nonce = r.ReadInt64(); if (nonce < 1) throw new InvalidDataException("Invalid latency nonce."); return nonce;
    });
    internal static byte[] WaitFrame(long tick, uint[] players, int milliseconds) => Wire.Pack(w => {
        if (tick < 1 || players.Length is < 1 or > 8 || milliseconds is < 0 or > 60000 ||
            players.Any(p => p is < 1 or > 8) || !players.SequenceEqual(players.Distinct().Order())) throw new InvalidDataException("Invalid wait report.");
        w.Write(Waiting); w.Write(tick); w.Write(milliseconds); w.Write((byte)players.Length);
        foreach (uint player in players) w.Write(player);
    });
    internal static (long Tick, uint[] Players, int Milliseconds) ReadWait(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != Waiting) throw new InvalidDataException("Expected wait report.");
        long tick = r.ReadInt64(); int milliseconds = r.ReadInt32(), count = r.ReadByte();
        if (count is < 1 or > 8) throw new InvalidDataException("Invalid wait count.");
        uint[] players = new uint[count]; for (int i = 0; i < count; i++) players[i] = r.ReadUInt32();
        _ = WaitFrame(tick, players, milliseconds); return (tick, players, milliseconds);
    });
    internal const byte AdmissionChallenge = 36, AdmissionProof = 37, Rejected = 38;
    internal const byte Credential = 32, Recovery = 33, RecoveryChunk = 34, RecoveryReady = 35;
    internal const byte Settings = 30, SettingsAck = 31;
    internal const byte State = 20, Hello = 21, Request = 22, Chat = 23, Turn = 24,
        Hash = 25, Finish = 26, Ack = 27, Complete = 28, Ping = 29;
    internal enum Operation : byte { Ready, Start, Pause, Leave, Ai, Stop }
    internal static byte[] WriteState(MatchState s) => Wire.Pack(w => {
        w.Write(State); w.Write(s.Match.ToByteArray()); w.Write(s.Revision); w.Write(s.Tick); w.Write((byte)s.Phase);
        w.Write((byte)s.Slots.Count);
        foreach (var p in s.Slots) { w.Write((byte)p.Controller); w.Write(p.Connected); w.Write(p.Ready); }
        w.Write((byte)s.Spectators); Wire.Text(w, s.Reason);
    });
    internal static MatchState ReadState(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != State) throw new InvalidDataException("Expected match state.");
        Guid id = new(r.ReadBytes(16)); long revision = r.ReadInt64(), tick = r.ReadInt64();
        var phase = (MatchPhase)r.ReadByte(); int count = r.ReadByte();
        if (id == Guid.Empty || revision < 1 || tick < 0 || count is < 2 or > 8 || !Enum.IsDefined(phase))
            throw new InvalidDataException("Invalid match state.");
        var slots = new MatchSlot[count];
        for (uint p = 1; p <= count; p++)
        {
            var controller = (SlotController)r.ReadByte(); byte connected = r.ReadByte(), ready = r.ReadByte();
            if (!Enum.IsDefined(controller) || connected > 1 || ready > 1 ||
                (controller != SlotController.Human && (connected != 0 || ready != 0)) || ready != 0 && connected == 0)
                throw new InvalidDataException("Invalid slot state.");
            slots[p - 1] = new(p, controller, connected != 0, ready != 0);
        }
        if (slots[0].Controller != SlotController.Human || !slots[0].Connected)
            throw new InvalidDataException("Missing coordinator.");
        int spectators = r.ReadByte(); if (spectators > 8) throw new InvalidDataException("Spectator limit.");
        return new MatchState(id, revision, tick, phase, slots, Wire.Text(r), spectators);
    });
    internal static byte[] RequestFrame(Operation op, long revision, uint player = 0, bool enabled = false) =>
        Wire.Pack(w => { w.Write(Request); w.Write((byte)op); w.Write(revision); w.Write(player); w.Write(enabled); });
    internal static (Operation Op, long Revision, uint Player, bool Enabled) ReadRequest(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != Request) throw new InvalidDataException("Expected request.");
        var op = (Operation)r.ReadByte(); long revision = r.ReadInt64(); uint player = r.ReadUInt32(); byte enabled = r.ReadByte();
        if (!Enum.IsDefined(op) || revision < 1 || enabled > 1 || op != Operation.Ai && player != 0 ||
            op is not (Operation.Ai or Operation.Pause) && enabled != 0) throw new InvalidDataException("Invalid request.");
        return (op, revision, player, enabled != 0);
    });
    internal static byte[] Check(byte kind, long tick, string hash) => Wire.Pack(w => { w.Write(kind); w.Write(tick); Wire.Text(w, hash); });
    internal static (long Tick, string Hash) ReadCheck(byte[] frame, byte kind) => Wire.Unpack(frame, r => {
        if (r.ReadByte() != kind) throw new InvalidDataException("Unexpected checkpoint.");
        long tick = r.ReadInt64(); string hash = Wire.Text(r);
        if (tick < 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid checkpoint.");
        return (tick, hash);
    });
    internal static byte[] TurnFrame(long tick, long revision) => Wire.Pack(w => { w.Write(Turn); w.Write(tick); w.Write(revision); });
    internal static long ReadTurn(byte[] bytes, long tick, MatchState state) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != Turn || r.ReadInt64() != tick || r.ReadInt64() != state.Revision || state.Phase != MatchPhase.Playing)
            throw new InvalidDataException("Turn/control order mismatch.");
        return tick;
    });
    internal static byte[] ChatFrame(uint player, string text) => Wire.Pack(w => {
        if (string.IsNullOrWhiteSpace(text) || System.Text.Encoding.UTF8.GetByteCount(text) > 256)
            throw new ArgumentException("Chat accepts 1..256 UTF-8 bytes.");
        w.Write(Chat); w.Write(player); Wire.Text(w, text);
    });
    internal static MatchChat ReadChat(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != Chat) throw new InvalidDataException("Expected chat.");
        uint player = r.ReadUInt32(); string text = Wire.Text(r);
        _ = ChatFrame(player, text); return new MatchChat(player, text);
    });
}
