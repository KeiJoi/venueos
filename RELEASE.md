# VenueOS 0.1.0 release

VenueOS uses semantic versioning. `0.1.0` is the first pre-1.0 operational release; breaking persistence or protocol changes require a documented migration and a minor-version increase until 1.0.

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

- Bingo automated trade/payout operations.
- ShoutRunner travel, world/DC visit, teleport, and Lifestream automation.
- Any addon-memory automation not separately version-gated and in-game validated.

See `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` and `PARTY_FINDER_PHASE_2D.md`.

## First-release validation

Install the Release package with Dalamud's dev-plugin workflow, exercise `MANUAL_TEST_PLAN.md` on the exact game build, confirm venue isolation/reload persistence, then record observations in `OPERATOR_FEEDBACK.md` before public distribution.
