# VenueOS phased migration plan

## Phase 2.0 — foundations

1. Create the clean SDK-15/net10 VenueOS solution and composition root.
2. Implement module registry/lifecycle, venue registry and module-keyed JSON store with unit tests for create, rename, duplicate, delete and atomic switch/rollback.
3. Build UI shell, semantic theme tokens/components and active-venue selector; prove instant per-venue theme swap.
4. Implement shared game context, chat queue, scheduler, logging/toast and one presence service. Use fake object snapshots/clock in tests.

Acceptance: changing venue changes every test module's settings and theme with no shared mutable payload. No source project changed.

## Phase 2.1 — Attendance, Greeter, VIP

Adapt `GuestIdentity`, filtering semantics and SQLite-history concepts from VenueStatusAndGreet; do not transplant its monolithic window/config. Split module DTOs. Adapt `GreeterService` queue/preset/tell sanitation and preserve active preset, five immediately selectable preset slots, delayed processing and greeted state.

Add VIP as a new module with normalized name/home-world key, enabled flag, standard venue tell template, shout/yell public message and notes. Integration tests must prove the exact desired order: arrival -> VIP tell -> normal active Greeter preset -> VIP public message -> Greeter owns greeted mark; a duplicate presence event must not repeat it.

## Phase 2.2 — Announcements

Adapt ShoutRunner action/preset/scheduling logic into a venue-scoped Announcements module. Start with scheduled chat messages and cancellation/rate limits. Add teleport/world/DC travel later behind an optional unsafe automation interface after SDK 15 validation. Preserve repeat interval and action delay behavior.

## Phase 2.3 — Party Finder

First update Party Finder's SDK/build environment and make its standalone build green. Adapt preset models/UI concepts. Rewrite `PartyFinderAutomation` against current game addon behavior behind tests/manual validation; never silently invoke it on a version mismatch. Validate create, refresh warning, listing-ended detection and abort paths in-game before release.

## Phase 2.4 — backend-compatible games

In this order: VenueRaffle4All, Mair's Trivia, TournamentControl, VenueBingo4All. For each, port only one vertical slice at a time: venue-scoped settings -> typed protocol client -> contract fixtures -> minimal UI -> remaining standalone operations. Maintain standalone plugins and backends as their own release line.

Compatibility checkpoint for every module: use the same base URL/config values; compare serialized requests with standalone output; test authentication/error/timeout/cancellation; deserialize real or recorded current-server responses; test venue switch cancels old requests and never sends old credentials to the new venue. For Tournament specifically test 409 revision recovery and WS reconnect/refetch; Trivia protocol version/auth headers; Bingo room/admin keys and polling; Raffle tokenized host/view links.

## Phase 2.5 — hardening and release

Run unit tests for store/theme/presence/scheduler/protocol serializers, module lifecycle tests with fake services, and manual Dalamud integration tests. Validate migration/import only if deliberately offered; do not overwrite standalone configs. Address VenueStatusAndGreet's SQLite dependency advisory before inheriting its dependency. Do not start this phase automatically from the audit.
