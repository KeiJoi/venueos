using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

/// <summary>
/// Pure layout-math tests for the responsive tiled Card Viewer (live-QA correction). These prove the ARITHMETIC
/// is correct; they cannot and do not prove actual ImGui rendering — the viewer's ImGui wiring itself is verified
/// by inspection only (see BingoPlayerCardViewerWindow.DrawContent).
/// </summary>
public sealed class BingoCardViewerLayoutTests
{
    [Fact] public void Narrow_width_produces_exactly_one_column()
        => Assert.Equal(1, BingoCardViewerLayout.CalculateColumnCount(300f));

    [Fact] public void Width_below_one_cards_minimum_still_returns_one_never_zero()
    {
        Assert.Equal(1, BingoCardViewerLayout.CalculateColumnCount(50f));
        Assert.Equal(1, BingoCardViewerLayout.CalculateColumnCount(0f));
        Assert.Equal(1, BingoCardViewerLayout.CalculateColumnCount(-100f));
    }

    [Fact] public void Medium_width_increases_columns()
    {
        // Exactly enough for 2 cards + 1 gutter between them.
        var width = 2 * BingoCardViewerLayout.DefaultMinCardWidth + BingoCardViewerLayout.DefaultSpacing;
        Assert.Equal(2, BingoCardViewerLayout.CalculateColumnCount(width));
    }

    [Fact] public void Wide_width_increases_columns_further()
    {
        var width = 5 * BingoCardViewerLayout.DefaultMinCardWidth + 4 * BingoCardViewerLayout.DefaultSpacing;
        Assert.Equal(5, BingoCardViewerLayout.CalculateColumnCount(width));
    }

    [Fact] public void Column_count_never_decreases_as_width_increases()
    {
        var previous = 1;
        for (var width = 100f; width <= 4000f; width += 50f)
        {
            var columns = BingoCardViewerLayout.CalculateColumnCount(width);
            Assert.True(columns >= previous, $"columns decreased at width={width}: {columns} < {previous}");
            Assert.True(columns >= 1, "column count must never be zero or negative");
            previous = columns;
        }
    }

    [Fact] public void Cards_never_receive_less_than_the_defined_minimum_readable_width()
    {
        // For every column count this function can produce, N cards of at least minCardWidth each plus (N-1)
        // gutters must fit within the width that produced it — i.e. an extra column is never squeezed in by
        // shrinking cards below the readable minimum. Only checked once width can actually fit one card at all —
        // below that, a single card simply overflows its own unreasonably narrow window (documented behavior),
        // which is not "shrinking" anything.
        for (var width = BingoCardViewerLayout.DefaultMinCardWidth; width <= 4000f; width += 37f)
        {
            var columns = BingoCardViewerLayout.CalculateColumnCount(width);
            var requiredWidth = columns * BingoCardViewerLayout.DefaultMinCardWidth + (columns - 1) * BingoCardViewerLayout.DefaultSpacing;
            Assert.True(requiredWidth <= width, $"width={width} produced {columns} columns needing {requiredWidth}, which doesn't fit");
        }
    }

    [Fact] public void Ten_cards_wrap_correctly_at_various_column_counts()
    {
        Assert.Equal(10, BingoCardViewerLayout.CalculateRowCount(10, 1));
        Assert.Equal(5, BingoCardViewerLayout.CalculateRowCount(10, 2));
        Assert.Equal(4, BingoCardViewerLayout.CalculateRowCount(10, 3)); // 3+3+3+1
        Assert.Equal(3, BingoCardViewerLayout.CalculateRowCount(10, 4)); // 4+4+2
        Assert.Equal(2, BingoCardViewerLayout.CalculateRowCount(10, 5));
        Assert.Equal(1, BingoCardViewerLayout.CalculateRowCount(10, 10));
        Assert.Equal(1, BingoCardViewerLayout.CalculateRowCount(10, 16)); // more columns than cards — still one row
    }

    [Fact] public void Sixteen_cards_wrap_correctly_at_various_column_counts()
    {
        Assert.Equal(16, BingoCardViewerLayout.CalculateRowCount(16, 1));
        Assert.Equal(8, BingoCardViewerLayout.CalculateRowCount(16, 2));
        Assert.Equal(4, BingoCardViewerLayout.CalculateRowCount(16, 4));
        Assert.Equal(4, BingoCardViewerLayout.CalculateRowCount(16, 5)); // 5+5+5+1 -> ceil(16/5) = 4 rows
        Assert.Equal(2, BingoCardViewerLayout.CalculateRowCount(16, 8));
        Assert.Equal(1, BingoCardViewerLayout.CalculateRowCount(16, 16));
    }

    [Fact] public void Row_count_is_zero_for_no_cards_or_no_columns()
    {
        Assert.Equal(0, BingoCardViewerLayout.CalculateRowCount(0, 4));
        Assert.Equal(0, BingoCardViewerLayout.CalculateRowCount(10, 0));
    }
}
