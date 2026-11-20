using System.Security.Cryptography;
using System.Text.Json;
using OpenRTS.Network;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:17773");
builder.Logging.ClearProviders(); // No request bodies, bearer/lease/invite credentials or ticket strings in logs.
builder.WebHost.ConfigureKestrel(options => { options.Limits.MaxRequestBodySize = 16384; options.Limits.MaxConcurrentConnections = 128; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); });
string? keyPath = builder.Configuration["signing-key"];
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
if (!string.IsNullOrWhiteSpace(keyPath))
{
    if (File.Exists(keyPath)) key.ImportFromPem(File.ReadAllText(keyPath));
    else
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var output = new FileStream(keyPath, options);
        using var writer = new StreamWriter(output); writer.Write(key.ExportECPrivateKeyPem());
    }
}
using var catalogue = new MatchDirectory(key);
var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
var rates = new Dictionary<string, (long At, int Count)>(); var rateGate = new object(); long globalAt = 0; int globalCount = 0;
app.Use(async (context, next) => {
    try
    {
        // Trust the connection address, not forwarding headers supplied by a caller.
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        long now = Environment.TickCount64;
        lock (rateGate)
        {
            if (now - globalAt >= 1000) { globalAt = now; globalCount = 0; }
            if (++globalCount > 200) throw new MatchDirectoryException("rate_limit", "Каталог занят. Повторите позже.", 429);
            if (rates.Count >= 4096) foreach (var expired in rates.Where(p => now - p.Value.At >= 60000).Select(p => p.Key).ToArray()) rates.Remove(expired);
            var rate = rates.GetValueOrDefault(ip);
            if (now - rate.At >= 60000) rate = (now, 0);
            if (rate.Count >= 120 || rates.Count >= 4096 && !rates.ContainsKey(ip)) throw new MatchDirectoryException("rate_limit", "Слишком много запросов. Повторите позже.", 429);
            rates[ip] = (rate.At, rate.Count + 1);
        }
        if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address))
            throw new MatchDirectoryException("https_required", "Удалённый каталог требует HTTPS.", 400);
        context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }
    catch (MatchDirectoryException error) { context.Response.StatusCode = error.Status; await context.Response.WriteAsJsonAsync(new DirectoryError(error.Code, error.Message), json); }
    catch (Exception error) when (error is JsonException or BadHttpRequestException or ArgumentException)
    { context.Response.StatusCode = error is BadHttpRequestException bad ? bad.StatusCode : 400; await context.Response.WriteAsJsonAsync(new DirectoryError("invalid_request", "Некорректный запрос каталога."), json); }
});
string Session(HttpContext c)
{
    string value = c.Request.Headers.Authorization.ToString();
    if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71) throw new MatchDirectoryException("session_required", "Требуется учётная сессия каталога.", 401);
    return value[7..];
}
string Lease(HttpContext c) => c.Request.Headers["X-OpenRTS-Lease"].ToString();
async Task<T> Body<T>(HttpContext c) => await JsonSerializer.DeserializeAsync<T>(c.Request.Body, json, c.RequestAborted) ?? throw new MatchDirectoryException("invalid_request", "Пустой запрос.");
app.MapGet("/v1/host-address", (HttpContext c) => {
    var address = c.Connection.RemoteIpAddress ?? throw new MatchDirectoryException("address_unknown", "Не удалось определить адрес хоста.");
    return Results.Json((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
});
app.MapGet("/v1/info", () => catalogue.Info);
app.MapPost("/v1/challenges", (Delegate)(async (HttpContext c) => catalogue.Challenge(await Body<AccountChallengeRequest>(c))));
app.MapPost("/v1/sessions", (Delegate)(async (HttpContext c) => catalogue.Login(await Body<AccountLogin>(c))));
app.MapDelete("/v1/sessions", (HttpContext c) => { catalogue.Logout(Session(c)); return new { ok = true }; });
app.MapGet("/v1/matches", (string rules, string content) => catalogue.Find(rules, content));
app.MapPost("/v1/matches", (Delegate)(async (HttpContext c) => catalogue.Register(Session(c), await Body<MatchAdvertisement>(c))));
app.MapPost("/v1/matches/{match:guid}/heartbeat", async (Guid match, HttpContext c) => { catalogue.Heartbeat(Session(c), match, Lease(c), await Body<MatchAdvertisement>(c)); return new { ok = true }; });
app.MapDelete("/v1/matches/{match:guid}", (Guid match, HttpContext c) => { catalogue.Close(Session(c), match, Lease(c)); return new { ok = true }; });
app.MapPost("/v1/matches/{match:guid}/join", async (Guid match, HttpContext c) => catalogue.Join(Session(c), match, await Body<MatchJoinRequest>(c)));
app.MapDelete("/v1/matches/{match:guid}/reservations/{request:guid}", (Guid match, Guid request, HttpContext c) => { catalogue.Cancel(Session(c), match, request); return new { ok = true }; });
app.MapPost("/v1/matches/{match:guid}/confirm", async (Guid match, HttpContext c) => { catalogue.Confirm(Session(c), match, Lease(c), await Body<MatchAdmissionReceipt>(c)); return new { ok = true }; });
Console.WriteLine("OPENRTS_DIRECTORY_PROTOCOL:1");
await app.RunAsync();
