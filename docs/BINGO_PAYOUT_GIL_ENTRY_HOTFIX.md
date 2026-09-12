# Bingo Auto Payout — gil-entry hotfix (#2)

Targeted production bugfix, phase 2 of the Bingo automated-payout hotfix series. Phase 1
(`BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md`) fixed a live "Not on main thread!" crash during pinned-target verification and
has since been live-tested successfully. This phase fixes the next live-observed defect: gil could not be entered
into the real FFXIV Trade window once it opened. This is not a redesign of Bingo, the payout engine, or the
server-backed payout ledger — see `BINGO_FORENSIC_AUDIT.md`, `BINGO_V2_PROTOCOL.md`, and phase 1's own report for the
design this hotfix builds on.

## 1. Executive summary

With phase 1 live, VenueOS now correctly verifies the pinned target and opens the real FFXIV Trade window with the
correct player. But the gil field stayed at `0 gil`, and the attempt terminated with `Failed — Could not enter the
gil amount into the trade.` — correctly leaving the ledger untouched (`paid: 0`, `outstanding: 7,750,000`).

Root cause: the engine's gil-entry code was **never proven to work against a real client** — it searched the Trade
addon for a button literally labeled `"Gil"`/`"Add Gil"` (which the real Trade window very likely does not have —
its gil control is not a text-labeled button), and separately wrote `AtkComponentNumericInput.SetValue(...)` directly
followed by simulating a click on the numeric popup's `OkButton` node. Neither step is what the game's own UI
scripting layer actually uses to communicate "the user entered this amount" back to the addon that opened the
popup.

Fix: ported the **donor Bingo plugin's own proven-in-production mechanism** for exactly this one step —
`ECommons.Automation.Callback.Fire`, a thin wrapper over the game's native `AtkUnitBase.FireCallback` (the same
synthetic-event path the game itself uses for a button click or a completed numeric entry) — verbatim for opening
the gil-input popup and for committing the amount. Nothing about the donor's payout **accounting** was touched or
reused; VenueOS's server-authoritative payout ledger, chunking, target-identity, cancellation, and diagnostics are
entirely unchanged. A mandatory positive read-back was added on top of the donor's own mechanism, since the donor
itself never verified what it wrote — it is scanned for as a new requirement of this hotfix, not something the donor
did.

## 2. Live failure

```
Maya Baker
owed: 7,750,000
paid: 0
outstanding: 7,750,000
Target: Maya Baker@Rafflesia
Trade window: opened correctly, gil field remained 0 gil
Result: Failed — Could not enter the gil amount into the trade.
Ledger after failure: paid: 0, outstanding: 7,750,000 (correct, unchanged)
```

## 3. Donor files/methods inspected

Read-only inspection of `C:\FFXIVplugs\ffxivbingo4all` (never modified — see §26):

- `_tmp_dropbox/Dropbox/TradeTask.cs` — `TradeTask.Enqueue`, `TradeTask.OpenGilInput`,
  `TradeTask.SetNumericInput`, `TradeTask.UseTradeOn`, `TradeTask.WaitUntilTradeOpen`/`WaitUntilTradeNotOpen`.
- `_tmp_dropbox/Dropbox/Dropbox.cs` — `Framework_Update` (the Ready/Confirm-button gating and gil/item read-back
  logic used for auto-accepting incoming trades), `GetSpecificYesno`, `Chat_ChatMessage`.
- `_tmp_dropbox/Dropbox/Memory.cs` — `Memory.SafeOfferItemTrade` (item-slot automation; not relevant to gil entry,
  inspected for completeness).
- `_tmp_ecommons/ECommons/Automation/Callback.cs` — `Callback.Fire`, `Callback.ZeroAtkValue`,
  `Callback.FireRaw`/`AtkUnitBase_FireCallbackDetour` (confirms `Fire` calls `AtkUnitBase.FireCallback` directly and
  needs no hook installed).

## 4. Exact donor Trade UI mechanism

`Dropbox.TradeTask.Enqueue` drives a task-queue sequence:

```
ConfirmAllowed = false
→ UseTradeOn(player)          // sets target if needed, sends "/trade" once throttled
→ WaitUntilTradeOpen           // polls Svc.Condition[ConditionFlag.TradeOpen] — NOT an addon-ready check
→ OpenGilInput                 // Callback.Fire(tradeAddon, true, 2, Callback.ZeroAtkValue)
→ SetNumericInput(gil)          // Callback.Fire(numericInputAddon, true, gil)
→ DelayNext(15 frames)
→ ConfirmAllowed = true         // gates Framework_Update's own Ready/Confirm-button click + SelectYesno auto-accept
→ WaitUntilTradeNotOpen
```

`OpenGilInput`/`SetNumericInput` each retry every few frames (via the task queue's own throttled re-invocation) until
the target addon (`"Trade"` / `"InputNumeric"`) is found ready; neither is a strict one-shot call.

## 5. Donor gil insertion sequence

The critical technique in both `OpenGilInput` and `SetNumericInput` is `ECommons.Automation.Callback.Fire(addon,
updateState, values...)`, which allocates an `AtkValue[]` from the given `values` and calls
`Base->FireCallback((uint)values.Length, atkValues, updateState)` — invoking the addon's own native callback handler
directly, the exact same synthetic-event mechanism the game's UI scripting layer uses internally to report a button
click or a completed numeric entry to its owning addon:

- `OpenGilInput`: `Callback.Fire(tradeAddon, true, 2, Callback.ZeroAtkValue)` — callback index `2` is the donor's own
  proven value for "open the gil-input popup" on the Trade addon.
- `SetNumericInput(num)`: `Callback.Fire(numericInputAddon, true, num)` — a single `int` value, both setting AND
  confirming the amount in one atomic call (there is no separate "click OK" step anywhere in the donor's sequence).

Never once does the donor call `AtkComponentNumericInput.SetValue` directly, and never once does it search for a
button by its displayed text on the Trade addon.

## 6. Explicit donor authority boundary

Per this task's own instruction, the donor is authoritative **only** for the mechanical Trade-UI interaction
technique above. It is explicitly **not** authoritative for payout accounting, obligation/attempt modeling, ledger
authority, chunking policy, target-identity verification, cancellation semantics, or diagnostics — all of which
remain exactly as phase 1 left them, entirely VenueOS's own design.

## 7. Known donor memory/accounting defect (why it was not ported)

Per `BINGO_FORENSIC_AUDIT.md` and this task's own brief: the donor tracked cumulative payout state in
process-local/in-memory structures that could lose track of how much gil had actually been paid — e.g., cleared on
room rejoin, with no durable backend record of `paid`/`outstanding` at all. VenueOS's `payout_obligations`/
`payout_attempts` backend tables (`BINGO_V2_PROTOCOL.md`) and `BingoPayoutOrchestrator`'s re-derive-outstanding-from-
the-backend-response design (never a local running total) exist specifically to not have this defect. Nothing about
this hotfix changes that: **no donor accounting code, data model, or persistence approach was read into, referenced
by, or ported into any VenueOS payout-state code.** Only the two `Callback.Fire` calls (a game-UI interaction
technique with no accounting semantics at all) were ported.

## 8. Current VenueOS Trade UI mechanism before this fix

`BingoPayoutAutomationService.TryEnterGilAsync` (pre-fix):

```csharp
if (!TryClickButtonByText(TradeAddonName, ["Gil", "Add Gil"], [])) return false; // searches Trade's buttons by text
...
numericAddon->NumericInput->SetValue(amount);                                    // raw component-field write
return ActivateButton((AtkUnitBase*)numericAddon, numericAddon->OkButton);        // simulates a node click event
```

`TryClickButtonByText` walks the addon's node tree collecting `AtkComponentButton`s and matches by their rendered
text against `["Gil", "Add Gil"]`; `ActivateButton` calls `ReceiveEvent` on the button's own registered UI event.
Both were explicitly flagged in the code's own doc comments as unverified against a live client.

## 9. Exact technical difference

| | Old VenueOS mechanism | Donor / new VenueOS mechanism |
|---|---|---|
| Open gil popup | Search Trade addon's buttons for text `"Gil"`/`"Add Gil"`, then simulate a click event on whichever button matched | `Callback.Fire(tradeAddon, true, 2, ZeroAtkValue)` — the addon's own native callback |
| Commit amount | `NumericInput->SetValue(amount)` (raw field write) + simulate a click on `OkButton` | `Callback.Fire(numericInputAddon, true, amount)` — one atomic native callback |
| Read-back | None — the write was trusted merely because it did not throw | Mandatory (§15 below) |

The root difference: a raw component-field write and a simulated node-click event are **not the same code path**
the game engine uses internally to process a numeric-entry confirmation or a button press communicated to a parent
addon. `FireCallback` is that actual code path. Separately, no button literally labeled `"Gil"`/`"Add Gil"` is
confirmed to exist on the real Trade addon at all, which independently could have caused the very first step to
silently fail every time.

## 10. Root cause

Both defects were present in the code from the start, each independently sufficient to explain `0 gil` staying at
`0 gil`: (a) the "Gil" button search likely never matched anything real, so the numeric popup may never have opened
correctly in the way the game expected, and/or (b) even when the popup did appear, writing its value directly and
clicking a node did not propagate the value back to the Trade window's own display, because that requires firing
the popup's own native confirm callback — which the old code never did.

## 11. New VenueOS Trade UI implementation

`src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs`:

```csharp
private unsafe bool TryOpenGilInputPopup()
{
    if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
    try { Callback.Fire(addon, true, TradeOpenGilInputCallbackIndex, Callback.ZeroAtkValue); return true; }
    catch (Exception ex) { log.Warning(ex, "..."); return false; }
}

private unsafe bool TryCommitNumericInput(int amount)
{
    if (!TryGetReadyAddon(NumericInputAddonName, out var addon)) return false;
    try { Callback.Fire(addon, true, amount); return true; }
    catch (Exception ex) { log.Warning(ex, "..."); return false; }
}
```

`TradeOpenGilInputCallbackIndex = 2` — the donor's own proven value, carried over verbatim; flagged
`LIVE VERIFICATION REQUIRED` since an addon's internal callback-index protocol is defined by the game client itself,
not by Dalamud/ECommons, and can in principle change across game versions independent of the FFXIVClientStructs
struct layout used to reach the addon. `ECommons.Automation.Callback` (package version `3.2.1.18`, the exact version
VenueOS already pins — the donor itself pins the older `3.1.0.5`) was confirmed, by inspecting the referenced
`ECommons.dll`'s metadata directly in this session, to still expose `Fire`/`ZeroAtkValue` with the identical
signature the donor uses.

## 12. Framework-thread behavior

Both new calls are dispatched exactly like every other addon touch in this class (phase 1's fix, preserved
unchanged): `TryEnterGilAsync` calls them via `OnFrameworkThreadAsync(() => TryOpenGilInputPopup(), cancellationToken)`
/ `OnFrameworkThreadAsync(() => TryCommitNumericInput(amount), cancellationToken)`, never invoked directly from the
`async` continuation chain. The new read-back check (`TradeGilTextShows`) is likewise only ever invoked through
`WaitUntilAsync`, which itself marshals every poll of its condition via the same dispatcher. No `Task.Run`, `.Wait()`,
`.Result`, `Thread.Sleep`, or spin-wait was introduced.

## 13. Trade addon/component/node/callback details

- **Open gil popup**: `Callback.Fire` on the `"Trade"` addon (found via the existing `TryGetReadyAddon` helper,
  unchanged) with values `(2, ZeroAtkValue)`.
- **Commit amount**: `Callback.Fire` on the `"InputNumeric"` addon (the same shared game-wide numeric-entry popup
  already used elsewhere; unchanged addon name) with the single value `amount`.
- **Read-back**: a new node-index-agnostic text scan (§15) — deliberately does **not** use the donor's own
  `NodeList[6]` (that index is the donor's proxy for the OTHER party's offered gil, used there to decide whether to
  auto-accept an incoming trade — not proven to be MY OWN staged amount, and guessing an index for that would be
  exactly the kind of unverified assumption this task explicitly warns against).

## 14. Readiness/timing behavior

Unchanged for popup readiness: `WaitUntilAsync(() => IsAddonReady(NumericInputAddonName), StepTimeout, ...)` still
gates firing the commit callback on the popup actually being ready. New: after committing, a bounded
`WaitUntilAsync(() => TradeGilTextShows(amount), StagedGilReconcileTimeout (3s), ...)` allows the game's own UI at
least one framework tick (typically several, bounded to 3 seconds) to reconcile the popup's callback into the Trade
window's own display before the read-back is checked — matching this hotfix's required "detect readiness → perform
write once → wait/reconcile → verify" sequence. No mutation is retried in a tight loop; only the verification polls.

## 15. Exact amount read-back behavior

`TradeGilTextShows(int amount)` scans every currently visible text node reachable from the Trade addon (a new
traversal, `CollectVisibleText`/`CollectTextFromNode`, structurally identical to the existing
`CollectButtons`/`CollectButtonsFromNode` traversal but collecting `AtkTextNode` text instead of buttons — kept as a
separate method rather than folding into the button collector, to avoid touching the already-working, previously
live-tested button-search path used elsewhere in this class for Ready/Confirm and SelectYesno). Each visible text's
digits (after stripping thousands separators/whitespace) are compared for an exact match against the intended
amount. This is deliberately **node-index-agnostic**: since there is no donor-proven index for "my own" staged gil
display, searching all visible text for the exact expected number avoids depending on an unverified index, at the
cost of relying on the practical fact that the intended amount is always a specific, deliberately-chosen large gil
number (collision with an unrelated on-screen number is very unlikely). This is explicitly flagged
`LIVE VERIFICATION REQUIRED` — it is the best available verification given no better donor-proven alternative exists,
not a claim that it is provably correct.

## 16. Failure/timeout behavior

If the popup never opens (`TryOpenGilInputPopup` returns false, or the subsequent `IsAddonReady` wait times out), if
committing the amount fails, or if the positive read-back never observes the exact expected amount within the
bounded timeout, `TryEnterGilAsync` returns `false` and the caller reports `Fail("Could not enter the gil amount into
the trade.")` — unchanged wording, still routed through the existing terminal-status machinery from phase 1
(`BingoTradeResultStatusText`), so `Status` always ends up describing this failure rather than staying on an
in-progress message. This can never hang: every step is either a single dispatched call or a bounded
`WaitUntilAsync`.

## 17. Cancellation/stale callback protections

Unchanged from phase 1, and confirmed still correct for the newly-reachable gil-entry stage: `abortRequested` is
checked at the same step boundaries around gil entry (`if (abortRequested) return BingoTradeResult.Canceled(...)`
immediately before and after `TryEnterGilAsync` in `RunAttemptAsync`), and `IsBusy` still gates the entire attempt,
so a second `ExecutePayoutAttemptAsync` call cannot start — and therefore cannot race a stale queued framework-thread
callback from a still-in-flight prior attempt — until the current one (including every pending
`OnFrameworkThreadAsync` continuation it started) has fully returned. No new stale-callback surface was introduced:
the two new dispatched calls (`TryOpenGilInputPopup`, `TryCommitNumericInput`) and the new bounded read-back poll all
go through the exact same `IsBusy`-gated, single-attempt-at-a-time model as every other step.

## 18. Ledger authority/safety analysis

No orchestrator code changed. A gil-entry failure surfaces from the engine as `BingoTradeResult.Failed(...)`, which
`BingoPayoutOrchestrator.RunAsync` already handles by transitioning the backend attempt to `"failed"` and returning
immediately — never touching `confirmedPaid`/`outstanding`, never creating a second attempt for the same chunk. This
is proven by the new orchestrator test in §24 using the exact live-incident numbers (7,750,000 owed, 1,000,000
chunk, `Failed`) and was already true architecturally before this hotfix — this hotfix does not change that
behavior, only the mechanics of the in-game step that produces the `Failed` outcome.

## 19. Confirmation donor accounting was NOT ported

No file under `VenueOS.Modules.Operations/Bingo` (the payout-ledger/orchestration layer) was touched by this hotfix
— only `VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs` (the unsafe engine) and its `Plugin.cs` wiring (already
established in phase 1) changed. `BingoPayoutOrchestrator` still creates every attempt against the backend before any
in-game action, still re-derives `outstanding` only from the backend's own response after a confirmed attempt, and
still never accumulates a local running total. The new `A_freshly_constructed_orchestrator_resumes_purely_from_
backend_reported_outstanding` test (§24) explicitly proves a brand-new orchestrator instance — modeling a plugin
reload or engine recreation between chunks — completes an obligation correctly using only backend-supplied values,
with no in-memory carryover required or possible.

## 20. Multi-chunk behavior

Unchanged: `Math.Min(outstanding, maxChunkGil)` still determines each chunk, `outstanding` is still only ever
reassigned from a confirmed `PATCH` response, and a new chunk is still only created after the previous one reaches
`Done`/terminates. See §24's new "Maya Baker" tests for direct confirmation using the exact incident numbers.

## 21. Immediate next-stage audit (§13/§31 of this task)

Audited the Ready/Confirm-button click and the post-Ready `SelectYesno` auto-confirm — the two stages immediately
after gil entry, now reachable for the first time in live testing:

- `TryClickButtonByText(TradeAddonName, ["Ready to Trade", "Confirm Trade", "Ready"], ["Cancel", "Decline"])` (the
  Ready/Confirm click) and `TryConfirmIfExpected`'s `TryClickButtonByText(SelectYesnoAddonName, ["Yes"], ["No"])`
  (the post-Ready confirmation) are both still dispatched through `OnFrameworkThreadAsync`/`WaitUntilAsync` exactly
  as phase 1 left them — **confirmed intact, no thread-affinity regression.**
- **Not fixed, only noted**: the donor's own equivalent Ready-button interaction
  (`Dropbox.Framework_Update`) accesses the Trade addon's Ready/Trade button by a **fixed node index**
  (`NodeList[3]`), not by searching rendered text the way VenueOS's `TryClickButtonByText` does. This raises the
  same category of question gil entry had — whether the real button is reliably discoverable by displayed text at
  all — but this stage has **not yet been live-tested and no failure has been observed there**. Per this task's
  explicit scope ("audit the next stage — do not redesign it," "only fix proven equivalent defects"), this was
  **not** changed pre-emptively. It is called out here as the most likely next thing to watch if live QA reaches
  that stage and hits a similar silent failure.

## 22. Historical ambiguous-record handling

No code in this hotfix touches persisted payout-attempt records of any kind. The two existing `ambiguous ·
1,000,000` rows (documented as very likely caused by the phase-1 thread-affinity bug) remain exactly as phase 1 left
them — untouched, available only for the operator's own `Mark Paid`/`Mark Not Paid` reconciliation.

## 23. Files changed

- `src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs` — replaced the gil-entry mechanism
  (`TryEnterGilAsync`/new `TryOpenGilInputPopup`/`TryCommitNumericInput`/`TradeGilTextShows`/
  `CollectVisibleText`/`CollectTextFromNode`; removed the old `TrySetNumericInputAndConfirm`), added the
  `ECommons.Automation` using and the `TradeOpenGilInputCallbackIndex`/`StagedGilReconcileTimeout` constants, and
  updated the class-level doc comment to describe the new mechanism. No other method changed.
- `tests/VenueOS.Services.Tests/BingoPayoutOrchestratorTests.cs` — two new orchestrator-level regression tests
  (§24).

No backend/protocol files, no other Bingo file, and no other module were touched. `Plugin.cs` and
`IBingoPayoutAutomation.cs` (changed in phase 1) were not touched again in this phase.

## 24. Tests added/updated

`BingoPayoutOrchestratorTests.cs` (2 new facts):

- `Gil_entry_failure_leaves_the_full_obligation_outstanding_and_creates_no_further_attempts` — replays the exact
  live incident (Maya Baker, 7,750,000 owed, 1,000,000 chunk, engine reports `Failed — Could not enter the gil
  amount into the trade.`) and proves the backend transition is `"failed"`, `outstanding` stays at 7,750,000, and
  exactly one attempt is created.
- `A_freshly_constructed_orchestrator_resumes_purely_from_backend_reported_outstanding` — the donor-accounting
  regression guard (§19): a brand-new `BingoPayoutOrchestrator` instance, given an obligation reflecting
  1,000,000 already confirmed by a prior session (6,750,000 outstanding), completes correctly using only the
  backend-supplied values.

As in phase 1, the actual gil-entry mechanics (`TryOpenGilInputPopup`/`TryCommitNumericInput`/`TradeGilTextShows`)
live in `BingoPayoutAutomationService`, in the untested `VenueOS.Plugin` project (NEW_MODULE_GUIDE.md §30) — they
cannot be unit-tested directly. The new tests exercise the orchestrator/ledger-safety boundary, which is what is
actually testable and is exactly what this hotfix's own ledger-safety requirements (§18–§20 of the task) are about.
See §28 for what automated testing cannot prove.

## 25. Final test count

**922 tests, 0 failed, 0 skipped** (VenueOS.Core.Tests: 4, VenueOS.Venues.Tests: 23, VenueOS.Services.Tests: 895 —
up from 920 before this phase's 2 new tests).

## 26. Debug build result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 27. Release build result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 28. Exact live QA required

Automated tests cannot prove actual FFXIV Trade addon behavior — only that the code goes through the framework-
thread abstraction, that ledger transitions stay correct, and that the state machine terminates. The following must
be verified live, in Dalamud, before this is trusted further:

**Live Test A — correct target/amount insertion.** Use a real payout obligation. Target the correct player. Attempt
Payout. Confirm target verification succeeds, the real Trade window opens, and the gil field visibly changes from
`0 gil` to the exact intended chunk. Confirm `Failed — Could not enter the gil amount into the trade.` does **not**
occur.

**Live Test B — next stage.** Allow the workflow to proceed far enough to exercise Ready/Confirm and, if reached,
`SelectYesno`. Confirm no `Not on main thread!` error and normal confirmation behavior. Abort at the last safe point
before final transfer if a real gil transfer is not wanted for this test — do not invent a test-only bypass.

**Live Test C — wrong target.** Target a different player, Attempt Payout; confirm verification fails safely, no gil
is staged, no Trade submission, ledger unchanged.

**Live Test D — no target.** Clear target, Attempt Payout; confirm clean failure, no gil staged, no Trade
submission, ledger unchanged.

**Live Test E — abort.** Begin a correct payout, Abort after Trade opens but before final submission if timing
permits; confirm no stale later Trade action, no ledger mutation, clean termination.

**Live Test F — retry.** After a failure/abort, correctly target the intended player and retry; confirm a clean new
attempt with no stale state from the previous one.

**Live Test G — ledger.** Confirm failed attempts did not change `paid`/`outstanding`, and the existing historical
`ambiguous · 1,000,000` rows remain untouched.

Additionally, if Test A reveals that `TradeOpenGilInputCallbackIndex = 2` does not open the gil popup on the current
client, or that `TradeGilTextShows`'s text scan does not find the staged amount even though the Trade window
visibly shows it correctly, both are isolated, single-point LIVE VERIFICATION REQUIRED items called out in the code
and in §11/§15 above — not evidence the overall approach is wrong.

## 29. Git status

VenueOS: exactly the files in §23 modified, nothing else; nothing staged. Donor (`ffxivbingo4all`): unchanged —
`git status` shows only pre-existing untracked reference material (`_tmp_clicklib/`, `_tmp_dropbox/`,
`_tmp_ecommons/`, present before this session and never written to by it); `git diff --stat` against any tracked
file is empty.

## 30. Confirmation

Nothing was staged, committed, pushed, tagged, or released in VenueOS as part of this hotfix. The donor repository
(`ffxivbingo4all`) was not modified, formatted, staged, committed, or otherwise altered in any way — read-only
forensic inspection only.
