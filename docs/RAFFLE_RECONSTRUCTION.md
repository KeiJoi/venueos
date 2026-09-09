# Raffle Reconstruction

**Status:** Implementation complete, automated tests passing, Debug and Release builds clean. Live in-game acceptance by the operator has already covered the core operational flow (see the acceptance note in §17); a residual live-QA checklist for the newer hardening/edge-case work remains in §17.

**Donor HEAD at session start:** `f685a4165f3df577ee5d0e9a302114d41f234a2a` (per the prior forensic audit). **Donor HEAD now:** `cbd8e1a3dcea42d35b6dd0cd6fef0202f39ab9e8` — a commit titled "Backend update for VenueOS" made outside this reconstruction session (by the repository owner, after live-testing the implementation) that captured the backend/browser reconstruction work described below. See §18 for exactly what that commit contains and a hygiene note about it.

**VenueOS HEAD:** `6b2948cfca73dc71c491f23637b65f1a21ff5fcc`, unchanged for the entire duration of this work — every VenueOS change described here is an **uncommitted working-tree change**, left for manual review per instructions.

---

## 1. Executive Summary

This reconstruction replaces the dead-code Raffle scaffold documented in `docs/RAFFLE_FORENSIC_AUDIT.md` with a complete, functional module spanning three layers:

- **Donor backend (`ffxivraffle4all/backend`)** — added an organizer access-key authentication layer, a confirmed-redraw/previous-winner-exclusion protocol, crash-safe atomic persistence with backup recovery, safe multi-client broadcast (no more single-bad-socket crash risk), a `DELETE /api/raffles/:id` endpoint, and a from-scratch automated test suite (25 tests, `node --test`).
- **Donor browser wheel (`backend/public/*`)** — moved the Spin/Redraw control off the wheel face into the Winner panel, made the winner name persist indefinitely instead of receding after the reveal animation, and added a client-side redraw confirmation that the server still enforces authoritatively regardless of what the browser does.
- **VenueOS (`games.raffle`)** — a full Settings/live operational split, HomeWorld-aware participant identity, a live realtime client that mirrors backend spin/redraw/winner state into VenueOS automatically, archive/restore, permanent delete (with best-effort backend cleanup), a non-destructive Reset, publish/link display with an explicit "Unpublished Changes" indicator, and XLSX import/export extended with a Home World column.

Server-side, CSPRNG-based (`crypto.randomInt`) winner selection was preserved exactly as the donor implemented it — VenueOS and the browser remain pure organizer/visualization clients that never compute a winner themselves.

The module remains `UnderDevelopment: true` / disabled-by-default (`games.raffle`, `DisplayOrder: 9`) per explicit instruction — tests passing does not graduate it to release-ready; that is a separate, deliberate future decision after further live QA.

---

## 2. Product Decisions Implemented

All six product decisions from the reconstruction brief were implemented as specified:

1. **Redraw/re-spin is allowed but never silent.** A raffle with an existing winner refuses a second draw unless the caller explicitly passes `redraw: true` — enforced authoritatively on the backend (`spinRaffle`, `server.js`), not merely by disabling a browser button. The browser's own Spin/Redraw button shows a native confirmation dialog before ever sending `redraw: true`.
2. **A confirmed redraw removes every ticket belonging to the previous winner** from the authoritative pool (`raffle.tickets`), recorded in a persistent `excludedTickets` list on the raffle so the exclusion survives republishing, backend restarts, and reconnects. VenueOS mirrors this list into `LocalRaffle.ExcludedParticipants` and visibly flags excluded participants in its roster UI.
3. **Archive and Delete are separate, both new to VenueOS.** Archive is nondestructive (hides from the active list, fully restorable, preserves everything). Delete is permanent, requires the existing `ConfirmDialog` component, and best-effort deletes the backend's published copy too (via the new `DELETE /api/raffles/:id`), without blocking the local deletion the operator already explicitly confirmed if the backend call fails.
4. **The live browser wheel is fully implemented and required**, superseding the prior Phase 3A "out of scope" decision — see §8 for the realtime architecture.
5. **The backend authentication gap is closed server-side** (organizer access key, §6) rather than only warned about client-side, per the brief's framing that a client-side mitigation alone cannot close a server-side hole.
6. **HomeWorld is preserved as part of participant identity**, following VenueOS's own `GuestIdentity(Name, HomeWorld)` convention instead of the donor's `@`-stripping normalization, with legacy Name-only entrants preserved as a distinct, valid identity rather than invented HomeWorld values.

---

## 3. Donor Files Modified

All under `C:\FFXIVplugs\ffxivraffle4all` (backend/browser only, per explicit authorization — the Dalamud plugin project `FFXIVRaffle4All/` was not touched):

- `backend/server.js` — access-key middleware, redraw/exclusion protocol, safe broadcast, atomic persistence + backup recovery, `DELETE /api/raffles/:id`, error codes on WS error frames, testability exports.
- `backend/package.json` — added `"test": "node --test test/*.test.js"`.
- `backend/config.example.json` — new, documents the access-key config file format.
- `backend/.gitignore` — new/extended (`config.local.json`, `data/`, `node_modules/`).
- `backend/public/host.html` — moved the Spin/Redraw button from the wheel face into the Winner panel.
- `backend/public/styles.css` — restyled the button for its new inline position (no longer absolutely centered over the wheel).
- `backend/public/wheel.js` — redraw confirmation flow, persistent winner display, error-code handling, `deleted` message handling.
- `backend/test/helpers.js`, `backend/test/auth.test.js`, `backend/test/spin.test.js`, `backend/test/persistence.test.js` — new, 25 tests total.

---

## 4. VenueOS Files Modified / Added

**New — `src/VenueOS.Modules.Operations/Raffle/`** (own file/folder per `NEW_MODULE_GUIDE.md` §21):
- `VenueRaffleClient.cs` — wire contract + HTTP client (upsert/fetch/delete, access-key header, HomeWorld-aware ticket keys, local ticket-hash fingerprinting).
- `RaffleRealtimeClient.cs` — WebSocket realtime client + `IRaffleRealtimeTransport` abstraction, mirroring `TournamentRealtimeClient`'s connect/reconnect/backoff pattern for the raffle backend's simpler join/state/spin protocol.
- `VenueRaffleService.cs` — the service (settings, lifecycle, participant/ticket management, publish, realtime reconciliation) and `VenueRaffleModule`.
- `RaffleXlsx.cs` — XLSX export/import (ClosedXML), extended with a Home World column.

**New — `src/VenueOS.Plugin/Raffle/RaffleOperatorPanel.cs`** — the Settings/live-split operator panel.

**Modified:**
- `src/VenueOS.Modules.Operations/Operations.cs` — old inline `VenueRaffleSettings`/`VenueRaffleService`/`VenueRaffleModule` removed, replaced with a pointer comment (matching the existing Trivia/Tournament precedent).
- `src/VenueOS.Modules.Operations/VenueOS.Modules.Operations.csproj` — added `ClosedXML` package reference.
- `src/VenueOS.Plugin/NativeOperationsPanels.cs` — old inline `RaffleOperatorPanel` removed, replaced with a pointer comment.
- `src/VenueOS.Plugin/Plugin.cs` — updated construction/registration for the new service/panel signatures (`DiagnosticsService` now injected; panel takes `ITargetedPlayerProvider`; module registers both `Draw`/`DrawSettings` delegates).
- `src/VenueOS.Plugin/VenueOperationsDashboard.cs` — updated for the renamed `RaffleDashboard` fields and new namespace.
- `src/VenueOS.Plugin/Shell/Forms.cs` — added `Forms.FloatField` (the donor's pot/cost/percentage settings are `float`; no existing UI-kit field supported that).

**Tests, `tests/VenueOS.Services.Tests/`:**
- `RaffleClientTests.cs` — reworked for the new wire contract (access key, HomeWorld, delete, unpublished-change detection).
- `RaffleServiceTests.cs`, `RaffleRealtimeClientTests.cs`, `RaffleXlsxTests.cs` — new.
- `ModuleDisplayOrderTests.cs`, `UnfinishedModuleDefaultsTests.cs` — updated only for the `VenueRaffleService` constructor's new `DiagnosticsService` parameter; their actual assertions (disabled-by-default, under-development, display order) are unchanged.

Files with pre-existing, unrelated Tournament/Brackets changes (`TournamentControlClient.cs`, `TournamentControlOperatorPanel.cs`, `TournamentControlTests.cs`, `TournamentControlService.cs`, `TournamentRealtimeClient.cs`, `TournamentRealtimeClientTests.cs`, `BRACKETS_RECONSTRUCTION.md`, `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md`, `packages.lock.json`, `README.md`, `docs/USER_MANUAL.md`, `repo.json`) were **not touched** by this work — they were already present in the working tree before this session and are called out here only so the `git status` in §18 is fully explained.

---

## 5. Backend Protocol Changes

- `POST /api/raffles` and `DELETE /api/raffles/:id` now require an `X-Access-Key` header matching the configured organizer access key (401/503 otherwise).
- `GET /api/raffles/:id` now requires either a valid host/viewer `token` query parameter **or** the organizer access key (previously a missing token was silently accepted).
- WS `spin` accepts an optional `redraw: boolean` field. A raffle with an existing winner rejects an unconfirmed spin with `{type:'error', code:'WINNER_EXISTS'}`; a confirmed redraw excludes the previous winner's tickets, then draws again.
- Every WS error frame now carries a machine-readable `code` (`WINNER_EXISTS`, `NO_TICKETS`, `FORBIDDEN`, `NOT_FOUND`, `NOT_JOINED`, `INVALID_JSON`, `INTERNAL_ERROR`) alongside the existing human-readable `message`.
- `buildPublicState` (sent as `state`/`updated`) now also includes `hasWinner` and `excludedTickets`.
- New `DELETE /api/raffles/:id` (organizer-only): removes the raffle, notifies and closes any connected sockets with a `{type:'deleted'}` frame, and returns 200 (or 404 if already gone — never a client-visible failure for a raffle that's already deleted).
- New optional `clearExclusions: boolean` field on the upsert payload lets an explicit republish wipe the backend's exclusion memory (used by VenueOS's "also clear previously-excluded winners on publish" option).

---

## 6. Backend Security Model

- Organizer access key sourced from the `RAFFLE_ACCESS_KEY` environment variable, or a gitignored `backend/config.local.json` (`{"accessKey": "..."}`, documented via the committed `backend/config.example.json`). If neither is configured, every organizer endpoint returns 503 rather than silently operating unauthenticated.
- The key is never returned in any API response and never reaches the browser wheel UI — confirmed by an automated test that inspects the served host page HTML and every WS frame for the configured key string.
- Host/viewer tokens remain their own separate capability credentials (unchanged from the donor), gating read/join/spin exactly as before.
- VenueOS stores the access key in `RaffleConnectionSettings.AccessKey`, persisted per-venue, **deliberately unmasked** (plain `Forms.TextField`, no password flag) per explicit product requirement — the operator must be able to read/copy/verify it. `DiagnosticsService.RecordFailure`/`Redact` still keep it (and host/viewer URLs) out of logs; VenueOS never interpolates a raw `HostUrl`/`ViewerUrl` or the access key into a diagnostic message (addressing the forensic audit's specific concern that `Redact`'s marker list wouldn't catch a bare path-segment token).

---

## 7. Persistence Changes

- `saveRaffles()` now writes to a temp file, snapshots the previous primary file as a backup (`raffles.json.bak`) before replacing it, then atomically renames the temp file into place — no more risk of a half-written file from a crash mid-write.
- `loadRaffles()` falls back to the backup file (with a clear, non-secret console warning) if the primary file is missing or corrupt, instead of silently starting with zero raffles.
- VenueOS's own per-venue persistence is unchanged in mechanism (`VenueProfileService.GetModuleConfig`/`SaveModuleConfig`, schema version 1) but now stores a larger `VenueRaffleSettings` shape (`Connection`, `Defaults`, `Raffles`, `SelectedRaffleId`) with `LocalRaffle` gaining `ExcludedParticipants`, `PublishedTicketHash`, and `IsArchived`.

---

## 8. Realtime / WebSocket Architecture

VenueOS's `RaffleRealtimeClient` (`Modules.Operations/Raffle/RaffleRealtimeClient.cs`) mirrors the already-proven `TournamentRealtimeClient` pattern:

- `IRaffleRealtimeTransport` keeps the actual `ClientWebSocket` I/O behind a small interface so the connect/reconnect/backoff logic is unit-testable with a fake transport (`RaffleRealtimeClientTests.cs`).
- VenueOS **always joins as a read-only `viewer`**, never `host` — it never triggers a spin itself (the browser host page owns that control, per product decision 4/§4.1 of the brief); it only needs to *learn* results the instant they happen.
- A background receive loop enqueues decoded frames into a thread-safe queue; `VenueRaffleService.Tick()` (called from the Dalamud framework thread via `VenueRaffleModule.Tick`) drains it every frame and applies `state`/`updated`/`spin`/`deleted`/`error` frames to the matching `LocalRaffle`.
- On reconnect (not the first connect), a one-shot signal triggers a REST `FetchAsync` reconciliation, so a socket that missed frames while disconnected can't leave VenueOS with stale state.
- Survives backend restart, browser/VenueOS disconnects, and multiple viewers by construction (REST reconciliation + reconnect backoff); verified by `RaffleRealtimeClientTests` (queueing, reconnect-signal semantics, join-frame content) and `RaffleServiceTests` (end-to-end mirroring into `LocalRaffle`).

---

## 9. Redraw / Exclusion Semantics

- First spin: `winnerName` is null → proceeds normally.
- Second+ spin without `redraw: true`: rejected with `WINNER_EXISTS`, regardless of which host tab/socket sent it — this is what makes concurrent/multi-tab host access safe (§19 test coverage below).
- Confirmed redraw: removes every ticket equal to the previous winner's ticket string from the pool, records it in `excludedTickets`, clears `winnerName`, then draws again from the reduced pool. An empty pool after exclusion returns `NO_TICKETS` rather than crashing or fabricating a winner.
- Exclusions persist across republishes (the backend filters incoming tickets against its stored `excludedTickets` list) until the operator explicitly clears them (`clearExclusions: true`), which VenueOS exposes as an explicit toggle next to Publish rather than an implicit side effect.
- VenueOS mirrors `excludedTickets` into `LocalRaffle.ExcludedParticipants` and visibly badges excluded participants in the roster as "Excluded (previous winner)" rather than silently removing their row.

---

## 10. HomeWorld Identity Changes

- `RaffleParticipant(Name, HomeWorld, PaidTickets, FreeTickets)` — `HomeWorld` is nullable; null means a legacy Name-only entrant (never invented for an imported/legacy record).
- `TicketKey` is `"Name"` for a legacy entrant or `"Name@World"` otherwise — this is the literal string that goes into the backend's flattened ticket pool and comes back as `winnerName`, so two same-named characters on different worlds are provably distinct throughout the pool and winner state (covered by both backend tests, `RaffleClientTests`, and `RaffleServiceTests`).
- Duplicate-entry merging uses `IdentityKey` (`GuestIdentity(Name, HomeWorld ?? "").Key`), VenueOS's established per-guest identity convention, rather than the donor's `@`-stripping name normalization.
- "Use Current Target" (`ITargetedPlayerProvider.GetTargetedPlayer()`) captures both Name and HomeWorld from the targeted player character.
- XLSX export/import carry a "Home World" participant column; import of a donor-era workbook with no such column produces legacy Name-only participants rather than inventing a world.

---

## 11. Browser UI Changes

- **Spin/Redraw control relocated**: `host.html`'s `<button id="spinButton">` moved out of `.wheel-shell` (where it was absolutely centered over the wheel) into the Winner panel in the host controls sidebar; `styles.css`'s `.spin-button` rule was rewritten from an absolutely-positioned circular overlay to an inline block-width button. The wheel canvas is now the uncontested visual centerpiece.
- **Persistent winner display**: `wheel.js`'s `queueWinner()` no longer blanks the winner name the instant a new spin starts — the previous winner stays visible for the whole redraw animation, swapping to the new name only once it's actually revealed. `updateWinnerImmediate` never auto-clears on a timer; the only things that ever change the displayed winner are a new reveal or the server reporting no winner (a ticket-set-changing republish).
- The Spin button now reads "Redraw" (with a distinct color) once a winner exists, and clicking it in that state shows a native confirmation dialog naming the current winner before sending `{type:'spin', redraw:true}`. This is a client-side courtesy only — the server enforces the guard regardless.

---

## 12. Archive / Delete / Reset Semantics

| Action | Effect | Backend touched? | Confirmation |
|---|---|---|---|
| **Archive** | Sets `IsArchived = true`; hidden from the active list, fully preserved, restorable | No | No (nondestructive, reversible) |
| **Unarchive** | Sets `IsArchived = false` | No | No |
| **Reset** | Clears `Participants`, `ExcludedParticipants`, `WinnerName`; keeps the raffle, its `Id`, and its backend link (`ExternalId`/URLs); marks it `HasUnpublishedChanges` | No (until the operator republishes) | Yes (`ConfirmDialog`) |
| **Delete** | Permanently removes the local raffle; best-effort `DELETE`s the backend's copy if it was ever published; a failed backend call does not block the already-confirmed local deletion (reported to Diagnostics instead) | Yes, best-effort | Yes (`ConfirmDialog`, states it cannot be undone) |

---

## 13. XLSX Changes

Reconstructed the donor's three-sheet workbook (Settings, Summary, Participants) via ClosedXML (the donor used `DocumentFormat.OpenXml` directly; VenueOS already depends on ClosedXML elsewhere in the solution, so no new library concept was introduced — see `VenueOS.Modules.Operations.csproj`). Extended the Participants sheet with **Home World** and **Excluded** columns. Import always creates a brand-new local raffle (fresh id, no backend linkage), preserves a recorded winner name as a read-only label, and falls back to the Summary sheet (3 of 5 settings only, matching donor behavior) when no usable Settings sheet is present. A legacy donor-era workbook with no Home World column imports every participant as a legacy Name-only entrant.

---

## 14. Tests Added

**Backend (`backend/test/*.test.js`, Node's built-in `node --test`, no new dependency): 25 tests, all passing.**
`auth.test.js` (6): authenticated upsert succeeds; missing/wrong access key rejected; no-key-configured refuses mutations; delete endpoint authorization; browser pages/WS frames never contain the access key.
`persistence.test.js` (6): idempotent unchanged republish; changed-ticket republish resets winner; exclusions survive republish and `clearExclusions` resets them; atomic save (no leftover temp file, backup mirrors prior save); corrupt-primary-recovers-from-backup; winner survives a simulated restart.
`spin.test.js` (13): first spin; second unconfirmed spin rejected; confirmed redraw excludes the previous winner; a fully excluded winner can never win again across repeated redraws; single entrant; weighted multi-entrant; empty pool rejected safely; CSPRNG-only source check; HomeWorld-qualified tickets never merged; legacy Name-only tickets usable; reconnect sees the persistent winner; concurrent double-spin cannot silently overwrite; a dead/closing socket doesn't crash the server and healthy viewers still get the broadcast.

**VenueOS (`tests/VenueOS.Services.Tests/`): 38 Raffle-related tests, all passing (out of 629 total in the solution).**
`RaffleClientTests.cs` (11): wire contract + access-key header; no-key-configured fails locally; fetch token escaping + exclusion/hasWinner deserialization; delete sends the key and treats 404 as success; errors never leak secrets; `WithoutSecrets`; distinct ticket keys per HomeWorld; legacy ticket key; excluded participants omitted from the built pool; unpublished-change detection; venue-switch cancellation.
`RaffleServiceTests.cs` (15, plus 3 `RaffleTicketMathTests` in the same file = 18): paid-ticket merge-by-identity + bonus rule; distinct HomeWorlds never merge; legacy vs. HomeWorld-qualified distinctness; ticket adjustment removes a zeroed participant; counts never go negative; archive/unarchive; reset semantics; delete without/with a backend call, and a failed backend delete still deleting locally with a diagnostics record; publish + unpublished-change lifecycle; realtime mirroring of winner/exclusions into the selected raffle; two-venue isolation; access key persists as plain text through save/reload; access key never leaks into a diagnostics message.
`RaffleRealtimeClientTests.cs` (4): ordered message draining; reconnect-signal one-shot semantics; join frame always uses the `viewer` role; `Stop()` clears undrained state.
`RaffleXlsxTests.cs` (3): full export/import round trip incl. HomeWorld and exclusions; legacy donor-style Summary-only fallback never invents a world; excluded participants marked in the export.
`UnfinishedModuleDefaultsTests.cs` (2, updated not added): still-disabled/still-under-development assertions.

---

## 15. Test / Build Results

- **Backend:** `npm test` → **25 passed, 0 failed.**
- **VenueOS full solution (`dotnet test VenueOS.sln`):** `VenueOS.Core.Tests` 4/4, `VenueOS.Venues.Tests` 23/23, `VenueOS.Services.Tests` **602/602** (38 of which are Raffle-specific, filtered separately for confirmation). **629/629 total, 0 failed.**
- **Debug build (`dotnet build VenueOS.sln -c Debug`):** Build succeeded, **0 Warnings, 0 Errors.**
- **Release build (`dotnet build VenueOS.sln -c Release`):** Build succeeded, **0 Warnings, 0 Errors.**
- Backend server startup sanity-checked (env-based access key + a temporary data directory, no production data touched) — starts and logs cleanly.

---

## 16. Known Limitations

- Rendered ImGui behavior (layout, theme correctness across Dark/Light/Neon/Midnight, no-overlap at various window sizes) is **not** proven by the automated test suite — per `NEW_MODULE_GUIDE.md` §30, there is no `VenueOS.Plugin` test project, deliberately, since ImGui needs a live rendering context. The operator has already live-tested the core operational flow in-game (per the acceptance note at the top of this document); the newer hardening work (archive/delete confirmations, the "Unpublished Changes" indicator, the exclusion badge, the connection status badge, Settings→Modules field split) has not yet had an equivalent live pass — see §17.
- The backend remains a single-process, single-instance Node service with in-memory state plus a flat JSON file — this was true of the donor and is unchanged; it was explicitly out of scope to introduce a database.
- `DeleteAsync`'s best-effort backend cleanup means a raffle deleted locally while the backend is unreachable leaves an orphaned published copy on the backend until the operator notices (surfaced via a Diagnostics entry, not silently swallowed) — there is no automatic retry queue for this narrow case.
- The Reset action does not itself contact the backend; a reset raffle shows "Unpublished Changes" until the operator explicitly republishes (optionally with "clear exclusions"). This is a deliberate simplicity choice (no surprise network calls from a Reset click), documented here so it isn't mistaken for an oversight.

---

## 17. Required Live QA

The operator has already validated the core flow live in FFXIV (create raffle → configure → add players → publish → open host/viewer links → spin → VenueOS learns the winner) and it is accepted as working. The following items are specific to the hardening/edge-case work completed in this pass and have not yet had an equivalent live pass:

- [ ] Attempt a second spin with a winner already showing — confirm the browser's confirmation dialog appears and, on Cancel, nothing is sent; on Confirm, the redraw happens and the previous winner's tickets visibly disappear from the wheel.
- [ ] After a redraw, confirm the excluded participant shows the "Excluded (previous winner)" badge in VenueOS's roster and cannot be selected again even after adding more tickets for other participants and republishing.
- [ ] Publish, then add/adjust a ticket without republishing — confirm the "Unpublished Changes" badge appears, and clears after Publish.
- [ ] Archive a raffle, confirm it disappears from the active list and reappears correctly via "Show archived raffles" → Restore.
- [ ] Delete a raffle that was never published — confirm no network call is attempted (Diagnostics stays clean) and it disappears immediately.
- [ ] Delete a published raffle with the backend reachable — confirm the backend raffle is actually gone (host/viewer links now 404 or show "deleted").
- [ ] Delete a published raffle with the backend briefly unreachable — confirm the local raffle is still removed and a Diagnostics entry appears, with no crash.
- [ ] Reset a raffle with a winner already showing, then republish — confirm the backend's winner/rotation reset correctly.
- [ ] Two browser tabs open on the same host link, both attempting to spin close together — confirm only one draw succeeds and the second tab's UI correctly resyncs to "Redraw" instead of silently erroring forever.
- [ ] Kill and restart the backend process mid-session — confirm VenueOS's realtime connection reconnects and REST-reconciles the correct winner/exclusion state.
- [ ] Export a raffle with mixed HomeWorld/legacy participants to XLSX, re-import it, and confirm the roster and settings match.
- [ ] Import a genuine donor-era (pre-VenueOS) XLSX export and confirm every participant imports as a legacy Name-only entrant with no invented HomeWorld.
- [ ] Verify all four themes (Dark/Light/Neon/Midnight) render the new panel sections (status badges, connection badge, excluded-participant badge) without contrast/readability issues.
- [ ] Verify no control overlap at wide, normal, and minimum supported window sizes, both embedded and detached.

---

## 18. Git Status — Both Repositories

**VenueOS (`C:\FFXIVplugs\venueos`):** HEAD `6b2948cfca73dc71c491f23637b65f1a21ff5fcc`, unchanged. All Raffle work (and the pre-existing, untouched Tournament/Brackets work) remains **uncommitted** in the working tree — see the file list in §4 and the `git status` below. Nothing was staged, committed, pushed, tagged, or released by this session.

```
 M README.md                                                    (pre-existing, not touched by this work)
 M docs/USER_MANUAL.md                                          (pre-existing, not touched by this work)
 M repo.json                                                    (pre-existing, not touched by this work)
 M src/VenueOS.Modules.Operations/Operations.cs
 M src/VenueOS.Modules.Operations/Raffle/VenueRaffleClient.cs
 M src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs   (pre-existing, not touched)
 M src/VenueOS.Modules.Operations/VenueOS.Modules.Operations.csproj
 M src/VenueOS.Plugin/NativeOperationsPanels.cs
 M src/VenueOS.Plugin/Plugin.cs
 M src/VenueOS.Plugin/Shell/Forms.cs
 M src/VenueOS.Plugin/TournamentControlOperatorPanel.cs         (pre-existing, not touched)
 M src/VenueOS.Plugin/VenueOperationsDashboard.cs
 M src/VenueOS.Plugin/packages.lock.json                        (pre-existing, not touched)
 M tests/VenueOS.Services.Tests/ModuleDisplayOrderTests.cs
 M tests/VenueOS.Services.Tests/RaffleClientTests.cs
 M tests/VenueOS.Services.Tests/TournamentControlTests.cs       (pre-existing, not touched)
 M tests/VenueOS.Services.Tests/UnfinishedModuleDefaultsTests.cs
?? BRACKETS_RECONSTRUCTION.md                                   (pre-existing, not touched)
?? TOURNAMENT_CONTROL_BRACKETS_AUDIT.md                         (pre-existing, not touched)
?? docs/RAFFLE_FORENSIC_AUDIT.md                                (pre-existing)
?? docs/RAFFLE_RECONSTRUCTION.md                                (this file)
?? src/VenueOS.Modules.Operations/Raffle/RaffleRealtimeClient.cs
?? src/VenueOS.Modules.Operations/Raffle/RaffleXlsx.cs
?? src/VenueOS.Modules.Operations/Raffle/VenueRaffleService.cs
?? src/VenueOS.Modules.Operations/Tournament/TournamentControlService.cs (pre-existing, not touched)
?? src/VenueOS.Modules.Operations/Tournament/TournamentRealtimeClient.cs (pre-existing, not touched)
?? src/VenueOS.Plugin/Raffle/
?? tests/VenueOS.Services.Tests/RaffleRealtimeClientTests.cs
?? tests/VenueOS.Services.Tests/RaffleServiceTests.cs
?? tests/VenueOS.Services.Tests/RaffleXlsxTests.cs
?? tests/VenueOS.Services.Tests/TournamentRealtimeClientTests.cs (pre-existing, not touched)
```

**Donor (`C:\FFXIVplugs\ffxivraffle4all`):** HEAD `cbd8e1a3dcea42d35b6dd0cd6fef0202f39ab9e8`. This commit (message "Backend update for VenueOS", authored by the repository owner) was made **outside this reconstruction session** — most likely by the operator directly, after live-testing the implementation — and contains the backend/browser reconstruction work described in this document. **This session did not create that commit** and made no further commits, pushes, tags, or releases in the donor repository; the only change in this session's donor working tree is a one-line uncommitted addition to `backend/.gitignore` (adding `node_modules/`, currently `M backend/.gitignore` in `git status`).

**Hygiene note (flagged, not fixed):** that commit's diff includes 657 files under `backend/node_modules/` (~83,500 insertions) because `node_modules/` was not yet in `.gitignore` at commit time. No secrets were committed — `backend/config.local.json` and `backend/data/` were correctly excluded and confirmed absent from the commit. Recommended cleanup (not performed by this session, since it requires a commit): `git rm -r --cached backend/node_modules` followed by a commit, now that `.gitignore` excludes it going forward.

---

## 19. Confirmation

- No file in `C:\FFXIVplugs\ffxivraffle4all\FFXIVRaffle4All` (the Dalamud plugin project) was modified.
- No `git add`, `git commit`, `git push`, `git tag`, or release action was taken by this session in **either** repository.
- No branch was created in either repository.
- The pre-existing, unrelated Tournament/Brackets working-tree changes in VenueOS were not modified, staged, reverted, or otherwise touched.
