# Post-0.3.7 Maintenance Ledger (training-audit findings)

Purpose: a single high-level record of every issue found during post-0.3.7 real-world use ("training"), so that no
finding is lost while the 0.3.8 maintenance batch is worked one work package at a time. Fixes accumulate
**uncommitted** in the working tree and ship together as one combined 0.3.8 release. This file is a ledger, not a
design document — each item gets its own investigation/report when its work package runs.

Baseline: VenueOS 0.3.7 (release source commit `acea54c`). Test baseline: 1173 / 1173 (1206 after WP1, 1231 after WP2, 1273 after WP3, 1298 after WP4, 1329 after WP5, 1343 after WP6).

| # | Finding | Module | Status | Work package / report |
|---|---|---|---|---|
| 1 | Party Finder first-refresh failure: the first refresh after starting recruitment fails at the final **Apply Changes** action; later refreshes succeed. | Party Finder | **IMPLEMENTED — awaiting live QA** (native root cause unproven; repair + instrumentation) | WP1 — [`PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md`](PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md), [`PARTY_FINDER_HARDENING.md`](PARTY_FINDER_HARDENING.md) (post-0.3.7 section) |
| 2 | VenueOS actions can unexpectedly open the FFXIV System Menu. | Cross-cutting (game-UI automation) | **IMPLEMENTED — awaiting live QA** (root cause proven at source level: ShoutRunner's idle `HardStop()` — run on every reload, venue switch, module disable — called `Abort()`, which synthesized a real Escape key to the game window; the call is now made only when a run is active and Escape is never sent while the character is in the world) | WP6 — [`SYSTEM_MENU_0.3.8_INVESTIGATION.md`](SYSTEM_MENU_0.3.8_INVESTIGATION.md) |
| 3 | ShoutRunner "Add Aetheryte" currently does nothing. | ShoutRunner | **IMPLEMENTED — awaiting live QA** (service/routing/persistence were already sound; the control silently ignored blank input, accepted unvalidated names, and had no picker — now a validated game-data picker with on-screen feedback) | WP3 — [`SHOUTRUNNER_0.3.8_MAINTENANCE.md`](SHOUTRUNNER_0.3.8_MAINTENANCE.md) Part A |
| 4 | ShoutRunner needs an optional second shout line. | ShoutRunner | **IMPLEMENTED — awaiting live QA** | WP3 — [`SHOUTRUNNER_0.3.8_MAINTENANCE.md`](SHOUTRUNNER_0.3.8_MAINTENANCE.md) Part B |
| 5 | Attendance "Nearby Guests" layout should show Name + HomeWorld on the same line. | Attendance | **IMPLEMENTED — awaiting live QA** (presentation-only: the name/world pairing was always correct; the two-line row drew the world below a blank gap) | WP4 — [`ATTENDANCE_RAFFLE_0.3.8_MAINTENANCE.md`](ATTENDANCE_RAFFLE_0.3.8_MAINTENANCE.md) Part A |
| 6 | Block Letters mixed text/block insertion and caret bug. | Block Letters | **IMPLEMENTED — awaiting live QA** (root cause proven against the native ImGui: the panel read `SelectionStart`/`SelectionEnd`, which typing leaves frozen at the last click position; the caret is `CursorPos`. Focus + caret are now restored after each insertion) | WP5 — [`BLOCK_LETTERS_CARET_0.3.8_MAINTENANCE.md`](BLOCK_LETTERS_CARET_0.3.8_MAINTENANCE.md) |
| 7 | Brackets: first-time Organizer authentication can return HTTP 429. | Brackets / TournamentControl | **IMPLEMENTED — backend updated/deployed and `TRUST_PROXY_HOPS=1` configured by the owner — awaiting live QA** (exact production trigger not observed) | WP2-B — [`BRACKETS_FIRST_AUTH_INVESTIGATION.md`](BRACKETS_FIRST_AUTH_INVESTIGATION.md); backend `tournamentcontrol/docs/auth-rate-limit-investigation.md` |
| 8 | Raffle live screen redundantly exposes configuration fields that belong in Settings and should not mutate an active raffle. | Raffle | **IMPLEMENTED — awaiting live QA** (the live screen edited the raffle's own settings copy through a public service setter; setter removed, live screen now read-only, per-raffle settings frozen at Create) | WP4 — [`ATTENDANCE_RAFFLE_0.3.8_MAINTENANCE.md`](ATTENDANCE_RAFFLE_0.3.8_MAINTENANCE.md) Part B |
| 9 | Bingo web page: clicking a called ball should idempotently daub the exactly matching numbers on all cards and must never toggle/un-daub unrelated values. | Bingo (web) | **IMPLEMENTED — backend/web updated and deployed by the owner — awaiting live QA** | WP2-A — [`BINGO_CALLED_BALL_WEB_INVESTIGATION.md`](BINGO_CALLED_BALL_WEB_INVESTIGATION.md); backend `ffxivbingo4all/docs/CALLED_BALL_DAUB_HARDENING.md` |

## Release status (0.3.8)

All nine findings ship together in **VenueOS 0.3.8**, a maintenance/hardening release. Status of the batch at release time:

* **Implemented:** yes, all nine.
* **Automated validation complete:** yes — full VenueOS suite 1343 / 1343 (Core 69, Venues 23, Services 1251), 0 failed, 0 skipped; Debug and Release builds 0 warnings / 0 errors.
* **Backends deployed/configured (owner-performed):** the Bingo backend/web repo (item 9) and the TournamentControl backend, with `TRUST_PROXY_HOPS=1` set in its Render environment (item 7), have been updated and deployed. Neither backend repository was touched by the VenueOS release.
* **FFXIV live QA:** **PENDING for every item.** The consolidated live-QA pass is performed by the owner after the 0.3.8 release. No item in this ledger is marked live-tested, verified in FFXIV, or accepted; each per-item report keeps its own live-QA checklist.

Known limitations deliberately left as-is in 0.3.8 (not regressions, not silently fixed): Block Letters typed/pasted text can exceed the byte limit (the over-limit warning and disabled Copy apply; palette insertion is still byte-limited — item 6 report §15); and an in-world, genuinely stuck ShoutRunner transfer dialog is no longer dismissed with a synthetic Escape (`AgentWorldTravel` hide remains; a targeted native-addon close would be needed if live QA finds one — item 2 report).

Rules for this batch (historical — they governed the work-package phase, before the release was requested): no version bump, packaging, commit, push, tag, GitHub Release, or `repo.json` change until the
combined release is explicitly requested. Donor/backend repositories are never touched from this ledger's work — except WP2, where the owner explicitly authorised edits (working tree only, never committed/pushed/deployed) to the Bingo backend/web repo and the TournamentControl backend; the owner handles their commits, pushes, releases, Render deployment and environment variables.
