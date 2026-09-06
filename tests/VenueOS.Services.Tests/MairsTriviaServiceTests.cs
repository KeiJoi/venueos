using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class TriviaActionEligibilityTests
{
    [Theory]
    [InlineData("lobby", true, false, false, false, false, true)]
    [InlineData("preview", false, true, true, false, false, true)]
    [InlineData("question_open", false, false, false, true, false, false)]
    [InlineData("results", true, false, false, false, true, true)]
    [InlineData("finished", false, false, false, false, false, false)]
    [InlineData(null, false, false, false, false, false, false)]
    public void Eligibility_matches_the_backend_state_machine_exactly(string? state, bool preview, bool open, bool skip, bool close, bool repeat, bool endGame)
    {
        var e = TriviaActionEligibility.For(state);
        Assert.Equal(preview, e.CanPreview);
        Assert.Equal(open, e.CanOpen);
        Assert.Equal(skip, e.CanSkip);
        Assert.Equal(close, e.CanClose);
        Assert.Equal(repeat, e.CanRepeat);
        Assert.Equal(endGame, e.CanEndGame);
    }

    [Fact] public void Preview_is_never_eligible_at_the_same_time_as_open_or_skip()
    {
        foreach (var state in new[] { "lobby", "preview", "question_open", "results", "finished" })
        {
            var e = TriviaActionEligibility.For(state);
            Assert.False(e.CanPreview && (e.CanOpen || e.CanSkip));
        }
    }
}

public sealed class MairsTriviaServiceLiveConsoleTests
{
    private const string GameId = "11111111-1111-4111-8111-111111111111";
    private const string QuestionId = "22222222-2222-4222-8222-222222222222";

    private static string HostState(string state, string? activeQuestionId = QuestionId, string players = "[]", string leaderboard = "[]") => $$"""
        {"id":"{{GameId}}","joinCode":"ABC123","playerUrl":"https://test/play/ABC123","venueName":"Venue","gameName":"Test","state":"{{state}}",
         "scoring":{"correctPoints":100,"incorrectPoints":0,"firstCorrectBonus":50,"allowAnswerChange":false,"timeBonusMultiplier":5},
         "questionTimeLimitSeconds":0,"cumulativeScoring":false,"cumulativePlayers":[],
         "activeSetId":null,"activeQuestionId":{{(activeQuestionId is null ? "null" : $"\"{activeQuestionId}\"")}},"activeQuestionClosesAt":null,
         "players":{{players}},"leaderboard":{{leaderboard}},"seriesId":null,"seriesStandings":null,"attachedSourceSetIds":[]}
        """;
    private const string PreviewedQuestion = $$"""
        {"id":"{{QuestionId}}","question":"This is a test press 1","correctAnswer":"1","incorrectAnswers":["2","3","4"],"category":null,"tags":[]}
        """;

    private static string SeriesState(string id, string state, string? currentGameId, string participants = "[]", string standings = "[]") => $$"""
        {"id":"{{id}}","name":"Series","state":"{{state}}","joinCode":"XYZ999","playerUrl":"https://test/play/XYZ999","currentGameId":{{(currentGameId is null ? "null" : $"\"{currentGameId}\"")}},"games":[],"participants":{{participants}},"standings":{{standings}}}
        """;
    private static HttpResponseMessage ErrorJson(string code, string message = "Access Token has expired", HttpStatusCode status = HttpStatusCode.Unauthorized) => new(status) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", Encoding.UTF8, "application/json") };
    private static TriviaQuestionSet ReadySet() => new("fftrivia-question-set", 2, Guid.NewGuid(), "Set", "", "Author", "1.0", [], [], [new TriviaQuestion(Guid.NewGuid(), "Q?", "Right", ["A", "B", "C"], null, [])]);

    private static (MairsTriviaService Service, ScriptedHandler Handler) Build(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => BuildWith(new FakeRepository(), respond);
    private static (MairsTriviaService Service, ScriptedHandler Handler) BuildWith(FakeRepository repository, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new ScriptedHandler(respond);
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new MairsTriviaService(new MairsTriviaClient(new HttpClient(handler)), profiles, repository, diagnostics);
        service.Load(profiles.Current.Id);
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { BaseUrl = "https://test", AccessToken = "token", ServerAccessPassword = "server-secret" } });
        return (service, handler);
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact] public async Task Successful_preview_refreshes_authoritative_state_from_lobby_to_preview_and_retains_the_question()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/games/11111111-1111-4111-8111-111111111111/questions/preview" => Json(PreviewedQuestion),
            "/v1/games/11111111-1111-4111-8111-111111111111" => Json(HostState("preview")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        }));
        // Seed CurrentGame at "lobby" through the real public Resume path before exercising Preview.
        handler.Override("/v1/games/11111111-1111-4111-8111-111111111111", () => Task.FromResult(Json(HostState("lobby"))));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.Equal("lobby", service.CurrentGame!.State);

        handler.ClearOverride("/v1/games/11111111-1111-4111-8111-111111111111"); // now the script above answers with "preview" host state
        var succeeded = await service.PreviewAsync();

        Assert.True(succeeded);
        Assert.Equal("preview", service.CurrentGame!.State); // this is the exact live bug: this used to stay "lobby"
        Assert.NotNull(service.Preview);
        Assert.Equal("This is a test press 1", service.Preview!.Question);
        Assert.Equal("1", service.Preview.CorrectAnswer);

        var eligibility = TriviaActionEligibility.For(service.CurrentGame.State);
        Assert.True(eligibility.CanOpen);
        Assert.True(eligibility.CanSkip);
        Assert.False(eligibility.CanPreview); // not eligible again while already previewing the same question
    }

    [Fact] public async Task One_preview_click_sends_exactly_one_preview_mutation_even_though_state_is_refreshed_afterward()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/games/11111111-1111-4111-8111-111111111111/questions/preview" => Json(PreviewedQuestion),
            "/v1/games/11111111-1111-4111-8111-111111111111" => Json(HostState("lobby")),
            _ => throw new InvalidOperationException(),
        }));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        handler.Override("/v1/games/11111111-1111-4111-8111-111111111111", () => Task.FromResult(Json(HostState("preview"))));

        await service.PreviewAsync();

        Assert.Equal(1, handler.RequestedPaths.Count(p => p.EndsWith("/questions/preview", StringComparison.Ordinal)));
    }

    [Fact] public async Task A_second_preview_click_while_the_first_is_still_in_flight_is_dropped_not_duplicated()
    {
        var gate = new TaskCompletionSource();
        var previewCalls = 0;
        var (service, handler) = Build(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/questions/preview", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref previewCalls);
                await gate.Task; // genuinely suspends (not a thread-blocking call) until the test releases it below
                return Json(PreviewedQuestion);
            }
            return Json(HostState(previewCalls > 0 ? "preview" : "lobby"));
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.Equal("lobby", service.CurrentGame!.State);

        var first = service.PreviewAsync();   // starts, suspends awaiting the gated HTTP response
        var second = service.PreviewAsync();  // fired while the first is still in flight — must be dropped by the mutation guard, not sent
        var secondResult = await second;
        gate.SetResult();
        var firstResult = await first;

        Assert.False(secondResult); // dropped locally — never reached the network
        Assert.True(firstResult);
        Assert.Equal(1, previewCalls); // exactly one backend mutation for the whole sequence
    }

    [Fact] public async Task Open_transitions_preview_to_question_open_and_close_transitions_question_open_to_results()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/games/11111111-1111-4111-8111-111111111111/questions/open" => Json(HostState("question_open")),
            "/v1/games/11111111-1111-4111-8111-111111111111/questions/close" =>
                Json($$$"""{"game":{{{HostState("results")}}},"result":{"questionId":"{{{QuestionId}}}","correctAnswer":"1","firstResponder":null}}"""),
            "/v1/games/11111111-1111-4111-8111-111111111111" => Json(HostState("preview")),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString()),
        }));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.Equal("preview", service.CurrentGame!.State);

        Assert.True(await service.OpenAsync());
        Assert.Equal("question_open", service.CurrentGame!.State);
        Assert.True(TriviaActionEligibility.For(service.CurrentGame.State).CanClose);

        Assert.True(await service.CloseAsync());
        Assert.Equal("results", service.CurrentGame!.State);
        var afterClose = TriviaActionEligibility.For(service.CurrentGame.State);
        Assert.True(afterClose.CanPreview); // results progresses back to Preview Next
        Assert.True(afterClose.CanRepeat);
    }

    [Fact] public async Task A_genuine_backend_rejection_still_surfaces_normally_when_it_is_not_a_duplicate_internal_request()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/games/{GameId}"
            ? Json(HostState("lobby"))
            : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":{\"code\":\"invalid_state\",\"message\":\"A question cannot be previewed now.\"}}", Encoding.UTF8, "application/json") }));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        var succeeded = await service.PreviewAsync();

        Assert.False(succeeded);
        Assert.Equal("A question cannot be previewed now.", service.LastError);
    }

    // ------------------------------------------------------------------------------------------ live sync (QA fix #2)
    [Fact] public async Task PollAsync_picks_up_a_newly_joined_player_and_updated_standings_without_any_host_mutation()
    {
        var playerJoined = false;
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/games/{GameId}"
            ? Json(HostState("lobby",
                players: playerJoined ? """[{"id":"33333333-3333-4333-8333-333333333333","displayName":"Newcomer","score":0,"correctCount":0,"incorrectCount":0,"removed":false}]""" : "[]",
                leaderboard: playerJoined ? """[{"rank":1,"id":"33333333-3333-4333-8333-333333333333","displayName":"Newcomer","score":0}]""" : "[]"))
            : throw new InvalidOperationException(request.RequestUri!.ToString())));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.Empty(service.CurrentGame!.Players);

        playerJoined = true; // a player joined via the browser — no host action was taken
        await service.PollAsync();

        Assert.Single(service.CurrentGame!.Players);
        Assert.Equal("Newcomer", service.CurrentGame.Players[0].DisplayName);
        Assert.Single(service.CurrentGame.Leaderboard);
    }

    [Fact] public async Task PollAsync_discovers_a_server_side_transition_out_of_a_stale_question_open_belief()
    {
        var backendState = "question_open";
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/games/{GameId}" ? Json(HostState(backendState)) : throw new InvalidOperationException()));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.Equal("question_open", service.CurrentGame!.State);

        backendState = "results"; // the backend moved on without any VenueOS mutation observing it directly
        await service.PollAsync();

        Assert.Equal("results", service.CurrentGame!.State);
        Assert.True(TriviaActionEligibility.For(service.CurrentGame.State).CanPreview);
    }

    [Fact] public async Task A_four_question_set_progresses_through_all_four_questions_without_ending_the_game_after_the_first()
    {
        string[] questionIds = ["aaaaaaaa-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000002", "aaaaaaaa-0000-4000-8000-000000000003", "aaaaaaaa-0000-4000-8000-000000000004"];
        var completed = new List<string>();
        var current = "lobby"; var activeQuestion = questionIds[0];
        var (service, _) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/questions/preview", StringComparison.Ordinal)) { current = "preview"; return Task.FromResult(Json($$"""{"id":"{{activeQuestion}}","question":"Q","correctAnswer":"1","incorrectAnswers":["2","3","4"],"category":null,"tags":[]}""")); }
            if (path.EndsWith("/questions/open", StringComparison.Ordinal)) { current = "question_open"; return Task.FromResult(Json(HostState(current, activeQuestion))); }
            if (path.EndsWith("/questions/close", StringComparison.Ordinal))
            {
                current = "results"; completed.Add(activeQuestion);
                var nextIndex = Array.IndexOf(questionIds, activeQuestion) + 1;
                if (nextIndex < questionIds.Length) activeQuestion = questionIds[nextIndex];
                return Task.FromResult(Json($$$"""{"game":{{{HostState(current, activeQuestion)}}},"result":{"questionId":"{{{completed[^1]}}}","correctAnswer":"1","firstResponder":null}}"""));
            }
            if (path == $"/v1/games/{GameId}") return Task.FromResult(Json(HostState(current, activeQuestion)));
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        for (var i = 0; i < 4; i++)
        {
            Assert.True(await service.PreviewAsync());
            Assert.Equal("preview", service.CurrentGame!.State);
            Assert.True(await service.OpenAsync());
            Assert.Equal("question_open", service.CurrentGame!.State);
            Assert.True(await service.CloseAsync());
            Assert.Equal("results", service.CurrentGame!.State); // never auto-transitions to "finished" merely because a question closed
            Assert.NotEqual("finished", service.CurrentGame.State);
        }
        Assert.Equal(questionIds.ToList(), completed);
        Assert.NotEqual("finished", service.CurrentGame!.State); // still requires an explicit End Game after the final question
    }

    [Fact] public async Task Tick_does_nothing_when_no_game_or_series_is_active()
    {
        var (service, handler) = Build(request => Task.FromResult(Json(HostState("lobby"))));
        service.Tick(DateTimeOffset.UtcNow);
        await Task.Delay(30);
        Assert.Empty(handler.RequestedPaths);
    }

    [Fact] public async Task Tick_polls_at_most_once_per_interval_and_applies_the_authoritative_result()
    {
        var backendState = "lobby";
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/games/{GameId}" ? Json(HostState(backendState)) : throw new InvalidOperationException()));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        var baseline = handler.RequestedPaths.Count;

        var now = DateTimeOffset.UtcNow;
        backendState = "preview";
        service.Tick(now); // does not depend on Draw() — this test never touches any rendering code at all
        await Task.Delay(50);
        Assert.Equal("preview", service.CurrentGame!.State);
        var afterFirstTick = handler.RequestedPaths.Count;
        Assert.True(afterFirstTick > baseline);

        service.Tick(now); // same instant: interval has not elapsed, must not poll again
        await Task.Delay(50);
        Assert.Equal(afterFirstTick, handler.RequestedPaths.Count);

        backendState = "question_open";
        service.Tick(now + TimeSpan.FromSeconds(3)); // interval elapsed
        await Task.Delay(50);
        Assert.Equal("question_open", service.CurrentGame!.State);
        Assert.True(handler.RequestedPaths.Count > afterFirstTick);
    }

    [Fact] public async Task Tick_does_not_poll_while_a_mutation_is_in_flight()
    {
        var gate = new TaskCompletionSource();
        var (service, handler) = Build(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/questions/preview", StringComparison.Ordinal)) { await gate.Task; return Json(PreviewedQuestion); }
            return Json(HostState("lobby"));
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        var previewTask = service.PreviewAsync(); // now in flight, suspended on the gate
        await Task.Delay(10); // let the preview request itself register before capturing the baseline
        var baseline = handler.RequestedPaths.Count;
        service.Tick(DateTimeOffset.UtcNow); // must be a no-op while a mutation is in flight, not a competing GetGame call
        await Task.Delay(30);
        Assert.Equal(baseline, handler.RequestedPaths.Count);

        gate.SetResult();
        Assert.True(await previewTask);
    }

    // -------------------------------------------------------------------------------------- credential persistence
    [Fact] public void Password_and_server_access_password_persist_through_a_settings_save_and_a_simulated_restart()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var repository = new FakeRepository();
        var client = new MairsTriviaClient(new HttpClient(new ScriptedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        var service = new MairsTriviaService(client, profiles, repository, diagnostics);
        service.Load(profiles.Current.Id);
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { Password = "hunter2", ServerAccessPassword = "door-code" } });

        // A brand-new service instance reading the same persisted store — simulates a plugin/game restart.
        var reloaded = new MairsTriviaService(client, profiles, repository, diagnostics);
        reloaded.Load(profiles.Current.Id);

        Assert.Equal("hunter2", reloaded.Settings.Connection.Password);
        Assert.Equal("door-code", reloaded.Settings.Connection.ServerAccessPassword);
    }

    [Fact] public async Task Successful_login_persists_and_never_clears_the_configured_password()
    {
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/v1/auth/login"
            ? Json("""{"accessToken":"a","refreshToken":"r","user":{"id":"44444444-4444-4444-8444-444444444444","username":"Mair","createdAt":"2026-01-01T00:00:00Z"}}""")
            : throw new InvalidOperationException()));

        var succeeded = await service.LoginAsync("Mair", "hunter2");

        Assert.True(succeeded);
        Assert.Equal("hunter2", service.Settings.Connection.Password); // Decision: never cleared on a successful sign-in
    }

    [Fact] public async Task Auto_reconnect_refreshes_the_session_on_load_when_a_refresh_token_is_already_stored()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        profiles.SaveModuleConfig(profiles.Current.Id, "games.trivia", 1, new MairsTriviaSettings(new("https://test", AccessToken: "stale", RefreshToken: "stored-refresh"), "Game", new(100, 0, 50)));
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var refreshed = new TaskCompletionSource();
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/auth/refresh") { refreshed.TrySetResult(); return Task.FromResult(Json("""{"accessToken":"fresh","refreshToken":"stored-refresh"}""")); }
            throw new InvalidOperationException(request.RequestUri!.ToString());
        });
        var service = new MairsTriviaService(new MairsTriviaClient(new HttpClient(handler)), profiles, new FakeRepository(), diagnostics);

        service.Load(profiles.Current.Id); // no explicit RefreshAsync() call anywhere in this test — auto-connect must fire it

        await Task.WhenAny(refreshed.Task, Task.Delay(500));
        Assert.True(refreshed.Task.IsCompletedSuccessfully);
        await Task.Delay(30); // let Configure() land after the awaited response
        Assert.Equal("fresh", service.Settings.Connection.AccessToken);
    }

    // ------------------------------------------------------------------------------------- finished-game navigation (QA fix #3)
    [Fact] public async Task Finished_standalone_game_stops_periodic_polling_and_preserves_the_final_result()
    {
        var getGameCalls = 0;
        var (service, handler) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/games/{GameId}/end") return Task.FromResult(Json("""{"winners":[{"rank":1,"id":"55555555-5555-4555-8555-555555555555","displayName":"Kei Joi","score":290}],"standings":[{"rank":1,"id":"55555555-5555-4555-8555-555555555555","displayName":"Kei Joi","score":290}],"seriesId":null,"seriesStandings":null}"""));
            if (path == $"/v1/games/{GameId}") { getGameCalls++; return Task.FromResult(Json(HostState("results"))); }
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        var callsAfterResume = getGameCalls;

        var result = await service.EndGameAsync();
        Assert.NotNull(result);
        Assert.Equal("finished", service.CurrentGame!.State);
        Assert.Equal("Kei Joi", result!.Winners[0].DisplayName);
        Assert.Equal(290, result.Winners[0].Score);

        // Tick repeatedly the way Framework.Update would every frame — a finished Game must never poll again.
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) { service.Tick(now + TimeSpan.FromSeconds(i * 3)); await Task.Delay(15); }
        Assert.Equal(callsAfterResume, getGameCalls); // no additional GetGame calls after End
    }

    [Fact] public async Task Back_to_setup_clears_local_game_context_without_calling_the_backend_again()
    {
        var endCalls = 0;
        var (service, handler) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/games/{GameId}/end") { endCalls++; return Task.FromResult(Json("""{"winners":[],"standings":[],"seriesId":null,"seriesStandings":null}""")); }
            if (path == $"/v1/games/{GameId}") return Task.FromResult(Json(HostState("results")));
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.NotNull(await service.EndGameAsync());
        Assert.Equal(1, endCalls);
        var accessTokenBefore = service.Settings.Connection.AccessToken;
        var defaultsBefore = service.Settings.DefaultGameName;

        service.ReturnToSetup();

        Assert.Null(service.CurrentGame);
        Assert.Null(service.Preview);
        Assert.Null(service.LastQuestionResult);
        Assert.Equal(1, endCalls); // never calls End/Delete again
        Assert.Equal(accessTokenBefore, service.Settings.Connection.AccessToken); // auth/session preserved
        Assert.Equal(defaultsBefore, service.Settings.DefaultGameName); // Trivia defaults preserved
    }

    [Fact] public async Task Back_to_setup_allows_a_new_game_to_be_created_afterward()
    {
        var repository = new FakeRepository { ReadResult = ReadySet() };
        const string newGameId = "66666666-6666-4666-8666-666666666666";
        var (service, handler) = BuildWith(repository, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/games/{GameId}") return Task.FromResult(Json(HostState("results")));
            if (path == "/v1/games") return Task.FromResult(Json(HostState("lobby").Replace(GameId, newGameId)));
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        service.ReturnToSetup();
        Assert.Null(service.CurrentGame);

        var created = await service.CreateStandaloneGameAsync(Guid.NewGuid(), "New Game");

        Assert.True(created);
        Assert.NotNull(service.CurrentGame);
        Assert.Equal(newGameId, service.CurrentGame!.Id.ToString());
    }

    [Fact] public async Task An_in_flight_poll_completing_after_back_to_setup_cannot_resurrect_the_cleared_game()
    {
        var gate = new TaskCompletionSource();
        var getGameCalls = 0;
        var (service, handler) = Build(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/games/{GameId}/end") return Json("""{"winners":[],"standings":[],"seriesId":null,"seriesStandings":null}""");
            if (path == $"/v1/games/{GameId}")
            {
                getGameCalls++;
                if (getGameCalls == 1) return Json(HostState("results")); // the initial Resume seed
                await gate.Task; // the second call simulates a poll already in flight before End/ReturnToSetup
                return Json(HostState("results"));
            }
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        var stalePoll = service.PollAsync(); // dispatches the gated second GetGame call
        await Task.Delay(10);
        Assert.NotNull(await service.EndGameAsync());
        Assert.Equal("finished", service.CurrentGame!.State);
        service.ReturnToSetup();
        Assert.Null(service.CurrentGame);

        gate.SetResult(); // the stale poll's response arrives only now
        await stalePoll;

        Assert.Null(service.CurrentGame); // must not have been resurrected by the late response
    }

    [Fact] public async Task Routine_background_poll_never_sets_the_user_facing_busy_flag()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/games/{GameId}" ? Json(HostState("lobby")) : throw new InvalidOperationException()));
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));
        Assert.False(service.Busy);

        await service.PollAsync();

        Assert.False(service.Busy); // silent background sync — no "Working" flash for routine polling
    }

    [Fact] public async Task Explicit_mutation_still_shows_busy_while_in_flight()
    {
        var gate = new TaskCompletionSource();
        var (service, handler) = Build(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/questions/preview", StringComparison.Ordinal)) { await gate.Task; return Json(PreviewedQuestion); }
            return Json(HostState("lobby"));
        });
        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        var previewTask = service.PreviewAsync();
        await Task.Delay(10);
        Assert.True(service.Busy); // an explicit operator action still shows Working while in flight

        gate.SetResult();
        Assert.True(await previewTask);
        Assert.False(service.Busy);
    }

    [Fact] public async Task Resumable_games_list_lets_callers_filter_out_finished_games()
    {
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/v1/games"
            ? Json("""[{"id":"11111111-1111-4111-8111-111111111111","joinCode":"A","venueName":"V","gameName":"Old","state":"finished","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","attachedSourceSetIds":[]},{"id":"22222222-2222-4222-8222-222222222222","joinCode":"B","venueName":"V","gameName":"Active","state":"lobby","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","attachedSourceSetIds":[]}]""")
            : throw new InvalidOperationException()));

        await service.RefreshResumableGamesAsync();
        var resumable = service.ResumableGames.Where(g => g.State != "finished").ToList(); // mirrors MairsTriviaOperatorPanel.DrawSetup's own filter

        Assert.Equal(2, service.ResumableGames.Count); // full history is returned...
        Assert.Single(resumable); // ...but only the non-finished game is ever offered as resumable
        Assert.Equal("Active", resumable[0].GameName);
    }

    [Fact] public async Task A_finished_series_game_retains_active_series_context_and_can_start_the_next_game()
    {
        const string seriesId = "77777777-7777-4777-8777-777777777777";
        const string nextGameId = "88888888-8888-4888-8888-888888888888";
        var repository = new FakeRepository { ReadResult = ReadySet() };
        var currentGameState = "results";
        var (service, handler) = BuildWith(repository, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/series/{seriesId}") return Task.FromResult(Json(SeriesState(seriesId, "active", GameId)));
            if (path == $"/v1/games/{GameId}/end") { currentGameState = "finished"; return Task.FromResult(Json($$"""{"winners":[],"standings":[],"seriesId":"{{seriesId}}","seriesStandings":[]}""")); }
            if (path == $"/v1/games/{GameId}") return Task.FromResult(Json(HostState(currentGameState)));
            if (path == $"/v1/series/{seriesId}/games") return Task.FromResult(Json(HostState("lobby").Replace(GameId, nextGameId)));
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeSeriesAsync(Guid.Parse(seriesId)));
        Assert.Equal("results", service.CurrentGame!.State);

        Assert.NotNull(await service.EndGameAsync());
        Assert.Equal("finished", service.CurrentGame!.State);
        Assert.NotNull(service.CurrentSeries); // Series context retained — Game completion never clears it
        Assert.Equal("active", service.CurrentSeries!.State);

        // The finished Game must stop polling, but the still-active Series must keep synchronizing.
        var seriesPollsBefore = handler.RequestedPaths.Count(p => p == $"/v1/series/{seriesId}");
        var gamePollsBefore = handler.RequestedPaths.Count(p => p == $"/v1/games/{GameId}");
        service.Tick(DateTimeOffset.UtcNow);
        await Task.Delay(30);
        Assert.True(handler.RequestedPaths.Count(p => p == $"/v1/series/{seriesId}") > seriesPollsBefore);
        Assert.Equal(gamePollsBefore, handler.RequestedPaths.Count(p => p == $"/v1/games/{GameId}"));

        Assert.True(await service.StartNextGameInSeriesAsync(Guid.NewGuid(), "Game 2"));
        Assert.NotEqual("finished", service.CurrentGame!.State);
    }

    [Fact] public async Task Back_to_setup_never_ends_or_clears_an_active_series()
    {
        const string seriesId = "77777777-7777-4777-8777-777777777777";
        var endSeriesCalls = 0;
        var (service, handler) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/v1/series/{seriesId}") return Task.FromResult(Json(SeriesState(seriesId, "active", null)));
            if (path == $"/v1/series/{seriesId}/end") { endSeriesCalls++; return Task.FromResult(Json("""{"champions":[],"standings":[]}""")); }
            throw new InvalidOperationException(path);
        });
        Assert.True(await service.ResumeSeriesAsync(Guid.Parse(seriesId)));

        service.ReturnToSetup();

        Assert.Equal(0, endSeriesCalls);
        Assert.NotNull(service.CurrentSeries);
        Assert.Equal("active", service.CurrentSeries!.State);
    }

    [Fact] public async Task Completed_series_back_to_setup_clears_context_and_returns_to_normal_setup()
    {
        const string seriesId = "77777777-7777-4777-8777-777777777777";
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/series/{seriesId}" ? Json(SeriesState(seriesId, "finished", null)) : throw new InvalidOperationException()));
        Assert.True(await service.ResumeSeriesAsync(Guid.Parse(seriesId)));
        Assert.Equal("finished", service.CurrentSeries!.State);

        service.ReturnToSetup();

        Assert.Null(service.CurrentSeries);
        Assert.Null(service.CurrentGame);
    }

    [Fact] public async Task Auth_recovery_refreshes_an_expired_access_token_and_transparently_retries_the_original_request()
    {
        var gameCalls = 0;
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/auth/refresh" => Json("""{"accessToken":"new-token","refreshToken":"new-refresh"}"""),
            "/v1/games/11111111-1111-4111-8111-111111111111" => Interlocked.Increment(ref gameCalls) == 1 ? ErrorJson("expired_token") : Json(HostState("lobby")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        }));
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { RefreshToken = "stored-refresh" } });

        var succeeded = await service.ResumeGameAsync(Guid.Parse(GameId));

        Assert.True(succeeded);
        Assert.NotNull(service.CurrentGame);
        Assert.Null(service.LastError); // the operator must never see "Access Token has expired" when recovery succeeds
        Assert.Equal(1, handler.RequestedPaths.Count(p => p == "/v1/auth/refresh"));
        Assert.Equal(2, gameCalls); // the original call, then exactly one retry after recovery
        Assert.Equal("new-token", service.Settings.Connection.AccessToken);
        Assert.Equal("new-refresh", service.Settings.Connection.RefreshToken);
    }

    [Fact] public async Task Auth_recovery_falls_back_to_automatic_login_when_the_stored_refresh_token_is_rejected()
    {
        var gameCalls = 0;
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/auth/refresh" => ErrorJson("invalid_token"),
            "/v1/auth/login" => Json("""{"accessToken":"login-token","refreshToken":"login-refresh","user":{"id":"33333333-3333-4333-8333-333333333333","username":"host","createdAt":"2024-01-01T00:00:00Z"}}"""),
            "/v1/games/11111111-1111-4111-8111-111111111111" => Interlocked.Increment(ref gameCalls) == 1 ? ErrorJson("expired_token") : Json(HostState("lobby")),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        }));
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { RefreshToken = "stored-refresh", Username = "host", Password = "correct horse battery staple" } });

        var succeeded = await service.ResumeGameAsync(Guid.Parse(GameId));

        Assert.True(succeeded);
        Assert.Null(service.LastError);
        Assert.Equal(1, handler.RequestedPaths.Count(p => p == "/v1/auth/refresh"));
        Assert.Equal(1, handler.RequestedPaths.Count(p => p == "/v1/auth/login"));
        Assert.Equal("login-token", service.Settings.Connection.AccessToken);
    }

    [Fact] public async Task Auth_recovery_surfaces_exactly_one_error_when_both_refresh_and_login_fail()
    {
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/auth/refresh" => ErrorJson("invalid_token"),
            "/v1/auth/login" => ErrorJson("invalid_login", "Incorrect username or password."),
            "/v1/games/11111111-1111-4111-8111-111111111111" => ErrorJson("expired_token"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        }));
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { RefreshToken = "stored-refresh", Username = "host", Password = "wrong" } });

        var succeeded = await service.ResumeGameAsync(Guid.Parse(GameId));

        Assert.False(succeeded);
        Assert.Equal("Session expired and automatic sign-in failed.", service.LastError); // one useful message, not the raw backend "expired" text and not a retry loop
    }

    [Fact] public async Task A_non_auth_expiry_backend_error_never_triggers_automatic_recovery()
    {
        var (service, handler) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/games/11111111-1111-4111-8111-111111111111" => ErrorJson("game_not_found", "Game not found.", HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        }));
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { RefreshToken = "stored-refresh" } });

        var succeeded = await service.ResumeGameAsync(Guid.Parse(GameId));

        Assert.False(succeeded);
        Assert.Equal("Game not found.", service.LastError);
        Assert.Equal(0, handler.RequestedPaths.Count(p => p == "/v1/auth/refresh")); // invalid_login/invalid_server_access-style literal rejections and unrelated errors must never trigger recovery
    }

    [Fact] public async Task Concurrent_expired_requests_trigger_exactly_one_refresh_attempt()
    {
        var refreshGate = new TaskCompletionSource<HttpResponseMessage>();
        var refreshCalls = 0;
        var gamesCalls = 0;
        var (service, handler) = Build(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/auth/refresh") { Interlocked.Increment(ref refreshCalls); return refreshGate.Task; }
            if (path == "/v1/games") { var call = Interlocked.Increment(ref gamesCalls); return Task.FromResult(call <= 2 ? ErrorJson("expired_token") : Json("[]")); }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        service.Configure(service.Settings with { Connection = service.Settings.Connection with { RefreshToken = "stored-refresh" } });

        var task1 = service.RefreshResumableGamesAsync();
        var task2 = service.RefreshResumableGamesAsync();
        await Task.Delay(20); // let both requests reach the 401 and start recovery before the refresh call resolves
        refreshGate.SetResult(Json("""{"accessToken":"new-token","refreshToken":"new-refresh"}"""));
        await Task.WhenAll(task1, task2);

        Assert.Equal(1, refreshCalls); // single-flight: two concurrent expired requests share one recovery attempt
        Assert.Null(service.LastError);
    }

    [Fact] public async Task Leaderboard_entries_carry_correct_and_incorrect_counts_for_the_correct_over_answered_QOL_display()
    {
        const string leaderboard = """[{"rank":1,"id":"44444444-4444-4444-8444-444444444444","displayName":"Alice","score":150,"correctCount":3,"incorrectCount":1}]""";
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/v1/games/11111111-1111-4111-8111-111111111111" ? Json(HostState("lobby", leaderboard: leaderboard)) : throw new InvalidOperationException()));

        Assert.True(await service.ResumeGameAsync(Guid.Parse(GameId)));

        var entry = service.CurrentGame!.Leaderboard.Single();
        Assert.Equal(3, entry.CorrectCount);
        Assert.Equal(1, entry.IncorrectCount);
        Assert.Equal(4, entry.Answered); // always correct+incorrect — never a source-question-count denominator
    }

    [Fact] public async Task Series_state_exposes_the_persistent_player_url_and_enriched_participant_stats()
    {
        const string seriesId = "88888888-8888-4888-8888-888888888888";
        const string participants = """[{"id":"99999999-9999-4999-8999-999999999999","displayName":"Bob","removed":false,"score":220,"correctCount":5,"incorrectCount":2}]""";
        var (service, _) = Build(request => Task.FromResult(request.RequestUri!.AbsolutePath == $"/v1/series/{seriesId}" ? Json(SeriesState(seriesId, "active", null, participants)) : throw new InvalidOperationException()));

        Assert.True(await service.ResumeSeriesAsync(Guid.Parse(seriesId)));

        Assert.Equal("https://test/play/XYZ999", service.CurrentSeries!.PlayerUrl); // the ONE persistent Series link — same one shown/copied from DrawSeriesConsole
        var participant = service.CurrentSeries.Participants.Single();
        Assert.Equal(220, participant.Score);
        Assert.Equal(5, participant.CorrectCount);
        Assert.Equal(2, participant.IncorrectCount);
        Assert.Equal(7, participant.Answered);
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    private sealed class FakeRepository : IQuestionSetRepository
    {
        public TriviaQuestionSet? ReadResult { get; set; }
        public IReadOnlyList<QuestionLibraryIndexEntry> List() => [];
        public TriviaQuestionSet? Read(Guid id) => ReadResult;
        public void Save(TriviaQuestionSet set) { }
        public bool Delete(Guid id, Func<Guid, bool> isInUse) => false;
        public TriviaQuestionSet Duplicate(Guid id, string? title = null) => throw new NotSupportedException();
        public ImportCollisionInfo? CheckImportCollision(string fftriviaJson) => null;
        public TriviaQuestionSet ImportReplacing(string fftriviaJson) => throw new NotSupportedException();
        public TriviaQuestionSet ImportAsNew(string fftriviaJson, string? title = null) => throw new NotSupportedException();
        public string Export(Guid id) => "";
        public void Reorder(IReadOnlyList<Guid> orderedIdsInDesiredOrder) { }
        public IReadOnlyList<string> RebuildIndex() => [];
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<Task<HttpResponseMessage>>> overrides = [];
        public List<string> RequestedPaths { get; } = [];
        public void Override(string path, Func<Task<HttpResponseMessage>> response) => overrides[path] = response;
        public void ClearOverride(string path) => overrides.Remove(path);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            return await (overrides.TryGetValue(path, out var over) ? over() : respond(request));
        }
    }
}
