using System.Net;
using System.Text;
using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Services;
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
    [Fact] public async Task FindPlayerLinkAsync_sends_room_and_seed_as_query_params_with_the_admin_key_and_no_room_key()
    {
        Uri? sentUri = null; string? adminKeyHeader = null; var handler = new Handler(request =>
        {
            sentUri = request.RequestUri;
            adminKeyHeader = request.Headers.TryGetValues("x-admin-key", out var v) ? v.First() : null;
            Assert.False(request.Headers.Contains("x-room-key")); // same trust boundary as CreateBrowserLinkAsync — no room key involved
            return Task.FromResult(Json("{\"ok\":true,\"code\":\"ABC123\",\"count\":3}"));
        });
        var result = await new VenueBingoClient(new HttpClient(handler)).FindPlayerLinkAsync(new("https://bingo.test", AdminKey: "admin-secret"), "ROOM 1", "seed one", default);

        Assert.True(result.Success);
        Assert.Equal("ABC123", result.Value!.Code);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal("admin-secret", adminKeyHeader);
        Assert.Equal("/api/links/lookup", sentUri!.AbsolutePath);
        Assert.Contains("room=ROOM%201", sentUri.Query); // room/seed values are escaped, not sent raw
        Assert.Contains("seed=seed%20one", sentUri.Query);
    }

    [Fact] public async Task FindPlayerLinkAsync_reports_no_link_found_without_error()
    {
        var handler = new Handler(_ => Task.FromResult(Json("{\"ok\":true,\"code\":null,\"count\":null}")));
        var result = await new VenueBingoClient(new HttpClient(handler)).FindPlayerLinkAsync(new("https://bingo.test", AdminKey: "admin-secret"), "ROOM", "seed1", default);

        Assert.True(result.Success); // "not found" is a normal, successful outcome — not a failure
        Assert.Null(result.Value!.Code);
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
        // Prime a successful v2 poll first so ActiveGame.Lifecycle is no longer null — otherwise PollAsync would
        // also attempt the legacy-room fallback call, and a single hanging handler can only observe cancellation
        // on the first of two in-flight requests. Once Lifecycle is known, a poll makes exactly one HTTP call.
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior = (_, _) => Task.FromResult(Json(V2Snapshot("ROOM", "Legacy")));
        var handler = new Handler((r, ct) => behavior(r, ct));
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var first = profiles.Current; var second = profiles.Create("Second");
        profiles.SaveModuleConfig(first.Id, "games.bingo", 1, VenueBingoDefaults.Default() with { Connection = new("https://one", RoomKey: "one") });
        profiles.SaveModuleConfig(second.Id, "games.bingo", 1, VenueBingoDefaults.Default() with { Connection = new("https://two", RoomKey: "two") });
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), _ => true);
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(first.Id); service.ObserveExistingRoom("ROOM", "one");
        Assert.True(await service.PollAsync());
        Assert.Equal("Legacy", service.ActiveGame.Lifecycle);

        behavior = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException());
        var poll = service.PollAsync();
        service.Load(second.Id);
        var result = await poll;
        Assert.False(result);
        Assert.Equal("https://two", service.Defaults.Connection.ServerUrl);
        Assert.Equal("", service.ActiveGame.RoomCode); // reset on Load, never carries the previous venue's room across
    }

    // --- v2 (docs/BINGO_V2_PROTOCOL.md §6) ---
    [Fact] public async Task Create_room_v2_sends_room_key_in_body_not_header()
    {
        string? body = null; var handler = new Handler(async request => { Assert.False(request.Headers.Contains("x-room-key")); body = await request.Content!.ReadAsStringAsync(); return Json(V2Snapshot("ROOM", "Draft")); });
        var result = await new VenueBingoClient(new HttpClient(handler)).CreateRoomV2Async(new("https://bingo.test"), new("ROOM", "rk", "Venue", 100, 0, 100, "Single Line", null, "BINGO", null, null, null, null, null, null, "idem-1"), default);
        Assert.True(result.Success); Assert.Equal("Draft", result.Value!.Lifecycle); Assert.Contains("\"roomKey\":\"rk\"", body); Assert.Contains("\"idempotencyKey\":\"idem-1\"", body);
    }
    [Fact] public async Task Start_room_v2_sends_room_key_header_and_idempotency_key_in_body()
    {
        string? body = null; var handler = new Handler(async request => { Assert.Equal("rk", request.Headers.GetValues("x-room-key").Single()); Assert.Equal(HttpMethod.Post, request.Method); body = await request.Content!.ReadAsStringAsync(); return Json(V2Snapshot("ROOM", "Active")); });
        var result = await new VenueBingoClient(new HttpClient(handler)).StartRoomV2Async(new("https://bingo.test", RoomKey: "rk"), "ROOM", new("idem-2"), default);
        Assert.True(result.Success); Assert.Equal("Active", result.Value!.Lifecycle); Assert.Contains("idem-2", body);
    }
    [Fact] public async Task Economics_locked_409_from_start_surfaces_as_a_failure_not_a_crash()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"economics_locked\"}", Encoding.UTF8, "application/json") }));
        var result = await new VenueBingoClient(new HttpClient(handler)).StartRoomV2Async(new("https://bingo.test", RoomKey: "rk"), "ROOM", new("idem-3"), default);
        Assert.False(result.Success); Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Contains("economics_locked", result.Error);
    }
    [Fact] public async Task Grant_cards_transitions_attempt_and_pot_fields_via_scripted_snapshot()
    {
        string? body = null; var handler = new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(V2SnapshotWithPot("ROOM", paidCards: 3, compCards: 2, totalCards: 5, currentPot: 300, prizePool: 300)); });
        var result = await new VenueBingoClient(new HttpClient(handler)).GrantCardsAsync(new("https://bingo.test", RoomKey: "rk"), "ROOM", new("seed1", "Alice", null, 3, 2, "idem-4"), default);
        Assert.True(result.Success); Assert.Contains("\"paidCount\":3", body); Assert.Contains("\"compCount\":2", body);
        Assert.Equal(3, result.Value!.Pot.PaidCards); Assert.Equal(2, result.Value.Pot.CompCards); Assert.Equal(5, result.Value.Pot.TotalCards);
    }
    [Fact] public async Task Create_payout_attempt_and_transition_send_idempotency_key_and_use_patch()
    {
        HttpMethod? transitionMethod = null; string? attemptBody = null; string? transitionBody = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptBody = await request.Content!.ReadAsStringAsync(); return Json("{\"attemptId\":\"a1\",\"payoutId\":\"p1\",\"amount\":1000000,\"status\":\"pending\"}"); }
            transitionMethod = request.Method; transitionBody = await request.Content!.ReadAsStringAsync();
            return Json("{\"attemptId\":\"a1\",\"status\":\"confirmed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":1000000,\"confirmedPaid\":1000000,\"outstanding\":0,\"status\":\"paid\"}}");
        });
        var client = new VenueBingoClient(new HttpClient(handler));
        var attempt = await client.CreatePayoutAttemptAsync(new("https://bingo.test", RoomKey: "rk"), "ROOM", "p1", new(1_000_000, "idem-5"), default);
        Assert.True(attempt.Success); Assert.Contains("idem-5", attemptBody);
        var transition = await client.TransitionPayoutAttemptAsync(new("https://bingo.test", RoomKey: "rk"), "ROOM", "p1", "a1", new("confirmed", "Trade complete.", "idem-6"), default);
        Assert.True(transition.Success); Assert.Equal(HttpMethod.Patch, transitionMethod); Assert.Contains("idem-6", transitionBody); Assert.Equal(0, transition.Value!.Obligation.Outstanding);
    }

    [Fact] public async Task Get_room_v2_never_sends_the_room_key_and_the_response_has_no_room_key_field()
    {
        // The public single-room read (matching legacy GET /api/room-state's trust model) must not need or transmit
        // the host's Room Key — Room Key is a host-side credential, not something every backend-authoritative field
        // implies exposing (product correction: "backend authoritative" does not mean every field is public).
        var handler = new Handler(request => { Assert.False(request.Headers.Contains("x-room-key")); return Task.FromResult(Json(V2Snapshot("ROOM", "Active"))); });
        var result = await new VenueBingoClient(new HttpClient(handler)).GetRoomV2Async(new("https://bingo.test", RoomKey: "should-not-be-sent"), "ROOM", default);
        Assert.True(result.Success);
        // BingoV2RoomState has no RoomKey property at all — there is no field for a leaked value to even land in.
        Assert.DoesNotContain("RoomKey", typeof(BingoV2RoomState).GetProperties().Select(p => p.Name));
    }

    [Fact] public async Task Sync_payouts_sends_room_key_header_and_idempotency_key_and_deserializes_callers_pool_and_split()
    {
        string? roomKeyHeader = null; string? body = null;
        var handler = new Handler(async request => { roomKeyHeader = request.Headers.GetValues("x-room-key").Single(); body = await request.Content!.ReadAsStringAsync(); return Json(V2SnapshotWithCallers()); });
        var result = await new VenueBingoClient(new HttpClient(handler)).SyncPayoutsAsync(new("https://bingo.test", RoomKey: "rk"), "ROOM", new("idem-7"), default);
        Assert.True(result.Success); Assert.Equal("rk", roomKeyHeader); Assert.Contains("idem-7", body);
        Assert.Equal(2, result.Value!.BingoCallers!.Count);
        Assert.Equal("Alice", result.Value.BingoCallers![0].Name); Assert.Equal("Bob", result.Value.BingoCallers![1].Name); // chronological, not re-sorted
        Assert.Equal(1000, result.Value.CurrentPrizePool); Assert.Equal(500, result.Value.SplitAmount);
    }

    private static string V2SnapshotWithCallers() => """
        {"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[],"allowedSeeds":["seed1","seed2"],"allowedCards":{"seed1":1,"seed2":1},"players":{"seed1":{"name":"Alice","count":1,"paidCount":1,"compCount":0},"seed2":{"name":"Bob","count":1,"paidCount":1,"compCount":0}},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":2,"compCards":0,"totalCards":2,"currentPot":200,"prizePool":200},"payouts":[],"bingoCallers":[{"seed":"seed1","name":"Alice","timestamp":1000,"phase":null},{"seed":"seed2","name":"Bob","timestamp":2000,"phase":null}],"currentPrizePool":1000,"splitAmount":500}
        """;

    [Fact] public void WithoutSecrets_strips_room_and_admin_key_even_though_the_settings_ui_now_shows_them_in_plain_text()
    {
        // Product correction: Settings shows Room Key/Admin Key as plain, readable, copyable text (no masking) —
        // but "visible in the UI" must never mean "safe to log". WithoutSecrets is the diagnostic-copy mechanism
        // that keeps these out of DiagnosticsService.RecordFailure/exception text regardless of UI presentation.
        var connection = new BingoConnectionSettings("https://bingo.test", "https://bingo.test/play", AdminKey: "admin-secret", RoomKey: "room-secret");
        var redacted = connection.WithoutSecrets();
        Assert.Null(redacted.AdminKey);
        Assert.Null(redacted.RoomKey);
        Assert.Equal("https://bingo.test", redacted.ServerUrl); // non-secret fields are untouched
    }

    private static string V2Snapshot(string roomCode, string lifecycle) => $$"""
        {"ok":true,"roomCode":"{{roomCode}}","lifecycle":"{{lifecycle}}","calledNumbers":[],"allowedSeeds":[],"allowedCards":{},"players":{},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":0,"compCards":0,"totalCards":0,"currentPot":0,"prizePool":0},"payouts":[]}
        """;
    // Plain (non-interpolated) raw-string template + token replacement — deliberately NOT $$"""...""" here: this
    // JSON's nesting produces runs of 3+ consecutive closing braces right after an interpolation, which raw-string
    // interpolation cannot disambiguate no matter how many '$' are added without reformatting the JSON itself.
    private static string V2SnapshotWithPot(string roomCode, int paidCards, int compCards, int totalCards, int currentPot, int prizePool) => """
        {"ok":true,"roomCode":"__ROOM__","lifecycle":"Active","calledNumbers":[],"allowedSeeds":["seed1"],"allowedCards":{"seed1":5},"players":{"seed1":{"name":"Alice","count":5,"paidCount":__PAID__,"compCount":__COMP__}},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":__PAID__,"compCards":__COMP__,"totalCards":__TOTAL__,"currentPot":__POT__,"prizePool":__PRIZE__},"payouts":[]}
        """.Replace("__ROOM__", roomCode).Replace("__PAID__", paidCards.ToString()).Replace("__COMP__", compCards.ToString()).Replace("__TOTAL__", totalCards.ToString()).Replace("__POT__", currentPot.ToString()).Replace("__PRIZE__", prizePool.ToString());

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : this((r, _) => action(r)) { } public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
