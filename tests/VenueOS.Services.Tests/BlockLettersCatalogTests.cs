using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Services.Tests;

/// <summary>Verifies the researched ffxivalpha.com glyph mapping (docs/BLOCK_LETTERS_IMPLEMENTATION.md) was
/// transcribed into <see cref="BlockLetterCatalog"/> correctly and completely.</summary>
public sealed class BlockLettersCatalogTests
{
    [Fact]
    public void Contains_every_researched_letter_digit_and_punctuation_glyph()
    {
        Assert.Equal(26, BlockLetterCatalog.Letters.Count);
        Assert.Equal(10, BlockLetterCatalog.Digits.Count);
        Assert.Equal(2, BlockLetterCatalog.Punctuation.Count);
        Assert.Equal(85, BlockLetterCatalog.Other.Count);
        Assert.Equal(123, BlockLetterCatalog.All.Count);
    }

    [Fact]
    public void Labels_are_unique_within_each_group()
    {
        Assert.Equal(BlockLetterCatalog.Letters.Count, BlockLetterCatalog.Letters.Select(g => g.Label).Distinct().Count());
        Assert.Equal(BlockLetterCatalog.Digits.Count, BlockLetterCatalog.Digits.Select(g => g.Label).Distinct().Count());
        Assert.Equal(BlockLetterCatalog.Other.Count, BlockLetterCatalog.Other.Select(g => g.Label).Distinct().Count());
    }

    [Fact]
    public void No_two_catalog_entries_share_a_codepoint()
    {
        var codepoints = BlockLetterCatalog.All.Select(g => g.Codepoint).ToList();
        Assert.Equal(codepoints.Count, codepoints.Distinct().Count());
    }

    [Theory]
    [InlineData('A', 0xE071)]
    [InlineData('M', 0xE07D)]
    [InlineData('Z', 0xE08A)]
    public void Letter_glyphs_match_the_researched_mapping(char letter, int expectedCodepoint)
    {
        var glyph = BlockLetterCatalog.Letters.Single(g => g.Label == letter.ToString());
        Assert.Equal(expectedCodepoint, glyph.Codepoint);
        Assert.Equal(((char)expectedCodepoint).ToString(), glyph.Value);
    }

    [Theory]
    [InlineData(0, 0xE08F)]
    [InlineData(5, 0xE094)]
    [InlineData(9, 0xE098)]
    public void Digit_glyphs_match_the_researched_mapping(int digit, int expectedCodepoint)
    {
        var glyph = BlockLetterCatalog.Digits.Single(g => g.Label == digit.ToString());
        Assert.Equal(expectedCodepoint, glyph.Codepoint);
    }

    [Fact]
    public void Punctuation_glyphs_match_the_researched_mapping()
    {
        Assert.Equal(0xE070, BlockLetterCatalog.Punctuation.Single(g => g.Label == "?").Codepoint);
        Assert.Equal(0xE0AF, BlockLetterCatalog.Punctuation.Single(g => g.Label == "+").Codepoint);
    }

    [Fact]
    public void Every_glyph_value_is_a_single_character_encoding_its_own_codepoint()
    {
        Assert.All(BlockLetterCatalog.All, glyph =>
        {
            Assert.Equal(1, glyph.Value.Length);
            Assert.Equal(glyph.Codepoint, glyph.Value[0]);
        });
    }

    [Fact]
    public void Other_group_codepoints_never_collide_with_the_named_block_letters_range()
    {
        // The named "block letters" style occupies U+E070-E098 and U+E0AF; the "Other Characters" bank must stay
        // outside that range so a future mapping change can't silently redefine a named letter's glyph.
        Assert.All(BlockLetterCatalog.Other, glyph => Assert.False(glyph.Codepoint is >= 0xE070 and <= 0xE098 or 0xE0AF));
    }
}
