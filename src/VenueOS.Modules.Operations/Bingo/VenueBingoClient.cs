using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VenueOS.Modules.Operations.Bingo;

// Compatible with FFXIVBingo4All standalone commit 015d5d6, audited 2026-09-04. No trade or payout API exists here.
public sealed record BingoConnectionSettings(string ServerUrl = "", string BrowserUrl = "", string? AdminKey = null, string? RoomKey = null)
{ public BingoConnectionSettings WithoutSecrets() => this with { AdminKey = null, RoomKey = null }; }
public sealed record BingoColors(string? Bg = null, string? Card = null, string? Header = null, string? Text = null, string? Daub = null, string? Ball = null);
public sealed record BingoPlayer(string Name, int Count, string? ShortCode = null, int? PaidCount = null, int? CompCount = null);
public sealed record BingoProgressive(object? Value = null);
// Additive, docs/BINGO_V2_PROTOCOL.md §4: computed fresh on every read from the authoritative players map.
// Complimentary cards never appear in this formula — clients display it, never recompute pot arithmetic locally.
public sealed record BingoPot(int PaidCards, int CompCards, int TotalCards, int CurrentPot, int PrizePool);
// Additive, §8: attempts nested under an obligation via GET /api/v2/rooms/:roomCode; Attempts is null when this
// record is returned standalone from POST .../payouts (create/return-existing-open), which carries no attempt list.
public sealed record BingoPayoutAttempt(string AttemptId, int Amount, string Status, string? Note, long CreatedAt, long UpdatedAt);
public sealed record BingoPayoutObligation(string PayoutId, string WinnerSeed, string WinnerName, int TotalOwed, int ConfirmedPaid, int Outstanding, string Status, List<BingoPayoutAttempt>? Attempts = null);
// Additive (product correction, docs/BINGO_V2_PROTOCOL.md payouts/sync section): the backend-authoritative,
// de-duplicated-by-(seed,phase), chronologically-ordered (oldest first) list of players who have validly called
// Bingo for the room's current phase — this is what VenueOS displays and drives payout sync from; the host never
// manually picks a winner from the roster. Field names/casing match server.js's getEligibleCallers() verbatim.
public sealed record BingoCaller(string Seed, string Name, long Timestamp, int? Phase);
public sealed record BingoHostSyncRequest(string RoomCode, string RoomKey, bool ClearBingoState, IReadOnlyList<int> CalledNumbers, Dictionary<string, int> AllowedCards, Dictionary<string, BingoPlayer> Players, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, BingoProgressive? Progressive, string Letters, string Title, string? Bg, string? Card, string? Header, string? Text, string? Daub, string? Ball);
public sealed record BingoHostSyncResponse(bool Ok, List<int> CalledNumbers, List<string> AllowedSeeds, Dictionary<string, int> AllowedCards, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string? GameTypeBase, string? DisplayGameType, BingoProgressive? Progressive, string? Letters, string? Title, BingoColors? Colors, Dictionary<string, BingoPlayer>? Players = null, BingoPot? Pot = null, string? Lifecycle = null);
public sealed record BingoCallNumberResponse(bool Ok, bool Added, List<int> CalledNumbers);
// Daubs wire shape (server.js session.daubs, confirmed against the `daub_update` Socket.IO handler and the v2
// claims validator): { [seed]: { [cardIndexAsString]: number[] } } — a plain array of DAUBED BALL NUMBERS per
// seed+card, never cell coordinates. Was previously typed as an opaque `Dictionary<string, object>` and never
// actually consumed anywhere on the VenueOS side (live-QA correction — host Card Viewer daub sync); now typed
// precisely so VenueBingoService can map it into VenueBingoActiveGame.Daubs.
public sealed record BingoRoomState(bool Ok, string RoomCode, List<int> CalledNumbers, List<string> AllowedSeeds, Dictionary<string, int> AllowedCards, Dictionary<string, BingoPlayer> Players, Dictionary<string, Dictionary<string, List<int>>>? Daubs, object? LastBingo, List<object> BingoCalls, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string? GameTypeBase, string? DisplayGameType, BingoProgressive? Progressive, string? Letters, string? Title, BingoColors? Colors, BingoPot? Pot = null, string? Lifecycle = null);
// Additive v2 surface, docs/BINGO_V2_PROTOCOL.md §6/§8. GET /api/v2/rooms/:roomCode and every v2 room-mutation
// endpoint (create/start/close/grant-cards) return this exact same "full authoritative snapshot" shape
// (server.js's buildRoomSnapshot) — one response record for all of them.
// BingoCallers/CurrentPrizePool/SplitAmount are additive (product correction — see BingoCaller's doc comment):
// present on every v2 snapshot response (buildRoomSnapshot), absent/defaulted only for a Legacy-lifecycle room read
// via the legacy GET /api/room-state fallback (BingoRoomState below deliberately does NOT carry them — a legacy
// room predates the caller/split concept entirely, see VenueBingoService.ApplyLegacySnapshot).
// BallsToBingo is additive (live-QA correction, docs/BINGO_V2_PROTOCOL.md): { [seed]: minimumAdditionalCalls } —
// present on every v2 snapshot (server.js's computeBallsToBingo), absent/defaulted for a Legacy-lifecycle room for
// the same reason BingoCallers/CurrentPrizePool/SplitAmount are (see below) — it is a v2-only concept.
public sealed record BingoV2RoomState(bool Ok, string RoomCode, string Lifecycle, List<int> CalledNumbers, List<string> AllowedSeeds, Dictionary<string, int> AllowedCards, Dictionary<string, BingoPlayer> Players, Dictionary<string, Dictionary<string, List<int>>>? Daubs, object? LastBingo, List<object> BingoCalls, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string? GameTypeBase, string? DisplayGameType, BingoProgressive? Progressive, string? Letters, string? Title, BingoColors? Colors, BingoPot Pot, List<BingoPayoutObligation> Payouts, List<BingoCaller>? BingoCallers = null, int CurrentPrizePool = 0, int SplitAmount = 0, Dictionary<string, int>? BallsToBingo = null);
public sealed record BingoV2CreateRoomRequest(string RoomCode, string RoomKey, string? VenueName, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, BingoProgressive? Progressive, string Letters, string? Bg, string? Card, string? Header, string? Text, string? Daub, string? Ball, string IdempotencyKey);
public sealed record BingoStartRoomRequest(string IdempotencyKey);
public sealed record BingoCloseRoomRequest(string IdempotencyKey);
public sealed record BingoGrantCardsRequest(string Seed, string? Name, string? ShortCode, int? PaidCount, int? CompCount, string IdempotencyKey);
public sealed record BingoCreatePayoutRequest(string WinnerSeed, string? WinnerName, int TotalOwed, string IdempotencyKey);
public sealed record BingoSyncPayoutsRequest(string IdempotencyKey);
public sealed record BingoCreateAttemptRequest(int Amount, string IdempotencyKey);
public sealed record BingoTransitionAttemptRequest(string Status, string? Note, string IdempotencyKey);
public sealed record BingoAttemptTransitionResponse(string AttemptId, string Status, string? Note, BingoPayoutObligation Obligation);
public sealed record BingoClaimRequest(string Seed, int CardIndex, string? Name);
public sealed record BingoClaimResponse(bool Ok, bool Validated, string? Pattern = null, string? Reason = null);
// UpdatedAt is epoch milliseconds (long), matching the backend's actual wire format exactly (server.js's
// `row.updated_at`) — NOT an ISO date string, so it must not be typed DateTimeOffset (System.Text.Json cannot parse
// a raw number into one by default; this was a latent, never-before-exercised bug in the pre-existing scaffold,
// caught when this reconstruction first actually populated a non-empty rooms list in a test).
public sealed record BingoRoomSummary(string RoomCode, int CalledNumbersCount, int AllowedSeedsCount, int AllowedCardsCount, int DaubPlayers, object? LastBingo, int BingoCallsCount, string GameType, long? UpdatedAt);
public sealed record BingoRoomsResponse(bool Ok, List<BingoRoomSummary> Rooms);
public sealed record BingoLinkRequest(string Seed, int Count, string? Letters = null, string? Player = null, string? Title = null, string? Room = null, string? Game = null, string? Bg = null, string? Card = null, string? Header = null, string? Text = null, string? Daub = null, string? Ball = null, string? Server = null);
public sealed record BingoLinkResponse(bool Ok, string Code);
// Additive (live-QA correction, docs/BINGO_V2_PROTOCOL.md §6): GET /api/links/lookup — Code/Count are both null
// when no existing link is found for the room+seed, which is a normal outcome, not an error.
public sealed record BingoLinkLookupResponse(bool Ok, string? Code, int? Count);
public sealed record BingoResult<T>(bool Success, T? Value = default, string? Error = null, HttpStatusCode? StatusCode = null)
{ public static BingoResult<T> Failed(string error, HttpStatusCode? status = null) => new(false, default, error, status); }

public sealed class VenueBingoClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Uri? NormalizeBaseUri(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var url = value.Trim().TrimEnd('/') + "/"; return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) ? uri : null; }
    public Task<BingoResult<BingoHostSyncResponse>> HostSyncAsync(BingoConnectionSettings settings, BingoHostSyncRequest request, CancellationToken ct) => SendAsync<BingoHostSyncResponse>(settings, HttpMethod.Post, "/api/host-sync", request, roomKey: request.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoCallNumberResponse>> CallNumberAsync(BingoConnectionSettings settings, string roomCode, int number, CancellationToken ct) => number is < 1 or > 75 ? Task.FromResult(BingoResult<BingoCallNumberResponse>.Failed("Bingo number must be from 1 to 75.")) : SendAsync<BingoCallNumberResponse>(settings, HttpMethod.Post, "/api/call-number", new { roomCode, number }, null, null, ct);
    public Task<BingoResult<BingoRoomState>> GetRoomStateAsync(BingoConnectionSettings settings, string roomCode, CancellationToken ct) => SendAsync<BingoRoomState>(settings, HttpMethod.Get, "/api/room-state?roomCode=" + Uri.EscapeDataString(roomCode), null, null, null, ct);
    public Task<BingoResult<BingoRoomsResponse>> ListRoomsAsync(BingoConnectionSettings settings, CancellationToken ct) => SendAsync<BingoRoomsResponse>(settings, HttpMethod.Get, "/api/rooms", null, settings.RoomKey, null, ct);
    public Task<BingoResult<BingoRoomsResponse>> ListAdminRoomsAsync(BingoConnectionSettings settings, CancellationToken ct) => SendAsync<BingoRoomsResponse>(settings, HttpMethod.Get, "/api/admin/rooms", null, null, settings.AdminKey, ct);
    public Task<BingoResult<BingoLinkResponse>> CreateBrowserLinkAsync(BingoConnectionSettings settings, BingoLinkRequest request, CancellationToken ct) => SendAsync<BingoLinkResponse>(settings, HttpMethod.Post, "/api/links", request, null, settings.AdminKey, ct);
    // Additive — see BingoLinkLookupResponse. Same trust boundary (Admin Key) as CreateBrowserLinkAsync above.
    public Task<BingoResult<BingoLinkLookupResponse>> FindPlayerLinkAsync(BingoConnectionSettings settings, string roomCode, string seed, CancellationToken ct) => SendAsync<BingoLinkLookupResponse>(settings, HttpMethod.Get, $"/api/links/lookup?room={Uri.EscapeDataString(roomCode)}&seed={Uri.EscapeDataString(seed)}", null, null, settings.AdminKey, ct);
    public Task<BingoResult<object>> CloseRoomAsync(BingoConnectionSettings settings, string roomCode, bool admin, CancellationToken ct) => SendAsync<object>(settings, HttpMethod.Post, admin ? "/api/admin/rooms/close" : "/api/rooms/close", new { roomCode }, admin ? null : settings.RoomKey, admin ? settings.AdminKey : null, ct);

    // --- v2, additive (docs/BINGO_V2_PROTOCOL.md §6). RoomKey is embedded in the create-room body (trust-on-first-
    // use, same as legacy host-sync) rather than sent as x-room-key; every other v2 room-scoped call sends
    // x-room-key like the legacy room-scoped routes. idempotencyKey is a field on the request record itself —
    // generated fresh per logical operation by the CALLER (VenueBingoService), never here. ---
    public Task<BingoResult<BingoV2RoomState>> CreateRoomV2Async(BingoConnectionSettings settings, BingoV2CreateRoomRequest request, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Post, "/api/v2/rooms", request, roomKey: null, adminKey: null, ct);
    public Task<BingoResult<BingoV2RoomState>> StartRoomV2Async(BingoConnectionSettings settings, string roomCode, BingoStartRoomRequest request, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/start", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoV2RoomState>> CloseRoomV2Async(BingoConnectionSettings settings, string roomCode, BingoCloseRoomRequest request, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/close", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoV2RoomState>> GetRoomV2Async(BingoConnectionSettings settings, string roomCode, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Get, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}", null, roomKey: null, adminKey: null, ct);
    public Task<BingoResult<BingoV2RoomState>> GrantCardsAsync(BingoConnectionSettings settings, string roomCode, BingoGrantCardsRequest request, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/cards", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoPayoutObligation>> CreatePayoutAsync(BingoConnectionSettings settings, string roomCode, BingoCreatePayoutRequest request, CancellationToken ct) => SendAsync<BingoPayoutObligation>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/payouts", request, roomKey: settings.RoomKey, adminKey: null, ct);
    // Product correction, docs/BINGO_V2_PROTOCOL.md payouts/sync section: the NORMAL host payout path — creates/
    // updates an obligation per backend-eligible caller at the backend-computed split, returning the full updated
    // snapshot (same shape as GetRoomV2Async). Never takes a winner seed or total owed from the caller.
    public Task<BingoResult<BingoV2RoomState>> SyncPayoutsAsync(BingoConnectionSettings settings, string roomCode, BingoSyncPayoutsRequest request, CancellationToken ct) => SendAsync<BingoV2RoomState>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/payouts/sync", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoPayoutAttempt>> CreatePayoutAttemptAsync(BingoConnectionSettings settings, string roomCode, string payoutId, BingoCreateAttemptRequest request, CancellationToken ct) => SendAsync<BingoPayoutAttempt>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/payouts/{Uri.EscapeDataString(payoutId)}/attempts", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoAttemptTransitionResponse>> TransitionPayoutAttemptAsync(BingoConnectionSettings settings, string roomCode, string payoutId, string attemptId, BingoTransitionAttemptRequest request, CancellationToken ct) => SendAsync<BingoAttemptTransitionResponse>(settings, HttpMethod.Patch, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/payouts/{Uri.EscapeDataString(payoutId)}/attempts/{Uri.EscapeDataString(attemptId)}", request, roomKey: settings.RoomKey, adminKey: null, ct);
    public Task<BingoResult<BingoClaimResponse>> SubmitClaimAsync(BingoConnectionSettings settings, string roomCode, BingoClaimRequest request, CancellationToken ct) => SendAsync<BingoClaimResponse>(settings, HttpMethod.Post, $"/api/v2/rooms/{Uri.EscapeDataString(roomCode)}/claims", request, roomKey: null, adminKey: null, ct);

    private async Task<BingoResult<T>> SendAsync<T>(BingoConnectionSettings settings, HttpMethod method, string path, object? body, string? roomKey, string? adminKey, CancellationToken ct)
    {
        var root = NormalizeBaseUri(settings.ServerUrl); if (root is null) return BingoResult<T>.Failed("Bingo server URL is not set."); if (roomKey is not null && string.IsNullOrWhiteSpace(roomKey)) return BingoResult<T>.Failed("Room key is required."); if (adminKey is not null && string.IsNullOrWhiteSpace(adminKey)) return BingoResult<T>.Failed("Admin key is required.");
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(root, path)); if (!string.IsNullOrWhiteSpace(roomKey)) request.Headers.Add("x-room-key", roomKey); if (!string.IsNullOrWhiteSpace(adminKey)) request.Headers.Add("x-admin-key", adminKey); if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false); var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Include the raw response body (e.g. {"error":"economics_locked"} on a 409) in the failure text — the
            // service layer's diagnostics need enough detail to distinguish "economics locked" from a generic
            // failure without the client having to know every possible error code the backend can return.
            if (!response.IsSuccessStatusCode) return BingoResult<T>.Failed(string.IsNullOrWhiteSpace(content) ? $"Bingo backend returned {(int)response.StatusCode}." : $"Bingo backend returned {(int)response.StatusCode}: {content}", response.StatusCode);
            if (typeof(T) == typeof(object)) return new(true, (T)(object)new object());
            var value = JsonSerializer.Deserialize<T>(content, Json); return value is null ? BingoResult<T>.Failed("Bingo backend response was invalid.") : new(true, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return BingoResult<T>.Failed("Request cancelled."); } catch (Exception) { return BingoResult<T>.Failed("Unable to complete Bingo backend request."); }
    }
}
