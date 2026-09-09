using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>Pure roll-comparison/leaderboard tests for <see cref="GiveawayRollBoard"/> — synthetic
/// <see cref="GuestIdentity"/>/roll values only, no chat/Dalamud dependency (GIVEAWAYS spec §21-§27/§41).</summary>
public sealed class GiveawayRollBoardTests
{
    private static GuestIdentity Player(string name, string world = "Balmung") => new(name, world);

    // ---- Roll limit (spec §22) ----

    [Fact]
    public void Default_allowed_rolls_of_one_accepts_first_and_ignores_second()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, []);
        Assert.True(board.Accept(Player("A"), 100).Accepted);
        var second = board.Accept(Player("A"), 500);
        Assert.False(second.Accepted);
        Assert.Equal(GiveawayRollRejectReason.OverPersonLimit, second.RejectReason);
        Assert.Equal(1, board.TotalRolls);
        Assert.Equal(100, board.GetLeaderboard().Single().Value);
    }

    [Fact]
    public void Positive_n_accepts_exactly_n_rolls_and_rejects_the_n_plus_first()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 3, []);
        Assert.True(board.Accept(Player("A"), 1).Accepted);
        Assert.True(board.Accept(Player("A"), 2).Accepted);
        Assert.True(board.Accept(Player("A"), 3).Accepted);
        var fourth = board.Accept(Player("A"), 4);
        Assert.False(fourth.Accepted);
        Assert.Equal(GiveawayRollRejectReason.OverPersonLimit, fourth.RejectReason);
        Assert.Equal(3, board.TotalRolls); // the rejected 4th roll must NOT increment Total Rolls
    }

    [Fact]
    public void Zero_allows_unlimited_rolls_from_the_same_person()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 0, []);
        for (var i = 0; i < 25; i++) Assert.True(board.Accept(Player("A"), i).Accepted);
        Assert.Equal(25, board.TotalRolls);
        Assert.Single(board.GetLeaderboard()); // still only ONE displayed row per person
    }

    [Fact]
    public void Accepted_roll_increments_total_rolls_even_when_it_does_not_replace_the_stored_best()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 0, []);
        board.Accept(Player("A"), 900);
        board.Accept(Player("A"), 1); // accepted (unlimited rolls), but worse than 900 — must not replace it
        Assert.Equal(2, board.TotalRolls);
        Assert.Equal(900, board.GetLeaderboard().Single().Value);
    }

    // ---- Highest ----

    [Fact]
    public void Highest_mode_keeps_the_better_high_roll_and_discards_the_worse_one()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 0, []);
        board.Accept(Player("A"), 500);
        board.Accept(Player("A"), 490); // worse — discarded
        Assert.Equal(500, board.GetLeaderboard().Single().Value);
        board.Accept(Player("A"), 510); // better — replaces
        Assert.Equal(510, board.GetLeaderboard().Single().Value);
    }

    [Fact]
    public void Highest_leader_sorts_first()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, []);
        board.Accept(Player("A"), 200);
        board.Accept(Player("B"), 900);
        board.Accept(Player("C"), 500);
        var rows = board.GetLeaderboard();
        Assert.Equal("B", rows[0].Player.Name);
        Assert.True(rows[0].IsLeader);
        Assert.False(rows[1].IsLeader);
    }

    // ---- Lowest ----

    [Fact]
    public void Lowest_mode_keeps_the_better_low_roll_and_discards_the_worse_one()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Lowest, 0, allowedRollsPerPerson: 0, []);
        board.Accept(Player("A"), 500);
        board.Accept(Player("A"), 510); // worse (higher) — discarded
        Assert.Equal(500, board.GetLeaderboard().Single().Value);
        board.Accept(Player("A"), 490); // better (lower) — replaces
        Assert.Equal(490, board.GetLeaderboard().Single().Value);
    }

    [Fact]
    public void Lowest_leader_sorts_first()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Lowest, 0, allowedRollsPerPerson: 1, []);
        board.Accept(Player("A"), 200);
        board.Accept(Player("B"), 5);
        board.Accept(Player("C"), 500);
        Assert.Equal("B", board.GetLeaderboard()[0].Player.Name);
    }

    // ---- Closest ----

    [Fact]
    public void Closest_mode_keeps_the_closer_roll_and_discards_the_farther_one()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Closest, 500, allowedRollsPerPerson: 0, []);
        board.Accept(Player("A"), 450); // distance 50
        board.Accept(Player("A"), 600); // distance 100 — farther, discarded
        Assert.Equal(450, board.GetLeaderboard().Single().Value);
        board.Accept(Player("A"), 490); // distance 10 — closer, replaces
        Assert.Equal(490, board.GetLeaderboard().Single().Value);
    }

    [Fact]
    public void Closest_exact_target_distance_zero_wins()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Closest, 500, allowedRollsPerPerson: 1, []);
        board.Accept(Player("A"), 480);
        board.Accept(Player("B"), 500);
        board.Accept(Player("C"), 520);
        var leader = board.GetLeaderboard().Single(x => x.IsLeader);
        Assert.Equal("B", leader.Player.Name);
        Assert.Equal(0, leader.Distance);
    }

    [Fact]
    public void Closest_leader_sorts_first()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Closest, 500, allowedRollsPerPerson: 1, []);
        board.Accept(Player("A"), 200);
        board.Accept(Player("B"), 505);
        board.Accept(Player("C"), 900);
        Assert.Equal("B", board.GetLeaderboard()[0].Player.Name);
    }

    // ---- Ties ----

    [Fact]
    public void Equal_leaders_are_detected_as_a_tie_and_both_appear_at_the_top()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, []);
        board.Accept(Player("A"), 500);
        board.Accept(Player("B"), 500);
        board.Accept(Player("C"), 100);
        var rows = board.GetLeaderboard();
        Assert.True(rows[0].IsLeader);
        Assert.True(rows[1].IsLeader);
        Assert.False(rows[2].IsLeader);
        Assert.Equal(2, rows.Count(x => x.IsLeader));
    }

    [Fact]
    public void Tie_never_silently_picks_one_arbitrary_winner()
    {
        // A tie must be reported as multiple IsLeader=true rows, never collapsed to a single implicit winner by
        // arrival order or any other hidden tiebreak (GIVEAWAYS spec §24).
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Closest, 500, allowedRollsPerPerson: 1, []);
        board.Accept(Player("First"), 490); // distance 10, arrives first
        board.Accept(Player("Second"), 510); // distance 10, arrives second — exactly tied with First
        var leaders = board.GetLeaderboard().Where(x => x.IsLeader).ToArray();
        Assert.Equal(2, leaders.Length);
    }

    // ---- Identity ----

    [Fact]
    public void Same_name_different_home_world_are_distinct_participants()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, []);
        board.Accept(new GuestIdentity("Alice", "Balmung"), 100);
        board.Accept(new GuestIdentity("Alice", "Gilgamesh"), 900);
        Assert.Equal(2, board.GetLeaderboard().Count);
        Assert.Equal(2, board.TotalRolls);
    }

    [Fact]
    public void An_unresolved_home_world_never_silently_merges_with_a_known_world_record()
    {
        // NEW_MODULE_GUIDE.md §28's ambiguity rule: a legacy/ambiguous Name-only identity (HomeWorld == "") must
        // never be treated as the same participant as a positively-resolved Name+HomeWorld record.
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, []);
        board.Accept(new GuestIdentity("Alice", ""), 100);
        board.Accept(new GuestIdentity("Alice", "Balmung"), 900);
        Assert.Equal(2, board.GetLeaderboard().Count);
    }

    // ---- Special numbers ----

    [Fact]
    public void Special_number_hit_is_recorded_when_allowed_rolls_is_exactly_one()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, [69, 420]);
        board.Accept(Player("A"), 69);
        Assert.True(board.GetLeaderboard().Single().IsSpecialHit);
    }

    [Fact]
    public void Special_number_does_not_alter_ordinary_winner_comparison()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 1, [69]);
        board.Accept(Player("A"), 69);
        board.Accept(Player("B"), 900);
        var leader = board.GetLeaderboard().Single(x => x.IsLeader);
        Assert.Equal("B", leader.Player.Name);
        Assert.False(leader.IsSpecialHit);
    }

    [Fact]
    public void Special_number_behavior_is_ignored_when_allowed_rolls_is_greater_than_one()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 2, [69]);
        board.Accept(Player("A"), 69);
        Assert.False(board.GetLeaderboard().Single().IsSpecialHit);
        Assert.False(board.SpecialNumbersActive);
    }

    [Fact]
    public void Special_number_behavior_is_ignored_when_allowed_rolls_is_unlimited()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 0, [69]);
        board.Accept(Player("A"), 69);
        Assert.False(board.GetLeaderboard().Single().IsSpecialHit);
        Assert.False(board.SpecialNumbersActive);
    }

    // ---- Display: one row per person ----

    [Fact]
    public void Only_one_row_is_ever_displayed_per_participant_even_with_many_allowed_rolls()
    {
        var board = new GiveawayRollBoard(GiveawayWinnerMode.Highest, 0, allowedRollsPerPerson: 10, []);
        for (var i = 1; i <= 10; i++) board.Accept(Player("A"), i * 10);
        Assert.Single(board.GetLeaderboard());
        Assert.Equal(100, board.GetLeaderboard().Single().Value);
    }
}
