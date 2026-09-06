using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VenueOS.Modules.Operations.Raffle;

// Compatible with standalone FFXIVRaffle4All BackendClient and backend/server.js, audited 2026-09-03.
public sealed record RaffleConnectionSettings(string BackendBaseUrl = "");
public sealed record RaffleSettings(float StartingPot = 0, float TicketCost = 0, float PrizePercentage = 100, int PaidTicketsForFree = 0, int FreeTicketsPerBlock = 0);
public sealed record RaffleParticipant(string Name, int PaidTickets = 0, int FreeTickets = 0);
public sealed record LocalRaffle(string Id, string Name, DateTime CreatedAt, RaffleSettings Settings, List<RaffleParticipant> Participants, string? ExternalId = null, string? HostUrl = null, string? ViewerUrl = null, string? WinnerName = null)
{
    public int TotalTickets => Participants.Sum(x => Math.Max(0, x.PaidTickets) + Math.Max(0, x.FreeTickets));
    public LocalRaffle WithoutSecrets() => this with { HostUrl = null, ViewerUrl = null };
}
public sealed record RaffleCreateRequest(string? RaffleId, string? Name, DateTime CreatedAt, RaffleSettings? Settings, List<RaffleParticipant>? Participants, List<string>? Tickets);
public sealed record RaffleCreateResponse(string? RaffleId, string? HostUrl, string? ViewerUrl, string? WinnerName);
public sealed record RaffleStateResponse(string? RaffleId, string? Name, string? WinnerName, List<string>? Tickets);
public sealed record RaffleClientResult<T>(bool Success, T? Value = default, string? Error = null, HttpStatusCode? StatusCode = null)
{ public static RaffleClientResult<T> Failed(string error, HttpStatusCode? status = null) => new(false, default, error, status); }

public sealed class VenueRaffleClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Uri? NormalizeBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null; var trimmed = value.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out uri) ? uri : null;
    }
    public static RaffleCreateRequest BuildCreateRequest(LocalRaffle raffle)
    {
        var tickets = raffle.Participants.SelectMany(p => Enumerable.Repeat(p.Name, Math.Max(0, p.PaidTickets) + Math.Max(0, p.FreeTickets))).ToList();
        return new(raffle.ExternalId ?? raffle.Id, raffle.Name, raffle.CreatedAt, raffle.Settings, raffle.Participants, tickets);
    }
    public async Task<RaffleClientResult<RaffleCreateResponse>> UpsertAsync(RaffleConnectionSettings settings, LocalRaffle raffle, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl); if (baseUri is null) return RaffleClientResult<RaffleCreateResponse>.Failed("Backend base URL is not set.");
        try { using var response = await http.PostAsJsonAsync(new Uri(baseUri, "/api/raffles"), BuildCreateRequest(raffle), Json, cancellationToken).ConfigureAwait(false); if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleCreateResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode); var body = await response.Content.ReadFromJsonAsync<RaffleCreateResponse>(Json, cancellationToken).ConfigureAwait(false); return body is null || string.IsNullOrWhiteSpace(body.HostUrl) || string.IsNullOrWhiteSpace(body.ViewerUrl) ? RaffleClientResult<RaffleCreateResponse>.Failed("Backend response missing host/viewer URLs.") : new(true, body); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleCreateResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleCreateResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }
    public async Task<RaffleClientResult<RaffleStateResponse>> FetchAsync(RaffleConnectionSettings settings, string raffleId, string? token, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(settings.BackendBaseUrl); if (baseUri is null) return RaffleClientResult<RaffleStateResponse>.Failed("Backend base URL is not set."); if (string.IsNullOrWhiteSpace(raffleId)) return RaffleClientResult<RaffleStateResponse>.Failed("Raffle ID is required.");
        try { var path = $"/api/raffles/{Uri.EscapeDataString(raffleId)}?token={Uri.EscapeDataString(token ?? string.Empty)}"; using var response = await http.GetAsync(new Uri(baseUri, path), cancellationToken).ConfigureAwait(false); if (!response.IsSuccessStatusCode) return RaffleClientResult<RaffleStateResponse>.Failed($"Backend error: {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode); var body = await response.Content.ReadFromJsonAsync<RaffleStateResponse>(Json, cancellationToken).ConfigureAwait(false); return body is null ? RaffleClientResult<RaffleStateResponse>.Failed("Backend response missing raffle state.") : new(true, body); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return RaffleClientResult<RaffleStateResponse>.Failed("Request cancelled."); }
        catch (Exception ex) { return RaffleClientResult<RaffleStateResponse>.Failed($"Backend request failed: {ex.Message}"); }
    }
}
