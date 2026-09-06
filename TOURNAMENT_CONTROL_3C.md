# TournamentControl — Phase 3C

`games.tournament` is a compatible VenueOS controller client; it does not move or replace TournamentControl's Express, SQLite, or WebSocket backend.

## Compatibility pin

Audited against standalone commit `c29e984`, Dalamud plugin version `0.1.6`, on 2026-09-03. The wire contract remains the Phase 1 contract: bearer controller sessions, REST controller routes, `expectedRevision` mutation bodies, and `/ws` version-1 authenticate/subscribe frames.

## Safety behavior

- Each mutation sends its authoritative `expectedRevision`.
- A 409 is never retried. VenueOS refetches the selected tournament state once, records a deliberate-retry notice, and leaves the operator to choose the next mutation.
- `/ws` event helpers detect revision gaps and unauthorized/error frames so callers refetch or clear their session rather than applying unsafe deltas. Socket URI and protocol-frame construction are isolated from UI.
- Venue changes cancel the old request context, stop queued tournament callouts, clear selected state, and load the destination venue's isolated credentials.
- Match callouts reuse the shared scheduler and `ChatCommandService`; they sanitize names/templates, use the standalone shout/yell semantics, and do not create a second chat scheduler.

## Validation

Tests cover revision serialization, 409 authoritative refetch/no retry, bearer authentication, token expiry, WebSocket gap/expiry detection, venue credential isolation, and chat-queue callouts. A live socket reconnect loop should be manually exercised against the pinned backend before production operation.
