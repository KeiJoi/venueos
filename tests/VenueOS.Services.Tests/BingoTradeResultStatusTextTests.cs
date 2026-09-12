using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

/// <summary>Regression coverage for the live-QA-confirmed "stuck on Verifying pinned target..." defect
/// (BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md): the engine's operator-visible Status must always resolve to a terminal,
/// coherent string for every possible <see cref="BingoTradeResult"/> outcome — never left describing a mid-flight
/// step after the attempt has actually ended.</summary>
public sealed class BingoTradeResultStatusTextTests
{
    [Fact] public void Confirmed_describes_trade_complete() =>
        Assert.Equal("Trade complete.", BingoTradeResultStatusText.Describe(BingoTradeResult.Confirmed("Trade complete.")));

    [Fact] public void Canceled_with_detail_includes_the_detail() =>
        Assert.Equal("Canceled — Aborted by operator.", BingoTradeResultStatusText.Describe(BingoTradeResult.Canceled("Aborted by operator.")));

    [Fact] public void Canceled_with_no_detail_falls_back_to_a_plain_terminal_string() =>
        Assert.Equal("Canceled.", BingoTradeResultStatusText.Describe(BingoTradeResult.Canceled()));

    [Fact] public void Failed_includes_the_reason() =>
        Assert.Equal("Failed — Trade window did not open in time.", BingoTradeResultStatusText.Describe(BingoTradeResult.Failed("Trade window did not open in time.")));

    [Fact] public void Ambiguous_includes_the_reason() =>
        Assert.Equal("Ambiguous — Unhandled engine error: Not on main thread!", BingoTradeResultStatusText.Describe(BingoTradeResult.Ambiguous("Unhandled engine error: Not on main thread!")));

    [Theory]
    [InlineData(BingoTradeOutcome.Confirmed)]
    [InlineData(BingoTradeOutcome.Canceled)]
    [InlineData(BingoTradeOutcome.Failed)]
    [InlineData(BingoTradeOutcome.Ambiguous)]
    public void No_terminal_status_ever_still_reads_as_an_in_progress_verification_message(BingoTradeOutcome outcome)
    {
        var result = new BingoTradeResult(outcome, Detail: "some detail");
        var status = BingoTradeResultStatusText.Describe(result);
        Assert.DoesNotContain("Verifying pinned target", status, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(status));
    }
}
