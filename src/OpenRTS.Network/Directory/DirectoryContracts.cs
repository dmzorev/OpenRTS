using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRTS.Network;

public enum MatchAccessRole { Player, Spectator }
public enum MatchEndpointKind { Direct, Relay }
public sealed record MatchEndpoint(MatchEndpointKind Kind, string Address, int Port);
public sealed record DirectoryInfo(int Version, int MatchProtocol, string SigningKey);
public sealed record AccountChallengeRequest(string Name, string PublicKey);
public sealed record AccountChallenge(Guid Id, string Nonce, DateTimeOffset Expires);
public sealed record AccountLogin(Guid Challenge, string Signature);
public sealed record DirectoryAccount(string Id, string Name, string PublicKey);
public sealed record AccountSession(DirectoryAccount Account, string Token, DateTimeOffset Expires)
{ public override string ToString() => $"Account={Account.Id}, Expires={Expires}, Token=<redacted>"; }
public sealed record MatchAdvertisement(Guid Match, long Revision, int Protocol, int PlayerCount, string Rules, string ContentHash,
    string Title, string Map, string Fingerprint, MatchPhase Phase, uint[] AvailablePlayers, int Players, int Spectators,
    MatchEndpoint[] Endpoints, bool Private = false, string Mode = "client-lockstep", bool PasswordRequired = false, bool HostAdmission = true);
public sealed record DirectoryMatch(MatchAdvertisement Game, DateTimeOffset Expires, int ReservedPlayers, int ReservedSpectators)
{ public string Trust => "peer-hosted"; }
public sealed record DirectoryLease(Guid Match, string Token, string? Invite, DateTimeOffset Expires)
{ public override string ToString() => $"Match={Match}, Expires={Expires}, Credentials=<redacted>"; }
public sealed record MatchJoinRequest(Guid Request, MatchAccessRole Role, uint Player = 0, string? Invite = null)
{ public override string ToString() => $"Request={Request}, Role={Role}, Player={Player}, Invite=<redacted>"; }
public sealed record MatchJoinGrant(Guid Request, Guid Match, int PlayerCount, uint Player, MatchAccessRole Role, string Ticket,
    DateTimeOffset Expires, MatchEndpoint[] Endpoints, string Fingerprint)
{ public override string ToString() => $"Match={Match}, Player={Player}, Role={Role}, Ticket=<redacted>"; }
public sealed record MatchAdmissionReceipt(Guid Ticket, string Account);
public sealed record InviteRequest(string Invite)
{ public override string ToString() => "Invite=<redacted>"; }
public sealed record DirectoryError(string Code, string Message);
public sealed class MatchDirectoryException(string code, string message, int status = 400) : Exception(message)
{ public string Code { get; } = code; public int Status { get; } = status; }

internal static class DirectoryJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

internal static class DirectoryValidation
{
    internal static void Text(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new MatchDirectoryException("invalid_field", $"Некорректное поле: {name}.");
    }
    internal static void Hash(string? value, string name)
    { if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new MatchDirectoryException("invalid_field", $"Некорректный {name}."); }
    internal static void Endpoint(MatchEndpoint endpoint)
    {
        if (endpoint is null || !Enum.IsDefined(endpoint.Kind) || endpoint.Port is < 1 or > 65535 ||
            string.IsNullOrEmpty(endpoint.Address) || endpoint.Address.Length > 253 || Uri.CheckHostName(endpoint.Address) == UriHostNameType.Unknown ||
            IPAddress.TryParse(endpoint.Address, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast))
            throw new MatchDirectoryException("invalid_endpoint", "Нужен доступный адрес endpoint и порт1..65535.");
    }
    internal static void Advertisement(MatchAdvertisement game)
    {
        if (game is null || game.Match == Guid.Empty || game.Revision < 1 || game.Protocol != StarProtocol.Version ||
            game.Mode != "client-lockstep" || !Enum.IsDefined(game.Phase) || game.Phase == MatchPhase.Finished ||
            game.PlayerCount is < 2 or > 8 || game.Players < 1 || game.Players > game.PlayerCount || game.Spectators is < 0 or > 8 || game.AvailablePlayers is null ||
            game.Players + game.AvailablePlayers.Length > game.PlayerCount || game.AvailablePlayers.Any(p => p < 2 || p > game.PlayerCount) || game.AvailablePlayers.Distinct().Count() != game.AvailablePlayers.Length ||
            game.Endpoints is null || game.Endpoints.Length is < 1 or > 3 || game.Endpoints.Any(e => e is null) || game.Endpoints.Select(e => e.Kind).Distinct().Count() != game.Endpoints.Length)
            throw new MatchDirectoryException("invalid_match", "Некорректная версия, режим или состав матча.");
        Text(game.Title, 64, "название"); Text(game.Map, 128, "карта"); Text(game.Rules, 128, "правила");
        Text(game.ContentHash, 128, "контент"); Hash(game.Fingerprint, "отпечаток хоста");
        foreach (var endpoint in game.Endpoints) Endpoint(endpoint);
    }
    internal static Uri ServiceUri(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Каталог требует HTTPS; HTTP разрешён только на loopback.");
        return uri;
    }
}
