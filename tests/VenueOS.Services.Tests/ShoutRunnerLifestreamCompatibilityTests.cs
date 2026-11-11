using VenueOS.Core;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>0.3.12 regression tests for the ShoutRunner travel/announcement audit
/// (<c>docs/SHOUTRUNNER_LIFESTREAM_COMPATIBILITY_AUDIT.md</c>). Every external dependency is a deterministic double:
/// a scripted Lifestream/game timeline is fed through the REAL <see cref="ShoutRunnerArrivalVerifier"/>, and the REAL
/// <see cref="ShoutRunnerService"/> runs against a fake automation layer, the real <see cref="ChatCommandService"/>
/// and a hand-moved clock. These tests prove the VenueOS-side logic only. They do not prove anything about real FFXIV
/// or Lifestream travel, which needs live QA.</summary>
public sealed class ShoutRunnerLifestreamCompatibilityTests
{
    private const string Uldah = "Ul'dah - Steps of Nald";
    private const string Gridania = "New Gridania";
    private const string Limsa = "Limsa Lominsa Lower Decks";
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    // =====================================================================================================
    // Arrival verifier (pure): the decision the real engine's same-DC and cross-DC wait loops now delegate to.
    // =====================================================================================================

    private static ShoutRunnerArrivalSample S(string? world, bool? busy, bool transitioning = false, bool prerequisite = true, bool loggedIn = true, bool player = true) =>
        new(loggedIn, player, transitioning, prerequisite, world, busy);

    /// <summary>Feeds (seconds, sample) pairs and returns the verdict after each one.</summary>
    private static List<ShoutRunnerArrivalVerifier.Verdict> Feed(ShoutRunnerArrivalVerifier v, params (double At, ShoutRunnerArrivalSample Sample)[] timeline) =>
        [.. timeline.Select(x => v.Observe(x.Sample, T0.AddSeconds(x.At)))];

    [Fact]
    public void Incident_timeline_Adamantoise_to_Cactuar_landing_on_Jenova_is_a_wrong_world_only_after_it_is_stable()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.0, S("Adamantoise", busy: true, prerequisite: false)),
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (20.0, S(null, busy: true, transitioning: true)),
            (40.0, S("Jenova", busy: true)),          // landed, Lifestream still busy: never a verdict
            (40.5, S("Jenova", busy: false)),         // first idle reading starts the window
            (41.5, S("Jenova", busy: false)),
            (43.0, S("Jenova", busy: false)),         // 2.5 s: still inside the window
            (43.5, S("Jenova", busy: false)));        // 3.0 s: confirmed

        Assert.All(verdicts.Take(7), x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts[^1]);
        Assert.False(v.EndedOnOriginWorld);
        Assert.Equal(["Adamantoise", "Jenova"], v.WorldsObserved);
        Assert.Equal(1, v.TransitionsObserved);
        Assert.StartsWith("Arrived at Jenova instead of Cactuar", v.DescribeWrongWorld("Jenova"));
    }

    [Fact]
    public void A_single_idle_reading_on_the_wrong_world_followed_by_Lifestream_retrying_never_faults_and_the_retry_arrival_succeeds()
    {
        // Lifestream retries a failed World Visit by default (Config.RetryWorldVisit = true). Before 0.3.12 the first
        // qualifying idle sample ended the transfer as "Arrived at Jenova instead of Cactuar".
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (40.0, S("Jenova", busy: false)),         // momentary idle gap
            (40.5, S("Jenova", busy: true)),          // Lifestream busy again (retrying)
            (41.0, S("Jenova", busy: true, transitioning: true)),
            (80.0, S("Cactuar", busy: false)),
            (80.5, S("Cactuar", busy: false)),
            (81.0, S("Cactuar", busy: false)));

        Assert.DoesNotContain(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts);
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Arrived, verdicts[^1]);
        Assert.Equal(["Adamantoise", "Jenova", "Cactuar"], v.WorldsObserved);
        Assert.Equal(2, v.TransitionsObserved);
    }

    [Fact]
    public void Halicarnassus_intermediate_city_settle_while_Lifestream_is_busy_is_still_never_a_verdict()
    {
        var v = new ShoutRunnerArrivalVerifier("Cuchulainn", "Halicarnassus");
        var verdicts = Feed(v,
            (0.5, S("Halicarnassus", busy: true, transitioning: true)),
            (10, S("Halicarnassus", busy: true)),
            (20, S("Halicarnassus", busy: true)),
            (60, S("Halicarnassus", busy: true)));
        Assert.All(verdicts, x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
    }

    [Fact]
    public void Arrival_on_the_target_requires_one_second_of_continuous_settled_observation()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (30.0, S("Cactuar", busy: false)),
            (30.5, S("Cactuar", busy: false)),
            (31.0, S("Cactuar", busy: false)));
        Assert.Equal([ShoutRunnerArrivalVerifier.Verdict.Continue, ShoutRunnerArrivalVerifier.Verdict.Continue, ShoutRunnerArrivalVerifier.Verdict.Continue, ShoutRunnerArrivalVerifier.Verdict.Arrived], verdicts);
    }

    [Fact]
    public void An_unreadable_Lifestream_busy_state_never_produces_either_verdict()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (30, S("Jenova", busy: null)), (40, S("Jenova", busy: null)), (50, S("Cactuar", busy: null)), (60, S("Cactuar", busy: null)));
        Assert.All(verdicts, x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
    }

    [Fact]
    public void No_verdict_before_travel_actually_began()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v, (0, S("Adamantoise", busy: false, prerequisite: false)), (10, S("Adamantoise", busy: false, prerequisite: false)));
        Assert.All(verdicts, x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
    }

    [Fact]
    public void Ending_back_on_the_origin_world_is_reported_as_an_incomplete_world_visit_not_as_a_different_world()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (10.0, S("Adamantoise", busy: false)),
            (13.0, S("Adamantoise", busy: false)));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts[^1]);
        Assert.True(v.EndedOnOriginWorld);
        Assert.Contains("ended back on Adamantoise", v.DescribeWrongWorld("Adamantoise"));
    }

    [Fact]
    public void A_new_transition_or_a_different_world_restarts_the_wrong_world_window()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (10.0, S("Jenova", busy: false)),
            (12.0, S("Jenova", busy: false, transitioning: true)),  // moving again
            (14.0, S("Faerie", busy: false)),                       // a different World: new window
            (16.5, S("Faerie", busy: false)),                       // 2.5 s on Faerie
            (17.0, S("Faerie", busy: false)));                      // 3.0 s on Faerie
        Assert.All(verdicts.Take(5), x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts[^1]);
        Assert.Equal("Faerie", v.SettledWorld);
    }

    [Fact]
    public void A_logged_out_sample_during_cross_data_center_travel_restarts_the_window()
    {
        var v = new ShoutRunnerArrivalVerifier("Balmung", "Adamantoise");
        var verdicts = Feed(v,
            (5, S(null, busy: true, loggedIn: false, player: false)),
            (60, S("Jenova", busy: false)),
            (61, S(null, busy: false, loggedIn: false, player: false)),
            (62, S("Jenova", busy: false)),
            (64, S("Jenova", busy: false)),
            (65, S("Jenova", busy: false)));
        Assert.All(verdicts.Take(5), x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts[^1]);
    }

    [Fact]
    public void Diagnostic_history_names_worlds_and_lifestream_state_but_nothing_else()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise");
        Feed(v, (0.5, S("Adamantoise", busy: true, transitioning: true)), (40, S("Jenova", busy: false)));
        var text = v.DescribeHistory();
        Assert.Contains("Adamantoise → Jenova", text);
        Assert.Contains("transitions: 1", text);
        Assert.Contains("Lifestream seen busy: yes", text);
        Assert.Contains("last Lifestream busy: idle", text);
    }

    // =====================================================================================================
    // Transfer classification (pure): Lifestream CanVisitSameDC / CanVisitCrossDC.
    // =====================================================================================================

    [Theory]
    [InlineData(true, false, ShoutRunnerCrossDataCenterCheck.SameDataCenter)]
    [InlineData(false, true, ShoutRunnerCrossDataCenterCheck.CrossDataCenter)]
    [InlineData(false, false, ShoutRunnerCrossDataCenterCheck.Unknown)]  // was silently SameDataCenter before 0.3.12
    [InlineData(true, true, ShoutRunnerCrossDataCenterCheck.Unknown)]    // contradictory: never guess
    [InlineData(null, true, ShoutRunnerCrossDataCenterCheck.Unknown)]
    [InlineData(true, null, ShoutRunnerCrossDataCenterCheck.Unknown)]
    [InlineData(false, null, ShoutRunnerCrossDataCenterCheck.Unknown)]
    [InlineData(null, null, ShoutRunnerCrossDataCenterCheck.Unknown)]
    public void Transfer_type_is_only_reported_when_Lifestream_positively_confirms_it(bool? sameDc, bool? crossDc, ShoutRunnerCrossDataCenterCheck expected) =>
        Assert.Equal(expected, ShoutRunnerTransferClassification.Classify(sameDc, crossDc));

    // =====================================================================================================
    // Resume plan (pure).
    // =====================================================================================================

    private static readonly IReadOnlyList<ShoutRunnerDestinationStep> Plan = [new("A", false), new("B", true), new("C", true)];

    [Fact] public void Resume_skips_completed_destinations_by_name_not_by_count() =>
        Assert.Equal(["A", "C"], ShoutRunnerResumePlan.RemainingSteps(Plan, ["B"]).Select(s => s.Destination));

    [Fact] public void Resume_after_the_first_destination_continues_with_the_rest() =>
        Assert.Equal(["B", "C"], ShoutRunnerResumePlan.RemainingSteps(Plan, ["A"]).Select(s => s.Destination));

    [Fact] public void Resume_with_everything_completed_has_nothing_left() =>
        Assert.Empty(ShoutRunnerResumePlan.RemainingSteps(Plan, ["C", "a", "B"]));

    [Fact] public void Every_resumed_step_teleports_even_one_planned_without_a_teleport() =>
        Assert.All(ShoutRunnerResumePlan.RemainingSteps(Plan, ["B"]), s => Assert.True(s.RequiresTeleport));

    [Fact] public void A_destination_listed_twice_is_only_skipped_as_often_as_it_was_completed() =>
        Assert.Equal(["B", "A"], ShoutRunnerResumePlan.RemainingSteps([new("A", true), new("B", true), new("A", true)], ["A"]).Select(s => s.Destination));

    // =====================================================================================================
    // Service-level scenarios (real ShoutRunnerService).
    // =====================================================================================================

    [Fact]
    public void Exact_reported_sequence_Adamantoise_completed_Cactuar_requested_Jenova_observed_stop_recover_resume_Cactuar_stop()
    {
        var rig = new Rig();
        rig.Automation.Travel = (target, _) => target == "Cactuar"
            ? SimulatedLifestreamTravel(rig.Automation, target, "Jenova")
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);

        var text = rig.Texts();
        Assert.Contains($"{Gridania} — SHOUT SENT.", text);
        Assert.Contains($"{Limsa} — SHOUT SENT.", text);
        Assert.Contains("Adamantoise — completed.", text);
        Assert.Contains("Cactuar — starting.", text);
        Assert.Contains(text, t => t.StartsWith("RUN 1 stopped — Arrived at Jenova instead of Cactuar", StringComparison.Ordinal));

        // No announcement at the wrong destination: every /shout so far was sent on Adamantoise.
        Assert.Equal(3, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal("Adamantoise", s.World));

        // The wrong-world fault keeps a checkpoint that names Cactuar, with nothing completed there.
        Assert.Equal("Cactuar", rig.Store.Current!.CurrentWorld);
        Assert.Empty(rig.Store.Current.CompletedDestinationsInCurrentWorld);
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("transfer Failed") && e.Message.Contains("Worlds observed: Adamantoise → Jenova") && e.Message.Contains("checkpoint: Aether/Cactuar"));

        // Recover → resumes Cactuar (never Adamantoise again, never the stale Jenova). The resumed travel is left in
        // flight so the operator's Stop lands mid-travel, as in the report.
        rig.Automation.TravelRequests.Clear();
        rig.Automation.Travel = (_, token) => new TaskCompletionSource<ShoutRunnerTransferOutcome>().Task.WaitAsync(token);
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig.Service.Resume());
        Assert.Contains("Recovered interrupted RUN 1.", rig.Texts());
        Assert.Contains("Cactuar — resuming.", rig.Texts());
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count > 0);
        Assert.Equal(["Cactuar"], rig.Automation.TravelRequests);

        Assert.Equal(ShoutRunnerState.TravelingWorld, rig.Service.State);
        rig.Service.Stop();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Contains("Stop requested.", rig.Texts());
        Assert.Null(rig.Store.Current);
        Assert.Equal(3, rig.Sent.Count);
    }

    [Fact]
    public void After_a_wrong_world_fault_Resume_completes_Cactuar_and_continues_to_the_next_world()
    {
        var rig = new Rig();
        var cactuarAttempts = 0;
        rig.Automation.Travel = (target, _) => target == "Cactuar" && cactuarAttempts++ == 0
            ? SimulatedLifestreamTravel(rig.Automation, target, "Jenova")
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);

        rig.Service.Resume();
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Faerie") == 3);

        Assert.Equal(3, rig.Sent.Count(s => s.World == "Adamantoise"));
        Assert.Equal(3, rig.Sent.Count(s => s.World == "Cactuar"));
        Assert.DoesNotContain(rig.Sent, s => s.World == "Jenova");   // never announced on the wrong World
    }

    [Fact]
    public void A_retry_inside_Lifestream_that_finally_reaches_Cactuar_is_a_normal_success()
    {
        var rig = new Rig();
        rig.Automation.Travel = (target, _) => target == "Cactuar"
            ? SimulatedLifestreamTravel(rig.Automation, target, "Jenova", retryReachesTarget: true)
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Cactuar") == 3);
        Assert.DoesNotContain(rig.Texts(), t => t.Contains("instead of"));
    }

    [Fact]
    public void Pre_shout_guard_withholds_the_announcement_when_the_character_is_moved_after_arrival()
    {
        var rig = new Rig();
        rig.Automation.Teleport = (destination, _) =>
        {
            rig.Automation.Teleports.Add(destination);
            if (destination == Gridania) rig.Automation.CurrentWorld = "Jenova"; // manual/external travel
            return Task.FromResult(ShoutRunnerTeleportOutcome.Success());
        };
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);

        Assert.Single(rig.Sent);                               // Ul'dah only
        Assert.DoesNotContain(rig.Sent, s => s.World == "Jenova");
        Assert.Contains(rig.Texts(), t => t.Contains($"Shout at {Gridania} withheld: the character is on Jenova, not Adamantoise."));
        Assert.Equal([Uldah], rig.Store.Current!.CompletedDestinationsInCurrentWorld);
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("pre-shout World check failed"));
    }

    [Fact]
    public void Pre_shout_guard_withholds_the_announcement_when_the_current_world_cannot_be_read()
    {
        var rig = new Rig();
        rig.Automation.CurrentWorldOverride = () => null;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);
        Assert.Empty(rig.Sent);
        Assert.Contains(rig.Texts(), t => t.Contains("current World could not be confirmed"));
    }

    [Fact]
    public void Resume_after_an_earlier_failed_destination_retries_it_and_never_repeats_a_sent_announcement()
    {
        var rig = new Rig();
        rig.Automation.Teleport = (destination, _) =>
        {
            rig.Automation.Teleports.Add(destination);
            return Task.FromResult(destination == Uldah ? ShoutRunnerTeleportOutcome.Failed("not attuned") : ShoutRunnerTeleportOutcome.Success());
        };
        rig.Service.Start();
        rig.PumpUntil(() => rig.Store.Current!.CompletedDestinationsInCurrentWorld.Contains(Gridania));
        Assert.Single(rig.Sent);

        // Crash: a brand-new service over the same store/profiles (no Stop on the old one).
        var rig2 = rig.Restart();
        var teleports = new List<string>();
        rig2.Automation.Teleport = (destination, _) => { teleports.Add(destination); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig2.Service.Resume());
        rig2.PumpUntil(() => rig2.Texts().Contains("Adamantoise — completed."));

        Assert.Equal([Uldah, Limsa], teleports);              // Ul'dah retried, Gridania NOT repeated
        Assert.Equal(2, rig2.Sent.Count);
    }

    [Fact]
    public void A_late_travel_completion_after_Stop_cannot_advance_the_route()
    {
        var rig = new Rig();
        var pending = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        rig.Automation.Travel = (_, _) => pending.Task;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count == 1);
        rig.Service.Stop();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        rig.Automation.CurrentWorld = "Adamantoise";
        pending.SetResult(ShoutRunnerTransferOutcome.Success());   // stale completion arrives late
        for (var i = 0; i < 50; i++) { rig.TickOnce(); }

        Assert.Equal(ShoutRunnerState.Stopped, rig.Service.State);
        Assert.Empty(rig.Sent);
        Assert.Empty(rig.Automation.Teleports);
        Assert.Equal(0, rig.Automation.LocateCount);
    }

    [Fact]
    public void A_stale_travel_task_from_a_stopped_run_cannot_affect_the_next_run()
    {
        var rig = new Rig();
        var stale = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        rig.Automation.Travel = (_, _) => stale.Task;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count == 1);
        rig.Service.HardStop();

        var fresh = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        rig.Automation.Travel = (_, _) => fresh.Task;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count == 2);
        stale.SetResult(ShoutRunnerTransferOutcome.Failed("Arrived at Jenova instead of Adamantoise."));
        for (var i = 0; i < 20; i++) rig.TickOnce();

        Assert.Equal(ShoutRunnerState.TravelingWorld, rig.Service.State);   // still waiting on ITS OWN travel
        Assert.DoesNotContain(rig.Texts(), t => t.Contains("instead of"));
    }

    [Fact]
    public void Each_travel_completion_is_consumed_exactly_once()
    {
        var rig = new Rig();
        rig.Service.Start();
        rig.PumpUntil(() => rig.Texts().Contains("Cactuar — completed."));
        Assert.Equal(2, rig.Automation.LocateCount);                   // one locate per World entered
        Assert.Equal(["Adamantoise", "Cactuar"], rig.Automation.TravelRequests);
    }

    [Fact]
    public void Lifestream_not_accepting_the_request_faults_with_a_clear_reason_and_sends_nothing()
    {
        var rig = new Rig();
        rig.Automation.Travel = (target, _) => Task.FromResult(ShoutRunnerTransferOutcome.Failed($"Lifestream did not start travel to {target} within 10s (request not accepted; check Lifestream's on-screen notifications).", "transfer #1 (same-DC); no Lifestream busy state and no zone transition after /li"));
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);
        Assert.Empty(rig.Sent);
        Assert.Contains(rig.Texts(), t => t.Contains("request not accepted"));
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("no Lifestream busy state"));
    }

    [Fact]
    public void An_unclassifiable_world_is_skipped_without_any_travel_request_and_the_route_continues()
    {
        var rig = new Rig();
        rig.Automation.Classify = world => world == "Cactuar" ? ShoutRunnerCrossDataCenterCheck.Unknown : ShoutRunnerCrossDataCenterCheck.SameDataCenter;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Faerie") == 3);
        Assert.DoesNotContain("Cactuar", rig.Automation.TravelRequests);
        Assert.DoesNotContain(rig.Sent, s => s.World == "Cactuar");
        Assert.Contains(rig.Texts(), t => t.StartsWith("Cactuar — SKIPPED: could not determine", StringComparison.Ordinal));
    }

    [Fact]
    public void Congestion_two_strike_skip_is_preserved_and_the_route_moves_to_the_next_world()
    {
        var rig = new Rig();
        rig.Automation.Travel = (target, _) => target == "Cactuar"
            ? Task.FromResult(ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.", "transfer #2"))
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Faerie") == 3);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Cactuar — SKIPPED: congested", StringComparison.Ordinal));
        Assert.Equal(1, rig.Automation.TravelRequests.Count(w => w == "Cactuar"));   // no uncontrolled retry
    }

    [Fact]
    public void Stop_while_waiting_for_Line_2_never_sends_Line_2_and_never_repeats_Line_1()
    {
        var rig = new Rig();
        rig.Service.UpdateShoutMessageLine2("Line two");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.WaitingForLine2, advanceLine2: false);
        rig.Service.Stop();
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Equal(["/shout Test shout"], rig.Sent.Select(s => s.Command));
    }

    [Fact]
    public void Two_line_shout_still_waits_the_full_2_seconds_after_the_world_check()
    {
        var rig = new Rig();
        rig.Service.UpdateShoutMessageLine2("Line two");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Sent.Count == 1, advanceLine2: false);
        var line1At = rig.Clock.UtcNow;
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        for (var i = 0; i < 5; i++) rig.TickOnce();
        Assert.Single(rig.Sent);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 3; i++) rig.TickOnce();
        Assert.Equal(["/shout Test shout", "/shout Line two"], rig.Sent.Select(s => s.Command));
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Clock.UtcNow - line1At);
    }

    [Fact]
    public void Venue_change_during_travel_stops_everything_and_nothing_is_sent_afterwards()
    {
        var rig = new Rig();
        var pending = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        rig.Automation.Travel = (_, _) => pending.Task;
        rig.Service.Start();
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count == 1);
        var other = rig.Profiles.Create("Other venue");
        rig.Service.Load(other.Id);
        rig.Automation.CurrentWorld = "Adamantoise";
        pending.SetResult(ShoutRunnerTransferOutcome.Success());
        for (var i = 0; i < 20; i++) rig.TickOnce();
        Assert.Equal(ShoutRunnerState.Stopped, rig.Service.State);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void Plugin_reload_during_recovery_offers_the_same_checkpoint_and_resumes_the_unfinished_world()
    {
        var rig = new Rig();
        rig.Automation.Travel = (target, _) => target == "Cactuar"
            ? SimulatedLifestreamTravel(rig.Automation, target, "Jenova")
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);

        var rig2 = rig.Restart();                                   // /xlreload
        Assert.True(rig2.Service.CanResume);
        Assert.Equal("Cactuar", rig2.Service.RecoveredSummary!.World);
        rig2.Service.Resume();
        rig2.PumpUntil(() => rig2.Sent.Count == 3);
        Assert.All(rig2.Sent, s => Assert.Equal("Cactuar", s.World));
        Assert.Equal("Cactuar", rig2.Automation.TravelRequests[0]);
    }

    [Fact]
    public void A_new_run_after_Stop_starts_clean_with_no_state_from_the_previous_run()
    {
        var rig = new Rig();
        rig.Service.Start();
        rig.PumpUntil(() => rig.Texts().Contains("Cactuar — starting."));
        var firstRunId = rig.Store.Current!.RunId;
        rig.Service.Stop();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        rig.Automation.TravelRequests.Clear();
        rig.Service.Start();
        Assert.Equal("Adamantoise", rig.Store.Current!.CurrentWorld);
        Assert.Empty(rig.Store.Current.CompletedDestinationsInCurrentWorld);
        Assert.NotEqual(firstRunId, rig.Store.Current.RunId);
        rig.PumpUntil(() => rig.Automation.TravelRequests.Count == 1);
        Assert.Equal(["Adamantoise"], rig.Automation.TravelRequests);
    }

    [Fact]
    public void Normal_full_Aether_route_visits_every_world_in_order_and_attributes_every_shout_to_the_world_it_was_sent_on()
    {
        var rig = new Rig(repeat: false);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        var worlds = ShoutRunnerCatalog.WorldsIn("Aether");
        Assert.Equal(worlds, rig.Automation.TravelRequests);
        Assert.Equal(worlds.Count * 3, rig.Sent.Count);
        foreach (var world in worlds) Assert.Equal(3, rig.Sent.Count(s => s.World == world));
        Assert.Equal(worlds, rig.Sent.Select(s => s.World).Distinct());
        Assert.Contains("RUN 1 complete.", rig.Texts());
        Assert.Null(rig.Store.Current);
    }

    // =====================================================================================================
    // Harness
    // =====================================================================================================

    /// <summary>Drives the REAL <see cref="ShoutRunnerArrivalVerifier"/> through a scripted Lifestream timeline:
    /// a transition, landing on <paramref name="landsOn"/> while busy, then idle there (or, with
    /// <paramref name="retryReachesTarget"/>, Lifestream's own retry carrying on to the target). Moves the fake
    /// character exactly as the timeline does and maps the verdict to an outcome the way the real engine does.</summary>
    private static Task<ShoutRunnerTransferOutcome> SimulatedLifestreamTravel(Automation automation, string target, string landsOn, bool retryReachesTarget = false)
    {
        var origin = automation.CurrentWorld;
        var verifier = new ShoutRunnerArrivalVerifier(target, origin);
        var timeline = new List<(double, ShoutRunnerArrivalSample)>
        {
            (0.5, S(origin, busy: true, transitioning: true)),
            (30.0, S(landsOn, busy: true)),
        };
        if (retryReachesTarget)
            timeline.AddRange([(30.5, S(landsOn, busy: false)), (31.0, S(landsOn, busy: true, transitioning: true)), (60.0, S(target, busy: false)), (60.5, S(target, busy: false)), (61.0, S(target, busy: false))]);
        else
            timeline.AddRange(Enumerable.Range(0, 10).Select(i => (30.5 + i * 0.5, S(landsOn, busy: false))));

        foreach (var (at, sample) in timeline)
        {
            if (sample.CurrentWorld is not null) automation.CurrentWorld = sample.CurrentWorld;
            var verdict = verifier.Observe(sample, T0.AddSeconds(at));
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.Arrived) return Task.FromResult(ShoutRunnerTransferOutcome.Success(verifier.DescribeHistory()));
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.WrongWorld)
                return Task.FromResult(ShoutRunnerTransferOutcome.Failed(verifier.DescribeWrongWorld(sample.CurrentWorld), $"transfer (same-DC); requested {target}; {verifier.DescribeHistory()}"));
        }

        return Task.FromResult(ShoutRunnerTransferOutcome.Failed($"Same-Data-Center transfer to {target} timed out after 3 minutes."));
    }

    private sealed class Automation : IShoutRunnerAutomation
    {
        public string? CurrentWorld = "Siren";
        public Func<string?>? CurrentWorldOverride;
        public readonly List<string> TravelRequests = [];
        public readonly List<string> Teleports = [];
        public int LocateCount, AbortCount;
        public Func<string, ShoutRunnerCrossDataCenterCheck> Classify = _ => ShoutRunnerCrossDataCenterCheck.SameDataCenter;
        public Func<string, CancellationToken, Task<ShoutRunnerTransferOutcome>> Travel;
        public Func<string, CancellationToken, Task<ShoutRunnerTeleportOutcome>> Teleport;

        public Automation()
        {
            Travel = (target, _) => ArriveAt(target);
            Teleport = (destination, _) => { Teleports.Add(destination); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };
        }

        public Task<ShoutRunnerTransferOutcome> ArriveAt(string world) { CurrentWorld = world; return Task.FromResult(ShoutRunnerTransferOutcome.Success()); }

        public void ResetForVenue() { }
        public void Abort() => AbortCount++;
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => Task.FromResult(Classify(targetWorld));
        public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) { TravelRequests.Add(targetWorld); return Travel(targetWorld, token); }
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) { LocateCount++; return Task.FromResult<string?>(null); }
        public Task<string?> TryGetCurrentWorldAsync(CancellationToken token) => Task.FromResult(CurrentWorldOverride is null ? CurrentWorld : CurrentWorldOverride());
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => Teleport(destinationName, token);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan span) => UtcNow += span;
    }

    private sealed class Store : IShoutRunnerRecoveryStore
    {
        public ShoutRunnerRecoveryJournal? Current { get; private set; }
        public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return Current; }
        public void Save(ShoutRunnerRecoveryJournal journal) => Current = journal;
        public void Delete() => Current = null;
    }

    private sealed class Rig
    {
        public readonly VenueProfileService Profiles;
        public readonly DiagnosticsService Diagnostics;
        public readonly Store Store;
        public readonly Clock Clock;
        public readonly Automation Automation = new();
        public readonly ChatCommandService Chat;
        public readonly ShoutRunnerService Service;
        public readonly List<(string? World, string Command)> Sent = [];

        public Rig(bool repeat = true) : this(null, repeat) { }

        private Rig(Rig? previous, bool repeat)
        {
            if (previous is null)
            {
                var host = new ModuleHost();
                Profiles = new VenueProfileService(new InMemoryVenueStore(), host);
                Clock = new Clock();
                Diagnostics = new DiagnosticsService(host, Profiles, Clock);
                Store = new Store();
            }
            else
            {
                (Profiles, Clock, Diagnostics, Store) = (previous.Profiles, previous.Clock, previous.Diagnostics, previous.Store);
            }

            Chat = new ChatCommandService(Clock, new InlineFrameworkDispatcher(), command => { Sent.Add((Automation.CurrentWorld, command)); return true; }, TimeSpan.Zero);
            Service = new ShoutRunnerService(Automation, Chat, Profiles, Diagnostics, Clock, Store);
            Service.Load(Profiles.Current.Id);
            if (previous is not null) return;

            Service.UpdateShoutMessage("Test shout");
            while (Service.Settings.Destinations.Count > 0) Service.RemoveDestinationAt(0);
            foreach (var d in new[] { Uldah, Gridania, Limsa }) Service.AddDestination(d);
            Service.SetDataCenterSelected("Aether", true);
            Service.SetRepeatEnabled(repeat);
        }

        /// <summary>A plugin restart: same venue store, recovery file, clock and diagnostics; everything else new.</summary>
        public Rig Restart() => new(this, repeat: true);

        public List<string> Texts() => [.. Service.TerminalEvents.Select(e => e.Text)];

        public void TickOnce()
        {
            Chat.TickAsync().GetAwaiter().GetResult();
            Service.Tick(Clock.UtcNow);
        }

        public void PumpUntil(Func<bool> condition, bool advanceLine2 = true, int maxSteps = 5000)
        {
            for (var i = 0; i < maxSteps; i++)
            {
                if (condition()) return;
                if (Service.State == ShoutRunnerState.WaitingActionDelay) Clock.Advance(TimeSpan.FromSeconds(Service.Settings.ClampedDelaySeconds() + 1));
                else if (advanceLine2 && Service.State == ShoutRunnerState.WaitingForLine2) Clock.Advance(ShoutRunnerShoutLines.InterLineDelay);
                TickOnce();
            }

            Assert.True(condition(), $"Condition never met (state {Service.State}). Terminal: {string.Join(" | ", Texts())}");
        }
    }
}
