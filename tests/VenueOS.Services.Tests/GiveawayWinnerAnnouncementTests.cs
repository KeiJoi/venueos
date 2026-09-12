using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>
/// Pure-logic coverage for the Winner Announcement feature's grammar formatter and template resolver
/// (GIVEAWAYS Winner Announcement spec §29/§33/§35/§36) — no ImGui, no Dalamud, no <see cref="GiveawayService"/>
/// wiring. <see cref="GiveawayServiceTests"/> covers the eligibility lifecycle, chat dispatch, and snapshot/
/// persistence behavior that actually depends on the service.
/// </summary>
public sealed class GiveawayWinnerAnnouncementTests
{
    // =========================================================================================================
    // Name-list grammar (spec §7-§10/§29/§33)
    // =========================================================================================================

    [Fact]
    public void Zero_winners_formats_to_null()
    {
        Assert.Null(GiveawayWinnerNameFormatter.Format(Array.Empty<GuestIdentity>()));
    }

    [Fact]
    public void One_winner_formats_to_just_the_name()
    {
        Assert.Equal("Kei Joi", GiveawayWinnerNameFormatter.Format([new GuestIdentity("Kei Joi", "Kraken")]));
    }

    [Fact]
    public void Two_winners_join_with_and_and_no_comma()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("Kei Joi", "Kraken"), new GuestIdentity("Rabid Squirrel", "Balmung")]);
        Assert.Equal("Kei Joi and Rabid Squirrel", result);
    }

    [Fact]
    public void Three_winners_use_an_oxford_comma()
    {
        var result = GiveawayWinnerNameFormatter.Format([
            new GuestIdentity("Kei Joi", "Kraken"),
            new GuestIdentity("Rabid Squirrel", "Balmung"),
            new GuestIdentity("Mairwen Kor", "Zalera"),
        ]);
        Assert.Equal("Kei Joi, Rabid Squirrel, and Mairwen Kor", result);
    }

    [Fact]
    public void Four_winners_use_an_oxford_comma_list()
    {
        var result = GiveawayWinnerNameFormatter.Format([
            new GuestIdentity("Kei Joi", "Kraken"),
            new GuestIdentity("Rabid Squirrel", "Balmung"),
            new GuestIdentity("Mairwen Kor", "Zalera"),
            new GuestIdentity("Fiducia Ancilla", "Gilgamesh"),
        ]);
        Assert.Equal("Kei Joi, Rabid Squirrel, Mairwen Kor, and Fiducia Ancilla", result);
    }

    [Fact]
    public void Exactly_two_winners_never_gets_a_comma_before_and()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("A", "W1"), new GuestIdentity("B", "W2")]);
        Assert.DoesNotContain(", and", result);
        Assert.DoesNotContain(",", result);
    }

    [Fact]
    public void Three_or_more_always_carries_the_oxford_comma_before_and()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("A", "W1"), new GuestIdentity("B", "W2"), new GuestIdentity("C", "W3")]);
        Assert.Contains(", and", result);
    }

    [Fact]
    public void HomeWorld_is_never_included_in_the_formatted_list()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("Kei Joi", "Kraken")]);
        Assert.DoesNotContain("@", result);
        Assert.DoesNotContain("Kraken", result);
    }

    [Fact]
    public void Structured_identity_reduces_to_name_only_for_every_tied_winner()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("Kei Joi", "Kraken"), new GuestIdentity("Rabid Squirrel", "Balmung")]);
        Assert.Equal("Kei Joi and Rabid Squirrel", result);
    }

    [Fact]
    public void Stable_winner_order_is_preserved_not_sorted_alphabetically()
    {
        var result = GiveawayWinnerNameFormatter.Format([new GuestIdentity("Zed", "W1"), new GuestIdentity("Amy", "W2")]);
        Assert.Equal("Zed and Amy", result); // alphabetical would be "Amy and Zed" — must not silently reorder
    }

    [Fact]
    public void Duplicate_structured_identity_is_not_output_twice()
    {
        var duplicate = new GuestIdentity("Kei Joi", "Kraken");
        var result = GiveawayWinnerNameFormatter.Format([duplicate, duplicate, new GuestIdentity("Rabid Squirrel", "Balmung")]);
        Assert.Equal("Kei Joi and Rabid Squirrel", result);
    }

    // =========================================================================================================
    // Template resolution / <name> replacement (spec §5/§15-§18/§25-§27/§35)
    // =========================================================================================================

    private static readonly GuestIdentity[] OneWinner = [new("Kei Joi", "Kraken")];
    private static readonly GuestIdentity[] TwoWinners = [new("Kei Joi", "Kraken"), new("Rabid Squirrel", "Balmung")];
    private static readonly GuestIdentity[] ThreeWinners = [new("Kei Joi", "Kraken"), new("Rabid Squirrel", "Balmung"), new("Mairwen Kor", "Zalera")];

    [Fact]
    public void Name_token_replaces_with_the_single_winner()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Congratulations <name>! You won!", OneWinner);
        Assert.True(result.Success);
        Assert.Equal("Congratulations Kei Joi! You won!", result.Message);
    }

    [Fact]
    public void Name_token_replaces_with_the_two_winner_formatted_string()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Congratulations <name>! You are our winners!", TwoWinners);
        Assert.True(result.Success);
        Assert.Equal("Congratulations Kei Joi and Rabid Squirrel! You are our winners!", result.Message);
    }

    [Fact]
    public void Name_token_replaces_with_the_three_winner_formatted_string()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Winners: <name>!", ThreeWinners);
        Assert.True(result.Success);
        Assert.Equal("Winners: Kei Joi, Rabid Squirrel, and Mairwen Kor!", result.Message);
    }

    [Fact]
    public void Every_occurrence_of_the_name_token_is_replaced()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("<name> wins! Congratulations again to <name>!", TwoWinners);
        Assert.True(result.Success);
        Assert.Equal("Kei Joi and Rabid Squirrel wins! Congratulations again to Kei Joi and Rabid Squirrel!", result.Message);
    }

    [Fact]
    public void Template_without_the_name_token_is_still_valid_and_sent_unchanged()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Winners have been selected! Please come collect your prize!", OneWinner);
        Assert.True(result.Success);
        Assert.Equal("Winners have been selected! Please come collect your prize!", result.Message);
    }

    [Fact]
    public void Empty_template_is_invalid()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("", OneWinner);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Whitespace_only_template_is_invalid()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("   ", OneWinner);
        Assert.False(result.Success);
    }

    [Fact]
    public void No_winners_is_invalid_even_with_a_valid_template()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Congratulations <name>!", Array.Empty<GuestIdentity>());
        Assert.False(result.Success);
        Assert.Equal("No winner is available yet.", result.Error);
    }

    [Fact]
    public void Cross_world_winners_never_include_at_world_in_resolved_text()
    {
        var result = GiveawayWinnerAnnouncementResolver.Resolve("Congrats <name>!", OneWinner);
        Assert.True(result.Success);
        Assert.DoesNotContain("@", result.Message);
        Assert.DoesNotContain("Kraken", result.Message);
    }

    [Fact]
    public void Resolved_text_over_the_chat_byte_limit_is_rejected()
    {
        var template = new string('X', BlockLettersLimits.ChatBytes + 1); // one byte over the limit before <name> even expands
        var result = GiveawayWinnerAnnouncementResolver.Resolve(template, OneWinner);
        Assert.False(result.Success);
        Assert.Contains("too long", result.Error);
    }

    [Fact]
    public void A_short_template_that_expands_past_the_limit_via_a_large_tie_is_rejected_without_truncation()
    {
        var manyWinners = Enumerable.Range(0, 40).Select(i => new GuestIdentity($"Winner Number {i:D3}", "World")).ToArray();
        var result = GiveawayWinnerAnnouncementResolver.Resolve("GG <name>!", manyWinners); // short template, huge tie
        Assert.False(result.Success);
        Assert.Contains("too long", result.Error);
    }

    [Fact]
    public void Unicode_byte_counting_uses_the_shared_block_letters_logic()
    {
        // Each of these glyphs is a multi-byte UTF-8 sequence but a single UTF-16 code unit — this only rejects
        // correctly if byte counting (not string.Length) is what's actually being measured.
        var template = new string('あ', 200) + " <name>";
        var result = GiveawayWinnerAnnouncementResolver.Resolve(template, OneWinner);
        Assert.False(result.Success); // 200 * 3 bytes already exceeds the 500-byte chat limit
    }
}
