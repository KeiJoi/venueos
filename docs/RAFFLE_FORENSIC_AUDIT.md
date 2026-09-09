# Raffle Forensic Audit

**Status:** Audit only. No files were edited, staged, committed, pushed, tagged, or released in the donor repository or in VenueOS during this pass, other than the creation of this report.

**Date:** 2026-09-08
**Donor HEAD:** `f685a4165f3df577ee5d0e9a302114d41f234a2a` (`ffxivraffle4all`, branch `main`, clean working tree)
**VenueOS HEAD:** `6b2948cfca73dc71c491f23637b65f1a21ff5fcc` (`venueos`, branch `main`) — working tree has unrelated, pre-existing uncommitted changes (a Tournament Control / Brackets reconstruction in progress: `Operations.cs`, `TournamentControlClient.cs`, `Plugin.cs`, etc., plus new `TournamentControlService.cs`/`TournamentRealtimeClient.cs`). None of those files were touched by this audit and none of them are Raffle-related; they are noted here only so the read-only claim in §29 is verifiable against the actual `git status` at audit time.

---

## 1. Executive Summary

FFXIV Raffle 4 All is a small, three-part system: a single-window Dalamud plugin (`RaffleWindow.cs`, 598 lines) that is the sole source of truth for raffle identity/settings/participant counts, a thin Node/Express/`ws` backend (`server.js`, 293 lines) that holds the flattened ticket pool and performs the actual randomization, and a static two-page browser client (`host.html`/`view.html`/`wheel.js`, 486 lines together) that is pure visualization plus a "Spin" button for the host. There is no database — the backend is an in-memory `Map` debounced to a single flat JSON file (`backend/data/raffles.json`), and the plugin has its own, entirely separate, local JSON file (`raffles.json` in the Dalamud config directory). These two stores are never reconciled automatically; the operator must manually click "Generate host link" (or the functionally-identical "Generate viewer link") to push local ticket changes to the backend.

The most important structural fact is this: **randomization is entirely server-side and uses a cryptographically secure RNG** (`crypto.randomInt`, `server.js:49-55` and `:190`). The browser never picks a winner — it only plays an animation driven by a `rotation`/`durationMs`/`winnerName` payload the server already computed and persisted. The plugin never picks a winner either; it can only display whatever the backend last reported, or be hand-edited by the operator with no validation (`RaffleWindow.cs:282-287`). This is a clean authority model to preserve.

Two classes of defect were found and root-caused:

- **No guard against a double/repeated spin.** The WebSocket `spin` handler (`server.js:251-277`) has no "already has a winner" or "spin in progress" check — a second `spin` message (a genuine double-click before the button visually disables, or a second host-authenticated browser tab) re-runs `spinRaffle()` and silently overwrites `raffle.winnerName`/`raffle.rotation` with a fresh, independent draw. Every connected client (including the one that "won" a moment earlier) is told the new result with no history of the first. This is a real, reachable "double winner selection" bug class, not a hypothetical one — see §19, Finding R-1.
- **`upsertRaffle` (`POST /api/raffles`) has no authentication or ownership check at all.** Any caller who can reach the backend and knows (or guesses) a `raffleId` — which appears in plaintext in every viewer/host URL — can silently replace that raffle's entire participant list, settings, and ticket pool, and reset its `winnerName`/`rotation` to null, without knowing either the host or viewer token. Reading state or spinning is token-gated; *writing* is not. See §19, Finding R-2, and §23.

The current VenueOS `games.raffle` scaffold is honest about its own incompleteness: it is `UnderDevelopment: true`, disabled by default (`DisplayOrder: 9`, `IsEnabled = false`), and its operator panel (`RaffleOperatorPanel.Draw`, `NativeOperationsPanels.cs:695-722`) only supports creating a named local raffle and viewing a read-only list (name, ticket count, winner). **There is currently no UI path to set the backend URL, add a ticket, adjust a participant, generate a host/viewer link, fetch a winner, or import/export XLSX** — every one of those donor features exists as a protocol-compatible method on `VenueRaffleService`/`VenueRaffleClient` but has no caller anywhere in `VenueOS.Plugin`. This means the network code, while contract-correct (verified against the donor's actual JSON shapes by `RaffleClientTests.cs`), is currently unreachable dead code from the operator's perspective. See §17–§18.

No functional donor code changed between the commit the existing VenueOS client was validated against (`111b3a9`, per `VENUE_RAFFLE_3A.md`) and current HEAD (`f685a41`) — the only diffs are a Dalamud API-level bump (14→15), a version bump, an icon change, a README edit, and a rebuilt zip artifact (§21). VenueOS's existing understanding of the donor wire protocol is still accurate.

---

## 2. Repository / HEAD Information

| | Donor (`ffxivraffle4all`) | VenueOS |
|---|---|---|
| Path | `C:\FFXIVplugs\ffxivraffle4all` | `C:\FFXIVplugs\venueos` |
| HEAD | `f685a4165f3df577ee5d0e9a302114d41f234a2a` | `6b2948cfca73dc71c491f23637b65f1a21ff5fcc` |
| Branch | `main` | `main` |
| Working tree | Clean | Modified (unrelated in-progress Tournament/Brackets work — not touched, not Raffle) |
| Plugin manifest version | `AssemblyVersion 1.0.0.1`, `DalamudApiLevel 15` | VenueOS `AssemblyVersion 0.2.2.0`, `DalamudApiLevel 15` |
| Commit count (repo lifetime) | 11 | 8 (plus this session's uncommitted Tournament work) |
| Automated tests | **None** — no test project, no `*.test.js`, no `backend/test` | `tests/VenueOS.Services.Tests/RaffleClientTests.cs` (5 tests) + Raffle assertions inside `ModuleDisplayOrderTests.cs`/`UnfinishedModuleDefaultsTests.cs` |

Donor commit log (oldest → newest):

```
c81bdf8 Create README.me
7825673 Added Icon
88e6a1b Created base System and files
22ef459 Updated Web Page and plugin with better export and ability to import games and file picker windows for the export and import.
c170e91 Updated Webpage Again
b24e156 Updated winner animations and rotation of triangle
ba5fc98 Still hoping I got the rotation right on the triangle
e172bf7 Added Zip File... Will be creating a new Repo to Store all Files
5363c3a Updated Readme with Test Server address. Can also be used by anyone if htey do not want to customize the CSS.
7c0ba9d Updated icon for raffle plugin
111b3a9 Updated to Dalamud API 15
f685a41 Updated for testing with VenueOS just build files and artifacts   ← HEAD
```

See §21 for the diff between `111b3a9` and HEAD and why it changes nothing functionally.

---

## 3. Donor Architecture

```
┌───────────────────────────┐   HTTP POST /api/raffles (upsert,        ┌───────────────────────────┐
│ Dalamud Plugin             │   full ticket-list replace)              │ Node/Express + ws backend  │
│ FFXIVRaffle4All/           │ ────────────────────────────────────►    │ backend/server.js          │
│  Windows/RaffleWindow.cs   │   HTTP GET /api/raffles/:id?token=       │ (293 lines)                │
│  Services/RaffleManager.cs │ ◄────────────────────────────────────    │ + in-memory Map, debounced │
│  Services/BackendClient.cs │   (manual "Fetch winner" button only —   │   1s flush to              │
│  Services/RaffleRepository │    no push notifications to the plugin)  │   backend/data/raffles.json│
│  (local raffles.json,      │                                          │ + WebSocket /ws            │
│   plugin config dir)       │                                          │   (join/spin/state/updated/ │
│                             │                                          │    spin/error)             │
└───────────────────────────┘                                          └──────────────┬─────────────┘
                                                                                        │
                                                          WebSocket only (join once,     │
                                                          push-only thereafter)          │
                                                                          ┌──────────────▼─────────────┐
                                                                          │ Browser client              │
                                                                          │ backend/public/host.html,   │
                                                                          │ view.html, wheel.js (405)   │
                                                                          │ - Canvas wheel, pure         │
                                                                          │   visualization              │
                                                                          │ - Host role: "Spin" button   │
                                                                          │   sends {type:'spin'}        │
                                                                          │ - No randomization here;     │
                                                                          │   server already decided     │
                                                                          └──────────────────────────────┘
```

The plugin and backend are only connected by two things: the plugin's outbound HTTP calls (`BackendClient.cs`), and the raffle's identity (`raffleId`/`hostUrl`/`viewerUrl`) that the backend hands back and the plugin stores locally. There is no WebSocket client in the plugin at all — the plugin never sees a live spin happen; it can only poll `GET /api/raffles/:id` after the fact via the "Fetch winner from backend" button.

---

## 4. Product Workflow

**Lifecycle actually implemented** (not the idealized CREATE→CONFIGURE→…→COMPLETE template — the donor has no explicit "status" enum at all; every raffle is always fully mutable):

1. **CREATE** — operator opens `/raffle` (or the Dalamud plugin-installer gear icon, both call the same `raffleWindow.IsOpen = true`, `Plugin.cs:43-46,71-74`), types a name, clicks "Create new raffle" (`RaffleWindow.cs:139-144` → `RaffleManager.CreateRaffle`, `RaffleManager.cs:25-37`). A `Raffle` gets a new GUID, is inserted at the front of the in-memory/persisted list, and becomes `CurrentRaffle`. No player or backend involvement yet.
2. **CONFIGURE** — operator sets `StartingPot`, `TicketCost`, `PrizePercentage`, and a "paid tickets per bonus → free tickets per bonus" rule (`RaffleWindow.cs:158-206`, backing fields on `RaffleSettings`, `Models/RaffleModels.cs:27-34`). These are freely editable at any time, including after tickets/winners exist — there is no lock/freeze once a raffle is "live."
3. **ENTER PARTICIPANTS** — operator types (or captures via "Use target", reading `ITargetManager.Target.Name.TextValue`, `RaffleWindow.cs:213-216,452-463`) a name, then clicks "Add paid tickets" or "Add free tickets" (`RaffleWindow.cs:231-249`). This is **entirely operator-driven**; there is no player-facing way to self-enter (no chat command, no in-game menu, no browser entry form — see §13). Adding paid tickets can also auto-grant free tickets per the bonus rule (`RaffleManager.CalculateFreeTickets`, `RaffleManager.cs:182-191`, previewed live in the UI, `RaffleWindow.cs:225-229`).
4. **(Optional) PUBLISH** — operator clicks "Generate host link" / "Generate viewer link" (functionally identical — both call `GenerateLinksAsync`, `RaffleWindow.cs:263-272`). This `POST`s the raffle (flattened to a ticket array) to the backend, which upserts it and returns `hostUrl`/`viewerUrl` (`BackendClient.cs:23-46`, `server.js:110-120`). Links are shown read-only with a per-field "Copy" button (`RaffleWindow.cs:314-327`).
5. **DRAW** — a host (someone who opened the `hostUrl`, i.e. possesses the host token) clicks the on-page "Spin" button, which sends `{type:'spin'}` over the already-open WebSocket (`wheel.js:90-97`). The backend validates `ws.role === 'host'` (set at `join` time and gated by `hostToken`, `server.js:226-267`), computes a winner (`spinRaffle`, `server.js:185-214`, see §8), and broadcasts a `spin` message with `winnerName`, `winnerIndex`, `rotation`, `durationMs` to every connected socket for that raffle (`broadcastSpin`, `server.js:175-183`).
6. **WINNER** — every client (host and viewer) animates the wheel to the target rotation, then reveals the winner name after a fixed 3-second highlight delay (`wheel.js:128-146`). The plugin does **not** learn about this automatically; the operator must click "Fetch winner from backend" (`RaffleWindow.cs:277-280,369-413`) to pull `winnerName` back down via `GET /api/raffles/:id`, or can simply type a name directly into the "Winner" `InputText` with no validation whatsoever (`RaffleWindow.cs:282-287`).
7. **RESET / REDRAW** — "Reset current raffle" (`RaffleWindow.cs:152-155` → `RaffleManager.ResetCurrent`, `RaffleManager.cs:86-98`) clears `Participants`, `WinnerName`, `HostUrl`, `ViewerUrl` locally with **zero confirmation dialog** — it is a single click, fully destructive, and does not touch the backend's copy of the raffle at all (the backend still has the old ticket list/winner until the operator re-publishes). Separately, nothing stops the host from clicking "Spin" again at any time after a winner is already shown — see §8/§19 Finding R-1; there is no explicit "redraw" affordance distinct from "spin again."
8. **EXPORT / ARCHIVE** — "Export raffle to XLSX" (three-sheet workbook: Settings, Summary, Participants, `XlsxExporter.cs`) and "Import raffle from XLSX" (`XlsxImporter.cs`, reads Settings sheet if present else falls back to Summary sheet, then Participants) let the operator keep an offline record or restore one; import always assigns a fresh GUID and clears any previous `HostUrl`/`ViewerUrl`/`ExternalId` (`RaffleManager.ImportRaffle`, `RaffleManager.cs:39-55`), i.e. import always produces a brand-new, unpublished local raffle — it never overwrites the backend.

**Completed raffles remain available forever** — the raffle selector combo (`RaffleWindow.cs:116-137`) lists every raffle ever created locally, with no archiving/deletion UI at all (no "delete raffle" button exists anywhere in `RaffleWindow.cs`). The backend equally never deletes a raffle (no retention job — contrast with the Bingo donor's 30-day cleanup, confirmed absent by inspection of the whole 293-line `server.js`).

**There is no "venue"/"event" identity concept in the donor at all** beyond the raffle's own free-text `Name` — see §16.

---

## 5. State / Authority Table

| State | Authority | Persistent? | Recovery / how it survives |
|---|---|---|---|
| Raffle identity (Id, Name, CreatedAt) | Plugin (local `Raffle.Id`/`Name`) | Yes — plugin's `raffles.json` | Reloaded by `RaffleRepository.Load()` at plugin construction (`Plugin.cs:34-35`) |
| `RaffleSettings` (pot/cost/%/free-ticket rule) | Plugin only | Yes — plugin's `raffles.json` | Same as above. **Never sent to or read from the backend at all** — the backend receives `settings` in the upsert payload (`server.js:89`) but never uses it for anything (no pot math on the server side; it just stores the blob) |
| Participants + Paid/Free ticket counts | Plugin only, until published | Yes — plugin's `raffles.json` | Same as above |
| Flattened ticket pool (`tickets: string[]`) | **Backend**, once published | Yes — `backend/data/raffles.json` | `loadRaffles()` at process boot (`server.js:16-27`); re-derived from the plugin's participant list only when the operator re-publishes (ticket-set hash changes, `server.js:76-78`) |
| Shuffle order of the ticket pool | Backend (`shuffle`, `server.js:49-55`) | Yes, as part of the raffle blob | Re-shuffled only when the ticket *set* changes (order-insensitive hash, `hashTickets`, `server.js:57-59`) — re-publishing the *same* names/counts keeps the previously shuffled array, discarding the newly submitted order entirely |
| Selected winner (`winnerName`) | **Backend** (authoritative) | Yes | Reset to `null` whenever the ticket set changes (`server.js:83`); otherwise persists across backend restarts. Plugin's local `WinnerName` field is a **separate, freely-editable copy** with no authority — can silently diverge (§4 step 6) |
| Wheel `rotation` | Backend | Yes | Same lifecycle as `winnerName` |
| `hostToken` / `viewerToken` | Backend generates once, on first creation; plugin stores them only as URL path segments embedded in `HostUrl`/`ViewerUrl` | Yes, in both stores | Backend: survives restart in its own file. Plugin: survives restart as a plain string inside its `raffles.json` — see §15 |
| Active WebSocket "who is host" (`ws.role`) | Connection-local, memory only | No | Re-established every time a socket re-joins; entirely re-derived from the token supplied in the `join` message — no session/cookie concept |
| Browser wheel animation state (`state.rotation` mid-spin, highlight timers) | Browser, memory only | No | Lost on refresh; re-synced instantly from the next `state`/`updated`/`spin` message with no mid-spin resume (§10) |
| Backend URL (`PluginConfiguration.BackendBaseUrl`) | Plugin | Yes — Dalamud plugin config (separate file from `raffles.json`) | `configuration.Initialize`/`.Save()` (`Configuration.cs`) |
| `LastSelectedRaffleId` | Plugin | Yes — same Dalamud plugin config | Restores the previously active raffle on next launch (`RaffleManager.cs:19`) |

**No state in this system is derived/computed-only at request time in a way that matters for correctness** except the plugin's `RunningPot`/`PrizePot`/`HouseTake` (pure computed properties on `Raffle`, `Models/RaffleModels.cs:22-24`) — these exist only in the plugin and are never sent anywhere; the backend has no pot concept whatsoever (confirmed by full read of `server.js` — `settings` is stored verbatim and never arithmetic'd).

---

## 6. Backend API Endpoint Table

| Method | Route | Auth | Purpose | Mutates state? | Notes |
|---|---|---|---|---|---|
| `POST` | `/api/raffles` | **None** | Create-or-replace a raffle by `raffleId` (defaults to a fresh UUID if omitted) | **Yes** — full ticket-list replace, resets winner/rotation on ticket-set change, preserves tokens if raffle already exists | **No ownership/auth check of any kind** — see §19 Finding R-2 and §23. `raffleId` is visible in plaintext in every shared URL. |
| `GET` | `/api/raffles/:id?token=` | Token must equal `hostToken` or `viewerToken` | Read current `raffleId`/`name`/`winnerName`/`tickets` | No | 404 if raffle unknown, 403 if token mismatches; used only by the plugin's "Fetch winner" button |
| `GET` | `/host/:raffleId/:token` | **None at the HTTP layer** | Serves the static `host.html` shell unconditionally | No | Token in the path is not checked until the browser's own WebSocket `join` message is sent (`server.js:143-145`) — the HTML page itself is public to anyone who requests any `raffleId`/any-string `token` combination |
| `GET` | `/view/:raffleId/:token` | Same as above | Serves `view.html` | No | Same caveat |
| (static) | `express.static(public/)` | None | Serves `styles.css`, `wheel.js` | No | Expected/benign |
| WS `join` | `/ws` | `token` checked against `hostToken`(`role==='host'`) or `viewerToken`/`hostToken` (any other role) | Registers the socket for a raffle, returns current public state | No | `role` is entirely client-declared in the message; only the accompanying token gates it (§23) |
| WS `spin` | `/ws` (after `join`) | Requires the *socket* to have joined with `role==='host'` | Triggers `spinRaffle()` and broadcasts the result | **Yes** — always, with no guard against repeat calls | See §8, §19 Finding R-1 |

**Public/browser-reachable endpoints:** `GET /host/*`, `GET /view/*`, static assets, and the WebSocket `join`/`spin` messages. **Organizer/plugin-only endpoints:** `POST /api/raffles` (in practice, since only the plugin builds the right payload shape — but nothing on the server enforces that it *must* come from the plugin) and `GET /api/raffles/:id` (token-gated).

No rate limiting, no request-size guard beyond Express's global `json({limit:'2mb'})` (`server.js:9`), no input validation on `payload.name`/`payload.settings` shape (both stored as whatever JSON was sent, `server.js:88-90`), no CORS middleware (irrelevant to the real risk — see §23).

---

## 7. Database / Persistence Model

There is no SQL database. `raffles` is a `Map<string, object>` living in the Node process's memory (`server.js:13`). Every mutation calls `scheduleSave()` (`server.js:29-37`), which debounces to a single `fs.writeFileSync` of the *entire* map (`JSON.stringify(Array.from(raffles.values()))`) to `backend/data/raffles.json` at most once per second (`saveRaffles`, `server.js:39-47`). At boot, `loadRaffles()` (`server.js:16-27`) reads that file back wholesale into the `Map`; a missing file is treated as "no raffles yet," and **a corrupt/unparseable file is silently swallowed** (`catch (err) { console.error(...) }`) and the backend starts with an **empty** `Map` — i.e., a single malformed write (e.g., a crash mid-`writeFileSync`) can silently erase every raffle the backend ever held, with no backup, no versioning, and no warning surfaced anywhere except a server console line.

There are no migrations, no schema-version field, and no foreign keys/cascade relationships at all — the entire persisted unit is one flat object per raffle with no cross-references to any other table. This means none of the FK/cascade-order failure modes named in the audit brief (the class of bug Brackets exposed) are structurally possible here — there is nothing to cascade. The corresponding risk in this system is different in kind: **no cleanup ever runs at all**, so `backend/data/raffles.json` grows forever, one entry per raffle ever created by any plugin instance that ever published to this backend (contrast with the Bingo donor's 30-day retention job, confirmed absent here by full inspection).

The plugin's own persistence (`RaffleRepository.cs`) is a second, independent flat JSON file (`raffles.json` in the Dalamud plugin config directory), holding the full `List<Raffle>` including settings/participants/`HostUrl`/`ViewerUrl`/`WinnerName`, but **not** the flattened ticket array (that only exists on the backend). Load failures here are also silently swallowed to an empty list (`RaffleRepository.cs:30-38`, bare `catch { return new List<Raffle>(); }`) — a corrupted plugin-local file silently discards every raffle the operator ever created, with the same "no backup" characteristic as the backend side.

**What survives what:**
- **Backend restart:** every raffle (participants blob, flattened tickets, tokens, winner, rotation) — yes, via `data/raffles.json`.
- **Dalamud/plugin restart:** every local raffle (name, settings, participant counts, link strings including embedded tokens, winner) plus `LastSelectedRaffleId` — yes, via the plugin's own `raffles.json` + Dalamud plugin config.
- **Browser refresh:** nothing browser-side is authoritative; a refresh just rejoins and receives a fresh `state` message (§10).
- **A raffle can absolutely be resumed** after any of the above — nothing is torn down; the state simply sits until someone reconnects.
- **Completed raffles persist indefinitely** on both sides, with no archiving, exporting-then-deleting, or manual-deletion path anywhere in the donor UI.

---

## 8. Randomization / Winner Selection

This is the most safety-relevant subsystem, traced completely:

1. **RNG:** Node's built-in `crypto` module, specifically `crypto.randomInt(min, max)` — a cryptographically secure RNG, **not** `Math.random()`. Used for: the ticket-pool Fisher-Yates shuffle (`shuffle()`, `server.js:49-55`), the actual winner index (`crypto.randomInt(0, ticketCount)`, `server.js:190`), the cosmetic extra "turns" count (`server.js:194`), and the cosmetic spin duration jitter (`server.js:202`). **This is a genuine strength of the donor and should be preserved as-is** — there is no bias/predictability concern here.
2. **Where it runs:** entirely server-side, inside `spinRaffle(raffle)` (`server.js:185-214`), invoked only from the WebSocket `spin` message handler (`server.js:264-276`). The browser never computes a winner — it only receives `{winnerName, winnerIndex, rotation, durationMs}` and animates to that target (`wheel.js:74-79,343-372`). The plugin never computes a winner either.
3. **Does entrant order affect probability?** No, in two independent ways: (a) the ticket array is **fully reshuffled** whenever the ticket *set* changes (`ticketsChanged`, order-insensitive comparison via a sorted-copy hash, `hashTickets`, `server.js:57-59,77-78`), discarding whatever order the plugin submitted; and (b) even within the shuffled array, the winning index is drawn independently via `crypto.randomInt`, not by any positional bias.
4. **Does re-publishing the same names change anything?** If the ticket *multiset* is unchanged (same names, same counts, any order), `ticketsChanged` is `false` and the backend **keeps the previously shuffled array and the previous winner/rotation** (`server.js:78,82-83`) — i.e., republishing with no actual ticket change is a safe no-op for the draw state.
5. **Weighting:** the only "weight" mechanism is literal repetition — a participant with `PaidTickets=3, FreeTickets=1` contributes four copies of their name to the flattened `tickets` array (`RaffleManager`'s ticket-expansion happens in `BackendClient.BuildCreateRequest`, `BackendClient.cs:93-113`). This is intentional and correct for a raffle-drum model.
6. **Duplicate names:** cannot exist as *separate* participants — `RaffleManager.FindOrCreateParticipant`/`FindParticipant` merge by case-insensitive normalized name (`RaffleManager.cs:193-209`). "Weight" is realized purely through the ticket-count fields, never through duplicate participant rows.
7. **Name normalization:** `NormalizeName` (`RaffleManager.cs:165-180`) trims whitespace and truncates at the first `@` character — this is meant to strip a `Name@World` suffix a player might paste, but it means **HomeWorld is silently discarded**: two different characters named "Ada" on different worlds collide into a single participant. There is also a hard minimum: normalized names under 3 characters are rejected client-side (`TryValidateName`, `RaffleWindow.cs:465-475`) — this does prevent blank/near-blank entries, but nothing validates that a name corresponds to a real, currently-present player; entries are entirely trust-based free text (the only in-game assist is "Use target," which reads whatever is currently targeted).
8. **Empty ticket pool:** `spinRaffle` returns `null` and the client is told `"No tickets to spin."` (`server.js:186-188,270-273`) — no crash, no winner assigned.
9. **Can a winner be redrawn / is there a guard against double selection?** **No guard exists.** See Finding R-1 in §19 — nothing in the `spin` WS handler checks whether a winner already exists or whether a spin is already in flight; a second `spin` message simply re-runs the whole draw and overwrites the previous result. There is no winner-history array anywhere in the donor (backend or plugin) — only the single latest `winnerName` survives.
10. **Are previous winners excluded from later draws?** No — `spinRaffle` always draws from the full, unmodified `raffle.tickets` array; a winning ticket is never removed from the pool, so the same person can win twice in a row with nothing preventing it. This is a genuine product ambiguity, not obviously a "bug" — see §24.
11. **Is selection persisted?** Yes, immediately, as part of the same `raffle` object write (`server.js:203-206`), so it survives a backend restart.
12. **Race/concurrent draws:** because Node processes each WebSocket message to completion synchronously (no `await` inside `spinRaffle`), two `spin` messages arriving on two different event-loop ticks never interleave at the JS-object level, but each still fully executes — the *net effect* is "last spin wins," both on the server's persisted state and on every browser's displayed result (because `wheel.js`'s `queueWinner` clears any pending reveal timer whenever a new `spin` message arrives, `wheel.js:111-126`, so an in-flight reveal is silently discarded in favor of the newer one). Server and client end up consistent with each other, but neither protects the *host* from an unintended redraw.
13. **Does a browser reload ever produce a different perceived result?** No — the browser is never authoritative; a reload just re-fetches whatever `winnerName`/`rotation` the server currently holds, with no re-randomization.
14. **Reconnect and the selected winner:** a rejoin after disconnect immediately receives the current authoritative `winnerName`/`rotation` in the `state` message (`server.js:247`, `wheel.js:54-62`) — no state is lost, only the *animation* of an in-progress spin is lost if the client wasn't connected for it (cosmetic only).

---

## 9. Entrant / Duplicate / Ordering / Weight Semantics

- **Can the same name exist more than once?** No, not as separate participant rows (merged by normalized name). Effective duplication only happens in the backend's flattened ticket array, and only as a direct function of the `PaidTickets`/`FreeTickets` counts — this **is** the donor's weighting mechanism, not an accident to "fix."
- **Case sensitivity:** matching is case-insensitive (`StringComparison.OrdinalIgnoreCase`, `RaffleManager.cs:208`); display preserves whichever casing was entered first (or most recently overwritten, since there is no rename-participant control — a participant's stored `Name` never changes once created except by being removed via `CleanupParticipant` and re-added under new casing).
- **Leading/trailing whitespace:** trimmed by `NormalizeName` (`RaffleManager.cs:167`).
- **Explicit weight/count field:** yes — `PaidTickets`/`FreeTickets` integers directly control repetition count in the flattened pool.
- **Reordering:** there is no explicit reorder control in the plugin UI at all (participants render in list/insertion order, `RaffleWindow.cs:553-588`, with no drag/sort). Reordering is moot anyway because the backend always reshuffles on any ticket-set change (§8.3).
- **Shuffle:** happens automatically and only server-side; there is no manual "shuffle now" button anywhere in the donor.
- **Does ordering influence the winner algorithm?** No (§8.3).
- **Does browser display order equal authoritative order?** The browser wheel slices render in whatever order the server's `tickets` array is currently in (`wheel.js:226-246`) — this *is* the authoritative order, it's just that the authoritative order itself has no bearing on odds (every slice's *probability* of being chosen is independent of its angular position, since selection is `crypto.randomInt` over the index, not a positional bias in the shuffle).
- **Concurrent reordering by multiple clients:** not applicable — there is no client-side reorder capability at all, donor- or browser-side.
- **Is ordering persisted?** The shuffled array's order is persisted as part of the raffle blob (§7), but is functionally irrelevant to fairness.
- **Delete/re-add edge cases:** deleting a participant is implicit — `AdjustTickets`/`CleanupParticipant` (`RaffleManager.cs:140-158,211-217`) removes a participant once both `PaidTickets` and `FreeTickets` reach `≤0` via the per-row `-Paid`/`-Free` small buttons (`RaffleWindow.cs:568-587`); there is no explicit "delete participant" button. Re-adding the same normalized name afterward creates a fresh row that resumes at whatever paid/free counts are entered next — no memory of prior ticket counts survives a full removal, which is expected/correct behavior for a "zeroed out" participant.

---

## 10. Reconnect / Resume / Multi-Client Behavior

| Scenario | Donor behavior |
|---|---|
| Dalamud closes / plugin reloads | Local raffle list and settings reload from `raffles.json` at next construction (`Plugin.cs:34-35`); `LastSelectedRaffleId` restores the active raffle. Backend is unaffected — it never knew the plugin was gone (no session/heartbeat concept between plugin and backend at all; the plugin is a pure HTTP client, not a persistent connection). |
| FFXIV crashes | Same as above — nothing plugin-side is memory-only that isn't also saved on every mutation (`RaffleManager.Save()` is called after essentially every mutating method). |
| Backend restarts | All raffles reload from `data/raffles.json` (§7); any browser tabs that were connected lose their WebSocket and must manually refresh (no auto-reconnect, see below) — on refresh they rejoin and get the current state immediately. |
| Browser closes / refreshes | No authoritative state lost — a refresh simply rejoins (§8.14). Mid-spin animation state is lost on refresh (cosmetic only — the final `winnerName`/`rotation` is still delivered on rejoin, with no "spin was in progress" flag, so a viewer who refreshes mid-spin sees the end state immediately with no animation). |
| Network disconnects | **No automatic reconnect exists in `wheel.js`.** The `close` handler only sets a status string (`"Disconnected from server."`, `wheel.js:86-88`) — there is no retry/backoff loop; the operator/viewer must manually reload the page. |
| Organizer (host) reconnects | Rejoins with the same `hostToken` from the URL; receives current state; nothing server-side distinguishes "the original host reconnecting" from "a second host tab" — both are just sockets with `role==='host'` validated by the same token. |
| A second organizer client connects | Fully permitted — any number of sockets can join with `role='host'` as long as they present the correct `hostToken` (e.g., the operator has the host link open in two tabs, or shares the "host" link with a co-organizer). Both can independently click Spin — this is the concrete mechanism behind Finding R-1 (§19). |
| Multiple viewer clients | Fully supported and consistent — `broadcastState`/`broadcastSpin` push to every registered socket for that raffle (`server.js:157-183`), so all viewers see the same state/winner. |
| Winner selected while another client has stale state | Not really possible to observe "staleness" incorrectly, because every client is push-only and re-syncs fully on `state`/`updated`; the only stale-state risk is the plugin's own hand-typed/unfetched `WinnerName` field (§4 step 6, §8.9), which is a plugin-local UI gap, not a multi-client protocol issue. |

**Resume token / room code model:** the "room/game code" is simply the server-generated `raffleId` embedded in the URL path; the "resume" mechanism is nothing more elaborate than "keep the URL." There is no signed session, no expiry, no revocation mechanism for a leaked link (§23).

---

## 11. Dalamud Client

**File:** `FFXIVRaffle4All/Windows/RaffleWindow.cs` (598 lines) + `Plugin.cs` (75 lines) + `Configuration.cs` (23 lines). Single `sealed class Plugin : IDalamudPlugin`; a single `RaffleWindow : Window`; small stateless service classes (`RaffleManager`, `RaffleRepository`, `BackendClient`, `XlsxExporter`, `XlsxImporter`) with no other windows, no floating popups, no nested classes of note.

**Commands:** `/raffle` (`Plugin.cs:12,43-46`) opens the window; Dalamud's own installer "gear" `OpenConfigUi` hook does the same (`Plugin.cs:41,71-74`). No sub-commands, no chat-argument parsing.

**Screen (one window, two side-by-side child regions):**
- *Left panel* (`DrawLeftPanel`, `RaffleWindow.cs:93-114`): raffle selector combo + create/rename/reset (no confirmation on reset); numeric settings fields with live pot/take computation; ticket-entry group (name field, "Use target" button, paid/free int fields, Add buttons); backend section (URL field + Save, "Generate host link"/"Generate viewer link" — **both call the identical `GenerateLinksAsync` method**, `RaffleWindow.cs:263-272`, so the two buttons currently do exactly the same thing — a naming/UX duplication worth noting, not a functional bug); read-only link rows with per-field clipboard-copy buttons; "Fetch winner from backend" button; a freely-editable "Winner" text field with no validation; export/import section (native file dialogs, XLSX only); a single mutable status-message line at the bottom (no history/log of prior messages).
- *Right panel* (`DrawRightPanel`, `RaffleWindow.cs:538-597`): a participants table (Name, Paid, Free, and per-row `+Paid`/`-Paid`/`+Free`/`-Free` small buttons) with no sort control and no explicit delete button (zeroing both counts via the `-` buttons removes the row implicitly).

**Does the plugin create raffles?** Yes (local only until published). **Manage entrants?** Yes, fully. **Select a winner?** No — never; it can only display or hand-edit whatever value is in its local `WinnerName` field. **Send a winner to the backend?** No — the plugin never writes `WinnerName` back to the backend; the only backend-writing call is the full-raffle upsert (`POST /api/raffles`), which does not include a winner field in its request shape (`RaffleCreateRequest`, `BackendClient.cs:126-134`) — winner flows strictly backend→plugin, never plugin→backend. **Opens a browser?** No — it only copies links to the clipboard; the operator must paste them into a browser themselves. **Copies clipboard?** Yes (`ImGui.SetClipboardText`, `RaffleWindow.cs:323`). **Persists active state?** Yes, on essentially every mutation. **Reconnects automatically?** N/A — the plugin has no persistent connection to lose (pure request/response HTTP). **Depends on browser functionality?** No — every plugin-side feature (settings, tickets, export/import) works with zero browser/backend interaction; only link generation and winner-fetch touch the network.

**Dead/unwired code:** none found — every button in `RaffleWindow.cs` is wired to a real, reachable handler. The class is small enough that a full read (598 lines) confirms no orphaned methods.

---

## 12. Browser Client

**Files:** `backend/public/host.html` (41 lines), `view.html` (40 lines), `wheel.js` (405 lines), `styles.css` (219 lines, pure CSS/animation, no logic).

**Connection model:** no login, no lobby, no server-issued player identity of any kind. Identity is entirely positional: the URL path `/host/:raffleId/:token` or `/view/:raffleId/:token` — `wheel.js` reads `raffleId`/`token` straight from `window.location.pathname` (`wheel.js:24-26`) and the page's *role* (`host` vs `viewer`) is hard-set per-file via an inline `window.RAFFLE_ROLE` script (`host.html:36-38`, `view.html:35-37`), not derived from the token. A single shared `wheel.js` drives both pages; the only visual/behavioral difference is that `host.html` renders a "Spin" button and `view.html` does not (`host.html:22` vs. absent in `view.html`) — but this is purely a rendering choice, since a viewer-role socket attempting to send `{type:'spin'}` would simply be rejected server-side (`"Only the host can spin."`, `server.js:264-266`) if it somehow did send one; the real authorization boundary is server-side, not the missing button.

**Public vs. organizer access:** anyone with the viewer link sees the live wheel/winner read-only; anyone with the host link can spin. Both are pure capability-URLs (§23) — there is no separate "organizer login."

**Entrant display:** the wheel is divided into `state.tickets.length` equal slices, one per *ticket* (not per unique participant) — so a participant with 4 tickets occupies 4 separate slices, all showing their name, which is a correct visualization of their actual odds share (`drawWheelBase`/`drawLabel`, `wheel.js:226-270`).

**Animation:** `animateSpin` eases the wheel rotation to the server-supplied target over `durationMs` (`wheel.js:343-372`, minimum 5000ms even if the server sent less — `Math.max(5000, durationMs || 6000)`), then a separate 3-second "highlight takeover" animation zooms the winning slice to fill the wheel before the winner name is text-revealed (`revealWinnerAfterHighlight`/`drawHighlightTakeover`, `wheel.js:128-146,283-330`). Purely cosmetic; no randomization happens here.

**Randomization:** confirmed **not** present in the browser at all (§8) — worth stating explicitly per the audit brief's requirement.

**Reconnect/reload behavior:** covered in §10 — no auto-reconnect, refresh is always safe (never produces a different result), mid-spin animation is lost on refresh but the end state is not.

**Mobile/responsive behavior:** `styles.css` has two breakpoints (900px: stacks the wheel and the controls column vertically; 600px: reduces padding/heading size, `styles.css:196-219`) — genuinely responsive, no separate mobile-only markup or logic.

**Any controls only available in the browser?** Only the "Spin" button itself — the plugin has no equivalent trigger (it can only fetch the *result* of a spin after the fact, never cause one).

**Is the browser merely visualization, or part of game authority?** **Purely visualization plus the human action of pressing Spin.** All state (who's entered, ticket odds, the actual winner) is decided and held by the backend; the browser holds zero authoritative data of its own (confirmed by full read of `wheel.js` — `localStorage`/`sessionStorage` are not used anywhere in this file at all, unlike the Bingo donor's one card-zoom preference).

---

## 13. FFXIV Integration

**None**, beyond reading the currently-targeted game object's name for the "Use target" convenience button (`ITargetManager.Target.Name.TextValue`, `RaffleWindow.cs:213-216,452-463`). There is no `/random`, `/dice`, tell/party/shout/yell/say listening, emote handling, clipboard-*reading* (only clipboard-*writing* for link copy), chat command beyond `/raffle` itself, framework-thread dispatch of note, ECommons usage, or any other Dalamud chat API usage anywhere in the donor plugin. This was verified by a full read of every `.cs` file in `FFXIVRaffle4All/` (Configuration, Plugin, Models, and all four Services classes plus the one Window) — none of them reference `IChatGui`, `IFramework`, or any chat/game-event subscription.

---

## 14. Settings Inventory

| Setting | Default | Scope | Persistence | UI control | Secret? | Masked? | Used where |
|---|---|---|---|---|---|---|---|
| `BackendBaseUrl` | `""` (empty) | Global (all local raffles share one) | Dalamud plugin config, via `PluginConfiguration.Save()` | Plain `ImGui.InputText`, `RaffleWindow.cs:255-261` | No — a server address, not a credential | No | `BackendClient.GetBaseUri()` for every network call |
| `LastSelectedRaffleId` | `null` | Global | Dalamud plugin config | Not directly editable — set implicitly whenever a raffle is selected/created | No | N/A | `RaffleManager` constructor, to restore the active raffle |
| `Raffle.Name` | `"Raffle {yyyy-MM-dd HHmm}"` | Per-raffle | Plugin `raffles.json` | `ImGui.InputText`, rename/create fields | No | N/A | Display, XLSX export |
| `RaffleSettings.StartingPot` | `0` | Per-raffle | Plugin `raffles.json` | `ImGui.InputFloat` | No | N/A | Plugin-only pot math (never sent meaningfully to backend logic) |
| `RaffleSettings.TicketCost` | `0` | Per-raffle | Plugin `raffles.json` | `ImGui.InputFloat` | No | N/A | Same |
| `RaffleSettings.PrizePercentage` | `100` | Per-raffle | Plugin `raffles.json` | `ImGui.InputFloat`, clamped 0–100 | No | N/A | Same |
| `RaffleSettings.PaidTicketsForFree` | `0` | Per-raffle | Plugin `raffles.json` | `ImGui.InputInt` | No | N/A | Free-ticket bonus rule |
| `RaffleSettings.FreeTicketsPerBlock` | `0` | Per-raffle | Plugin `raffles.json` | `ImGui.InputInt` | No | N/A | Same |
| `Raffle.WinnerName` | `null` | Per-raffle | Both stores independently (§5) | Freely-editable `ImGui.InputText`, no validation | No | N/A | Display, XLSX export |
| `Raffle.HostUrl` / `ViewerUrl` | `null` | Per-raffle | Plugin `raffles.json` (as plain strings, tokens embedded) | Read-only `InputText` + Copy button | **Effectively yes** — the token is embedded in the URL string | No (fully visible/copyable, by design) | Sharing, `BackendClient.FetchRaffleAsync` token extraction |
| `hostToken` / `viewerToken` | server-generated UUID | Per-raffle | Backend `data/raffles.json`; plugin-side only as a URL substring | Never directly displayed/edited as a bare token — only visible embedded in the link strings above | Yes | No (by donor design; not masked anywhere) | WS `join` auth, `GET /api/raffles/:id` auth |

There is no organizer/room "password," API key, or admin key anywhere in the donor. The only per-raffle secrets are the server-issued tokens, and the donor's existing behavior already matches VenueOS's stated product requirement of "plainly visible, unmasked, copyable" credentials (§15) — there was never a masking mechanism to remove.

---

## 15. Credential Behavior

**Donor:** `BackendBaseUrl` is a plain, unmasked `ImGui.InputText` (`RaffleWindow.cs:255`), fully selectable/copyable/editable, saved via `configuration.Save()`, and reloaded on every plugin construction (`Plugin.cs:31-32`) — persists across plugin reload and FFXIV restart. Host/viewer tokens are never entered by a human at all (server-generated); they are only ever *displayed* embedded in a full URL, in a read-only-but-copyable field (`RaffleWindow.cs:314-327`). Nothing in the donor logs a raw exception message that would echo a token-bearing URL back into any persistent log — the donor has **no logging framework at all** (confirmed by a full read of every plugin `.cs` file — no `Log.Information`/Serilog/console usage anywhere in the plugin; failures surface only as an in-window `statusMessage` string visible solely to the operator). The backend's only failure output is `console.error(err.message)` on load/save I/O errors (`server.js:25,45`), which never includes raffle content or tokens.

**VenueOS today:** `VenueRaffleClient.cs` already follows the "no masking, but redact in diagnostics" split correctly at the *protocol* layer — `LocalRaffle.WithoutSecrets()` strips `HostUrl`/`ViewerUrl` specifically for any future diagnostics/dashboard projection (`VenueRaffleClient.cs:14`), confirmed exercised by `RaffleClientTests.cs`'s `Local_raffle_strips_urls_from_diagnostic_copy` test. However, **there is currently no UI in `RaffleOperatorPanel` that displays `BackendBaseUrl`, `HostUrl`, or `ViewerUrl` at all** — so the "must be plainly visible/unmasked/copyable" requirement is not actually violated today, but only because the feature that would display them doesn't exist yet (§17). When it is built, it must follow the donor's own precedent (plain `Forms.TextField`, no password-style masking) — which is also the pattern already used by Mair's Trivia's "Backend URL" field (`MairsTriviaOperatorPanel.cs:49`) — and must route any failure message through `DiagnosticsService.RecordFailure`, which auto-redacts `token=`/`accessToken`/`refreshToken`/`AdminKey`/`RoomKey`/`password` markers (`NEW_MODULE_GUIDE.md` §25–26). Note that a raffle host/viewer URL's token does **not** appear as `token=` in the URL *path* (it's a path segment, `/host/{id}/{token}`, not a query parameter) — so if a future Raffle service ever interpolates a raw `HostUrl`/`ViewerUrl` into a `RecordFailure` message, **`DiagnosticsService.Redact`'s existing marker list would not catch it**, since there is no `token=` substring to trigger on. This is a concrete adaptation note for reconstruction, not a donor bug (see §24/§25 recommendations).

---

## 16. Venue / Multi-Venue Adaptation

The donor has no venue/multi-venue concept whatsoever — it is a single-operator, single-backend-URL tool; "Raffle Name" is the only identity field, and it identifies the *raffle*, not any venue/event. There is nothing here that plays the role of VenueOS's `VenueProfile.DisplayName`, so — unlike Mair's Trivia/Tournament/Bingo, which each inherited an independent editable `VenueName` field from their own donors that now needs superseding by `venues.Current.DisplayName` (`NEW_MODULE_GUIDE.md` §10, §21) — **Raffle has no such field to fix**. This is a genuine simplification relative to the other three backend modules: a future reconstruction only needs to *add* venue-scoping (already done, via `VenueRaffleService.Load(Guid)`/per-venue `GetModuleConfig`), not *remove* a conflicting legacy venue-name field.

**Per-venue isolation in the current scaffold:** correct in shape — `VenueRaffleService.Load` cancels and replaces its `CancellationTokenSource` and reloads `Settings` from `profiles.GetModuleConfig(venueId, "games.raffle", 1, ...)` on every venue switch (`Operations.cs:687`), matching the guide's required pattern (§11, §"42b"), and is exercised by `RaffleClientTests.cs`'s `Venue_switch_cancels_inflight_raffle_request` test, which confirms an in-flight `UpsertAsync` call is cancelled when the venue changes mid-request. **One structural note for reconstruction:** `VenueRaffleSettings.Connection` is a single `RaffleConnectionSettings` (one `BackendBaseUrl`) shared by every raffle inside a venue (`Operations.cs:682`) — this mirrors the donor exactly (its `PluginConfiguration.BackendBaseUrl` is likewise one global URL for all local raffles), so it is not a regression, just something to keep in mind if a later product decision wants per-raffle backend URLs (the donor never supported that either).

---

## 17. Current VenueOS Raffle Scaffold

**Module ID:** `games.raffle`. **Display name:** "Raffle". **Description:** "Backend-compatible raffle operations." **Icon:** `"ticket"`. **`UnderDevelopment: true`. `DisplayOrder: 9`. `IsEnabled = false` by default** (`Operations.cs:695`) — confirmed by `UnfinishedModuleDefaultsTests.cs` (`Raffle_defaults_disabled_on_a_fresh_install`, `Raffle_is_flagged_under_development`) and `ModuleDisplayOrderTests.cs` (comment: "Raffle/TournamentControl default `IsEnabled = false` (fresh install) — HomeScreen filters those out").

**Lifecycle:** `InitializeAsync` is a no-op; `OnVenueChangedAsync` calls `raffle.Load(c.VenueId)` (reload settings for the new venue); `Tick` is a no-op (no polling, no periodic refresh of any kind); `Draw()`/`DrawSettings()` both call the same delegate (`rafflePanel.Draw`) — no separation between persistent config and live operation yet, matching the guide's documented "known current gap" (§8/§21) shared by every module except Party Finder.

**Settings/persistence:** `VenueRaffleSettings(RaffleConnectionSettings Connection, List<LocalRaffle> Raffles, string? SelectedRaffleId)`, schema version `1`, stored/loaded via the generic `VenueProfileService.GetModuleConfig`/`SaveModuleConfig` mechanism (`Operations.cs:687,691`) — correct, idiomatic use of the shared per-venue config store (§11).

**Service/client architecture:** `VenueRaffleClient` (protocol layer, `VenueRaffleClient.cs`, matches the donor wire contract exactly — see §21 for verification) → `VenueRaffleService` (venue-scoped state + `Create`/`UpsertAsync`/`FetchAsync`, `Operations.cs:683-691`) → `VenueRaffleModule` (`IVenueModule` wrapper, `Operations.cs:694-695`) → `RaffleOperatorPanel` (UI, `NativeOperationsPanels.cs:695-722`).

**UI actually present today (`RaffleOperatorPanel.Draw`):** a "New raffle name" field + "Create raffle" button, and a read-only list of existing raffles showing name, total ticket count, and a winner badge if `WinnerName` is set. **That is the entire operator-facing surface.** Confirmed by full read of the panel (28 lines of body) and by grepping the whole `VenueOS.Plugin` tree for `BackendBaseUrl`/"Backend URL": **zero matches in any Raffle-related file** — contrast with Mair's Trivia, which does have a "Backend URL" `Forms.TextField` (`MairsTriviaOperatorPanel.cs:49`). This means:
- There is no way to set/edit the connection URL through any UI, ever, in the current build.
- There is no ticket-entry UI (no name field, no paid/free ticket inputs, no "Use target").
- There is no `RaffleSettings` UI (no starting pot / ticket cost / prize % / free-ticket-rule fields).
- There is no "Generate host/viewer link" button, no link display, no copy-to-clipboard.
- There is no "Fetch winner" button.
- There is no XLSX import/export.
- There is no WebSocket client anywhere in VenueOS (confirmed absent from `VenueRaffleClient.cs`, which only implements the two HTTP calls) — so even if the rest of the UI existed, there would be no way to actually *spin* the wheel or receive a live spin broadcast from VenueOS; `UpsertAsync`/`FetchAsync` are the only network operations, both already implemented and unit-tested.

**Tests:** `RaffleClientTests.cs` (5 tests) — verifies the camelCase ticket-expansion contract against a mocked HTTP handler, token URL-escaping, that errors/cancellation never leak "secret" into an error string, that `WithoutSecrets()` strips URLs, and that a venue switch cancels an in-flight request. These are solid protocol-layer tests but, necessarily, do not (and cannot, per §30 of the guide — there is no `VenueOS.Plugin` test project) exercise any of the missing UI described above, because that UI does not exist to test.

---

## 18. Donor Parity Matrix

| Feature | Donor | Current VenueOS | Gap | Risk |
|---|---|---|---|---|
| Backend connection URL configuration | Plain text field, save button | Field exists in the data model (`RaffleConnectionSettings.BackendBaseUrl`) but **no UI control anywhere sets it** | Cannot be configured through the app at all today | **CRITICAL** — makes every network feature below unreachable |
| Create raffle | Yes | Yes | None | — |
| Rename raffle | Yes | No | Missing | LOW |
| Reset raffle (clear participants/winner/links) | Yes (no confirmation — donor bug, not a VenueOS gap) | No | Missing entirely | LOW (arguably safer that it's currently impossible to do accidentally) |
| Delete raffle | No (donor doesn't have this either) | No | Parity (both lack it) | LOW |
| Raffle settings (pot/cost/%/free-ticket rule) | Full UI + live pot computation | Data model exists (`LocalRaffle`/`RaffleSettings` are referenced by the client) but **no settings UI in the panel at all** | Missing UI entirely | **HIGH** — no way to run a real paid raffle |
| Add paid/free tickets to a participant | Full UI incl. "Use target" | **No UI at all** | Missing entirely | **CRITICAL** — no way to enter any participant |
| Adjust/remove ticket counts per participant | Per-row `+/-` buttons | No | Missing entirely | HIGH |
| Generate host/viewer links | Two (identical) buttons, copyable link display | `UpsertAsync` exists and is tested, but **no button/UI calls it** | Dead code path | **CRITICAL** |
| Fetch winner from backend | Button | `FetchAsync` exists and is tested, but **no button/UI calls it** | Dead code path | HIGH |
| Display winner | Editable field, no validation | Read-only badge if present in local state | VenueOS is actually *safer* here (no accidental hand-edit) but can never receive a real winner today since `FetchAsync` is never invoked | HIGH (blocked by the above) |
| Live wheel spin (WebSocket) | Full host+viewer wheel, CSPRNG server draw | **Not implemented at all** — no WS client exists in VenueOS | Missing entirely, by design per `VENUE_RAFFLE_3A.md` ("out of scope") | MEDIUM — a documented, deliberate scope decision, not an oversight, but still a product decision to revisit (§24) |
| XLSX import/export | Full 3-sheet workbook, both directions | Not implemented | Missing entirely | MEDIUM |
| Per-venue isolation | N/A (donor has no venues) | Implemented correctly (`Load`/cancellation pattern, tested) | N/A — VenueOS-only concern, done well | — |
| Venue-name field independent of Venue Profile | N/A (no such field exists in donor) | N/A (none introduced) | None — nothing to fix here, unlike Trivia/Tournament/Bingo | — |
| Credential visibility (unmasked, copyable) | Plain text, by design | Data model already protects `WithoutSecrets()` for diagnostics; nothing to un-mask because nothing is displayed yet | N/A until the link-display UI is built (§15) | — |
| Automated protocol tests | **None** | 5 xUnit tests covering the client contract | VenueOS is strictly ahead here | — |

---

## 19. Bugs / Risks with Severity

**Donor bugs (confirmed by code-path tracing):**

- **[R-1] HIGH — No guard against repeated/double "spin."** `server.js:251-277` (the `type === 'spin'` branch) checks only `ws.raffleId`, raffle existence, and `ws.role === 'host'`. It never checks whether `raffle.winnerName` is already set or whether a spin is already animating anywhere. A second `spin` message — from a genuine fast double-click before `spinButton.disabled = true` takes visual effect (`wheel.js:347-349`), or from a second host-authenticated tab/window — re-runs `spinRaffle()` (`server.js:185-214`) and silently overwrites the previous `winnerName`/`rotation`, broadcasting the new result to every connected client with no history of the discarded first draw. **Failure scenario:** a host opens the host link in two browser tabs (e.g., one on a stream-overlay display, one on their own laptop) and clicks Spin on each within the same few seconds; two independent winners are drawn back-to-back, the backend persists only the second, and everyone who was watching sees the wheel spin twice with no explanation of why the first winner "changed."
- **[R-2] HIGH — `POST /api/raffles` has no authentication.** `server.js:110-120`, backed by `upsertRaffle` (`server.js:71-108`). Any HTTP client that knows a `raffleId` (visible in plaintext as the second path segment of every shared host/viewer URL) can call this endpoint directly and completely replace that raffle's `name`/`settings`/`participants`/`tickets`, resetting `winnerName`/`rotation` to `null` as a side effect of the ticket-set change (`server.js:82-83`). Existing `hostToken`/`viewerToken` are preserved (so the attacker can't hijack existing links), but the raffle's *content* is fully overwritable by anyone, with no token required at all for this specific endpoint (contrast with `GET /api/raffles/:id`, which does require a token). **Failure scenario:** a viewer link is shared in a public Discord channel; anyone who inspects that URL's `raffleId` can `POST` a replacement participant list (e.g., "Alice" ×1000, everyone else removed) to `/api/raffles`, and the next legitimate spin the real host runs will draw from the attacker's substituted pool.
- **[R-3] MEDIUM (plausible, not fully certain) — Unguarded `ws.send()` inside broadcast loops.** `broadcastState` (`server.js:164-173`) and `broadcastSpin` (`server.js:175-183`) call `socket.send(message)` for every socket in a `Set`, with no `try/catch`. A socket is only removed from that `Set` on its `close` event (`server.js:280-286`); there is no `error`-event cleanup and no readiness check (`socket.readyState`) before sending. If a connection is in a non-OPEN state at the moment of broadcast (e.g., a half-closed or already-terminating socket whose `close` event has not yet fired), depending on the installed `ws` version's behavior, `.send()` can throw synchronously. Because this call happens inside a bare `ws.on('message', ...)` callback with no surrounding `try/catch` anywhere in `server.js`, and there is no `process.on('uncaughtException', ...)` registered, such a throw is not caught anywhere in this codebase — in Node.js, an uncaught exception thrown from inside an event-emitter callback terminates the process by default. **Failure scenario (plausible, version-dependent):** one bad/dying connection among several open sockets for a raffle causes every other viewer's spin/update broadcast to silently stop being delivered for the remainder of that iteration, or crashes the whole backend process (taking down every venue/raffle it hosts, since this is a single shared Node process with no per-raffle isolation).
- **[R-4] MEDIUM — Corrupt persistence file silently discards all data, both sides.** `loadRaffles()` (`server.js:16-27`) and `RaffleRepository.Load()` (`RaffleRepository.cs:23-39`) both swallow any parse/read exception and silently fall back to an empty collection, with no backup file, no versioned snapshot, and no warning surfaced to the operator (the plugin side doesn't even log to console; the backend side logs to its own console only). A single malformed write (e.g., process killed mid-`writeFileSync`) permanently and silently erases every raffle that store held.
- **[R-5] LOW — "Generate host link" and "Generate viewer link" are the same button twice.** `RaffleWindow.cs:263-272` — both call `GenerateLinksAsync(raffle)` with no differentiation; there is no way to regenerate only one link type. Purely a UX/naming duplication, not a functional defect (the response always contains both URLs anyway).
- **[R-6] LOW — No confirmation on "Reset current raffle."** `RaffleWindow.cs:152-155` → `RaffleManager.ResetCurrent` (`RaffleManager.cs:86-98`) is a single click that irrecoverably clears participants/winner/links for the current raffle with zero confirmation dialog, unlike genuinely destructive actions elsewhere in VenueOS's own UI kit (`ConfirmDialog`).
- **[R-7] LOW — HomeWorld silently discarded in name normalization.** `RaffleManager.NormalizeName` (`RaffleManager.cs:165-180`) truncates at the first `@`, so `"Name@World"` becomes `"Name"` — two different characters with the same first name on different worlds are treated as the same participant everywhere (ticket totals combine, and a "winner" of `"Name"` doesn't disambiguate which of them actually won).
- **[R-8] LOW — Unauthenticated public GET of the host/view HTML shell.** `server.js:143-149` serves `host.html`/`view.html` for *any* `raffleId`/`token` path combination with zero validation at the HTTP layer (only the subsequent WebSocket `join` actually checks the token) — of limited practical impact since the static shell reveals nothing sensitive on its own, but it does mean a link-preview bot (Discord/Slack unfurling) or web crawler can freely fetch any guessed raffle path and receive a 200, which is a mild information-disclosure/fingerprinting surface (confirms a `raffleId` "exists" even without a valid token).

**VenueOS-side risks (current scaffold), distinct from the donor's own bugs:**

- **[V-1] CRITICAL — The connection URL can never be set.** No UI path exists to populate `RaffleConnectionSettings.BackendBaseUrl` (§17/§18); every network method (`UpsertAsync`, `FetchAsync`) will always fail with `"Backend base URL is not set."` in production today. This is the single blocking gap before anything else in the module can function.
- **[V-2] HIGH — No participant/ticket entry UI at all.** Even if the URL were configurable, there is no way to add a single participant/ticket in the current build — `Create` is the only mutating method the panel calls.
- **[V-3] MEDIUM — `VenueRaffleModule.Tick` is a no-op; no polling.** Unlike a design that periodically re-`FetchAsync`s to catch a winner drawn via the (not-yet-built) live wheel, the current scaffold has no mechanism to notice a winner at all short of a future explicit "Fetch" button being clicked — worth deciding deliberately during reconstruction (§24) rather than defaulting to "poll every N seconds" without considering the backend's complete absence of rate limiting (§6/§23) that such polling would hit directly.

---

## 20. Test Coverage / Missing Regressions

**Donor:** zero automated tests of any kind (no test project referenced by any `.sln`, no `backend/test`, no `*.spec.js`/`*.test.js` anywhere in the repository). Every behavior described in §4–§10 is therefore only "tested" by whatever manual QA the original author did; there is no regression protection at all on the donor side for any of the following: empty raffle, single entrant, duplicate-name handling, the free-ticket bonus math, the shuffle-on-change vs. keep-on-no-change branch, repeated-spin behavior (R-1), the upsert endpoint's lack of auth (R-2), or the persistence-corruption fallback (R-4).

**VenueOS (`RaffleClientTests.cs`):** covers the HTTP contract well — ticket-expansion camelCase shape, token URL-escaping, non-2xx/cancellation error paths not leaking "secret" into messages, `WithoutSecrets()` stripping, and venue-switch cancellation. **Not covered, because the corresponding service/UI logic doesn't exist yet:** participant/ticket-count mutation logic (there is none to test — `VenueRaffleService` has no `AddPaidTickets`/`AdjustTickets`-equivalent), free-ticket bonus computation (not ported), name normalization/duplicate-merge behavior (not ported — `LocalRaffle.Participants` is a plain list with no merge-by-name logic anywhere in `VenueRaffleClient.cs`/`Operations.cs`, meaning if this were wired up today, duplicate normalized names would create *separate* participant rows rather than merging, a behavioral drift from the donor that would need an explicit decision + test during reconstruction, not just a straight port), the repeated-fetch/"already has a winner" scenario, and the venue-authority interaction between `Settings.Connection` and a not-yet-existing per-raffle settings UI.

**High-value missing regressions for a reconstruction to add:** (1) an "already has a winner — is a second draw request rejected/confirmed?" test, matching whatever product decision is made about R-1; (2) an "unauthenticated upsert" test at the client level confirming VenueOS's own client never silently trusts a response it didn't request (defense in depth against R-2, since the fix for R-2 itself lives in the backend, which is out of scope to modify per this audit's donor-is-read-only constraint — see §24); (3) a duplicate-normalized-name merge test once ticket-entry logic is ported; (4) a corrupt-persisted-payload recovery test for the Raffle schema specifically (the generic `VenueProfileService.Recover<T>` mechanism already covers this at the framework level per `NEW_MODULE_GUIDE.md` §13, so this may already be adequately covered — worth confirming with a Raffle-specific test rather than assuming).

---

## 21. Git History Findings

Donor HEAD (`f685a41`) is the correct authoritative version — verified via `git diff 111b3a9 HEAD -- FFXIVRaffle4All backend README.me`, which shows **zero functional source changes**. The only diffs between the commit VenueOS's existing client was validated against (`111b3a9`, per `VENUE_RAFFLE_3A.md`) and current HEAD are: a Dalamud API-level bump (`14`→`15`) and version bump (`1.0.0.0`→`1.0.0.1`) reflected in the plugin manifest and rebuilt binaries/caches, an icon change (`7c0ba9d`), a README edit adding the live test-server URL (`5363c3a`), and a large zip artifact addition (`e172bf7`, the pre-built release zip now also committed at repo root) — none of which touch `Plugin.cs`, `RaffleWindow.cs`, `RaffleManager.cs`, `BackendClient.cs`, `RaffleRepository.cs`, `Models/RaffleModels.cs`, `XlsxExporter.cs`, `XlsxImporter.cs`, or `backend/server.js`/`backend/public/*`. **No regression risk from donor history — the wire protocol and every behavior described in this report has been stable since the repository's early commits** (`88e6a1b`, "Created base System and files", through `22ef459`, "Updated Web Page and plugin with better export and ability to import games and file picker windows," which is where XLSX import/export and the wheel/winner-animation behavior were added — `b24e156`/`c170e91`/`ba5fc98` only tuned the wheel's rotation math and animation, not the underlying `crypto.randomInt` selection logic).

VenueOS side (lighter pass, per the brief): the existing `games.raffle` scaffold was introduced and documented in `VENUE_RAFFLE_3A.md` ("Phase 3A"), validated against donor `111b3a9` — still accurate per the above. Nothing in the current uncommitted working-tree changes (all Tournament/Brackets-related) touches any Raffle file, confirmed by `git status`/`git diff --stat` showing only Tournament-named paths modified/added.

---

## 22. Deployment Findings

The donor backend has **no Render (or any other) deployment configuration file** in the repository — no `render.yaml`, no `server.config.js`/`admin.config.js` (unlike the Bingo donor, which had exactly these), no Dockerfile, no CI config. The only evidence of a deployment target is: (a) `package.json`'s `"start": "node server.js"` script (`backend/package.json`), (b) a live URL published in the plugin's own `README.me` ("Server settings to connect for the Test Server — https://ffxivraffle4all.onrender.com/"), and (c) the server listening on `process.env.PORT || 3000` (`server.js:289`), which is the conventional pattern for a PaaS like Render that injects `PORT`. Persistence is a plain file under `backend/data/` (`server.js:11-12`) — on Render's free/starter tiers without an attached persistent disk, this directory would not survive a redeploy/restart, which would silently reset every raffle (same failure mode as R-4, just triggered by infrastructure rather than corruption). This repository gives no way to confirm whether a persistent disk is actually attached to the live test server; that is operational knowledge outside what the source repository can answer. No WebSocket-unfriendly proxy/scaling concerns are visible in the code itself, but a horizontally-scaled (multi-instance) deployment of this exact backend would break immediately, since all state is a single in-process `Map` with no shared cache/pub-sub — this backend is single-instance by construction, whatever its actual hosting looks like today.

**Nothing here needs to be "preserved" by VenueOS**, since VenueOS does not run or fork the backend (§13 architecture note in `VENUE_RAFFLE_3A.md`: "The backend source was not copied, changed, proxied, or replaced") — VenueOS only ever acts as an HTTP client against whatever backend instance an operator points it at, exactly like the donor plugin does.

---

## 23. Security Findings

| Finding | Severity | Detail |
|---|---|---|
| No authentication on `POST /api/raffles` | **HIGH** | See R-2, §19. Anyone who learns a `raffleId` (leaked via any shared link) can overwrite that raffle's content with no token at all. |
| `role` is client-declared, gated only by token possession | LOW (by design, acceptable) | `join`'s `role` field is trusted once the accompanying token matches (`server.js:233-242`) — this is a legitimate capability-URL pattern; the risk is entirely in how the token itself can leak (below), not in this check. |
| Capability-URL leakage surface | MEDIUM | Host/viewer "auth" is knowledge of an opaque token embedded in a URL. Such URLs can leak via browser history, shared screenshots, chat-link-preview bots (partially mitigated — see R-8, the HTML shell reveals nothing on its own), or a copy-paste into the wrong channel. There is no expiry and no revocation mechanism for a leaked host token — once leaked, the only remedy is publishing an entirely new raffle (a new `raffleId` and new tokens), abandoning the old one. |
| No rate limiting anywhere (HTTP or WS) | MEDIUM | `POST /api/raffles` combined with no auth (above) means a scripted client could spam-overwrite a raffle or create unbounded new raffles (each persisted forever, §7), a mild resource-exhaustion vector against the `data/raffles.json` file's growth and the debounce-save timer. |
| CORS not configured | LOW / not applicable to real risk | No `cors` middleware or headers are set. This blocks nothing meaningful, since the actual risk (R-2) is exploitable directly via any non-browser HTTP client, which ignores CORS entirely; CORS's absence is neither a mitigation nor an additional exposure here. |
| No CSRF protection | LOW / not applicable | There is no cookie-based session to forge — the "auth" model is bearer-capability (the token in the URL/message), which is inherently not CSRF-vulnerable in the traditional sense; the real weakness is token leakage (above), not cross-site request forgery. |
| Secrets in logs | Not found | Donor has no logging framework; VenueOS's `DiagnosticsService.Redact` covers `token=`/`accessToken`/etc., but a raffle URL's token is a bare path segment, not a `key=value` pair, so it would **not** be caught by the existing redaction marker list if a future Raffle service ever interpolated a raw `HostUrl`/`ViewerUrl` into a diagnostic message (§15) — a concrete adaptation item, not a currently-exploited gap (nothing does this today). |
| Cross-organizer data access | Confirmed possible, ties to R-2 | Because upsert requires no token, one organizer's raffle content is not actually protected from another party who has only ever seen a shared viewer link (which necessarily reveals the `raffleId`). |
| Ownership checks | Absent | No concept of "who owns this raffle" beyond "whoever holds a token" for read/spin, and "anyone" for write (R-2). |

---

## 24. Product Decisions Required Before Reconstruction

These are genuine open design choices the donor leaves unresolved, not questions answerable purely by reading more code:

1. **Should a second "spin" be allowed once a winner already exists, and if so, should it require an explicit confirmation ("redraw"), or should VenueOS actively prevent/guard against the donor's current unguarded-repeat-spin behavior (R-1)?** The donor's current behavior (redraw always silently allowed, no history) may be intentional for some raffle formats (e.g., "spin again if the winner isn't present") or may simply be an oversight nobody hit in practice. This determines whether reconstruction should preserve donor behavior exactly, add a confirmation step, or add winner history/exclusion.
2. **Should previous winners be excluded from later draws within the same raffle, or should the pool remain unchanged (current donor behavior)?** Directly related to #1 — the donor never removes a winning ticket from the pool.
3. **Should completed-raffle history remain accessible indefinitely (current donor behavior on both plugin and backend), or should VenueOS introduce an archive/delete affordance the donor never had?** Related to the "no retention/cleanup" observation in §7/§22.
4. **Should VenueOS eventually implement the browser WebSocket "live spin" feature at all**, given `VENUE_RAFFLE_3A.md` explicitly scoped it out previously ("Browser WebSocket spin control remains out of scope because the standalone C# client does not implement it")? Without it, VenueOS can manage raffle rosters/pots and fetch a result, but cannot itself trigger the on-stream wheel animation — the operator would still need the donor's own browser page open to actually spin. This is the single biggest scope question for reconstruction and materially changes its size.
5. **Should the backend's authentication gap (R-2, no auth on the upsert endpoint) be treated as something VenueOS must defend against client-side** (e.g., warn the operator, or refuse to trust an upsert response that doesn't match what was sent), given that the donor backend itself is out of scope to modify per this audit's constraints? A VenueOS-side mitigation cannot fully close a server-side hole, but the product may still want a stance on it (e.g., surfacing a Diagnostics warning the first time a raffle is published to a given backend URL, noting that anyone who receives the link can, in principle, overwrite its contents).
6. **Should HomeWorld be preserved as part of participant identity** (fixing R-7), given VenueOS's own established identity model elsewhere (`GuestIdentity(Name, HomeWorld)`, `NEW_MODULE_GUIDE.md` §28) actually already disagrees with the donor's Name-only, `@world`-stripping behavior? This is a real product tension: matching the donor's wire contract (which has no HomeWorld field at all in its ticket/participant shapes) vs. matching VenueOS's own established per-guest identity convention used by every other module.

---

## 25. Recommended Reconstruction Strategy

**(Proposed only — not implemented, per this audit's constraints.)**

**MUST PRESERVE FROM DONOR:**
- Server-side, CSPRNG-based (`crypto.randomInt`) winner selection — never move randomization to the browser or the plugin.
- The ticket-count-as-weight model (paid + free tickets = repeated entries), not a separate "weight" number.
- The wire contract already implemented and tested in `VenueRaffleClient.cs` (`POST /api/raffles`, `GET /api/raffles/:id?token=`, camelCase, ticket flattening) — confirmed unchanged at donor HEAD (§21).
- The plugin-side pot/prize/house-take computation as a local-only, non-authoritative convenience (the backend has never needed it and should not suddenly be asked to compute it).
- Unmasked, plainly-visible/copyable backend URL and link fields — this already matches VenueOS's stated product requirement and needs no adaptation, only implementation.

**MUST FIX (in a VenueOS-side reconstruction, since the donor backend itself is out of scope to modify per this audit):**
- Build the missing connection-URL settings UI (V-1) — nothing else can work without it.
- Build the ticket-entry/adjustment UI (V-2), including a duplicate-name merge decision consistent with the donor's normalize-and-merge behavior or a deliberate, documented departure from it (§24 item 6).
- Decide and implement a stance on repeated-spin/redraw (R-1) and winner history (§24 items 1–2) before wiring up any live-spin feature, if one is built.
- If a future Raffle service ever routes a raw `HostUrl`/`ViewerUrl` into a diagnostic message, do so through an explicit redaction step rather than relying on `DiagnosticsService.Redact`'s existing markers, which do not match a bare path-segment token (§15/§23).

**VENUEOS ADAPTATIONS:**
- Give `VenueRaffleService`/`VenueRaffleModule` their own file/folder under `VenueOS.Modules.Operations/Raffle/` and their own `RaffleOperatorPanel.cs` file, per `NEW_MODULE_GUIDE.md` §21's explicit guidance that `NativeOperationsPanels.cs`/`Operations.cs` are legacy groupings and a module getting real functional work should move out, matching the precedent already followed by Party Finder/Trivia/Tournament/Bingo. (Note: a comment at `Operations.cs:697-699,701-705` currently lists "Raffle" alongside those modules as having "already set" the own-file precedent — this is inaccurate today per §21 of the guide itself, since Raffle's service/panel are still in the shared files; worth correcting whenever this move happens.)
- Separate `DrawSettings()` (backend URL, defaults) from `Draw()` (live roster/ticket operations), per `NEW_MODULE_GUIDE.md` §8–§9 — Party Finder is the reference example to follow, and Raffle is well-suited to this split since its settings (URL) and operational data (roster) are already cleanly distinct in the data model.
- Keep venue-scoped connection settings exactly as they are today (already correct, §16).
- Decide deliberately (§24 item 4) whether to build a WebSocket client at all before investing further; if not, the reconstruction should focus on making roster management + link generation + winner fetch fully usable, which alone would already close most of the parity gap in §18.

**OPTIONAL PRODUCT IMPROVEMENTS REQUIRING USER APPROVAL:**
- A "delete raffle" affordance (neither system has one today).
- A confirmation dialog on "Reset current raffle" (R-6) — a strict improvement over the donor with no compatibility downside.
- Winner-history tracking (a genuinely new feature, not present in the donor at all).
- Any client-side mitigation/warning for the backend's unauthenticated-upsert exposure (R-2/§24 item 5).

**DO NOT IMPLEMENT YET** — this entire section is a proposal for a future task, pending the product decisions in §24.

---

## 26. Recommended Live QA

Once a reconstruction exists, a live-in-game QA pass should specifically exercise:

1. Empty raffle → add one participant with 1 paid ticket → publish → spin → confirm the plugin's "Fetch winner" (once built) retrieves the correct name.
2. Multiple participants with mixed paid/free tickets, including one participant added via "Use target" against an actual in-game character — confirm the odds visualization (ticket-count-weighted wheel) matches the entered counts.
3. Two participants with the same first name but different HomeWorlds (if §24 item 6 is resolved in favor of preserving donor behavior) — confirm they are intentionally merged, with a visible UI acknowledgment, not a silent surprise.
4. Publish, then change tickets locally without re-publishing — confirm the operator is warned (or at minimum can tell) that the backend copy is stale, addressing the "forgot to regenerate links" risk noted in §7.
5. Two browser tabs open with the host link, both clicking Spin close together — confirm whatever redraw policy was decided in §24 item 1 actually holds (block, confirm, or accept-and-log).
6. Backend restart mid-session — confirm the plugin's next "Fetch winner" still returns the pre-restart winner correctly (validates §7's persistence claim end-to-end, not just by code reading).
7. A deliberately corrupted local `raffles.json` (or backend `data/raffles.json`, if a test backend is available) — confirm VenueOS surfaces a Diagnostics recovery warning (`VenueProfileService.Recover`, §7/§21 of the guide) rather than silently losing data the way the donor does (R-4).
8. Venue switch mid-in-flight-request — already covered by an automated test (`Venue_switch_cancels_inflight_raffle_request`), but worth confirming once in a live client too, per the guide's own manual-QA expectations (§31).

---

## 27. Definition-of-Done Concerns

Per `NEW_MODULE_GUIDE.md` §42's Definition of Done and the module-lifecycle requirements in §22–§26, a functional Raffle reconstruction is not done until: it has a Settings/operational split (§9); every credential/URL field is unmasked and per-venue (§11, §12, confirmed the model already supports this); failures route through `DiagnosticsService.RecordFailure` with no un-redacted secret ever surfacing (§15/§23's redaction-marker gap must be addressed, not assumed covered); the module's own file/folder convention is followed (§21 of the guide); and — specific to this module — a deliberate, documented decision exists for each item in §24 before the corresponding feature is built, so that "redraw," "winner history," and "live spin" are product choices made on purpose rather than emergent from whichever order features happen to get implemented in.

---

## 28. Files Inspected / Audit Method

| System | Path | Role | Modified? |
|---|---|---|---|
| Donor Dalamud plugin | `C:\FFXIVplugs\ffxivraffle4all\FFXIVRaffle4All\*.cs` (all 8 source files, full read) | Host UI, local raffle/settings/participant state, XLSX import/export, HTTP client | No |
| Donor backend | `C:\FFXIVplugs\ffxivraffle4all\backend\server.js`, `package.json` (full read) | Express/`ws` relay, in-memory + flat-file persistence, randomization | No |
| Donor browser client | `C:\FFXIVplugs\ffxivraffle4all\backend\public\*` (`host.html`, `view.html`, `wheel.js`, `styles.css`, all full read) | Wheel visualization, spin trigger | No |
| Donor manifest/docs | `FFXIVRaffle4All.json`, `README.me` | Version/Dalamud API level, deployment hint | No |
| Donor git history | `git log`/`git diff 111b3a9 HEAD` in the donor repo | HEAD verification, regression check | No (read-only `git` commands only) |
| VenueOS Raffle scaffold | `VenueRaffleClient.cs`, relevant sections of `Operations.cs`, `NativeOperationsPanels.cs`, `Plugin.cs`, `VenueOperationsDashboard.cs` (all grepped/read in full for Raffle-relevant lines) | Current reconstruction scaffold, audited as scaffold, not spec | No |
| VenueOS tests | `RaffleClientTests.cs` (full read), Raffle-relevant lines of `ModuleDisplayOrderTests.cs`/`UnfinishedModuleDefaultsTests.cs` | Existing regression coverage | No |
| VenueOS guidance docs | `NEW_MODULE_GUIDE.md` (§1–§30 read in full or targeted), `VENUE_RAFFLE_3A.md` (full read) | Architectural conventions, prior-phase history | No |
| VenueOS shared infrastructure | `VenueProfileService.cs` (full read) | Per-venue config persistence mechanism | No |

No GitHub fetch, no web search, and no execution of any donor or VenueOS code was performed — this audit is entirely static-source and `git`-history based. Every quantitative claim (line counts, commit hashes, diff contents) was produced by direct `git`/`wc`/`find`/`grep` commands against the actual repositories at audit time, not inferred from filenames or prior documentation alone. Prior VenueOS documents (`VENUE_RAFFLE_3A.md`) were treated as a prior snapshot to verify against current source, not as ground truth — and were confirmed still accurate by the `111b3a9`→HEAD diff in §21.

---

## 29. Confirmation of Read-Only Compliance

- No file in `C:\FFXIVplugs\ffxivraffle4all` was created, edited, or deleted during this audit.
- No file in `C:\FFXIVplugs\venueos` was created, edited, or deleted during this audit **except this report itself**, at `docs/RAFFLE_FORENSIC_AUDIT.md`.
- No `git add`, `git commit`, `git push`, `git tag`, or release action was taken in either repository.
- No branch was created in either repository.
- The only shell commands executed were read-only: `ls`/`find`/`wc`/`grep`, and `git status`/`git log`/`git diff` (no mutating `git` command).
- The pre-existing uncommitted Tournament/Brackets changes in the VenueOS working tree (noted in §2) were not modified, staged, or otherwise touched by this audit.
