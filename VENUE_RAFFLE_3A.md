# VenueRaffle4All Phase 3A

The VenueOS client is compatible with standalone repository commit `111b3a9` (inspected 2026-09-03). No contract changes from the Phase 1 document were found.

`VenueRaffleClient` is a dedicated protocol layer under `VenueOS.Modules.Operations/Raffle`. It preserves standalone web-default camel-case JSON, base-URL normalization, `POST /api/raffles` ticket expansion, tokenized `GET /api/raffles/:id?token=`, response validation, and non-2xx error behavior. It adds a 15-second VenueOS HTTP timeout and cancellation; it does not retry mutations.

Host and viewer URLs are retained only in the venue-scoped raffle payload, never included in `LocalRaffle.WithoutSecrets()` diagnostics/dashboard data, and never logged by the client. The backend source was not copied, changed, proxied, or replaced. Browser WebSocket spin control remains out of scope because the standalone C# client does not implement it.

`games.raffle` owns venue-scoped connection data and local raffle records. Changing venue cancels the current client context before loading the destination payload. The dashboard projection deliberately exposes only raffle count, selected raffle name, and winner—not URLs or tokens.

Validated vertical slice: construct local raffle -> serialize/upsert to existing backend route -> retain returned host/view URLs privately -> fetch state using supplied token -> update winner state. XLSX import/export remains a later adaptation item; no old standalone window was copied.
