using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    private sealed class Peer(TcpClient socket, PeerPipeline pipeline, TickInbox inbox) : IDisposable
    {
        internal readonly TcpClient Socket = socket;
        internal readonly PeerPipeline Pipe = pipeline;
        internal TickInbox Inbox = inbox;
        internal string? SettingsDigest;
        internal MatchResumeCredential? ResumeWith;
        internal MatchAdmissionReceipt? Admission;
        internal long Ack, LastSeen = Stopwatch.GetTimestamp(), RateAt = Stopwatch.GetTimestamp();
        internal int Requests, Chats;
        internal readonly LatencyProbe Probe = new();
        internal double? RoundTrip;
        internal void Limit(bool chat)
        {
            if (Stopwatch.GetElapsedTime(RateAt).TotalSeconds >= 1) { RateAt = Stopwatch.GetTimestamp(); Requests = Chats = 0; }
            if (chat ? ++Chats > 4 : ++Requests > 16) throw new InvalidDataException("Peer control/chat rate exceeded.");
        }
        public void Dispose() { Pipe.Dispose(); Socket.Dispose(); }
    }
    private SimulationReport RunHost(string address, int port, X509Certificate2 certificate, SimulationWorld world)
    {
        var listener = new TcpListener(IPAddress.Parse(address), port); listener.Start();
        var peers = new SortedDictionary<uint, Peer>();
        var lost = new HashSet<uint>();
        byte[]? recoverySnapshot = null; long recoverySnapshotTick = -1;
        var credentials = new Dictionary<uint, MatchResumeCredential>();
        var unconfirmedCredentials = new Dictionary<uint, MatchResumeCredential>();
        var recovering = new Dictionary<uint, (Peer Peer, Task Transfer, long Prefix, long Started)>();
        using var acceptStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var handshakes = new List<Task<(TcpClient Socket, PeerPipeline Pipe, HelloData Hello)>>();
        int count = State.Slots.Count, window = 2 * Descriptor.InputDelay + Descriptor.HashInterval;
        var slots = State.Slots.ToArray(); var phase = MatchPhase.Lobby;
        var pending = Enumerable.Range(0, count).Select(_ => new Dictionary<long, WorldCommand[]>()).ToArray();
        long[] sequences = new long[count];
        var hashes = new Dictionary<long, string>();
        StarJournal? log = null; FileStream? output = null;
        var clock = Stopwatch.StartNew(); long prepared = -1; double nextDue = 0, pingAt = 0, blockedAt = 0;
        long revision = State.Revision;
        MatchState Snapshot(string reason) => new(State.Match, revision, world.Tick, phase, slots, reason, peers.Keys.Count(p => p > 8));
        void Send(uint player, Peer peer, byte[] frame)
        {
            try { if (player > 8) { if (!peer.Pipe.TrySend(frame)) lost.Add(player); } else peer.Pipe.Send(frame); }
            catch (Exception e) when (PeerFailure(e)) { lost.Add(player); }
        }
        void Broadcast(byte[] frame) { foreach (var pair in peers) Send(pair.Key, pair.Value, frame); }
        void PublishLatencies() => Volatile.Write(ref _latencies, Array.AsReadOnly(peers.Where(p => p.Value.RoundTrip.HasValue)
            .Select(p => new NetworkPeerLatency(p.Key, p.Value.RoundTrip!.Value)).ToArray()));
        void Change(string reason, bool resetReady = false, bool increment = true)
        {
            if (increment) revision++;
            if (resetReady) for (int i = 0; i < count; i++) slots[i] = slots[i] with { Ready = false };
            var state = Snapshot(reason); SetState(state);
            var frame = StarProtocol.WriteState(state); log?.Write(frame); Broadcast(frame);
        }
        void WaitFinal(Peer peer)
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                byte[] reply = peer.Pipe.Read(_timeout - deadline.Elapsed);
                if (reply[0] == StarProtocol.Ack)
                {
                    var check = StarProtocol.ReadCheck(reply, StarProtocol.Ack);
                    if (check.Tick != world.Tick || check.Hash != world.Report().StateHash)
                        throw new DesyncException(world.Tick, world.Report().StateHash, check.Hash);
                    break;
                }
                if (reply[0] is not (3 or StarProtocol.Hash or StarProtocol.Ping or StarProtocol.Pong or StarProtocol.Request or StarProtocol.Chat))
                    throw new InvalidDataException("Unexpected departing peer message.");
            }
        }
        void FinishPeer(uint player, Peer peer)
        {
            peer.Pipe.Send(StarProtocol.Check(StarProtocol.Finish, world.Tick, world.Report().StateHash));
            if (player <= 8) WaitFinal(peer); peer.Pipe.Send([StarProtocol.Complete]); peer.Pipe.Flush(player > 8 ? TimeSpan.FromMilliseconds(100) : null);
        }
        void Drop(uint player, string reason, bool graceful)
        {
            if (!peers.TryGetValue(player, out var peer)) return;
            if (player > 8)
            {
                peers.Remove(player); lost.Remove(player); PublishLatencies(); Change("Зритель отключился");
                try { if (graceful && log is not null) FinishPeer(player, peer); }
                catch (Exception e) when (PeerFailure(e)) { }
                finally { peer.Dispose(); }
                return;
            }
            slots[player - 1] = slots[player - 1] with { Connected = false, Ready = false,
                Controller = phase == MatchPhase.Lobby ? SlotController.Human : SlotController.None };
            if (phase != MatchPhase.Lobby) phase = MatchPhase.Paused;
            Change($"Игрок {player}: {reason}", resetReady: phase == MatchPhase.Lobby);
            peers.Remove(player); lost.Remove(player); PublishLatencies(); pending[player - 1].Clear();
            try { if (graceful && log is not null) FinishPeer(player, peer); }
            catch (Exception e) when (PeerFailure(e)) { /* The surviving peers retain the same committed prefix. */ }
            finally { peer.Dispose(); }
        }
        void Process(uint player, byte[] bytes)
        {
            if (bytes[0] == StarProtocol.Chat)
            {
                if (player != 1) peers[player].Limit(true);
                var message = StarProtocol.ReadChat(bytes);
                if (message.Player != (player > 8 ? 0 : player)) throw new InvalidDataException("Chat sender spoof.");
                Chat(message); Broadcast(bytes); return;
            }
            if (bytes[0] == 3 && player is > 1 and <= 8 && phase != MatchPhase.Lobby)
            {
                peers[player].Inbox.Add(bytes, world.Tick); return;
            }
            if (bytes[0] == StarProtocol.Hash && player is > 1 and <= 8 && phase != MatchPhase.Lobby)
            {
                var check = StarProtocol.ReadCheck(bytes, StarProtocol.Hash); var peer = peers[player];
                if (check.Tick != peer.Ack + Descriptor.HashInterval || !hashes.TryGetValue(check.Tick, out var expected))
                    throw new InvalidDataException("Peer checkpoint cadence/window mismatch.");
                if (check.Hash != expected) throw new DesyncException(check.Tick, expected, check.Hash);
                peer.Ack = check.Tick; unconfirmedCredentials.Remove(player); return;
            }
            if (bytes[0] == StarProtocol.Ping && player != 1)
            { Send(player, peers[player], StarProtocol.Probe(StarProtocol.Pong, StarProtocol.ReadProbe(bytes, StarProtocol.Ping))); return; }
            if (bytes[0] == StarProtocol.Pong && player != 1)
            {
                if (peers[player].Probe.Receive(StarProtocol.ReadProbe(bytes, StarProtocol.Pong)) is { } roundTrip)
                { peers[player].RoundTrip = roundTrip; PublishLatencies(); }
                return;
            }
            if (bytes[0] == StarProtocol.SettingsAck && player != 1 && phase == MatchPhase.Lobby && _prepare is not null)
            {
                var acknowledgement = Wire.Unpack(bytes, r => { r.ReadByte(); return (Digest: Wire.Text(r), Hash: Wire.Text(r)); });
                if (acknowledgement.Digest != SettingsDigest) return;
                if (acknowledgement.Hash != _initialHash) throw new InvalidDataException("Lobby world acknowledgement mismatch.");
                peers[player].SettingsDigest = SettingsDigest; return;
            }
            var request = StarProtocol.ReadRequest(bytes);
            if (player != 1) peers[player].Limit(false);
            if (request.Op == StarProtocol.Operation.Leave)
            {
                if (player == 1) { if (phase == MatchPhase.Lobby) throw new OperationCanceledException(_stop.Token); phase = MatchPhase.Finished; Change("Хост завершил матч"); }
                else Drop(player, "покинул матч", true);
                return;
            }
            if (player > 8) throw new InvalidDataException("Spectator control request.");
            if (request.Op is StarProtocol.Operation.Start or StarProtocol.Operation.Ai or StarProtocol.Operation.Stop && player != 1)
                throw new InvalidDataException("Coordinator operation from a guest.");
            if (request.Revision != revision) return; // A stale Ready/config/pause cannot authorize the current revision.
            switch (request.Op)
            {
                case StarProtocol.Operation.Ready when phase == MatchPhase.Lobby:
                    if (_prepare is not null && player != 1 && peers[player].SettingsDigest != SettingsDigest) break;
                    slots[player - 1] = slots[player - 1] with { Ready = true }; Change("Готовность", increment: false); break;
                case StarProtocol.Operation.Start when phase == MatchPhase.Lobby:
                    if (slots.Any(p => p.Controller == SlotController.Human && (!p.Connected || !p.Ready))) break;
                    phase = MatchPhase.Playing; revision++;
                    var initial = Snapshot("Матч начат"); SetState(initial);
                    output = new FileStream(JournalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read); log = new StarJournal(output, Descriptor, world.Report().StateHash, StarProtocol.WriteState(initial), LobbySettings);
                    Broadcast(StarProtocol.WriteState(initial)); Publish(world); nextDue = clock.Elapsed.TotalMilliseconds + 1000.0 / Descriptor.TickRate;
                    break;
                case StarProtocol.Operation.Pause when recovering.Count == 0 && phase is (MatchPhase.Playing or MatchPhase.Paused):
                    var desired = request.Enabled ? MatchPhase.Paused : MatchPhase.Playing;
                    if (phase != desired) { phase = desired; Change($"Игрок {player}"); nextDue = clock.Elapsed.TotalMilliseconds + 1000.0 / Descriptor.TickRate; }
                    break;
                case StarProtocol.Operation.Ai:
                    uint target = request.Player;
                    if (target < 2 || target > count || request.Enabled && _planAi is null) throw new InvalidDataException("Invalid AI assignment.");
                    if (recovering.Count != 0 || slots[target - 1].Connected || phase is MatchPhase.Playing or MatchPhase.Finished) break;
                    slots[target - 1] = slots[target - 1] with { Controller = request.Enabled ? SlotController.Ai : phase == MatchPhase.Lobby ? SlotController.Human : SlotController.None };
                    pending[target - 1].Clear(); prepared = -1;
                    Change($"Управление игроком {target}", resetReady: phase == MatchPhase.Lobby); break;
                case StarProtocol.Operation.Stop when phase != MatchPhase.Lobby:
                    phase = MatchPhase.Finished; Change("Хост завершил матч"); break;
            }
        }
        void Poll()
        {
            if (clock.Elapsed.TotalMilliseconds >= pingAt)
            {
                foreach (var pair in peers)
                    if (!pair.Value.Probe.Pending) Send(pair.Key, pair.Value, StarProtocol.Probe(StarProtocol.Ping, pair.Value.Probe.Start()));
                pingAt = clock.Elapsed.TotalMilliseconds + Math.Min(1000, _timeout.TotalMilliseconds / 3);
            }
            foreach (var pair in peers.ToArray())
            {
                try
                {
                    for (int i = 0; i < PeerPipeline.Capacity && pair.Value.Pipe.TryRead(out var frame); i++)
                    {
                        pair.Value.LastSeen = Stopwatch.GetTimestamp(); Process(pair.Key, frame);
                        if (!peers.ContainsKey(pair.Key)) break;
                    }
                    if (peers.ContainsKey(pair.Key))
                    {
                        pair.Value.Pipe.Check();
                        if (Stopwatch.GetElapsedTime(pair.Value.LastSeen) > _timeout) lost.Add(pair.Key);
                    }
                }
                catch (Exception e) when (PeerFailure(e)) { lost.Add(pair.Key); }
            }
            foreach (uint player in lost.ToArray()) { lost.Remove(player); Drop(player, "соединение потеряно", false); }
            while (_configuration.TryDequeue(out var configuration))
            {
                if (phase != MatchPhase.Lobby) continue;
                if (configuration.Slots.Where((controller, i) => slots[i].Connected && controller != SlotController.Human).Any())
                { Volatile.Write(ref _status, "Сначала освободите занятый слот."); continue; }
                world = PrepareSettings(configuration.Data);
                window = 2 * Descriptor.InputDelay + Descriptor.HashInterval;
                for (int i = 0; i < count; i++) slots[i] = slots[i] with { Controller = configuration.Slots[i] };
                foreach (var pair in peers)
                {
                    pair.Value.SettingsDigest = null;
                    pair.Value.Inbox = new TickInbox(State.Match, pair.Key, Descriptor.InputDelay, window);
                }
                Broadcast(SettingsFrame()); Change("Хост изменил настройки", resetReady: true);
            }
            int requests = Interlocked.CompareExchange(ref _requestCount, 0, 0);
            for (int i = 0; i < requests && _requests.TryDequeue(out var frame); i++)
            { Interlocked.Decrement(ref _requestCount); Process(1, frame); }
        }
        byte[] History(long offset = 0)
        {
            output!.Flush(); long end = output.Position;
            if (end - offset > 64 * 1024 * 1024) throw new InvalidDataException("Recovery history quota exceeded.");
            byte[] bytes = new byte[checked((int)(end - offset))];
            output.Position = offset; output.ReadExactly(bytes); output.Position = end; return bytes;
        }
        void Recoveries()
        {
            foreach (var (player, recovery) in recovering.ToArray())
            {
                try
                {
                    if (Stopwatch.GetElapsedTime(recovery.Started) > TimeSpan.FromSeconds(30)) throw new TimeoutException("Recovery admission deadline exceeded.");
                    if (!recovery.Transfer.IsCompleted) continue;
                    recovery.Transfer.GetAwaiter().GetResult();
                    recovery.Peer.Pipe.Check();
                    if (!recovery.Peer.Pipe.TryRead(out var ready)) continue;
                    if (ready[0] == StarProtocol.SettingsAck && _prepare is not null)
                    {
                        var ack = Wire.Unpack(ready, r => { r.ReadByte(); return (Digest: Wire.Text(r), Hash: Wire.Text(r)); });
                        if (ack.Digest != SettingsDigest || ack.Hash != _initialHash) throw new InvalidDataException("Recovery recipe acknowledgement mismatch.");
                        continue;
                    }
                    var check = StarProtocol.ReadCheck(ready, StarProtocol.RecoveryReady);
                    if (check.Tick != world.Tick || check.Hash != world.Report().StateHash) throw new InvalidDataException("Recovery boundary hash mismatch.");
                    // Catch up controls committed during transfer before admitting the new connection.
                    using (var tail = new MemoryStream(History(recovery.Prefix)))
                    using (var reader = new BinaryReader(tail))
                        while (tail.Position < tail.Length) { int n = reader.ReadInt32(); recovery.Peer.Pipe.Send(reader.ReadBytes(n)); }
                    if (player <= 8)
                    {
                        var credential = NewCredential(State.Match, player, checked(credentials[player].Epoch + 1));
                        unconfirmedCredentials[player] = recovery.Peer.ResumeWith ?? credentials[player];
                        credentials[player] = credential; recovery.Peer.Pipe.Send(CredentialFrame(credential));
                        slots[player - 1] = slots[player - 1] with { Controller = SlotController.Human, Connected = true, Ready = true };
                        pending[player - 1].Clear();
                        recovery.Peer.Ack = world.Tick - world.Tick % Descriptor.HashInterval;
                        for (long t = world.Tick + 1; t < world.Tick + Descriptor.InputDelay; t++)
                            recovery.Peer.Inbox.Add(Wire.Batch(State.Match, player, t, []), world.Tick);
                    }
                    peers.Add(player, recovery.Peer); pingAt = 0; recovering.Remove(player); Admitted(recovery.Peer);
                    recovery.Peer.LastSeen = Stopwatch.GetTimestamp(); prepared = -1;
                    Change(player <= 8 ? $"Игрок {player} вернулся · новая эпоха соединения" : "Зритель подключён к текущему матчу");
                }
                catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or OperationCanceledException or SocketException)
                { recovering.Remove(player); recovery.Peer.Dispose(); Volatile.Write(ref _status, "Возврат отклонён: " + e.Message); }
            }
        }
        void Accept()
        {
            if (listener.Pending() && handshakes.Count < 8)
            {
                TcpClient socket = listener.AcceptTcpClient();
                handshakes.Add(Task.Run(async () => {
                    SslStream? tls = null; PeerPipeline? pipe = null;
                    try
                    {
                        socket.NoDelay = true; tls = new SslStream(socket.GetStream());
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(acceptStop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        await Authenticate(tls, certificate, deadline.Token);
                        pipe = new PeerPipeline(tls, _timeout, acceptStop.Token);
                        var hello = ReadHello(pipe.Read(TimeSpan.FromSeconds(5)));
                        ProveAdmission(hello, pipe);
                        CheckPassword(hello, pipe);
                        return (socket, pipe, hello);
                    }
                    catch (Exception error) { RejectPeer(pipe, error is InvalidDataException ? error.Message : "TLS handshake не завершён."); pipe?.Dispose(); tls?.Dispose(); socket.Dispose(); throw; }
                }));
            }
            foreach (var task in handshakes.Where(t => t.IsCompleted).ToArray())
            {
                handshakes.Remove(task); TcpClient? socket = null; PeerPipeline? pipe = null;
                try
                {
                    (socket, pipe, var hello) = task.GetAwaiter().GetResult();
                    uint p = hello.Player; int n = hello.Count;
                    string digest = hello.Digest, hash = hello.Hash, rules = hello.Rules, content = hello.Content;
                    var credential = hello.Resume;
                    bool assignSeat = p == StarProtocol.AnySeat;
                    if (assignSeat)
                    {
                        if (_admissionGate is not null || credential is not null || phase != MatchPhase.Lobby) throw new InvalidDataException("Приём игроков закрыт.");
                        p = slots.Where(s => s.Player > 1 && s.Controller == SlotController.Human && !s.Connected).Select(s => s.Player).FirstOrDefault();
                        if (p == 0) throw new InvalidDataException("Свободных мест больше нет.");
                    }
                    if (phase != MatchPhase.Lobby && recovering.Count >= 2 || credential is not null && credential.Player != p || n != count || rules != Descriptor.Rules || content != Descriptor.ContentHash ||
                            p != 0 && (p < 2 || p > count || slots[p - 1].Connected || recovering.ContainsKey(p)) ||
                            phase != MatchPhase.Lobby && p == 0 && phase != MatchPhase.Paused ||
                            p == 0 && peers.Keys.Concat(recovering.Keys).Count(k => k > 8) >= 8 ||
                            phase == MatchPhase.Lobby && (credential is not null || p != 0 && slots[p - 1].Controller != SlotController.Human) ||
                            phase != MatchPhase.Lobby && p != 0 && (credential is null || !SameCredential(credential, credentials.GetValueOrDefault(p)) && !SameCredential(credential, unconfirmedCredentials.GetValueOrDefault(p))) ||
                            _prepare is null && (digest != Wire.Hash(DirectMatch.Descriptor(Descriptor)) || hash != _initialHash))
                            throw new InvalidDataException("Slot, resume credential, descriptor or initial world mismatch.");
                    uint assigned = p == 0 ? Enumerable.Range(9, 8).Select(i => (uint)i).First(i => !peers.ContainsKey(i) && !recovering.ContainsKey(i)) : p;
                    uint player = assigned;
                    var peer = new Peer(socket, pipe, new TickInbox(State.Match, player, Descriptor.InputDelay, window)) { ResumeWith = credential };
                    if (hello.Ticket is not null) peer.Admission = _admissionGate!.Redeem(hello.Ticket, State.Match, p, Fingerprint, Descriptor.Rules, Descriptor.ContentHash, count);
                    if (assignSeat) peer.Pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.SeatAssigned); w.Write(player); }));
                    if (_prepare is not null) peer.Pipe.Send(SettingsFrame());
                    if (phase == MatchPhase.Lobby)
                    {
                        peers.Add(player, peer); pingAt = 0;
                        if (player <= 8)
                        {
                            credentials[player] = NewCredential(State.Match, player, 1);
                            peer.Pipe.Send(CredentialFrame(credentials[player]));
                            slots[player - 1] = slots[player - 1] with { Connected = true };
                        }
                        Change($"Подключён игрок {player}", resetReady: true); Admitted(peer);
                    }
                    else
                    {
                        // All simulations stay at K while transfer runs; admission is the K+1 barrier.
                        phase = MatchPhase.Paused; Change("Восстановление участника · общая пауза");
                        if (recoverySnapshot is null || recoverySnapshotTick != world.Tick)
                        { recoverySnapshot = world.CaptureSnapshot(_stop.Token); recoverySnapshotTick = world.Tick; }
                        byte[] snapshot = recoverySnapshot, history = History();
                        var transfer = Task.Run(() => RecoveryTransfer.Send(peer.Pipe, snapshot, history, acceptStop.Token), acceptStop.Token);
                        recovering.Add(player, (peer, transfer, history.Length, Stopwatch.GetTimestamp()));
                    }
                    socket = null; pipe = null; // Ownership moved to peers/recovering.
                }
                catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or TimeoutException or ArgumentException or NotSupportedException or SocketException)
                { RejectPeer(pipe, e is InvalidDataException ? e.Message : "Подключение отклонено."); pipe?.Dispose(); socket?.Dispose(); if (_stop.IsCancellationRequested) throw; Volatile.Write(ref _status, "Подключение отклонено: " + e.Message); }
            }
        }
        try
        {
            SetState(Snapshot("Лобби"));
            while (phase == MatchPhase.Lobby) { _stop.Token.ThrowIfCancellationRequested(); Accept(); Poll(); Idle(); }
            while (phase != MatchPhase.Finished)
            {
                _stop.Token.ThrowIfCancellationRequested(); Accept(); Recoveries(); Poll();
                if (phase == MatchPhase.Finished) break;
                if (phase == MatchPhase.Paused) { blockedAt = clock.Elapsed.TotalMilliseconds; Idle(); continue; }
                recoverySnapshot = null;
                if (prepared != world.Tick)
                {
                    long target = world.Tick + Descriptor.InputDelay;
                    for (uint p = 1; p <= count; p++)
                    {
                        if (slots[p - 1].Controller == SlotController.Ai && !pending[p - 1].ContainsKey(target))
                            pending[p - 1][target] = _planAi!(world, p).ToArray();
                        else if (p == 1 && !pending[0].ContainsKey(target)) pending[0][target] = Collect();
                    }
                    prepared = world.Tick; blockedAt = clock.Elapsed.TotalMilliseconds;
                }
                long tick = world.Tick + 1;
                var blockers = peers.Where(pair => pair.Key <= 8 && ( world.Tick - pair.Value.Ack >= window ||
                    tick >= Descriptor.InputDelay && !pair.Value.Inbox.Contains(tick))).Select(pair => pair.Key).ToArray();
                if (blockers.Length != 0)
                {
                    int elapsed = (int)Math.Clamp(clock.Elapsed.TotalMilliseconds - Math.Max(blockedAt, nextDue), 0, 60000);
                    if (elapsed >= 100 && (Waiting is not { } wait || wait.Tick != tick || !wait.Players.SequenceEqual(blockers)))
                    {
                        Volatile.Write(ref _waiting, new(tick, blockers, elapsed));
                        Broadcast(StarProtocol.WaitFrame(tick, blockers, elapsed));
                    }
                    if (clock.Elapsed.TotalMilliseconds - blockedAt >= _timeout.TotalMilliseconds)
                        foreach (uint p in blockers) Drop(p, "истёк срок команд/проверки", false);
                    Idle(); continue;
                }
                if (clock.Elapsed.TotalMilliseconds < nextDue) { Idle(); continue; }
                // Validate every peer before consuming any batch or assigning committed sequences.
                if (tick >= Descriptor.InputDelay)
                {
                    foreach (var pair in peers.Where(p => p.Key <= 8).ToArray())
                    {
                        try { pair.Value.Inbox.Validate(tick); }
                        catch (Exception e) when (PeerFailure(e)) { Drop(pair.Key, "некорректные команды", false); }
                    }
                    if (phase == MatchPhase.Paused) continue;
                }
                double waiting = clock.Elapsed.TotalMilliseconds - blockedAt;
                var frames = new List<byte[]> { StarProtocol.TurnFrame(tick, revision) };
                var commands = new List<WorldCommand>();
                for (uint p = 1; p <= count; p++)
                {
                    WorldCommand[] input = [];
                    if (tick >= Descriptor.InputDelay)
                    {
                        if (p != 1 && slots[p - 1].Controller == SlotController.Human) input = peers[p].Inbox.Take(tick);
                        else if (slots[p - 1].Controller != SlotController.None) pending[p - 1].Remove(tick, out input!);
                    }
                    var stamped = (input ?? []).Select(c => c.WithStamp(tick, p, checked(++sequences[p - 1]))).ToArray();
                    frames.Add(Wire.Batch(State.Match, p, tick, stamped)); commands.AddRange(stamped);
                }
                world.Step(commands.ToArray(), _stop.Token);
                Volatile.Write(ref _waiting, null);
                string hash = world.Report().StateHash; hashes[tick] = hash;
                frames.Add(StarProtocol.Check(StarProtocol.Hash, tick, hash));
                foreach (var frame in frames) { log!.Write(frame); Broadcast(frame); }
                Publish(world);
                long verified = peers.Where(p => p.Key <= 8).Select(p => p.Value.Ack).DefaultIfEmpty(tick).Min();
                foreach (long old in hashes.Keys.Where(t => t <= verified).ToArray()) hashes.Remove(old);
                Volatile.Write(ref _statistics, new(tick, verified, waiting, null,
                    peers.Values.Select(p => p.Pipe.SendHighWater).DefaultIfEmpty().Max(), peers.Values.Select(p => p.Pipe.ReceiveHighWater).DefaultIfEmpty().Max(), clock.Elapsed.TotalMilliseconds));
                double period = 1000.0 / Descriptor.TickRate;
                nextDue = Math.Max(nextDue + period, clock.Elapsed.TotalMilliseconds);
                if (Descriptor.EndTick != 0 && tick >= Descriptor.EndTick) { phase = MatchPhase.Finished; Change("Достигнут конечный тик"); }
            }
            byte[] final = StarProtocol.Check(StarProtocol.Finish, world.Tick, world.Report().StateHash); log!.Write(final);
            // No successful footer until every retained peer confirms the terminal hash.
            foreach (var pair in peers) Send(pair.Key, pair.Value, final);
            foreach (var pair in peers.Where(p => p.Key <= 8)) WaitFinal(pair.Value);
            foreach (var pair in peers)
            {
                Send(pair.Key, pair.Value, [StarProtocol.Complete]);
                try { pair.Value.Pipe.Flush(pair.Key > 8 ? TimeSpan.FromMilliseconds(100) : null); } catch (Exception e) when (pair.Key > 8 && PeerFailure(e)) { }
            }
            log.Complete(); return world.Report();
        }
        finally
        {
            listener.Stop(); acceptStop.Cancel();
            foreach (var recovery in recovering.Values)
            { recovery.Peer.Dispose(); try { recovery.Transfer.GetAwaiter().GetResult(); } catch { } }
            foreach (var handshake in handshakes)
            { try { var accepted = handshake.GetAwaiter().GetResult(); accepted.Pipe.Dispose(); accepted.Socket.Dispose(); } catch { } }
            foreach (var peer in peers.Values) peer.Dispose();
            log?.Dispose(); output?.Dispose();
        }
    }
}
