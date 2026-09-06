using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VenueOS.Modules.Operations.QuestionLibrary;

namespace VenueOS.Modules.Operations.Trivia;

// Compatible with the reconstructed Mair's Trivia backend (Phase 2: occurrences, Series, time bonus, dense-rank
// leaderboards, manual score adjustment, player removal). This is intentionally a host-side protocol layer.
// Player-facing projections never contain answer keys; that boundary is entirely backend-enforced.
// Product decision: Password and ServerAccessPassword are deliberately persisted in plain, readable form — the
// operator must be able to retrieve/share venue connection configuration later, with no "forgot password, no
// recovery" trap. This is a conscious tradeoff (anyone with access to the local Dalamud config file can read them)
// that the guide's usual "never persist a raw password" instinct does not apply to here. They must still never be
// written to DiagnosticsService/logs/exceptions — see DiagnosticsService.Redact and MairsTriviaService.RunAsync's
// error path, neither of which ever interpolates these fields.
public sealed record TriviaConnectionSettings(string BaseUrl = "", string? Username = null, string? Password = null, string? AccessToken = null, string? RefreshToken = null, string? ServerAccessPassword = null)
{
    /// <summary>Clears only the SESSION (sign-in-derived) secrets. Password/ServerAccessPassword are durable
    /// connection configuration, not session state — logging out must never force the operator to retype them.</summary>
    public TriviaConnectionSettings WithoutSessionTokens() => this with { AccessToken = null, RefreshToken = null };
}
public sealed record TriviaError(string Code, string Message);
public sealed record TriviaResult<T>(bool Success, T? Value = default, TriviaError? Error = null, HttpStatusCode? StatusCode = null)
{ public static TriviaResult<T> Failed(string code, string message, HttpStatusCode? status = null) => new(false, default, new(code, message), status); }

public sealed record TriviaHealthResponse(string Status, string Service, string ApiVersion, DateTimeOffset Timestamp);
public sealed record TriviaHostProfile(Guid Id, string Username, DateTimeOffset CreatedAt);
public sealed record TriviaLoginResponse(string AccessToken, string RefreshToken, TriviaHostProfile User);
public sealed record TriviaRefreshResponse(string AccessToken, string RefreshToken);
public sealed record TriviaScoringRequest(int CorrectPoints, int IncorrectPoints, int FirstCorrectBonus, bool AllowAnswerChange = false, int? SecondCorrectBonus = null, int? ThirdCorrectBonus = null, int TimeBonusMultiplier = 5);

/// <summary>An opaque, per-player, per-occurrence choice — never A/B/C/D codes or source answer indices.</summary>
public sealed record TriviaChoice(string Id, string Text);
public sealed record TriviaPlayerQuestion(Guid Id, string Question, IReadOnlyList<TriviaChoice> Choices, DateTimeOffset? ClosesAt, bool AnswerSubmitted, string? SelectedAnswerId); // deliberately no correct-answer field

// CorrectCount/IncorrectCount ride along on every leaderboard entry (Game AND Series). Answered is always derived
// client-side as CorrectCount + IncorrectCount — never a source-question-count denominator, since a player who
// joined late or missed a question is not "behind" in any meaningful sense.
public sealed record TriviaLeaderboardEntry(int Rank, Guid Id, string DisplayName, int Score, int CorrectCount, int IncorrectCount)
{
    public int Answered => CorrectCount + IncorrectCount;
}
public sealed record TriviaPlayerScore(Guid Id, string DisplayName, int Score, int CorrectCount, int IncorrectCount, bool Removed);
public sealed record TriviaCumulativePlayerScore(string DisplayName, int Score);
public sealed record TriviaFirstResponder(Guid PlayerId, string DisplayName);
public sealed record TriviaQuestionResult(Guid QuestionId, string CorrectAnswer, TriviaFirstResponder? FirstResponder);
public sealed record TriviaCloseResult(TriviaHostGameState Game, TriviaQuestionResult Result);
public sealed record TriviaGameCompleteResult(IReadOnlyList<TriviaLeaderboardEntry> Winners, IReadOnlyList<TriviaLeaderboardEntry> Standings, Guid? SeriesId, IReadOnlyList<TriviaLeaderboardEntry>? SeriesStandings);
public sealed record TriviaSeriesCompleteResult(IReadOnlyList<TriviaLeaderboardEntry> Champions, IReadOnlyList<TriviaLeaderboardEntry> Standings);

public sealed record TriviaHostGameState(
    Guid Id, string JoinCode, string PlayerUrl, string VenueName, string GameName, string State, TriviaScoringRequest Scoring,
    int QuestionTimeLimitSeconds, bool CumulativeScoring, IReadOnlyList<TriviaCumulativePlayerScore> CumulativePlayers,
    Guid? ActiveSetId, Guid? ActiveQuestionId, DateTimeOffset? ActiveQuestionClosesAt, IReadOnlyList<TriviaPlayerScore> Players,
    IReadOnlyList<TriviaLeaderboardEntry> Leaderboard, Guid? SeriesId, IReadOnlyList<TriviaLeaderboardEntry>? SeriesStandings, IReadOnlyList<Guid> AttachedSourceSetIds);

/// <summary>Summary projection only — never deserialize this endpoint's response as <see cref="TriviaHostGameState"/>.</summary>
public sealed record TriviaGameSummary(Guid Id, string JoinCode, string VenueName, string GameName, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<Guid> AttachedSourceSetIds);

public sealed record TriviaCreateGameRequest(string VenueName, string GameName, TriviaQuestionSet QuestionSet, string OrderingMode, TriviaScoringRequest Scoring, int QuestionTimeLimitSeconds, bool CumulativeScoring);
public sealed record TriviaQuestionSetAddRequest(TriviaQuestionSet QuestionSet, string OrderingMode);
public sealed record TriviaQuestionSetAddResponse(Guid GameSetId, bool Reused);
public sealed record TriviaScoreAdjustmentRequest(int Delta, string Reason);

public sealed record TriviaSeriesSummary(Guid Id, string Name, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? EndedAt);
public sealed record TriviaSeriesGameSummary(Guid Id, string GameName, string State, int SequenceOrdinal);
// Cumulative stats + removal state for the "Series Players" roster — every active participant, even one who
// hasn't played a Game yet (at 0/0/0), distinct from the ranked "Series Standings" projection (TriviaLeaderboardEntry).
public sealed record TriviaSeriesParticipant(Guid Id, string DisplayName, bool Removed, int Score, int CorrectCount, int IncorrectCount)
{
    public int Answered => CorrectCount + IncorrectCount;
}
// One PERSISTENT join code/link for the Series' entire lifetime — the same value from creation through every
// subsequent Start Next Game call. This, not any individual Game's own JoinCode/PlayerUrl, is what the operator
// should ever hand to players when a Series is involved.
public sealed record TriviaSeriesState(Guid Id, string Name, string State, string JoinCode, string PlayerUrl, Guid? CurrentGameId, IReadOnlyList<TriviaSeriesGameSummary> Games, IReadOnlyList<TriviaSeriesParticipant> Participants, IReadOnlyList<TriviaLeaderboardEntry> Standings);
public sealed record TriviaStartNextGameRequest(string VenueName, string GameName, TriviaQuestionSet QuestionSet, string OrderingMode, TriviaScoringRequest Scoring, int QuestionTimeLimitSeconds);

public sealed record TriviaWebSocketAuthentication(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("accessToken")] string AccessToken);

public static class TriviaWebSocketProtocol
{
    public const int ProtocolVersion = 1;
    public static TriviaWebSocketAuthentication CreateHostAuthentication(string accessToken) => new("authenticate", ProtocolVersion, accessToken);
    public static Uri? BuildUri(string? baseUrl)
    {
        var baseUri = MairsTriviaClient.NormalizeBaseUri(baseUrl);
        if (baseUri is null) return null;
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs, Path = "/v1/ws" };
        return builder.Uri;
    }
}

public sealed class MairsTriviaClient(HttpClient http)
{
    private const int MaxPayloadBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Uri? NormalizeBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimEnd('/') + "/";
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out uri) ? uri : null;
    }

    public Task<TriviaResult<TriviaHealthResponse>> HealthAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<TriviaHealthResponse>(settings, HttpMethod.Get, "/health", null, false, false, cancellationToken);
    public Task<TriviaResult<object>> ValidateAccessAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<object>(settings, HttpMethod.Post, "/v1/access/validate", new { }, false, true, cancellationToken);
    public Task<TriviaResult<TriviaLoginResponse>> RegisterAsync(TriviaConnectionSettings settings, string username, string password, CancellationToken cancellationToken) => SendAsync<TriviaLoginResponse>(settings, HttpMethod.Post, "/v1/auth/register", new { username, password }, false, true, cancellationToken);
    public Task<TriviaResult<TriviaLoginResponse>> LoginAsync(TriviaConnectionSettings settings, string username, string password, CancellationToken cancellationToken) => SendAsync<TriviaLoginResponse>(settings, HttpMethod.Post, "/v1/auth/login", new { username, password }, false, true, cancellationToken);
    public Task<TriviaResult<TriviaRefreshResponse>> RefreshAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<TriviaRefreshResponse>(settings, HttpMethod.Post, "/v1/auth/refresh", new { refreshToken = settings.RefreshToken }, false, false, cancellationToken);
    public Task<TriviaResult<object>> LogoutAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<object>(settings, HttpMethod.Post, "/v1/auth/logout", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostProfile>> GetProfileAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<TriviaHostProfile>(settings, HttpMethod.Get, "/v1/me", null, true, false, cancellationToken);

    public Task<TriviaResult<IReadOnlyList<TriviaGameSummary>>> GetGamesAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<IReadOnlyList<TriviaGameSummary>>(settings, HttpMethod.Get, "/v1/games", null, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> GetGameAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Get, $"/v1/games/{gameId}", null, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> CreateGameAsync(TriviaConnectionSettings settings, TriviaCreateGameRequest request, CancellationToken cancellationToken) => request.QuestionSet.IsValidHostSet() ? SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, "/v1/games", request, true, false, cancellationToken) : Task.FromResult(TriviaResult<TriviaHostGameState>.Failed("invalid_question_set", "Question set is not READY."));
    public Task<TriviaResult<TriviaQuestionSetAddResponse>> AddQuestionSetAsync(TriviaConnectionSettings settings, Guid gameId, TriviaQuestionSetAddRequest request, CancellationToken cancellationToken) => request.QuestionSet.IsValidHostSet() ? SendAsync<TriviaQuestionSetAddResponse>(settings, HttpMethod.Post, $"/v1/games/{gameId}/question-sets", request, true, false, cancellationToken) : Task.FromResult(TriviaResult<TriviaQuestionSetAddResponse>.Failed("invalid_question_set", "Question set is not READY."));
    public Task<TriviaResult<TriviaHostGameState>> SelectQuestionSetAsync(TriviaConnectionSettings settings, Guid gameId, Guid gameSetId, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/games/{gameId}/question-sets/{gameSetId}/select", new { }, true, false, cancellationToken);

    // Each host action now has its own precise response type — the donor scaffold's single method that deserialized
    // preview/skip/end acknowledgements as a full TriviaHostGameState is the exact protocol mismatch this replaces.
    public Task<TriviaResult<TriviaQuestion>> PreviewAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaQuestion>(settings, HttpMethod.Post, $"/v1/games/{gameId}/questions/preview", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> SkipAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/games/{gameId}/questions/skip", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> OpenAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/games/{gameId}/questions/open", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaCloseResult>> CloseAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaCloseResult>(settings, HttpMethod.Post, $"/v1/games/{gameId}/questions/close", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaQuestion>> RepeatQuestionAsync(TriviaConnectionSettings settings, Guid gameId, Guid questionId, CancellationToken cancellationToken) => SendAsync<TriviaQuestion>(settings, HttpMethod.Post, $"/v1/games/{gameId}/questions/{questionId}/repeat", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaGameCompleteResult>> EndAsync(TriviaConnectionSettings settings, Guid gameId, CancellationToken cancellationToken) => SendAsync<TriviaGameCompleteResult>(settings, HttpMethod.Post, $"/v1/games/{gameId}/end", new { }, true, false, cancellationToken);

    public Task<TriviaResult<TriviaHostGameState>> KickPlayerAsync(TriviaConnectionSettings settings, Guid gameId, Guid playerId, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/games/{gameId}/players/{playerId}/kick", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> AdjustScoreAsync(TriviaConnectionSettings settings, Guid gameId, Guid playerId, TriviaScoreAdjustmentRequest request, CancellationToken cancellationToken) => SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/games/{gameId}/players/{playerId}/score-adjustment", request, true, false, cancellationToken);

    public Task<TriviaResult<IReadOnlyList<TriviaSeriesSummary>>> GetSeriesListAsync(TriviaConnectionSettings settings, CancellationToken cancellationToken) => SendAsync<IReadOnlyList<TriviaSeriesSummary>>(settings, HttpMethod.Get, "/v1/series", null, true, false, cancellationToken);
    public Task<TriviaResult<TriviaSeriesState>> CreateSeriesAsync(TriviaConnectionSettings settings, string name, CancellationToken cancellationToken) => SendAsync<TriviaSeriesState>(settings, HttpMethod.Post, "/v1/series", new { name }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaSeriesState>> GetSeriesAsync(TriviaConnectionSettings settings, Guid seriesId, CancellationToken cancellationToken) => SendAsync<TriviaSeriesState>(settings, HttpMethod.Get, $"/v1/series/{seriesId}", null, true, false, cancellationToken);
    public Task<TriviaResult<TriviaHostGameState>> StartNextGameInSeriesAsync(TriviaConnectionSettings settings, Guid seriesId, TriviaStartNextGameRequest request, CancellationToken cancellationToken) => request.QuestionSet.IsValidHostSet() ? SendAsync<TriviaHostGameState>(settings, HttpMethod.Post, $"/v1/series/{seriesId}/games", request, true, false, cancellationToken) : Task.FromResult(TriviaResult<TriviaHostGameState>.Failed("invalid_question_set", "Question set is not READY."));
    public Task<TriviaResult<TriviaSeriesCompleteResult>> EndSeriesAsync(TriviaConnectionSettings settings, Guid seriesId, CancellationToken cancellationToken) => SendAsync<TriviaSeriesCompleteResult>(settings, HttpMethod.Post, $"/v1/series/{seriesId}/end", new { }, true, false, cancellationToken);
    public Task<TriviaResult<TriviaSeriesState>> RemoveFromSeriesAsync(TriviaConnectionSettings settings, Guid seriesId, Guid participantId, CancellationToken cancellationToken) => SendAsync<TriviaSeriesState>(settings, HttpMethod.Post, $"/v1/series/{seriesId}/participants/{participantId}/remove", new { }, true, false, cancellationToken);

    private async Task<TriviaResult<T>> SendAsync<T>(TriviaConnectionSettings settings, HttpMethod method, string path, object? body, bool bearer, bool serverPassword, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BaseUrl);
        if (baseUri is null) return TriviaResult<T>.Failed("configuration_required", "Backend base URL is not set.");
        if (bearer && string.IsNullOrWhiteSpace(settings.AccessToken)) return TriviaResult<T>.Failed("authentication_required", "A host session is required.");
        if (serverPassword && string.IsNullOrWhiteSpace(settings.ServerAccessPassword)) return TriviaResult<T>.Failed("access_password_required", "Server-access password is required.");
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
            if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
            if (serverPassword) request.Headers.Add("X-Server-Access-Password", settings.ServerAccessPassword);
            if (body is not null) { var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json); if (bytes.Length > MaxPayloadBytes) return TriviaResult<T>.Failed("payload_too_large", "Request exceeds the backend payload limit."); request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = TryError(text) ?? new TriviaError("backend_error", $"Backend returned {(int)response.StatusCode}.");
                return new(false, default, error, response.StatusCode);
            }
            if (typeof(T) == typeof(object)) return new(true, (T)(object)new object());
            T? value;
            try { value = JsonSerializer.Deserialize<T>(text, Json); }
            catch (JsonException) { return TriviaResult<T>.Failed("invalid_response", "Backend response did not match the expected contract.", response.StatusCode); }
            if (value is null || !RequiredFieldsPresent(value)) return TriviaResult<T>.Failed("invalid_response", "Backend returned an empty or incomplete response.", response.StatusCode);
            return new(true, value, null, response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return TriviaResult<T>.Failed("cancelled", "Request cancelled."); }
        catch (Exception) { return TriviaResult<T>.Failed("connection_failed", "Unable to complete the backend request."); }
    }

    /// <summary>
    /// System.Text.Json silently default-fills missing required properties on records with non-nullable value-type
    /// members (e.g. a missing "id" deserializes to Guid.Empty) rather than rejecting the payload. A response whose
    /// identity came back empty is treated as malformed rather than "successfully" adopted as current state — this
    /// is what prevented preview's/skip's/end's mismatched shapes from ever silently corrupting CurrentGame.Id.
    /// </summary>
    private static bool RequiredFieldsPresent<T>(T value) => value switch
    {
        TriviaHostGameState g => g.Id != Guid.Empty && !string.IsNullOrEmpty(g.JoinCode),
        TriviaQuestion q => q.Id != Guid.Empty,
        TriviaCloseResult c => c.Game.Id != Guid.Empty && c.Result.QuestionId != Guid.Empty,
        TriviaSeriesState s => s.Id != Guid.Empty,
        _ => true,
    };

    private static TriviaError? TryError(string text)
    {
        try { using var doc = JsonDocument.Parse(text); var error = doc.RootElement.TryGetProperty("error", out var e) ? e : default; return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && error.TryGetProperty("message", out var message) ? new(code.GetString() ?? "backend_error", message.GetString() ?? "Backend request failed.") : null; } catch (JsonException) { return null; }
    }
}
