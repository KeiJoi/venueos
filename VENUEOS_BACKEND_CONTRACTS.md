# VenueOS backend compatibility contracts

These contracts are inferred from the checked standalone clients, backend source and local protocol docs. The standalone plugin remains authoritative when its server changes.

## VenueBingo4All

Default server/client base URL: `https://ffxivbingo4all.onrender.com`, each configurable separately. The C# client uses a five-second `HttpClient` timeout, camel-case `System.Text.Json`, and generally treats a non-2xx response as failure without retries. The server uses REST plus Socket.IO for browser clients; the C# plugin polls `GET /api/room-state` rather than participating in Socket.IO.

| Endpoint | Client/server contract |
|---|---|
| `POST /api/host-sync` | body includes `roomCode`, `roomKey`, called numbers, players/allowed cards, game/rule/progressive/payout and skin values. `roomKey` may be body, query or `x-room-key`; creates/updates room and returns normalized state. |
| `POST /api/call-number` | `{ roomCode, number }`; returns `{ ok, added, calledNumbers }`; current server does not require room key. |
| `GET /api/room-state?roomCode=` | returns room/card/player/daub/bingo/game/title/color state; 404 if no room. |
| `GET /api/rooms`, `POST /api/rooms/close` | room-key-authenticated room management. |
| `GET /api/admin/rooms`, `POST /api/admin/rooms/close`, `POST /api/links` | admin-key protected (`x-admin-key` or `key` query); short-link body includes seed/card/display/color/server settings. |

`/api/host-sync` validates `roomKey`; admin routes validate `x-admin-key`/query key. Socket.IO emits browser room updates such as `room_state` and `number_called`. Unknowns: the checked C# source has a broad monolithic surface, so any unlisted browser-only endpoints/events must remain browser-server concerns until exercised by the VenueOS module.

## VenueRaffle4All

The configurable `BackendBaseUrl` is optional; client accepts absolute URL or assumes HTTPS. C# uses web-default camel-case JSON, has cancellation but no explicit timeout/retry. The server is Express with `ws` on `/ws`; persistent raffle state is server-owned.

| Endpoint | Contract |
|---|---|
| `POST /api/raffles` | C# sends `{ raffleId, name, createdAt, settings, participants, tickets }`; tickets repeat participant names per paid + free ticket. Response: `{ raffleId, hostUrl, viewerUrl, winnerName }`. |
| `GET /api/raffles/:id?token=` | returns raffle state; a provided token must match host or viewer token. C# currently requires only ID/name/winner but server also returns tickets. |
| `GET /host/:id/:token`, `GET /view/:id/:token` | browser presentation pages. |
| WebSocket `/ws` | browser clients receive `updated` public state and `spin` events; inspect browser protocol before a VenueOS WS client is proposed. |

The create/upsert request is effectively authenticated by possession of the raffle ID and URL tokens, not a conventional API key. Treat host/view URLs as secrets. Unknown: no C# websocket participant exists, so the browser's complete spin-command protocol is out of scope for the current module.

## TournamentControl

Configurable HTTPS base URL. The Dalamud client has a ten-second HTTP timeout, web-default JSON, `Authorization: Bearer <opaque token>` for controller routes, and `ClientWebSocket` for `/ws`. All JSON is camelCase and timestamps are ISO-8601 UTC. Mutations require `expectedRevision`; a stale/unsafe mutation returns 409 and the module must refetch authoritative state.

Key controller routes: `POST /api/controller/sessions` and `/organizers` accept `{ serverAccessPassword, userKey }`; `GET/POST /api/controller/tournaments`; `GET .../:id/state`; contestant add/bulk/rename/delete; `PUT .../seeds`; `POST .../seeds/randomize`, `/start`, `/matches/:matchId/result`, and `/correction`. Mutation bodies include `expectedRevision`; result/correction include winner ID and correction includes `rollbackDownstream`.

WebSocket endpoint is `ws(s)://host/ws`: client authenticates first with `{ version: 1, type: "authenticate", data: { accessToken } }`, then uses documented subscription messages. Events contain type, tournament ID, revision, time and data; clients reconnect/refetch on gaps. Master routes/cookie/CSRF are browser-administration concerns and should not be added to VenueOS unless later explicitly needed.

## Mair's Trivia

All REST paths use `/v1` beneath configurable HTTPS base URI. `TriviaApiClient` has a 15-second timeout, camel-case JSON, no automatic retry, bearer token on host calls and `X-Server-Access-Password` for server-credential calls. Errors are `{ error: { code, message } }`; API payload maximum is 1 MiB.

Bootstrap: `GET /health`; `POST /access/validate`, `/auth/register`, `/auth/login` with server-access header; `POST /auth/refresh`; `GET /me` with bearer token. Host game routes cover create/list/read and question-set/preview/open/close/skip/end commands. Creating a game supplies venue/game name, a valid question set, `inOrder` or `shuffleOnce`, optional scoring, `questionTimeLimitSeconds` 0–20, and optional cumulative scoring. Player routes are `/player/join`, `/player/reconnect`, `/player/answer`.

WebSocket is `/v1/ws`; first frame has `protocolVersion: 1` plus host `accessToken` or player `reconnectToken`; no state arrives before `authenticated`. The checked Fastify server presently makes most command work REST-driven and rejects later generic frames with `use_http_commands`; player state updates arrive as `player.state`. Never expose correct-answer metadata in player projections.

## Implementation rule

Create one typed protocol client per module, copy only the tested wire behavior, and pin each client to the standalone plugin's tested release/commit in module metadata. Do not create a shared VenueOS server, proxy, or replacement API. Compatibility tests should serialize representative requests and deserialize recorded server responses for every listed route.
