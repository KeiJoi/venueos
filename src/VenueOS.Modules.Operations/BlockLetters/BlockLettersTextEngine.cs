using System.Text;

namespace VenueOS.Modules.Operations.BlockLetters;

/// <summary>The FFXIV text destination a composed message is ultimately headed for. Chat's eight channels (Say,
/// Yell, Shout, Party, Tell, Free Company, Linkshell, CWLS) share one relevant limit per the product decision behind
/// this module, so they are represented as the single <see cref="Chat"/> value rather than eight identical entries.
/// Party Finder's only free-text field is its recruitment Comment — every other Party Finder field is an
/// enum/toggle, not something this composer targets.</summary>
public enum BlockLettersDestination
{
    Chat,
    PartyFinderComment,
    MacroLine,
}

/// <summary>Researched, verified FFXIV text-length limits — see docs/BLOCK_LETTERS_IMPLEMENTATION.md §6 for full
/// sourcing. All three are modeled as UTF-8 byte counts (see <see cref="BlockTextLength"/>):
/// <list type="bullet">
/// <item><see cref="ChatBytes"/> = 500 — corroborated by a Square Enix forum thread documenting a firsthand-tested
/// hard 500-character input stop, and independently by Dalamud's own chat-entry reimplementation allocating a
/// 500-byte input buffer (goatcorp/Dalamud#862). FFXIV's client string representation is UTF-8 (SeString)
/// throughout, consistent with a byte-based count, though no source directly demonstrates a multi-byte character
/// consuming more than one unit — flagged for live verification.</item>
/// <item><see cref="PartyFinderCommentBytes"/> = 192 — the highest-confidence of the three: it is the value
/// VenueOS's own already-reconstructed Party Finder module (<c>PartyFinderModels.TruncateCommentUtf8</c>) already
/// uses in production, sourced from the donor project's live "{byteCount}/192 bytes" counter reverse-engineered
/// against the real game client. Explicitly UTF-8 bytes, not characters.</item>
/// <item><see cref="MacroLineBytes"/> = 181 — corroborated by two independent Square Enix forum threads citing the
/// same figure (181 chars/line × 15 lines = 2,715 total), both clearly wrong-guess-free (not 150/255). Its counting
/// unit for non-ASCII content specifically was not directly verified by any source (all firsthand testing used
/// plain ASCII macro commands); UTF-8 bytes is used here for consistency with the other two, confirmed, byte-based
/// destinations and FFXIV's uniformly UTF-8 client architecture — flagged for live verification.</item>
/// </list></summary>
public static class BlockLettersLimits
{
    public const int ChatBytes = 500;
    public const int PartyFinderCommentBytes = 192;
    public const int MacroLineBytes = 181;

    public static int MaxBytes(BlockLettersDestination destination) => destination switch
    {
        BlockLettersDestination.Chat => ChatBytes,
        BlockLettersDestination.PartyFinderComment => PartyFinderCommentBytes,
        BlockLettersDestination.MacroLine => MacroLineBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    public static string DisplayName(BlockLettersDestination destination) => destination switch
    {
        BlockLettersDestination.Chat => "Chat",
        BlockLettersDestination.PartyFinderComment => "Party Finder",
        BlockLettersDestination.MacroLine => "Macro Line",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    public static bool ExceedsLimit(string text, BlockLettersDestination destination) =>
        BlockTextLength.CountBytes(text) > MaxBytes(destination);
}

/// <summary>The single measurement VenueOS uses for FFXIV text-limit accounting: UTF-8 byte length, matching how
/// FFXIV's own client (and VenueOS's already-verified Party Finder comment counter) represents and measures text —
/// never <c>string.Length</c> (UTF-16 code units), which would under-count every block-letter glyph in this module
/// (each is a 3-byte UTF-8 sequence but a single UTF-16 code unit).</summary>
public static class BlockTextLength
{
    public static int CountBytes(string text) => Encoding.UTF8.GetByteCount(text);
}

/// <summary>Bridges an ImGui <c>ImGuiInputTextCallbackData</c> byte offset (Dear ImGui indexes CursorPos/
/// SelectionStart/SelectionEnd into its own UTF-8 <c>Buf</c>, not into a UTF-16 <see cref="string"/>) to a C# string
/// character index. ImGui only ever produces offsets that land on a UTF-8 sequence boundary (it manages its own
/// cursor movement in whole-codepoint steps), so this never has to guess mid-sequence.</summary>
public static class Utf8Offsets
{
    public static int ToCharIndex(ReadOnlySpan<byte> utf8, int byteOffset)
    {
        var clamped = Math.Clamp(byteOffset, 0, utf8.Length);
        return Encoding.UTF8.GetCharCount(utf8[..clamped]);
    }

    /// <summary>The inverse of <see cref="ToCharIndex"/> — how many UTF-8 bytes the first <paramref name="charIndex"/>
    /// characters of <paramref name="text"/> encode to. Used after mutating ImGui's buffer via InsertChars/DeleteChars
    /// to report a new cursor position back in the byte offset ImGui itself expects.</summary>
    public static int ToByteOffset(string text, int charIndex)
    {
        var clamped = Math.Clamp(charIndex, 0, text.Length);
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, clamped));
    }
}

/// <summary>The result of an insertion/replacement attempt against the composition text.</summary>
public readonly record struct BlockTextEditResult(string Text, int CursorPos, bool Truncated);

/// <summary>Pure, ImGui-free text-editing logic: insert-or-replace-selection with a hard UTF-8 byte budget, the one
/// piece of behavior NEW_MODULE_GUIDE.md §4/§7 requires to be real cursor/selection-aware editing (never
/// append-only) and to never silently exceed or split a glyph. Used identically for typed/pasted characters and for
/// palette-button glyph insertion — see BlockLettersOperatorPanel for how ImGui's callback data is bridged into
/// these calls.</summary>
public static class BlockTextEditor
{
    /// <summary>Replaces the text between <paramref name="selectionStart"/> and <paramref name="selectionEnd"/>
    /// (a zero-length selection is a plain cursor position) with as much of <paramref name="insertion"/> as fits
    /// within <paramref name="maxBytes"/> total, without ever splitting a Unicode scalar value. If nothing at all
    /// fits, the original text is returned unchanged with <see cref="BlockTextEditResult.Truncated"/> set whenever
    /// the insertion was non-empty.</summary>
    public static BlockTextEditResult Insert(string current, int selectionStart, int selectionEnd, string insertion, int maxBytes)
    {
        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, current.Length);

        var prefix = current[..start];
        var suffix = current[end..];
        var budgetForInsertion = maxBytes - BlockTextLength.CountBytes(prefix) - BlockTextLength.CountBytes(suffix);

        if (budgetForInsertion <= 0 || insertion.Length == 0)
            return new BlockTextEditResult(current, start, budgetForInsertion <= 0 && insertion.Length > 0);

        var fitted = FitWithinByteBudget(insertion, budgetForInsertion);
        var newText = prefix + fitted + suffix;
        return new BlockTextEditResult(newText, start + fitted.Length, fitted.Length < insertion.Length);
    }

    /// <summary>Truncates <paramref name="text"/> to the longest whole-scalar-value prefix whose UTF-8 byte length
    /// does not exceed <paramref name="maxBytes"/>. Enumerating by <see cref="System.Text.Rune"/> guarantees a
    /// surrogate pair (or any multi-byte sequence) is taken or dropped as a unit, never split.</summary>
    private static string FitWithinByteBudget(string text, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (BlockTextLength.CountBytes(text) <= maxBytes) return text;

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (used + runeBytes > maxBytes) break;
            builder.Append(rune.ToString());
            used += runeBytes;
        }
        return builder.ToString();
    }
}
