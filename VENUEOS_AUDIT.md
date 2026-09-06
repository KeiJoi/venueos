# VenueOS Phase 1 repository audit

Audit date: 2026-09-03. This document describes the checked source, not a proposed rewrite. Source repositories were not edited.

## Compatibility summary

| Source | VenueOS module | SDK/API target | Current role | Migration assessment |
|---|---|---|---|---|
| `ffxivbingo4all` | VenueBingo4All | Dalamud SDK 15 | host-side Bingo, rolls, room/card state, payout helper, web links | High: retain protocol, replace monolithic UI/client incrementally |
| `ffxivraffle4all` | VenueRaffle4All | SDK 15 / net10 | local raffle storage/import/export plus browser presentation links | Medium: extract models, repository and backend client; replace window |
| `tournamentcontrol` | TournamentControl | SDK 15 / net10 | authenticated bracket operator and realtime service client | Medium: clean REST client and view boundary already exist |
| `mairstrivia` | Mair's Trivia | SDK 15 / net10 | authenticated trivia host, question sets, player web service | Medium: protocol/client is isolated; preserve name exactly |
| `venuepartyfinder` | Party Finder | resolves SDK 14.0.2 | creates/refreshes Party Finder listings through game UI automation | High: unsafe/API-sensitive automation needs a compatibility gate |
| `venuestatusandgreet` | Greeter + Attendance | SDK 15 | venue session tracking, SQLite analytics, exports, delayed tells/presets | Medium: best Phase-2 foundation for shared presence |
| `shoutrunner` | Announcements | SDK 14.0.2 / net10 | scheduled action macros, shouts, teleports and optional Lifestream travel | High: separate message scheduling from travel automation |

## Per-project findings

### FFXIVBingo4All

`FFXIVBingo4All.Plugin/Plugin.cs` is a large all-in-one plugin. It owns ImGui tabs, config, commands, chat-roll parsing, HTTP, room state polling, queued chat, link generation, and optional trade/payout automation. `Configuration.cs` stores server/client URLs, `AdminKey`, `RoomKey`, game/display settings and skin presets through `IDalamudPluginInterface.SavePluginConfig`.

Useful extraction targets: game-state/models, `HttpClient` protocol calls, card/link calculations, queued chat sender, and display skin value models. Do not port the trade/payout automation in the first compatible module: it uses ECommons, FFXIVClientStructs and addon callbacks, and should be independently safety-reviewed. It uses `IChatGui`, `ICommandManager`, `IObjectTable`, `ITargetManager`, `IPluginLog`, `UiBuilder`, ECommons automation and unsafe client structs.

### FFXIVRaffle4All

The plugin is comparatively separated: `RaffleManager`, `RaffleRepository`, XLSX importer/exporter, models, `BackendClient`, and `Windows/RaffleWindow`. Its single plugin configuration keeps an optional `BackendBaseUrl`. `BackendClient` uses web-default camel-case JSON but does not set a timeout or retry policy.

Reuse the models, local repository/import-export behavior and client contract; write the VenueOS view over those boundaries. Browser host/view tokens are secrets and must remain venue-scoped and excluded from ordinary diagnostics/logs.

### TournamentControl

This is a mature monorepo. `apps/dalamud/Services/TournamentApiClient.cs` owns REST calls; `TournamentEventClient.cs` owns RFC-6455 connection/authentication; `MatchCalloutService.cs` emits game chat; `Windows/MainWindow.cs` owns the view. Its server and TypeScript shared contracts are canonical for the service, but the C# module deliberately consumes JSON only. Configuration contains server endpoint and session/organizer state. The server is independently deployed Express/SQLite/WebSocket infrastructure.

Adapt the C# API client and callout service behind the VenueOS module facade. Preserve optimistic revision handling, access-token expiry behavior and refetch/reconcile behavior; do not put a backend in VenueOS.

### Mair's Trivia

The repository separates plugin, shared question-set library, WPF editor and Fastify/SQLite server. `plugin/Api/TriviaApiClient.cs` is a single REST transport (15-second timeout) and `ApiModels.cs` contains wire shapes; `QuestionSetLibrary.cs` manages local question-set data. Config and the main ImGui window sit in `plugin/Configuration.cs` and `Windows/MainWindow.cs`. The server protocol is explicitly versioned and documented.

Reuse/adapt the API transport, models and question-set library. Keep the module display name **Mair's Trivia**. Do not fold editor or server code into VenueOS.

### Party Finder

`PartyFinderAutomation.cs` is an unsafe state machine that opens, reads and clicks game addons through FFXIVClientStructs, ECommons tasking and game commands. It responds to chat warning/end messages and can refresh a listing. `PartyFinderPreset` captures game-native category/duty/objective/job-slot/filter fields, while `JobCatalog` reads game data. `Configuration.cs` stores presets and automatic refresh settings using ordinary plugin config.

The preset model and high-level create/refresh workflow are reusable. Rewrite the automation adapter behind a narrow `IPartyFinderAutomation` boundary after verifying every target addon and node interaction against the current game/Dalamud version. This is not a generic UI component and must be version-gated.

### VenueStatusAndGreet

This is the most direct source for Attendance and Greeter. `VenueTrackerService` scans `IObjectTable` once per second, filters by locked territory and optional radius/outdoor center, identifies guests as **character name + home world** (with current world fallback), and emits `FirstVisitTonightDetected`. It writes sessions, presence transitions and five-minute samples through `DatabaseService` (SQLite). `GreeterService` queues unique guests, respects presence, delay and two-second pacing, sends `/tell` command candidates, and raises `GreetingCompleted`; greeting tags/state live in the database/UI. `GreetPreset` supports message lines plus a command, with active preset ID persisted.

Split the UI and module settings, but make the scan service the single VenueOS presence publisher. Attendance owns history/session analytics. Greeter owns queue and greeted state. VIP subscribes to the same arrival event; it must not launch a second scan.

### ShoutRunner

`MacroRunner` executes configurable `MacroAction`s: shout, teleport, world visit and data-center visit. It has cancellation, repeat intervals, per-action delay, Framework-thread marshaling, condition checks, travel timeouts and an optional Lifestream IPC integration. `Configuration` stores actions/presets and a simplified repeating shout mode. `Ui/MainWindow.cs` edits/runs them.

For Announcements, retain presets, scheduler, cancellation, pacing and chat-message execution. Make world/DC travel and teleport optional, separately permissioned capabilities rather than a prerequisite for sending scheduled announcements. Move only UI-independent scheduling into a shared scheduler service.

## Cross-cutting duplication and dependencies

Every standalone plugin registers its own command/window, retains its own configuration object, runs its own lifecycle, logs and sends chat/game commands. VenueOS must own these once. HTTP clients occur in all four backend modules; create a typed-client factory with timeout, cancellation, redacted logging and no automatic retry for non-idempotent mutations. Framework/update callbacks occur in several projects; one core dispatcher should call registered module ticks.

`VenueStatusAndGreet` already has the required identity/presence semantics and should be the source for a central publisher. Backend modules must not share a generic, invented API abstraction: use typed per-protocol clients. Party Finder and parts of Bingo/ShoutRunner use unsafe game/UI automation and remain isolated adapters.

## Build and repository status

Build command: `dotnet build <project> --no-restore -v:minimal` on 2026-09-03.

| Project | Result |
|---|---|
| FFXIVBingo4All | success, 0 warnings/errors |
| FFXIVRaffle4All | success, 0 warnings/errors |
| TournamentControl Dalamud | success, 0 warnings/errors |
| Mair's Trivia plugin | success, 0 warnings/errors |
| VenuePartyFinder | failed before compilation: SDK 14.0.2 expects missing `...Hooks\\15.0.0.1` |
| VenueStatusAndGreet | success; NU1903 reports known high-severity `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 vulnerability |
| ShoutRunner | success, 0 warnings/errors |

SDK 15 targets are Bingo, Raffle, Tournament, Mair's Trivia and VenueStatusAndGreet. ShoutRunner is SDK 14.0.2. Party Finder's csproj is `Microsoft.NET.Sdk`, but its resolved build machinery is SDK 14.0.2 and is presently unavailable. Upgrade/repair Party Finder's SDK reference and update ShoutRunner to SDK 15 before their integration.

Pre-existing uncommitted material was found in Bingo (`_tmp_*` directories), TournamentControl (test build artifacts/releases), and ShoutRunner (`.tmp`). The audit did not alter those source trees. Builds may have refreshed normal `bin/obj` outputs; no source/configuration files were changed.
