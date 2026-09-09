using VenueOS.Modules.Operations.Macro;

namespace VenueOS.Services.Tests;

/// <summary><see cref="MacroBodyText"/>/<see cref="MacroLineValidator"/>: the multiline-body ↔ executable-line
/// conversion layer and its EOF/CRLF/byte-limit rules (MACRO LIVE QA FIX §8-§13, §29-§34).</summary>
public sealed class MacroBodyTextTests
{
    [Fact]
    public void A_single_line_parses()
    {
        Assert.Equal(["/say Hello"], MacroBodyText.ToExecutableLines("/say Hello"));
    }

    [Fact]
    public void Multiple_LF_lines_parse_in_order()
    {
        Assert.Equal(["/say One", "/say Two", "/say Three"], MacroBodyText.ToExecutableLines("/say One\n/say Two\n/say Three"));
    }

    [Fact]
    public void CRLF_line_endings_parse_identically_to_LF()
    {
        Assert.Equal(["/say One", "/say Two"], MacroBodyText.ToExecutableLines("/say One\r\n/say Two"));
    }

    [Fact]
    public void Lone_CR_line_endings_are_also_normalized()
    {
        Assert.Equal(["/say One", "/say Two"], MacroBodyText.ToExecutableLines("/say One\r/say Two"));
    }

    [Fact]
    public void The_first_empty_line_ends_the_macro()
    {
        var body = "/echo One\n/echo Two\n\n/echo Four";
        Assert.Equal(["/echo One", "/echo Two"], MacroBodyText.ToExecutableLines(body));
    }

    [Fact]
    public void A_whitespace_only_line_ends_the_macro_exactly_like_a_truly_empty_one()
    {
        var body = "/echo One\n   \t  \n/echo Never";
        Assert.Equal(["/echo One"], MacroBodyText.ToExecutableLines(body));
    }

    [Fact]
    public void Content_after_EOF_is_completely_excluded_not_just_unmarked()
    {
        var body = "/ac \"Ten\"\n/actionready\n/ac \"Chi\"\n/actionready\n/ac \"Jin\"\n\n/ac \"Huraijin\"";
        var lines = MacroBodyText.ToExecutableLines(body);
        Assert.DoesNotContain(lines, l => l.Contains("Huraijin"));
        Assert.Equal(["/ac \"Ten\"", "/actionready", "/ac \"Chi\"", "/actionready", "/ac \"Jin\""], lines);
    }

    [Fact]
    public void Nested_macro_invocation_after_EOF_never_becomes_executable()
    {
        var body = "/echo Parent Start\n/venueos macro \"Child\"\n/echo Parent End\n\n/echo SHOULD NOT RUN";
        var lines = MacroBodyText.ToExecutableLines(body);
        Assert.Equal(["/echo Parent Start", "/venueos macro \"Child\"", "/echo Parent End"], lines);
    }

    [Fact]
    public void No_blank_line_present_means_the_whole_body_is_executable()
    {
        var body = string.Join('\n', Enumerable.Range(0, 150).Select(i => $"/say {i}"));
        Assert.Equal(150, MacroBodyText.ToExecutableLines(body).Count);
    }

    [Fact]
    public void A_trailing_newline_from_a_paste_does_not_introduce_a_phantom_line()
    {
        Assert.Equal(["/say One", "/say Two"], MacroBodyText.ToExecutableLines("/say One\n/say Two\n"));
    }

    [Fact]
    public void An_empty_or_null_body_produces_zero_executable_lines()
    {
        Assert.Empty(MacroBodyText.ToExecutableLines(""));
        Assert.Empty(MacroBodyText.ToExecutableLines(null));
    }

    [Fact]
    public void Actionready_directive_text_survives_parsing_unchanged()
    {
        Assert.Equal(["/actionready"], MacroBodyText.ToExecutableLines("/actionready"));
    }

    [Fact]
    public void Nested_macro_directive_text_survives_parsing_unchanged()
    {
        Assert.Equal(["/venueos macro \"Some Name\""], MacroBodyText.ToExecutableLines("/venueos macro \"Some Name\""));
    }

    [Fact]
    public void ToBodyText_is_the_inverse_of_ToExecutableLines_for_already_normalized_input()
    {
        IReadOnlyList<string> lines = ["/say One", "/say Two", "/say Three"];
        var roundTripped = MacroBodyText.ToExecutableLines(MacroBodyText.ToBodyText(lines));
        Assert.Equal(lines, roundTripped);
    }

    // =========================================================================================================
    // Per-line byte-limit validation (fix spec §12)
    // =========================================================================================================

    [Fact]
    public void A_line_at_exactly_the_byte_limit_passes()
    {
        var line = new string('a', MacroLineLimits.MaxLineBytes);
        Assert.Empty(MacroLineValidator.FindOversizedLines([line]));
    }

    [Fact]
    public void A_line_one_byte_over_the_limit_fails_with_its_line_number()
    {
        var line = new string('a', MacroLineLimits.MaxLineBytes + 1);
        var errors = MacroLineValidator.FindOversizedLines(["/say ok", line]);
        Assert.Single(errors);
        Assert.Contains("Line 2", errors[0]);
    }

    [Fact]
    public void Only_the_offending_lines_are_reported_others_are_unaffected()
    {
        var tooLong = new string('a', MacroLineLimits.MaxLineBytes + 10);
        var errors = MacroLineValidator.FindOversizedLines(["/say ok", tooLong, "/say also ok", tooLong]);
        Assert.Equal(2, errors.Count);
        Assert.Contains("Line 2", errors[0]);
        Assert.Contains("Line 4", errors[1]);
    }

    [Fact]
    public void Utf8_multibyte_glyphs_are_counted_the_same_way_Block_Letters_counts_them()
    {
        // A 3-byte-per-codepoint block-letter-style glyph repeated until just over the byte limit — mirrors
        // BlockLettersTextEngineTests' own UTF-8 byte-counting assumption, reused (not reimplemented) here.
        var glyph = "█"; // U+2588 FULL BLOCK — encodes to 3 UTF-8 bytes
        var repeated = string.Concat(Enumerable.Repeat(glyph, (MacroLineLimits.MaxLineBytes / 3) + 1));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(repeated) > MacroLineLimits.MaxLineBytes);
        Assert.Single(MacroLineValidator.FindOversizedLines([repeated]));
    }
}
