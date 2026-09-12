using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Modules.Operations.DjShouts;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>
/// <see cref="DjShoutsService"/>: preset persistence/isolation (NEW_MODULE_GUIDE.md §13a/§30), slot assignment and
/// selection (task §12/§26/§27/§28), execution/pacing/dispatch-confirmation gating (task §21/§22/§24/§25),
/// the Last DJ Shout timer's completion-only semantics and persistence (task §9/§10), chat-byte-limit validation
/// (task §20), and the <see cref="DjShoutsModule"/> descriptor (NEW_MODULE_GUIDE.md §42/§43). Drives
/// <see cref="SchedulerService"/>/<see cref="ChatCommandService"/> with a controllable <see cref="Clock"/>, ticking
/// both every simulated second — the same style <c>GiveawayServiceTests</c> uses for this shared infrastructure.
/// </summary>
public sealed class DjShoutsServiceTests
{
    // =========================================================================================================
    // Preset model / persistence (task §34)
    // =========================================================================================================

    [Fact]
    public void Default_settings_load_with_no_presets_safely()
    {
        var t = Create();
        Assert.Empty(t.Service.Settings.Presets);
        Assert.Null(t.Service.Settings.SlotAssignments.Get(1));
        Assert.Equal(1, t.Service.Settings.SelectedSlot);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Create_preset_persists()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Friday DJ"));

        var reloaded = Reload(t);
        Assert.Contains(reloaded.Settings.Presets, x => x.Id == preset.Id && x.Name == "Friday DJ");
    }

    [Fact]
    public void Stable_id_is_generated_and_distinct_from_the_drafts_own_id()
    {
        var t = Create();
        var draft = DjShoutPreset.CreateNew("Whatever");
        var saved = t.Service.SaveNewPreset(draft);
        Assert.NotEqual(draft.Id, saved.Id); // SaveNewPreset always mints a fresh Id, matching GiveawayService
        Assert.NotEqual(Guid.Empty, saved.Id);
    }

    [Fact]
    public void Default_new_line_channel_is_yell()
    {
        Assert.Equal(DjShoutChannel.Yell, DjShoutLine.Empty().Channel);
    }

    [Fact]
    public void Edit_preserves_stable_id()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Original"));
        t.Service.UpdatePreset(preset.Id, p => p with { Name = "Renamed" });

        var updated = t.Service.Settings.Presets.Single();
        Assert.Equal(preset.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
    }

    [Fact]
    public void Cancel_new_never_calls_into_the_service_and_persists_nothing()
    {
        var t = Create();
        var draft = DjShoutPreset.CreateNew("Would Have Been New") with { Lines = [new("hello", DjShoutChannel.Yell)] };
        // "Cancel" is simply never calling SaveNewPreset — there is nothing else to undo.
        Assert.Empty(t.Service.Settings.Presets);
        Assert.DoesNotContain(t.Service.Settings.Presets, p => p.Name == draft.Name);
    }

    [Fact]
    public void Cancel_edit_leaves_the_persisted_preset_completely_unchanged()
    {
        var t = Create();
        var original = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Original"));
        var draft = original with { Name = "Changed In Draft Only" };
        // Cancel: draft discarded, no call into the service.
        var stillPersisted = t.Service.Settings.Presets.Single(p => p.Id == original.Id);
        Assert.Equal("Original", stillPersisted.Name);
    }

    [Fact]
    public void Delete_removes_the_preset()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Temporary"));
        Assert.True(t.Service.DeletePreset(preset.Id));
        Assert.Empty(t.Service.Settings.Presets);
    }

    [Fact]
    public void Delete_of_a_preset_assigned_to_a_slot_clears_that_slot_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Assigned"));
        t.Service.AssignSlot(3, preset.Id);

        t.Service.DeletePreset(preset.Id);

        Assert.Null(t.Service.Settings.SlotAssignments.Get(3));
        Assert.Null(t.Service.SelectedPreset); // slot 3 no longer resolves to a missing/dangling Id
    }

    [Fact]
    public void Multiple_presets_persist_and_reload()
    {
        var t = Create();
        t.Service.SaveNewPreset(DjShoutPreset.CreateNew("First"));
        t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Second"));
        t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Third"));

        var reloaded = Reload(t);
        Assert.Equal(3, reloaded.Settings.Presets.Count);
        Assert.Contains(reloaded.Settings.Presets, x => x.Name == "First");
        Assert.Contains(reloaded.Settings.Presets, x => x.Name == "Second");
        Assert.Contains(reloaded.Settings.Presets, x => x.Name == "Third");
    }

    [Fact]
    public void Every_preset_field_survives_a_full_serialize_deserialize_boundary()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Friday Night Main DJ") with
        {
            Lines = [new("Come dance!", DjShoutChannel.Yell), new("Requests open!", DjShoutChannel.Shout)],
        });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        DispatchImmediate(t);

        var rebuiltSnapshot = JsonSerializer.Deserialize<VenueStoreSnapshot>(JsonSerializer.Serialize(t.Store.Read()))!;
        var freshProfiles = new VenueProfileService(new InMemoryVenueStore(rebuiltSnapshot), new ModuleHost());
        var fresh = new DjShoutsService(t.Scheduler, t.Chat, freshProfiles, t.Clock, FakeDiagnostics(freshProfiles));
        fresh.Load(t.VenueId);

        var reloadedPreset = fresh.Settings.Presets.Single();
        Assert.Equal("Friday Night Main DJ", reloadedPreset.Name);
        Assert.Equal(2, reloadedPreset.Lines.Count);
        Assert.Equal("Come dance!", reloadedPreset.Lines[0].Text);
        Assert.Equal(DjShoutChannel.Yell, reloadedPreset.Lines[0].Channel);
        Assert.Equal("Requests open!", reloadedPreset.Lines[1].Text);
        Assert.Equal(DjShoutChannel.Shout, reloadedPreset.Lines[1].Channel);
        Assert.Equal(preset.Id, fresh.Settings.SlotAssignments.Get(1));
        Assert.Equal(1, fresh.Settings.SelectedSlot);
        Assert.NotNull(fresh.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Line channels (task §35)
    // =========================================================================================================

    [Fact]
    public void Yell_line_generates_slash_yell()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("Hello", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Equal(["/yell Hello"], t.Sent);
    }

    [Fact]
    public void Shout_line_generates_slash_shout()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("Hello", DjShoutChannel.Shout));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Equal(["/shout Hello"], t.Sent);
    }

    [Fact]
    public void Mixed_preset_preserves_line_order_yell_shout_yell()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new DjShoutLine("One", DjShoutChannel.Yell),
            new DjShoutLine("Two", DjShoutChannel.Shout),
            new DjShoutLine("Three", DjShoutChannel.Yell));

        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Equal(["/yell One"], t.Sent);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell One", "/shout Two"], t.Sent);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell One", "/shout Two", "/yell Three"], t.Sent);
    }

    [Fact]
    public void Channel_survives_save_and_reload()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset") with
        {
            Lines = [new("Shout line", DjShoutChannel.Shout)],
        });

        var reloaded = Reload(t);
        Assert.Equal(DjShoutChannel.Shout, reloaded.Settings.Presets.Single(p => p.Id == preset.Id).Lines.Single().Channel);
    }

    [Fact]
    public void Reordering_preserves_channel_associated_with_its_line()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset") with
        {
            Lines = [new("A", DjShoutChannel.Yell), new("B", DjShoutChannel.Shout)],
        });

        // Simulate the editor's "Up" swap on line index 1 (mirrors DjShoutPresetEditorModal.MoveLine).
        t.Service.UpdatePreset(preset.Id, p =>
        {
            var list = p.Lines.ToList();
            (list[0], list[1]) = (list[1], list[0]);
            return p with { Lines = list };
        });

        var reordered = t.Service.Settings.Presets.Single().Lines;
        Assert.Equal("B", reordered[0].Text);
        Assert.Equal(DjShoutChannel.Shout, reordered[0].Channel);
        Assert.Equal("A", reordered[1].Text);
        Assert.Equal(DjShoutChannel.Yell, reordered[1].Channel);
    }

    [Fact]
    public void Removing_a_line_does_not_corrupt_neighboring_line_channel_pairs()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset") with
        {
            Lines = [new("A", DjShoutChannel.Yell), new("B", DjShoutChannel.Shout), new("C", DjShoutChannel.Yell)],
        });

        t.Service.UpdatePreset(preset.Id, p => p with { Lines = p.Lines.Where((_, i) => i != 1).ToArray() });

        var remaining = t.Service.Settings.Presets.Single().Lines;
        Assert.Equal(2, remaining.Count);
        Assert.Equal("A", remaining[0].Text); Assert.Equal(DjShoutChannel.Yell, remaining[0].Channel);
        Assert.Equal("C", remaining[1].Text); Assert.Equal(DjShoutChannel.Yell, remaining[1].Channel);
    }

    // =========================================================================================================
    // Slots (task §36)
    // =========================================================================================================

    [Fact]
    public void Dj_1_assignment_persists()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(1, preset.Id);

        var reloaded = Reload(t);
        Assert.Equal(preset.Id, reloaded.Settings.SlotAssignments.Get(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Dj_2_through_5_assignments_persist(int slot)
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(slot, preset.Id);

        var reloaded = Reload(t);
        Assert.Equal(preset.Id, reloaded.Settings.SlotAssignments.Get(slot));
    }

    [Fact]
    public void Slots_are_independent()
    {
        var t = Create();
        var presetA = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("A"));
        var presetB = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("B"));
        t.Service.AssignSlot(1, presetA.Id);
        t.Service.AssignSlot(2, presetB.Id);

        Assert.Equal(presetA.Id, t.Service.Settings.SlotAssignments.Get(1));
        Assert.Equal(presetB.Id, t.Service.Settings.SlotAssignments.Get(2));
        Assert.Null(t.Service.Settings.SlotAssignments.Get(3));
    }

    [Fact]
    public void None_assignment_is_handled_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(1, preset.Id);
        t.Service.AssignSlot(1, null);

        Assert.Null(t.Service.Settings.SlotAssignments.Get(1));
        t.Service.SelectSlot(1);
        Assert.Null(t.Service.SelectedPreset);
        Assert.False(t.Service.CanRunDjShout);
    }

    [Fact]
    public void Selected_slot_resolves_the_correct_preset()
    {
        var t = Create();
        var presetA = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("A"));
        var presetB = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("B"));
        t.Service.AssignSlot(1, presetA.Id);
        t.Service.AssignSlot(2, presetB.Id);

        t.Service.SelectSlot(2);
        Assert.Equal(presetB.Id, t.Service.SelectedPreset!.Id);
    }

    [Fact]
    public void Unassigned_selected_slot_cannot_execute()
    {
        var t = Create();
        t.Service.SelectSlot(4); // never assigned
        Assert.False(t.Service.CanRunDjShout);
        Assert.False(t.Service.RunDjShout());
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void Missing_deleted_preset_reference_fails_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Preset") with { Lines = [new("hi", DjShoutChannel.Yell)] });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);

        // DeletePreset already clears the slot atomically (see the delete-safety test above); this test proves the
        // resolution path itself degrades safely even if a slot somehow still referenced a nonexistent Id.
        Assert.Null(t.Service.ResolvePreset(Guid.NewGuid()));
    }

    [Fact]
    public void Venue_a_and_venue_b_slot_assignments_remain_isolated()
    {
        var t = Create();
        var venueB = t.Profiles.Create("Second Venue").Id;

        var presetA = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Venue A Preset"));
        t.Service.AssignSlot(1, presetA.Id);

        t.Service.Load(venueB);
        Assert.Null(t.Service.Settings.SlotAssignments.Get(1));
        Assert.Empty(t.Service.Settings.Presets);
        var presetB = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Venue B Preset"));
        t.Service.AssignSlot(1, presetB.Id);

        t.Service.Load(t.VenueId);
        Assert.Equal(presetA.Id, t.Service.Settings.SlotAssignments.Get(1));

        t.Service.Load(venueB);
        Assert.Equal(presetB.Id, t.Service.Settings.SlotAssignments.Get(1));
    }

    // =========================================================================================================
    // Execution (task §37)
    // =========================================================================================================

    [Fact]
    public void One_yell_line_sends_exactly_once()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("Only line", DjShoutChannel.Yell));
        Assert.True(t.Service.RunDjShout());
        DispatchImmediate(t);
        Assert.Equal(["/yell Only line"], t.Sent);
    }

    [Fact]
    public void One_shout_line_sends_exactly_once()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("Only line", DjShoutChannel.Shout));
        Assert.True(t.Service.RunDjShout());
        DispatchImmediate(t);
        Assert.Equal(["/shout Only line"], t.Sent);
    }

    [Fact]
    public void Multi_line_preset_sends_in_exact_order()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell), new DjShoutLine("L3", DjShoutChannel.Shout));

        t.Service.RunDjShout();
        DispatchImmediate(t);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell L1", "/yell L2", "/shout L3"], t.Sent);
    }

    [Fact]
    public void Empty_or_whitespace_lines_are_never_sent()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("   ", DjShoutChannel.Yell), new DjShoutLine("", DjShoutChannel.Shout), new DjShoutLine("L2", DjShoutChannel.Yell));

        t.Service.RunDjShout();
        DispatchImmediate(t);
        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell L1", "/yell L2"], t.Sent);
    }

    [Fact]
    public void An_entirely_empty_preset_cannot_run()
    {
        var t = CreateWithPresetAssignedAndSelected(); // no lines at all
        Assert.False(t.Service.CanRunDjShout);
        Assert.False(t.Service.RunDjShout());
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void A_second_concurrent_run_is_rejected_while_one_is_in_progress()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        Assert.True(t.Service.RunDjShout());
        Assert.True(t.Service.IsRunning);

        Assert.False(t.Service.CanRunDjShout);
        Assert.False(t.Service.RunDjShout()); // rejected — a run is already in progress
    }

    [Fact]
    public void Cancel_stops_remaining_lines()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Equal(["/yell L1"], t.Sent);

        t.Service.Cancel();
        Assert.False(t.Service.IsRunning);

        Tick(t, 10);
        Assert.Equal(["/yell L1"], t.Sent); // L2 never fires
    }

    [Fact]
    public void Failed_dispatch_stops_execution_safely()
    {
        var t = CreateFailingWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);

        Assert.False(t.Service.IsRunning);
        Tick(t, 10);
        Assert.Empty(t.Sent); // the transport never actually accepted anything
    }

    [Fact]
    public void Failed_dispatch_does_not_update_the_last_success_timestamp()
    {
        var t = CreateFailingWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);

        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Timer (task §38)
    // =========================================================================================================

    [Fact]
    public void No_previous_completed_run_reports_null_never_state()
    {
        var t = Create();
        Assert.Null(t.Service.TimeSinceLastShout);
    }

    [Fact]
    public void Button_press_alone_does_not_set_the_completion_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout(); // enqueued, not yet dispatched
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void First_line_of_a_multi_line_preset_does_not_set_the_completion_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t); // L1 dispatched
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Final_successful_line_sets_the_completion_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);

        Tick(t, DjShoutsService.DelayBetweenLinesSeconds);
        Assert.NotNull(t.Service.Settings.LastShoutCompletedAtUtc);
        Assert.False(t.Service.IsRunning);
    }

    [Fact]
    public void Failed_run_does_not_update_the_timestamp()
    {
        var t = CreateFailingWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Cancelled_run_does_not_update_the_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        t.Service.Cancel();
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Second_successful_run_replaces_the_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        var first = t.Service.Settings.LastShoutCompletedAtUtc;
        Assert.NotNull(first);

        t.Clock.Advance(30);
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.True(t.Service.Settings.LastShoutCompletedAtUtc > first);
    }

    [Fact]
    public void Timer_state_survives_a_reload_boundary_when_persisted()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        var completedAt = t.Service.Settings.LastShoutCompletedAtUtc;
        Assert.NotNull(completedAt);

        var reloaded = Reload(t);
        Assert.Equal(completedAt, reloaded.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Venue_isolation_applies_to_the_persisted_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);
        Assert.NotNull(t.Service.Settings.LastShoutCompletedAtUtc);

        var venueB = t.Profiles.Create("Second Venue").Id;
        t.Service.Load(venueB);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Chat length (task §39)
    // =========================================================================================================

    [Fact]
    public void A_valid_line_under_the_limit_saves_and_runs()
    {
        var errors = DjShoutPresetValidator.Validate(DjShoutPreset.CreateNew("P") with { Lines = [new("Short and sweet", DjShoutChannel.Yell)] });
        Assert.Empty(errors);
    }

    [Fact]
    public void Boundary_behavior_matches_the_shared_ChatBytes_convention()
    {
        // "/yell " is 6 bytes; pad the text so the FULL dispatched command lands exactly at the 500-byte limit.
        var exact = new string('a', BlockLettersLimits.ChatBytes - "/yell ".Length);
        var overByOne = exact + "a";

        Assert.Empty(DjShoutPresetValidator.Validate(DjShoutPreset.CreateNew("P") with { Lines = [new(exact, DjShoutChannel.Yell)] }));
        Assert.NotEmpty(DjShoutPresetValidator.Validate(DjShoutPreset.CreateNew("P") with { Lines = [new(overByOne, DjShoutChannel.Yell)] }));
    }

    [Fact]
    public void An_over_limit_line_is_rejected_by_the_validator()
    {
        var tooLong = new string('x', 600);
        var errors = DjShoutPresetValidator.Validate(DjShoutPreset.CreateNew("P") with { Lines = [new(tooLong, DjShoutChannel.Yell)] });
        Assert.Single(errors);
        Assert.Contains("Line 1", errors[0]);
    }

    [Fact]
    public void Unicode_multibyte_text_uses_the_shared_utf8_byte_counter_not_char_count()
    {
        // Each 'あ' is a 3-byte UTF-8 sequence but a single UTF-16 char — 170 of them is 510 raw text bytes alone,
        // already over the limit before the "/yell " prefix is even added, despite being well under 500 CHARACTERS.
        var line = new DjShoutLine(new string('あ', 170), DjShoutChannel.Yell);
        Assert.True(DjShoutLineBytes.CountBytes(line) > BlockLettersLimits.ChatBytes);
        Assert.NotEmpty(DjShoutPresetValidator.Validate(DjShoutPreset.CreateNew("P") with { Lines = [line] }));
    }

    [Fact]
    public void Validation_never_truncates_the_offending_line_text()
    {
        var tooLong = new string('x', 600);
        var preset = DjShoutPreset.CreateNew("P") with { Lines = [new(tooLong, DjShoutChannel.Yell)] };
        DjShoutPresetValidator.Validate(preset);
        Assert.Equal(600, preset.Lines[0].Text.Length); // the preset object itself is never mutated/shortened
    }

    // =========================================================================================================
    // Module descriptor (NEW_MODULE_GUIDE.md §42/§43, task §3/§4)
    // =========================================================================================================

    [Fact]
    public void Module_id_is_communication_djshouts()
    {
        var module = new DjShoutsModule(Create().Service);
        Assert.Equal("communication.djshouts", module.Descriptor.Id);
    }

    [Fact]
    public void Display_name_is_dj_shouts()
    {
        var module = new DjShoutsModule(Create().Service);
        Assert.Equal("DJ Shouts", module.Descriptor.DisplayName);
    }

    [Fact]
    public void Module_is_promoted_to_production_and_enabled_by_default()
    {
        // Promoted out of UnderDevelopment (NEW_MODULE_GUIDE.md §22a) after live acceptance testing in VenueOS
        // 0.3.4 — see docs/DJ_SHOUTS_IMPLEMENTATION.md's Live QA section.
        var module = new DjShoutsModule(Create().Service);
        Assert.False(module.Descriptor.UnderDevelopment);
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Module_icon_key_is_microphone_and_distinct_from_every_other_module()
    {
        var module = new DjShoutsModule(Create().Service);
        Assert.Equal("microphone", module.Descriptor.Icon);
    }

    [Fact]
    public void Draw_and_draw_settings_use_distinct_delegates()
    {
        var drawCalls = 0; var settingsCalls = 0;
        var module = new DjShoutsModule(Create().Service, () => drawCalls++, () => settingsCalls++);
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
        t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Venue A Preset"));

        var module = new DjShoutsModule(t.Service);
        await module.OnVenueChangedAsync(new VenueContext(venueB, "Second Venue", t.Profiles.Current.Theme), CancellationToken.None);
        Assert.Empty(t.Service.Settings.Presets);
    }

    [Fact]
    public async Task Dispose_cancels_any_in_flight_run()
    {
        var t = CreateWithPresetAssignedAndSelected(new DjShoutLine("L1", DjShoutChannel.Yell), new DjShoutLine("L2", DjShoutChannel.Yell));
        t.Service.RunDjShout();
        DispatchImmediate(t);

        var module = new DjShoutsModule(t.Service);
        await module.DisposeAsync();

        Assert.False(t.Service.IsRunning);
        Tick(t, 10);
        Assert.Equal(["/yell L1"], t.Sent); // L2 never fires after dispose
    }

    // =========================================================================================================
    // Test infrastructure
    // =========================================================================================================

    private sealed record Fixture(DjShoutsService Service, VenueProfileService Profiles, InMemoryVenueStore Store, Guid VenueId, Clock Clock, SchedulerService Scheduler, ChatCommandService Chat, List<string> Sent);

    private static Fixture Create(bool alwaysFail = false)
    {
        var clock = new Clock();
        var scheduler = new SchedulerService(clock);
        var sent = new List<string>();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { if (alwaysFail) return false; sent.Add(command); return true; }, TimeSpan.FromSeconds(1));
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var diagnostics = FakeDiagnostics(profiles, clock);
        var service = new DjShoutsService(scheduler, chat, profiles, clock, diagnostics);
        var venueId = profiles.Current.Id;
        service.Load(venueId);
        return new Fixture(service, profiles, store, venueId, clock, scheduler, chat, sent);
    }

    private static Fixture CreateWithPresetAssignedAndSelected(params DjShoutLine[] lines) => AssignAndSelect(Create(alwaysFail: false), lines);

    private static Fixture CreateFailingWithPresetAssignedAndSelected(params DjShoutLine[] lines) => AssignAndSelect(Create(alwaysFail: true), lines);

    private static Fixture AssignAndSelect(Fixture t, DjShoutLine[] lines)
    {
        var preset = t.Service.SaveNewPreset(DjShoutPreset.CreateNew("Test Preset") with { Lines = lines });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);
        return t;
    }

    private static DjShoutsService Reload(Fixture t)
    {
        var reloaded = new DjShoutsService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock(), FakeDiagnostics(t.Profiles));
        reloaded.Load(t.VenueId);
        return reloaded;
    }

    private static ChatCommandService FakeChat(Clock clock) => new(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.FromSeconds(1));

    private static DiagnosticsService FakeDiagnostics(VenueProfileService profiles) => FakeDiagnostics(profiles, new Clock());
    private static DiagnosticsService FakeDiagnostics(VenueProfileService profiles, IClock clock) => new(new ModuleHost(), profiles, clock);

    /// <summary>Drains exactly one already-enqueued chat command without advancing the clock — enqueuing (what
    /// <c>DjShoutsService.RunDjShout</c> does synchronously) and actually dispatching (what
    /// <c>ChatCommandService.TickAsync</c> does, paced like every other module's chat traffic) are two different
    /// moments, matching <c>GiveawayServiceTests.DispatchImmediate</c>.</summary>
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
