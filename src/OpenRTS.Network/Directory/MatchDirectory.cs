using System.Security.Cryptography;

namespace OpenRTS.Network;

/// <summary>Bounded optional catalogue and access issuer. It never owns a simulation, tick or result.</summary>
public sealed class MatchDirectory : IDisposable
{
    private sealed record LoginChallenge(DirectoryAccount Account, AccountChallenge Value);
    private sealed record Session(DirectoryAccount Account, DateTimeOffset Expires);
    private sealed class Listing(string owner, string lease, MatchAdvertisement game, DateTimeOffset expires)
    {
        internal readonly string Owner = owner, Lease = lease;
        internal MatchAdvertisement Game = game;
        internal DateTimeOffset Expires = expires;
        internal readonly Dictionary<Guid, Reservation> Reservations = [];
    }
    private sealed class Reservation(string account, MatchJoinGrant grant, Guid nonce, long revision)
    {
        internal readonly string Account = account;
        internal readonly MatchJoinGrant Grant = grant;
        internal readonly Guid Nonce = nonce;
        internal readonly long Revision = revision;
        internal bool Used, Cancelled;
    }
    private readonly object _gate = new();
    private readonly ECDsa _signer;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, LoginChallenge> _challenges = [];
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<Guid, Listing> _matches = [];
    public DirectoryInfo Info { get; }
    public MatchDirectory(ECDsa signingKey, TimeProvider? clock = null)
    {
        _signer = ECDsa.Create(); _signer.ImportPkcs8PrivateKey(signingKey.ExportPkcs8PrivateKey(), out _);
        using var check = DirectoryIdentity.ReadKey(Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo()));
        _clock = clock ?? TimeProvider.System;
        Info = new(1, StarProtocol.Version, Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo()));
    }
    private DateTimeOffset Now => _clock.GetUtcNow();
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
    private static MatchDirectoryException Error(string code, string message, int status = 409) => new(code, message, status);
    private void Prune()
    {
        foreach (var key in _challenges.Where(p => p.Value.Value.Expires <= Now).Select(p => p.Key).ToArray()) _challenges.Remove(key);
        foreach (var key in _sessions.Where(p => p.Value.Expires <= Now).Select(p => p.Key).ToArray()) _sessions.Remove(key);
        foreach (var key in _matches.Where(p => p.Value.Expires <= Now && !p.Value.Reservations.Values.Any(Holds)).Select(p => p.Key).ToArray()) _matches.Remove(key);
        foreach (var listing in _matches.Values)
            foreach (var key in listing.Reservations.Where(p => p.Value.Grant.Expires.AddMinutes(2) <= Now).Select(p => p.Key).ToArray()) listing.Reservations.Remove(key);
    }
    private DirectoryAccount Account(string token)
    {
        Prune();
        if (token is null || token.Length != 64 || !_sessions.TryGetValue(Hash(token), out var session)) throw Error("session_expired", "Учётная сессия истекла. Войдите в каталог снова.", 401);
        return session.Account;
    }
    public AccountChallenge Challenge(AccountChallengeRequest request)
    {
        lock (_gate)
        {
            Prune(); DirectoryValidation.Text(request.Name, 40, "имя"); using var key = DirectoryIdentity.ReadKey(request.PublicKey);
            if (_challenges.Count >= 4096) throw Error("capacity", "Лимит ожидающих входов.", 429);
            var challenge = new AccountChallenge(Guid.NewGuid(), DirectoryIdentity.Secret(), Now.AddSeconds(30));
            _challenges.Add(challenge.Id, new(new(DirectoryIdentity.Id(request.PublicKey), request.Name, request.PublicKey), challenge));
            return challenge;
        }
    }
    public AccountSession Login(AccountLogin request)
    {
        lock (_gate)
        {
            Prune();
            if (!_challenges.Remove(request.Challenge, out var challenge) || !DirectoryIdentity.Verify(challenge.Account.PublicKey,
                    DirectoryIdentity.LoginBytes(challenge.Value), request.Signature)) throw Error("login_rejected", "Подтверждение учётной сессии отклонено.", 401);
            if (_sessions.Count >= 4096) throw Error("capacity", "Лимит учётных сессий.", 429);
            string token = DirectoryIdentity.Secret(); var expires = Now.AddHours(1);
            _sessions.Add(Hash(token), new(challenge.Account, expires)); return new(challenge.Account, token, expires);
        }
    }
    public void Logout(string token) { lock (_gate) { if (token is { Length: 64 }) _sessions.Remove(Hash(token)); } }
    public DirectoryLease Register(string session, MatchAdvertisement advertisement)
    {
        lock (_gate)
        {
            var account = Account(session); DirectoryValidation.Advertisement(advertisement);
            if (advertisement.Private) throw Error("private_unsupported", "Все игры публикуются. Для ограничения входа используйте пароль матча.");
            if (_matches.ContainsKey(advertisement.Match)) throw Error("already_registered", "Матч уже зарегистрирован.");
            if (_matches.Count >= 512 || _matches.Values.Count(m => m.Owner == account.Id) >= 4) throw Error("capacity", "Лимит опубликованных матчей.", 429);
            string token = DirectoryIdentity.Secret(); var expires = Now.AddSeconds(30);
            _matches.Add(advertisement.Match, new(account.Id, Hash(token), Copy(advertisement), expires));
            return new(advertisement.Match, token, null, expires);
        }
    }
    private Listing Owned(string session, Guid match, string lease)
    {
        var account = Account(session);
        if (!_matches.TryGetValue(match, out var listing) || listing.Expires <= Now || listing.Owner != account.Id || lease is null || lease.Length != 64 ||
            !DirectoryIdentity.SecretEquals(listing.Lease, Hash(lease))) throw Error("lease_invalid", "Регистрация матча истекла или недоступна.", 404);
        return listing;
    }
    public void Heartbeat(string session, Guid match, string lease, MatchAdvertisement advertisement)
    {
        lock (_gate)
        {
            var listing = Owned(session, match, lease); DirectoryValidation.Advertisement(advertisement);
            if (advertisement.Match != match || advertisement.PlayerCount != listing.Game.PlayerCount || advertisement.Revision < listing.Game.Revision || advertisement.Private != listing.Game.Private ||
                advertisement.HostAdmission != listing.Game.HostAdmission || advertisement.PasswordRequired != listing.Game.PasswordRequired || advertisement.Fingerprint != listing.Game.Fingerprint || advertisement.Rules != listing.Game.Rules || advertisement.ContentHash != listing.Game.ContentHash ||
                advertisement.Revision == listing.Game.Revision && !Same(advertisement, listing.Game)) throw Error("stale_revision", "Регистрация изменилась; устаревший heartbeat отклонён.");
            listing.Game = Copy(advertisement); listing.Expires = Now.AddSeconds(30);
        }
    }
    // Outstanding offline capabilities keep this Match ID reserved even after an explicit Close.
    public void Close(string session, Guid match, string lease) { lock (_gate) { Owned(session, match, lease).Expires = Now; Prune(); } }
    public DirectoryMatch[] Find(string rules, string content)
    {
        lock (_gate)
        {
            Prune(); DirectoryValidation.Text(rules, 128, "правила"); DirectoryValidation.Text(content, 128, "контент");
            return _matches.Values.Where(m => m.Expires > Now && m.Game.Rules == rules && m.Game.ContentHash == content)
                .OrderBy(m => m.Game.Title, StringComparer.Ordinal).ThenBy(m => m.Game.Match).Take(100).Select(Public).ToArray();
        }
    }
    private bool Active(Reservation r) => !r.Cancelled && !r.Used && r.Grant.Expires > Now;
    private bool Holds(Reservation r) => !r.Used && r.Grant.Expires > Now;
    private bool AwaitingPublication(Listing m, Reservation r) => r.Used && m.Game.Revision <= r.Revision;
    private DirectoryMatch Public(Listing listing) => new(Copy(listing.Game), listing.Expires,
        listing.Reservations.Values.Count(r => (Holds(r) || AwaitingPublication(listing, r)) && r.Grant.Role == MatchAccessRole.Player), listing.Reservations.Values.Count(r => (Holds(r) || AwaitingPublication(listing, r)) && r.Grant.Role == MatchAccessRole.Spectator));
    public MatchJoinGrant Join(string session, Guid match, MatchJoinRequest request)
    {
        lock (_gate)
        {
            var account = Account(session);
            if (!_matches.TryGetValue(match, out var listing) || listing.Expires <= Now) throw Error("match_expired", "Матч закрыт или heartbeat истёк.", 404);
            if (listing.Game.HostAdmission) throw Error("host_admission", "Место в этой игре выдаёт хост.");
            if (request.Request == Guid.Empty || !Enum.IsDefined(request.Role) || request.Player > 8 || request.Role == MatchAccessRole.Spectator && request.Player != 0)
                throw Error("invalid_join", "Некорректная роль или место.", 400);
            if (listing.Reservations.TryGetValue(request.Request, out var previous))
            {
                if (previous.Account != account.Id || previous.Grant.Role != request.Role || request.Player != 0 && previous.Grant.Player != request.Player)
                    throw Error("request_conflict", "Request ID уже использован для другого допуска.");
                if (previous.Cancelled || previous.Grant.Expires <= Now) throw Error("ticket_expired", "Резервация истекла. Повторите вход с новым request ID.", 410);
                return previous.Grant with { Endpoints = previous.Grant.Endpoints.ToArray() };
            }
            if (listing.Reservations.Count >= 256) throw Error("capacity", "Лимит запросов допуска к матчу.", 429);
            if (listing.Game.Phase != MatchPhase.Lobby && (request.Role == MatchAccessRole.Player || listing.Game.Phase != MatchPhase.Paused))
                throw Error("match_running", "Для нового игрока матч уже начат; поздний зритель ожидает общей паузы.");
            if (listing.Reservations.Values.Any(r => Active(r) && r.Account == account.Id)) throw Error("already_reserved", "У учётной записи уже есть активная резервация этого матча.");
            uint player = 0;
            if (request.Role == MatchAccessRole.Player)
            {
                var available = listing.Game.AvailablePlayers.Where(p => !listing.Reservations.Values.Any(r => (Holds(r) || AwaitingPublication(listing, r)) && r.Grant.Player == p)).Order().ToArray();
                player = request.Player == 0 ? available.FirstOrDefault() : request.Player;
                if (player == 0 || !available.Contains(player)) throw Error("slot_reserved", "Место занято или зарезервировано.");
            }
            else if (listing.Game.Spectators + listing.Reservations.Values.Count(r => (Holds(r) || AwaitingPublication(listing, r)) && r.Grant.Role == MatchAccessRole.Spectator) >= 8)
                throw Error("spectators_full", "Свободных мест для зрителей нет.");
            DateTimeOffset issued = Now;
            if (issued >= listing.Expires) throw Error("match_expired", "Матч закрыт или heartbeat истёк.", 404);
            Guid nonce = Guid.NewGuid(); DateTimeOffset expires = issued.AddSeconds(30) < listing.Expires ? issued.AddSeconds(30) : listing.Expires;
            var claims = new AdmissionClaims(2, nonce, match, account.Id, account.PublicKey, request.Role, player, listing.Game.PlayerCount, issued, expires,
                listing.Game.Protocol, listing.Game.Rules, listing.Game.ContentHash, listing.Game.Fingerprint, listing.Game.Endpoints.ToArray());
            var grant = new MatchJoinGrant(request.Request, match, listing.Game.PlayerCount, player, request.Role, AdmissionTicket.Sign(_signer, claims), expires,
                claims.Endpoints.ToArray(), claims.Fingerprint);
            listing.Reservations.Add(request.Request, new(account.Id, grant, nonce, listing.Game.Revision)); return grant with { Endpoints = grant.Endpoints.ToArray() };
        }
    }
    public void Cancel(string session, Guid match, Guid request)
    {
        lock (_gate)
        {
            var account = Account(session);
            if (_matches.TryGetValue(match, out var listing) && listing.Reservations.TryGetValue(request, out var reservation))
            { if (reservation.Account != account.Id) throw Error("forbidden", "Чужая резервация.", 403); if (!reservation.Used) reservation.Cancelled = true; }
        }
    }
    public void Confirm(string session, Guid match, string lease, MatchAdmissionReceipt receipt)
    {
        lock (_gate)
        {
            var listing = Owned(session, match, lease);
            var reservation = listing.Reservations.Values.FirstOrDefault(r => r.Nonce == receipt.Ticket && r.Account == receipt.Account);
            if (reservation is null || reservation.Cancelled) throw Error("reservation_missing", "Резервация допуска не найдена.", 404);
            reservation.Used = true;
            // Occupancy is held separately until a newer host revision; never rewrite a published revision.
        }
    }
    private static MatchAdvertisement Copy(MatchAdvertisement g) => g with { AvailablePlayers = g.AvailablePlayers.ToArray(), Endpoints = g.Endpoints.ToArray() };
    private static bool Same(MatchAdvertisement a, MatchAdvertisement b) => (a with { AvailablePlayers = b.AvailablePlayers, Endpoints = b.Endpoints }) == b &&
        a.AvailablePlayers.SequenceEqual(b.AvailablePlayers) && a.Endpoints.SequenceEqual(b.Endpoints);
    public void Dispose() { lock (_gate) _signer.Dispose(); }
}
