using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VenueOS.Modules.Operations.Tournament;

// Compatible with TournamentControl standalone commit c29e984 / Dalamud 0.1.6, audited 2026-09-03.
public sealed record TournamentConnectionSettings(string BaseUrl = "", string? AccessToken = null, DateTimeOffset? SessionExpiresAt = null, string? ServerAccessPassword = null, string? UserKey = null)
{ public TournamentConnectionSettings WithoutSecrets() => this with { AccessToken = null, ServerAccessPassword = null, UserKey = null }; }
public sealed record TournamentApiError(string Code, string Message);
public sealed record TournamentResult<T>(bool Success, T? Value = default, TournamentApiError? Error = null, HttpStatusCode? StatusCode = null, TournamentControllerState? AuthoritativeState = null)
{ public static TournamentResult<T> Failed(string code, string message, HttpStatusCode? status = null) => new(false, default, new(code, message), status); }
public sealed record TournamentSession(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record ControllerTournament(string Id, string PublicCode, string VenueName, string GameName, string TournamentName, DateTimeOffset EventDate, string Status, int Revision, int PlayerCount);
public sealed record TournamentCreateRequest(string VenueName, string GameName, string TournamentName, DateTimeOffset EventDate);
public sealed record TournamentCreateResponse(ControllerTournament Tournament, string PublicUrl);
public sealed record TournamentContestant(string Id, string DisplayName, int Seed, string Status);
public sealed record TournamentRound(string Id, int RoundNumber, string Name);
public sealed record TournamentMatch(string Id, string RoundId, int Position, string? Player1Id, string? Player2Id, string? WinnerId, string Status, string? NextWinnerMatchId, int? NextWinnerSlot);
public sealed record TournamentControllerState(ControllerTournament Tournament, List<TournamentContestant> Contestants, List<TournamentRound> Rounds, List<TournamentMatch> Matches);
public sealed record TournamentEventMessage(int Version, string Type, string? TournamentCode, string? TournamentId, int? Revision, JsonElement? Data);
public sealed record TournamentEventDecision(bool ShouldRefetch, bool TokenExpired, string? Reason = null);

public static class TournamentEventProtocol
{
    public const int Version = 1;
    public static object Authenticate(string accessToken) => new { version = Version, type = "authenticate", data = new { accessToken } };
    public static object Subscribe(string tournamentCode, string? accessToken = null) { var data = new Dictionary<string, string> { ["tournamentCode"] = tournamentCode }; if (!string.IsNullOrWhiteSpace(accessToken)) data["accessToken"] = accessToken; return new { version = Version, type = "subscribe", data }; }
    public static Uri? SocketUri(string? url) { var baseUri = TournamentControlClient.NormalizeBaseUri(url); if (baseUri is null) return null; return new UriBuilder(baseUri) { Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = "/ws" }.Uri; }
    public static TournamentEventDecision Inspect(TournamentEventMessage message, int? knownRevision)
    {
        if (message.Version != Version) return new(true, false, "Unsupported event protocol version.");
        if (message.Type == "error" && message.Data is { } error && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.GetString() == "UNAUTHORIZED") return new(true, true, "Socket session expired.");
        return message.Revision is { } revision && knownRevision is { } known && revision > known + 1 ? new(true, false, "Tournament event revision gap.") : new(false, false);
    }
}

public sealed class TournamentControlClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Uri? NormalizeBaseUri(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var valueWithSlash = value.Trim().TrimEnd('/') + "/"; return Uri.TryCreate(valueWithSlash, UriKind.Absolute, out var uri) ? uri : Uri.TryCreate("https://" + valueWithSlash, UriKind.Absolute, out uri) ? uri : null; }
    public Task<TournamentResult<object>> HealthAsync(TournamentConnectionSettings settings, CancellationToken ct) => SendAsync<object>(settings, HttpMethod.Get, "/health", null, false, ct);
    public Task<TournamentResult<TournamentSession>> AuthenticateAsync(TournamentConnectionSettings settings, CancellationToken ct) => SendAsync<TournamentSession>(settings, HttpMethod.Post, "/api/controller/sessions", new { serverAccessPassword = settings.ServerAccessPassword, userKey = settings.UserKey }, false, ct);
    public Task<TournamentResult<TournamentCreateResponse>> CreateAsync(TournamentConnectionSettings settings, TournamentCreateRequest request, CancellationToken ct) => SendAsync<TournamentCreateResponse>(settings, HttpMethod.Post, "/api/controller/tournaments", request, true, ct);
    public Task<TournamentResult<TournamentControllerState>> GetStateAsync(TournamentConnectionSettings settings, string id, CancellationToken ct) => SendAsync<TournamentControllerState>(settings, HttpMethod.Get, $"/api/controller/tournaments/{Uri.EscapeDataString(id)}/state", null, true, ct);
    public Task<TournamentResult<TournamentControllerState>> AddContestantAsync(TournamentConnectionSettings settings, string id, int expectedRevision, string name, CancellationToken ct) => MutationAsync(settings, id, "contestants", HttpMethod.Post, new { expectedRevision, displayName = name }, ct);
    public Task<TournamentResult<TournamentControllerState>> StartAsync(TournamentConnectionSettings settings, string id, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, "start", HttpMethod.Post, new { expectedRevision }, ct);
    public Task<TournamentResult<TournamentControllerState>> RecordWinnerAsync(TournamentConnectionSettings settings, string id, string matchId, int expectedRevision, string winnerId, CancellationToken ct) => MutationAsync(settings, id, $"matches/{Uri.EscapeDataString(matchId)}/result", HttpMethod.Post, new { expectedRevision, winnerId }, ct);
    public Task<TournamentResult<TournamentControllerState>> CancelAsync(TournamentConnectionSettings settings, string id, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, "cancel", HttpMethod.Post, new { expectedRevision }, ct);
    private Task<TournamentResult<TournamentControllerState>> MutationAsync(TournamentConnectionSettings settings, string id, string action, HttpMethod method, object body, CancellationToken ct) => SendAsync<TournamentControllerState>(settings, method, $"/api/controller/tournaments/{Uri.EscapeDataString(id)}/{action}", body, true, ct);
    private async Task<TournamentResult<T>> SendAsync<T>(TournamentConnectionSettings settings, HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        var baseUri = NormalizeBaseUri(settings.BaseUrl); if (baseUri is null) return TournamentResult<T>.Failed("configuration_required", "Tournament endpoint is not set.");
        if (authenticated && (string.IsNullOrWhiteSpace(settings.AccessToken) || settings.SessionExpiresAt <= DateTimeOffset.UtcNow)) return TournamentResult<T>.Failed("session_expired", "Organizer session is required.", HttpStatusCode.Unauthorized);
        try { using var request = new HttpRequestMessage(method, new Uri(baseUri, path)); if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken); if (body is not null) request.Content = JsonContent.Create(body, options: Json); using var response = await http.SendAsync(request, ct).ConfigureAwait(false); var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); if (response.StatusCode == HttpStatusCode.Conflict) return new(false, default, new("stale_tournament", "Tournament changed; authoritative state was requested."), response.StatusCode); if (response.StatusCode == HttpStatusCode.Unauthorized) return TournamentResult<T>.Failed("session_expired", "Organizer session expired.", response.StatusCode); if (!response.IsSuccessStatusCode) return TournamentResult<T>.Failed("backend_error", $"Tournament backend returned {(int)response.StatusCode}.", response.StatusCode); if (typeof(T) == typeof(object)) return new(true, (T)(object)new object()); var value = JsonSerializer.Deserialize<T>(text, Json); return value is null ? TournamentResult<T>.Failed("invalid_response", "Tournament backend returned an invalid response.") : new(true, value); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return TournamentResult<T>.Failed("cancelled", "Request cancelled."); }
        catch (Exception) { return TournamentResult<T>.Failed("connection_failed", "Unable to complete tournament request."); }
    }
}
