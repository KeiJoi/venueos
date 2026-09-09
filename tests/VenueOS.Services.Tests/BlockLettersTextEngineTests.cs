using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Services.Tests;

/// <summary>Pure, ImGui-free tests for Block Letters' editing/limit logic — the actual ImGui InputTextMultiline
/// callback wiring (BlockLettersOperatorPanel) can only be verified live in Dalamud (NEW_MODULE_GUIDE.md §30), but
/// every decision it delegates to <see cref="BlockTextEditor"/>/<see cref="BlockLettersLimits"/>/
/// <see cref="Utf8Offsets"/> is fully covered here.</summary>
public sealed class BlockLettersTextEngineTests
{
    // Catalog "A" (U+E071) — a 3-byte-in-UTF-8 FFXIV Private Use Area glyph. Built from the codepoint rather than
    // an embedded literal so the source file never has to carry an unrenderable raw character.
    private static readonly string BlockA = char.ConvertFromUtf32(0xE071);

    // ---- Limits -------------------------------------------------------------------------------------------------

    [Fact] public void Chat_limit_is_the_researched_value() => Assert.Equal(500, BlockLettersLimits.MaxBytes(BlockLettersDestination.Chat));

    [Fact] public void Party_finder_comment_limit_is_the_researched_value() => Assert.Equal(192, BlockLettersLimits.MaxBytes(BlockLettersDestination.PartyFinderComment));

    [Fact] public void Macro_line_limit_is_the_researched_value() => Assert.Equal(181, BlockLettersLimits.MaxBytes(BlockLettersDestination.MacroLine));

    [Fact]
    public void Length_is_measured_in_utf8_bytes_not_utf16_chars_or_codepoints()
    {
        // One block-letter glyph is a single UTF-16 char / single Unicode scalar, but 3 UTF-8 bytes — the
        // measurement that must drive every limit decision in this module.
        Assert.Equal(1, BlockA.Length);
        Assert.Equal(3, BlockTextLength.CountBytes(BlockA));
        Assert.Equal(30, BlockTextLength.CountBytes(string.Concat(Enumerable.Repeat(BlockA, 10))));
    }

    [Fact]
    public void Exceeds_limit_is_false_at_exactly_the_limit_and_true_one_byte_over()
    {
        var atLimit = new string('a', BlockLettersLimits.MacroLineBytes);
        var overLimit = new string('a', BlockLettersLimits.MacroLineBytes + 1);
        Assert.False(BlockLettersLimits.ExceedsLimit(atLimit, BlockLettersDestination.MacroLine));
        Assert.True(BlockLettersLimits.ExceedsLimit(overLimit, BlockLettersDestination.MacroLine));
    }

    [Fact]
    public void Exceeds_limit_recognizes_multi_byte_glyphs_pushing_ascii_sized_text_over()
    {
        // 64 block-letter glyphs = 192 bytes (fits exactly); one more is 195 bytes (over) even though the char
        // count (65) looks nowhere near "192".
        var underLimit = string.Concat(Enumerable.Repeat(BlockA, 64)); // 192 bytes exactly
        var overLimit = string.Concat(Enumerable.Repeat(BlockA, 65)); // 195 bytes
        Assert.False(BlockLettersLimits.ExceedsLimit(underLimit, BlockLettersDestination.PartyFinderComment));
        Assert.True(BlockLettersLimits.ExceedsLimit(overLimit, BlockLettersDestination.PartyFinderComment));
    }

    // ---- Insertion / selection editing --------------------------------------------------------------------------

    [Fact]
    public void Insert_at_end()
    {
        var result = BlockTextEditor.Insert("Hello", 5, 5, " World", 500);
        Assert.Equal("Hello World", result.Text);
        Assert.Equal(11, result.CursorPos);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Insert_at_beginning()
    {
        var result = BlockTextEditor.Insert("World", 0, 0, "Hello ", 500);
        Assert.Equal("Hello World", result.Text);
        Assert.Equal(6, result.CursorPos);
    }

    [Fact]
    public void Insert_in_middle()
    {
        var result = BlockTextEditor.Insert("Helo", 2, 2, "l", 500);
        Assert.Equal("Hello", result.Text);
        Assert.Equal(3, result.CursorPos);
    }

    [Fact]
    public void Replace_selection()
    {
        var result = BlockTextEditor.Insert("Hello World", 6, 11, "Venue", 500);
        Assert.Equal("Hello Venue", result.Text);
        Assert.Equal(11, result.CursorPos);
    }

    [Fact]
    public void Normal_ascii_is_left_untouched_by_a_no_op_zero_length_insertion()
    {
        var result = BlockTextEditor.Insert("Plain ASCII text", 4, 4, "", 500);
        Assert.Equal("Plain ASCII text", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Mixed_ascii_and_block_characters_compose_correctly()
    {
        var withGlyph = BlockTextEditor.Insert("VENUE OPEN", 5, 5, BlockA, 500);
        Assert.Equal("VENUE" + BlockA + " OPEN", withGlyph.Text);
        var withMoreAscii = BlockTextEditor.Insert(withGlyph.Text, withGlyph.CursorPos, withGlyph.CursorPos, "!!!", 500);
        Assert.Equal("VENUE" + BlockA + "!!! OPEN", withMoreAscii.Text);
    }

    // ---- Hard limit behavior --------------------------------------------------------------------------------------

    [Fact]
    public void Exact_limit_text_is_accepted_in_full()
    {
        var current = new string('a', 190);
        var result = BlockTextEditor.Insert(current, 190, 190, "bc", 192); // exactly reaches 192
        Assert.Equal(192, result.Text.Length);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void One_byte_over_the_limit_is_clamped_not_silently_permitted()
    {
        var current = new string('a', 190);
        var result = BlockTextEditor.Insert(current, 190, 190, "bcd", 192); // "bcd" would land at 193
        Assert.Equal(192, BlockTextLength.CountBytes(result.Text));
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Block_glyph_insertion_cannot_exceed_the_limit()
    {
        // 500-byte destination filled to exactly 500 with ASCII; a 3-byte glyph must be fully rejected, not
        // partially inserted (a single scalar is atomic — there is no valid partial insertion of one glyph).
        var current = new string('a', 500);
        var result = BlockTextEditor.Insert(current, 500, 500, BlockA, 500);
        Assert.Equal(current, result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Paste_accepts_only_the_portion_that_fits()
    {
        // Simulates a large paste landing where only part of it fits before the destination's limit is reached.
        var current = new string('a', 495);
        var pasted = new string('b', 20);
        var result = BlockTextEditor.Insert(current, 495, 495, pasted, 500);
        Assert.Equal(500, result.Text.Length);
        Assert.EndsWith(new string('b', 5), result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Selection_replacement_succeeds_when_the_replacement_fits()
    {
        var current = new string('a', 200);
        var result = BlockTextEditor.Insert(current, 50, 150, "REPLACED", 500);
        Assert.Equal(new string('a', 50) + "REPLACED" + new string('a', 50), result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void No_operation_ever_splits_a_block_letter_glyph_mid_sequence()
    {
        // A run of block-letter glyphs (3 bytes each) truncated at a limit that is NOT a multiple of 3 must still
        // land on a whole-glyph boundary — never emit a lone, invalid UTF-8 continuation byte.
        var manyGlyphs = string.Concat(Enumerable.Repeat(BlockA, 10)); // 30 bytes
        var result = BlockTextEditor.Insert("", 0, 0, manyGlyphs, 20); // 20 is not a multiple of 3
        Assert.True(BlockTextLength.CountBytes(result.Text) <= 20);
        Assert.Equal(0, BlockTextLength.CountBytes(result.Text) % 3); // only whole glyphs survived
        Assert.All(result.Text, ch => Assert.Equal(BlockA[0], ch)); // every remaining char is a complete, valid glyph
    }

    // ---- Mode change (destination switch) gating ------------------------------------------------------------------

    [Fact]
    public void Switching_to_a_smaller_limit_is_detected_without_any_text_mutation()
    {
        var text = new string('a', 300); // fits Chat (500), exceeds Party Finder (192) and Macro Line (181)
        Assert.False(BlockLettersLimits.ExceedsLimit(text, BlockLettersDestination.Chat));
        Assert.True(BlockLettersLimits.ExceedsLimit(text, BlockLettersDestination.PartyFinderComment));
        Assert.True(BlockLettersLimits.ExceedsLimit(text, BlockLettersDestination.MacroLine));
        // BlockLettersLimits.ExceedsLimit is a pure predicate over the untouched text — nothing in this module's
        // logic layer ever rewrites `text` as a side effect of evaluating it, which is what lets the operator panel
        // gate Copy without ever truncating authored content on a destination switch.
    }

    [Fact]
    public void Copy_eligibility_is_restored_once_text_is_shortened_back_within_limit()
    {
        var tooLong = new string('a', 200);
        var trimmed = tooLong[..180];
        Assert.True(BlockLettersLimits.ExceedsLimit(tooLong, BlockLettersDestination.MacroLine));
        Assert.False(BlockLettersLimits.ExceedsLimit(trimmed, BlockLettersDestination.MacroLine));
    }

    // ---- UTF-8 byte offset <-> char index bridge (ImGui callback support) -----------------------------------------

    [Fact]
    public void Utf8_offset_conversions_round_trip_through_ascii_and_block_glyphs()
    {
        var text = "AB" + BlockA + "CD";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        var charIndexAfterGlyph = Utf8Offsets.ToCharIndex(bytes, 5); // "AB" (2 bytes) + glyph (3 bytes) = byte 5
        Assert.Equal(3, charIndexAfterGlyph); // "AB" + glyph = 3 chars

        var byteOffsetAfterGlyph = Utf8Offsets.ToByteOffset(text, 3);
        Assert.Equal(5, byteOffsetAfterGlyph);
    }

    [Fact]
    public void Utf8_offset_conversions_clamp_out_of_range_input_instead_of_throwing()
    {
        var text = "AB";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        Assert.Equal(2, Utf8Offsets.ToCharIndex(bytes, 999));
        Assert.Equal(2, Utf8Offsets.ToByteOffset(text, 999));
        Assert.Equal(0, Utf8Offsets.ToCharIndex(bytes, -5));
        Assert.Equal(0, Utf8Offsets.ToByteOffset(text, -5));
    }
}
