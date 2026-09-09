# Brackets Reconstruction — Completion Report

Module ID: `games.tournament`. Display name: **Brackets** (renamed from "TournamentControl"; ID unchanged). Status: **still `UnderDevelopment: true`, `IsEnabled = false` by default — not promoted, not released.** This document records the backend repair and the VenueOS reconstruction pass performed against `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md`'s findings and the user's follow-up implementation brief. It is a completion report, not a promotion of Brackets to release-ready — that requires the live QA pass in §14 below, which was not performed (no live Dalamud/FFXIV environment was available in this session).

---

## 1. TournamentControl (donor) files changed

Exactly two files, both in `apps/server/`, the only area authorized for this pass:

- `apps/server/src/domain/bracket-service.ts` — the `resolveByes()` fix (§3).
- `apps/server/test/bracket.test.ts` — the regression matrix and load-bearing tests (§4).

`apps/dalamud/*` was **not touched** — out of the authorized scope for this repository (backend + tests only). The pre-existing dirty build artifacts under `apps/dalamud.tests/bin|obj` and the untracked `artifacts/*.zip` files were already present before this session started (confirmed against the audit's original `git status` snapshot) and are untouched by this work.

## 2. VenueOS files changed

**Modified:**
- `src/VenueOS.Modules.Operations/Operations.cs` — old Tournament classes removed, replaced with a "moved to" pointer comment (matching the Bingo/Trivia/Raffle/PartyFinder precedent, `NEW_MODULE_GUIDE.md` §21).
- `src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs` — full endpoint parity added (§5).
- `src/VenueOS.Plugin/TournamentControlOperatorPanel.cs` — rewritten in full (§6).
- `src/VenueOS.Plugin/Plugin.cs` — `TournamentControlService` construction gained a `diagnostics` argument; module registration now passes `tournamentPanel.DrawSettings` (previously only `Draw` was wired, so Settings → Modules → Brackets rendered the operational screen instead of a real Settings surface).
- `src/VenueOS.Plugin/VenueOperationsDashboard.cs` — Home dashboard badge label `"TournamentControl"` → `"Brackets"`.
- `README.md`, `docs/USER_MANUAL.md`, `repo.json` — the four/two/one user-visible mentions of "TournamentControl" updated to "Brackets" (still framed as disabled/Under Development — no claim of readiness added).
- `tests/VenueOS.Services.Tests/TournamentControlTests.cs`, `ModuleDisplayOrderTests.cs`, `UnfinishedModuleDefaultsTests.cs` — updated for the new constructor signature and display name; `ModuleDisplayOrderTests`'s expected-order array now reads `"Brackets"`.

**Added:**
- `src/VenueOS.Modules.Operations/Tournament/TournamentControlService.cs` — the service + module wrapper, moved out of `Operations.cs` into its own file/folder (`NEW_MODULE_GUIDE.md` §21/§33) and substantially expanded (§7).
- `src/VenueOS.Modules.Operations/Tournament/TournamentRealtimeClient.cs` — the realtime transport interface, real `ClientWebSocket` implementation, and reconnect/backoff client (§9).
- `tests/VenueOS.Services.Tests/TournamentRealtimeClientTests.cs` — realtime plumbing tests against a fake transport.
- `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md` — the forensic audit this reconstruction is built against (written in the prior session).

**Deliberately not renamed** (internal identifiers, per the brief's own instruction): `TournamentControlModule`, `TournamentControlService`, `TournamentControlClient`, `TournamentControlOperatorPanel`, the module ID `games.tournament`, the `games.tournament:` prefix on diagnostic messages, and historical/point-in-time docs that name the donor plugin or record past audit findings under its old name (`TOURNAMENT_CONTROL_3C.md`, `UI_STATUS.md`, `VENUEOS_AUDIT.md`, `VENUEOS_MIGRATION_PLAN.md`'s donor-repository reference, etc.) — rewriting those would misrepresent history rather than correct a live surface.

## 3. Exact `resolveByes()` repair implemented

Root cause (from the audit): `BracketService.resolveByes()` iterated a `listMatches()` snapshot array and, within the same pass, read `player1Id`/`player2Id` off that **stale** snapshot object even after an earlier iteration in the same pass had already written a bye winner into that exact row via `setMatch(nextWinnerMatchId, ...)`. The stale nulls made the code fall into the bye branch with `winner = null`, permanently stamping a match holding two real contestants as `status: "BYE", winnerId: null` — a terminal state, since `status !== "PENDING"` excludes it from every future pass.

Fix (`apps/server/src/domain/bracket-service.ts`, `resolveByes()`): each pass now re-reads every candidate's **current** row via `this.persistence.bracket.findMatch(candidate.id)` immediately before evaluating it, instead of trusting the loop's original snapshot object. `incoming()` was already read fresh each time — only `player1Id`/`player2Id`/`status` needed the same treatment. This is a generic fix (no entrant-count special-casing), stays entirely inside the existing transaction boundaries (`start()`/`recordResult()`/`correct()` already wrap `resolveByes()` in `this.persistence.transaction(...)`), and touches no client code.

Verified by direct reproduction (not just the new automated tests): the exact 17-participant trace script used during the audit was re-run against the fixed code and now shows all 8 Round-of-16 matches at `READY` with two real, distinct contestants each, immediately after the single Round-1 real match resolves — matching the required outcome exactly.

## 4. Regression tests added and results

`apps/server/test/bracket.test.ts` grew from 12 to 33 tests. Full breakdown:

- **Boundary matrix** (`it.each`, 16 cases: 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 18, 19, 31, 32, 33 entrants) — plays every `READY` match to completion for each count, asserting after **every** result that no match is ever in the impossible state (`player1Id && player2Id && status === "BYE" && winnerId === null`), and that the tournament reaches `COMPLETED` with a real champion within a bounded number of iterations. Before the fix, the affected counts (5, 9, 17, 18, 19, 33) would never reach `COMPLETED` at all — a starved descendant chain — so this test is a strong, generic regression guard, not just a snapshot check.
- **17-entrant load-bearing test** (exact wording from the brief): asserts Round 1 has exactly 1 real match + 15 byes, the real match is `READY`, and after recording its winner, the Round of 16 has exactly 8 matches, **all `READY`**, each with two real distinct contestants and `winnerId: null` (i.e., genuinely waiting to be called — not a stale bye).
- **Explicit neighboring cases** (18/2+14, 19/3+13, 31/15+1, 33/1+31): asserts the exact real/bye composition of Round 1, then that the entire next round is fully `READY` with two real players once those real matches resolve.
- All 6 pre-existing tests (bracket creation, seed separation, championship, optimistic revisions, randomize, correction+rollback) preserved unchanged and still pass.

**Result:** `npx vitest run apps/server/test/` → **58/58 passed** (7 files) — the full donor server suite, not just `bracket.test.ts`, confirming no regressions elsewhere (auth, backups, config, integration, persistence). `npx tsc --noEmit` in `apps/server` → **clean, no errors**.

## 5. Correction/rollback implementation

**Backend:** unchanged — `BracketService.correct(..., rollback)` already existed correctly (audit confirmed this).

**Client (`TournamentControlClient.CorrectAsync`):** now sends `rollbackDownstream` as an explicit parameter (previously this endpoint wasn't exposed in the VenueOS client at all).

**Decision logic (`TournamentCorrectionAnalysis.RequiresRollbackConfirmation`, new, pure, unit-tested):** walks the same linear forward chain the donor's own `descendants()` walks (`NextWinnerMatchId`), returning `true` iff any descendant match is `COMPLETED` — mirroring the backend's own safety gate client-side so the UI can decide *before* sending anything whether a plain or a destructive confirmation is needed. The backend still independently enforces the same rule server-side regardless of what the client decided.

**Operator panel (`DrawMatch`):** a completed match always shows a "Correct Result" action that flips the winner to the other contestant (matching the donor's own established one-click correction UX — no need to invent a fancier winner-picker). If `RequiresRollbackConfirmation` is false, it's a plain `GhostButton` + a non-destructive confirmation. If true, it's a `DangerButton` labeled "Correct Result (clears completed later matches)" that opens an explicit confirmation stating plainly that every already-completed downstream match will be cleared back to waiting and that this cannot be undone — only on confirming does the panel call `CorrectAsync(..., rollbackDownstream: true)`. Nothing is ever silently rolled back and nothing silently fails; a 409 from the backend (if the client's own gate somehow disagreed with the server) surfaces through the existing `Notice`/`WarningState` path.

Covered by `Correction_analysis_only_requires_confirmation_when_a_downstream_match_has_already_completed` and the client-serialization test asserting `"rollbackDownstream":true` is actually sent.

## 6. VenueOS Brackets UI/workflow implemented

Previous scaffold (per the audit): a single flat card with a text-entry "Tournament ID," Start, and "Call first ready match" — no player management, no seeding, no round view, no result recording, no correction, no champion display, no browser. Rebuilt into the full requested workflow, `Draw()`/`DrawSettings()` split per `NEW_MODULE_GUIDE.md` §8/§9:

- **`DrawSettings()`** (persistent config only): Server URL, Server Access Password, Organizer Key (all plain text — §8), Authenticate + Create Organizer actions, default game/tournament name for new tournaments, and the callout channel/lines/delay. Venue Name is explicitly *not* a field here — a `TextWrapped` note points at the active Venue Profile instead (§10).
- **`Draw()`** (live operation): a tournament **browser** (search + status filter + list, backed by the newly-added `ListTournamentsAsync`) with an inline **create** form when authenticated and no tournament is loaded; once a tournament is loaded, a **Setup** view (add/bulk-add/rename/remove/reorder-via-Up-Down/randomize players, Start gated behind a confirmation) while `SETUP`; a **round-by-round match view** (`Forms.Segmented` round tabs, one card per match showing both contestants, a Call Players action wired to the existing `TournamentCalloutService`, Win buttons behind a confirmation, bye auto-advance display, and the correction flow from §5) while `ACTIVE`; and a **champion** card (`UiKit.StatCard`) once `COMPLETED`. Cancel Tournament is available (destructive confirmation) while `SETUP`/`ACTIVE`. "Back to browser" and "Copy Public Bracket URL" round out navigation.
- Every user-facing control goes through `UiKit`/`Forms` (no raw `ImGui.Button`/`InputText`), and every color is a `ThemeTokens` reference — no hard-coded colors were introduced.

## 7. Realtime implementation

The donor's own Dalamud WebSocket client (`TournamentEventClient.cs`) was found dead — constructed nowhere, its receive loop discarding every frame. VenueOS's previous scaffold had no realtime code at all. This pass adds a genuine implementation:

- `ITournamentRealtimeTransport` (interface) + `WebSocketTournamentRealtimeTransport` (real `ClientWebSocket`, speaking the donor's version-1 authenticate/subscribe frame protocol via the existing `TournamentEventProtocol` helpers).
- `TournamentRealtimeClient` — owns connect → authenticate → subscribe → receive-loop → reconnect-with-backoff, queuing decoded frames into a `ConcurrentQueue` and exposing a one-shot-per-reconnect signal.
- `TournamentControlService.Tick(now)` drains that queue **on the Dalamud framework thread** (never from the socket's background task) by calling the existing, already-tested `ApplyEventAsync` for each frame, and forces a full `LoadStateAsync` REST refetch after every reconnect — REST remains the sole authority; the socket is a nudge, never a second source of truth, and there is no race between the socket thread and `Draw()`/`Tick()` since the socket thread only ever touches the thread-safe queue.
- `TournamentControlModule.Tick` now calls `tournament.Tick(now)` (previously a no-op).

Because the module ships disabled by default, this loop never runs unless an operator explicitly enables Brackets — consistent with `ModuleHost`'s existing enable/disable semantics.

Tested (`TournamentRealtimeClientTests.cs`, fake transport per `NEW_MODULE_GUIDE.md` §30, since a real socket can only be verified live): frames are queued in order; the first connect never raises a reconnect signal but a later one does, exactly once; authenticate/subscribe frames are sent on every connect. The real `ClientWebSocket` transport itself is, like the donor's own note in `TOURNAMENT_CONTROL_3C.md`, something only a live socket reconnect against the actual backend can fully confirm — flagged explicitly in §14, not silently assumed working.

## 8. Settings/persistence implementation

Unchanged mechanism, extended shape: `TournamentModuleSettings` persists via `VenueProfileService.GetModuleConfig`/`SaveModuleConfig(venueId, "games.tournament", 1, ...)` exactly as before — module ID and schema version untouched, so no existing venue's saved config is orphaned. The only shape change is **removing `TournamentModuleSettings.VenueName`** (the known inconsistency `NEW_MODULE_GUIDE.md` §10/§21 flagged): the venue name sent to the backend on tournament creation is now read live from `VenueProfileService.Current.DisplayName` at request time (`TournamentControlService.ActiveVenueDisplayName`), matching the precedent already set by `MairsTriviaService`. Covered by `Create_tournament_sources_venue_name_from_the_active_venue_profile_not_a_module_setting`.

## 9. Confirmation: credential fields are unmasked

**Explicitly confirmed by code review** (ImGui rendering cannot be unit-tested per `NEW_MODULE_GUIDE.md` §30 — this is a `grep`-verifiable, not test-verifiable, guarantee): `TournamentControlOperatorPanel.DrawSettings()` calls `Forms.TextField(theme, "Server access password", ref password, 256)` and `Forms.TextField(theme, "Organizer key", ref userKey, 256)` with **no `password: true` argument** — `Forms.TextField`'s `password` parameter defaults to `false`, which renders a plain, unmasked `ImGuiInputTextFlags.None` field. Neither field uses password dots, asterisks, `ImGuiInputTextFlags.Password`, or a reveal-eye toggle anywhere. Both fields are standard `ImGui.InputText` widgets under the hood, so they are inherently **selectable, copyable, pasteable, and editable** like any other text field, and every edit calls `service.Configure(...)` → `SaveModuleConfig`, so the value is **saved and persistent** across plugin reload, venue switch, and module enable/disable (per `NEW_MODULE_GUIDE.md` §22, disabling a module never touches its saved config). This is a genuine change from the audited scaffold, which previously passed `password: true` for both fields. An explanatory comment identical in spirit to Bingo's own (already-plain-text) credential fields documents why, right above the fields.

Secrets remain redacted everywhere else: every `diagnostics.RecordFailure(...)` call added in this pass wraps the backend error message in `DiagnosticsService.Redact(...)`, and no credential value is ever interpolated directly into a diagnostic, log, or error string — only `result.Error?.Message` (server-provided, non-secret error text) is logged, matching the pattern already used by `MairsTriviaService`.

## 10. Multi-venue isolation behavior

Unchanged mechanism (already correct in the prior scaffold, re-verified after the reconstruction): `TournamentControlService.Load(nextVenue)` cancels and replaces `contextCancellation`, stops the callout service, **stops the realtime client** (new — added so a venue switch can't leave a background socket authenticated against the previous venue's credentials), clears `Current`/`Notice`/`Tournaments`/`BrowserStatus`, and reloads `Settings` fresh from the new venue's stored config. `TournamentControlModule.OnVenueChangedAsync` calls this on every venue activation, including the very first one at startup. Covered by the existing (fixed, still-passing) `Venue_switch_does_not_reuse_credentials_and_callouts_use_chat_queue` test, which asserts a second venue's `AccessToken` never leaks from the first.

## 11. Build/test totals

| Repository | Command | Result |
|---|---|---|
| TournamentControl | `npx vitest run apps/server/test/` | **58/58 passed** (7 files) |
| TournamentControl | `npx tsc --noEmit` (apps/server) | Clean |
| VenueOS | `dotnet build VenueOS.sln -c Debug` | **Build succeeded, 0 warnings, 0 errors** |
| VenueOS | `dotnet build VenueOS.sln -c Release` | **Build succeeded, 0 warnings, 0 errors** |
| VenueOS | `dotnet test VenueOS.sln -c Debug` | **592/592 passed** (VenueOS.Core.Tests: 4, VenueOS.Venues.Tests: 23, VenueOS.Services.Tests: 565) |

## 12. Warnings

None in either repository, Debug or Release.

## 13. Known remaining issues / explicitly out of scope

- **No bracket-tree/grid visualization** — the round-tab list view was kept (matching the donor's own round-tab presentation) rather than building a graphical tournament tree; per the brief, "a full graphical visualization is not required if it harms usability." Not a defect, a deliberate scope choice already sanctioned by the brief.
- **The Dalamud/master-admin surfaces the donor's own client never exposed** (tournament metadata editing via `PATCH`, organizer revoke/restore, hard-delete-with-confirmation-code, audit-event browsing) were **not** added — the donor's own Dalamud plugin never built UI for these either (they're server-operator-only routes reached through the donor's separate React admin page), so adding them would be *expanding* scope beyond what "preserve the donor's client" calls for, not preserving it. Flagged here explicitly rather than silently omitted.
- **`TournamentCalloutService`'s per-match status introspection** (the donor's rich `CanSend`/`IsSending`/`GetStatus` reasons) was not rebuilt — VenueOS's existing `TournamentCalloutService` (unchanged, just relocated) only ever returned a bare success/failure `bool`, and that was already true before this reconstruction pass; the panel shows a simple local "Callout sent." / "Could not send callout" message instead. This is a pre-existing simplification in VenueOS, not a regression introduced here — noted rather than silently left unexplained.

## 14. Live QA the user should perform (17-player test first)

**Nothing above substitutes for this.** No live Dalamud/FFXIV client was available in this session — every claim above is backed by automated tests and clean builds, not a rendered ImGui session, per `NEW_MODULE_GUIDE.md` §30's own boundary. Brackets must stay `UnderDevelopment`/disabled until this pass is done.

1. **17-player regression (do this first):** Enable Brackets in Settings → Modules (temporarily, for this test). Authenticate against a real (or local) TournamentControl backend. Create a tournament, add 17 players, Start. Confirm Round 1 shows exactly 1 real, callable match and the rest auto-advanced as byes. Resolve the one real match. Confirm **all 8 Round-of-16 matches** show two real contestants each and are immediately callable/editable — none stuck disabled or mislabeled as a bye. This is the exact scenario that failed live before the fix.
2. **Normal power-of-two bracket** (e.g. 8 players): play a full bracket to a champion; confirm the champion card renders correctly.
3. **Odd entrant count** other than 17 (e.g. 5 or 9): confirm byes and the next round both behave correctly, matching the automated matrix.
4. **Correction before downstream play:** record a wrong winner, correct it with no downstream matches completed yet — confirm the plain (non-destructive) confirmation path.
5. **Correction after downstream play:** let a later match complete, then correct the earlier one — confirm the destructive `DangerButton` path appears, the warning text is accurate, and downstream matches genuinely reset to a playable state after confirming.
6. **Callout behavior:** confirm Call Players sends the configured `<1>`/`<2>` template to the configured Shout/Yell channel with the configured delay, and that the framework-thread dispatch fix from donor commit `c29e984` still holds (no chat calls fired off-thread).
7. **Plugin/module reload and resume:** disable and re-enable Brackets, or reload the plugin; confirm settings (including the plain-text credentials) and the loaded tournament's ability to resume both survive.
8. **Venue switching:** switch the active venue mid-session with a tournament loaded; confirm the tournament view clears, the realtime socket disconnects, and the new venue's own (or absent) credentials load — never the previous venue's.
9. **Settings persistence / credentials surviving reload:** set a Server URL, password, and organizer key; reload the plugin (or restart FFXIV if practical); confirm all three are still there, still in plain readable text, not reset or masked.
10. **Realtime specifically:** with the tournament open on two controllers (or one controller plus the public web page), record a result on one and confirm the other reflects it without a manual reopen, and unplug/replug network access briefly to confirm the reconnect-and-refetch path recovers cleanly.

## 15. Source control confirmation

**No commits, stages, pushes, branches, or tags were made in either repository.** All work is unstaged, modified/new files in each working tree, left for manual review:

- `C:\FFXIVplugs\tournamentcontrol` — `git status --short` shows exactly `apps/server/src/domain/bracket-service.ts` and `apps/server/test/bracket.test.ts` modified (plus the pre-existing, untouched-by-this-session `apps/dalamud.tests` build-artifact noise and `artifacts/*.zip` files already present before this work began). The user manages this repository through SourceTree, as instructed.
- `c:\FFXIVplugs\venueos` — 11 modified files and 4 new files (listed in §2), all unstaged/untracked, nothing committed.

---

## Post-QA update: the 17-player live regression PASSED

Confirmed in-game: 17 players added, Round 1 correctly produced one real match (Mika Rowan defeated Avery Quinn) plus 15 byes, and the Round of 16 became fully usable. **The critical bye-progression failure this reconstruction exists to fix is now live-verified fixed**, not just automated-test-verified. Between this confirmation and the addendum below, the donor repository's backend fix and regression matrix (§3–§4 above) were committed by the user directly (donor HEAD is now `1d0ccfa` "Updated backend for VenueOS", committed via SourceTree as expected — not by this session). The rest of the §14 live QA list (correction/rollback, callouts, venue switching, persistence, realtime) has not been reported back yet and Brackets remains `UnderDevelopment`/disabled pending that.

---

## Addendum — Manual Tournament Delete / Browser Cleanup

A narrowly-scoped post-QA improvement, requested after the 17-player pass above: organizers can now permanently delete their own old tournaments from the Brackets browser, instead of waiting out the ~30-day automatic retention window. Everything from the reconstruction above is unchanged and untouched by this addition except where noted.

### 1. Backend route added

`DELETE /api/controller/tournaments/:id` (`apps/server/src/app.ts`), inserted directly after the existing `PATCH /api/controller/tournaments/:id` (edit) route, whose exact structure it mirrors: Bearer `ORGANIZER` auth, a route-level ownership check via `dependencies.tournaments!.tournaments.findById(id)`, then a status gate, then the mutation inside `dependencies.tournaments!.transaction(...)`. No new `BracketService` method was needed — like `cancel` and `edit`, this is a tournament-level (not bracket-mutation) operation and bypasses `BracketService` entirely, exactly matching those two routes' existing pattern.

### 2. Allowed statuses

`SETUP`, `COMPLETED`, `CANCELLED` — anything that isn't `ACTIVE`.

### 3. ACTIVE rejection behavior

Returns `400 { error: { code: "INVALID_TOURNAMENT_STATE", message: "Cancel this tournament before deleting it." } }` and deletes nothing. This is a defense-in-depth backstop: the VenueOS browser never renders an enabled Delete control for an `ACTIVE` tournament in the first place (see §8), so in normal operation this path is only reachable by a race (e.g., the tournament was started by another controller between the browser loading and the click).

### 4. Ownership/auth behavior

Requires a valid organizer bearer session (`401` otherwise). A missing tournament id and an id owned by a different organizer both return the identical `404 TOURNAMENT_NOT_FOUND` — the same non-disclosing convention already used by the state/edit/cancel routes, so a probing request can't distinguish "doesn't exist" from "exists but isn't yours." No `expectedRevision` or typed public-code confirmation is required (unlike bracket mutations and the master-delete route) — deleting a non-active tournament has no stale-write hazard the way a bracket mutation does, and the master route's stronger typed confirmation remains appropriately reserved for master-admin deletion of arbitrary organizer data.

### 5. Cascade behavior

Unchanged, reused as-is: `dependencies.tournaments!.tournaments.delete(id)` is the same `DELETE FROM tournaments WHERE id = ?` the master route already uses, and the existing `ON DELETE CASCADE` foreign keys (contestants, rounds, matches, match_events all reference `tournaments(id)`) remove every dependent row in the same statement. Verified directly in the new test suite (§14) by counting raw rows in all four tables before and after deleting a tournament that has real contestants/rounds/matches/match_events, not merely trusting the schema.

### 6. Audit-event behavior

A `TOURNAMENT_DELETED` audit event is appended inside the same transaction, `tournamentId: null` with `deletedTournamentId`/`publicCode` carried in the payload instead — deliberately mirroring the master-delete route's own audit record shape, and for the same reason: `audit_events.tournament_id` cascades on `tournaments(id)`, so an event attached to the tournament being deleted would itself vanish in the same transaction, defeating the point of auditing the deletion. `organizerId` is the deleting organizer (not `null`), so the record is attributable.

### 7. Retention confirmation

**Unchanged.** `TournamentService.cleanupExpired`/`deleteExpired` were not touched. Automatic retention still only removes `COMPLETED`/`CANCELLED` tournaments after the configured retention window; `SETUP` tournaments still never auto-expire (they may now additionally be deleted manually by their organizer, which is new); `ACTIVE` tournaments remain protected from both retention and manual delete. Verified by a new test asserting that manually deleting one tournament doesn't disturb `cleanupExpired`'s behavior for a separate, still-`SETUP` tournament, plus the full pre-existing `persistence.test.ts`/`integration.test.ts` retention coverage remaining green.

### 8. VenueOS browser Delete behavior

`TournamentControlOperatorPanel.DrawBrowser` now renders a `Delete` (`UiKit.DangerButton`) beneath each tournament row. `TournamentDeleteEligibility.CanDelete(status)` (new, pure, unit-tested, in `TournamentControlClient.cs` alongside the existing `TournamentCorrectionAnalysis`) gates it: enabled for `SETUP`/`COMPLETED`/`CANCELLED`, wrapped in `ImGui.BeginDisabled` with a `UiKit.Tooltip("Cancel this tournament before deleting it.")` for `ACTIVE`. The operator never needs to open a tournament first — Delete is available directly from the browser row.

### 9. Confirmation UX

A single `ConfirmDialog.Request("Delete Tournament?", "\"{name}\"\n\nThis permanently deletes the tournament and its bracket data. This cannot be undone.", ...)` — the same shared, standard-styled `ConfirmDialog` component already used throughout Brackets (Start, Randomize, Cancel, Correct/Rollback) and the rest of VenueOS (e.g. Bingo's Room Key replacement). No typed public-code confirmation was added — that stronger mechanism stays master-admin-only, per the brief. Nothing deletes on a single click; Cancelling the dialog performs no action at all. The existing generic ImGui confirmation dialog styling was deliberately left untouched, per the brief's explicit instruction not to do the future UI-consistency pass in this task.

### 10. Selected-tournament cleanup

`TournamentControlService.DeleteTournamentAsync(id)`: on success, removes the entry from `Tournaments` locally (no extra round trip needed for the browser to update immediately), and if `Current?.Tournament.Id == id`, calls the existing `CloseCurrent()` — clearing `Current`/`Notice` and stopping the realtime client — returning the operator to the browser with no stale match/player data left rendered and no further attempt to subscribe to the now-deleted tournament's socket. Deleting an unrelated tournament while a different one is open leaves that open tournament (`Current`) completely untouched — verified explicitly in the new tests.

### 11. WebSocket cleanup/reconciliation

Covered by the same `CloseCurrent()` call in §10 — `realtime.Stop()` cancels the background receive loop and clears the message queue, so there is no continued subscription to a tournament that no longer exists. No new realtime protocol message was added (a "deleted/unavailable" push notification was considered per the brief's own suggestion to inspect whether it's actually necessary, and judged unnecessary): REST remains the fallback, exactly as designed — another controller still subscribed to a just-deleted tournament will have its next mutation attempt or reconnect-triggered refetch fail against the now-missing tournament (a `404`/backend error surfaced through the existing `Notice`/diagnostics path), which is the same reconciliation path every other stale-state case already uses. Building a bespoke delete-broadcast frame purely for this narrow cleanup feature was judged out of proportion to the improvement requested.

### 12. Files changed in TournamentControl (this addendum, on top of donor HEAD `1d0ccfa`)

- `apps/server/src/app.ts` — the new `DELETE /api/controller/tournaments/:id` route.
- `docs/api.md` — one paragraph documenting the new route's behavior and its intentionally lighter confirmation requirement.
- `apps/server/test/tournament-delete.test.ts` — new, 9 tests (§14).

### 13. Files changed in VenueOS (this addendum, on top of the reconstruction in §1–§13 above)

- `src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs` — `DeleteAsync` client method and the new `TournamentDeleteEligibility` static helper.
- `src/VenueOS.Modules.Operations/Tournament/TournamentControlService.cs` — `DeleteTournamentAsync`.
- `src/VenueOS.Plugin/TournamentControlOperatorPanel.cs` — the browser's Delete button, disabled state, tooltip, and confirmation.
- `tests/VenueOS.Services.Tests/TournamentControlTests.cs` — 6 new tests (§14).

### 14. Backend tests added/result

`apps/server/test/tournament-delete.test.ts`, 9 tests, all against the real Express app via `supertest` (matching `auth.test.ts`'s fixture style): delete own `SETUP`; delete own `COMPLETED` with full raw-row cascade verification (contestants/rounds/matches/match_events counted before and after via direct SQL, not just schema trust); delete own `CANCELLED`; refuse `ACTIVE` with `400 INVALID_TOURNAMENT_STATE` and leave it untouched; organizer A cannot delete organizer B's tournament (`404`, B's tournament intact); unauthenticated delete rejected (`401`); unknown id returns `404 TOURNAMENT_NOT_FOUND` identically to the wrong-owner case; deleted tournament disappears from the organizer's own list immediately; manual delete doesn't disturb retention behavior for an unrelated tournament. **Result: 9/9 passed.** Existing master-delete test (`auth.test.ts`, "provides safe master administration with CSRF and destructive confirmations") and the full 17-player/boundary-matrix regression suite (`bracket.test.ts`, 33 tests) were re-run unchanged and remain green — nothing about this addition touched `BracketService`, `resolveByes()`, or the master routes. **Full suite: 67/67 passed** (8 files, up from 58/58 before this addendum). `npx tsc --noEmit`: clean.

### 15. VenueOS tests added/result

Added to `TournamentControlTests.cs`: eligibility gate (`SETUP`/`COMPLETED`/`CANCELLED` → true, `ACTIVE` → false); the client sends an actual `DELETE` to the correct resource path with no body required; a successful delete removes the entry from the local `Tournaments` list, calls the client exactly once, and leaves an unrelated currently-open tournament (`Current`) completely untouched; deleting the currently-open tournament clears `Current` and stops the realtime client (`IsRealtimeConnected` becomes false); a failed delete does **not** remove the entry locally and the reconciled list still shows it (no pretending success); a delete issued after a venue switch uses that venue's own credentials, not the previous venue's. Items that are inherently ImGui-rendering concerns (confirmation dialog appears on click, Cancel truly performs no action, the button is literally disabled/hidden on screen for `ACTIVE`) are **not** unit-tested — per `NEW_MODULE_GUIDE.md` §30, there is deliberately no `VenueOS.Plugin` test project, and this addendum doesn't pretend otherwise; those remain live-QA items (§16 below). Module-enable-by-default and credential-masking behavior are unchanged by this addendum and were not re-tested — they're still covered by the pre-existing `UnfinishedModuleDefaultsTests` and the code-review confirmation in §9 of the main reconstruction report above. **Result: 6 new tests, all passing.**

### 16. Final VenueOS test count

**598/598 passing** (VenueOS.Core.Tests: 4, VenueOS.Venues.Tests: 23, VenueOS.Services.Tests: 571 — up from 592/592 before this addendum, entirely from the 6 new delete tests).

### 17. Debug build result

`dotnet build VenueOS.sln -c Debug` — **succeeded, 0 warnings, 0 errors.**

### 18. Release build result

`dotnet build VenueOS.sln -c Release` — **succeeded, 0 warnings, 0 errors.**

### 19. Exact live checks still required

1. **Old test cleanup:** open the Brackets browser, find the completed 17-player tournament, click Delete, click Cancel on the confirmation once and verify nothing happens, click Delete again and confirm, verify it disappears immediately, then reload the plugin and verify it stays gone.
2. **Active protection:** create/start a small tournament, verify Delete is disabled with the "Cancel this tournament before deleting it" tooltip while `ACTIVE`, Cancel it, verify Delete becomes available, delete it.
3. **Persistence regression:** close/reopen VenueOS (or reload the plugin) and confirm the Server URL/password/organizer key are still visible, plain text, and saved — this addendum did not touch any credential-rendering code, but it's cheap to reconfirm alongside the new feature.
4. The remaining items from §14 of the main reconstruction report above (correction/rollback, callouts, realtime reconnection, full venue-switch behavior) are still outstanding independent of this addendum.

### 20. TournamentControl git status

`git status --short`: `apps/server/src/app.ts` and `docs/api.md` modified, `apps/server/test/tournament-delete.test.ts` untracked. (The prior session's `bracket-service.ts`/`bracket.test.ts` changes no longer show as modified because the user committed them separately via SourceTree — donor HEAD is now `1d0ccfa "Updated backend for VenueOS"`, not this session's doing.)

### 21. VenueOS git status

`git status --short`: 11 files modified (10 from the original reconstruction plus `TournamentControlClient.cs`/`TournamentControlService.cs`/`TournamentControlOperatorPanel.cs`/`TournamentControlTests.cs` now carrying this addendum's changes on top), 5 untracked new files (`BRACKETS_RECONSTRUCTION.md`, `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md`, `TournamentControlService.cs`, `TournamentRealtimeClient.cs`, `TournamentRealtimeClientTests.cs`). VenueOS HEAD unchanged at `6b2948c`.

### 22. Confirmation

**No commits, stages, pushes, branches, tags, or releases were made in either repository during this addendum.** All changes remain unstaged/untracked working-tree edits, exactly as instructed.

---

## Live QA Hotfix — Organizer Delete Returned HTTP 500 for Real Existing Tournaments

Filed after live QA against real tournaments (not fresh test fixtures) found that deleting most existing tournaments failed with `games.tournament: delete failed (Tournament backend returned 500.)`, while one tournament deleted successfully. The addendum above's own test coverage (§14) did not catch this because every test there used either a trivial `SETUP`/`CANCELLED` row created directly via the repository, or a `COMPLETED` tournament with exactly one round and one match — the one shape that happens not to trigger the bug.

### 1. Exact backend exception

`SqliteError: FOREIGN KEY constraint failed`, `code: SQLITE_CONSTRAINT_TRIGGER` — thrown by better-sqlite3, not caught anywhere on the path from the route handler, propagating out as an unhandled exception that Express's default error handling turns into a bare `500`.

### 2. Exact source line/path

`apps/server/src/db/repositories.ts:51`, inside `TournamentRepository.delete(id)` — specifically the `DELETE FROM tournaments WHERE id = ?` statement (and, identically, inside `deleteExpired(...)`'s equivalent bulk `DELETE FROM tournaments WHERE (...)`). Confirmed by direct reproduction: a throwaway script imported the real `bracket-service.ts`/`repositories.ts`, built an 8-player tournament to `COMPLETED` through the actual `BracketService` API, then called `tournaments.delete(id)` inside a transaction exactly like the route does — it threw the exact exception above, with a stack trace pointing at that line.

### 3. Difference between the successful delete and the failing ones

The one tournament that deleted successfully was almost certainly a `SETUP` tournament, or a `COMPLETED`/`CANCELLED` tournament that never progressed past a single real match (e.g., a 2-player final with no further rounds). The tournaments that returned `500` were real multi-round brackets — reproduced with an 8-player (3-round, 7-match) and a 17-player (5-round, 31-match) tournament played to a real champion; both failed identically. **The number of rounds/matches is the load-bearing difference, not tournament status, `expiresAt`, revision, or anything schema-version-related** — a `COMPLETED` tournament with only one match (no `next_winner_match_id` chain, as already covered by the addendum's original test) deletes fine; a `COMPLETED` tournament with two or more rounds does not.

### 4. Whether the DB mutation occurred before the 500

**No.** SQLite's `FOREIGN KEY constraint failed` aborts and rolls back the statement that violated it. Confirmed directly: after the throw, `tournaments.tournaments.findById(id)` still returned the tournament, and every dependent row (contestants, rounds, matches, match_events) was still fully present, unchanged. The delete was atomically all-or-nothing — a clean failure, not a partial one.

### 5. Whether any failed-response tournament was actually deleted despite the 500

**No, for the same reason as #4** — nothing that received a `500` was ever actually removed. Every tournament the operator saw fail is still present in the database and safe to retry once this fix is in place; no data was lost by the buggy attempts.

### 6. Exact root cause

`matches` rows carry `ON DELETE RESTRICT` foreign keys back to `contestants` (`player1_id`/`player2_id`/`winner_id`/`loser_id`) and to themselves (`next_winner_match_id`) — deliberate, correct guards against orphaning a single match or contestant during ordinary bracket mutation. But `DELETE FROM tournaments WHERE id = ?` triggers **two separate, unordered cascade paths** at once: `tournaments → contestants` (direct `ON DELETE CASCADE`) and `tournaments → rounds → matches` (via the composite `(tournament_id, round_id)` key, also `CASCADE`). SQLite does not guarantee these two cascade paths complete in a particular order relative to each other. When the tournament has more than one round, the `contestants` cascade can reach and remove a contestant row while a `matches` row (not yet reached by the still-in-progress `rounds → matches` cascade) still references that contestant as `winner_id`/`player1_id`/etc. — at that instant the `RESTRICT` fires, and the entire statement (and therefore the whole `DELETE FROM tournaments` operation) rolls back with `SQLITE_CONSTRAINT_TRIGGER`. A single-round tournament has only one match with no chained `next_winner_match_id` reference and few enough rows that this race never manifested in the earlier addendum's testing — which is exactly why it was missed.

Verified experimentally (not just reasoned about): explicitly running `DELETE FROM rounds WHERE tournament_id = ?` **on its own, first**, cascades away every `match` (and, via `matches → match_events` `ON DELETE CASCADE`, every match event) cleanly with no conflict, because it is a single, uncontested cascade path with nothing else racing it. Only the combination of that operation happening implicitly and concurrently with the `contestants` cascade — as part of one bare `DELETE FROM tournaments` — was the problem.

### 7. Fix implemented

`apps/server/src/db/repositories.ts` — `TournamentRepository`:

```ts
private clearRounds(id: string): void { this.database.prepare("DELETE FROM rounds WHERE tournament_id = ?").run(id); }
delete(id: string): boolean {
  return this.database.transaction(() => { this.clearRounds(id); return this.database.prepare("DELETE FROM tournaments WHERE id = ?").run(id).changes === 1; })();
}
deleteExpired(retentionDays: number, nowIso = now()): number {
  const eligible = `(status = 'COMPLETED' AND completed_at IS NOT NULL AND datetime(completed_at, '+' || ? || ' days') <= datetime(?))
       OR (status = 'CANCELLED' AND cancelled_at IS NOT NULL AND datetime(cancelled_at, '+' || ? || ' days') <= datetime(?))`;
  return this.database.transaction(() => {
    const ids = this.database.prepare(`SELECT id FROM tournaments WHERE ${eligible}`).all(retentionDays, nowIso, retentionDays, nowIso) as { id: string }[];
    for (const { id } of ids) this.clearRounds(id);
    return this.database.prepare(`DELETE FROM tournaments WHERE ${eligible}`).run(retentionDays, nowIso, retentionDays, nowIso).changes;
  })();
}
```

Both methods now explicitly clear `rounds` (which deterministically cascades `matches`/`match_events`) for every affected tournament **before** deleting the tournament row(s), removing the race entirely. Each is wrapped in its own `database.transaction(...)` for atomicity; better-sqlite3 nests transactions as SAVEPOINTs automatically, so this composes safely with the outer `dependencies.tournaments!.transaction(...)` both the organizer-delete route and the master-delete route already wrap their calls in — no caller changes were needed. This is a **generic** fix at the single shared low-level method both entry points call; no tournament IDs were special-cased and no production rows were manually edited or deleted.

### 8. Schema/FK/migration issue discovered

Not a migration or schema-version problem — every tournament, old or new, uses the same schema, and the bug reproduces identically against a tournament created and completed entirely fresh within the fix-verification script. It is a **latent ordering bug in how the codebase relied on SQLite's automatic multi-path cascade**, present since the schema's original migration (`bracketSlotSchema`/`initialSchema` in `migrations.ts`) — it simply had never been exercised before, because until this delete feature existed, **no code path ever issued a bare `DELETE FROM tournaments` against a multi-round tournament at all** (this was the first time anything tried to delete a real, played-out bracket). Importantly, this means **automatic retention (`deleteExpired`) had the exact same latent bug** and would have thrown the identical unhandled `SQLITE_CONSTRAINT_TRIGGER` the first time any multi-round `COMPLETED`/`CANCELLED` tournament aged past its retention window — `startRetentionScheduler` (`apps/server/src/db/retention.ts`) calls `service.cleanupExpired()` with no `try`/`catch`, both once at startup and on every subsequent interval tick, so this would have thrown out of an uncaught `setInterval` callback in production, which Node.js treats as an unhandled exception. This was not previously identified in the original audit or reconstruction; it is now fixed as a side effect of fixing the same shared method, and is called out explicitly here since it was discovered, not something this task set out to look for.

### 9. Audit-event behavior

No change to the audit-event logic or ordering — `TOURNAMENT_DELETED` is still appended (with `tournamentId: null`) before the tournament row delete, in the same transaction, exactly as the addendum described. Verified directly: a multi-round tournament's full audit history (`CONTESTANTS_MODIFIED` × 8, `TOURNAMENT_STARTED`, `MATCH_RESULT_RECORDED` × N, `TOURNAMENT_COMPLETED`) — all of which carry the real `tournament_id` and therefore cascade on `ON DELETE CASCADE` — is now correctly and completely removed by the fixed delete, while the organizer-level `TOURNAMENT_DELETED` record (`tournament_id: NULL`) survives, unaffected by either the bug or the fix.

### 10. Realtime/post-delete behavior

Not implicated. The failure happened entirely inside the synchronous SQLite transaction, before the route ever reaches its `response.status(204).send()` or anything realtime-related; `onBracketUpdated`/realtime publication is never invoked for a delete at all (only bracket-state-returning mutations publish). Confirmed via #4/#5 above that the database was left completely untouched by every failed attempt — there was no "delete succeeded, then something afterward threw" case to reconcile.

### 11. Tests added

`apps/server/test/tournament-delete.test.ts`, two new tests, replacing reliance on the single-match case alone:
- **"deletes an organizer's own COMPLETED multi-round tournament with real match/audit history (regression for the live 500)"** — plays a full 8-player, 3-round bracket to a real champion through the actual REST/organizer flow (the same shape of flow the live tournament used), asserts more than one round and at least one `next_winner_match_id` chain exist (the load-bearing shape), asserts full audit history is present before deleting, then deletes and asserts **every** dependent table (`contestants`, `rounds`, `matches`, `match_events`, `audit_events`) is fully empty for that tournament while the organizer-level `TOURNAMENT_DELETED` record survives.
- **"automatic retention also deletes an expired multi-round COMPLETED tournament without the same regression"** — plays the same 8-player bracket to completion, backdates `completed_at`, and calls `cleanupExpired` directly, confirming the discovered retention-side bug (§8) is fixed by the same change and asserting full cascade there too.

The pre-existing single-match `COMPLETED` test, the `SETUP`/`CANCELLED`/`ACTIVE`/cross-organizer/unauthenticated/not-found/list-refresh/retention-isolation tests, the full `bracket.test.ts` 17-player/boundary-matrix suite (unchanged, still 33 tests), and the existing master-delete test in `auth.test.ts` were all re-run and remain green. Additionally, a separate one-off script confirmed the **master**-delete route (`DELETE /api/master/tournaments/:id`) — which calls the exact same fixed `tournaments.delete(id)` method — now also correctly deletes a real multi-round `COMPLETED` tournament end to end, something that would have failed identically before this fix (master delete's own existing automated test only ever exercised a trivial single-match tournament, the same gap the organizer-delete tests had).

### 12. Backend final test count

**69/69 passed** (8 files) — up from 67/67 before this hotfix, from the 2 new regression tests.

### 13. Typecheck result

`npx tsc --noEmit` (apps/server) — **clean, no errors.**

### 14. Whether VenueOS required any change

**No.** The entire bug and fix are server-side (a SQLite cascade-ordering defect inside `TournamentRepository`). VenueOS's error message — `games.tournament: delete failed (Tournament backend returned 500.)` — was reporting a genuine backend failure accurately; `TournamentControlService.DeleteTournamentAsync`'s existing behavior on failure (leave the tournament visible, reconcile via `RefreshTournamentListAsync`, report through `diagnostics.RecordFailure`) is exactly correct for a real `500` and required no change. The backend does not return a more specific structured error for this failure mode (a `500` has no application-level error code to surface, unlike the clean `400`/`404` cases), so there is nothing more specific for VenueOS to display here even after the fix — the fix simply means this path is no longer hit for legitimate deletes. VenueOS was not modified in this hotfix.

### 15. VenueOS tests/build results

Not applicable — VenueOS was not touched. Its test/build state is unchanged from the addendum above (598/598 tests, Debug and Release builds both clean).

### 16. Exact live retest required (after the user commits/pushes/deploys this backend fix)

1. Open Brackets.
2. Delete one of the exact tournaments that previously returned the `500`.
3. Verify it disappears from the browser.
4. Delete the remaining old tournaments one by one.
5. Verify each disappears without a `500`.
6. Refresh/reopen Brackets (reload the plugin or reopen the module).
7. Verify all the deleted tournaments remain gone.
8. Verify an `ACTIVE` tournament still cannot be directly deleted (Delete stays disabled with the "Cancel this tournament before deleting it" tooltip).

### 17. TournamentControl git status

`git status --short`: `apps/server/src/db/repositories.ts` and `apps/server/test/tournament-delete.test.ts` modified. (The prior addendum's `app.ts`/`docs/api.md`/`tournament-delete.test.ts` changes no longer show as modified because the user committed them separately via SourceTree — donor HEAD is now `c22803c "VenueOS Update for backend"` — not this session's doing.) Nothing staged or committed by this session.

### 18. VenueOS git status

Unchanged from the addendum above — `git status --short` shows the identical 11 modified / 5 untracked files, nothing further added or touched by this hotfix, nothing staged or committed. VenueOS HEAD unchanged at `6b2948c`.

### 19. Confirmation

**No commits, stages, pushes, branches, tags, or releases were made in either repository during this hotfix.** All changes remain unstaged working-tree edits in `TournamentControl` only, exactly as instructed.
