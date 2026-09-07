using VenueOS.Core;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Crash-recovery journal tests — see <c>ShoutRunnerRecoveryJournal</c>'s doc comment for the design this
/// verifies. A "crash" is simulated throughout by simply constructing a brand-new <see cref="ShoutRunnerService"/>
/// instance (fresh automation, fresh chat, but the SAME <see cref="InMemoryRecoveryStore"/> and
/// <see cref="VenueProfileService"/>) without ever calling <c>Stop()</c> on the old one — exactly like a real FFXIV
/// crash leaves no chance for any graceful shutdown code to run, only whatever was already durably checkpointed.</summary>
public sealed class ShoutRunnerRecoveryTests
{
    // ----- JOURNAL -----

    [Fact] public void Starting_a_run_creates_a_recovery_journal()
    {
        var (service, _, _, _, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        Assert.NotNull(store.Current);
        Assert.True(store.SaveCount > 0);
    }

    [Fact] public void Initial_recovery_identifies_the_first_data_center_and_world_with_nothing_completed_yet()
    {
        var (service, _, _, _, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        Assert.Equal("Aether", store.Current!.CurrentDataCenter);
        Assert.Equal("Adamantoise", store.Current.CurrentWorld); // first alphabetically
        Assert.Empty(store.Current.CompletedDestinationsInCurrentWorld);
    }

    [Fact] public void A_successful_destination_is_added_to_completed()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A", "B"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);
        Assert.Contains("A", store.Current!.CompletedDestinationsInCurrentWorld);
    }

    [Fact] public void Checkpoint_happens_before_the_route_advances_to_the_next_destination()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A", "B"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);
        Assert.Equal(ShoutRunnerState.WaitingActionDelay, service.State);
        Assert.DoesNotContain("B", store.Current!.CompletedDestinationsInCurrentWorld);
    }

    [Fact] public void A_destination_whose_shout_failed_is_not_marked_completed()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"], chatExecute: _ => false);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => service.TerminalEvents.Any(e => e.Text.Contains("SHOUT FAILED")));
        Assert.True(store.Current is null || !store.Current.CompletedDestinationsInCurrentWorld.Contains("A"));
    }

    [Fact] public void The_real_file_store_write_is_atomic_and_never_exposes_a_partial_or_temp_file()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileShoutRunnerRecoveryStore(dir);
            var journal = SampleJournal();
            store.Save(journal);

            var files = Directory.GetFiles(dir);
            Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => Path.GetFileName(f) == "shoutrunner-active-run.json");

            var loaded = store.TryLoad(out var corrupt);
            Assert.False(corrupt);
            Assert.Equal(journal.RunId, loaded!.RunId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void Successful_final_completion_deletes_the_journal()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.SetRepeatEnabled(false);
        service.Start();
        PumpUntilSettled(service, clock, chat);
        Assert.Equal(ShoutRunnerState.Stopped, service.State);
        Assert.Null(store.Current);
        Assert.True(store.DeleteCount > 0);
    }

    [Fact] public void Manual_stop_clears_the_recovery_journal()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        service.Stop();
        Assert.Null(store.Current);
    }

    [Fact] public void Plugin_dispose_module_disable_and_venue_switch_hard_stop_never_clears_the_journal()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        var runIdBefore = store.Current!.RunId;

        service.HardStop(); // the exact call ShoutRunnerModule.DisposeAsync/IsEnabled-false/Load all make

        Assert.NotNull(store.Current);
        Assert.Equal(runIdBefore, store.Current!.RunId);
    }

    [Fact] public void Malformed_journal_json_fails_safely_without_throwing()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shoutrunner-active-run.json"), "{ this is not valid json ");
            var store = new FileShoutRunnerRecoveryStore(dir);
            var loaded = store.TryLoad(out var corrupt);
            Assert.Null(loaded);
            Assert.True(corrupt);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void Unsupported_schema_version_fails_safely()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var badVersion = SampleJournal() with { SchemaVersion = 999 };
            File.WriteAllText(Path.Combine(dir, "shoutrunner-active-run.json"), System.Text.Json.JsonSerializer.Serialize(badVersion));
            var store = new FileShoutRunnerRecoveryStore(dir);
            var loaded = store.TryLoad(out var corrupt);
            Assert.Null(loaded);
            Assert.True(corrupt);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void A_corrupt_journal_is_reported_by_the_service_without_crashing_and_can_be_discarded()
    {
        var dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shoutrunner-active-run.json"), "not json");
            var fileStore = new FileShoutRunnerRecoveryStore(dir);
            var host = new ModuleHost();
            var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
            var clock = new Clock();
            var service = new ShoutRunnerService(new FakeAutomation(), new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.Zero), profiles, new DiagnosticsService(host, profiles, clock), clock, fileStore);
            service.Load(profiles.Current.Id);

            Assert.True(service.RecoveredJournalIsCorrupt);
            Assert.Null(service.RecoveredJournal);
            Assert.False(service.CanResume);

            service.DiscardRecovery();
            Assert.False(File.Exists(Path.Combine(dir, "shoutrunner-active-run.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ----- RESUME -----

    [Fact] public void Resume_never_repeats_completed_destinations_and_retries_the_interrupted_one_in_order()
    {
        // The shout MESSAGE is always the same configured text — order/identity is tracked via which destination
        // was actually teleported to (or, for a no-teleport "already there" first stop, would need a different
        // signal; none of these fixed test destinations ever match a detected location, so every stop teleports).
        var teleportedTo = new List<string>();
        var (profiles, diagnostics, store, clock) = SharedFixture();

        var (service1, _) = NewInstance(profiles, diagnostics, store, clock, out var chat1, teleportedTo);
        service1.Load(profiles.Current.Id);
        Configure(service1, ["Limsa", "Gridania", "Uldah"], ["Aether"]);

        service1.Start();
        // Crash only once Gridania's shout has actually durably checkpointed — merely having teleported there (as
        // opposed to its shout succeeding) is exactly the "in-progress, not yet completed" case a different test
        // covers (An_incomplete... / the interrupted-destination-retry assertion below).
        PumpUntilCondition(service1, clock, chat1, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count >= 2);
        Assert.Equal(["Limsa", "Gridania"], teleportedTo);

        var (service2, _) = NewInstance(profiles, diagnostics, store, clock, out var chat2, teleportedTo);
        service2.Load(profiles.Current.Id);

        Assert.True(service2.CanResume);
        Assert.Equal(ShoutRunnerResumeResult.Resumed, service2.Resume());
        // PumpUntilSettled runs the entire rest of the RUN (all remaining Worlds in Aether, each with their own
        // ping-ponged traversal) before it next settles — only the first three teleports (this one interrupted
        // World's own remaining destinations) are what this test is about.
        PumpUntilSettled(service2, clock, chat2);

        Assert.Equal(["Limsa", "Gridania", "Uldah"], teleportedTo.Take(3));
    }

    [Fact] public void Resume_preserves_ping_pong_direction_and_run_identity_across_a_world_boundary()
    {
        var teleportedTo = new List<string>();
        var (profiles, diagnostics, store, clock) = SharedFixture();

        var (service1, _) = NewInstance(profiles, diagnostics, store, clock, out var chat1, teleportedTo);
        service1.Load(profiles.Current.Id);
        Configure(service1, ["A", "B"], ["Aether"]);

        service1.Start();
        // World 1 (Adamantoise) completes A, B; World 2 (Cactuar) then ping-pongs back, starting at B. Crash only
        // once that first Cactuar destination is durably checkpointed, not merely teleported to.
        PumpUntilCondition(service1, clock, chat1, () => store.Current is not null && store.Current.CurrentWorld == "Cactuar" && store.Current.CompletedDestinationsInCurrentWorld.Count >= 1);
        Assert.Equal(["A", "B", "B"], teleportedTo);

        var (service2, _) = NewInstance(profiles, diagnostics, store, clock, out var chat2, teleportedTo);
        service2.Load(profiles.Current.Id);
        Assert.Equal(ShoutRunnerResumeResult.Resumed, service2.Resume());
        Assert.Equal(1, service2.RunNumber);
        Assert.Contains(service2.TerminalEvents, e => e.Text.Contains("Recovered interrupted RUN 1"));

        PumpUntilCondition(service2, clock, chat2, () => teleportedTo.Count >= 4);
        Assert.Equal(["A", "B", "B", "A"], teleportedTo);
    }

    [Fact] public void A_completed_previous_run_is_never_replayed_by_a_later_runs_recovery()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.SetRepeatEnabled(true);
        service.SetInterval(0, 1, 0);
        service.Start();
        PumpUntilSettled(service, clock, chat); // RUN 1 completes fully -> WaitingRepeat, journal cleared
        Assert.Null(store.Current);
        var runIdAfterRun1 = default(Guid?);

        clock.Advance(service.NextRunAtUtc!.Value - clock.UtcNow + TimeSpan.FromSeconds(1));
        service.Tick(clock.UtcNow);
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);

        Assert.Equal(2, store.Current!.RunNumber);
        Assert.Equal(2, service.RunNumber);
        Assert.NotEqual(runIdAfterRun1, store.Current.RunId);
        Assert.Empty(store.Current.SkippedDataCenters); // RUN 1's skip history (if any) never leaks into RUN 2
    }

    [Fact] public void Resume_uses_the_frozen_route_snapshot_even_if_live_settings_changed_after_the_crash()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A", "B"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);
        service.HardStop();

        // Settings edited after the "crash" — must not affect the interrupted run.
        service.SetDataCenterSelected("Crystal", true);
        service.AddDestination("Z");
        service.RemoveDestinationAt(0);

        Assert.Equal(ShoutRunnerResumeResult.Resumed, service.Resume());
        PumpUntilSettled(service, clock, chat);

        Assert.DoesNotContain(service.TerminalEvents, e => e.DataCenter == "Crystal");
        Assert.DoesNotContain(service.TerminalEvents, e => e.Destination == "Z");
    }

    // ----- SAME-DATA-CENTER WORLD TRANSFER BUG — recovery interaction -----
    // See ShoutRunnerSameDataCenterArrivalTests.cs for the actual arrival-decision bug/fix (live: Halicarnassus ->
    // Cuchulainn, both Dynamis). These confirm the recovery journal behaves correctly around a same-Data-Center
    // transfer failure — whether from this exact bug (pre-fix) or any other genuine same-Data-Center failure
    // (post-fix) — never checkpointing a destination as complete merely because travel was attempted.

    [Fact] public void A_failed_same_data_center_transfer_leaves_the_target_world_and_its_destinations_incomplete()
    {
        var (service, automation, clock, chat, _, store) = ReadyToStart(["A"], ["Dynamis"]);
        // Cuchulainn is alphabetically first in Dynamis — the exact live route shape (Halicarnassus -> Cuchulainn).
        automation.OnTravel = (world, _, _) => world == "Cuchulainn"
            ? Task.FromResult(ShoutRunnerTransferOutcome.Failed("Arrived at Halicarnassus instead of Cuchulainn."))
            : Task.FromResult(ShoutRunnerTransferOutcome.Success());

        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.Equal(ShoutRunnerState.Faulted, service.State);
        Assert.NotNull(store.Current); // Faulted keeps the journal — this is the interrupted run to resume, not a completed one.
        Assert.Equal("Cuchulainn", store.Current!.CurrentWorld);
        Assert.Empty(store.Current.CompletedDestinationsInCurrentWorld);
    }

    [Fact] public void Resume_retries_the_target_world_once_the_same_data_center_transfer_succeeds()
    {
        var (profiles, diagnostics, store, clock) = SharedFixture();
        var teleportedTo = new List<string>();

        var (service1, automation1) = NewInstance(profiles, diagnostics, store, clock, out var chat1, teleportedTo);
        automation1.OnTravel = (world, _, _) => world == "Cuchulainn"
            ? Task.FromResult(ShoutRunnerTransferOutcome.Failed("Arrived at Halicarnassus instead of Cuchulainn."))
            : Task.FromResult(ShoutRunnerTransferOutcome.Success());
        service1.Load(profiles.Current.Id);
        Configure(service1, ["A"], ["Dynamis"]);
        service1.Start();
        PumpUntilSettled(service1, clock, chat1);
        Assert.Equal(ShoutRunnerState.Faulted, service1.State);

        // A fresh instance (simulating the fix now in effect / the underlying congestion having cleared) — the
        // same-Data-Center transfer succeeds this time.
        var (service2, _) = NewInstance(profiles, diagnostics, store, clock, out var chat2, teleportedTo);
        service2.Load(profiles.Current.Id);
        Assert.True(service2.CanResume);
        Assert.Equal(ShoutRunnerResumeResult.Resumed, service2.Resume());
        PumpUntilCondition(service2, clock, chat2, () => service2.TerminalEvents.Any(e => e.Text.Contains("SHOUT SENT")));

        Assert.Contains("A", teleportedTo);
        Assert.Contains(service2.TerminalEvents, e => e.Text.Contains("SHOUT SENT") && e.Destination == "A");
    }

    [Fact] public void A_successful_shout_on_the_previously_troublesome_target_world_checkpoints_normally()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Dynamis"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);

        Assert.Equal("Cuchulainn", store.Current!.CurrentWorld);
        Assert.Contains("A", store.Current.CompletedDestinationsInCurrentWorld);
    }

    // ----- DATA CENTER SKIPS -----

    [Fact] public void A_data_center_skip_decision_is_persisted_with_its_reason()
    {
        var (service, automation, clock, chat, _, store) = ReadyToStart(["A"], ["Aether", "Crystal"]);
        automation.OnTravel = (world, _, _) => world == "Adamantoise" ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("congested")) : Task.FromResult(ShoutRunnerTransferOutcome.Success());

        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.SkippedDataCenters.Count > 0);

        var skip = store.Current!.SkippedDataCenters.Single();
        Assert.Equal("Aether", skip.DataCenter);
        Assert.Contains("congested", skip.Reason);
    }

    [Fact] public void Resume_restores_a_skipped_data_center_never_retries_it_and_never_edits_saved_settings()
    {
        var (profiles, diagnostics, store, clock) = SharedFixture();
        var visited = new List<string>();

        var (service1, automation1) = NewInstance(profiles, diagnostics, store, clock, out var chat1, []);
        automation1.OnTravel = (world, _, _) =>
        {
            visited.Add(world);
            return world == "Adamantoise" ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("congested")) : Task.FromResult(ShoutRunnerTransferOutcome.Success());
        };
        service1.Load(profiles.Current.Id);
        Configure(service1, ["A"], ["Aether", "Crystal"]);

        service1.Start();
        PumpUntilCondition(service1, clock, chat1, () => store.Current is not null && store.Current.CurrentDataCenter == "Crystal" && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);
        Assert.Contains(store.Current!.SkippedDataCenters, s => s.DataCenter == "Aether");

        var (service2, automation2) = NewInstance(profiles, diagnostics, store, clock, out var chat2, []);
        automation2.OnTravel = (world, _, _) => { visited.Add(world); return Task.FromResult(ShoutRunnerTransferOutcome.Success()); };
        service2.Load(profiles.Current.Id);

        var summary = service2.RecoveredSummary!;
        Assert.Contains(summary.SkippedDataCenters, s => s.DataCenter == "Aether"); // restored for display

        var visitedBeforeResume = visited.Count;
        Assert.Equal(ShoutRunnerResumeResult.Resumed, service2.Resume());
        PumpUntilSettled(service2, clock, chat2);

        Assert.DoesNotContain(visited.Skip(visitedBeforeResume), w => ShoutRunnerCatalog.DataCenterFor(w) == "Aether"); // never retried
        Assert.Contains("Aether", service2.Settings.SelectedDataCenters); // saved route untouched
    }

    [Fact] public void Multiple_skipped_data_centers_all_restore_correctly()
    {
        var (service, automation, clock, chat, _, store) = ReadyToStart(["A"], ["Aether", "Crystal", "Dynamis"]);
        automation.OnTravel = (world, _, _) => world is "Adamantoise" or "Balmung"
            ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("congested"))
            : Task.FromResult(ShoutRunnerTransferOutcome.Success());

        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.SkippedDataCenters.Count >= 2);

        Assert.Equal(["Aether", "Crystal"], store.Current!.SkippedDataCenters.Select(s => s.DataCenter));
        Assert.Equal("Dynamis", store.Current.CurrentDataCenter);
    }

    [Fact] public void Successful_destinations_after_a_skipped_data_center_still_checkpoint_normally()
    {
        var (service, automation, clock, chat, _, store) = ReadyToStart(["A"], ["Aether", "Crystal"]);
        automation.OnTravel = (world, _, _) => world == "Adamantoise" ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("congested")) : Task.FromResult(ShoutRunnerTransferOutcome.Success());

        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CurrentDataCenter == "Crystal" && store.Current.CompletedDestinationsInCurrentWorld.Count > 0);

        Assert.Equal("Balmung", store.Current!.CurrentWorld);
        Assert.Contains("A", store.Current.CompletedDestinationsInCurrentWorld);
    }

    // ----- VENUE / UI -----

    [Fact] public void Recovery_records_the_owning_venue_profile()
    {
        var (service, _, clock, chat, profiles, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        Assert.Equal(profiles.Current.Id, store.Current!.VenueId);
    }

    [Fact] public void The_owning_venue_can_resume()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        service.HardStop();

        Assert.True(service.RecoveredJournalBelongsToCurrentVenue);
        Assert.True(service.CanResume);
    }

    [Fact] public void A_different_venue_does_not_blindly_resume_another_venues_recovery()
    {
        var (profiles, diagnostics, store, clock) = SharedFixture();
        var venueB = profiles.Create("Venue B");

        var (service, _) = NewInstance(profiles, diagnostics, store, clock, out var chat, []);
        service.Load(profiles.Current.Id);
        Configure(service, ["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        service.HardStop();

        service.Load(venueB.Id);

        Assert.False(service.RecoveredJournalBelongsToCurrentVenue);
        Assert.Equal(ShoutRunnerResumeResult.DifferentVenue, service.Resume());
    }

    [Fact] public void Starting_a_new_run_overwrites_whatever_recovery_journal_existed()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        var oldRunId = store.Current!.RunId;
        service.HardStop();
        Assert.NotNull(store.Current);

        // The operator-panel confirmation gate itself is ImGui UI and is not unit-tested here — only the service
        // guarantee it protects: Start() unconditionally supersedes whatever journal was already on disk.
        service.Start();
        Assert.NotEqual(oldRunId, store.Current!.RunId);
    }

    [Fact] public void Discard_recovery_removes_only_the_journal_never_saved_settings()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A", "B"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null);
        service.HardStop();

        var destinationsBefore = service.Settings.Destinations.ToList();
        var dataCentersBefore = service.Settings.SelectedDataCenters.ToList();

        service.DiscardRecovery();

        Assert.Null(service.RecoveredJournal);
        Assert.Null(store.Current);
        Assert.Equal(destinationsBefore, service.Settings.Destinations);
        Assert.Equal(dataCentersBefore, service.Settings.SelectedDataCenters);
    }

    [Fact] public void A_successfully_completed_run_no_longer_offers_resume()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A"], ["Aether"]);
        service.SetRepeatEnabled(false);
        service.Start();
        PumpUntilSettled(service, clock, chat);

        Assert.False(service.CanResume);
        Assert.Null(service.RecoveredJournal);
    }

    [Fact] public void Recovered_summary_reports_the_last_completed_and_next_destination()
    {
        var (service, _, clock, chat, _, store) = ReadyToStart(["A", "B", "C"], ["Aether"]);
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.CompletedDestinationsInCurrentWorld.Count >= 1);
        service.HardStop();

        var summary = service.RecoveredSummary!;
        Assert.Equal("A", summary.LastCompletedDestination);
        Assert.Equal("B", summary.NextDestination);
    }

    [Fact] public void Recovered_summary_lists_skipped_data_centers_with_reasons()
    {
        var (service, automation, clock, chat, _, store) = ReadyToStart(["A"], ["Aether", "Crystal"]);
        automation.OnTravel = (world, _, _) => world == "Adamantoise" ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("congested")) : Task.FromResult(ShoutRunnerTransferOutcome.Success());
        service.Start();
        PumpUntilCondition(service, clock, chat, () => store.Current is not null && store.Current.SkippedDataCenters.Count > 0);
        service.HardStop();

        var summary = service.RecoveredSummary!;
        var skip = Assert.Single(summary.SkippedDataCenters);
        Assert.Equal("Aether", skip.DataCenter);
        Assert.Contains("congested", skip.Reason);
    }

    // ----- test fixtures -----

    private static (ShoutRunnerService Service, FakeAutomation Automation, Clock Clock, ChatCommandService Chat, VenueProfileService Profiles, InMemoryRecoveryStore Store) New(Func<string, bool>? chatExecute = null)
    {
        var host = new ModuleHost();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        var clock = new Clock();
        var automation = new FakeAutomation();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), chatExecute ?? (_ => true), TimeSpan.Zero);
        var diagnostics = new DiagnosticsService(host, profiles, clock);
        var store = new InMemoryRecoveryStore();
        var service = new ShoutRunnerService(automation, chat, profiles, diagnostics, clock, store);
        service.Load(profiles.Current.Id);
        return (service, automation, clock, chat, profiles, store);
    }

    private static (ShoutRunnerService Service, FakeAutomation Automation, Clock Clock, ChatCommandService Chat, VenueProfileService Profiles, InMemoryRecoveryStore Store) ReadyToStart(IReadOnlyList<string> destinations, IReadOnlyList<string> dataCenters, Func<string, bool>? chatExecute = null)
    {
        var (service, automation, clock, chat, profiles, store) = New(chatExecute);
        Configure(service, destinations, dataCenters);
        return (service, automation, clock, chat, profiles, store);
    }

    private static void Configure(ShoutRunnerService service, IReadOnlyList<string> destinations, IReadOnlyList<string> dataCenters)
    {
        service.UpdateShoutMessage("Test shout");
        while (service.Settings.Destinations.Count > 0) service.RemoveDestinationAt(0);
        foreach (var d in destinations) service.AddDestination(d);
        foreach (var dc in dataCenters) service.SetDataCenterSelected(dc, true);
    }

    /// <summary>Shared underlying dependencies for a multi-instance "crash then restart" scenario — everything a
    /// real plugin restart would NOT reconstruct from scratch (the venue store and the on-disk recovery file) stays
    /// the same across instances; everything a restart genuinely does reconstruct (the automation engine, chat
    /// transport, and the service itself) is created fresh via <see cref="NewInstance"/>.</summary>
    private static (VenueProfileService Profiles, DiagnosticsService Diagnostics, InMemoryRecoveryStore Store, Clock Clock) SharedFixture()
    {
        var host = new ModuleHost();
        var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        var clock = new Clock();
        var diagnostics = new DiagnosticsService(host, profiles, clock);
        return (profiles, diagnostics, new InMemoryRecoveryStore(), clock);
    }

    /// <summary><paramref name="teleportedTo"/> is appended to, in order, every time the automation's teleport is
    /// invoked — the reliable way to observe destination order/identity across instances, since the actual /shout
    /// text is always the same configured message regardless of which destination it was sent from.</summary>
    private static (ShoutRunnerService Service, FakeAutomation Automation) NewInstance(VenueProfileService profiles, DiagnosticsService diagnostics, InMemoryRecoveryStore store, Clock clock, out ChatCommandService chat, List<string> teleportedTo)
    {
        var automation = new FakeAutomation();
        automation.OnTeleport = (destination, _) => { teleportedTo.Add(destination); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };
        chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.Zero);
        var service = new ShoutRunnerService(automation, chat, profiles, diagnostics, clock, store);
        return (service, automation);
    }

    private static void PumpUntilSettled(ShoutRunnerService service, Clock clock, ChatCommandService chat, int maxSteps = 3000)
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

    private static void PumpUntilCondition(ShoutRunnerService service, Clock clock, ChatCommandService chat, Func<bool> condition, int maxSteps = 3000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (condition()) return;
            if (service.State is ShoutRunnerState.Stopped or ShoutRunnerState.Faulted or ShoutRunnerState.WaitingRepeat) break;
            if (service.State == ShoutRunnerState.WaitingActionDelay) clock.Advance(TimeSpan.FromSeconds(service.Settings.ClampedDelaySeconds() + 1));
            chat.TickAsync().GetAwaiter().GetResult();
            service.Tick(clock.UtcNow);
        }
        Assert.True(condition(), $"Condition was never met (settled in {service.State} after {maxSteps} ticks).");
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "shoutrunner-recovery-tests-" + Guid.NewGuid().ToString("N"));

    private static ShoutRunnerRecoveryJournal SampleJournal() => new(
        ShoutRunnerRecoveryJournal.CurrentSchemaVersion,
        Guid.NewGuid(),
        "Sample Venue",
        Guid.NewGuid(),
        1,
        DateTimeOffset.UnixEpoch,
        new ShoutRunnerRunConfig(["Aether"], ["A"], 2),
        "Aether",
        "Adamantoise",
        [],
        null,
        ShoutRunnerRoutePlanner.TraversalDirection.Forward,
        [],
        DateTimeOffset.UnixEpoch);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan span) => UtcNow += span;
    }

    private sealed class InMemoryRecoveryStore : IShoutRunnerRecoveryStore
    {
        public ShoutRunnerRecoveryJournal? Current { get; private set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return Current; }
        public void Save(ShoutRunnerRecoveryJournal journal) { Current = journal; SaveCount++; }
        public void Delete() { Current = null; DeleteCount++; }
    }

    private sealed class FakeAutomation : IShoutRunnerAutomation
    {
        public Func<CancellationToken, Task<ShoutRunnerReadinessOutcome>> OnEnsureReady = _ => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Func<string, CancellationToken, Task<ShoutRunnerCrossDataCenterCheck>> OnClassify = (_, _) => Task.FromResult(ShoutRunnerCrossDataCenterCheck.SameDataCenter);
        public Func<string, bool, CancellationToken, Task<ShoutRunnerTransferOutcome>> OnTravel = (_, _, _) => Task.FromResult(ShoutRunnerTransferOutcome.Success());
        public Func<CancellationToken, Task<string?>> OnLocate = _ => Task.FromResult<string?>(null);
        public Func<string, CancellationToken, Task<ShoutRunnerTeleportOutcome>> OnTeleport = (_, _) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());

        public void ResetForVenue() { }
        public void Abort() { }
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => OnEnsureReady(token);
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => OnClassify(targetWorld, token);
        public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) => OnTravel(targetWorld, crossDataCenter, token);
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => OnLocate(token);
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => OnTeleport(destinationName, token);
    }
}
