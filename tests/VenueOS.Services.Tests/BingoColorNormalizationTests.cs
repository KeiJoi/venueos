using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

/// <summary>
/// Unit tests for <see cref="BingoColorNormalization"/> — the fix for the live "Copy Link" 400 "invalid color
/// value" failure. The backend's own <c>normalizeHex</c> (server.js) accepts only exactly 3 or 6 hex digits after
/// stripping a leading "#"; the donor plugin's <c>ColorToHex</c> always emits exactly 6. VenueOS's Settings color
/// field allowed up to 9 characters ("#" + up to 8 hex digits), so an 8-digit RGBA value sent unnormalized always
/// failed the backend's strict check. These tests prove the normalization is exact — not weakened — validation.
/// </summary>
public sealed class BingoColorNormalizationTests
{
    [Fact] public void Six_digit_hex_passes_through_uppercased()
        => Assert.Equal("1C2126", BingoColorNormalization.ToBackendHex("#1c2126"));

    [Fact] public void Three_digit_hex_is_kept_short_the_backend_expands_it_itself()
        => Assert.Equal("ABC", BingoColorNormalization.ToBackendHex("#abc"));

    [Fact] public void Eight_digit_rgba_has_its_trailing_alpha_dropped_not_rejected()
        // "121418FF" matches the donor's own SkinPreset RGBA convention (alpha last) — this is the exact shape
        // that produced the live 400 before this fix existed.
        => Assert.Equal("121418", BingoColorNormalization.ToBackendHex("#121418FF"));

    [Fact] public void Missing_leading_hash_is_tolerated()
        => Assert.Equal("336699", BingoColorNormalization.ToBackendHex("336699"));

    [Fact] public void Blank_or_whitespace_input_normalizes_to_null_not_an_empty_string()
    {
        Assert.Null(BingoColorNormalization.ToBackendHex(null));
        Assert.Null(BingoColorNormalization.ToBackendHex(""));
        Assert.Null(BingoColorNormalization.ToBackendHex("   "));
    }

    [Theory]
    [InlineData("#GGGGGG")] // non-hex characters
    [InlineData("#12345")] // 5 digits — not 3, 6, or 8
    [InlineData("#1234567")] // 7 digits
    [InlineData("red")] // a CSS color name, not hex
    public void Truly_unparseable_values_normalize_to_null_rather_than_being_forwarded_malformed(string value)
        => Assert.Null(BingoColorNormalization.ToBackendHex(value));

    [Fact] public void Result_is_always_exactly_the_backends_accepted_3_or_6_digit_shape()
    {
        foreach (var value in new[] { "#1c2126", "#abc", "#121418FF", "336699" })
        {
            var normalized = BingoColorNormalization.ToBackendHex(value);
            Assert.NotNull(normalized);
            Assert.True(normalized!.Length is 3 or 6, $"'{value}' normalized to '{normalized}', which is not 3 or 6 hex digits");
            Assert.All(normalized, c => Assert.True(Uri.IsHexDigit(c)));
        }
    }
}
