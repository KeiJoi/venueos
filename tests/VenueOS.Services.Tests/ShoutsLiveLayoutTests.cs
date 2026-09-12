using VenueOS.Core;
using VenueOS.Modules.Operations.Shouts;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>
/// <see cref="ShoutsLiveLayout"/> — the 0.3.5 layout hotfix's pure row-wrapping logic. Before this fix, every
/// visible Shout slot was drawn on one unconditional row (<c>if (i > 0) ImGui.SameLine();</c> with no cap); this
/// verifies the fixed 5-per-row wrapping without needing an ImGui rendering context, per NEW_MODULE_GUIDE.md §30's
/// guidance to extract pure logic below the ImGui boundary specifically so it can be tested.
/// </summary>
public sealed class ShoutsLiveLayoutTests
{
    [Fact]
    public void Max_slots_per_row_is_five()
    {
        Assert.Equal(5, ShoutsLiveLayout.MaxSlotsPerRow);
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 5 })]
    [InlineData(6, new[] { 5, 1 })]
    [InlineData(9, new[] { 5, 4 })]
    [InlineData(10, new[] { 5, 5 })]
    [InlineData(11, new[] { 5, 5, 1 })]
    [InlineData(15, new[] { 5, 5, 5 })]
    public void Row_sizes_group_visible_slots_into_rows_of_at_most_five(int visibleCount, int[] expectedRows)
    {
        Assert.Equal(expectedRows, ShoutsLiveLayout.RowSizes(visibleCount));
    }

    [Fact]
    public void Zero_visible_slots_produces_no_rows()
    {
        Assert.Empty(ShoutsLiveLayout.RowSizes(0));
    }

    [Fact]
    public void Negative_count_is_treated_as_no_rows_rather_than_throwing()
    {
        Assert.Empty(ShoutsLiveLayout.RowSizes(-3));
    }

    [Theory]
    [InlineData(0, false)] // first item overall — never continues a (nonexistent) previous row
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)] // 5th item on the row (index 4) still continues it
    [InlineData(5, false)] // 6th item overall — starts row 2, no trailing SameLine after item 5
    [InlineData(6, true)]
    [InlineData(9, true)] // 10th item overall — still row 2
    [InlineData(10, false)] // 11th item overall — starts row 3, no trailing SameLine after item 10
    [InlineData(14, true)] // 15th item overall — still row 3
    public void Continues_row_never_places_a_sameline_at_the_start_of_a_new_row(int index, bool expectedContinuesRow)
    {
        Assert.Equal(expectedContinuesRow, ShoutsLiveLayout.ContinuesRow(index));
    }

    [Fact]
    public void No_sameline_immediately_after_an_exact_multiple_of_five()
    {
        // Items at zero-based indices 5, 10 (the 6th and 11th items overall) must start fresh rows — verifying the
        // task's explicit "no SameLine after items 5, 10, 15" requirement (index 15 would be the 16th item, past
        // the 15-slot maximum, so only 5 and 10 are reachable in practice).
        Assert.False(ShoutsLiveLayout.ContinuesRow(5));
        Assert.False(ShoutsLiveLayout.ContinuesRow(10));
    }

    [Fact]
    public void A_custom_max_per_row_is_honored()
    {
        Assert.Equal([3, 3, 1], ShoutsLiveLayout.RowSizes(7, maxPerRow: 3));
        Assert.False(ShoutsLiveLayout.ContinuesRow(3, maxPerRow: 3));
        Assert.True(ShoutsLiveLayout.ContinuesRow(2, maxPerRow: 3));
    }

    // =========================================================================================================
    // Integration with ShoutsService.VisibleSlots — hidden slots never consume a grid position, and the already-
    // correct ascending slot-number order is what gets grouped into rows, never resorted or renumbered.
    // =========================================================================================================

    [Fact]
    public void A_non_contiguous_seven_slot_configuration_groups_into_five_then_two()
    {
        var service = CreateServiceWithSlotsAssigned(1, 3, 4, 7, 8, 12, 15);

        var visibleSlotNumbers = service.VisibleSlots.Select(v => v.Slot).ToArray();
        Assert.Equal([1, 3, 4, 7, 8, 12, 15], visibleSlotNumbers); // ascending logical order, never resorted

        var rows = ShoutsLiveLayout.RowSizes(service.VisibleSlots.Count);
        Assert.Equal([5, 2], rows);

        // Row membership by actual slot number, matching the task's worked example exactly:
        var row1 = visibleSlotNumbers.Take(5).ToArray();
        var row2 = visibleSlotNumbers.Skip(5).ToArray();
        Assert.Equal([1, 3, 4, 7, 8], row1);
        Assert.Equal([12, 15], row2);
    }

    [Fact]
    public void Unassigned_slots_between_visible_ones_never_consume_a_grid_position()
    {
        // Slots 2, 5, 6, 9-11, 13-14 are deliberately left unassigned — VisibleSlots (and therefore the row
        // grouping) must count only the 5 actually-assigned ones, never reserve a blank column for the gaps.
        var service = CreateServiceWithSlotsAssigned(1, 3, 4, 7, 8);

        Assert.Equal(5, service.VisibleSlots.Count);
        Assert.Equal([5], ShoutsLiveLayout.RowSizes(service.VisibleSlots.Count));
    }

    /// <summary>A minimal, self-contained fixture (deliberately not shared with <see cref="ShoutsServiceTests"/>'s
    /// own private fixture) — one preset assigned to every requested slot number, enough to exercise
    /// <see cref="ShoutsService.VisibleSlots"/>'s ordering/count for this layout-focused test class.</summary>
    private static ShoutsService CreateServiceWithSlotsAssigned(params int[] slots)
    {
        var clock = new Clock();
        var scheduler = new SchedulerService(clock);
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.FromSeconds(1));
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, clock);
        var service = new ShoutsService(scheduler, chat, profiles, clock, diagnostics);
        service.Load(profiles.Current.Id);

        var preset = service.SaveNewPreset(ShoutPreset.CreateNew("Preset"));
        foreach (var slot in slots) service.AssignSlot(slot, preset.Id);
        return service;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }
}
