# Bingo Auto Payout — Ready/Confirm hotfix (#3)

Targeted production bugfix, phase 3 of the Bingo automated-payout hotfix series. Phase 1
(`BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md`) fixed a "Not on main thread!" crash during pinned-target verification; phase 2
(`BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md`) fixed gil never actually staging in the real Trade window. Both are now
live-validated. This phase fixes the next live-observed blocker: the engine could not find the Trade window's own
Ready/Confirm control once gil was correctly staged. This is not a redesign of Bingo, the payout state machine, or
the server-backed ledger — see the two prior reports and `BINGO_FORENSIC_AUDIT.md`/`BINGO_V2_PROTOCOL.md` for the
design this hotfix builds on.

## 0. Live QA result (0.3.2 release, post-hotfix)

The full live QA plan in §28 below was subsequently executed successfully end to end. A real payout obligation of
6,500,000 gil was paid in full: pinned target verification, Trade window opening, gil staging with positive
read-back, Ready/Confirm activation, and the SelectYesno confirmation all completed correctly across six confirmed
1,000,000-gil chunks plus one correctly-derived 500,000-gil remainder chunk (never an eighth full chunk past what
was actually owed). The backend/server-authoritative ledger reconciled to `paid: 6,500,000`, `outstanding: 0`.
Transaction history correctly retained an earlier `failed · 1,000,000` attempt (predating these hotfixes) alongside
the seven confirmed chunks, without counting it toward paid or converting/hiding it once the obligation was fully
paid. See `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` for the consolidated live-verified status and RELEASE.md's 0.3.2
notes. This closes out this hotfix's own §28 "exact live QA required" list — no further live QA is currently
pending for the payout paths this three-phase hotfix series covered (English-client text/node assumptions remain
the one documented residual caveat — see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md`).

## 1. Executive summary

With phases 1 and 2 live, VenueOS now verifies the pinned target, opens the real Trade window, and successfully
stages the intended gil amount (`1,000,000 gil` visibly confirmed in the live client). The engine then failed with
`Failed — Could not find a Ready/Confirm button on the trade window.` — again correctly leaving the ledger
untouched.

Root cause: the same class of defect phase 2 already fixed for gil entry, now hit one stage later. The engine
searched the Trade addon's buttons for rendered text (`"Ready to Trade"`, `"Confirm Trade"`, `"Ready"`), which very
likely does not exist on the real control — the donor's own proven implementation never searches by text either; it
accesses a **fixed node index** on the Trade addon directly.

Fix: the Ready/Confirm control is now located via that same donor-proven fixed node index (`NodeList[3]`, cast to
`AtkComponentButton*`), with an explicit enabled-state gate before activation — but **activation itself still uses
VenueOS's own existing click mechanism** (`ActivateButton`), not a new technique, to keep the "narrowest fix"
promise. Separately, because this stage is now live-reachable for the first time, the immediately-following
SelectYesno confirmation step was audited per this task's explicit safety requirement and found to be weaker than
the donor's own proven approach in one specific way: it never verified the dialog's own prompt content. That gap is
now closed using the donor's own technique (matching the dialog's prompt text against the game's localized
Trade-confirmation string), so the engine can never confirm an unrelated Yes/No dialog. Gil entry, gil read-back,
target verification, thread marshaling, chunking, and ledger authority are all unchanged.

## 2. Latest live symptom

```
Rabid Squirrel@Halicarnassus
owed:        6,500,000
paid:        0
outstanding: 6,500,000
Attempted chunk: 1,000,000

1. Target verified (phase 1 — live-pass)
2. Trade window opened (phase 1 — live-pass)
3. 1,000,000 gil staged and positively read back (phase 2 — live-pass)
4. Failed — Could not find a Ready/Confirm button on the trade window.

Ledger after failure: paid: 0, outstanding: 6,500,000 (correct, unchanged)
```

## 3. Previously live-validated stages (not reopened without evidence)

- Pinned target verification (phase 1).
- Framework-thread marshaling via `IFrameworkDispatcher`/`DalamudFrameworkDispatcher` (phase 1).
- Trade window opening (`/trade` command + `IsAddonReady` polling) (phase 1/2).
- `Callback.Fire(tradeAddon, true, 2, ZeroAtkValue)` opening the gil-entry popup (phase 2).
- `Callback.Fire(numericInputAddon, true, amount)` committing the gil amount (phase 2).
- `TradeGilTextShows` positive read-back of the staged amount (phase 2).

None of the above were changed in this phase.

## 4. Donor files/methods inspected

Read-only inspection of `C:\FFXIVplugs\ffxivbingo4all` (never modified — see §29):

- `_tmp_dropbox/Dropbox/Dropbox.cs` — `Framework_Update` (the Ready/Trade-button gating and click, and the
  SelectYesno auto-confirm gating and click), `GetSpecificYesno`, the `TradeText` property.
- `_tmp_dropbox/Dropbox/TradeTask.cs` — `TradeTask.Enqueue`/`ConfirmAllowed` (re-confirmed how the donor's own
  automated-payout task interacts with `Framework_Update`'s gating logic — see §7).
- `_tmp_ecommons/ECommons/Automation/Callback.cs` — re-confirmed (already inspected in phase 2, no new findings
  here since the Ready/Confirm path does not use `Callback.Fire` — see §5).

Actual FFXIVClientStructs member verification (this session, against the exact `FFXIVClientStructs.dll` shipped by
the pinned Dalamud SDK — the same verification method used in phase 2): confirmed `AtkResNode.GetComponent()` and
`AtkComponentButton.AtkComponentBase.OwnerNode`/`.IsEnabled` are current, real members with the same shape the
donor's code assumes.

## 5. Donor Ready/Trade mechanism

`Dropbox.Framework_Update`, inside its `if (C.Active || TaskManager.IsBusy) && Svc.Condition[ConditionFlag.TradeOpen]`
gate:

```csharp
var tradeButton = (AtkComponentButton*)(addon->UldManager.NodeList[3]->GetComponent());
if (TradeTask.IsActive) ready = TradeTask.ConfirmAllowed;
if (ready)
{
    if (EzThrottler.Check(ThrottleName) && FrameThrottler.Check(ThrottleName) && tradeButton->IsEnabled
        && EzThrottler.Throttle("Delay", 200) && EzThrottler.Throttle("ReadyTrade", 2000))
    {
        if (!C.NoOp) new ClickGeneric("Trade", (nint)addon).ClickButton(tradeButton);
    }
}
```

No text search of any kind — the button is obtained directly from a fixed node index and clicked via ClickLib's
`ClickGeneric` (a genuine simulated click on an already-identified button, conceptually the same operation as
VenueOS's own `ActivateButton`, just a different library). The `"Trade"` string passed to `ClickGeneric` is a
label used only for the donor's own logging, not a text search — this call could not fail to find the button, only
fail to activate it once identified.

## 6. Exact meaning/type of donor `NodeList[3]`

`NodeList[3]` is a node on the Trade addon's own top-level `UldManager.NodeList`, whose `GetComponent()` the donor
casts directly to `AtkComponentButton*` and calls `->IsEnabled` on before clicking it — i.e. it is asserted (not
merely hoped) to be a button-shaped component, and the donor's own gate (`tradeButton->IsEnabled`) proves the code
author already knew it needed to check interactability before clicking, exactly matching this task's own
requirement (§15). Semantically this is the Trade window's own **"Trade" button** — the control a player clicks to
mark their own side ready/submit the trade (donor's `ClickGeneric("Trade", ...)` label, and its behavior — gating
the click on `ConfirmAllowed` becoming true only after gil has been staged and a short delay has elapsed — confirms
it is the same "Ready/Confirm" action VenueOS's engine already models as one step).

**Whether it is "still current/appropriate":** cannot be proven in this environment (no live client access) — this
is exactly the class of thing this task explicitly says requires live QA, not an assumption. What can be established
without a live client: the *type* of access (`AtkResNode.GetComponent()` cast to `AtkComponentButton*`, gated on
`IsEnabled`) is built from FFXIVClientStructs members confirmed still present and unchanged in the exact
FFXIVClientStructs version VenueOS already references (§4) — so the **mechanism** is current even though the
specific **index** (`3`) can only be confirmed correct by live observation (§28, Live Test B).

## 7. Donor Trade handshake

Tracing `TradeTask.Enqueue` together with `Framework_Update` clarifies the actual sequence the donor's own automated
payout uses:

```
gil staged (Callback.Fire) → DelayNext(15 frames) → ConfirmAllowed = true
→ Framework_Update: `ready` is FORCED to `TradeTask.ConfirmAllowed` (bypassing the "other side already readied"
  check that exists for the DIFFERENT feature of auto-accepting an incoming trade) → click "Trade" once enabled
→ WaitUntilTradeNotOpen (the task queue's own completion signal — the addon disappearing)
```

Critically: for an **outbound automated payout** specifically, the donor does **not** wait for the recipient to
ready up before clicking its own Trade button — it clicks as soon as it is allowed to (staged + brief delay +
button enabled), then relies on the trade naturally completing once the human recipient also readies on their own
side. This matches VenueOS's own existing design exactly: after activating the Ready/Confirm control, VenueOS
already waits (bounded, up to `CompletionSignalTimeout` = 25s) for the game's own `"Trade complete."`/`"Trade
canceled."` chat message — which is precisely the waiting-for-the-other-party period. **No new "wait for remote
readiness" step was needed or added** — the existing completion-signal wait already covers it, and this hotfix
does not change that timeout or loop.

## 8. Donor SelectYesno behavior

```csharp
var addon = GetSpecificYesno(TradeText);
if (addon != null && EzThrottler.Throttle("Delay", 200) && EzThrottler.Throttle("SelectYes", 2000))
    if (!C.NoOp) ClickSelectYesNo.Using((nint)addon).Yes();
```

```csharp
internal static AtkUnitBase* GetSpecificYesno(params string[] s)
{
    for (int i = 1; i < 100; i++)
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i);
        if (addon == null) return null;
        if (IsAddonReady(addon))
        {
            var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
            var text = MemoryHelper.ReadSeString(&textNode->NodeText).ExtractText();
            if (text.EqualsAny(s)) return addon;
        }
    }
    return null;
}
```

`TradeText => Svc.Data.GetExcelSheet<Addon>().GetRow(102223).Text.ExtractText();` — the donor **never** confirms an
arbitrary SelectYesno. It reads the specific dialog's own prompt text (`NodeList[15]`) and only proceeds if it
matches the exact, game-sourced, localized Trade-confirmation string. This is a stronger, content-verified
discriminator than merely "a SelectYesno with a Yes button happens to be open" — see §21 for why this mattered here.

## 9. Current VenueOS old Ready discovery mechanism

`BingoPayoutAutomationService.RunAttemptAsync` (pre-fix):

```csharp
expectingConfirmation = true;
if (!await OnFrameworkThreadAsync(() => TryClickButtonByText(TradeAddonName,
        ["Ready to Trade", "Confirm Trade", "Ready"], ["Cancel", "Decline"]), cancellationToken).ConfigureAwait(false))
    return Fail("Could not find a Ready/Confirm button on the trade window.");
```

`TryClickButtonByText` walks the Trade addon's entire node tree collecting `AtkComponentButton`s and scores them by
matching their rendered text against the three candidate strings — exactly the same technique phase 2 already
proved wrong for the gil control, applied here to a different control with the same result.

## 10. Exact root cause

The real Trade window's Ready/Confirm control is (like the gil control) very likely not exposed as a button with
literally-rendered text matching any of `"Ready to Trade"`/`"Confirm Trade"`/`"Ready"` — so the discovery search
never located a candidate at all, and the attempt failed with the generic "not found" message before ever reaching
an activation attempt.

## 11. New VenueOS mechanism

`src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs`:

```csharp
private unsafe bool TryGetTradeReadyButton(out AtkComponentButton* button)
{
    button = null;
    if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
    if (addon->UldManager.NodeList is null || TradeReadyButtonNodeIndex >= addon->UldManager.NodeListCount) return false;
    var node = addon->UldManager.NodeList[TradeReadyButtonNodeIndex];
    if (node is null) return false;
    var component = node->GetComponent();
    if (component is null) return false;
    var candidate = (AtkComponentButton*)component;
    if (candidate->AtkComponentBase.OwnerNode is null) return false;
    button = candidate;
    return true;
}

private unsafe bool IsTradeReadyButtonAvailable() => TryGetTradeReadyButton(out var button) && button->IsEnabled;

private unsafe bool TryActivateTradeReadyButton()
{
    if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
    if (!TryGetTradeReadyButton(out var button) || !button->IsEnabled) return false;
    return ActivateButton(addon, button);
}
```

`TradeReadyButtonNodeIndex = 3` — the donor's own proven value, ported verbatim, flagged `LIVE VERIFICATION
REQUIRED` (an addon's node layout is defined by the game client, and can shift across versions independent of
FFXIVClientStructs' struct definitions). `RunAttemptAsync` now does a bounded readiness wait
(`WaitUntilAsync(() => IsTradeReadyButtonAvailable(), StepTimeout, ...)`) before a single dispatched activation
attempt (`OnFrameworkThreadAsync(() => TryActivateTradeReadyButton(), ...)`), with `abortRequested` re-checked
immediately before the irreversible activation step.

## 12. Why the new mechanism is safer/current

- **Discovery** now matches the donor's own proven-in-production technique instead of an unverified text guess.
- **Activation** deliberately still uses VenueOS's own existing `ActivateButton` helper (unchanged, the same
  mechanism already used for the SelectYesno click) rather than adopting the donor's ClickLib-based click —
  keeping exactly one click-activation technique in this codebase rather than introducing a second one, per this
  task's "narrowest fix" instruction. Only the *discovery* problem (which the donor also solves differently) was
  ported.
- The button is validated structurally (`OwnerNode` non-null) and gated on `IsEnabled` before any click is
  attempted — the donor's own gate, preserved — so VenueOS will never activate a present-but-not-yet-interactable
  control merely because the addon itself is "ready."

## 13. Framework-thread behavior

Unchanged pattern from phases 1–2: `TryGetTradeReadyButton`/`IsTradeReadyButtonAvailable`/`TryActivateTradeReadyButton`
are only ever invoked via `WaitUntilAsync`/`OnFrameworkThreadAsync`, never directly from the `async` continuation
chain. The new SelectYesno prompt-text check (§21) is likewise only invoked through
`OnFrameworkThreadAsync(() => TryConfirmIfExpected(expectedConfirmationPrompt), cancellationToken)`, and the new
`GetTradeConfirmationPromptTextAsync` Excel-sheet lookup is dispatched the same way (matching
`ShoutRunnerAutomationService`'s own precedent of marshaling `IDataManager` reads through the framework dispatcher).
No `Task.Run`, `.Wait()`, `.Result`, `Thread.Sleep`, or spin-wait was introduced.

## 14. State validation before activation

Before the Ready/Confirm control is ever activated, the existing sequential structure of `RunAttemptAsync` already
guarantees, unchanged: target re-verified against the live trade partner, gil entered AND positively read back
(phase 2, untouched), and `abortRequested` re-checked immediately before this specific irreversible step (newly
added re-check at this exact point, §11). The control itself is additionally required to be both present and
`IsEnabled` (§11/§12) before any activation is attempted.

## 15. Gil read-back preservation

`TryEnterGilAsync`/`TradeGilTextShows` (phase 2) were not touched in this phase. The Ready/Confirm stage only begins
after `TryEnterGilAsync` has already returned `true` — i.e. only after the exact staged amount was positively
confirmed — exactly as before.

## 16. Remote-player/wait behavior

No new "wait for the other player" logic was added — see §7. The existing post-Ready completion-signal wait
(`CompletionSignalTimeout` = 25s, unchanged) already covers this period; the donor's own outbound-automation
behavior confirms this is the correct model (it does not wait for remote readiness before clicking its own button
either).

## 17. SelectYesno safety

Per this task's explicit instruction (§20/§21 of the task), the SelectYesno step was re-audited now that
Ready/Confirm success makes it reachable for the first time. Finding: VenueOS's pre-existing
`TryConfirmIfExpected` gated only on an internal `expectingConfirmation` flag — it never verified the dialog's own
prompt content, unlike the donor's `GetSpecificYesno(TradeText)`. Fixed additively (the existing click mechanism,
`TryClickButtonByText(SelectYesnoAddonName, ["Yes"], ["No"])`, is unchanged) by requiring the open SelectYesno
dialog's own prompt text (read from `NodeList[15]`, the donor's own proven index) to match the exact localized
Trade-confirmation string (resolved once via `dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
.GetRow(102223)`, the donor's own proven Excel row) before a click is ever attempted:

```csharp
private unsafe void TryConfirmIfExpected(string? expectedPromptText)
{
    if (!expectingConfirmation) return;
    if (string.IsNullOrWhiteSpace(expectedPromptText)) return;               // fail closed, never confirm blind
    if (!TryGetReadyAddon(SelectYesnoAddonName, out var addon)) return;
    if (!TryReadSelectYesnoPromptText(addon, out var promptText)) return;
    if (!string.Equals(promptText.Trim(), expectedPromptText.Trim(), StringComparison.Ordinal)) return;
    if (TryClickButtonByText(SelectYesnoAddonName, ["Yes"], ["No"])) expectingConfirmation = false;
}
```

## 18. How unrelated SelectYesno dialogs are prevented from being confirmed

Two independent gates must both hold before any click: (1) `expectingConfirmation` — this attempt is in the brief
post-Ready window where a confirmation is expected at all (pre-existing, unchanged); (2) the open dialog's own
prompt text exactly matches the resolved Trade-confirmation string (new, §17). If the Excel-sheet lookup itself
ever fails (returns null/empty), `expectedPromptText` stays null/empty and `TryConfirmIfExpected` returns
immediately without ever inspecting or clicking anything — a deliberate fail-closed default: an operator can always
confirm manually, but this engine must never guess. Any other SelectYesno dialog (a duty invite, a friend request,
anything else that happens to appear during the same window) will have different prompt text and is therefore never
touched.

## 19. Cancellation/stale callback protections

Unchanged architecture from phases 1–2, confirmed still correct at this now-reachable stage: `IsBusy` still gates
the entire attempt end-to-end, so a stale queued framework-thread callback from a prior attempt cannot race a new
one (no new attempt can start until the previous one, including every pending dispatched call it started, has
fully returned). `abortRequested` is checked immediately before the new activation step (§11) in addition to its
existing check points. No new stale-callback surface was introduced — the new dispatched calls
(`TryGetTradeReadyButton`/`TryActivateTradeReadyButton`/the Excel-sheet lookup/the SelectYesno prompt check) all go
through the same `IsBusy`-gated, single-attempt-at-a-time model as every other step in this class.

## 20. Failure vs Ambiguous boundary

Confirmed and preserved, matching this task's own description exactly (§22/§23 of the task): a Ready/Confirm
discovery or activation failure occurs strictly **before** the game's own Trade submission — nothing has been
placed at risk — so it is classified `Failed`, never `Ambiguous` (unchanged code structure; `Fail(...)` already
always produces `BingoTradeResult.Failed`). The existing boundary for when `Ambiguous` becomes appropriate — the
completion-signal wait timing out with no `"Trade complete."`/`"Trade canceled."` chat signal, *after* Ready/Confirm
has actually been activated — is unchanged and was not touched by this phase; it already correctly reflects "gil
might have transferred and this engine cannot prove the outcome either way."

## 21. Backend ledger safety

No orchestrator code changed in this phase (as in phase 2). A Ready/Confirm-stage failure surfaces from the engine
as `BingoTradeResult.Failed(...)`, which `BingoPayoutOrchestrator.RunAsync` already transitions to `"failed"`
server-side and returns immediately — never touching `confirmedPaid`/`outstanding`, never creating a second attempt
for the same chunk. Proven by the new orchestrator test (§25) using the exact live-incident numbers (6,500,000
owed, 1,000,000 chunk, `Failed`).

## 22. Confirmation donor accounting was NOT ported

No file under `VenueOS.Modules.Operations/Bingo` (the payout-ledger/orchestration layer) was touched. Only
`VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs` (the unsafe engine) and its `Plugin.cs` constructor wiring
(adding the `IDataManager` dependency already used elsewhere in this file) changed. The orchestrator still creates
every attempt against the backend before any in-game action and still re-derives `outstanding` only from the
backend's own response — nothing about this hotfix touches, reads, or depends on any donor accounting concept.

## 23. Multi-chunk behavior

Unchanged. `Math.Min(outstanding, maxChunkGil)` still determines each chunk; a new chunk is still only created
after the previous one reaches a terminal orchestrator stage.

## 24. Files changed

- `src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs` — replaced the Ready/Confirm discovery/activation
  mechanism (new `TryGetTradeReadyButton`/`IsTradeReadyButtonAvailable`/`TryActivateTradeReadyButton`, removed the
  text-search call site in `RunAttemptAsync`); added the SelectYesno prompt-content safety check (new
  `TryReadSelectYesnoPromptText`/`GetTradeConfirmationPromptTextAsync`, changed `TryConfirmIfExpected`'s signature
  to require and check the expected prompt text); added the `IDataManager dataManager` constructor dependency and
  the `TradeReadyButtonNodeIndex`/`SelectYesnoPromptTextNodeIndex`/`TradeConfirmationAddonSheetRowId` constants;
  updated the class-level doc comment. `TryEnterGilAsync`, `TradeGilTextShows`, `TryOpenGilInputPopup`,
  `TryCommitNumericInput`, target verification, and Abort's own best-effort Cancel/Decline click were **not**
  touched.
- `src/VenueOS.Plugin/Plugin.cs` — updated the `BingoPayoutAutomationService` construction call to pass
  `DataManager` (an existing `[PluginService]` already used by other modules in this file).
- `tests/VenueOS.Services.Tests/BingoPayoutOrchestratorTests.cs` — one new orchestrator-level regression test
  (§25).

No backend/protocol files, no other Bingo file, and no other module were touched.

## 25. Tests added/updated

`BingoPayoutOrchestratorTests.cs` (1 new fact):

- `Ready_confirm_discovery_failure_leaves_the_full_obligation_outstanding_and_creates_no_further_attempts` —
  replays the exact live incident (Rabid Squirrel@Halicarnassus, 6,500,000 owed, 1,000,000 chunk, engine reports
  `Failed — Could not find a Ready/Confirm button on the trade window.`) and proves the backend transition is
  `"failed"`, `outstanding` stays at 6,500,000, and exactly one attempt is created.

As in phases 1–2, the actual Ready/Confirm and SelectYesno mechanics (`TryGetTradeReadyButton`,
`TryActivateTradeReadyButton`, `TryConfirmIfExpected`, `GetTradeConfirmationPromptTextAsync`) live in
`BingoPayoutAutomationService`, in the untested `VenueOS.Plugin` project (NEW_MODULE_GUIDE.md §30) — they cannot be
unit-tested directly. The new test exercises the orchestrator/ledger-safety boundary, which is what is actually
testable. See §27 for what automated testing cannot prove.

## 26. Exact final test results

**923 tests, 0 failed, 0 skipped** (VenueOS.Core.Tests: 4, VenueOS.Venues.Tests: 23, VenueOS.Services.Tests: 896 —
up from 922 before this phase's 1 new test).

## 27. Debug build result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## Release build result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 28. Exact live QA required

Automated tests cannot prove the actual current Trade addon's node layout, that `NodeList[3]` is really the
Ready/Confirm button on the current client, or that `NodeList[15]`/Excel row `102223` still hold what the donor
found. The following must be verified live, in Dalamud:

**Live Test A — reproduce now-working stages.** Target correctly, Attempt Payout; confirm target verification,
Trade opening, and gil staging/read-back all still work exactly as phase 2 left them. Stop and report immediately
if any of these regress.

**Live Test B — Ready/Confirm control.** Confirm `Failed — Could not find a Ready/Confirm button on the trade
window.` no longer occurs; confirm VenueOS activates the correct control (observe the actual Trade window state
change — e.g. the local side shows as readied) and does not touch any unrelated control.

**Live Test C — remote player state.** If the game requires the recipient to ready up too, confirm VenueOS waits
normally (via the existing completion-signal loop) without spamming the Ready control, and advances correctly once
the recipient acts.

**Live Test D — SelectYesno.** Confirm no `Not on main thread!` error; confirm VenueOS recognizes only the expected
Trade confirmation and does not react to an unrelated SelectYesno dialog if one happens to appear. Stop at the last
safe point before transfer if a real gil transfer is not intended for this test.

**Live Test E — first successful real payout (if intentionally permitted).** Allow one 1,000,000 chunk to complete
for the current 6,500,000 example; verify the recipient actually receives it; verify the backend records exactly
`paid: 1,000,000`, `outstanding: 5,500,000`; then sync/reload payout state and confirm the server truth is what
persists (per this task's §39, since the donor's own known defect was exactly this kind of state getting lost
locally) — do not perform a second payment until the first's ledger state is confirmed correct.

**Live Test F — abort.** Abort before final transfer if timing permits; confirm no stale Ready/Trade/Yes action
occurs afterward and the ledger is unchanged.

**Live Test G — retry.** After a failed/aborted attempt, retry cleanly; confirm no stale state contamination.

## 29. Git status

VenueOS: exactly the files in §24 modified, nothing else; nothing staged. Donor (`ffxivbingo4all`): unchanged —
`git status` shows only pre-existing untracked reference material (`_tmp_clicklib/`, `_tmp_dropbox/`,
`_tmp_ecommons/`, present before this session and never written to by it); `git diff --stat` against any tracked
file is empty.

## 30. Confirmation

Nothing was staged, committed, pushed, tagged, or released in VenueOS as part of this hotfix. The donor repository
(`ffxivbingo4all`) was not modified, formatted, staged, committed, or otherwise altered in any way — read-only
forensic inspection only.
