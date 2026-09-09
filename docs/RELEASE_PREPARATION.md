# VenueOS 0.3.0 — Release Preparation Report

**Date:** 2026-09-09
**Branch:** `main`
**Remote:** `origin` → `https://github.com/KeiJoi/venueos`

This report records the release-preparation pass that promoted every currently implemented VenueOS module out of
`UnderDevelopment`, brought the User Manual up to date with the current release, and validated/built/packaged the
result. It is a release-preparation record, not a new engineering pass — see `docs/RAFFLE_RECONSTRUCTION.md`,
`BRACKETS_RECONSTRUCTION.md`, `docs/GIVEAWAYS_IMPLEMENTATION.md`, `docs/MACRO_IMPLEMENTATION.md`, and
`docs/BLOCK_LETTERS_IMPLEMENTATION.md` for the actual engineering/QA work this pass promotes.

## Version

**0.3.0** (previous release: 0.2.2). No explicit "next version" marker existed in the repository, so this was
inferred from VenueOS's own established semantic-versioning pattern (documented in `RELEASE.md`): `0.1.0` → `0.2.0`
was a minor bump for a feature/module-status change (Bingo promoted, ShoutRunner crash recovery, built-in manual);
`0.2.0` → `0.2.1` → `0.2.2` were patch bumps for isolated bug fixes only. This release promotes five modules out of
`UnderDevelopment` (two previously-included-disabled modules plus three brand-new modules) and updates the User
Manual accordingly — squarely a minor-version-class change under the repository's own established pattern, not a
patch. `<Version>`/`<AssemblyVersion>`/`<FileVersion>` in `VenueOS.Plugin.csproj`, `VenueOS.json`'s
`AssemblyVersion`, and `repo.json`'s `AssemblyVersion`/download links were all updated to `0.3.0` together.

## Modules promoted out of UnderDevelopment

| Module | ID | Display Name | Was | Now |
|---|---|---|---|---|
| Raffle | `games.raffle` | Raffle | `UnderDevelopment: true`, disabled by default | `UnderDevelopment: false`, enabled by default |
| Brackets | `games.tournament` | Brackets | `UnderDevelopment: true`, disabled by default | `UnderDevelopment: false`, enabled by default |
| Block Letters | `tools.blockletters` | Block Letters | `UnderDevelopment: true`, disabled by default | `UnderDevelopment: false`, enabled by default |
| Giveaways | `events.giveaways` | Giveaways | `UnderDevelopment: true`, disabled by default | `UnderDevelopment: false`, enabled by default |
| Macro | `tools.macro` | Macro | `UnderDevelopment: true`, disabled by default | `UnderDevelopment: false`, enabled by default |

No module ID, display name, or persisted config schema/key changed — promotion only touched each module's
`ModuleDescriptor.UnderDevelopment` argument and its `IsEnabled` default. A user who previously explicitly disabled
Raffle or Brackets from Settings → Modules keeps that choice (module enabled/disabled state is a global override
recorded in `GlobalSettingsService`, independent of the module's own default — a fresh install, or an install that
never touched that toggle, is the only case the new default applies to).

**Full production module roster after this release** (all enabled by default, all out of `UnderDevelopment`):
`core.attendance` (Attendance), `core.greeter` (Greeter), `core.vip` (VIP), `communication.announcements`
(ShoutRunner), `promotion.partyfinder` (Party Finder), `games.bingo` (Bingo), `games.raffle` (Raffle),
`games.tournament` (Brackets), `games.trivia` (Mair's Trivia), `games.mairseditor` (Mair's Editor),
`events.giveaways` (Giveaways), `tools.blockletters` (Block Letters), `tools.macro` (Macro).

The `UnderDevelopment` infrastructure itself (the generic "Under Development" badge/banner in
`ModulesSettingsPage.cs`, and the `ModuleDescriptor.UnderDevelopment` field) was **not** removed — it remains
available, unused by any current module, for a future module to start in.

## User Manual updates

`docs/USER_MANUAL.md` (the single authoritative manual — also bundled into the release package and readable
in-plugin via the Home → User Manual tile) was updated to document every production module:

- Header, Quick Start, and Module Quick Reference table updated for 13 production modules (removed all "disabled
  by default"/"Under Development" language for Raffle/Brackets).
- **New sections added:** §13 Raffle, §14 Brackets, §15 Block Letters, §16 Giveaways, §17 Macro — written as
  operator-facing practical instructions (Settings vs. live operation, exact field names, workflows), not
  implementation reports.
- Existing sections §1–§12 (Installation through Bingo) were reviewed against current source and left unchanged
  where still accurate; only cross-reference anchor numbers shifted for the sections renumbered below.
- §13–§17 (Detached Windows, Persistence, Troubleshooting, Data/Privacy, Updates) renumbered to §18–§22 to make
  room for the five new module sections; their content was reviewed and extended (Persistence table, Troubleshooting
  entries) to cover the newly promoted modules.
- The old "Under Development Modules" section was replaced with **§23 Known Issues**, covering the two legitimate,
  still-open caveats below.

## Known non-blocking issues (documented in the User Manual, §23)

- **Macro: live tile → faux hotbar drag/drop is not reliable in the live ImGui runtime.** A `SetItemAllowOverlap()`
  fix was attempted and did not resolve it (see `docs/MACRO_IMPLEMENTATION.md`'s final "LIVE QA FIX" section).
  Per explicit instruction, no further fix attempt was made in this pass. The Settings → Modules → Macro → Hotbars
  assignment path (drag-and-drop or click-to-place) works correctly and is documented as the current method.
- **Bingo automated payout remains an experimental convenience feature**, not fully verified — pre-existing,
  unchanged caveat carried forward from the 0.2.0 release; manual reconciliation remains fully supported.

## Tests

`dotnet test VenueOS.sln -c Debug`:

```
VenueOS.Core.Tests:      4 passed
VenueOS.Venues.Tests:   23 passed
VenueOS.Services.Tests: 851 passed
--------------------------------------
Total:                  878 passed, 0 failed, 0 skipped
```

No test was skipped, disabled, or deleted without justification. Three test files were updated to invert their
assertions for the newly promoted modules (following the existing precedent `BingoReleaseStatusTests` set when
Bingo was promoted in 0.2.0):
- `tests/VenueOS.Services.Tests/UnfinishedModuleDefaultsTests.cs` was renamed to
  `RaffleAndBracketsReleaseStatusTests.cs` and its four assertions inverted (enabled by default, no longer flagged
  `UnderDevelopment`) for Raffle and Brackets.
- `GiveawayServiceTests.cs`, `MacroServiceTests.cs`, `BlockLettersServiceTests.cs` — each module's own
  "starts under development and disabled" test was replaced with a "promoted to production and enabled by default"
  test.
- `ModuleDisplayOrderTests.cs` — the fresh-install Home-order test was updated: since every module in its
  ten-module fixture is now enabled by default, the Home-visible order equals the full authoritative order: an
  `Assert.All(..., UnderDevelopment == false)` replaces the old "the two disabled modules are also flagged
  UnderDevelopment" assertion.

Total test count is unchanged (878 before and after) — these were assertion inversions/renames, not additions or
removals of coverage.

## Debug build

```
dotnet build VenueOS.sln -c Debug
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Release build

```
dotnet build VenueOS.sln -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Packaging / release artifact

`scripts/Package-Release.ps1 -Configuration Release` (the repository's existing, established packaging mechanism —
no new packaging system was introduced):

```
Release ZIP: C:\FFXIVplugs\venueos\release\VenueOS-0.3.0.zip (4.59 MB)
```

Contents verified: VenueOS's own five assemblies (`VenueOS.dll` + `VenueOS.Core/Venues/Services/Modules.Operations`),
`VenueOS.json`/`VenueOS.deps.json`, `ECommons.dll`, the Sqlite chain (`Microsoft.Data.Sqlite`, `SQLitePCLRaw.*`)
plus the Windows x64 native `e_sqlite3.dll`, the ClosedXML/OpenXml/SixLabors/RBush/ExcelNumberFormat/
System.IO.Packaging chain, and the updated `USER_MANUAL.md` (73,400 bytes, reflecting the new sections) staged
alongside the assemblies as the bundled offline manual. `VenueOS.json`'s `AssemblyVersion` reads `0.3.0.0`,
consistent with the csproj and `repo.json`. No PDBs, no test assemblies, no other-platform native libraries.

This pass built and staged the artifact only — it did not create a GitHub Release or upload the ZIP as a release
asset; per the packaging workflow in `RELEASE.md`, that remains a separate, deliberate manual step after this
commit is pushed.

## Secret / credential audit

No secrets found. VenueOS's established plain-text-credential-in-Settings product convention (per
`NEW_MODULE_GUIDE.md` §9a) is unchanged and was not altered for any module. The diff and every new/untracked file
were scanned for secret-shaped literals (`password=`, `token=`, `key=`, `bearer `, etc. followed by a real-looking
value) — no matches outside placeholder/example text (e.g. "shared secret configured on the backend",
"https://your-raffle-backend.example.com"). No local machine paths were found in user-facing documentation.

## Files changed

27 tracked files modified, 1 tracked file renamed (`UnfinishedModuleDefaultsTests.cs` →
`RaffleAndBracketsReleaseStatusTests.cs`), and the full set of previously-untracked module implementation files
(Raffle, Brackets/Tournament, Block Letters, Giveaways, Macro — source, tests, and their implementation/audit
reports) newly tracked. See `git show --stat` against the release commit for the exact list.

Promotion-specific source edits (this pass, on top of the pre-existing uncommitted module implementations):
- `src/VenueOS.Modules.Operations/Tournament/TournamentControlService.cs` — `UnderDevelopment`/`IsEnabled` flip.
- `src/VenueOS.Modules.Operations/Raffle/VenueRaffleService.cs` — `UnderDevelopment`/`IsEnabled` flip.
- `src/VenueOS.Modules.Operations/Giveaways/GiveawayService.cs` — `UnderDevelopment`/`IsEnabled` flip.
- `src/VenueOS.Modules.Operations/Macro/MacroService.cs` — `UnderDevelopment`/`IsEnabled` flip; doc comment updated
  to point at the Known Issues section for the drag/drop caveat.
- `src/VenueOS.Modules.Operations/BlockLetters/BlockLettersService.cs` — `UnderDevelopment`/`IsEnabled` flip.
- `docs/USER_MANUAL.md`, `README.md`, `RELEASE.md`, `docs/RELEASE_CHECKLIST.md`, `repo.json`,
  `src/VenueOS.Plugin/VenueOS.Plugin.csproj`, `src/VenueOS.Plugin/VenueOS.json` — documentation/version updates.
- Three test files updated + one renamed, per the Tests section above.

## Deliberately not changed in this pass

- `NEW_MODULE_GUIDE.md` still cites Raffle/Brackets as the "current live examples" of `UnderDevelopment: true` in
  §22a — this is developer-facing documentation, not user-facing, and its synchronization is explicitly deferred to
  the future post-UI-cleanup guide synchronization pass (see Deferred Post-Release Work below). Not fixed here.
- No donor repository (`ffxivbingo4all`, `ffxivraffle4all`, `tournamentcontrol`, `mairstrivia`, `venuepartyfinder`,
  `venuestatusandgreet`, `shoutrunner`) was touched, committed, or pushed.
- No unrelated bug was fixed; no module was redesigned; no global UI cleanup was started.

## Commit / push

- **Commit:** `a872f111ac96c4b85e80d8d8c7a4c9be070f8196` — "VenueOS: prepare 0.3.0 release"
- **Branch:** `main`
- **Remote:** `origin` → `https://github.com/KeiJoi/venueos`
- **Push result:** `6b2948c..a872f11  main -> main` — succeeded, normal (non-force) push, no history rewrite.
- **Post-push verification:** local `HEAD` and `origin/main` both resolve to `a872f111ac96c4b85e80d8d8c7a4c9be070f8196`;
  working tree clean; 0 commits ahead/behind.

## Deferred post-release work (not performed in this pass)

- Macro live tile → faux hotbar drag/drop — needs another focused investigation.
- Global VenueOS UI/popup/modal/window quality-consistency pass.
- Global logged-out-state UI suppression (with a ShoutRunner active-travel exception).
- Final `NEW_MODULE_GUIDE.md` synchronization pass, after the UI cleanup above.
