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
// Error codes an operator can act on (Error.Code): configuration_required, session_expired (the saved organizer SESSION was
// rejected/expired — never the Organizer Key), invalid_credentials (Server Access Password / Organizer Key not accepted at
// sign-in), invalid_key (backend key-policy rejection at Create Organizer), rate_limited (HTTP 429; RetryAfter says how long),
// backend_unavailable (502/503/504 or unreachable), stale_tournament, cancelled, backend_error, invalid_response.
public sealed record TournamentResult<T>(bool Success, T? Value = default, TournamentApiError? Error = null, HttpStatusCode? StatusCode = null, TournamentControllerState? AuthoritativeState = null, TimeSpan? RetryAfter = null)
{ public static TournamentResult<T> Failed(string code, string message, HttpStatusCode? status = null, TimeSpan? retryAfter = null) => new(false, default, new(code, message), status, null, retryAfter); }
public sealed record TournamentSession(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record ControllerTournament(string Id, string PublicCode, string VenueName, string GameName, string TournamentName, DateTimeOffset EventDate, string Status, int Revision, int PlayerCount);
public sealed record TournamentCreateRequest(string VenueName, string GameName, string TournamentName, DateTimeOffset EventDate);
public sealed record TournamentCreateResponse(ControllerTournament Tournament, string PublicUrl);
public sealed record TournamentContestant(string Id, string DisplayName, int Seed, string Status);
public sealed record TournamentRound(string Id, int RoundNumber, string Name);
public sealed record TournamentMatch(string Id, string RoundId, int Position, string? Player1Id, string? Player2Id, string? WinnerId, string Status, string? NextWinnerMatchId, int? NextWinnerSlot);
public sealed record TournamentControllerState(ControllerTournament Tournament, List<TournamentContestant> Contestants, List<TournamentRound> Rounds, List<TournamentMatch> Matches);
public sealed record TournamentListResponse(List<ControllerTournament> Tournaments);
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

// Pure mirror of the donor's own correction safety gate (BracketService.correct()/descendants() in
// apps/server/src/domain/bracket-service.ts): a match's downstream impact is the linear forward chain reachable via
// NextWinnerMatchId (single elimination — each match feeds exactly one later match). Used client-side so the
// operator panel can decide, BEFORE ever sending a request, whether to show a plain confirmation or the explicit
// destructive "this clears completed downstream results" confirmation — the backend still independently refuses a
// non-rollback correction over a completed descendant (409 UNSAFE_CORRECTION) regardless of what the client decided.
public static class TournamentCorrectionAnalysis
{
    public static bool RequiresRollbackConfirmation(TournamentControllerState state, string matchId)
    {
        var byId = state.Matches.ToDictionary(m => m.Id);
        if (!byId.TryGetValue(matchId, out var match)) return false;
        var next = match.NextWinnerMatchId;
        while (next is not null && byId.TryGetValue(next, out var descendant))
        {
            if (descendant.Status == "COMPLETED") return true;
            next = descendant.NextWinnerMatchId;
        }
        return false;
    }
}

// Pure mirror of the backend's own delete safety gate (the new organizer-owned DELETE
// /api/controller/tournaments/:id route rejects ACTIVE with 400 INVALID_TOURNAMENT_STATE — see
// docs/api.md in the donor repository). Used so the browser can hide/disable Delete for an ACTIVE tournament
// before ever sending a request; the backend still independently enforces the same rule regardless.
public static class TournamentDeleteEligibility
{
    public static bool CanDelete(string status) => status != "ACTIVE";
}

public sealed class TournamentControlClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Uri? NormalizeBaseUri(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var valueWithSlash = value.Trim().TrimEnd('/') + "/"; return Uri.TryCreate(valueWithSlash, UriKind.Absolute, out var uri) ? uri : Uri.TryCreate("https://" + valueWithSlash, UriKind.Absolute, out uri) ? uri : null; }
    public Task<TournamentResult<object>> HealthAsync(TournamentConnectionSettings settings, CancellationToken ct) => SendAsync<object>(settings, HttpMethod.Get, "/health", null, false, ct);
    public Task<TournamentResult<TournamentSession>> CreateOrganizerAsync(TournamentConnectionSettings settings, CancellationToken ct) => SendAsync<TournamentSession>(settings, HttpMethod.Post, "/api/controller/organizers", new { serverAccessPassword = settings.ServerAccessPassword, userKey = settings.UserKey }, false, ct);
    public Task<TournamentResult<TournamentSession>> AuthenticateAsync(TournamentConnectionSettings settings, CancellationToken ct) => SendAsync<TournamentSession>(settings, HttpMethod.Post, "/api/controller/sessions", new { serverAccessPassword = settings.ServerAccessPassword, userKey = settings.UserKey }, false, ct);
    public Task<TournamentResult<TournamentListResponse>> ListTournamentsAsync(TournamentConnectionSettings settings, string? query, string? status, CancellationToken ct)
    {
        var path = "/api/controller/tournaments";
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add("q=" + Uri.EscapeDataString(query));
        if (!string.IsNullOrWhiteSpace(status)) parameters.Add("status=" + Uri.EscapeDataString(status));
        if (parameters.Count > 0) path += "?" + string.Join("&", parameters);
        return SendAsync<TournamentListResponse>(settings, HttpMethod.Get, path, null, true, ct);
    }
    public Task<TournamentResult<TournamentCreateResponse>> CreateAsync(TournamentConnectionSettings settings, TournamentCreateRequest request, CancellationToken ct) => SendAsync<TournamentCreateResponse>(settings, HttpMethod.Post, "/api/controller/tournaments", request, true, ct);
    public Task<TournamentResult<TournamentControllerState>> GetStateAsync(TournamentConnectionSettings settings, string id, CancellationToken ct) => SendAsync<TournamentControllerState>(settings, HttpMethod.Get, $"/api/controller/tournaments/{Uri.EscapeDataString(id)}/state", null, true, ct);
    public Task<TournamentResult<TournamentControllerState>> AddContestantAsync(TournamentConnectionSettings settings, string id, int expectedRevision, string name, CancellationToken ct) => MutationAsync(settings, id, "contestants", HttpMethod.Post, new { expectedRevision, displayName = name }, ct);
    public Task<TournamentResult<TournamentControllerState>> BulkAddContestantsAsync(TournamentConnectionSettings settings, string id, int expectedRevision, string bulkText, CancellationToken ct) => MutationAsync(settings, id, "contestants", HttpMethod.Post, new { expectedRevision, bulkText }, ct);
    public Task<TournamentResult<TournamentControllerState>> RenameContestantAsync(TournamentConnectionSettings settings, string id, string contestantId, int expectedRevision, string name, CancellationToken ct) => MutationAsync(settings, id, $"contestants/{Uri.EscapeDataString(contestantId)}", HttpMethod.Patch, new { expectedRevision, displayName = name }, ct);
    public Task<TournamentResult<TournamentControllerState>> RemoveContestantAsync(TournamentConnectionSettings settings, string id, string contestantId, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, $"contestants/{Uri.EscapeDataString(contestantId)}", HttpMethod.Delete, new { expectedRevision }, ct);
    public Task<TournamentResult<TournamentControllerState>> ReorderAsync(TournamentConnectionSettings settings, string id, int expectedRevision, IReadOnlyList<string> contestantIds, CancellationToken ct) => MutationAsync(settings, id, "seeds", HttpMethod.Put, new { expectedRevision, contestantIds }, ct);
    public Task<TournamentResult<TournamentControllerState>> RandomizeAsync(TournamentConnectionSettings settings, string id, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, "seeds/randomize", HttpMethod.Post, new { expectedRevision }, ct);
    public Task<TournamentResult<TournamentControllerState>> StartAsync(TournamentConnectionSettings settings, string id, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, "start", HttpMethod.Post, new { expectedRevision }, ct);
    public Task<TournamentResult<TournamentControllerState>> RecordWinnerAsync(TournamentConnectionSettings settings, string id, string matchId, int expectedRevision, string winnerId, CancellationToken ct) => MutationAsync(settings, id, $"matches/{Uri.EscapeDataString(matchId)}/result", HttpMethod.Post, new { expectedRevision, winnerId }, ct);
    // rollbackDownstream=false (the default correction path) is refused with a 409/UNSAFE by the backend the instant
    // any downstream match has already completed — see TournamentCorrectionAnalysis.RequiresRollbackConfirmation
    // above, which the operator panel uses to require an explicit, deliberate confirmation before ever sending
    // rollbackDownstream=true. This mirrors BracketService.correct()'s own safety contract (donor
    // apps/server/src/domain/bracket-service.ts).
    public Task<TournamentResult<TournamentControllerState>> CorrectAsync(TournamentConnectionSettings settings, string id, string matchId, int expectedRevision, string winnerId, bool rollbackDownstream, CancellationToken ct) => MutationAsync(settings, id, $"matches/{Uri.EscapeDataString(matchId)}/correction", HttpMethod.Post, new { expectedRevision, winnerId, rollbackDownstream }, ct);
    public Task<TournamentResult<TournamentControllerState>> CancelAsync(TournamentConnectionSettings settings, string id, int expectedRevision, CancellationToken ct) => MutationAsync(settings, id, "cancel", HttpMethod.Post, new { expectedRevision }, ct);
    // No expectedRevision/typed confirmation — deleting a non-ACTIVE tournament has no unsafe-stale-write case the
    // way a bracket mutation does (see the route's own comment in the donor's apps/server/src/app.ts). The backend
    // rejects ACTIVE with 400 INVALID_TOURNAMENT_STATE; TournamentDeleteEligibility mirrors that gate client-side.
    public Task<TournamentResult<object>> DeleteAsync(TournamentConnectionSettings settings, string id, CancellationToken ct) => SendAsync<object>(settings, HttpMethod.Delete, $"/api/controller/tournaments/{Uri.EscapeDataString(id)}", null, true, ct);
    private Task<TournamentResult<TournamentControllerState>> MutationAsync(TournamentConnectionSettings settings, string id, string action, HttpMethod method, object body, CancellationToken ct) => SendAsync<TournamentControllerState>(settings, method, $"/api/controller/tournaments/{Uri.EscapeDataString(id)}/{action}", body, true, ct);
    // Fallback when a 429 carries no usable Retry-After: long enough not to hammer, short enough to be useful. The backend's own
    // window is at most ten minutes (see docs/auth-rate-limit-investigation.md in the backend repository).
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(15);
    private static TournamentResult<T> RateLimited<T>(HttpResponseMessage response, string body)
    {
        var wait = ParseRetryAfter(response, body);
        var message = wait is { } known ? $"The server is rate limiting sign-in attempts. Try again in {FormatWait(known)}." : "The server is rate limiting sign-in attempts. Try again later.";
        return TournamentResult<T>.Failed("rate_limited", message, response.StatusCode, wait ?? DefaultRateLimitWait);
    }
    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response, string body)
    {
        TimeSpan? wait = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) wait = delta;
        else if (header?.Date is { } date) wait = date - DateTimeOffset.UtcNow;
        else
        {
            try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("retryAfterSeconds", out var seconds) && seconds.TryGetInt32(out var value)) wait = TimeSpan.FromSeconds(value); } catch (JsonException) { }
        }
        return wait is { } w ? TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(w.TotalSeconds), 1, MaxRateLimitWait.TotalSeconds)) : null;
    }
    public static string FormatWait(TimeSpan wait) => wait.TotalMinutes >= 1 ? $"{(int)wait.TotalMinutes}m {wait.Seconds:00}s" : $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s";
    // Backend key-policy messages are fixed, secret-free sentences; only the vocabulary is aligned with what the operator sees ("Organizer Key").
    private static TournamentApiError? ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return null;
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : null; var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (code is null) return null;
            message = string.IsNullOrWhiteSpace(message) ? "The Organizer Key was rejected." : message.Replace("user key", "Organizer Key", StringComparison.OrdinalIgnoreCase);
            return new(code, message.Length > 200 ? message[..200] : message);
        }
        catch (JsonException) { return null; }
    }
    private async Task<TournamentResult<T>> SendAsync<T>(TournamentConnectionSettings settings, HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        var baseUri = NormalizeBaseUri(settings.BaseUrl); if (baseUri is null) return TournamentResult<T>.Failed("configuration_required", "Tournament endpoint is not set.");
        if (authenticated && string.IsNullOrWhiteSpace(settings.AccessToken)) return TournamentResult<T>.Failed("session_expired", "No organizer session. Authenticate first.", HttpStatusCode.Unauthorized);
        if (authenticated && settings.SessionExpiresAt <= DateTimeOffset.UtcNow) return TournamentResult<T>.Failed("session_expired", "Organizer session expired.", HttpStatusCode.Unauthorized);
        try { using var request = new HttpRequestMessage(method, new Uri(baseUri, path)); if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken); if (body is not null) request.Content = JsonContent.Create(body, options: Json); using var response = await http.SendAsync(request, ct).ConfigureAwait(false); var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); if (response.StatusCode == HttpStatusCode.Conflict) return new(false, default, new("stale_tournament", "Tournament changed; authoritative state was requested."), response.StatusCode); if (response.StatusCode == HttpStatusCode.Unauthorized) return authenticated ? TournamentResult<T>.Failed("session_expired", "Organizer session expired.", response.StatusCode) : TournamentResult<T>.Failed("invalid_credentials", "The Server Access Password or Organizer Key was not accepted.", response.StatusCode); if (response.StatusCode == HttpStatusCode.TooManyRequests) return RateLimited<T>(response, text); if (!authenticated && response.StatusCode == HttpStatusCode.BadRequest && ReadError(text) is { Code: "INVALID_USER_KEY" } keyError) return TournamentResult<T>.Failed("invalid_key", keyError.Message, response.StatusCode); if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) return TournamentResult<T>.Failed("backend_unavailable", $"Tournament backend is unavailable ({(int)response.StatusCode}).", response.StatusCode); if (!response.IsSuccessStatusCode) return TournamentResult<T>.Failed("backend_error", $"Tournament backend returned {(int)response.StatusCode}.", response.StatusCode); if (typeof(T) == typeof(object)) return new(true, (T)(object)new object()); var value = JsonSerializer.Deserialize<T>(text, Json); return value is null ? TournamentResult<T>.Failed("invalid_response", "Tournament backend returned an invalid response.") : new(true, value); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return TournamentResult<T>.Failed("cancelled", "Request cancelled."); }
        catch (Exception) { return TournamentResult<T>.Failed("connection_failed", "Unable to complete tournament request."); }
    }
}
