using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class VenueBingoClientTests
{
    [Fact] public async Task Host_sync_uses_camel_case_room_key_contract()
    {
        string? body = null; var handler = new Handler(async request => { Assert.Equal("rk", request.Headers.GetValues("x-room-key").Single()); body = await request.Content!.ReadAsStringAsync(); return Json("{\"ok\":true,\"calledNumbers\":[7],\"allowedSeeds\":[],\"allowedCards\":{},\"costPerCard\":0,\"startingPot\":0,\"prizePercentage\":100,\"gameType\":\"Single Line\"}"); });
        var request = new BingoHostSyncRequest("ROOM", "rk", false, [7], [], [], 0, 0, 100, "Single Line", null, "BINGO", "Venue", null, null, null, null, null, null); var result = await new VenueBingoClient(new HttpClient(handler)).HostSyncAsync(new("https://bingo.test", RoomKey: "rk"), request, default);
        Assert.True(result.Success); Assert.Contains("\"roomCode\":\"ROOM\"", body); Assert.Contains("\"calledNumbers\":[7]", body);
    }
    [Fact] public async Task Call_number_does_not_send_room_or_admin_key()
    {
        var handler = new Handler(request => { Assert.False(request.Headers.Contains("x-room-key")); Assert.False(request.Headers.Contains("x-admin-key")); return Task.FromResult(Json("{\"ok\":true,\"added\":true,\"calledNumbers\":[12]}")); }); var result = await new VenueBingoClient(new HttpClient(handler)).CallNumberAsync(new("https://bingo.test", AdminKey: "admin", RoomKey: "room"), "CODE", 12, default); Assert.True(result.Success);
    }
    [Fact] public async Task Admin_and_room_operations_keep_keys_separate()
    {
        var headers = new List<string>(); var handler = new Handler(request => { headers.Add(string.Join(',', request.Headers.Select(x => x.Key))); return Task.FromResult(Json("{\"ok\":true,\"rooms\":[]}")); }); var client = new VenueBingoClient(new HttpClient(handler)); var connection = new BingoConnectionSettings("https://bingo.test", AdminKey: "admin", RoomKey: "room"); await client.ListRoomsAsync(connection, default); await client.ListAdminRoomsAsync(connection, default); Assert.Contains("x-room-key", headers[0], StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("x-admin-key", headers[0], StringComparison.OrdinalIgnoreCase); Assert.Contains("x-admin-key", headers[1], StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("x-room-key", headers[1], StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task Venue_switch_cancels_poll_and_never_reuses_old_keys()
    {
        var handler = new Handler((_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException())); var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var first = profiles.Current; var second = profiles.Create("Second"); profiles.SaveModuleConfig(first.Id, "games.bingo", 1, new VenueBingoSettings(new("https://one", RoomKey: "one"), "A", "", "Single Line", "BINGO", 0, 0, 100, new())); profiles.SaveModuleConfig(second.Id, "games.bingo", 1, new VenueBingoSettings(new("https://two", RoomKey: "two"), "B", "", "Single Line", "BINGO", 0, 0, 100, new())); var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles); service.Load(first.Id); var poll = service.PollAsync(); service.Load(second.Id); var result = await poll; Assert.False(result.Success); Assert.Equal("two", service.Settings.Connection.RoomKey); Assert.Equal("B", service.Settings.RoomCode);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : this((r, _) => action(r)) { } public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
}
