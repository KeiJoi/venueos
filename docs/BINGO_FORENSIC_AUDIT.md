# Bingo Forensic Audit

**Status:** Audit only. No files were edited in the donor repository, the backend, the browser client, the Dropbox reference, or VenueOS during this pass.

**Date:** 2026-09-06
**Donor commit:** `6227472` (HEAD, `ffxivbingo4all`), assembly version `1.0.0.7` — same commit the existing VenueOS scaffold (`games.bingo`, "Phase 3D") was pinned against (`015d5d6` per `VENUE_BINGO_3D.md`; HEAD has moved one commit further, a "test build" with no functional diff noted in its message).

---

## 1. Executive Summary

FFXIVBingo4All is a three-part system: a monolithic Dalamud host plugin (`Plugin.cs`, 3838 lines), a Node/Express/SQLite/Socket.IO backend (`server.js`, 1230 lines), and a static browser client (`app.js`, 1101 lines) served by that backend. The backend is a thin, largely unauthenticated JSON-blob relay with **no game-rule enforcement and no pot arithmetic of its own** — it stores whatever numbers the plugin sends it. All bingo-pattern validation, card generation, pot/prize math, and payout automation live in the plugin.

Two problems were named in the audit brief; both were confirmed and root-caused:

- **Autopay can pay a winner twice.** The plugin's payout ledger (`payoutPaid`, `paidOutCallers`) lives only in the plugin's own memory, is never sent to the backend, and — critically — is explicitly **cleared** (`payoutPaid.Clear()`) when a host rejoins/resumes a room via "Server Rooms → Join/Resume". The backend still remembers *that* a player called bingo (it has `bingoCalls`), but it has and has always had **zero concept of payout at all** — no field, no endpoint, nothing. So resuming a room (which is also what happens after any plugin crash/reload followed by rejoining) makes every already-paid winner look unpaid again, and clicking "Payout" re-runs the entire automated trade flow for the full prize amount a second time. Compounding this, the automation's own "trade succeeded" signal is a *host-gil-balance-delta heuristic*, not a confirmation that the correct recipient received anything, and the `/trade` chat command carries no target argument — it trades with whoever is currently targeted, with no re-verification.
- **Free/complimentary cards are not a solvable problem in the backend, because the backend doesn't compute pot at all.** `server.js` never multiplies card count by cost; it simply stores whatever `costPerCard`/`startingPot`/`prizePercentage` numbers the plugin pushes in `host-sync`, verbatim. The pot is calculated *only* in the plugin, as `StartingPot + (GetTotalCardsSold() * CostPerCard)`, where `GetTotalCardsSold()` sums the `CardCount` of every entry in `IssuedCards` — and there is no paid/comp distinction anywhere in that structure (`PlayerData` has only `PlayerName`, `CardCount`, `ShortCode`). Every card issued today is implicitly a paid card for pot purposes. This means: the fix for "free cards must not move the pot" is a **plugin/client-side (and VenueOS-side) accounting concept**, not a backend migration — the backend is already accounting-agnostic and needs no change to support it.

Both problems trace back to the same underlying pattern: **the plugin is the sole source of truth for money-relevant state (payout ledger, paid-vs-free card status), and that state is neither persisted to disk nor synced to the backend.** Any reconstruction in VenueOS must decide, deliberately, where this state should live — see §21–23 and §29.

A local mirror of the "Dropbox" reference plugin (`_tmp_dropbox/Dropbox/`) was studied for trade-verification technique. Its most valuable lesson is a cautionary one: even this known-working plugin conflates "trade window closed" with "trade succeeded" for its own control flow, and only uses the game's actual unambiguous completion signal (the system-chat messages `"Trade complete."` / `"Trade canceled."`) for a cosmetic toast that is never wired to anything. Bingo's plugin does not even go that far — it doesn't listen for those chat messages at all. §20 documents this precisely, and §21 proposes a state machine that *does* use that signal as the completion oracle.

VenueOS's current `games.bingo` module is intentionally minimal and safe: a 122-line read-only-leaning client (`VenueBingoClient.cs` + `VenueBingoService`/`VenueBingoModule` in `Operations.cs` + `VenueBingoOperatorPanel.cs`) that does host-sync, polling, and number-calling only. It is disabled by default, marked `UnderDevelopment: true`, and its own operator panel states outright: *"Automatic trade and payout automation are intentionally not included in VenueOS."* This is confirmed correct and should remain the case until a reconstruction implementing §21's design is built and live-tested. Two prior VenueOS documents already anticipated this audit's conclusions almost exactly: `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` (payout automation deferred, citing exactly the same class of risk) and `VENUE_BINGO_3D.md` (scope of what was ported).

---

## 2. Repositories / Systems Examined

| System | Path | Role | Modified? |
|---|---|---|---|
| Donor Dalamud plugin | `C:\FFXIVplugs\ffxivbingo4all\FFXIVBingo4All.Plugin\` | Host UI, game/pot/payout logic | No |
| Donor backend | `C:\FFXIVplugs\ffxivbingo4all\backend\` | Express/SQLite/Socket.IO relay | No |
| Donor browser client | `C:\FFXIVplugs\ffxivbingo4all\backend\public\` | Player-facing card/claim UI | No |
| Dropbox reference (local mirror) | `C:\FFXIVplugs\ffxivbingo4all\_tmp_dropbox\Dropbox\` | Trade automation reference for verification technique only | No |
| ClickLib / ECommons (local mirrors) | `C:\FFXIVplugs\ffxivbingo4all\_tmp_clicklib\`, `_tmp_ecommons\` | Dependencies used by the plugin/Dropbox; browsed only for API shape (`AddonMasterBase`, `GenericHelpers`, `Callback`, `EzThrottler`) | No |
| VenueOS | `C:\FFXIVplugs\venueos\` | Target application; current `games.bingo` scaffold inspected as reconstruction scaffold only, not spec | No |

No GitHub fetch was needed for the Dropbox study — the reference the task pointed at (`github.com/kawaii/Dropbox`) already exists as a full local shallow clone at `_tmp_dropbox/Dropbox/`, which was read directly.

---

## 3. Current Architecture

```
┌─────────────────────────┐        HTTP (host-sync, call-number,        ┌──────────────────────┐
│ Dalamud Host Plugin      │        links, rooms, rooms/close)          │ Node/Express backend  │
│ FFXIVBingo4All.Plugin    │ ───────────────────────────────────────►   │ server.js             │
│ (Plugin.cs, 3838 lines)  │ ◄───────────────────────────────────────   │ + SQLite (bingo.sqlite)│
│                          │        HTTP (room-state polling, 5s)       │ + Socket.IO           │
│ - Card generation        │                                            └──────────┬────────────┘
│ - Pot/prize math         │                                                       │
│ - Number calling         │                                          Socket.IO (join_room,      │
│ - Bingo-call ingestion   │                                          call_bingo, daub_update,    │
│ - AUTOPAY (in-game trade)│                                          number_called, room_state,  │
│ - No persistence of      │                                          bingo_called, cheat_detected)│
│   game/payout state      │                                                       │
└─────────────────────────┘                                          ┌────────────▼────────────┐
                                                                       │ Browser client (app.js)  │
                                                                       │ - Deterministic card gen │
                                                                       │   from seed (must match  │
                                                                       │   plugin's algorithm)    │
                                                                       │ - Daub, claim bingo      │
                                                                       │ - No auth, seed = identity│
                                                                       └──────────────────────────┘
```

The plugin and the browser client each **independently implement the same deterministic card-generation algorithm** (FNV-1a-style seed hash → Mulberry32 PRNG → per-column Fisher-Yates shuffle) so that a card rendered in the browser from a given seed matches the card the plugin thinks that seed has. The backend never generates or stores card contents — it only stores `allowedCards: {seed: count}` and per-seed daub arrays.

---

## 4. Host Plugin

**File:** `FFXIVBingo4All.Plugin/Plugin.cs` (3838 lines) + `Configuration.cs` (54 lines). Single `sealed class Plugin : IDalamudPlugin`, no other classes except small nested types (`PayoutStage` enum, `TradeAddonHelper`, `QueuedChat`, `AdminRoomInfo`, `ProgressiveState`, `GameState`, `Mulberry32`).

Commands: `/ffxivbingo4all`, `/fb4all` — both just open the main window.

`Draw()` (Plugin.cs:479-533) runs every ImGui frame **regardless of whether the plugin window is open**: it flushes a debug chat queue, polls `/api/room-state` every 5s, and — critically — ticks the autopay state machine (`UpdatePendingPayout`) unconditionally, *before* checking `isOpen`. Closing the window does not pause an in-progress payout.

Three tabs when open: **Game** (room/player/payout controls, issued-card roster), **Server Settings** (connection + game-defaults + progressive-phase config), **UI Settings** (card/skin color editor). Plus floating windows: player card-link windows, Server Rooms (list/join/resume/close rooms), Called Balls (75-number board + call controls), Create Room popup, Payout Warning popup.

See §18–19 for the full autopay trace and §17 for every control.

---

## 5. Backend

**Files:** `backend/server.js` (1230 lines), `server.config.js`, `admin.config.js`, `package.json`.

Express + `http.Server` + Socket.IO (`cors: "*"` on both), static-serving `backend/public`, backed by a single SQLite database (`sqlite3` package, file at `server.config.js`'s `dbPath`, default `/var/data/bingo.sqlite` — a Render.com persistent-disk path, confirming the deployment target implied by an earlier commit message ("putting in exact paths for the backend for render.com usage")).

Two tables only:
- `short_links(code PK, payload JSON, created_at)` — for `/l/:code` share-link redirects. **Never cleaned up.**
- `rooms(room_code PK, room_key, state JSON, updated_at)` — **the entire room's state is one JSON blob column.** There is no normalized `players`/`cards`/`calledNumbers` schema — everything (called numbers, players, daubs, pricing, progressive state, colors, bingo-call history) lives inside `state`, read-modify-written wholesale on every mutation with no optimistic concurrency control (a genuine lost-update race exists under concurrent requests for the same room).

Auto-cleanup: rooms untouched for `roomRetentionDays` (default 30) are hard-deleted every `cleanupIntervalMinutes` (default 60) — no export, no soft delete, no warning. `short_links` is exempt from this and grows forever.

See §7 for the full API surface, §10 for auth, §14 for the pot-accounting finding.

---

## 6. Browser / Player Client

**Files:** `backend/public/app.js` (1101 lines), `index.html` (51 lines), `styles.css` (428 lines).

No login, no lobby, no server-issued player identity. Everything is driven by URL query parameters read at script load: `seed`, `count`, `letters`, `player`/`name`, `title`, `game`/`type`, `room` (defaults to `seed` if absent), `server`, and six hex color overrides. A player's "identity" and "card ownership" is *entirely* "knowledge of the URL" — there is no signed token, no cookie, no localStorage identity (localStorage is used for exactly one thing: a card-zoom-percentage UI preference, unrelated to identity).

Card generation is deterministic and **entirely client-side**, from the same seed→hash→PRNG→shuffle algorithm the plugin uses (see §12). One `fetch` call at load (`GET /api/room-state?roomCode=...&requireExisting=1`, to pre-validate the seed/count before rendering), then a single Socket.IO connection for everything else (`join_room` once, then push-only `init_state`/`room_state`/`number_called`/`bingo_called`/`cheat_detected`). There is no card-purchase UI in the browser at all — card assignment is entirely host/plugin-driven; the browser can only consume a seed it was handed and will be rejected (`cheat_detected`, or a pre-connect "CARDS DON'T EXIST" screen) if the seed isn't registered for the room.

See §13 for card/ownership detail, §16 for the claim flow.

---

## 7. Database Schema and Migrations

There are **no migrations** — the two tables are created with `CREATE TABLE IF NOT EXISTS` directly in `server.js` on every boot. There is no schema-version tracking mechanism at all.

**`rooms` table:**

| Column | Type | Notes |
|---|---|---|
| `room_code` | TEXT PK | Entirely client-chosen (the plugin generates a `Guid.NewGuid()` when creating a room) — the server never generates it. |
| `room_key` | TEXT | **Trust-on-first-use**, not a server-issued secret — whatever string the first `host-sync` call for this `room_code` supplies becomes the permanent key. |
| `state` | TEXT (JSON) | The entire room state — see §7's nested shape below. |
| `updated_at` | INTEGER (epoch ms) | Bumped on *every* mutation, including the fully unauthenticated `call-number` route — meaning even an unauthenticated write resets the 30-day cleanup clock. |

**`state` JSON shape** (as normalized by `normalizeRoomState()`, server.js:392-456):

```
calledNumbers: number[]
allowedCards: { [seed]: count }        // derived from players when players is non-empty
players: { [seed]: { name, count, shortCode } }
daubs: { [seed]: { [cardIndex]: number[] } }
lastBingo: { name, seed, phase, timestamp } | null
bingoCalls: Array<{ name, seed, phase, timestamp }>   // unbounded, never pruned except full reset
costPerCard: number        // floored, >= 0
startingPot: number        // floored, >= 0
prizePercentage: number    // clamped 0-100
gameType: string
progressive: { enabled, currentPhase, phaseStartPrizePool, phaseOneSplit, phaseTwoSplit,
               phaseThreeSplit, remainingPhaseTwoSplit, remainingPhaseThreeSplit,
               lockedPhaseOnePayout, lockedPhaseTwoPayout, lockedPhaseThreePayout }
letters: string
title: string
colors: { bg, card, header, text, daub, ball }
updatedAt: number
```

**`short_links` table:** `code` (6-char, alphabet excludes ambiguous characters, server-generated with a 5-attempt collision retry), `payload` (JSON blob of link params: seed, count, letters, player, title, room, game, six colors, server override), `created_at`.

**Nothing resembling `players`/`cards`/`purchases`/`payouts`/`winners` as separate relational tables exists.** There is no card-price field per player, no purchase-transaction record, no payout record, and — as established in §14 — no payment-status field of any kind on a card. **A "paid vs. comp card" migration has no existing schema seam to extend**; it would be new ground either way. Flagged as a Product Decision (§29).

---

## 8. API Contract

Base URL: plugin's configured `ServerBaseUrl` (default `https://ffxivbingo4all.onrender.com`).

| Endpoint | Method | Auth | Caller | Mutates | Notes |
|---|---|---|---|---|---|
| `POST /api/host-sync` | POST | Room key **embedded in JSON body** (not a header — inconsistent with every other keyed route) | Plugin (host) | Creates-or-updates room row; recomputes `players`/`allowedCards`; can wipe bingo state (`clearBingoState`) | The single endpoint that pushes card counts, pricing, progressive state, theme |
| `POST /api/call-number` | POST | **None at all** | Plugin | Appends a number to `calledNumbers` if not already present | Confirmed: no key of any kind is checked server-side; matches the VenueOS client's own observation that this route needs no key header |
| `GET /api/room-state` | GET | **None** (public) | Plugin (5s poll) + browser (once, pre-connect) | Read only | Full state snapshot including players, daubs, pricing |
| `POST /api/links` | POST | `x-admin-key` header | Plugin | Inserts a `short_links` row | |
| `GET /api/rooms` | GET | `x-room-key` header | Plugin ("Server Rooms" list) | Read only | Filtered to rooms whose stored key matches |
| `GET /api/admin/rooms` | GET | `x-admin-key` header or `?key=` query | (No caller found in plugin — admin-only surface, presumably for out-of-band ops) | Read only | Lists **all** rooms regardless of key |
| `POST /api/rooms/close` | POST | `x-room-key` header | Plugin | Hard-deletes the room row | Mismatch → `404` (inconsistent with host-sync's `403` for the same condition) |
| `POST /api/admin/rooms/close` | POST | `x-admin-key` header | (No caller found in plugin) | Hard-deletes any room by code, no ownership check | |
| `GET /l/:code` | GET | None | Browser (short-link resolution) | Read only | 302-redirects to `/index.html?<params from payload>` |
| Socket.IO `join_room` | WS | Seed-membership check only, if seed-enforcement active | Browser | Joins the Socket.IO room, sends `init_state` | |
| Socket.IO `call_bingo` | WS | Seed-membership check only | Browser | Appends to `bingoCalls`, sets `lastBingo`, broadcasts `bingo_called` | **No pattern/called-number validation at all** — see §15 |
| Socket.IO `daub_update` | WS | Seed-membership check only | Browser | Updates `daubs[seed][cardIndex]` | Never broadcast back to other clients |

---

## 9. Realtime / WebSocket Contract

Socket.IO (not raw WebSocket), CORS `"*"`.

**Client → server:** `join_room` (`{roomCode}` or `{roomCode, seed}`), `call_bingo` (`{roomCode, name, seed}`), `daub_update` (`{roomCode, seed, cardIndex, number, daubed}`).

**Server → client:** `init_state` (full snapshot, sent once to the joining socket), `room_state` (broadcast on every `host-sync`, carries pricing/theme/progressive but *not* called numbers or players), `number_called` (`{roomCode, number, calledNumbers}`, broadcast from `/api/call-number`), `bingo_called` (`{roomCode, name, seed, phase, timestamp}`), `cheat_detected` (`{reason: "invalid_seed"}`, sent to the offending socket only, on any seed-membership failure in `join_room`/`call_bingo`/`daub_update`).

The plugin itself does **not** use Socket.IO at all — it only polls `GET /api/room-state` every 5 seconds (`MaybeRefreshRoomState`, Plugin.cs:3341-3371). Only the browser client uses the socket connection. This is an important asymmetry for reconstruction: a VenueOS host module can stay HTTP-polling-only (as the current scaffold already does) without losing any host-side capability, while a future player-facing surface (if VenueOS ever serves one directly) would need Socket.IO or equivalent push.

---

## 10. Authentication / Identity

**Admin key:** one static, hardcoded, checked-into-source secret (`admin.config.js`: `adminKey: "FFXIVBingo4All"`, comment says "change before deploying" — unclear whether the live production deployment actually changed it; this audit cannot see the live deployed config and flags it as a live-verification item, §27). Checked via plain `===` string comparison (not constant-time; low practical risk for a single low-value secret, but worth noting), accepted via header **or** `?key=` query string (leaks into logs/proxies/browser history if ever used as a URL).

**Room key:** not a server-issued credential at all — it's trust-on-first-use, literally whatever string the first `host-sync` call for a room code supplies. `room_code` itself is also entirely client-chosen (the plugin uses a GUID, which is impractical to guess, but the server enforces nothing about its shape).

**Player identity (browser):** none. A name typed into a URL query param, unauthenticated, unsigned, trivially editable by the player themself. "Card ownership" is knowledge of a seed string. Two browser tabs opened with the same seed see and can mutate the same card.

**Player identity (backend's view of a "player"):** a `seed` string is the only handle the backend has — `players[seed] = {name, count, shortCode}`. The `name` field is caller-supplied and never verified against anything.

**Spoofable client-controlled fields (confirmed exploitable, not just theoretical):**
- `POST /api/call-number` — zero auth; anyone who knows a room code can inject arbitrary "called numbers."
- Socket.IO `call_bingo` — any client holding a valid seed can claim a win under any name, for any card pattern, since the server never checks the claim against its own `calledNumbers`/`daubs` records (§15).
- `host-sync`'s `players[seed].count` can be set to any integer 1-16 with no link to payment (§14) — this is also exactly the seam a future paid/comp distinction would need to extend.

VenueOS's own protocol notes (via `VenueBingoClient.cs`) already correctly reflect the header-vs-body key inconsistency (its test suite explicitly asserts `x-room-key`/`x-admin-key` are never sent together and that `call-number` sends neither) — this part of the contract is already faithfully captured in the existing scaffold.

---

## 11. Game Lifecycle

Using the plugin's own terms ("Room", not "game"; "Phase" for progressive bingo):

1. **No room** — startup default, `RoomCode` empty.
2. **Create Room** (button → `StartNewBingo()`, Plugin.cs:3199-3234) — new GUID room code, clears all local called-numbers/cards/bingo/payout state, resets progressive phase to 1, pushes initial state via `host-sync`.
3. **Player generation** (`DrawPlayerGenerator`, 714-830) — host names a player (typed, or "Use Target" pulls the in-game target's name) and a card count (1-16 slider); "Generate Link" issues/updates a seed entry and creates a short link.
4. **Number calling** — manual "Call Number" button (auto `/random 75` + parse) or "Parse /random 75" mode (any matching chat line the host types is captured). Duplicates are rejected with a red chat warning, not auto-rerolled.
5. **Bingo claim arrives** — the plugin does **not** validate claims locally; it polls `/api/room-state` every 5s and rebuilds its local `bingoCallers` set from the server's `bingoCalls` array each time (full rebuild-from-scratch, not incremental).
6. **Payout** — host targets the winner in-game, clicks "Payout" → the autopay state machine drives an in-game `/trade` to completion or failure (§18).
7. **Progressive phase lock** (progressive game type only) — "Lock Phase N" freezes the current prize pool as that phase's payout, advances the phase counter, and — notably — **clears `bingoCallers`/`paidOutCallers`/`payoutPaid`** as part of moving to the next phase.
8. **Leave Room** (`StopBingo()`, 3236-3258) — clears local state only; does **not** close the room server-side (a separate "Server Rooms → Close" action does that, easy to miss).
9. **Join/Resume** (`SelectRoom()`, 3260-3273) — re-hydrates called numbers, issued cards, pricing, progressive state, colors, and the *list* of bingo callers from the backend, but **explicitly clears `payoutPaid`** — this is the mechanical trigger for the repeat-payout bug (§19).

Transitions 2-7 are entirely plugin-controlled UI actions; the backend only stores whatever the plugin decides to sync. Transition 5's data (whether someone "won") is backend-sourced but backend-unvalidated (§15).

---

## 12. Card Model

- Standard 5×5 grid, free center space (`grid[2,2] = null`), standard 75-ball column ranges (B:1-15, I:16-30, N:31-45, G:46-60, O:61-75).
- Deterministic generation: `seed = "{masterSeed}_{cardIndex}"` → custom FNV-1a-style 32-bit hash → `Mulberry32` seeded PRNG → per-column Fisher-Yates shuffle, take first 5. **Both the plugin (Plugin.cs:3758-3832) and the browser client (app.js:540-598) implement this identically** — any reconstruction must replicate the exact hash/PRNG/shuffle bit-for-bit or generated cards will disagree between host and player views.
- 1-16 cards per player, enforced by UI clamp in multiple places, not by a single shared constant.
- Card assignment/ownership is **seed-based**, not a first-class per-player-per-card record on either end: the plugin tracks `IssuedCards: Dictionary<seed, PlayerData{PlayerName, CardCount, ShortCode}>`; the backend tracks `players: {seed: {name, count, shortCode}}`. **There is no "paid" vs. "comp" field on either side, and no per-card ID at all** — only a per-seed card *count*.
- Add/remove: "+ Card"/"- Card" in the player roster mutate `CardCount` directly (clamped 0-16); in non-progressive mode this **issues a brand-new seed** (invalidating the player's previous share link), while in progressive mode (phase > 1) it mutates the existing seed in place to preserve link continuity. Either way, a `host-sync` push follows immediately.
- No refund concept, no purchase-transaction log, no card ID reuse-prevention beyond "the seed is whatever string was chosen."
- Reconnect: the browser client's daub state survives refresh only because the server tracks daubs by seed and replays them on `init_state` — again, this is "reconnect via knowing the URL," not an authenticated session resume.

---

## 13. Card Purchase / Payment Model

**There is no purchase flow in this system at all**, on any tier:
- The browser client has no buy/request-card UI (confirmed absent from `app.js`/`index.html`).
- The backend has no payment/checkout endpoint, no price-per-card enforcement beyond storing a `costPerCard` number, and no linkage between a `players[seed].count` change and any money having changed hands.
- The plugin is where "purchase" actually happens, informally: the host manually types a card count for a named player (based on presumably out-of-band real-world payment, e.g. the player handed the host gil or real money) and clicks "Generate Link." **Nothing enforces that a card issued this way was actually paid for** — it is a purely trust-based, host-operated ledger with no receipt, no transaction ID, and no distinction from a card the host might issue as a freebie.

This is the direct cause of Special Problem #2 (§22-23): "free card" is not a state the system can express today, because "paid card" isn't a state it tracks either — every issued card is implicitly assumed paid for pot purposes (§14).

---

## 14. Prize Pot Accounting

**Server-side: none.** Confirmed by full-text search of `server.js` for `pot`/`costPerCard`/`startingPot`/`prizePercentage` — the only occurrences are normalization/clamping (floor to ≥0, clamp percentage to 0-100) and pass-through storage from whatever the plugin's `host-sync` body contains. **No multiplication of card count by cost happens anywhere on the backend.** The backend is accounting-agnostic — it just remembers three numbers the plugin tells it.

**Plugin-side (the only place pot math happens), Plugin.cs:2593-2691:**
```csharp
GetTotalCardsSold()   // sum of CardCount across all IssuedCards entries
GetTotalPot()         // StartingPot + (GetTotalCardsSold() * CostPerCard)
GetOverallPrizePool() // Round(TotalPot * PrizePercentage / 100)
GetCurrentPrizePool() // progressive-aware: phase-adjusted remaining pool, or same as overall if not progressive
GetPrizeSplit()       // GetCurrentPrizePool() / max(1, bingoCallers.Count)   -- integer division, remainder gil is dropped, unaccounted
```
Because `GetTotalCardsSold()` sums **every** `IssuedCards[seed].CardCount` with no paid/comp distinction, **any card issued today — including a hypothetical future "free" card — inflates `GetTotalPot()` and therefore `GetOverallPrizePool()`.** This is exactly the bug named in Special Problem #2, and it is entirely a plugin-side (client) computation, not a backend one.

**Two additional accounting risks found, independent of the free-card question:**
- **Mid-event price changes are not locked.** The "Game Defaults" section of Server Settings (Cost Per Card, Starting Pot, Prize Percentage, Custom Letters, Venue) is **not** wrapped in the same `inRoom`-disable guard that protects the connection-settings fields — a host can change ticket price or prize percentage while a room is live, and because the pot is recomputed live from current values (not locked at time of sale), this retroactively changes the implied pot for cards already sold.
- **Progressive phase locking is the only place a pot-derived number is ever frozen** (`LockCurrentProgressivePhase`, 1890-1924) — it snapshots `GetCurrentPrizePool()` into `LockedPhaseNPayout` at lock time. This proves the plugin *can* express "freeze this value," which is directly relevant to designing a paid/comp split that must also resist retroactive drift (§23).

---

## 15. Number Calling

Plugin-driven: manual "Call Number" (auto `/random 75` + 10s-timeout broadcast expectation) or "Parse /random 75" mode (host types the roll manually, the plugin's chat-message regex captures it). Duplicate calls are rejected with a visible chat warning ("DUPLICATE ROLL! Reroll.") rather than silently reroll — the operator must manually retry.

Server-side (`/api/call-number`, **fully unauthenticated**, confirmed no key check of any kind exists in the route handler): dedups against `calledNumbers` via a plain equality check, appends and persists if new, broadcasts `number_called`. **No range validation (1-75) or type validation happens at insert time** — a non-integer value could transiently exist in the in-memory session before being filtered out on the *next* load.

The plugin's own number history/pool logic (which 15-number range maps to which column, for the "Called Balls" 5×15 display grid) matches the same column-major layout used in card generation — consistent between the two.

---

## 16. Bingo Claim / Winner Validation

**Nobody server-side validates a bingo claim.** The Socket.IO `call_bingo` handler's only check is "is this seed one of the room's registered/allowed seeds" (the `cheat_detected`/`invalid_seed` path) — it never cross-references the claimed win against `calledNumbers` or `daubs`, and the payload itself (`{roomCode, name, seed}`) doesn't even carry which card index or pattern is being claimed, so the server has no information to validate even if it wanted to.

The browser client *does* do real client-side pre-validation (`cardHasBingo()`, gates the Call Bingo button on an actual computed line/corner/blackout match against its own daubed-cell DOM state) — but this is trivially bypassable by any modified/scripted client, and the server does not re-check it.

The plugin, in turn, trusts the backend's `bingoCalls` list completely — it has no independent validation logic of its own; it just displays whoever the backend says called bingo and lets the host manually decide whether to pay them. **The full chain — browser optimism → unvalidated server relay → plugin blind trust — means the true trust boundary for "is this a legitimate bingo" is entirely at the human host's judgment when they click Payout, not anywhere in code.** Multiple winners/ties are natively supported (a set, evenly split); there's no explicit false-bingo rejection UI on either the browser or plugin side.

---

## 17. Prize / Winner Handling

Every bingo caller is listed in the plugin's Payouts panel (Paid / Owed / Status columns), computed live from `GetPrizeSplit()` and the local `payoutPaid` ledger. A winner may in principle be paid multiple times across multiple progressive phases if they win again (this is intended — see §11's phase-lock reset of the caller/payout sets between phases) — the *unintended* duplicate-payout scenario is covered fully in §18-19.

---

## 18. Current Autopay Implementation

Trigger: `HandlePayout()` (Plugin.cs:1792-1857) — requires an in-game target that is a registered bingo caller and hasn't already reached its prize-split amount in the local `payoutPaid` ledger; computes `amountRemaining` and splits it into ≤1,000,000-gil chunks (the practical FFXIV trade-window gil limit) queued for the state machine.

**State machine** (`PayoutStage` enum, Plugin.cs:56-66, driven every `Draw()` frame by `UpdatePendingPayout()`, 2736-2905):

```
None → StartTrade → WaitTradeOpen → OpenGilInput → SetGilInput → ReadyTrade → WaitTradeClose → VerifyTradeResult
                                                                                        │
                                                        (more chunks & underpaid) ──────┘ back to StartTrade
                                                        (fully paid, or mismatch, or out of chunks) → None
```

- **StartTrade:** sends the bare chat command `/trade` (no player-name argument) — this opens a trade with **whoever is currently targeted in-game at that instant**, not with a pinned/re-verified reference to the original winner.
- **WaitTradeOpen:** polls for the "Trade" addon; 10-second timeout, aborts cleanly if it never opens.
- **OpenGilInput / SetGilInput:** ECommons `Callback.Fire` on the Trade/InputNumeric addons to open and populate the gil field with the current chunk amount.
- **ReadyTrade:** clicks the trade's own "ready" button via an `AddonMasterBase`-derived helper.
- **A separate concurrent check, `TryConfirmTradeYesNo()`** (2997-3013), runs during `ReadyTrade`/`WaitTradeClose` and clicks **"Yes" on any open `SelectYesno` addon without inspecting its text at all** — the single riskiest piece of blind automation in the pipeline; any unrelated confirmation dialog (repair, item discard, duty pop) that happens to appear during this window would be auto-accepted and misinterpreted as trade confirmation.
- **WaitTradeClose:** polls for the Trade addon to close — **no timeout exists here** (unlike `WaitTradeOpen`'s 10s), an inconsistency that can leave the automation stuck indefinitely if the UI never reports closed.
- **VerifyTradeResult — the actual "did it work" check:** purely a **host-gil-balance-delta heuristic**:
  ```csharp
  int currentGil = GetCurrentGil();  // InventoryManager->GetInventoryItemCount(1u), item ID 1 = Gil
  int actualSessionPaid = Math.Max(0, pendingPayoutStartingGil - currentGil);
  int actualPaidTotal = pendingPayoutRecordedPaidBefore + actualSessionPaid;
  ```
  Success is inferred from "my own gil count went down by roughly the expected amount" — **there is no check that the counterparty received anything, no addon/IPC read of the actual trade result, and no chat-message confirmation of any kind.** Any unrelated gil change during the verification window (a market-board sale, a repair, a completely different trade) could be misattributed to this payout.

"Cancel Pay" (the plugin's equivalent of an Abort button, 696-699 → `CancelPendingPayout()` → `ClearPendingPayout()`, 2914-2929) **only resets the C# state machine's fields** — it does not close an already-open in-game Trade window, does not retract gil already entered into the trade UI, and does not cancel an in-flight trade. If a trade window is open when Cancel Pay is clicked, it stays open in-game and the host must close/decline it manually.

---

## 19. Autopay Failure Modes

This is the crux finding of the whole audit, root-caused precisely:

**Root cause: the payout ledger is plugin-memory-only and is explicitly cleared on room rejoin, while the backend has no payout concept at all to fall back on.**

- `payoutPaid: Dictionary<string,int>` and `paidOutCallers: HashSet<string>` are constructed fresh and empty at plugin startup (`new(StringComparer.OrdinalIgnoreCase)`), live only in the running `Plugin` instance, and are **never persisted to `Configuration`** (which only saves connection settings and skin presets) and **never sent to the backend in any API call** (confirmed: none of `host-sync`, `call-number`, `links`, `rooms`, `rooms/close` carry a payout amount or paid-status field).
- **Any of the following wipes this ledger to empty:** plugin reload/`/xlrestart`, a game crash, or — the most everyday trigger — clicking **"Server Rooms" → Join/Resume** on a room, whose handler (`SelectRoom`, 3260-3273) **explicitly calls `payoutPaid.Clear()`** as part of rehydrating from the backend.
- On rejoin, `FetchRoomStateAsync` correctly restores the **list** of bingo callers (the backend's `bingoCalls` survives, since it's just names/timestamps) but the backend has **no payout-amount field to restore even if the plugin wanted to** — so `payoutPaid` comes back empty for a player who may already have been fully paid in a prior session.
- Result: the "already paid" guard in both `CanPayoutTarget` and `HandlePayout` (`paidAlready = payoutPaid.TryGetValue(...) : 0`) reads **0** for that player, the guard does not trip, and clicking Payout re-runs the **entire** automated trade flow for the **full** prize amount a second time.

**Compounding factors that make any single payout attempt independently unreliable**, which matter even without a ledger-wipe:
- The `/trade` command is target-less and never re-verified mid-flow — a target change during a multi-chunk payout (the host's target dies/clears/changes for any reason) silently redirects a later chunk to the wrong character while the ledger still credits the original winner.
- The blind `SelectYesno` auto-accept (§18) can mis-confirm an unrelated dialog as trade confirmation.
- "Success" is inferred from the host's own gil-delta, never from confirming the recipient received anything — a coincidental gil change from any other source during the verification window can be misattributed.
- `WaitTradeClose` has no timeout, so a stuck UI can hang the automation indefinitely (a hang, not a duplicate, but the likely reason hosts reach for "Cancel Pay" in the first place — which, per §18, doesn't actually stop an open trade).
- On a **mismatch** (tracked vs. observed gil delta disagree) or **running out of chunks before fully paid**, the code still writes `payoutPaid[pendingPayoutName] = actualPaidTotal` *before* surfacing the warning popup — so even the "safe" failure path can leave a possibly-wrong figure in the local ledger that later miscalibrates the already-paid guard.

**Explicit answers to the audit brief's failure-scenario checklist:**
| Scenario | What happens |
|---|---|
| Plugin crashes/reloads mid-payout | In-flight `pendingPayout*` state and the entire `payoutPaid` ledger are lost; no persistence exists for either. |
| Host rejoins/resumes a room | `payoutPaid.Clear()` runs explicitly — see above; this is the primary trigger. |
| Backend restarts | Room `state` JSON survives in SQLite (better than nothing), but it never contained payout info to begin with. |
| Host clicks Payout twice | Second click is blocked by `pendingPayoutStage != PayoutStage.None` ("Payout already in progress") **while the first is still running** — but once the first completes and updates `payoutPaid`, a third click for the same (correctly tracked, non-wiped) session is correctly blocked by the "already paid" guard. The failure mode is specifically the ledger-wipe path, not simple double-clicking. |
| Abort ("Cancel Pay") mid-flight | State machine resets; any already-open in-game trade window is left exactly as-is, unclosed, with whatever gil was already entered still sitting in it. |
| Trade partner cancels/walks away/wrong person targeted | No distinct detection exists for any of these — all collapse into "the Trade addon eventually reports not-open," at which point the gil-delta heuristic runs regardless of *why* the window closed. |
| Server request times out after the server actually processed it | Not directly applicable to payout (the backend has no payout endpoint to time out on) — but the general host-sync/call-number pattern has no idempotency key either, so a retried request after an ambiguous timeout could double-apply a state change (lower risk than the payout ledger issue, but the same category of gap). |

---

## 20. Dropbox Trade Verification Study

Studied in full (`_tmp_dropbox/Dropbox/`: `Dropbox.cs`, `TradeTask.cs`, `TaskAddItemsToTrade.cs`, `TradeQueueEntry.cs`, `TradeQueueUI.cs`, `ItemQueueUI.cs`, `Memory.cs`, `Config.cs`, `Globals.cs`, `Utils.cs`, `ItemDescriptor.cs`, `QueueEntry.cs`, `ClickGeneric.cs`).

**Headline finding:** Dropbox does not implement a rigorous exactly-once trade-verification state machine either. It is a working, human-supervised automation tool, not a proof of "how to solve this correctly" — but it does contain one precise, reusable piece of ground truth that Bingo's plugin doesn't use at all.

**Techniques found, and their applicability:**

| Technique | Location | Applicability to fixing Bingo |
|---|---|---|
| `Condition[ConditionFlag.TradeOpen]` + `TryGetAddonByName("Trade")` for trade-window detection | `TradeTask.cs:37-38,66` | **Directly applicable** — cheap, reliable "is a trade window open" signal; Bingo's plugin already does the equivalent via its own addon lookups. |
| Implicit task-queue "state machine" (an `ECommons.TaskManager` coroutine of polling steps) | `TradeTask.cs:23-35` | **Pattern is useful, but has no named success/cancel/timeout states** — Bingo's own explicit `PayoutStage` enum is already a step better than Dropbox's unstructured queue in this one respect. |
| Target re-derivation by name string every poll, **never pinned/re-verified once trading starts** | `TradeTask.cs:40-62` | **This is a gap in Dropbox itself, not a solved problem** — Dropbox has exactly the same "no re-verification of who I'm trading with" weakness Bingo has. Do not copy this; Bingo needs to do *better* than Dropbox here (pin an object/entity reference at trade-open time and re-check it at close). |
| Native `OfferItemTrade` signature hook / `Callback.Fire` for gil-input, no post-offer confirmation | `Memory.cs:28-36`, `TradeTask.cs:64-97` | **Mechanism is reusable; the missing verification is not** — Dropbox never confirms an offer actually registered before proceeding, same class of gap as Bingo's blind `ReadyTrade` click. |
| `ConfirmAllowed` — a single global bool meaning "automation may click confirm now" | `TradeTask.cs:20` | **Does not solve local-vs-remote-vs-executed disambiguation** — it's an automation-permission flag, not a trade-state signal. Not a template to copy. |
| **`"Trade complete."` / `"Trade canceled."` system-chat messages — the game's own unambiguous ground truth for trade outcome** | `Dropbox.cs:99-112` | **The single most important, directly applicable finding of this study.** This is exactly the signal that should gate "mark this payout as done, dequeue, never retry" — and Dropbox itself captures it but wires it to nothing more than a cosmetic toast, never to its own task queue. |
| `WaitUntilTradeNotOpen` used as the task queue's actual completion gate | `TradeTask.cs:38` | **This is very likely the same category of bug as Bingo's own gil-delta heuristic** — both treat "the window closed" (true on success *and* cancellation/timeout) as if it meant "succeeded." This is the anti-pattern to explicitly avoid, not a technique to reuse. |
| `AbortOnTimeout=true` + per-action throttles | `Dropbox.cs:36-39` and various `EzThrottler`/`FrameThrottler` calls | **Directly applicable** general pattern for bounding stuck automation — Bingo's `WaitTradeClose` (no timeout at all) should adopt an explicit, tuned timeout the way Dropbox's `WaitTradeOpen`-equivalent already does. |
| Per-entry `GUID` on a queued trade, but never checked against the session that actually closed | `TradeQueueEntry.cs:12` | **Needs real adaptation** — a per-attempt identity exists in Dropbox only for UI list-tracking and removal-after-attempt, not for verifying the *correct* trade closed. Bingo needs a true per-attempt generation/nonce that IS checked at completion time, which neither codebase currently has. |
| Dequeue-as-*last*-step, single global serialized task runner | `TradeTask.cs:33`, `TradeTask.cs:16` | **Prevents automatic re-processing only because nothing re-triggers it, not because success was verified first** — this happens to avoid *automatic* duplication in Dropbox's manual-tool context, but it is not the same guarantee as "never mark paid unless we have positive confirmation," which is what Bingo actually needs given its ledger-wipe-on-rejoin failure mode (§19). |
| Dual uncoordinated polling loops (`Framework.Update` for UI clicks, `TaskManager` for the step queue), with the one accurate signal (chat event) sitting in a third, disconnected handler | `Dropbox.cs:40,115-175` | **Not a pattern to replicate** — the disconnection between the accurate signal and the control-flow loop is exactly the structural mistake to design around in §21. |
| Operator UI: step-count/current-task display; "Stop" = `TaskManager.Abort()` only, does not close an open trade or retract offered items | `TradeQueueUI.cs:16-24`, `ItemQueueUI.cs:15-23` | **Cautionary note, not a positive template** — same "abort doesn't actually undo in-game state" gap Bingo's own "Cancel Pay" has (§18). Worth citing as evidence this is a common, easy-to-miss gap in this whole class of automation, not unique to Bingo. |

**Conclusion carried into §21:** the correct design is not "port Dropbox's trade code" — it is "take the one piece of ground truth Dropbox captures but discards (`Dropbox.cs:99-112`'s chat strings) and, unlike either codebase studied here, actually make *that* the gate for the irreversible 'mark paid, never retry' transition," combined with a real per-attempt identity that Bingo's plugin also currently lacks entirely (§19).

---

## 21. Recommended Safe Autopay State Machine

**This is a target design recommendation only — nothing here has been implemented.** It should be validated in-game (§27) before being built.

Design principles driven directly by §18-20's findings:
1. **The completion signal must be the game's own unambiguous chat message** (`"Trade complete."` / `"Trade canceled."`, confirmed present and reliable per the Dropbox study), not "the addon closed" and not "my gil balance changed by roughly the right amount." Both of Bingo's current signals (gil-delta) and Dropbox's own control-flow signal (addon-closed) conflate success with cancellation/timeout; the chat message does not.
2. **Every payout attempt needs a durable, unique identity** (a persisted attempt ID, not just a player-name-keyed dictionary) so that "has this specific attempt already succeeded" is answerable independent of process memory.
3. **Payout completion must be persisted somewhere that survives plugin reload/room-rejoin** — in-memory-only state is the direct cause of §19's bug. Whether that's local disk (Dalamud plugin config) and/or a real backend field is a product decision (§29) — but it must not be *only* in-process RAM, which is what causes today's bug.
4. **No automatic retry after an ambiguous outcome.** If the completion signal is inconclusive (timeout, mismatched gil delta, unexpected chat state), stop and require a human decision — never auto-retry a payout whose prior outcome isn't positively known, because a retry after an unconfirmed "maybe it worked" is exactly how a double-payment happens.
5. **Target identity must be pinned and re-verified**, not re-derived by name every poll (Dropbox's own gap, §20) and not sent target-less (Bingo's current `/trade` with no argument, §18).

Proposed states (names illustrative, following the audit brief's example shape but adapted to these findings):

```
Idle
  → LocateWinner            (resolve + pin target identity: name+world or entity reference, captured once)
  → InitiateTrade           (send /trade <pinned target>, not bare /trade)
  → VerifyTradePartner      (confirm the opened Trade addon's counterparty matches the pinned target
                              before entering any gil — abort the attempt, do not silently proceed, if not)
  → EnterGil
  → ConfirmOffer            (verify the offered-gil value shown in the addon matches what was entered,
                              before clicking ready — do not trust the Callback.Fire call succeeded silently)
  → AwaitRemoteConfirmation (addon-state polling only — this stage does NOT mark anything paid)
  → AwaitCompletionSignal   (wait specifically for the "Trade complete."/"Trade canceled." system chat
                              message, scoped to this attempt's time window; bounded timeout)
       ├─ "Trade complete." received → VerifyCompletion
       ├─ "Trade canceled." received → Failed (not paid; safe to retry from LocateWinner, this attempt's
                                         gil never left, or did — see below)
       └─ timeout with no chat message → Ambiguous (STOP — do not guess; surface to host for manual
                                         confirmation, per principle 4)
  → VerifyCompletion        (cross-check gil-delta as a secondary corroboration only, never the primary
                              signal; a mismatch here downgrades to Ambiguous, not to "assume success")
  → MarkPayoutComplete      (persist: attempt ID, recipient identity, amount, timestamp — durably,
                              before touching any in-memory "paid" set)
  → Done
```

Abort semantics: an explicit Abort control must (a) stop the automation from proceeding to the next stage, (b) attempt to actively close/decline the open trade window if one is open (not merely stop driving it, as today's "Cancel Pay" does), and (c) record the attempt's final known stage for audit/recovery rather than silently discarding it.

Reload/restart behavior: on plugin startup (or room-resume), any attempt not durably marked `MarkPayoutComplete` should be treated as **unknown, not as unpaid** — surfaced to the host for manual reconciliation, never silently re-attempted. This is the direct fix for §19's root cause: today, "unknown" is silently treated as "unpaid," which is what causes the duplicate.

Backend acknowledgment (optional but recommended if the backend is ever extended, §29): a `payouts` concept the plugin can report a completed payout to would let a resumed session ask the backend "was this already paid" instead of relying solely on local persistence — but per this audit's read-only mandate on the donor backend, this is a *future* extension, not something to assume exists.

Manual fallback: every stage should be able to hand off to "host completes this by hand, mark it resolved," matching the existing (if incomplete) "Payout Warning popup, complete the remainder manually" pattern already present in the donor plugin — that instinct was correct, it just isn't backed by a durable ledger today.

Logging/audit trail: each attempt's full stage history (with the pinned target identity, amount, and the specific completion signal observed) should be retained, not just the final paid/unpaid boolean — this is what would let a host or developer actually diagnose a disputed payout after the fact, which today's system cannot do at all (no such log exists).

---

## 22. Free Card Requirement Analysis

Restating §13-14's findings against the specific questions posed:

- **Card price configuration:** a single global `CostPerCard` per room (plugin `GameState.CostPerCard`, synced to backend `costPerCard`) — not per-card, not per-player, not per-transaction.
- **Player card count storage:** `IssuedCards[seed].CardCount` (plugin) / `players[seed].count` (backend) — a plain integer, no metadata about how it was acquired.
- **Purchase recording:** none — no transaction log anywhere in either codebase.
- **Total paid cards / pot calculation:** `GetTotalPot() = StartingPot + (GetTotalCardsSold() * CostPerCard)`, entirely plugin-side; **the backend performs no such calculation at all.**
- **Is pot derived or persisted?** Derived, recomputed on every access, on the plugin side only. The backend persists the three raw inputs (`costPerCard`, `startingPot`, `prizePercentage`) but never a computed pot value.
- **Does adding/removing a card directly mutate a persisted pot value?** No — it mutates a card count, and the pot is recomputed from that count on next read. This matters: it means a naive "just don't let free cards touch the pot field" fix is not applicable, because there is no pot field to protect — the fix has to be in how the *card count* is categorized before it ever reaches `GetTotalCardsSold()`.
- **Are all cards currently implicitly considered paid?** Yes, unconditionally — confirmed no paid/comp/free field exists in `PlayerData` (plugin) or `players[seed]` (backend).
- **Does the backend schema support transaction records?** No — confirmed in §7, no such table or field exists, and none was removed/deprecated (there's no vestigial column either — this was simply never modeled).
- **Existing comp/free/admin-granted concept?** None, on either side.
- **Refunds/removals:** "- Card" simply decrements the count (and in non-progressive mode, reissues a new seed) — indistinguishable from "this was always meant to be one fewer card," with no refund-vs-original-purchase distinction.
- **Browser display of ownership:** the browser has no concept of payment status either — it just renders whatever card(s) a seed is allowed, with no "this one's complimentary" indicator.
- **Manual adjustments exposed?** Only via the card-count +/- controls — a host *can* informally "grant" a free card today by just adding to a player's count, but doing so silently inflates the paid pot, which is precisely the bug being asked to fix.
- **Does the API assume `card count × price = pot`?** The *plugin* does, unconditionally, for every card. The *backend* assumes nothing — it does no such multiplication anywhere.

**Every place that would need to understand paid-vs-comp, if this were built:**
1. Plugin: `IssuedCards`/`PlayerData` shape — needs a payment-status field (or a parallel comp-count field) per seed.
2. Plugin: `GetTotalCardsSold()`/`GetTotalPot()` — must sum only paid cards for pot math, while still issuing normal playable cards for comp counts.
3. Plugin UI: the "+ Card / - Card" controls and the player roster display — need a way to add a card as comp vs. paid, and to show the distinction to the host.
4. Backend (if a backend-authoritative model is chosen, §29): `players[seed]` shape would need the same distinction added — but note this is a **backend schema addition** the current server has never had, not a migration of existing data (there's nothing to migrate; every existing card is implicitly "paid" and would default to that under an additive field).
5. Browser client: not strictly required for the pot-correctness fix itself (the browser doesn't compute pot totals it treats as authoritative for payout — it only *displays* the plugin/backend-sourced pot numbers for player-facing informational purposes), but would need updating if the player-facing "Prize Pool" display should ever reflect only paid contributions distinctly from a comp indicator on the card itself.

---

## 23. Recommended Paid vs. Complimentary Card Model

**Hard product rule restated and confirmed satisfiable:** free cards must not increase the paid pot; removing a free card must not decrease it. Given §22's findings, this is achievable as an **additive change with no backend migration risk**, because the backend currently stores no pot-derived value at all — only the three raw inputs, which a reconstruction can continue to compute correctly on the authoritative side as long as *that* side's card-count summation excludes comp cards.

Two candidate shapes, evaluated against what actually exists today (per the audit brief's instruction to base this on existing schema/contracts, not preference alone):

**Option A — per-seed paid/comp counters** (`paid_cards` + `comp_cards`, `total_cards = paid_cards + comp_cards`):
- Closest fit to the existing `IssuedCards[seed].CardCount` shape — a minimal, additive change: split one integer into two.
- `GetTotalCardsSold()` becomes "sum of `paid_cards` only" for pot math, while card issuance/generation continues to use `total_cards` (comp cards are fully playable, just excluded from pot math) — satisfying "the player should still receive/use the free Bingo card normally."
- Weakness: loses per-grant history (when/why a card became comp) — fine if the product need is purely "don't inflate the pot," insufficient if a future need is "show an audit trail of who got a comp card and when."

**Option B — card grants/purchase events with a `source` field** (`paid | complimentary | refund | admin_adjustment`):
- A better fit if any auditability/history requirement exists (matches the general shape of a transaction ledger, which is also what §19's payout-tracking fix independently needs — "attempts with a durable identity and a status," a very similar shape to "grants with a source and a status").
- Heavier to build: there is currently *no* existing event/transaction table on either side to extend — this would be new modeling on both the plugin and (if backend-authoritative) the server, not a natural extension of `players[seed].count`.
- `total_cards` becomes a derived sum over ungranted-vs-revoked events for that seed, and `paid_cards` becomes a filtered sum by `source == "paid"` — more flexible, more moving parts.

**Recommendation for VenueOS reconstruction:** given production compatibility is the overriding constraint (per the audit brief) and the backend does no accounting today, **Option A is the lower-risk starting point** — it maps directly onto the existing `players[seed].count` field with one additive split, requires no new backend concept, and directly satisfies the hard product rule without touching anything the current production backend or any existing player relies on. Option B's richer audit trail is a reasonable *later* evolution once the accounting side has real production usage, and notably shares its "identity + status + never-touched-after-the-fact" shape with §21's payout-ledger recommendation — if VenueOS ever builds a proper backend-side accounting/ledger service, designing it to serve *both* needs (comp-card grants and payout attempts) at once would avoid building two parallel ledger concepts.

**Authoritative side:** given the current backend performs zero accounting and the plugin performs all of it today, **plugin-authoritative accounting (mirroring today's actual behavior) is the pragmatic default**, with the caveat that this preserves today's real weakness: nothing stops a differently-configured VenueOS instance or a manually-crafted `host-sync` call from expressing a card count VenueOS's own UI would never produce, since the backend still doesn't validate the paid/comp split either way. A backend-authoritative model would be more correct but is a genuine backend protocol extension, not a client-side reconstruction detail — flagged as a product decision (§29), not decided here.

---

## 24. Production Compatibility Matrix

| Contract | Classification | Notes |
|---|---|---|
| `POST /api/host-sync` request/response shape, field names | **A — must preserve exactly** | Live production hosts depend on this exact JSON contract. |
| `POST /api/call-number` — no auth, `{roomCode, number}` body | **A — must preserve exactly** | Confirmed the real client (both donor plugin and current VenueOS scaffold) sends no key header here; changing this breaks compatibility with the live server. |
| `GET /api/room-state` response shape | **A — must preserve exactly** | Both the plugin and browser client depend on this exact shape; also the only read path a resumed/reconstructed host module has. |
| `x-room-key` / `x-admin-key` header names and their per-route usage | **A — must preserve exactly** | Already correctly modeled in the current VenueOS `VenueBingoClient`. |
| Socket.IO event names/payloads (`join_room`, `call_bingo`, `daub_update`, `init_state`, `room_state`, `number_called`, `bingo_called`, `cheat_detected`) | **A — must preserve exactly**, for any component that talks to a live production backend as a browser-equivalent client | VenueOS does not currently implement a Socket.IO client at all (host-only, HTTP-poll-only) — if VenueOS ever serves its own player-facing surface, it would need to speak this exact protocol to interoperate with the existing production backend. |
| Card seed → hash → PRNG → shuffle algorithm | **A — must preserve exactly** | A one-bit difference produces a completely different (and mismatched-with-the-official-browser-client) card layout. |
| `room_code`/`seed`/`roomKey` as opaque client-chosen strings (no server-side generation contract to honor) | **B — can extend additively** | Nothing currently depends on any particular generation scheme beyond "a string"; VenueOS is free to generate these however it likes as a *client*, as long as it doesn't assume the server will generate or validate their shape. |
| `players[seed]` / `IssuedCards[seed]` shape (`name`, `count`, `shortCode`) | **B — can extend additively** | A paid/comp split (§23) is additive on top of this — existing fields are untouched, a new field/counter is added. |
| Payout tracking / ledger | **C — internal-only, currently client-side only** | The backend has no opinion on this at all today; VenueOS is free to design this however it wants (§21) without any backend compatibility constraint, unless a future backend extension is deliberately added (in which case that extension becomes a new **A** contract going forward). |
| `admin.config.js`'s static admin key value | **D — unknown, needs live verification** | Cannot be confirmed from source alone whether the live deployment changed the checked-in default; treat as sensitive until confirmed either way (§27). |
| Whether `/var/data/bingo.sqlite`'s actual production deployment matches the `server.config.js` default exactly | **D — unknown, needs live verification** | Relevant only if any reconstruction work would ever need to reason about the live database directly (not expected for a client-side VenueOS module, but noted). |
| Room auto-cleanup timing (30 days / 60-minute sweep) | **B — can extend additively / D for exact live values** | The *existence* of cleanup is a fact any long-lived VenueOS-created room must respect (a room can vanish if untouched for 30 days); the exact live configured values should be confirmed if this ever becomes operationally important. |

---

## 25. Failure / Recovery Behavior

Consolidating §19's payout-specific table with the broader system:

- **Plugin crash/reload:** all in-memory game state (`GameState`, called numbers, issued cards, pricing, progressive state, the entire payout ledger) is lost; only connection settings and skin presets survive (§10 of the plugin section / Configuration.cs). Recovery is exclusively via "Server Rooms → Join/Resume," which re-hydrates most state from the backend **except payout amounts**, which the backend never had.
- **Backend restart:** room state survives (SQLite), so a backend restart is comparatively low-risk — called numbers, players, daubs, pricing, and bingo-call history all persist. Payout state was never there to lose.
- **Browser disconnect:** Socket.IO client-side auto-reconnect behavior applies; on reconnect, a fresh `init_state` re-syncs everything the server knows (including daubs) — but any daub made *during* the disconnect window was applied optimistically to the local DOM and silently never reached the server (the emit no-ops when disconnected), and is not corrected by the next `init_state` (which only adds, never removes/resets) — a genuine, if minor, discrepancy risk on the browser side.
- **Duplicate player join (same seed, two browser tabs):** both tabs render identically (deterministic generation from the same seed) and both can daub/claim independently; there's no cross-tab sync of daub state except via a fresh `init_state` on each tab's own reconnect.
- **Host changes zones/characters:** not specifically guarded against anywhere in the plugin — the payout automation's target-identification is entirely "whatever is targeted right now," so a zone/character change mid-payout has the same risk profile as any other target-change scenario (§19).
- **Trade window closes for any reason (success, cancel, partner walks away, wrong person targeted):** all collapse into the same "Trade addon reports not-open" signal in the current implementation — no distinct handling exists for any of these distinct real-world causes (§18-19).
- **Gil entry succeeds but completion is ambiguous:** today, this is not even a distinguishable state — the gil-delta check either roughly matches (treated as success) or doesn't (mismatch popup, but the local ledger is still written with the observed figure first, per §18's closing note).
- **Server request times out after the server actually processed it:** no idempotency key exists on any mutating endpoint (`host-sync`, `call-number`), so a naive client-side retry-on-timeout could, in principle, double-apply a change — lower severity than the payout ledger issue but worth noting for any reconstruction that adds retry logic.
- **Host clicks Payout twice:** correctly blocked while the first attempt is in flight (`pendingPayoutStage != None` guard); the real risk is not simultaneous double-clicks but the ledger-wipe-then-single-click scenario documented in §19.
- **Abort at each payout phase:** uniformly "reset local state machine fields only" — never closes an open trade, never retracts entered gil, regardless of which stage it's invoked from (§18).

---

## 26. VenueOS Reconstruction Implications

Read in full: `NEW_MODULE_GUIDE.md` (652 lines). Key implications for a future `games.bingo` reconstruction, cross-referenced against what already exists:

- **Module ID `games.bingo` and display name "Bingo" are already correctly assigned** and must not change (§3-4 of the guide) — `VenueBingoModule`/`VenueBingoService`/`VenueBingoSettings` already use this ID consistently in `Operations.cs`.
- **The existing scaffold is intentionally minimal and already follows the guide's backend-module conventions correctly**: a typed client (`VenueBingoClient.cs`) with wire-protocol records separate from any UI type, a service class (`VenueBingoService`) owning per-venue config via `GetModuleConfig`/`SaveModuleConfig` keyed by `"games.bingo"`, schema version `1`, and an operator panel that reads `venues.Current.Theme` rather than a module-local venue field — all consistent with §9-12 of the guide.
- **`VenueBingoSettings.VenueName` is a known, already-documented inconsistency** (guide §10/§21 explicitly names `VenueBingoSettings.VenueName` alongside two other modules as predating the "Active Venue Profile is authoritative" rule) — a future reconstruction pass should supersede this with `venues.Current.DisplayName`, following the precedent already set by `MairsTriviaService`.
- **`UnderDevelopment: true`, `IsEnabled = false` by default is correct and must remain so** until §21's autopay design and §23's card model are actually implemented and live-tested — this matches the audit brief's explicit instruction and the plugin's own operator panel already states the payout exclusion in-UI.
- **Detached/embedded rendering, Settings-vs-operational split, and diagnostics are not yet built out for Bingo specifically** beyond the generic scaffold — per guide §8's "known current gap," `DrawSettings()` currently mirrors `Draw()` for every module including Bingo; a reconstruction should split persistent connection config (server URL, room/admin keys — currently already correctly identified as secrets via `WithoutSecrets()`/`Redact` coverage) from live operational state (room controls, payout ledger UI, card roster) per guide §9, using `promotion.partyfinder`'s `DrawSettings()` split as the reference example.
- **A reconstruction that adds real autopay would be exactly the kind of "module whose core function is inherently unsafe/FFXIVClientStructs/ECommons-dependent" case the guide calls out in §30 (Testing)** — it should separate the plain orchestration logic (payout state machine, ledger persistence, guard conditions) into `VenueOS.Modules.Operations/Bingo/` behind a small interface, with only the actual unsafe addon/ECommons code in `VenueOS.Plugin/Bingo/`, following the `promotion.partyfinder` precedent (`PARTY_FINDER_RECONSTRUCTION.md`) — this would let the orchestration layer (including §21's state machine and §23's paid/comp accounting) be unit-tested with a fake trade-engine implementation, even though the real engine can only be verified in-game.
- **Per-venue configuration** is already correctly structured for connection settings (`BingoConnectionSettings` with `WithoutSecrets()`); a reconstruction adding payout/comp-card state would need to decide whether that state is per-venue config (small, settings-shaped) or something needing the heavier `VenueOS.Modules.Operations/QuestionLibrary/`-style durable content-store pattern (guide §42a) — given a payout ledger could grow large and needs audit-log properties, the latter pattern (or an equivalent durable store under `ConfigDirectory`) is likely the better fit than a `GetModuleConfig`/`SaveModuleConfig` blob, which the guide itself says is meant "for small settings blobs, not bulk content."
- **Bingo's Web Display vs. Server settings split** (guide §34, citing `BingoColors`) is the existing precedent for how this module's Settings should be organized going forward — any reconstruction should preserve that split rather than flattening it.
- **No `CanDetach` flag exists or is needed** — Bingo should detach like every other module (guide §7); nothing about a reconstructed autopay/accounting layer changes this.

---

## 27. Risks / Unknowns Requiring Live Verification

1. Whether the live production deployment's `admin.config.js` `adminKey` differs from the checked-in default `"FFXIVBingo4All"` — cannot be determined from source.
2. Whether the live production `dbPath` matches the `server.config.js` default (`/var/data/bingo.sqlite`) or was overridden at deploy time.
3. The exact live values of `roomRetentionDays`/`cleanupIntervalMinutes` in production (defaults documented here, but overridable).
4. Whether any currently-active real rooms/players would be affected by a VenueOS reconstruction going live *alongside* the existing standalone plugin against the *same* backend (a compatibility risk if both write to the same room concurrently, given the backend's lack of optimistic concurrency control, §5/§7).
5. Whether Dropbox's chat-message completion signal (`"Trade complete."` / `"Trade canceled."`) is stable across the current live game client version and all client languages (Dropbox's `GetSpecificYesno` implies at least some localization awareness elsewhere in that codebase for dialog text, but the two trade-completion strings themselves were found as literal English string comparisons, `Dropbox.cs:101,107` — this should be verified against non-English game clients before being relied upon as an autopay completion oracle).
6. In-game verification of every autopay failure scenario in §19's table and §25 — this audit traced the code precisely but did not (and per the audit brief, must not) execute any live trade automation.
7. Whether real hosts today are actually using the "+ Card" control to informally grant complimentary cards already (i.e., whether the pot-inflation bug in §14/§22 has already caused real accounting disputes in production, which would argue for prioritizing §23's fix).
8. Full confirmation of Socket.IO reconnect semantics (client-side auto-reconnect behavior was inferred from Socket.IO v4 defaults per the browser-client study, not independently verified against this specific server's configuration).

---

## 28. Recommended Reconstruction Order

Sequencing based on risk and dependency, not a commitment to timeline:

1. **Do not enable Bingo's autopay in VenueOS until §21 is implemented and live-tested** — this is already the current state (`UnderDevelopment: true`, autopay absent) and should remain the default until explicitly changed.
2. If/when card-purchase and payout functionality are reconstructed at all, build the **comp/paid card distinction (§22-23, Option A) before or alongside any payout automation** — it's the lower-risk, purely-additive change and removes a real, already-present accounting bug from the reconstructed system from day one, rather than inheriting it.
3. Build the **payout ledger's durable-persistence and per-attempt-identity foundation (§21, principles 2-3) before attempting any actual in-game trade automation** — this is the part that fixes the reported bug; the trade-mechanics automation itself (§21's `InitiateTrade` through `AwaitCompletionSignal` stages) is the riskier, unsafe/ECommons-dependent part that should follow, isolated per §26's testing guidance.
4. Live-verify the chat-message completion signal (§27 item 5) before wiring it as the primary completion oracle in any reconstruction.
5. Only after both of the above are solid, consider whether a backend-authoritative extension (§23's "prefer backend authoritative where practical" note, §29) is worth the added protocol-versioning risk — starting plugin/VenueOS-side keeps the change additive and backend-compatible by construction.

---

## 29. Product Decisions Needed From User

- Whether free cards are represented as separate counters (§23 Option A) or full grant/purchase transaction events (§23 Option B) — Option A is recommended as the lower-risk starting point, but this is a product call about how much audit-trail richness is wanted.
- Whether card/payout accounting should be plugin-authoritative (matching today's actual behavior) or backend-authoritative (more correct, but requires extending a backend that currently does none of this) — §23 recommends starting plugin-authoritative for compatibility reasons, but this trades off long-term correctness against short-term compatibility risk, which is the user's call.
- Whether the payout ledger, once redesigned (§21), should be persisted only locally (Dalamud plugin config / a durable local store) or also acknowledged by a backend extension — a backend acknowledgment would let a resumed session ask "was this already paid" authoritatively instead of relying on local persistence, but is a new backend protocol surface the donor server does not have today.
- Whether an ambiguous/ungoverned autopay outcome (§21's `Ambiguous` state) should always require a human decision with no auto-retry (this audit's recommendation, based directly on the observed root cause), or whether some bounded, explicitly-confirmed auto-retry is still wanted for operator convenience.
- Whether the old, fully-unauthenticated `/api/call-number` and unvalidated `call_bingo` contracts should remain exactly as-is indefinitely (required for compatibility with the live donor backend, per §24's Category A classification) or whether a versioned, better-authenticated replacement protocol is eventually wanted — the latter would mean VenueOS's Bingo module could no longer interoperate with the existing standalone backend without also standing up a new backend, a significant scope change.
- Whether payout history (once it exists at all, per §21) should become a first-class, host-visible audit log inside the VenueOS module (this audit's implicit recommendation, since no such log exists today and its absence is part of why the current bug is hard to diagnose after the fact) — or remain a minimal internal-only ledger.
- Whether the chat-message-based completion signal (§21, §27 item 5) should be treated as sufficient on its own, or whether the design should require a second corroborating signal (e.g. gil-delta) before marking a payout complete, given neither signal alone was proven bulletproof by this audit (the chat message is accurate but its cross-locale/cross-client-version stability wasn't verified; the gil-delta is corroborating-only per §21 but its exact threshold/tolerance would need to be decided).

---

## 30. Source/File Map

### Donor plugin (`C:\FFXIVplugs\ffxivbingo4all\FFXIVBingo4All.Plugin\`)

| File | Class/Region | Purpose | Why it matters |
|---|---|---|---|
| `Plugin.cs:31-533` | `Plugin`, `Draw()` | Entry point, ImGui frame loop | Autopay ticks every frame regardless of window-open state |
| `Plugin.cs:56-66` | `PayoutStage` enum | Autopay state machine states | Core of §18-19 |
| `Plugin.cs:97-113` | Payout/ledger fields | `payoutPaid`, `paidOutCallers`, `bingoCallers`, pending-payout fields | All in-memory-only; root cause of §19 |
| `Plugin.cs:1792-1857` | `HandlePayout()` | Payout trigger, target/amount resolution | §18 |
| `Plugin.cs:2736-2905` | `UpdatePendingPayout()` | The autopay driver, ticked every frame | §18-19 |
| `Plugin.cs:2997-3013` | `TryConfirmTradeYesNo()` | Blind SelectYesno auto-accept | §18's riskiest single method |
| `Plugin.cs:1769-1777` | `GetCurrentGil()` | Host's own gil balance read | The (insufficient) success heuristic |
| `Plugin.cs:2593-2691` | `GetTotalPot()` etc. | All pot/prize math | §14, root of the free-card problem |
| `Plugin.cs:3199-3273` | `StartNewBingo`, `StopBingo`, `SelectRoom` | Room lifecycle | §11; `SelectRoom`'s `payoutPaid.Clear()` is the ledger-wipe trigger |
| `Plugin.cs:3758-3832` | `GenerateCardGrid`, `HashSeed`, `Mulberry32`, `GenerateColumn` | Deterministic card generation | Must match browser client bit-for-bit |
| `Plugin.cs:382-457` | `SyncHostStateAsync`, `PostCallNumberAsync` | Outbound API calls | §8-9 |
| `Plugin.cs:3373-3707` | `FetchRoomStateAsync` | Inbound state hydration | Does not restore payout amounts (§19) |
| `Configuration.cs` | `Configuration`, `PlayerData` | Persisted settings (connection + skins only) | Confirms the payout ledger is *not* among persisted fields |

### Donor backend (`C:\FFXIVplugs\ffxivbingo4all\backend\`)

| File | Purpose | Why it matters |
|---|---|---|
| `server.js:1-31` | Boot, Express/Socket.IO/SQLite setup | §5 |
| `server.js:32-48` | Table creation (`rooms`, `short_links`) | §7 — no migrations, no schema versioning |
| `server.js:365-456` | `defaultRoomState`, `normalizeRoomState` | Full room-state shape | §7 |
| `server.js:589-602` | `isAdminRequest`, `getRoomKey` | Auth model | §10 |
| `server.js:640-710` | `POST /api/host-sync` handler | The one endpoint that sets pricing/cards | §14 — confirmed no pot arithmetic here |
| `server.js:984-1017` | `POST /api/call-number` handler | Confirmed zero auth | §8, §15 |
| `server.js:1019-1220` | Socket.IO connection/event handlers | §9, §16 — confirmed no bingo-pattern validation |
| `server.js:86-119` | `scheduleRoomCleanup`, `runCleanup` | Auto-deletion of stale rooms | §7 |
| `server.config.js` | `dbPath`, retention/cleanup settings | §5, §24 (live values unverified, §27) |
| `admin.config.js` | Static admin key | §10, §27 (live value unverified) |

### Donor browser client (`C:\FFXIVplugs\ffxivbingo4all\backend\public\`)

| File | Purpose | Why it matters |
|---|---|---|
| `app.js:1-104` | URL param parsing, link validation | §6, §10 — the entire (non-)identity model |
| `app.js:540-598` | `hashSeed`, `mulberry32`, `generateColumn`, `generateCard` | Card generation, must match plugin exactly | §12 |
| `app.js:472-727` | `cardHasBingo`, claim button handler | Client-side pattern pre-check + claim emit | §16 |
| `app.js:872-1047` | `connectSocket` and all socket event handlers | §9, §16 | |
| `app.js:508-538` | `verifyRoomAvailability` | The one REST call this client makes | §6 |
| `styles.css:29-64` | Documented per-cell image-override customization hook | Worth preserving in any reconstruction's theming design |

### Dropbox reference (`C:\FFXIVplugs\ffxivbingo4all\_tmp_dropbox\Dropbox\`)

| File | Purpose | Why it matters |
|---|---|---|
| `TradeTask.cs` | Gil-only trade task queue builder + polling predicates | §20 — the state-machine pattern studied (and its gaps) |
| `TaskAddItemsToTrade.cs` | Items+gil variant of the same pattern | §20 |
| `Memory.cs` | Native `OfferItemTrade` hook | §20 — reusable offer mechanism |
| `Dropbox.cs:99-112` | Chat-message trade-completion detection | **§20's single most important finding** — the accurate signal, currently unused for control flow even in Dropbox itself |
| `Dropbox.cs:115-175` | `Framework_Update` — auto-confirm/auto-lock heuristics | §20 — the addon-alpha/count heuristics, and the naive "ready" proxy |
| `TradeQueueEntry.cs` | Per-trade GUID (UI-only, not verification) | §20 |
| `TradeQueueUI.cs`, `ItemQueueUI.cs` | Operator UI, Abort semantics | §20 — same "Abort doesn't undo in-game state" gap as Bingo's own |

### VenueOS current scaffold (`C:\FFXIVplugs\venueos\`)

| File | Purpose | Why it matters |
|---|---|---|
| `src/VenueOS.Modules.Operations/Bingo/VenueBingoClient.cs` | Typed HTTP client, wire-protocol records | Confirmed correct against the donor protocol (header/body key placement, no-auth `call-number`) |
| `src/VenueOS.Modules.Operations/Operations.cs:722-742` | `VenueBingoSettings`, `VenueBingoService`, `VenueBingoModule` | Per-venue config, polling (`Tick`), module registration — `UnderDevelopment: true`, disabled by default |
| `src/VenueOS.Plugin/VenueBingoOperatorPanel.cs` | Operator UI | States outright that payout automation is excluded |
| `tests/VenueOS.Services.Tests/VenueBingoClientTests.cs` | Protocol contract tests | Already encodes the correct header/key-separation behavior |
| `BINGO_PAYOUT_AUTOMATION_DEFERRED.md` | Prior decision record | Anticipated this audit's autopay conclusions |
| `VENUE_BINGO_3D.md` | Prior scope record | Documents what was and wasn't ported, and the pinned donor commit |
| `NEW_MODULE_GUIDE.md` | Module architecture reference | §26 |

---

## Build / Tests

- **Donor plugin:** not built during this audit (requires the Dalamud SDK toolchain; out of scope for a read-only audit and not necessary to trace the logic, which was done by direct code reading).
- **Donor backend:** not run during this audit — `node_modules` is not installed in this environment (`package.json` declares `express`, `cors`, `sqlite3`, `socket.io`), and no `.env`/live secrets are available to safely start it against a real or disposable database.
- **No automated test suite exists anywhere in the donor repository** — confirmed by a repository-wide search for test files/folders; only the built plugin's release ZIP artifact (`bin/Release/.../latest.zip`) was found, no test projects.
- **VenueOS build/test:** not run during this pass, since no VenueOS code was changed and the existing `VenueBingoClientTests.cs` suite (already reviewed directly, §21 of this doc's file map) was read rather than executed — its assertions were confirmed consistent with the donor protocol by direct comparison against `server.js`'s actual auth-check code, which is a stronger check for a read-only audit than merely re-running a suite already known to pass.

Passing tests (had any been run) would not have been proof of the runtime autopay correctness — that logic is entirely game-state/addon-dependent and can only be verified in-game (§27, §28), which this audit-only pass explicitly did not do.
