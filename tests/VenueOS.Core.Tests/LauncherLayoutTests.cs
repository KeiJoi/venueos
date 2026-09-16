using VenueOS.Core;

namespace VenueOS.Core.Tests;

public sealed class LauncherLayoutTests
{
    // ---- ContinuesRow ---------------------------------------------------------------------------------------------

    [Fact] public void Index_zero_never_continues_a_row() => Assert.False(LauncherLayout.ContinuesRow(0, 5));
    [Fact] public void An_index_mid_row_continues_it() => Assert.True(LauncherLayout.ContinuesRow(1, 5));
    [Fact] public void An_index_that_starts_a_new_row_does_not_continue() => Assert.False(LauncherLayout.ContinuesRow(5, 5));
    [Fact] public void A_buttons_per_row_of_one_means_every_index_starts_a_new_row()
    {
        Assert.False(LauncherLayout.ContinuesRow(0, 1));
        Assert.False(LauncherLayout.ContinuesRow(1, 1));
        Assert.False(LauncherLayout.ContinuesRow(2, 1));
    }
    [Fact] public void A_non_positive_buttons_per_row_is_treated_as_one() => Assert.Equal(LauncherLayout.ContinuesRow(1, 1), LauncherLayout.ContinuesRow(1, 0));

    // ---- RowSizes ---------------------------------------------------------------------------------------------------

    [Fact] public void One_button_per_row_produces_a_single_column() => Assert.Equal([1, 1, 1], LauncherLayout.RowSizes(3, 1));
    [Fact] public void A_mid_value_wraps_into_multiple_rows() => Assert.Equal([5, 1], LauncherLayout.RowSizes(6, 5));
    [Fact] public void A_buttons_per_row_greater_than_or_equal_to_count_produces_one_row() => Assert.Equal([6], LauncherLayout.RowSizes(6, 10));
    [Fact] public void An_exact_multiple_produces_no_trailing_empty_row() => Assert.Equal([5, 5], LauncherLayout.RowSizes(10, 5));
    [Fact] public void Zero_or_negative_count_produces_no_rows()
    {
        Assert.Empty(LauncherLayout.RowSizes(0, 5));
        Assert.Empty(LauncherLayout.RowSizes(-1, 5));
    }

    // ---- LooksOffscreen -------------------------------------------------------------------------------------------

    [Fact] public void A_typical_on_screen_position_does_not_look_offscreen() => Assert.False(LauncherLayout.LooksOffscreen(24, 24));
    [Fact] public void NaN_looks_offscreen() => Assert.True(LauncherLayout.LooksOffscreen(float.NaN, 0));
    [Fact] public void Infinity_looks_offscreen() => Assert.True(LauncherLayout.LooksOffscreen(0, float.PositiveInfinity));
    [Fact] public void A_wildly_negative_position_looks_offscreen() => Assert.True(LauncherLayout.LooksOffscreen(-50000, 0));
    [Fact] public void A_wildly_large_position_looks_offscreen() => Assert.True(LauncherLayout.LooksOffscreen(0, 100000));
}
