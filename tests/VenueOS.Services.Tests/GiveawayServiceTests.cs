using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>
/// <see cref="GiveawayService"/>: preset persistence/isolation (NEW_MODULE_GUIDE.md §13a/§30), the announcement
/// timeline/roll-window state machine (GIVEAWAYS spec §9/§10/§19), cancellation/stale-timer safety (§24a), and the
/// <see cref="GiveawaysModule"/> descriptor (§42/§43). Drives <see cref="SchedulerService"/>/<see cref="ChatCommandService"/>
/// with a controllable <see cref="Clock"/>, ticking both every simulated second — the same style
/// <c>SharedServicesTests</c> and <c>TournamentControlTests</c> already use for this shared infrastructure.
/// </summary>
public sealed class GiveawayServiceTests
{
    // =========================================================================================================
    // Preset persistence / isolation (spec §6/§29/§30/§31)
    // =========================================================================================================

    [Fact]
    public void Create_preset_adds_it_to_settings_and_persists()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Friday Giveaway");

        var reloaded = new GiveawayService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock());
        reloaded.Load(t.VenueId);
        Assert.Contains(reloaded.Settings.Presets, x => x.Id == preset.Id && x.Name == "Friday Giveaway");
    }

    [Fact]
    public void Rename_preset_persists()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Original Name");
        t.Service.RenamePreset(preset.Id, "Renamed");

        var reloaded = new GiveawayService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock());
        reloaded.Load(t.VenueId);
        Assert.Equal("Renamed", reloaded.Settings.Presets.Single().Name);
    }

    [Fact]
    public void Delete_preset_removes_it_and_persists()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Temporary");
        Assert.True(t.Service.DeletePreset(preset.Id));

        var reloaded = new GiveawayService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock());
        reloaded.Load(t.VenueId);
        Assert.Empty(reloaded.Settings.Presets);
    }

    [Fact]
    public void Every_preset_field_survives_a_full_serialize_deserialize_boundary()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Friday Night 1M Giveaway");
        t.Service.UpdatePreset(preset.Id, p => p with
        {
            Channel = GiveawayChatChannel.Yell,
            DelayBetweenLinesSeconds = 3,
            GiveawayDurationSeconds = 120,
            StartBlock = new GiveawayAnnouncementBlock(["Starting now!", "Roll /random!"]),
            MidpointBlock = new GiveawayAnnouncementBlock(["Halfway there!"]),
            ClosingBlock = new GiveawayAnnouncementBlock(["Closed!", "Winner soon.", "Thanks all!"]),
            WinnerMode = GiveawayWinnerMode.Closest,
            ClosestTargetNumber = 777,
            AllowedRollsPerPerson = 3,
            SpecialNumbersRaw = "69,420",
        });
        t.Service.SelectPreset(preset.Id);

        var rebuiltSnapshot = JsonSerializer.Deserialize<VenueStoreSnapshot>(JsonSerializer.Serialize(t.Store.Read()))!;
        var freshProfiles = new VenueProfileService(new InMemoryVenueStore(rebuiltSnapshot), new ModuleHost());
        var fresh = new GiveawayService(t.Scheduler, t.Chat, freshProfiles, t.Clock);
        fresh.Load(t.VenueId);

        var reloaded = fresh.Settings.Presets.Single();
        Assert.Equal("Friday Night 1M Giveaway", reloaded.Name);
        Assert.Equal(GiveawayChatChannel.Yell, reloaded.Channel);
        Assert.Equal(3, reloaded.DelayBetweenLinesSeconds);
        Assert.Equal(120, reloaded.GiveawayDurationSeconds);
        Assert.Equal(["Starting now!", "Roll /random!"], reloaded.StartBlock.Lines);
        Assert.Equal(["Halfway there!"], reloaded.MidpointBlock.Lines);
        Assert.Equal(["Closed!", "Winner soon.", "Thanks all!"], reloaded.ClosingBlock.Lines);
        Assert.Equal(GiveawayWinnerMode.Closest, reloaded.WinnerMode);
        Assert.Equal(777, reloaded.ClosestTargetNumber);
        Assert.Equal(3, reloaded.AllowedRollsPerPerson);
        Assert.Equal("69,420", reloaded.SpecialNumbersRaw);
        Assert.Equal(preset.Id, fresh.Settings.ActivePresetId);
    }

    [Fact]
    public void Two_venues_do_not_share_presets()
    {
        var t = Create();
        var venueB = t.Profiles.Create("Second Venue").Id;

        t.Service.CreatePreset("Venue A Preset");

        t.Service.Load(venueB);
        Assert.Empty(t.Service.Settings.Presets);
        t.Service.CreatePreset("Venue B Preset");

        t.Service.Load(t.VenueId);
        Assert.Equal("Venue A Preset", t.Service.Settings.Presets.Single().Name);

        t.Service.Load(venueB);
        Assert.Equal("Venue B Preset", t.Service.Settings.Presets.Single().Name);
    }

    [Fact]
    public void Active_preset_selection_persists_per_venue()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Chosen One");
        t.Service.SelectPreset(preset.Id);

        var reloaded = new GiveawayService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock());
        reloaded.Load(t.VenueId);
        Assert.Equal(preset.Id, reloaded.Settings.ActivePresetId);
    }

    [Fact]
    public void Running_snapshot_does_not_change_when_the_saved_preset_is_edited_afterward()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 100, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        // Edit the SAVED preset after the run has already captured its snapshot (GIVEAWAYS spec §11).
        t.Service.UpdatePreset(preset.Id, p => p with { GiveawayDurationSeconds = 5, Name = "Edited After Start" });

        Assert.Equal("Test Preset", t.Service.RunningPreset!.Name);
        Assert.Equal(100, t.Service.RunningPreset.GiveawayDurationSeconds);
        Assert.Equal("Edited After Start", t.Service.SelectedPreset!.Name); // the saved record itself did change
    }

    [Fact]
    public void A_running_preset_cannot_be_deleted()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Cannot Delete Me");
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        Assert.False(t.Service.CanDeletePreset(preset.Id));
        Assert.False(t.Service.DeletePreset(preset.Id));
        Assert.Single(t.Service.Settings.Presets);
    }

    // =========================================================================================================
    // Timeline (spec §7-§10/§19)
    // =========================================================================================================

    [Fact]
    public void Start_block_sends_in_line_order_with_the_configured_delay()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 2, duration: 60, start: ["Line 1", "Line 2", "Line 3"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        DispatchImmediate(t); // Line 1 is enqueued by Start(), not yet dispatched — one chat tick actually sends it
        Assert.Equal(["/shout Line 1"], t.Sent);

        Tick(t, 2);
        Assert.Equal(["/shout Line 1", "/shout Line 2"], t.Sent);

        Tick(t, 2);
        Assert.Equal(["/shout Line 1", "/shout Line 2", "/shout Line 3"], t.Sent);
    }

    [Fact]
    public void Blank_lines_within_a_block_are_skipped()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: ["Line 1", "   ", "", "Line 2"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        DispatchImmediate(t); // dispatch Line 1 (enqueued by Start) before Line 2's own scheduled continuation fires
        Tick(t, 1);
        Assert.Equal(["/shout Line 1", "/shout Line 2"], t.Sent);
    }

    [Fact]
    public void An_empty_block_is_skipped_cleanly()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 4, start: []); // nothing to send — acceptance should begin immediately
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Assert.Equal(GiveawayPhase.AcceptingRolls, t.Service.Phase);
    }

    [Fact]
    public void Roll_acceptance_begins_only_after_the_final_start_line_completes()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 2, duration: 60, start: ["Line 1", "Line 2"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Assert.False(t.Service.IsAcceptingRolls); // still sending Line 1/2

        Tick(t, 2); // Line 2 sent, block complete
        Assert.True(t.Service.IsAcceptingRolls);
        Assert.Equal(GiveawayPhase.AcceptingRolls, t.Service.Phase);
    }

    [Fact]
    public void Midpoint_begins_at_half_the_configured_duration()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 20, start: [], midpoint: ["Halfway!"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start(); // Start block empty — acceptance begins immediately at "T=0"
        Tick(t, 9);
        Assert.DoesNotContain("/shout Halfway!", t.Sent);

        Tick(t, 1); // T=10, half of 20
        Assert.Contains("/shout Halfway!", t.Sent);
        Assert.True(t.Service.IsAcceptingRolls); // rolls remain open through Midpoint
    }

    // GIVEAWAYS QA correction: Giveaway Duration now only decides when the CLOSING BLOCK BEGINS — it no longer
    // closes roll acceptance by itself. Roll acceptance stays open through the entire Closing sequence and closes
    // only once Closing's final non-empty line is enqueued, so a Closing block that says something like "last
    // chance to roll!" can't have VenueOS silently stop accepting rolls out from under it. The tests below replace
    // the old "roll acceptance stops exactly at Giveaway Duration" behavior with this one.

    [Fact]
    public void Duration_expiry_starts_closing_but_does_not_close_roll_acceptance_when_closing_has_lines()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 10, start: [], closing: ["Closed!", "Bye!"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Tick(t, 10); // T=10, full duration — Closing begins
        Assert.Equal(GiveawayPhase.Closing, t.Service.Phase);
        Assert.Contains("/shout Closed!", t.Sent);
        Assert.True(t.Service.IsAcceptingRolls); // NOT closed just because the duration elapsed
        Assert.Null(t.Service.TimeRemaining); // the fixed-duration countdown itself is over
    }

    [Fact]
    public void Roll_acceptance_remains_open_through_every_closing_line_and_closes_only_after_the_last_one()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 2, duration: 10, start: [], closing: ["C1", "C2", "C3"]);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        Tick(t, 10); // T=10: Closing begins, C1 enqueued
        Assert.Contains("/shout C1", t.Sent);
        var afterC1 = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 100, GiveawayRollKind.Standard));
        Assert.True(afterC1.Accepted); // a roll received after duration expiry but before the final Closing line

        Tick(t, 2); // T=12: C2 enqueued
        Assert.Contains("/shout C2", t.Sent);
        var betweenC1AndC2 = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("B", "Balmung"), 900, GiveawayRollKind.Standard));
        Assert.True(betweenC1AndC2.Accepted); // a roll received between Closing lines is accepted

        Tick(t, 2); // T=14: C3 (final non-empty line) enqueued — roll acceptance closes immediately after this
        Assert.Contains("/shout C3", t.Sent);
        Assert.Equal(GiveawayPhase.Complete, t.Service.Phase);
        var afterFinalLine = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("C", "Balmung"), 500, GiveawayRollKind.Standard));
        Assert.False(afterFinalLine.Accepted); // rejected immediately after the final Closing line

        // Both accepted Closing-phase rolls landed on the tracker normally (Total Rolls + leaderboard).
        Assert.Equal(2, t.Service.TotalRolls);
        Assert.Equal(2, t.Service.Leaderboard.Count);
        Assert.Contains(t.Service.Leaderboard, x => x.Player.Name == "A");
        Assert.Contains(t.Service.Leaderboard, x => x.Player.Name == "B");
    }

    [Fact]
    public void Empty_closing_block_closes_roll_acceptance_immediately_at_duration_expiry()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 4, start: [], closing: []);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Tick(t, 4); // T=4, full duration — nothing to send, so acceptance closes right away
        Assert.False(t.Service.IsAcceptingRolls);
        Assert.Equal(GiveawayPhase.Complete, t.Service.Phase);

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 100, GiveawayRollKind.Standard));
        Assert.False(outcome.Accepted);
    }

    [Fact]
    public void Multi_line_closing_respects_the_configured_delay_between_lines()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 3, duration: 5, start: [], closing: ["C1", "C2"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Tick(t, 5); // T=5: Closing begins, C1 sent
        Assert.Contains("/shout C1", t.Sent);
        Assert.DoesNotContain("/shout C2", t.Sent);

        Tick(t, 2); // T=7 — not yet the configured 3s delay
        Assert.DoesNotContain("/shout C2", t.Sent);

        Tick(t, 1); // T=8 — exactly 3s after C1
        Assert.Contains("/shout C2", t.Sent);
    }

    [Fact]
    public void Overlapping_blocks_never_interleave_closing_waits_for_midpoint_to_finish()
    {
        // Duration=6 -> Midpoint due at T=3; Midpoint has 4 lines at 2s delay (needs 6s to finish, i.e. done at
        // T=9) — well past Closing's own T=6 deadline. Closing must wait for Midpoint to finish, then start
        // immediately, never interleaving lines from the two blocks (GIVEAWAYS spec §10) — and roll acceptance
        // must stay open throughout this entire wait (GIVEAWAYS QA correction — it no longer closes at T=6).
        var t = Create();
        var preset = MakePreset(t, delay: 2, duration: 6, start: [], midpoint: ["M1", "M2", "M3", "M4"], closing: ["C1"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Tick(t, 6); // T=6: the duration elapses while Midpoint (started at T=3) is still sending
        Assert.True(t.Service.IsAcceptingRolls); // rolls stay open — Closing hasn't even started yet
        Assert.Null(t.Service.TimeRemaining); // but the fixed-duration countdown itself is over
        Assert.DoesNotContain("/shout C1", t.Sent); // Closing must NOT have started yet — Midpoint still in flight
        Assert.Equal(GiveawayPhase.Midpoint, t.Service.Phase);

        // Midpoint's remaining lines finish at T=9 (started T=3, 4 lines * 2s delay); Closing's C1 is enqueued in
        // the same instant M4 is, but ChatCommandService's own 1-message/second pacing means C1 doesn't actually
        // dispatch until the tick after — one more second than the phase transition itself needs.
        Tick(t, 4);
        Assert.Contains("/shout M4", t.Sent);
        Assert.Contains("/shout C1", t.Sent); // Closing starts immediately once Midpoint finishes — never skipped
        Assert.True(t.Sent.IndexOf("/shout M4") < t.Sent.IndexOf("/shout C1")); // never interleaved
        // C1 is Closing's only (and therefore final) line, so roll acceptance closes the instant it's enqueued.
        Assert.False(t.Service.IsAcceptingRolls);
        Assert.Equal(GiveawayPhase.Complete, t.Service.Phase);
    }

    [Fact]
    public void Cancel_during_closing_closes_roll_acceptance_immediately_and_prevents_later_closing_lines()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 3, duration: 4, start: [], closing: ["C1", "C2"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        Tick(t, 4); // T=4: Closing begins, C1 sent, C2 scheduled for T=7
        Assert.Contains("/shout C1", t.Sent);
        Assert.True(t.Service.IsAcceptingRolls);

        t.Service.Cancel();
        Assert.False(t.Service.IsAcceptingRolls);
        Assert.Equal(GiveawayPhase.Cancelled, t.Service.Phase);

        Tick(t, 5); // past T=7 — C2 must never fire
        Assert.DoesNotContain("/shout C2", t.Sent);
    }

    [Fact]
    public void Cancel_prevents_any_future_scheduled_announcement_from_firing()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 2, duration: 60, start: ["Line 1", "Line 2", "Line 3"]);
        t.Service.SelectPreset(preset.Id);

        t.Service.Start();
        DispatchImmediate(t);
        Assert.Equal(["/shout Line 1"], t.Sent);
        t.Service.Cancel();

        Tick(t, 10);
        Assert.Equal(["/shout Line 1"], t.Sent); // Line 2/3 never fire
        Assert.Equal(GiveawayPhase.Cancelled, t.Service.Phase);
    }

    [Fact]
    public void Cancel_preserves_captured_roll_results_until_clear_or_next_start()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();
        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 500, GiveawayRollKind.Standard));

        t.Service.Cancel();
        Assert.Single(t.Service.Leaderboard); // still visible after Cancel

        t.Service.ClearResults();
        Assert.Empty(t.Service.Leaderboard);
    }

    [Fact]
    public void A_stale_run_cancelled_mid_start_block_cannot_leak_a_scheduled_send_into_the_next_run()
    {
        var t = Create();
        var presetA = MakePreset(t, delay: 5, duration: 60, start: ["A1", "A2"]);
        t.Service.SelectPreset(presetA.Id);
        t.Service.Start(); // schedules A2 for T+5

        t.Service.Cancel(); // must cancel that pending scheduled send

        var presetB = MakePreset(t, delay: 5, duration: 60, start: ["B1", "B2"]);
        t.Service.SelectPreset(presetB.Id);
        t.Service.Start();

        Tick(t, 5);
        Assert.DoesNotContain("/shout A2", t.Sent); // the stale run's second line must never fire in run B
        Assert.Contains("/shout B2", t.Sent);
    }

    [Fact]
    public void Venue_switch_cancels_the_current_scheduled_work()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 5, duration: 60, start: ["Line 1", "Line 2"]);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        var venueB = t.Profiles.Create("Second Venue").Id;
        t.Service.Load(venueB); // venue switch — must stop venue A's in-flight run first (§12a)

        Tick(t, 10);
        Assert.DoesNotContain("/shout Line 2", t.Sent);
        Assert.Equal(GiveawayPhase.Idle, t.Service.Phase);
    }

    // =========================================================================================================
    // Roll capture gating (spec §19)
    // =========================================================================================================

    [Fact]
    public void A_roll_observed_before_the_start_block_completes_is_ignored()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 5, duration: 60, start: ["Line 1", "Line 2"]);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start(); // still Starting — Line 2 not sent yet

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 100, GiveawayRollKind.Standard));
        Assert.False(outcome.Accepted);
        Assert.Equal(GiveawayRollRejectReason.GiveawayNotAcceptingRolls, outcome.RejectReason);
    }

    [Fact]
    public void A_roll_observed_after_the_window_closes_is_ignored()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 4, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();
        Tick(t, 4); // window closed

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 100, GiveawayRollKind.Standard));
        Assert.False(outcome.Accepted);
    }

    [Fact]
    public void Start_fails_with_no_preset_selected()
    {
        var t = Create();
        var result = t.Service.Start();
        Assert.False(result.Success);
        Assert.Equal(GiveawayPhase.Idle, t.Service.Phase);
    }

    [Fact]
    public void Start_fails_validation_for_an_invalid_preset()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Bad");
        t.Service.UpdatePreset(preset.Id, p => p with { GiveawayDurationSeconds = 0 });
        t.Service.SelectPreset(preset.Id);

        var result = t.Service.Start();
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Start_clears_previous_live_roll_state()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();
        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("A", "Balmung"), 500, GiveawayRollKind.Standard));
        Assert.Equal(1, t.Service.TotalRolls);

        t.Service.Start(); // fresh run on the same preset
        Assert.Equal(0, t.Service.TotalRolls);
        Assert.Empty(t.Service.Leaderboard);
    }

    // =========================================================================================================
    // Module descriptor (NEW_MODULE_GUIDE.md §42/§43)
    // =========================================================================================================

    [Fact]
    public void Module_id_is_events_giveaways()
    {
        var module = new GiveawaysModule(Create().Service);
        Assert.Equal("events.giveaways", module.Descriptor.Id);
    }

    [Fact]
    public void Display_name_is_giveaways()
    {
        var module = new GiveawaysModule(Create().Service);
        Assert.Equal("Giveaways", module.Descriptor.DisplayName);
    }

    [Fact]
    public void Module_is_promoted_to_production_and_enabled_by_default()
    {
        // Promoted out of UnderDevelopment (NEW_MODULE_GUIDE.md §22a) after live acceptance testing — see
        // docs/GIVEAWAYS_IMPLEMENTATION.md and the release-preparation report.
        var module = new GiveawaysModule(Create().Service);
        Assert.False(module.Descriptor.UnderDevelopment);
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Module_icon_key_is_gift()
    {
        var module = new GiveawaysModule(Create().Service);
        Assert.Equal("gift", module.Descriptor.Icon);
    }

    [Fact]
    public void Draw_and_draw_settings_use_distinct_delegates()
    {
        var drawCalls = 0; var settingsCalls = 0;
        var module = new GiveawaysModule(Create().Service, () => drawCalls++, () => settingsCalls++);
        module.Draw();
        module.DrawSettings();
        Assert.Equal(1, drawCalls);
        Assert.Equal(1, settingsCalls);
    }

    [Fact]
    public async Task Venue_change_loads_that_venues_settings_via_the_module()
    {
        var t = Create();
        var venueB = t.Profiles.Create("Second Venue").Id;
        t.Service.CreatePreset("Venue A Preset");

        var module = new GiveawaysModule(t.Service);
        await module.OnVenueChangedAsync(new VenueContext(venueB, "Second Venue", t.Profiles.Current.Theme), CancellationToken.None);
        Assert.Empty(t.Service.Settings.Presets);
    }

    // =========================================================================================================
    // Preset editor modal workflow (Live-QA fix #2, Issue 1) — the modal itself lives in VenueOS.Plugin and is
    // ImGui-dependent (NEW_MODULE_GUIDE.md §30: no test project exists for that boundary), so these tests exercise
    // the exact service-level building blocks the modal drives (SaveNewPreset for "+ New Preset", UpdatePreset for
    // Edit) and the record-value-copy semantics ("draft" never being the persisted instance) the modal relies on.
    // =========================================================================================================

    [Fact]
    public void Building_a_draft_in_memory_never_touches_persisted_presets()
    {
        var t = Create();
        t.Service.CreatePreset("Existing"); // baseline, so the list isn't trivially empty either way
        var before = t.Service.Settings.Presets.Count;

        var draft = GiveawayPreset.CreateNew("Draft Only");
        draft = draft with { GiveawayDurationSeconds = 42, StartBlock = new GiveawayAnnouncementBlock(["hello"]) };

        Assert.Equal(42, draft.GiveawayDurationSeconds); // the edit really did land — but only on the local draft
        Assert.Equal(before, t.Service.Settings.Presets.Count); // no SaveModuleConfig call ever happened
        Assert.DoesNotContain(t.Service.Settings.Presets, p => p.Name == draft.Name);
    }

    [Fact]
    public void Canceling_new_never_calls_into_the_service_and_leaves_presets_unchanged()
    {
        var t = Create();
        t.Service.CreatePreset("Existing");
        var before = t.Service.Settings.Presets;

        var draft = GiveawayPreset.CreateNew("Would Have Been New");
        // "Cancel" is simply never calling SaveNewPreset — there is nothing else to undo.
        Assert.Same(before, t.Service.Settings.Presets); // literally the same list reference — nothing reassigned Settings
        Assert.DoesNotContain(t.Service.Settings.Presets, p => p.Name == draft.Name);
    }

    [Fact]
    public void Saving_new_creates_exactly_one_preset()
    {
        var t = Create();
        var draft = GiveawayPreset.CreateNew("Friday Giveaway");
        t.Service.SaveNewPreset(draft);
        Assert.Single(t.Service.Settings.Presets);
    }

    [Fact]
    public void Saving_new_persists_every_configured_field()
    {
        var t = Create();
        var draft = GiveawayPreset.CreateNew("Friday Night 1M Giveaway") with
        {
            Channel = GiveawayChatChannel.Yell,
            DelayBetweenLinesSeconds = 3,
            GiveawayDurationSeconds = 120,
            StartBlock = new GiveawayAnnouncementBlock(["Starting now!"]),
            MidpointBlock = new GiveawayAnnouncementBlock(["Halfway!"]),
            ClosingBlock = new GiveawayAnnouncementBlock(["Closed!"]),
            WinnerMode = GiveawayWinnerMode.Closest,
            ClosestTargetNumber = 777,
            AllowedRollsPerPerson = 3,
            SpecialNumbersRaw = "69,420",
        };

        var saved = t.Service.SaveNewPreset(draft);

        var reloaded = new GiveawayService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock());
        reloaded.Load(t.VenueId);
        var persisted = reloaded.Settings.Presets.Single(p => p.Id == saved.Id);
        Assert.Equal("Friday Night 1M Giveaway", persisted.Name);
        Assert.Equal(GiveawayChatChannel.Yell, persisted.Channel);
        Assert.Equal(3, persisted.DelayBetweenLinesSeconds);
        Assert.Equal(120, persisted.GiveawayDurationSeconds);
        Assert.Equal(["Starting now!"], persisted.StartBlock.Lines);
        Assert.Equal(["Halfway!"], persisted.MidpointBlock.Lines);
        Assert.Equal(["Closed!"], persisted.ClosingBlock.Lines);
        Assert.Equal(GiveawayWinnerMode.Closest, persisted.WinnerMode);
        Assert.Equal(777, persisted.ClosestTargetNumber);
        Assert.Equal(3, persisted.AllowedRollsPerPerson);
        Assert.Equal("69,420", persisted.SpecialNumbersRaw);
    }

    [Fact]
    public void Editing_starts_from_a_value_copy_that_can_never_mutate_the_persisted_record()
    {
        var t = Create();
        var original = t.Service.CreatePreset("Original");

        var draft = original; // exactly what GiveawayPresetEditorModal.OpenForEdit does
        draft = draft with { Name = "Edited In The Modal Only", GiveawayDurationSeconds = 999 };

        // The persisted record (still referenced by `original`, and still the one in Settings) is untouched —
        // records are immutable, so the `with` expression above could never have reached back into it.
        Assert.Equal("Original", original.Name);
        Assert.Equal("Original", t.Service.Settings.Presets.Single().Name);
        Assert.NotEqual(999, t.Service.Settings.Presets.Single().GiveawayDurationSeconds);
    }

    [Fact]
    public void Editing_draft_fields_does_not_mutate_persisted_state_before_save()
    {
        var t = Create();
        var original = t.Service.CreatePreset("Original");
        var draft = original with { DelayBetweenLinesSeconds = 55, SpecialNumbersRaw = "1,2,3" };

        // Editing `draft` locally must never be reflected in Settings until an explicit Save call.
        var stillPersisted = t.Service.Settings.Presets.Single(p => p.Id == original.Id);
        Assert.NotEqual(draft.DelayBetweenLinesSeconds, stillPersisted.DelayBetweenLinesSeconds);
        Assert.Equal("", stillPersisted.SpecialNumbersRaw);
    }

    [Fact]
    public void Canceling_edit_leaves_the_persisted_preset_completely_unchanged()
    {
        var t = Create();
        var original = t.Service.CreatePreset("Original");
        var draft = original with { Name = "Changed", GiveawayDurationSeconds = 1234, SpecialNumbersRaw = "5,6,7" };
        // Cancel: draft is simply discarded — no call into the service at all.

        var stillPersisted = t.Service.Settings.Presets.Single(p => p.Id == original.Id);
        Assert.Equal("Original", stillPersisted.Name);
        Assert.Equal(original.GiveawayDurationSeconds, stillPersisted.GiveawayDurationSeconds);
        Assert.Equal("", stillPersisted.SpecialNumbersRaw);
    }

    [Fact]
    public void Saving_edit_updates_the_intended_preset_preserves_its_id_and_creates_no_duplicate()
    {
        var t = Create();
        var other = t.Service.CreatePreset("Untouched Sibling");
        var original = t.Service.CreatePreset("Original");
        var draft = original with { Name = "Renamed Via Modal", DelayBetweenLinesSeconds = 9 };

        t.Service.UpdatePreset(original.Id, _ => draft with { Id = original.Id }); // exactly what the modal's Save does for Edit

        Assert.Equal(2, t.Service.Settings.Presets.Count); // no duplicate created
        var updated = t.Service.Settings.Presets.Single(p => p.Id == original.Id);
        Assert.Equal(original.Id, updated.Id); // stable Id preserved
        Assert.Equal("Renamed Via Modal", updated.Name);
        Assert.Equal(9, updated.DelayBetweenLinesSeconds);
        Assert.Equal("Untouched Sibling", t.Service.Settings.Presets.Single(p => p.Id == other.Id).Name); // sibling untouched
    }

    // =========================================================================================================
    // Self-roll handling (Live-QA fix #2, Issue 2) — RandomRollParserTests.cs proves the parser-level regex fix;
    // these prove the SERVICE treats a resolved self-identity identically to any other participant, with no
    // special-casing anywhere in the acceptance/comparison pipeline. Actual extraction of the local player's
    // Name/HomeWorld happens in GiveawaysRollChatAdapter.ResolveSelf, which is Dalamud-dependent and therefore not
    // unit-testable here (NEW_MODULE_GUIDE.md §30) — these tests instead use a GuestIdentity shaped exactly like
    // what that method produces, to prove everything downstream of it is correct.
    // =========================================================================================================

    private static readonly GuestIdentity SelfIdentity = new("Kei Joi", "Balmung"); // stands in for the resolved local player

    [Fact]
    public void Self_roll_is_accepted_and_resolves_to_the_actual_local_character_name_and_home_world()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 415, GiveawayRollKind.Standard));
        Assert.True(outcome.Accepted);

        var row = t.Service.Leaderboard.Single();
        Assert.Equal("Kei Joi", row.Player.Name); // never "You"
        Assert.Equal("Balmung", row.Player.HomeWorld);
        Assert.Equal(415, row.Value);
    }

    [Fact]
    public void Self_random_999_roll_is_accepted()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 124, GiveawayRollKind.RangedOutOf));
        Assert.True(outcome.Accepted);
        Assert.Equal(1, t.Service.TotalRolls);
    }

    [Fact]
    public void Self_roll_increments_total_rolls()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 100, GiveawayRollKind.Standard));
        Assert.Equal(1, t.Service.TotalRolls);
    }

    [Fact]
    public void Self_participant_obeys_allowed_rolls_per_person()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []); // AllowedRollsPerPerson defaults to 1
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        var first = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 100, GiveawayRollKind.Standard));
        var second = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 900, GiveawayRollKind.Standard));

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(GiveawayRollRejectReason.OverPersonLimit, second.RejectReason);
        Assert.Equal(1, t.Service.TotalRolls);
    }

    [Fact]
    public void Self_participant_wins_under_highest()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("Cross World Alt", "Gilgamesh"), 200, GiveawayRollKind.Standard));
        t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 900, GiveawayRollKind.Standard));

        var leader = t.Service.Leaderboard.Single(x => x.IsLeader);
        Assert.Equal("Kei Joi", leader.Player.Name);
    }

    [Fact]
    public void Self_participant_wins_under_lowest()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Lowest Preset");
        t.Service.UpdatePreset(preset.Id, p => p with { WinnerMode = GiveawayWinnerMode.Lowest, DelayBetweenLinesSeconds = 1, GiveawayDurationSeconds = 60, StartBlock = GiveawayAnnouncementBlock.Empty() });
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("Cross World Alt", "Gilgamesh"), 800, GiveawayRollKind.Standard));
        t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 5, GiveawayRollKind.Standard));

        var leader = t.Service.Leaderboard.Single(x => x.IsLeader);
        Assert.Equal("Kei Joi", leader.Player.Name);
    }

    [Fact]
    public void Self_participant_wins_under_closest()
    {
        var t = Create();
        var preset = t.Service.CreatePreset("Closest Preset");
        t.Service.UpdatePreset(preset.Id, p => p with { WinnerMode = GiveawayWinnerMode.Closest, ClosestTargetNumber = 500, DelayBetweenLinesSeconds = 1, GiveawayDurationSeconds = 60, StartBlock = GiveawayAnnouncementBlock.Empty() });
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("Cross World Alt", "Gilgamesh"), 100, GiveawayRollKind.Standard));
        t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 505, GiveawayRollKind.Standard));

        var leader = t.Service.Leaderboard.Single(x => x.IsLeader);
        Assert.Equal("Kei Joi", leader.Player.Name);
    }

    [Fact]
    public void Self_participant_can_roll_during_closing()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 4, start: [], closing: ["Last chance!", "Closed!"]);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        Tick(t, 4); // Closing begins, first line sent, rolls remain open (Fix #1 behavior — must not regress)
        Assert.True(t.Service.IsAcceptingRolls);

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 42, GiveawayRollKind.Standard));
        Assert.True(outcome.Accepted);
    }

    [Fact]
    public void Host_and_cross_world_participant_remain_two_distinct_leaderboard_rows()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 1, duration: 60, start: []);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start();

        t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 300, GiveawayRollKind.Standard));
        t.Service.HandleRollObservation(new ObservedRandomRoll(new GuestIdentity("Poinsettia BloodlilyCuchulainn", "Zalera"), 700, GiveawayRollKind.Standard));

        Assert.Equal(2, t.Service.Leaderboard.Count);
        Assert.Contains(t.Service.Leaderboard, x => x.Player.Key == SelfIdentity.Key);
        Assert.Contains(t.Service.Leaderboard, x => x.Player.Name == "Poinsettia BloodlilyCuchulainn" && x.Player.HomeWorld == "Zalera");
    }

    [Fact]
    public void Self_roll_outside_the_acceptance_window_is_still_ignored()
    {
        var t = Create();
        var preset = MakePreset(t, delay: 5, duration: 60, start: ["Line 1", "Line 2"]);
        t.Service.SelectPreset(preset.Id);
        t.Service.Start(); // still Starting — Line 2 not sent yet, rolls not open

        var outcome = t.Service.HandleRollObservation(new ObservedRandomRoll(SelfIdentity, 100, GiveawayRollKind.Standard));
        Assert.False(outcome.Accepted);
        Assert.Equal(GiveawayRollRejectReason.GiveawayNotAcceptingRolls, outcome.RejectReason);
    }

    // =========================================================================================================
    // Test infrastructure
    // =========================================================================================================

    private sealed record Fixture(GiveawayService Service, VenueProfileService Profiles, InMemoryVenueStore Store, Guid VenueId, Clock Clock, SchedulerService Scheduler, ChatCommandService Chat, List<string> Sent);

    private static Fixture Create()
    {
        var clock = new Clock();
        var scheduler = new SchedulerService(clock);
        var sent = new List<string>();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { sent.Add(command); return true; }, TimeSpan.FromSeconds(1));
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var service = new GiveawayService(scheduler, chat, profiles, clock);
        var venueId = profiles.Current.Id;
        service.Load(venueId);
        return new Fixture(service, profiles, store, venueId, clock, scheduler, chat, sent);
    }

    private static ChatCommandService FakeChat(Clock clock) => new(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.FromSeconds(1));

    private static GiveawayPreset MakePreset(Fixture t, int delay, int duration, IReadOnlyList<string>? start = null, IReadOnlyList<string>? midpoint = null, IReadOnlyList<string>? closing = null)
    {
        var preset = t.Service.CreatePreset("Test Preset");
        t.Service.UpdatePreset(preset.Id, p => p with
        {
            DelayBetweenLinesSeconds = delay,
            GiveawayDurationSeconds = duration,
            StartBlock = new GiveawayAnnouncementBlock(start ?? Array.Empty<string>()),
            MidpointBlock = new GiveawayAnnouncementBlock(midpoint ?? Array.Empty<string>()),
            ClosingBlock = new GiveawayAnnouncementBlock(closing ?? Array.Empty<string>()),
        });
        return t.Service.Settings.Presets.Single(x => x.Id == preset.Id);
    }

    /// <summary>Drains exactly one already-enqueued chat command without advancing the clock — enqueuing (what
    /// <c>GiveawayService.Start</c> does synchronously) and actually dispatching (what <c>ChatCommandService.TickAsync</c>
    /// does, paced like every other module's chat traffic) are two different moments; a test asserting on
    /// <see cref="Fixture.Sent"/> right after <c>Start()</c> needs this, exactly like the real plugin's own
    /// Framework.Update tick would provide it.</summary>
    private static void DispatchImmediate(Fixture t) => t.Chat.TickAsync().GetAwaiter().GetResult();

    private static void Tick(Fixture t, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            t.Clock.Advance(1);
            t.Scheduler.Tick();
            t.Chat.TickAsync().GetAwaiter().GetResult();
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }
}
