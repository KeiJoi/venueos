# VenueOS 0.3.2 release

VenueOS uses semantic versioning. `0.1.0` was the first pre-1.0 operational release; breaking persistence or protocol changes require a documented migration and a minor-version increase until 1.0.

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
