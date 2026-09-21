# Bingo web page: called-ball daub correctness (0.3.8 maintenance, WP2 Subtask A)

Status: **IMPLEMENTED — awaiting live QA.** All changes are in the Bingo backend/web repository (`C:\FFXIVplugs\ffxivbingo4all`), uncommitted. VenueOS itself needed no code change for this finding. Full detail: `C:\FFXIVplugs\ffxivbingo4all\docs\CALLED_BALL_DAUB_HARDENING.md`.

## Symptom
On the player web page, clicking a number in **Called Numbers** behaved like a toggle: a second click un-daubed the matching cells, and clicking 8 could un-daub an unrelated 18. (Clicking cells on a card is a separate feature and is unchanged.)

## Root cause
1. **Toggle semantics.** `toggleDaubForNumber` computed one `shouldDaub` from "are all matches already daubed?" and applied it to all of them, so a repeat click un-daubed every match. Its selector `[data-number="8"]` also matched the called-ball button itself.
2. **Cross-number effect (not a substring bug — every comparison is exact).** The server handled daubs as an unserialised load-whole-room / mutate / save-whole-room round trip, so the N `daub_update` events one click emits on an N-card page lost updates; and the page reconciled every cell of a card from each ack's whole-card array. A stale/lost array therefore un-daubed an unrelated number whenever any other number was acked. Reproduced on the unfixed server (burst of 6 daubs → card 0 empty) and on the pre-fix page (harness cases I, I2).

## Architecture / data flow
Server-persisted: browser `daub_update` → per-room state in SQLite → `daub_state` ack → replayed in `init_state` on load/reconnect. Fixed at the server (authority) and the page.

## Exact repair (Bingo repo)
- `public/daub-logic.js` (new): strict integer parsing (1–75), exact `===` matching, ack-scoping rules.
- `public/app.js`: Called Ball click = idempotent **SET** of every exactly matching cell on **all** cards (never the ball button, FREE, or other numbers); acks that name a number only touch that number; manual card-cell toggle unchanged.
- `server.js`: per-room lock over every room read-modify-write path (socket `daub_update`/`call_bingo`, host-sync, call-number, v2 mutating routes); `daub_state` ack additively carries `number` and `daubed`.

## Tests
`node test/daub-client.test.js` **16/16** (real `app.js` in a Node `vm`, fake DOM/socket; matrix A–J), `daub-logic.test.js` 7 checks, `daub-concurrency.test.js` 5 checks against a real server + SQLite (fails on the unfixed server), and the existing `cardgen` and `v2-integration` suites still pass. The harness against the pre-fix `app.js` passes 11/16. The repo has no test runner script, so these are `node <file>` commands. VenueOS suite: 1231/1231, Debug/Release 0 warnings.

## Live QA
See the Bingo report: several cards, click called ball 8 (all 8s daub, 18/28/38… untouched), click again/rapidly (nothing un-daubs), manual 18 then ball 8 (18 stays), un-daub one 8 then ball 8 (restored), another ball, refresh (state persists), FREE/uncalled ball unaffected.

## Files changed by repository
Bingo: `M backend/public/app.js`, `M backend/public/index.html`, `M backend/server.js`, `?? backend/public/daub-logic.js`, `?? backend/test/{daub-logic,daub-client,daub-concurrency}.test.js`, `?? docs/CALLED_BALL_DAUB_HARDENING.md`. VenueOS: this report and the ledger only.

## Remaining uncertainty
No browser run yet; real CSS/Socket.IO verified only via the harness. The room lock is per-process (the service is a single instance). Whether the production "8 changed 18" report was the lost-update path or the toggle alone cannot be told from source; both are fixed.
