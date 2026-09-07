# VenueOS 0.2.0 release

VenueOS uses semantic versioning. `0.1.0` was the first pre-1.0 operational release; breaking persistence or protocol changes require a documented migration and a minor-version increase until 1.0.

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

- Bingo automated trade/payout operations remain experimental — see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` (a live self-trade abuse test confirmed no false-success/unintended payout, but the automation engine's game-facing assumptions are still not fully live-verified).
- Any addon-memory automation not separately version-gated and in-game validated.

ShoutRunner's own travel/World-Visit/teleport/Lifestream automation is no longer deferred as of 0.2.0 (crash recovery and the same-Data-Center reliability fix above were both live-tested). See `PARTY_FINDER_PHASE_2D.md` for Party Finder's own deferred items.

## First-release validation

Install the Release package with Dalamud's dev-plugin workflow, exercise `MANUAL_TEST_PLAN.md` on the exact game build, confirm venue isolation/reload persistence, then record observations in `OPERATOR_FEEDBACK.md` before public distribution.
