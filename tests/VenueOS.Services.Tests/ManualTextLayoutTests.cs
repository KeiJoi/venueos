using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>Covers the 0.2.2 hotfix: the User Manual reader's word-wrap previously relied on
/// <c>ImGui.GetCursorPosX()</c> to track "how far along the current line is" — but every ImGui item resets the
/// cursor to the window's left margin on the next line regardless of whether it was itself placed via
/// <c>SameLine()</c>, so that check almost never fired and every word ended up chained onto one ever-widening line.
/// <see cref="ManualTextLayout"/> has no ImGui dependency at all — these tests use a simple deterministic
/// per-character width function (no live rendering needed) to verify the width math itself, independent of
/// whatever ImGui/font measurements would produce in game.</summary>
public sealed class ManualTextLayoutTests
{
    /// <summary>1 unit per character — simple, deterministic, and enough to reason about wrap points exactly.</summary>
    private static float Measure(string s) => s.Length;

    private static List<(string, int)> Words(params string[] words) => words.Select(w => (w, 0)).ToList();

    [Fact]
    public void Short_text_that_fits_stays_on_one_line()
    {
        var lines = ManualTextLayout.Layout(Words("one", "two", "three"), availableWidth: 100, spaceWidth: 1, Measure);
        var line = Assert.Single(lines);
        Assert.Equal(["one", "two", "three"], line.Pieces.Select(p => p.Text));
    }

    [Fact]
    public void Long_text_wraps_onto_multiple_lines_when_it_exceeds_the_available_width()
    {
        // "aaaa bbbb cccc dddd" - each word is 4 chars, +1 space between = 5 per word after the first.
        // width 9 fits exactly two words per line ("aaaa" + " " + "bbbb" = 9).
        var lines = ManualTextLayout.Layout(Words("aaaa", "bbbb", "cccc", "dddd"), availableWidth: 9, spaceWidth: 1, Measure);
        Assert.True(lines.Count > 1, "expected wrapping onto more than one line");
        Assert.Equal(["aaaa", "bbbb"], lines[0].Pieces.Select(p => p.Text));
        Assert.Equal(["cccc", "dddd"], lines[1].Pieces.Select(p => p.Text));
    }

    [Fact]
    public void A_smaller_available_width_produces_at_least_as_many_lines_as_a_larger_one()
    {
        // Stands in for "indentation reduces available width" (blockquotes, table cells) - the same words must
        // wrap at least as aggressively when given less room, never less.
        var words = Words("alpha", "bravo", "charlie", "delta", "echo", "foxtrot");
        var wide = ManualTextLayout.Layout(words, availableWidth: 200, spaceWidth: 1, Measure);
        var narrow = ManualTextLayout.Layout(words, availableWidth: 20, spaceWidth: 1, Measure);
        Assert.True(narrow.Count >= wide.Count);
    }

    [Fact]
    public void Resizing_the_available_width_changes_where_lines_break()
    {
        var words = Words("aaaa", "bbbb", "cccc", "dddd");
        var atNine = ManualTextLayout.Layout(words, availableWidth: 9, spaceWidth: 1, Measure);
        var atFourteen = ManualTextLayout.Layout(words, availableWidth: 14, spaceWidth: 1, Measure); // fits 3 words: 4+1+4+1+4=14
        Assert.NotEqual(atNine.Select(l => l.Pieces.Count), atFourteen.Select(l => l.Pieces.Count));
        Assert.Equal(3, atFourteen[0].Pieces.Count);
    }

    [Fact]
    public void Mixed_styles_all_participate_in_width_calculation_regardless_of_which_run_they_came_from()
    {
        // Simulates "normal text + **bold** + `code` + link" - a style-aware measure (bold/code render wider,
        // as a real bold-glyph-doubling/code-background inset would) must still be respected by the wrap decision;
        // width math must never assume every run measures identically.
        var words = new List<(string Text, int RunIndex)> { ("plain", 0), ("boldword", 1), ("codeword", 2), ("linktext", 3) };
        float StyleAwareMeasure(string s) => s switch { "boldword" => s.Length + 2f, "codeword" => s.Length + 1f, _ => s.Length };
        var lines = ManualTextLayout.Layout(words, availableWidth: 15, spaceWidth: 1, StyleAwareMeasure);
        // Every produced piece's width must equal what the style-aware measurement actually said for its text.
        foreach (var line in lines)
            foreach (var piece in line.Pieces)
                Assert.Equal(StyleAwareMeasure(piece.Text), piece.Width);
        // And it must have wrapped at all given the narrow width and the wider bold/code measurements.
        Assert.True(lines.Count > 1);
    }

    [Fact]
    public void List_and_table_cell_lines_never_exceed_the_assigned_width_except_for_an_unavoidable_over_width_token()
    {
        var words = Words("this", "is", "a", "reasonably", "long", "piece", "of", "continuation", "text", "for", "a", "list", "item");
        const float width = 18f;
        var lines = ManualTextLayout.Layout(words, width, spaceWidth: 1, Measure);
        foreach (var line in lines)
        {
            var used = 0f;
            for (var i = 0; i < line.Pieces.Count; i++) used += (i == 0 ? 0 : 1) + line.Pieces[i].Width;
            Assert.True(used <= width, $"line used {used} > available {width}");
        }
    }

    [Fact]
    public void An_over_width_single_token_is_hard_split_across_multiple_lines_without_losing_any_text()
    {
        var longToken = "https://example.com/a/very/long/path/that/does/not/contain/any/spaces/at/all";
        var lines = ManualTextLayout.Layout(Words(longToken), availableWidth: 10, spaceWidth: 1, Measure);
        Assert.True(lines.Count > 1, "an over-width token must be split across more than one line");
        foreach (var line in lines)
        {
            Assert.Single(line.Pieces); // each line of a hard-split token is just that one chunk
            Assert.True(line.Pieces[0].Width <= 10);
        }
        // No character was dropped or duplicated by the split.
        Assert.Equal(longToken, string.Concat(lines.SelectMany(l => l.Pieces.Select(p => p.Text))));
    }

    [Fact]
    public void A_short_word_is_never_split_even_if_it_would_have_fit_differently()
    {
        var lines = ManualTextLayout.Layout(Words("hi"), availableWidth: 100, spaceWidth: 1, Measure);
        var line = Assert.Single(lines);
        var piece = Assert.Single(line.Pieces);
        Assert.Equal("hi", piece.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Zero_or_negative_available_width_never_throws_and_still_produces_a_result(float width)
    {
        var lines = ManualTextLayout.Layout(Words("one", "two", "three"), width, spaceWidth: 1, Measure);
        Assert.NotEmpty(lines);
        Assert.Equal(["one", "two", "three"], lines[0].Pieces.Select(p => p.Text));
    }

    [Fact]
    public void Empty_word_list_produces_no_lines()
    {
        Assert.Empty(ManualTextLayout.Layout([], availableWidth: 100, spaceWidth: 1, Measure));
    }

    [Fact]
    public void Blank_entries_are_skipped_without_producing_empty_pieces()
    {
        var lines = ManualTextLayout.Layout(Words("one", "", "two"), availableWidth: 100, spaceWidth: 1, Measure);
        var line = Assert.Single(lines);
        Assert.Equal(["one", "two"], line.Pieces.Select(p => p.Text));
    }
}
