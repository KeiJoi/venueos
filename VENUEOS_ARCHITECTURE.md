# VenueOS architecture proposal

## Solution boundary

Create the solution in this repository in Phase 2, not inside a source-plugin repository:

```text
VenueOS.sln
src/VenueOS.Plugin/             # composition root, IDalamudPlugin, command, UiBuilder
src/VenueOS.Core/               # module contracts, event dispatcher, result/error primitives
src/VenueOS.Venues/             # venue registry, profile store, switching transaction
src/VenueOS.Services/           # presence, chat, clock/scheduler, config, logging, notifications
src/VenueOS.UI/                 # shell, navigation, semantic components, theme application
src/VenueOS.Modules.* /         # one project/assembly boundary per module when useful
tests/VenueOS.*.Tests/
```

Keep API protocol clients adjacent to their module (for example `Modules.Bingo/Protocol`), rather than in a universal backend library. This preserves independently versioned contracts.

## Modules and lifecycle

Use stable IDs, not display names: `core.attendance`, `core.greeter`, `core.vip`, `communication.announcements`, `promotion.partyfinder`, `games.bingo`, `games.raffle`, `games.trivia`, `games.tournament`. A module supplies immutable metadata and implements initialization, optional update, primary view, settings view, active-venue transition, and disposal. Core owns module registration, dependency ordering, failure isolation and navigation; it never gains typed properties for module settings.

```csharp
interface IVenueModule {
  ModuleDescriptor Descriptor { get; }
  Task InitializeAsync(ModuleContext context, CancellationToken ct);
  void Tick(DateTimeOffset now); // opt-in / inexpensive
  void Draw(); void DrawSettings();
  Task OnVenueChangedAsync(VenueContext next, CancellationToken ct);
  ValueTask DisposeAsync();
}
```

## Venue profiles and configuration

The global config stores only schema version, venue registry summaries and `ActiveVenueId`. Each profile uses an immutable GUID, display name and branding. Module configuration is keyed as `(venueId, moduleId, schemaVersion)` and stored as independently serialized JSON payloads; a module owns its DTO, defaults, validation and migration. Secrets (API keys/tokens) are also venue-module scoped and should be redacted from exported diagnostics. No enormous global settings DTO.

Venue switch transaction: validate target -> flush current module stores -> change active ID durably -> load/validate target payloads -> construct `VenueContext` -> notify modules in dependency order -> apply theme -> refresh shell. On failure, retain/restore the previous active context and display an error. Create/rename/duplicate/delete are registry operations; duplicate deep-copies module payloads and assets to a new venue ID, while delete refuses the last profile and confirms data removal.

## Shared services

`PresenceService` has exactly one `IObjectTable` scan per interval and emits enter/leave/snapshot events with `GuestIdentity(Name, HomeWorld)`, territory, position and object ID. It supports territory/radius policy supplied by the active venue. Attendance consumes transitions/history; Greeter consumes arrivals and controls greeted state; VIP consumes arrivals and matches its local records. No module scans characters itself.

`ChatCommandService` queues/rate-limits game commands and reports success/failure; `SchedulerService` owns cancellable timed work; `GameContextService` centralizes ClientState/territory/conditions; `VenueStore` owns persistence; `HttpClientFactory` constructs typed clients; logging, toasts and command registration are core services. Unsafe automation stays in explicit module adapters, never in shared services.

For VIP arrival, use an orchestrator subscribed to one arrival event: match enabled VIP by normalized name + home world, enqueue standard venue VIP tell, ask Greeter to queue its normal active-preset workflow, enqueue the VIP public shout/yell after Greeter completes, and leave the greeted tag entirely to Greeter. Make the event idempotent per session/guest so rescan/re-entry does not duplicate actions. VIP data is per venue and contains requested identity/enabled/template/channel/notes fields only.

## UI and themes

The shell contains a persistent active-venue selector, venue-identifying header/branding, module navigation, content area, notification surface and settings. Semantic `VenueTheme` tokens cover requested colors, spacing/density and rounding plus optional logo/background/opacity. Modules consume `VenueUi.Button`, `Card`, `StatusBadge`, `NavItem` and typography helpers; they never embed brand colors. Built-in themes are defaults and each venue overlays its own values.

The Aetherphone reference informed the direction—separate app/shell components, durable navigation, independently scrollable content, reusable widgets and asset-aware backgrounds—but its AGPL code and visual identity must not be copied. VenueOS should use its own desktop-operations design.

## Backend strategy and migration seams

Each backend module holds a small typed client with contract tests based on its standalone client. Its per-venue settings include URL and credentials. Venue change disposes/recreates or retargets a client only after in-flight work is cancelled. Bingo/Raffle/Tournament/Trivia retain their existing services; no source/server migration occurs. Start with a compatible minimal operations view, then port additional standalone functionality behind the tested client.
