using VenueOS.Services;

namespace VenueOS.Modules.Operations.Giveaways;

/// <summary>Which FFXIV roll command produced a result. Both count identically toward a giveaway (GIVEAWAYS spec
/// §15) — this is tracked only so the message-form distinction is preserved (§48: the resulting number alone must
/// never be used to infer which command was used), never to maintain separate leaderboards.</summary>
public enum GiveawayRollKind { Standard, RangedOutOf }

/// <summary>A single normalized roll observation, already resolved to a <see cref="GuestIdentity"/> — the
/// Dalamud-free boundary between the chat adapter (VenueOS.Plugin.Giveaways.GiveawaysRollChatAdapter) and this pure
/// service. An unresolved/ambiguous <see cref="GuestIdentity.HomeWorld"/> is represented as <c>""</c> (never
/// invented — NEW_MODULE_GUIDE.md §28), which is why <see cref="GuestIdentity.Key"/> alone (not Name alone) is what
/// this board keys participants on.</summary>
public sealed record ObservedRandomRoll(GuestIdentity Player, int Value, GiveawayRollKind Kind);

public enum GiveawayRollRejectReason { GiveawayNotAcceptingRolls, OverPersonLimit }

public readonly record struct GiveawayRollOutcome(bool Accepted, GiveawayRollRejectReason? RejectReason)
{
    public static GiveawayRollOutcome Accept() => new(true, null);
    public static GiveawayRollOutcome Reject(GiveawayRollRejectReason reason) => new(false, reason);
}

/// <summary>One participant's current displayed state — always their single BEST accepted roll under the active
/// <see cref="GiveawayWinnerMode"/> (GIVEAWAYS spec §21), never every roll they made. <see cref="AcceptedCount"/> is
/// tracked only to enforce the per-person roll limit and is deliberately never surfaced in the UI (spec §20/§28).
/// </summary>
public sealed record GiveawayRollEntry(GuestIdentity Player, int Value, int AcceptedCount, bool IsSpecialHit);

/// <summary>One row of the sorted, leader-highlighted view the operator actually looks at (GIVEAWAYS spec §25/§28).
/// <see cref="IsLeader"/> is true for every participant tied for best, never just one arbitrarily chosen row (spec
/// §24) — the caller is responsible for rendering a tie clearly, this type only tells it which rows are tied.
/// <see cref="Distance"/> is always populated (abs(Value - target)) but is only meaningful/shown for Closest mode.
/// </summary>
public sealed record GiveawayLeaderboardRow(GuestIdentity Player, int Value, int Distance, bool IsLeader, bool IsSpecialHit);

/// <summary>Pure, Dalamud-free roll correlation/comparison engine for one giveaway run — no I/O, no chat, no
/// scheduling. Owns exactly the state GIVEAWAYS spec §21/§22/§23/§24/§25/§26 describes: one best roll per person,
/// a running Total Rolls count, per-person roll-limit enforcement, and leader/tie sorting. A fresh instance is
/// created per run by <see cref="GiveawayService.Start"/> — this class has no notion of "previous giveaway" at all,
/// so there is nothing here that could leak a stale run's state into a new one.</summary>
public sealed class GiveawayRollBoard(GiveawayWinnerMode mode, int closestTarget, int allowedRollsPerPerson, IReadOnlyList<int> specialNumbers)
{
    private readonly Dictionary<string, GiveawayRollEntry> entries = new(StringComparer.Ordinal);

    public int TotalRolls { get; private set; }

    /// <summary>GIVEAWAYS spec §26: special-number highlighting is inactive whenever more than one roll (or
    /// unlimited rolls) is allowed, so gaming the special prize by re-rolling isn't possible.</summary>
    public bool SpecialNumbersActive => allowedRollsPerPerson == 1;

    public GiveawayRollOutcome Accept(GuestIdentity player, int value)
    {
        var key = player.Key;
        entries.TryGetValue(key, out var existing);
        var priorCount = existing?.AcceptedCount ?? 0;

        if (allowedRollsPerPerson > 0 && priorCount >= allowedRollsPerPerson)
            return GiveawayRollOutcome.Reject(GiveawayRollRejectReason.OverPersonLimit);

        TotalRolls++;
        var newCount = priorCount + 1;
        var isSpecial = SpecialNumbersActive && specialNumbers.Contains(value);

        if (existing is null || IsBetter(value, existing.Value))
            entries[key] = new GiveawayRollEntry(player, value, newCount, isSpecial);
        else
            entries[key] = existing with { AcceptedCount = newCount };

        return GiveawayRollOutcome.Accept();
    }

    public IReadOnlyList<GiveawayLeaderboardRow> GetLeaderboard()
    {
        if (entries.Count == 0) return Array.Empty<GiveawayLeaderboardRow>();

        var ranked = entries.Values.Select(e => (Entry: e, SortKey: SortKey(e.Value))).OrderBy(x => x.SortKey).ToArray();
        var bestKey = ranked[0].SortKey;
        return ranked.Select(x => new GiveawayLeaderboardRow(x.Entry.Player, x.Entry.Value, Math.Abs(x.Entry.Value - closestTarget), x.SortKey == bestKey, x.Entry.IsSpecialHit)).ToArray();
    }

    private bool IsBetter(int candidate, int current) => mode switch
    {
        GiveawayWinnerMode.Highest => candidate > current,
        GiveawayWinnerMode.Lowest => candidate < current,
        GiveawayWinnerMode.Closest => Math.Abs(candidate - closestTarget) < Math.Abs(current - closestTarget),
        _ => false,
    };

    /// <summary>A single ascending sort key that makes Highest/Lowest/Closest all sort "best first" through the same
    /// <c>OrderBy</c> call, and makes tie detection ("every row whose key equals the best key") uniform across all
    /// three modes.</summary>
    private int SortKey(int value) => mode switch
    {
        GiveawayWinnerMode.Highest => -value,
        GiveawayWinnerMode.Lowest => value,
        GiveawayWinnerMode.Closest => Math.Abs(value - closestTarget),
        _ => 0,
    };
}
