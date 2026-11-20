using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Network;

internal static class StarChecks
{
    private static void Check(bool value, string message = "NET3 assertion failed") { if (!value) throw new Exception(message); }
    private static void Wait(Func<bool> ready, params StarSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            foreach (var session in sessions) if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult();
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("NET3 scenario timed out: " + string.Join(" | ", sessions.Select(s => s.Status)));
            Thread.Sleep(5);
        }
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static long Tick(StarSession session) { using var frame = session.Frames.AcquireLatest(); return frame?.Tick ?? -1; }
    private static SimulationWorld World(int workers = 1, int players = 4)
    {
        var world = new SimulationWorld([new UnitDefinition("unit", 100)], 65536, 65536, 42, navigationWorkers: workers);
        for (uint p = 1; p <= players; p++) world.AddInitialUnit(0, new((int)p * 4096, 2048), p);
        return world;
    }
    private static readonly MatchDescriptor Descriptor = new("net3-test", "unit", "four-players", 42, EndTick: 0, InputDelay: 3, HashInterval: 4);
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _socket = new();
        private readonly System.Net.Security.SslStream _tls;
        internal RawPeer(Match match, uint player, int protocol = StarProtocol.Version)
        {
            _socket.Connect(IPAddress.Loopback, match.Port);
            _tls = new(_socket.GetStream(), false, (_, _, _, _) => true);
            _tls.AuthenticateAsClient("openrts.local");
            using var world = World(); world.RestrictCommandsToPlayers();
            Send(Wire.Pack(w => { w.Write(StarProtocol.Hello); w.Write(protocol); w.Write(player); w.Write(4);
                Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, world.Report().StateHash);
                Wire.Text(w, Descriptor.Rules); Wire.Text(w, Descriptor.ContentHash); w.Write(false); w.Write((ushort)0); }));
        }
        internal void Send(byte[] frame) => Wire.Send(_tls, frame, default).GetAwaiter().GetResult();
        internal void OversizedFrame() => _tls.Write(new byte[] { 1, 0, 4, 0 });
        public void Dispose() { _tls.Dispose(); _socket.Dispose(); }
    }
    private sealed class Match : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "openrts-net3-" + Guid.NewGuid().ToString("N"));
        internal readonly int Port = StarChecks.Port();
        internal readonly List<StarSession> Sessions = [];
        internal readonly StarSession Host;
        internal Match(Func<SimulationWorld, uint, IReadOnlyList<WorldCommand>>? ai = null, string password = "")
        {
            System.IO.Directory.CreateDirectory(Directory);
            Host = new(true, "127.0.0.1", Port, null, Descriptor, () => World(), Path.Combine(Directory, "1.journal"),
                playerCount: 4, aiPlayers: [4], planAi: ai ?? ((_, _) => []), password: password);
            Sessions.Add(Host);
            // The listener is started before the owner's lobby publication.
            Wait(() => Host.Status.StartsWith("Лобби", StringComparison.Ordinal), Host);
        }
        internal StarSession Join(uint player, string? pin = null, int? relayPort = null, MatchResumeCredential? resume = null, string password = "")
        {
            var session = new StarSession(false, "127.0.0.1", relayPort ?? Port, pin ?? Host.Fingerprint, Descriptor, () => World(2),
                Path.Combine(Directory, player + (resume is null ? "" : "-resume-" + Guid.NewGuid().ToString("N")) + ".journal"), playerCount: 4, player: player,
                planAi: (_, _) => throw new Exception("A client attempted to run AI"), resumeCredential: resume, password: password);
            Sessions.Add(session); return session;
        }
        internal void Start(StarSession two, StarSession three)
        {
            StarSession[] active = [Host, two, three];
            Wait(() => Host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == Host.State.Revision && three.State.Revision == Host.State.Revision, active);
            foreach (var s in active) s.Ready(s.State.Revision);
            Wait(() => Host.State.Slots.Take(3).All(s => s.Ready), active); Host.Start();
            Wait(() => active.All(s => Tick(s) >= 1), active);
        }
        internal void Replay(StarSession session)
        {
            using var input = File.OpenRead(session.JournalPath);
            Check(StarJournal.Replay(input, Descriptor, () => World()) == session.Completion.GetAwaiter().GetResult(), "NET3 offline replay differs");
        }
        public void Dispose() { foreach (var session in Sessions.AsEnumerable().Reverse()) session.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("NET3 RTT accepts only matching probes and diagnostics reject malformed frames", () => {
            var clock = new ProbeClock(); var probe = new LatencyProbe(clock); long first = probe.Start();
            Check(probe.Pending && probe.Receive(first + 1) is null); clock.Advance(37);
            Check(probe.Receive(first) == 37 && !probe.Pending && probe.Receive(first) is null);
            long second = probe.Start(); clock.Advance(12); Check(probe.Receive(first) is null && probe.Receive(second) == 12);
            void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return; } throw new Exception("Expected diagnostic rejection"); }
            Check(StarProtocol.ReadProbe(StarProtocol.Probe(StarProtocol.Ping, 123), StarProtocol.Ping) == 123);
            Reject(() => StarProtocol.ReadProbe([StarProtocol.Ping], StarProtocol.Ping));
            Reject(() => StarProtocol.Probe(StarProtocol.Pong, 0));
            Reject(() => StarProtocol.ReadProbe(StarProtocol.Probe(StarProtocol.Ping, 1), StarProtocol.Pong));
            var wait = StarProtocol.ReadWait(StarProtocol.WaitFrame(5, [2, 3], 150));
            Check(wait.Tick == 5 && wait.Players.SequenceEqual(new uint[] { 2, 3 }) && wait.Milliseconds == 150);
            foreach (var ids in new uint[][] { [], [3, 2], [2, 2], [9] }) Reject(() => StarProtocol.WaitFrame(5, ids, 100));
            Reject(() => StarProtocol.WaitFrame(0, [2], 0)); Reject(() => StarProtocol.WaitFrame(5, [2], 60001));
            Reject(() => StarProtocol.ReadWait(StarProtocol.WaitFrame(5, [2], 100).Concat(new byte[] { 0 }).ToArray()));
        });
        test("NET3 waiting names the blocking peer on host and client and clears on pause", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); using var raw = new RawPeer(match, 3);
            Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == host.State.Revision, host, two);
            host.Ready(host.State.Revision); two.Ready(two.State.Revision); raw.Send(StarProtocol.RequestFrame(StarProtocol.Operation.Ready, host.State.Revision));
            Wait(() => host.State.Slots.Take(3).All(s => s.Ready), host, two); host.Start();
            Wait(() => host.Waiting?.Players.SequenceEqual(new uint[] { 3 }) == true && two.Waiting?.Players.SequenceEqual(new uint[] { 3 }) == true, host, two);
            Check(host.Waiting!.Tick == Descriptor.InputDelay && host.Waiting.Milliseconds >= 100 && host.Status.Contains("Ожидание", StringComparison.Ordinal));
            Wait(() => host.Latencies.Any(p => p.Player == 2) && two.Latencies.Any(p => p.Player == 1), host, two);
            Check(host.Latencies.All(p => p.RoundTripMilliseconds >= 0) && Tick(host) == Descriptor.InputDelay - 1);
            raw.Dispose(); Wait(() => host.State.Phase == MatchPhase.Paused && two.State.Phase == MatchPhase.Paused, host, two);
            Check(host.Waiting is null && two.Waiting is null); host.Pause(false);
            Wait(() => Tick(two) >= 8, host, two); host.Stop(); Wait(() => host.Completion.IsCompleted && two.Completion.IsCompleted, host, two);
            match.Replay(host); match.Replay(two); Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(two.JournalPath)));
        });
        test("NET3 rejects protocol4 before slot admission", () => {
            using var match = new Match(); using var old = new RawPeer(match, 3, protocol: 4);
            Wait(() => match.Host.Status.StartsWith("Подключение отклонено", StringComparison.Ordinal), match.Host);
            Check(!match.Host.State.Slots[2].Connected && match.Host.State.Phase == MatchPhase.Lobby && !match.Host.Completion.IsCompleted);
        });
        foreach (string violation in new[] { "spectator control", "guest control", "chat spoof", "invalid UTF8", "frame quota", "chat rate", "sequence gap" })
            test("NET3 isolates peer protocol violation: " + violation, () => {
                using var match = new Match(); var host = match.Host; var two = match.Join(2);
                bool spectator = violation is not ("guest control" or "sequence gap");
                var three = spectator ? match.Join(3) : null;
                using var raw = new RawPeer(match, spectator ? 0u : 3u);
                var survivors = three is null ? new[] { host, two } : new[] { host, two, three };
                Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && (!spectator || host.State.Spectators == 1) &&
                    survivors.All(s => s.State.Revision == host.State.Revision), survivors);
                foreach (var session in survivors) session.Ready(session.State.Revision);
                if (!spectator) raw.Send(StarProtocol.RequestFrame(StarProtocol.Operation.Ready, host.State.Revision));
                Wait(() => host.State.Slots.Take(3).All(s => s.Ready), survivors); host.Start();
                Wait(() => Tick(host) >= 1, survivors);
                if (violation == "sequence gap") host.Submit(new(0, 0, 0, CommandKind.Move, new(1), new(12000, 2048)));
                switch (violation)
                {
                    case "spectator control": raw.Send(StarProtocol.RequestFrame(StarProtocol.Operation.Ready, host.State.Revision)); break;
                    case "guest control": raw.Send(StarProtocol.RequestFrame(StarProtocol.Operation.Stop, host.State.Revision)); break;
                    case "chat spoof": raw.Send(StarProtocol.ChatFrame(2, "spoof")); break;
                    case "invalid UTF8": raw.Send(Wire.Pack(w => { w.Write(StarProtocol.Chat); w.Write(0u); w.Write((ushort)1); w.Write((byte)255); })); break;
                    case "frame quota": raw.OversizedFrame(); break;
                    case "chat rate": for (int i = 0; i < 5; i++) raw.Send(StarProtocol.ChatFrame(0, "hello")); break;
                    case "sequence gap": raw.Send(Wire.Batch(host.State.Match, 3, Descriptor.InputDelay,
                        [new(Descriptor.InputDelay, 3, 2, CommandKind.Stop, new(3))])); break;
                }
                Wait(() => spectator ? host.State.Spectators == 0 : !host.State.Slots[2].Connected, survivors);
                Check(host.State.Phase == (spectator ? MatchPhase.Playing : MatchPhase.Paused));
                if (!spectator) { Check(Tick(host) < Descriptor.InputDelay); host.Pause(false); }
                Wait(() => survivors.All(s => Tick(s) >= 8), survivors);
                if (violation == "sequence gap") { using var frame = host.Frames.AcquireLatest(); Check(frame!.Poses[0].Current.X > 4096, "Valid host input was lost while rejecting a peer batch"); }
                host.Stop();
                Wait(() => survivors.All(s => s.Completion.IsCompleted), survivors);
                foreach (var session in survivors)
                { match.Replay(session); Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(session.JournalPath))); }
            });
        test("NET3 a real hash mismatch still aborts the match", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); using var raw = new RawPeer(match, 3);
            Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == host.State.Revision, host, two);
            host.Ready(host.State.Revision); two.Ready(two.State.Revision); raw.Send(StarProtocol.RequestFrame(StarProtocol.Operation.Ready, host.State.Revision));
            Wait(() => host.State.Slots.Take(3).All(s => s.Ready), host, two); host.Start();
            Wait(() => Tick(host) >= 1, host, two);
            for (int tick = Descriptor.InputDelay; tick <= Descriptor.HashInterval; tick++) raw.Send(Wire.Batch(host.State.Match, 3, tick, []));
            Wait(() => Tick(host) >= Descriptor.HashInterval, host, two);
            raw.Send(StarProtocol.Check(StarProtocol.Hash, Descriptor.HashInterval, new string('0', 64)));
            Wait(() => host.Completion.IsCompleted);
            Check(host.Completion.Exception?.GetBaseException() is DesyncException { Tick: 4 });
        });
        test("NET4.3 transfer rejects quotas, wrong offsets and damaged chunks", () => {
            byte[] Begin(int snapshot = 1, int history = 1, bool valid = true) => Wire.Pack(w => {
                w.Write(StarProtocol.Recovery); w.Write(snapshot); w.Write(history);
                w.Write(valid ? System.Security.Cryptography.SHA256.HashData(new byte[] { 1 }) : new byte[32]);
                w.Write(System.Security.Cryptography.SHA256.HashData(new byte[] { 2 }));
            });
            void Reject(Action action)
            { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException) { return; } throw new Exception("Expected transfer rejection"); }
            Reject(() => StarSession.RecoveryTransfer.Receive(Begin(int.MaxValue), null!, TimeSpan.FromSeconds(1), default));
            byte[] Chunk(byte artifact, int offset, byte value) => Wire.Pack(w => { w.Write(StarProtocol.RecoveryChunk); w.Write(artifact); w.Write(offset); w.Write(1); w.Write(value); });
            foreach (bool wrongOffset in new[] { true, false })
            {
                var stream = new MemoryStream(); var writer = new BinaryWriter(stream);
                foreach (var frame in new[] { Chunk(0, wrongOffset ? 1 : 0, 1), Chunk(1, 0, 2) }) { writer.Write(frame.Length); writer.Write(frame); }
                stream.Position = 0;
                using var pipe = new PeerPipeline(stream, TimeSpan.FromSeconds(1), default);
                Reject(() => StarSession.RecoveryTransfer.Receive(Begin(valid: false), pipe, TimeSpan.FromSeconds(1), default));
            }
        });
        test("NET4.3 two simultaneous authenticated returns preserve the common journal", () => {
            using var match = new Match(); var host = match.Host;
            var two = match.Join(2); var three = match.Join(3); match.Start(two, three);
            Wait(() => Tick(host) >= 8 && two.ResumeCredential is not null && three.ResumeCredential is not null, host, two, three);
            var tokenTwo = two.ResumeCredential!; var tokenThree = three.ResumeCredential!;
            two.Dispose(); three.Dispose();
            Wait(() => host.State.Phase == MatchPhase.Paused && !host.State.Slots[1].Connected && !host.State.Slots[2].Connected, host);
            var returnedTwo = match.Join(2, resume: tokenTwo); var returnedThree = match.Join(3, resume: tokenThree);
            Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && returnedTwo.ResumeCredential?.Epoch == 2 && returnedThree.ResumeCredential?.Epoch == 2 &&
                returnedTwo.State.Revision == host.State.Revision && returnedThree.State.Revision == host.State.Revision, host, returnedTwo, returnedThree);
            long boundary = Tick(host); Check(Tick(returnedTwo) == boundary && Tick(returnedThree) == boundary);
            host.Pause(false); Wait(() => Tick(host) >= boundary + 12, host, returnedTwo, returnedThree); host.Stop();
            Wait(() => new[] { host, returnedTwo, returnedThree }.All(s => s.Completion.IsCompleted), host, returnedTwo, returnedThree);
            foreach (var session in new[] { host, returnedTwo, returnedThree })
            { match.Replay(session); Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(session.JournalPath)), "Rejoin journal differs"); }
        });
        test("NET4.3 an authenticated human takes back the AI-controlled side at a common boundary", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); var three = match.Join(3);
            match.Start(two, three); Wait(() => Tick(host) >= 6 && two.ResumeCredential is not null, host, two, three);
            var credential = two.ResumeCredential!; two.Dispose();
            Wait(() => !host.State.Slots[1].Connected && host.State.Phase == MatchPhase.Paused, host, three);
            host.SetAi(2, true); Wait(() => host.State.Slots[1].Controller == SlotController.Ai, host, three);
            long boundary = Tick(host); host.Pause(false); Wait(() => Tick(host) > boundary + 6, host, three);
            var returned = match.Join(2, resume: credential);
            Wait(() => host.State.Slots[1].Controller == SlotController.Human && returned.ResumeCredential?.Epoch == 2 && returned.State.Revision == host.State.Revision, host, three, returned);
            Check(host.State.Phase == MatchPhase.Paused && Tick(host) == Tick(returned));
            boundary = Tick(host); host.Pause(false); Wait(() => Tick(returned) > boundary + 6, host, three, returned); host.Stop();
            Wait(() => new[] { host, three, returned }.All(s => s.Completion.IsCompleted), host, three, returned); match.Replay(returned);
        });
        test("NET4.3 rejects wrong and retired tokens without replacing the active connection", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); var three = match.Join(3);
            match.Start(two, three); Wait(() => Tick(host) >= 5 && two.ResumeCredential is not null, host, two, three);
            var old = two.ResumeCredential!; two.Dispose();
            Wait(() => !host.State.Slots[1].Connected && host.State.Phase == MatchPhase.Paused, host, three);
            var bad = match.Join(2, resume: old with { Token = new string('0', 64) });
            Wait(() => bad.Completion.IsCompleted, host, three); Check(bad.Completion.IsFaulted && !host.State.Slots[1].Connected);
            var returned = match.Join(2, resume: old);
            Wait(() => host.State.Slots[1].Connected && returned.ResumeCredential?.Epoch == 2, host, three, returned);
            var newer = returned.ResumeCredential!;
            long resumedAt = Tick(host); host.Pause(false);
            Wait(() => Tick(host) > resumedAt + Descriptor.HashInterval * 2, host, three, returned);
            host.Pause(true); Wait(() => host.State.Phase == MatchPhase.Paused && returned.State.Revision == host.State.Revision, host, three, returned);
            returned.Dispose();
            Wait(() => !host.State.Slots[1].Connected, host, three);
            var stale = match.Join(2, resume: old); Wait(() => stale.Completion.IsCompleted, host, three);
            Check(stale.Completion.IsFaulted && !host.State.Slots[1].Connected);
            var latest = match.Join(2, resume: newer);
            Wait(() => latest.ResumeCredential?.Epoch == 3 && host.State.Slots[1].Connected, host, three, latest);
            var duplicate = match.Join(2, resume: latest.ResumeCredential);
            Wait(() => duplicate.Completion.IsCompleted, host, three, latest); Check(duplicate.Completion.IsFaulted && host.State.Slots[1].Connected);
            host.Stop(); Wait(() => new[] { host, three, latest }.All(s => s.Completion.IsCompleted), host, three, latest);
            match.Replay(latest);
        });
        test("NET4.3 mid-game spectator loads a runtime boundary and follows live turns", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); var three = match.Join(3);
            match.Start(two, three); Wait(() => Tick(host) >= 8, host, two, three);
            var uninvited = match.Join(0); Wait(() => uninvited.Completion.IsCompleted, host, two, three);
            Check(uninvited.Completion.IsFaulted && host.State.Phase == MatchPhase.Playing, "An observer paused a running game");
            host.Pause(true); Wait(() => host.State.Phase == MatchPhase.Paused, host, two, three);
            var observer = match.Join(0);
            Wait(() => observer.State.Spectators == 1 && observer.State.Revision == host.State.Revision && Tick(observer) == Tick(host), host, two, three, observer);
            Check(host.State.Phase == MatchPhase.Paused); long boundary = Tick(host); host.Pause(false);
            Wait(() => Tick(observer) > boundary + 8, host, two, three, observer); host.Stop();
            Wait(() => new[] { host, two, three, observer }.All(s => s.Completion.IsCompleted), host, two, three, observer);
            match.Replay(observer); Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(observer.JournalPath)));
        });
        test("NET3 stale Ready, config reset, three TLS players and shared pause", () => {
            using var match = new Match(); var host = match.Host;
            var two = match.Join(2); var three = match.Join(3);
            Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == host.State.Revision && three.State.Revision == host.State.Revision, match.Sessions.ToArray());
            long stale = host.State.Revision;
            host.SetAi(4, false); Wait(() => host.State.Revision > stale, match.Sessions.ToArray());
            Check(host.State.Slots.All(s => !s.Ready));
            host.Ready(stale); two.Ready(stale); three.Ready(stale); host.Start(); Thread.Sleep(100);
            Check(host.State.Phase == MatchPhase.Lobby && host.State.Slots.All(s => !s.Ready));
            host.SetAi(4, true); Wait(() => host.State.Slots[3].Controller == SlotController.Ai, match.Sessions.ToArray());
            match.Start(two, three);
            for (uint p = 1; p <= 3; p++) match.Sessions[(int)p - 1].Submit(new(0, 0, 0, CommandKind.Move, new(p), new(20000, (int)p * 4096)));
            two.SendChat("Привет, RTS");
            Wait(() => Tick(host) >= 8, match.Sessions.ToArray()); two.Pause(true);
            Wait(() => match.Sessions.All(s => s.State.Phase == MatchPhase.Paused), match.Sessions.ToArray());
            long tick = Tick(host); Check(match.Sessions.All(s => Tick(s) == tick)); Thread.Sleep(150); Check(match.Sessions.All(s => Tick(s) == tick));
            Check(match.Sessions.All(s => s.TryReadChat(out var chat) && chat == new MatchChat(2, "Привет, RTS")));
            three.Pause(false); Wait(() => Tick(host) >= tick + 6, match.Sessions.ToArray()); host.Stop();
            Wait(() => match.Sessions.All(s => s.Completion.IsCompleted), match.Sessions.ToArray());
            Check(match.Sessions.All(s => s.Completion.Result == host.Completion.Result));
            foreach (var session in match.Sessions) { match.Replay(session); Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)), "Control/command histories differ"); }
            using var incomplete = new MemoryStream(File.ReadAllBytes(host.JournalPath)[..^5]);
            try { StarJournal.Replay(incomplete, Descriptor, () => World()); throw new Exception("Truncated replay was accepted"); } catch (EndOfStreamException) { }
        });
        test("NET3 agreed leave, host AI takeover and survivor histories", () => {
            var planned = new Dictionary<uint, List<long>>();
            using var match = new Match((world, p) => {
                if (!planned.TryGetValue(p, out var ticks)) planned[p] = ticks = [];
                Check(!ticks.Contains(world.Tick), "AI planned twice on one world tick"); ticks.Add(world.Tick);
                return [new(0, 0, 0, CommandKind.Move, new(p), new(30000, (int)p * 4096))];
            });
            var host = match.Host; var two = match.Join(2); var three = match.Join(3); match.Start(two, three);
            Wait(() => Tick(host) >= 8, match.Sessions.ToArray()); three.Leave();
            Wait(() => three.Completion.IsCompleted && host.State.Phase == MatchPhase.Paused && two.State.Revision == host.State.Revision, match.Sessions.ToArray());
            Check(host.State.Slots[2].Controller == SlotController.None && Tick(host) == three.Completion.Result.Tick);
            match.Replay(three); host.SetAi(3, true);
            Wait(() => host.State.Slots[2].Controller == SlotController.Ai && two.State.Revision == host.State.Revision, host, two);
            host.Pause(false); Wait(() => Tick(host) >= three.Completion.Result.Tick + 10, host, two); host.Stop();
            Wait(() => host.Completion.IsCompleted && two.Completion.IsCompleted, host, two);
            Check(host.Completion.Result == two.Completion.Result && planned[3].Count >= 7);
            match.Replay(host); match.Replay(two);
            Check(File.ReadAllBytes(host.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(two.JournalPath)));
            byte[] departed = File.ReadAllBytes(three.JournalPath), survived = File.ReadAllBytes(host.JournalPath);
            // The departing history has its own terminal checkpoint/footer after the common membership prefix.
            int footer = 4 + 1 + 8 + 2 + 64 + 5;
            Check(departed.AsSpan(0, departed.Length - footer).SequenceEqual(survived.AsSpan(0, departed.Length - footer)));
        });
        test("NET3 spectator loss keeps playing; guest loss pauses; host loss aborts clients", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); var three = match.Join(3);
            var observer = match.Join(0); Wait(() => host.State.Spectators == 1, host, two, three, observer); match.Start(two, three);
            long beforeObserverLeave = Tick(host); observer.Dispose();
            Wait(() => host.State.Spectators == 0 && Tick(host) >= beforeObserverLeave + 3, host, two, three);
            Check(host.State.Phase == MatchPhase.Playing, "A departed spectator paused the match");
            Wait(() => Tick(host) >= 5, match.Sessions.ToArray()); three.Dispose();
            Wait(() => host.State.Phase == MatchPhase.Paused && two.State.Phase == MatchPhase.Paused, host, two);
            long tick = Tick(two); Thread.Sleep(150); Check(Tick(host) == tick && Tick(two) == tick);
            host.Pause(false); Wait(() => Tick(two) >= tick + 4, host, two);
            host.Dispose(); Wait(() => two.Completion.IsCompleted); Check(!two.Completion.IsCompletedSuccessfully);
            long ended = Tick(two); Thread.Sleep(100); Check(Tick(two) == ended);
        });
        test("NET3 rejects slot collision and bad pin without killing lobby", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2);
            Wait(() => host.State.Slots[1].Connected, host, two);
            var collision = match.Join(2); Wait(() => collision.Completion.IsCompleted); Check(collision.Completion.IsFaulted);
            var badPin = match.Join(3, new string('A', 64)); Wait(() => badPin.Completion.IsCompleted); Check(badPin.Completion.IsFaulted);
            Check(!host.Completion.IsCompleted && host.State.Phase == MatchPhase.Lobby);
            var three = match.Join(3); match.Start(two, three); host.Stop();
            Wait(() => host.Completion.IsCompleted && two.Completion.IsCompleted && three.Completion.IsCompleted, host, two, three);
            Check(host.Completion.Result == two.Completion.Result && host.Completion.Result == three.Completion.Result);
        });
        test("NET3 control revision, frame limits and command ownership envelope", () => {
            var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing,
                [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true), new(3, SlotController.Ai, false, false)], "start");
            var copy = StarProtocol.ReadState(StarProtocol.WriteState(state)); Check(copy.Slots.Count == 3);
            void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; } throw new Exception("Expected rejection"); }
            Reject(() => StarJournal.ValidateTransition(state, new(state.Match, 3, 0, MatchPhase.Paused, state.Slots, "gap"), 0));
            Reject(() => StarProtocol.ChatFrame(2, new string('я', 129)));
            Reject(() => Wire.Batch(Guid.NewGuid(), 9, 1, []));
            var batch = Wire.Batch(state.Match, 3, 1, [new(1, 3, 1, CommandKind.Stop, new(3))]);
            Check(StarJournal.ReadCommands(batch, state, 3, 1, new long[3]).Single().Issuer == 3);
            Reject(() => StarJournal.ReadCommands(batch, state, 2, 1, new long[3]));
        });
        test("NET3 asymmetric WAN delay/loss retains bounded verification and identical journals", () => {
            using var match = new Match(); var host = match.Host;
            using var fast = new WanLink(match.Port, rtt: 50, jitter: 20);
            using var slow = new WanLink(match.Port, rtt: 150, jitter: 20, lossPercent: 5);
            var two = match.Join(2, relayPort: fast.Port); var three = match.Join(3, relayPort: slow.Port); match.Start(two, three);
            two.Submit(new(0, 0, 0, CommandKind.Move, new(2), new(25000, 4096)));
            three.Submit(new(0, 0, 0, CommandKind.Move, new(3), new(25000, 6144)));
            Wait(() => Tick(host) >= 35, host, two, three);
            Check(host.Statistics is { } sample && sample.Tick - sample.VerifiedTick <= 2 * Descriptor.InputDelay + Descriptor.HashInterval);
            Check(slow.Retransmissions > 0 && host.Statistics!.SendQueueHighWater <= PeerPipeline.Capacity);
            host.Stop(); Wait(() => match.Sessions.All(s => s.Completion.IsCompleted), match.Sessions.ToArray());
            foreach (var session in match.Sessions) { Check(session.Completion.Result == host.Completion.Result); match.Replay(session); }
            Check(File.ReadAllBytes(host.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(three.JournalPath)));
        });
        test("Lobby host configuration replaces guest defaults; NET4.3 restores the host recipe", () => {
            string directory = Path.Combine(Path.GetTempPath(), "openrts-lobby-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            int port = Port();
            (MatchDescriptor Descriptor, SimulationWorld World) Prepare(string data)
            {
                var world = World();
                if (data == "host-revised") world.SetInitialTeams(new Dictionary<uint, uint> { [1] = 1, [2] = 1 });
                return (Descriptor with { ScenarioHash = data }, world);
            }
            try
            {
                using var host = new StarSession(true, "127.0.0.1", port, null, Descriptor with { ScenarioHash = "host" }, () => World(), Path.Combine(directory, "host"),
                    playerCount: 4, aiPlayers: [3], closedPlayers: [4], planAi: (_, _) => [], lobbySettings: "host", prepareSettings: Prepare);
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, Descriptor with { ScenarioHash = "different-local-map" }, () => World(), Path.Combine(directory, "guest"),
                    playerCount: 4, player: 2, lobbySettings: "different-local-map", prepareSettings: Prepare);
                using var spectator = new StarSession(false, "127.0.0.1", port, host.Fingerprint, Descriptor, () => World(), Path.Combine(directory, "spectator"),
                    playerCount: 4, player: 0, lobbySettings: "default", prepareSettings: Prepare);
                Wait(() => guest.LobbySettings == "host" && spectator.LobbySettings == "host" && host.State.Spectators == 1, host, guest, spectator);
                host.ConfigureLobby("host-revised", [SlotController.Human, SlotController.Human, SlotController.Ai, SlotController.None]);
                Wait(() => guest.LobbySettings == "host-revised" && spectator.LobbySettings == "host-revised" && guest.State.Revision == host.State.Revision, host, guest, spectator);
                void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected role rejection"); }
                Reject(() => guest.ConfigureLobby("foreign", [SlotController.Human, SlotController.Human, SlotController.Ai, SlotController.None]));
                Reject(() => spectator.Ready(spectator.State.Revision)); Reject(() => spectator.Pause(true));
                Reject(() => spectator.Submit(new(0, 0, 0, CommandKind.Stop, new(1))));
                host.Ready(host.State.Revision); guest.Ready(guest.State.Revision);
                Wait(() => host.State.Slots.Take(2).All(p => p.Ready), host, guest, spectator); host.Start();
                Wait(() => Tick(spectator) >= 12 && guest.ResumeCredential is not null, host, guest, spectator);
                var credential = guest.ResumeCredential!; guest.Dispose();
                Wait(() => host.State.Phase == MatchPhase.Paused && !host.State.Slots[1].Connected, host, spectator);
                using var returned = new StarSession(false, "127.0.0.1", port, host.Fingerprint, Descriptor with { ScenarioHash = "different-local-map" }, () => World(), Path.Combine(directory, "returned"),
                    playerCount: 4, player: 2, lobbySettings: "different-local-map", prepareSettings: Prepare, resumeCredential: credential);
                Wait(() => returned.ResumeCredential?.Epoch == 2 && returned.State.Revision == host.State.Revision, host, returned, spectator);
                Check(returned.LobbySettings == "host-revised", "Rejoin lost authoritative recipe");
                long boundary = Tick(host); host.Pause(false); Wait(() => Tick(returned) >= boundary + 8, host, returned, spectator); host.Stop();
                Wait(() => host.Completion.IsCompleted && returned.Completion.IsCompleted && spectator.Completion.IsCompleted, host, returned, spectator);
                foreach (var session in new[] { host, returned, spectator })
                {
                    Check(session.Completion.Result == host.Completion.Result);
                    using var log = File.OpenRead(session.JournalPath); Check(StarJournal.Replay(log, session.Descriptor, () => Prepare("host-revised").World) == host.Completion.Result);
                    Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)));
                }
            }
            finally { Directory.Delete(directory, true); }
        });
        test("LAN directories accept own-source registrations and forward only one hop with expiry", () => {
            using var origin = new LanRegistry(() => null, 0); using var mirror = new LanRegistry(() => null, 0);
            using var next = new LanRegistry(() => null, 0);
            var game = new LanGame("192.0.2.1", 17771, "LAN", "map", 1, 1, 0, new string('C', 64), [2], true, Guid.NewGuid());
            LanRegistry.Entry[] List(LanRegistry node) => LanRegistry.FindAsync("localhost", default, node.Port).GetAwaiter().GetResult();
            Check(List(origin).Length == 0);
            LanRegistry.RegisterAsync("localhost", game, default, origin.Port).GetAwaiter().GetResult();
            var seen = List(origin).Single(); Check(seen.Game.Address == "127.0.0.1" && seen.Game.Match == game.Match && !seen.Copy);
            mirror.Remember([seen with { RemainingMs = 250 }]);
            var copy = List(mirror).Single(); Check(copy.Copy && copy.RemainingMs <= 250);
            next.Remember([copy]); Check(List(next).Length == 0);
            Wait(() => List(mirror).Length == 0);
        });
        test("LAN client answers before hosting and unicast ignores an advertised source address", () => {
            LanGame? entry = null;
            using var node = new LanGames(() => Volatile.Read(ref entry));
            Check(LanGames.QueryAsync("127.0.0.1").GetAwaiter().GetResult() is null);
            Volatile.Write(ref entry, new("192.0.2.1", 17771, "LAN", "crossroads", 1, 1, 0, new string('B', 64), [2], true));
            var found = LanGames.QueryAsync("localhost").GetAwaiter().GetResult();
            Check(found is { Address: "127.0.0.1", PasswordRequired: true } && found.Fingerprint == entry.Fingerprint);
        });
        test("Room password guards both seats and spectators before admission", () => {
            using var match = new Match(password: "пароль");
            foreach (uint player in new uint[] { 2, 0 })
            {
                var bad = match.Join(player, password: "wrong");
                Wait(() => bad.Completion.IsCompleted, match.Host);
                Check(bad.Completion.IsFaulted && !match.Host.State.Slots[1].Connected && match.Host.State.Spectators == 0);
            }
            var guest = match.Join(2, password: "пароль"); var watch = match.Join(0, password: "пароль");
            Wait(() => match.Host.State.Slots[1].Connected && match.Host.State.Spectators == 1, match.Host, guest, watch);
        });
        test("LAN directory discovers a real lobby and its TLS fingerprint", () => {
            var entry = new LanGame("", 17771, "LAN test", "crossroads", 1, 2, 0, new string('A', 64), [2, 3]);
            using var advertise = new LanGames(() => entry);
            using var browser = new LanGames(destination: IPAddress.Loopback);
            Wait(() => browser.Games.Count != 0);
            Check(browser.Games.Any(g => g.Title == entry.Title && g.Address == "127.0.0.1" && g.Fingerprint == entry.Fingerprint && g.AvailablePlayers!.SequenceEqual(new uint[] { 2, 3 })));
        });
        test("NET3 eight human slots at minimum input delay", () => {
            int port = Port(); var sessions = new List<StarSession>();
            string directory = Path.Combine(Path.GetTempPath(), "openrts-net3-eight-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var descriptor = Descriptor with { EndTick = 16, InputDelay = 1 };
            try
            {
                var host = new StarSession(true, "127.0.0.1", port, null, descriptor, () => World(players: 8), Path.Combine(directory, "1"), playerCount: 8); sessions.Add(host);
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                for (uint p = 2; p <= 8; p++) sessions.Add(new(false, "127.0.0.1", port, host.Fingerprint, descriptor,
                    () => World(2, 8), Path.Combine(directory, p.ToString()), playerCount: 8, player: p));
                Wait(() => host.State.Slots.All(s => s.Connected) && sessions.All(s => s.State.Revision == host.State.Revision), sessions.ToArray());
                foreach (var session in sessions) session.Ready(session.State.Revision);
                Wait(() => host.State.Slots.All(s => s.Ready), sessions.ToArray()); host.Start();
                Wait(() => sessions.All(s => Tick(s) >= 1), sessions.ToArray());
                foreach (var session in sessions) session.Submit(new(0, 0, 0, CommandKind.Move, new(session.Player), new((int)session.Player * 4096, 10000)));
                Wait(() => sessions.All(s => s.Completion.IsCompleted), sessions.ToArray());
                foreach (var session in sessions)
                {
                    Check(session.Completion.Result == host.Completion.Result);
                    using var input = File.OpenRead(session.JournalPath); Check(StarJournal.Replay(input, descriptor, () => World(players: 8)) == host.Completion.Result);
                    Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)));
                }
            }
            finally { foreach (var session in sessions) session.Dispose(); Directory.Delete(directory, true); }
        });
        test("NET3 heartbeat cannot extend missing-command deadline", () => {
            string directory = Path.Combine(Path.GetTempPath(), "openrts-net3-deadline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            int port = Port(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                using var host = new StarSession(true, "127.0.0.1", port, null, Descriptor, () => World(), Path.Combine(directory, "host"),
                    playerCount: 4, aiPlayers: [3, 4], planAi: (_, _) => [], timeout: TimeSpan.FromMilliseconds(300));
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                using var socket = new TcpClient(); socket.Connect(IPAddress.Loopback, port);
                using var tls = new System.Net.Security.SslStream(socket.GetStream(), false, (_, _, _, _) => true);
                tls.AuthenticateAsClient("openrts.local");
                using var initial = World(); initial.RestrictCommandsToPlayers();
                Wire.Send(tls, Wire.Pack(w => { w.Write(StarProtocol.Hello); w.Write(StarProtocol.Version); w.Write(2u); w.Write(4);
                    Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, initial.Report().StateHash); Wire.Text(w, Descriptor.Rules); Wire.Text(w, Descriptor.ContentHash); w.Write(false); w.Write((ushort)0); }), cancel.Token).GetAwaiter().GetResult();
                long revision = 0;
                while (revision == 0)
                {
                    var bytes = Wire.Receive(tls, cancel.Token).GetAwaiter().GetResult();
                    if (bytes[0] == StarProtocol.State) revision = StarProtocol.ReadState(bytes).Revision;
                }
                Wire.Send(tls, StarProtocol.RequestFrame(StarProtocol.Operation.Ready, revision), cancel.Token).GetAwaiter().GetResult();
                host.Ready(revision); Wait(() => host.State.Slots.Take(2).All(s => s.Ready), host); host.Start();
                int pings = 0;
                var reader = Task.Run(async () => {
                    try
                    {
                        while (true)
                        {
                            var frame = await Wire.Receive(tls, cancel.Token);
                            if (frame[0] == StarProtocol.Ping) { Interlocked.Increment(ref pings); await Wire.Send(tls, StarProtocol.Probe(StarProtocol.Pong, StarProtocol.ReadProbe(frame, StarProtocol.Ping)), cancel.Token); }
                            if (frame[0] == StarProtocol.State && StarProtocol.ReadState(frame).Phase == MatchPhase.Paused) return;
                        }
                    }
                    catch (EndOfStreamException) { }
                });
                Wait(() => host.State.Phase == MatchPhase.Paused, host); reader.GetAwaiter().GetResult();
                Check(Tick(host) == Descriptor.InputDelay - 1 && Volatile.Read(ref pings) >= 2);
                Check(host.State.Slots[1].Controller == SlotController.None);
                host.Stop(); Wait(() => host.Completion.IsCompleted, host);
                using var log = File.OpenRead(host.JournalPath); Check(StarJournal.Replay(log, Descriptor, () => World()) == host.Completion.Result);
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private sealed class ProbeClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        internal void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}
