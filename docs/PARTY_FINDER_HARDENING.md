# Party Finder Refresh Hardening

Status: **implementation accepted for release (VenueOS 0.3.7). Short/live functional QA acceptable; extended
real-session soak validation remains ongoing/recommended.**

The three concrete defects described below (single-active-refresh ownership, state-based context validation, and
the leaked chat-message subscription) have been reviewed and accepted for release. The hardening is described in
release notes as refresh/lifecycle **hardening**, not as a conclusive fix for the historical intermittent
production symptom — that symptom was intermittent by nature and requires a long real venue-night soak/torture
test (TEST H, §22) for high confidence, which has not yet been performed. Short functional exercises of the
module (including confirming Party Finder keeps refreshing normally while its window is Collapsed/Hidden, done as
part of the Module Launcher & Window Management pass's own live QA) are acceptable evidence the changes don't
regress normal operation, but they are not a substitute for the extended soak test.

Module ID: `promotion.partyfinder`. This is a production-reliability hardening pass over Party Finder's refresh
lifecycle, triggered by a user report of intermittent refresh failures that has now reproduced on the user's main
PC (so it is treated as a real timing/state-ownership defect, not a machine-specific performance issue). It builds
on `PARTY_FINDER_RECONSTRUCTION.md` (the module's original VenueOS-native build-out) and `PARTY_FINDER_PHASE_2D.md`
(pre-reconstruction standalone repair) — both remain the historical record of how the module was built; this
document is the current source of truth for its reliability characteristics.

## 1. Executive summary

Party Finder's automation engine (`PartyFinderAutomationService`, `src/VenueOS.Plugin/PartyFinder/`) already used a
non-blocking, per-tick task-chain model (ECommons `TaskManager`) with bounded, state-polling waits
(`BoundedWait`) for most of its native-UI readiness checks — this was clearly the product of an earlier hardening
pass (extensive "Reliability hardening" doc comments already existed throughout the file before this pass began).
The forensic trace for this pass found three concrete, provable gaps left in that otherwise-solid design, all of
which plausibly explain the reported intermittent symptom, plus one unrelated but real resource-leak defect:

1. **No single-active-refresh ownership at the orchestration layer.** `PartyFinderService.CreateOrUpdate`/`Refresh`
   (and the automatic 5-minute-warning chat trigger) had no guard against being invoked while a previous request
   was still in flight. The automation engine's own `QueueOperation` responded to a second request by aborting the
   chain already running and restarting from scratch. This meant: (a) an operator clicking "Refresh" a second time
   because they saw no immediate visual feedback would restart the ~1.5–2 second automation chain, and repeated
   clicking could prevent it from ever reaching completion; (b) the native 5-minute-warning chat event firing while
   the operator happened to be mid-manual-Create/Edit could silently abort their in-flight action. Both are
   plausible, timing-dependent explanations for "refresh sometimes just doesn't seem to work," including on a fast,
   otherwise-healthy PC — this is a state-ownership race, not a performance problem, which matches the user's
   report reproducing on their main machine.
2. **No positive game-context validation before or during automation.** The engine's `PrepareOperation` step
   checked `IClientState.IsLoggedIn` but, when false, returned `true` (task-chain convention for "step complete,
   proceed") instead of stopping the chain — so it continued into `EnsureMainAddonReady`'s addon-discovery loop
   regardless, which would then spend its full 8-second timeout failing to find a Party Finder addon that could
   never appear, and report a scary, misleading "Failed to detect a visible Party Finder window" diagnostic error
   even when the real, entirely benign cause was that the character was logging out or between zone loads. No wait
   loop re-checked context validity mid-poll, either, so an attempt that started in a valid context but hit a zone
   transition partway through would similarly burn its full timeout before failing.
3. **A leaked chat-message subscription on plugin dispose.** `Plugin.cs` wired Party Finder's automatic-refresh chat
   trigger as an inline lambda (`ChatGui.ChatMessage += message => partyFinderService.HandleChatText(...)`) and
   never unsubscribed it — unlike every sibling chat subscription in the same constructor
   (`giveawaysRollChatAdapter.Unsubscribe(ChatGui)`, and `ShoutRunnerAutomationService`/`BingoPayoutAutomationService`
   both unhook their own chat events in their own `Dispose()`). On `/xlreload`/`/xlrestart`/plugin
   disable-then-re-enable, the stale handler stayed registered on Dalamud's shared `IChatGui`, permanently closed
   over the disposed `PartyFinderService`/`PartyFinderAutomationService` from the old plugin instance.

None of these required redesigning the module's task-chain/bounded-wait architecture, which was sound. All three
are fixed by tightening ownership/guard conditions and a dispose-path correction, not by introducing a new state
machine.

## 2. Production symptom

Intermittent Party Finder refresh failures reported in real venue use, now reproduced on the user's main PC — ruling
out a slow/loaded-machine-only explanation and pointing at a genuine timing/state-race defect in the refresh
workflow rather than a one-off environmental issue.

## 3. Full old refresh lifecycle (as traced)

Party Finder has **no internal recurring timer**. Refresh is driven entirely by the native FFXIV chat message that
warns a listing is about to expire ("...five minutes...", matched by
`PartyFinderChatEvents.IsFiveMinuteWarning`), subscribed once for the plugin's lifetime via
`IChatGui.ChatMessage`, dispatched to `PartyFinderService.HandleChatText` on every chat line. A 4-minute throttle
(`PartyFinderChatEvents.ShouldThrottleRefresh`, keyed off `Settings.LastRefreshAttemptUtc`) prevents the same
warning (or a near-duplicate one) from queuing more than one automatic refresh in a short window. A manual
"Refresh Active Listing"/"Recruit Members"/"Edit / Apply Changes" button in the operator panel
(`PartyFinderOperatorPanel.DrawActionArea`) triggers the identical underlying operation on demand.

Both paths converge on `PartyFinderService.CreateOrUpdate`/`Refresh`, which mark `LastRefreshAttemptUtc` (persisted
immediately via `VenueProfileService.SaveModuleConfig`) and delegate to `IPartyFinderAutomation.QueueCreateOrUpdate`/
`QueueRefresh`, implemented by the unsafe engine `PartyFinderAutomationService` (FFXIVClientStructs/ECommons; lives
in `VenueOS.Plugin` per `NEW_MODULE_GUIDE.md` §30's unsafe-boundary convention).

The engine's `QueueOperation` is the single entry point for Create/Edit/Refresh: it unconditionally aborts whatever
ECommons `TaskManager` chain is currently running, resets five per-attempt runtime fields
(`mainAddonOpenAttempts`, `usedSlashCommandFallback`, three timing fields, plus every `BoundedWait` tracker), and
enqueues a fixed sequence of per-tick steps: `PrepareOperation` → `EnsureMainAddonReady` (opens/finds the native
`LookingForGroup` addon, retrying with a `/partyfinder` slash-command fallback after two failed `ShowAddon`/
`FocusAddon` attempts, bounded to 8s) → `ApplyPresetToAgent` (writes every recruitment field directly into
`AgentLookingForGroup`'s native struct) → `ClickMainAction` (clicks Create/Edit, bounded readiness retry) → a
300ms pacing delay → re-apply the preset (donor-parity: the editor screen re-reads from the agent) → a 200ms pacing
delay → `SyncEditorCheckboxes` (world-limit/private-party checkboxes not covered by the agent struct, bounded
2s soft retry) → a 150ms pacing delay → `SubmitEditor` (clicks Apply/Register, bounded readiness retry) → a 300ms
pacing delay → `ConfirmYesNoIfNeeded` (native confirmation popup) → `VerifySubmission` (polls the authoritative
native listing state, bounded 5s) → `ClosePartyFinderWindowIfVisible` → a terminal step that sets
`state = Idle`. `TaskManager` ticks once per `Framework.Update` (confirmed against the ECommons source — the same
framework thread every other VenueOS game-state access uses), and `Abort()` stops immediately mid-step with no
partial-completion carry-over, so only one chain object exists at a time by construction.

`EndPartyFinder` (VenueOS-only, no donor equivalent) is a distinct shutdown workflow: `PartyFinderService` disables
and persists Auto Refresh *first*, then the engine aborts any in-flight chain and runs its own sequence sharing
`EnsureMainAddonReady` and the extracted `NavigateToOwnListingDetail` navigation helper with normal Refresh/Edit,
diverging only at the final button search (`ClickEndButton`, its own scoped "End" text search) and the completion
check (`VerifyEnded`, polling `AgentLookingForGroup.OwnListingId == 0`).

Module lifecycle: `PartyFinderModule.OnVenueChangedAsync` calls `PartyFinderService.Load(venueId)`, which loads the
venue's persisted `PartyFinderSettings` and calls `automation.ResetForVenue()` (aborts any chain, recreates the
`TaskManager`, clears every runtime field — full parity with the donor's own "recreate the automation object on
venue switch" behavior). `PartyFinderModule.DisposeAsync` calls `service.Abort()` only (never withdraws a listing,
by design — donor parity + explicit End Party Finder separation). `Plugin.Dispose()` calls
`partyFinderAutomation.Dispose()` (disposes the `TaskManager`).

## 4. Fixed-wait inventory

| # | File / method | Duration | What it waits for | Replaced with observable state? | Disposition |
|---|---|---|---|---|---|
| 1 | `QueueOperation`, pacing delay after clicking Create/Edit | 300ms | Editor screen to begin accepting the re-applied preset | No — kept as pacing | **Kept, intentional.** Donor-parity pacing between "click Create/Edit" and "re-apply preset fields," not a substitute for a readiness check — the *next* real readiness gate is `SyncEditorCheckboxes`/`SubmitEditor`'s own bounded waits, which do positively verify state. |
| 2 | `QueueOperation`, pacing delay before checkbox sync | 200ms | Same as above | No — kept as pacing | **Kept, intentional**, same reasoning. |
| 3 | `QueueOperation`, pacing delay before submit | 150ms | Same as above | No — kept as pacing | **Kept, intentional**, same reasoning. |
| 4 | `QueueOperation`, pacing delay before confirm-popup check | 300ms | Give a submitted form a moment before polling for a confirmation popup | No — kept as pacing | **Kept, intentional** — `ConfirmYesNoIfNeeded`/`VerifySubmission` immediately follow with real bounded polling. |
| 5 | `EndPartyFinder`, pacing delay before confirm-popup check | 300ms | Same as #4, End's own sequence | No — kept as pacing | **Kept, intentional**, same reasoning. |
| 6 | `MainAddonRetryDelay` (350ms) inside `EnsureMainAddonReady` | 350ms | Throttle between `ShowAddon`/`FocusAddon` retry attempts | N/A — this *is* the retry interval, not a substitute for a state check | **Kept, intentional** — the retry cadence for a bounded poll (`MainAddonOpenTimeout`, 8s), not a "wait and hope." |
| 7 | `MainAddonOpenTimeout` (8s), `UiElementReadinessTimeout` (5s), `EndButtonDiscoveryTimeout`/`CheckboxSyncSoftTimeout` (2s), `SubmissionVerificationTimeout` (5s) | various | Native addon/button/checkbox readiness, submission/withdrawal completion | **Yes — already state-based.** Every one of these bounds a loop that polls real observable state (`AtkUnitBase.IsReady`/`IsVisible`, `AgentLookingForGroup.OwnListingId`, button/checkbox text+enabled state) via `BoundedWait.Poll`, not a fixed sleep. | **Kept — already correct.** This is the pre-existing hardening this pass builds on, not a defect. |

**No fixed "wait N ms then assume ready" pattern was found remaining in the Party Finder path.** Every wait that
isn't pure pacing between two already-verified steps was already a bounded, state-polling wait before this pass
began. The gap this pass closes is not "a wait was measuring the wrong thing" — it's "the chain could be
restarted/duplicated by a second trigger before any wait even started" (§5 below) and "the chain didn't check
whether the wait was even meaningful to run at all" (§6 below).

## 5. Single-refresh ownership (root cause 1)

**Before:** `PartyFinderService.CreateOrUpdate`/`Refresh` called straight through to the engine with no ownership
check; `HandleChatText`'s automatic-refresh branch did the same. The engine's own `QueueOperation` always aborted
whatever chain was running and started fresh — correct for *mutual exclusion* (never two chains ticking at once)
but wrong for *ownership*: the newest request always won, silently discarding an attempt that may have been close
to completing.

**After:** `PartyFinderService.CreateOrUpdate`, `Refresh`, and the automatic-refresh branch of `HandleChatText` all
now check `automation.IsBusy` (in addition to the pre-existing `automation.IsEnding` check) and no-op if a request
is already in flight — from *either* trigger source, uniformly. This is safe because a redundant request (spam-click
or an auto-refresh landing mid-manual-edit) always carries the *same* current preset as the attempt already
running; skipping it loses nothing, since the in-flight attempt already submits that same data. `Abort()` remains
available at all times as the explicit, immediate way to cancel and start over — this guard only blocks *implicit*
duplicate requests, never a deliberate cancel. `EndPartyFinder` is untouched by this guard (it calls the engine's
`Abort()`/`EndPartyFinder()` directly, which must and does still win over an in-flight refresh per
`PARTY_FINDER_RECONSTRUCTION.md`'s existing "End must win" requirement).

The operator panel's Create/Refresh buttons are now also disabled while `service.IsBusy` (previously only disabled
while `IsEnding`), so the operator gets clear visual feedback that a request is already running instead of being
able to spam-click it. Abort stays enabled unconditionally.

This is the change most likely to fix the reported production symptom: it directly removes the "operator clicks
Refresh, doesn't see it complete instantly, clicks again, chain never gets a chance to finish" failure mode, and
the "automatic 5-minute-warning refresh silently cancels an in-flight manual edit" race.

## 6. State-based context validation (root cause 2)

**Before:** `PrepareOperation` checked `IClientState.IsLoggedIn` but returned `true` (task-chain "proceed") even
when false, so the chain continued into `EnsureMainAddonReady`'s 8-second addon-discovery loop regardless — which
could only ever time out, reporting a misleading "Failed to detect a visible Party Finder window" `Diagnostics`
error for what was actually a benign "not logged in" condition. No step re-checked context validity mid-wait.

**After:** a new `TryGetContextInvalidReason` helper (checks `IClientState.IsLoggedIn` plus
`ConditionFlag.LoggingOut`/`BetweenAreas`/`BetweenAreas51` — the same condition-flag signals
`ShoutRunnerAutomationService` already uses for this exact purpose, not an invented signal) is checked once at the
start of every operation (`PrepareOperation`) and again on every tick of `EnsureMainAddonReady` (shared by both
normal Create/Edit/Refresh's "open_pf" step and End Party Finder's "end_open_pf" step, so one change covers both
flows). When the context is invalid, a new `StopGracefully` helper aborts the chain and returns to `Idle` — same
control flow as the existing `FailAndAbort`, but deliberately **never** calls
`DiagnosticsService.RecordFailure`, per `NEW_MODULE_GUIDE.md` §24a: an expected/benign precondition-not-met outcome
(not logged in, mid-zone-transition, logging out, or — the pre-existing "refresh requested but no listing exists"
check — now also routed through `StopGracefully` instead of silently falling through) must not be reported as if it
were a real automation defect. The next natural trigger (a fresh 5-minute warning, or an explicit operator click)
retries cleanly; nothing here creates a retry storm or requires new state to persist across attempts.

`ICondition` is now a constructor dependency of `PartyFinderAutomationService` (added alongside the existing
`IClientState`/`IPluginLog`/`DiagnosticsService`, wired from the same `Condition` field `Plugin.cs` already injects
via `[PluginService]` for other modules). No new Dalamud service was added to the plugin.

This does not fully solve "Party Finder automation while zoning" in the sense of resuming a suspended attempt after
the zone finishes — it was not resuming before this pass either, and doing so would be a larger, undocumented
behavior change. What it fixes is: an attempt that starts or continues during an invalid context now fails fast and
quietly instead of burning a multi-second timeout and reporting a confusing diagnostic error for an entirely benign
cause. The very next 5-minute warning (throttled to at most once per 4 minutes) or manual click retries normally
once the context is valid again.

## 7. Remaining intentional delays

Every remaining `EnqueueDelay`/timeout in the Party Finder path is accounted for in the inventory (§4) above as
either pure pacing between two steps that are each independently, positively verified, or the retry cadence /
timeout bound of an already state-based poll. None is a "wait and hope" substitute for a readiness check.

## 8. Framework-thread handling

Unchanged, and already correct: `TaskManager` ticks once per `Framework.Update` (confirmed against the ECommons
package source), so every addon/struct access in `PartyFinderAutomationService` already runs on the framework
thread. The chat-message trigger (`OnPartyFinderChatMessage` → `PartyFinderService.HandleChatText` →
`automation.QueueRefresh`) is dispatched by Dalamud's `IChatGui.ChatMessage` event, which — like every other chat
subscription in this codebase — fires on the same thread. No `Task.Run`, `.Wait()`, `.Result`, or blocking call was
introduced or found in the Party Finder path.

## 9. Single-refresh ownership behavior

See §5. Ownership is expressed as the existing `IPartyFinderAutomation.IsBusy`/`IsEnding` flags, now actually
enforced at the call sites (`PartyFinderService`) instead of only inside the engine's already-correct
abort-then-replace mutual exclusion. No new `AttemptId`/token type was introduced — the existing boolean flags,
now checked *before* queuing rather than only inside the engine, are sufficient and match
`NEW_MODULE_GUIDE.md` §22's "don't build a competing state machine" guidance.

## 10. Timer scheduling semantics

Unchanged and out of scope for a redesign: there is no VenueOS-internal recurring timer to hard. Scheduling is
driven entirely by the native 5-minute-warning chat event plus the existing 4-minute
`PartyFinderChatEvents.ShouldThrottleRefresh` throttle. §5's `IsBusy` guard is the mechanism that now prevents that
external trigger from overlapping with an in-flight attempt (manual or automatic).

## 11. Overlap prevention

Covered by §5 (orchestration-layer `IsBusy` guard) plus the pre-existing engine-layer mutual exclusion (one
`TaskManager`, `Abort()`-before-`Enqueue` inside `QueueOperation`, so two chains could never tick concurrently even
before this pass). Together: no second attempt can be queued while one is active, and no chain can ever run two
chains concurrently.

## 12. Stale callback protections

The engine's task chain is entirely synchronous per-tick (no `async`/`await`, no captured continuations that could
land after a newer attempt started) — `Abort()` clears the queue and current task immediately, and any new
`Enqueue` calls in `QueueOperation`/`EndPartyFinder` happen synchronously afterward in the same method, so there is
no window for an old attempt's "continuation" to fire after a new one begins. This was already correct and is
unchanged. §5's `IsBusy` guard adds a second layer: most of the scenarios that used to reach "abort and replace" now
never reach the engine at all, since a redundant request is rejected before it can call `Abort()`.

The one genuine stale-callback-shaped defect found was **not** inside the task-chain engine — it was the leaked
`ChatGui.ChatMessage` subscription (§14).

## 13. Zoning/world/venue invalidation behavior

- **Zoning/world travel/login/logout:** §6 — `TryGetContextInvalidReason` now stops an attempt cleanly (not as a
  diagnostics error) when `IClientState.IsLoggedIn` is false or `ConditionFlag.LoggingOut`/`BetweenAreas`/
  `BetweenAreas51` is set, checked at the start of every operation and on every tick of the longest wait.
- **Venue switch:** unchanged, already correct — `PartyFinderModule.OnVenueChangedAsync` → `PartyFinderService.Load`
  → `automation.ResetForVenue()` aborts any in-flight chain and resets every runtime field before loading the new
  venue's settings. Covered by the existing `Switching_venues_resets_the_automation_engines_runtime_state` and
  `Two_venues_never_see_each_others_settings` tests (unchanged by this pass).

## 14. Stop/disable/dispose behavior

- **Abort:** unchanged, already immediate — cancels the in-flight chain only, does not touch Auto Refresh or
  withdraw a listing. Still available at all times, including while a §5-guarded request would otherwise be
  rejected.
- **Module disable:** `PartyFinderModule.DisposeAsync` calls `service.Abort()` — unchanged, correct (donor-parity:
  disabling the module must never be treated as withdrawing a listing).
- **Plugin dispose/reload:** **fixed** — see §15. `Plugin.Dispose()` now unsubscribes the Party Finder chat handler
  before disposing `modules`/`partyFinderAutomation`, so no queued chat callback can reach a disposed
  `PartyFinderService`/`PartyFinderAutomationService` after unload.

## 15. Root cause 3: leaked chat-message subscription

**Before:** `Plugin.cs`'s constructor wired `ChatGui.ChatMessage += message =>
partyFinderService.HandleChatText(message.Message.TextValue);` — an inline lambda, never stored, never
unsubscribed anywhere in `Plugin.Dispose()`. Every sibling chat subscription in the same constructor *is*
unsubscribed on dispose (`giveawaysRollChatAdapter.Unsubscribe(ChatGui)`;
`ShoutRunnerAutomationService`/`BingoPayoutAutomationService` each unhook their own `IChatGui` event inside their
own `Dispose()`), so this was a clear, isolated omission rather than an intentional design choice. On
`/xlreload`/`/xlrestart`/a Dalamud disable-then-re-enable cycle, the old handler stayed registered on Dalamud's
shared `IChatGui` for the life of the game session, permanently closed over the disposed `PartyFinderService`
(and, through it, the disposed `PartyFinderAutomationService`/`TaskManager`) from the previous plugin instance.

**After:** the subscription is now a named instance method, `Plugin.OnPartyFinderChatMessage`, subscribed via
`ChatGui.ChatMessage += OnPartyFinderChatMessage;` and unsubscribed via `ChatGui.ChatMessage -=
OnPartyFinderChatMessage;` as the very first line of `Plugin.Dispose()` (before `modules.DisposeAsync()` and
`partyFinderAutomation.Dispose()`, so nothing can reach either disposed object even during the rest of teardown).
`PartyFinderService` was promoted from a constructor-local variable to a field (`partyFinderServiceRef`) so the
named handler method can reach it — the same pattern already used for `shoutRunnerServiceRef`.

This cannot be exercised by a unit test (`VenueOS.Plugin` has no test project, per `NEW_MODULE_GUIDE.md` §30) — it
is a required live-QA item (Test G, §16 below).

## 16. Status-state behavior

Unchanged and already correct: `PartyFinderService.Status` delegates directly to `automation.Status`, which the
engine's `SetStatus`/`FailAndAbort`/`StopGracefully` (new) all keep as the single authoritative source — every
terminal path (success, `FailAndAbort`, the new `StopGracefully`, `Abort()`) sets `state = Idle` in the same call
that updates `Status`, so "Refreshing…"-shaped status can never outlive the actual attempt. No scattered/duplicate
status fields were introduced.

## 17. Diagnostics/error handling

`FailAndAbort` (real automation defects — unchanged) continues to route through `DiagnosticsService.RecordFailure`.
The new `StopGracefully` (expected/benign stops: invalid context, no listing to refresh) deliberately does **not**
call `RecordFailure`, per `NEW_MODULE_GUIDE.md` §24a — an expected condition is not a scary operator-facing error.
No new exception paths were introduced; no stack traces are surfaced directly to the operator UI anywhere in this
module (unchanged).

## 18. Tests added/updated

Six new `[Fact]` tests in `tests/VenueOS.Services.Tests/PartyFinderServiceTests.cs`, using the existing
`FakeAutomation`/`InMemoryVenueStore` pattern already established in that file (no test infrastructure changes):

1. `A_second_create_or_update_is_ignored_while_the_first_is_still_in_flight`
2. `A_second_refresh_is_ignored_while_the_first_is_still_in_flight`
3. `Automatic_five_minute_warning_is_ignored_while_a_manual_operation_is_in_flight`
4. `A_manual_refresh_click_is_ignored_while_an_automatic_refresh_is_in_flight`
5. `Once_the_in_flight_attempt_completes_a_new_request_is_accepted_normally`
6. `Cancelling_the_in_flight_attempt_clears_ownership_so_a_new_one_can_start`

Together these prove, at the orchestration layer: single-active-refresh ownership from both trigger sources,
symmetric blocking in both directions (manual blocks automatic, automatic blocks manual), and that both a natural
completion and an explicit `Abort()` correctly clear ownership for the next request. All pre-existing Party Finder
tests (persistence/round-trip, per-venue isolation, End Party Finder's full behavior matrix, auto-refresh
throttling) pass unchanged — none of their assumptions were invalidated by this pass.

The context-validation hardening (§6) and the dispose-leak fix (§15) both live in `VenueOS.Plugin`
(`PartyFinderAutomationService`, `Plugin.cs`), which has no test project per `NEW_MODULE_GUIDE.md` §30 — they are
code-reviewable but can only be proven live, via the QA checklist below.

## 19. Exact test count

**1092 tests total, 1092 passed, 0 failed, 0 skipped** (`dotnet test VenueOS.sln -c Debug`):
`VenueOS.Core.Tests` 4, `VenueOS.Venues.Tests` 23, `VenueOS.Services.Tests` 1065 (up from a baseline of
approximately 1086 total across the suite — +6 for this pass's new tests).

## 20. Debug build result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s), 0 Error(s).**

## 21. Release build result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s), 0 Error(s).**

## 22. Live QA status

This pass is accepted for release on the strength of code review plus short/live functional exercise; the
exhaustive per-scenario test matrix below (TEST A–H) remains the recommended validation plan and has not been
exhaustively executed test-by-test. **TEST H (extended real-session soak) in particular has not been run** and is
the test that would actually confirm the originally reported intermittent symptom is gone — until it has been run
over a real, multi-hour venue session, release wording must describe this pass as hardening, not as a conclusive
fix. These items remain in addition to (not replacing) the existing acceptance items already listed in
`PARTY_FINDER_RECONSTRUCTION.md`:

**TEST A — Normal refresh.** Start Party Finder operation normally (create a listing, enable Auto Refresh). Observe
multiple native 5-minute-warning refresh cycles over an extended session. Confirm each completes, status returns to
idle/waiting between cycles, and no duplicate clicks/actions occur.

**TEST B — Button-mash regression (the primary fix in this pass).** While a Create/Edit/Refresh is visibly in
progress, repeatedly click "Recruit Members"/"Edit / Apply Changes"/"Refresh Active Listing." Confirm the buttons
are disabled (not merely ignored) while busy, no second chain starts, and the original attempt reaches completion
normally. Then click Abort mid-attempt and confirm it still cancels immediately and the buttons re-enable.

**TEST C — Manual/automatic collision.** Manually trigger Create/Edit/Refresh, and — timed to land while it's still
in flight — either wait for a natural 5-minute warning or (if reproducible) simulate one. Confirm the automatic
trigger is silently skipped (no aborted/restarted chain, no duplicate listing action) and the manual attempt
completes normally. Then reverse it: let an automatic refresh start, and click a manual action button while it's
running — confirm the button is disabled and the automatic attempt is not disturbed.

**TEST D — Zone during refresh.** Trigger a refresh, then zone/teleport before it completes. Confirm the attempt
stops without a scary Diagnostics error (check Settings → Diagnostics — it should show nothing, or at most an
informational log line, not a recorded failure), and that the native game UI is left in a safe state. Confirm a
normal refresh works again once loading finishes (via the next 5-minute warning or a manual click).

**TEST E — Login-state edge.** If reproducible, trigger a refresh at a moment the character is not fully logged in
(e.g., very early after zoning into a fresh session). Confirm it stops quietly rather than reporting a "Failed to
detect a visible Party Finder window" error.

**TEST F — Stop.** Stop/disable Party Finder while a refresh is queued/waiting. Confirm no further automatic
refresh fires. Re-enable and confirm normal operation resumes.

**TEST G — Reload (the dispose-leak fix).** With Party Finder's Auto Refresh enabled and a listing active, perform
`/xlreload` or a Dalamud disable-then-re-enable of VenueOS. Confirm: no disposed-object errors in `/xllog`; the
native 5-minute warning (or a manual click) after reload triggers exactly one refresh attempt, not two (which would
indicate the old handler is still registered); Party Finder's preset/Auto Refresh setting persisted correctly
across the reload.

**TEST H — Extended run.** Leave Party Finder running with Auto Refresh enabled for a long real session (multiple
hours if practical) covering ordinary venue operation — occasional manual clicks, occasional zoning, at least one
plugin reload. Confirm the originally reported intermittent failure does not reproduce. This is the test that
actually validates the production symptom is fixed, since it was intermittent by nature.

All of `PARTY_FINDER_RECONSTRUCTION.md`'s existing End Party Finder / durable-preset / VenueOS-integration QA items
remain required and are unaffected by this pass (End Party Finder's own navigation/button-search code was not
touched, beyond now sharing the hardened `EnsureMainAddonReady` context check).

## 23. Files changed

- `src/VenueOS.Modules.Operations/PartyFinder/PartyFinderService.cs` — `IsBusy` ownership guard on
  `CreateOrUpdate`/`Refresh`/the automatic-refresh branch of `HandleChatText`.
- `src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs` — `ICondition` dependency,
  `TryGetContextInvalidReason`/`StopGracefully`, wired into `PrepareOperation` and `EnsureMainAddonReady`.
- `src/VenueOS.Plugin/PartyFinderOperatorPanel.cs` — Create/Refresh buttons also disabled while `IsBusy`.
- `src/VenueOS.Plugin/Plugin.cs` — `PartyFinderService` promoted to a field; chat subscription converted from an
  unsubscribed lambda to a named, dispose-unsubscribed handler; `Condition` passed into
  `PartyFinderAutomationService`'s constructor.
- `tests/VenueOS.Services.Tests/PartyFinderServiceTests.cs` — six new regression tests (§18).
- `docs/PARTY_FINDER_HARDENING.md` — this document (new).

## 24. Git status

At the time this pass was authored, nothing had been staged, committed, pushed, tagged, or released — all changes
above were uncommitted working-tree edits. This hardening was subsequently released together with the rest of the
accumulated maintenance batch as part of VenueOS 0.3.7 — see `RELEASE.md`. Extended real-session soak validation
(TEST H, §22) remains recommended after release, independent of the source release itself.

## 25. Confirmations

- **Accepted for release in VenueOS 0.3.7** on code review plus short/live functional exercise; extended soak
  validation (TEST H) remains ongoing/recommended and does not block this release.
- **Mair's Trivia was not touched by this pass.**
- No other module's files were modified by this pass.

---

# Post-0.3.7 investigation: first-refresh `Apply Changes` failure (VenueOS 0.3.8 maintenance, work package 1)

Status: **implemented and unit-tested; awaiting live QA.** Uncommitted; rides the combined 0.3.8 release. The 0.3.7
history above is unchanged and remains accurate: everything it describes is preserved. Full report:
[`PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md`](PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md).

## Production observation

The TEST H soak this document recommended has now been run (five hours, two PCs). It **falsified** "hardening is
enough": the original symptom still occurs, so the first-refresh defect is **open**, not merely un-validated.

## First-refresh-only pattern

Main PC (stronger, Debug build, VenueOS Party Finder window closed after starting recruitment): refresh #1 failed,
refreshes #2–#5 passed. Historical occurrences were also first-refresh. Mini PC (weaker, two accounts, window open):
every refresh passed.

## Exact failure boundary

The chain reached the fully populated Recruitment Criteria (edit) screen showing **Apply Changes** and then failed to get
it activated — one native action from completing. Not: Party Finder open, locating the listing, Edit, opening or populating
the criteria form, zoning, logout, world travel, or machine load.

## Main-PC vs mini-PC; module open vs closed (investigated variable)

The machine difference is not throughput (the weaker machine passed). Module-window state was **proven irrelevant from
code**: `PartyFinderModule.Tick` is empty; automation is driven only by ECommons `TaskManager` (`Framework.Update`) and the
`IChatGui` subscription; the operator panel only reads service state; the service/module assembly references no ImGui/
Dalamud assembly; and a test runs the whole Start → refresh → refresh sequence drawn-every-frame vs never-drawn with
identical traces. Debug vs Release has no Party Finder difference (no conditional compilation).

## Root cause

Native reason **not provable from source**; see the report §9/§26. Proven source defects on the failing path:
(1) pacing to `Apply Changes` was measured from the Edit *click*, not from the editor being observed, and readiness was only
`IsReady && IsVisible`; (2) a dispatch was treated as acceptance, with no recovery; (3) an unusable control was
indistinguishable from a missing one; (4) — a latent 0.3.7 interaction — ECommons aborts a chain silently on a per-task timeout
or step exception, which would leave the engine's `IsBusy` true forever and, because of 0.3.7's own guard, silently block
every later refresh; (5) an unbounded wait for the Edit button fed (4). Leading (unproven) hypothesis: the first refresh
is the first cold pass through the detail/edit route, so `Apply Changes` is dispatched into an addon that is
ready/visible but not yet settled.

## Repair

State-based only. An observed-editor gate (`wait_editor_ready`) before the editor-dependent pacing; a pure, tested
readiness classifier for the submit control; dispatch once; confirm the authoritative transition (editor closes); on
*observed* non-transition (≥ 2 s, editor still open, control still enabled, no popup pending, update-only) exactly one bounded
re-dispatch; lost-chain reconciliation so ownership can never be stranded; bounded Edit-button wait. No delay, sleep or
timeout was added or lengthened; the donor-parity pacing is unchanged but now anchored to an observed event.

## Diagnostics added

Per-operation id/kind/reason/first-update flag/addon load state at start; a millisecond milestone timeline and outcome at
end; submit-control readiness on change; every dispatch with mechanism, label and native event type/param; confirmation;
recovery warning; attributable Diagnostics suffix (op, step, elapsed); lost-chain report. Scan logging throttled to changes;
the party password is no longer written to the log.

## Tests

33 new (`PartyFinderApplyFlowTests` 17, `PartyFinderOperationOwnershipTests` 16): readiness classification, confirmation,
bounded recovery, never-sent vs sent-ignored, ownership held/cleared on every outcome, first vs later refresh,
presentation independence. Total 1206/1206. Native behavior is live-QA-only and was not faked.

## Remaining live-QA requirements

TESTS A–I in report §25 — first-refresh manual and automatic with the module closed and open, second+ refreshes,
observability (the `op#` lines), regression sweep, and a long soak whose harvested `op#` lines (especially any
`re-dispatching` warning and the `addons:` cold/warm start line) will confirm or refute the leading hypothesis. Until
that soak passes, describe this as a repair with instrumentation, not a confirmed fix.

---

# Post-0.3.9 hotfix: End Party Finder must preserve the 5-minute Auto Refresh preference

**Status: CODE PASS / AUTOMATED PASS / LIVE QA PASSED.** The operator retested this fix in FFXIV and accepted it ("Tested, works."). It ships in VenueOS 0.3.10.

## Live finding

* During earlier live testing, the old **standalone Party Finder plugin** (`venuepartyfinder`) was still enabled and interfered with VenueOS's own Party Finder automation.
* With the standalone plugin disabled, VenueOS Party Finder worked on its own: it started recruitment and refreshed on the native 5-minute warning.
* The one remaining defect: pressing **End Party Finder** also turned off the "Auto Refresh on native 5 minute warning" setting, so the operator had to turn it back on before every venue night.

## Root cause

The persisted preference is `PartyFinderSettings.AutoRefreshEnabled`. It is per venue, stored through `VenueProfileService.SaveModuleConfig` under module `promotion.partyfinder`, schema 1. Only two code paths ever wrote it:

1. `PartyFinderService.SetAutoRefreshEnabled`, called from the Settings toggle (`PartyFinderOperatorPanel.DrawSettings`). This is correct: it is the operator's explicit change.
2. `PartyFinderService.EndPartyFinder`, which called `SetAutoRefreshEnabled(false)` and saved the change before it handed off to `automation.EndPartyFinder`. **This was the defect.** It was a deliberate reconstruction-era choice ("disable Auto Refresh first so a mid-shutdown warning can't queue a refresh"), but it treated a persistent operator preference as if it were run state. The engine's `IsEnding` guard in `HandleChatText` already gives that mid-shutdown protection on its own.

Other paths, all audited and none of them touch the preference: natural expiry (`HandleChatText` → `NotifyListingEnded`), Abort (`automation.Abort`), venue switch and reload (`Load` → `GetModuleConfig` + `automation.ResetForVenue`), and module disable/unload (`PartyFinderModule.DisposeAsync` → `Abort`). The settings record holds no run state. The only non-preference value it holds is `LastRefreshAttemptUtc`, the per-venue throttle timestamp, which is unchanged here.

## Fix

* `PartyFinderService.EndPartyFinder` now only calls `automation.EndPartyFinder(reason)`. That still aborts the in-flight chain, releases operation ownership, withdraws the listing, verifies it, and closes the window. The preference is never written.
* **No-active-recruitment guard.** `HandleChatText` now also returns early when `!automation.HasOwnListing`. Before this fix, the engine's `QueueRefresh` already refused with "No active listing to refresh.", but the service had already saved a throttle timestamp first. Now "preference enabled + no listing" does nothing and persists nothing. This adds no polling: auto refresh is still triggered only by chat events.
* Plugin layer: End's status text changed from "Party Finder ended — Auto Refresh disabled" to "Party Finder ended.". The End button tooltip now says the setting is kept. The End button is now disabled when there is no listing (`IsEnding || !HasOwnListing`). The old condition also enabled it while Auto Refresh was on, which only made sense when End was the way to turn that off. End with no listing is still a safe no-op in the engine.
* Docs describing the old behavior were updated: `docs/USER_MANUAL.md` and `PARTY_FINDER_RECONSTRUCTION.md` (now marked superseded).

## Tests (`PartyFinderServiceTests`)

New:
* `End_party_finder_preserves_an_enabled_auto_refresh_preference` checks the value in memory, at the moment the engine is invoked, and in a reconstructed service.
* `End_party_finder_preserves_a_disabled_auto_refresh_preference`
* `End_party_finder_does_not_trigger_a_refresh_or_create`
* `Enabled_preference_with_no_active_listing_never_auto_refreshes`
* `Next_recruitment_after_end_auto_refreshes_without_reconfiguration`
* `Enabled_preference_survives_end_and_a_full_serialize_deserialize_reload`
* `Manual_disable_after_end_still_persists_and_blocks_the_next_recruitments_refresh`

Inverted from the old semantics, because they asserted the defect:
* `Repeated_end_calls_never_change_auto_refresh_or_disturb_the_preset`
* `End_party_finder_leaves_auto_refresh_unchanged_even_if_withdrawal_fails`
* `Ending_with_no_active_listing_calls_the_engine_and_preserves_auto_refresh`
* `Ending_in_either_venue_never_cross_contaminates_the_other_venues_preference` (Venue A on / Venue B off)

Tightened:
* `Stale_auto_refresh_cannot_run_once_ending_has_begun` now keeps the listing active, so only `IsEnding` can block the refresh.
* `Auto_refresh_is_ignored_while_disabled` now has an active listing, so only the preference can block the refresh.

The first-refresh, ownership/collision and Abort suites are unchanged and still pass.

**Mutation checks.** Restoring `SetAutoRefreshEnabled(false)` in End makes 8 tests fail. Removing the `HasOwnListing` guard makes 3 tests fail. Both mutations were reverted and the suite is green again.

**Validation:** 1399 / 1399 (Core 69, Venues 23, Services 1307), 0 failed, 0 skipped. Debug and Release builds: 0 warnings / 0 errors.

## Live QA — PASSED (operator retest)

**Result:** the operator tested this fix live in FFXIV, reported "Tested, works.", and accepted it. The scenario was: standalone Party Finder plugin disabled, VenueOS Party Finder tested on its own, Auto Refresh enabled, recruitment started, then ended. Auto Refresh stayed enabled as configured. The operator reported nothing beyond that; the checklist below is the scenario list that was provided for the retest.

Run with the standalone Party Finder plugin **disabled**.

* **A. Enabled state is kept.** Enable Auto Refresh (Settings → Modules → Party Finder). Start Party Finder. Confirm the setting is on. End Party Finder. PASS: recruitment ends, the status reads "Party Finder ended.", and the toggle is still on.
* **B. No refresh while idle.** After End, keep playing normally for a while. PASS: no Party Finder window opens and no refresh happens while there is no listing.
* **C. Next recruitment.** Start Party Finder again without touching Settings. PASS: Auto Refresh is already on, and the next native 5-minute warning refreshes the listing.
* **D. Disabled state is kept.** Turn Auto Refresh off. Start, then End. PASS: the toggle stays off and no auto refresh happens during that recruitment.
* **E. Reload.** With Auto Refresh on and no listing, run `/xlreload`. PASS: the toggle is still on, no recruitment starts, and no refresh fires.
* **F. Venue switch.** Venue A on, Venue B off. End in each venue and switch between them. PASS: each venue keeps its own value.
