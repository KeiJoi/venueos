using VenueOS.Modules.Operations.Giveaways;

namespace VenueOS.Services.Tests;

/// <summary>
/// Pure text-parsing tests for <see cref="RandomRollParser"/> against the EXACT chat-log fixtures the user captured
/// live (GIVEAWAYS spec §14/§42). These prove the text-recognition layer only — they do not and cannot prove real
/// FFXIV/Chat2 structured-payload behavior; see <c>GiveawaysRollChatAdapter</c>'s doc comment and the completion
/// report for what still requires live verification.
/// </summary>
public sealed class RandomRollParserTests
{
    [Fact]
    public void Self_standard_random_is_parsed()
    {
        var result = RandomRollParser.TryParse("Random! You Roll a 415.");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsSelf);
        Assert.Equal(415, result.Value.Value);
        Assert.Null(result.Value.OutOf);
        Assert.Equal(GiveawayRollKind.Standard, result.Value.Kind);
    }

    [Fact]
    public void Self_random_999_is_parsed()
    {
        var result = RandomRollParser.TryParse("Random! You Roll a 124 (out of 999).");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsSelf);
        Assert.Equal(124, result.Value.Value);
        Assert.Equal(999, result.Value.OutOf);
        Assert.Equal(GiveawayRollKind.RangedOutOf, result.Value.Kind);
    }

    // LIVE-QA REGRESSION FIX (Fix #2, Issue 2): the host's own /random was silently dropped in live FFXIV testing
    // even though a cross-world participant's roll was captured correctly. Forensic finding: the self pattern
    // required a literally-capitalized "You Roll a" with no RegexOptions.IgnoreCase, while the other-player pattern
    // already had that flag — cross-referenced against BingoRollCorrelation.cs's own already-live-verified
    // RollResultRegex for this SAME game-text family, which matches lowercase "you roll a" case-insensitively. These
    // two tests are regression tests for exactly that: the self pattern must match regardless of case.
    [Fact]
    public void Self_standard_random_is_parsed_case_insensitively_lowercase_form()
    {
        var result = RandomRollParser.TryParse("Random! You roll a 415.");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsSelf);
        Assert.Equal(415, result.Value.Value);
    }

    [Fact]
    public void Self_random_999_is_parsed_case_insensitively_lowercase_form()
    {
        var result = RandomRollParser.TryParse("Random! You roll a 124 (out of 999).");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsSelf);
        Assert.Equal(124, result.Value.Value);
        Assert.Equal(999, result.Value.OutOf);
    }

    [Fact]
    public void Other_player_standard_random_is_parsed()
    {
        var result = RandomRollParser.TryParse("Random! Poinsettia BloodlilyCuchulainn rolls a 65.");
        Assert.NotNull(result);
        Assert.False(result!.Value.IsSelf);
        Assert.Equal("Poinsettia BloodlilyCuchulainn", result.Value.OtherPlayerNameText);
        Assert.Equal(65, result.Value.Value);
        Assert.Null(result.Value.OutOf);
    }

    [Fact]
    public void Other_player_random_999_is_parsed()
    {
        var result = RandomRollParser.TryParse("Random! Poinsettia BloodlilyCuchulainn rolls a 65 (out of 999).");
        Assert.NotNull(result);
        Assert.False(result!.Value.IsSelf);
        Assert.Equal("Poinsettia BloodlilyCuchulainn", result.Value.OtherPlayerNameText);
        Assert.Equal(65, result.Value.Value);
        Assert.Equal(999, result.Value.OutOf);
        Assert.Equal(GiveawayRollKind.RangedOutOf, result.Value.Kind);
    }

    [Fact]
    public void Both_standard_and_ranged_forms_are_giveaway_eligible_with_the_same_resulting_value()
    {
        // GIVEAWAYS spec §15: a result of 65 can legitimately come from either /random or /random 999 — the
        // message TEXT decides which, never the resulting number (spec §15/§48 — checked explicitly below).
        var standard = RandomRollParser.TryParse("Random! Poinsettia BloodlilyCuchulainn rolls a 65.");
        var ranged = RandomRollParser.TryParse("Random! Poinsettia BloodlilyCuchulainn rolls a 65 (out of 999).");
        Assert.Equal(standard!.Value.Value, ranged!.Value.Value);
        Assert.NotEqual(standard.Value.Kind, ranged.Value.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("You said something in general chat.")]
    [InlineData("Random! rolls a 65.")] // no name captured — must not match with an empty name
    [InlineData("The weather looks nice today.")]
    [InlineData("Random! You rolled a 415.")] // wrong verb form entirely
    public void Malformed_or_unrelated_messages_are_ignored(string? text)
    {
        Assert.Null(RandomRollParser.TryParse(text));
    }

    [Fact]
    public void Null_text_is_ignored()
    {
        Assert.Null(RandomRollParser.TryParse(null));
    }
}
