namespace VenueOS.Modules.Operations.Shouts;

/// <summary>Pure, ImGui-free row-wrapping logic for the Live module's Shout slot grid (0.3.5 layout hotfix — before
/// this, every visible slot was placed on one unconditional row via <c>ImGui.SameLine()</c> regardless of count).
/// Fixed at <see cref="MaxSlotsPerRow"/> slots per row; a future UI-tightening pass may make this responsive to the
/// Live module's actual available width, but that is explicitly out of scope for this hotfix — see
/// docs/SHOUTS_IMPLEMENTATION.md's layout hotfix note.
///
/// This operates purely on POSITION within the already-ordered <see cref="ShoutsService.VisibleSlots"/> sequence —
/// it never reorders, filters, or renumbers anything. A configuration of logical slots 1, 3, 4, 7, 8, 12, 15 is
/// still exactly that ascending sequence; this class only decides which of those seven visual positions start a new
/// row (indices 0 and 5, for <see cref="MaxSlotsPerRow"/> = 5).</summary>
public static class ShoutsLiveLayout
{
    public const int MaxSlotsPerRow = 5;

    /// <summary>True if the item at this zero-based position within the visible sequence should continue the
    /// current row (the caller should call <c>ImGui.SameLine()</c> before drawing it) rather than starting a new
    /// row. The first item of every row — including index 0 — always returns false, so there is never a stray
    /// leading <c>SameLine()</c> and never an extra blank row after an exact multiple of <paramref name="maxPerRow"/>.</summary>
    public static bool ContinuesRow(int index, int maxPerRow = MaxSlotsPerRow)
    {
        if (maxPerRow < 1) maxPerRow = 1;
        return index > 0 && index % maxPerRow != 0;
    }

    /// <summary>Splits a sequential count of visible items into row sizes of at most <paramref name="maxPerRow"/>
    /// (the final row holds whatever remainder is left) — e.g. 6 → [5, 1], 10 → [5, 5], 11 → [5, 5, 1]. Pure
    /// arithmetic, provided mainly so row-grouping logic has a directly assertable shape in tests without needing
    /// an ImGui context.</summary>
    public static IReadOnlyList<int> RowSizes(int count, int maxPerRow = MaxSlotsPerRow)
    {
        if (maxPerRow < 1) maxPerRow = 1;
        if (count <= 0) return Array.Empty<int>();

        var rows = new List<int>();
        var remaining = count;
        while (remaining > 0)
        {
            var take = Math.Min(maxPerRow, remaining);
            rows.Add(take);
            remaining -= take;
        }
        return rows;
    }
}
