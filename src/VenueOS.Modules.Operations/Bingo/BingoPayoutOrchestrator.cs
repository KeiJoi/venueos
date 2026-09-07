namespace VenueOS.Modules.Operations.Bingo;

/// <summary>Orchestration-level view of progress, for UI display. The fine-grained in-game sub-stages the forensic
/// audit's §21 design names (VerifyTradePartner, EnterGil, ConfirmOffer, AwaitRemoteConfirmation,
/// AwaitCompletionSignal) are encapsulated inside one <see cref="IBingoPayoutAutomation.ExecutePayoutAttemptAsync"/>
/// call — this orchestrator only ever sees their combined outcome — so this enum only tracks what the orchestrator
/// itself does: create the backend attempt, hand it to the engine, and reconcile the result.</summary>
public enum BingoPayoutStage { Idle, LocateWinner, InitiateTrade, VerifyCompletion, MarkPayoutComplete, Done, Ambiguous, Failed, Canceled }

public sealed record BingoPayoutAttemptLog(string AttemptId, int Amount, BingoPayoutStage FinalStage, string? Detail);

public sealed record BingoPayoutRunResult(bool FullyPaid, int Outstanding, IReadOnlyList<BingoPayoutAttemptLog> Attempts, BingoPayoutStage FinalStage, string? Error);

/// <summary>
/// Pure orchestration logic for the safe-autopay state machine (forensic audit §21) — no Dalamud/game dependency,
/// fully unit-testable against a fake <see cref="IBingoPayoutAutomation"/>.
///
/// Hard rules enforced HERE, not left to the engine (forensic audit §21, "Recommended Safe Autopay State Machine"):
///  - <b>AMBIGUOUS DOES NOT MEAN UNPAID.</b> An Ambiguous engine outcome transitions the attempt to "ambiguous"
///    server-side (<c>PATCH .../attempts/:id</c>) and stops — it never automatically retries and never marks
///    anything paid.
///  - Every attempt is created via the backend payout-obligation/attempt endpoints BEFORE any in-game action starts.
///    The backend attempt id is the durable identity; nothing in this class's own memory is the source of truth for
///    "was this paid" — only the backend's <c>confirmedPaid</c>/<c>outstanding</c> is.
///  - Completion is confirmed via <c>PATCH .../attempts/:id {status:"confirmed"}</c> ONLY when the engine reports
///    <see cref="BingoTradeOutcome.Confirmed"/> (gated on the primary "Trade complete." chat signal inside the real
///    engine — see <see cref="IBingoPayoutAutomation"/>'s doc comment).
///  - A multi-chunk payout re-reads <c>outstanding</c> from the PATCH response's <see cref="BingoPayoutObligation"/>
///    after each confirmed attempt and only creates a NEW attempt for <c>min(remaining, maxChunkGil)</c> — this
///    class's own local variable is reassigned FROM that response, never derived independently, so a resumed/
///    second-host scenario naturally works.
///  - <see cref="Abort"/> stops future chunk creation and forwards to the engine's own Abort(); if an attempt was
///    already created server-side but the engine never resolved it (aborted mid-flight, or the engine threw), it is
///    transitioned to "ambiguous" rather than left silently "pending" forever.
/// </summary>
public sealed class BingoPayoutOrchestrator(IBingoPayoutAutomation automation, VenueBingoClient client)
{
    private volatile bool abortRequested;

    public BingoPayoutStage Stage { get; private set; } = BingoPayoutStage.Idle;
    public string? CurrentAttemptId { get; private set; }

    /// <summary>Stops future chunk creation and forwards to the engine's own Abort(). An attempt currently in
    /// flight is reconciled to "ambiguous" by <see cref="RunAsync"/> itself once the in-flight engine call returns
    /// (or throws) — never left "pending".</summary>
    public void Abort() { abortRequested = true; automation.Abort(); }

    /// <summary>Runs chunked attempts for <paramref name="obligation"/> against <paramref name="targetNameAndWorld"/>
    /// until fully paid, canceled/failed/ambiguous, or aborted. <paramref name="maxChunkGil"/> should be the
    /// practical FFXIV trade-window gil limit (donor: 1,000,000).</summary>
    public async Task<BingoPayoutRunResult> RunAsync(BingoConnectionSettings connection, string roomCode, BingoPayoutObligation obligation, string targetNameAndWorld, int maxChunkGil, CancellationToken cancellationToken)
    {
        abortRequested = false;
        var attempts = new List<BingoPayoutAttemptLog>();
        var outstanding = obligation.Outstanding;
        Stage = BingoPayoutStage.LocateWinner;

        while (outstanding > 0)
        {
            if (abortRequested || cancellationToken.IsCancellationRequested)
            { Stage = BingoPayoutStage.Canceled; return new(false, outstanding, attempts, Stage, "Aborted by operator."); }

            var chunk = Math.Min(outstanding, maxChunkGil);
            var created = await client.CreatePayoutAttemptAsync(connection, roomCode, obligation.PayoutId, new(chunk, Guid.NewGuid().ToString()), cancellationToken).ConfigureAwait(false);
            if (!created.Success || created.Value is null)
            { Stage = BingoPayoutStage.Failed; return new(false, outstanding, attempts, Stage, created.Error ?? "Failed to create payout attempt."); }

            var attemptId = created.Value.AttemptId;
            CurrentAttemptId = attemptId;
            Stage = BingoPayoutStage.InitiateTrade;

            BingoTradeResult tradeResult;
            try { tradeResult = await automation.ExecutePayoutAttemptAsync(targetNameAndWorld, chunk, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Never leave a server-created attempt silently "pending" forever — an unresolved attempt is
                // UNKNOWN, not unpaid (forensic audit §21, principle 4).
                await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "ambiguous", "Automation canceled before a result was observed.", cancellationToken).ConfigureAwait(false);
                attempts.Add(new(attemptId, chunk, BingoPayoutStage.Ambiguous, "Canceled before a result was observed."));
                Stage = BingoPayoutStage.Ambiguous;
                return new(false, outstanding, attempts, Stage, "Automation was canceled mid-attempt; the attempt was marked ambiguous for manual reconciliation.");
            }
            catch (Exception ex)
            {
                await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "ambiguous", $"Engine threw: {ex.Message}", cancellationToken).ConfigureAwait(false);
                attempts.Add(new(attemptId, chunk, BingoPayoutStage.Ambiguous, ex.Message));
                Stage = BingoPayoutStage.Ambiguous;
                return new(false, outstanding, attempts, Stage, "The payout engine threw an unhandled error; the attempt was marked ambiguous for manual reconciliation.");
            }

            // Abort requested WHILE the engine call was in flight: never trust whatever outcome came back — treat
            // this specific attempt as unresolved (ambiguous), and never start a next chunk.
            if (abortRequested)
            {
                await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "ambiguous", "Aborted by operator while the attempt was in flight.", cancellationToken).ConfigureAwait(false);
                attempts.Add(new(attemptId, chunk, BingoPayoutStage.Ambiguous, "Aborted by operator."));
                Stage = BingoPayoutStage.Ambiguous;
                return new(false, outstanding, attempts, Stage, "Aborted by operator; the in-flight attempt was marked ambiguous for manual reconciliation.");
            }

            Stage = BingoPayoutStage.VerifyCompletion;
            switch (tradeResult.Outcome)
            {
                case BingoTradeOutcome.Confirmed:
                    var confirmed = await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "confirmed", tradeResult.ObservedChatText, cancellationToken).ConfigureAwait(false);
                    attempts.Add(new(attemptId, chunk, BingoPayoutStage.MarkPayoutComplete, tradeResult.ObservedChatText));
                    if (confirmed?.Obligation is null)
                    { Stage = BingoPayoutStage.Failed; return new(false, outstanding, attempts, Stage, "Confirmed the trade but could not re-read the updated obligation from the backend."); }
                    // Re-derive outstanding from the backend's response — never decremented locally — so a
                    // resumed/second-host scenario naturally works.
                    outstanding = confirmed.Obligation.Outstanding;
                    Stage = outstanding <= 0 ? BingoPayoutStage.Done : BingoPayoutStage.LocateWinner;
                    continue;

                case BingoTradeOutcome.Canceled:
                    await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "canceled", tradeResult.Detail, cancellationToken).ConfigureAwait(false);
                    attempts.Add(new(attemptId, chunk, BingoPayoutStage.Canceled, tradeResult.Detail));
                    Stage = BingoPayoutStage.Canceled;
                    return new(false, outstanding, attempts, Stage, tradeResult.Detail ?? "Trade canceled.");

                case BingoTradeOutcome.Failed:
                    await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "failed", tradeResult.Detail, cancellationToken).ConfigureAwait(false);
                    attempts.Add(new(attemptId, chunk, BingoPayoutStage.Failed, tradeResult.Detail));
                    Stage = BingoPayoutStage.Failed;
                    return new(false, outstanding, attempts, Stage, tradeResult.Detail ?? "Trade failed.");

                default: // Ambiguous — NEVER auto-retry, NEVER mark paid. Surface for manual reconciliation.
                    await TransitionAsync(connection, roomCode, obligation.PayoutId, attemptId, "ambiguous", tradeResult.Detail, cancellationToken).ConfigureAwait(false);
                    attempts.Add(new(attemptId, chunk, BingoPayoutStage.Ambiguous, tradeResult.Detail));
                    Stage = BingoPayoutStage.Ambiguous;
                    return new(false, outstanding, attempts, Stage, tradeResult.Detail ?? "Trade outcome was ambiguous; no chat completion signal was observed in time.");
            }
        }

        Stage = BingoPayoutStage.Done;
        return new(true, 0, attempts, Stage, null);
    }

    private async Task<BingoAttemptTransitionResponse?> TransitionAsync(BingoConnectionSettings connection, string roomCode, string payoutId, string attemptId, string status, string? note, CancellationToken ct)
    {
        var result = await client.TransitionPayoutAttemptAsync(connection, roomCode, payoutId, attemptId, new(status, note, Guid.NewGuid().ToString()), ct).ConfigureAwait(false);
        return result.Success ? result.Value : null;
    }
}
