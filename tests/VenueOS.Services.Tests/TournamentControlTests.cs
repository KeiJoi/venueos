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
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "V", "G", "T", new())); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts()); service.Load(venue.Id); await service.LoadStateAsync("t1"); var result = await service.StartAsync();
        Assert.True(result.Success); Assert.Equal(1, mutations); Assert.Equal(8, service.Current!.Tournament.Revision); Assert.Contains("refreshed", service.Notice!);
    }
    [Fact] public async Task Token_expiry_clears_session()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))); var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var venue = profiles.Current; profiles.SaveModuleConfig(venue.Id, "games.tournament", 1, new TournamentModuleSettings(Connection(), "V", "G", "T", new())); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(handler)), profiles, Callouts()); service.Load(venue.Id); var result = await service.LoadStateAsync("t1");
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
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var first = profiles.Current; var second = profiles.Create("Second"); profiles.SaveModuleConfig(first.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("one"), "A", "", "", new())); profiles.SaveModuleConfig(second.Id, "games.tournament", 1, new TournamentModuleSettings(Connection("two"), "B", "", "", new())); var sent = new List<string>(); var clock = new SystemClock(); var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { sent.Add(command); return true; }); var calls = new TournamentCalloutService(new SchedulerService(clock), chat); var service = new TournamentControlService(new TournamentControlClient(new HttpClient(new Handler(_ => Task.FromResult(Json(State(1)))))), profiles, calls); service.Load(first.Id); service.Load(second.Id); Assert.Equal("two", service.Settings.Connection.AccessToken); Assert.True(calls.Send("match", new(Line1: "<1> vs <2>", Line2: ""), "Ada", "Bea")); await chat.TickAsync(); Assert.Equal("/shout Ada vs Bea", sent.Single());
    }
    private static TournamentConnectionSettings Connection(string token = "token") => new("https://tournament.test", token, DateTimeOffset.UtcNow.AddHours(1), "password", "key");
    private static TournamentCalloutService Callouts() => new(new SchedulerService(new SystemClock()), new ChatCommandService(new SystemClock(), new InlineFrameworkDispatcher(), _ => true));
    private static string State(int revision) => $$"""{"tournament":{"id":"t1","publicCode":"CODE","venueName":"Venue","gameName":"Game","tournamentName":"Cup","eventDate":"2026-01-01T00:00:00Z","status":"SETUP","revision":{{revision}},"playerCount":0},"contestants":[],"rounds":[],"matches":[]}""";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
}
