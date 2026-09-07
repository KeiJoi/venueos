namespace VenueOS.Services;

/// <summary>One word, or one hard-split chunk of an over-width word, ready to be drawn. <see cref="RunIndex"/> maps
/// back to the caller's original list of styled inline runs (bold/code/link) so a single wrapped line can still mix
/// styles from different runs.</summary>
public sealed record ManualTextPiece(string Text, int RunIndex, float Width);

public sealed record ManualTextLine(IReadOnlyList<ManualTextPiece> Pieces);

/// <summary>Pure word-wrap layout for the User Manual reader's inline text (paragraphs, list items, table cells,
/// blockquotes) — no ImGui dependency, so the width math is unit-testable independent of live rendering.
///
/// Extracted specifically because the 0.2.2 wrapping hotfix's root cause was ImGui cursor-state bookkeeping: the
/// renderer used to ask <c>ImGui.GetCursorPosX()</c> to find "how far along the current line is", but every ImGui
/// item (including the <c>Dummy</c> calls used to place each word) resets the cursor to the window's left margin on
/// the line after it, regardless of whether that item was itself placed via <c>SameLine()</c>. That made the wrap
/// check compare "left margin + one word's width" against the right edge — nearly always true-enough to never
/// trigger a wrap — while the actual placement (which uses ImGui's own internal previous-line tracking, unrelated
/// to <c>GetCursorPosX()</c>) kept chaining every word onto one ever-widening line. This type has no cursor state at
/// all — just arithmetic over caller-supplied word widths — so that class of bug cannot recur here.</summary>
public static class ManualTextLayout
{
    /// <summary><paramref name="words"/> is a flattened (text, originating-run-index) sequence — already split on
    /// spaces by the caller, since word-splitting rules (Markdown inline runs) are the caller's concern, not this
    /// layout's. A non-positive <paramref name="availableWidth"/> is treated as unbounded (produces a single line)
    /// rather than dividing by, or endlessly comparing against, a zero/negative width. A single word wider than the
    /// available width is hard-split into multiple pieces spanning consecutive lines via <paramref name="measure"/>,
    /// rather than silently overflowing past the pane's edge or forcing the whole reader wider.</summary>
    public static IReadOnlyList<ManualTextLine> Layout(IReadOnlyList<(string Text, int RunIndex)> words, float availableWidth, float spaceWidth, Func<string, float> measure)
    {
        var safeWidth = availableWidth > 0 ? availableWidth : float.MaxValue;
        var lines = new List<ManualTextLine>();
        var current = new List<ManualTextPiece>();
        var lineWidthUsed = 0f;

        void FlushLine()
        {
            if (current.Count == 0) return;
            lines.Add(new ManualTextLine(current));
            current = [];
            lineWidthUsed = 0f;
        }

        foreach (var (text, runIndex) in words)
        {
            if (text.Length == 0) continue;
            var width = measure(text);

            if (width > safeWidth)
            {
                // Doesn't fit even alone on an empty line - flush whatever's pending, then hard-split this one
                // token across as many lines as it needs, each chunk guaranteed to fit.
                FlushLine();
                foreach (var chunk in SplitOverWidthToken(text, safeWidth, measure))
                {
                    current.Add(new ManualTextPiece(chunk, runIndex, measure(chunk)));
                    FlushLine();
                }
                continue;
            }

            var piece = current.Count == 0 ? width : spaceWidth + width;
            if (current.Count > 0 && lineWidthUsed + piece > safeWidth) { FlushLine(); piece = width; }
            current.Add(new ManualTextPiece(text, runIndex, width));
            lineWidthUsed += piece;
        }
        FlushLine();
        return lines;
    }

    private static IEnumerable<string> SplitOverWidthToken(string text, float availableWidth, Func<string, float> measure)
    {
        var remaining = text;
        while (remaining.Length > 0)
        {
            var chunkLength = remaining.Length;
            while (chunkLength > 1 && measure(remaining[..chunkLength]) > availableWidth) chunkLength--;
            yield return remaining[..chunkLength];
            remaining = remaining[chunkLength..];
        }
    }
}
