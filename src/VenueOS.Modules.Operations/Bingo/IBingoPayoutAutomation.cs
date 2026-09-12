namespace VenueOS.Modules.Operations.Bingo;

public enum BingoTradeOutcome { Confirmed, Canceled, Failed, Ambiguous }

/// <summary>The outcome of one in-game trade attempt. <see cref="ObservedChatText"/> is the literal system-chat line
/// that produced a <see cref="BingoTradeOutcome.Confirmed"/>/<see cref="BingoTradeOutcome.Canceled"/> result (forensic
/// audit §20-21) — kept for the attempt's audit note. <see cref="GilDelta"/> is secondary corroboration only, never
/// the basis for the outcome itself.</summary>
public sealed record BingoTradeResult(BingoTradeOutcome Outcome, string? ObservedChatText = null, int? GilDelta = null, string? Detail = null)
{
    public static BingoTradeResult Confirmed(string? chatText = null, int? gilDelta = null) => new(BingoTradeOutcome.Confirmed, chatText, gilDelta);
    public static BingoTradeResult Canceled(string? detail = null) => new(BingoTradeOutcome.Canceled, Detail: detail);
    public static BingoTradeResult Failed(string detail) => new(BingoTradeOutcome.Failed, Detail: detail);
    public static BingoTradeResult Ambiguous(string detail) => new(BingoTradeOutcome.Ambiguous, Detail: detail);
}

/// <summary>Pure, unit-testable derivation of the terminal, human-readable engine status text for any
/// <see cref="BingoTradeResult"/>. Exists specifically to guarantee the live engine's own operator-visible
/// <see cref="IBingoPayoutAutomation.Status"/> always reflects a finished outcome once an attempt has actually
/// ended — never left showing a stale in-progress message (e.g. "Verifying pinned target...") after the attempt
/// returned or threw. This was a live-QA-confirmed defect: a self-trade test's target verification threw
/// "Not on main thread!" (see docs/BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md), the attempt correctly resolved to
/// Ambiguous, but the engine's <c>Status</c> field was never updated on that path and kept showing
/// "Verifying pinned target ..." even though the attempt had already ended — producing the internally
/// inconsistent "Idle" / "Verifying pinned target ..." double-status the operator panel displayed at once.</summary>
public static class BingoTradeResultStatusText
{
    public static string Describe(BingoTradeResult result) => result.Outcome switch
    {
        BingoTradeOutcome.Confirmed => "Trade complete.",
        BingoTradeOutcome.Canceled => string.IsNullOrWhiteSpace(result.Detail) ? "Canceled." : $"Canceled — {result.Detail}",
        BingoTradeOutcome.Failed => $"Failed — {result.Detail}",
        _ => $"Ambiguous — {result.Detail}",
    };
}

/// <summary>The boundary around unsafe, game-version-sensitive Bingo payout (in-game trade) automation — mirrors
/// <c>VenueOS.Modules.Operations.PartyFinder.IPartyFinderAutomation</c>'s shape and role exactly (see
/// PARTY_FINDER_RECONSTRUCTION.md / NEW_MODULE_GUIDE.md §30). Implemented by the real, unsafe
/// FFXIVClientStructs/ECommons engine in <c>VenueOS.Plugin</c> (<c>BingoPayoutAutomationService</c>) so that
/// <see cref="BingoPayoutOrchestrator"/> and its tests never need a live Dalamud/game context — a fake test double
/// implements this interface instead.
///
/// One call to <see cref="ExecutePayoutAttemptAsync"/> is expected to internally drive the FULL in-game flow for one
/// chunk — locate/pin the target, open a trade, verify the trade partner matches the pinned target BEFORE entering
/// gil, enter the gil amount, confirm the offer, and wait specifically for the game's own "Trade complete." /
/// "Trade canceled." system-chat messages as the PRIMARY completion signal (forensic audit §20-21) — never merely
/// "the trade window closed" and never merely "my own gil balance changed by roughly the right amount" as the
/// primary signal (those may be used as secondary, non-authoritative corroboration inside the real engine only).
/// Nothing in this interface's caller (<see cref="BingoPayoutOrchestrator"/>) ever treats an ambiguous outcome as
/// "unpaid" — see its own doc comment.</summary>
public interface IBingoPayoutAutomation
{
    string Status { get; }

    bool IsBusy { get; }

    /// <summary>Stops the in-flight attempt and, in the real engine, attempts to actively close/decline any open
    /// trade window (donor's own "Cancel Pay" never did this — forensic audit §18/§25 — so this is a deliberate
    /// improvement, but the actual addon-close behavior needs LIVE VERIFICATION — see
    /// <c>BingoPayoutAutomationService.Abort</c>). Does not itself touch the backend payout ledger; the orchestrator
    /// owns reconciling an aborted attempt's server-side status.</summary>
    void Abort();

    /// <summary>Drives one full trade attempt for exactly <paramref name="amount"/> gil against the pinned
    /// <paramref name="targetNameAndWorld"/> ("Name@World", captured once before the trade opens and re-verified
    /// against the actual trade partner before any gil is entered — never a bare, target-less <c>/trade</c>).
    /// Returns <see cref="BingoTradeOutcome.Confirmed"/> ONLY when the game's own "Trade complete." chat message was
    /// observed for this specific attempt; <see cref="BingoTradeOutcome.Canceled"/> on an explicit "Trade canceled."
    /// message or a declined/aborted trade; <see cref="BingoTradeOutcome.Ambiguous"/> on a bounded timeout with no
    /// chat signal either way (never guessed as Confirmed); <see cref="BingoTradeOutcome.Failed"/> for any other
    /// reason the attempt could not proceed at all (compatibility not verified, target mismatch observed at
    /// trade-open, etc).</summary>
    Task<BingoTradeResult> ExecutePayoutAttemptAsync(string targetNameAndWorld, int amount, CancellationToken cancellationToken);
}
