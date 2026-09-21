using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Brackets first-time Organizer authentication (docs/BRACKETS_FIRST_AUTH_INVESTIGATION.md): request counts per operator action,
/// the state left behind by Create Organizer, HTTP 429 handling, venue isolation, and honest key-vs-session terminology. Everything runs
/// through the real <see cref="TournamentControlService"/> and <see cref="TournamentControlClient"/> against a counting fake HTTP handler.</summary>
public sealed class TournamentAuthFlowTests
{
    private const string Server = "https://tournament.test";
    private const string Password = "server-password-SECRET";
    private const string Key = "Northern!Comet2026-Alpha-SECRET";

    // ---- request counts -------------------------------------------------------------------------------------------------------------
    [Fact] public async Task One_Create_Organizer_click_is_exactly_one_request_and_leaves_the_venue_signed_in_and_persisted()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/organizers", () => Session("tok-created"));
        var result = await rig.Service.CreateOrganizerAsync();
        Assert.True(result.Success);
        Assert.Equal(["POST /api/controller/organizers"], rig.Requests);
        Assert.True(rig.Service.Dashboard.IsAuthenticated);
        Assert.Equal("tok-created", rig.Service.Settings.Connection.AccessToken);
        Assert.Empty(rig.Service.Tournaments); Assert.Equal("Loaded 0 tournament(s).", rig.Service.BrowserStatus);
        Assert.Equal(TournamentAuthStatusKind.Success, rig.Service.AuthStatus!.Kind);
        var reloaded = rig.NewService(); reloaded.Load(rig.Venue.Id); // a genuinely fresh service reading only what was persisted
        Assert.Equal("tok-created", reloaded.Settings.Connection.AccessToken); Assert.True(reloaded.Dashboard.IsAuthenticated);
    }

    [Fact] public async Task One_Authenticate_click_is_exactly_a_login_then_one_list_request()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => Session("tok-login")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        var result = await rig.Service.AuthenticateAsync();
        Assert.True(result.Success);
        Assert.Equal(["POST /api/controller/sessions", "GET /api/controller/tournaments"], rig.Requests);
        Assert.Equal("Bearer tok-login", rig.AuthorizationOf("GET /api/controller/tournaments"));
    }

    [Fact] public async Task Authenticate_right_after_Create_Organizer_sends_no_login_at_all_only_one_confirming_list_request()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/organizers", () => Session("tok-created")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        await rig.Service.CreateOrganizerAsync(); rig.Requests.Clear();
        var result = await rig.Service.AuthenticateAsync();
        Assert.True(result.Success);
        Assert.Equal(["GET /api/controller/tournaments"], rig.Requests);
        Assert.DoesNotContain(rig.Requests, r => r.StartsWith("POST"));
        Assert.Equal("Already signed in", rig.Service.AuthStatus!.Title);
    }

    [Fact] public async Task Authenticate_after_the_Organizer_Key_was_edited_does_a_real_login_because_the_session_belongs_to_the_old_key()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/organizers", () => Session("tok-created")); rig.Respond("POST /api/controller/sessions", () => Session("tok-other")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        await rig.Service.CreateOrganizerAsync(); rig.Requests.Clear();
        rig.Service.Configure(rig.Service.Settings with { Connection = rig.Service.Settings.Connection with { UserKey = "Velvet!Orbit2026-Bravo-Different" } });
        await rig.Service.AuthenticateAsync();
        Assert.Equal(["POST /api/controller/sessions", "GET /api/controller/tournaments"], rig.Requests);
        Assert.Equal("tok-other", rig.Service.Settings.Connection.AccessToken);
    }

    [Fact] public async Task A_just_created_session_the_server_then_rejects_falls_through_to_exactly_one_real_login()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/organizers", () => Session("tok-created")); rig.Respond("POST /api/controller/sessions", () => Session("tok-fresh"));
        await rig.Service.CreateOrganizerAsync(); rig.Requests.Clear();
        var listCalls = 0; rig.Respond("GET /api/controller/tournaments", () => ++listCalls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json("""{"tournaments":[]}"""));
        var result = await rig.Service.AuthenticateAsync();
        Assert.True(result.Success); Assert.Equal("tok-fresh", rig.Service.Settings.Connection.AccessToken);
        Assert.Equal(["GET /api/controller/tournaments", "POST /api/controller/sessions", "GET /api/controller/tournaments"], rig.Requests);
    }

    [Fact] public async Task A_session_saved_by_an_earlier_run_is_not_trusted_blindly_Authenticate_signs_in_for_real()
    {
        var rig = new Rig(); rig.Save(Conn(token: "tok-from-yesterday", expires: DateTimeOffset.UtcNow.AddHours(3)));
        rig.Respond("POST /api/controller/sessions", () => Session("tok-fresh")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        Assert.True((await rig.Service.AuthenticateAsync()).Success);
        Assert.Equal(["POST /api/controller/sessions", "GET /api/controller/tournaments"], rig.Requests); Assert.Equal("tok-fresh", rig.Service.Settings.Connection.AccessToken);
    }

    [Fact] public async Task Repeated_rendering_reads_no_network_state_and_sends_no_request()
    {
        var rig = new Rig(); rig.Save(Conn());
        for (var i = 0; i < 200; i++) { _ = rig.Service.Dashboard; _ = rig.Service.AuthStatus; _ = rig.Service.SignInPausedFor; _ = rig.Service.BrowserStatus; _ = rig.Service.Tournaments; }
        Assert.Empty(rig.Requests);
        await Task.CompletedTask;
    }

    [Fact] public async Task A_second_sign_in_started_while_one_is_in_flight_is_refused_without_a_request()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        var gate = new TaskCompletionSource(); var started = new TaskCompletionSource();
        rig.RespondAsync("POST /api/controller/sessions", async () => { started.SetResult(); await gate.Task; return Session("tok-1"); }); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        var first = rig.Service.AuthenticateAsync(); await started.Task;
        var second = await rig.Service.AuthenticateAsync(); var third = await rig.Service.CreateOrganizerAsync();
        Assert.Equal("operation_in_progress", second.Error!.Code); Assert.Equal("operation_in_progress", third.Error!.Code);
        gate.SetResult(); Assert.True((await first).Success);
        Assert.Equal(1, rig.Requests.Count(r => r.StartsWith("POST")));
    }

    // ---- honest errors ----------------------------------------------------------------------------------------------------------------
    [Fact] public async Task A_rejected_login_is_invalid_credentials_not_an_expired_session_and_names_neither_secret()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await rig.Service.AuthenticateAsync();
        Assert.Equal("invalid_credentials", result.Error!.Code); Assert.DoesNotContain("expired", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TournamentAuthStatusKind.Error, rig.Service.AuthStatus!.Kind); Assert.Null(rig.Service.Settings.Connection.AccessToken);
        rig.AssertNoSecretsAnywhere();
    }

    [Fact] public async Task A_key_policy_rejection_at_Create_Organizer_is_shown_as_an_Organizer_Key_problem_with_the_servers_own_reason()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/organizers", () => Status(HttpStatusCode.BadRequest, """{"error":{"code":"INVALID_USER_KEY","message":"That user key is already in use."}}"""));
        var result = await rig.Service.CreateOrganizerAsync();
        Assert.Equal("invalid_key", result.Error!.Code); Assert.Contains("Organizer Key is already in use", result.Error.Message); Assert.DoesNotContain("user key", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Organizer Key rejected", rig.Service.AuthStatus!.Title);
    }

    [Fact] public async Task An_unavailable_server_is_reported_as_unavailable_not_as_a_credential_problem()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var result = await rig.Service.AuthenticateAsync();
        Assert.Equal("backend_unavailable", result.Error!.Code); Assert.Equal("Server unavailable", rig.Service.AuthStatus!.Title);
    }

    [Fact] public async Task A_session_the_server_rejects_mid_use_is_reported_as_an_expired_organizer_session_and_says_the_key_is_fine()
    {
        var rig = new Rig(); rig.Save(Conn());
        rig.Respond("GET /api/controller/tournaments", () => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await rig.Service.RefreshTournamentListAsync();
        Assert.Null(rig.Service.Settings.Connection.AccessToken); Assert.False(rig.Service.Dashboard.IsAuthenticated);
        Assert.Equal("Organizer session expired", rig.Service.AuthStatus!.Title); Assert.Contains("Organizer Key is still saved", rig.Service.AuthStatus.Message);
        Assert.NotNull(rig.Service.Settings.Connection.UserKey);
    }

    [Fact] public async Task Returning_to_a_venue_whose_saved_session_has_lapsed_explains_it_and_Authenticate_then_succeeds()
    {
        var rig = new Rig(); rig.Save(Conn(token: "tok-old", expires: DateTimeOffset.UtcNow.AddHours(-1)));
        rig.Service.Load(rig.Venue.Id);
        Assert.Equal("Organizer session expired", rig.Service.AuthStatus!.Title); Assert.False(rig.Service.Dashboard.IsAuthenticated);
        Assert.Contains("has not expired", rig.Service.AuthStatus.Message);
        rig.Respond("POST /api/controller/sessions", () => Session("tok-new")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        Assert.True((await rig.Service.AuthenticateAsync()).Success);
        Assert.True(rig.Service.Dashboard.IsAuthenticated); Assert.Equal(TournamentAuthStatusKind.Success, rig.Service.AuthStatus!.Kind);
    }

    // ---- HTTP 429 ---------------------------------------------------------------------------------------------------------------------
    [Fact] public async Task A_429_with_Retry_After_pauses_sign_in_for_exactly_that_long_sends_nothing_meanwhile_and_recovers_after_the_cooldown()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => RateLimited(retryAfterSeconds: 42));
        var first = await rig.Service.AuthenticateAsync();
        Assert.Equal("rate_limited", first.Error!.Code); Assert.Equal(TimeSpan.FromSeconds(42), first.RetryAfter); Assert.Contains("42s", first.Error.Message);
        Assert.Equal(TournamentAuthStatusKind.Warning, rig.Service.AuthStatus!.Kind); Assert.Equal("Temporarily rate limited", rig.Service.AuthStatus.Title); Assert.Contains("not a wrong-key error", rig.Service.AuthStatus.Message);
        Assert.Equal(TimeSpan.FromSeconds(42), rig.Service.SignInPausedFor);
        // hammering: ten more clicks during the pause never reach the network
        for (var i = 0; i < 10; i++) Assert.Equal("rate_limited", (await rig.Service.AuthenticateAsync()).Error!.Code);
        Assert.Equal("rate_limited", (await rig.Service.CreateOrganizerAsync()).Error!.Code);
        Assert.Single(rig.Requests);
        rig.Clock.Advance(TimeSpan.FromSeconds(41)); Assert.Equal("rate_limited", (await rig.Service.AuthenticateAsync()).Error!.Code); Assert.Single(rig.Requests);
        // the allowed cooldown has elapsed: one deliberate retry goes out and succeeds
        rig.Clock.Advance(TimeSpan.FromSeconds(2)); Assert.Null(rig.Service.SignInPausedFor);
        rig.Respond("POST /api/controller/sessions", () => Session("tok-after")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));
        Assert.True((await rig.Service.AuthenticateAsync()).Success);
        Assert.Equal(3, rig.Requests.Count); Assert.Null(rig.Service.SignInPausedFor);
    }

    [Fact] public async Task A_429_is_recorded_in_diagnostics_as_a_rate_limit_without_any_secret()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null)); rig.Respond("POST /api/controller/sessions", () => RateLimited(retryAfterSeconds: 90));
        await rig.Service.AuthenticateAsync();
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("rate limited") && e.Message.Contains("1m 30s"));
        rig.AssertNoSecretsAnywhere();
    }

    [Theory]
    [InlineData("""{"error":{"code":"TOO_MANY_ATTEMPTS","message":"Try again later.","retryAfterSeconds":75}}""", null, 75)]   // body only (older clients' fallback)
    [InlineData("""{"error":{"code":"TOO_MANY_ATTEMPTS","message":"Try again later."}}""", null, 60)]                            // nothing usable: default, never zero
    [InlineData("""not json""", null, 60)]
    [InlineData("""{}""", "0", 1)]                                                                                             // clamped up: never a zero-second "retry now"
    [InlineData("""{}""", "99999", 900)]                                                                                       // clamped down to 15 minutes
    public async Task Retry_After_parsing_prefers_the_header_falls_back_to_the_body_and_is_always_clamped(string body, string? header, int expectedSeconds)
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => { var r = Status((HttpStatusCode)429, body); if (header is not null) r.Headers.TryAddWithoutValidation("Retry-After", header); return r; });
        var result = await rig.Service.AuthenticateAsync();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryAfter);
    }

    [Fact] public async Task Retry_After_as_an_HTTP_date_is_understood()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null));
        rig.Respond("POST /api/controller/sessions", () => { var r = Status((HttpStatusCode)429, "{}"); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30)); return r; });
        var result = await rig.Service.AuthenticateAsync();
        Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 28, 31);
    }

    [Fact] public async Task A_429_on_Create_Organizer_pauses_Authenticate_too_because_the_server_counts_them_together()
    {
        var rig = new Rig(); rig.Save(Conn(token: null, expires: null)); rig.Respond("POST /api/controller/organizers", () => RateLimited(retryAfterSeconds: 30));
        await rig.Service.CreateOrganizerAsync();
        Assert.Equal("rate_limited", (await rig.Service.AuthenticateAsync()).Error!.Code); Assert.Single(rig.Requests);
    }

    // ---- venue isolation --------------------------------------------------------------------------------------------------------------
    [Fact] public async Task Two_venues_keep_separate_credentials_and_sessions_and_never_send_one_venues_token_for_the_other()
    {
        var rig = new Rig(); var second = rig.Profiles.Create("Second venue");
        rig.Profiles.SaveModuleConfig(rig.Venue.Id, "games.tournament", 1, Settings(Conn(token: "tok-A", expires: DateTimeOffset.UtcNow.AddHours(2), key: "Key-A-Northern!Comet2026-Alpha")));
        rig.Profiles.SaveModuleConfig(second.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null, key: "Key-B-Velvet!Orbit2026-Bravo")));
        rig.Respond("POST /api/controller/organizers", () => Session("tok-B")); rig.Respond("GET /api/controller/tournaments", () => Json("""{"tournaments":[]}"""));

        rig.Service.Load(rig.Venue.Id); Assert.True(rig.Service.Dashboard.IsAuthenticated); Assert.Null(rig.Service.AuthStatus);
        rig.Service.Load(second.Id);
        Assert.False(rig.Service.Dashboard.IsAuthenticated); Assert.Null(rig.Service.Settings.Connection.AccessToken); Assert.Equal("Key-B-Velvet!Orbit2026-Bravo", rig.Service.Settings.Connection.UserKey);
        await rig.Service.CreateOrganizerAsync(); await rig.Service.RefreshTournamentListAsync();
        Assert.Equal("Bearer tok-B", rig.AuthorizationOf("GET /api/controller/tournaments"));

        rig.Service.Load(rig.Venue.Id); // back to A: only A's own data, untouched by what happened in B
        Assert.Equal("tok-A", rig.Service.Settings.Connection.AccessToken); Assert.Equal("Key-A-Northern!Comet2026-Alpha", rig.Service.Settings.Connection.UserKey); Assert.Null(rig.Service.AuthStatus);
        Assert.Equal("tok-B", rig.Profiles.GetModuleConfig(second.Id, "games.tournament", 1, TournamentModuleSettings.Default).Connection.AccessToken);
    }

    [Fact] public async Task The_server_pause_follows_the_server_not_the_venue_two_venues_on_one_server_share_it_a_different_server_does_not()
    {
        var rig = new Rig(); var sameServer = rig.Profiles.Create("Same server"); var otherServer = rig.Profiles.Create("Other server");
        rig.Profiles.SaveModuleConfig(rig.Venue.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null)));
        rig.Profiles.SaveModuleConfig(sameServer.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null)));
        rig.Profiles.SaveModuleConfig(otherServer.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null) with { BaseUrl = "https://elsewhere.test" }));
        rig.Respond("POST /api/controller/sessions", () => RateLimited(retryAfterSeconds: 120));
        rig.Service.Load(rig.Venue.Id); await rig.Service.AuthenticateAsync(); Assert.Single(rig.Requests);
        rig.Service.Load(sameServer.Id); Assert.NotNull(rig.Service.SignInPausedFor); Assert.Equal("rate_limited", (await rig.Service.AuthenticateAsync()).Error!.Code); Assert.Single(rig.Requests);
        rig.Service.Load(otherServer.Id); Assert.Null(rig.Service.SignInPausedFor); await rig.Service.AuthenticateAsync(); Assert.Equal(2, rig.Requests.Count);
    }

    [Fact] public async Task A_sign_in_response_that_lands_after_a_venue_switch_cannot_write_into_the_new_venue()
    {
        var rig = new Rig(); var second = rig.Profiles.Create("Second venue");
        rig.Profiles.SaveModuleConfig(rig.Venue.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null)));
        rig.Profiles.SaveModuleConfig(second.Id, "games.tournament", 1, Settings(Conn(token: null, expires: null, key: "Key-B-Velvet!Orbit2026-Bravo")));
        var gate = new TaskCompletionSource(); var started = new TaskCompletionSource();
        rig.RespondAsync("POST /api/controller/organizers", async () => { started.SetResult(); await gate.Task; return Session("tok-from-A"); });
        rig.Service.Load(rig.Venue.Id);
        var pending = rig.Service.CreateOrganizerAsync(); await started.Task;
        rig.Service.Load(second.Id); gate.SetResult();
        var result = await pending;
        Assert.Equal("cancelled", result.Error?.Code);
        Assert.Null(rig.Service.Settings.Connection.AccessToken); Assert.False(rig.Service.Dashboard.IsAuthenticated);
        Assert.Null(rig.Profiles.GetModuleConfig(second.Id, "games.tournament", 1, TournamentModuleSettings.Default).Connection.AccessToken);
        Assert.Null(rig.Profiles.GetModuleConfig(rig.Venue.Id, "games.tournament", 1, TournamentModuleSettings.Default).Connection.AccessToken);
        Assert.Empty(rig.Diagnostics.Capture().RecentErrors); // an intentional cancel is not a failure
    }

    // ---- harness ----------------------------------------------------------------------------------------------------------------------
    private static TournamentConnectionSettings Conn(string? token = "tok", DateTimeOffset? expires = null, string key = Key) => new(Server, token, token is null ? null : expires ?? DateTimeOffset.UtcNow.AddHours(2), Password, key);
    private static TournamentModuleSettings Settings(TournamentConnectionSettings c) => new(c, "G", "T", new());
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Session(string token) => Status(HttpStatusCode.Created, $$$"""{"accessToken":"{{{token}}}","expiresAt":"{{{DateTimeOffset.UtcNow.AddHours(8):O}}}","organizer":{"id":"o1","keyPrefix":"Nort","createdAt":"2026-01-01T00:00:00Z"}}""");
    private static HttpResponseMessage RateLimited(int retryAfterSeconds) { var r = Status((HttpStatusCode)429, $$$"""{"error":{"code":"TOO_MANY_ATTEMPTS","message":"Try again later.","retryAfterSeconds":{{{retryAfterSeconds}}}}}"""); r.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.ToString()); return r; }

    private sealed class FakeClock { public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow; public void Advance(TimeSpan by) => Now += by; }
    private sealed class Rig
    {
        private readonly Dictionary<string, Func<Task<HttpResponseMessage>>> routes = [];
        private readonly List<(string Key, string? Auth)> seen = [];
        public FakeClock Clock { get; } = new();
        public VenueProfileService Profiles { get; } = new(new InMemoryVenueStore(), new ModuleHost());
        public VenueProfile Venue => Profiles.Current;
        public DiagnosticsService Diagnostics { get; }
        public TournamentControlService Service { get; }
        public List<string> Requests { get; } = [];
        public Rig() { Diagnostics = new(new ModuleHost(), Profiles, new SystemClock()); Service = NewService(); Service.Load(Venue.Id); }
        public TournamentControlService NewService() => new(new TournamentControlClient(new HttpClient(new Handler(this))), Profiles, new(new SchedulerService(new SystemClock()), new ChatCommandService(new SystemClock(), new InlineFrameworkDispatcher(), _ => true)), Diagnostics, utcNow: () => Clock.Now);
        public void Save(TournamentConnectionSettings c) { Profiles.SaveModuleConfig(Venue.Id, "games.tournament", 1, Settings(c)); Service.Load(Venue.Id); }
        public void Respond(string route, Func<HttpResponseMessage> response) => routes[route] = () => Task.FromResult(response());
        public void RespondAsync(string route, Func<Task<HttpResponseMessage>> response) => routes[route] = response;
        public string? AuthorizationOf(string route) => seen.Last(s => s.Key == route).Auth;
        public void AssertNoSecretsAnywhere()
        {
            var text = string.Join('\n', Diagnostics.Capture().RecentErrors.Select(e => e.Message)) + "\n" + Service.AuthStatus?.Title + Service.AuthStatus?.Message + Service.BrowserStatus + Service.Notice;
            Assert.DoesNotContain(Password, text); Assert.DoesNotContain(Key, text); Assert.DoesNotContain("SECRET", text);
        }
        private sealed class Handler(Rig rig) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var route = $"{request.Method} {request.RequestUri!.AbsolutePath}"; rig.Requests.Add(route); rig.seen.Add((route, request.Headers.Authorization?.ToString()));
                return rig.routes.TryGetValue(route, out var respond) ? await respond() : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
