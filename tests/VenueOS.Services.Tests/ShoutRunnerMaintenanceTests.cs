using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>0.3.8 maintenance: the custom-Aetheryte ("Add Aetheryte") destination model and the optional second shout
/// line. Everything here drives the real <see cref="ShoutRunnerService"/> and the real <see cref="ChatCommandService"/>
/// against a fake automation/catalog, so ordering, pacing, cancellation and persistence are exercised end to end —
/// only the ImGui picker and the in-game teleport/Lifestream engine are outside the automated boundary.</summary>
public sealed class ShoutRunnerAetheryteTests
{
    private static readonly string[] Defaults = ["Ul'dah - Steps of Nald", "New Gridania", "Limsa Lominsa Lower Decks"];
    private static readonly string[] Known = ["Foundation", "Idyllshire", "Limsa Lominsa Lower Decks", "New Gridania", "Ul'dah - Steps of Nald", "Tuliyollal"];

    [Fact] public void Default_settings_still_seed_the_three_city_aetherytes_in_order()
    {
        Assert.Equal(Defaults, ShoutRunnerSettings.Default().Destinations);
        Assert.Equal(Defaults, ShoutRunnerHarness.New().Service.Settings.Destinations);
    }

    [Fact] public void An_existing_saved_config_from_before_this_change_loads_unchanged_with_an_empty_second_line()
    {
        // The exact shape 0.3.7 wrote: no ShoutMessageLine2 property at all.
        const string oldJson = "{\"ShoutMessage\":\"Come on in!\",\"RepeatEnabled\":true,\"IntervalHours\":1,\"IntervalMinutes\":0,\"IntervalSeconds\":0,\"DelayBetweenActionsSeconds\":2,\"SelectedDataCenters\":[\"Aether\"],\"Destinations\":[\"Ul'dah - Steps of Nald\",\"New Gridania\",\"Limsa Lominsa Lower Decks\"]}";
        var settings = JsonSerializer.Deserialize<ShoutRunnerSettings>(oldJson)!;

        Assert.Equal("Come on in!", settings.ShoutMessage);
        Assert.Equal(string.Empty, settings.ShoutMessageLine2);
        Assert.Equal(Defaults, settings.Destinations);
        Assert.Equal(["Aether"], settings.SelectedDataCenters);
    }

    [Fact] public void An_old_payload_in_real_storage_loads_through_the_service_without_recovery_or_data_loss()
    {
        var h = ShoutRunnerHarness.New();
        h.Profiles.SaveModuleConfig(h.Profiles.Current.Id, ShoutRunnerService.ModuleId, ShoutRunnerService.SchemaVersion,
            new { ShoutMessage = "old line", RepeatEnabled = false, IntervalHours = 2, IntervalMinutes = 0, IntervalSeconds = 0, DelayBetweenActionsSeconds = 3, SelectedDataCenters = new[] { "Primal" }, Destinations = new[] { "New Gridania" } });

        var reloaded = h.Reload();

        Assert.Equal("old line", reloaded.Settings.ShoutMessage);
        Assert.Equal(string.Empty, reloaded.Settings.ShoutMessageLine2);
        Assert.Equal(["New Gridania"], reloaded.Settings.Destinations);
        Assert.Equal(["Primal"], reloaded.Settings.SelectedDataCenters);
        Assert.Empty(h.Profiles.RecoveryWarnings);
    }

    [Fact] public void Adding_a_valid_aetheryte_appends_it_after_the_defaults_and_reports_Added()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);

        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("Foundation"));

        Assert.Equal([.. Defaults, "Foundation"], h.Service.Settings.Destinations);
    }

    [Fact] public void An_added_aetheryte_is_stored_in_the_games_canonical_spelling_and_trimmed()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("  fOuNdAtIoN  "));
        Assert.Equal("Foundation", h.Service.Settings.Destinations[^1]);
    }

    [Fact] public void An_added_aetheryte_persists_across_a_destroyed_and_reconstructed_service()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        h.Service.AddDestination("Foundation");
        h.Service.AddDestination("Tuliyollal");

        var reloaded = h.Reload();

        Assert.Equal([.. Defaults, "Foundation", "Tuliyollal"], reloaded.Settings.Destinations);
    }

    [Fact] public void An_added_aetheryte_survives_a_plain_serialize_deserialize_round_trip()
    {
        var settings = ShoutRunnerSettings.Default() with { Destinations = [.. Defaults, "Foundation"], ShoutMessageLine2 = "second" };
        var back = JsonSerializer.Deserialize<ShoutRunnerSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.Destinations, back.Destinations);
        Assert.Equal("second", back.ShoutMessageLine2);
    }

    [Fact] public void An_added_aetheryte_is_actually_routed_by_a_real_run()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        h.Service.SetDataCenterSelected("Aether", true);
        h.Service.SetRepeatEnabled(false);
        h.Service.UpdateShoutMessage("hello");
        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("Foundation"));
        var teleports = new List<string>();
        h.Automation.OnTeleport = (name, _) => { teleports.Add(name); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };

        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        h.PumpUntilStopped();

        // Every World (Adamantoise, the very first, unmatched -> forward pass) visits all four configured stops.
        Assert.Contains("Foundation", teleports);
        Assert.Contains(h.Service.TerminalEvents, e => e.Destination == "Foundation" && e.Text.Contains("SHOUT SENT"));
        Assert.Equal(4 * 8, h.Sent.Count(c => c == "/shout hello"));
    }

    [Fact] public void The_route_planner_treats_an_added_aetheryte_like_any_other_destination()
    {
        var direction = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        var steps = ShoutRunnerRoutePlanner.PlanWorldRoute([.. Defaults, "Foundation"], "Foundation", ref direction);
        // Standing at the last configured stop walks backward through the rest — the documented ping-pong rule.
        Assert.Equal(["Foundation", Defaults[2], Defaults[1], Defaults[0]], steps.Select(x => x.Destination));
        Assert.False(steps[0].RequiresTeleport);
        Assert.All(steps.Skip(1), x => Assert.True(x.RequiresTeleport));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_entry_is_rejected_and_changes_nothing(string name)
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        Assert.Equal(ShoutRunnerAddDestinationResult.Blank, h.Service.AddDestination(name));
        Assert.Equal(Defaults, h.Service.Settings.Destinations);
    }

    [Fact] public void An_unknown_aetheryte_is_rejected_when_game_data_is_available()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        Assert.Equal(ShoutRunnerAddDestinationResult.UnknownAetheryte, h.Service.AddDestination("Not A Real Place"));
        Assert.Equal(Defaults, h.Service.Settings.Destinations);
    }

    [Fact] public void An_over_long_entry_is_rejected()
    {
        var h = ShoutRunnerHarness.New();
        Assert.Equal(ShoutRunnerAddDestinationResult.TooLong, h.Service.AddDestination(new string('x', ShoutRunnerService.MaxDestinationNameLength + 1)));
        Assert.Equal(Defaults, h.Service.Settings.Destinations);
    }

    [Fact] public void Without_game_data_any_well_formed_name_is_still_accepted_rather_than_making_Add_impossible()
    {
        var h = ShoutRunnerHarness.New();
        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("  Some Typed Name "));
        Assert.Equal("Some Typed Name", h.Service.Settings.Destinations[^1]);
    }

    [Fact] public void A_duplicate_of_a_default_or_a_custom_entry_is_rejected_case_insensitively()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        Assert.Equal(ShoutRunnerAddDestinationResult.Duplicate, h.Service.AddDestination("new gridania"));
        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("Foundation"));
        Assert.Equal(ShoutRunnerAddDestinationResult.Duplicate, h.Service.AddDestination("FOUNDATION"));
        Assert.Equal([.. Defaults, "Foundation"], h.Service.Settings.Destinations);
    }

    [Fact] public void A_custom_entry_can_be_removed_and_the_removal_persists_and_is_not_routed()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        h.Service.AddDestination("Foundation");
        h.Service.RemoveDestinationAt(h.Service.Settings.Destinations.Count - 1);

        Assert.Equal(Defaults, h.Service.Settings.Destinations);
        Assert.Equal(Defaults, h.Reload().Settings.Destinations);

        h.Service.SetDataCenterSelected("Aether", true);
        h.Service.SetRepeatEnabled(false);
        h.Service.UpdateShoutMessage("hello");
        var teleports = new List<string>();
        h.Automation.OnTeleport = (name, _) => { teleports.Add(name); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };
        h.Service.Start();
        h.PumpUntilStopped();
        Assert.DoesNotContain("Foundation", teleports);
    }

    [Fact] public void The_seeded_defaults_are_ordinary_entries_and_stay_removable_as_before()
    {
        // Documented decision: the three city entries are the seeded contents of the one destination list, not a fixed
        // built-in set — removing one has always been supported (and is what the existing route tests rely on).
        var h = ShoutRunnerHarness.New(catalog: Known);
        h.Service.RemoveDestinationAt(0);
        Assert.Equal(Defaults.Skip(1), h.Service.Settings.Destinations);
        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("Ul'dah - Steps of Nald"));
    }

    [Fact] public void A_run_keeps_the_destinations_it_started_with_even_if_one_is_added_mid_run()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        h.Service.SetDataCenterSelected("Aether", true);
        h.Service.SetRepeatEnabled(false);
        h.Service.UpdateShoutMessage("hello");
        var teleports = new List<string>();
        h.Automation.OnTeleport = (name, _) => { teleports.Add(name); return Task.FromResult(ShoutRunnerTeleportOutcome.Success()); };
        h.Service.Start();

        Assert.Equal(ShoutRunnerAddDestinationResult.Added, h.Service.AddDestination("Foundation"));
        h.PumpUntilStopped();

        Assert.DoesNotContain("Foundation", teleports);
        Assert.Contains("Foundation", h.Service.Settings.Destinations);
    }

    [Fact] public void Custom_destinations_are_per_venue_and_switching_venues_reloads_the_right_list()
    {
        var h = ShoutRunnerHarness.New(catalog: Known);
        var venueA = h.Profiles.Current;
        var venueB = h.Profiles.Create("Venue B");

        h.Service.AddDestination("Foundation");

        h.Service.Load(venueB.Id);
        Assert.Equal(Defaults, h.Service.Settings.Destinations);
        h.Service.AddDestination("Idyllshire");

        h.Service.Load(venueA.Id);
        Assert.Equal([.. Defaults, "Foundation"], h.Service.Settings.Destinations);
        h.Service.Load(venueB.Id);
        Assert.Equal([.. Defaults, "Idyllshire"], h.Service.Settings.Destinations);

        // ...and both survive a full reconstruction of the service/store.
        var reloaded = h.Reload();
        reloaded.Load(venueA.Id);
        Assert.Equal([.. Defaults, "Foundation"], reloaded.Settings.Destinations);
    }
}

public sealed class ShoutRunnerSecondLineTests
{
    private const string L1 = "first line";
    private const string L2 = "second line";

    // Aether has 8 Worlds; two destinations -> 16 shout steps per run.
    private const int StepsPerRun = 8 * 2;

    [Fact] public void A_one_line_config_from_before_this_change_is_Line_1_with_an_empty_Line_2()
    {
        var h = ShoutRunnerHarness.New();
        h.Profiles.SaveModuleConfig(h.Profiles.Current.Id, ShoutRunnerService.ModuleId, ShoutRunnerService.SchemaVersion,
            new { ShoutMessage = "legacy", RepeatEnabled = true, IntervalHours = 1, IntervalMinutes = 0, IntervalSeconds = 0, DelayBetweenActionsSeconds = 2, SelectedDataCenters = new[] { "Aether" }, Destinations = new[] { "A" } });
        var reloaded = h.Reload();
        Assert.Equal("legacy", reloaded.Settings.ShoutMessage);
        Assert.Equal(string.Empty, reloaded.Settings.ShoutMessageLine2);
    }

    [Fact] public void Line_1_only_sends_exactly_one_chat_command_per_destination()
    {
        var h = Ready();
        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        h.PumpUntilStopped();

        Assert.Equal(StepsPerRun, h.Sent.Count);
        Assert.All(h.Sent, c => Assert.Equal("/shout " + L1, c));
    }

    [Fact] public void Both_lines_send_exactly_two_commands_per_destination_Line_1_then_Line_2()
    {
        var h = Ready(line2: L2);
        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        h.PumpUntilStopped();

        Assert.Equal(StepsPerRun * 2, h.Sent.Count);
        for (var i = 0; i < h.Sent.Count; i += 2)
        {
            Assert.Equal("/shout " + L1, h.Sent[i]);
            Assert.Equal("/shout " + L2, h.Sent[i + 1]);
        }

        Assert.Equal(StepsPerRun, h.Service.TerminalEvents.Count(e => e.Text.Contains("SHOUT SENT (2 lines)")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t  ")]
    public void A_blank_or_whitespace_Line_2_is_treated_as_empty_and_sends_only_Line_1(string line2)
    {
        var h = Ready(line2: line2);
        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        h.PumpUntilStopped();

        Assert.Equal(StepsPerRun, h.Sent.Count);
        Assert.All(h.Sent, c => Assert.Equal("/shout " + L1, c));
    }

    [Fact] public void Line_2_filled_with_a_blank_Line_1_is_refused_because_Line_1_is_the_required_line()
    {
        var h = Ready(line1: "  ", line2: L2);
        Assert.Equal(ShoutRunnerStartResult.ShoutMessageRequired, h.Service.Start());
        Assert.Empty(h.Sent);
    }

    [Fact] public void Line_1_and_Line_2_are_trimmed_before_sending()
    {
        var h = Ready(line1: "  padded one  ", line2: "\tpadded two ");
        h.Service.Start();
        h.PumpUntilStopped();
        Assert.Equal("/shout padded one", h.Sent[0]);
        Assert.Equal("/shout padded two", h.Sent[1]);
    }

    [Fact] public void Each_line_is_measured_in_utf8_bytes_including_its_shout_prefix_against_the_shared_chat_limit()
    {
        var max = BlockLettersLimits.ChatBytes - ShoutRunnerShoutLines.CommandPrefix.Length;
        var atLimit = new string('a', max);
        var overLimit = new string('a', max + 1);
        Assert.False(ShoutRunnerShoutLines.ExceedsLimit(atLimit));
        Assert.True(ShoutRunnerShoutLines.ExceedsLimit(overLimit));

        // Two-byte characters: half as many characters already exceed the limit — bytes, not characters.
        var multiByte = new string('é', max / 2 + 1);
        Assert.True(multiByte.Length < max);
        Assert.True(ShoutRunnerShoutLines.ExceedsLimit(multiByte));
        Assert.False(ShoutRunnerShoutLines.ExceedsLimit("   "));
    }

    [Fact] public void Line_1_and_Line_2_are_validated_independently_at_Start()
    {
        var tooLong = new string('a', BlockLettersLimits.ChatBytes);
        Assert.Equal(ShoutRunnerStartResult.ShoutMessageTooLong, Ready(line1: tooLong, line2: L2).Service.Start());
        Assert.Equal(ShoutRunnerStartResult.ShoutMessageLine2TooLong, Ready(line1: L1, line2: tooLong).Service.Start());
        // Both fine at exactly the limit.
        var atLimit = new string('a', BlockLettersLimits.ChatBytes - ShoutRunnerShoutLines.CommandPrefix.Length);
        Assert.Equal(ShoutRunnerStartResult.Started, Ready(line1: atLimit, line2: atLimit).Service.Start());
    }

    [Fact] public void An_oversized_Line_2_is_rejected_at_Start_and_nothing_is_sent_or_truncated()
    {
        var h = Ready(line2: new string('z', 600));
        Assert.Equal(ShoutRunnerStartResult.ShoutMessageLine2TooLong, h.Service.Start());
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
        h.Pump(50);
        Assert.Empty(h.Sent);
    }

    [Fact] public void A_Line_2_made_oversized_mid_run_is_never_truncated_or_sent_and_Line_1_is_not_resent()
    {
        var h = Ready(line2: L2);
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);
        h.Service.UpdateShoutMessageLine2(new string('z', 600));
        h.PumpUntilStopped();

        Assert.All(h.Sent, c => Assert.Equal("/shout " + L1, c));
        Assert.Equal(StepsPerRun, h.Sent.Count);
        Assert.DoesNotContain(h.Sent, c => c.Contains("zzz"));
        Assert.Contains(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT INCOMPLETE") && e.Text.Contains("Line 1 sent, Line 2 FAILED"));
        Assert.DoesNotContain(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT SENT"));
    }

    [Fact] public void Stopping_after_Line_1_is_dispatched_prevents_Line_2_from_ever_being_sent()
    {
        var h = Ready(line2: L2);
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);
        Assert.Equal("/shout " + L1, h.Sent[0]);

        h.Service.Stop();
        h.Pump(200);

        Assert.Single(h.Sent);
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
    }

    [Fact] public void A_Line_2_already_queued_but_not_yet_dispatched_is_skipped_when_the_run_is_stopped()
    {
        var h = Ready(line2: L2, chatInterval: TimeSpan.FromSeconds(1));
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);
        h.Service.Tick(h.Clock.UtcNow); // observes Line 1's acceptance and enqueues Line 2 behind the pacing interval

        h.Service.Stop();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.TickChat();
        h.TickChat();

        Assert.Single(h.Sent);
    }

    [Fact] public void A_venue_switch_after_Line_1_cannot_deliver_a_stale_Line_2()
    {
        var h = Ready(line2: L2);
        var other = h.Profiles.Create("Other venue");
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);

        h.Service.Load(other.Id);
        h.Pump(200);

        Assert.Single(h.Sent);
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
    }

    [Fact] public void Disabling_the_module_after_Line_1_cannot_deliver_a_stale_Line_2()
    {
        var h = Ready(line2: L2);
        var module = new ShoutRunnerModule(h.Service);
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);

        module.IsEnabled = false;
        h.Pump(200);

        Assert.Single(h.Sent);
    }

    [Fact] public void A_Line_2_transport_failure_is_reported_as_a_partial_failure_and_Line_1_is_never_resent()
    {
        var h = Ready(line2: L2, failWhen: c => c == "/shout " + L2);
        h.Service.Start();
        h.PumpUntilStopped();

        Assert.Equal(StepsPerRun, h.Sent.Count(c => c == "/shout " + L1));
        Assert.Equal(StepsPerRun, h.Sent.Count(c => c == "/shout " + L2));
        Assert.Equal(StepsPerRun, h.Service.TerminalEvents.Count(e => e.Text.Contains("SHOUT INCOMPLETE: Line 1 sent, Line 2 FAILED")));
        Assert.DoesNotContain(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT SENT"));
        // The run itself carries on to the next destination — a partial shout is a failed destination, not a fault.
        Assert.Contains(h.Service.TerminalEvents, e => e.Destination == "B" && e.Text.Contains("SHOUT INCOMPLETE"));
    }

    [Fact] public void A_Line_1_failure_means_Line_2_is_not_sent_for_that_destination()
    {
        var h = Ready(line2: L2, failWhen: c => c == "/shout " + L1);
        h.Service.Start();
        h.PumpUntilStopped();

        Assert.DoesNotContain(h.Sent, c => c == "/shout " + L2);
        Assert.Equal(StepsPerRun, h.Sent.Count);
        Assert.Contains(h.Service.TerminalEvents, e => e.Text.Contains("Line 1 failed") && e.Text.Contains("Line 2 not sent"));
    }

    [Fact] public void A_partial_shout_is_not_checkpointed_as_a_completed_destination()
    {
        var store = new CapturingRecoveryStore();
        var h = Ready(line2: L2, failWhen: c => c == "/shout " + L2, recoveryStore: store);
        h.Service.Start();
        h.StepUntil(() => h.Service.TerminalEvents.Any(e => e.Text.Contains("SHOUT INCOMPLETE")));
        Assert.All(store.Saved, j => Assert.Empty(j.CompletedDestinationsInCurrentWorld));
    }

    [Fact] public void Line_2_is_paced_by_the_shared_chat_interval_and_only_after_Line_1_was_accepted()
    {
        var h = Ready(line2: L2, chatInterval: TimeSpan.FromSeconds(1));
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);
        var afterLine1 = h.Clock.UtcNow;

        // Line 1 was accepted; the service enqueues Line 2 on its next tick — but the chat service will not dispatch it
        // inside the same interval.
        h.Service.Tick(h.Clock.UtcNow);
        h.TickChat();
        Assert.Single(h.Sent);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.TickChat();
        Assert.Equal(["/shout " + L1, "/shout " + L2], h.Sent);
        Assert.True(h.Clock.UtcNow - afterLine1 >= TimeSpan.FromSeconds(1));
    }

    [Fact] public void Both_lines_use_the_same_channel_route_and_run_semantics()
    {
        var h = Ready(line2: L2);
        h.Service.Start();
        h.PumpUntilStopped();

        // Same /shout channel for both; same destination loop; both counted inside one destination's success event.
        Assert.All(h.Sent, c => Assert.StartsWith(ShoutRunnerShoutLines.CommandPrefix, c));
        Assert.Equal(h.Sent.Count / 2, h.Service.TerminalEvents.Count(e => e.Destination is not null && e.Text.Contains("SHOUT SENT")));
    }

    [Fact] public void Both_lines_persist_across_a_destroyed_and_reconstructed_service()
    {
        var h = ShoutRunnerHarness.New();
        h.Service.UpdateShoutMessage(L1);
        h.Service.UpdateShoutMessageLine2(L2);

        var reloaded = h.Reload();

        Assert.Equal(L1, reloaded.Settings.ShoutMessage);
        Assert.Equal(L2, reloaded.Settings.ShoutMessageLine2);
    }

    [Fact] public void Line_2_is_per_venue()
    {
        var h = ShoutRunnerHarness.New();
        var venueA = h.Profiles.Current;
        var venueB = h.Profiles.Create("Venue B");
        h.Service.UpdateShoutMessageLine2("venue A line 2");

        h.Service.Load(venueB.Id);
        Assert.Equal(string.Empty, h.Service.Settings.ShoutMessageLine2);

        h.Service.Load(venueA.Id);
        Assert.Equal("venue A line 2", h.Service.Settings.ShoutMessageLine2);
    }

    private static ShoutRunnerHarness Ready(string line1 = L1, string line2 = "", Action<ShoutRunnerHarness>? configure = null, Func<string, bool>? failWhen = null, TimeSpan? chatInterval = null, CapturingRecoveryStore? recoveryStore = null)
    {
        var h = ShoutRunnerHarness.New(failWhen: failWhen, chatInterval: chatInterval, recoveryStore: recoveryStore);
        while (h.Service.Settings.Destinations.Count > 0) h.Service.RemoveDestinationAt(0);
        h.Service.AddDestination("A");
        h.Service.AddDestination("B");
        h.Service.SetDataCenterSelected("Aether", true);
        h.Service.SetRepeatEnabled(false);
        h.Service.UpdateShoutMessage(line1);
        h.Service.UpdateShoutMessageLine2(line2);
        configure?.Invoke(h);
        return h;
    }
}

internal sealed class CapturingRecoveryStore : IShoutRunnerRecoveryStore
{
    public List<ShoutRunnerRecoveryJournal> Saved { get; } = [];
    public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return null; }
    public void Save(ShoutRunnerRecoveryJournal journal) => Saved.Add(journal);
    public void Delete() { }
}

internal sealed class FakeAetheryteCatalog(params string[] names) : IShoutRunnerAetheryteCatalog
{
    public IReadOnlyList<string> Names { get; } = names;
}

/// <summary>Owns a fully-wired <see cref="ShoutRunnerService"/> over an in-memory venue store, the REAL
/// <see cref="ChatCommandService"/> (with a capturing transport), a controllable clock, and a fake automation whose
/// every call succeeds immediately unless a test overrides it.</summary>
internal sealed class ShoutRunnerHarness
{
    public ModuleHost Host { get; } = new();
    public InMemoryVenueStore Store { get; } = new();
    public VenueProfileService Profiles { get; }
    public TestClock Clock { get; } = new();
    public List<string> Sent { get; } = [];
    public ChatCommandService Chat { get; }
    public HarnessAutomation Automation { get; } = new();
    public ShoutRunnerService Service { get; }
    private readonly IShoutRunnerAetheryteCatalog? catalog;
    private readonly IShoutRunnerRecoveryStore recoveryStore;
    private readonly Func<string, bool>? failWhen;
    private readonly TimeSpan chatInterval;

    private ShoutRunnerHarness(string[]? catalogNames, Func<string, bool>? failWhen, TimeSpan? chatInterval, IShoutRunnerRecoveryStore? recoveryStore)
    {
        this.failWhen = failWhen;
        this.chatInterval = chatInterval ?? TimeSpan.Zero;
        this.recoveryStore = recoveryStore ?? new CapturingRecoveryStore();
        catalog = catalogNames is null ? null : new FakeAetheryteCatalog(catalogNames);
        Profiles = new VenueProfileService(Store, Host);
        Chat = new ChatCommandService(Clock, new InlineFrameworkDispatcher(), text => { Sent.Add(text); return !(this.failWhen?.Invoke(text) ?? false); }, this.chatInterval);
        Service = Build(Profiles, Chat);
        Service.Load(Profiles.Current.Id);
    }

    public static ShoutRunnerHarness New(string[]? catalog = null, Func<string, bool>? failWhen = null, TimeSpan? chatInterval = null, IShoutRunnerRecoveryStore? recoveryStore = null) =>
        new(catalog, failWhen, chatInterval, recoveryStore);

    private ShoutRunnerService Build(VenueProfileService profiles, ChatCommandService chat) =>
        new(Automation, chat, profiles, new DiagnosticsService(Host, profiles, Clock), Clock, recoveryStore, catalog);

    /// <summary>Destroys the service and store wrapper and reconstructs both from what was actually serialized — the
    /// only kind of test that proves persistence (NEW_MODULE_GUIDE §13a/§30).</summary>
    public ShoutRunnerService Reload()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(Store.Read()), Host);
        var service = Build(profiles, Chat);
        service.Load(Profiles.Current.Id);
        return service;
    }

    /// <summary>One chat-service tick, then one service tick — the same order production drives them in (a shared
    /// service ticked once per frame independent of any module, then the module).</summary>
    public void Step()
    {
        if (Service.State == ShoutRunnerState.WaitingActionDelay) Clock.Advance(TimeSpan.FromSeconds(Service.Settings.ClampedDelaySeconds() + 1));
        else if (chatInterval > TimeSpan.Zero) Clock.Advance(chatInterval);
        Chat.TickAsync().GetAwaiter().GetResult();
        Service.Tick(Clock.UtcNow);
    }

    /// <summary>Drives only the shared chat service — a non-async helper so tests don't block on a Task themselves.</summary>
    public void TickChat() => Chat.TickAsync().GetAwaiter().GetResult();

    public void Pump(int steps)
    {
        for (var i = 0; i < steps; i++) Step();
    }

    /// <summary>Steps until <paramref name="condition"/> holds, checking after the chat half of a step too — so a test
    /// can stop at the exact moment Line 1 was dispatched, before the service has observed it.</summary>
    public void StepUntil(Func<bool> condition, int maxSteps = 4000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (condition()) return;
            if (Service.State == ShoutRunnerState.WaitingActionDelay) Clock.Advance(TimeSpan.FromSeconds(Service.Settings.ClampedDelaySeconds() + 1));
            else if (chatInterval > TimeSpan.Zero) Clock.Advance(chatInterval);
            Chat.TickAsync().GetAwaiter().GetResult();
            if (condition()) return;
            Service.Tick(Clock.UtcNow);
        }

        Assert.Fail($"Condition never became true within {maxSteps} steps (state {Service.State}).");
    }

    public void PumpUntilStopped(int maxSteps = 4000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (Service.State is ShoutRunnerState.Stopped or ShoutRunnerState.Faulted) return;
            Step();
        }

        Assert.Fail($"ShoutRunnerService did not stop within {maxSteps} steps (state {Service.State}).");
    }
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    public void Advance(TimeSpan span) => UtcNow += span;
}

internal sealed class HarnessAutomation : IShoutRunnerAutomation
{
    public Func<string, CancellationToken, Task<ShoutRunnerTeleportOutcome>> OnTeleport = (_, _) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());
    public void ResetForVenue() { }
    public void Abort() { }
    public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
    public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => Task.FromResult(ShoutRunnerCrossDataCenterCheck.SameDataCenter);
    public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) => Task.FromResult(ShoutRunnerTransferOutcome.Success());
    public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => Task.FromResult<string?>(null);
    public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => OnTeleport(destinationName, token);
}
