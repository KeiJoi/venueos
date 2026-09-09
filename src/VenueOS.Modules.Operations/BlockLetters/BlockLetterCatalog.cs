namespace VenueOS.Modules.Operations.BlockLetters;

/// <summary>How a <see cref="BlockGlyph"/> should be grouped in the palette.</summary>
public enum BlockGlyphCategory
{
    Letter,
    Digit,
    Punctuation,
    Other,
}

/// <summary><paramref name="Label"/> is what the palette button/tooltip shows; <paramref name="Value"/> is the
/// exact string inserted into the composition (a single FFXIV Private Use Area character). <paramref name="Codepoint"/>
/// is kept alongside for tooltips/diagnostics even though it's recoverable from <paramref name="Value"/>.</summary>
public sealed record BlockGlyph(string Label, string Value, BlockGlyphCategory Category, int Codepoint);

/// <summary>The FFXIV "block letter" glyph set, researched from the public tool at ffxivalpha.com (2026-09-08 — see
/// docs/BLOCK_LETTERS_IMPLEMENTATION.md for the full research record). Every codepoint below was extracted directly
/// from that site's client-side character-mapping table (a-z/0-9/?/+ → U+E070-E0AF) and its separate "Other
/// Characters" bank (85 further glyphs), not guessed from visual appearance. All codepoints sit in FFXIV's own
/// Private Use Area symbol range and render correctly in-game via the client's own AXIS-family font — no bundled
/// font asset is required for VenueOS to reproduce the same glyphs the site produces.
///
/// ffxivalpha.com exposes a single, case-insensitive "block letters" style (typed/clicked lowercase and uppercase
/// both resolve to the same 26 glyphs) — there is no separate uppercase/lowercase glyph pair to expose, so this
/// catalog deliberately has one entry per letter, not two.</summary>
public static class BlockLetterCatalog
{
    public static IReadOnlyList<BlockGlyph> Letters { get; } = BuildLetters();
    public static IReadOnlyList<BlockGlyph> Digits { get; } = BuildDigits();
    public static IReadOnlyList<BlockGlyph> Punctuation { get; } = BuildPunctuation();
    public static IReadOnlyList<BlockGlyph> Other { get; } = BuildOther();

    public static IReadOnlyList<BlockGlyph> All { get; } =
        [.. Letters, .. Digits, .. Punctuation, .. Other];

    private static BlockGlyph FromHex(string label, string hex, BlockGlyphCategory category)
    {
        var codepoint = Convert.ToInt32(hex, 16);
        return new BlockGlyph(label, ((char)codepoint).ToString(), category, codepoint);
    }

    private static IReadOnlyList<BlockGlyph> BuildLetters()
    {
        // a-z -> U+E071-U+E08A, in alphabetical order (ffxivalpha.com's own button order is QWERTY-row order; this
        // catalog orders alphabetically for a predictable palette grid instead of reproducing that donor UX choice).
        string[] hex =
        [
            "E071", "E072", "E073", "E074", "E075", "E076", "E077", "E078", "E079", "E07A",
            "E07B", "E07C", "E07D", "E07E", "E07F", "E080", "E081", "E082", "E083", "E084",
            "E085", "E086", "E087", "E088", "E089", "E08A",
        ];
        var glyphs = new BlockGlyph[26];
        for (var i = 0; i < 26; i++)
        {
            var letter = (char)('A' + i);
            glyphs[i] = FromHex(letter.ToString(), hex[i], BlockGlyphCategory.Letter);
        }
        return glyphs;
    }

    private static IReadOnlyList<BlockGlyph> BuildDigits()
    {
        // 0-9 -> U+E08F-E098.
        string[] hex = ["E08F", "E090", "E091", "E092", "E093", "E094", "E095", "E096", "E097", "E098"];
        var glyphs = new BlockGlyph[10];
        for (var i = 0; i < 10; i++)
            glyphs[i] = FromHex(i.ToString(), hex[i], BlockGlyphCategory.Digit);
        return glyphs;
    }

    private static IReadOnlyList<BlockGlyph> BuildPunctuation() =>
    [
        // Reachable only via ffxivalpha.com's keyboard-intercept toggle (no dedicated button there) — VenueOS gives
        // both their own palette buttons since we have the verified codepoints either way.
        FromHex("?", "E070", BlockGlyphCategory.Punctuation),
        FromHex("+", "E0AF", BlockGlyphCategory.Punctuation),
    ];

    private static IReadOnlyList<BlockGlyph> BuildOther()
    {
        // ffxivalpha.com's "Other Characters" tab: 85 further FFXIV symbol-font glyphs with no semantic name in the
        // site's own source (no label to reproduce) — labeled here by hex codepoint, which is honest (we don't know
        // what these depict beyond the rendered glyph itself) and stable. Order matches the site's own array order.
        string[] hex =
        [
            "E099", "E09A", "E09B", "E09C", "E09D", "E09E", "E09F",
            "E0B1", "E0B2", "E0B3", "E0B4", "E0B5", "E0B6", "E0B7", "E0B8", "E0B9",
            "E061", "E062", "E063", "E064", "E065", "E066", "E067", "E068", "E069",
            "E020", "E021", "E023", "E024", "E025", "E026", "E027",
            "E031", "E032", "E033", "E034", "E035", "E038", "E039", "E03A", "E03B", "E03C", "E03D", "E03E",
            "E040", "E041", "E042", "E043", "E044", "E048", "E049", "E04A", "E04B", "E04C", "E04D", "E04E",
            "E050", "E051", "E052", "E053", "E054", "E055", "E056", "E057", "E058", "E059", "E05A", "E05B",
            "E05C", "E05D", "E05E", "E05F", "E060", "E06A", "E06B", "E06C", "E06D", "E06E", "E06F",
            "E0BA", "E0BB", "E0BC", "E0BD", "E0BE", "E0BF",
        ];
        var glyphs = new BlockGlyph[hex.Length];
        for (var i = 0; i < hex.Length; i++)
            glyphs[i] = FromHex(hex[i], hex[i], BlockGlyphCategory.Other);
        return glyphs;
    }
}
