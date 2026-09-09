# Brackets (TournamentControl) — Donor Forensic Audit

**Status:** Audit-only. No source files were modified, staged, committed, or pushed in either repository during this audit. This document records findings only; reconstruction has not begun.

**Audit date:** 2026-09-08

**Donor repository:** `C:\FFXIVplugs\tournamentcontrol`
**Donor HEAD at time of audit:** `c29e984` — "Fix delayed callout thread dispatch" — 2026-08-29. Working tree had only build-artifact noise (`bin/`/`obj/` diffs, untracked release zips); no source changes. **This is the exact commit that was running during the live 17-participant tournament that triggered this audit** — no commits exist between it and the audit date, and VenueOS's own `TOURNAMENT_CONTROL_3C.md` independently confirms it was audited for wire-protocol compatibility against this same commit on 2026-09-03.

**VenueOS repository:** `c:\FFXIVplugs\venueos`
**VenueOS HEAD at time of audit:** `6b2948c` — "VenueOS 0.2.2 - fix manual word wrapping" — 2026-09-07. Clean working tree.

**Future visible module name:** Brackets (not yet renamed — see §11).
**Current internal module ID:** `games.tournament` (verified, unchanged).
**Current display name:** `"TournamentControl"` (verified, unchanged).

---

## 1. Donor architecture

A monorepo with four workspaces:

- `apps/dalamud` — C# Dalamud plugin, ImGui operator UI, .NET 10.
- `apps/server` — Express + `better-sqlite3` + native `ws`, TypeScript, deployed to Render.com as a **single instance** with a persistent disk (`DATABASE_PATH` is hard-locked to the mounted production path via zod validation).
- `apps/web` — React/Vite, spectator + master-admin UI only (no match-control UI).
- `packages/shared` — shared TypeScript types.

SQLite is the sole source of truth (WAL mode, explicit ordered migrations, FK cascades). Bracket format is **single elimination only** — no double elimination, round robin, Swiss, losers bracket, or best-of-series exist anywhere in the codebase. This is a genuinely simple, well-scoped bracket system; the "inflate scope" risk called out in the audit brief does not apply — donor scope should be preserved as-is.

## 2. Core workflow (as implemented, not aspirational)

Create tournament (venue/game/tournament name + ISO event date) → add players (single or bulk paste; no FFXIV target/actor entry exists anywhere) → reorder via drag/drop or explicit `PUT /seeds`, or randomize (server-side Fisher–Yates via Node's CSPRNG `randomInt`) → Start (generates the full bracket plan server-side) → operator works per-round tabs, sends a chat callout, records a winner → server auto-advances byes and is supposed to unlock downstream matches → correct a wrong result (optionally cascading a rollback) → champion is derived automatically when the final match completes → tournament ages out via retention once `COMPLETED`/`CANCELLED` (never while `SETUP`/`ACTIVE`).

## 3. Player and match models

**Contestant**: `id, tournamentId, displayName, seed, status (ACTIVE | ELIMINATED | WITHDRAWN), createdAt, updatedAt`. No FFXIV-specific fields — no world, no target/actor identity (confirmed via grep: zero references to `ClientState`/`TargetManager`/`LocalPlayer`/`.Target` anywhere in `apps/dalamud`).

**Match**: `id, tournamentId, roundId, position, player1Id, player2Id, winnerId, loserId, status, nextWinnerMatchId, nextWinnerSlot(1|2), createdAt, updatedAt, completedAt`. `status ∈ PENDING | READY | IN_PROGRESS | COMPLETED | BYE | REOPENED`. **`IN_PROGRESS` and `REOPENED` are dead enum values** — declared in the schema CHECK constraint and shared enum array, never written anywhere in `bracket-service.ts`.

**Bye state lives on the match/slot, not the player.** A bye is a Round-1 match where one of `player1Id`/`player2Id` is `null` at plan-generation time (from the seed-doubling algorithm's permutation), later collapsed by the resolver into `status = "BYE"` with a `winnerId` propagated forward via `nextWinnerMatchId`/`nextWinnerSlot`.

**Readiness is a computed, persisted field**, produced entirely by one function, `resolveByes()` in `bracket-service.ts`. Both clients (Dalamud plugin and React web app) do zero independent recomputation of readiness — they render strictly off the server's `match.status`. This matters directly for the root cause below: it means the bug is isolated to that one backend function, not the UI.

## 4. THE CRITICAL BUG — 17-participant Round-of-16 lockout

### 4.1 Symptom (as experienced live)

17 entrants → Round 1 correctly produced exactly 1 real match and 15 bye advances. Once the one real match was resolved, only that original match area remained usable; every other downstream (Round-of-16) match stayed blocked/disabled, as if those participants were still on byes. The bracket became unusable and the tournament was abandoned in favor of Challonge.

### 4.2 Root cause — confirmed by direct reproduction

A throwaway script was built importing the actual donor `bracket-service.ts` / `single-elimination.ts` against a real SQLite instance, and the exact 17-entrant flow was executed end to end (bracket generation, then recording the sole real Round-1 winner), with raw match rows inspected before and after. The corrupted state was reproduced exactly as described live (see §4.5 for the raw trace data).

The bug is a **stale in-memory snapshot inside `resolveByes()`'s inner loop**, at `apps/server/src/domain/bracket-service.ts:39`:

```ts
private resolveByes(tid: string) {
  let changed = true;
  while (changed) {
    changed = false;
    for (const match of this.persistence.bracket.listMatches(tid)) {   // one snapshot array for this whole pass
      if (match.status !== "PENDING" || !this.persistence.bracket.incoming(match.id).every(item => item.status === "COMPLETED" || item.status === "BYE")) continue;
      const winner = match.player1Id ?? match.player2Id;               // reads the STALE snapshot object
      if (match.player1Id && match.player2Id) { this.persistence.bracket.setMatch(match.id, { status: "READY" }); changed = true; continue; }
      // ...bye branch: sets status BYE, winnerId = winner, and (if winner) propagates it downstream...
```

`listMatches(tid)` is ordered `round_number, position` (`apps/server/src/db/repositories.ts:95`), so within one `for` pass, **all Round-1 rows are visited before any Round-2 row**. When a Round-1 bye resolves, the code immediately writes its winner straight into the DB row for the corresponding Round-2 match's `player1Id`/`player2Id` column (`setMatch(match.nextWinnerMatchId, ...)`), executed synchronously mid-loop.

But the Round-2 match object the loop reaches a few iterations later in this **same pass** is a different, already-fetched JS object from the top-of-pass `listMatches()` call — it still holds `player1Id: null, player2Id: null` from before any of this pass's Round-1 mutations happened. `incoming(match.id)` *is* re-queried fresh from the DB each time, so the readiness gate correctly passes — but the next line, `const winner = match.player1Id ?? match.player2Id`, reads the **stale** snapshot fields, both still `null`. `winner` evaluates to `null`, the `if (match.player1Id && match.player2Id)` check also fails against the same stale nulls, and execution falls into the bye branch: it commits `{ status: "BYE", winnerId: null, completedAt }` to a Round-2 row that, per the actual database (not the stale object), **already has two real, live contestants** sitting in `player1_id`/`player2_id` (those columns are untouched by this call — `setMatch` here only writes `status`/`winnerId`/`loserId`/`completedAt`).

The row ends up permanently: `status = "BYE"`, `winnerId = null`, both `player1Id` and `player2Id` populated with real people. Because `status` is no longer `"PENDING"`, every future `resolveByes()` pass skips this row (`if (match.status !== "PENDING") continue;`) — **it is a terminal, unrecoverable dead state**. Because the computed `winner` was `null`, the bug does not even propagate a phantom winner further downstream — that branch of the bracket simply stops advancing.

**Exact field/function responsible**: the `winner` local variable in `BracketService.resolveByes()` (`apps/server/src/domain/bracket-service.ts:39`), computed from a stale loop-snapshot `match` object instead of a freshly-read row, combined with `listMatches()`'s round-then-position ordering (`apps/server/src/db/repositories.ts:95`), which guarantees Round-1 byes are processed — and their downstream writes committed — before the same pass reaches the Round-2 rows they just fed.

### 4.3 Why Round 2 stayed disabled — not a UI bug

`ControllerMatch.Status` (Dalamud `ConnectionModels.cs:12`) is a raw passthrough string from the server. `MainWindow`'s gate — `match.Status is "READY" or "IN_PROGRESS" && first is not null && second is not null` — correctly refuses to render "CALL PLAYERS"/"X Wins" buttons for a match whose server-reported status is `"BYE"`. The client behaved exactly as designed. The Round-of-16 matches were genuinely, persistently stamped `status = "BYE"` in the database despite holding two real contestants. Reloading, reconnecting, or restarting changes nothing — the corrupted row is stable, terminal state.

This is also why "correct/reopen" was no help: `correct()` requires `match.winnerId` to be truthy, and these rows have `winnerId = null`, so even the correction path is a dead end — there was no UI affordance whatsoever for these matches, matching the reported "bracket became unusable."

**Conclusion: this is a pure backend/domain bug**, isolated entirely to `BracketService.resolveByes()`. The data model itself is sound. Neither the Dalamud plugin nor the React web app has independent bye/readiness logic to be wrong — both faithfully render whatever `status` the server computes. No client code needs to change for the core fix; it requires changing exactly one function's data-freshness handling (e.g., re-querying each match immediately before the `player1Id`/`player2Id` check, restructuring the cascade to operate match-by-match against live reads rather than a frozen array snapshot, or driving the whole cascade in a single SQL statement/transaction).

### 4.4 General trigger condition and affected entrant counts

The bug triggers whenever at least one *sibling pair* of Round-1 matches are both "one real + one bye" type, both feeding the same Round-2 slot. This happens whenever byes clearly outnumber real matches in Round 1 — i.e., for any bracket where the entrant count isn't itself a power of two and byes exceed roughly a quarter of the bracket size. **This is not a rare edge case** — for a typical live tournament (17 sign-ups is completely ordinary), it is the dominant, expected outcome.

### 4.5 Reproduction data (confirmed by direct execution against real code)

| N | bracket size | R1 real matches | R1 byes | Broken Round-2+ matches |
|---|---|---|---|---|
| 2 | 2 | 1 | 0 | 0 |
| 3 | 4 | 1 | 1 | 0 |
| 4 | 4 | 2 | 0 | 0 |
| **5** | 8 | 1 | 3 | **1** |
| 6 | 8 | 2 | 2 | 0 |
| 7 | 8 | 3 | 1 | 0 |
| 8 | 8 | 4 | 0 | 0 |
| **9** | 16 | 1 | 7 | **3** |
| 15 | 16 | 7 | 1 | 0 |
| 16 | 16 | 8 | 0 | 0 |
| **17** | 32 | 1 | 15 | **7** |
| **18** | 32 | 2 | 14 | **6** |
| **19** | 32 | 3 | 13 | **5** |
| 31 | 32 | 15 | 1 | 0 |
| 32 | 32 | 16 | 0 | 0 |
| **33** | 64 | 1 | 31 | **15** |

Exact powers of two (2, 4, 8, 16, 32) and counts with exactly one bye (3, 7, 15, 31) never trigger it. N=6 escapes it only because the standard seeding algorithm happened to interleave its two byes with real matches rather than pairing them together — this is incidental, not a guarantee, and should not be relied on.

For the confirmed-broken 17-entrant case specifically, the raw trace showed, immediately after `start()` and before any result was even recorded: Round 1 = 1 `READY` real match (pos 2) + 15 `BYE` matches (all others); Round 2 (Round of 16) = pos 1 `PENDING` (correctly waiting on the real match) but **pos 2 through pos 8 already `BYE` with `winnerId: null` despite both `player1Id` and `player2Id` populated** — i.e., the corruption exists from the moment the bracket is generated, before the operator even calls the first match.

## 5. Other confirmed issues (ranked)

- **CRITICAL** — §4 above: `resolveByes()` stale-snapshot bug. Silently and permanently corrupts downstream match state for the majority of realistic (non-power-of-two) entrant counts, with no operator recovery path. This is what ended the live tournament.
- **HIGH** — The Dalamud plugin's `SubmitWinnerAsync` hardcodes `rollback=false` on every correction call (`MainWindow.cs`). `BracketService.correct()` supports a `rollback` flag that clears the entire downstream forward chain and allows fixing an early mistake, but there is **no UI path in the plugin to ever pass `rollback=true`**. If a downstream match has already been played, an operator who needs to correct an earlier result has no working recovery path from the plugin at all.
- **MEDIUM** — `apps/dalamud/Services/TournamentEventClient.cs` (the WebSocket client) is fully dead code: constructed nowhere in `Plugin.cs`, and its receive loop discards all message content even if it were connected. The Dalamud plugin has **no realtime push at all** — unlike the web app, which genuinely does push via `/ws` — so multi-controller scenarios require a manual reload (re-opening the tournament) to see other controllers' changes.
- **MEDIUM** — Rate limiting on auth/organizer-creation endpoints is in-memory, per-process. Acceptable given the documented single-instance Render deployment; would silently stop working if ever horizontally scaled (already flagged in the donor's own `security.md`).
- **LOW** — Dead `IN_PROGRESS`/`REOPENED` status enum values in the schema; harmless but signals an incomplete/aspirational state machine.
- **Not a bug** — Plaintext, unmasked, persistent local credential storage (`ServerAccessPassword`, `UserKey` in `Configuration.cs`) is intentional and documented (`docs/dalamud.md`), with an on-screen warning label. This already matches the future VenueOS requirement (see §7).

## 6. Security/integrity summary

Overall strong: Argon2id-hashed organizer keys, HMAC lookup digests (timing-safe comparisons), opaque bearer session tokens (only digests stored server-side), a separate master-admin cookie + CSRF token gating destructive admin actions, ownership re-verified on every mutation (returns 404 not 403 to avoid disclosure), no anonymous mutation endpoints anywhere, and public tournament codes are 6-character/32-symbol CSPRNG values (~2^30 space) with internal UUIDs never exposed publicly. No findings beyond the rate-limiting note in §5.

## 7. Credentials / standing settings requirement

Every configuration field that must persist across plugin reloads (backend URL, API/admin keys, passwords, room/server keys) is enumerated in `apps/dalamud/Configuration.cs`: `ServerUrl`, `ServerAccessPassword`, `UserKey`, plus non-credential callout settings (`CalloutChannel`, `CalloutLine1`, `CalloutLine2`, `CalloutDelayMilliseconds`). All are persisted via standard Dalamud `SavePluginConfig` JSON serialization — no custom crypto, no masking, no `SecureString`. Current `MainWindow.DrawSetup` renders `ServerAccessPassword`/`UserKey` in plain, unmasked `ImGui.InputText` fields with an explicit warning label. This **already satisfies** the future VenueOS requirement that these fields be visible, selectable, copyable, pasteable, and persistent, with logs/diagnostics continuing to redact secrets (confirmed: exception handlers explicitly log "no credentials were logged").

**Deviation found in the current VenueOS scaffold**: `TournamentControlOperatorPanel.cs` currently renders its server-password and organizer-key fields with `password: true` (masked ImGui input) — the opposite of both the donor's current convention and the stated future requirement. This needs to be changed during reconstruction (align with VenueOS's Bingo module, whose equivalent backend/room/admin key fields are already plain text and unmasked).

## 8. Current VenueOS scaffold (as found — not modified)

Far more built-out than a stub:

- `src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs` — full REST + WebSocket protocol client matching the donor contract.
- `src/VenueOS.Modules.Operations/Operations.cs:700-757` — `TournamentCalloutService`, `TournamentModuleSettings`, `TournamentDashboard`, `TournamentControlService`, `TournamentControlModule` (the `IVenueModule` wrapper).
- `src/VenueOS.Plugin/TournamentControlOperatorPanel.cs` — flat match/contestant list UI with connection and auth fields (no bracket-tree/grid visualization yet — a UX gap versus the donor's round-tab view, not a functional one).
- `tests/VenueOS.Services.Tests/TournamentControlTests.cs` — 5 passing tests covering revision serialization, 409/no-retry behavior, token expiry, WebSocket protocol edge cases, and venue-credential isolation on venue switch.
- `TOURNAMENT_CONTROL_3C.md` (repo root) — design/compatibility doc, explicitly pinned to donor commit `c29e984` (the exact commit analyzed in this audit), and explicitly states VenueOS's client **does not replace** the donor's Express/SQLite/WebSocket backend — it is a compatible thin client to the same server.
- **Module ID**: `games.tournament` (verified). **Display name**: `"TournamentControl"` (verified). **Status**: `UnderDevelopment: true`, `IsEnabled = false` by default (`Operations.cs:754`) — registered but hidden from Home, visible in Settings→Modules with "Disabled" + "Under Development" badges, and explicitly excluded from `docs/USER_MANUAL.md`'s covered features (lines 9, 22, 572, 609–617).

**Critical architectural implication**: because VenueOS's Brackets client talks to the *same shared Node backend* rather than owning its own bracket-generation/bye logic, it will inherit the exact CRITICAL bug from §4 unmodified the first time anyone runs a non-power-of-two tournament through it. **The fix must land in the shared donor backend** (or be ported if VenueOS ever forks its own copy of the domain logic) — a VenueOS-side-only fix cannot correct this, since the bug is server-persisted state, not a client rendering choice.

## 9. Test coverage gap that let this ship

`apps/server/test/bracket.test.ts` has 5 tests (seed separation/deterministic byes, championship completion, optimistic revisions, randomize retains entrants, correction+rollback) but **none assert that a Round-2+ match with two real (non-bye) incoming players actually flips from `PENDING` to `READY`** once its two feeder matches resolve. The existing 17-entrant test only checks `state.matches).toHaveLength(size - 1)` and tournament status — it never inspects individual match statuses. This is the exact gap that allowed the bug to ship undetected. `apps/web/src/tournament.test.ts` similarly only covers pure formatting helpers, not bracket/bye rendering.

## 10. Reconstruction requirements

### A. MUST PRESERVE
Single-elimination-only scope; revision-based optimistic concurrency and 409 handling; SQLite backend-authoritative persistence with WAL, retention, and rolling backups; Argon2id/session/master-admin auth model; standard seed-doubling algorithm plus CSPRNG randomize; the `<1>`/`<2>` callout convention with Shout/Yell channels, sanitization, and cooldown gating; the public read-only spectator page with WebSocket push; the plaintext-visible local credential convention (already correct in the donor — VenueOS's panel just needs to stop masking it, see §7).

### B. MUST FIX
1. The `resolveByes()` stale-snapshot bug (§4) — must be fixed in the shared backend before Brackets can go live for any real-world (non-power-of-two) tournament.
2. Add regression tests asserting all Round-N matches become genuinely `READY` (not silently misclassified `BYE`) across the full boundary matrix in §4.5, not just structural match-count assertions.
3. Fix the correction/rollback flow so `rollback=true` is actually reachable from the operator UI (§5 HIGH finding) — otherwise wrong-winner recovery remains a dead end once later matches are played.
4. Explicitly decide and execute where the backend fix is applied — since VenueOS depends on the shared donor server as-is (§8), the fix belongs there, not solely in VenueOS's client code.

### C. SHOULD IMPROVE
Flip VenueOS's `TournamentControlOperatorPanel.cs` credential fields from masked to plain text; wire real realtime push into the Dalamud client (or make "manual refresh only" a deliberate documented decision rather than dead leftover code — currently `TournamentEventClient.cs` is unwired); consider a bracket-tree/grid visualization for parity with the donor's round-tab view.

### D. OPTIONAL
Venue-Profile-sourced venue/game name defaults to avoid duplicate typing per tournament; distributed-safe rate limiting if ever scaled past one server instance; remove (or properly implement) the dead `IN_PROGRESS`/`REOPENED` enum values.

### E. OUT OF SCOPE
Double elimination, round robin, Swiss, ladders, league management, registration portal, streaming integration — none exist in the donor and none were requested. Keep this a simple bracket manager.

## 11. Visible rename surface for "Brackets" (do not rename yet)

Every place the string `"TournamentControl"` or the ID `games.tournament` currently appears as a user- or maintainer-facing label:

- `ModuleDescriptor` display name at `src/VenueOS.Modules.Operations/Operations.cs:754`
- Dashboard badge text, `VenueOperationsDashboard.cs:35`
- `repo.json` plugin-repository description text
- `docs/USER_MANUAL.md` lines 9, 22, 572, 609–617 ("Under Development Modules" section)
- Root docs mentioning TournamentControl (`README.md`, `UI_STATUS.md`, etc.)
- `TOURNAMENT_CONTROL_3C.md` itself
- Test assertions on the literal string `"TournamentControl"` (`ModuleDisplayOrderTests.cs`)
- Diagnostics snapshot key label, `DiagnosticsService.cs:18`
- Type/class names (`TournamentControlModule`, `TournamentControlService`, `TournamentControlClient`, `TournamentControlOperatorPanel`) — internal identifiers, not user-visible strings; per the audit brief, preserve these unless reconstruction has a separate reason to rename them.

## 12. Required future regression matrix

Automated tests must cover all of: 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 18, 19, 31, 32, 33 participants — including the counts that already pass today (§4.5), as genuine regression protection once the fix lands. The 17-participant case is the load-bearing test and must assert exactly: Round 1 = 1 real match + 15 bye advances; after resolving the real match, Round of 16 = exactly 8 matches, all `status="READY"`, all with two real, distinct players, none left at `status="BYE"` with a null winner. Additional required coverage: wrong-result correction with downstream invalidation, final champion detection, reload/resume, duplicate names, venue switching, and announcement formatting — per the live QA plan already scoped in the originating audit request (Tests A–E), with Test C (17 players, reproducing this exact failure) blocking any release.

## 13. Files/repositories touched during this audit

None. All investigation was read-only against both repositories (`Read`, `Grep`, `git log`, `git status`, `git show`, plus running the existing test suite via `vitest`). The only files written were two throwaway TypeScript trace scripts in an isolated scratch directory outside both repositories, executed against temporary SQLite databases in the OS temp folder, used purely to reproduce and verify the bug in §4 — not to modify any tracked file. No edits, stages, commits, pushes, branches, or tags were made in either `C:\FFXIVplugs\tournamentcontrol` or `c:\FFXIVplugs\venueos`.
