using VenueOS.Core;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Post-0.3.7 first-refresh investigation: operation ownership/lifecycle bookkeeping
/// (<see cref="PartyFinderOperationTracker"/>), how <see cref="PartyFinderService"/> behaves across Start Recruitment
/// → first refresh → later refreshes on top of it, and proof that closing/never-drawing the module's presentation
/// cannot change any of it. The native chain itself is live-QA-only (VenueOS.Plugin has no test project).</summary>
public sealed class PartyFinderOperationOwnershipTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- tracker: ownership ----------------------------------------------------------------------------------

    [Fact] public void Beginning_an_operation_takes_ownership_and_finishing_releases_it()
    {
        var tracker = new PartyFinderOperationTracker();
        Assert.False(tracker.IsRunning);

        var op = tracker.Begin(PartyFinderOperationKind.Update, "operator panel", T0);
        Assert.True(tracker.IsRunning);
        Assert.Same(op, tracker.Current);

        var record = tracker.Finish(PartyFinderOperationOutcome.Completed, "done", T0.AddSeconds(2));
        Assert.False(tracker.IsRunning);
        Assert.NotNull(record);
        Assert.Equal(PartyFinderOperationOutcome.Completed, record!.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), record.Elapsed);
    }

    [Theory]
    [InlineData(PartyFinderOperationOutcome.Failed)]
    [InlineData(PartyFinderOperationOutcome.Stopped)]
    [InlineData(PartyFinderOperationOutcome.Aborted)]
    [InlineData(PartyFinderOperationOutcome.ChainLost)]
    public void Ownership_is_released_on_every_non_success_outcome_too(PartyFinderOperationOutcome outcome)
    {
        var tracker = new PartyFinderOperationTracker();
        tracker.Begin(PartyFinderOperationKind.Update, "5 minute warning", T0);
        tracker.Finish(outcome, "why", T0.AddSeconds(1));
        Assert.False(tracker.IsRunning);
        Assert.Null(tracker.Current);

        tracker.Begin(PartyFinderOperationKind.Update, "next", T0.AddSeconds(2)); // a new request can own it again
        Assert.True(tracker.IsRunning);
    }

    [Fact] public void A_double_finish_is_harmless()
    {
        var tracker = new PartyFinderOperationTracker();
        tracker.Begin(PartyFinderOperationKind.Update, "x", T0);
        Assert.NotNull(tracker.Finish(PartyFinderOperationOutcome.Completed, "done", T0));
        Assert.Null(tracker.Finish(PartyFinderOperationOutcome.Failed, "stray second finish", T0));
        Assert.Equal(PartyFinderOperationOutcome.Completed, tracker.Last!.Outcome);
    }

    [Fact] public void Only_one_operation_is_ever_current_a_new_begin_aborts_the_previous_one()
    {
        var tracker = new PartyFinderOperationTracker();
        var first = tracker.Begin(PartyFinderOperationKind.Update, "refresh", T0);
        var second = tracker.Begin(PartyFinderOperationKind.End, "end", T0.AddSeconds(1));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Same(second, tracker.Current);
        Assert.Equal(PartyFinderOperationOutcome.Aborted, tracker.Last!.Outcome);
        Assert.Equal(first.Id, tracker.Last.Id);
    }

    [Fact] public void The_current_step_and_milestone_timeline_are_recorded_for_diagnostics()
    {
        var tracker = new PartyFinderOperationTracker();
        tracker.Begin(PartyFinderOperationKind.Update, "operator panel", T0);
        tracker.EnterStep("open_pf", T0.AddMilliseconds(50));
        tracker.EnterStep("open_pf", T0.AddMilliseconds(60)); // re-entering the same step every tick must not spam
        tracker.Mark("edit-clicked", T0.AddMilliseconds(400));
        tracker.EnterStep("submit_editor", T0.AddMilliseconds(1200));

        Assert.Equal("submit_editor", tracker.Current!.Step);
        var record = tracker.Finish(PartyFinderOperationOutcome.Failed, "boom", T0.AddMilliseconds(1500))!;
        Assert.Equal("submit_editor", record.LastStep);
        Assert.Equal("begin@0ms > open_pf@50ms > edit-clicked@400ms > submit_editor@1200ms > failed@1500ms", record.Timeline);
    }

    // ---- tracker: first refresh vs later refresh -------------------------------------------------------------

    [Fact] public void The_first_update_after_a_completed_create_is_flagged_first_and_the_next_is_not()
    {
        var tracker = new PartyFinderOperationTracker();

        tracker.Begin(PartyFinderOperationKind.Create, "operator panel", T0);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "created", T0.AddSeconds(3));

        var first = tracker.Begin(PartyFinderOperationKind.Update, "5 minute warning", T0.AddMinutes(55));
        Assert.True(first.IsFirstUpdateForListing);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "ok", T0.AddMinutes(55).AddSeconds(3));

        var second = tracker.Begin(PartyFinderOperationKind.Update, "5 minute warning", T0.AddMinutes(110));
        Assert.False(second.IsFirstUpdateForListing);
        Assert.Equal(1, tracker.UpdatesCompletedForListing);
    }

    [Fact] public void A_failed_or_aborted_first_update_leaves_the_next_one_still_flagged_first()
    {
        var tracker = new PartyFinderOperationTracker();
        tracker.Begin(PartyFinderOperationKind.Create, "x", T0);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "created", T0);

        tracker.Begin(PartyFinderOperationKind.Update, "r1", T0);
        tracker.Finish(PartyFinderOperationOutcome.Failed, "Apply Changes was not accepted", T0);
        Assert.True(tracker.Begin(PartyFinderOperationKind.Update, "r1 retry", T0).IsFirstUpdateForListing);
        tracker.Finish(PartyFinderOperationOutcome.Aborted, "operator abort", T0);
        Assert.True(tracker.Begin(PartyFinderOperationKind.Update, "r1 again", T0).IsFirstUpdateForListing);
    }

    [Fact] public void Ending_the_listing_or_a_venue_reset_makes_the_next_update_a_first_update_again()
    {
        var tracker = new PartyFinderOperationTracker();
        tracker.Begin(PartyFinderOperationKind.Update, "r", T0);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "ok", T0);
        Assert.Equal(1, tracker.UpdatesCompletedForListing);

        tracker.ResetListingHistory(); // native "recruitment has ended" chat line, or venue reset
        Assert.True(tracker.Begin(PartyFinderOperationKind.Update, "r", T0).IsFirstUpdateForListing);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "ok", T0);

        tracker.Begin(PartyFinderOperationKind.End, "end", T0);
        tracker.Finish(PartyFinderOperationOutcome.Completed, "withdrawn", T0);
        Assert.Equal(0, tracker.UpdatesCompletedForListing);
    }

    // ---- service on top: Start -> first refresh -> later refreshes, presentation independence ---------------

    [Fact] public void Start_then_first_then_second_refresh_each_run_once_with_ownership_held_and_released()
    {
        var (service, _, venueId, engine, _) = Create();
        service.Load(venueId);

        service.CreateOrUpdate("operator panel"); // Start Recruitment
        Assert.True(service.IsBusy);
        service.CreateOrUpdate("spam click");
        Assert.Single(engine.Begun);
        engine.Complete(); // Start finishes; listing now exists
        Assert.False(service.IsBusy);
        Assert.True(service.HasOwnListing);

        service.Refresh("5 minute warning"); // first refresh after Start
        Assert.True(service.IsBusy);
        Assert.True(engine.Tracker.Current!.IsFirstUpdateForListing);
        service.Refresh("manual click while running");
        Assert.Equal(2, engine.Begun.Count);
        engine.Complete();

        service.Refresh("5 minute warning"); // second refresh
        Assert.False(engine.Tracker.Current!.IsFirstUpdateForListing);
        Assert.Equal(3, engine.Begun.Count);
        engine.Complete();
        Assert.False(service.IsBusy);
    }

    [Fact] public void A_failed_first_refresh_releases_ownership_so_the_next_request_is_accepted()
    {
        var (service, _, venueId, engine, _) = Create();
        service.Load(venueId);
        service.CreateOrUpdate("operator panel");
        engine.Complete();

        service.Refresh("5 minute warning");
        engine.Fail("Submitted Party Finder refresh, but the editor did not close.");
        Assert.False(service.IsBusy);

        service.Refresh("manual retry");
        Assert.Equal(3, engine.Begun.Count);
        Assert.True(engine.Tracker.Current!.IsFirstUpdateForListing); // still the first *successful* update
    }

    [Fact] public void Abort_releases_ownership_and_never_changes_auto_refresh_or_the_preset()
    {
        var (service, _, venueId, engine, _) = Create();
        service.Load(venueId);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Keep me" });
        service.CreateOrUpdate("operator panel");
        engine.Complete();
        service.Refresh("5 minute warning");

        service.Abort();

        Assert.False(service.IsBusy);
        Assert.True(service.Settings.AutoRefreshEnabled);
        Assert.Equal("Keep me", service.Settings.Preset.Comment);
    }

    /// <summary>Closing the VenueOS Party Finder window must not stop, alter, or depend on automation. Proven here at
    /// the module boundary: a module whose Draw delegate is never invoked (the window closed / never opened) runs the
    /// whole Start → refresh → refresh sequence identically to one that is drawn every frame, and drawing never
    /// changes service state.</summary>
    [Fact] public void Closing_the_presentation_does_not_change_automation_or_service_state()
    {
        static (List<string> Trace, int Draws) Run(bool presentationOpen)
        {
            var (service, _, venueId, engine, clock) = Create();
            var draws = 0;
            var module = new PartyFinderModule(service, () => draws++);
            var trace = new List<string>();

            void Frame()
            {
                module.Tick(DateTimeOffset.UnixEpoch);
                if (presentationOpen) module.Draw(); // the panel's Draw is the only thing "closing the window" removes
                trace.Add($"busy={service.IsBusy} status={service.Status} listing={service.HasOwnListing} auto={service.Settings.AutoRefreshEnabled}");
            }

            module.OnVenueChangedAsync(new VenueContext(venueId, "Venue", new object()), CancellationToken.None).GetAwaiter().GetResult();
            Frame();
            service.CreateOrUpdate("operator panel"); Frame();
            engine.Complete(); Frame();
            clock.UtcNow += TimeSpan.FromMinutes(55); // the native warning arrives ~55 minutes into the listing
            service.HandleChatText("Your party recruitment closes in five minutes."); Frame();
            engine.Complete(); Frame();
            service.Refresh("second"); Frame();
            engine.Complete(); Frame();
            return (trace, draws);
        }

        var closed = Run(presentationOpen: false);
        var open = Run(presentationOpen: true);

        Assert.Equal(open.Trace, closed.Trace);
        Assert.Equal(0, closed.Draws);
        Assert.True(open.Draws > 0);
    }

    [Fact] public void The_service_and_module_layer_has_no_dependency_on_any_UI_assembly()
    {
        foreach (var assembly in new[] { typeof(PartyFinderService).Assembly, typeof(PartyFinderModule).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
            Assert.DoesNotContain(references, r => r.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, r => r.Contains("Dalamud", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static (PartyFinderService Service, VenueProfileService Profiles, Guid VenueId, TrackerBackedEngine Engine, FakeClock Clock) Create()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var engine = new TrackerBackedEngine();
        var clock = new FakeClock();
        return (new PartyFinderService(engine, profiles, clock), profiles, profiles.Current.Id, engine, clock);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
    }

    /// <summary>Models ONLY the engine's ownership contract — the same calls the real
    /// <c>PartyFinderAutomationService</c> makes on the real <see cref="PartyFinderOperationTracker"/> (Begin on queue,
    /// Finish on success/failure/abort). It does not model, and this file does not claim to test, any native FFXIV
    /// behavior.</summary>
    private sealed class TrackerBackedEngine : IPartyFinderAutomation
    {
        private DateTime now = T0;
        public PartyFinderOperationTracker Tracker { get; } = new();
        public List<long> Begun { get; } = [];
        public string Status { get; private set; } = "Idle";
        public bool IsCompatibilityVerified => true;
        public bool HasOwnListing { get; private set; }
        public bool IsBusy => Tracker.IsRunning;
        public bool IsEnding => false;

        public void ResetForVenue() { Tracker.Finish(PartyFinderOperationOutcome.Aborted, "venue reset", Tick()); Tracker.ResetListingHistory(); }
        public void Abort() { Tracker.Finish(PartyFinderOperationOutcome.Aborted, "operator abort", Tick()); Status = "Aborted"; }
        public void QueueCreateOrUpdate(PartyFinderPreset preset, string reason) => Queue(HasOwnListing ? PartyFinderOperationKind.Update : PartyFinderOperationKind.Create, reason);
        public void QueueRefresh(PartyFinderPreset preset, string reason) => Queue(PartyFinderOperationKind.Update, reason);
        public void EndPartyFinder(string reason) { }
        public void NotifyListingEnded() { HasOwnListing = false; Tracker.ResetListingHistory(); }

        public void Complete()
        {
            var kind = Tracker.Current!.Kind;
            Tracker.Finish(PartyFinderOperationOutcome.Completed, "ok", Tick());
            if (kind == PartyFinderOperationKind.Create) HasOwnListing = true;
            Status = "Automation sequence finished.";
        }

        public void Fail(string reason) { Tracker.Finish(PartyFinderOperationOutcome.Failed, reason, Tick()); Status = reason; }

        private void Queue(PartyFinderOperationKind kind, string reason)
        {
            Begun.Add(Tracker.Begin(kind, reason, Tick()).Id);
            Status = $"Queued {kind}.";
        }

        private DateTime Tick() => now = now.AddSeconds(1);
    }
}
