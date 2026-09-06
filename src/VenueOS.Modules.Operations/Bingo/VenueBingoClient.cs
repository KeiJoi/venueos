using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VenueOS.Modules.Operations.Bingo;

// Compatible with FFXIVBingo4All standalone commit 015d5d6, audited 2026-09-04. No trade or payout API exists here.
public sealed record BingoConnectionSettings(string ServerUrl = "", string BrowserUrl = "", string? AdminKey = null, string? RoomKey = null)
{ public BingoConnectionSettings WithoutSecrets() => this with { AdminKey = null, RoomKey = null }; }
public sealed record BingoColors(string? Bg = null, string? Card = null, string? Header = null, string? Text = null, string? Daub = null, string? Ball = null);
public sealed record BingoPlayer(string Name, int Count, string? ShortCode = null);
public sealed record BingoProgressive(object? Value = null);
public sealed record BingoHostSyncRequest(string RoomCode, string RoomKey, bool ClearBingoState, IReadOnlyList<int> CalledNumbers, Dictionary<string, int> AllowedCards, Dictionary<string, BingoPlayer> Players, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, BingoProgressive? Progressive, string Letters, string Title, string? Bg, string? Card, string? Header, string? Text, string? Daub, string? Ball);
public sealed record BingoHostSyncResponse(bool Ok, List<int> CalledNumbers, List<string> AllowedSeeds, Dictionary<string, int> AllowedCards, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string? GameTypeBase, string? DisplayGameType, BingoProgressive? Progressive, string? Letters, string? Title, BingoColors? Colors);
public sealed record BingoCallNumberResponse(bool Ok, bool Added, List<int> CalledNumbers);
public sealed record BingoRoomState(bool Ok, string RoomCode, List<int> CalledNumbers, List<string> AllowedSeeds, Dictionary<string, int> AllowedCards, Dictionary<string, BingoPlayer> Players, Dictionary<string, object>? Daubs, object? LastBingo, List<object> BingoCalls, int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string? GameTypeBase, string? DisplayGameType, BingoProgressive? Progressive, string? Letters, string? Title, BingoColors? Colors);
public sealed record BingoRoomSummary(string RoomCode, int CalledNumbersCount, int AllowedSeedsCount, int AllowedCardsCount, int DaubPlayers, object? LastBingo, int BingoCallsCount, string GameType, DateTimeOffset? UpdatedAt);
public sealed record BingoRoomsResponse(bool Ok, List<BingoRoomSummary> Rooms);
public sealed record BingoLinkRequest(string Seed, int Count, string? Letters = null, string? Player = null, string? Title = null, string? Room = null, string? Game = null, string? Bg = null, string? Card = null, string? Header = null, string? Text = null, string? Daub = null, string? Ball = null, string? Server = null);
public sealed record BingoLinkResponse(bool Ok, string Code);
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
    public Task<BingoResult<object>> CloseRoomAsync(BingoConnectionSettings settings, string roomCode, bool admin, CancellationToken ct) => SendAsync<object>(settings, HttpMethod.Post, admin ? "/api/admin/rooms/close" : "/api/rooms/close", new { roomCode }, admin ? null : settings.RoomKey, admin ? settings.AdminKey : null, ct);
    private async Task<BingoResult<T>> SendAsync<T>(BingoConnectionSettings settings, HttpMethod method, string path, object? body, string? roomKey, string? adminKey, CancellationToken ct)
    {
        var root = NormalizeBaseUri(settings.ServerUrl); if (root is null) return BingoResult<T>.Failed("Bingo server URL is not set."); if (roomKey is not null && string.IsNullOrWhiteSpace(roomKey)) return BingoResult<T>.Failed("Room key is required."); if (adminKey is not null && string.IsNullOrWhiteSpace(adminKey)) return BingoResult<T>.Failed("Admin key is required.");
        try { using var request = new HttpRequestMessage(method, new Uri(root, path)); if (!string.IsNullOrWhiteSpace(roomKey)) request.Headers.Add("x-room-key", roomKey); if (!string.IsNullOrWhiteSpace(adminKey)) request.Headers.Add("x-admin-key", adminKey); if (body is not null) request.Content = JsonContent.Create(body, options: Json); using var response = await http.SendAsync(request, ct).ConfigureAwait(false); var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); if (!response.IsSuccessStatusCode) return BingoResult<T>.Failed($"Bingo backend returned {(int)response.StatusCode}.", response.StatusCode); if (typeof(T) == typeof(object)) return new(true, (T)(object)new object()); var value = JsonSerializer.Deserialize<T>(content, Json); return value is null ? BingoResult<T>.Failed("Bingo backend response was invalid.") : new(true, value); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return BingoResult<T>.Failed("Request cancelled."); } catch (Exception) { return BingoResult<T>.Failed("Unable to complete Bingo backend request."); }
    }
}
