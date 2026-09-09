# Giveaways — Implementation Report

## 1. Executive Summary

Giveaways is a new, greenfield, purely-local VenueOS module for running timed venue giveaways with automated
chat announcements and an FFXIV `/random` roll tracker. It has no backend, no network dependency, and no shared
state with any other module. It was built end-to-end (pure logic, Dalamud adapter, operator UI, composition-root
wiring, and tests) following `NEW_MODULE_GUIDE.md`, using Bingo's own `/random`-adjacent roll adapter
(`BingoRollChatAdapter`/`BingoRollCorrelation.cs`) and TournamentControl's `TournamentCalloutService` as the closest
existing reference implementations for chat-parsing and scheduled-announcement patterns, respectively.

The module starts `UnderDevelopment: true`, disabled by default, per the guide's promotion policy — it has **not**
been promoted, and this session did not stage, commit, push, tag, or release anything (§48/§0 below confirms this
against the actual git state).

## 1a. Post-Implementation QA Correction — Roll-Window Timing (applied after initial build/test pass)

Before live QA, a targeted behavior correction was made to when roll acceptance actually closes. The **original**
implementation closed roll acceptance at the raw Giveaway Duration expiry, then began the Closing block. This was
changed because a Closing block routinely tells participants something like "last chance to roll!" — VenueOS must
not stop accepting rolls while its own announcements are still claiming the giveaway is open.

**Current (corrected) behavior**: Giveaway Duration now only decides when the **Closing block begins**. Roll
acceptance stays open through the entire Closing sequence and closes only once Closing's **final non-empty line is
enqueued** — or immediately at duration expiry if the Closing block is empty. See §9/§27 below for the full revised
timeline and QA checklist. This correction did **not** touch Highest/Lowest/Closest logic, roll limits, special
numbers, identity handling, parsing, Start/Midpoint timing, announcement channel behavior, preset persistence, the
tracker window, or per-venue isolation — it is scoped entirely to when the roll window closes relative to Closing.

## 1b. Live QA Fix #2 — Preset Editor Modal + Host/Self `/random` Capture

The module was tested live in FFXIV for the first time. **Confirmed working and not regressed by this pass:**
Giveaways launches and runs; saved preset selection; cross-world `/random` capture (correct Name + HomeWorld);
the Roll Tracker displaying that participant; and Closing-phase roll acceptance (a participant could still roll
while Closing announcements were being sent — the exact behavior §1a introduced).

Two problems were found and fixed in this pass.

### Issue 1 — Preset authoring UI: root cause, old workflow, new modal workflow

**Root cause of the duplicate "New Preset" controls:** `UiKit.TextInputModal` (`Shell/UiKit.cs`) identifies its
popup with a single `private const string PopupId` — identical across every instance of the class, by design,
since normally only one such modal is ever open at a time. `GiveawaysOperatorPanel` had TWO separate
`TextInputModal` fields (`newPresetModal` for "+ New Preset" and `renamePresetModal` for "Rename"), both drawn
unconditionally every frame in `DrawSettings()`. Because both instances share the exact same ImGui popup identity
string, whichever one had `Request(...)` called opened a popup that BOTH instances' `Draw()` calls then rendered
content into in the same frame — the reported "two Name/Create/Cancel areas". This was a real, mechanical ID
collision, not a rendering glitch.

**Old preset-authoring workflow:** Settings → Modules → Giveaways rendered the compact preset list, and directly
underneath it, the COMPLETE editor for whichever preset was currently selected — general settings, winner settings,
and up to 30 announcement-line text fields (10 each for Start/Midpoint/Closing), all permanently visible and each
one persisting immediately on every keystroke via `GiveawayService.UpdatePreset`. "+ New Preset" called
`GiveawayService.CreatePreset(name)` immediately — i.e. an empty preset was persisted the instant the operator
typed a name, before any other field was configured.

**New preset-authoring workflow:** Settings → Modules → Giveaways now shows ONLY the compact preset list (name,
channel/winner-mode subtitle, RUNNING flag) with an **Edit** and **Delete** action per row, plus one
**+ New Preset** button. All authoring — Name, Shout/Yell, Delay, Duration, Winner Mode, Closest Target, Allowed
Rolls, Special Numbers, and all three announcement blocks — moved into one dedicated modal,
`GiveawayPresetEditorModal` (`src/VenueOS.Plugin/Giveaways/GiveawayPresetEditorModal.cs`). There is exactly one
authoring surface; `newPresetModal`/`renamePresetModal` and the in-page editor methods (`DrawPresetEditor`,
`DrawGeneralSection`, `DrawWinnerSection`, `DrawBlockEditor`) were removed from `GiveawaysOperatorPanel` entirely
(their block-editor logic moved into the modal, operating on the draft instead of the service).

**Exact Save semantics:** the modal holds a private `GiveawayPreset draft` — never the persisted `Settings` record.
Every field edit inside the modal only reassigns `draft` via a `with` expression; nothing is persisted until Save
is clicked. Save first runs `GiveawayPresetValidator.Validate(draft)` (the SAME validator `GiveawayService.Start`
already used); on any failure the modal stays open and shows the error(s) inline (`UiKit.ErrorState`) rather than
persisting an invalid preset. On success: for a new preset, `GiveawayService.SaveNewPreset(draft)` — a brand-new
service method that mints a fresh Id and appends+persists in ONE atomic `SaveModuleConfig` call (never a two-step
"create empty, then edit" sequence); for an existing preset, the already-existing
`GiveawayService.UpdatePreset(existingId, _ => draft with { Id = existingId })` — also one atomic save, preserving
the stable Id. Either way the popup then closes (`ImGui.CloseCurrentPopup()`).

**Exact Cancel semantics:** the Cancel button calls `ImGui.CloseCurrentPopup()` and nothing else — `draft` is
simply discarded in memory; no service method is ever called, so nothing is created or changed. Closing the modal
via its native title-bar X behaves identically (ImGui's own popup-close path; no code in this modal treats X any
differently from Cancel), satisfying the requirement that X-close must behave like Cancel.

**How editing existing presets works:** clicking **Edit** on a preset row calls
`presetEditorModal.OpenForEdit(preset)`, which sets `draft = preset` (a genuine value copy — `GiveawayPreset` is a
record, so every subsequent `draft = draft with {...}` inside the modal can never reach back and mutate the
persisted instance still sitting in `GiveawayService.Settings.Presets`) and opens the same modal used for "+ New
Preset". Save/Cancel behave exactly as described above; Delete stays on the compact list row (unchanged semantics
and `ConfirmDialog` confirmation — see below).

**Delete preserved:** Delete moved from inside the (now-removed) full in-page editor to a `DangerButton` on each
preset-list row, still gated by `GiveawayService.CanDeletePreset` (disabled + tooltip while that preset is
running) and still routed through the same `ConfirmDialog` with the same "permanently deleted... cannot be undone"
wording. Nothing about Delete's actual semantics changed.

### Issue 2 — Host/self `/random` not registered: forensic root cause and fix

**Forensic trace performed** (chat event → SeString/payload inspection → random-roll recognition → self detection
→ local-player identity resolution → `GiveawayService` roll acceptance), per the task's explicit requirement not to
guess: live QA proved the cross-world participant's roll was captured correctly, meaning `IChatGui.ChatMessage`
firing, `message.Message.TextValue` extraction, and `GiveawaysRollChatAdapter.HandleChatMessage`'s overall flow all
work. The only self-specific code is `RandomRollParser.SelfPattern` (deciding `IsSelf`) and
`GiveawaysRollChatAdapter.ResolveSelf` (reading `IObjectTable.LocalPlayer`) — since a roll that never parses at all
returns early from `RandomRollParser.TryParse` with `null`, `ResolveSelf` is never even reached, which matches
"silently dropped, no error" exactly.

Inspecting `RandomRollParser.cs` found the concrete discrepancy: `SelfPattern` was
`@"^Random!\s*You\s+Roll\s+a\s+..."` compiled with **no `RegexOptions.IgnoreCase`**, requiring a literally
capitalized "You Roll a" (transcribed verbatim from this module's originally-given fixture text) — while
`OtherPattern`, right next to it, already had `RegexOptions.IgnoreCase`. Cross-referencing
`BingoRollCorrelation.cs`'s `RollResultRegex` — an **independently, already live-verified** regex for this exact
underlying game-text family (`/random N`'s "You roll a N (...)." shape) — confirms it matches lowercase "you roll
a" with `RegexOptions.IgnoreCase` explicitly. This is strong, concrete corroboration (not a guess) that FFXIV's
actual self-roll text case does not reliably match what was originally assumed, and that case-insensitive matching
is the correct, already-proven way to handle it for this message family.

**Fix applied:** `SelfPattern` now compiles with `RegexOptions.Compiled | RegexOptions.IgnoreCase`, matching
`OtherPattern`'s existing flag. This is a one-line change with a clear, corroborated root cause — not a new blind
text special-case. `GiveawaysRollChatAdapter.ResolveSelf` (which resolves the self roll to
`GuestIdentity(LocalPlayer.Name, LocalPlayer.HomeWorld)`, never to a participant literally named "You") was
**not** changed — it was already correct; it simply was never being reached.

**Self-roll behavior confirmed unchanged/correct by design:** once `IsSelf` parses correctly, the resulting
`ObservedRandomRoll` flows into `GiveawayService.HandleRollObservation` → `GiveawayRollBoard.Accept` exactly like
any other participant's roll — neither of those methods has ever had any identity-source special-casing, so
Allowed Rolls Per Person, Total Rolls, Highest/Lowest/Closest, tie handling, leader calculation, special numbers,
and Closing-phase acceptance all already applied uniformly to a self roll the moment it successfully parses. No
rule excluding the local player was ever present and none was added.

**Cross-world handling preserved:** `OtherPattern` and `GiveawaysRollChatAdapter.ResolveOtherPlayer`'s
`PlayerPayload`-preferred, plain-text-fallback identity resolution were not touched by this fix.

**Closing roll-window behavior preserved:** `GiveawayService`'s §1a timeline/roll-window logic was not touched by
this pass at all — self and cross-world rolls both go through the same unchanged `IsAcceptingRolls` gate.

### Roll-visibility help note

No such note existed near the roll tracker/Total Rolls previously. Added as an always-visible caption (not only a
hover tooltip, so it can't be missed) directly under the Winner Mode/Total Rolls line in
`GiveawaysOperatorPanel.DrawTracker`: *"Roll visibility is limited by FFXIV's normal /random message range —
players must be close enough to the host for their roll to appear in the host's chat."* Rendered via
`ImGui.TextDisabled`, the same "muted caption" convention `BingoCalledNumbersWindow` already uses elsewhere in this
codebase for a similar secondary-status line.

## 2. Module ID / Descriptor

| Field | Value |
|---|---|
| Internal ID | `events.giveaways` |
| Display Name | Giveaways |
| Description | "Run timed venue giveaways and track FFXIV random rolls." |
| Icon key | `gift` (new, added to `AppIcons`; distinct from every existing key, including `ticket`) |
| `UnderDevelopment` | `true` |
| `IsEnabled` default | `false` |
| `DisplayOrder` | `12` (next after Block Letters' `11`) |

No existing module used the `events.*` namespace, and no ID collision exists with `events.giveaways` — used as
proposed, no rename needed.

## 3. Product Behavior Implemented

- **Announcement automation**: a saved preset's three blocks (Start/Midpoint/Closing, up to 10 lines each) are sent
  automatically at the correct times through the shared `ChatCommandService`, on the preset's configured
  Shout/Yell channel, with a configurable delay between lines.
- **Roll tracking**: while a giveaway is active, `/random` and `/random 999` results are parsed from chat, resolved
  to a `GuestIdentity`, and tracked against Highest/Lowest/Closest winner rules, with per-person roll limits, tie
  detection, and special-number highlighting. The roll window opens once the Start block finishes and stays open
  through Midpoint AND through Closing, closing only once Closing's final non-empty line is sent (§1a/§9).
- **Active-preset prominence**: the live screen renders the running (or, if none is running, the currently
  selected) preset's name in large, bold, accent-colored text inside its own section card, in addition to the
  ordinary preset dropdown — satisfying the "impossible to confuse with another preset" requirement.
- **Dedicated pop-out tracker**: a second, independently toggleable window (`GiveawaysTrackerWindow`, mirroring
  Bingo's `BingoCalledNumbersWindow` precedent) renders the exact same tracker content as the embedded panel, reading
  the same `GiveawayService` state — not a competing implementation.

## 4. Architecture / State Authority Model

Per `NEW_MODULE_GUIDE.md` §34a: **Giveaways' local per-venue config is the only state it owns, full stop.** There
is no backend, no shared-service state beyond the already-shared `SchedulerService`/`ChatCommandService`, and no
in-game state read beyond the local player's own identity (self-roll resolution) and structured chat payloads
(other-player roll identity). Everything else — the running preset snapshot, current phase, roll board, countdown —
is deliberately ephemeral live-operation state, reset on every venue switch/disable/reload and never persisted.

Files:

```
src/VenueOS.Modules.Operations/Giveaways/
  GiveawayModels.cs        — preset/settings records, special-number parsing, preset validation, overlap-risk check
  GiveawayRollBoard.cs     — pure roll comparison/leaderboard/tie/special-number engine
  RandomRollParser.cs      — pure text recognition for /random and /random 999
  GiveawayService.cs       — orchestrator: persistence, preset CRUD, timeline state machine, IVenueModule wrapper

src/VenueOS.Plugin/Giveaways/
  GiveawaysOperatorPanel.cs     — Draw()/DrawSettings(), the preset editor, live controls, tracker rendering
  GiveawaysTrackerWindow.cs     — the auxiliary detached tracker-only window
  GiveawaysRollChatAdapter.cs   — the Dalamud chat-event → ObservedRandomRoll boundary
```

No file was added to `Operations.cs` or `NativeOperationsPanels.cs`.

## 5. Preset Model

`GiveawayPreset(Id, Name, Channel, DelayBetweenLinesSeconds, GiveawayDurationSeconds, StartBlock, MidpointBlock,
ClosingBlock, WinnerMode, ClosestTargetNumber, AllowedRollsPerPerson, SpecialNumbersRaw)` — every field from the
task spec is present. `GiveawayAnnouncementBlock(IReadOnlyList<string> Lines)` caps each block at 10 lines
(`MaxLines`) and exposes `NonEmptyLines` (blank lines are never sent). `GiveawaySpecialNumbers.Parse` turns the
comma-separated field into a deduplicated `IReadOnlyList<int>`. `GiveawayPresetValidator.Validate` checks delay ≥ 1s,
duration ≥ 2s, non-negative roll allowance, and a non-negative Closest target. `GiveawayPreset.HasMidpointOverlapRisk`
is the pure "would Midpoint still be sending when Closing is due" warning check surfaced in Settings.

`GiveawaySettings(IReadOnlyList<GiveawayPreset> Presets, Guid? ActivePresetId)` is the entire per-venue payload,
persisted via `VenueProfileService.GetModuleConfig`/`SaveModuleConfig` under `events.giveaways`, schema version 1.

## 6. Settings vs. Live Split

- **Settings → Modules → Giveaways** (`DrawSettings`): the preset browser (list, Create/Rename/Delete), and the full
  editor for the selected preset — channel, delay, duration, winner rules, special numbers, and all three
  announcement blocks. This is the *only* place announcement text is authored.
- **Live Giveaways module** (`Draw`): the prominent active-preset display, the preset selector (locked while a
  giveaway is running), Start/Cancel/Clear Results, phase/status, time remaining, and the roll tracker. No
  announcement-text editing exists here at all.

## 7. Announcement Sequencing

Each block's non-empty lines are sent in order through `ChatCommandService.Enqueue` (`/shout` or `/yell`, per the
preset's channel), with `SchedulerService.Schedule` driving the delay between lines — the same composition
`TournamentCalloutService` already uses for its own two-line callouts. The whole per-line chain is threaded through
one `CancellationToken`, owned by the current run.

**Revised timeline (§1a correction — current, authoritative behavior):**

1. Start block sends; when its final non-empty line is enqueued, the giveaway timer (`T=0`) starts and roll
   acceptance opens.
2. At `T = Duration / 2`, Midpoint begins sending. Roll acceptance stays open.
3. At `T = Duration`, **Closing begins sending**. Roll acceptance **stays open** — the fixed-duration countdown
   itself is over (`GiveawayService.TimeRemaining` returns `null` from this instant), but this no longer closes the
   roll window by itself.
4. If Closing has no non-empty lines, roll acceptance closes immediately at this same instant, and the giveaway
   becomes Complete.
5. If Closing has lines, roll acceptance stays open through every line, including between lines, closing only the
   instant the **final non-empty Closing line is enqueued** — at which point the giveaway becomes Complete.
6. The existing no-interleaving rule (Closing waits for an in-flight Midpoint block to finish before starting, never
   overlapping) is unchanged; roll acceptance stays open through that entire wait too.
7. `Cancel()` still closes roll acceptance immediately regardless of phase, and prevents any remaining scheduled
   Closing lines from firing — unchanged by this correction.

## 8. Scheduler/ChatCommandService Reuse

No competing queue or timer was built. `GiveawayService` takes `SchedulerService`/`ChatCommandService` exactly like
every other timed-announcement module, ticked by the plugin's existing shared `Framework.Update` loop
(`scheduler.Tick()`/`chat.TickAsync()`) — no module-local `Timer`/`Task.Delay`.

## 9. Random-Chat Research Findings

The current Dalamud chat subscription convention in this codebase is `IChatGui.ChatMessage += handler`
(`IHandleableChatMessage message`), exactly as used by `BingoRollChatAdapter`/`BingoPayoutAutomationService`. That
existing adapter is the direct precedent for this module's own `/random` handling: no `XivChatType` filter is
applied (a real, documented live regression Bingo's own doc comment records from an earlier over-filtered version),
and identity for the local player comes from `IObjectTable.LocalPlayer`, never `IClientState`.

**Deathroll Helper**: no local copy of this reference plugin's source was available in this environment (it is not
one of this repository's read-only donor checkouts, and no network fetch of third-party plugin source was
performed). Per the task's own instruction ("use it as a behavioral/reference source only unless explicit
modification authorization is given"), and given Bingo's own adapter already documents a proven, live-verified
`/random`-family parsing/identity approach for this exact game message family, that in-repo precedent was used as
the primary reference instead. **This is a documented gap, not a silent substitution** — if Deathroll Helper's
actual source becomes available, it should be cross-checked against the approach below before this module's chat
adapter is treated as fully vetted.

## 10. Structured Dalamud Identity Extraction Approach

- **Self** ("You Roll a N…"): resolved directly from `IObjectTable.LocalPlayer` — `Name.TextValue`,
  `HomeWorld.ValueNullable?.Name.ExtractText()` falling back to `CurrentWorld`. Never a participant literally named
  "You".
- **Other player**: the message's own `SeString` (`message.Message.Payloads`) is scanned for an embedded
  `PlayerPayload` — exactly the structured Name+World Dalamud attaches to a player's name segment, unaffected by
  Chat2's purely-visual cross-world rendering (the flower-icon substitution the user described). When present, it
  supplies both Name and World directly. When absent, the parser's plain-text name capture is used as a fallback,
  and HomeWorld is left `""` (unresolved, never guessed — see §13).

This is the hybrid parser the task asked for: structured payload for identity, text recognition for the roll
value/type.

## 11. Standard `/random` Parsing

`RandomRollParser.TryParse` recognizes `"Random! You Roll a N."` (self) and `"Random! <name> rolls a N."` (other)
via two dedicated regexes, distinguishing self/other purely by the game's own literal verb form ("You Roll" vs.
"rolls") — a real FFXIV character name can never literally be "You" (names are always a two-word First-Last pair).

## 12. `/random 999` Parsing

The same two regexes optionally capture a trailing `"(out of N)"` group. Presence of that suffix — never the
resulting roll value — is what marks a result as `GiveawayRollKind.RangedOutOf` vs. `Standard`; both kinds are
accepted and count identically toward the giveaway (spec §15), with no separate leaderboard.

## 13. GuestIdentity Handling

`GuestIdentity(Name, HomeWorld)` is used throughout. An unresolved HomeWorld (no `PlayerPayload` found) is
represented as `""`, never invented — and `GuestIdentity.Key` (`"NAME@WORLD"`) means a `""`-world record can never
silently collide with a positively-resolved `Name+World` record of the same name (tested explicitly —
`An_unresolved_home_world_never_silently_merges_with_a_known_world_record`).

## 14. Per-Person Roll-Limit Semantics

`GiveawayRollBoard.Accept` enforces the exact rule table from the spec: `1` (default) accepts the first roll only;
a positive `N` accepts up to `N` rolls, comparing all of them and keeping the best; `0` accepts unlimited rolls.
`TotalRolls` increments only for genuinely accepted rolls — a rejected over-limit roll never increments it. Exactly
one row is ever displayed per participant, holding their best accepted result.

## 15. Highest/Lowest/Closest Comparison

`GiveawayRollBoard.IsBetter`/`SortKey` implement the three modes with a single ascending sort key
(`-value`/`value`/`abs(value-target)` respectively) so leader detection and sorting are uniform across all three
modes.

## 16. Tie Behavior

`GetLeaderboard` marks every row whose sort key equals the best sort key as `IsLeader = true` — a tie is always
multiple `IsLeader` rows, never an arbitrary single pick by arrival order or any other hidden tiebreak.

## 17. Special-Number Behavior

`GiveawayRollBoard.SpecialNumbersActive` is `true` only when `AllowedRollsPerPerson == 1`. A matching accepted roll
is flagged `IsSpecialHit`, shown with a distinct "SPECIAL" badge separate from the "LEADER" badge (both can appear
on the same row without one hiding the other). With multiple/unlimited rolls allowed, special-number highlighting
is inactive entirely (never computed), so re-rolling for the special prize cannot be gamed.

## 18. Live Tracker Sorting/Highlighting

The roll table (Player/World/Roll[/Distance for Closest]) is rendered pre-sorted by the leaderboard's own order
(best first), with leader rows in the theme's `Success` color and a "LEADER" badge, and special hits carrying an
additional "SPECIAL" badge — both via `UiKit.StatusBadge`, no hard-coded colors.

## 19. Cancellation / Venue-Switch Behavior

Every phase transition is guarded by a per-run `Guid currentRunId` (not preset-value comparison, which would be
unsafe — see the code comment in `GiveawayService` explaining why record equality can't be used for this), in
addition to the `CancellationToken` that already stops `SchedulerService` from ever invoking a cancelled run's
scheduled callback. `Cancel()`/venue switch (`Load`) both call the same `CancelRun()` — cancel-and-recreate the
token, every time. A dedicated test (`A_stale_run_cancelled_mid_start_block_cannot_leak_a_scheduled_send_into_the_next_run`)
proves a cancelled run's pending scheduled line genuinely never fires into a subsequent run.

## 20. Persistence

All preset fields persist through `VenueProfileService.GetModuleConfig`/`SaveModuleConfig`, verified across an
actual serialize/deserialize boundary (`Every_preset_field_survives_a_full_serialize_deserialize_boundary` —
JSON round-trips the whole `VenueStoreSnapshot`, then reconstructs a brand-new `VenueProfileService`/`GiveawayService`
from it), not merely a window close/reopen. Every mutating panel action goes through a `GiveawayService` method
that itself calls `SaveModuleConfig` — no ImGui field mutates `Settings` directly.

## 21. Files Changed

**New:**
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayModels.cs`
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayRollBoard.cs`
- `src/VenueOS.Modules.Operations/Giveaways/RandomRollParser.cs`
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayService.cs`
- `src/VenueOS.Plugin/Giveaways/GiveawaysOperatorPanel.cs`
- `src/VenueOS.Plugin/Giveaways/GiveawaysTrackerWindow.cs`
- `src/VenueOS.Plugin/Giveaways/GiveawaysRollChatAdapter.cs`
- `tests/VenueOS.Services.Tests/GiveawayModelsTests.cs`
- `tests/VenueOS.Services.Tests/GiveawayRollBoardTests.cs`
- `tests/VenueOS.Services.Tests/GiveawayServiceTests.cs`
- `tests/VenueOS.Services.Tests/RandomRollParserTests.cs`
- `docs/GIVEAWAYS_IMPLEMENTATION.md` (this file)

**Modified (minimal, additive only):**
- `src/VenueOS.Plugin/Plugin.cs` — construct `GiveawayService`/`GiveawaysOperatorPanel`, register `GiveawaysModule`,
  wire `GiveawaysRollChatAdapter`, draw the tracker window, unsubscribe on dispose.
- `src/VenueOS.Plugin/Shell/Icons.cs` — added the `gift` icon case + `DrawGift`.

No other file was touched. No pre-existing Brackets/Raffle/Block Letters/documentation work in progress was
modified or disturbed.

**§1a correction pass — files touched:**
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayService.cs` — the roll-window timing rewrite (§1a/§9).
- `src/VenueOS.Plugin/Giveaways/GiveawaysOperatorPanel.cs` — Closing-phase status text/badge color updated to
  reflect that rolls stay open.
- `tests/VenueOS.Services.Tests/GiveawayServiceTests.cs` — timeline tests updated/added per §1a.
- `docs/GIVEAWAYS_IMPLEMENTATION.md` — this file, revised in place.

No other file was touched in this pass. In particular, the Macro module and its files (added concurrently by
another session between the initial Giveaways build and this correction) were left untouched.

**§1b Live QA Fix #2 — files touched:**
- `src/VenueOS.Modules.Operations/Giveaways/RandomRollParser.cs` — `SelfPattern` regex fix (Issue 2).
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayService.cs` — added `SaveNewPreset` (Issue 1).
- `src/VenueOS.Plugin/Giveaways/GiveawaysOperatorPanel.cs` — Settings page reduced to the compact preset list +
  Edit/Delete per row + "+ New Preset"; the old in-page editor methods and the two colliding `TextInputModal`
  fields removed; roll-visibility caption added to the tracker (Issue 1 / roll-visibility note).
- `src/VenueOS.Plugin/Giveaways/GiveawayPresetEditorModal.cs` — **new file**, the dedicated preset editor modal
  (Issue 1).
- `tests/VenueOS.Services.Tests/GiveawayServiceTests.cs` — added preset-modal-workflow and self-roll tests.
- `tests/VenueOS.Services.Tests/RandomRollParserTests.cs` — added the case-insensitivity regression tests.
- `docs/GIVEAWAYS_IMPLEMENTATION.md` — this file, revised in place.

No other file was touched in this pass. Macro was not touched.

## 22. Tests Added

78 tests across 4 files after the §1a pass, extended to 98 by this §1b pass:

- `RandomRollParserTests.cs` (9 tests) — the exact user-captured fixtures (self/other, standard/999), malformed-text
  rejection, and the "same value, different kind" non-inference check.
- `GiveawayModelsTests.cs` (12 tests) — special-number parsing, preset defaults, blank-line filtering, validator
  rules, overlap-risk detection.
- `GiveawayRollBoardTests.cs` (23 tests) — roll limits (1/N/0), Highest/Lowest/Closest comparison and leader
  sorting, exact-distance-zero, ties, identity (same name/different world; ambiguous-world non-merging), special
  numbers under all three roll-allowance regimes, one-row-per-person.
- `GiveawayServiceTests.cs` (34 tests) — preset CRUD + full serialize/deserialize persistence, per-venue isolation,
  running-snapshot immutability against later edits, delete-while-running guard, the full announcement timeline
  (ordering, blank-line skip, empty-block skip, midpoint-vs-closing overlap non-interleaving), cancellation and
  stale-timer/venue-switch safety, roll-capture gating, and the module descriptor/lifecycle.

**§1a correction — test changes**: the original `Roll_acceptance_stops_exactly_at_the_full_duration_before_closing_sends`
and `Closing_begins_at_the_end_of_the_duration` tests (which asserted the now-superseded "closes at raw duration"
behavior) were removed/superseded. Replacing and extending them:

- `Duration_expiry_starts_closing_but_does_not_close_roll_acceptance_when_closing_has_lines` — Closing begins at the
  full duration, but `IsAcceptingRolls` stays true and `TimeRemaining` goes null.
- `Roll_acceptance_remains_open_through_every_closing_line_and_closes_only_after_the_last_one` — a single test
  driving three Closing lines, asserting an accepted roll after duration expiry, an accepted roll between Closing
  lines, a rejected roll immediately after the final line, and that both accepted Closing-phase rolls landed
  normally on Total Rolls/the leaderboard.
- `Empty_closing_block_closes_roll_acceptance_immediately_at_duration_expiry` — the empty-Closing-block case closes
  immediately, no meaningless wait.
- `Multi_line_closing_respects_the_configured_delay_between_lines` — Closing's own per-line delay is unaffected by
  this correction.
- `Overlapping_blocks_never_interleave_closing_waits_for_midpoint_to_finish` — updated: roll acceptance now stays
  open (previously asserted closed) while Closing waits for an in-flight Midpoint block to finish; still closes the
  instant Closing's single line is enqueued once Midpoint finishes.
- `Cancel_during_closing_closes_roll_acceptance_immediately_and_prevents_later_closing_lines` — Cancel semantics
  during Closing are unchanged: immediate close, no further Closing lines fire.

**§1b Live QA Fix #2 — tests added:**

- `RandomRollParserTests.cs` (+2): `Self_standard_random_is_parsed_case_insensitively_lowercase_form` and
  `Self_random_999_is_parsed_case_insensitively_lowercase_form` — the exact regression tests for the self-roll
  parsing fix (Issue 2).
- `GiveawayServiceTests.cs` (+18): preset-editor-modal workflow —
  `Building_a_draft_in_memory_never_touches_persisted_presets`,
  `Canceling_new_never_calls_into_the_service_and_leaves_presets_unchanged`,
  `Saving_new_creates_exactly_one_preset`, `Saving_new_persists_every_configured_field`,
  `Editing_starts_from_a_value_copy_that_can_never_mutate_the_persisted_record`,
  `Editing_draft_fields_does_not_mutate_persisted_state_before_save`,
  `Canceling_edit_leaves_the_persisted_preset_completely_unchanged`,
  `Saving_edit_updates_the_intended_preset_preserves_its_id_and_creates_no_duplicate`; and self-roll integration —
  `Self_roll_is_accepted_and_resolves_to_the_actual_local_character_name_and_home_world`,
  `Self_random_999_roll_is_accepted`, `Self_roll_increments_total_rolls`,
  `Self_participant_obeys_allowed_rolls_per_person`, `Self_participant_wins_under_highest/lowest/closest` (3),
  `Self_participant_can_roll_during_closing`, `Host_and_cross_world_participant_remain_two_distinct_leaderboard_rows`,
  `Self_roll_outside_the_acceptance_window_is_still_ignored`.

These prove every SERVICE-level building block the modal/adapter drive; the modal itself (ImGui) and
`GiveawaysRollChatAdapter.ResolveSelf`'s actual `IObjectTable.LocalPlayer` read remain outside this repository's
test boundary (NEW_MODULE_GUIDE.md §30 — no test project exists for `VenueOS.Plugin`) and still require the live
retest below.

## 23. Final Test Count

Before this session's `dotnet test` run, `VenueOS.Core.Tests`/`VenueOS.Venues.Tests`/`VenueOS.Services.Tests`
already contained the in-progress Brackets/Raffle/Block Letters/Macro work's own tests (from the repository's
current, uncommitted working tree — a Macro module was added concurrently by another session; it was left
untouched throughout, per this task's "small, targeted" scope each time). The full suite after this §1b pass:

```
VenueOS.Core.Tests:     4 passed
VenueOS.Venues.Tests:  23 passed
VenueOS.Services.Tests: 823 passed
-----------------------------------
Total:                 850 passed, 0 failed, 0 skipped
```

(Of the 823 in `VenueOS.Services.Tests`, 98 are the four Giveaways files above — 78 after §1a, +20 in this §1b
pass.)

## 24. Debug Result

```
dotnet build VenueOS.sln -c Debug
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 25. Release Result

```
dotnet build VenueOS.sln -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 26. Known Limitations

- **Deathroll Helper was not available to inspect** in this environment (§9) — Bingo's own already-live-verified
  `/random` adapter was used as the reference instead. If Deathroll Helper's source becomes available later, cross-
  check it against `RandomRollParser`/`GiveawaysRollChatAdapter` before treating the chat parsing as fully vetted.
- **Not live-verified**: whether a same-world player's Random-result name segment reliably carries a `PlayerPayload`
  the same way a cross-world one does; Chat2's exact effect (if any) on the underlying `SeString` payload structure
  versus only its rendering; and whether the self/other text-shape distinction holds for every game client language
  (the fixtures are all English). The plain-text fallback (unresolved `""` HomeWorld) exists specifically so an
  incorrect assumption here degrades safely rather than mis-attributing identity.
- No export/CSV/web leaderboard exists, per the task's explicit "keep it local" instruction.
- **(§1b)** `GiveawayPresetEditorModal` (ImGui) and `GiveawaysRollChatAdapter.ResolveSelf`'s actual
  `IObjectTable.LocalPlayer` read remain outside this repository's automated-test boundary (NEW_MODULE_GUIDE.md
  §30) — the modal's Save/Cancel/X-close behavior and the self-roll fix's real-game effect both still require the
  live retest below; only the service-level building blocks underneath them are unit-tested.
- **(§1b)** The exact real-world casing of FFXIV's self-roll message text is still not independently confirmed —
  the fix makes matching case-insensitive specifically so the exact case no longer matters, rather than betting on
  one assumed casing a second time.

## 27. Exact Live QA Checklist

**Presets — §1b: preset editor modal**
1. Enable Giveaways; open Settings → Modules → Giveaways; confirm the preset list stays compact (no full editor
   inline).
2. Click **+ New Preset**; confirm exactly ONE editor modal appears (not two).
3. Configure Name, Shout/Yell, delay, duration, winner mode, allowed rolls, special numbers, and all three blocks
   inside the modal.
4. Click **Cancel**; confirm no preset was created (list unchanged).
5. Click **+ New Preset** again, configure it, click **Save**; confirm the modal closes and the preset now appears
   in the list.
6. Click **Edit** on an existing preset; change something, click **Cancel**; confirm the change was discarded.
7. Click **Edit** again, change something, click **Save**; confirm the change persists in the list.
8. Reload the plugin; confirm the saved preset (name, all fields, all block lines) survives.
9. Confirm **Delete** still shows its confirmation dialog and still refuses to delete a currently-running preset.
10. Confirm closing the modal via its title-bar X behaves like Cancel (nothing created/changed).

**Live Start**
11. Open Giveaways.
12. Confirm the active preset is large/bold/highlighted and unmistakable.
13. Start.
14. Verify Start announcements send in order.
15. Verify the countdown begins only after the final Start announcement.
16. Verify Midpoint announcements begin at halfway.
17. Verify Closing announcements begin at the end of the configured duration.
17a. **(§1a correction)** Verify roll acceptance does NOT stop when Closing begins — with a multi-line Closing
     block, roll while Closing's first line is visible in chat and confirm it's still accepted (check Total Rolls).
17b. Verify a roll made between two Closing lines is still accepted.
17c. Verify roll acceptance finally closes only once Closing's LAST line has been sent — attempt a roll immediately
     after and confirm it is NOT accepted (Total Rolls does not increment).
17d. With an empty Closing block configured, verify roll acceptance closes immediately when the duration expires
     (no lingering "still open" window).
17e. Verify the status text/phase during Closing reads as still accepting rolls (not "rolls closed") until the
     final line has actually gone out, and that Time Remaining stops counting once Closing begins rather than
     showing a stale/misleading countdown.

**Rolls**
18. Have the HOST use `/random`.
19. **(§1b Fix #2 — this is the exact bug that was just fixed)** Verify the host's own roll IS now registered, and
    resolves to the actual local character Name + HomeWorld, not "You" and not silently dropped.
20. Have another SAME-WORLD person roll; verify identity.
21. Have another CROSS-WORLD person roll if available; verify Name + HomeWorld despite Chat2's flower presentation
    (must still work — not regressed by the self-roll fix).
21a. Confirm the host and the cross-world/same-world participant appear as two distinct rows, not merged.
22. Use `/random 999`; verify it is accepted (both for the host and for another participant).
23. Verify one row per participant.
24. Verify repeated allowed rolls retain only the best result.
25. Verify Total Rolls increments correctly, including for the host's own roll.
26. Verify over-limit rolls are ignored, including the host's own over-limit roll.
26a. Confirm the roll-visibility note is visible near Total Rolls in the tracker.

**Winner modes**
27. Test Highest.
28. Test Lowest.
29. Test Closest.
30. Verify the current leader automatically moves to the top.
31. Verify tie behavior.
31a. Confirm the host can become leader like any other participant under each mode.

**Closing regression (§1a) — must not have been reverted by this pass**
31b. Roll once during Closing (host or another participant); confirm it is still accepted.

**Special**
32. With Allowed Rolls = 1, hit a configured special number and verify special styling.
33. With Allowed Rolls > 1, verify special-number behavior is ignored.
34. With unlimited rolls, verify special-number behavior is ignored.

**Cancel**
35. Start another test giveaway.
36. Cancel.
37. Verify future scheduled announcements do not fire.
38. Verify roll acceptance stops.
39. Verify captured results remain visible until cleared/new Start.

**Venue / windows**
40. Switch venue and verify presets/state do not leak.
41. Test embedded.
42. Test detached.
43. Test Auto Pop-Out.
44. Test the dedicated tracker pop-out window independently of the main panel.
45. Check all four themes (Dark, Light, Neon, Midnight).

## 28. Hardened-Guide Definition-of-Done Review

**Architecture** — own folder/files (not in `Operations.cs`/`NativeOperationsPanels.cs`); stable unique ID; display
name defined once; unique icon key; state authority documented (§4 above); registered in `Plugin.cs`; Home tile
generic; enable/disable generic. ✅

**Venue** — reads `venues.Current` for identity, no module-local Venue Name field; per-venue config via
`GetModuleConfig`/`SaveModuleConfig`; venue-switch isolation tested with two venues; no stale settings/operation
state/participants/timers cross venues (tested explicitly). ✅

**Settings** — `DrawSettings()` shows only persistent configuration, distinct from `Draw()`'s live operation; every
editable field saves immediately; no credentials exist in this module (n/a); schema version set (1); no discard-on-
recovery issue introduced. ✅

**Lifecycle** — explicit per-run `CancellationTokenSource` + run-id guard; enable/disable preserves config;
venue-switch reload/reset verified; disposal cancels the active run; stale async/scheduled callbacks guarded
(tested). No realtime channel exists (n/a). ✅

**UI** — `UiKit`/`Forms` used throughout, no raw ImGui controls; only semantic theme tokens; `ConfirmDialog` used
for Cancel/Delete with consequence-stating text; empty/status/warning/error states handled; `DangerButton` for
Cancel/Delete; embedded via `AppFrame.DrawModule` (generic); detached via `ModuleWindowManager` (generic); Auto
Pop-Out is fully generic (no module-specific branching was added anywhere). Visual Definition of Done items
(resizing, ID-collision-free line editors, distinct Settings/Live content, etc.) were addressed in code — **actual
rendered-in-Dalamud verification is Live QA, not something this session can certify** (see §29/§48 note below).

**Security** — no credentials exist in this module. N/A.

**Guest Identity** — `GuestIdentity(Name, HomeWorld)` used throughout; no target-capture need (rolls come from
chat, not targeting); ambiguous/legacy Name-only identity is an explicit, tested, documented case (`""` HomeWorld),
never silently merged with a resolved one.

**Data** — persistence verified across an actual serialize/deserialize boundary; no import/export (n/a, per spec);
no archive/reset concept for this module beyond Cancel/Clear Results, which are named and tested distinctly.

**Testing** — pure logic covered; per-venue isolation tested; error paths tested (invalid preset, over-limit rolls,
pre/post-window rolls); no realtime/backend client (n/a); Debug and Release builds clean, 0 warnings.

**Live QA / Promotion** — not performed by this session, and `UnderDevelopment`/`IsEnabled` were left exactly as
required (true/false) — no self-promotion.

## 29. Git Status

At the start of this session, the working tree already carried substantial uncommitted work for Brackets, Raffle,
Block Letters, and New Module Guide documentation. This session touched **only**:

- Added: everything under `src/VenueOS.Modules.Operations/Giveaways/`, `src/VenueOS.Plugin/Giveaways/`, the four new
  `tests/VenueOS.Services.Tests/Giveaway*.cs`/`RandomRollParserTests.cs` files, and this report.
- Modified: `src/VenueOS.Plugin/Plugin.cs` (additive composition-root wiring only) and
  `src/VenueOS.Plugin/Shell/Icons.cs` (one new icon case).

No other file was read-modified, and no unrelated in-progress work was altered.

## 30. Confirmation

Nothing was staged, committed, pushed, tagged, branched, reset, cleaned, or released during this session. Only
read-only git commands (`status`, nothing destructive) were run.
