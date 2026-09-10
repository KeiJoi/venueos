using VenueOS.Core;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class ShoutRunnerCatalogTests
{
    [Fact] public void Canonical_data_center_order_is_fixed_alphabetical()
    {
        Assert.Equal(["Aether", "Crystal", "Dynamis", "Primal"], ShoutRunnerCatalog.CanonicalDataCenterOrder);
    }

    [Fact] public void Worlds_within_each_data_center_are_alphabetical()
    {
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
        {
            var worlds = ShoutRunnerCatalog.WorldsIn(dc);
            Assert.Equal(worlds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), worlds);
        }
    }

    [Fact] public void Every_world_maps_back_to_exactly_one_data_center()
    {
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
            foreach (var world in ShoutRunnerCatalog.WorldsIn(dc))
                Assert.Equal(dc, ShoutRunnerCatalog.DataCenterFor(world));
    }

    [Fact] public void All_32_expected_na_worlds_are_represented()
    {
        var total = ShoutRunnerCatalog.CanonicalDataCenterOrder.Sum(dc => ShoutRunnerCatalog.WorldsIn(dc).Count);
        Assert.Equal(32, total);
    }

    [Fact] public void Selected_data_center_filtering_preserves_canonical_order_regardless_of_input_order()
    {
        var ordered = ShoutRunnerCatalog.OrderSelectedDataCenters(["Primal", "Aether"]);
        Assert.Equal(["Aether", "Primal"], ordered);
    }
}

public sealed class ShoutRunnerRoutePlannerTests
{
    [Fact] public void Single_destination_always_produces_one_step_with_no_teleport_when_already_there()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["Ul'dah"], "Ul'dah", ref direction);
        Assert.Single(steps);
        Assert.False(steps[0].RequiresTeleport);
    }

    [Fact] public void Two_destination_ping_pong_reverses_between_worlds()
    {
        string[] destinations = ["A", "B"];
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;

        var world1 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, null, ref direction);
        Assert.Equal(["A", "B"], world1.Select(x => x.Destination));

        // World 2: location detected at B (where World 1 ended) — direction is derived purely from that position.
        var world2 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, "B", ref direction);
        Assert.Equal(["B", "A"], world2.Select(x => x.Destination));
    }

    [Fact] public void Three_destination_ping_pong_matches_the_documented_worked_example()
    {
        string[] destinations = ["Ul'dah", "Gridania", "Limsa"];
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;

        var world1 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, "Ul'dah", ref direction);
        Assert.Equal(["Ul'dah", "Gridania", "Limsa"], world1.Select(x => x.Destination));

        var world2 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, "Limsa", ref direction);
        Assert.Equal(["Limsa", "Gridania", "Ul'dah"], world2.Select(x => x.Destination));
    }

    [Fact] public void Four_destination_ping_pong_visits_every_destination_exactly_once_in_each_direction()
    {
        string[] destinations = ["A", "B", "C", "D"];
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;

        var world1 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, null, ref direction);
        Assert.Equal(["A", "B", "C", "D"], world1.Select(x => x.Destination));

        var world2 = ShoutRunnerRoutePlanner.PlanWorldRoute(destinations, "D", ref direction);
        Assert.Equal(["D", "C", "B", "A"], world2.Select(x => x.Destination));
    }

    [Fact] public void Current_location_at_first_endpoint_travels_forward_with_no_initial_teleport()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["Ul'dah", "Gridania", "Limsa"], "Ul'dah", ref direction);
        Assert.Equal(["Ul'dah", "Gridania", "Limsa"], steps.Select(x => x.Destination));
        Assert.False(steps[0].RequiresTeleport);
        Assert.True(steps[1].RequiresTeleport);
        Assert.True(steps[2].RequiresTeleport);
    }

    [Fact] public void Current_location_at_last_endpoint_travels_backward_with_no_initial_teleport()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["Ul'dah", "Gridania", "Limsa"], "Limsa", ref direction);
        Assert.Equal(["Limsa", "Gridania", "Ul'dah"], steps.Select(x => x.Destination));
        Assert.False(steps[0].RequiresTeleport);
    }

    [Fact] public void Current_location_in_the_middle_heads_toward_the_end_of_configured_order_first()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["Ul'dah", "Gridania", "Limsa"], "Gridania", ref direction);
        Assert.Equal(["Gridania", "Limsa", "Ul'dah"], steps.Select(x => x.Destination));
        Assert.False(steps[0].RequiresTeleport);
    }

    [Fact] public void Unknown_current_location_falls_back_to_a_full_pass_in_the_remembered_direction()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Backward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["A", "B", "C"], "somewhere unrelated", ref direction);
        Assert.Equal(["C", "B", "A"], steps.Select(x => x.Destination));
        // An unmatched location is never treated as "already there" for any stop, including the first.
        Assert.All(steps, s => Assert.True(s.RequiresTeleport));
    }

    [Fact] public void Fallback_direction_flips_after_every_world_so_the_ping_pong_stays_continuous()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        ShoutRunnerRoutePlanner.PlanWorldRoute(["A", "B"], null, ref direction);
        Assert.Equal(ShoutRunnerRoutePlanner.TraversalDirection.Backward, direction);
        ShoutRunnerRoutePlanner.PlanWorldRoute(["A", "B"], null, ref direction);
        Assert.Equal(ShoutRunnerRoutePlanner.TraversalDirection.Forward, direction);
    }

    [Fact] public void Only_the_first_stop_can_ever_skip_its_teleport()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute(["A", "B", "C"], "A", ref direction);
        Assert.False(steps[0].RequiresTeleport);
        Assert.True(steps.Skip(1).All(s => s.RequiresTeleport));
    }
}

public sealed class ShoutRunnerTerminalFormatterTests
{
    [Fact] public void Empty_terminal_exports_safely()
    {
        Assert.Equal(string.Empty, ShoutRunnerTerminalFormatter.ToPlainText([]));
    }

    [Fact] public void Multiple_runs_are_both_included()
    {
        var events = new[]
        {
            Event(1, ShoutRunnerEventSeverity.InProgress, "RUN 1 started."),
            Event(2, ShoutRunnerEventSeverity.InProgress, "RUN 2 started."),
        };
        var text = ShoutRunnerTerminalFormatter.ToPlainText(events);
        Assert.Contains("RUN 1 — RUN 1 started.", text);
        Assert.Contains("RUN 2 — RUN 2 started.", text);
    }

    [Fact] public void Data_center_hierarchy_is_indented_two_spaces()
    {
        var text = ShoutRunnerTerminalFormatter.ToPlainText([Event(1, ShoutRunnerEventSeverity.InProgress, "Aether — starting.", dataCenter: "Aether")]);
        Assert.StartsWith("  [", text);
    }

    [Fact] public void World_hierarchy_is_indented_four_spaces()
    {
        var text = ShoutRunnerTerminalFormatter.ToPlainText([Event(1, ShoutRunnerEventSeverity.InProgress, "Adamantoise — starting.", dataCenter: "Aether", world: "Adamantoise")]);
        Assert.StartsWith("    [", text);
    }

    [Fact] public void Destination_hierarchy_is_indented_six_spaces()
    {
        var text = ShoutRunnerTerminalFormatter.ToPlainText([Event(1, ShoutRunnerEventSeverity.InProgress, "Ul'dah — starting.", dataCenter: "Aether", world: "Adamantoise", destination: "Ul'dah")]);
        Assert.StartsWith("      [", text);
    }

    [Fact] public void Every_line_carries_its_own_timestamp()
    {
        var at = new DateTimeOffset(2026, 1, 1, 20, 46, 52, TimeSpan.Zero);
        var text = ShoutRunnerTerminalFormatter.ToPlainText([new ShoutRunnerTerminalEvent(at, 1, ShoutRunnerEventSeverity.InProgress, "RUN started.")]);
        Assert.Contains(at.ToLocalTime().ToString("h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture), text);
    }

    [Fact] public void Success_events_are_represented()
    {
        var text = ShoutRunnerTerminalFormatter.ToPlainText([Event(1, ShoutRunnerEventSeverity.Success, "Ul'dah — SHOUT SENT.", destination: "Ul'dah")]);
        Assert.Contains("SHOUT SENT", text);
    }

    [Fact] public void Skipped_and_failure_events_are_represented()
    {
        var text = ShoutRunnerTerminalFormatter.ToPlainText([Event(1, ShoutRunnerEventSeverity.Failure, "Cactuar — SKIPPED: congested.", world: "Cactuar")]);
        Assert.Contains("SKIPPED", text);
    }

    [Fact] public void Stop_events_are_represented()
    {
        var events = new[]
        {
            Event(1, ShoutRunnerEventSeverity.Warning, "Stop requested."),
            Event(1, ShoutRunnerEventSeverity.Success, "Stopped."),
        };
        var text = ShoutRunnerTerminalFormatter.ToPlainText(events);
        Assert.Contains("Stop requested.", text);
        Assert.Contains("Stopped.", text);
    }

    [Fact] public void A_blank_line_separates_each_new_data_center_world_or_destination_context()
    {
        var events = new[]
        {
            Event(1, ShoutRunnerEventSeverity.InProgress, "RUN 1 started."),
            Event(1, ShoutRunnerEventSeverity.InProgress, "Crystal — starting.", dataCenter: "Crystal"),
            Event(1, ShoutRunnerEventSeverity.InProgress, "Balmung — starting.", dataCenter: "Crystal", world: "Balmung"),
            Event(1, ShoutRunnerEventSeverity.InProgress, "Ul'dah — starting.", dataCenter: "Crystal", world: "Balmung", destination: "Ul'dah"),
            Event(1, ShoutRunnerEventSeverity.Success, "Ul'dah — SHOUT SENT.", dataCenter: "Crystal", world: "Balmung", destination: "Ul'dah"),
        };
        var lines = ShoutRunnerTerminalFormatter.ToPlainText(events).Replace("\r\n", "\n").Split('\n');
        // RUN-started, blank, Crystal, blank, Balmung, blank, Ul'dah-starting, Ul'dah-SHOUT SENT (no blank — same context), trailing empty.
        Assert.Equal("", lines[1]);
        Assert.Equal("", lines[3]);
        Assert.Equal("", lines[5]);
        Assert.NotEqual("", lines[6]); // "Ul'dah — starting."
        Assert.NotEqual("", lines[7]); // "Ul'dah — SHOUT SENT." — no blank line inserted between these two (same context)
    }

    [Fact] public void Exporting_never_mutates_or_removes_retained_terminal_events()
    {
        var terminal = new ShoutRunnerTerminal();
        terminal.Add(Event(1, ShoutRunnerEventSeverity.InProgress, "RUN 1 started."));
        terminal.Add(Event(1, ShoutRunnerEventSeverity.Success, "Ul'dah — SHOUT SENT.", destination: "Ul'dah"));

        var before = terminal.Events;
        ShoutRunnerTerminalFormatter.ToPlainText(terminal.Events);
        var after = terminal.Events;

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before, after);
    }

    [Fact] public void Formatting_a_terminal_at_full_bounded_capacity_does_not_alter_its_retention()
    {
        var terminal = new ShoutRunnerTerminal();
        for (var i = 0; i < ShoutRunnerTerminal.MaxEntries + 10; i++)
            terminal.Add(Event(1, ShoutRunnerEventSeverity.InProgress, $"event {i}"));

        _ = ShoutRunnerTerminalFormatter.ToPlainText(terminal.Events);

        Assert.Equal(ShoutRunnerTerminal.MaxEntries, terminal.Events.Count);
    }

    private static ShoutRunnerTerminalEvent Event(int runNumber, ShoutRunnerEventSeverity severity, string text, string? dataCenter = null, string? world = null, string? destination = null) =>
        new(DateTimeOffset.UnixEpoch, runNumber, severity, text, dataCenter, world, destination);
}

public sealed class ShoutRunnerServiceTests
{
    [Fact] public void Default_settings_use_a_one_hour_repeat_interval()
    {
        Assert.Equal(TimeSpan.FromHours(1), ShoutRunnerSettings.Default().GetInterval());
    }

    [Fact] public void Zero_interval_is_clamped_to_the_one_minute_minimum()
    {
        var (service, _, _, _, _) = New();
        service.SetInterval(0, 0, 0);
        Assert.Equal(TimeSpan.FromSeconds(ShoutRunnerSettings.MinimumIntervalSeconds), service.Settings.GetInterval());
    }

    [Fact] public void Start_refuses_an_empty_shout_message()
    {
        var (service, _, _, _, _) = New();
        service.SetDataCenterSelected("Aether", true);
        Assert.Equal(ShoutRunnerStartResult.ShoutMessageRequired, service.Start());
    }

    [Fact] public void Start_refuses_with_no_data_center_selected()
    {
        var (service, _, _, _, _) = New();
        service.UpdateShoutMessage("Hello!");
        Assert.Equal(ShoutRunnerStartResult.NoDataCenterSelected, service.Start());
    }

    [Fact] public void Start_refuses_with_no_destination_configured()
    {
        var (service, _, _, _, _) = New();
        service.UpdateShoutMessage("Hello!");
        service.SetDataCenterSelected("Aether", true);
        while (service.Settings.Destinations.Count > 0) service.RemoveDestinationAt(0);
        Assert.Equal(ShoutRunnerStartResult.NoDestinationConfigured, service.Start());
    }

    [Fact] public void A_full_run_across_one_world_shouts_at_every_configured_destination()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A", "B"], dataCenters: ["Aether"]);
        automation.OnTravel = (world, _, _) => world == "Adamantoise" ? Done(ShoutRunnerTransferOutcome.Success()) : Done(ShoutRunnerTransferOutcome.WorldCongested("skip"));

        Assert.Equal(ShoutRunnerStartResult.Started, service.Start());
        PumpUntilSettled(service, clock, chat);

        Assert.Contains(service.TerminalEvents, e => e.World == "Adamantoise" && e.Destination == "A" && e.Text.Contains("SHOUT SENT"));
        Assert.Contains(service.TerminalEvents, e => e.World == "Adamantoise" && e.Destination == "B" && e.Text.Contains("SHOUT SENT"));
    }

    [Fact] public void World_congested_twice_skips_that_world_and_continues_with_the_next_world_same_run()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        automation.OnTravel = (world, _, _) => world == "Adamantoise"
            ? Done(ShoutRunnerTransferOutcome.WorldCongested("congested twice"))
            : Done(ShoutRunnerTransferOutcome.Success());

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Contains(service.TerminalEvents, e => e.World == "Adamantoise" && e.Text.Contains("SKIPPED") && e.Text.Contains("congested"));
        Assert.Contains(service.TerminalEvents, e => e.World == "Cactuar" && e.Destination == "A");
    }

    [Fact] public void Skipped_world_is_not_retried_within_the_same_run_but_is_eligible_again_next_run()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var adamantoiseAttempts = 0;
        automation.OnTravel = (world, _, _) =>
        {
            if (world != "Adamantoise") return Done(ShoutRunnerTransferOutcome.Success());
            adamantoiseAttempts++;
            return Done(ShoutRunnerTransferOutcome.WorldCongested("congested"));
        };
        service.SetRepeatEnabled(true);
        service.SetInterval(0, 1, 0);

        service.Start();
        PumpUntilSettled(service, clock, chat); // settles at WaitingRepeat after RUN 1
        Assert.Equal(1, adamantoiseAttempts);

        AdvanceToNextRun(service, clock);
        PumpUntilSettled(service, clock, chat);
        Assert.Equal(2, adamantoiseAttempts); // eligible again in RUN 2
    }

    [Fact] public void Data_center_unavailable_skips_remaining_worlds_in_that_data_center_only_after_recovery_succeeds()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var visited = new List<string>();
        automation.OnTravel = (world, _, _) =>
        {
            visited.Add(world);
            return world == "Adamantoise" ? Done(ShoutRunnerTransferOutcome.DataCenterUnavailable("recovered")) : Done(ShoutRunnerTransferOutcome.Success());
        };

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(["Adamantoise"], visited); // no further worlds in Aether attempted
        Assert.Contains(service.TerminalEvents, e => e.DataCenter == "Aether" && e.Text.Contains("SKIPPED") && e.Text.Contains("unavailable"));
        Assert.NotEqual(ShoutRunnerState.Faulted, service.State);
    }

    [Fact] public void Failed_recovery_ends_the_entire_automation_rather_than_continuing_to_the_next_data_center()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether", "Crystal"]);
        automation.OnEnsureReady = _ => Done(ShoutRunnerReadinessOutcome.RecoveryFailed("could not recover"));

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Faulted, service.State);
        Assert.DoesNotContain(service.TerminalEvents, e => e.DataCenter == "Crystal");
    }

    // ----- IsActive (0.3.0 session-presentation gate signal) -----

    [Fact] public void A_freshly_loaded_service_is_not_active()
    {
        var (service, _, _, _, _) = New();
        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.False(service.IsActive);
    }

    [Fact] public void Starting_a_run_immediately_marks_the_service_active_before_any_Tick()
    {
        var (service, _, _, _, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        Assert.NotEqual(ShoutRunnerState.Stopped, service.State);
        Assert.NotEqual(ShoutRunnerState.Faulted, service.State);
        Assert.True(service.IsActive);
    }

    [Fact] public void A_run_that_settles_into_Faulted_is_not_active()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether", "Crystal"]);
        automation.OnEnsureReady = _ => Done(ShoutRunnerReadinessOutcome.RecoveryFailed("could not recover"));

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Faulted, service.State);
        Assert.False(service.IsActive); // the 0.3.0 session-gate ShoutRunner exception must not stay open forever after a fault
    }

    [Fact] public void Loading_a_venue_forces_Stopped_and_therefore_not_active_even_mid_run()
    {
        // Load() calls HardStop() first (see ShoutRunnerService.Load) — this is the exact mechanism
        // SessionPresentationGateService's doc comment relies on to guarantee IsActive is false at the very first
        // venue activation of a plugin session, before any character login is even possible.
        var (service, _, _, _, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        Assert.True(service.IsActive);

        service.Load(Guid.NewGuid());

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.False(service.IsActive);
    }

    [Fact] public void Teleport_failure_marks_the_destination_skipped_but_the_world_continues()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A", "B"], dataCenters: ["Aether"]);
        automation.OnTeleport = (destination, _) => destination == "A" ? Done(ShoutRunnerTeleportOutcome.Failed("no aetheryte")) : Done(ShoutRunnerTeleportOutcome.Success());

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Contains(service.TerminalEvents, e => e.Destination == "A" && e.Text.Contains("SKIPPED") && e.Text.Contains("teleport failed"));
        Assert.Contains(service.TerminalEvents, e => e.Destination == "B" && e.Text.Contains("SHOUT SENT"));
        Assert.Contains(service.TerminalEvents, e => e.World == "Adamantoise" && e.Text.Contains("completed with errors"));
    }

    [Fact] public void Unclassifiable_transfer_is_treated_as_an_explicit_failure_never_a_silent_same_data_center_guess()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        automation.OnClassify = (_, _) => Done(ShoutRunnerCrossDataCenterCheck.Unknown);

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Contains(service.TerminalEvents, e => e.Text.Contains("SKIPPED") && e.Text.Contains("could not determine"));
    }

    // ----- same-Data-Center vs cross-Data-Center routing (live-verified bug regression) -----
    // See ShoutRunnerSameDataCenterArrivalTests.cs for the actual arrival-decision bug/fix — these tests only cover
    // the ORCHESTRATION-level guarantee that a same-Data-Center classification never triggers cross-Data-Center
    // travel (and vice versa); the automation itself (where the bug lived) has no fake standing in for this specific
    // internal polling logic, since the fake automation's OnTravel bypasses that logic entirely.

    [Fact] public void Same_data_center_classification_never_triggers_cross_data_center_travel()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var crossDataCenterFlags = new List<bool>();
        automation.OnClassify = (_, _) => Done(ShoutRunnerCrossDataCenterCheck.SameDataCenter);
        automation.OnTravel = (_, crossDc, _) => { crossDataCenterFlags.Add(crossDc); return Done(ShoutRunnerTransferOutcome.Success()); };

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.NotEmpty(crossDataCenterFlags);
        Assert.All(crossDataCenterFlags, Assert.False);
    }

    [Fact] public void Cross_data_center_classification_is_passed_through_unchanged()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var crossDataCenterFlags = new List<bool>();
        automation.OnClassify = (_, _) => Done(ShoutRunnerCrossDataCenterCheck.CrossDataCenter);
        automation.OnTravel = (_, crossDc, _) => { crossDataCenterFlags.Add(crossDc); return Done(ShoutRunnerTransferOutcome.Success()); };

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.NotEmpty(crossDataCenterFlags);
        Assert.All(crossDataCenterFlags, Assert.True);
    }

    [Fact] public void Run_start_anchored_repeat_schedules_the_next_run_from_when_this_one_started()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.SetInterval(0, 1, 0); // 1 minute
        var runStart = clock.UtcNow;

        service.Start();
        clock.Advance(TimeSpan.FromSeconds(5)); // the RUN takes 5 simulated seconds to finish
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.WaitingRepeat, service.State);
        Assert.Equal(runStart + TimeSpan.FromMinutes(1), service.NextRunAtUtc);
    }

    [Fact] public void Overrunning_the_next_boundary_advances_to_the_next_future_boundary_instead_of_running_immediately()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.SetInterval(0, 1, 0); // 1 minute
        var runStart = clock.UtcNow;
        // The very first World's transfer alone takes long enough to blow past one full interval before the RUN
        // completes; every other World in the Data Center travels instantly.
        var delayed = false;
        automation.OnTravel = (_, _, _) =>
        {
            if (!delayed) { delayed = true; clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30)); }
            return Done(ShoutRunnerTransferOutcome.Success());
        };

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(runStart + TimeSpan.FromMinutes(2), service.NextRunAtUtc);
    }

    [Fact] public void Missing_multiple_boundaries_jumps_straight_to_the_next_future_one()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.SetInterval(0, 1, 0);
        var runStart = clock.UtcNow;
        var delayed = false;
        automation.OnTravel = (_, _, _) =>
        {
            if (!delayed) { delayed = true; clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); }
            return Done(ShoutRunnerTransferOutcome.Success());
        };

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.True(service.NextRunAtUtc > clock.UtcNow);
        Assert.Equal(0, (service.NextRunAtUtc!.Value - runStart).Ticks % TimeSpan.FromMinutes(1).Ticks);
    }

    [Fact] public void No_overlapping_run_can_ever_start_while_one_is_already_in_flight()
    {
        var (service, _, _, _, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        Assert.Equal(ShoutRunnerStartResult.Started, service.Start());
        Assert.Equal(ShoutRunnerStartResult.AlreadyRunning, service.Start());
    }

    [Fact] public void Stop_during_a_wait_cancels_the_route_and_settles_into_stopped()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        service.Tick(clock.UtcNow); // enter the readiness gate for the first World
        service.Stop();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.Contains(service.TerminalEvents, e => e.Text == "Stop requested.");
    }

    [Fact] public void Stop_prevents_any_future_repeat()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        PumpUntilSettled(service, clock, chat); // WaitingRepeat
        service.Stop();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        clock.Advance(TimeSpan.FromDays(1));
        service.Tick(clock.UtcNow);
        Assert.Equal(ShoutRunnerState.Stopped, service.State); // never silently resumed
    }

    [Fact] public void A_stale_completion_from_before_stop_can_never_resurrect_route_state()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A", "B"], dataCenters: ["Aether"]);
        var pending = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        automation.OnTravel = (_, _, _) => pending.Task; // never completes on its own

        service.Start();
        service.Tick(clock.UtcNow); // readiness gate begins
        service.Tick(clock.UtcNow); // classify + travel begins, now pending

        service.Stop();
        // The abandoned task from before Stop completes only now — after Stop already moved State away from
        // whatever phase was consuming it.
        pending.SetResult(ShoutRunnerTransferOutcome.Success());
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.DoesNotContain(service.TerminalEvents, e => e.Text.Contains("SHOUT SENT"));
    }

    [Fact] public void Start_is_unavailable_while_stopping()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var pending = new TaskCompletionSource<ShoutRunnerReadinessOutcome>();
        automation.OnEnsureReady = _ => pending.Task;

        service.Start();
        service.Tick(clock.UtcNow);
        service.Stop();

        Assert.False(service.CanStart);
        Assert.Equal(ShoutRunnerStartResult.AlreadyRunning, service.Start());
    }

    [Fact] public void Venue_switch_hard_stops_a_run_in_flight_and_clears_the_terminal()
    {
        var (service, automation, clock, chat, profiles) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        service.Tick(clock.UtcNow);
        Assert.NotEmpty(service.TerminalEvents);

        var otherVenue = profiles.Create("Second venue");
        service.Load(otherVenue.Id);

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.Empty(service.TerminalEvents);
        Assert.Equal(string.Empty, service.Settings.ShoutMessage); // the new venue's own (default) config, not the old venue's
    }

    [Fact] public void Module_disable_hard_stops_a_run_in_flight_without_hanging()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        var module = new ShoutRunnerModule(service);
        service.Start();
        service.Tick(clock.UtcNow);

        module.IsEnabled = false;

        Assert.Equal(ShoutRunnerState.Stopped, service.State);
    }

    [Fact] public void Terminal_events_carry_a_timestamp_run_number_and_hierarchy_context()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        PumpUntilSettled(service, clock, chat);

        var destinationEvent = service.TerminalEvents.First(e => e.Destination is not null);
        Assert.Equal(1, destinationEvent.RunNumber);
        Assert.Equal("Aether", destinationEvent.DataCenter);
        Assert.Equal("Adamantoise", destinationEvent.World);
        Assert.NotEqual(default, destinationEvent.At);
    }

    [Fact] public void Run_number_increments_across_repeated_runs()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.SetInterval(0, 1, 0);
        service.Start();
        PumpUntilSettled(service, clock, chat);
        Assert.Equal(1, service.RunNumber);

        AdvanceToNextRun(service, clock);
        PumpUntilSettled(service, clock, chat);
        Assert.Equal(2, service.RunNumber);
    }

    [Fact] public void Terminal_retention_is_bounded_and_drops_the_oldest_entries_first()
    {
        var terminal = new ShoutRunnerTerminal();
        for (var i = 0; i < ShoutRunnerTerminal.MaxEntries + 50; i++)
            terminal.Add(new ShoutRunnerTerminalEvent(DateTimeOffset.UnixEpoch.AddSeconds(i), 1, ShoutRunnerEventSeverity.InProgress, $"event {i}"));

        Assert.Equal(ShoutRunnerTerminal.MaxEntries, terminal.Events.Count);
        Assert.Equal("event 50", terminal.Events[0].Text); // the oldest 50 were dropped
        Assert.Equal($"event {ShoutRunnerTerminal.MaxEntries + 49}", terminal.Events[^1].Text);
    }

    [Fact] public void Terminal_history_is_service_state_not_tied_to_any_particular_ui_read()
    {
        var (service, automation, clock, chat, _) = ReadyToStart(destinations: ["A"], dataCenters: ["Aether"]);
        service.Start();
        PumpUntilSettled(service, clock, chat);
        var firstRead = service.TerminalEvents.Count;
        var secondRead = service.TerminalEvents.Count; // an independent read of the same underlying service state
        Assert.Equal(firstRead, secondRead);
        Assert.True(firstRead > 0);
    }

    [Fact] public void Per_venue_settings_are_fully_isolated()
    {
        var host = new ModuleHost();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        var venueA = profiles.Current;
        var venueB = profiles.Create("Venue B");

        var clock = new Clock();
        var chat = Chat(clock);
        var diagnostics = new DiagnosticsService(host, profiles, clock);
        var service = new ShoutRunnerService(new FakeAutomation(), chat, profiles, diagnostics, clock, new NullRecoveryStore());

        service.Load(venueA.Id);
        service.UpdateShoutMessage("Venue A message");
        service.SetDataCenterSelected("Aether", true);

        service.Load(venueB.Id);
        Assert.Equal(string.Empty, service.Settings.ShoutMessage);
        Assert.Empty(service.Settings.SelectedDataCenters);

        service.Load(venueA.Id);
        Assert.Equal("Venue A message", service.Settings.ShoutMessage);
        Assert.Contains("Aether", service.Settings.SelectedDataCenters);
    }

    [Fact] public void Shout_message_data_center_selection_destinations_and_interval_all_persist_across_reload()
    {
        var host = new ModuleHost();
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, host);
        var venue = profiles.Current;
        var clock = new Clock();
        var service = new ShoutRunnerService(new FakeAutomation(), Chat(clock), profiles, new DiagnosticsService(host, profiles, clock), clock, new NullRecoveryStore());
        service.Load(venue.Id);

        service.UpdateShoutMessage("Reload me");
        service.SetDataCenterSelected("Crystal", true);
        service.SetDataCenterSelected("Primal", true);
        service.RemoveDestinationAt(0); service.RemoveDestinationAt(0); service.RemoveDestinationAt(0);
        service.AddDestination("Custom Aetheryte");
        service.SetInterval(2, 15, 30);

        var reloadedProfiles = new VenueProfileService(new InMemoryVenueStore(store.Read()), host);
        var reloaded = new ShoutRunnerService(new FakeAutomation(), Chat(clock), reloadedProfiles, new DiagnosticsService(host, reloadedProfiles, clock), clock, new NullRecoveryStore());
        reloaded.Load(venue.Id);

        Assert.Equal("Reload me", reloaded.Settings.ShoutMessage);
        Assert.Equal(["Crystal", "Primal"], reloaded.Settings.SelectedDataCenters);
        Assert.Equal(["Custom Aetheryte"], reloaded.Settings.Destinations);
        Assert.Equal(2, reloaded.Settings.IntervalHours);
        Assert.Equal(15, reloaded.Settings.IntervalMinutes);
        Assert.Equal(30, reloaded.Settings.IntervalSeconds);
    }

    /// <summary>The old "Announcements" schema lived under this exact module ID at schema version 1. Bumping to
    /// version 2 means the new config is simply never found under the old key — no exception, no invented
    /// conversion, and the old payload is left completely untouched (see <see cref="ShoutRunnerService.SchemaVersion"/>'s
    /// doc comment).</summary>
    [Fact] public void An_old_schema_version_1_payload_under_the_same_module_id_is_never_read_and_never_crashes()
    {
        var host = new ModuleHost();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        var venue = profiles.Current;
        profiles.SaveModuleConfig(venue.Id, ShoutRunnerService.ModuleId, 1, new { Presets = new[] { new { Name = "Old preset" } } });

        var clock = new Clock();
        var service = new ShoutRunnerService(new FakeAutomation(), Chat(clock), profiles, new DiagnosticsService(host, profiles, clock), clock, new NullRecoveryStore());
        service.Load(venue.Id);

        // Field-by-field, not a whole-record Assert.Equal: ShoutRunnerSettings holds List<string> properties, whose
        // default record-generated equality compares list references, not contents — two separate Default() calls
        // are never "equal" by that measure even with identical contents.
        var expected = ShoutRunnerSettings.Default();
        Assert.Equal(expected.ShoutMessage, service.Settings.ShoutMessage);
        Assert.Equal(expected.SelectedDataCenters, service.Settings.SelectedDataCenters);
        Assert.Equal(expected.Destinations, service.Settings.Destinations);
        Assert.Equal(expected.IntervalHours, service.Settings.IntervalHours);
    }

    // ----- test fixtures -----

    private static (ShoutRunnerService Service, FakeAutomation Automation, Clock Clock, ChatCommandService Chat, VenueProfileService Profiles) New()
    {
        var host = new ModuleHost();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        var clock = new Clock();
        var automation = new FakeAutomation();
        var chat = Chat(clock);
        var diagnostics = new DiagnosticsService(host, profiles, clock);
        var service = new ShoutRunnerService(automation, chat, profiles, diagnostics, clock, new NullRecoveryStore());
        service.Load(profiles.Current.Id);
        return (service, automation, clock, chat, profiles);
    }

    /// <summary>A crash-recovery journal that never actually persists anything — used by every test in this file
    /// that isn't itself about recovery (see <c>ShoutRunnerRecoveryTests</c> for those), so this large, otherwise
    /// unrelated suite doesn't need to care about the feature at all.</summary>
    private sealed class NullRecoveryStore : IShoutRunnerRecoveryStore
    {
        public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return null; }
        public void Save(ShoutRunnerRecoveryJournal journal) { }
        public void Delete() { }
    }

    private static (ShoutRunnerService Service, FakeAutomation Automation, Clock Clock, ChatCommandService Chat, VenueProfileService Profiles) ReadyToStart(IReadOnlyList<string> destinations, IReadOnlyList<string> dataCenters)
    {
        var (service, automation, clock, chat, profiles) = New();
        service.UpdateShoutMessage("Test shout");
        foreach (var d in service.Settings.Destinations.ToArray()) service.RemoveDestinationAt(0);
        foreach (var d in destinations) service.AddDestination(d);
        foreach (var dc in dataCenters) service.SetDataCenterSelected(dc, true);
        return (service, automation, clock, chat, profiles);
    }

    private static void AdvanceToNextRun(ShoutRunnerService service, Clock clock)
    {
        Assert.Equal(ShoutRunnerState.WaitingRepeat, service.State);
        clock.Advance(service.NextRunAtUtc!.Value - clock.UtcNow + TimeSpan.FromSeconds(1));
        service.Tick(clock.UtcNow);
    }

    /// <summary>Also drains <see cref="ChatCommandService"/>'s own queue every step — in production this is
    /// <c>Plugin.Update</c>'s job (a shared service ticked once per frame independent of any module), so a
    /// ShoutRunner shout/`` /li`` command enqueued here would otherwise never actually dispatch and the state
    /// machine would sit in <see cref="ShoutRunnerState.SendingShout"/> forever — a test-harness concern only, not a
    /// production one.</summary>
    private static void PumpUntilSettled(ShoutRunnerService service, Clock clock, ChatCommandService chat, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (service.State is ShoutRunnerState.Stopped or ShoutRunnerState.Faulted or ShoutRunnerState.WaitingRepeat) return;
            if (service.State == ShoutRunnerState.WaitingActionDelay) clock.Advance(TimeSpan.FromSeconds(service.Settings.ClampedDelaySeconds() + 1));
            chat.TickAsync().GetAwaiter().GetResult();
            service.Tick(clock.UtcNow);
        }

        Assert.Fail($"ShoutRunnerService did not settle within {maxSteps} ticks (stuck in {service.State}).");
    }

    private static Task<T> Done<T>(T value) => Task.FromResult(value);

    private static ChatCommandService Chat(Clock clock) => new(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.Zero);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan span) => UtcNow += span;
    }

    /// <summary>A fully scriptable <see cref="IShoutRunnerAutomation"/> — every method defaults to an immediate,
    /// successful, already-completed <see cref="Task"/> so a test only needs to override the one behavior it's
    /// actually exercising. See <see cref="ShoutRunnerService"/>'s type-level remark: because it only ever polls
    /// <c>IsCompleted</c>, an already-completed <see cref="Task"/> here advances the state machine on the very next
    /// <see cref="ShoutRunnerService.Tick"/> call with no real waiting.</summary>
    private sealed class FakeAutomation : IShoutRunnerAutomation
    {
        public Func<CancellationToken, Task<ShoutRunnerReadinessOutcome>> OnEnsureReady = _ => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Func<string, CancellationToken, Task<ShoutRunnerCrossDataCenterCheck>> OnClassify = (_, _) => Task.FromResult(ShoutRunnerCrossDataCenterCheck.SameDataCenter);
        public Func<string, bool, CancellationToken, Task<ShoutRunnerTransferOutcome>> OnTravel = (_, _, _) => Task.FromResult(ShoutRunnerTransferOutcome.Success());
        public Func<CancellationToken, Task<string?>> OnLocate = _ => Task.FromResult<string?>(null);
        public Func<string, CancellationToken, Task<ShoutRunnerTeleportOutcome>> OnTeleport = (_, _) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());
        public int ResetCount, AbortCount;

        public void ResetForVenue() => ResetCount++;
        public void Abort() => AbortCount++;
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => OnEnsureReady(token);
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => OnClassify(targetWorld, token);
        public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) => OnTravel(targetWorld, crossDataCenter, token);
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => OnLocate(token);
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => OnTeleport(destinationName, token);
    }
}
