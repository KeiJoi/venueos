using System.Net;
using System.Text;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations;
using VenueOS.Venues;
using VenueOS.Core;

namespace VenueOS.Services.Tests;

public sealed class RaffleClientTests
{
    [Fact]
    public async Task Upsert_uses_camel_case_ticket_contract_and_sends_the_access_key_header()
    {
        string? body = null; string? accessKeyHeader = null;
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/raffles", request.RequestUri!.AbsolutePath);
            accessKeyHeader = request.Headers.TryGetValues("X-Access-Key", out var values) ? values.FirstOrDefault() : null;
            body = await request.Content!.ReadAsStringAsync();
            return Json("{\"raffleId\":\"remote\",\"hostUrl\":\"https://x/host/remote/secret-host\",\"viewerUrl\":\"https://x/view/remote/secret-view\",\"winnerName\":null}");
        });
        var client = new VenueRaffleClient(new HttpClient(handler));
        var raffle = new LocalRaffle("local", "Friday", DateTime.UnixEpoch, new(), [new("Ada", "Balmung", 2, 1)], []);
        var result = await client.UpsertAsync(new("raffle.test", "shh-its-a-secret"), raffle, default);
        Assert.True(result.Success);
        Assert.Equal("shh-its-a-secret", accessKeyHeader);
        Assert.Contains("\"raffleId\":\"local\"", body);
        Assert.Contains("\"tickets\":[\"Ada@Balmung\",\"Ada@Balmung\",\"Ada@Balmung\"]", body);
        Assert.Equal("remote", result.Value!.RaffleId);
    }

    [Fact]
    public async Task Upsert_without_an_access_key_configured_fails_locally_without_a_network_call()
    {
        var called = false;
        var handler = new Handler(_ => { called = true; return Task.FromResult(Json("{}")); });
        var client = new VenueRaffleClient(new HttpClient(handler));
        var raffle = new LocalRaffle("local", "Friday", DateTime.UtcNow, new(), [], []);
        var result = await client.UpsertAsync(new("https://raffle.test", ""), raffle, default);
        Assert.False(result.Success);
        Assert.False(called);
    }

    [Fact]
    public async Task Fetch_escapes_token_and_deserializes_state_including_exclusions()
    {
        Uri? uri = null;
        var handler = new Handler(request => { uri = request.RequestUri; return Task.FromResult(Json("{\"raffleId\":\"abc\",\"name\":\"Friday\",\"winnerName\":\"Ada@Balmung\",\"tickets\":[\"Ada@Balmung\"],\"hasWinner\":true,\"excludedTickets\":[\"Bob\"]}")); });
        var result = await new VenueRaffleClient(new HttpClient(handler)).FetchAsync(new("https://raffle.test", "key"), "abc", "host token?", default);
        Assert.True(result.Success);
        Assert.Contains("token=host%20token%3F", uri!.Query);
        Assert.Equal("Ada@Balmung", result.Value!.WinnerName);
        Assert.True(result.Value.HasWinner);
        Assert.Equal(["Bob"], result.Value.ExcludedTickets);
    }

    [Fact]
    public async Task Delete_sends_the_access_key_and_treats_a_404_as_success()
    {
        string? accessKeyHeader = null; HttpMethod? method = null;
        var handler = new Handler(request => { method = request.Method; accessKeyHeader = request.Headers.TryGetValues("X-Access-Key", out var v) ? v.FirstOrDefault() : null; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
        var result = await new VenueRaffleClient(new HttpClient(handler)).DeleteAsync(new("https://raffle.test", "key"), "gone-already", default);
        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("key", accessKeyHeader);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Errors_cancellation_and_missing_url_do_not_expose_secrets()
    {
        var client = new VenueRaffleClient(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "Forbidden" }))));
        var raffle = new LocalRaffle("a", "A", DateTime.UtcNow, new(), [], []);
        var error = await client.UpsertAsync(new("https://x", "key"), raffle, default);
        Assert.False(error.Success);
        Assert.DoesNotContain("secret", error.Error!, StringComparison.OrdinalIgnoreCase);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelled = await client.FetchAsync(new("", "key"), "id", "secret", cancel.Token);
        Assert.False(cancelled.Success);
        Assert.DoesNotContain("secret", cancelled.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_raffle_strips_urls_from_diagnostic_copy()
    {
        var raffle = new LocalRaffle("a", "A", DateTime.UtcNow, new(), [], [], HostUrl: "https://x/secret", ViewerUrl: "https://x/secret");
        Assert.Null(raffle.WithoutSecrets().HostUrl);
        Assert.Equal(0, raffle.TotalTickets);
    }

    [Fact]
    public void Same_name_different_homeworld_produce_distinct_ticket_keys()
    {
        var a = new RaffleParticipant("Ada", "Balmung");
        var b = new RaffleParticipant("Ada", "Gilgamesh");
        Assert.NotEqual(a.TicketKey, b.TicketKey);
        Assert.NotEqual(a.IdentityKey, b.IdentityKey);
    }

    [Fact]
    public void Legacy_name_only_participant_keeps_a_bare_ticket_key_with_no_invented_homeworld()
    {
        var legacy = new RaffleParticipant("LegacyName", null, 2, 0);
        Assert.Equal("LegacyName", legacy.TicketKey);
        Assert.Equal("LegacyName", legacy.DisplayName);
    }

    [Fact]
    public void Excluded_participants_are_omitted_from_the_built_ticket_pool()
    {
        var raffle = new LocalRaffle("a", "A", DateTime.UtcNow, new(), [new("Ada", "Balmung", 2, 0), new("Bob", null, 1, 0)], ["Ada@Balmung"]);
        Assert.Equal(["Bob"], raffle.BuildTickets());
    }

    [Fact]
    public void Unpublished_changes_are_detected_after_the_ticket_pool_changes_post_publish()
    {
        var published = new LocalRaffle("a", "A", DateTime.UtcNow, new(), [new("Ada", null, 1, 0)], [], ExternalId: "remote");
        var publishedWithHash = published with { PublishedTicketHash = published.CurrentTicketHash };
        Assert.False(publishedWithHash.HasUnpublishedChanges);

        var changed = publishedWithHash with { Participants = [.. publishedWithHash.Participants, new RaffleParticipant("Bob", null, 1, 0)] };
        Assert.True(changed.HasUnpublishedChanges);
    }

    [Fact]
    public async Task Venue_switch_cancels_inflight_raffle_request()
    {
        var handler = new Handler((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException()));
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new SystemClock());
        var first = profiles.Current;
        var second = profiles.Create("Second");
        profiles.SaveModuleConfig(first.Id, "games.raffle", 1, new VenueRaffleSettings(new("https://x", "key"), new(), []));
        var service = new VenueRaffleService(new VenueRaffleClient(new HttpClient(handler)), profiles, diagnostics);
        service.Load(first.Id);
        service.Create("name");
        var raffleId = service.Selected!.Id;
        var request = service.PublishAsync(raffleId);
        service.Load(second.Id);
        var result = await request;
        Assert.False(result.Success);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler { private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action; public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : this((request, _) => action(request)) { } public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
}
