using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class VenueBingoServiceTests
{
    [Fact] public void Defaults_survive_a_save_and_reload_and_stay_isolated_per_venue()
    {
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var venueA = profiles.Current; var venueB = profiles.Create("Second");

        var chat = Chat();
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected")))), profiles, diagnostics, chat);
        service.Load(venueA.Id);
        service.SaveDefaults(service.Defaults with { CostPerCard = 500, Letters = "PARTY", RollCommand = BingoRollCommand.Dice, AnnounceChannel = BingoAnnounceChannel.Yell });

        service.Load(venueB.Id);
        Assert.Equal(0, service.Defaults.CostPerCard); // venue B never sees venue A's saved default
        Assert.Equal("BINGO", service.Defaults.Letters);

        // A brand-new service instance reloading venue A's config proves this actually persisted, not merely that
        // the in-memory object survived (NEW_MODULE_GUIDE.md §30's "state surviving a window close" caveat).
        var reloaded = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected")))), profiles, diagnostics, chat);
        reloaded.Load(venueA.Id);
        Assert.Equal(500, reloaded.Defaults.CostPerCard);
        Assert.Equal("PARTY", reloaded.Defaults.Letters);
        Assert.Equal(BingoRollCommand.Dice, reloaded.Defaults.RollCommand);
        Assert.Equal(BingoAnnounceChannel.Yell, reloaded.Defaults.AnnounceChannel);
    }

    [Fact] public async Task Create_game_never_rewrites_the_saved_default()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2Snapshot("ROOM", "Draft", costPerCard: 250))));
        var chat = Chat();
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { CostPerCard = 100, Connection = new("https://bingo.test", RoomKey: "test-room-key") });

        Assert.True(await service.CreateGameAsync());
        Assert.Equal(100, service.Defaults.CostPerCard); // the one-off game-creation response (250) never rewrites the saved default

        var reloaded = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        reloaded.Load(profiles.Current.Id);
        Assert.Equal(100, reloaded.Defaults.CostPerCard);
    }

    [Fact] public async Task Every_v2_mutating_call_sends_a_fresh_idempotency_key()
    {
        var keys = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            var marker = "\"idempotencyKey\":\"";
            var start = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            keys.Add(body[start..body.IndexOf('"', start)]);
            return Json(request.RequestUri!.AbsolutePath.EndsWith("/cards") ? V2SnapshotWithCards() : V2Snapshot("ROOM", "Draft"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });

        Assert.True(await service.CreateGameAsync());
        Assert.True(await service.StartGameAsync());
        Assert.True(await service.GrantCardsAsync("seed1", "Alice", 2, 1));

        Assert.Equal(3, keys.Count);
        Assert.Equal(3, keys.Distinct().Count()); // every logical operation used its OWN fresh key, never reused
    }

    [Fact] public async Task Economics_locked_409_from_start_is_surfaced_as_a_diagnostic_failure_not_a_crash_or_retry()
    {
        var attempts = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            attempts++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/start")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"economics_locked\"}", Encoding.UTF8, "application/json") });
            return Task.FromResult(Json(V2Snapshot("ROOM", "Draft")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());
        var createAttempts = attempts;

        var started = await service.StartGameAsync();

        Assert.False(started);
        Assert.Equal(createAttempts + 1, attempts); // exactly one /start call — no automatic retry with different values
        Assert.Contains("economics_locked", service.Status);
        Assert.Contains(diagnostics.Capture().RecentErrors, e => e.Message.Contains("games.bingo") && e.Message.Contains("economics_locked"));
    }

    [Fact] public async Task Paid_grant_increases_the_backend_reported_pot_while_comp_grant_does_not()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2Snapshot("ROOM", "Draft"),
            V2SnapshotWithPot(paidCards: 1, compCards: 0, totalCards: 1, currentPot: 100, prizePool: 100),
            V2SnapshotWithPot(paidCards: 1, compCards: 1, totalCards: 2, currentPot: 100, prizePool: 100), // comp card added — currentPot/prizePool unchanged
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        Assert.True(await service.GrantCardsAsync("seed1", "Alice", 1, 0));
        Assert.Equal(100, service.ActiveGame.Pot!.CurrentPot);
        Assert.Equal(1, service.ActiveGame.Pot!.PaidCards);

        Assert.True(await service.GrantCardsAsync("seed1", "Alice", 1, 1));
        Assert.Equal(100, service.ActiveGame.Pot!.CurrentPot); // comp card never moves the pot
        Assert.Equal(1, service.ActiveGame.Pot!.CompCards);
        Assert.Equal(2, service.ActiveGame.Pot!.TotalCards);
    }

    // --- Room Key semantics correction: Room Key is a PERSISTENT per-venue host credential/grouping key, reused
    // across every game a venue creates — never minted fresh per game. See VenueBingoService.CreateGameAsync's
    // doc comment and GenerateAndSaveRoomKey. ---

    [Fact] public void Room_key_is_persistent_per_venue_and_switching_venues_restores_the_correct_one()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var venueA = profiles.Current; var venueB = profiles.Create("Second");
        var throwingClient = new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected"))));
        var service = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());

        service.Load(venueA.Id);
        service.SaveDefaults(service.Defaults with { Connection = service.Defaults.Connection with { RoomKey = "VenueASecret" } });
        service.Load(venueB.Id);
        Assert.Null(service.Defaults.Connection.RoomKey); // venue B never sees venue A's key
        service.SaveDefaults(service.Defaults with { Connection = service.Defaults.Connection with { RoomKey = "VenueBSecret" } });

        service.Load(venueA.Id);
        Assert.Equal("VenueASecret", service.Defaults.Connection.RoomKey); // switching back to venue A restores its own key
        service.Load(venueB.Id);
        Assert.Equal("VenueBSecret", service.Defaults.Connection.RoomKey);

        // A brand-new service instance proves this is actually persisted, not just in-memory.
        var reloaded = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());
        reloaded.Load(venueA.Id);
        Assert.Equal("VenueASecret", reloaded.Defaults.Connection.RoomKey);
    }

    [Fact] public async Task Create_game_with_no_room_key_configured_fails_without_calling_the_backend()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP call should be made")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") }); // RoomKey deliberately left blank

        var created = await service.CreateGameAsync();

        Assert.False(created);
        Assert.Contains("Room Key", service.Status);
        Assert.True(string.IsNullOrWhiteSpace(service.ActiveGame.RoomCode)); // blank Room Key must never silently mint a per-game key and proceed
    }

    [Fact] public async Task Creating_multiple_games_for_one_venue_reuses_the_same_persistent_room_key()
    {
        var sentRoomKeys = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var roomCounter = 0;
        var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/close")) return Json(V2Snapshot("CLOSED", "Closed"));
            var body = await request.Content!.ReadAsStringAsync();
            sentRoomKeys.Add(ExtractJsonString(body, "roomKey")); // only the create-room body carries roomKey at all
            roomCounter++;
            return Json(V2Snapshot($"ROOM{roomCounter}", "Draft"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });

        Assert.True(await service.CreateGameAsync());
        var firstRoomCode = service.ActiveGame.RoomCode;
        Assert.True(await service.CloseGameAsync());
        Assert.True(await service.CreateGameAsync());
        var secondRoomCode = service.ActiveGame.RoomCode;

        Assert.NotEqual(firstRoomCode, secondRoomCode); // different Room Code per game...
        Assert.Equal(2, sentRoomKeys.Count);
        Assert.All(sentRoomKeys, key => Assert.Equal("VenueASecret", key)); // ...but the SAME persistent Room Key every time
    }

    [Fact] public async Task Listing_rooms_sends_the_persistent_room_key_and_returns_the_venues_rooms()
    {
        string? sentRoomKeyHeader = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            sentRoomKeyHeader = request.Headers.TryGetValues("x-room-key", out var values) ? values.First() : null;
            return Task.FromResult(Json("""{"ok":true,"rooms":[{"roomCode":"ROOM1","calledNumbersCount":3,"allowedSeedsCount":1,"allowedCardsCount":1,"daubPlayers":1,"lastBingo":null,"bingoCallsCount":0,"gameType":"Single Line","updatedAt":0}]}"""));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });

        var rooms = await service.ListVenueRoomsAsync();

        Assert.Equal("VenueASecret", sentRoomKeyHeader);
        Assert.NotNull(rooms);
        Assert.Single(rooms!);
        Assert.Equal("ROOM1", rooms![0].RoomCode);
    }

    [Fact] public async Task Host_B_can_discover_and_resume_venue_As_room_using_the_same_persistent_room_key()
    {
        // Simulates two separate hosts (two separate VenueBingoService instances, as if on two different machines)
        // sharing the same venue's persistent Room Key against the same backend.
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var connection = new BingoConnectionSettings("https://bingo.test", RoomKey: "VenueASecret");

        var listHandler = new Handler(_ => Task.FromResult(Json("""{"ok":true,"rooms":[{"roomCode":"ROOM-A","calledNumbersCount":5,"allowedSeedsCount":2,"allowedCardsCount":2,"daubPlayers":1,"lastBingo":null,"bingoCallsCount":0,"gameType":"Single Line","updatedAt":0}]}""")));
        var hostB = new VenueBingoService(new VenueBingoClient(new HttpClient(listHandler)), profiles, diagnostics, Chat());
        hostB.Load(profiles.Current.Id);
        hostB.SaveDefaults(hostB.Defaults with { Connection = connection });

        var discovered = await hostB.ListVenueRoomsAsync();
        Assert.NotNull(discovered);
        var roomCode = Assert.Single(discovered!).RoomCode;

        string? snapshotRequestPath = null;
        var pollHandler = new Handler(request => { snapshotRequestPath = request.RequestUri!.AbsolutePath; return Task.FromResult(Json(V2Snapshot(roomCode, "Active", costPerCard: 100))); });
        var hostBResume = new VenueBingoService(new VenueBingoClient(new HttpClient(pollHandler)), profiles, diagnostics, Chat());
        hostBResume.Load(profiles.Current.Id);
        hostBResume.SaveDefaults(hostBResume.Defaults with { Connection = connection });
        hostBResume.ObserveExistingRoom(roomCode, connection.RoomKey!);
        Assert.True(await hostBResume.PollAsync());

        Assert.Equal(roomCode, hostBResume.ActiveGame.RoomCode);
        Assert.Equal("Active", hostBResume.ActiveGame.Lifecycle);
        Assert.Contains(roomCode, snapshotRequestPath); // resumed by the discovered room code, never a re-generated one
    }

    [Fact] public void Generate_and_save_room_key_persists_as_the_venue_default_not_a_transient_game_key()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var throwingClient = new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected"))));
        var service = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        Assert.Null(service.Defaults.Connection.RoomKey);

        var generated = service.GenerateAndSaveRoomKey();

        Assert.False(string.IsNullOrWhiteSpace(generated));
        Assert.Equal(generated, service.Defaults.Connection.RoomKey); // saved as the persistent default immediately

        var reloaded = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());
        reloaded.Load(profiles.Current.Id);
        Assert.Equal(generated, reloaded.Defaults.Connection.RoomKey); // survives a fresh reload — a real persisted default, not transient state
    }

    // --- Roll command / correlation, service-level (see BingoRollCorrelationTests.cs for the pure correlator/
    // parser unit tests — these exercise VenueBingoService's own orchestration: exact command text, the pending-
    // roll lifecycle, duplicate-delivery protection, and timeout behavior). ---

    [Fact] public async Task Random_mode_emits_exactly_random_75()
    {
        var sentCommands = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { sentCommands.Add(text); return true; });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => Task.FromResult(Json(V2Snapshot("ROOM", "Active")))))), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), RollCommand = BingoRollCommand.Random });
        service.ObserveExistingRoom("ROOM", "rk");

        service.RollAndCall();
        await chat.TickAsync();

        Assert.Equal(["/random 75"], sentCommands);
    }

    [Fact] public async Task Dice_mode_emits_exactly_dice_75_never_a_bare_dice()
    {
        var sentCommands = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { sentCommands.Add(text); return true; });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => Task.FromResult(Json(V2Snapshot("ROOM", "Active")))))), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), RollCommand = BingoRollCommand.Dice });
        service.ObserveExistingRoom("ROOM", "rk");

        service.RollAndCall();
        await chat.TickAsync();

        Assert.Equal(["/dice 75"], sentCommands);
    }

    [Fact] public async Task RollAndCall_establishes_AwaitingRoll_before_the_command_is_actually_dispatched()
    {
        // Explicit ordering guard (live-QA correction pass): rules out "the result arrives so fast that pendingRoll
        // is created AFTER it, so a genuinely valid observation gets rejected as NoPendingRoll". The fake chat
        // dispatcher below inspects IsAwaitingRoll from INSIDE the send callback — i.e. at the exact instant the
        // command would actually leave the client — which must already be true by then.
        var isAwaitingRollAtDispatchTime = false;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        VenueBingoService? serviceRef = null;
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), _ => { isAwaitingRollAtDispatchTime = serviceRef!.IsAwaitingRoll; return true; });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => Task.FromResult(Json(V2Snapshot("ROOM", "Active")))))), profiles, diagnostics, chat);
        serviceRef = service;
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        service.RollAndCall();
        await chat.TickAsync();

        Assert.True(isAwaitingRollAtDispatchTime); // pendingRoll must exist strictly BEFORE /random 75 is actually sent
    }

    [Fact] public async Task Backend_rejection_of_a_fresh_call_never_announces()
    {
        var announcements = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { announcements.Add(text); return true; });
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/call-number"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"room not found\"}", Encoding.UTF8, "application/json") })
            : Task.FromResult(Json(V2Snapshot("ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), AnnounceChannel = BingoAnnounceChannel.Yell });
        service.ObserveExistingRoom("ROOM", "rk");
        service.RollAndCall();

        await service.HandleRollObservation(new BingoRollObservation(BingoRollMode.Random, true, 42, DateTimeOffset.UtcNow));

        Assert.Empty(announcements); // a backend rejection must never be announced as if the number were called
    }

    [Fact] public async Task Backend_acceptance_of_a_fresh_call_triggers_the_configured_Yell_announcement()
    {
        var announcements = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        // minimumInterval: TimeSpan.Zero — the default 1s rate limit combined with a FakeClock that never advances
        // would otherwise let only the FIRST-ever enqueued command (the /random 75 itself) actually dispatch in
        // this test, permanently blocking the announcement's own later tick.
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { announcements.Add(text); return true; }, TimeSpan.Zero);
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/call-number"
            ? Task.FromResult(Json("{\"ok\":true,\"added\":true,\"calledNumbers\":[42]}"))
            : Task.FromResult(Json(V2Snapshot("ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), AnnounceChannel = BingoAnnounceChannel.Yell });
        service.ObserveExistingRoom("ROOM", "rk");
        service.RollAndCall();
        await chat.TickAsync(); // dispatches the /random 75 itself, freeing the queue for the announcement below

        await service.HandleRollObservation(new BingoRollObservation(BingoRollMode.Random, true, 42, DateTimeOffset.UtcNow));
        await chat.TickAsync(); // dispatches the announcement enqueued by the accepted call above

        Assert.Contains(announcements, a => a.StartsWith("/yell")); // downstream of backend acceptance, exactly once
    }

    [Fact] public async Task Announcement_uses_the_locked_games_authoritative_letters_not_the_venues_current_settings_default()
    {
        // Custom Letters correction: an operator editing the venue's Settings default mid-game must never
        // retroactively relabel an already-locked game's balls — the backend-authoritative snapshot's own Letters
        // (captured when the room was created/resumed) must win over whatever Defaults.Letters says right now.
        var announcements = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { announcements.Add(text); return true; }, TimeSpan.Zero);
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/call-number"
            ? Task.FromResult(Json("{\"ok\":true,\"added\":true,\"calledNumbers\":[16]}"))
            : Task.FromResult(Json(V2SnapshotWithLetters("ROOM", "Active", "PARTY"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        // The venue's CURRENT Settings default is deliberately left as "BINGO" (the default) — only the locked
        // game's own snapshot says "PARTY".
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), AnnounceChannel = BingoAnnounceChannel.Yell });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // establishes ActiveGame.Snapshot.Letters = "PARTY"
        service.RollAndCall();
        await chat.TickAsync(); // dispatches the /random 75 itself

        await service.HandleRollObservation(new BingoRollObservation(BingoRollMode.Random, true, 16, DateTimeOffset.UtcNow));
        await chat.TickAsync(); // dispatches the announcement

        Assert.Contains(announcements, a => a.StartsWith("/yell A-16")); // number 16 falls in column 1 (16-30); "PARTY"[1] = 'A' — never the venue's current "BINGO" default
    }

    [Fact] public async Task Duplicate_chat_delivery_of_the_same_local_host_result_cannot_cause_duplicate_backend_submission()
    {
        var callNumberRequests = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/call-number")
            { callNumberRequests++; return Task.FromResult(Json("{\"ok\":true,\"added\":true,\"calledNumbers\":[42]}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        service.RollAndCall();

        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, 42, DateTimeOffset.UtcNow);
        await service.HandleRollObservation(observation); // first delivery — accepted
        await service.HandleRollObservation(observation); // a duplicate delivery of the identical chat message

        Assert.Equal(1, callNumberRequests); // exactly one backend submission, never two
    }

    [Fact] public async Task Backend_duplicate_rejection_reconciles_correctly_without_re_announcing()
    {
        var announcements = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), text => { announcements.Add(text); return true; });
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/call-number"
            ? Task.FromResult(Json("{\"ok\":true,\"added\":false,\"calledNumbers\":[42]}")) // backend says: already called
            : Task.FromResult(Json(V2Snapshot("ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test"), AnnounceChannel = BingoAnnounceChannel.Shout });
        service.ObserveExistingRoom("ROOM", "rk");
        service.RollAndCall();

        await service.HandleRollObservation(new BingoRollObservation(BingoRollMode.Random, true, 42, DateTimeOffset.UtcNow));

        Assert.Contains("already called", service.Status);
        Assert.DoesNotContain(announcements, a => a.StartsWith("/shout")); // a duplicate the backend rejects is never announced as a fresh call
    }

    [Fact] public void Timeout_produces_no_backend_call_for_the_missed_roll()
    {
        // Note: VenueBingoService.Tick also drives the regular ~5s room-state poll in the same call, so a poll is
        // expected to fire alongside the roll-timeout check here (exactly as it would in real operation after 11
        // seconds) — the fake handler answers it harmlessly. The assertion that actually matters is that the
        // MISSED ROLL itself never produces a call-number submission, which is unaffected by that incidental poll.
        var callNumberRequests = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var chat = new ChatCommandService(new FakeClock(), new InlineFrameworkDispatcher(), _ => true);
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/call-number") callNumberRequests++;
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, chat);
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        var start = DateTimeOffset.UtcNow;
        service.RollAndCall();

        service.Tick(start.AddSeconds(11)); // past the 10s roll timeout, no observation ever arrived

        Assert.Equal(0, callNumberRequests); // the missed roll must never produce a backend call-number submission, no number is ever invented
    }

    // --- Room Close/Delete (product correction: manual cleanup, using the existing keyed backend route + the
    // venue's persistent Room Key; active-room protection is enforced in the operator panel, verified by
    // inspection — see the completion report). ---

    [Fact] public async Task Deleting_a_room_uses_the_venues_persistent_room_key_and_the_correct_room_code()
    {
        string? sentPath = null; string? sentRoomKeyHeader = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            sentPath = request.RequestUri!.AbsolutePath;
            sentRoomKeyHeader = request.Headers.TryGetValues("x-room-key", out var v) ? v.First() : null;
            return Task.FromResult(Json("{\"ok\":true}"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });

        var deleted = await service.DeleteRoomAsync("SOME-ROOM-CODE");

        Assert.True(deleted);
        Assert.Equal("/api/rooms/close", sentPath); // the existing keyed room-close route — not the unauthenticated admin one
        Assert.Equal("VenueASecret", sentRoomKeyHeader);
    }

    [Fact] public async Task Backend_rejection_of_a_delete_does_not_falsely_report_success()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"room not found\"}", Encoding.UTF8, "application/json") }));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "WrongKey") });

        var deleted = await service.DeleteRoomAsync("SOME-ROOM-CODE");

        Assert.False(deleted); // a wrong Room Key (backend 404s on mismatch) must fail safely, never be reported as deleted
        Assert.Contains(diagnostics.Capture().RecentErrors, e => e.Message.Contains("games.bingo"));
    }

    [Fact] public async Task Deleting_the_currently_active_rooms_code_clears_local_active_game_state()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/close")
            ? Task.FromResult(Json("{\"ok\":true}"))
            : Task.FromResult(Json(V2Snapshot("ACTIVE-ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });
        service.ObserveExistingRoom("ACTIVE-ROOM", "VenueASecret");
        Assert.True(await service.PollAsync());
        Assert.Equal("ACTIVE-ROOM", service.ActiveGame.RoomCode);

        Assert.True(await service.DeleteRoomAsync("ACTIVE-ROOM"));

        Assert.Equal("", service.ActiveGame.RoomCode); // local state stays consistent with the backend having deleted it
        Assert.Null(service.ActiveGame.Lifecycle);
    }

    [Fact] public async Task Deleting_a_room_with_no_room_key_configured_fails_without_calling_the_backend()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP call should be made")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") }); // RoomKey deliberately blank

        Assert.False(await service.DeleteRoomAsync("SOME-ROOM"));
    }

    // --- Leave Game vs Close Room (live-QA correction pass): "Leave Game" is a purely local, non-destructive
    // detach — it must NEVER call any backend close/delete endpoint. "Close Room" (DeleteRoomAsync, tested above)
    // remains the only destructive action, and is already proven to use the correct keyed legacy endpoint. ---

    [Fact] public void Leave_game_makes_no_backend_request_at_all()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("Leave Game must never call the backend")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });
        service.ObserveExistingRoom("ROOM1", "VenueASecret");

        service.LeaveGame(); // if this throws, the fake handler above proves a backend call was attempted

        Assert.Equal("", service.ActiveGame.RoomCode);
    }

    [Fact] public void Leave_game_clears_local_active_room_state_and_abandons_any_pending_roll()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM1", "rk");
        service.RollAndCall(); // opens a pending roll window
        Assert.True(service.IsAwaitingRoll);

        service.LeaveGame();

        Assert.Equal("", service.ActiveGame.RoomCode);
        Assert.Null(service.ActiveGame.Lifecycle);
        Assert.False(service.IsAwaitingRoll); // a late-arriving roll result can never be submitted for a room this client left
    }

    [Fact] public void Leave_game_with_no_active_game_is_a_safe_no_op()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);

        service.LeaveGame();

        Assert.Equal("", service.ActiveGame.RoomCode);
        Assert.Contains("No active game", service.Status);
    }

    [Fact] public async Task Resuming_after_leave_restores_full_authoritative_state_from_the_backend()
    {
        // Simulates: operator leaves, then later resumes the same room — everything shown after Resume must come
        // straight from the backend's own snapshot, never from anything cached locally across the Leave.
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithCallers())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });
        service.ObserveExistingRoom("ROOM", "VenueASecret");
        Assert.True(await service.PollAsync());
        Assert.Equal(2, service.ActiveGame.Players.Count);

        service.LeaveGame();
        Assert.Equal("", service.ActiveGame.RoomCode);

        service.ObserveExistingRoom("ROOM", "VenueASecret");
        Assert.True(await service.PollAsync());

        Assert.Equal("ROOM", service.ActiveGame.RoomCode);
        Assert.Equal("Active", service.ActiveGame.Lifecycle);
        Assert.Equal(2, service.ActiveGame.Players.Count); // players survive Leave/Resume
        Assert.Equal(2, service.ActiveGame.BingoCallers.Count); // caller history survives Leave/Resume
        Assert.Single(service.ActiveGame.Payouts); // payout state survives Leave/Resume
        Assert.Equal(500, service.ActiveGame.SplitAmount);
    }

    [Fact] public async Task Leaving_the_active_room_never_touches_a_different_rooms_local_state()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => Task.FromResult(Json(V2Snapshot("ROOM-A", "Active")))))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "VenueASecret") });
        service.ObserveExistingRoom("ROOM-A", "VenueASecret");
        Assert.True(await service.PollAsync());

        service.LeaveGame();

        Assert.Equal("", service.ActiveGame.RoomCode); // only the LOCAL client detaches — ROOM-A itself is never touched (no delete/close call was made)
    }

    // --- Payout sync / player links (live-QA correction pass): backend-authoritative caller list drives payouts —
    // the host never manually picks a winner. Copy Link's cache/staleness policy lives in the operator panel (UI
    // layer, not unit-testable per NEW_MODULE_GUIDE.md §30) — GetOrCreatePlayerLinkAsync itself is proven stateless
    // here instead. ---

    [Fact] public async Task GetOrCreatePlayerLinkAsync_sends_the_correct_request_shape_and_returns_a_url_built_from_the_code()
    {
        string? sentPath = null; string? sentBody = null; string? adminKeyHeader = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links")
            { sentPath = request.RequestUri.AbsolutePath; adminKeyHeader = request.Headers.TryGetValues("x-admin-key", out var v) ? v.First() : null; sentBody = await request.Content!.ReadAsStringAsync(); return Json("{\"ok\":true,\"code\":\"ABC123\"}"); }
            return Json(V2Snapshot("ROOM", "Active"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", "https://play.bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var url = await service.GetOrCreatePlayerLinkAsync("seed1", "Alice", 5);

        Assert.Equal("/api/links", sentPath);
        Assert.Equal("admin-secret", adminKeyHeader);
        Assert.Contains("\"seed\":\"seed1\"", sentBody);
        Assert.Contains("\"count\":5", sentBody);
        Assert.Contains("\"player\":\"Alice\"", sentBody);
        Assert.Equal("https://play.bingo.test/l/ABC123", url); // BrowserUrl preferred over ServerUrl, trailing slash trimmed
    }

    [Fact] public async Task GetOrCreatePlayerLinkAsync_is_a_stateless_per_call_wrapper_with_no_service_level_caching()
    {
        var linkCalls = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links") { linkCalls++; return Task.FromResult(Json($"{{\"ok\":true,\"code\":\"CODE{linkCalls}\"}}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var first = await service.GetOrCreatePlayerLinkAsync("seed1", "Alice", 5);
        var second = await service.GetOrCreatePlayerLinkAsync("seed1", "Alice", 5); // identical args — the service still calls the backend every time; caching is the caller's job

        Assert.Equal(2, linkCalls);
        Assert.NotEqual(first, second);
    }

    [Fact] public async Task GetOrCreatePlayerLinkAsync_with_no_admin_key_fails_cleanly_without_calling_the_backend()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links") throw new InvalidOperationException("no /api/links call should be made without an Admin Key");
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") }); // AdminKey deliberately blank
        Assert.True(await service.CreateGameAsync());

        var url = await service.GetOrCreatePlayerLinkAsync("seed1", "Alice", 5);

        Assert.Null(url);
        Assert.Contains("Admin Key", service.Status);
    }

    // --- EnsureCurrentPlayerLinkAsync (live-QA correction — auto-create player link on Add Player/card-count
    // change/room resume): tries GET /api/links/lookup first so a client that already knows about an existing
    // link (or one the backend already has from before a local cache loss) never mints a needless duplicate
    // POST /api/links code. ---

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_creates_a_link_when_the_backend_has_none_yet()
    {
        var createCalls = 0; var lookupCalls = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") { lookupCalls++; return Task.FromResult(Json("{\"ok\":true,\"code\":null,\"count\":null}")); }
            if (request.RequestUri!.AbsolutePath == "/api/links") { createCalls++; return Task.FromResult(Json("{\"ok\":true,\"code\":\"NEWCODE\"}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", "https://play.bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 3);

        Assert.Equal(1, lookupCalls);
        Assert.Equal(1, createCalls); // lookup found nothing — exactly one create call, not skipped
        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Count);
        Assert.Equal("https://play.bingo.test/l/NEWCODE", result.Value.Url);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_reuses_an_existing_backend_link_without_creating_a_new_one()
    {
        // Simulates room resume / plugin reload after local cache loss: the backend already has a link for this
        // seed (count=5, over-provisioned relative to the current total of 3) — must be rediscovered, not
        // duplicated. This is also the exact "card count DECREASE keeps its existing link" continuity case.
        var createCalls = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Task.FromResult(Json("{\"ok\":true,\"code\":\"EXISTING\",\"count\":5}"));
            if (request.RequestUri!.AbsolutePath == "/api/links") { createCalls++; return Task.FromResult(Json("{\"ok\":true,\"code\":\"SHOULDNOTHAPPEN\"}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", "https://play.bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 3);

        Assert.Equal(0, createCalls); // no duplicate short code minted — the existing one covers the current total
        Assert.NotNull(result);
        Assert.Equal(5, result!.Value.Count); // the real, still-valid baked-in count is reported back, not the requested 3
        Assert.Equal("https://play.bingo.test/l/EXISTING", result.Value.Url);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_creates_a_fresh_link_when_the_existing_ones_count_is_too_low()
    {
        // Paid/Comp card-count INCREASE past what the backend's known link covers.
        var createCalls = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Task.FromResult(Json("{\"ok\":true,\"code\":\"OLD\",\"count\":1}"));
            if (request.RequestUri!.AbsolutePath == "/api/links") { createCalls++; return Task.FromResult(Json("{\"ok\":true,\"code\":\"GROWN\"}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", "https://play.bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 2); // total grew from 1 to 2

        Assert.Equal(1, createCalls);
        Assert.NotNull(result);
        Assert.Equal("https://play.bingo.test/l/GROWN", result!.Value.Url);
        Assert.Equal(2, result.Value.Count);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_falls_back_to_creating_when_the_lookup_itself_fails()
    {
        var createCalls = 0;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            if (request.RequestUri!.AbsolutePath == "/api/links") { createCalls++; return Task.FromResult(Json("{\"ok\":true,\"code\":\"RECOVERED\"}")); }
            return Task.FromResult(Json(V2Snapshot("ROOM", "Active")));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 1);

        Assert.Equal(1, createCalls); // a failed lookup is never fatal — falls through to create
        Assert.NotNull(result);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_fails_cleanly_without_an_admin_key_and_never_calls_the_backend()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/links")
            ? throw new InvalidOperationException("no link call should be made without an Admin Key")
            : Task.FromResult(Json(V2Snapshot("ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") }); // AdminKey blank
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 1);

        Assert.Null(result);
        Assert.Contains("Admin Key", service.Status);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_two_different_players_get_two_separate_correctly_seeded_links()
    {
        var sentSeeds = new List<string>();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") { sentSeeds.Add(ExtractQueryValue(request.RequestUri.Query, "seed")); return Json("{\"ok\":true,\"code\":null,\"count\":null}"); }
            if (request.RequestUri!.AbsolutePath == "/api/links") { var body = await request.Content!.ReadAsStringAsync(); return Json(body.Contains("\"seed\":\"seed1\"") ? "{\"ok\":true,\"code\":\"CODE1\"}" : "{\"ok\":true,\"code\":\"CODE2\"}"); }
            return Json(V2Snapshot("ROOM", "Active"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", "https://play.bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        var result1 = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 1);
        var result2 = await service.EnsureCurrentPlayerLinkAsync("seed2", "Bob", 1);

        Assert.Equal(["seed1", "seed2"], sentSeeds);
        Assert.Equal("https://play.bingo.test/l/CODE1", result1!.Value.Url);
        Assert.Equal("https://play.bingo.test/l/CODE2", result2!.Value.Url);
    }

    [Fact] public async Task EnsureCurrentPlayerLinkAsync_result_url_never_contains_the_room_or_admin_key()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/api/links/lookup"
            ? Task.FromResult(Json("{\"ok\":true,\"code\":null,\"count\":null}"))
            : request.RequestUri!.AbsolutePath == "/api/links"
                ? Task.FromResult(Json("{\"ok\":true,\"code\":\"SAFE123\"}"))
                : Task.FromResult(Json(V2Snapshot("ROOM", "Active"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", AdminKey: "TOP-SECRET-ADMIN", RoomKey: "TOP-SECRET-ROOM") });
        Assert.True(await service.CreateGameAsync());

        var result = await service.EnsureCurrentPlayerLinkAsync("seed1", "Alice", 1);

        Assert.NotNull(result);
        Assert.DoesNotContain("TOP-SECRET-ADMIN", result!.Value.Url);
        Assert.DoesNotContain("TOP-SECRET-ROOM", result.Value.Url);
    }

    // --- Copy Link color normalization (live-QA correction pass): the backend's normalizeHex only accepts 3 or 6
    // hex digits; VenueOS's Settings color field allowed up to 9 characters (# + up to 8 hex digits, an RGBA
    // convention with no backend-side meaning), which produced a live 400 "invalid color value" on Copy Link. See
    // BingoColorNormalizationTests.cs for the pure normalization-logic tests; these prove it's actually applied at
    // both request-construction points. ---

    [Fact] public async Task CreateGameAsync_sends_normalized_6_digit_hex_even_when_an_8_digit_rgba_value_is_configured()
    {
        string? sentBody = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request => { sentBody = await request.Content!.ReadAsStringAsync(); return Json(V2Snapshot("ROOM", "Draft")); });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with
        {
            Connection = new("https://bingo.test", RoomKey: "test-room-key"),
            Colors = new BingoColors(Bg: "#121418FF", Card: "#1c2126", Header: "abc", Text: null, Daub: "", Ball: "  "),
        });

        Assert.True(await service.CreateGameAsync());

        Assert.Contains("\"bg\":\"121418\"", sentBody); // alpha dropped, uppercased
        Assert.Contains("\"card\":\"1C2126\"", sentBody);
        Assert.Contains("\"header\":\"ABC\"", sentBody); // missing "#" tolerated
        Assert.DoesNotContain("FF\"", sentBody); // the offending 8th/9th alpha digits never reach the backend at all
    }

    [Fact] public async Task GetOrCreatePlayerLinkAsync_also_sends_normalized_colors_fixing_the_live_copy_link_400()
    {
        string? sentBody = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/links") { sentBody = await request.Content!.ReadAsStringAsync(); return Json("{\"ok\":true,\"code\":\"ABC123\"}"); }
            return Json(V2Snapshot("ROOM", "Active"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with
        {
            Connection = new("https://bingo.test", AdminKey: "admin-secret", RoomKey: "test-room-key"),
            Colors = new BingoColors(Bg: "#121418FF"),
        });
        Assert.True(await service.CreateGameAsync());

        var url = await service.GetOrCreatePlayerLinkAsync("seed1", "Alice", 1);

        Assert.NotNull(url); // the fix: this previously failed live with a 400 "invalid color value"
        Assert.Contains("\"bg\":\"121418\"", sentBody);
    }

    [Fact] public async Task SyncPayoutsAsync_sends_a_fresh_idempotency_key_and_updates_payouts_callers_and_split()
    {
        string? sentPath = null; string? sentBody = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/payouts/sync")) { sentPath = request.RequestUri.AbsolutePath; sentBody = await request.Content!.ReadAsStringAsync(); return Json(V2SnapshotWithCallers()); }
            return Json(V2Snapshot("ROOM", "Active"));
        });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });
        Assert.True(await service.CreateGameAsync());

        Assert.True(await service.SyncPayoutsAsync());

        Assert.Contains("/payouts/sync", sentPath);
        Assert.Contains("\"idempotencyKey\":\"", sentBody);
        Assert.Single(service.ActiveGame.Payouts);
        Assert.Equal(2, service.ActiveGame.BingoCallers.Count);
        Assert.Equal(500, service.ActiveGame.SplitAmount);
        Assert.Equal(1000, service.ActiveGame.CurrentPrizePool);
    }

    [Fact] public async Task Multi_caller_snapshot_preserves_the_backends_chronological_order_never_re_sorted()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithCallers())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });
        service.ObserveExistingRoom("ROOM", "test-room-key");

        Assert.True(await service.PollAsync());

        Assert.Equal(["Alice", "Bob"], service.ActiveGame.BingoCallers.Select(c => c.Name));
        Assert.True(service.ActiveGame.BingoCallers[0].Timestamp < service.ActiveGame.BingoCallers[1].Timestamp);
    }

    // --- Bingo Call Alert (live-QA addition): a host-attention layer over the already-authoritative BingoCallers
    // list — never a second winner state, and the backend caller history is never mutated by dismissal. ---

    [Fact] public async Task No_callers_produces_no_pending_alert()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithCallerList("[]"))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());

        Assert.Empty(service.PendingAlertCallers);
    }

    [Fact] public async Task A_caller_appearing_after_the_baseline_produces_exactly_one_pending_alert()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"), // baseline: no callers yet
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // establishes baseline — no alert yet
        Assert.Empty(service.PendingAlertCallers);

        Assert.True(await service.PollAsync()); // Kei Joi's Bingo is accepted by the backend

        var pending = Assert.Single(service.PendingAlertCallers);
        Assert.Equal("Kei Joi", pending.Name);
        Assert.Equal("seed1", pending.Seed);
    }

    [Fact] public async Task The_same_caller_reappearing_on_a_later_poll_never_duplicates_the_alert()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var callerSnapshot = V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null}]""");
        var responses = new Queue<string>([V2SnapshotWithCallerList("[]"), callerSnapshot, callerSnapshot, callerSnapshot]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // baseline

        Assert.True(await service.PollAsync()); // Kei Joi's call detected
        Assert.True(await service.PollAsync()); // same caller, next poll (e.g. the ~5s Tick cadence)
        Assert.True(await service.PollAsync()); // and again

        Assert.Single(service.PendingAlertCallers); // never duplicated, even across many repeated polls
    }

    [Fact] public async Task A_second_unique_caller_appears_once_alongside_the_first_never_replacing_it()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"),
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null}]"""),
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // baseline
        Assert.True(await service.PollAsync()); // Kei Joi alerts

        Assert.True(await service.PollAsync()); // Alice also calls Bingo

        Assert.Equal(2, service.PendingAlertCallers.Count); // BOTH preserved — Alice's alert never replaced Kei's
        Assert.Contains(service.PendingAlertCallers, c => c.Name == "Kei Joi");
        Assert.Contains(service.PendingAlertCallers, c => c.Name == "Alice");
    }

    [Fact] public async Task Multiple_pending_callers_preserve_their_own_first_accepted_timestamps()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"), // baseline: neither caller present yet
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1111,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2222,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // baseline

        Assert.True(await service.PollAsync()); // both call Bingo before either alert is dismissed

        Assert.Equal(1111, service.PendingAlertCallers.Single(c => c.Seed == "seed1").Timestamp);
        Assert.Equal(2222, service.PendingAlertCallers.Single(c => c.Seed == "seed2").Timestamp);
    }

    [Fact] public async Task Dismissing_one_alert_leaves_the_other_pending_and_never_touches_backend_state()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"),
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync());
        Assert.True(await service.PollAsync());
        Assert.Equal(2, service.PendingAlertCallers.Count);
        var callersBeforeDismiss = service.ActiveGame.BingoCallers;

        service.DismissBingoAlert("seed1", null);

        Assert.Single(service.PendingAlertCallers);
        Assert.Equal("Alice", service.PendingAlertCallers[0].Name); // Kei's dismissal never touched Alice's pending alert
        Assert.Same(callersBeforeDismiss, service.ActiveGame.BingoCallers); // backend-authoritative caller list object is completely untouched by dismissal
        Assert.Equal(2, service.ActiveGame.BingoCallers.Count); // Kei is still in the authoritative history — dismissal is local-only
    }

    [Fact] public async Task Dismiss_all_clears_every_pending_alert_at_once()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"),
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync());
        Assert.True(await service.PollAsync());
        Assert.Equal(2, service.PendingAlertCallers.Count);

        service.DismissAllBingoAlerts();

        Assert.Empty(service.PendingAlertCallers);
    }

    [Fact] public async Task Resuming_a_room_with_existing_historical_callers_establishes_them_as_the_baseline_without_alerting()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        // The room ALREADY has two historical callers the very first time this host observes it (e.g. resuming a
        // room another host was already running, or reattaching after a plugin reload) — must never nuisance-pop
        // ten stale alerts for pre-existing history.
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithCallerList(
            """[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""))));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());

        Assert.Empty(service.PendingAlertCallers); // both pre-existing callers became the baseline, not an alert
        Assert.Equal(2, service.ActiveGame.BingoCallers.Count); // still fully visible in the authoritative history
    }

    [Fact] public async Task A_caller_arriving_after_a_resumes_baseline_alerts_normally()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null}]"""), // baseline: one historical caller
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""), // Alice is genuinely new
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // baseline — Kei is historical, no alert
        Assert.Empty(service.PendingAlertCallers);

        Assert.True(await service.PollAsync()); // Alice calls Bingo after this host attached

        var pending = Assert.Single(service.PendingAlertCallers);
        Assert.Equal("Alice", pending.Name); // only the genuinely-new caller alerts — Kei stays baselined
    }

    [Fact] public async Task A_missed_poll_never_suppresses_detection_of_a_caller_that_appeared_during_the_gap()
    {
        // Simulates polling interruption: the host's very NEXT successful poll jumps straight from the baseline
        // (zero callers) to a state with TWO new callers already present — there was never an intermediate
        // "one caller" snapshot. Both must still be detected and alerted; detection must never depend on comparing
        // against only the immediately-previous poll.
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"),
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null},{"seed":"seed2","name":"Alice","timestamp":2000,"phase":null}]"""),
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");
        Assert.True(await service.PollAsync()); // baseline

        Assert.True(await service.PollAsync()); // several polls' worth of activity arrives at once

        Assert.Equal(2, service.PendingAlertCallers.Count); // neither caller was suppressed by the gap
    }

    [Fact] public async Task Creating_a_new_room_never_carries_over_a_previous_rooms_pending_alerts()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithCallerList("[]"), // Room A baseline
            V2SnapshotWithCallerList("""[{"seed":"seed1","name":"Kei Joi","timestamp":1000,"phase":null}]"""), // Room A: Kei alerts
            V2Snapshot("ROOM-B", "Draft"), // Room B created fresh
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key") });
        service.ObserveExistingRoom("ROOM-A", "test-room-key");
        Assert.True(await service.PollAsync());
        Assert.True(await service.PollAsync());
        Assert.Single(service.PendingAlertCallers); // Kei is pending in Room A
        service.LeaveGame();

        Assert.True(await service.CreateGameAsync()); // a brand new room

        Assert.Empty(service.PendingAlertCallers); // Room A's pending alert did not leak into the new room
    }

    [Fact] public async Task Legacy_room_snapshot_defaults_caller_and_split_state_to_empty_without_throwing()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        // No v2 route on this "backend" — forces VenueBingoService.PollAsync's legacy GET /api/room-state fallback,
        // exactly like resuming a room the standalone plugin created.
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v2")
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : Task.FromResult(Json("{\"ok\":true,\"roomCode\":\"ROOM\",\"lifecycle\":\"Legacy\",\"calledNumbers\":[],\"allowedSeeds\":[],\"allowedCards\":{},\"players\":{},\"daubs\":{},\"lastBingo\":null,\"bingoCalls\":[],\"costPerCard\":0,\"startingPot\":0,\"prizePercentage\":100,\"gameType\":\"Single Line\",\"letters\":\"BINGO\",\"title\":\"Venue\",\"pot\":{\"paidCards\":0,\"compCards\":0,\"totalCards\":0,\"currentPot\":0,\"prizePool\":0}}")));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());

        Assert.Equal("Legacy", service.ActiveGame.Lifecycle);
        Assert.Empty(service.ActiveGame.BingoCallers); // acceptable limitation, not a bug — a legacy room predates the caller/split concept
        Assert.Equal(0, service.ActiveGame.CurrentPrizePool);
        Assert.Equal(0, service.ActiveGame.SplitAmount);
    }

    // --- Daubs / BallsToBingo (live-QA correction — host Card Viewer daub sync root cause was that this state
    // was never mapped into VenueBingoActiveGame at all, despite already being present on the wire). ---

    [Fact] public async Task V2_snapshot_maps_daubs_and_ballsToBingo_into_active_game_state()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithDaubsAndBalls())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());

        Assert.Equal(3, service.ActiveGame.BallsToBingo["seed1"]);
        Assert.Equal([5, 22], service.GetDaubedNumbers("seed1", 0));
        Assert.Equal([41], service.GetDaubedNumbers("seed1", 1));
    }

    [Fact] public async Task Daub_toggle_off_is_reflected_after_the_next_poll_no_local_ram_dependency()
    {
        // Simulates a player undaubing a number in their browser between two polls — the ONLY source of truth
        // GetDaubedNumbers reads from is whatever the most recent poll's snapshot said, never anything cached
        // locally from an earlier poll.
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var responses = new Queue<string>([
            V2SnapshotWithDaubsAndBalls(), // seed1/card0 daubed: [5, 22]
            """{"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[5,22],"allowedSeeds":["seed1"],"allowedCards":{"seed1":2},"players":{"seed1":{"name":"Alice","count":2,"paidCount":2,"compCount":0}},"daubs":{"seed1":{"0":[5],"1":[41]}},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":2,"compCards":0,"totalCards":2,"currentPot":200,"prizePool":200},"payouts":[],"ballsToBingo":{"seed1":3}}""",
        ]);
        var handler = new Handler(_ => Task.FromResult(Json(responses.Dequeue())));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());
        Assert.Equal([5, 22], service.GetDaubedNumbers("seed1", 0));

        Assert.True(await service.PollAsync()); // player undaubed 22 in their browser between these two polls

        Assert.Equal([5], service.GetDaubedNumbers("seed1", 0)); // reflects the fresh authoritative state, not a stale cached value
    }

    [Fact] public async Task Legacy_room_snapshot_also_maps_daubs_unlike_the_v2_only_caller_split_fields()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v2")
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : Task.FromResult(Json("{\"ok\":true,\"roomCode\":\"ROOM\",\"lifecycle\":\"Legacy\",\"calledNumbers\":[7],\"allowedSeeds\":[\"seed1\"],\"allowedCards\":{\"seed1\":1},\"players\":{},\"daubs\":{\"seed1\":{\"0\":[7]}},\"lastBingo\":null,\"bingoCalls\":[],\"costPerCard\":0,\"startingPot\":0,\"prizePercentage\":100,\"gameType\":\"Single Line\",\"letters\":\"BINGO\",\"title\":\"Venue\",\"pot\":{\"paidCards\":0,\"compCards\":0,\"totalCards\":0,\"currentPot\":0,\"prizePool\":0}}")));
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test") });
        service.ObserveExistingRoom("ROOM", "rk");

        Assert.True(await service.PollAsync());

        Assert.Equal([7], service.GetDaubedNumbers("seed1", 0)); // legacy rooms DO carry daub state — unlike BingoCallers/SplitAmount
        Assert.Empty(service.ActiveGame.BallsToBingo); // ballsToBingo has no legacy equivalent — matches the caller/split precedent
    }

    [Fact] public void GetDaubedNumbers_returns_empty_never_throws_for_an_unknown_seed_or_card()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected")))), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);

        Assert.Empty(service.GetDaubedNumbers("nobody", 0));
    }

    // --- Game Type near Create Game (live-QA correction) ---

    [Fact] public void Game_type_default_persists_across_reload_like_every_other_bingo_default()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var throwingClient = new VenueBingoClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("no HTTP expected"))));
        var service = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        Assert.Equal("Single Line", service.Defaults.GameType); // default

        service.SaveDefaults(service.Defaults with { GameType = "Blackout" });

        var reloaded = new VenueBingoService(throwingClient, profiles, diagnostics, Chat());
        reloaded.Load(profiles.Current.Id);
        Assert.Equal("Blackout", reloaded.Defaults.GameType); // the SAME persistent default the Game Type control (Settings or near Create Game) reads/writes — never a second independent state
    }

    [Fact] public async Task Creating_a_game_sends_the_currently_selected_game_type()
    {
        string? sentBody = null;
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(async request => { sentBody = await request.Content!.ReadAsStringAsync(); return Json(V2Snapshot("ROOM", "Draft")); });
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key"), GameType = "Four Corners" });

        Assert.True(await service.CreateGameAsync());

        Assert.Contains("\"gameType\":\"Four Corners\"", sentBody);
    }

    [Fact] public async Task Changing_the_default_game_type_after_creation_never_retroactively_changes_the_already_created_games_locked_snapshot()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var handler = new Handler(_ => Task.FromResult(Json(V2SnapshotWithLetters("ROOM", "Draft", "BINGO")))); // gameType inside V2SnapshotWithLetters's fixed JSON is "Single Line"
        var service = new VenueBingoService(new VenueBingoClient(new HttpClient(handler)), profiles, diagnostics, Chat());
        service.Load(profiles.Current.Id);
        service.SaveDefaults(service.Defaults with { Connection = new("https://bingo.test", RoomKey: "test-room-key"), GameType = "Single Line" });
        Assert.True(await service.CreateGameAsync());
        Assert.Equal("Single Line", service.ActiveGame.Snapshot!.GameType);

        // The operator changes the venue's default for the NEXT game — must never reach back and alter the
        // already-created (locked) game's own snapshot.
        service.SaveDefaults(service.Defaults with { GameType = "Blackout" });
        Assert.True(await service.PollAsync());

        Assert.Equal("Single Line", service.ActiveGame.Snapshot!.GameType); // unchanged — locked at creation time
    }

    // Plain (non-interpolated) raw-string template + token replacement, matching V2SnapshotWithPot's own rationale.
    private static string V2SnapshotWithCallers() => """
        {"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[],"allowedSeeds":["seed1","seed2"],"allowedCards":{"seed1":1,"seed2":1},"players":{"seed1":{"name":"Alice","count":1,"paidCount":1,"compCount":0},"seed2":{"name":"Bob","count":1,"paidCount":1,"compCount":0}},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":2,"compCards":0,"totalCards":2,"currentPot":200,"prizePool":200},"payouts":[{"payoutId":"p1","winnerSeed":"seed1","winnerName":"Alice","totalOwed":100,"confirmedPaid":0,"outstanding":100,"status":"open","attempts":[]}],"bingoCallers":[{"seed":"seed1","name":"Alice","timestamp":1000,"phase":null},{"seed":"seed2","name":"Bob","timestamp":2000,"phase":null}],"currentPrizePool":1000,"splitAmount":500}
        """;

    // Bingo Call Alert tests: an arbitrary bingoCallers array, everything else a minimal valid v2 snapshot.
    private static string V2SnapshotWithCallerList(string callersJsonArray) => $$"""
        {"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[],"allowedSeeds":[],"allowedCards":{},"players":{},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":0,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":0,"compCards":0,"totalCards":0,"currentPot":0,"prizePool":0},"payouts":[],"bingoCallers":{{callersJsonArray}},"currentPrizePool":0,"splitAmount":0}
        """;

    private static string ExtractJsonString(string json, string field)
    {
        var marker = $"\"{field}\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        return json[start..json.IndexOf('"', start)];
    }

    private static string ExtractQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == key) return Uri.UnescapeDataString(parts[1]);
        }
        return "";
    }

    private static string V2Snapshot(string roomCode, string lifecycle, int costPerCard = 0) => $$"""
        {"ok":true,"roomCode":"{{roomCode}}","lifecycle":"{{lifecycle}}","calledNumbers":[],"allowedSeeds":[],"allowedCards":{},"players":{},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":{{costPerCard}},"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":0,"compCards":0,"totalCards":0,"currentPot":0,"prizePool":0},"payouts":[]}
        """;
    private static string V2SnapshotWithCards() => V2Snapshot("ROOM", "Active");
    // Plain (non-interpolated) raw-string template — daubs/ballsToBingo additions (live-QA host Card Viewer sync).
    private static string V2SnapshotWithDaubsAndBalls() => """
        {"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[5,22],"allowedSeeds":["seed1"],"allowedCards":{"seed1":2},"players":{"seed1":{"name":"Alice","count":2,"paidCount":2,"compCount":0}},"daubs":{"seed1":{"0":[5,22],"1":[41]}},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":2,"compCards":0,"totalCards":2,"currentPot":200,"prizePool":200},"payouts":[],"ballsToBingo":{"seed1":3}}
        """;
    private static string V2SnapshotWithLetters(string roomCode, string lifecycle, string letters) => $$"""
        {"ok":true,"roomCode":"{{roomCode}}","lifecycle":"{{lifecycle}}","calledNumbers":[],"allowedSeeds":[],"allowedCards":{},"players":{},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":0,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"{{letters}}","title":"Venue","colors":null,"pot":{"paidCards":0,"compCards":0,"totalCards":0,"currentPot":0,"prizePool":0},"payouts":[]}
        """;
    // Plain (non-interpolated) raw-string template + token replacement — deliberately NOT $$"""...""" here: this
    // JSON's nesting produces runs of 3+ consecutive closing braces right after an interpolation, which raw-string
    // interpolation cannot disambiguate no matter how many '$' are added without reformatting the JSON itself.
    private static string V2SnapshotWithPot(int paidCards, int compCards, int totalCards, int currentPot, int prizePool) => """
        {"ok":true,"roomCode":"ROOM","lifecycle":"Active","calledNumbers":[],"allowedSeeds":["seed1"],"allowedCards":{"seed1":__TOTAL__},"players":{"seed1":{"name":"Alice","count":__TOTAL__,"paidCount":__PAID__,"compCount":__COMP__}},"daubs":{},"lastBingo":null,"bingoCalls":[],"costPerCard":100,"startingPot":0,"prizePercentage":100,"gameType":"Single Line","gameTypeBase":"Single Line","displayGameType":"Single Line","progressive":null,"letters":"BINGO","title":"Venue","colors":null,"pot":{"paidCards":__PAID__,"compCards":__COMP__,"totalCards":__TOTAL__,"currentPot":__POT__,"prizePool":__PRIZE__},"payouts":[]}
        """.Replace("__PAID__", paidCards.ToString()).Replace("__COMP__", compCards.ToString()).Replace("__TOTAL__", totalCards.ToString()).Replace("__POT__", currentPot.ToString()).Replace("__PRIZE__", prizePool.ToString());

    private static ChatCommandService Chat() => new(new FakeClock(), new InlineFrameworkDispatcher(), _ => true);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
