# Bingo Auto Payout — main-thread verification hotfix

Targeted production bugfix for a live-observed defect in Bingo's automated payout (`BingoPayoutAutomationService`
/ `BingoPayoutOrchestrator`). This is not a redesign of Bingo or the payout system — see `BINGO_FORENSIC_AUDIT.md`
and `BINGO_V2_PROTOCOL.md` for the design this hotfix builds on.

## 1. Executive summary

A live self-trade test of "Attempt Payout" never progressed past target verification. The operator panel showed
`Idle` and `Verifying pinned target Fiducia Ancilla@Diabolos...` simultaneously, with `Last attempt: Ambiguous —
Unhandled engine error: Not on main thread!`.

Two independent defects caused this:

1. **Thread-affinity violation.** `BingoPayoutAutomationService.ExecutePayoutAttemptAsync` is an `async` method
   invoked (via `BingoPayoutOrchestrator.RunAsync`) only after the orchestrator has already `await`ed a real HTTP
   call with `ConfigureAwait(false)` (`VenueBingoClient.CreatePayoutAttemptAsync`). Once that continuation resumes,
   execution is on an arbitrary .NET thread-pool thread, not Dalamud's game/framework thread. Every subsequent
   Dalamud/FFXIVClientStructs touch inside the engine — starting with the very first one, reading
   `ITargetManager.Target` to verify the pinned winner — was therefore executing off-thread and could throw
   Dalamud's `"Not on main thread!"` guard.
2. **No terminal status on the error path.** The engine's operator-visible `Status` field was only ever updated by
   explicit `SetStatus(...)` calls sprinkled through the method. The unhandled-exception catch block (and several
   other early-return paths — aborts mid-flight, the completion-signal timeout, `OperationCanceledException`) never
   called `SetStatus(...)`, so `Status` stayed on whatever message was last set before the exception — in the
   observed case, `"Verifying pinned target Fiducia Ancilla@Diabolos..."` — even though the attempt had already
   ended and resolved to `Ambiguous`. That produced the internally inconsistent "Idle" + "Verifying..." double
   status shown at once.

Both are fixed. Neither the thread-affinity fix nor the status fix changes payout semantics, the trade-automation
design, the backend protocol, or ledger behavior.

## 2. Live symptom

```
Fiducia Ancilla — owed 4,650,000, paid 0, outstanding 4,650,000
Target: Fiducia Ancilla@Diabolos
Status: Idle
Verifying pinned target Fiducia Ancilla@Diabolos...
Last attempt: Ambiguous — Unhandled engine error: Not on main thread!
```

The outstanding balance never moved — the orchestrator's "ambiguous does not mean unpaid" rule already prevented
any false success. This hotfix does not change that guarantee; it fixes why the attempt could never even reach a
real trade, and why the status display looked stuck.

## 3. Exact exception

`System.InvalidOperationException: Not on main thread!` (Dalamud's own thread-affinity guard, thrown from inside
a Dalamud game-state accessor when called off the framework thread).

## 4. Exact throw site (pre-fix)

`src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs`, inside `ExecutePayoutAttemptAsync`:

```csharp
SetStatus($"Verifying pinned target {targetNameAndWorld}...");
if (!CurrentTargetMatches(targetNameAndWorld))   // <- targetManager.Target read here, off the framework thread
    return Fail(...);
```

`CurrentTargetMatches` → `targetManager.Target is IPlayerCharacter player && NameAndWorldMatches(player, ...)` —
reading `ITargetManager.Target` is exactly the API that asserts main-thread execution.

## 5. Calling/thread context

`VenueBingoOperatorPanel.DrawAttemptPayoutControls` (Draw(), on the framework thread) fires
`_ = AttemptPayoutAsync(...)` → `BingoPayoutOrchestrator.RunAsync` → `client.CreatePayoutAttemptAsync(...)
.ConfigureAwait(false)` (a real `HttpClient` call) → once that HTTP call's continuation resumes, execution is on a
thread-pool thread → `automation.ExecutePayoutAttemptAsync(...).ConfigureAwait(false)` runs from there → the first
Dalamud game-state read inside it (`CurrentTargetMatches`) throws.

## 6. Root cause

Nothing in `BingoPayoutAutomationService` ever marshaled its Dalamud/FFXIVClientStructs calls back onto the
framework thread. The class was written and compiled correctly against the real Dalamud/FFXIVClientStructs types
(per its own doc comment), but every unsafe/addon/target-table touch assumed it would run on the same thread as
its caller — true only as long as nothing upstream had ever awaited a real asynchronous boundary with
`ConfigureAwait(false)` first. The orchestrator's own backend-attempt-first design (create the attempt via HTTP
*before* touching the game) — a deliberate, correct safety property (BINGO_V2_PROTOCOL.md) — is exactly what
guarantees an awaited, thread-hopping boundary always happens before the engine ever runs.

## 7. Framework/main-thread fix

Used the codebase's existing, already-tested-elsewhere abstraction rather than inventing a new one:
`VenueOS.Services.IFrameworkDispatcher` (`SharedServices.cs`) — already the established pattern for marshaling
onto the framework thread (`ChatCommandService` is the existing consumer; `InlineFrameworkDispatcher` is its
Dalamud-free fake for tests).

- Added `DalamudFrameworkDispatcher : IFrameworkDispatcher` (`Plugin.cs`) — the real implementation, backed by
  Dalamud's `IFramework.RunOnFrameworkThread`, using the same `TaskCompletionSource` +
  `CancellationToken.Register` pattern `ShoutRunnerAutomationService` already uses for the identical problem
  (queue the action onto the framework thread; let a cancellation unblock the *awaiting* caller immediately even
  if the queued action is still pending).
- `BingoPayoutAutomationService` now takes an `IFrameworkDispatcher` constructor parameter and routes every single
  game-state-touching call through a small `OnFrameworkThreadAsync` helper — never invoked directly from the
  `async` method chain.
- Production wiring (`Plugin.cs`): `new VenueOS.Plugin.Bingo.BingoPayoutAutomationService(ClientState, ChatGui,
  TargetManager, Log, diagnostics, new DalamudFrameworkDispatcher(Framework))`.
- Nothing else changed about *how* verification/trade/gil-entry/confirmation work — only *which thread* each
  Dalamud/FFXIVClientStructs call actually executes on.
- Pure calculations, timers, and the chat-signal capture (`OnChatMessage`, already raised on the framework thread
  by Dalamud itself) were deliberately left un-marshaled — only genuine game-state interaction was moved.

## 8. All payout stages audited for equivalent thread violations

Per §22's requirement to search the whole engine for the same pattern, every Dalamud/FFXIVClientStructs touch in
`BingoPayoutAutomationService` was audited and fixed identically:

| Call | Purpose | Fixed |
|---|---|---|
| `CurrentTargetMatches` (`targetManager.Target`) | initial pinned-target verification | ✅ (the confirmed throw site) |
| `CurrentTargetMatches` | trade-partner re-verification after `/trade` opens | ✅ (same helper, second call site) |
| `clientState.IsLoggedIn` | pre-flight login check | ✅ (marshaled defensively — no live proof it asserts main thread, but it's a Dalamud game-state read and costs nothing to marshal) |
| `ExecuteGameCommand` (`UIModule`/`RaptureShellModule` statics, `Utf8String`) | sending `/trade` | ✅ |
| `IsAddonReady`/`TryGetReadyAddon` (via `WaitUntilAsync`'s condition) | polling for the Trade window, then the numeric-input popup | ✅ — `WaitUntilAsync` itself now marshals every poll of its condition |
| `TryClickButtonByText` (Gil button) | opening the gil-entry popup | ✅ |
| `TrySetNumericInputAndConfirm` | writing the gil amount + confirming | ✅ |
| `TryClickButtonByText` (Ready/Confirm) | readying the offer | ✅ |
| `TryConfirmIfExpected` → `TryClickButtonByText` (SelectYesno) | the post-Ready confirmation dialog, polled once per loop iteration while awaiting the completion chat signal | ✅ — this loop iteration was **already off the framework thread by construction** (it runs after the same await chain), making it a second, real instance of the same defect, not merely a precaution |
| `Abort()` → `TryClickButtonByText` (Cancel/Decline) | best-effort trade-window close on operator Abort | ✅ — dispatched via the same mechanism for defense in depth, since `Abort()` is public API and must not assume its caller is on the framework thread even though today's only caller (the operator panel's Abort button) happens to call it synchronously from Draw() |

The `TryConfirmIfExpected` call inside the completion-signal wait loop is worth calling out specifically: even
before this hotfix, that loop iteration already ran after the exact same off-thread continuation as the initial
target-verification call — it was a second live main-thread violation waiting to be hit by the next live test that
got far enough to reach the Ready/Confirm stage.

## 9. Verification state-machine fix

`ExecutePayoutAttemptAsync` was restructured (its actual step sequence is unchanged) so that:

- All step logic moved into a private `RunAttemptAsync`, called from inside a single try/catch/finally in the
  public method.
- The public method now **always** derives the engine's terminal `Status` from the actual `BingoTradeResult` via a
  new pure helper, `BingoTradeResultStatusText.Describe` (`VenueOS.Modules.Operations.Bingo`,
  `IBingoPayoutAutomation.cs`), immediately before returning — for every outcome: success, explicit failure,
  cancellation, timeout-ambiguous, or unhandled exception. This replaces the old approach of relying on whichever
  in-flight `SetStatus(...)` call happened to run last, which is exactly what left `Status` stuck on `"Verifying
  pinned target..."` after the exception.
- `BingoTradeResultStatusText` is a plain, Dalamud-free static class specifically so this exact regression is
  unit-testable (see §9 tests below) despite `BingoPayoutAutomationService` itself living in the untested
  `VenueOS.Plugin` project (NEW_MODULE_GUIDE.md §30).

Every attempt now ends in exactly one terminal `BingoTradeResult` (`Confirmed`/`Canceled`/`Failed`/`Ambiguous`) —
unchanged from before this hotfix — and now also in exactly one terminal, coherent `Status` string that always
matches that outcome.

## 10. Terminal failure/recovery behavior

`finally { IsBusy = false; expectingConfirmation = false; }` already guaranteed (pre- and post-fix) that the
engine returns to a non-busy state after every attempt, regardless of outcome. This hotfix does not change that
guarantee; it only ensures the *visible* `Status` string agrees with it. The orchestrator layer
(`BingoPayoutOrchestrator.Stage`) already reached a terminal stage (`Done`/`Canceled`/`Failed`/`Ambiguous`) on every
path before this hotfix — unaffected.

## 11. Cancellation / stale-callback protections

- `IsBusy` is set at the very start of `ExecutePayoutAttemptAsync` and only cleared in its `finally` block, so a
  second call while one is genuinely in flight is rejected immediately (`"An attempt is already in progress."`) —
  unchanged by this hotfix, and it is what prevents a queued, stale framework-thread callback from a still-running
  Attempt A from ever being observed concurrently with a *new* Attempt B: a new attempt cannot start until the
  previous one (including all its pending framework-thread continuations) has fully returned.
- `Abort()` still sets `abortRequested` synchronously and is polled at every step boundary in `RunAttemptAsync`
  (unchanged logic, now also dispatching its own best-effort trade-window-close click through the framework
  dispatcher rather than calling the unsafe addon touch inline).
- The orchestrator's own `abortRequested`/attempt-reconciliation logic (`BingoPayoutOrchestrator.RunAsync`) was not
  modified — it already transitions an in-flight-when-aborted attempt to `ambiguous` rather than leaving it
  "pending" (§8 of that class's own doc comment), and this hotfix's new tests exercise that path with a genuinely
  thrown exception/cancellation from the automation boundary (see §17 tests).

## 12. Ledger safety analysis

No change to *when* the orchestrator calls `client.TransitionPayoutAttemptAsync` or with what status — this hotfix
only changes (a) which thread the engine's own game-state calls run on, and (b) how a pre-trade-open engine
failure is classified (`Failed` vs `Ambiguous` — §13 below). Verified via the existing and new orchestrator tests
(§17/§18) that:

- A `Failed`, `Canceled`, or `Ambiguous` engine outcome never advances `outstanding`/`confirmedPaid` — those are
  only ever mutated by the backend in response to a `"confirmed"` transition, which the orchestrator only sends on
  `BingoTradeOutcome.Confirmed`.
- A rejected/mismatched target, an engine exception, and an `OperationCanceledException` from the automation
  boundary each create exactly one backend attempt and never a second one for the same chunk.
- A manual retry after a failed attempt starts a clean, independent `RunAsync` call and does not double-count
  (new test, §17).

## 13. Engine failures: Failed vs. Ambiguous classification

Per the task brief's own question: an engine error that occurs **before** the trade window has ever opened cannot
possibly have moved any gil — there is nothing genuinely uncertain to reconcile, so it should never be recorded as
`Ambiguous`. `BingoPayoutAutomationService` now tracks a `tradeWindowOpened` flag (`volatile bool`, reset at the
start of every attempt, set true the instant the Trade addon is first observed ready). The top-level catch for an
unhandled exception now classifies:

```csharp
result = tradeWindowOpened
    ? BingoTradeResult.Ambiguous($"Unhandled engine error: {ex.Message}")
    : BingoTradeResult.Failed($"Unhandled engine error before the trade window opened (no gil could have changed hands): {ex.Message}");
```

This reuses the **existing** `Failed` outcome/backend status (`VenueBingoClient`/`BingoPayoutOrchestrator` already
send/accept `"failed"` for `BingoTradeOutcome.Failed`) rather than introducing any new backend status value or
protocol field — no backend/protocol change was made or is required. This is a narrow reclassification of exactly
one case (an unhandled exception with no trade window ever opened) — every other `Ambiguous` path (a genuine
post-open timeout with no completion signal, an abort while a trade was in flight, an exception after the trade
window opened) is unchanged and still correctly `Ambiguous`.

## 14. The two existing `ambiguous · 1,000,000` attempts

**Very likely caused by this exact bug**, based on the observed numbers and the code path — documented here as an
explanation, not a guess acted upon (no historical data was touched):

- The orchestrator creates the backend attempt for `min(outstanding, maxChunkGil)` **before** calling into the
  automation engine at all (`BingoPayoutOrchestrator.RunAsync`, `client.CreatePayoutAttemptAsync(...)` happens
  first, `automation.ExecutePayoutAttemptAsync(...)` second). With `maxChunkGil` hard-coded to `1,000,000`
  (`VenueBingoOperatorPanel.AttemptPayoutAsync`) and an observed obligation of 4,650,000 owed, the first chunk
  created would be exactly 1,000,000.
- The engine's target-verification throw happens immediately after that — before `/trade` is ever sent — so
  `tradeWindowOpened` (as tracked by this hotfix) would have been `false`. Pre-fix, that exception was
  unconditionally classified `Ambiguous`, which the orchestrator dutifully transitioned server-side to
  `"ambiguous"` for the already-created 1,000,000 attempt.
- This matches the reported `ambiguous · 1,000,000` rows exactly (amount, and the fact that two identical rows
  exist — consistent with two separate live attempts each hitting the same pre-fix defect).

**These two rows were left completely untouched by this hotfix** (§25 forbids modifying historical data). They
remain available for the operator's own `Mark Paid`/`Mark Not Paid` reconciliation, exactly as VenueOS's existing
model already supports (VenueBingoOperatorPanel's ambiguous-attempt controls, unchanged). Going forward, a
pre-trade-open engine failure will be recorded as `Failed` rather than `Ambiguous` (§13), which better reflects
that no gil could have changed hands in that case.

## 15. UI/status behavior before and after

No operator-panel code was changed. Before this hotfix, `VenueBingoOperatorPanel.DrawAttemptPayoutControls` could
show `Idle` (from `payoutOrchestrator.Stage`/`payoutRunInFlight`, both of which already correctly returned to a
terminal state) alongside `payoutAutomation.Status` still reading `"Verifying pinned target ..."` — the exact
inconsistency from the live screenshot. After this hotfix, `payoutAutomation.Status` is unconditionally derived
from the attempt's actual terminal outcome (§9), so it will read one of `"Trade complete."`, `"Canceled — ..."`,
`"Failed — ..."`, or `"Ambiguous — ..."` once the attempt has ended — never a stale in-progress message.

## 16. Diagnostics behavior

Unchanged: an unhandled engine exception still logs via `log.Error(...)` and routes through
`diagnostics.RecordFailure($"{ModuleId}: payout attempt threw ({ex.Message})")` — still concise, still without a
raw stack trace or any secret, still attributable to `games.bingo`. No new diagnostics call sites were added; the
existing one now additionally feeds the `Failed`-vs-`Ambiguous` classification described in §13.

## 17. Files changed

- `src/VenueOS.Modules.Operations/Bingo/IBingoPayoutAutomation.cs` — added `BingoTradeResultStatusText` (pure,
  unit-tested terminal-status derivation).
- `src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs` — framework-thread marshaling for every game-state
  touch (§7/§8), the `RunAttemptAsync` restructuring for a guaranteed terminal `Status` (§9), and the
  `tradeWindowOpened`-gated `Failed`-vs-`Ambiguous` classification (§13).
- `src/VenueOS.Plugin/Plugin.cs` — added `DalamudFrameworkDispatcher : IFrameworkDispatcher`; updated
  `BingoPayoutAutomationService`'s construction to pass it.
- `tests/VenueOS.Services.Tests/BingoPayoutOrchestratorTests.cs` — four new orchestrator-level regression tests
  (§18).
- `tests/VenueOS.Services.Tests/BingoTradeResultStatusTextTests.cs` — new file, pure status-text regression tests
  (§18).

No other Bingo files, no backend/protocol files, and no other module were touched.

## 18. Tests added/updated

**`BingoTradeResultStatusTextTests.cs` (new, 5 facts + 1 theory over 4 cases = 9 test executions):**
Confirmed/Canceled(with and without detail)/Failed/Ambiguous each describe correctly, and — the direct regression
guard for the live bug — no terminal outcome's description ever contains `"Verifying pinned target"`.

**`BingoPayoutOrchestratorTests.cs` (4 new facts):**
- `Wrong_target_failure_leaves_paid_and_outstanding_unchanged_and_creates_no_further_attempts` — a rejected
  Name@HomeWorld (covers wrong name, wrong world, and no-target-selected, which all surface identically as
  `Failed` from the engine) never touches the ledger and never double-creates an attempt.
- `Engine_exception_during_the_attempt_is_caught_and_transitions_to_ambiguous_never_paid` — an automation
  implementation that throws (simulating an exception escaping the engine's own boundary) is caught by the
  orchestrator, never marks anything paid, and reconciles the server-created attempt to `ambiguous` rather than
  leaving it pending.
- `Canceled_before_a_result_was_observed_transitions_to_ambiguous_and_never_double_creates` — an
  `OperationCanceledException` from the automation boundary (models a stale/aborted framework-thread verification)
  is handled the same way.
- `Manual_retry_after_a_failed_attempt_begins_cleanly_and_does_not_double_count` — two sequential `RunAsync` calls
  on the same orchestrator instance (first fails, operator retries) each create exactly one attempt and the retry
  reaches `Done` cleanly, proving a retry is not contaminated by the prior failed attempt's state.

These, together with the five pre-existing `BingoPayoutOrchestratorTests` (normal payout, canceled outcome,
ambiguous outcome, abort-mid-flight, multi-chunk re-derivation), cover the orchestration/ledger-safety boundary
this hotfix touches. The engine's actual thread-marshaling code (`BingoPayoutAutomationService`) lives in
`VenueOS.Plugin`, which has no test project (NEW_MODULE_GUIDE.md §30, unchanged by this hotfix) — see §20.

## 19. Final test count

**920 tests, 0 failed** (VenueOS.Core.Tests: 4, VenueOS.Venues.Tests: 23, VenueOS.Services.Tests: 893 — up from
907 before this hotfix's 13 new test executions).

## 20. Debug build result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 21. Release build result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 22. Exact live QA still required

Per NEW_MODULE_GUIDE.md §20/§30, automated tests cannot prove actual Dalamud runtime thread behavior — only that
the code now goes through the framework-thread abstraction, that the state machine and ledger transitions are
correct, and that the terminal status text is coherent. **The following must still be verified live, in Dalamud,
before this is trusted further:**

- **Test A — valid target:** pin/target the correct player, Attempt Payout, confirm "Verifying pinned target..."
  appears briefly, **no** "Not on main thread!" error occurs, verification completes, and the flow proceeds to
  opening `/trade`. Abort immediately after successful verification if no real gil transfer should occur in this
  test.
- **Test B — wrong target:** target a different player, Attempt Payout, confirm verification fails safely
  (`Status` shows `Failed — ...`), no trade opens, paid/outstanding unchanged, engine returns to a retryable state.
- **Test C — no target:** clear target, Attempt Payout, confirm clean `Failed` result, no trade, ledger unchanged.
- **Test D — cancel during verify:** begin payout, Abort during "Verifying pinned target...", confirm no later
  continuation/trade occurs and the engine reaches `Canceled`/idle safely.
- **Test E — retry:** after a failed verification, correctly target the intended player and retry; confirm the
  second attempt operates normally and is not contaminated by the first.
- **Test F — existing ambiguous records:** confirm the two existing `ambiguous · 1,000,000` rows remain exactly as
  they were (this hotfix did not touch them) and are not marked paid/unpaid merely as a byproduct of this retest.
- Additionally, once a live attempt can proceed past target verification: confirm the Ready/Confirm-button click
  and the post-Ready `SelectYesno` auto-confirm (`TryConfirmIfExpected`) — the second real thread-affinity site
  found in §8 — also no longer throw, since neither had been reachable in any prior live test.

## 23. Git status

Working tree has exactly the files listed in §17 modified/added, nothing else. No files staged.

## 24. Confirmation

Nothing was staged, committed, pushed, tagged, or released as part of this hotfix. All changes remain in the
working tree for review and the live QA pass in §22.
