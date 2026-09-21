# Party Finder First-Refresh Failure — Forensic Investigation & Repair (post-0.3.7, work package 1)

Status: **implemented, unit-tested, built (Debug + Release, 0 warnings / 0 errors). NOT live-verified. NOT committed,
NOT version-bumped, NOT packaged.** Intended to ride the combined 0.3.8 maintenance release.

Read this first: the **native reason** FFXIV ignored (or could not receive) the first `Apply Changes` is **not
provable from source** and this report does not claim it is. What *is* proven from source is a set of concrete
defects on exactly the failing path that make a first refresh uniquely exposed and make any failure there
indistinguishable from any other. The repair removes those defects, adds an authoritative confirm-and-recover step, and adds
diagnostics so the next live failure identifies the native cause directly. See §26 for the residual uncertainty.

---

## 1. Executive summary

* The 0.3.7 hardening (single-active-refresh ownership, context validation, chat-subscription cleanup) is correct and
  is preserved untouched in behavior. The first-refresh defect is a separate, still-open defect on the **final-submit
  path** of the refresh chain.
* The chain reached the populated Recruitment Criteria editor and then did not get `Apply Changes` accepted. Source
  audit of that final stretch found:
  1. **Pacing measured from the wrong event.** All pacing between "Edit was clicked" and "Apply Changes is dispatched"
     (300 ms → re-apply preset → 200 ms → checkbox sync → 150 ms → submit) was measured from the *Edit click*, on the
     unverified assumption that the editor exists a few hundred ms later. Nothing observed the editor actually
     appearing before the preset re-apply ran, and `Apply` could be dispatched as little as ~150 ms after the editor
     first appeared. Readiness was only `AtkUnitBase.IsReady && IsVisible`.
  2. **Dispatch was treated as acceptance.** `ActivateButton` returns `true` if the button has an event registered —
     it says nothing about the game acting on it. There was no recovery from an ignored dispatch.
  3. **A present-but-unusable button was indistinguishable from a missing one** (disabled / hidden / no click event),
     and the operator could not tell "never sent" from "sent and ignored" without reading two differently-worded
     diagnostics strings.
  4. **A latent 0.3.7 interaction:** ECommons `TaskManager` aborts its chain *silently* on a per-task timeout or a
     step exception, without running the engine's own `FailAndAbort`/`StopGracefully`. Since 0.3.7 the engine's
     `IsBusy` flag gates every request, so a silently dead chain would leave `IsBusy == true` forever and every later
     manual/automatic refresh would be silently ignored until the operator pressed Abort.
  5. An unbounded wait for the Edit button (bounded only by ECommons' silent 15 s limit) — feeds defect 4.
* Repair (all state-based): observe the editor before anything that depends on it; classify the submit control's exact
  native state; dispatch once; confirm the authoritative transition (editor closes); on *observed* non-transition
  with the control still enabled, no popup pending, and the operation an idempotent update, re-dispatch **once**;
  release ownership even if the chain dies silently; log/diagnose every transition with an operation id, step and
  millisecond timeline.
* Module-window state was proven, from code, to have **no** influence on automation (§13). Debug vs Release has no
  Party Finder difference (§14).
* 33 new tests; total **1206 / 1206 passing**. Debug and Release builds: **0 warnings, 0 errors**.

## 2. Exact production failure signature (as reported)

Start recruitment succeeds. The **first** refresh after Start proceeds through: open native Party Finder → locate the
active recruitment → Edit → Recruitment Criteria opened → form populated → final screen showing **Apply Changes**.
The automation then **fails to get Apply Changes activated**. Refreshes 2…N in the same uninterrupted session succeed.
Historical occurrences of this issue were also first-refresh.

## 3. Five-hour soak-test evidence (as supplied)

| | Main PC | Mini PC |
|---|---|---|
| Hardware | much stronger | weaker, heavily loaded, two FFXIV accounts (ShoutRunner on one) |
| Build | Debug (same source as 0.3.7) | likely packaged Release |
| VenueOS PF window | opened to start, then **closed** | **open** throughout |
| Zoning / travel / relog | none | none |
| Refresh #1 | **FAILED at Apply Changes** | passed |
| Refresh #2…#N | passed | passed |

**Evidence limitation, stated plainly:** the soak's own Dalamud log and the text of the Diagnostics entry produced by
the failed refresh were not part of the work package. A search of this machine's `dalamud.log` (session started
2026-09-20 10:56) found **no** `promotion.partyfinder` lines, so no first-hand log evidence was available to this
investigation. The Diagnostics message text of the failed refresh is the single cheapest discriminator between
"never sent" and "sent and ignored" (§8) — if it was recorded, it resolves question 12 immediately.

## 4. First-refresh vs later-refresh comparison

Engine state after **Start Recruitment** vs after a **successful refresh** — every persistent field:

| Field | After Start | After a refresh | Explains first-only? |
|---|---|---|---|
| `IsCompatibilityVerified` | true | true | no |
| `observedActiveListing` | true (set by `VerifySubmission`) | true | no |
| `state` | Idle | Idle | no |
| per-op counters/timers/`BoundedWait`s | reset by `QueueOperation` | reset by `QueueOperation` | no |
| `TaskManager` | same instance | same instance | no |
| Cached native pointer/node/addon | **none cached** — addons are re-resolved by name every tick | none | no |

**No engine field carries state from Start into the first refresh that differs from what a refresh leaves for the
next.** The code path is identical (`QueueRefresh` → `QueueOperation(requireExistingListing: true)`); the engine cannot
tell a first refresh from a later one. Any first-vs-later difference must therefore come from **native game state at
that moment**. The one structural asymmetry visible in source: the Start (create) chain drives
`LookingForGroup` node 46 → `LookingForGroupCondition` (create mode) and never visits `LookingForGroupDetail`;
the **first refresh is the first time this chain opens `LookingForGroupDetail` and the editor in edit mode**, and (after
~55 minutes with the PF window closed) it starts from an unloaded-addon state. Later refreshes start from a state the
previous refresh has just exercised. That is the exposure the repair targets and the diagnostics now measure
(`addons:` line and `node46-clicks`, §18).

## 5. Full refresh state-machine trace (before this work)

`QueueOperation` enqueues, on ECommons `TaskManager` (one task per `Framework.Update`, framework thread):

1. `prepare` — context check (`IsLoggedIn`, `LoggingOut`, `BetweenAreas*`), own-listing check → `StopGracefully` if not met.
2. `open_pf` — `EnsureMainAddonReady`: `agent->ShowAddon()/FocusAddon()` (+ `/partyfinder` fallback after attempt 2), until `LookingForGroup` is `IsReady && IsVisible` (8 s bound).
3. `apply_preset` — writes the preset into `AgentLookingForGroup.StoredRecruitmentInfo`.
4. `open_editor` — `ClickMainAction`: with a listing, `NavigateToOwnListingDetail`: if `LookingForGroupDetail` visible → click **Edit**; else click main-addon node 46 **every tick** until Detail is visible. Returns `true` the moment Edit is *dispatched*.
5. delay 300 ms → `reapply_preset_in_editor` → delay 200 ms → `sync_editor_checkboxes` (soft 2 s wait for the editor, then continues silently) → delay 150 ms.
6. `submit_editor` — `SubmitEditor`: editor found (`IsReady && IsVisible`, first of 4 names) → button id 111 (Condition editor) if enabled+visible → else best enabled text match → `ActivateButton` → returns `true`.
7. delay 300 ms → `confirm_yesno` → `verify_submission` (editor gone ⇒ success; 5 s bound ⇒ `FailAndAbort`) → `close_pf` → `finish` (`state = Idle`).

## 6. Exact Apply Changes activation mechanism (unchanged by this work)

* **Addon identity:** by name via `RaptureAtkUnitManager.GetAddonByName` (indices 1, 0, 2). Editor names, in order: `LookingForGroupCondition`, `LookingForGroupRecruit`, `LookingForGroupSetting`, `LookingForGroupInput`; first with `IsReady && IsVisible` wins. `LookingForGroupCondition` is the Recruitment Criteria editor.
* **Control identity:** `addon->GetComponentButtonById(111)` (Condition only); otherwise a recursive scan of every visible `AtkComponentButton` scored against `["Apply Changes","Update","Save"]` (update) / `["Recruit Members","Register","Create Listing"]` (create), reject list `Cancel/Back/Close/Withdraw/Leave/No`.
* **Mechanism:** synthetic replay of the control's **own first registered native event**:
  `addon->ReceiveEvent(evt->State.EventType, evt->Param, ownerNode->AtkEventManager.Event)`. Not an Atk callback, not an agent call, not a click helper.

## 7. Exact readiness predicates

Before (0.3.7): editor `IsReady && IsVisible`; button 111 non-null, `IsEnabled`, node visible — checked once per tick,
preceded by ~650 ms of pacing from the Edit click. Success of the dispatch = "the button had an event".

After: `PartyFinderApplyTracker.Classify` (pure, tested) reduces a native observation to exactly one of
`EditorMissing / ButtonMissing / ButtonNotVisible / ButtonDisabled / ButtonNotActivatable / Ready`. Dispatch only on
`Ready`. Additionally the whole editor block is now gated on an *observed* visible+ready editor (`wait_editor_ready`).

Distinctions: window visible ✔ · addon loaded ✔ (`GetAddonByName`) · addon ready ✔ (`IsReady`) · button node present ✔ ·
button enabled ✔ · **callback data ready ✘ · underlying agent/state ready ✘** — `AgentLookingForGroup` exposes no
"editor settled" flag in the pinned ClientStructs, so there is no authoritative native signal for those two; that gap
is why confirmation + bounded recovery exist (§16, §17) instead of a stronger predicate.

Can the button be visually present before the underlying state can accept the callback? **Yes, that cannot be ruled
out from source**, and it is the leading candidate (§9).

## 8. Was Apply Changes attempted or skipped in the observed failure?

**Not determinable from the evidence supplied.** The two possibilities are distinguished by the failure text the old
code would have recorded:

* *Never sent* (control not usable for 5 s, e.g. present but disabled): `Timed out waiting for a submit button on LookingForGroupCondition. Visible buttons: …` (or `…editor to appear…`).
* *Sent and ignored:* `Submitted Party Finder refresh, but the editor did not close.` after a `Clicked submit button 'Apply Changes' on LookingForGroupCondition.` status/log line.

If the failed refresh's Diagnostics entry is still available, it answers this. Regardless, the new code names the state
directly in both cases (§18).

## 9. Root cause

Split honestly into **proven** and **inferred**:

**Proven from source (each is a real defect on the failing path):**

* **D1 – unobserved ordering assumption.** The chain assumed the editor exists within ~300 ms of the Edit click.
  Preset re-apply and everything after were timed from the click, not from the editor. If the editor appears later, the
  re-apply runs against nothing and `Apply` is dispatched ≈150 ms after the editor's first ready/visible tick, against
  content whose readiness was never observed (only `IsReady && IsVisible`).
* **D2 – dispatch ≠ acceptance, and no recovery.** An ignored synthetic event was terminal.
* **D3 – unusable ≠ missing, and dispatched ≠ never-sent were not separable** without out-of-band knowledge.
* **D4 – silent chain death strands ownership** under the 0.3.7 `IsBusy` guard (latent; did not occur in the soak because refreshes 2…N ran).
* **D5 – unbounded Edit-button wait** (ends only via ECommons' silent 15 s limit → D4).

**Inferred (leading hypothesis, unproven):** on a cold first open of the edit-mode editor (first time this chain visits
the detail/edit route, PF window closed for ~55 min), the Recruitment Criteria addon reports `IsReady && IsVisible` and its
Apply control reports enabled *before* the addon has finished its own setup; the synthetic event is dispatched into that
window (D1) and the game silently drops it (D2). Later refreshes open a warm addon, so the same 650 ms of pacing is
enough. This fits "one native action away, then subsequent refreshes fine, machine-independent". **It is not the only
fit** (§26) and the source cannot rank the alternatives; the repair is designed to be correct under any of them and the
diagnostics to tell them apart.

## 10. Why the first refresh was uniquely vulnerable

The chain has no first-refresh state of its own (§4). It is vulnerable because it is the first pass through the native
detail/edit route from a cold, closed-window state, and that is exactly where D1 (unobserved editor timing) and D2
(no confirmation/recovery) bite. Every other refresh finds the route warm.

## 11. Why later refreshes succeeded

Same code, warm native state: the editor is up well inside the 650 ms of click-relative pacing, so the accidental
timing assumption in D1 holds and the first dispatch is accepted.

## 12. Main PC vs mini PC relevance

Not throughput: the weaker, more loaded mini PC passed every refresh. Reading the pair as evidence for/against the cold-open
hypothesis is **not** possible — a faster machine loads a cold addon *sooner*, so the machine comparison neither supports
nor contradicts it; the relevant variable is native addon warm/cold state, which the PC comparison did not control and
which the new `addons:` log line now records per operation.

## 13. Module-open vs module-closed investigation

**Proven: no operational Party Finder code depends on the operator panel being constructed, drawn, or open.**

* `PartyFinderModule.Tick` is empty; `Draw()` is `draw?.Invoke()` and nothing else.
* Automation is driven solely by ECommons `TaskManager` (subscribed to `Framework.Update` at construction) and by
  `IChatGui.ChatMessage → PartyFinderService.HandleChatText`, both wired in `Plugin`'s constructor independent of any window.
* `PartyFinderOperatorPanel` only *reads* `PartyFinderService` (`Status`, `HasOwnListing`, `IsBusy`, `Settings`) and calls
  its public methods on clicks; its only private state is a duty-search string. `IsBusy`'s only panel use is graying buttons.
* `VenueOS.Modules.Operations` (service + module) references no ImGui/Dalamud assembly (asserted by a reflection test).
* Test `Closing_the_presentation_does_not_change_automation_or_service_state` runs Start → auto-refresh → refresh with
  the module drawn every frame vs never drawn and asserts identical service/engine traces.

Conclusion: the module-open/closed difference between the two PCs is **not** a cause. (One indirect coupling exists and
is benign: while the panel *is* drawn, `IsBusy` is read every frame, which now also runs the lost-chain reconcile
earlier — that only ever *releases* a dead chain sooner; it cannot start, stop or alter a live one.)

## 14. Debug vs Release investigation

No `#if`, `[Conditional]`, `DEBUG` or `DefineConstants` exists anywhere in the Party Finder engine, service, panel or
their project files; behavior is identical. The only difference is JIT/optimization timing, which is not a root cause
without evidence and which the mini PC (likely Release) vs main PC (Debug) comparison cannot isolate.

## 15. Repair implemented

Engine (`src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs`) + two new pure files in
`src/VenueOS.Modules.Operations/PartyFinder/`:

1. **`wait_editor_ready` gate** after the Edit/Create click: observes the editor addon actually visible+ready (5 s
   bound, context check → quiet stop) *before* the 300 ms/re-apply/200 ms/sync/150 ms sequence, which is unchanged but
   is now measured from an observed event.
2. **Submit readiness classification** (`ObserveSubmitControl` → `PartyFinderApplyTracker.Classify`): control selection
   is exactly what it was (id 111 when enabled+visible, else best enabled text match); an unusable control is now
   classified (disabled / hidden / no event / missing) instead of "not found".
3. **Confirmation** (`VerifySubmission` driven by `PartyFinderApplyTracker.EvaluateConfirmation`): success = editor
   closed. Popups are handled first and never mistaken for a non-transition.
4. **Bounded recovery** (§17).
5. **Lost-chain reconciliation** (`ReconcileLostChain`, run by `IsBusy`, `IsEnding`, and the three queue/end entry
   points): if `state != Idle` but the `TaskManager` is idle, the chain died silently → ownership released, status set,
   Diagnostics entry naming the step, operation recorded as `ChainLost`.
6. **Edit-button wait bounded** (5 s, same bound as the equivalent End-button wait on the same screen) with the visible
   buttons in the failure text.
7. All ownership transitions funnel through `BeginOperation`/`EndOperation` over the tested `PartyFinderOperationTracker`.

## 16. Why the repair is state-based, not timing-based

* No delay, sleep, or timeout was added or lengthened. The existing donor-parity pacing is unchanged; it is now
  *anchored to an observed native event* instead of to a click.
* Every wait polls a named native observation and is bounded (the same 5 s bound the module already used).
* Success is the observed editor-closed transition, not elapsed time.
* The one new duration, `AcceptanceGrace` (2 s), is not a "wait and hope" sleep: it is the window in which
  *absence* of the expected transition can be observed at all (non-transition is only observable as a lack of change
  over time). Recovery additionally requires the positive observations in §17.

## 17. Bounded recovery — behavior and justification

Rule (`PartyFinderApplyTracker.EvaluateConfirmation`): re-dispatch **once** iff **all** hold — the operation is an
*update* (not create); the editor is still visible; the submit control is still enabled and activatable; no native
confirmation popup is pending; ≥ 2 s have passed since the last dispatch; total dispatches < 2. Otherwise wait, then
fail at 5 s after the last dispatch with the exact observed state and dispatch count.

Against the requirements: **(1) idempotent/safe** — every refresh already re-submits the identical preset; a second
identical update cannot corrupt state; create is excluded because a duplicate "Recruit Members" is not provably
harmless. **(2) failure mode understood** — *partially, and stated as such*: "synthetic event dispatched, no observed
transition, control still enabled". The deeper native reason is not proven (§26); recovery is conditioned on the
observation, not on the theory. **(3) based on observed non-transition** — yes. **(4) bounded** — max 2 dispatches,
hard-tested. **(5) diagnostics** — a `Warning` log names it as a recovery, and the operation timeline marks
`apply-redispatch#2`, so recovery frequency is visible in every soak.

## 18. Diagnostics added

All in the Dalamud log via `IPluginLog`; state transitions only (never per frame); no secrets.

* Per operation start: `op#N Update (reason) starting; firstUpdateForListing=…; updatesCompletedForListing=…; addons: LookingForGroup[ready,visible|not loaded], …Detail…, …Condition…` — records cold vs warm addon state and whether this is the first update the listing has had.
* Per operation end: `op#N Update (reason) Completed|Failed|Stopped|Aborted|ChainLost after Xms; firstUpdateForListing=…; lastStep='…'; timeline: begin@0ms > open_pf@… > node46-clicked@… > detail-visible(node46-clicks=N)@… > edit-clicked@… > editor-visible(LookingForGroupCondition)@… > submit-ready@… > apply-dispatch#1@… > editor-closed@… > completed@…`.
* Submit-control readiness on each *change*: `submit control on <addon>: ButtonDisabled (the submit control is present but disabled); label='…' via id:111`.
* Every dispatch: `submit dispatch #N[ (recovery…)] on <addon> via id:111|text: label='…', ReceiveEvent(type=…, param=…)` — the mechanism and event actually sent.
* Confirmation: `submit confirmed: editor closed after N dispatch(es)`; recovery: `… editor still open 2.0s after dispatch 1, control still enabled; re-dispatching (dispatch 2 of at most 2)`.
* Diagnostics (Settings → Diagnostics) failures now carry ` [op #N Update, first update for this listing, step 'submit_editor', Xms in]`; failure text now names the exact state (`…: the submit control is present but disabled…`, `…editor still open 5.0s after 2 dispatch(es); control enabled=True…`).
* Lost chain: attributable Diagnostics entry naming the dead step.
* Log noise reduced: the per-tick `Scanned N button candidates…` line now logs only when the result changes; the pre-existing log line that wrote the raw **party password** now logs only `passwordSet=true/false`.

## 19. Files changed

| File | Change |
|---|---|
| `src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs` | modified — gate, readiness observation, dispatch, confirmation, recovery, ownership funnel, lost-chain reconcile, bounded Edit wait, diagnostics |
| `src/VenueOS.Modules.Operations/PartyFinder/PartyFinderApplyFlow.cs` | **new** — pure readiness/confirm/recover logic |
| `src/VenueOS.Modules.Operations/PartyFinder/PartyFinderOperationTracker.cs` | **new** — pure operation ownership/timeline/first-update bookkeeping |
| `tests/VenueOS.Services.Tests/PartyFinderApplyFlowTests.cs` | **new** — 17 tests |
| `tests/VenueOS.Services.Tests/PartyFinderOperationOwnershipTests.cs` | **new** — 16 tests |
| `docs/PARTY_FINDER_HARDENING.md` | appended post-0.3.7 section (0.3.7 history intact) |
| `docs/PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md` | **new** — this report |
| `docs/POST_0.3.7_TRAINING_AUDIT.md` | **new** — maintenance ledger |

`PartyFinderService`, `IPartyFinderAutomation`, the operator panel, `Plugin.cs`, and the version were **not** modified.

## 20. Tests added

`PartyFinderApplyFlowTests` (17): every not-ready native state classified distinctly; ready ⇒ immediate dispatch;
disabled control never dispatched and named; control becoming ready inside the bound dispatches then; never-ready fails
at the bound naming the state with 0 dispatches; readiness change flagged once (no per-frame logging); label never gates
readiness; nothing confirmed before dispatch; editor-closed ⇒ confirmed; open inside the grace ⇒ wait; ignored dispatch ⇒ exactly one
re-dispatch then confirmed; recovery bounded (no third dispatch; fails naming "2 dispatch(es)"); recovery requires the control
still enabled; popup pending never mistaken for non-transition; create never recovers; never-sent vs sent-ignored are
distinguishable; first and later refreshes make identical decisions.

`PartyFinderOperationOwnershipTests` (16): ownership taken/released; released on **every** non-success outcome
(Failed/Stopped/Aborted/ChainLost, theory ×4); double finish harmless; only one operation ever current; step + millisecond
timeline recorded; first-update-after-create flagged, next not; failed/aborted first update leaves the next still "first";
End/listing-ended/venue reset re-arms "first"; service-level Start → first refresh → second refresh with ownership held
and released; failed first refresh releases ownership; Abort releases ownership without touching Auto Refresh/preset;
**closing the presentation does not change automation/service state**; service/module layer has no UI-assembly reference.

**Pure/state-machine testable (covered):** readiness classification, confirm/recover decisions, ownership, first-update
tracking, presentation independence at the module boundary. **Native-integration (NOT testable, live QA only):**
addon/node observation, the actual `ReceiveEvent`, `TaskManager` behavior, whether FFXIV accepts a dispatch. The
engine-side wiring of the pure logic cannot be exercised by a unit test (no test project for `VenueOS.Plugin`, per
NEW_MODULE_GUIDE.md §30). No native behavior was faked to make a test pass.

## 21. Exact test totals

`dotnet test VenueOS.sln -c Debug`: **1206 total, 1206 passed, 0 failed, 0 skipped**
(`VenueOS.Core.Tests` 69, `VenueOS.Venues.Tests` 23, `VenueOS.Services.Tests` 1114). Baseline 1173 → +33.

## 22. Debug build result

`dotnet build VenueOS.sln -c Debug` — Build succeeded, **0 Warning(s), 0 Error(s)**.

## 23. Release build result

`dotnet build VenueOS.sln -c Release` — Build succeeded, **0 Warning(s), 0 Error(s)**. Debug tests, Debug build and Release build were all re-run after the final source edit.

## 24. Regression-risk assessment

| Change | Risk | Note |
|---|---|---|
| `wait_editor_ready` gate | low | Same predicate/failure text the chain already applied at its end; adds at most the editor's appearance latency (typically < 300 ms) to each Start/refresh. |
| Confirmation via `PartyFinderApplyTracker` | low | Success predicate unchanged (editor no longer visible); timeout now measured from last dispatch (≈ +0.3 s worst case). |
| Bounded recovery | low–medium | Only after ≥ 2 s of observed non-transition with the control enabled; worst case is one extra identical update. Untested against real FFXIV. |
| Edit-button wait 15 s → 5 s | low | Matches End's identical wait on the same screen (live-verified); a genuine miss now reports visible buttons instead of dying silently. |
| `ReconcileLostChain` in getters | low | A false positive would require `state != Idle` with an idle `TaskManager` outside the synchronous enqueue windows (none read the flags); consequence would be pre-0.3.7 behavior (next request restarts). Getter side effect runs on the same thread as all callers. |
| Control selection | none | Unchanged (id 111 → text). Label is diagnostic-only and cannot block. |
| `ListButtons` shows `[disabled]`, scan-log throttle, password no longer logged | none | Diagnostic text only. |

Not regressed by construction (untouched code/behavior): Start/Refresh/Edit/End entry points and their queue order,
`PartyFinderService` guards, End chain steps, Abort semantics, zoning/logout stop (`TryGetContextInvalidReason`),
venue reset, chat-subscription lifecycle. `/xlreload` path (dispose/unsubscribe) is untouched.

## 25. Live-QA checklist (required for acceptance; nothing below has been performed)

Where a test says "check the log", search `dalamud.log` for `promotion.partyfinder` and the `op#` lines in §18.

* **TEST A — Start + first manual refresh.** Start recruitment; close the VenueOS Party Finder window; click Refresh Active Listing. Expect `Completed`, `firstUpdateForListing=True`, timeline containing `editor-visible`, `apply-dispatch#1`, `editor-closed`.
* **TEST B — First automatic refresh.** Fresh recruitment; close the module; let the native 5-minute warning drive the first refresh. Same expectations.
* **TEST C — Second+ refreshes.** Several further cycles: `firstUpdateForListing=False`, all `Completed`.
* **TEST D — Module open.** Start, leave the module open, first refresh.
* **TEST E — Module closed.** Start, close the module, first refresh. (D and E must be indistinguishable.)
* **TEST F — Observability.** Confirm the log shows the readiness line, `submit dispatch #1 … ReceiveEvent(type=…, param=…)`, and `submit confirmed`; note the `addons:` start line (cold = detail/editor `not loaded`).
* **TEST G — Failure path.** Not safely reproducible live. The decision logic for never-sent / sent-ignored / disabled-control / lost-chain messages is covered by unit tests. Optional live probe: press Abort mid-refresh and confirm ownership releases and the next click starts a new `op#`.
* **TEST H — Long soak.** A real venue night, multiple automatic cycles. **Harvest** every `op#` `Completed/Failed` line and any `re-dispatching` warning: *how often recovery fires, and on which refresh number, is the data that confirms or refutes §9's hypothesis.*
* **TEST I — Regression sweep.** Start, manual Refresh, Edit / Apply Changes, End Party Finder, Abort mid-chain, warning-triggered refresh, zoning during a refresh (expect quiet stop, no Diagnostics entry), `/xlreload` then one refresh (exactly one attempt).
* **If a refresh fails again:** capture the Diagnostics entry and the `op#` timeline lines — they will state whether dispatch occurred, the control's exact native state, the label, the event type/param, and the addon warm/cold state.

## 26. Remaining uncertainty

1. **Why the game ignored (or could not receive) the first Apply** is unproven. Alternatives the source cannot rank against §9: the control being genuinely disabled for a native reason unrelated to load timing; the repeated node-46 activation (the detail route re-activates node 46 *every tick* until the detail addon is visible — unchanged behavior, now counted in the log as `node46-clicks=N`); the preset re-apply/checkbox toggles interacting with an addon that re-reads its own state; an asynchronous own-listing data fetch on first Edit.
2. Whether bounded recovery *actually* rescues an ignored dispatch in the real game is unknown until live-run — if the game keeps ignoring the second dispatch too, the failure will now report `2 dispatch(es)` with the exact control state, which is itself decisive evidence.
3. The label read from the button text node has no live evidence of reliability; it is diagnostic-only for that reason.
4. Evidence limitation (§3): no first-hand soak log or original Diagnostics text was available.

## 27. Confirmation that 0.3.7 hardening was preserved

Single-active-refresh ownership (`PartyFinderService` `IsBusy`/`IsEnding` guards — unmodified; the engine's flags now
additionally self-heal), manual/automatic collision protection, busy-state UI gating, context invalidation and graceful
stop (`TryGetContextInvalidReason`/`StopGracefully` — same control flow, plus the same check in the new gate), the
subscription cleanup in `Plugin.Dispose`, framework-thread safety (no `Task.Run`/blocking added; everything still runs as
`TaskManager` steps), End Party Finder's chain, and all 0.3.7 tests pass unchanged.

## 28. Confirmation that unrelated maintenance items were not implemented

Only Party Finder was touched. ShoutRunner, Bingo, Brackets, Attendance, Raffle, Block Letters, and the System Menu
investigation were not started (recorded as OPEN in `POST_0.3.7_TRAINING_AUDIT.md`). No donor/backend repository was
touched. No version bump, packaging, `repo.json` change, commit, push, tag or release.

## 29. Final git status

Nothing staged, committed, pushed, tagged or released. Working tree:

```
 M docs/PARTY_FINDER_HARDENING.md
 M src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs
?? docs/PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md
?? docs/POST_0.3.7_TRAINING_AUDIT.md
?? src/VenueOS.Modules.Operations/PartyFinder/PartyFinderApplyFlow.cs
?? src/VenueOS.Modules.Operations/PartyFinder/PartyFinderOperationTracker.cs
?? tests/VenueOS.Services.Tests/PartyFinderApplyFlowTests.cs
?? tests/VenueOS.Services.Tests/PartyFinderOperationOwnershipTests.cs
```

`git diff --stat` (tracked files): `PARTY_FINDER_HARDENING.md` +72; `PartyFinderAutomationService.cs` +442/−82 (≈524 changed lines).
