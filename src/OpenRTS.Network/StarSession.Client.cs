using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    private SimulationReport RunClient(string address, int port, SimulationWorld world, Func<SimulationWorld> createWorld)
    {
        using var connection = ConnectClient(address, port);
        var tls = connection.Tls;
        using var pipe = new PeerPipeline(tls, _timeout, _stop.Token);
        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.Hello); w.Write(StarProtocol.Version); w.Write(_requestSeat ? StarProtocol.AnySeat : Player); w.Write(State.Slots.Count);
            Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, world.Report().StateHash); Wire.Text(w, Descriptor.Rules); Wire.Text(w, Descriptor.ContentHash);
            w.Write(ResumeCredential is not null);
            if (ResumeCredential is { } credential) { byte[] data = CredentialFrame(credential); w.Write(data.Length); w.Write(data); }
            byte[] ticket = _joinGrant is null ? [] : System.Text.Encoding.UTF8.GetBytes(_joinGrant.Ticket);
            if (ticket.Length > 8192) throw new InvalidDataException("Admission ticket size limit.");
            w.Write((ushort)ticket.Length); w.Write(ticket);
        }));
        var clock = Stopwatch.StartNew(); double lastSeen = 0, lastTurn = 0, finishAt = 0; long sentSequence = 0, sealedTick = -1;
        long[] sequences = new long[State.Slots.Count];
        StarJournal? log = null; FileStream? output = null; bool finishing = false;
        var probe = new LatencyProbe(); double pingAt = 0;
        void Seal()
        {
            long target = world.Tick + Descriptor.InputDelay;
            if (Player == 0 || sealedTick == target || State.Slots[(int)Player - 1].Controller != SlotController.Human) return;
            var commands = Collect().Select(c => c.WithStamp(target, Player, checked(++sentSequence))).ToArray();
            pipe.Send(Wire.Batch(State.Match, Player, target, commands)); sealedTick = target;
        }
        void PumpRequests()
        {
            int count = Volatile.Read(ref _requestCount);
            for (int i = 0; i < count && _requests.TryDequeue(out var request); i++)
            { Interlocked.Decrement(ref _requestCount); pipe.Send(request); }
        }
        // Only the coordinator emits turns. A disconnected client can never confirm a local branch.
        byte[] Read()
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (!finishing && State.Match != Guid.Empty && clock.Elapsed.TotalMilliseconds >= pingAt && !probe.Pending)
                {
                    try { pipe.Send(StarProtocol.Probe(StarProtocol.Ping, probe.Start())); }
                    catch (Exception e) when (LinkFailure(e)) { /* Final buffered messages still precede EOF. */ }
                    pingAt = clock.Elapsed.TotalMilliseconds + Math.Min(1000, _timeout.TotalMilliseconds / 3);
                }
                if ((log is not null && State.Phase == MatchPhase.Playing && clock.Elapsed.TotalMilliseconds - lastTurn > _timeout.TotalMilliseconds) ||
                    finishing && clock.Elapsed.TotalMilliseconds - finishAt > _timeout.TotalMilliseconds)
                    throw new TimeoutException("Coordinator turn/completion deadline exceeded.");
                if (pipe.TryRead(out var frame))
                {
                    lastSeen = clock.Elapsed.TotalMilliseconds;
                    if (frame[0] == StarProtocol.Ping)
                    {
                        long nonce = StarProtocol.ReadProbe(frame, StarProtocol.Ping);
                        try { pipe.Send(StarProtocol.Probe(StarProtocol.Pong, nonce)); } catch (Exception e) when (LinkFailure(e)) { /* Consume any buffered final messages before EOF. */ }
                        continue;
                    }
                    if (frame[0] == StarProtocol.Pong)
                    {
                        if (probe.Receive(StarProtocol.ReadProbe(frame, StarProtocol.Pong)) is { } roundTrip)
                            Volatile.Write(ref _latencies, Array.AsReadOnly(new[] { new NetworkPeerLatency(1, roundTrip) }));
                        continue;
                    }
                    if (frame[0] == StarProtocol.Waiting)
                    {
                        var wait = StarProtocol.ReadWait(frame);
                        if (log is null || State.Phase != MatchPhase.Playing || wait.Tick != world.Tick + 1 || wait.Players.Any(p => p > State.Slots.Count))
                            throw new InvalidDataException("Wait report boundary mismatch.");
                        Volatile.Write(ref _waiting, new(wait.Tick, wait.Players, wait.Milliseconds)); continue;
                    }
                    if (frame[0] == StarProtocol.Chat) { var message = StarProtocol.ReadChat(frame); if (message.Player > State.Slots.Count) throw new InvalidDataException("Invalid chat player."); Chat(message); continue; }
                    return frame;
                }
                if (!finishing && State.Phase != MatchPhase.Finished && (Player == 0 || State.Slots[(int)Player - 1].Controller == SlotController.Human)) PumpRequests();
                pipe.Check();
                double waiting = clock.Elapsed.TotalMilliseconds - lastTurn - 1000.0 / Descriptor.TickRate;
                if (log is not null && State.Phase == MatchPhase.Playing && waiting >= 100 && Waiting is null)
                    Volatile.Write(ref _waiting, new(world.Tick + 1, [1], (int)Math.Min(waiting, 60000)));
                if (clock.Elapsed.TotalMilliseconds - lastSeen > _timeout.TotalMilliseconds) throw new TimeoutException("Player-host progress deadline exceeded.");
                Idle();
            }
        }
        try
        {
            while (true)
            {
                byte[] frame = Read();
                switch (frame[0])
                {
                    case StarProtocol.Rejected:
                        throw new InvalidDataException(Wire.Unpack(frame, r => { r.ReadByte(); return Wire.Text(r); }));
                    case StarProtocol.SeatAssigned:
                        uint seat = Wire.Unpack(frame, r => { r.ReadByte(); return r.ReadUInt32(); });
                        if (!_requestSeat || State.Match != Guid.Empty || ResumeCredential is not null || seat < 2 || seat > State.Slots.Count)
                            throw new InvalidDataException("Некорректное место в лобби.");
                        Volatile.Write(ref _player, seat); break;
                    case StarProtocol.PasswordChallenge:
                        if (State.Match != Guid.Empty || frame.Length != 33) throw new InvalidDataException("Некорректный запрос пароля.");
                        pipe.Send([StarProtocol.PasswordProof, .. PasswordProof(frame[1..])]);
                        break;
                    case StarProtocol.AdmissionChallenge:
                        if (_joinGrant is null || _accountIdentity is null || log is not null) throw new InvalidDataException("Unexpected admission challenge.");
                        string nonce = Wire.Unpack(frame, r => { r.ReadByte(); return Wire.Text(r); });
                        if (nonce.Length != 64 || !nonce.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid admission challenge.");
                        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.AdmissionProof); Wire.Text(w, _accountIdentity.SignAdmission(_joinGrant.Ticket, nonce)); }));
                        break;
                    case StarProtocol.Credential:
                        var credential = ReadCredential(frame);
                        if (credential.Player != Player || State.Match != Guid.Empty && credential.Match != State.Match ||
                            ResumeCredential is { } old && (credential.Match != old.Match || credential.Epoch <= old.Epoch))
                            throw new InvalidDataException("Resume credential identity/epoch mismatch.");
                        Volatile.Write(ref _resumeCredential, credential);
                        break;
                    case StarProtocol.Recovery:
                        if (log is not null || Player != 0 && ResumeCredential is null) throw new InvalidDataException("Unexpected recovery transfer.");
                        Volatile.Write(ref _status, "Загрузка состояния матча…");
                        var transfer = RecoveryTransfer.Receive(frame, pipe, _timeout, _stop.Token);
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                        using (var history = new MemoryStream(transfer.History, false))
                        using (var reader = new StarJournalReader(history, Descriptor, stop: deadline.Token, openPrefix: true))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(15));
                            while (reader.Next() is not null) { }
                            if (reader.State.Phase != MatchPhase.Paused || reader.Info.Recipe != LobbySettings || reader.Info.InitialHash != _initialHash ||
                                Player != 0 && ResumeCredential!.Match != reader.State.Match)
                                throw new InvalidDataException("Recovery history identity/boundary mismatch.");
                            var restored = SimulationWorld.RestoreSnapshot(transfer.Snapshot,
                                () => _prepare is null ? createWorld() : _prepare(LobbySettings).World, deadline.Token);
                            if (!restored.PlayerCommandsRestricted || restored.Tick != reader.Tick || restored.Report().StateHash != reader.Hash)
                            { restored.Dispose(); throw new InvalidDataException("Snapshot/history boundary mismatch."); }
                            world.Dispose(); world = restored; _ownedWorld = restored;
                            sequences = reader.Sequences; SetState(reader.State);
                        }
                        output = File.Create(JournalPath); output.Write(transfer.History);
                        log = new StarJournal(output, transfer.History.Length);
                        pipe.Send(StarProtocol.Check(StarProtocol.RecoveryReady, world.Tick, world.Report().StateHash));
                        Publish(world); lastTurn = lastSeen = clock.Elapsed.TotalMilliseconds;
                        break;
                    case StarProtocol.Settings:
                        if (_prepare is null || log is not null) throw new InvalidDataException("Unexpected lobby configuration.");
                        var configuration = ReadSettings(frame);
                        world = PrepareSettings(configuration.Data);
                        if (Wire.Hash(DirectMatch.Descriptor(Descriptor)) != configuration.Digest || _initialHash != configuration.Hash)
                            throw new InvalidDataException("Lobby configuration hash mismatch.");
                        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.SettingsAck); Wire.Text(w, SettingsDigest); Wire.Text(w, _initialHash); }));
                        break;
                    case StarProtocol.State:
                        var next = StarProtocol.ReadState(frame); var previous = State;
                        if (previous.Match != Guid.Empty && next.Match != previous.Match || next.Slots.Count != previous.Slots.Count ||
                            Player != 0 && next.Slots[(int)Player - 1].Controller == SlotController.Human && !next.Slots[(int)Player - 1].Connected)
                            throw new InvalidDataException("Lobby identity mismatch.");
                        if (log is null)
                        {
                            if (next.Tick != 0 || next.Revision < previous.Revision || next.Phase is not (MatchPhase.Lobby or MatchPhase.Playing))
                                throw new InvalidDataException("Invalid lobby/start transition.");
                            if (next.Phase == MatchPhase.Playing)
                            {
                                if (previous.Match == Guid.Empty || next.Slots.Any(p => p.Controller == SlotController.Human && (!p.Connected || !p.Ready)))
                                    throw new InvalidDataException("Start without agreed Ready.");
                                output = File.Create(JournalPath); log = new StarJournal(output, Descriptor, world.Report().StateHash, frame, LobbySettings);
                            }
                        }
                        else { StarJournal.ValidateTransition(previous, next, world.Tick); log.Write(frame); }
                        SetState(next);
                        lastTurn = clock.Elapsed.TotalMilliseconds;
                        if (log is not null) { Seal(); if (world.Tick == 0) Publish(world); }
                        break;
                    case StarProtocol.Turn:
                        if (log is null || finishing) throw new InvalidDataException("Unexpected turn.");
                        long tick = StarProtocol.ReadTurn(frame, world.Tick + 1, State);
                        if (Descriptor.EndTick != 0 && tick > Descriptor.EndTick) throw new InvalidDataException("End tick exceeded.");
                        var records = new List<byte[]> { frame }; var commands = new List<WorldCommand>();
                        foreach (var slot in State.Slots)
                        {
                            byte[] batch = Read();
                            commands.AddRange(StarJournal.ReadCommands(batch, State, slot.Player, tick, sequences)); records.Add(batch);
                        }
                        byte[] checkFrame = Read(); var check = StarProtocol.ReadCheck(checkFrame, StarProtocol.Hash); records.Add(checkFrame);
                        if (check.Tick != tick) throw new InvalidDataException("Turn hash order mismatch.");
                        world.Step(commands.ToArray(), _stop.Token);
                        Volatile.Write(ref _waiting, null);
                        string hash = world.Report().StateHash;
                        if (hash != check.Hash) throw new DesyncException(tick, hash, check.Hash);
                        foreach (var record in records) log.Write(record);
                        if (Player != 0 && tick % Descriptor.HashInterval == 0) pipe.Send(StarProtocol.Check(StarProtocol.Hash, tick, hash));
                        Publish(world); Seal(); lastTurn = clock.Elapsed.TotalMilliseconds;
                        Volatile.Write(ref _statistics, new(tick, tick, 0, null, pipe.SendHighWater, pipe.ReceiveHighWater, clock.Elapsed.TotalMilliseconds));
                        break;
                    case StarProtocol.Finish:
                        if (log is null || finishing || State.Phase != MatchPhase.Finished && Player != 0 && State.Slots[(int)Player - 1].Controller == SlotController.Human)
                            throw new InvalidDataException("Unexpected final boundary.");
                        var final = StarProtocol.ReadCheck(frame, StarProtocol.Finish);
                        if (final.Tick != world.Tick || final.Hash != world.Report().StateHash) throw new DesyncException(world.Tick, world.Report().StateHash, final.Hash);
                        log.Write(frame); finishing = true; finishAt = clock.Elapsed.TotalMilliseconds;
                        if (Player != 0) { pipe.Send(StarProtocol.Check(StarProtocol.Ack, world.Tick, final.Hash)); pipe.Flush(); } break;
                    case StarProtocol.Complete:
                        if (frame.Length != 1 || !finishing) throw new InvalidDataException("Premature completion.");
                        log!.Complete(); return world.Report();
                    default: throw new InvalidDataException("Unknown coordinator message.");
                }
            }
        }
        finally { log?.Dispose(); output?.Dispose(); }
    }
}
