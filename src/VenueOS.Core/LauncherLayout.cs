namespace VenueOS.Core;

/// <summary>Pure, ImGui-free row-wrapping math for the Module Launcher's button grid — mirrors
/// <c>VenueOS.Modules.Operations.Shouts.ShoutsLiveLayout</c>'s pattern exactly, but is an intentionally separate,
/// independent setting: the Launcher's <c>ButtonsPerRow</c> never shares a code path with
/// <c>ShoutsLiveLayout.MaxSlotsPerRow</c> (which stays fixed at 5), and changing one must never affect the
/// other.</summary>
public static class LauncherLayout
{
    /// <summary>True if the item at this zero-based position should continue the current row (the caller should
    /// call <c>ImGui.SameLine()</c> before drawing it) rather than starting a new one. The first item of every row —
    /// including index 0 — always returns false, so there is never a stray leading <c>SameLine()</c> and never an
    /// extra blank row after an exact multiple of <paramref name="buttonsPerRow"/>.</summary>
    public static bool ContinuesRow(int index, int buttonsPerRow)
    {
        if (buttonsPerRow < 1) buttonsPerRow = 1;
        return index > 0 && index % buttonsPerRow != 0;
    }

    /// <summary>Splits a sequential button count into row sizes of at most <paramref name="buttonsPerRow"/> (the
    /// final row holds whatever remainder is left) — e.g. 6 → [5, 1] for a per-row of 5, 10 → [5, 5], 11 → [5, 5, 1].
    /// Pure arithmetic, provided so the row-grouping shape is directly assertable in tests without an ImGui
    /// context.</summary>
    public static IReadOnlyList<int> RowSizes(int count, int buttonsPerRow)
    {
        if (buttonsPerRow < 1) buttonsPerRow = 1;
        if (count <= 0) return [];

        var rows = new List<int>();
        var remaining = count;
        while (remaining > 0)
        {
            var take = Math.Min(buttonsPerRow, remaining);
            rows.Add(take);
            remaining -= take;
        }
        return rows;
    }

    /// <summary>A launcher position that's almost certainly off any real monitor — used by the "Reset Launcher
    /// Position" recovery action (and the <c>/venueos launcher</c> command's own offscreen-safe fallback when
    /// turning the launcher back on) to decide whether the stored position needs resetting. Deliberately generous
    /// bounds — this is manual recovery for a genuinely broken value (NaN, a wildly out-of-range drag, a corrupted
    /// config edit), not general multi-monitor offscreen detection, which is explicitly out of scope (§38).</summary>
    public static bool LooksOffscreen(float x, float y) =>
        float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y) || x < -2000f || y < -2000f || x > 20000f || y > 20000f;
}
