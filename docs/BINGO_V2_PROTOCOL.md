# Bingo Backend v2 Protocol — Additive Extension

This documents everything added to `ffxivbingo4all/backend/server.js` for the VenueOS reconstruction. **Every existing legacy route, Socket.IO event, field name, and response shape from the forensic audit's Production Compatibility Matrix (§24) is preserved byte-for-byte** — this is a pure additive extension. Legacy standalone plugin/browser clients continue to function with zero changes and are not aware any of this exists.

## 1. Design principles

- **No new required fields on old contracts.** Every new field added to an existing response (`pot`, `lifecycle`) is additive — old clients that don't read it are unaffected.
- **No behavior change on old write paths**, with one narrow, deliberate exception: `POST /api/call-number` now validates range (1-75) and rejects non-integers before insertion, rather than only filtering on the next load. A legitimate existing caller was always sending an in-range integer; only a malformed/malicious call is newly rejected. This closes a real gap identified in the audit (§16, §17 of the forensic audit) without changing behavior for any conforming client.
- **Legacy rooms remain unlocked/legacy-accounted forever.** A room is only lifecycle-locked or paid/comp-accounted if it was created through the new `POST /api/v2/rooms` endpoint (`lifecycle: "Draft"` at creation). A room created the old way (first `host-sync` call for a new `roomCode`) gets `lifecycle: "Legacy"` and is never subject to the economics lock — this guarantees the standalone plugin's exact current behavior (mid-event price edits allowed) is untouched for anyone still using it.
- **Paid/comp accounting survives being touched by an old client.** See §3 — the merge rule preserves a seed's recorded `compCount` even when an old-shaped `host-sync` call (which knows nothing about comp cards) later updates that seed's total count.
- **New tables only where a JSON blob genuinely can't give correctness/concurrency guarantees**: payout obligations/attempts (need atomic, guarded status transitions and idempotency) and a generic idempotency-key table. Card paid/comp accounting and lifecycle stay as additive fields inside the existing `rooms.state` JSON blob — the existing design already durably persists that blob on every write, and a normalized table would add nothing but query convenience there.

**Live-QA-caught bug fix (multi-player card grants):** `mergePlayers(existingPlayers, incomingRaw)` was designed for `host-sync`, which always receives the standalone plugin's *complete* players map every sync — "replace with exactly what's given" is correct there. `POST /api/v2/rooms/:roomCode/cards` reused the same function with only a *single* seed's data as `incomingRaw`, which meant every single-player card grant silently **discarded every other previously-granted player's record** — granting a second player cards would wipe out the first player entirely. Live testing with a real multi-player room caught this (it never surfaced in earlier single-seed-per-room test coverage). Fixed: the `/cards` handler now merges the (correctly delta-preserved) single-seed result into the existing players map (`session.players = { ...session.players, ...mergedSingleSeed }`) rather than replacing the whole map. `host-sync` is unaffected and did not need this fix.

## 2. New tables (additive, `CREATE TABLE IF NOT EXISTS`, no change to `rooms`/`short_links`)

```sql
CREATE TABLE IF NOT EXISTS idempotency_keys (
  key TEXT PRIMARY KEY,
  room_code TEXT NOT NULL,
  endpoint TEXT NOT NULL,
  response_json TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS payout_obligations (
  payout_id TEXT PRIMARY KEY,
  room_code TEXT NOT NULL,
  winner_seed TEXT NOT NULL,
  winner_name TEXT NOT NULL,
  total_owed INTEGER NOT NULL,
  confirmed_paid INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'open',   -- open | paid | void
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS payout_attempts (
  attempt_id TEXT PRIMARY KEY,
  payout_id TEXT NOT NULL,
  amount INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending', -- pending | confirmed | failed | canceled | ambiguous
  note TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
```

Idempotency keys are opaque client-generated strings (VenueOS uses a GUID per logical operation — one grant, one attempt-creation, one status transition). Any v2 mutating endpoint that accepts `idempotencyKey`: on first use, performs the mutation and stores `(key, roomCode, endpoint, responseJson)`; on a repeated key, returns the stored response verbatim without reapplying the mutation. This is what makes "host clicks Payout twice", "network retry after the server already processed it", and "two hosts acting simultaneously" all safe by construction rather than by UI discipline.

## 3. Paid vs. complimentary cards (additive fields on the existing `players[seed]` object)

```
players[seed] = {
  name, count, shortCode,   // UNCHANGED legacy fields — count is now a computed mirror, see below
  paidCount,                // NEW
  compCount                 // NEW
}
```

`count` is preserved as `paidCount + compCount` so legacy readers (which only ever look at `count`) keep seeing the correct total card count.

**Merge rule** (`mergePlayerEntry(existing, incoming)`), applied whenever *any* endpoint (legacy `host-sync` or new `POST /api/v2/rooms/:roomCode/cards`) writes a `players[seed]` entry:

- If the incoming entry explicitly carries `paidCount`/`compCount` (only the new card-grant endpoint ever sends this), trust it directly: clamp each to `[0, 16]`, recompute `count`.
- If the incoming entry is legacy-shaped (`count` only — this is what the standalone plugin's `host-sync` always sends), **preserve the existing `compCount`** and attribute the entire count delta to `paidCount`: `comp = min(existingCompCount, newCount)`, `paid = newCount - comp`. A brand-new seed with no prior record defaults to `paidCount = count, compCount = 0` — exactly the audit's compatibility rule ("treating existing issued cards as paid is the expected compatibility behavior").

This means: a VenueOS host who grants a player 2 comp cards, and later an old-style sync happens to touch that same room and bumps that player's count from 3 to 4, correctly reads as "3 comp-preserved, +1 new paid card" rather than silently discarding the comp grant. Mixing the old plugin and VenueOS against the *same live room* is not a supported workflow, but it degrades safely rather than corrupting accounting if it happens.

## 4. Authoritative pot (additive `pot` object)

Added to the response of `host-sync`, `GET /api/room-state`, and `GET /api/v2/rooms/:roomCode`:

```json
"pot": {
  "paidCards": 12,
  "compCards": 3,
  "totalCards": 15,
  "currentPot": 620,
  "prizePool": 496
}
```

`currentPot = startingPot + paidCards * costPerCard`. `prizePool = round(currentPot * prizePercentage / 100)`. **Complimentary cards never appear in this formula.** This is computed fresh on every read from the authoritative `players` map — clients display it, they do not recompute an independent total (VenueOS's `VenueBingoService` reads and displays this object rather than reimplementing the arithmetic locally, matching the reconstruction's "backend is authoritative" requirement).

## 5. Lifecycle lock (additive `lifecycle` field, `"Legacy" | "Draft" | "Active" | "Closed"`)

- A room whose first write is a legacy `host-sync` call gets `lifecycle: "Legacy"` — economics are always mutable, exactly like today. No behavior change for the standalone plugin.
- A room created via `POST /api/v2/rooms` starts as `"Draft"` — economics (`costPerCard`, `startingPot`, `prizePercentage`) are mutable.
- `POST /api/v2/rooms/:roomCode/start` transitions `Draft → Active`. Idempotent (calling it again while already `Active` is a no-op 200).
- **While `Active`, any write to `costPerCard`/`startingPot`/`prizePercentage` (via `host-sync` or a future v2 settings-update call) that would actually *change* the stored value is rejected with `409 { error: "economics_locked" }`.** A write that resends the *same* value (which is what the standalone plugin always does, since it re-sends its full local state every sync) is silently accepted as a no-op — this is what makes the lock compatible with a client that doesn't know it exists.
- `POST /api/v2/rooms/:roomCode/close` transitions to `"Closed"` (distinct from legacy's hard-delete `rooms/close` — `Closed` here just stops number-calling/claims/payout-attempt-creation from being accepted; the room row itself still exists until cleanup or an explicit legacy-style delete).

## 5a. Daub acknowledgment (additive Socket.IO event, `daub_state`)

The legacy `daub_update` client→server event is unchanged in shape and validation. What's new: after persisting (or no-op'ing) a daub mutation, the server now also emits `daub_state` back to the *sending* socket only: `{roomCode, seed, cardIndex, numbers}` — the full authoritative daubed-number array for that seed+card, sent unconditionally (even when the mutation was a no-op) so a replayed/queued mutation on reconnect gets a fresh ack too.

This closes the actual root cause the forensic audit identified for daub loss (§25 of the audit): the backend already persisted daubs durably before this change, but the *browser* trusted its own optimistic DOM state, silently dropped a mutation made while disconnected (the emit was a no-op with no queuing), and on reconnect only ever *added* server-known daubs to local state rather than replacing it. The browser client (`backend/public/app.js`) now: (1) queues every daub intention locally (`pendingDaubOps`) regardless of connection state and replays anything unacknowledged once reconnected, (2) treats every `init_state` as an authoritative *rebuild* of daub state per card rather than an additive merge, and (3) reconciles a card's cells to the exact array in each `daub_state` ack, preferring a strictly newer un-acked local click over a stale ack. A legacy browser build (or a modified/older client) that never listens for `daub_state` is completely unaffected — it behaves exactly as it did before.

## 6. New endpoints

All under the existing Express app, additive routes only.

| Method & Path | Auth | Purpose |
|---|---|---|
| `POST /api/v2/rooms` | body carries `roomKey` (same trust-on-first-use model as legacy) | Create a room with a full settings snapshot (`venueName`, `costPerCard`, `startingPot`, `prizePercentage`, `gameType`, `progressive`, `letters`, colors). Sets `lifecycle: "Draft"`. |
| `POST /api/v2/rooms/:roomCode/start` | `x-room-key` | Lock economics (`Draft → Active`). |
| `POST /api/v2/rooms/:roomCode/close` | `x-room-key` | Soft-close (`→ Closed`); does not delete the row. |
| `GET /api/v2/rooms/:roomCode` | none (public, same trust model as `GET /api/room-state`) | Full authoritative snapshot: every legacy field + `pot` + `lifecycle` + `payouts[]` (obligations with their attempts) — this is what a second host reads to resume after a crash/handoff. |
| `POST /api/v2/rooms/:roomCode/cards` | `x-room-key`, body `{seed, name, shortCode?, paidCount?, compCount?, idempotencyKey}` | Grant/set a **single** seed's paid/comp card counts (§3), merged into the existing players map — every other seed's record is left untouched. |
| `POST /api/v2/rooms/:roomCode/payouts` | `x-room-key`, body `{winnerSeed, winnerName, totalOwed, idempotencyKey}` | Manual/admin obligation creation (or, if an `open` obligation already exists for that seed, return the existing). Not the normal host path — see §8a. |
| `POST /api/v2/rooms/:roomCode/payouts/sync` | `x-room-key`, body `{idempotencyKey}` | **The normal host payout path** (§8a) — creates/updates obligations for every backend-eligible Bingo caller at the backend-computed split, with no client-supplied winner or amount. |
| `POST /api/v2/rooms/:roomCode/payouts/:payoutId/attempts` | `x-room-key`, body `{amount, idempotencyKey}` | Start a new attempt. Rejected with `409 { error: "obligation_not_open" }` if the obligation is already `paid`/`void`, or `409 { error: "amount_exceeds_outstanding" }` if `amount` exceeds `totalOwed - confirmed_paid`. |
| `PATCH /api/v2/rooms/:roomCode/payouts/:payoutId/attempts/:attemptId` | `x-room-key`, body `{status, note?, idempotencyKey}` | Transition an attempt (`pending → confirmed\|failed\|canceled\|ambiguous`). Confirming does `UPDATE payout_attempts SET status='confirmed' WHERE attempt_id=? AND status='pending'` — a duplicate confirm (e.g. a retried request after a timeout) affects 0 rows and is answered from the stored idempotency response instead, so `confirmed_paid` can never be incremented twice for the same attempt. |
| `POST /api/v2/rooms/:roomCode/claims` | none (player-facing, matches `call_bingo`'s trust model) | **New, stricter bingo-claim validation** (§7). |
| `GET /api/links/lookup?room=&seed=` | `x-admin-key` (same trust boundary as `POST /api/links`) | Live-QA correction: find the most recently created short link (if any) for a given `room`+`seed`, without creating one. Returns `{ok:true, code, count}` (code/count `null` if none exists yet) — lets a client that lost its local link cache (plugin reload, resumed room) rediscover an existing player link instead of always minting a new code. Filters `short_links.payload` in JS (matching every other access to that opaque-JSON column in this file), bounded to the 2000 most-recently-created rows. |

`POST /api/call-number`, `POST /api/host-sync`, `GET /api/room-state`, `GET /api/rooms`, `POST /api/rooms/close`, `POST /api/links`, `GET /l/:code`, and every existing Socket.IO event are **unchanged** in shape and behavior (see the one narrow call-number hardening noted in §1).

## 7. Validated claim path (`POST /api/v2/rooms/:roomCode/claims`)

Body: `{seed, cardIndex, name}`. The server:

1. Confirms `seed` is a currently-allowed seed for the room (same check `call_bingo` already does).
2. Confirms `cardIndex` is within that seed's allowed card count.
3. **Recomputes the deterministic card** for `(seed, cardIndex)` server-side using a byte-for-byte port of the existing hash → Mulberry32 → shuffle algorithm (`backend/lib/cardgen.js`, transcribed from `app.js`'s `hashSeed`/`mulberry32`/`generateColumn`/`generateCard`, covered by a cross-language deterministic-vector test — see §9).
4. Cross-checks the card's non-free numbers against `session.daubs[seed][cardIndex]` (must all be daubed) **and** against `session.calledNumbers` (every daubed number must actually have been called — a client cannot pre-daub an uncalled number and claim it).
5. Confirms the daubed set actually satisfies the room's `gameType` pattern rule (line/two-line/four-corners/blackout), using the same rule shape as the browser's `cardHasBingo`.
6. On success: records the claim exactly like legacy `call_bingo` (appends to `bingoCalls`, sets `lastBingo`, broadcasts the existing `bingo_called` Socket.IO event so legacy-path viewers see it identically) **and** returns `{ok: true, validated: true, pattern: "..."}`.
7. On failure: returns `400 { ok: false, validated: false, reason: "..." }` — it does **not** touch `bingoCalls`/`lastBingo` and does **not** broadcast `bingo_called`.

The legacy `call_bingo` Socket.IO handler is untouched and remains fully permissive, for the standalone browser client and any existing player links. This is the versioned, additive path the reconstruction brief asked for rather than a breaking change to the permissive legacy one.

## 8. Payout ledger shape (returned by `GET /api/v2/rooms/:roomCode`)

```json
"payouts": [
  {
    "payoutId": "…",
    "winnerSeed": "…",
    "winnerName": "Alice Example",
    "totalOwed": 1750000,
    "confirmedPaid": 1000000,
    "outstanding": 750000,
    "status": "open",
    "attempts": [
      {"attemptId": "…", "amount": 1000000, "status": "confirmed", "note": null, "createdAt": 0, "updatedAt": 0},
      {"attemptId": "…", "amount": 750000, "status": "pending", "note": null, "createdAt": 0, "updatedAt": 0}
    ]
  }
]
```

This is exactly the shape a resuming second host needs: `outstanding` is always server-computed (`totalOwed - confirmedPaid`), never something a client derives from its own memory.

## 8a. Bingo caller de-duplication and backend-driven payout split (live-QA correction)

**The host never manually picks a winner or types a total owed.** Both `call_bingo` (legacy) and `POST /api/v2/rooms/:roomCode/claims` (v2) now reject a repeat call from a seed that already has an accepted call for the current applicable phase — `hasAlreadyCalledThisPhase(session, seed, phase)` — as a silent no-op (still answered as success, no error, no second `bingoCalls` entry, no re-broadcast). This is safe for the legacy path: the standalone plugin already only ever displays unique caller *names* (it rebuilds a `HashSet<string>` from `bingoCalls` on every poll), so removing duplicate entries server-side changes nothing it relies on.

`GET /api/v2/rooms/:roomCode` (and every v2 room-mutating response, since they all return the full snapshot) now additionally includes:

```json
"bingoCallers": [
  {"seed": "…", "name": "Kei Joi", "timestamp": 1788758945798, "phase": null}
],
"currentPrizePool": 20000000,
"splitAmount": 10000000
```

- `bingoCallers` — the de-duplicated, chronologically-ordered (oldest first), current-applicable-phase-filtered caller list. The FIRST accepted call's timestamp is what's shown — a de-duped duplicate never moves it.
- `currentPrizePool` — the donor's exact phase-aware formula (`computeCurrentPrizePool`): for a non-progressive game, the normal `pot.prizePool`; for progressive, `(phaseStartPrizePool - payouts locked in earlier phases) × current phase's split percent`, matching `GetCurrentPrizePool()`/`GetCurrentPhaseSplitPercent()` in the donor plugin exactly.
- `splitAmount` — `Math.floor(currentPrizePool / max(1, bingoCallers.length))`, the donor's exact `GetPrizeSplit()` semantics: plain integer division, remainder gil dropped, never redistributed.

`POST /api/v2/rooms/:roomCode/payouts/sync` is the endpoint that turns this into real obligations: for every eligible caller, **at most one obligation is ever auto-created**, matched by `(room_code, winner_seed)` regardless of status (not just `status = 'open'` — an earlier version of this endpoint only checked `open`, which meant a caller who'd already been fully paid got a *second, duplicate* obligation on the next sync; fixed). While that single obligation is still `open` with `confirmed_paid = 0`, its `total_owed` is kept in sync with the current split (e.g. a second caller joining changes everyone's share, or a comp-card grant changes nothing since comp cards never move the pot). The instant any payment against it — partial or full — is confirmed, it is **frozen permanently**: never retroactively shrunk, and never auto-topped-up even if the pot later grows (e.g. a third *paid* caller joins after the first two are already fully paid). A genuine top-up in that situation is a deliberate manual action via `POST /api/v2/rooms/:roomCode/payouts` (still available, intentionally separate from the normal flow), never something `sync` invents on its own.

## 9. Card-generation determinism

`backend/lib/cardgen.js` (new, CommonJS) is a verbatim transcription of `app.js`'s `hashSeed`/`mulberry32`/`generateColumn`/`generateCard` — used only by the new claim-validation path. A test (`backend/test/cardgen.test.js`, run manually since no test runner is wired into `package.json` — see the final report) asserts a fixed set of `(seed, cardIndex) → card` vectors, and the same vectors are asserted in VenueOS's C# port (`VenueOS.Modules.Operations.Bingo.BingoCardGenerator`) so all three implementations (plugin/VenueOS, browser, backend) are proven to agree.

## 10. What deliberately was not built

- No change to the legacy `rooms` table shape or the JSON blob's existing fields.
- No backend involvement in *which* roll command (`/random`, `/dice`) or announcement channel (`/shout`, `/yell`, `/party`) a host uses — those are purely VenueOS-side per-venue settings; the backend only ever sees the resulting validated number via the existing `/api/call-number`.
- No change to `admin.config.js`/`server.config.js` semantics.
- No removal of the legacy Socket.IO `daub_update`/`call_bingo` permissive behavior — see §7.
