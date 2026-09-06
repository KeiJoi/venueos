# Mair's Trivia — Phase 3B

`games.trivia` is a separate VenueOS client for the existing Mair's Trivia backend. It does not include the standalone server, WPF question-set editor, or database.

## Compatibility pin

The client was reinspected against standalone commit `6ba4855` on 2026-09-03. The standalone `TriviaApiClient`, API models, `/v1` routes, and WebSocket handshake remain the source of truth. No backend-contract change from the Phase 1 audit was found.

## Preserved protocol behavior

- `/v1` JSON uses the standalone camel-case wire contract.
- Login/access validation use `X-Server-Access-Password`; protected host routes use bearer authentication; refresh uses the stored refresh token.
- WebSocket endpoint is `/v1/ws`; its initial host authentication frame uses `protocolVersion: 1`.
- Typed operations cover health, access validation, login, refresh, profile, game listing/retrieval/creation, adding a question set, and preview/skip/open/close/end controls.
- Request bodies are capped at the server's 1 MiB payload boundary. Errors are parsed from the `{ error: { code, message } }` DTO and never include credentials.

## Venue isolation and safety

Connection URL, credentials, refresh token, server-access password, defaults, scoring, and question-set selection are stored under the active venue's `games.trivia` configuration. A venue switch cancels the prior context and clears current game state before loading the next venue. Diagnostic copies remove all tokens/passwords.

The typed player projection intentionally has no correct-answer property. Host question-set models are only sent to host endpoints.

## Validation

Automated tests cover request serialization, headers, protocol version, error DTOs, cancellation, venue-switch isolation, and question-set validation. Build both VenueOS and the standalone Mair's Trivia plugin after changes. WebSocket endpoint behavior remains version-sensitive and should receive manual in-game/backend validation before operational use.
