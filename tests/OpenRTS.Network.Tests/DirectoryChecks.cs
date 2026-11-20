using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core;
using OpenRTS.Network;

internal static class DirectoryChecks
{
    private sealed class Faults(bool failConfirm = false, bool changeCount = false) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        private int _failConfirm = failConfirm ? 1 : 0;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Guid> Confirmed = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/confirm", StringComparison.Ordinal))
            {
                var receipt = System.Text.Json.JsonSerializer.Deserialize<MatchAdmissionReceipt>(await request.Content!.ReadAsStringAsync(stop), DirectoryJson.Options)!;
                Confirmed.Enqueue(receipt.Ticket);
                if (Interlocked.Exchange(ref _failConfirm, 0) != 0)
                    return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"code\":\"busy\",\"message\":\"Retry confirm\"}") };
            }
            var response = await base.SendAsync(request, stop);
            if (changeCount && request.RequestUri.AbsolutePath.EndsWith("/join", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var grant = System.Text.Json.JsonSerializer.Deserialize<MatchJoinGrant>(await response.Content.ReadAsStringAsync(stop), DirectoryJson.Options)!;
                response.Content.Dispose();
                response.Content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(grant with { PlayerCount = grant.PlayerCount == 2 ? 4 : 2 }, DirectoryJson.Options));
            }
            return response;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private static void Check(bool value, string message = "NET5 assertion failed") { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string? code = null)
    {
        try { action(); } catch (MatchDirectoryException e) when (code is null || e.Code == code) { return; }
        catch (InvalidDataException) when (code is null) { return; }
        throw new Exception("Expected NET5 rejection: " + code);
    }
    private static AccountSession Login(MatchDirectory service, DirectoryIdentity identity)
    { var challenge = service.Challenge(new("Player", identity.PublicKey)); return service.Login(new(challenge.Id, identity.SignLogin(challenge))); }
    private static MatchAdvertisement Game(Guid? match = null, bool hidden = false) => new(match ?? Guid.NewGuid(), 1, StarSession.ProtocolVersion, 2,
        "net5", "content", "Test match", "test-map", new string('A', 64), MatchPhase.Lobby, [2], 1, 0,
        [new(MatchEndpointKind.Direct, "127.0.0.1", 17771)], hidden, HostAdmission: false);
    private static int Port()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Wait(Func<bool> condition, params StarSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        { foreach (var session in sessions) if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult(); if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("NET5 TLS scenario timed out"); Thread.Sleep(5); }
    }
    private static long Tick(StarSession session) { using var frame = session.Frames.AcquireLatest(); return frame?.Tick ?? -1; }
    private sealed class Service : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "openrts-net5-" + Guid.NewGuid().ToString("N"));
        internal readonly string Url;
        private Process _process = null!;
        internal Service()
        {
            System.IO.Directory.CreateDirectory(Directory); Url = "http://127.0.0.1:" + Port();
            Start();
        }
        private void Start()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath("tools/OpenRTS.MatchDirectory/bin/Debug/net10.0/OpenRTS.MatchDirectory.dll"));
            start.ArgumentList.Add("--urls=" + Url); start.ArgumentList.Add("--signing-key=" + Path.Combine(Directory, "key.pem"));
            _process = Process.Start(start)!;
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) }; var clock = Stopwatch.StartNew();
            while (true)
            {
                try { if (http.GetAsync(Url + "/v1/info").GetAwaiter().GetResult().IsSuccessStatusCode) break; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                if (_process.HasExited) throw new Exception(_process.StandardError.ReadToEnd());
                if (clock.Elapsed.TotalSeconds > 8) throw new TimeoutException("Directory process did not start"); Thread.Sleep(30);
            }
        }
        internal void Restart() { Stop(); _process.Dispose(); Start(); }
        internal void Stop() { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } }
        public void Dispose() { Stop(); _process.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("Host admits after catalogue shutdown and serializes competing requests for the last seat", () => {
            using var service = new Service(); using var owner = new MatchDirectoryClient(service.Url, "host");
            var d = new MatchDescriptor("net5", "content", "host-admission", 42, EndTick: 0); int port = Port();
            SimulationWorld World() => new([new("unit", 100)], 16384, 16384, 42);
            using var host = new StarSession(true, "0.0.0.0", port, null, d, World, Path.Combine(service.Directory, "host"),
                playerCount: 3, closedPlayers: [2], password: "test");
            using var listing = new DirectoryListing(owner, host, "Locked", "127.0.0.1", port, () => "map");
            Wait(() => listing.Status.StartsWith("Игра опубликована", StringComparison.Ordinal), host);
            var game = owner.FindAsync(d.Rules, d.ContentHash).Result.Single().Game;
            Check(game.HostAdmission && game.PasswordRequired);
            Reject(() => owner.JoinAsync(game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult(), "host_admission");
            service.Stop();
            StarSession Join(string file) => new(false, game.Endpoints[0].Address, game.Endpoints[0].Port, game.Fingerprint, d, World,
                Path.Combine(service.Directory, file), playerCount: game.PlayerCount, password: "test", requestSeat: true);
            using var first = Join("first"); using var second = Join("second");
            Wait(() => first.Completion.IsFaulted && second.ResumeCredential is not null || second.Completion.IsFaulted && first.ResumeCredential is not null, host);
            var admitted = first.Completion.IsFaulted ? second : first;
            Check(admitted.Player == 3 && admitted.ResumeCredential!.Player == 3 && host.State.Slots[2].Connected);
        });
        foreach (int count in new[] { 2, 4, 8 })
            test($"NET5 signed capacity bootstraps {count} sides including AI and closed slots", () => {
                using var service = new Service(); using var owner = new MatchDirectoryClient(service.Url, "Host");
                using var player = new MatchDirectoryClient(service.Url, "Player"); using var observer = new MatchDirectoryClient(service.Url, "Observer");
                owner.ConnectAsync().GetAwaiter().GetResult();
                var d = new MatchDescriptor("net5", "content", "capacity", 42, EndTick: 0); int port = Port();
                SimulationWorld World() { var w = new SimulationWorld([new("unit", 100)], 65536, 65536, 42); for (uint p = 1; p <= count; p++) w.AddInitialUnit(0, new((int)p * 4096, 2048), p); return w; }
                using var host = new StarSession(true, "127.0.0.1", port, null, d, World, Path.Combine(service.Directory, "host"), playerCount: count,
                    aiPlayers: count > 2 ? [3] : [], closedPlayers: Enumerable.Range(4, Math.Max(0, count - 3)).Select(p => (uint)p).ToArray(), planAi: (_, _) => [], admissionPolicy: new(owner.Info!.SigningKey));
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                var game = Game(host.State.Match) with { PlayerCount = count, Fingerprint = host.Fingerprint, Endpoints = [new(MatchEndpointKind.Direct, "127.0.0.1", port)] };
                var lease = owner.RegisterAsync(game).GetAwaiter().GetResult();
                Check(player.FindAsync("net5", "content").Result.Single().Game.PlayerCount == count);
                var grant = player.JoinAsync(game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult();
                Check(grant.PlayerCount == count);
                using (var wrong = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "wrong"),
                    admission: grant with { PlayerCount = count == 2 ? 4 : 2 }, accountIdentity: player.Identity))
                { Wait(() => wrong.Completion.IsCompleted, host); Check(wrong.Completion.IsFaulted && !host.State.Slots[1].Connected); }
                using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "guest"), admission: grant, accountIdentity: player.Identity);
                var watch = observer.JoinAsync(game.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator)).GetAwaiter().GetResult();
                using var spectator = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "watch"), player: 0, admission: watch, accountIdentity: observer.Identity);
                Wait(() => host.State.Slots[1].Connected && host.State.Spectators == 1 && guest.State.Revision == host.State.Revision, host, guest, spectator);
                Check(guest.State.Slots.Count == count && spectator.State.Slots.Count == count);
                host.Ready(host.State.Revision); guest.Ready(guest.State.Revision); Wait(() => host.State.Slots.Where(s => s.Controller == SlotController.Human).All(s => s.Ready), host, guest, spectator);
                host.Start(); Wait(() => Tick(guest) >= 8, host, guest, spectator); host.Stop();
                Wait(() => host.Completion.IsCompleted && guest.Completion.IsCompleted && spectator.Completion.IsCompleted, host, guest, spectator);
                foreach (var session in new[] { guest, spectator })
                { Check(session.Completion.Result == host.Completion.Result); Check(File.ReadAllBytes(session.JournalPath).SequenceEqual(File.ReadAllBytes(host.JournalPath))); }
                using var journal = File.OpenRead(host.JournalPath); Check(StarJournal.Replay(journal, d, World) == host.Completion.Result);
            });
        test("NET5 rejects missing capacity, count changes and unsigned grant tampering", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var directory = new MatchDirectory(key); using var identity = new DirectoryIdentity();
            var account = Login(directory, identity); var game = Game();
            foreach (var invalid in new[] { game with { PlayerCount = 0 }, game with { PlayerCount = 9 }, game with { AvailablePlayers = [3] }, game with { Protocol = 4 } })
                Reject(() => directory.Register(account.Token, invalid), "invalid_match");
            var lease = directory.Register(account.Token, game);
            Reject(() => directory.Heartbeat(account.Token, game.Match, lease.Token, game with { Revision = 2, PlayerCount = 4 }), "stale_revision");
            using var service = new Service(); using var owner = new MatchDirectoryClient(service.Url, "Host"); using var handler = new Faults(changeCount: true);
            using var client = new MatchDirectoryClient(service.Url, "Player", handler);
            var published = Game(); owner.RegisterAsync(published).GetAwaiter().GetResult();
            Reject(() => client.JoinAsync(published.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult());
        });
        test("NET5 retries transient Confirm and skips a cancelled receipt without blocking later admissions", () => {
            using var service = new Service(); using var faults = new Faults(failConfirm: true);
            using var owner = new MatchDirectoryClient(service.Url, "Host", faults); using var player = new MatchDirectoryClient(service.Url, "Player");
            using var cancelled = new MatchDirectoryClient(service.Url, "Cancelled"); using var observer = new MatchDirectoryClient(service.Url, "Observer");
            owner.ConnectAsync().GetAwaiter().GetResult(); int port = Port(); var d = new MatchDescriptor("net5", "content", "receipts", 42, EndTick: 0);
            SimulationWorld World() { var w = new SimulationWorld([new("unit", 100)], 16384, 16384, 42); w.AddInitialUnit(0, new(2048, 2048), 1); return w; }
            using var host = new StarSession(true, "127.0.0.1", port, null, d, World, Path.Combine(service.Directory, "host"), admissionPolicy: new(owner.Info!.SigningKey));
            Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
            using var publication = new DirectoryListing(owner, host, "Receipts", "127.0.0.1", port, () => "map");
            Wait(() => publication.Status.StartsWith("Игра опубликована", StringComparison.Ordinal), host);
            var grant = player.JoinAsync(host.State.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult();
            using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "guest"), admission: grant, accountIdentity: player.Identity);
            Wait(() => host.State.Slots[1].Connected, host, guest);
            var old = cancelled.JoinAsync(host.State.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator)).GetAwaiter().GetResult();
            cancelled.CancelAsync(old.Match, old.Request).GetAwaiter().GetResult();
            using var first = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "first"), player: 0, admission: old, accountIdentity: cancelled.Identity);
            Wait(() => host.State.Spectators == 1, host, guest, first);
            var next = observer.JoinAsync(host.State.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator)).GetAwaiter().GetResult();
            using var second = new StarSession(false, "127.0.0.1", port, host.Fingerprint, d, World, Path.Combine(service.Directory, "second"), player: 0, admission: next, accountIdentity: observer.Identity);
            Wait(() => host.State.Spectators == 2 && guest.State.Revision == host.State.Revision, host, guest, first, second);
            host.Ready(host.State.Revision); guest.Ready(guest.State.Revision); Wait(() => host.State.Slots.All(s => s.Ready), host, guest, first, second); host.Start();
            Wait(() => publication.Status.StartsWith("Каталог недоступен", StringComparison.Ordinal), host, guest, first, second);
            long tick = Tick(host); Wait(() => Tick(host) >= tick + 8, host, guest, first, second);
            Wait(() => publication.Status.StartsWith("Игра опубликована", StringComparison.Ordinal), host, guest, first, second);
            var state = player.FindAsync("net5", "content").Result.Single(); Check(state.ReservedPlayers == 0 && state.ReservedSpectators == 1);
            var attempts = faults.Confirmed.ToArray(); Check(attempts.Length == 4 && attempts[0] == attempts[1] && attempts.Distinct().Count() == 3);
            host.Stop(); Wait(() => host.Completion.IsCompleted && guest.Completion.IsCompleted && first.Completion.IsCompleted && second.Completion.IsCompleted, host, guest, first, second);
            using var journal = File.OpenRead(host.JournalPath); Check(StarJournal.Replay(journal, d, World) == host.Completion.Result);
        });
        test("NET5 tickets cannot outlive a lease or overlap re-registration after Close", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var hostKey = new DirectoryIdentity(); using var oneKey = new DirectoryIdentity(); using var twoKey = new DirectoryIdentity();
            var owner = Login(service, hostKey); var one = Login(service, oneKey); var two = Login(service, twoKey);
            var gate = new MatchAdmissionGate(new(service.Info.SigningKey), clock); var game = Game(); var lease = service.Register(owner.Token, game);
            clock.Advance(29);
            var first = service.Join(one.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            Check(first.Expires == lease.Expires && gate.Inspect(first.Ticket).Expires == lease.Expires);
            clock.Advance(2); lease = service.Register(owner.Token, game);
            var second = service.Join(two.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            Reject(() => gate.Inspect(first.Ticket)); Check(gate.Inspect(second.Ticket).Player == 2);
            service.Close(owner.Token, game.Match, lease.Token);
            Check(service.Find(game.Rules, game.ContentHash).Length == 0);
            Reject(() => service.Join(one.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "match_expired");
            Reject(() => service.Heartbeat(owner.Token, game.Match, lease.Token, game), "lease_invalid");
            Reject(() => service.Register(owner.Token, game), "already_registered");
            Check(gate.Inspect(second.Ticket).Player == 2); // Close cannot revoke an offline capability.
            clock.Advance(30); Reject(() => gate.Inspect(second.Ticket)); service.Register(owner.Token, game);
            Check(service.Join(one.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).Player == 2);
            Reject(() => service.Register(owner.Token, Game(hidden: true)), "private_unsupported");
        });
        test("NET5 key-bound session, challenge replay, expiry and lease ownership", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var first = new DirectoryIdentity(); using var other = new DirectoryIdentity();
            var challenge = service.Challenge(new("Player", first.PublicKey));
            Reject(() => service.Login(new(challenge.Id, other.SignLogin(challenge))), "login_rejected");
            Reject(() => service.Login(new(challenge.Id, first.SignLogin(challenge))), "login_rejected");
            var session = Login(service, first); var stranger = Login(service, other); var game = Game(); var lease = service.Register(session.Token, game);
            Reject(() => service.Heartbeat(stranger.Token, game.Match, lease.Token, game), "lease_invalid");
            Reject(() => service.Close(session.Token, game.Match, new string('0', 64)), "lease_invalid");
            service.Heartbeat(session.Token, game.Match, lease.Token, game);
            clock.Advance(31); Check(service.Find("net5", "content").Length == 0);
            Reject(() => service.Join(session.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "match_expired");
            var active = Game(); var activeLease = service.Register(session.Token, active);
            service.Close(session.Token, active.Match, activeLease.Token); Check(service.Find("net5", "content").Length == 0);
            clock.Advance(3600); Reject(() => service.Register(session.Token, Game()), "session_expired");
            Check(!session.ToString().Contains(session.Token) && !lease.ToString().Contains(lease.Token), "Credentials escaped ToString");
        });
        test("NET5 atomic last seat, idempotency, cancellation, public locked room and stale heartbeat", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var ownerKey = new DirectoryIdentity(); using var oneKey = new DirectoryIdentity(); using var twoKey = new DirectoryIdentity();
            var owner = Login(service, ownerKey); var one = Login(service, oneKey); var two = Login(service, twoKey);
            var game = Game(); var lease = service.Register(owner.Token, game);
            Guid request = Guid.NewGuid(); int admitted = 0; MatchJoinGrant? grant = null;
            Parallel.ForEach(new[] { one, two }, account => {
                try { var value = service.Join(account.Token, game.Match, new(request, MatchAccessRole.Player)); Interlocked.Increment(ref admitted); grant = value; }
                catch (MatchDirectoryException e) when (e.Code is "slot_reserved" or "request_conflict") { }
            });
            Check(admitted == 1 && grant is not null); var winner = AdmissionTicket.Verify(service.Info.SigningKey, grant!.Ticket, clock.GetUtcNow()).Account == one.Account.Id ? one : two;
            Check(service.Join(winner.Token, game.Match, new(request, MatchAccessRole.Player)).Ticket == grant.Ticket);
            service.Cancel(winner.Token, game.Match, request); Reject(() => service.Join(winner.Token, game.Match, new(request, MatchAccessRole.Player)), "ticket_expired");
            Reject(() => service.Join(winner.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "slot_reserved");
            clock.Advance(25); service.Heartbeat(owner.Token, game.Match, lease.Token, game); clock.Advance(6);
            Check(service.Join(winner.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).Player == 2);
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 2, Phase = MatchPhase.Paused });
            Reject(() => service.Heartbeat(owner.Token, game.Match, lease.Token, game), "stale_revision");
            Check(service.Find("other", "content").Length == 0);
            var locked = Game() with { PasswordRequired = true }; service.Register(owner.Token, locked);
            Check(service.Find("net5", "content").Single(m => m.Game.Match == locked.Match).Game.PasswordRequired);
        });
        test("NET5 confirmed seats and spectators wait for publication without changing a revision", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var service = new MatchDirectory(key);
            using var hostKey = new DirectoryIdentity(); using var playerKey = new DirectoryIdentity();
            var owner = Login(service, hostKey); var account = Login(service, playerKey); var game = Game(); var lease = service.Register(owner.Token, game);
            var grant = service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            var claims = AdmissionTicket.Verify(service.Info.SigningKey, grant.Ticket, DateTimeOffset.UtcNow);
            service.Confirm(owner.Token, game.Match, lease.Token, new(claims.Nonce, claims.Account));
            service.Confirm(owner.Token, game.Match, lease.Token, new(claims.Nonce, claims.Account));
            service.Heartbeat(owner.Token, game.Match, lease.Token, game);
            Check(service.Find("net5", "content").Single().ReservedPlayers == 1);
            Reject(() => service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "slot_reserved");
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 2, AvailablePlayers = [] });
            Check(service.Find("net5", "content").Single().ReservedPlayers == 0);
            for (int i = 0; i < 8; i++)
            {
                var watch = service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator));
                var observation = AdmissionTicket.Verify(service.Info.SigningKey, watch.Ticket, DateTimeOffset.UtcNow);
                service.Confirm(owner.Token, game.Match, lease.Token, new(observation.Nonce, observation.Account));
            }
            Reject(() => service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator)), "spectators_full");
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 3, AvailablePlayers = [], Spectators = 8 });
            Check(service.Find("net5", "content").Single().ReservedSpectators == 0);
        });
        test("NET5 host validates signature, role, match, account proof, expiry and one-time use", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var ownerKey = new DirectoryIdentity(); using var playerKey = new DirectoryIdentity(); using var attacker = new DirectoryIdentity();
            var owner = Login(service, ownerKey); var player = Login(service, playerKey); var game = Game(); service.Register(owner.Token, game);
            var grant = service.Join(player.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            var gate = new MatchAdmissionGate(new(service.Info.SigningKey), clock); string nonce = DirectoryIdentity.Secret();
            gate.CheckProof(grant.Ticket, nonce, playerKey.SignAdmission(grant.Ticket, nonce));
            Reject(() => gate.CheckProof(grant.Ticket, nonce, attacker.SignAdmission(grant.Ticket, nonce)));
            Reject(() => gate.CheckProof(grant.Ticket, DirectoryIdentity.Secret(), playerKey.SignAdmission(grant.Ticket, nonce)));
            Reject(() => gate.Redeem(grant.Ticket, Guid.NewGuid(), 2, game.Fingerprint, game.Rules, game.ContentHash, game.PlayerCount));
            Reject(() => gate.Redeem(grant.Ticket, game.Match, 0, game.Fingerprint, game.Rules, game.ContentHash, game.PlayerCount));
            Reject(() => gate.Redeem(grant.Ticket, game.Match, 2, game.Fingerprint, game.Rules, game.ContentHash, game.PlayerCount + 1));
            string corrupt = grant.Ticket[..^4] + "AAAA"; Reject(() => gate.Inspect(corrupt));
            Check(gate.Redeem(grant.Ticket, game.Match, 2, game.Fingerprint, game.Rules, game.ContentHash, game.PlayerCount).Account == player.Account.Id);
            Reject(() => gate.Redeem(grant.Ticket, game.Match, 2, game.Fingerprint, game.Rules, game.ContentHash, game.PlayerCount));
            clock.Advance(31); Reject(() => gate.Inspect(grant.Ticket));
        });
        test("NET5 HTTP directory, ticketed TLS fallback and service outage preserve replay", () => {
            using var service = new Service(); using var owner = new MatchDirectoryClient(service.Url, "Host"); using var account = new MatchDirectoryClient(service.Url, "Player");
            owner.ConnectAsync().GetAwaiter().GetResult(); account.ConnectAsync().GetAwaiter().GetResult();
            int port = Port(); var descriptor = new MatchDescriptor("net5", "content", "map", 42, EndTick: 0);
            SimulationWorld World() { var w = new SimulationWorld([new("unit", 100)], 16384, 16384, 42); w.AddInitialUnit(0, new(2048, 2048), 1); w.AddInitialUnit(0, new(4096, 2048), 2); return w; }
            using var host = new StarSession(true, "127.0.0.1", port, null, descriptor, World, Path.Combine(service.Directory, "host.journal"), admissionPolicy: new(owner.Info!.SigningKey));
            Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
            using var relay = new WanLink(port);
            var game = Game(host.State.Match) with { Fingerprint = host.Fingerprint, Endpoints = [new(MatchEndpointKind.Direct, "127.0.0.1", Port()), new(MatchEndpointKind.Relay, "127.0.0.1", relay.Port)] };
            using var publication = new DirectoryListing(owner, host, game.Title, "127.0.0.1", game.Endpoints[0].Port, () => game.Map, relay: game.Endpoints[1]);
            Wait(() => publication.Status.StartsWith("Игра опубликована", StringComparison.Ordinal), host);
            Check(account.FindAsync("net5", "content").Result.Single().Trust == "peer-hosted");
            var grant = account.JoinAsync(game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult();
            using (var unsigned = new StarSession(false, "127.0.0.1", port, host.Fingerprint, descriptor, World, Path.Combine(service.Directory, "unsigned.journal")))
            { Wait(() => unsigned.Completion.IsCompleted, host); Check(unsigned.Completion.IsFaulted && !host.State.Slots[1].Connected); }
            using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, descriptor, World, Path.Combine(service.Directory, "guest.journal"), admission: grant, accountIdentity: account.Identity);
            Wait(() => host.State.Slots[1].Connected && guest.State.Revision == host.State.Revision, host, guest);
            host.Ready(host.State.Revision); guest.Ready(guest.State.Revision); Wait(() => host.State.Slots.All(s => s.Ready), host, guest); host.Start();
            Wait(() => Tick(guest) >= 150, host, guest);
            Check(account.FindAsync("net5", "content").Result.Single() is { ReservedPlayers: 0, Game.Phase: MatchPhase.Playing });
            service.Stop(); long before = Tick(guest);
            Wait(() => publication.Status.StartsWith("Каталог недоступен", StringComparison.Ordinal), host, guest);
            Wait(() => Tick(guest) >= before + 16, host, guest); host.Stop(); Wait(() => host.Completion.IsCompleted && guest.Completion.IsCompleted, host, guest);
            Check(host.Completion.Result == guest.Completion.Result && relay.Bytes > 0);
            Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(guest.JournalPath)));
            using var log = File.OpenRead(guest.JournalPath); Check(StarJournal.Replay(log, descriptor, World) == host.Completion.Result);
            Check(!File.ReadAllText(host.JournalPath).Contains(grant.Ticket), "Ticket leaked to journal");
        });
        test("NET5 HTTP session renews after a directory restart with the same signing key", () => {
            using var service = new Service(); using var account = new MatchDirectoryClient(service.Url, "Host");
            account.ConnectAsync().GetAwaiter().GetResult(); string identity = account.Identity.AccountId;
            account.RegisterAsync(Game()).GetAwaiter().GetResult(); service.Restart();
            var game = Game(); Check(account.RegisterAsync(game).GetAwaiter().GetResult().Match == game.Match);
            Check(account.Identity.AccountId == identity && account.FindAsync("net5", "content").Result.Single().Game.Match == game.Match);
        });
        test("NET5 HTTP rejects malformed/oversized bodies and rate limits", () => {
            using var service = new Service(); using var http = new HttpClient();
            using var oversized = http.PostAsync(service.Url + "/v1/challenges", new StringContent(new string('x', 20000), Encoding.UTF8, "application/json")).Result;
            Check((int)oversized.StatusCode is 400 or 413);
            using var malformed = http.PostAsync(service.Url + "/v1/challenges", new StringContent("null", Encoding.UTF8, "application/json")).Result; Check(malformed.StatusCode == HttpStatusCode.BadRequest);
            bool limited = false;
            for (int i = 0; i < 125; i++) { using var response = http.GetAsync(service.Url + "/v1/info").Result; if ((int)response.StatusCode == 429) limited = true; }
            Check(limited);
        });
    }
}
