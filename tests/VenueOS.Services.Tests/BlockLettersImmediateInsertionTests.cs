using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Services.Tests;

/// <summary>Live-QA fix coverage: a palette-button click (BlockLettersOperatorPanel.InsertGlyph) mutates the
/// authoritative composition string DIRECTLY and SYNCHRONOUSLY through <see cref="BlockTextEditor.Insert"/> — no
/// queue, no "wait for the next ImGui callback" step. That queue-based design is exactly what caused the live bug
/// (ImGuiInputTextFlags.CallbackAlways only fires while the InputText widget is ImGui's active item, and a button
/// click moves active-item status away from the widget, so a queued insertion was never applied until the operator
/// clicked back into the box). These tests exercise the panel's actual sequential call pattern — feed each call's
/// result straight into the next call's arguments, exactly as InsertGlyph does — without ever touching ImGui, to
/// prove the underlying operation has no callback/event dependency at all. The operator panel itself is not
/// unit-testable (no VenueOS.Plugin test project — NEW_MODULE_GUIDE.md §30); font rendering is live-ImGui behavior
/// and is not asserted here.</summary>
public sealed class BlockLettersImmediateInsertionTests
{
    private static readonly string BlockA = char.ConvertFromUtf32(0xE071); // catalog "A"
    private static readonly string BlockB = char.ConvertFromUtf32(0xE072); // catalog "B"
    private static readonly string BlockC = char.ConvertFromUtf32(0xE073); // catalog "C"

    [Fact]
    public void A_single_call_mutates_the_composition_synchronously_no_deferred_step()
    {
        var result = BlockTextEditor.Insert("", 0, 0, BlockA, 500);
        // The mutated text is available on the return value of the very call that performed the insertion — nothing
        // else needs to run afterward for it to be correct/current.
        Assert.Equal(BlockA, result.Text);
    }

    [Fact]
    public void Insertion_does_not_require_any_intervening_editor_input_event()
    {
        // Two insertions back to back, with nothing resembling an ImGui callback/event run between them — proving
        // the operation itself has no such dependency (unlike the old pending-insertion design, which needed
        // CallbackAlways to fire between a button click and the mutation actually landing).
        var first = BlockTextEditor.Insert("", 0, 0, BlockA, 500);
        var second = BlockTextEditor.Insert(first.Text, first.CursorPos, first.CursorPos, BlockB, 500);
        Assert.Equal(BlockA + BlockB, second.Text);
    }

    [Fact]
    public void Insert_at_end_using_the_stored_cursor()
    {
        var result = BlockTextEditor.Insert("Hello", 5, 5, BlockA, 500);
        Assert.Equal("Hello" + BlockA, result.Text);
        Assert.Equal(6, result.CursorPos);
    }

    [Fact]
    public void Insert_at_a_stored_mid_text_cursor_position()
    {
        var result = BlockTextEditor.Insert("VENUE OPEN", 5, 5, BlockA, 500);
        Assert.Equal("VENUE" + BlockA + " OPEN", result.Text);
        Assert.Equal(6, result.CursorPos);
    }

    [Fact]
    public void Insert_replaces_a_stored_selection()
    {
        var result = BlockTextEditor.Insert("Hello World", 6, 11, BlockA, 500);
        Assert.Equal("Hello " + BlockA, result.Text);
        Assert.Equal(7, result.CursorPos);
    }

    [Fact]
    public void Repeated_palette_clicks_each_insert_immediately_and_chain_correctly()
    {
        // Simulates clicking A, then B, then C in a row — each click's result feeds directly into the next click's
        // starting state, exactly as BlockLettersOperatorPanel.InsertGlyph does via selectionStartChars/EndChars.
        var afterA = BlockTextEditor.Insert("", 0, 0, BlockA, 500);
        var afterB = BlockTextEditor.Insert(afterA.Text, afterA.CursorPos, afterA.CursorPos, BlockB, 500);
        var afterC = BlockTextEditor.Insert(afterB.Text, afterB.CursorPos, afterB.CursorPos, BlockC, 500);

        Assert.Equal(BlockA + BlockB + BlockC, afterC.Text);
        Assert.Equal(3, afterC.CursorPos);
    }

    [Fact]
    public void Immediate_insertion_still_obeys_the_destination_byte_limit()
    {
        var current = new string('a', BlockLettersLimits.MacroLineBytes); // already at the Macro Line limit
        var result = BlockTextEditor.Insert(current, current.Length, current.Length, BlockA, BlockLettersLimits.MacroLineBytes);
        Assert.Equal(current, result.Text); // the 3-byte glyph could not fit — rejected, not partially inserted
        Assert.True(result.Truncated);
    }

    [Fact]
    public void A_rejected_immediate_insertion_leaves_the_composition_unchanged_right_away()
    {
        var current = new string('a', 500);
        var result = BlockTextEditor.Insert(current, 500, 500, BlockA, 500);
        Assert.Equal(current, result.Text);
        Assert.Equal(BlockTextLength.CountBytes(current), BlockTextLength.CountBytes(result.Text));
    }

    [Fact]
    public void Copy_would_see_the_freshly_inserted_glyph_immediately()
    {
        // "Copy" in the real panel just reads the `composition` field directly — proving the insertion result IS
        // the value that field takes on is exactly what makes Copy correct with no extra synchronization step.
        var result = BlockTextEditor.Insert("VENUE OPEN", 10, 10, BlockA, 500);
        var wouldBeCopied = result.Text; // stands in for `composition = result.Text;` in InsertGlyph
        Assert.Equal("VENUE OPEN" + BlockA, wouldBeCopied);
    }

    [Fact]
    public void Byte_count_display_reflects_the_newly_inserted_composition_immediately()
    {
        var before = BlockTextLength.CountBytes("VENUE OPEN");
        var result = BlockTextEditor.Insert("VENUE OPEN", 10, 10, BlockA, 500);
        var after = BlockTextLength.CountBytes(result.Text); // stands in for the panel's `currentBytes` recomputation
        Assert.Equal(before + 3, after); // one 3-byte glyph added
    }
}
