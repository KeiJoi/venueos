using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VenueOS.Services;

namespace VenueOS.Modules.Operations.Raffle;

// Compatible with the reconstructed backend/server.js (organizer access key, redraw/exclusion protocol,
// HomeWorld-aware ticket strings). See docs/RAFFLE_RECONSTRUCTION.md for the full protocol history.
public sealed record RaffleConnectionSettings(string BackendBaseUrl = "", string AccessKey = "");

public sealed record RaffleSettings(float StartingPot = 0, float TicketCost = 0, float PrizePercentage = 100, int PaidTicketsForFree = 0, int FreeTicketsPerBlock = 0);

/// <summary>A participant is identified by Name + optional HomeWorld, matching VenueOS's established
/// <see cref="GuestIdentity"/> convention rather than the donor's world-discarding name normalization. A null/blank
/// HomeWorld means a legacy Name-only entrant (e.g. imported from a donor-era XLSX) - it is never invented.</summary>
public sealed record RaffleParticipant(string Name, string? HomeWorld, int PaidTickets = 0, int FreeTickets = 0)
{
    /// <summary>The literal wire/ticket-pool value for this participant: "Name" for a legacy Name-only entrant,
    /// "Name@World" otherwise. This is what actually goes into the flattened ticket pool and what a winner name
    /// resolves back to - two different worlds' same-named characters get two distinct ticket keys.</summary>
    public string TicketKey => string.IsNullOrWhiteSpace(HomeWorld) ? Name : $"{Name}@{HomeWorld}";
    public string DisplayName => string.IsNullOrWhiteSpace(HomeWorld) ? Name : $"{Name} @ {HomeWorld}";
    public string IdentityKey => new GuestIdentity(Name, HomeWorld ?? string.Empty).Key;
}

public sealed record LocalRaffle(
    string Id,
    string Name,
    DateTime CreatedAt,
    RaffleSettings Settings,
    List<RaffleParticipant> Participants,
    List<string> ExcludedParticipants,
    string? ExternalId = null,
    string? HostUrl = null,
    string? ViewerUrl = null,
    string? WinnerName = null,
    string? PublishedTicketHash = null,
    bool IsArchived = false,
    // 0.3.0 short-link feature: the backend's opaque "/l/:code" alias for HostUrl/ViewerUrl (see
    // VenueRaffleClient.CreateShortLinkAsync and docs/RAFFLE_RECONSTRUCTION.md's short-link section). Mirrors
    // Bingo's pattern — the long token remains the real credential; these are purely a shareable alias for it, so
    // they carry no additional secret and are cleared by WithoutSecrets() exactly like HostUrl/ViewerUrl.
    string? HostLinkCode = null,
    string? ViewerLinkCode = null)
{
    public int TotalPaidTickets => Participants.Sum(x => Math.Max(0, x.PaidTickets));
    public int TotalFreeTickets => Participants.Sum(x => Math.Max(0, x.FreeTickets));
    public int TotalTickets => TotalPaidTickets + TotalFreeTickets;
    public float RunningPot => Settings.StartingPot + Settings.TicketCost * TotalPaidTickets;
    public float PrizePot => RunningPot * Math.Clamp(Settings.PrizePercentage, 0, 100) / 100f;
    public float HouseTake => RunningPot - PrizePot;

    /// <summary>The flattened ticket pool this raffle would publish right now - the same "one entry per paid or
    /// free ticket" expansion the donor uses, minus anyone already excluded by a confirmed redraw.</summary>
    public List<string> BuildTickets() => Participants
        .Where(p => !ExcludedParticipants.Contains(p.TicketKey))
        .SelectMany(p => Enumerable.Repeat(p.TicketKey, Math.Max(0, p.PaidTickets) + Math.Max(0, p.FreeTickets)))
        .ToList();

    public string CurrentTicketHash => VenueRaffleClient.HashTickets(BuildTickets());

    /// <summary>True once this raffle has been published at least once and its local ticket pool has since
    /// diverged from what the backend was last told - the "Unpublished Changes" indicator the operator needs
    /// instead of having to remember whether they clicked Publish.</summary>
    public bool HasUnpublishedChanges => ExternalId is not null && PublishedTicketHash != CurrentTicketHash;

    public string? HostToken => ExtractToken(HostUrl);
    public string? ViewerToken => ExtractToken(ViewerUrl);

    public LocalRaffle WithoutSecrets() => this with { HostUrl = null, ViewerUrl = null, HostLinkCode = null, ViewerLinkCode = null };

    /// <summary>The short "/l/:code" form of the host/viewer link when one has been minted, formed the same way
    /// Bingo's VenueBingoService.BuildBrowserUrl does (base URL + "/l/" + code) — falls back to the long URL when
    /// no short code exists yet, so callers never need a separate "is there a short link" branch.</summary>
    public string DisplayHostUrl(string? backendBaseUrl) => ShortUrlOrFallback(backendBaseUrl, HostLinkCode, HostUrl);
    public string DisplayViewerUrl(string? backendBaseUrl) => ShortUrlOrFallback(backendBaseUrl, ViewerLinkCode, ViewerUrl);

    private static string ShortUrlOrFallback(string? backendBaseUrl, string? code, string? longUrl)
    {
        if (!string.IsNullOrWhiteSpace(code) && VenueRaffleClient.NormalizeBaseUri(backendBaseUrl) is { } baseUri)
            return baseUri.ToString().TrimEnd('/') + "/l/" + code;
        return longUrl ?? "";
    }

    private static string? ExtractToken(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }
}

public sealed record RaffleCreateRequest(string? RaffleId, string? Name, DateTime CreatedAt, RaffleSettings? Settings, List<RaffleParticipant>? Participants, List<string>? Tickets, bool ClearExclusions = false);
public sealed record RaffleCreateResponse(string? RaffleId, string? HostUrl, string? ViewerUrl, string? WinnerName);
public sealed record RaffleStateResponse(string? RaffleId, string? Name, string? WinnerName, List<string>? Tickets, bool HasWinner = false, List<string>? ExcludedTickets = null);
public sealed record RaffleDeleteResponse(string? RaffleId, bool Deleted);

// 0.3.0 short-link feature — mirrors BingoLinkRequest/BingoLinkResponse/BingoLinkLookupResponse's shape
// (VenueBingoClient.cs), against the backend's new POST /api/links / GET /api/links/lookup routes
// (backend/server.js, additive alongside Bingo's proven short_links pattern).
public sealed record RaffleLinkRequest(string RaffleId, string Role);
public sealed record RaffleLinkResponse(bool Ok, string Code);
public sealed record RaffleLinkLookupResponse(bool Ok, string? Code);

public sealed record RaffleClientResult<T>(bool Success, T? Value = default, string? Error = null, HttpStatusCode? StatusCode = null)
{
    public static RaffleClientResult<T> Failed(string error, HttpStatusCode? status = null) => new(false, default, error, status);
}

public sealed class VenueRaffleClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Uri? NormalizeBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri
            : Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out uri) ? uri : null;
    }

    /// <summary>Mirrors the backend's own hashTickets(): sort a copy of the ticket array, join with '|'. Used only
    /// as VenueOS's own local "has this changed since I last published?" fingerprint - the backend never returns
    /// its hash, so there is no cross-system exact-match requirement, only local before/after comparison.</summary>
    public static string HashTickets(IEnumerable<string> tickets) => string.Join('|', tickets.OrderBy(x => x, StringComparer.Ordinal));

    public static RaffleCreateRequest BuildCreateRequest(LocalRaffle raffle, bool clearExclusions = false) =>
        new(raffle.ExternalId ?? raffle.Id, raffle.Name, raffle.CreatedAt, raffle.Settings, raffle.Participants, raffle.BuildTickets(), clearExclusions);

    public async Task<RaffleClientResult<RaffleCreateResponse>> UpsertAsync(RaffleConnectionSettings settings, LocalRaffle raffle, CancellationToken cancellationToken, bool clearExclusions = false)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl);
        if (baseUri is null) return RaffleClientResult<RaffleCreateResponse>.Failed("Backend base URL is not set.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey)) return RaffleClientResult<RaffleCreateResponse>.Failed("Backend access key is not set.");
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/raffles"))
            {
                Content = JsonContent.Create(BuildCreateRequest(raffle, clearExclusions), options: Json),
            };
            httpRequest.Headers.Add("X-Access-Key", settings.AccessKey);
            using var response = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleCreateResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<RaffleCreateResponse>(Json, cancellationToken).ConfigureAwait(false);
            return body is null || string.IsNullOrWhiteSpace(body.HostUrl) || string.IsNullOrWhiteSpace(body.ViewerUrl)
                ? RaffleClientResult<RaffleCreateResponse>.Failed("Backend response missing host/viewer URLs.")
                : new(true, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleCreateResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleCreateResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }

    public async Task<RaffleClientResult<RaffleStateResponse>> FetchAsync(RaffleConnectionSettings settings, string raffleId, string? token, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl);
        if (baseUri is null) return RaffleClientResult<RaffleStateResponse>.Failed("Backend base URL is not set.");
        if (string.IsNullOrWhiteSpace(raffleId)) return RaffleClientResult<RaffleStateResponse>.Failed("Raffle ID is required.");
        try
        {
            var path = $"/api/raffles/{Uri.EscapeDataString(raffleId)}?token={Uri.EscapeDataString(token ?? string.Empty)}";
            using var response = await http.GetAsync(new Uri(baseUri, path), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleStateResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<RaffleStateResponse>(Json, cancellationToken).ConfigureAwait(false);
            return body is null ? RaffleClientResult<RaffleStateResponse>.Failed("Backend response missing raffle state.") : new(true, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleStateResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleStateResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }

    /// <summary>Permanently deletes the backend's copy of a published raffle. Organizer-only (access key
    /// required), matching the backend's DELETE /api/raffles/:id. A 404 is treated as success - the backend
    /// already agrees the raffle doesn't exist, which is exactly the caller's desired end state.</summary>
    public async Task<RaffleClientResult<bool>> DeleteAsync(RaffleConnectionSettings settings, string raffleId, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl);
        if (baseUri is null) return RaffleClientResult<bool>.Failed("Backend base URL is not set.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey)) return RaffleClientResult<bool>.Failed("Backend access key is not set.");
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUri, $"/api/raffles/{Uri.EscapeDataString(raffleId)}"));
            httpRequest.Headers.Add("X-Access-Key", settings.AccessKey);
            using var response = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(true, true);
            if (!response.IsSuccessStatusCode) return RaffleClientResult<bool>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            return new(true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<bool>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<bool>.Failed($"Backend request failed: {ex.Message}"); }
    }

    /// <summary>Mints a short "/l/:code" link for an already-published raffle's host or viewer URL. Organizer-only
    /// (access key required, matching every other write here). The backend never returns/accepts the long
    /// host/viewer token through this endpoint — only <paramref name="role"/> ("host" or "view") and the raffle
    /// id — so this call can never leak or duplicate the actual capability credential.</summary>
    public async Task<RaffleClientResult<RaffleLinkResponse>> CreateShortLinkAsync(RaffleConnectionSettings settings, string raffleId, string role, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl);
        if (baseUri is null) return RaffleClientResult<RaffleLinkResponse>.Failed("Backend base URL is not set.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey)) return RaffleClientResult<RaffleLinkResponse>.Failed("Backend access key is not set.");
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/links"))
            {
                Content = JsonContent.Create(new RaffleLinkRequest(raffleId, role), options: Json),
            };
            httpRequest.Headers.Add("X-Access-Key", settings.AccessKey);
            using var response = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleLinkResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<RaffleLinkResponse>(Json, cancellationToken).ConfigureAwait(false);
            return body is null || string.IsNullOrWhiteSpace(body.Code) ? RaffleClientResult<RaffleLinkResponse>.Failed("Backend response missing short-link code.") : new(true, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleLinkResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleLinkResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }

    /// <summary>Lets a resuming client rediscover an already-minted short link instead of churning a new code every
    /// time (matching Bingo's FindPlayerLinkAsync/GET /api/links/lookup rationale). Same trust boundary as
    /// <see cref="CreateShortLinkAsync"/>.</summary>
    public async Task<RaffleClientResult<RaffleLinkLookupResponse>> FindShortLinkAsync(RaffleConnectionSettings settings, string raffleId, string role, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl);
        if (baseUri is null) return RaffleClientResult<RaffleLinkLookupResponse>.Failed("Backend base URL is not set.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey)) return RaffleClientResult<RaffleLinkLookupResponse>.Failed("Backend access key is not set.");
        try
        {
            var path = $"/api/links/lookup?raffleId={Uri.EscapeDataString(raffleId)}&role={Uri.EscapeDataString(role)}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            httpRequest.Headers.Add("X-Access-Key", settings.AccessKey);
            using var response = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleLinkLookupResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<RaffleLinkLookupResponse>(Json, cancellationToken).ConfigureAwait(false);
            return body is null ? RaffleClientResult<RaffleLinkLookupResponse>.Failed("Backend response missing lookup result.") : new(true, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleLinkLookupResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleLinkLookupResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }
}
