using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

public sealed class BingoCardGeneratorTests
{
    // Known vector, regenerated after the live-QA Card 1 fix (verified against backend/lib/cardgen.js) — index 0
    // now uses "{seed}_0", matching app.js's createCard(index) and the donor plugin's GenerateCardGrid EXACTLY
    // (neither has ever special-cased index 0 to the bare seed — that special case was this port's own bug).
    private static readonly int?[][] SeedCCardZero =
    [
        [9, 28, 35, 47, 75],
        [13, 24, 44, 49, 73],
        [1, 25, null, 54, 64],
        [11, 26, 42, 53, 61],
        [5, 30, 34, 56, 67],
    ];

    [Fact] public void Known_vector_seedC_card_zero_matches_the_backend_port_exactly()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
                Assert.Equal(SeedCCardZero[row][col], grid[row][col].Number);
        Assert.True(grid[2][2].IsFree);
    }

    /// <summary>REGRESSION GUARD for the exact live-QA Card 1 bug: <c>GenerateCardForIndex(seed, 0)</c> must equal
    /// <c>GenerateCard("{seed}_0")</c> — the SAME "{seed}_{index}" rule every other index uses — and must NOT equal
    /// the bare, unsuffixed seed's card. This test FAILS against the previous (buggy) implementation, which
    /// special-cased index 0 to the bare seed, and PASSES against the fix.</summary>
    [Fact] public void Card_index_zero_uses_seed_underscore_zero_never_the_bare_seed()
    {
        var viaIndex = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var viaComposedSeed = BingoCardGenerator.GenerateCard("seedC_0");
        for (var row = 0; row < 5; row++) for (var col = 0; col < 5; col++) Assert.Equal(viaComposedSeed[row][col].Number, viaIndex[row][col].Number);

        var viaBareSeed = BingoCardGenerator.GenerateCard("seedC");
        Assert.NotEqual(BingoCardGenerator.CardNumbers(viaBareSeed), BingoCardGenerator.CardNumbers(viaIndex)); // the exact live-QA Card 1 bug: index 0 must NOT match the bare seed
    }

    [Fact] public void Card_index_above_zero_uses_seed_underscore_index()
    {
        var viaIndex = BingoCardGenerator.GenerateCardForIndex("seedC", 1);
        var viaComposedSeed = BingoCardGenerator.GenerateCard("seedC_1");
        for (var row = 0; row < 5; row++) for (var col = 0; col < 5; col++) Assert.Equal(viaComposedSeed[row][col].Number, viaIndex[row][col].Number);
        // And it must genuinely differ from index 0's card — otherwise the "_index" suffix isn't actually doing anything.
        var indexZero = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        Assert.NotEqual(BingoCardGenerator.CardNumbers(indexZero), BingoCardGenerator.CardNumbers(viaIndex));
    }

    // --- Cross-implementation golden compatibility vectors (live-QA requirement) — IDENTICAL to the vectors
    // asserted in backend/test/cardgen.test.js. Card 1 (index 0), Card 2 (index 1), Card 3 (index 2), and Card 16
    // (index 15), for two representative seeds. Both implementations must independently produce these exact
    // matrices; particular attention to index 0, the one index the live bug actually affected. ---
    [Theory]
    [InlineData("seedC", 0, new object?[] { 9, 28, 35, 47, 75 }, new object?[] { 13, 24, 44, 49, 73 }, new object?[] { 1, 25, null, 54, 64 }, new object?[] { 11, 26, 42, 53, 61 }, new object?[] { 5, 30, 34, 56, 67 })]
    [InlineData("seedC", 1, new object?[] { 11, 21, 40, 56, 65 }, new object?[] { 2, 17, 43, 60, 70 }, new object?[] { 10, 16, null, 55, 74 }, new object?[] { 12, 20, 42, 50, 68 }, new object?[] { 1, 26, 45, 49, 75 })]
    [InlineData("seedC", 2, new object?[] { 6, 23, 41, 57, 64 }, new object?[] { 9, 27, 40, 58, 69 }, new object?[] { 13, 22, null, 55, 67 }, new object?[] { 4, 21, 38, 59, 74 }, new object?[] { 15, 28, 36, 52, 70 })]
    [InlineData("seedC", 15, new object?[] { 4, 23, 45, 49, 70 }, new object?[] { 7, 18, 41, 54, 75 }, new object?[] { 9, 30, null, 55, 69 }, new object?[] { 3, 29, 37, 51, 71 }, new object?[] { 2, 24, 43, 58, 64 })]
    [InlineData("venue-QA-seed-42", 0, new object?[] { 13, 30, 33, 46, 72 }, new object?[] { 10, 22, 37, 55, 75 }, new object?[] { 11, 26, null, 56, 67 }, new object?[] { 8, 29, 45, 52, 62 }, new object?[] { 4, 24, 40, 50, 66 })]
    [InlineData("venue-QA-seed-42", 1, new object?[] { 3, 26, 41, 56, 61 }, new object?[] { 9, 28, 42, 51, 73 }, new object?[] { 12, 18, null, 53, 72 }, new object?[] { 6, 16, 40, 54, 66 }, new object?[] { 5, 19, 38, 50, 68 })]
    [InlineData("venue-QA-seed-42", 15, new object?[] { 10, 29, 41, 52, 71 }, new object?[] { 9, 24, 33, 47, 67 }, new object?[] { 6, 19, null, 60, 61 }, new object?[] { 3, 23, 39, 48, 66 }, new object?[] { 4, 17, 37, 49, 64 })]
    public void Golden_cross_implementation_vector(string seed, int index, object?[] row0, object?[] row1, object?[] row2, object?[] row3, object?[] row4)
    {
        var expected = new[] { row0, row1, row2, row3, row4 };
        var grid = BingoCardGenerator.GenerateCardForIndex(seed, index);
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
                Assert.Equal((int?)expected[row][col], grid[row][col].Number);
        Assert.True(grid[2][2].IsFree);
    }

    [Fact] public void Same_seed_and_index_always_reproduce_the_same_grid()
    {
        var a = BingoCardGenerator.GenerateCardForIndex("determinism-seed", 3);
        var b = BingoCardGenerator.GenerateCardForIndex("determinism-seed", 3);
        Assert.Equal(BingoCardGenerator.CardNumbers(a), BingoCardGenerator.CardNumbers(b));
    }

    [Fact] public void Card_numbers_exclude_the_free_space_and_cover_every_non_free_cell()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var numbers = BingoCardGenerator.CardNumbers(grid);
        Assert.Equal(24, numbers.Count);
        Assert.DoesNotContain(numbers, n => n == 0);
    }

    [Fact] public void Blackout_requires_every_cell_daubed()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var all = new HashSet<int>(BingoCardGenerator.CardNumbers(grid));
        Assert.True(BingoCardGenerator.CardHasBingo(grid, all, "Blackout"));
        var missingOne = new HashSet<int>(all); missingOne.Remove(missingOne.First());
        Assert.False(BingoCardGenerator.CardHasBingo(grid, missingOne, "Blackout"));
    }

    [Fact] public void Four_corners_only_needs_the_four_corner_cells()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var corners = new HashSet<int> { grid[0][0].Number!.Value, grid[0][4].Number!.Value, grid[4][0].Number!.Value, grid[4][4].Number!.Value };
        Assert.True(BingoCardGenerator.CardHasBingo(grid, corners, "Four Corners"));
        Assert.False(BingoCardGenerator.CardHasBingo(grid, new HashSet<int>(), "Four Corners"));
    }

    [Fact] public void Single_line_is_satisfied_by_one_complete_row()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var row0 = new HashSet<int> { grid[0][0].Number!.Value, grid[0][1].Number!.Value, grid[0][2].Number!.Value, grid[0][3].Number!.Value, grid[0][4].Number!.Value };
        Assert.True(BingoCardGenerator.CardHasBingo(grid, row0, "Single Line"));
    }

    [Fact] public void Two_lines_requires_at_least_two_complete_lines()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var row0 = new HashSet<int> { grid[0][0].Number!.Value, grid[0][1].Number!.Value, grid[0][2].Number!.Value, grid[0][3].Number!.Value, grid[0][4].Number!.Value };
        Assert.False(BingoCardGenerator.CardHasBingo(grid, row0, "Two Lines"));
        var row0AndRow1 = new HashSet<int>(row0) { grid[1][0].Number!.Value, grid[1][1].Number!.Value, grid[1][2].Number!.Value, grid[1][3].Number!.Value, grid[1][4].Number!.Value };
        Assert.True(BingoCardGenerator.CardHasBingo(grid, row0AndRow1, "Two Lines"));
    }

    [Fact] public void Free_space_counts_as_daubed_for_every_pattern()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var diag = new HashSet<int> { grid[0][0].Number!.Value, grid[1][1].Number!.Value, grid[3][3].Number!.Value, grid[4][4].Number!.Value }; // deliberately omits [2][2], which is the free space
        Assert.True(BingoCardGenerator.CardHasBingo(grid, diag, "Single Line"));
    }

    // --- WinningCells (live-QA addition — host Card Viewer's "highlight the winning pattern" visual state). ---

    [Fact] public void WinningCells_is_empty_when_no_pattern_is_complete()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        Assert.Empty(BingoCardGenerator.WinningCells(grid, new HashSet<int>(), "Single Line"));
    }

    [Fact] public void WinningCells_returns_exactly_the_complete_rows_cells_for_Single_Line()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var row0 = new HashSet<int> { grid[0][0].Number!.Value, grid[0][1].Number!.Value, grid[0][2].Number!.Value, grid[0][3].Number!.Value, grid[0][4].Number!.Value };
        var winning = BingoCardGenerator.WinningCells(grid, row0, "Single Line");
        Assert.Equal(5, winning.Count);
        for (var col = 0; col < 5; col++) Assert.Contains((0, col), winning);
    }

    [Fact] public void WinningCells_for_Four_corners_returns_exactly_the_four_corner_cells()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var corners = new HashSet<int> { grid[0][0].Number!.Value, grid[0][4].Number!.Value, grid[4][0].Number!.Value, grid[4][4].Number!.Value };
        var winning = BingoCardGenerator.WinningCells(grid, corners, "Four Corners");
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (0, 4), (4, 0), (4, 4) }, winning);
    }

    [Fact] public void WinningCells_for_Blackout_returns_every_cell_only_once_fully_daubed()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var all = new HashSet<int>(BingoCardGenerator.CardNumbers(grid));
        Assert.Empty(BingoCardGenerator.WinningCells(grid, new HashSet<int>(all.Skip(1)), "Blackout")); // one short
        Assert.Equal(25, BingoCardGenerator.WinningCells(grid, all, "Blackout").Count);
    }

    [Fact] public void WinningCells_for_Two_lines_is_empty_with_only_one_complete_line()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var row0 = new HashSet<int> { grid[0][0].Number!.Value, grid[0][1].Number!.Value, grid[0][2].Number!.Value, grid[0][3].Number!.Value, grid[0][4].Number!.Value };
        Assert.Empty(BingoCardGenerator.WinningCells(grid, row0, "Two Lines")); // threshold (2) not met — even though row 0 is individually complete
        var row1 = new HashSet<int> { grid[1][0].Number!.Value, grid[1][1].Number!.Value, grid[1][2].Number!.Value, grid[1][3].Number!.Value, grid[1][4].Number!.Value };
        var winning = BingoCardGenerator.WinningCells(grid, new HashSet<int>(row0.Concat(row1)), "Two Lines");
        Assert.Equal(10, winning.Count); // both complete rows' cells
    }

    [Fact] public void WinningCells_and_CardHasBingo_always_agree_on_whether_a_pattern_is_complete()
    {
        var grid = BingoCardGenerator.GenerateCardForIndex("seedC", 0);
        var gameTypes = new[] { "Single Line", "Two Lines", "Four Corners", "Blackout" };
        var probes = new[]
        {
            new HashSet<int>(),
            new HashSet<int> { grid[0][0].Number!.Value, grid[0][1].Number!.Value, grid[0][2].Number!.Value, grid[0][3].Number!.Value, grid[0][4].Number!.Value },
            new HashSet<int>(BingoCardGenerator.CardNumbers(grid)),
        };
        foreach (var gameType in gameTypes)
            foreach (var probe in probes)
                Assert.Equal(BingoCardGenerator.CardHasBingo(grid, probe, gameType), BingoCardGenerator.WinningCells(grid, probe, gameType).Count > 0);
    }
}
