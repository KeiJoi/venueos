# VenueOS 0.3.6 release

VenueOS uses semantic versioning. `0.1.0` was the first pre-1.0 operational release; breaking persistence or protocol changes require a documented migration and a minor-version increase until 1.0.

## 0.3.6 — Shouts Live Layout Hotfix

A targeted, presentation-only hotfix to 0.3.5's Shouts module — no other module's registration, behavior, or persistence changed, and Shouts' own execution/persistence/migration are untouched.

- **Shouts Live now wraps configured hotbar slots at a maximum of five per row.** Previously every visible slot rendered on a single, ever-widening row; a venue with more than five configured slots produced an unusably wide Live screen. Rows now wrap at 5, 10, and 15 visible slots as needed.
- **Unassigned slots remain hidden**, exactly as in 0.3.5 — a gap in the logical slot numbers never reserves a blank position in the grid.
- **Slot ordering, identity, and execution are unchanged.** Ascending logical slot order is preserved across row boundaries; a slot's persisted number, its assignment, and `SelectedSlot` are unaffected by which row it visually lands in; selecting and running a Shout behaves identically regardless of row.
- **Responsive/width-driven row sizing remains deferred** to a future UI-tightening pass — this hotfix uses a simple fixed maximum of 5 slots per row, not a computed column count.
- **Live-QA verified** and promoted alongside this release — see `docs/SHOUTS_IMPLEMENTATION.md` §12 for the full hotfix record.
- All 14 production modules remain enabled by default; no other module's registration or behavior changed in this release.

See `docs/SHOUTS_IMPLEMENTATION.md` for the full hotfix record.

## 0.3.5 — Shouts (generalized from DJ Shouts)

A targeted generalization pass over 0.3.4's DJ Shouts module — its name, slot capacity, and live-screen presentation changed; its accepted execution engine did not. No other module's registration, behavior, or persistence changed.

- **Renamed: DJ Shouts → Shouts.** The module is no longer framed as DJ-specific — it's a general-purpose, operator-triggered venue announcement tool. DJ introductions remain a perfectly good use, just not the only one; venue hype, event notices, reminders, requests, and closing messages are equally at home here. The internal module ID is unchanged (`communication.djshouts`) — see `docs/SHOUTS_IMPLEMENTATION.md` §2 for why a display-name rename doesn't require an ID/persistence-key change, the same pattern ShoutRunner already established.
- **Slot capacity expanded from 5 to 15.** Settings → Modules → Shouts → Shout Slot Assignments now exposes all 15 rows (Shout 1–Shout 15) at once, each independently assignable to a saved preset.
- **Live screen shows only configured slots.** Where DJ Shouts always rendered all five slot buttons, Shouts' live screen now renders only the slots that actually have a preset assigned, in their own numeric order (never renumbered) — assigning slots 1, 4, 7, and 12 shows exactly "Shout 1," "Shout 4," "Shout 7," "Shout 12," with no blank placeholders for the other eleven. Visibility updates immediately when Settings changes an assignment, with no restart required. If every slot is unassigned, the live screen shows a plain empty-state message instead of any slot controls, and the Shout button stays disabled.
- **Safe selection fallback.** Only a currently-configured slot can become selected; if the selected slot's assignment is cleared or its preset deleted, selection safely falls back to another configured slot, or clears if none remain — it can never get stuck on a slot the live screen no longer shows.
- **Existing 0.3.4 DJ Shouts data migrates automatically, with no user action required.** Saved presets, the original slots 1–5 assignments, and the Last Shout completion timestamp all carry over the first time each venue loads after upgrading; the new slots 6–15 simply start unassigned. Migration is a one-time, idempotent schema upgrade (v1 → v2) — see `docs/SHOUTS_IMPLEMENTATION.md` §4 for the exact mechanism and its test coverage.
- **Execution engine, pacing, and persistence unchanged.** The Yell/Shout dispatch model, the 2-second pacing between confirmed lines, dispatch-confirmation gating, the transactional preset editor, chat byte-limit validation, and the Last Shout timer's completion-only-on-final-line semantics are all identical to the accepted 0.3.4 baseline. Everything remains per-venue.
- **Live-QA verified** and promoted alongside this release — see `docs/SHOUTS_IMPLEMENTATION.md` for the full implementation and migration record.
- All 14 production modules remain enabled by default; no other module's registration or behavior changed in this release.

See `docs/SHOUTS_IMPLEMENTATION.md` for the full implementation record and `docs/DJ_SHOUTS_IMPLEMENTATION.md` for the original 0.3.4 DJ Shouts build/QA record this release generalizes.

## 0.3.4 — DJ Shouts

A new, greenfield production module — no other module's registration, behavior, or persistence changed.

- **DJ Shouts (new).** A simple, operator-triggered announcement utility for venue DJs: prepare reusable DJ Shout presets ahead of time in Settings, then fire the currently selected one manually during an event. Not automated, not ShoutRunner, and not a Greeter behavior replacement — the **DJ Shout** button is the only execution trigger.
- **Five assignable DJ preset slots (DJ 1–DJ 5).** Mirrors Greeter's five-slot hotbar concept, with its own independent per-venue configuration — selecting a slot never sends anything by itself.
- **Ordered, reusable presets with a per-line Yell/Shout channel.** Unlike Greeter (one destination for the whole preset) or Giveaways (one channel per block), every DJ Shout line has its own independent Yell/Shout selector, defaulting to Yell. Lines can be reordered, added, and removed in a dedicated transactional preset editor.
- **Manual execution with safe pacing.** Pressing DJ Shout sends every non-empty line in order, about two seconds apart — the same established pacing Greeter uses between its own lines — gated on each line actually being confirmed sent before the next one goes out.
- **Last DJ Shout elapsed timer.** Shown beside the DJ Shout button (`Never` / `00:42 ago` / `1h 12m ago`), it updates only once a run's final line is confirmed dispatched — never on button press, and never for a failed or cancelled run — and persists per Venue Profile across a panel close/reopen or a plugin reload.
- **Per-venue persistence throughout.** Saved presets, slot assignments, the selected slot, and the Last DJ Shout timestamp are all isolated per Venue Profile.
- **Live-QA verified** and promoted to production alongside this release — see `docs/DJ_SHOUTS_IMPLEMENTATION.md` for the full implementation record.
- All 14 production modules remain enabled by default; no other module's registration or behavior changed in this release.

## 0.3.3 — Giveaways: Announce Winner

A targeted feature addition to Giveaways, followed by a same-release QA correction — no other module's registration, behavior, or persistence changed.

- **Announce Winner panel.** The live Giveaways screen gained a new card, between Controls and the Roll Tracker: a Yell/Shout channel selector, an always-visible **Announce Winner** button, and a one-line announcement template.
- **`<name>` winner substitution, with grammatical multi-winner/tie handling.** The template's `<name>` placeholder is replaced with the current winner(s) — never their Home World — using normal English list grammar for ties: `Kei Joi` for one winner, `Kei Joi and Rabid Squirrel` for two, and an Oxford-comma list (`Kei Joi, Rabid Squirrel, and Mairwen Kor`) for three or more. A tie is never something the operator has to resolve by hand — every tied participant is announced automatically, in the order they rolled.
- **Persistent, per-preset configuration.** The Winner Announcement channel and template are saved as part of the Giveaway preset (default: Yell, `Congratulations <name>! You won the giveaway!`), authored in the same dedicated Preset Editor used for every other preset field.
- **Always-editable channel/template, with an active-run override.** The channel selector and template field can be edited at any time — including with no giveaway running. With no active giveaway, an edit saves directly to the selected preset (surviving preset switches, venue switches, and a plugin reload). While a giveaway is running or has just completed, an edit is a one-off touch-up for that specific announcement only and never overwrites the saved preset; **Clear Results** returns the field to the selected preset's saved values. Only the **Announce Winner button** itself is gated by giveaway state (Complete + at least one winner) — the field and selector are not.
- **Repeatable, chat-length-safe dispatch.** Announce Winner can be pressed more than once per giveaway. The fully-resolved message (after `<name>` is filled in) is validated against FFXIV's normal chat length limit before sending — VenueOS never truncates names, drops winners, or splits a long announcement into multiple lines; an over-length message is rejected with an on-screen message instead.
- **QA correction included in this release:** an initial live-test pass found the channel selector and template field were incorrectly locked whenever no giveaway was active, preventing the operator from preparing an announcement ahead of time. Fixed before this release shipped — see `docs/GIVEAWAYS_IMPLEMENTATION.md` §31 for the full before/after.
- All 13 production modules remain enabled by default; no other module's registration or behavior changed in this release.

See `docs/GIVEAWAYS_IMPLEMENTATION.md` for the full implementation record.

## 0.3.2 — Bingo automated payout live-verified

A three-phase targeted hotfix series took Bingo's automated payout from its first live-test failure through a
complete, successful live end-to-end payout, with no changes to module registration, backend accounting
architecture, or any other module's behavior:

- **Main-thread dispatch fix** — pinned-target verification (and every other Dalamud/FFXIVClientStructs touch in
  the payout engine) was being reached after an awaited HTTP call had already hopped off the framework thread,
  throwing `Not on main thread!`. Fixed by routing every such call through the existing `IFrameworkDispatcher`
  abstraction. See `docs/BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md`.
- **Gil-entry fix** — the engine's original gil-entry mechanism (a rendered-text button search plus a raw
  component-field write) never actually staged anything in the real Trade window. Replaced with the donor Bingo
  plugin's own proven-in-production mechanism (`ECommons.Automation.Callback.Fire`, the game's native
  addon-callback protocol), plus a new mandatory positive read-back of the staged amount before ever proceeding.
  See `docs/BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md`.
- **Ready/Confirm fix** — the same class of defect one stage later: the Trade window's Ready/Confirm control was
  also not discoverable by rendered text. Fixed using the donor's own proven fixed-node-index discovery, and the
  immediately-following SelectYesno confirmation step was hardened to verify the dialog's own prompt content
  (matching the game's localized Trade-confirmation string) before ever confirming it, so an unrelated Yes/No
  dialog can never be clicked. See `docs/BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md`.
- **Result:** a full live test subsequently paid out a real 6,500,000-gil obligation end to end — six confirmed
  1,000,000-gil chunks plus one correctly-derived 500,000-gil remainder, reconciled through the server-authoritative
  ledger to `paid: 6,500,000`, `outstanding: 0` — with an earlier `failed · 1,000,000` attempt correctly retained
  in transaction history without ever counting toward paid. See `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` for the
  consolidated current status.
- Throughout all three phases: ambiguous outcomes are still never treated as unpaid, never auto-retried, and still
  require manual reconciliation (`Mark Paid`/`Mark Not Paid`); the backend/server ledger remains the sole
  authoritative source for paid/outstanding — VenueOS never adopted the donor's own historical in-memory accounting
  approach. Existing historical `ambiguous` transaction records were left untouched.
- All 13 production modules remain enabled by default; no module's registration or behavior changed in this
  release beyond the Bingo payout engine fixes above.

## 0.3.1 maintenance release

A post-0.3.0 quality/maintenance pass — three targeted functional fixes, a central login/session presentation
gate, and a dialog/editor chrome-consistency pass across the whole plugin. No new modules, no persistence/protocol
changes, no module removed from production.

- **Mair's Trivia — fixed an intermittent "expired token" defect.** Root cause was a refresh-token race: the
  auto-reconnect-on-load path called the backend directly instead of going through the existing single-flight
  recovery guard, so an overlapping load-time refresh and a reactive 401 recovery could each send the backend's
  rotating refresh token and silently clobber each other's result. Also fixed a secondary defect where disabling
  Trivia, switching venues, then re-enabling it could resume with a previous venue's stale credentials (`ModuleHost`
  skips venue-changed notifications for a disabled module). See `docs/MAIRS_TRIVIA_TOKEN_FIX.md`.
- **Macro — fixed the Live tile → faux hotbar drag/drop defect, live-QA confirmed working.** Root cause was a
  `NullReferenceException` in the drag-payload handling (`ImGuiPayloadPtr` dereferences a null native pointer
  unless its `IsNull` property is checked first — the ordinary state on almost every frame with no drag in
  progress), not the cross-window hover interaction originally suspected. Also added a dedicated drag-handle strip
  so repositioning a hotbar and dropping a macro onto a slot no longer share screen space. See
  `docs/MACRO_IMPLEMENTATION.md`.
- **Raffle — short viewer/player links.** Publishing a raffle now also mints a short `.../l/<code>` link (mirroring
  Bingo's proven `short_links` pattern) that's practical to paste into FFXIV chat, with the full-length link still
  available via a "Show Full Links" toggle. The long host/viewer token remains the real, unchanged credential — the
  short code is purely a shareable alias for it. See `docs/RAFFLE_RECONSTRUCTION.md`.
- **Central character-session presentation gate.** No VenueOS-generated UI (main tablet, detached module windows,
  the Macro faux hotbars, Bingo's/Giveaways' auxiliary windows) renders while genuinely logged out — previously
  there was no such gate at all, and a persisted, enabled Macro hotbar could appear over the FFXIV title screen.
  One deliberate exception: an already-active ShoutRunner operation's own UI stays visible through a temporary
  world/Data Center travel transition. See `docs/SESSION_PRESENTATION_GATE.md`.
- **Dialog/editor chrome standardized across the whole plugin.** Every VenueOS-generated window and popup now uses
  consistent VenueOS chrome instead of ImGui's raw native title bar — Create/Edit Macro, the Giveaway Preset
  editor, Add/Edit VIP, and the Venue Switch Failed dialog all gained a shared themed header; the smaller
  Confirm/Text-Input dialogs had their redundant native title bar removed. Also fixed a live, unfixed
  modal-identity-collision bug (Mair's Editor's "New Set"/"Import As New" dialogs shared one popup identity, the
  same bug class already found once in Giveaways) by giving every `ConfirmDialog`/`TextInputModal` instance its own
  identity at the class level, closing the whole bug class rather than just the one instance. See
  `docs/UI_QUALITY_AUDIT.md`.
- All 13 production modules remain enabled by default; none were touched functionally beyond the fixes above.

## 0.3.0 highlights

- **All modules promoted to production.** Raffle and Brackets (formerly TournamentControl) completed reconstruction and live QA and are no longer "Under Development"; three new greenfield modules — Block Letters, Giveaways, and Macro — completed their own build/automated-test/live-QA passes and ship as production modules from their first release. Every module VenueOS ships is now enabled by default and appears on Home; `UnderDevelopment` remains available as generic infrastructure for a future module, but no current module uses it.
- **Raffle reconstruction** — organizer-key backend authentication, confirmed-redraw/previous-winner-exclusion semantics, crash-safe backend persistence, HomeWorld-aware participant identity, archive/delete/reset lifecycle, and a live realtime-mirrored browser wheel. See `docs/RAFFLE_RECONSTRUCTION.md`.
- **Brackets reconstruction** (module ID `games.tournament` unchanged) — fixed a backend bye-progression defect that broke odd-sized brackets (17 players, etc.), added realtime bracket sync, result correction with downstream-rollback confirmation, and organizer-initiated tournament deletion. See `BRACKETS_RECONSTRUCTION.md`.
- **Block Letters** (new) — compose FFXIV block-letter text within real per-destination character limits (Chat, Party Finder Comment, Macro Line), with cursor/selection-aware glyph insertion and actual in-game glyph rendering in the palette. See `docs/BLOCK_LETTERS_IMPLEMENTATION.md`.
- **Giveaways** (new) — timed venue giveaways with automated Shout/Yell announcements and an FFXIV `/random` roll tracker (Highest/Lowest/Closest winner modes, per-person roll limits, special numbers, cross-world identity). See `docs/GIVEAWAYS_IMPLEMENTATION.md`.
- **Macro** (new) — a persistent per-venue macro library authored in a dedicated multiline editor, a live tile launcher, up to four faux FFXIV-style hotbars, nested macro invocation with cycle protection, `/actionready` action-readiness waiting, and `/venueos macro "Name"`. One known non-blocking issue: dragging a macro tile from the Live launcher directly onto a faux hotbar slot is not yet reliable in the live ImGui runtime — assign hotbar slots from Settings → Modules → Macro → Hotbars instead. See `docs/MACRO_IMPLEMENTATION.md`.
- **User Manual updated** to cover every production module, including the workflows above and the Known Issues section.

See `docs/RELEASE_PREPARATION.md` for the full release-preparation record (tests, builds, packaging, files changed).

## 0.2.2 hotfix

Live QA on 0.2.1 found the built-in User Manual reader (now loading correctly) didn't word-wrap: long prose,
table cells, and list/blockquote text ran off the right edge of the pane instead of wrapping. Root cause: the
wrap decision read `ImGui.GetCursorPosX()` to track "how far along the current line is", but every ImGui item —
including the `Dummy` calls used to place each word — resets the cursor to the window's left margin on the next
line regardless of whether that item was itself placed via `SameLine()`. That made the wrap check compare "left
margin + one word's width" against the right edge, which is almost always false, so every word kept chaining
onto one ever-widening line via `SameLine()` (which uses ImGui's own internal previous-line tracking, unrelated
to `GetCursorPosX()`). Fixed by extracting a pure, ImGui-free word-wrap algorithm (`ManualTextLayout`) that
tracks line width itself instead of trusting ImGui cursor state — the renderer now just draws whatever line/piece
layout that produces. See `ManualTextLayout.cs`'s doc comment for the full reasoning.

## 0.2.1 hotfix

The built-in User Manual failed to load on a live installed plugin ("User Manual could not be loaded.") even
though `USER_MANUAL.md` was genuinely inside the 0.2.0 release ZIP. Root cause: `typeof(Plugin).Assembly.Location`
is not reliable for a Dalamud-installed plugin — Dalamud does not necessarily load the plugin assembly the way a
normal `Assembly.LoadFrom(path)` would, so that property can be empty or point somewhere other than the real
installed plugin folder. Fixed by resolving the manual's directory from
`IDalamudPluginInterface.AssemblyLocation` first (Dalamud's own officially-documented, tracked DLL path), with the
reflection-based path kept only as a last-resort fallback. See `UserManualLoader.cs`'s doc comment for the full
reasoning. No packaging change was needed — `USER_MANUAL.md` was already at the correct flat path in the ZIP.

## 0.2.0 highlights

- **Bingo promoted to release-ready** — completed live QA and now ships enabled by default alongside VenueOS's other working modules (module ID `games.bingo` unchanged). Automated payout specifically remains an experimental, conservatively-documented convenience feature — see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` and the User Manual's Bingo section.
- **Built-in offline User Manual** — a "User Manual" tile on Home (just before Settings) opens a VenueOS-styled reader for the bundled `docs/USER_MANUAL.md`, no internet connection required. `docs/USER_MANUAL.md` remains the single authoritative manual source; the release package just includes a copy of it.
- **ShoutRunner crash recovery** — an interrupted RUN (FFXIV/Dalamud/VenueOS exiting unexpectedly mid-route) can now be resumed from where it left off via a "Resume Run" control; live-tested against a forced FFXIV termination mid-transfer.
- **ShoutRunner same-Data-Center reliability fix** — corrected a false-positive failure when Lifestream performs an intermediate city visit before completing a same-Data-Center World Visit; live-tested (Halicarnassus → Cuchulainn, both Dynamis).
- **Attendance: Venue Area Type moved to the Live screen** — the Normal/Outdoor Event Area choice is now made per-opening, directly above "Start New Opening," instead of buried in Settings.
- **About panel now shows the real version** — replaced a stale hard-coded "Phase 4" label with the actual running assembly version.

## Package

Local development never needs packaging: build `src/VenueOS.Plugin/VenueOS.Plugin.csproj` (Debug or Release) and load the resulting DLL directly via Dalamud's dev-plugin loader, same as any other iteration.

To build the distributable package for the experimental repository, run `scripts/Package-Release.ps1` from the repository root. It builds `VenueOS.Plugin` in Release, stages only the runtime files VenueOS actually needs (its own assemblies, ECommons, the Sqlite/ClosedXML dependency chain, and the Windows x64 native Sqlite library — not the Debug/PDB output, not DalamudPackager's own default zip which bundles every platform's native Sqlite asset), and produces `release/VenueOS-<version>.zip`. `repo.json` at the repository root is the experimental-repository manifest; its `DownloadLinkInstall`/`DownloadLinkUpdate` point at a GitHub Release asset named to match that ZIP.

Publishing a new version is a deliberate act, never automatic on every commit:

1. Bump `<Version>`/`<AssemblyVersion>`/`<FileVersion>` in `src/VenueOS.Plugin/VenueOS.Plugin.csproj` if this is a new version (keep `VenueOS.json`'s `AssemblyVersion` and `repo.json`'s `AssemblyVersion`/download URLs in sync with it).
2. Run `scripts/Package-Release.ps1`.
3. Push the commit, create a GitHub Release for the matching tag, and upload `release/VenueOS-<version>.zip` as its asset.
4. Confirm `repo.json`'s download links resolve, then publish/update it wherever the experimental repository URL is hosted.

## Coexistence and migration

VenueOS never automatically imports, consumes, or overwrites standalone plugin configurations. Standalones remain independently usable. Any future import must be user-invoked, previewable, and copy-only.

## Deferred capabilities

- Any addon-memory automation not separately version-gated and in-game validated.

Bingo automated trade/payout operations are no longer deferred as of 0.3.2 — a full live end-to-end test (target verification, Trade UI interaction, actual gil transfer, multi-chunk payout, and server-ledger reconciliation) completed successfully; see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` for the current status and the three `BINGO_PAYOUT_*_HOTFIX.md` reports for what was fixed to get there.

ShoutRunner's own travel/World-Visit/teleport/Lifestream automation is no longer deferred as of 0.2.0 (crash recovery and the same-Data-Center reliability fix above were both live-tested). See `PARTY_FINDER_PHASE_2D.md` for Party Finder's own deferred items.

## First-release validation

Install the Release package with Dalamud's dev-plugin workflow, exercise `MANUAL_TEST_PLAN.md` on the exact game build, confirm venue isolation/reload persistence, then record observations in `OPERATOR_FEEDBACK.md` before public distribution.
