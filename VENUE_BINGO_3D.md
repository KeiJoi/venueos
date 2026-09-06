# VenueBingo4All — Phase 3D

`games.bingo` is a safe, backend-compatible client for the existing FFXIVBingo4All backend, pinned to standalone commit `015d5d6` (plugin assembly `1.0.0.7`) on 2026-09-04.

It preserves host sync, number calls, room-state polling, room/admin room listing and close operations, room-key/admin-key separation, card/player state, game/display/skin fields, and browser URL settings. `POST /api/call-number` intentionally has no key header, matching the backend.

Polling is cancellable and retargeted on venue switches. Keys have redacted diagnostic copies. Automatic trade/payout automation is explicitly excluded; see `BINGO_PAYOUT_AUTOMATION_DEFERRED.md`.
