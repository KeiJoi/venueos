using System.Text;

namespace VenueOS.Modules.Operations.BlockLetters;

/// <summary>A caret or selection expressed as C# (UTF-16) <see cref="string"/> character indices into the
/// composition, normalized so <see cref="Start"/> &lt;= <see cref="End"/>. An empty range (<see cref="IsCaret"/>) is a
/// plain caret position.</summary>
public readonly record struct BlockEditorSelection(int Start, int End)
{
    public bool IsCaret => Start == End;

    public static BlockEditorSelection Caret(int index) => new(index, index);
}

/// <summary>What the composition editor last reported about itself: the caret/selection, plus the length (in UTF-16
/// characters) of the exact text it was reported against. The length is what lets <see cref="BlockEditorCaret.Resolve"/>
/// refuse to trust a caret that belongs to a different version of the text.</summary>
public readonly record struct BlockEditorObservation(BlockEditorSelection Selection, int TextLength);

/// <summary>The outcome of one palette insertion: the new text, the caret/selection the editor should end up with, and
/// how the insertion position was chosen. <see cref="Inserted"/> is false when nothing fit the byte budget and the
/// composition (and selection) were left exactly as they were.</summary>
public readonly record struct BlockInsertOutcome(string Text, BlockEditorSelection Selection, bool Inserted, bool Truncated, bool UsedEndFallback);

/// <summary>Pure, ImGui-free translation of Dear ImGui's <c>InputText</c> callback state into a trustworthy caret, and
/// the palette-insertion decision built on it. Everything here is exercised without ImGui; only the act of reading
/// the callback data and restoring focus lives in the operator panel.
///
/// <para>Verified against the game's native ImGui (1.88, via Dalamud.Bindings.ImGui), <c>CallbackAlways</c> data: the
/// caret is <c>CursorPos</c>. <c>SelectionStart</c>/<c>SelectionEnd</c> are only meaningful while they DIFFER (a real
/// selection — then <c>CursorPos == SelectionEnd</c>, and a backwards drag reports Start &gt; End). Typing advances
/// <c>CursorPos</c> but leaves <c>SelectionStart == SelectionEnd</c> frozen at wherever the last click/selection
/// change/replaced selection left them, so an equal pair is a stale echo, never a caret. All three values are UTF-8
/// byte offsets into the widget's buffer, not <see cref="string"/> indices.</para></summary>
public static class BlockEditorCaret
{
    /// <summary>Builds the caret/selection from one <c>CallbackAlways</c> snapshot. <paramref name="utf8Text"/> is the
    /// buffer the offsets index into (<c>BufTextSpan</c>).</summary>
    public static BlockEditorObservation Observe(ReadOnlySpan<byte> utf8Text, int cursorBytes, int selectionStartBytes, int selectionEndBytes)
    {
        var hasSelection = selectionStartBytes != selectionEndBytes;
        var startBytes = hasSelection ? Math.Min(selectionStartBytes, selectionEndBytes) : cursorBytes;
        var endBytes = hasSelection ? Math.Max(selectionStartBytes, selectionEndBytes) : cursorBytes;

        var selection = new BlockEditorSelection(ToCharIndex(utf8Text, startBytes), ToCharIndex(utf8Text, endBytes));
        return new BlockEditorObservation(selection, Encoding.UTF8.GetCharCount(utf8Text));
    }

    /// <summary>The observed selection if — and only if — it still describes <paramref name="composition"/>; otherwise
    /// null, meaning "no trustworthy caret". An observation of a different-length text (a Clear, a venue switch, any
    /// mutation the editor did not itself report) is never applied to the current one.</summary>
    public static BlockEditorSelection? Resolve(BlockEditorObservation? observation, string composition)
    {
        if (observation is not { } observed) return null;
        if (observed.TextLength != composition.Length) return null;
        if (observed.Selection.Start < 0 || observed.Selection.End > composition.Length || observed.Selection.Start > observed.Selection.End) return null;
        return observed.Selection;
    }

    /// <summary>Inserts <paramref name="insertion"/> at the editor's current caret, replacing the selection if there
    /// is one. With no trustworthy caret (<see cref="Resolve"/> returned null) it appends at the end of the CURRENT
    /// composition — never at a position remembered from an earlier insertion. The byte budget is the same one
    /// <see cref="BlockTextEditor.Insert"/> enforces, counting the bytes of a replaced selection as freed.</summary>
    public static BlockInsertOutcome InsertAtEditorPosition(string composition, BlockEditorObservation? observation, string insertion, int maxBytes)
    {
        var resolved = Resolve(observation, composition);
        var usedFallback = resolved is null;
        var selection = resolved ?? BlockEditorSelection.Caret(composition.Length);

        var result = BlockTextEditor.Insert(composition, selection.Start, selection.End, insertion, maxBytes);
        var rejected = result.Truncated && string.Equals(result.Text, composition, StringComparison.Ordinal);
        return rejected
            ? new BlockInsertOutcome(composition, selection, Inserted: false, Truncated: true, usedFallback)
            : new BlockInsertOutcome(result.Text, BlockEditorSelection.Caret(result.CursorPos), Inserted: true, result.Truncated, usedFallback);
    }

    /// <summary>The UTF-8 byte offset of a <see cref="string"/> character index — what ImGui expects back when the
    /// caret is set through <c>CursorPos</c>/<c>SelectionStart</c>/<c>SelectionEnd</c>.</summary>
    public static int ToByteOffset(string text, int charIndex) => Utf8Offsets.ToByteOffset(text, SnapToScalarBoundary(text, charIndex, forward: false));

    /// <summary>Converts a byte offset to a character index, first snapping an offset that lands inside a multi-byte
    /// sequence back to that sequence's start (ImGui does not produce these; this keeps a corrupted offset from being
    /// counted as a replacement character).</summary>
    private static int ToCharIndex(ReadOnlySpan<byte> utf8, int byteOffset)
    {
        var offset = Math.Clamp(byteOffset, 0, utf8.Length);
        while (offset > 0 && offset < utf8.Length && (utf8[offset] & 0xC0) == 0x80) offset--;
        return Utf8Offsets.ToCharIndex(utf8, offset);
    }

    /// <summary>Moves a character index that falls between the two halves of a UTF-16 surrogate pair to the edge of
    /// that pair (back by default, forward for a selection's end) so an insertion never splits a scalar value.</summary>
    internal static int SnapToScalarBoundary(string text, int index, bool forward)
    {
        var clamped = Math.Clamp(index, 0, text.Length);
        if (clamped > 0 && clamped < text.Length && char.IsLowSurrogate(text[clamped]) && char.IsHighSurrogate(text[clamped - 1]))
            return forward ? clamped + 1 : clamped - 1;
        return clamped;
    }
}
