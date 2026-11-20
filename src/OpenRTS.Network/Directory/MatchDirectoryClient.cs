using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRTS.Network;

public sealed class MatchDirectoryClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _login = new(1);
    private readonly string _name;
    private AccountSession? _session;
    public DirectoryIdentity Identity { get; } = new();
    public static Uri ServiceAddress(string url) => DirectoryValidation.ServiceUri(url);
    public async Task<string> GetHostAddressAsync(CancellationToken cancellation = default)
    {
        string value = await Request<string>(HttpMethod.Get, "v1/host-address", null, null, cancellation);
        if (!System.Net.IPAddress.TryParse(value, out var observed)) throw new InvalidDataException("Каталог вернул неверный адрес.");
        if (!System.Net.IPAddress.IsLoopback(observed)) return observed.ToString();
        if (!_http.BaseAddress!.IsLoopback) throw new InvalidDataException("Каталог за прокси. Задайте AdvertiseHost в network.yaml.");
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address));
        return (local?.Address ?? observed).ToString();
    }
    public DirectoryInfo? Info { get; private set; }
    public MatchDirectoryClient(string url, string name) : this(url, name, null) { }
    internal MatchDirectoryClient(string url, string name, HttpMessageHandler? handler)
    {
        DirectoryValidation.Text(name, 40, "имя"); _name = name;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = DirectoryValidation.ServiceUri(url), Timeout = TimeSpan.FromSeconds(5) };
    }
    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        await _login.WaitAsync(cancellation);
        try
        {
            if (_session?.Expires > DateTimeOffset.UtcNow.AddSeconds(30)) return;
            var info = await Request<DirectoryInfo>(HttpMethod.Get, "v1/info", null, null, cancellation);
            if (info.Version != 1 || info.MatchProtocol != StarProtocol.Version) throw new InvalidDataException("Каталог использует другую версию протокола.");
            using var key = DirectoryIdentity.ReadKey(info.SigningKey);
            if (Info is not null && Info.SigningKey != info.SigningKey) throw new InvalidDataException("Ключ каталога изменился. Подключитесь заново.");
            Info = info;
            var challenge = await Request<AccountChallenge>(HttpMethod.Post, "v1/challenges", new AccountChallengeRequest(_name, Identity.PublicKey), null, cancellation);
            var session = await Request<AccountSession>(HttpMethod.Post, "v1/sessions", new AccountLogin(challenge.Id, Identity.SignLogin(challenge)), null, cancellation);
            if (session.Account is null || session.Account.Id != Identity.AccountId || session.Account.PublicKey != Identity.PublicKey || session.Token is not { Length: 64 } || !session.Token.All(Uri.IsHexDigit) ||
                session.Expires <= DateTimeOffset.UtcNow) throw new InvalidDataException("Некорректная учётная сессия каталога.");
            _session = session;
        }
        finally { _login.Release(); }
    }
    private async Task<T> Auth<T>(HttpMethod method, string path, object? data, CancellationToken cancellation, string? lease = null)
    {
        await ConnectAsync(cancellation); var session = _session!;
        try { return await Request<T>(method, path, data, session.Token, cancellation, lease); }
        catch (MatchDirectoryException error) when (error.Code == "session_expired")
        {
            Interlocked.CompareExchange(ref _session, null, session);
            await ConnectAsync(cancellation);
            return await Request<T>(method, path, data, _session!.Token, cancellation, lease);
        }
    }
    public async Task<DirectoryMatch[]> FindAsync(string rules, string content, CancellationToken cancellation = default)
    {
        var info = await Request<DirectoryInfo>(HttpMethod.Get, "v1/info", null, null, cancellation);
        if (info.Version != 1 || info.MatchProtocol != StarProtocol.Version) throw new InvalidDataException("Каталог использует другую версию протокола.");
        var games = await Request<DirectoryMatch[]>(HttpMethod.Get, "v1/matches?rules=" + Uri.EscapeDataString(rules) + "&content=" + Uri.EscapeDataString(content), null, null, cancellation);
        if (games.Length > 100) throw new InvalidDataException("Превышен размер каталога.");
        foreach (var game in games) DirectoryValidation.Advertisement(game.Game);
        return games;
    }
    public async Task<DirectoryLease> RegisterAsync(MatchAdvertisement game, CancellationToken cancellation = default)
    {
        var lease = await Auth<DirectoryLease>(HttpMethod.Post, "v1/matches", game, cancellation);
        if (lease.Match != game.Match || lease.Token is not { Length: 64 } || !lease.Token.All(Uri.IsHexDigit) ||
            game.Private && lease.Invite is not { Length: 97 }) throw new InvalidDataException("Некорректная регистрация каталога.");
        return lease;
    }
    public Task<object> HeartbeatAsync(DirectoryLease lease, MatchAdvertisement game, CancellationToken cancellation = default) => Auth<object>(HttpMethod.Post, $"v1/matches/{lease.Match:D}/heartbeat", game, cancellation, lease.Token);
    public Task<object> CloseAsync(DirectoryLease lease, CancellationToken cancellation = default) => Auth<object>(HttpMethod.Delete, $"v1/matches/{lease.Match:D}", null, cancellation, lease.Token);
    public async Task<MatchJoinGrant> JoinAsync(Guid match, MatchJoinRequest request, CancellationToken cancellation = default)
    {
        var grant = await Auth<MatchJoinGrant>(HttpMethod.Post, $"v1/matches/{match:D}/join", request, cancellation);
        var claims = AdmissionTicket.Verify(Info!.SigningKey, grant.Ticket, DateTimeOffset.UtcNow);
        if (grant.Match != match || grant.Request != request.Request || claims.Match != match || claims.Account != Identity.AccountId ||
            claims.PlayerCount != grant.PlayerCount || claims.Player != grant.Player || claims.Role != request.Role || grant.Role != request.Role || grant.Expires != claims.Expires || grant.Endpoints is null ||
            claims.Fingerprint != grant.Fingerprint || !claims.Endpoints.SequenceEqual(grant.Endpoints)) throw new InvalidDataException("Каталог вернул билет другой роли или identity.");
        return grant;
    }
    public Task<object> CancelAsync(Guid match, Guid request, CancellationToken cancellation = default) => Auth<object>(HttpMethod.Delete, $"v1/matches/{match:D}/reservations/{request:D}", null, cancellation);
    public Task<object> ConfirmAsync(DirectoryLease lease, MatchAdmissionReceipt receipt, CancellationToken cancellation = default) => Auth<object>(HttpMethod.Post, $"v1/matches/{lease.Match:D}/confirm", receipt, cancellation, lease.Token);
    private async Task<T> Request<T>(HttpMethod method, string path, object? data, string? token, CancellationToken cancellation, string? lease = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(5)); cancellation = deadline.Token;
        using var request = new HttpRequestMessage(method, path);
        if (data is not null) request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(data, DirectoryJson.Options));
        if (request.Content is not null) request.Content.Headers.ContentType = new("application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (lease is not null) request.Headers.Add("X-OpenRTS-Lease", lease);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Ответ каталога превышает лимит.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var body = new MemoryStream();
        byte[] buffer = new byte[8192]; int length;
        while ((length = await stream.ReadAsync(buffer, cancellation)) != 0)
        { if (body.Length + length > 1024 * 1024) throw new InvalidDataException("Ответ каталога превышает лимит."); body.Write(buffer, 0, length); }
        if (!response.IsSuccessStatusCode)
        {
            DirectoryError? error = null;
            try { error = JsonSerializer.Deserialize<DirectoryError>(body.GetBuffer().AsSpan(0, (int)body.Length), DirectoryJson.Options); } catch (JsonException) { }
            throw new MatchDirectoryException(error?.Code ?? "service_error", error?.Message ?? $"Каталог недоступен: HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        try { return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), DirectoryJson.Options) ?? throw new InvalidDataException("Пустой ответ каталога."); }
        catch (JsonException) { throw new InvalidDataException("Некорректный JSON-ответ каталога."); }
    }
    public void Dispose() { _http.Dispose(); Identity.Dispose(); }
}
