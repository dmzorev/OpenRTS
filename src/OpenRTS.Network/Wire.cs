using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

// Direct protocol uses one bounded batch frame; control frames have a separate smaller limit.
internal static class Wire
{
    internal const int MaximumFrame = 256 * 1024;
    internal static readonly CommandKind[] Kinds = [CommandKind.MoveSelected, CommandKind.MoveGroup,
        CommandKind.AttackMove, CommandKind.AttackTarget, CommandKind.Stop, CommandKind.HoldPosition,
        CommandKind.Move, CommandKind.RepairTarget, CommandKind.CaptureTarget, CommandKind.UseAbility,
        CommandKind.SetAutoCast, CommandKind.EnqueueTask, CommandKind.CancelTasks, CommandKind.PlaceProduced,
        CommandKind.SetRally, CommandKind.Enter, CommandKind.Unload, CommandKind.UnloadAt,
        CommandKind.PlanConstruction, CommandKind.BuildSite, CommandKind.CancelConstruction,
        CommandKind.StartUpgrade, CommandKind.CancelUpgrade, CommandKind.Transform, CommandKind.Land,
        CommandKind.ReturnToBase, CommandKind.AttackContact, CommandKind.Aim, CommandKind.ClearAim];
    internal static byte[] Pack(Action<BinaryWriter> write)
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s, Encoding.UTF8, true);
        write(w);
        if (s.Length > MaximumFrame) throw new InvalidDataException("Frame limit exceeded.");
        return s.ToArray();
    }
    internal static T Unpack<T>(byte[] bytes, Func<BinaryReader,T> read)
    {
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Frame limit exceeded.");
        using var s = new MemoryStream(bytes, false);
        using var r = new BinaryReader(s, Encoding.UTF8);
        var value = read(r);
        if (s.Position != s.Length) throw new InvalidDataException("Trailing bytes.");
        return value;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void Text(BinaryWriter w, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        if (b.Length > 1024) throw new InvalidDataException("Text limit exceeded.");
        w.Write((ushort)b.Length); w.Write(b);
    }
    internal static string Text(BinaryReader r)
    {
        int n = r.ReadUInt16();
        if (n > 1024) throw new InvalidDataException("Text limit exceeded.");
        var b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(b);
    }
    internal static byte[] Batch(Guid match, uint player, long tick, IReadOnlyList<WorldCommand> commands)
        => Pack(w => {
            w.Write((byte)3); w.Write(match.ToByteArray()); w.Write(player); w.Write(tick);
            if (player is < 1 or > 8 || tick <= 0) throw new InvalidDataException("Invalid batch identity.");
            if (commands.Count > 256) throw new InvalidDataException("Command count limit.");
            w.Write(commands.Count);
            int recipients = 0;
            foreach (var c in commands)
            {
                int kind = Array.IndexOf(Kinds, c.Kind);
                // Reject unsupported fields rather than silently changing an input's meaning.
                var supported = new WorldCommand(c.Tick, c.Issuer, c.Sequence, c.Kind, c.Entity, c.Position,
                    Owner: c.Owner, TargetEntity: c.TargetEntity, Recipients: c.Recipients, Queued: c.Queued,
                    Action: c.Action, Queue: c.Queue, Amount: c.Amount, Repeat: c.Repeat, Enabled: c.Enabled, TaskId: c.TaskId)
                    { QuarterTurns = c.QuarterTurns, TransformHere = c.TransformHere, ContactId = c.ContactId };
                if (c.Owner != 0 && (c.Owner != player || c.Kind is not (CommandKind.CancelConstruction or
                    CommandKind.CancelUpgrade or CommandKind.StartUpgrade or CommandKind.AttackContact)))
                    throw new InvalidDataException("Invalid player field.");
                if (kind < 0 || c != supported || c.Tick != tick || c.Issuer != player || c.Sequence <= 0)
                    throw new InvalidDataException("Unsupported player intent or stamp.");
                w.Write((byte)(kind + 1)); w.Write(c.Sequence); w.Write(c.Entity.Value);
                w.Write(c.Position.X); w.Write(c.Position.Y); w.Write(c.Position.Elevation);
                w.Write(c.TargetEntity.Value); w.Write(c.Queued);
                int count = c.Recipients?.Count ?? 0; recipients += count;
                if (count > 16384 || recipients > 32768) throw new InvalidDataException("Recipient limit.");
                w.Write(count);
                if (c.Recipients is { } set) foreach (var id in set.Ids) w.Write(id.Value);
                w.Write(c.Owner); OptionalText(w, c.Action); OptionalText(w, c.Queue);
                w.Write(c.Amount); w.Write(c.Repeat); w.Write(c.Enabled); w.Write(c.TaskId);
                w.Write(c.QuarterTurns); w.Write(c.TransformHere); w.Write(c.ContactId);
            }
        });
    internal static (Guid Match, uint Player, long Tick, WorldCommand[] Commands) ReadBatch(byte[] bytes)
        => Unpack(bytes, r => {
            if (r.ReadByte() != 3) throw new InvalidDataException("Expected batch.");
            var match = new Guid(r.ReadBytes(16)); uint player = r.ReadUInt32(); long tick = r.ReadInt64();
            if (player is < 1 or > 8 || tick <= 0) throw new InvalidDataException("Invalid batch identity.");
            int count = r.ReadInt32(), total = 0;
            if (count is < 0 or > 256) throw new InvalidDataException("Command count limit.");
            var commands = new WorldCommand[count];
            for (int i = 0; i < count; i++)
            {
                int kind = r.ReadByte() - 1;
                if ((uint)kind >= Kinds.Length) throw new InvalidDataException("Unknown intent.");
                long sequence = r.ReadInt64(); var entity = new EntityId(r.ReadUInt32());
                var p = new WorldPosition(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                var target = new EntityId(r.ReadUInt32()); byte queued = r.ReadByte(); int n = r.ReadInt32();
                if (sequence <= 0 || queued > 1 || n is < 0 or > 16384 || (total += n) > 32768)
                    throw new InvalidDataException("Invalid command bounds.");
                var ids = new EntityId[n]; uint previous = 0;
                for (int j = 0; j < n; j++)
                {
                    uint id = r.ReadUInt32();
                    if (id <= previous) throw new InvalidDataException("Noncanonical recipients.");
                    ids[j] = new(id); previous = id;
                }
                uint owner = r.ReadUInt32(); string? action = OptionalText(r), queue = OptionalText(r);
                int amount = r.ReadInt32(); bool repeat = Flag(r), enabled = Flag(r); long task = r.ReadInt64();
                int turns = r.ReadInt32(); bool here = Flag(r); long contact = r.ReadInt64();
                commands[i] = new(tick, player, sequence, Kinds[kind], entity, p, Owner: owner,
                    TargetEntity: target, Recipients: n == 0 ? null : new EntitySet(ids), Queued: queued == 1,
                    Action: action, Queue: queue, Amount: amount, Repeat: repeat, Enabled: enabled, TaskId: task)
                    { QuarterTurns = turns, TransformHere = here, ContactId = contact };
                // The encoder is also the canonical field-policy check for received commands.
                _ = Batch(match, player, tick, [commands[i]]);
            }
            return (match, player, tick, commands);
        });
    private static bool Flag(BinaryReader r) => r.ReadByte() switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid boolean.") };
    private static void OptionalText(BinaryWriter w, string? text)
    {
        w.Write(text is not null);
        if (text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > 128) throw new InvalidDataException("Action/queue limit.");
        Text(w, text);
    }
    private static string? OptionalText(BinaryReader r)
    {
        if (!Flag(r)) return null;
        var text = Text(r);
        if (Encoding.UTF8.GetByteCount(text) > 128) throw new InvalidDataException("Action/queue limit.");
        return text;
    }
    internal static async Task Send(Stream s, byte[] payload, CancellationToken ct)
    {
        if (payload.Length is < 1 or > MaximumFrame) throw new InvalidDataException("Frame limit.");
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
        await s.WriteAsync(size, ct); await s.WriteAsync(payload, ct); await s.FlushAsync(ct);
    }
    internal static async Task<byte[]> Receive(Stream s, CancellationToken ct, int maxLength = MaximumFrame)
    {
        byte[] size = new byte[4]; await s.ReadExactlyAsync(size, ct);
        int n = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (n < 1 || n > maxLength) throw new InvalidDataException("Frame limit.");
        var data = new byte[n]; await s.ReadExactlyAsync(data, ct); return data;
    }
    internal static byte[] Exchange(Stream s, byte[] payload, TimeSpan timeout, CancellationToken stop,
        Func<byte[], bool>? accept = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(timeout);
        async Task<byte[]> ReadAccepted()
        {
            for (int i = 0; i < 64; i++)
            {
                var bytes = await Receive(s, deadline.Token);
                if (accept is null || accept(bytes)) return bytes;
            }
            throw new InvalidDataException("Excess duplicate frames.");
        }
        var send = Send(s, payload, deadline.Token);
        var receive = ReadAccepted();
        try { Task.WhenAll(send, receive).GetAwaiter().GetResult(); return receive.Result; }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            deadline.Cancel(); s.Dispose(); throw new TimeoutException("NET1 exchange deadline exceeded.");
        }
        catch { deadline.Cancel(); s.Dispose(); throw; }
    }
}
