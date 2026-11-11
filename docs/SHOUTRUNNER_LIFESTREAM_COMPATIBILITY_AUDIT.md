# ShoutRunner — Lifestream Compatibility & Travel Reliability Audit (VenueOS 0.3.12)

**Status:** IMPLEMENTED and released in VenueOS 0.3.12. Automated validation is complete.
**Live QA: Pending — owner unavailable; release explicitly authorized based on automated validation.**
Nothing in this report was run inside FFXIV. Every "reproduction" below is a deterministic simulation and is not
equivalent to real in-game travel.

| | |
|---|---|
| VenueOS investigated | 0.3.11, `main` @ `27ae8b1` (clean tree, in sync with `origin/main`; tags up to `v0.3.11`) |
| Lifestream inspected | `NightmareXIV/Lifestream` `main` @ `b363201` (2026-10-16), project version `2.5.4.18`, read-only clone (not modified) |
| ECommons inspected | `NightmareXIV/ECommons` `master` (shallow clone, read-only): `Automation/NeoTaskManager/TaskManager.cs` (Lifestream's task queue) |
| Release produced | VenueOS 0.3.12 |

---

## 1. Incident

A ShoutRunner user reported failures "following recent updates" and suspected Lifestream. Their screenshot:

```text
[3:05:56 PM] RUN 1 — New Gridania — SHOUT SENT.
[3:05:56 PM] RUN 1 — Limsa Lominsa Lower Decks — starting.
[3:06:05 PM] RUN 1 — Limsa Lominsa Lower Decks — SHOUT SENT.
[3:06:11 PM] RUN 1 — Adamantoise — completed.
[3:06:11 PM] RUN 1 — Cactuar — starting.
[3:07:03 PM] RUN 1 — RUN 1 stopped — Arrived at Jenova instead of Cactuar.
[3:07:24 PM] RUN 1 — Recovered interrupted RUN 1.
[3:07:24 PM] RUN 1 — Cactuar — resuming.
[3:07:36 PM] RUN 1 — Stop requested.
```

What the code says each line means:

| Line | Produced by | Meaning |
|---|---|---|
| `SHOUT SENT.` | `ShoutRunnerService.ProcessShoutCompletion` | The chat transport accepted the `/shout` (one-line shout). Native teleport (`Telepo.Teleport`) and arrival detection worked for New Gridania and Limsa. |
| `Adamantoise — completed.` | `AdvanceToNextStep` | Every destination on Adamantoise was processed. |
| `Cactuar — starting.` | `AdvanceToNextWorld` | Checkpoint written (`CurrentWorld = Cactuar`, nothing completed), then readiness, classification, `TravelToWorldAsync("Cactuar", crossDataCenter: false)`. Adamantoise and Cactuar are both Aether, so this is a same-DC World Visit. |
| `RUN 1 stopped — Arrived at Jenova instead of Cactuar.` | `ShoutRunnerAutomationService.WaitForSameDataCenterTransferAsync` → `ShoutRunnerTransferResult.Failed` → `EnterFaulted` | 52 s after the request: not mid-transition, a transition had been seen, the local player's `CurrentWorld` read **Jenova**, and `Lifestream.IsBusy` returned **false** on that one sample. |
| `Recovered interrupted RUN 1.` / `Cactuar — resuming.` | `ShoutRunnerService.Resume` | The operator pressed Resume in the same session. The fault had kept the checkpoint; Resume correctly targeted Cactuar, the unfinished World. |
| `Stop requested.` | `ShoutRunnerService.Stop` | The operator pressed Stop 12 s later, during the resumed travel. The checkpoint was deleted, as Stop is designed to do. |

## 2. What the evidence establishes, and what it does not

**Established from code (Confirmed):**

1. **VenueOS asked for Cactuar.** The same `targetWorld` variable is used to build `/li Cactuar` and the
   `instead of Cactuar` text. The World comes from the fixed `ShoutRunnerCatalog` (Aether order: Adamantoise, Cactuar,
   Faerie, …). There is no index arithmetic between "Cactuar — starting" and the request.
2. **VenueOS really observed Jenova.** `CurrentWorld` is read fresh each poll on the framework thread from
   `objectTable.LocalPlayer.CurrentWorld`, with no caching. The character was on Jenova at that moment.
3. **There was no stale VenueOS travel request.** Exactly one automation task is ever in flight. The Adamantoise
   transfer had completed before, and `/li` is only issued after Lifestream reports not busy.
4. **The verdict rested on one Lifestream idle reading**, taken from a thread-pool thread (D1, D2 below).
5. **Recovery behaved correctly.** It resumed at Cactuar, not at Adamantoise or Jenova. It did not repeat an
   Adamantoise shout, and Stop cleared the checkpoint.

**Not established:** why the character was on Jenova, and why Lifestream read idle. See §6.

## 3. Current Lifestream integration (before 0.3.12)

| Purpose | Mechanism | VenueOS call site |
|---|---|---|
| Travel request | Chat command `/li <World>` through the shared `ChatCommandService` | `TravelToWorldAsync` → `IssueChatCommandAsync` |
| Busy / completion | IPC `Lifestream.IsBusy` → `bool` | wait loops, `WaitForLifestreamReadyAsync` |
| Transfer type | IPC `Lifestream.CanVisitCrossDC(string)` → `bool`; `false` was taken as "same DC" | `ClassifyTransferAsync` |
| Cancellation | IPC `Lifestream.Abort` (action) | `Abort`, failure paths, congestion two-strike, readiness recovery |
| Arrival | Game state: `BetweenAreas`/`BetweenAreas51`, `IsLoggedIn`, local player `CurrentWorld` | wait loops |
| Congestion | Unhandled chat messages matched against fixed markers; two strikes → abort + skip | `OnChatMessageUnhandled` |

Availability is not checked up front. An unavailable IPC surfaces as `IpcNotReadyError` and becomes
"Lifestream is unavailable or busy" (→ Faulted) or an Unknown classification (→ World skipped).

## 4. Lifestream compatibility findings (current source)

| # | Question | Finding |
|---|---|---|
| 1 | IPC names / signatures | **Unchanged.** `IsBusy() → bool`, `Abort()`, `CanVisitSameDC(string) → bool`, `CanVisitCrossDC(string) → bool` exist as `[EzIPC]` members of `IPC/IPCProvider.cs` (prefix `Lifestream.`). VenueOS's subscriber types match. |
| 2 | Destination argument types | Unchanged: World **name** strings. `/li <name>` resolves with `DataStore.Worlds/DCWorlds.TryGetFirst(x => x.StartsWith(name))`. No NA World name is a prefix of another. |
| 3 | World identification | By name. `ChangeWorldById(uint)` exists, but it only converts the id to a name. Nothing requires ids. |
| 4 | Request acceptance | **No acceptance signal for `/li`.** `TPAndChangeWorld` silently returns (toast/notification only) when: Lifestream is busy, the character is "Already in this world", a **Travel Block** applies (`AssertCanTravel` throws, caught and logged), there is no player, or (cross-DC) **"Allow Data Center travel" is off**. The IPC `ChangeWorld` returns `true` before those checks run, so it is no better. → handled by D4. |
| 5 | Queueing | One ECommons `TaskManager`. `/li` enqueues its whole chain synchronously, so `IsBusy` turns true at once on acceptance. |
| 6 | Busy state | `IsBusy = TaskManager.IsBusy || followPath active`. `TaskManager.IsBusy = Tasks.Count != 0 || CurrentTask != null`, plain non-thread-safe state mutated in `Framework.Update`. → D1. |
| 7 | Completion | **No completion event or IPC.** The same-DC chain ends in `RetryWorldVisit(target)` (5-minute timeout), which only finishes when `Player.CurrentWorld == target`. So Lifestream normally stays busy until it is on the target World. |
| 8 | Errors | Toasts/notifications and the Dalamud log only. No IPC error reporting. |
| 9 | Cancellation | `Abort()` clears the queue and stops `followPath`. Unchanged. |
| 10 | World-travel workflow | Target/interact with the aetheryte → "Visit Another World" → `SelectWorldToVisit` → `ConfirmWorldVisit`. **World selection is index-based:** `Utils.GetAvailableWorldDestinations()` reads `StringArrayType.WorldTranslate` entries 3–10 and fires `Callback(addon, 0, index + 2)` on `WorldTravelSelect`. **The confirm step does not check the World named in the Yes/No dialog.** Since commit `a89c7cb` (2026-09-20) it matches the full game text template with the World as a wildcard. **Retry:** `Config.RetryWorldVisit` defaults to **true** (every 30 ± 10 s); after a failed or wrong visit Lifestream re-enqueues the visit itself. |
| 11 | Cross-DC | `DCVType` HomeToGuest/GuestToHome/GuestToGuest via logout and character select (`ff48dcc`, `61575a9` changed instant-logout edge cases). The `CanVisitCrossDC` semantics are unchanged. |
| 12 | Teleport / Aetheryte | VenueOS teleports natively (`Telepo`), not through Lifestream, so this is unaffected. |

Recent Lifestream travel-code history since 2026-05-01 (`a89c7cb`, `ff48dcc`, `61575a9`, `2d96205`, `aff4b13`,
`08333b3`, `4d2890b`) shows **no IPC change** and **no change to world selection or the busy/retry semantics
ShoutRunner depends on**. The only change in the World Visit path is the template-based confirm match in `a89c7cb`.
That match still accepts whichever World the dialog names. It is not evidence of a regression, but it means a
mis-selected row is confirmed without complaint (external limitation L1).

## 5. Confirmed defects (VenueOS)

| ID | Defect | Evidence | Fix (0.3.12) |
|---|---|---|---|
| **D1** | **Lifestream IPC called off the framework thread.** After `ConfigureAwait(false)` the transfer loops, readiness recovery and failure paths called `IsBusy`/`Abort` from thread-pool threads, concurrently with Lifestream mutating that state each frame. | `ShoutRunnerAutomationService` (pre-0.3.12) `TryLifestreamIsBusy` inside `WaitFor*TransferAsync`; ECommons `TaskManager.IsBusy`/`Tick`. | Every IPC call now runs on the framework thread. `IsBusy` is read inside the same framework callback as the World (`GameState.LifestreamBusy`); aborts use `AbortLifestreamAsync`/`RunOnFrameworkThread`. |
| **D2** | **Wrong-World verdict from a single sample.** One "not transitioning + transition seen + World ≠ target + idle" poll faulted the run, with no tolerance for a momentary idle reading (D1, or a gap between chained Lifestream tasks) while Lifestream's default retry was still correcting course. Arrival had the same single-sample rule. | `ShoutRunnerSameDataCenterArrival.Evaluate` used directly per poll; cross-DC branch likewise. | New `ShoutRunnerArrivalVerifier`: the same settled conclusion must hold continuously for **3 s** (wrong World) or **1 s** (target). Any unsettled sample restarts the window, and the transfer's own 3/5-minute timeout still bounds it. The Halicarnassus intermediate-city rule and "never accept a wrong World" are unchanged. |
| **D3** | **Unreachable World classified as same-DC.** `CanVisitCrossDC == false` also means "Lifestream does not know this World from here", e.g. its lists are not built yet. | `IPCProvider.CanVisitCrossDC` = `DCWorlds.Contains`; `CanVisitSameDC` = `Worlds.Contains`. | `ShoutRunnerTransferClassification.Classify(sameDc, crossDc)`: same-DC only on `(true, false)`, cross-DC only on `(false, true)`, otherwise Unknown → an explicit, reported World skip. |
| **D4** | **A rejected `/li` waited out the full timeout.** "Command executed" was the only acceptance check, so a refusal (§4 row 4) cost 3 min (same-DC) or 5 min (cross-DC) before failing with a misleading "timed out". | Wait loops: the start window fell through to the main loop. | The start window now requires evidence of acceptance (Lifestream busy, a transition or a logout). Otherwise the transfer fails at 10 s / 15 s with "Lifestream did not start travel … (request not accepted …)". This is not retried. |
| **D5** | **No final World check before announcing.** Travel verified the World once. A World change between arrival and a later destination's shout (manual travel, another plugin) would announce on the wrong World. | `ProcessReadinessCompletion` → `BeginShout`. | New `VerifyingWorld` state: `TryGetCurrentWorldAsync` (framework thread) must equal the destination's World, or the shout is withheld and the run faults with the checkpoint kept. |
| **D6** | **Stale transfer could disable congestion monitoring.** A cancelled transfer's late `finally` (`EndTransferMonitoring`) cleared the shared monitor, including one now owned by a newer transfer. | Shared `monitoredTransfer*` fields, unkeyed. | Monitoring is keyed by a per-transfer operation id; only the owner can clear it or read its skip flag. |
| **D7** | **Resume could repeat an announcement.** Resume restarted the saved plan at index `CompletedDestinations.Count`. With plan [A, B, C], A failed and B sent, it resumed at B (re-shouting B) and never retried A. This contradicts the journal's documented contract (noted, not fixed, in `SHOUTRUNNER_0.3.8_MAINTENANCE.md` §15). | `Resume` / `ProcessTransferCompletion` (`pendingResumedStepIndex`). | `ShoutRunnerResumePlan.RemainingSteps`: the plan minus completed destinations by name (multiset), every remaining step forced to teleport. |

Diagnostics gap (fixed alongside): failures carried only a one-line reason. Each transfer now has an operation id,
and every non-successful outcome writes one Diagnostics entry and one `/xllog` warning. The entry holds: requested
World and World-sheet id; start and observed World with ids and Data Center; whether the observed World is the home
World (yes/no only, no names); Worlds observed in order; transition count; whether Lifestream was ever busy and its
last state; elapsed time; the reason; and the checkpoint position. The terminal line stays short.

## 6. Root-cause analysis of "Arrived at Jenova instead of Cactuar"

| Hypothesis | Classification | Reasoning |
|---|---|---|
| ShoutRunner issued the wrong destination | **Ruled out (Confirmed)** | §2 item 1. |
| ShoutRunner misread the current World | **Ruled out (Confirmed)** | §2 item 2. |
| Stale travel request / out-of-order callback / reused callback | **Ruled out for VenueOS (Confirmed)** | One task field per phase, cleared on every exit; tested (late completion after Stop, stale task across runs). |
| World read too early (transient state) | **Contributing, Likely but unverified (D2)** | The verdict needed only one idle sample. With Lifestream's default `RetryWorldVisit`, a wrong-World landing is normally followed by Lifestream retrying toward Cactuar. A transient idle reading would have faulted the run during that retry. |
| Torn cross-thread `IsBusy` read | **Possible, Likely but unverified (D1)** | Confirmed thread-affinity defect; whether it produced the idle reading cannot be shown without the session log. |
| Lifestream selected the wrong row (Jenova) in the World Visit list | **Likely but unverified — External dependency limitation (L1)** | Index-based selection from a string array plus a confirm step that accepts any World. A misaligned or stale list would send the character to a different World with no error. Not reproducible without the game. |
| Lifestream's chain ended early (task timeout/abort, Travel Block during retry, `/li stop`, another plugin calling `Lifestream.Abort`) | **Not reproduced** | This is the only way a correctly read `IsBusy` is false on a non-target World 52 s in. Lifestream logs would show it. |
| Manual travel or another plugin moved the character | **Not reproduced** | Cannot be excluded from the screenshot. |
| Jenova is the character's home World ("return home" step) | **Not reproduced** | Nothing in Lifestream's same-DC visit returns home first. The new diagnostics record "home World: yes/no" to settle this next time. |
| Recovery reused stale destination data | **Ruled out (Confirmed)** | Resume targeted Cactuar, the correct unfinished World (tested). |
| Recent Lifestream IPC change | **Ruled out (Confirmed)** | §4. |
| FFXIV/Dalamud change broke native travel | **Not supported by evidence** | Teleports and shouts in the same run worked, and the build is clean against the current SDK. |

**Conclusion:** the reported stop is the existing wrong-World safety check firing on a real wrong-World
observation, decided from a single, thread-unsafe idle reading. 0.3.12 removes both VenueOS-side weaknesses (D1,
D2) and keeps the safety check. **The reason the character was on Jenova is not determined.** The most plausible
external mechanisms are L1 and an early end of Lifestream's chain. Neither can be confirmed without the affected
session's `/xllog`. The new diagnostics capture what is needed to decide it next time.

## 7. Wider audit (no further defects found)

- **Same-world routing:** planner, teleport (`TryFindTeleportInfo` three-pass match, bounded readiness, transition
  wait) and per-destination flow are unchanged and sound. The screenshot confirms that teleports and shouts work live.
- **Route ordering:** fixed DC/World catalog order, per-World ping-pong plan, no skips or repeats (full Aether route
  test: 8 Worlds × 3 destinations, every shout attributed to the World it was sent on).
- **Congestion:** two-strike skip preserved; one attempt per World, no retry loop (tested). Only the ownership keying
  changed (D6).
- **Native travel:** `Telepo`, `ActionManager.GetActionStatus` and the `BetweenAreas*`/`LoggingOut` conditions are
  unchanged. `AgentWorldTravel` dismissal and the Escape policy (no Escape while in the world) are unchanged.
- **Announcements:** Line 1 → confirmed dispatch → ≥ 2.0 s → Line 2, unchanged (all 27 timing tests pass unmodified,
  plus a new test proving the World check adds nothing to the gap). A failed Line 1 never sends Line 2; a failed
  Line 2 never resends Line 1; Stop, venue change and disable cancel queued lines via the run token.
- **Stop / HardStop / fault:** clear every task field (now including the World check) and cancel the run token.
  Stop deletes the checkpoint; a fault keeps it (resumable by design).

## 8. Failure-handling matrix (after 0.3.12)

| Failure | Detected | Reported | Stops safely | Recovery | Can repeat an action? |
|---|---|---|---|---|---|
| Lifestream unavailable (IPC not ready) | Yes | "Lifestream is unavailable or busy." + Diagnostics | Faulted | Resume | No |
| Lifestream busy for 30 s before the request | Yes | same | Faulted | Resume | No |
| `/li` not accepted (busy, Travel Block, DC travel disabled, …) | **Yes (new)** | "Lifestream did not start travel …" | Faulted within 10/15 s | Resume | No |
| Unreachable / unclassifiable World | **Yes (new for `false/false`)** | "SKIPPED: could not determine …" + `/xllog` | World skipped | — | No |
| Wrong-World arrival | Yes (stable verdict) | "Arrived at X instead of Y …" or "… ended back on …" + Diagnostics | Faulted, nothing shouted | Resume → the intended World | No |
| Travel timeout (3/5 min) | Yes | "… timed out …" + Diagnostics | Faulted | Resume | No |
| Congestion ×2 | Yes | "SKIPPED: congested" | World skipped | — | No |
| DC unreachable at login | Yes | DC skipped (after recovery) / Faulted | Yes | Resume | No |
| Character logged out / loading | Readiness gate (30 s, one recovery) | Recovery lines | Faulted if not recovered | Resume | No |
| Manual travel during a run | **Yes (new, before each shout)** | "Shout at … withheld …" | Faulted, nothing shouted | Resume | No |
| Teleport failure | Yes | "SKIPPED: teleport failed" | Destination skipped | Retried on Resume (D7) | No |
| Line 1 / Line 2 dispatch failure | Yes | SHOUT FAILED / SHOUT INCOMPLETE | Continues | Retried on Resume (journal contract) | Line 1 is resent only on an explicit Resume of a partial shout (pre-existing, documented) |
| Stop / venue change / module disable / plugin reload | Yes | "Stop requested." / silent HardStop | Stopped | Stop clears the checkpoint; HardStop and reload keep it | No (stale tasks never read) |

## 9. Changes implemented

| File | Change |
|---|---|
| `src/VenueOS.Modules.Operations/ShoutRunner/ShoutRunnerArrivalVerifier.cs` (new) | `ShoutRunnerArrivalSample`, `ShoutRunnerArrivalVerifier` (stable verdicts, history, `DescribeWrongWorld`, `DescribeHistory`) |
| `…/ShoutRunnerModels.cs` | `ShoutRunnerState.VerifyingWorld`; `ShoutRunnerTransferClassification`; `ShoutRunnerTransferOutcome.Diagnostic` (optional, additive) |
| `…/IShoutRunnerAutomation.cs` | `TryGetCurrentWorldAsync`; `TravelToWorldAsync` contract documents verified arrival, wrong World and non-acceptance |
| `…/ShoutRunnerRecoveryJournal.cs` | `ShoutRunnerResumePlan.RemainingSteps` |
| `…/ShoutRunnerService.cs` | `BeginShoutWorldCheck` / `ProcessShoutWorldCheckCompletion` (D5); name-based resume (D7); a Diagnostics entry for every non-successful transfer |
| `src/VenueOS.Plugin/ShoutRunner/ShoutRunnerAutomationService.cs` | Framework-thread IPC (D1); `CanVisitSameDC` + classification (D3); acceptance fail-fast (D4); verifier in both wait loops (D2); per-operation monitoring (D6); `TransferContext` diagnostics, World-sheet id lookup, `TryGetCurrentWorldAsync`, `ShoutRunner transfer #n` log lines |
| Test fakes (5 files) | Implement `TryGetCurrentWorldAsync` (returns the World last travelled to). No assertions changed. |

No persisted schema changed (the journal and settings are untouched), the Dalamud API level is unchanged, and no
external repository was modified.

## 10. Regression tests

`tests/VenueOS.Services.Tests/ShoutRunnerLifestreamCompatibilityTests.cs`: **41 tests** (33 facts + 8 classification
theory cases). Coverage:

- **Verifier:** the exact incident timeline (Adamantoise → transition → Jenova busy → Jenova idle → verdict at
  exactly 3.0 s); a momentary idle reading followed by a Lifestream retry reaching Cactuar; the Halicarnassus
  intermediate settle; 1 s arrival window; unreadable busy state; no verdict before travel began; ending back on the
  origin World; window restart on a transition or a different World; cross-DC logout restart; diagnostic history
  content.
- **Classification:** all 8 combinations, including the pre-0.3.12 `(false, false)` misclassification.
- **Resume plan:** by name, after the first destination, all completed, forced teleport, duplicates.
- **Service:** the exact reported sequence (Adamantoise completed → Cactuar requested → Jenova observed → fault
  with diagnostics and a Cactuar checkpoint → Resume → Cactuar requested again → Stop mid-travel → checkpoint
  cleared, no extra shout); Resume completes Cactuar and continues; a Lifestream retry succeeding is normal; the
  pre-shout guard (moved World, unreadable World); Resume after an earlier failed destination (no duplicate shout);
  late completion after Stop; stale task across runs; one completion consumed once; request not accepted; Unknown
  classification skip; congestion skip with no retry; Stop during the Line 2 wait; the Line 2 ≥ 2.000 s gap still
  holds; venue change during travel; plugin reload during recovery; no cross-run contamination; the full Aether
  route with per-World attribution.

**Mutation checks** (each applied alone, the ShoutRunner test set re-run, then reverted): wrong-World window → 0
(5 failures); arrival window → 0 (1); no window reset (1); resume by count (2); resume keeps the planned
`RequiresTeleport` (1); pre-shout guard bypassed (2); classification `(_, false) → same-DC` (1); transfer
diagnostics not recorded (2). **8 / 8 killed.**

Not covered by automation: the plugin-layer engine itself (unsafe Dalamud/FFXIVClientStructs code, no test host;
`NEW_MODULE_GUIDE.md` §30), real Lifestream behavior, and real chat delivery.

## 11. Remaining limitations

- **L1 (external):** Lifestream's World Visit picks a list row by index and confirms without checking the World
  name. VenueOS cannot prevent a mis-selection. It can only detect it (wrong-World verdict) and refuse to announce.
- **L2 (external):** Lifestream reports neither acceptance nor completion. VenueOS infers both from `IsBusy` plus game
  state. The fail-fast windows (10 s / 15 s) assume Lifestream enqueues synchronously, as current source does.
- **L3:** a wrong-World arrival still faults the run instead of retrying automatically. That is deliberate: no new
  retry semantics without live evidence. Lifestream's own retry is now given time to finish (D2), and Resume
  retries on request.
- **L4:** the verdict windows add about 1 s per World arrival and are untested live.

## 12. Manual QA checklist (owner / affected user)

Live QA status: **Pending — owner unavailable; release explicitly authorized based on automated validation.**

1. **Same-world announcements:** one DC, three default destinations. Every destination shows `SHOUT SENT`, and only
   your current World appears in the terminal for that pass.
2. **Same-DC travel:** Aether selected. Each World shows `<World> — starting` → destinations → `completed`. There is
   about 1 s more settle time per World than before.
3. **Adamantoise → Cactuar:** watch the transfer. On success, Cactuar's three shouts follow. If Lifestream lands
   elsewhere, the terminal shows `Arrived at <World> instead of Cactuar …`, no shout is sent, and **Settings →
   Diagnostics** has a `transfer Failed` entry with `Worlds observed: …`.
4. **World-arrival verification:** during a World Visit, ShoutRunner must not start teleports or shouts until the
   loading screen is gone and you are on the new World.
5. **Cross-DC:** select two DCs. Logout → login → the first World of the second DC. If "Allow Data Center travel" is
   off in Lifestream, expect `Lifestream did not start Data Center travel …` within about 15 s.
6. **Stop during travel:** press Stop mid-World-Visit. Lifestream stops, the terminal shows `Stopped.`, and nothing
   is shouted afterwards.
7. **Recovery after interruption:** close the game mid-route. On restart, Resume continues at the interrupted World
   and does not repeat a destination already marked `SHOUT SENT`.
8. **Stop after recovery:** Resume, then Stop. The interrupted-run panel disappears.
9. **Line 1 / Line 2:** both lines arrive in order, about 2 s apart. One line only: no extra pause.
10. **No announcement on the wrong World:** during a run, travel manually to another World between destinations.
    The next destination shows `Shout at … withheld …` and nothing is sent.
11. **Congestion (only if it happens naturally):** a congested World is skipped once and the route moves on.

**If the wrong-World stop happens again**, send: (a) **Copy Terminal** output; (b) the matching **Settings →
Diagnostics** entry (Copy); (c) `/xllog` lines containing `ShoutRunner transfer #` and Lifestream's lines from the
same minute (Lifestream logs `Same dc/<World>` and its task names, such as `SelectWorldToVisit`); (d) your
Lifestream version and whether "Retry same-world failed world visits" is on. None of these contain character names
from VenueOS.

## 13. Validation

| Check | Result |
|---|---|
| `dotnet test VenueOS.sln -c Debug` | **1492 / 1492 passed**, 0 failed, 0 skipped (Core 69, Venues 23, Services 1400; baseline 1451) |
| `dotnet build VenueOS.sln -c Debug` | 0 warnings, 0 errors |
| `dotnet build VenueOS.sln -c Release` | 0 warnings, 0 errors |

Release artifact details (ZIP size, SHA-256, GitHub Release, `repo.json`) are recorded in the 0.3.12 publication
commit message. The ZIP is built from the source release commit, so they cannot be part of it.
