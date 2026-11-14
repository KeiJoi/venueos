using VenueOS.Core;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Travel;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>0.3.14 end to end: the REAL <see cref="ShoutRunnerService"/> drives the REAL <see cref="NativeTravelService"/>
/// against <see cref="NativeTravelSimulator"/>, through an adapter that does exactly what the plugin's
/// ShoutRunnerAutomationService does (game-data classification, native travel + <see cref="ShoutRunnerNativeTravelMapping"/>,
/// World + logical Data Center for the pre-shout guard). Every /shout is attributed to the World and Data Center the
/// simulated character is actually on at dispatch time. This proves the integration logic, not the real game UI.</summary>
public sealed class ShoutRunnerNativeTravelIntegrationTests
{
    private const string Uldah = "Ul'dah - Steps of Nald";
    private const string Gridania = "New Gridania";
    private const string Limsa = "Limsa Lominsa Lower Decks";

    private sealed class NativeAutomation(NativeTravelSimulator sim, NativeTravelService travel, Func<IEnumerable<string>> routeDestinations) : IShoutRunnerAutomation
    {
        public readonly List<(string World, bool CrossDataCenter)> Requests = [];
        public void ResetForVenue() { }
        public void Abort() { if (travel.Status.Owner == NativeTravelOwner.ShoutRunner) travel.CancelActive("ShoutRunner stopped"); }
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) =>
            Task.FromResult(ShoutRunnerTravelClassification.Classify(sim.Current, sim.Home, sim.Catalog.Resolve(targetWorld), ShoutRunnerCatalog.DataCenterFor(targetWorld)));
        public async Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token)
        {
            Requests.Add((targetWorld, crossDataCenter));
            // As in production: the World Visit cities on the run's destination list, first one preferred.
            var result = await travel.TravelAsync(new NativeTravelRequest(targetWorld, NativeTravelOwner.ShoutRunner, WorldVisitCities.FromRouteDestinations(routeDestinations())), token);
            return ShoutRunnerNativeTravelMapping.ToTransferOutcome(result);
        }
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<ShoutRunnerObservedLocation?> TryGetCurrentLocationAsync(CancellationToken token) =>
            Task.FromResult<ShoutRunnerObservedLocation?>(new(sim.Current.Name, sim.Current.LogicalDataCenter));
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan span) => UtcNow += span;
    }

    private sealed class Store : IShoutRunnerRecoveryStore
    {
        public ShoutRunnerRecoveryJournal? Current { get; private set; }
        public int Saves { get; private set; }
        public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return Current; }
        public void Save(ShoutRunnerRecoveryJournal journal) { Current = journal; Saves++; }
        public void Delete() => Current = null;
    }

    private sealed class Rig
    {
        public readonly NativeTravelSimulator Sim;
        public readonly NativeTravelService Travel;
        public readonly NativeAutomation Automation;
        public readonly ChatCommandService Chat;
        public readonly ShoutRunnerService Service;
        public readonly Store Store = new();
        public readonly Clock Clock = new();
        public readonly VenueProfileService Profiles;
        public readonly List<(string World, string DataCenter, string Command)> Sent = [];

        /// <param name="allowDc">null leaves <see cref="NativeTravelService.AllowDataCenterSelection"/> at its shipped default,
        /// which is what a fresh plugin load (and so a real ShoutRunner start) uses.</param>
        public Rig(string start, bool? allowDc, params string[] dataCenters) : this(start, allowDc, [Uldah, Gridania, Limsa], dataCenters) { }

        public Rig(string start, bool? allowDc, string[] destinations, string[] dataCenters)
        {
            Sim = new NativeTravelSimulator(start, home: "Cuchulainn");
            Travel = new NativeTravelService(Sim, Sim.Catalog);
            if (allowDc is { } allow) Travel.AllowDataCenterSelection = allow;
            Automation = new NativeAutomation(Sim, Travel, () => Service!.Settings.Destinations);
            var host = new ModuleHost();
            Profiles = new VenueProfileService(new InMemoryVenueStore(), host);
            Chat = new ChatCommandService(Clock, new InlineFrameworkDispatcher(), command => { Sent.Add((Sim.Current.Name, Sim.Current.LogicalDataCenter, command)); return true; }, TimeSpan.Zero);
            Service = new ShoutRunnerService(Automation, Chat, Profiles, new DiagnosticsService(host, Profiles, Clock), Clock, Store);
            Service.Load(Profiles.Current.Id);
            Service.UpdateShoutMessage("Test shout");
            while (Service.Settings.Destinations.Count > 0) Service.RemoveDestinationAt(0);
            foreach (var d in destinations) Service.AddDestination(d);
            foreach (var dc in dataCenters) Service.SetDataCenterSelected(dc, true);
            Service.SetRepeatEnabled(false);
        }

        public List<string> Texts() => [.. Service.TerminalEvents.Select(e => e.Text)];

        public void PumpUntil(Func<bool> condition, int maxSteps = 20000)
        {
            for (var i = 0; i < maxSteps; i++)
            {
                if (condition()) return;
                if (Service.State == ShoutRunnerState.WaitingActionDelay) Clock.Advance(TimeSpan.FromSeconds(Service.Settings.ClampedDelaySeconds() + 1));
                Chat.TickAsync().GetAwaiter().GetResult();
                Service.Tick(Clock.UtcNow);
            }

            Assert.True(condition(), $"Condition never met (state {Service.State}). Terminal: {string.Join(" | ", Texts().TakeLast(8))}");
        }
    }

    [Theory]
    [InlineData(new[] { Uldah, Gridania, Limsa }, 9u)]
    [InlineData(new[] { Gridania, Limsa, Uldah }, 2u)]
    [InlineData(new[] { "The Gold Saucer", Limsa, Uldah }, 8u)]
    public void Starting_away_from_a_World_Visit_aetheryte_teleports_to_the_first_World_Visit_city_on_the_list(string[] destinations, uint expectedAetheryte)
    {
        var rig = new Rig("Siren", allowDc: true, destinations, ["Aether"]);   // the first World (Adamantoise) needs travel
        rig.Sim.Territory = 1186;                                               // a field zone, no World Visit aetheryte
        rig.Sim.AetheryteDistance = 100f;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sim.Count(NativeTravelActionKind.ConfirmTravel) >= 1);
        Assert.Equal(NativeTravelActionKind.TeleportToCity, rig.Sim.Executed[0].Kind);
        Assert.Equal(expectedAetheryte, rig.Sim.Executed[0].Id);
        Assert.Equal(1, rig.Sim.Count(NativeTravelActionKind.TeleportToCity));
        rig.Service.Stop();
    }

    /// <summary>Live QA 2026-11-13 (evening): the first full ShoutRunner run after the arrows were verified skipped every
    /// other Data Center, because Data Center selection was still off by default and the QA switch resets on reload. A
    /// fresh service must travel between Data Centers without anyone touching Native Travel QA.</summary>
    [Fact]
    public void A_fresh_plugin_load_travels_between_Data_Centers_without_touching_the_QA_switch()
    {
        var fresh = new NativeTravelSimulator("Balmung");
        Assert.True(new NativeTravelService(fresh, fresh.Catalog).AllowDataCenterSelection);

        var rig = new Rig("Balmung", allowDc: null, "Aether", "Crystal", "Dynamis", "Primal");   // starts on Crystal; the first World is Aether's
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        Assert.DoesNotContain(rig.Texts(), t => t.Contains("SKIPPED", StringComparison.Ordinal));
        Assert.Equal(ShoutRunnerCatalog.CanonicalDataCenterOrder.SelectMany(ShoutRunnerCatalog.WorldsIn), rig.Sent.Select(s => s.World).Distinct());
        Assert.Equal(32 * 3, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal(ShoutRunnerCatalog.DataCenterFor(s.World), s.DataCenter));
        Assert.Equal(4, rig.Sim.Count(NativeTravelActionKind.SelectDataCenter));          // Crystal→Aether, then the three boundaries
        Assert.Contains("RUN 1 complete.", rig.Texts());
    }

    [Fact]
    public void Full_route_Aether_Crystal_Dynamis_Primal_natively_with_every_shout_on_its_own_World_and_Data_Center()
    {
        var rig = new Rig("Siren", allowDc: true, "Aether", "Crystal", "Dynamis", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        var expected = ShoutRunnerCatalog.CanonicalDataCenterOrder.SelectMany(ShoutRunnerCatalog.WorldsIn).ToList();
        Assert.Equal(expected, rig.Sent.Select(s => s.World).Distinct());
        Assert.Equal(32 * 3, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal(ShoutRunnerCatalog.DataCenterFor(s.World), s.DataCenter));
        Assert.Equal(3, rig.Sim.Count(NativeTravelActionKind.SelectDataCenter));          // only at the three DC boundaries
        Assert.Equal(32, rig.Sim.Count(NativeTravelActionKind.ConfirmTravel));            // one confirmation per World
        Assert.Equal(0, rig.Sim.Refused.Count(a => a.Kind == NativeTravelActionKind.ConfirmTravel));
        Assert.False(rig.Sim.LoggedOut);
        Assert.Contains("RUN 1 complete.", rig.Texts());
        Assert.Null(rig.Store.Current);
    }

    [Fact]
    public void With_the_QA_switch_off_other_Data_Centers_are_skipped_before_any_UI_is_touched_and_the_home_DC_runs()
    {
        var rig = new Rig("Siren", allowDc: false, "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Equal(8 * 3, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal("Aether", s.DataCenter));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Primal — SKIPPED: data center unavailable.", StringComparison.Ordinal) && t.Contains("switched off in Native Travel QA"));
        Assert.Equal(1, rig.Automation.Requests.Count(r => ShoutRunnerCatalog.DataCenterFor(r.World) == "Primal"));
        Assert.Equal(0, rig.Sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void A_misclick_toward_Jenova_is_never_confirmed_faults_safely_and_Resume_completes_Cactuar()
    {
        var rig = new Rig("Adamantoise", allowDc: true, "Aether");
        rig.Sim.SelectWorldRowOffset = 3;   // Cactuar row → "Travel to Jenova?"
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);

        Assert.Contains(rig.Texts(), t => t.Contains("SelectionMismatch") && t.Contains("Travel to Jenova?"));
        Assert.All(rig.Sent, s => Assert.Equal("Adamantoise", s.World));
        Assert.Equal(0, rig.Sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Equal("Cactuar", rig.Store.Current!.CurrentWorld);

        rig.Sim.SelectWorldRowOffset = 0;   // e.g. after the selector is corrected
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig.Service.Resume());
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Cactuar") == 3);
        Assert.DoesNotContain(rig.Sent, s => s.World == "Jenova");
    }

    [Fact]
    public void A_World_whose_click_opens_no_confirmation_is_skipped_and_its_Data_Center_continues()
    {
        var rig = new Rig("Siren", allowDc: true, "Aether");
        rig.Sim.UnselectableWorlds.Add("Faerie");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Faerie — SKIPPED", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Sent, s => s.World == "Faerie");
        Assert.Equal(7 * 3, rig.Sent.Count);
        Assert.Contains("Aether — completed with skips.", rig.Texts());
    }

    [Fact]
    public void Two_Worlds_in_a_row_that_open_no_confirmation_stop_the_run_resumably()
    {
        var rig = new Rig("Siren", allowDc: true, "Aether");
        rig.Sim.UnselectableWorlds.UnionWith(["Cactuar", "Faerie"]);   // consecutive in the Aether route
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State is ShoutRunnerState.Faulted or ShoutRunnerState.Stopped);
        Assert.Equal(ShoutRunnerState.Faulted, rig.Service.State);
        Assert.NotNull(rig.Store.Current);   // resumable
        Assert.DoesNotContain(rig.Sent, s => s.World is "Cactuar" or "Faerie");
    }

    [Fact]
    public void A_congested_World_row_is_skipped_and_its_Data_Center_continues()
    {
        var rig = new Rig("Siren", allowDc: true, "Aether");
        rig.Sim.DisabledWorlds.Add("Faerie");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Faerie — SKIPPED: congested.", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Sent, s => s.World == "Faerie");
        Assert.Equal(7 * 3, rig.Sent.Count);
        Assert.Contains("Aether — completed with skips.", rig.Texts());
    }

    [Fact]
    public async Task Stop_during_native_travel_cancels_it_closes_the_menus_and_sends_nothing()
    {
        var rig = new Rig("Siren", allowDc: true, "Aether");   // the first World (Adamantoise) needs travel
        var hold = new TaskCompletionSource();
        rig.Sim.DelayHook = token => rig.Sim.WindowOpen ? hold.Task.WaitAsync(token) : AdvanceAsync(rig.Sim, token);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sim.WindowOpen && rig.Travel.IsBusy);
        Assert.Empty(rig.Sent);

        rig.Service.Stop();
        await WaitUntilAsync(() => !rig.Travel.IsBusy);
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        Assert.Equal(NativeTravelOutcome.Cancelled, rig.Travel.LastResult!.Outcome);
        Assert.False(rig.Sim.WindowOpen || rig.Sim.ConfirmOpen || rig.Sim.MenuOpen);
        Assert.Equal(0, rig.Sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Empty(rig.Sent);
        Assert.Null(rig.Store.Current);
    }

    [Fact]
    public async Task A_QA_travel_test_sends_no_announcement_and_never_touches_the_ShoutRunner_checkpoint()
    {
        var rig = new Rig("Adamantoise", allowDc: true, "Aether");
        var savesBefore = rig.Store.Saves;
        var result = await rig.Travel.TravelAsync(new NativeTravelRequest("Behemoth", NativeTravelOwner.QaTest, [WorldVisitCities.Uldah]), default);
        for (var i = 0; i < 20; i++) { await rig.Chat.TickAsync(); rig.Service.Tick(rig.Clock.UtcNow); }

        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal("Behemoth", rig.Sim.Current.Name);
        Assert.Empty(rig.Sent);
        Assert.Equal(savesBefore, rig.Store.Saves);
        Assert.Null(rig.Store.Current);
        Assert.Equal(ShoutRunnerState.Stopped, rig.Service.State);
    }

    [Fact]
    public async Task ShoutRunner_cannot_travel_while_a_QA_test_owns_the_service_and_faults_resumably()
    {
        var rig = new Rig("Adamantoise", allowDc: true, "Aether");
        var hold = new TaskCompletionSource();
        rig.Sim.DelayHook = token => hold.Task.WaitAsync(token);
        var qa = rig.Travel.TravelAsync(new NativeTravelRequest("Faerie", NativeTravelOwner.QaTest, []), default);
        Assert.Equal(NativeTravelOwner.QaTest, rig.Travel.Status.Owner);

        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);
        Assert.Contains(rig.Texts(), t => t.Contains("Busy") && t.Contains("another travel operation"));
        Assert.Empty(rig.Sent);
        Assert.NotNull(rig.Store.Current);   // resumable

        rig.Service.Stop();   // explicit operator decision to abandon
        Assert.Equal(NativeTravelOwner.QaTest, rig.Travel.Status.Owner);   // ShoutRunner's Stop never cancels a QA test
        rig.Travel.CancelActive("QA done");
        Assert.Equal(NativeTravelOutcome.Cancelled, (await qa.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    private static Task AdvanceAsync(NativeTravelSimulator sim, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        sim.Now += NativeTravelService.PollInterval;
        return Task.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
