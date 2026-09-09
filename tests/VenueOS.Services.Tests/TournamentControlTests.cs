using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class TournamentControlTests
{
    [Fact] public async Task Mutation_serializes_expected_revision_and_bearer_token()
    {
        string? body = null; var handler = new Handler(async request => { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("token", request.Headers.Authorization.Parameter); body = await request.Content!.ReadAsStringAsync(); return Json(State(4)); });
        var client = new TournamentControlClient(new HttpClient(handler)); var result = await client.StartAsync(Connection(), "t1", 3, default);
        Assert.True(result.Success); Assert.Contains("\"expectedRevision\":3", body); Assert.Equal(4, result.Value!.Tournament.Revision);
    }
    [Fact] public async Task Conflict_refetches_authoritative_state_without_retrying_mutation()
    {
        var mutations = 0; var handler = new Handler(request => { if (request.RequestUri!.AbsolutePath.EndsWith("/start")) { mutations++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":{\"code\":\"STALE_TOURNAMENT\"}}") }); } return Task.FromResult(Json(State(8))); });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new())); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id); await service.LoadStateAsync("t1"); var result = await service.StartAsync();
        Assert.True(result.Success); Assert.Equal(1, mutations); Assert.Equal(8, service.Current!.Tournament.Revision); Assert.Contains("refreshed", service.Notice!);
    }
    [Fact] public async Task Token_expiry_clears_session()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))); var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new())); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id); var result = await service.LoadStateAsync("t1");
        Assert.False(result.Success); Assert.Null(service.Settings.Connection.AccessToken);
    }
    [Fact] public void Socket_protocol_detects_revision_gaps_and_unauthorized_errors()
    {
        var gap = TournamentEventProtocol.Inspect(new(1, "tournament.updated", "CODE", "id", 5, null), 3); Assert.True(gap.ShouldRefetch);
        using var error = System.Text.Json.JsonDocument.Parse("{\"code\":\"UNAUTHORIZED\"}"); var expired = TournamentEventProtocol.Inspect(new(1, "error", null, null, null, error.RootElement.Clone()), 3); Assert.True(expired.TokenExpired);
        Assert.Equal("wss", TournamentEventProtocol.SocketUri("https://tournament.test")!.Scheme);
    }
    [Fact] public async Task Venue_switch_does_not_reuse_credentials_and_callouts_use_chat_queue()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var first = profiles.Current; var second = profiles.Create("Second"); profiles.SaveModuleConfig(first.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("one"), "", "", new())); profiles.SaveModuleConfig(second.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("two"), "", "", new())); var sent = new List<string>(); var clock = new SystemClock(); var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { sent.Add(command); return true; }); var calls = new TournamentCalloutService(new SchedulerService(clock), chat); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(new Handler(_ => Task.FromResult(Json(State(1)))))), profiles, calls, Diagnostics(profiles)); service.Load(first.Id); service.Load(second.Id); Assert.Equal("two", service.Settings.Connection.AccessToken); Assert.True(calls.Send("match", new(Line1: "<1> vs <2>", Line2: ""), "Ada", "Bea")); await chat.TickAsync(); Assert.Equal("/shout Ada vs Bea", sent.Single());
    }
    [Fact] public async Task Create_tournament_sources_venue_name_from_the_active_venue_profile_not_a_module_setting()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"tournaments":[]}""");
            body = await request.Content!.ReadAsStringAsync();
            return Json($$"""{"tournament":{"id":"t1","publicCode":"CODE","venueName":"My Venue","gameName":"G","tournamentName":"T","eventDate":"2026-01-01T00:00:00Z","status":"SETUP","revision":0,"playerCount":0},"publicUrl":"https://tournament.test/t/CODE"}""");
        });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new())); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id);
        var result = await service.CreateTournamentAsync("Any Game", "Cup", DateTimeOffset.UtcNow);
        Assert.True(result.Success); Assert.Contains($"\"venueName\":\"{venue.DisplayName}\"", body);
    }
    [Fact] public async Task List_tournaments_encodes_query_and_status_filters()
    {
        string? path = null; var handler = new Handler(request => { path = request.RequestUri!.PathAndQuery; return Task.FromResult(Json("""{"tournaments":[]}""")); });
        var client = new TournamentControlClient(new HttpClient(handler));
        await client.ListTournamentsAsync(Connection(), "my cup", "ACTIVE", default);
        Assert.Contains("q=my%20cup", path); Assert.Contains("status=ACTIVE", path);
    }
    [Fact] public async Task Bulk_add_reorder_randomize_and_correction_serialize_the_expected_bodies()
    {
        var bodies = new List<(string Path, string Body)>(); var handler = new Handler(async request => { bodies.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync())); return Json(State(1)); });
        var client = new TournamentControlClient(new HttpClient(handler));
        await client.BulkAddContestantsAsync(Connection(), "t1", 0, "Ada\nBea", default);
        await client.ReorderAsync(Connection(), "t1", 0, ["a", "b"], default);
        await client.RandomizeAsync(Connection(), "t1", 0, default);
        await client.CorrectAsync(Connection(), "t1", "m1", 0, "winner", true, default);
        Assert.Contains(bodies, b => b.Path.EndsWith("/contestants") && b.Body.Contains("\"bulkText\":\"Ada\\nBea\""));
        Assert.Contains(bodies, b => b.Path.EndsWith("/seeds") && b.Body.Contains("\"contestantIds\":[\"a\",\"b\"]"));
        Assert.Contains(bodies, b => b.Path.EndsWith("/seeds/randomize"));
        Assert.Contains(bodies, b => b.Path.EndsWith("/correction") && b.Body.Contains("\"rollbackDownstream\":true"));
    }
    [Fact] public void Correction_analysis_only_requires_confirmation_when_a_downstream_match_has_already_completed()
    {
        var state = new TournamentControllerState(
            new ControllerTournament("t1", "CODE", "V", "G", "T", DateTimeOffset.UtcNow, "ACTIVE", 1, 4),
            [],
            [],
            [
                new TournamentMatch("m1", "r1", 1, "a", "b", "a", "COMPLETED", "m2", 1),
                new TournamentMatch("m2", "r2", 1, "a", "c", null, "PENDING", null, null),
            ]);
        Assert.False(TournamentCorrectionAnalysis.RequiresRollbackConfirmation(state, "m1"));
        var completedDownstream = state with { Matches = [state.Matches[0], state.Matches[1] with { Status = "COMPLETED" }] };
        Assert.True(TournamentCorrectionAnalysis.RequiresRollbackConfirmation(completedDownstream, "m1"));
    }
    [Fact] public void Delete_eligibility_mirrors_the_backend_active_tournament_gate()
    {
        Assert.True(TournamentDeleteEligibility.CanDelete("SETUP"));
        Assert.True(TournamentDeleteEligibility.CanDelete("COMPLETED"));
        Assert.True(TournamentDeleteEligibility.CanDelete("CANCELLED"));
        Assert.False(TournamentDeleteEligibility.CanDelete("ACTIVE"));
    }
    [Fact] public async Task Delete_sends_a_DELETE_request_to_the_tournament_resource_with_no_body_required()
    {
        HttpMethod? method = null; string? path = null;
        var handler = new Handler(request => { method = request.Method; path = request.RequestUri!.AbsolutePath; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); });
        var client = new TournamentControlClient(new HttpClient(handler));
        var result = await client.DeleteAsync(Connection(), "t1", default);
        Assert.True(result.Success); Assert.Equal(HttpMethod.Delete, method); Assert.EndsWith("/api/controller/tournaments/t1", path);
    }
    [Fact] public async Task Successful_delete_removes_the_entry_locally_calls_the_client_once_and_leaves_an_unrelated_open_tournament_untouched()
    {
        var deleteCalls = 0; string? deletedPath = null;
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deleteCalls++; deletedPath = request.RequestUri!.AbsolutePath; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/state")) return Task.FromResult(Json(State(1)));
            return Task.FromResult(Json("""{"tournaments":[{"id":"a","publicCode":"A1","venueName":"V","gameName":"G","tournamentName":"A","eventDate":"2026-01-01T00:00:00Z","status":"SETUP","revision":0,"playerCount":0},{"id":"b","publicCode":"B1","venueName":"V","gameName":"G","tournamentName":"B","eventDate":"2026-01-01T00:00:00Z","status":"COMPLETED","revision":2,"playerCount":2}]}"""));
        });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new()));
        var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id);
        await service.RefreshTournamentListAsync(); Assert.Equal(2, service.Tournaments.Count);
        await service.LoadStateAsync("t1"); Assert.Equal("t1", service.Current!.Tournament.Id);

        var result = await service.DeleteTournamentAsync("a");

        Assert.True(result.Success); Assert.Equal(1, deleteCalls); Assert.EndsWith("/api/controller/tournaments/a", deletedPath);
        Assert.DoesNotContain(service.Tournaments, t => t.Id == "a"); Assert.Contains(service.Tournaments, t => t.Id == "b");
        Assert.NotNull(service.Current); Assert.Equal("t1", service.Current!.Tournament.Id);
    }
    [Fact] public async Task Deleting_the_currently_open_tournament_clears_local_selected_state_and_stops_realtime()
    {
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (request.RequestUri!.AbsolutePath.EndsWith("/state")) return Task.FromResult(Json(State(1)));
            return Task.FromResult(Json("""{"tournaments":[]}"""));
        });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new()));
        var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id);
        await service.LoadStateAsync("t1"); Assert.NotNull(service.Current);

        var result = await service.DeleteTournamentAsync("t1");

        Assert.True(result.Success); Assert.Null(service.Current); Assert.False(service.IsRealtimeConnected);
    }
    [Fact] public async Task Failed_delete_does_not_pretend_success_and_reconciles_the_browser_list_instead()
    {
        var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"INVALID_TOURNAMENT_STATE\",\"message\":\"Cancel this tournament before deleting it.\"}}") });
            return Task.FromResult(Json("""{"tournaments":[{"id":"a","publicCode":"A1","venueName":"V","gameName":"G","tournamentName":"A","eventDate":"2026-01-01T00:00:00Z","status":"ACTIVE","revision":0,"playerCount":2}]}"""));
        });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "G", "T", new()));
        var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(venue.Id);
        await service.RefreshTournamentListAsync(); Assert.Single(service.Tournaments);

        var result = await service.DeleteTournamentAsync("a");

        Assert.False(result.Success); Assert.Contains(service.Tournaments, t => t.Id == "a");
    }
    [Fact] public async Task Delete_uses_the_active_venues_own_credentials_after_a_venue_switch()
    {
        string? authToken = null;
        var handler = new Handler(request => { if (request.Method == HttpMethod.Delete) authToken = request.Headers.Authorization?.Parameter; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); });
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var first = profiles.Current; var second = profiles.Create("Second");
        profiles.SaveModuleConfig(first.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("one"), "", "", new())); profiles.SaveModuleConfig(second.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("two"), "", "", new()));
        var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts(), Diagnostics(profiles)); service.Load(first.Id); service.Load(second.Id);
        await service.DeleteTournamentAsync("whatever");
        Assert.Equal("two", authToken);
    }
    private static TournamentConnectionSettings Connection(string token = "token") => new("https://tournament.test", token, DateTimeOffset.UtcNow.AddHours(1), "password", "key");
    private static TournamentCalloutService Callouts() => new(new SchedulerService(new SystemClock()), new ChatCommandService(new SystemClock(), new InlineFrameworkDispatcher(), _ => true));
    private static DiagnosticsService Diagnostics(VenueProfileService profiles) => new(new ModuleHost(), profiles, new SystemClock());
    private static string State(int revision) => $$"""{"tournament":{"id":"t1","publicCode":"CODE","venueName":"Venue","gameName":"Game","tournamentName":"Cup","eventDate":"2026-01-01T00:00:00Z","status":"SETUP","revision":{{revision}},"playerCount":0},"contestants":[],"rounds":[],"matches":[]}""";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
}
