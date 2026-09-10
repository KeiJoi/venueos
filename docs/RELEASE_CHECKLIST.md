# VenueOS release checklist

Checklist for the first public VenueOS release (Dalamud Experimental Plugin Repository). Work through it top to
bottom before authorizing a public push/tag/release; nothing here authorizes pushing or publishing by itself.

> **0.2.0 update:** Bingo completed live QA and is now release-ready (enabled by default, no longer "Under
> Development" — see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` for the one remaining caveat, automated payout).
> The module readiness matrix below is updated to match; the rest of this document is otherwise the historical
> record of the 0.1.0 pass. See `RELEASE.md`'s "0.2.0 highlights" for the full list of what changed.

> **0.3.0 update:** Raffle and Brackets (formerly TournamentControl) completed reconstruction and live QA and are
> now release-ready (enabled by default, no longer "Under Development"); Block Letters, Giveaways, and Macro are
> three new production modules added in this release. **Every module VenueOS ships is now release-ready and
> enabled by default** — the module readiness matrix below is historical (it reflects the 0.2.0 state) and is not
> updated line-by-line for this release; see `docs/RELEASE_PREPARATION.md` for the current, authoritative
> per-module status and `RELEASE.md`'s "0.3.0 highlights" for what changed.

> **0.3.1 update:** a maintenance release — three targeted functional fixes (Mair's Trivia token refresh, Macro
> Live tile → faux hotbar drag/drop), a new short-link workflow for Raffle, a central login/session presentation
> gate, and a dialog/editor chrome-consistency pass. No module's production/enabled status changed; the module
> readiness matrix below remains historical. See `RELEASE.md`'s "0.3.1 maintenance release" section for the full
> summary and `docs/UI_QUALITY_AUDIT.md`/`docs/MACRO_IMPLEMENTATION.md`/`docs/MAIRS_TRIVIA_TOKEN_FIX.md`/
> `docs/RAFFLE_RECONSTRUCTION.md`/`docs/SESSION_PRESENTATION_GATE.md` for the detailed engineering reports.

## Module readiness matrix

| Module | Stable ID | Display Name | Default enabled (fresh install) | Shown in Applications when disabled | Settings entry | Release status | Live tested? | Notes |
|---|---|---|---|---|---|---|---|---|
| Attendance | `core.attendance` | Attendance | Yes | — | Yes | Working | Yes | |
| Greeter | `core.greeter` | Greeter | Yes | — | Yes | Working | Yes | Depends on `core.attendance` |
| VIP | `core.vip` | VIP | Yes | — | Yes | Working | Yes | Depends on `core.greeter` |
| ShoutRunner | `communication.announcements` | ShoutRunner | Yes | — | Yes | Working | Yes | |
| Party Finder | `promotion.partyfinder` | Party Finder | Yes | — | Yes | Working | Yes | |
| Mair's Trivia | `games.trivia` | Mair's Trivia | Yes | — | Yes | Working | Yes | |
| Mair's Editor | `games.mairseditor` | Mair's Editor | Yes | — | Yes | Working | Yes | |
| Bingo | `games.bingo` | Bingo | Yes | — | Yes | Working (0.2.0) | Yes | Automated payout specifically remains experimental (`BINGO_PAYOUT_AUTOMATION_DEFERRED.md`); manual reconciliation supported |
| Raffle | `games.raffle` | Raffle | **No** | **No** | Yes, labeled "Under Development" | Unfinished | No | Backend-compatible, not QA'd |
| TournamentControl | `games.tournament` | TournamentControl | **No** | **No** | Yes, labeled "Under Development" | Unfinished | No | Backend-compatible, not QA'd |

A module never shown in Applications is still fully reachable from Settings → Modules → Configure, and enabling
it there both shows its Home tile and persists that choice (`GlobalSettingsService.SetModuleEnabled`) — the
disabled default only applies the first time a fresh install has never touched that module's toggle.

## Module display order

**Before this pass:** implicit only — `ModuleHost.Modules` sorted purely by `Descriptor.Id` (ordinal string
sort), and both Home (`HomeScreen.DrawGrid`) and Settings → Modules (`ModulesSettingsPage.DrawList`) already
happened to iterate that same property, so the two surfaces could never diverge — there was just no explicit
place to change the order other than renaming a module's stable ID (which is also its persistence key, so
never do that casually).

**After this pass:** `ModuleDescriptor.DisplayOrder` (`src/VenueOS.Core/Modules.cs`, default `0`) is the one
authoritative place to change display order — `ModuleHost.Modules` now sorts by `(DisplayOrder, Id)`. Both Home
(`HomeScreen.DrawGrid`) and Settings → Modules (`ModulesSettingsPage.DrawList`) iterate that same property, so
there is exactly one ordering to keep correct.

**Final order** (set explicitly, `DisplayOrder` 1–10 in registration order below — no more ties to break):

1. ShoutRunner — 2. Attendance — 3. Greeter — 4. VIP — 5. Party Finder — 6. Mair's Trivia — 7. Mair's Editor —
   8. Bingo — 9. Raffle — 10. TournamentControl.

This is what Settings → Modules shows in full. Fresh-install Home order (0.2.0: Raffle/TournamentControl hidden
while disabled; Bingo promoted to enabled-by-default) is the same sequence with those two removed: ShoutRunner,
Attendance, Greeter, VIP, Party Finder, Mair's Trivia, Mair's Editor, Bingo. Verified end to end with real
production module instances in `tests/VenueOS.Services.Tests/ModuleDisplayOrderTests.cs`.

**To change it later:** set `DisplayOrder` on the module's `ModuleDescriptor` construction (in
`src/VenueOS.Modules.Operations/Operations.cs` for the modules that live there, or the module's own file for
the rest) — no other file needs to change.

## Build / test

- [x] `dotnet build VenueOS.sln -c Debug` — 0 warnings, 0 errors.
- [x] `dotnet build VenueOS.sln -c Release` — 0 warnings, 0 errors.
- [x] `dotnet test VenueOS.sln` — 336/336 passing (up from a 321/321 baseline; 15 new tests cover the three
      unfinished modules' fresh-install defaults/Under Development flag, the module-enabled override
      persistence mechanism, the `DisplayOrder` sort mechanism, and (using real production module instances)
      the exact final ten-module order and the fresh-install Home subset).
- [x] Module default audit — see matrix above.
- [x] `repo.json` parses as valid JSON and its `AssemblyVersion` (`0.1.0.0`) matches the built
      `VenueOS.dll`'s actual assembly version and `VenueOS.json`'s `AssemblyVersion`.
- [x] Release ZIP staged and validated (`scripts/Package-Release.ps1` → `release/VenueOS-0.1.0.zip`, 21 files,
      ~4.4 MB) — see "Package contents" below.
- [x] No secrets found in the tracked tree (see repository hygiene notes in the release report).
- [ ] Clean `git status` before release — repository hygiene is addressed (`.gitignore` added) but nothing has
      been staged/committed yet; see the release report's recommended commit boundaries.
- [ ] Deliberate version/tag/release step — not performed in this pass (explicitly out of scope).

## Package contents (`release/VenueOS-0.1.0.zip`)

VenueOS's own assemblies (`VenueOS.dll` + the five `VenueOS.*.dll` project references), its manifest
(`VenueOS.json`) and `VenueOS.deps.json`, `ECommons.dll`, the ClosedXML/DocumentFormat.OpenXml/SixLabors.Fonts/
RBush/ExcelNumberFormat/System.IO.Packaging chain (attendance export), the Microsoft.Data.Sqlite/SQLitePCLRaw
chain (attendance database) plus only the `runtimes/win-x64/native/e_sqlite3.dll` native asset. No PDBs, no
test assemblies, no source, no other platforms' native libraries, no local/developer configuration.
`Dalamud.Bindings.ImGui`/`FFXIVClientStructs` are intentionally absent — VenueOS references them with
`Private="false"` because the Dalamud host itself provides them.

## ECommons unload fix

- [x] Root cause identified and documented — see `ECOMMONS_UNLOAD_FIX.md`.
- [x] `ECommons` moved from a hard-coded `HintPath` (`3.1.0.19`) to a real `PackageReference` pinned to
      `3.2.1.18` (past ECommons' own "Api15 update").
- [x] Confirmed the primary chat transport (`ECommons.Automation.Chat.ExecuteCommand` → native
      `RaptureShellModule` fallback) and every other ECommons call site still compile and are unchanged.
- [ ] **Live acceptance (manual, required):** load VenueOS in-game, exercise an ECommons-dependent feature,
      disable VenueOS from the Dalamud plugin installer, confirm neither the `IClientState.remove_TerritoryChanged`
      `MissingMethodException` nor the `ECommons.Automation.Callback` initializer/disposal exception appears in
      `/xllog`, then re-enable VenueOS and confirm modules still work.

## Manual acceptance — three test machines

- [ ] **Main PC** — continue loading the locally-built DLL directly for development; confirm the existing
      developer configuration (venues, module settings, enabled/disabled choices) is untouched by anything in
      this pass.
- [ ] **MiniPC** — switch from the locally-loaded DLL to the repo-installed package; confirm the existing
      VenueOS configuration (Venue Profiles, Attendance data, Greeter config, VIP records, Party Finder config,
      ShoutRunner settings, Mair's Trivia/Editor data, any already-toggled module enabled state) survives the
      switch unchanged.
- [ ] **Legion Go — fresh install** — install VenueOS from the experimental repository with no prior VenueOS
      configuration. Confirm: Raffle/Bingo/TournamentControl are absent from Applications; all seven working
      modules default enabled and appear; Settings → Modules lists all ten modules, with the three unfinished
      ones marked "Under Development"; the plugin icon displays correctly in the installer; a repo update check
      succeeds.

## Icon

- [x] `assets/VenueOS.svg` (source) and `assets/icon-512.png`/`assets/icon-64.png` (rendered via
      `scripts/render-icon.py`) created — a tablet/app-launcher glyph, no FFXIV/Square Enix trademarks, no
      Dalamud logo, vector/geometric, legible at 64px.
- [ ] Visual confirmation in the actual Dalamud plugin installer (requires the live repo.json/ZIP to be hosted).

## Documentation

- [x] `README.md` updated: what VenueOS is, working vs. under-development modules, experimental-repo install
      instructions, source link, disclaimer, issue-reporting location.
- [x] `RELEASE.md` updated with the actual packaging/versioning/publish workflow now that it exists.
- [x] This checklist.

## Repository hygiene

- [x] `.gitignore` added (build output, IDE state, test/coverage output, local databases, secrets/env files,
      NuGet caches, the generated `release/` staging output, OS junk, logs).
- [x] Secret scan performed — no committed or present secrets found (see release report).
- [ ] Nothing has been staged or committed in this pass — see the release report's recommended commit
      boundaries before the first real commit of `src/`/`tests/`/docs.

## Explicit non-goals of this pass

- Raffle, Bingo, and TournamentControl were **not** functionally reconstructed, fixed, or redesigned — only
  their default-enabled state, Applications visibility, and Settings labeling changed.
- No backend (Mair's Trivia, Bingo, Raffle, Tournament) was modified or redeployed.
- Nothing was pushed, no GitHub Release was created, no tag was pushed.
