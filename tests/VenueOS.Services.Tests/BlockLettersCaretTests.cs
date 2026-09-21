using System.Text;
using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Services.Tests;

/// <summary>0.3.8 caret/insertion fix. The numbers fed to <see cref="BlockEditorCaret.Observe"/> below are the exact
/// values Dear ImGui's <c>CallbackAlways</c> reported when the same interaction was replayed headlessly against the
/// game's native ImGui (1.88) through Dalamud.Bindings.ImGui: after typing, <c>CursorPos</c> advances but
/// <c>SelectionStart</c>/<c>SelectionEnd</c> stay frozen at the previous click position; a real selection reports
/// <c>CursorPos == SelectionEnd</c> (Start &gt; End for a backwards selection). Everything else here is pure text/index
/// logic. What these tests cannot prove — that the live FFXIV widget behaves the same, focus restoration, selection
/// surviving the click — is left to the live QA checklist in docs/BLOCK_LETTERS_CARET_0.3.8_MAINTENANCE.md.</summary>
public sealed class BlockLettersCaretTests
{
    private static readonly string BlockA = char.ConvertFromUtf32(0xE071); // 1 UTF-16 char, 3 UTF-8 bytes
    private static readonly string BlockB = char.ConvertFromUtf32(0xE072);
    private static readonly string BlockC = char.ConvertFromUtf32(0xE073);
    private const string Emoji = "\U0001F600"; // surrogate pair in UTF-16, 4 bytes in UTF-8

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>One CallbackAlways snapshot for <paramref name="text"/>, with the caret at <paramref name="caretChars"/>
    /// and ImGui's selection pair left wherever the caller says (in bytes) — pass the stale pair to mimic typing.</summary>
    private static BlockEditorObservation Frame(string text, int caretChars, int? selStartBytes = null, int? selEndBytes = null)
    {
        var caretBytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, caretChars));
        return BlockEditorCaret.Observe(Utf8(text), caretBytes, selStartBytes ?? caretBytes, selEndBytes ?? caretBytes);
    }

    // --- The defect, proven ------------------------------------------------------------------------------------

    [Fact]
    public void Block_then_typed_characters_then_block_inserts_after_the_typed_characters_not_after_the_previous_block()
    {
        // Composition after: click block A, click into the box, type "ab". Captured from real ImGui: the buffer is
        // A(3 bytes)+"ab", CursorPos=5, but SelectionStart=SelectionEnd=3 — frozen at the position right after block A.
        var text = BlockA + "ab";
        var observation = BlockEditorCaret.Observe(Utf8(text), cursorBytes: 5, selectionStartBytes: 3, selectionEndBytes: 3);

        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, observation, BlockB, 500);

        Assert.Equal(BlockA + "ab" + BlockB, outcome.Text);
        Assert.False(outcome.UsedEndFallback);
    }

    [Fact]
    public void The_old_reading_of_the_stale_selection_pair_lands_directly_after_the_previous_block()
    {
        // Documents WHY it broke: the pre-fix panel converted SelectionStart/SelectionEnd (3,3) and used that as the
        // caret. Byte 3 is right after the 3-byte glyph == char index 1, while the real caret is char index 3.
        var utf8 = Utf8(BlockA + "ab");
        var oldCaret = Utf8Offsets.ToCharIndex(utf8, 3);
        var realCaret = BlockEditorCaret.Observe(utf8, cursorBytes: 5, selectionStartBytes: 3, selectionEndBytes: 3).Selection.Start;

        Assert.Equal(1, oldCaret);
        Assert.Equal(3, realCaret);
        Assert.Equal(BlockA + BlockB + "ab", BlockTextEditor.Insert(BlockA + "ab", oldCaret, oldCaret, BlockB, 500).Text); // the reported wrong result
    }

    // --- Insertion positions -------------------------------------------------------------------------------------

    [Fact]
    public void Empty_composition_plus_block()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("", null, BlockA, 500);
        Assert.Equal(BlockA, outcome.Text);
        Assert.Equal(BlockEditorSelection.Caret(1), outcome.Selection);
        Assert.True(outcome.Inserted);
    }

    [Fact]
    public void Ordinary_text_then_block_at_the_end()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("HELLO", Frame("HELLO", 5), BlockA, 500);
        Assert.Equal("HELLO" + BlockA, outcome.Text);
    }

    [Fact]
    public void Block_at_the_beginning()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("HELLO", Frame("HELLO", 0), BlockA, 500);
        Assert.Equal(BlockA + "HELLO", outcome.Text);
        Assert.Equal(BlockEditorSelection.Caret(1), outcome.Selection);
    }

    [Fact]
    public void Block_in_the_middle()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("ABCDEF", Frame("ABCDEF", 3), BlockA, 500);
        Assert.Equal("ABC" + BlockA + "DEF", outcome.Text);
    }

    [Fact]
    public void Middle_insertion_after_typing_in_the_middle_uses_the_caret_not_the_frozen_click_position()
    {
        // Click between B and C (selection pair frozen at bytes 2), then type "xx": CursorPos=4, pair still (2,2).
        var text = "ABxxCDEF";
        var observation = BlockEditorCaret.Observe(Utf8(text), cursorBytes: 4, selectionStartBytes: 2, selectionEndBytes: 2);
        Assert.Equal("ABxx" + BlockA + "CDEF", BlockEditorCaret.InsertAtEditorPosition(text, observation, BlockA, 500).Text);
    }

    [Fact]
    public void Consecutive_block_insertions_chain_using_the_returned_caret()
    {
        var text = "";
        BlockEditorObservation? observation = null;
        foreach (var block in new[] { BlockA, BlockB, BlockC })
        {
            var outcome = BlockEditorCaret.InsertAtEditorPosition(text, observation, block, 500);
            text = outcome.Text;
            observation = new BlockEditorObservation(outcome.Selection, text.Length);
        }
        Assert.Equal(BlockA + BlockB + BlockC, text);
    }

    [Fact]
    public void Block_type_block_type_block_sequence_stays_in_logical_order()
    {
        var text = "";
        BlockEditorObservation? observation = null;

        void ClickBlock(string block)
        {
            var outcome = BlockEditorCaret.InsertAtEditorPosition(text, observation, block, 500);
            text = outcome.Text;
            observation = new BlockEditorObservation(outcome.Selection, text.Length);
        }
        // After a block the panel restores the caret just after it; typing then advances CursorPos while the
        // selection pair stays frozen where the restoration left it.
        void Type(string typed)
        {
            var caret = observation!.Value.Selection.Start;
            var frozenBytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, caret));
            text = text.Insert(caret, typed);
            var cursorBytes = frozenBytes + Encoding.UTF8.GetByteCount(typed);
            observation = BlockEditorCaret.Observe(Utf8(text), cursorBytes, frozenBytes, frozenBytes);
        }

        ClickBlock(BlockA); Type("12"); ClickBlock(BlockB); Type("3"); ClickBlock(BlockC); Type("é");
        Assert.Equal(BlockA + "12" + BlockB + "3" + BlockC + "é", text);
    }

    // --- Selection -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_forward_selection_is_replaced_by_the_block()
    {
        // "AB[CDE]F": start 2, end 5, CursorPos == SelectionEnd (captured from ImGui).
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 5, selectionStartBytes: 2, selectionEndBytes: 5);
        var outcome = BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500);
        Assert.Equal("AB" + BlockA + "F", outcome.Text);
        Assert.Equal(BlockEditorSelection.Caret(3), outcome.Selection);
    }

    [Fact]
    public void A_backwards_selection_is_replaced_by_the_block()
    {
        // Shift+Left from between C and D: ImGui reports SelectionStart=3 > SelectionEnd=1, CursorPos=1.
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 1, selectionStartBytes: 3, selectionEndBytes: 1);
        Assert.Equal(new BlockEditorSelection(1, 3), observation.Selection);
        Assert.Equal("A" + BlockA + "DEF", BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500).Text);
    }

    [Fact]
    public void Selection_at_the_beginning_is_replaced()
    {
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 2, selectionStartBytes: 0, selectionEndBytes: 2);
        Assert.Equal(BlockA + "CDEF", BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500).Text);
    }

    [Fact]
    public void Selection_at_the_end_is_replaced()
    {
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 6, selectionStartBytes: 4, selectionEndBytes: 6);
        Assert.Equal("ABCD" + BlockA, BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500).Text);
    }

    [Fact]
    public void A_whole_text_selection_is_replaced()
    {
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 6, selectionStartBytes: 0, selectionEndBytes: 6);
        var outcome = BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500);
        Assert.Equal(BlockA, outcome.Text);
        Assert.Equal(BlockEditorSelection.Caret(1), outcome.Selection);
    }

    [Fact]
    public void An_equal_selection_pair_is_never_a_selection_and_the_cursor_is_the_caret()
    {
        var observation = BlockEditorCaret.Observe(Utf8("ABCDEF"), cursorBytes: 4, selectionStartBytes: 1, selectionEndBytes: 1);
        Assert.True(observation.Selection.IsCaret);
        Assert.Equal(4, observation.Selection.Start);
        Assert.Equal("ABCD" + BlockA + "EF", BlockEditorCaret.InsertAtEditorPosition("ABCDEF", observation, BlockA, 500).Text);
    }

    // --- Fallback ------------------------------------------------------------------------------------------------

    [Fact]
    public void No_observation_falls_back_to_the_end_of_the_current_text()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("HELLO", null, BlockA, 500);
        Assert.Equal("HELLO" + BlockA, outcome.Text);
        Assert.True(outcome.UsedEndFallback);
    }

    [Fact]
    public void An_observation_of_a_different_text_is_not_trusted_and_the_previous_insertion_position_is_never_reused()
    {
        // A block was inserted into "" (caret now 1). The text is then replaced (e.g. Clear + retyping, or any change
        // the editor never reported) with a longer string: the remembered caret (1) must NOT be applied to it.
        var afterFirst = BlockEditorCaret.InsertAtEditorPosition("", null, BlockA, 500);
        var staleObservation = new BlockEditorObservation(afterFirst.Selection, afterFirst.Text.Length);

        var outcome = BlockEditorCaret.InsertAtEditorPosition("something else entirely", staleObservation, BlockB, 500);

        Assert.Equal("something else entirely" + BlockB, outcome.Text);
        Assert.True(outcome.UsedEndFallback);
        Assert.Null(BlockEditorCaret.Resolve(staleObservation, "something else entirely"));
    }

    [Fact]
    public void A_matching_observation_is_trusted()
    {
        var observation = Frame("ABC", 1);
        Assert.Equal(BlockEditorSelection.Caret(1), BlockEditorCaret.Resolve(observation, "ABC"));
    }

    // --- Unicode / index conversion -----------------------------------------------------------------------------

    [Fact]
    public void Non_ascii_text_before_the_caret_converts_bytes_to_chars_correctly()
    {
        // "éñ" = 4 bytes / 2 chars; caret after them at byte 4.
        var observation = BlockEditorCaret.Observe(Utf8("éñXY"), cursorBytes: 4, selectionStartBytes: 0, selectionEndBytes: 0);
        Assert.Equal(2, observation.Selection.Start);
        Assert.Equal("éñ" + BlockA + "XY", BlockEditorCaret.InsertAtEditorPosition("éñXY", observation, BlockA, 500).Text);
    }

    [Fact]
    public void Non_ascii_text_after_the_caret_is_preserved_intact()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("XYéñ", Frame("XYéñ", 2), BlockA, 500);
        Assert.Equal("XY" + BlockA + "éñ", outcome.Text);
    }

    [Fact]
    public void An_astral_character_is_one_unit_for_the_caret_and_is_never_split()
    {
        var text = "a" + Emoji + "b"; // 1 + 2 chars + 1 = 4 chars; 1 + 4 + 1 bytes
        var afterEmoji = BlockEditorCaret.Observe(Utf8(text), cursorBytes: 5, selectionStartBytes: 0, selectionEndBytes: 0);
        Assert.Equal(3, afterEmoji.Selection.Start);
        Assert.Equal("a" + Emoji + BlockA + "b", BlockEditorCaret.InsertAtEditorPosition(text, afterEmoji, BlockA, 500).Text);
    }

    [Fact]
    public void A_byte_offset_inside_a_multi_byte_sequence_snaps_back_to_the_sequence_start()
    {
        var text = "a" + Emoji + "b";
        for (var mid = 2; mid <= 4; mid++) // bytes 2..4 are inside the 4-byte emoji
        {
            var observation = BlockEditorCaret.Observe(Utf8(text), cursorBytes: mid, selectionStartBytes: 0, selectionEndBytes: 0);
            Assert.Equal(1, observation.Selection.Start);
            var result = BlockEditorCaret.InsertAtEditorPosition(text, observation, BlockA, 500).Text;
            Assert.Equal("a" + BlockA + Emoji + "b", result);
        }
    }

    [Fact]
    public void A_char_index_between_surrogates_never_splits_the_pair()
    {
        var text = "a" + Emoji + "b"; // index 2 is between the two surrogate halves
        Assert.Equal("a" + BlockA + Emoji + "b", BlockTextEditor.Insert(text, 2, 2, BlockA, 500).Text); // caret snaps back
        Assert.Equal("a" + BlockA + "b", BlockTextEditor.Insert(text, 2, 3, BlockA, 500).Text);          // selection covers the whole pair
        Assert.Equal("a" + BlockA + "b", BlockTextEditor.Insert(text, 1, 2, BlockA, 500).Text);
    }

    [Fact]
    public void Existing_block_glyphs_before_and_after_the_caret_stay_intact()
    {
        var text = BlockA + BlockB + " " + BlockC;
        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, Frame(text, 3), BlockA, 500);
        Assert.Equal(BlockA + BlockB + " " + BlockA + BlockC, outcome.Text);
    }

    [Fact]
    public void The_resulting_caret_is_immediately_after_the_inserted_block_in_chars_and_in_bytes()
    {
        var text = "éñ" + BlockB + Emoji;
        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, Frame(text, 3), BlockA, 500);

        Assert.Equal("éñ" + BlockB + BlockA + Emoji, outcome.Text);
        Assert.Equal(BlockEditorSelection.Caret(4), outcome.Selection); // é ñ B A
        Assert.Equal(4 + 3 + 3, BlockEditorCaret.ToByteOffset(outcome.Text, outcome.Selection.End)); // what ImGui is told
    }

    [Fact]
    public void ToByteOffset_never_lands_inside_a_surrogate_pair()
    {
        var text = "a" + Emoji + "b";
        Assert.Equal(1, BlockEditorCaret.ToByteOffset(text, 2)); // between the halves -> before the emoji
        Assert.Equal(5, BlockEditorCaret.ToByteOffset(text, 3));
    }

    // --- Byte accounting ----------------------------------------------------------------------------------------

    [Fact]
    public void Resulting_text_bytes_reflect_the_actual_composition_after_insertion()
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition("ABCDEF", Frame("ABCDEF", 3), BlockA, 500);
        Assert.Equal(6 + 3, BlockTextLength.CountBytes(outcome.Text));
    }

    [Fact]
    public void Replacing_a_selection_counts_the_removed_bytes_as_freed()
    {
        // 8 bytes of text, limit 10: appending a 3-byte block would be 11 (rejected), but replacing 6 selected bytes
        // leaves 2 + 3 = 5 (fits).
        var text = "abcdefgh";
        var atEnd = BlockEditorCaret.InsertAtEditorPosition(text, Frame(text, 8), BlockA, 10);
        Assert.False(atEnd.Inserted);
        Assert.Equal(text, atEnd.Text);

        var selection = BlockEditorCaret.Observe(Utf8(text), cursorBytes: 8, selectionStartBytes: 2, selectionEndBytes: 8);
        var replaced = BlockEditorCaret.InsertAtEditorPosition(text, selection, BlockA, 10);
        Assert.True(replaced.Inserted);
        Assert.Equal("ab" + BlockA, replaced.Text);
        Assert.True(BlockTextLength.CountBytes(replaced.Text) <= 10);
    }

    [Fact]
    public void A_block_that_does_not_fit_leaves_the_text_and_the_selection_untouched()
    {
        // 9 bytes, limit 10, select 1 byte: budget for the insertion is 2 bytes; a 3-byte block cannot fit. The
        // selected text must NOT be deleted-and-not-replaced, and the selection must survive.
        var text = "abcdefghi";
        var selection = BlockEditorCaret.Observe(Utf8(text), cursorBytes: 5, selectionStartBytes: 4, selectionEndBytes: 5);
        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, selection, BlockA, 10);

        Assert.False(outcome.Inserted);
        Assert.True(outcome.Truncated);
        Assert.Equal(text, outcome.Text);
        Assert.Equal(new BlockEditorSelection(4, 5), outcome.Selection);
    }

    [Fact]
    public void An_already_over_limit_composition_rejects_the_insertion_unchanged()
    {
        var text = new string('a', 20);
        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, Frame(text, 10), BlockA, 10);
        Assert.False(outcome.Inserted);
        Assert.Equal(text, outcome.Text);
    }

    [Fact]
    public void Insertion_exactly_filling_the_limit_is_accepted()
    {
        var text = new string('a', 7);
        var outcome = BlockEditorCaret.InsertAtEditorPosition(text, Frame(text, 7), BlockA, 10);
        Assert.True(outcome.Inserted);
        Assert.Equal(10, BlockTextLength.CountBytes(outcome.Text));
    }
}
