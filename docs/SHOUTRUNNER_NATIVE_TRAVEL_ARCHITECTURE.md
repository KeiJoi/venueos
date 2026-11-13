# ShoutRunner Native World Visit & Data Center Travel (VenueOS 0.3.14 — UNRELEASED QA BUILD)

**Status:** implemented and automatically tested. **Live FFXIV QA pending** (owner, see
[`SHOUTRUNNER_NATIVE_TRAVEL_LIVE_QA.md`](SHOUTRUNNER_NATIVE_TRAVEL_LIVE_QA.md)). Nothing in this document was run inside
FFXIV by the developer. Not published: no push, tag, GitHub Release or `repo.json` change.

Evidence labels used below: **[game data]** read from this PC's installed 7.58 client files (build 2026.11.03) with
Lumina; **[screenshot]** the owner's six screenshots in `C:\FFXIVplugs\FFXIV-World-DC-Travel`; **[official]** Lodestone;
**[source]** public plugin source; **[structs]** the FFXIVClientStructs build VenueOS compiles against (dev hooks,
10 Nov 2026); **[unverified]** needs live inspection.

## 1. Goal and scope

ShoutRunner travels between Worlds with VenueOS's own engine, including between the four North American logical
Data Centers, with no Lifestream, no logout, no character select and no Home World detour. The engine is a reusable
service (`VenueOS.Modules.Operations.Travel`) that other modules could use.

Supported: same logical DC; another logical DC of the character's region (Patch 7.58 World Visit). Not automated:
another region (Data Center Travel from character select; **[official]** still the method for NA/EU ↔ Oceania). It is
reported as a Data-Center-level skip.

## 2. Research findings

| # | Finding | Evidence |
|---|---|---|
| 1 | Since Patch 7.58 (10 Nov 2026) travel between logical DCs inside NA and inside EU uses "Visit Another World Server" with a DC dropdown, with no Home World detour. Data Center Travel no longer covers it; it remains for Oceania. | **[official]** NA/EU same-region topic `…/topics/detail/50458eeb98ca9466c69b85074f5326be0b3f3a94` |
| 2 | **Every NA World has `World.DataCenter = 4` ("Aether", the merged group).** The real logical DC is `World.Unknown0`: Aether 4, Primal 5, Crystal 8, Dynamis 11. Materia keeps both at 9. | **[game data]** World and WorldDCGroupType sheets; corroborated by **[source]** MasterOfPuppets `WorldCatalog`, SRankSentinel `NativeTravel.WorldVisit` |
| 3 | Confirmation text is Addon row 12519 "Travel to `<World>`?" (and 12624, the same plus a party notice line). | **[game data]**; **[screenshot]** "Travel to Behemoth?" with OK / Cancel |
| 4 | Aetheryte menu option is transport/Aetheryte row 3 " Visit Another World Server.". | **[game data]**; **[screenshot]** menu: Aethernet. / Visit Another World Server. / Set Home Point. / … / Cancel. |
| 5 | World Visit cities: aetheryte 8 Limsa Lominsa Lower Decks (territory 129), 2 New Gridania (132), 9 Ul'dah - Steps of Nald (130). | **[game data]** Aetheryte sheet; **[official]** "only available from the central aetheryte" |
| 6 | The World Visit window (addon `WorldTravelSelect`) has, under WORLD LIST, a DC selector showing the selected DC with ◁ ▷ arrows; it defaults to the current DC; choosing a DC immediately shows that DC's 8 Worlds alphabetically; the current World is listed, greyed, with a home icon. | **[screenshot]** × 4 pages |
| 7 | **Corrected by live Inspect (2026-11-13, QA attempt #1):** node 2 is a plain container (type 1, no component) for the regional section. Inside it, node 4 is the DC `AtkComponentDropDownList`, nodes 5 and 6 are the ◁ ▷ Buttons, and node 7 is the World list. Node 20 (TreeList) is hidden. The public sources treated node 2 as the dropdown; reading it that way was refused safely (UnexpectedUi, nothing clicked). | **[verified live]** node types and visibility; node 7 rows (8 Crystal Worlds) and its window-owned click event. **QA attempt #3 (Inspect):** node 4's labels read `Aether, Crystal, Dynamis, Primal` (verified). But it reports selected index **-1** while showing Crystal, and its list's item click is **not** owned by the dropdown, so VenueOS still never clicks it. The page shown is therefore taken from the rows when the dropdown reports no selection (every row must resolve to one logical DC), so a same-DC World Visit never touches the dropdown. **QA attempt #2:** the window reported ready before its DC section was shown, which gave an early "Unsupported". Fixed: the port treats the window as not ready until the regional section or the legacy list is visible, and the driver waits the 6 s settle time before concluding there is no selector. **QA attempt #4 (same DC, Diabolos → Balmung):** `AtkComponentList.DispatchItemEvent` moved node 7's selection but did not reach the window (no confirmation; `AgentWorldTravel.DestinationWorldId` stayed 0). The failure capture showed the event wiring: a World row click is `ListItemClick(param 0)` → window; the arrow Buttons 5 and 6 send `ButtonClick(param 2)` and `ButtonClick(param 3)` → window; the dropdown's list (4/3) sends `ListItemClick(param 1)` to **both** the dropdown and the window, which is why its ownership check fails; the shown DC is the dropdown CheckBox text (4/2/3, "Crystal"). Fix: the World click now delivers exactly that registered node-7 event to the window's `ReceiveEvent`, with the row's renderer and index (the technique of ECommons' `ClickHelper.InvokeReceiveEvent`, written against the 7.58 structs). The prompt and destination-id gates are unchanged. DC switching stays blocked until the arrow direction is verified. **QA attempt #5 (build `6a27301`): the owner reports same-DC World Visit works live (2026-11-13).** That World-click path is now the accepted baseline. **QA attempt #6 (arrow comparison):** the window was reopened before each press. From Crystal, ◁ showed Aether and ▷ showed Dynamis: the dropdown text (4/2/3), the World rows and AtkValue[11] (0/1/2) all moved one step. Node 5 (x=158, `ButtonClick` param 2 → window) is ◁, and node 6 (x=182, param 3 → window) is ▷. **DC switching is now implemented with these arrows,** still behind the session-only QA switch. The shown DC is read from the dropdown text. Each action is ONE adjacent step toward the target, revalidated in the same frame (adjacent to the page shown now, toward the target, target not disabled, arrows wired exactly as verified, ◁ left of ▷). A step completes only when the shown DC moves. There are at most 2 presses per step and 4 steps per operation, and the stale-list guard and confirmation gates apply after. The dropdown list (item clicks to two listeners) is never used. **QA attempt #7 (build `07c68b0`): the owner reports cross-DC travel works live (2026-11-13): Crystal → Aether → Primal → Aether, all without issues.** Diagnostics were not captured for this run. That covers one-step and three-step arrow switching in both directions. |
| 8 | `AtkComponentDropDownList` (List, SelectItem, GetSelectedItemIndex, OpenStateChangePending, OwnerNode), `AtkComponentList` (GetItemCount, GetItemLabel, GetItemRenderer, GetItemDisabledState, SelectItem, DispatchItemEvent, SelectedItemIndex, IsUpdatePending), `AgentWorldTravel.DestinationWorldId` all exist. | **[structs]** (reflection over the dev-hooks DLL) |
| 9 | Lifestream (newest 2.6.0.9, 16 Oct) has no DC dropdown handling. It builds its World lists from `World.DataCenter`, so on 7.58 data it treats all 32 NA Worlds as one DC and picks rows by index from a string array (`WorldTranslate` entries 3–10). | **[source]** Lifestream `b363201` |
| 10 | A public plugin drives the DC page with a raw callback `(1, tab)` attributed to "Ls 2.6". Public Lifestream has no such code. | **[source]** MasterOfPuppets; not used by VenueOS (unverified provenance) |

**Corrections to the 0.3.13 report.** (a) 0.3.13's classifier read `World.DataCenter`, which finding 2 shows is merged.
On real 7.58 data it classified every NA World as Aether. That failed safe (other-DC Worlds were skipped by the
catalog check, and the pre-shout guard withheld), but it was wrong. (b) 0.3.13 said Lifestream would use logout-based
Data Center Travel for NA. Finding 9 shows Lifestream's lists treat all NA Worlds as one DC, so it would attempt a
same-DC World Visit with index-based row selection on the new window. Both corrections are recorded as addenda in
the earlier reports.

## 3. Architecture

```text
VenueOS.Modules.Operations.Travel            (pure, unit-tested)
  TravelWorld / ITravelWorldCatalog          logical DC from World.Unknown0, region
  WorldVisitCities                           the three World Visit aetherytes
  NativeTravelPolicy                         CanVisit, IsConfirmationFor, CleanLabel, menu-entry match
  NativeTravelModel                          snapshots, actions, phases, outcomes, request/result/status
  NativeWorldVisitDriver                     the per-operation state machine (decides only)
  NativeTravelService                        one operation per client, ids, cancellation, loop, logs
VenueOS.Modules.Operations.ShoutRunner
  ShoutRunnerTravelClassification            Same DC / Cross DC / Cross-region / Unknown (game data)
  ShoutRunnerNativeTravelMapping             native outcome → route outcome
VenueOS.Plugin.Travel                        (unsafe, framework thread)
  DalamudTravelWorldCatalog                  Lumina World sheet → TravelWorld
  DalamudNativeTravelPort                    snapshot reads, revalidated actions, texts, congestion chat
  NativeTravelQaPanel                        Settings → ShoutRunner → Native Travel QA
VenueOS.Plugin.ShoutRunner
  ShoutRunnerAutomationService               classification + TravelToWorldAsync via NativeTravelService
```

### Loop (NativeTravelService)

`snapshot (framework thread) → driver.Next → if acting: port.ExecuteAsync(action, revalidate) → driver.Executed →
non-blocking poll delay (200 ms)`. `ExecuteAsync` re-reads the game and calls `driver.Revalidate` in the same framework
callback as the action. If anything changed (label, row's World, prompt, event owner, …) the action is skipped and the
driver decides again. Cancellation (ShoutRunner Stop, QA Cancel, plugin unload) ends the loop at its next step. Before
confirmation, the menus this operation opened are closed. After confirmation nothing is touched (**[official]** "the
transfer cannot be canceled").

### States (NativeTravelPhase)

ResolvingDestination → CheckingCapability → PreparingAetheryte (teleport to a World Visit city if needed) →
OpeningWorldVisit (interact, choose "Visit Another World Server.") → SelectingDataCenter → SelectingWorld →
VerifyingSelection → ConfirmingTravel → WaitingForDeparture → TravelInProgress → WaitingForStableArrival → Completed.
Terminal: Cancelled, Unavailable, Congested, TimedOut, WrongDestination, UnexpectedUi, Interrupted, Failed.

### Safety rules (each covered by tests and mutation checks)

| Rule | Mechanism |
|---|---|
| Never confirm an unverified destination | The prompt's first line must equal a game template with the target World **and** `AgentWorldTravel.DestinationWorldId` must be 0 or the target id. Otherwise `SelectionMismatch`: the UI is closed and nothing is confirmed. |
| No position-only selection | The DC item is chosen by its label; the World row by the World its label names in game data. Label decorations seen in the screenshots (▶, home icon) are stripped by `CleanLabel`. |
| No stale rows | Every rendered row must belong to the target logical DC before any row is clicked (the dropdown can change before the rows do). |
| No speculative input | The DC labels must be exactly this region's logical DCs (distinct, 1–8). The list's `ListItemClick` event must be registered once, to the expected owner (dropdown or window). Not pending. Otherwise `UnexpectedUi` and nothing is clicked. No raw callback index is used for DC or World selection. |
| Bounded everything | Step 10 s; list settle 6 s; teleport 60 s; departure 45 s; arrival 5 min after confirmation; overall 7 min; 2 interact attempts; 2 DC-select attempts; 1 teleport; 1 World selection; 1 confirmation. |
| Stable arrival on World AND DC | The 0.3.12 `ShoutRunnerArrivalVerifier` (shared): settled 1 s on the target with the travel conditions clear; a different World or DC settled 3 s is `WrongDestination`. |
| One owner | A second request while an operation runs gets `Busy`. ShoutRunner's Stop cancels only ShoutRunner's operation, never a QA test. |
| Leave the player's UI alone | A failure before VenueOS opened anything closes nothing. A leftover travel menu is closed once, never driven. |
| No logout path | No action kind exists for logout, character select or a Home World return (tested). |

### Data Center selection gate

The dropdown interaction (`SelectItem(index)` + list `DispatchItemEvent(index, ListItemClick)`, after label and
event-owner verification) follows **[source]** SRankSentinel and is **[unverified]** on this client. It is off by
default (`NativeTravelService.AllowDataCenterSelection`, session-only, never saved). While it is off, a World on
another logical DC finishes `Unsupported` before anything is clicked, and ShoutRunner skips that DC with a reason that
points to the QA switch. Same-DC World Visit is not gated. When the owner's live QA passes, a follow-up release makes it
the default.

## 4. ShoutRunner integration

- Classification (`ShoutRunnerTravelClassification`, game data): Same DC / Cross DC → native travel; Cross-region →
  DC-level skip with a reason, no travel attempted; Unknown (missing data or a stale catalog) → World skip.
- Mapping (`ShoutRunnerNativeTravelMapping`):

  | Native outcome | Route effect |
  |---|---|
  | Arrived, AlreadyThere | continue |
  | Congested, WorldUnavailable | World skipped (existing congestion path) |
  | DataCenterUnavailable, Unsupported | Data Center skipped (recorded in the checkpoint's skipped DCs) |
  | WrongDestination, NotStarted | World skipped; 2 in a row fault the run (0.3.13 rule) |
  | SelectionMismatch, UnexpectedUi, TimedOut, NotReady, Busy, Cancelled, Failed | run faults (resumable) |

- World Visit city: the first of the route's destinations that is a World Visit city, so the next World's route
  begins where the character arrives.
- Pre-shout guard: World and REAL logical DC (from `World.Unknown0`).
- Lifestream: no longer referenced by ShoutRunner (no `/li`, IPC, busy polling or abort).
- Unchanged: route planner, catalog and order, recovery journal, Line 1 / Line 2 (2 s), congestion two-strike, native
  teleport between destinations, Stop/Resume.

## 5. Native Travel QA tool

Settings → Modules → ShoutRunner → **Native Travel QA (diagnostic)** (collapsed). It shows the current World / DC /
home, location and aetheryte distance, window and confirmation state, and the operation status. It offers DC, World and
city pickers, the DC-selection switch, Start / Cancel Travel Test, **Inspect (read-only)** (node ids and types, the DC
selector's items, selection and event ownership, World rows and how each resolves, the Yes/No prompt, the agent's
destination, and the window's AtkValues), and **Copy Diagnostics**. It never sends chat, never runs a route, never
touches ShoutRunner's checkpoint, and refuses to start while ShoutRunner runs or another travel is active.

## 6. Tests

| File | Tests | Covers |
|---|---|---|
| `NativeTravelEngineTests.cs` | 71 | Real service + driver on the simulator: same-DC, the screenshot scenario (Cuchulainn → Primal → Behemoth), all NA DC pairs, label-based DC choice, teleport-first, lagging rows, misclick → never confirmed, wrong agent destination, inexact prompt, prompt changed between decision and click, revalidation skip, unowned events, corrupt labels, a distinct label from another region's DC, read-only UI capture on UnexpectedUi / SelectionMismatch (taken before closing; never on success; a failing capture changes nothing), ignored dropdown (bounded), stale list forever, window never opens, leftover menu, congested / missing / disabled-DC, switch off, cross-region, unknown World, wrong World / DC arrival, congestion ×2, never departs, never arrives, one selection + one confirmation, busy, cancel before/after confirmation, ids, combat/mounted/logged out, no logout step |
| `NativeTravelPolicyTests.cs` | 20 | Confirmation templates (incl. 12624, the screenshot prompt, Jenova), label cleaning, menu entry, region rule, cities |
| `ShoutRunnerNativeTravelIntegrationTests.cs` | 7 | Real ShoutRunnerService + real NativeTravelService: full Aether/Crystal/Dynamis/Primal route (96 shouts on their own World and DC, 3 DC selections, 32 confirmations), switch off, misclick → fault → Resume, congested row, Stop mid-travel, QA sends nothing and leaves the checkpoint alone, QA vs ShoutRunner concurrency |
| `ShoutRunnerPatch758TravelTests.cs` | 21 methods (rewritten) | 0.3.14 classification incl. merged-DataCenter guard, mapping table, DC verification, multi-DC routes, DC skip + Resume, wrong-arrival bounds |
| `NativeTravelSimulator.cs` | — | Simulated 7.58 window built from the screenshots, with fault switches |

**Full validation (2026-11-13, after the walking fix):** `dotnet test VenueOS.sln -c Debug` passes 1,610 of 1,610 (Core 69, Venues 23,
Services 1,518). `dotnet build VenueOS.sln` passes in Debug and Release (clean, `--no-incremental`) with 0 warnings and 0 errors. The
targeted travel filter (`ShoutRunner|NativeTravel`) runs 333 tests (the 15-mutant runs used 304. QA fix 2 added mutants Q1 and Q2, and the arrow fix added A1 to A5: direction, adjacency, step completion, step limit and unreadable shown DC. All 7 were killed. The first-city fix added 1 more and the walking fix added W1 to W3: walking stopped on failure, stop within reach, the 30 y walk limit. All killed).

**Mutation checks.** 15 hand-written mutants against the safety checks, each run against the 304 targeted tests.
**15 of 15 killed.**

| # | Mutant | Result |
|---|---|---|
| N1 | confirmation prompt not checked | killed (2) |
| N2 | game destination id not checked | killed (1) |
| N3 | confirm revalidation ignores the prompt | killed (1). It survived the first run; the test *prompt changed between decision and click* was added |
| N4 | stale World list accepted | killed (12) |
| N5 | DC selector event ownership ignored | killed (1) |
| N6 | World list event ownership ignored | killed (1) |
| N7 | QA gate for DC selection ignored | killed (1) |
| N8 | disabled (congested) row clicked | killed (2) |
| N9 | DC label region check off | killed (1). It survived the first run because the corrupt-label test also had a duplicate; a distinct-labels test was added |
| N10 | UI closed when VenueOS opened nothing | killed (6). The first version didn't compile (CS0414); re-run compile-safe |
| N11 | arrival accepted without the target id | killed (2) |
| N12 | selection mismatch mapped to a World skip | killed (2) |
| N13 | cross-region treated as World Visit | killed (2) |
| N14 | second concurrent operation allowed | killed (2). The first run hung on an unbounded await; the test is now bounded at 10 s with a cancelling `finally` |
| N15 | label decorations kept | killed (35) |

## 7. Requires live inspection (owner QA)

1. That `WorldTravelSelect` node 4 (the dropdown inside the node 2 section) has list labels that read "Aether", "Crystal", "Dynamis", "Primal", and
   that its item-click event is owned by the dropdown. Inspect shows this.
2. That `SelectItem` + `DispatchItemEvent` changes the page (the gated step).
3. ~~That node 7 rows read as World names and their click event is owned by the window.~~ **Verified live 2026-11-13.**
   Since that QA attempt, an UnexpectedUi or SelectionMismatch stop also captures this Inspect dump automatically,
   before the window is closed, in the result's diagnostic.
4. That the confirmation is the `SelectYesno` addon (OK / Cancel) and `AgentWorldTravel.DestinationWorldId` holds the
   selected World.
5. Aetheryte reach after teleport arrival: an arrival out of the 8 y reach and under 30 y (owner: Limsa) now walks to the aetheryte, using `/lockon on` + `/automove on` (the public Lifestream technique; both commands verified in the 7.58 TextCommand sheet). It stops at 6.5 y and gives up after 10 s or 2 s without progress. Auto-run is always turned off again: by the driver, on close, and in the port's EndOperation and Dispose safety nets. **[to verify live: checklist B4/B5]**
6. Departure / arrival condition flags during a real World Visit queue.

## 8. Known limitations

- DC selection is gated until item 2 is verified live.
- Cross-region travel is not automated.
- Congestion detection relies on disabled rows plus the existing chat markers. How a congested World looks in the
  7.58 list is **[unverified]**.
- `ShoutRunnerArrivalVerifier` and the 0.3.12 Lifestream-era terminology in historical tests remain; their logic is
  shared by native travel.
