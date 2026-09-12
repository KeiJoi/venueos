using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Modules.Operations.Shouts;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>
/// <see cref="ShoutsService"/>: preset persistence/isolation, slot assignment/selection across all 15 slots,
/// live-visibility filtering/ordering (only configured slots are ever "visible"), selection fallback when the
/// selected slot becomes unassigned/deleted, execution/pacing/dispatch-confirmation gating, the Last Shout timer's
/// completion-only semantics and persistence, chat-byte-limit validation, the schema v1→v2 (5→15 slot) migration
/// from the original DJ Shouts release, and the <see cref="ShoutsModule"/> descriptor. Drives
/// <see cref="SchedulerService"/>/<see cref="ChatCommandService"/> with a controllable <see cref="Clock"/>, ticking
/// both every simulated second.
/// </summary>
public sealed class ShoutsServiceTests
{
    // =========================================================================================================
    // Preset model / persistence
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
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Friday Night"));

        var reloaded = Reload(t);
        Assert.Contains(reloaded.Settings.Presets, x => x.Id == preset.Id && x.Name == "Friday Night");
    }

    [Fact]
    public void Stable_id_is_generated_and_distinct_from_the_drafts_own_id()
    {
        var t = Create();
        var draft = ShoutPreset.CreateNew("Whatever");
        var saved = t.Service.SaveNewPreset(draft);
        Assert.NotEqual(draft.Id, saved.Id); // SaveNewPreset always mints a fresh Id
        Assert.NotEqual(Guid.Empty, saved.Id);
    }

    [Fact]
    public void Default_new_line_channel_is_yell()
    {
        Assert.Equal(ShoutChannel.Yell, ShoutLine.Empty().Channel);
    }

    [Fact]
    public void Edit_preserves_stable_id()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Original"));
        t.Service.UpdatePreset(preset.Id, p => p with { Name = "Renamed" });

        var updated = t.Service.Settings.Presets.Single();
        Assert.Equal(preset.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
    }

    [Fact]
    public void Cancel_new_never_calls_into_the_service_and_persists_nothing()
    {
        var t = Create();
        var draft = ShoutPreset.CreateNew("Would Have Been New") with { Lines = [new("hello", ShoutChannel.Yell)] };
        // "Cancel" is simply never calling SaveNewPreset — there is nothing else to undo.
        Assert.Empty(t.Service.Settings.Presets);
        Assert.DoesNotContain(t.Service.Settings.Presets, p => p.Name == draft.Name);
    }

    [Fact]
    public void Cancel_edit_leaves_the_persisted_preset_completely_unchanged()
    {
        var t = Create();
        var original = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Original"));
        var draft = original with { Name = "Changed In Draft Only" };
        // Cancel: draft discarded, no call into the service.
        var stillPersisted = t.Service.Settings.Presets.Single(p => p.Id == original.Id);
        Assert.Equal("Original", stillPersisted.Name);
    }

    [Fact]
    public void Delete_removes_the_preset()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Temporary"));
        Assert.True(t.Service.DeletePreset(preset.Id));
        Assert.Empty(t.Service.Settings.Presets);
    }

    [Fact]
    public void Delete_of_a_preset_assigned_to_a_slot_clears_that_slot_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Assigned"));
        t.Service.AssignSlot(3, preset.Id);

        t.Service.DeletePreset(preset.Id);

        Assert.Null(t.Service.Settings.SlotAssignments.Get(3));
        Assert.Null(t.Service.SelectedPreset); // slot 3 no longer resolves to a missing/dangling Id
    }

    [Fact]
    public void Delete_clears_the_preset_from_any_of_the_15_slots_it_was_assigned_to()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Everywhere"));
        foreach (var slot in new[] { 1, 6, 10, 15 }) t.Service.AssignSlot(slot, preset.Id);

        t.Service.DeletePreset(preset.Id);

        foreach (var slot in new[] { 1, 6, 10, 15 }) Assert.Null(t.Service.Settings.SlotAssignments.Get(slot));
    }

    [Fact]
    public void Multiple_presets_persist_and_reload()
    {
        var t = Create();
        t.Service.SaveNewPreset(ShoutPreset.CreateNew("First"));
        t.Service.SaveNewPreset(ShoutPreset.CreateNew("Second"));
        t.Service.SaveNewPreset(ShoutPreset.CreateNew("Third"));

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
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Friday Night Main DJ") with
        {
            Lines = [new("Come dance!", ShoutChannel.Yell), new("Requests open!", ShoutChannel.Shout)],
        });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);
        t.Service.RunShout();
        DispatchImmediate(t);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        DispatchImmediate(t);

        var rebuiltSnapshot = JsonSerializer.Deserialize<VenueStoreSnapshot>(JsonSerializer.Serialize(t.Store.Read()))!;
        var freshProfiles = new VenueProfileService(new InMemoryVenueStore(rebuiltSnapshot), new ModuleHost());
        var fresh = new ShoutsService(t.Scheduler, t.Chat, freshProfiles, t.Clock, FakeDiagnostics(freshProfiles));
        fresh.Load(t.VenueId);

        var reloadedPreset = fresh.Settings.Presets.Single();
        Assert.Equal("Friday Night Main DJ", reloadedPreset.Name);
        Assert.Equal(2, reloadedPreset.Lines.Count);
        Assert.Equal("Come dance!", reloadedPreset.Lines[0].Text);
        Assert.Equal(ShoutChannel.Yell, reloadedPreset.Lines[0].Channel);
        Assert.Equal("Requests open!", reloadedPreset.Lines[1].Text);
        Assert.Equal(ShoutChannel.Shout, reloadedPreset.Lines[1].Channel);
        Assert.Equal(preset.Id, fresh.Settings.SlotAssignments.Get(1));
        Assert.Equal(1, fresh.Settings.SelectedSlot);
        Assert.NotNull(fresh.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Line channels
    // =========================================================================================================

    [Fact]
    public void Yell_line_generates_slash_yell()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("Hello", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.Equal(["/yell Hello"], t.Sent);
    }

    [Fact]
    public void Shout_line_generates_slash_shout()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("Hello", ShoutChannel.Shout));
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.Equal(["/shout Hello"], t.Sent);
    }

    [Fact]
    public void Mixed_preset_preserves_line_order_yell_shout_yell()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new ShoutLine("One", ShoutChannel.Yell),
            new ShoutLine("Two", ShoutChannel.Shout),
            new ShoutLine("Three", ShoutChannel.Yell));

        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.Equal(["/yell One"], t.Sent);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell One", "/shout Two"], t.Sent);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell One", "/shout Two", "/yell Three"], t.Sent);
    }

    [Fact]
    public void Channel_survives_save_and_reload()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset") with
        {
            Lines = [new("Shout line", ShoutChannel.Shout)],
        });

        var reloaded = Reload(t);
        Assert.Equal(ShoutChannel.Shout, reloaded.Settings.Presets.Single(p => p.Id == preset.Id).Lines.Single().Channel);
    }

    // =========================================================================================================
    // Slots — all 15
    // =========================================================================================================

    [Fact]
    public void Exactly_fifteen_logical_slots_are_available()
    {
        Assert.Equal(15, ShoutSlotAssignments.SlotCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(15)]
    public void Any_of_the_fifteen_slots_assigns_and_persists_independently(int slot)
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(slot, preset.Id);

        var reloaded = Reload(t);
        Assert.Equal(preset.Id, reloaded.Settings.SlotAssignments.Get(slot));
    }

    [Fact]
    public void Assigning_one_slot_does_not_modify_any_other_slot()
    {
        var t = Create();
        var presetA = t.Service.SaveNewPreset(ShoutPreset.CreateNew("A"));
        var presetB = t.Service.SaveNewPreset(ShoutPreset.CreateNew("B"));
        t.Service.AssignSlot(1, presetA.Id);
        t.Service.AssignSlot(15, presetB.Id);

        Assert.Equal(presetA.Id, t.Service.Settings.SlotAssignments.Get(1));
        Assert.Equal(presetB.Id, t.Service.Settings.SlotAssignments.Get(15));
        for (var slot = 2; slot <= 14; slot++) Assert.Null(t.Service.Settings.SlotAssignments.Get(slot));
    }

    [Fact]
    public void None_assignment_is_handled_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(1, preset.Id);
        t.Service.AssignSlot(1, null);

        Assert.Null(t.Service.Settings.SlotAssignments.Get(1));
        t.Service.SelectSlot(1);
        Assert.Null(t.Service.SelectedPreset);
        Assert.False(t.Service.CanRunShout);
    }

    [Fact]
    public void Selected_slot_resolves_the_correct_preset()
    {
        var t = Create();
        var presetA = t.Service.SaveNewPreset(ShoutPreset.CreateNew("A"));
        var presetB = t.Service.SaveNewPreset(ShoutPreset.CreateNew("B"));
        t.Service.AssignSlot(1, presetA.Id);
        t.Service.AssignSlot(2, presetB.Id);

        t.Service.SelectSlot(2);
        Assert.Equal(presetB.Id, t.Service.SelectedPreset!.Id);
    }

    [Fact]
    public void Unassigned_selected_slot_cannot_execute()
    {
        var t = Create();
        t.Service.SelectSlot(4); // never assigned — SelectSlot no-ops
        Assert.False(t.Service.CanRunShout);
        Assert.False(t.Service.RunShout());
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void An_unassigned_slot_cannot_become_selected()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(1, preset.Id); // SelectedSlot defaults to 1 and is already valid

        t.Service.SelectSlot(7); // slot 7 has no assignment
        Assert.Equal(1, t.Service.Settings.SelectedSlot); // selection unchanged
    }

    [Fact]
    public void Missing_deleted_preset_reference_fails_safely()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset") with { Lines = [new("hi", ShoutChannel.Yell)] });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);

        // DeletePreset already clears the slot atomically (see the delete-safety test above); this test proves the
        // resolution path itself degrades safely even if a slot somehow still referenced a nonexistent Id.
        Assert.Null(t.Service.ResolvePreset(Guid.NewGuid()));
    }

    [Fact]
    public void Venue_a_and_venue_b_slot_assignments_remain_isolated_across_all_fifteen_slots()
    {
        var t = Create();
        var venueB = t.Profiles.Create("Second Venue").Id;

        var presetA = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Venue A Preset"));
        t.Service.AssignSlot(1, presetA.Id);
        t.Service.AssignSlot(15, presetA.Id);

        t.Service.Load(venueB);
        Assert.Null(t.Service.Settings.SlotAssignments.Get(1));
        Assert.Null(t.Service.Settings.SlotAssignments.Get(15));
        Assert.Empty(t.Service.Settings.Presets);
        var presetB = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Venue B Preset"));
        t.Service.AssignSlot(1, presetB.Id);

        t.Service.Load(t.VenueId);
        Assert.Equal(presetA.Id, t.Service.Settings.SlotAssignments.Get(1));
        Assert.Equal(presetA.Id, t.Service.Settings.SlotAssignments.Get(15));

        t.Service.Load(venueB);
        Assert.Equal(presetB.Id, t.Service.Settings.SlotAssignments.Get(1));
    }

    // =========================================================================================================
    // Live visibility — only configured slots are ever visible, in slot-number order, never renumbered
    // =========================================================================================================

    [Fact]
    public void No_assignments_means_zero_visible_slots()
    {
        var t = Create();
        Assert.Empty(t.Service.VisibleSlots);
    }

    [Fact]
    public void Assigning_slot_one_makes_only_slot_one_visible()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(1, preset.Id);

        var visible = t.Service.VisibleSlots;
        Assert.Single(visible);
        Assert.Equal(1, visible[0].Slot);
    }

    [Fact]
    public void Slots_one_four_seven_twelve_are_exactly_the_four_visible_in_numeric_order()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        foreach (var slot in new[] { 1, 4, 7, 12 }) t.Service.AssignSlot(slot, preset.Id);

        var visible = t.Service.VisibleSlots.Select(x => x.Slot).ToArray();
        Assert.Equal([1, 4, 7, 12], visible); // never renumbered to 1,2,3,4
    }

    [Fact]
    public void Unassigning_a_visible_slot_removes_it_from_visible_slots()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        foreach (var slot in new[] { 1, 4, 7, 12 }) t.Service.AssignSlot(slot, preset.Id);

        t.Service.AssignSlot(7, null);

        Assert.Equal([1, 4, 12], t.Service.VisibleSlots.Select(x => x.Slot).ToArray());
    }

    [Fact]
    public void Assigning_slot_fifteen_inserts_it_in_proper_numeric_order()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        foreach (var slot in new[] { 1, 4, 7, 12 }) t.Service.AssignSlot(slot, preset.Id);

        t.Service.AssignSlot(15, preset.Id);

        Assert.Equal([1, 4, 7, 12, 15], t.Service.VisibleSlots.Select(x => x.Slot).ToArray());
    }

    [Fact]
    public void Deleting_the_assigned_preset_immediately_stops_that_slot_being_visible()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(3, preset.Id);
        Assert.Single(t.Service.VisibleSlots);

        t.Service.DeletePreset(preset.Id);
        Assert.Empty(t.Service.VisibleSlots);
    }

    // =========================================================================================================
    // Selection fallback — a hidden slot can never remain selected
    // =========================================================================================================

    [Fact]
    public void Unassigning_the_selected_slot_falls_back_to_the_first_remaining_visible_slot()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        t.Service.AssignSlot(4, preset.Id);
        t.Service.AssignSlot(9, preset.Id);
        t.Service.SelectSlot(4);

        t.Service.AssignSlot(4, null); // the selected slot becomes unassigned

        Assert.Equal(9, t.Service.Settings.SelectedSlot);
        Assert.NotNull(t.Service.SelectedPreset);
    }

    [Fact]
    public void Deleting_the_selected_presets_only_assignment_clears_selection_to_a_safe_state()
    {
        var t = Create();
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Only One"));
        t.Service.AssignSlot(5, preset.Id);
        t.Service.SelectSlot(5);

        t.Service.DeletePreset(preset.Id); // no visible slots remain at all

        Assert.Empty(t.Service.VisibleSlots);
        Assert.Null(t.Service.SelectedPreset); // fails safely: nothing resolves, nothing runnable
        Assert.False(t.Service.CanRunShout);
    }

    // =========================================================================================================
    // Execution
    // =========================================================================================================

    [Fact]
    public void One_yell_line_sends_exactly_once()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("Only line", ShoutChannel.Yell));
        Assert.True(t.Service.RunShout());
        DispatchImmediate(t);
        Assert.Equal(["/yell Only line"], t.Sent);
    }

    [Fact]
    public void One_shout_line_sends_exactly_once()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("Only line", ShoutChannel.Shout));
        Assert.True(t.Service.RunShout());
        DispatchImmediate(t);
        Assert.Equal(["/shout Only line"], t.Sent);
    }

    [Fact]
    public void Multi_line_preset_sends_in_exact_order()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell), new ShoutLine("L3", ShoutChannel.Shout));

        t.Service.RunShout();
        DispatchImmediate(t);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell L1", "/yell L2", "/shout L3"], t.Sent);
    }

    [Fact]
    public void Empty_or_whitespace_lines_are_never_sent()
    {
        var t = CreateWithPresetAssignedAndSelected(
            new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("   ", ShoutChannel.Yell), new ShoutLine("", ShoutChannel.Shout), new ShoutLine("L2", ShoutChannel.Yell));

        t.Service.RunShout();
        DispatchImmediate(t);
        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Assert.Equal(["/yell L1", "/yell L2"], t.Sent);
    }

    [Fact]
    public void An_entirely_empty_preset_cannot_run()
    {
        var t = CreateWithPresetAssignedAndSelected(); // no lines at all
        Assert.False(t.Service.CanRunShout);
        Assert.False(t.Service.RunShout());
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void A_second_concurrent_run_is_rejected_while_one_is_in_progress()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        Assert.True(t.Service.RunShout());
        Assert.True(t.Service.IsRunning);

        Assert.False(t.Service.CanRunShout);
        Assert.False(t.Service.RunShout()); // rejected — a run is already in progress
    }

    [Fact]
    public void Cancel_stops_remaining_lines()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
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
        var t = CreateFailingWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);

        Assert.False(t.Service.IsRunning);
        Tick(t, 10);
        Assert.Empty(t.Sent); // the transport never actually accepted anything
    }

    [Fact]
    public void Failed_dispatch_does_not_update_the_last_success_timestamp()
    {
        var t = CreateFailingWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);

        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Timer
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
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout(); // enqueued, not yet dispatched
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void First_line_of_a_multi_line_preset_does_not_set_the_completion_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t); // L1 dispatched
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Final_successful_line_sets_the_completion_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);

        Tick(t, ShoutsService.DelayBetweenLinesSeconds);
        Assert.NotNull(t.Service.Settings.LastShoutCompletedAtUtc);
        Assert.False(t.Service.IsRunning);
    }

    [Fact]
    public void Failed_run_does_not_update_the_timestamp()
    {
        var t = CreateFailingWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Cancelled_run_does_not_update_the_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        t.Service.Cancel();
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Second_successful_run_replaces_the_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        var first = t.Service.Settings.LastShoutCompletedAtUtc;
        Assert.NotNull(first);

        t.Clock.Advance(30);
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.True(t.Service.Settings.LastShoutCompletedAtUtc > first);
    }

    [Fact]
    public void Timer_state_survives_a_reload_boundary_when_persisted()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        var completedAt = t.Service.Settings.LastShoutCompletedAtUtc;
        Assert.NotNull(completedAt);

        var reloaded = Reload(t);
        Assert.Equal(completedAt, reloaded.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Venue_isolation_applies_to_the_persisted_timestamp()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);
        Assert.NotNull(t.Service.Settings.LastShoutCompletedAtUtc);

        var venueB = t.Profiles.Create("Second Venue").Id;
        t.Service.Load(venueB);
        Assert.Null(t.Service.Settings.LastShoutCompletedAtUtc);
    }

    // =========================================================================================================
    // Chat length
    // =========================================================================================================

    [Fact]
    public void A_valid_line_under_the_limit_saves_and_runs()
    {
        var errors = ShoutPresetValidator.Validate(ShoutPreset.CreateNew("P") with { Lines = [new("Short and sweet", ShoutChannel.Yell)] });
        Assert.Empty(errors);
    }

    [Fact]
    public void Boundary_behavior_matches_the_shared_ChatBytes_convention()
    {
        // "/yell " is 6 bytes; pad the text so the FULL dispatched command lands exactly at the 500-byte limit.
        var exact = new string('a', BlockLettersLimits.ChatBytes - "/yell ".Length);
        var overByOne = exact + "a";

        Assert.Empty(ShoutPresetValidator.Validate(ShoutPreset.CreateNew("P") with { Lines = [new(exact, ShoutChannel.Yell)] }));
        Assert.NotEmpty(ShoutPresetValidator.Validate(ShoutPreset.CreateNew("P") with { Lines = [new(overByOne, ShoutChannel.Yell)] }));
    }

    [Fact]
    public void An_over_limit_line_is_rejected_by_the_validator()
    {
        var tooLong = new string('x', 600);
        var errors = ShoutPresetValidator.Validate(ShoutPreset.CreateNew("P") with { Lines = [new(tooLong, ShoutChannel.Yell)] });
        Assert.Single(errors);
        Assert.Contains("Line 1", errors[0]);
    }

    [Fact]
    public void Unicode_multibyte_text_uses_the_shared_utf8_byte_counter_not_char_count()
    {
        var line = new ShoutLine(new string('あ', 170), ShoutChannel.Yell);
        Assert.True(ShoutLineBytes.CountBytes(line) > BlockLettersLimits.ChatBytes);
        Assert.NotEmpty(ShoutPresetValidator.Validate(ShoutPreset.CreateNew("P") with { Lines = [line] }));
    }

    [Fact]
    public void Validation_never_truncates_the_offending_line_text()
    {
        var tooLong = new string('x', 600);
        var preset = ShoutPreset.CreateNew("P") with { Lines = [new(tooLong, ShoutChannel.Yell)] };
        ShoutPresetValidator.Validate(preset);
        Assert.Equal(600, preset.Lines[0].Text.Length); // the preset object itself is never mutated/shortened
    }

    // =========================================================================================================
    // Migration — DJ Shouts 0.3.4 (schema v1, 5 named slots) → Shouts (schema v2, 15 slots)
    // =========================================================================================================

    [Fact]
    public void Existing_0_3_4_config_with_five_slots_loads()
    {
        var (profiles, venueId, _) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        Assert.Equal(2, service.Settings.Presets.Count);
    }

    [Fact]
    public void Old_slot_one_assignment_becomes_new_shout_one()
    {
        var (profiles, venueId, presets) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        Assert.Equal(presets[0].Id, service.Settings.SlotAssignments.Get(1));
    }

    [Fact]
    public void Old_slot_five_assignment_becomes_new_shout_five()
    {
        var (profiles, venueId, presets) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        Assert.Equal(presets[1].Id, service.Settings.SlotAssignments.Get(5));
    }

    [Fact]
    public void New_slots_six_through_fifteen_default_to_none_after_migration()
    {
        var (profiles, venueId, _) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        for (var slot = 6; slot <= 15; slot++) Assert.Null(service.Settings.SlotAssignments.Get(slot));
    }

    [Fact]
    public void Existing_presets_survive_migration()
    {
        var (profiles, venueId, presets) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        Assert.Contains(service.Settings.Presets, p => p.Id == presets[0].Id && p.Name == "Friday DJ");
        Assert.Contains(service.Settings.Presets, p => p.Id == presets[1].Id && p.Name == "Closing");
    }

    [Fact]
    public void Existing_last_dj_shout_timestamp_survives_migration()
    {
        var (profiles, venueId, _) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId);

        Assert.NotNull(service.Settings.LastShoutCompletedAtUtc);
    }

    [Fact]
    public void Legacy_per_venue_isolation_survives_migration()
    {
        var (profiles, venueId, presets) = SeedLegacyConfig();
        var venueB = profiles.Create("Second Venue").Id; // never had legacy config

        var service = NewService(profiles);
        service.Load(venueB);
        Assert.Empty(service.Settings.Presets);
        Assert.Null(service.Settings.SlotAssignments.Get(1));

        service.Load(venueId);
        Assert.Equal(presets[0].Id, service.Settings.SlotAssignments.Get(1));
    }

    [Fact]
    public void Migration_is_idempotent_reload_after_migration_does_not_duplicate_presets()
    {
        var (profiles, venueId, _) = SeedLegacyConfig();
        var service = NewService(profiles);
        service.Load(venueId); // first load — migrates
        Assert.Equal(2, service.Settings.Presets.Count);

        // Reload from scratch (a fresh service instance, as a plugin restart would produce) — must read the
        // already-migrated v2 payload, never re-read/re-migrate the v1 payload again.
        var again = NewService(profiles);
        again.Load(venueId);
        Assert.Equal(2, again.Settings.Presets.Count);

        // And a mutation made after the first migration must survive a THIRD load untouched by any re-migration.
        again.AssignSlot(6, again.Settings.Presets[0].Id);
        var third = NewService(profiles);
        third.Load(venueId);
        Assert.Equal(2, third.Settings.Presets.Count);
        Assert.Equal(again.Settings.Presets[0].Id, third.Settings.SlotAssignments.Get(6));
    }

    [Fact]
    public void A_brand_new_venue_with_no_legacy_payload_gets_plain_defaults()
    {
        var t = Create(); // fresh store — no v1 payload was ever seeded
        Assert.Empty(t.Service.Settings.Presets);
        Assert.Equal(15, Enumerable.Range(1, ShoutSlotAssignments.SlotCount).Count(slot => t.Service.Settings.SlotAssignments.Get(slot) is null));
    }

    /// <summary>Writes a schema-v1 (5-slot) payload directly through <see cref="VenueProfileService.SaveModuleConfig{T}"/>,
    /// exactly as the original DJ Shouts 0.3.4 release would have — the same module ID, schema version 1, and the
    /// legacy 5-named-slot shape — simulating an existing user's pre-upgrade configuration.</summary>
    private static (VenueProfileService Profiles, Guid VenueId, ShoutPreset[] Presets) SeedLegacyConfig()
    {
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var venueId = profiles.Current.Id;

        var presetA = new ShoutPreset(Guid.NewGuid(), "Friday DJ", [new ShoutLine("Come dance!", ShoutChannel.Yell)]);
        var presetB = new ShoutPreset(Guid.NewGuid(), "Closing", [new ShoutLine("Thanks for coming!", ShoutChannel.Shout)]);
        var legacy = new LegacyShoutsSettingsV1(
            [presetA, presetB],
            new LegacyShoutSlotAssignmentsV1(presetA.Id, null, null, null, presetB.Id),
            SelectedSlot: 1,
            LastShoutCompletedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(5));

        profiles.SaveModuleConfig(venueId, ShoutsService.ModuleId, 1, legacy);
        return (profiles, venueId, [presetA, presetB]);
    }

    private static ShoutsService NewService(VenueProfileService profiles)
    {
        var clock = new Clock();
        return new ShoutsService(new SchedulerService(clock), FakeChat(clock), profiles, clock, FakeDiagnostics(profiles, clock));
    }

    // =========================================================================================================
    // Module descriptor
    // =========================================================================================================

    [Fact]
    public void Module_id_is_unchanged_from_the_original_dj_shouts_release()
    {
        var module = new ShoutsModule(Create().Service);
        Assert.Equal("communication.djshouts", module.Descriptor.Id);
    }

    [Fact]
    public void Display_name_is_shouts()
    {
        var module = new ShoutsModule(Create().Service);
        Assert.Equal("Shouts", module.Descriptor.DisplayName);
    }

    [Fact]
    public void Module_is_production_and_enabled_by_default()
    {
        var module = new ShoutsModule(Create().Service);
        Assert.False(module.Descriptor.UnderDevelopment);
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Module_icon_key_is_still_microphone()
    {
        var module = new ShoutsModule(Create().Service);
        Assert.Equal("microphone", module.Descriptor.Icon);
    }

    [Fact]
    public void Draw_and_draw_settings_use_distinct_delegates()
    {
        var drawCalls = 0; var settingsCalls = 0;
        var module = new ShoutsModule(Create().Service, () => drawCalls++, () => settingsCalls++);
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
        t.Service.SaveNewPreset(ShoutPreset.CreateNew("Venue A Preset"));

        var module = new ShoutsModule(t.Service);
        await module.OnVenueChangedAsync(new VenueContext(venueB, "Second Venue", t.Profiles.Current.Theme), CancellationToken.None);
        Assert.Empty(t.Service.Settings.Presets);
    }

    [Fact]
    public async Task Dispose_cancels_any_in_flight_run()
    {
        var t = CreateWithPresetAssignedAndSelected(new ShoutLine("L1", ShoutChannel.Yell), new ShoutLine("L2", ShoutChannel.Yell));
        t.Service.RunShout();
        DispatchImmediate(t);

        var module = new ShoutsModule(t.Service);
        await module.DisposeAsync();

        Assert.False(t.Service.IsRunning);
        Tick(t, 10);
        Assert.Equal(["/yell L1"], t.Sent); // L2 never fires after dispose
    }

    // =========================================================================================================
    // Test infrastructure
    // =========================================================================================================

    private sealed record Fixture(ShoutsService Service, VenueProfileService Profiles, InMemoryVenueStore Store, Guid VenueId, Clock Clock, SchedulerService Scheduler, ChatCommandService Chat, List<string> Sent);

    private static Fixture Create(bool alwaysFail = false)
    {
        var clock = new Clock();
        var scheduler = new SchedulerService(clock);
        var sent = new List<string>();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { if (alwaysFail) return false; sent.Add(command); return true; }, TimeSpan.FromSeconds(1));
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var diagnostics = FakeDiagnostics(profiles, clock);
        var service = new ShoutsService(scheduler, chat, profiles, clock, diagnostics);
        var venueId = profiles.Current.Id;
        service.Load(venueId);
        return new Fixture(service, profiles, store, venueId, clock, scheduler, chat, sent);
    }

    private static Fixture CreateWithPresetAssignedAndSelected(params ShoutLine[] lines) => AssignAndSelect(Create(alwaysFail: false), lines);

    private static Fixture CreateFailingWithPresetAssignedAndSelected(params ShoutLine[] lines) => AssignAndSelect(Create(alwaysFail: true), lines);

    private static Fixture AssignAndSelect(Fixture t, ShoutLine[] lines)
    {
        var preset = t.Service.SaveNewPreset(ShoutPreset.CreateNew("Test Preset") with { Lines = lines });
        t.Service.AssignSlot(1, preset.Id);
        t.Service.SelectSlot(1);
        return t;
    }

    private static ShoutsService Reload(Fixture t)
    {
        var reloaded = new ShoutsService(new SchedulerService(new Clock()), FakeChat(new Clock()), t.Profiles, new Clock(), FakeDiagnostics(t.Profiles));
        reloaded.Load(t.VenueId);
        return reloaded;
    }

    private static ChatCommandService FakeChat(Clock clock) => new(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.FromSeconds(1));

    private static DiagnosticsService FakeDiagnostics(VenueProfileService profiles) => FakeDiagnostics(profiles, new Clock());
    private static DiagnosticsService FakeDiagnostics(VenueProfileService profiles, IClock clock) => new(new ModuleHost(), profiles, clock);

    /// <summary>Drains exactly one already-enqueued chat command without advancing the clock — enqueuing (what
    /// <c>ShoutsService.RunShout</c> does synchronously) and actually dispatching (what
    /// <c>ChatCommandService.TickAsync</c> does, paced like every other module's chat traffic) are two different
    /// moments.</summary>
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
