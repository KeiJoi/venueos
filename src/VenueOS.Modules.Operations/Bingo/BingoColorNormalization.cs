namespace VenueOS.Modules.Operations.Bingo;

/// <summary>
/// Pure hex-color normalization for the Bingo backend's appearance contract — no Dalamud dependency, fully
/// unit-testable. Root cause of the live "Copy Link" 400 "invalid color value" failure: the backend's own
/// <c>normalizeHex</c> (server.js) only ever accepts exactly 3 or 6 hex digits after stripping a leading "#" —
/// matching the donor plugin's own <c>ColorToHex</c>, which always emits exactly "RRGGBB" (no alpha) — but VenueOS's
/// color Settings field allowed typing up to 9 characters ("#" + up to 8 hex digits), and sent whatever was typed
/// straight through unnormalized. An 8-digit RGBA value (e.g. from a color picker convention like "1C2126FF") fails
/// the backend's strict 3-or-6-digit check outright, producing the 400.
///
/// This does NOT weaken the backend's validation (per the product correction: "the backend rejecting malformed
/// appearance data is useful") — it normalizes VenueOS's own appearance values into the exact format that
/// established contract already expects, applied both when a color is saved (so what's persisted is already clean)
/// and defensively again at the point a request is built (so a value already persisted before this fix still works
/// without requiring the operator to re-enter it in Settings).
/// </summary>
public static class BingoColorNormalization
{
    /// <summary>Normalizes an operator-entered or previously-persisted color value into the backend's exact
    /// accepted "RRGGBB" (or 3-digit "RGB", which the backend itself expands) hex format, or null if there is
    /// nothing to send (blank input) or the value cannot be interpreted as a color at all. An 8-digit RGBA value
    /// (alpha last, e.g. "RRGGBBAA" — matching the donor's own SkinPreset convention, e.g. "121418FF") has its
    /// alpha component dropped rather than being rejected, since the backend/browser color contract has no alpha
    /// channel at all (the hint "#RRGGBB" on the Settings field reflects that — alpha was never meaningful here).
    /// </summary>
    public static string? ToBackendHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().TrimStart('#');
        if (IsHex(cleaned, 8)) return cleaned[..6].ToUpperInvariant(); // RRGGBBAA -> RRGGBB, alpha dropped
        if (IsHex(cleaned, 6)) return cleaned.ToUpperInvariant();
        if (IsHex(cleaned, 3)) return cleaned.ToUpperInvariant(); // backend itself expands 3->6; kept short is fine
        return null;
    }

    private static bool IsHex(string value, int length)
    {
        if (value.Length != length) return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
