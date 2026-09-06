# VenueOS

VenueOS is a [Dalamud](https://dalamud.dev) plugin for Final Fantasy XIV: a single tablet-style operations console for running an in-game venue, built on the Dalamud SDK (API level 15) and .NET 10. It is unofficial, third-party, and not affiliated with Square Enix or the Dalamud/XIVLauncher project.

## Modules

**Working, live-tested:** Attendance, Greeter, VIP, ShoutRunner, Party Finder, Mair's Trivia, Mair's Editor.

**Under Development — included but disabled by default:** Raffle, Bingo, TournamentControl. These are backend-compatible modules that haven't finished reconstruction or live QA yet. They stay hidden from the Home screen until you explicitly enable them from Settings → Modules, where they're clearly labeled "Under Development."

Each module is a small application inside VenueOS — see [NEW_MODULE_GUIDE.md](NEW_MODULE_GUIDE.md) for the full architecture if you're extending it.

## User Manual

**[docs/USER_MANUAL.md](docs/USER_MANUAL.md)** — the practical, step-by-step guide for venue operators: installing VenueOS, Venue Profiles, Settings, and full instructions for every working module (ShoutRunner, Attendance, Greeter, VIP, Party Finder, Mair's Trivia, Mair's Editor).

## Installing

**From the experimental repository:** add this repository's `repo.json` URL under Dalamud Settings → Experimental → Custom Plugin Repositories, then install VenueOS from the plugin installer like any other plugin.

**From source (development):** open `VenueOS.sln`, build `src/VenueOS.Plugin` in Debug or Release, and load the resulting DLL directly via Dalamud's dev-plugin loader. This local workflow never requires a GitHub release, ZIP package, or repo.json publication — see [RELEASE.md](RELEASE.md) for how the two paths relate.

```powershell
dotnet build VenueOS.sln
dotnet test VenueOS.sln
```

## Issues and support

This is a personal/community project distributed as-is through an experimental repository. Report issues on the [GitHub repository](https://github.com/KeiJoi/venueos).

## Further reading

See [RELEASE.md](RELEASE.md), [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md), [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md), [OPERATOR_FEEDBACK.md](OPERATOR_FEEDBACK.md), [BACKEND_MODULE_MAINTENANCE.md](BACKEND_MODULE_MAINTENANCE.md), [ARCHITECTURE.md](ARCHITECTURE.md), [VENUE_PROFILES.md](VENUE_PROFILES.md), and [THEMING.md](THEMING.md). The Phase 1 audit and each standalone plugin remain authoritative for compatibility work.
