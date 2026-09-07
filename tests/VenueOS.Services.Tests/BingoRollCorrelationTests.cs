using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

/// <summary>
/// Unit tests for the pure roll-correlation/text-parsing logic (<see cref="BingoRollCorrelator"/>,
/// <see cref="BingoRollTextParser"/>) — synthetic <see cref="BingoRollObservation"/> values only, exercising
/// exactly the decision rules those two classes implement.
///
/// IMPORTANT SCOPE NOTE: these tests prove the CORRELATION/PARSING LOGIC is correct given a claimed observation.
/// They do NOT and cannot prove actual FFXIV chat behavior — whether `/dice 75` really produces
/// <c>XivChatType.Party</c> messages in this exact shape, whether a self-authored Party message really carries (or
/// omits) a structured <c>PlayerPayload</c>, and whether `/dice 75` requires party membership are all still
/// LIVE-VERIFICATION-REQUIRED facts about the real game client (see <c>BingoRollChatAdapter</c>'s doc comment and
/// this session's completion report) — a passing test here proves the logic layer, never the runtime chat
/// integration.
/// </summary>
public sealed class BingoRollCorrelationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // --- BingoRollTextParser: pure text parsing ---
    // These are, deliberately, regression tests for the live bug this correction pass fixed: the previous parser
    // used a paraphrased approximation (no parentheses around the range) that never matched a single real /random
    // result. The format below is transcribed byte-for-byte from the donor's own live-verified regex
    // (FFXIVBingo4All.Plugin/Plugin.cs ~line 37-40) — see BingoRollTextParser's doc comment.

    [Fact] public void Parser_accepts_the_donors_exact_live_verified_random_format_with_parentheses()
    {
        Assert.Equal(42, BingoRollTextParser.TryParseRollResult("You roll a 42 (1-75)."));
        Assert.Equal(1, BingoRollTextParser.TryParseRollResult("You roll a 1 (out of 75)."));
    }

    [Fact] public void Parser_accepts_the_optional_leading_random_prefix()
    {
        // The donor's regex tolerates an optional "Random! " prefix on the same template — this is also the basis
        // for treating /dice 75 as sharing the same underlying text (see BingoRollTextParser's doc comment).
        Assert.Equal(55, BingoRollTextParser.TryParseRollResult("Random! You roll a 55 (1-75)."));
    }

    [Fact] public void Parser_tolerates_whitespace_variance_around_the_dash_and_a_missing_trailing_period()
    {
        Assert.Equal(7, BingoRollTextParser.TryParseRollResult("You roll a 7 (1 - 75)"));
        Assert.Equal(7, BingoRollTextParser.TryParseRollResult("YOU ROLL A 7 (1-75)")); // case-insensitive, matching the donor's RegexOptions.IgnoreCase
    }

    [Fact] public void Parser_rejects_the_previously_used_incorrect_paren_less_format()
    {
        // This is the exact format the earlier, broken parser accepted and the real game never sends — asserting
        // it is now REJECTED is what proves this isn't just "a" fix but the CORRECT one (matching the donor's real
        // regex, which requires the parentheses).
        Assert.Null(BingoRollTextParser.TryParseRollResult("You roll a 42 1-75"));
    }

    [Fact] public void Parser_rejects_unrelated_text()
    {
        Assert.Null(BingoRollTextParser.TryParseRollResult("You roll a 42 (1-100).")); // wrong range — not a Bingo roll
        Assert.Null(BingoRollTextParser.TryParseRollResult("Hey, anyone up for a dungeon?"));
        Assert.Null(BingoRollTextParser.TryParseRollResult("anyone got 75 gil to spare lol")); // contains "75" but not a roll
        Assert.Null(BingoRollTextParser.TryParseRollResult(""));
        Assert.Null(BingoRollTextParser.TryParseRollResult(null));
    }

    [Fact] public void Random_and_dice_aliases_both_delegate_to_the_same_unified_parser()
    {
        const string text = "You roll a 30 (1-75).";
        Assert.Equal(BingoRollTextParser.TryParseRollResult(text), BingoRollTextParser.TryParseRandomResult(text));
        Assert.Equal(BingoRollTextParser.TryParseRollResult(text), BingoRollTextParser.TryParseDiceResult(text));
    }

    // --- BingoRollCorrelator: pure accept/reject decision ---

    [Fact] public void Valid_local_host_random_result_is_accepted()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, ParsedNumber: 42, Now);
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now);
        Assert.True(result.Accepted);
        Assert.Equal(42, result.Number);
    }

    [Fact] public void Valid_local_host_dice_result_is_accepted()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Dice, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Dice, IsFromLocalHost: true, ParsedNumber: 17, Now);
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now);
        Assert.True(result.Accepted);
        Assert.Equal(17, result.Number);
    }

    [Fact] public void Explicit_scenario_a_party_members_dice_roll_can_never_satisfy_the_hosts_pending_roll()
    {
        // Host "Kay Example@World" presses Call Number (Dice mode). Party member "Alice Example@World" also uses
        // /dice 75 during the same window. Alice's result must never be consumed, even though it has the correct
        // format, range, and mode, and even if it arrives before Kay's own roll (see the explicit scenario in the
        // product correction this test file was added for).
        var kaysPendingRoll = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Dice, Now.AddSeconds(10));
        var alicesRoll = new BingoRollObservation(BingoRollMode.Dice, IsFromLocalHost: false, ParsedNumber: 55, Now);

        var result = BingoRollCorrelator.Evaluate(kaysPendingRoll, alicesRoll, Now);

        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.NotLocalHost, result.RejectReason);
    }

    [Fact] public void Another_players_random_roll_is_ignored_when_sender_identity_is_available()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: false, ParsedNumber: 30, Now);
        Assert.Equal(BingoRollRejectReason.NotLocalHost, BingoRollCorrelator.Evaluate(pending, observation, Now).RejectReason);
    }

    [Fact] public void A_message_arriving_with_no_pending_roll_at_all_is_ignored()
    {
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, ParsedNumber: 42, Now);
        var result = BingoRollCorrelator.Evaluate(pending: null, observation, Now);
        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.NoPendingRoll, result.RejectReason);
    }

    [Fact] public void A_message_arriving_after_the_deadline_is_ignored()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, ParsedNumber: 42, Now.AddSeconds(11));
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now.AddSeconds(11));
        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.Expired, result.RejectReason);
    }

    [Fact] public void Wrong_roll_mode_while_awaiting_is_ignored()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Dice, IsFromLocalHost: true, ParsedNumber: 42, Now);
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now);
        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.WrongMode, result.RejectReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(76)]
    [InlineData(999)]
    public void Out_of_range_numbers_are_rejected(int outOfRangeNumber)
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, outOfRangeNumber, Now);
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now);
        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.NumberOutOfRange, result.RejectReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(75)]
    public void Boundary_numbers_are_accepted(int boundaryNumber)
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, boundaryNumber, Now);
        Assert.True(BingoRollCorrelator.Evaluate(pending, observation, Now).Accepted);
    }

    [Fact] public void A_message_with_no_parsed_number_is_ignored()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var observation = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, ParsedNumber: null, Now);
        var result = BingoRollCorrelator.Evaluate(pending, observation, Now);
        Assert.False(result.Accepted);
        Assert.Equal(BingoRollRejectReason.NumberMissing, result.RejectReason);
    }

    // --- BingoRollSenderIdentity: pure, Dalamud-free donor-parity self-check (live-QA correction pass). This is
    // the FIRST actual behavioral difference found between the donor's 16-stage roll lifecycle and VenueOS's own —
    // see BingoRollChatAdapter's doc comment for the full trace. The donor's REJECT condition is
    // `!string.IsNullOrEmpty(senderName) && senderName != "You" && senderName != localName`; VenueOS's prior
    // version rejected a blank sender outright, which these tests would have caught. ---

    [Fact] public void A_blank_sender_is_accepted_as_self_donor_exact()
    {
        // The donor's own condition never rejects an empty sender — de Morgan'd from its single reject branch.
        Assert.True(BingoRollSenderIdentity.IsLocalHost(senderText: "", localName: "Kay Example"));
        Assert.True(BingoRollSenderIdentity.IsLocalHost(senderText: null, localName: "Kay Example"));
        Assert.True(BingoRollSenderIdentity.IsLocalHost(senderText: "   ", localName: "Kay Example"));
    }

    [Fact] public void A_blank_sender_is_accepted_even_if_the_local_player_name_cannot_be_resolved()
        // Neither side being resolvable must never throw or default to a false rejection — donor-exact.
        => Assert.True(BingoRollSenderIdentity.IsLocalHost(senderText: null, localName: null));

    [Fact] public void The_literal_sender_You_is_always_accepted()
        => Assert.True(BingoRollSenderIdentity.IsLocalHost("You", localName: null));

    [Fact] public void A_sender_matching_the_local_players_bare_name_is_accepted_case_insensitively()
        => Assert.True(BingoRollSenderIdentity.IsLocalHost("kay example", "Kay Example"));

    [Fact] public void A_sender_naming_a_different_player_is_rejected()
        // The scenario this whole gate exists for: a party member's roll must never be attributed to the host.
        => Assert.False(BingoRollSenderIdentity.IsLocalHost("Alice Example", "Kay Example"));

    [Fact] public void A_non_blank_sender_is_rejected_when_the_local_name_cannot_be_resolved()
        // A non-empty sender that isn't "You" needs SOMETHING to positively match against — an unresolved local
        // name must not silently default to acceptance (that would defeat the whole party-member-rejection rule).
        => Assert.False(BingoRollSenderIdentity.IsLocalHost("Alice Example", localName: null));

    [Fact] public void Only_one_matching_local_host_result_can_be_consumed_per_pending_roll()
    {
        var pending = new BingoPendingRoll(Guid.NewGuid(), BingoRollMode.Random, Now.AddSeconds(10));
        var first = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, 42, Now);
        var firstResult = BingoRollCorrelator.Evaluate(pending, first, Now);
        Assert.True(firstResult.Accepted);
        pending.MarkConsumed(); // this is exactly what VenueBingoService.HandleRollObservation does on acceptance

        // A second matching message for the SAME pending roll (duplicate chat delivery, or another coincidental
        // local-host-looking result) must never be accepted again.
        var second = new BingoRollObservation(BingoRollMode.Random, IsFromLocalHost: true, 42, Now.AddSeconds(1));
        var secondResult = BingoRollCorrelator.Evaluate(pending, second, Now.AddSeconds(1));
        Assert.False(secondResult.Accepted);
        Assert.Equal(BingoRollRejectReason.AlreadyConsumed, secondResult.RejectReason);
    }
}
