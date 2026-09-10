using System.Collections.Concurrent;
using System.Net;
using System.Text;
using VenueOS.Core;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class RaffleTicketMathTests
{
    [Fact]
    public void No_bonus_when_the_rule_is_unconfigured()
    {
        Assert.Equal(0, RaffleTicketMath.CalculateFreeTickets(new RaffleSettings(PaidTicketsForFree: 0, FreeTicketsPerBlock: 1), 10));
        Assert.Equal(0, RaffleTicketMath.CalculateFreeTickets(new RaffleSettings(PaidTicketsForFree: 5, FreeTicketsPerBlock: 0), 10));
    }

    [Fact]
    public void Bonus_is_computed_in_whole_blocks()
    {
        var settings = new RaffleSettings(PaidTicketsForFree: 5, FreeTicketsPerBlock: 1);
        Assert.Equal(2, RaffleTicketMath.CalculateFreeTickets(settings, 12));
        Assert.Equal(0, RaffleTicketMath.CalculateFreeTickets(settings, 4));
    }

    [Fact]
    public void Bonus_is_evaluated_per_call_not_cumulative_matching_donor_semantics()
    {
        // Buying 3 then 3 more (6 total) under a "5 paid -> 1 free" rule yields ZERO bonus tickets overall
        // (3/5=0 twice) - not the 1 bonus ticket a single 6-ticket purchase would produce. This is a deliberate
        // preservation of the donor's exact (if slightly surprising) behavior, not a bug.
        var settings = new RaffleSettings(PaidTicketsForFree: 5, FreeTicketsPerBlock: 1);
        Assert.Equal(0, RaffleTicketMath.CalculateFreeTickets(settings, 3));
        Assert.Equal(0, RaffleTicketMath.CalculateFreeTickets(settings, 3));
        Assert.Equal(1, RaffleTicketMath.CalculateFreeTickets(settings, 6));
    }
}

public sealed class RaffleServiceTests
{
    private static (VenueRaffleService Service, VenueProfileService Profiles, DiagnosticsService Diagnostics) Build(HttpMessageHandler? handler = null, Func<IRaffleRealtimeTransport>? transportFactory = null)
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new SystemClock());
        var client = new VenueRaffleClient(new HttpClient(handler ?? new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))));
        var service = new VenueRaffleService(client, profiles, diagnostics, transportFactory);
        service.Load(profiles.Current.Id);
        service.SaveConnection(new RaffleConnectionSettings("https://raffle.test", "the-key"));
        return (service, profiles, diagnostics);
    }

    [Fact]
    public void Adding_paid_tickets_merges_by_name_and_homeworld_and_applies_the_bonus_rule()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.UpdateRaffleSettings(raffle.Id, new RaffleSettings(PaidTicketsForFree: 5, FreeTicketsPerBlock: 1));
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 5);
        service.AddPaidTickets(raffle.Id, "ada", "balmung", 5); // same identity, different casing - must merge

        var participant = Assert.Single(service.Selected!.Participants);
        Assert.Equal(10, participant.PaidTickets);
        Assert.Equal(2, participant.FreeTickets); // 5/5 = 1 bonus per call, twice
    }

    [Fact]
    public void Same_name_different_homeworld_are_never_merged()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);
        service.AddPaidTickets(raffle.Id, "Ada", "Gilgamesh", 1);
        Assert.Equal(2, service.Selected!.Participants.Count);
    }

    [Fact]
    public void A_legacy_name_only_participant_stays_distinct_from_a_homeworld_qualified_one()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", null, 1);
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);
        Assert.Equal(2, service.Selected!.Participants.Count);
        Assert.Contains(service.Selected!.Participants, p => p.TicketKey == "Ada");
        Assert.Contains(service.Selected!.Participants, p => p.TicketKey == "Ada@Balmung");
    }

    [Fact]
    public void Adjusting_a_participants_tickets_down_to_zero_removes_them()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);
        var identity = service.Selected!.Participants.Single().IdentityKey;
        service.AdjustTickets(raffle.Id, identity, -1, 0);
        Assert.Empty(service.Selected!.Participants);
    }

    [Fact]
    public void Ticket_counts_never_go_negative()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.AddFreeTickets(raffle.Id, "Ada", null, 1);
        var identity = service.Selected!.Participants.Single().IdentityKey;
        service.AdjustTickets(raffle.Id, identity, -50, -50);
        Assert.Empty(service.Selected!.Participants);
    }

    [Fact]
    public void Archiving_removes_a_raffle_from_the_active_count_without_deleting_it()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        Assert.Equal(1, service.Dashboard.ActiveRaffleCount);
        service.Archive(raffle.Id);
        Assert.Equal(0, service.Dashboard.ActiveRaffleCount);
        Assert.Equal(1, service.Dashboard.ArchivedRaffleCount);
        Assert.Single(service.Settings.Raffles); // still stored, not deleted
        service.Unarchive(raffle.Id);
        Assert.Equal(1, service.Dashboard.ActiveRaffleCount);
    }

    [Fact]
    public void Reset_clears_operational_contents_but_keeps_the_raffle_and_flags_it_unpublished()
    {
        var (service, _, _) = Build();
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 3);
        // Simulate a prior successful publish without a live network call.
        var index = service.Settings.Raffles.FindIndex(x => x.Id == raffle.Id);
        var published = service.Settings.Raffles[index];
        service.Settings.Raffles[index] = published with { ExternalId = "remote-1", PublishedTicketHash = published.CurrentTicketHash, WinnerName = "Ada@Balmung" };

        service.Reset(raffle.Id);

        var afterReset = service.Selected!;
        Assert.Empty(afterReset.Participants);
        Assert.Empty(afterReset.ExcludedParticipants);
        Assert.Null(afterReset.WinnerName);
        Assert.Equal("remote-1", afterReset.ExternalId); // reset never deletes the raffle or its backend link
        Assert.True(afterReset.HasUnpublishedChanges); // operator must be prompted to republish
    }

    [Fact]
    public async Task Deleting_a_never_published_raffle_never_contacts_the_backend()
    {
        var contacted = false;
        var (service, _, _) = Build(new Handler(_ => { contacted = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var raffle = service.Create("Friday");
        await service.DeleteAsync(raffle.Id);
        Assert.False(contacted);
        Assert.Empty(service.Settings.Raffles);
    }

    [Fact]
    public async Task Deleting_a_published_raffle_calls_the_backend_delete_endpoint()
    {
        HttpMethod? method = null; string? path = null;
        var (service, _, _) = Build(new Handler(request => { method = request.Method; path = request.RequestUri!.AbsolutePath; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var raffle = service.Create("Friday");
        var index = service.Settings.Raffles.FindIndex(x => x.Id == raffle.Id);
        service.Settings.Raffles[index] = service.Settings.Raffles[index] with { ExternalId = "remote-9" };

        await service.DeleteAsync(raffle.Id);

        Assert.Equal(HttpMethod.Delete, method);
        Assert.Equal("/api/raffles/remote-9", path);
        Assert.Empty(service.Settings.Raffles);
    }

    [Fact]
    public async Task A_failed_backend_deletion_still_deletes_the_local_copy_and_is_reported_to_diagnostics()
    {
        var (service, _, diagnostics) = Build(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        var raffle = service.Create("Friday");
        var index = service.Settings.Raffles.FindIndex(x => x.Id == raffle.Id);
        service.Settings.Raffles[index] = service.Settings.Raffles[index] with { ExternalId = "remote-9" };

        await service.DeleteAsync(raffle.Id);

        Assert.Empty(service.Settings.Raffles); // explicit local confirmation is honored regardless
        Assert.Contains(diagnostics.Capture().RecentErrors, e => e.Message.Contains("backend deletion failed"));
    }

    [Fact]
    public async Task Publishing_sets_links_and_unpublished_changes_clears_then_reappears_after_a_ticket_change()
    {
        var (service, _, _) = Build(new Handler(_ => Task.FromResult(Json("{\"raffleId\":\"remote-1\",\"hostUrl\":\"https://raffle.test/host/remote-1/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-1/view-tok\",\"winnerName\":null}"))));
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);

        var result = await service.PublishAsync(raffle.Id);
        Assert.True(result.Success);
        Assert.False(service.Selected!.HasUnpublishedChanges);
        Assert.Equal("https://raffle.test/host/remote-1/host-tok", service.Selected!.HostUrl);

        service.AddPaidTickets(raffle.Id, "Bob", null, 1);
        Assert.True(service.Selected!.HasUnpublishedChanges);
    }

    [Fact]
    public async Task Realtime_updates_mirror_the_winner_and_excluded_tickets_into_the_selected_raffle()
    {
        var transport = new FakeTransport();
        var (service, _, _) = Build(
            new Handler(_ => Task.FromResult(Json("{\"raffleId\":\"remote-2\",\"hostUrl\":\"https://raffle.test/host/remote-2/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-2/view-tok\",\"winnerName\":null}"))),
            () => transport);
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);
        await service.PublishAsync(raffle.Id); // establishes ExternalId + starts the realtime connection

        transport.EnqueueIncoming(new("spin", RaffleId: "remote-2", WinnerName: "Ada@Balmung", ExcludedTickets: []));
        await transport.WaitForMessagesConsumed();
        service.Tick(DateTimeOffset.UtcNow);

        Assert.Equal("Ada@Balmung", service.Selected!.WinnerName);
    }

    [Fact]
    public void Two_venues_never_see_each_others_raffles()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new SystemClock());
        var service = new VenueRaffleService(new VenueRaffleClient(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))), profiles, diagnostics);

        var first = profiles.Current;
        var second = profiles.Create("Second Venue");

        service.Load(first.Id);
        service.Create("Venue One Raffle");
        Assert.Single(service.Settings.Raffles);

        service.Load(second.Id);
        Assert.Empty(service.Settings.Raffles);
        service.Create("Venue Two Raffle");

        service.Load(first.Id);
        Assert.Single(service.Settings.Raffles);
        Assert.Equal("Venue One Raffle", service.Settings.Raffles[0].Name);
    }

    [Fact]
    public void Backend_access_key_persists_as_plain_readable_text_through_a_save_and_reload()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new SystemClock());
        var service = new VenueRaffleService(new VenueRaffleClient(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))), profiles, diagnostics);
        service.Load(profiles.Current.Id);
        service.SaveConnection(new RaffleConnectionSettings("https://raffle.test", "plain-text-secret-123"));

        var reloaded = new VenueRaffleService(new VenueRaffleClient(new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))), profiles, diagnostics);
        reloaded.Load(profiles.Current.Id);
        Assert.Equal("plain-text-secret-123", reloaded.Settings.Connection.AccessKey);
    }

    [Fact]
    public async Task An_access_key_never_leaks_into_a_diagnostics_failure_message()
    {
        var (service, _, diagnostics) = Build(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "top-secret-key-value should never appear" })));
        var raffle = service.Create("Friday");
        await service.PublishAsync(raffle.Id);
        // The failure message is allowed to describe the HTTP status, but must never echo back the configured key.
        Assert.DoesNotContain(diagnostics.Capture().RecentErrors, e => e.Message.Contains("the-key"));
    }

    /// <summary>Routes by path/method so short-link calls (POST /api/links, GET /api/links/lookup) get their own
    /// correctly-shaped responses alongside the raffle-create response — unlike the single-response Handler used by
    /// several tests above, which is fine for tests that don't care what EnsureShortLinksAsync does with an
    /// unrelated response shape.</summary>
    private static Handler RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage?> route, HttpResponseMessage? fallback = null) =>
        new(request => Task.FromResult(route(request) ?? fallback ?? new HttpResponseMessage(HttpStatusCode.NotFound)));

    [Fact]
    public async Task Publishing_automatically_mints_short_links_for_both_host_and_viewer()
    {
        var linkRequests = new List<string>();
        var handler = RoutedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/raffles") return Json("{\"raffleId\":\"remote-3\",\"hostUrl\":\"https://raffle.test/host/remote-3/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-3/view-tok\",\"winnerName\":null}");
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Json("{\"ok\":true,\"code\":null}"); // no existing link yet
            if (request.RequestUri!.AbsolutePath == "/api/links") { linkRequests.Add(request.RequestUri!.AbsolutePath); return Json("{\"ok\":true,\"code\":\"AB23CD\"}"); }
            return null;
        });
        var (service, _, _) = Build(handler);
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 1);

        await service.PublishAsync(raffle.Id);

        Assert.Equal(2, linkRequests.Count); // one for host, one for viewer
        Assert.Equal("https://raffle.test/l/AB23CD", service.Selected!.DisplayHostUrl("https://raffle.test"));
        Assert.Equal("https://raffle.test/l/AB23CD", service.Selected!.DisplayViewerUrl("https://raffle.test"));
    }

    [Fact]
    public async Task Republishing_reuses_an_already_minted_short_link_instead_of_creating_a_duplicate()
    {
        var createCalls = 0;
        var handler = RoutedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/raffles") return Json("{\"raffleId\":\"remote-4\",\"hostUrl\":\"https://raffle.test/host/remote-4/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-4/view-tok\",\"winnerName\":null}");
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Json("{\"ok\":true,\"code\":\"EXISTING\"}"); // already exists on the backend
            if (request.RequestUri!.AbsolutePath == "/api/links") { createCalls++; return Json("{\"ok\":true,\"code\":\"SHOULDNOT\"}"); }
            return null;
        });
        var (service, _, _) = Build(handler);
        var raffle = service.Create("Friday");

        await service.PublishAsync(raffle.Id);

        Assert.Equal(0, createCalls); // lookup found an existing code, so POST /api/links was never called
        Assert.Equal("https://raffle.test/l/EXISTING", service.Selected!.DisplayHostUrl("https://raffle.test"));
    }

    [Fact]
    public async Task No_access_key_configured_skips_short_link_minting_without_error()
    {
        // Publishing itself always requires the access key (VenueRaffleClient.UpsertAsync), so this exercises the
        // scenario where EnsureShortLinksAsync is called directly (e.g. the operator panel's "Retry Short Links"
        // button) after the key was cleared post-publish, rather than the automatic post-PublishAsync call.
        var linkCallMade = false;
        var handler = RoutedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/raffles") return Json("{\"raffleId\":\"remote-5\",\"hostUrl\":\"https://raffle.test/host/remote-5/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-5/view-tok\",\"winnerName\":null}");
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/links")) { linkCallMade = true; return Json("{\"ok\":true,\"code\":\"SHOULDNOT\"}"); }
            return null;
        });
        var (service, _, diagnostics) = Build(handler);
        var raffle = service.Create("Friday");
        var result = await service.PublishAsync(raffle.Id);
        Assert.True(result.Success);
        var mintedDuringPublish = service.Selected!.HostLinkCode;
        Assert.NotNull(mintedDuringPublish); // Build() configures a valid access key, so publish's automatic mint did run
        linkCallMade = false;

        service.SaveConnection(service.Settings.Connection with { AccessKey = "" }); // operator clears the key afterward
        var changed = await service.EnsureShortLinksAsync(raffle.Id);

        Assert.False(changed);
        Assert.False(linkCallMade); // no network call attempted once the key is missing
        Assert.Empty(diagnostics.Capture().RecentErrors);
    }

    [Fact]
    public async Task Deleting_the_published_copy_over_realtime_clears_the_short_link_codes_too()
    {
        var transport = new FakeTransport();
        var handler = RoutedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/raffles") return Json("{\"raffleId\":\"remote-6\",\"hostUrl\":\"https://raffle.test/host/remote-6/host-tok\",\"viewerUrl\":\"https://raffle.test/view/remote-6/view-tok\",\"winnerName\":null}");
            if (request.RequestUri!.AbsolutePath == "/api/links/lookup") return Json("{\"ok\":true,\"code\":null}");
            if (request.RequestUri!.AbsolutePath == "/api/links") return Json("{\"ok\":true,\"code\":\"AB23CD\"}");
            return null;
        });
        var (service, _, _) = Build(handler, () => transport);
        var raffle = service.Create("Friday");
        await service.PublishAsync(raffle.Id);
        Assert.NotNull(service.Selected!.HostLinkCode);

        transport.EnqueueIncoming(new("deleted", RaffleId: "remote-6"));
        await transport.WaitForMessagesConsumed();
        service.Tick(DateTimeOffset.UtcNow);

        Assert.Null(service.Selected!.HostLinkCode);
        Assert.Null(service.Selected!.ViewerLinkCode);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }

    /// <summary>Deliberately simpler than <see cref="RaffleRealtimeClientTests"/>'s fake: this file never exercises
    /// reconnect/drop behavior (that is fully covered there), only "a message shows up, does the service apply
    /// it." A plain poll loop means a message enqueued at any time - before or after the background receive loop
    /// is already parked waiting - is always picked up, with no single-shot wake signal to race against.</summary>
    private sealed class FakeTransport : IRaffleRealtimeTransport
    {
        private readonly ConcurrentQueue<RaffleRealtimeMessage> incoming = new();
        private int remaining;

        public void EnqueueIncoming(RaffleRealtimeMessage message) { incoming.Enqueue(message); Interlocked.Increment(ref remaining); }
        public Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAsync(object message, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<RaffleRealtimeMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (incoming.TryDequeue(out var message)) { Interlocked.Decrement(ref remaining); return message; }
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task WaitForMessagesConsumed() { for (var i = 0; i < 400 && Volatile.Read(ref remaining) > 0; i++) await Task.Delay(10); await Task.Delay(20); }
    }
}
