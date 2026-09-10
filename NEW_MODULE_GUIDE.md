# VenueOS New Module Guide

The authoritative, current-implementation guide for adding a new module to VenueOS. Every class, interface, method, and file path named below was verified against the actual repository at `C:\FFXIVplugs\venueos` as of the end of the Phase 3 shell/Settings/detached-window/Auto-Pop-Out work. Where something described in an earlier architecture document (`VENUEOS_ARCHITECTURE.md`, `ARCHITECTURE.md`) no longer matches the code, this guide follows the code and flags the discrepancy in §21 ("Known inconsistencies").

This document is written for a developer or an AI coding agent picking up a "build module X" task with no other context. Read it fully before writing code. If anything below turns out not to match the repository by the time you're reading it, trust the repository and treat this file as needing an update, not the other way around.

Donor/reference repositories — `venuepartyfinder`, `venuestatusandgreet`, `shoutrunner`, `ffxivraffle4all`, `mairstrivia`, `tournamentcontrol`, `ffxivbingo4all` — are **read-only**. They are the functional/protocol reference *when reconstructing a backend-compatible module from a donor*; never edit, format, or otherwise modify them, and never commit/push/tag/release inside them without explicit authorization for that specific task. **For a greenfield module with no donor** (e.g. Giveaways, Block Letters, Macro), this entire donor-authority concept simply does not apply — there is no donor behavior to preserve or "simplify away from," and the current VenueOS scaffold/architecture described in this guide *is* the specification.

**Normative language.** This guide uses **MUST**/**MUST NOT** for hard requirements — a module that violates one is not done, regardless of how well it otherwise works. **SHOULD**/**SHOULD NOT** are strong defaults with a real but narrow exception space — deviating requires a documented reason, not just convenience. **MAY** marks a genuine option. Plain descriptive sentences (no modal verb) are simply explaining how the current system works, not imposing a new rule. Where a rule is explicitly a VenueOS *product* decision rather than a general engineering principle (e.g. §9a's plain-text credential policy), it's called out as such — don't "fix" it toward a more conventional-looking pattern.

**No self-authorized deferrals.** If this guide, or the task that sent you to it, defines something as required, **you may not silently relabel it** "future enhancement," "Phase 2," "optional," "out of scope," "TODO," or "follow-up" merely because it's inconvenient to implement right now. If a genuine technical blocker prevents a required feature, **stop and report the blocker** — the user decides whether something is deferred, not the implementing agent. This applies to every MUST in this document.

**Proportionate, not ceremonial.** VenueOS modules range from a simple local utility to a backend-integrated multiplayer game. Do not make every subsystem in this guide mandatory for every module — a Block Letters-style utility does not need WebSockets, backend authentication, `GuestIdentity`, or an archive/delete/reset lifecycle unless its actual design calls for them. Sections below say "where applicable" or "if the module has one" for exactly this reason; read those qualifiers as real, not decorative. The goal is consistent engineering, not maximum surface area.

---

## 1. What a VenueOS module actually is

A module is not `business logic + Draw()`. A complete module is a small application integrated into the whole VenueOS environment:

```text
VenueOS Module
├── Identity            — stable ID, display name, description
├── Icon                — one procedural glyph, reused everywhere
├── Registration         — ModuleHost.Register in Plugin.cs
├── Home launcher        — an AppTile on Home, routed generically
├── Operational app      — the live Draw() screen
├── Settings contribution — DrawSettings(), reached via Settings → Modules
├── Active VenueContext  — reads venue identity/config from VenueProfileService
├── Per-venue config     — GetModuleConfig/SaveModuleConfig, keyed by venue
├── Theme integration    — semantic tokens only, no hard-coded colors
├── Embedded rendering   — via AppFrame inside the main tablet
├── Detached rendering   — via ModuleWindowManager, same Draw() content
├── Diagnostics          — failures attributable to the module, secrets redacted
├── Lifecycle            — Init / Tick / VenueChanged / Dispose
└── Tests                — whatever part of it is pure logic, outside ImGui
```

A module whose core logic works but skips several of these is not finished — it's a business-logic class with no home in the product.

## 2. High-level workflow for a new module

1. Define the module's purpose and its live operational workflow (what stays visible, what's one-click, what's setup-once).
2. Pick a stable, namespaced module ID (§3).
3. Pick a display name, description, and icon key (§4, §5).
4. If the icon doesn't exist yet, add it to `AppIcons` (§5).
5. Design the per-venue config DTO with a schema version (§11, §13).
6. Write the service class (business logic) and, if backend-backed, a typed client (§16–§20).
7. Write the operator panel (the `Draw()`/`DrawSettings()` content), using the UI kit (§14, §15).
8. Wrap it in an `IVenueModule` implementation and register it in `Plugin.cs` (§6).
9. Nothing else is required for Home, embedded rendering, detached rendering, or Auto Pop-Out — they are generic (§6–§9).
10. Add diagnostics reporting for failures (§25).
11. Add automated tests for whatever is pure logic (§30).
12. Build, then do the manual in-game QA pass (§31, §32).
13. Walk the Definition of Done (§33) before calling it complete.

## 3. Stable internal Module ID

Every module has a `ModuleDescriptor.Id` — a short, dotted, namespaced string, e.g. `core.attendance`, `core.greeter`, `core.vip`, `communication.announcements`, `promotion.partyfinder`, `games.raffle`, `games.trivia`, `games.tournament`, `games.bingo`.

- It is **not** the display name. `communication.announcements`'s display name is "ShoutRunner"; the ID never changed when the display name did (see §4).
- It is a **persistence key**: `VenueProfileService.GetModuleConfig<T>`/`SaveModuleConfig<T>` key stored payloads by `VenueModuleConfigKey(VenueId, ModuleId, SchemaVersion)` (`src/VenueOS.Venues/VenueModels.cs`). Renaming the ID orphans every venue's existing saved config for that module.
- It also participates in dependency ordering: `ModuleDescriptor.Dependencies` (e.g. `core.greeter` depends on `["core.attendance"]`, `core.vip` depends on `["core.greeter"]`) is resolved by `ModuleHost`'s topological sort in `src/VenueOS.Core/Modules.cs`.
- Pick the ID once, before writing any config-handling code, and don't change it later without a deliberate migration.

## 4. Display name

The display name (`ModuleDescriptor.DisplayName`) is read from exactly one place — `module.Descriptor.DisplayName` — and shown consistently in: the Home app tile (`AppTile.Draw` in `HomeScreen.DrawGrid`), the embedded app header (`AppFrame.DrawModule`), Settings → Modules' table and Configure breadcrumb (`ModulesSettingsPage`), and the detached window header (`ModuleWindowHeader.Draw`, driven by `ModuleWindowManager`). There is exactly one string to change if a display name needs correcting, and it never requires touching the module ID, config keys, or (for backend modules) protocol identifiers — this was proven in practice when `communication.announcements`/`games.raffle`/`games.bingo` were renamed to ShoutRunner/Raffle/Bingo without touching their IDs, config, or backend clients.

Current display names, exactly as they must appear everywhere: **Attendance, Greeter, VIP, ShoutRunner, Party Finder, Raffle, Mair's Trivia, Mair's Editor, TournamentControl, Bingo**. `Mair's Trivia` and `Mair's Editor` in particular must never be simplified or renamed — the apostrophe is deliberate in both.

## 5. Icon — hard requirement

Icons are procedural vector glyphs, not image assets and not Unicode emoji. They live in one place: `src/VenueOS.Plugin/Shell/Icons.cs`, `internal static class AppIcons`, with a single entry point:

```csharp
AppIcons.Draw(string iconKey, Vector2 center, float radius, uint color, float thickness = 2f)
```

It switches on `iconKey` (the same string stored in `ModuleDescriptor.Icon`) and calls a private `DrawXxx(ImDrawListPtr, center, radius, color, thickness)` method that draws a handful of `ImDrawList` primitives (`AddCircle`, `AddLine`, `AddRect`, `AddTriangleFilled`, etc.) — always in the caller-supplied `color` (a theme token converted via `UiKit.ColorU32`), never a fixed color. Currently registered keys: `users, message, star, megaphone, search, ticket, circle-question, trophy, grid, gear, home, close, popout, palette, terminal`. An unregistered key falls back to a plain circle outline rather than failing.

**To add a new icon:** add a `case "your-key": DrawYourIcon(drawList, center, radius, color, thickness); break;` to the switch, and a small private method following the existing style (a few primitives sized as fractions of `radius`, e.g. `r * 0.55f`). Pick a key that isn't already used by another module — a quick grep of `Icons.cs`'s `case` list is enough to check.

The icon key is set once, on `ModuleDescriptor.Icon`, and is automatically reused by: the Home tile, the Settings → Modules table row, the embedded `AppFrame` header, and the detached `ModuleWindowHeader` — there is no separate icon registration step for any of those surfaces.

## 6. Home launcher integration

`HomeScreen.DrawGrid` (`src/VenueOS.Plugin/Shell/HomeScreen.cs`) iterates `modules.Modules` (from `ModuleHost`) and draws one `AppTile` per module, passing `module.Descriptor.Icon`, `.DisplayName`, and `module.IsEnabled`. **This is fully generic** — a newly `modules.Register(...)`'d module appears on Home automatically, with no Home-specific code to write. `AppTile.Draw`'s own `enabled` gate means a disabled module's tile cannot register a click at all.

Clicking a tile calls `HomeScreen.LaunchModule`, which is the single routing decision point for every module (§8) — again, no per-module code required.

## 7. Global "Auto Pop-Out Modules" preference — hard requirement

Settings → General → **"Open modules in separate windows"**, default **off**. Backed by `src/VenueOS.Services/GlobalSettingsService.cs`:

```csharp
public sealed record GlobalSettings(bool AutoPopOutModules = false);
public interface IGlobalSettingsStore { GlobalSettings Read(); void Write(GlobalSettings settings); }
public sealed class GlobalSettingsService(IGlobalSettingsStore store) { ... }
```

Persisted by `DalamudGlobalSettingsStore` (`Plugin.cs`) through a **separate `Global` property** on `VenueOsPluginConfiguration` (the same Dalamud plugin-config object venue data uses, via its own `State` property) — a global preference and per-venue data structurally cannot collide. It is never venue-scoped and never resets on a venue switch, because `GlobalSettingsService` has zero reference to `VenueProfileService` or any venue type.

Routing (`HomeScreen.LaunchModule`):

```csharp
private static void LaunchModule(IVenueModule module, VenueShell shell, GlobalSettingsService globalSettings, ModuleWindowManager windowManager)
{
    switch (ModuleLaunchRouting.Resolve(globalSettings.AutoPopOutModules))
    {
        case ModuleLaunchTarget.Detached: windowManager.Open(module.Descriptor.Id); break;
        default: shell.SelectModule(module.Descriptor.Id); break;
    }
}
```

`ModuleLaunchRouting.Resolve(bool) : ModuleLaunchTarget` (also in `GlobalSettingsService.cs`) is a pure function — the single decision point every module's Home launch funnels through. **A new module needs zero code to support this.** There is no `if module == X` branching anywhere, and there must never be. `windowManager.Open(id)` is already open-or-focus (see §18), so a second click on an already-detached module focuses it instead of duplicating it.

Settings itself is a separate tile/code path in `HomeScreen.DrawGrid` (outside the `modules.Modules` loop) and is completely unaffected by this preference — it always opens embedded.

There is currently **no `CanDetach` capability flag** on `ModuleDescriptor` or `IVenueModule`. All nine current modules detach uniformly, so one hasn't been needed. If a future module genuinely cannot support detached rendering, that is the natural place to add such a flag (checked in `HomeScreen.LaunchModule` and in the embedded `AppFrame.DrawModule`'s pop-out button) — don't invent it speculatively before a module needs it.

**Startup-open is a separate concern from Auto Pop-Out — hard requirement.** Auto Pop-Out only decides *where* a module goes when the user explicitly launches it (embedded vs. detached); it must never be read as "restore this on plugin load." **Module/plugin UI must not automatically open during plugin initialization unless the product explicitly defines an opt-in startup-open preference** (VenueOS currently defines no such preference). This was a live-verified bug: the main shell's own `open` field (`Plugin.cs`) defaulted to `true`, so the very first `UiBuilder.Draw` call after every plugin load/enable rendered the tablet immediately, with no explicit user action involved — fixed by defaulting it to `false` instead. The window (and `ModuleWindowManager`'s detached-window set, which is already a plain in-memory `HashSet` reset fresh on every plugin construction, never persisted) should open only from an explicit action: `/venueos`, a Dalamud "Open Main UI" hookup if one is ever wired, or a detached window's Settings gear via `RequestOpenAndFocusTablet`. Never open anything merely because construction succeeded.

## 8. Universal Settings — module contribution

Settings → Modules (`src/VenueOS.Plugin/Shell/ModulesSettingsPage.cs`) lists every registered module generically: icon, display name, description, a Ready/Disabled `StatusBadge` (from `module.IsEnabled`), an enable/disable `Toggle`, and a "Configure" button. Configure sets `configuringModuleId` and switches the page into a master-detail view that calls:

```csharp
UiKit.SafeDraw(theme, diagnostics, module.Descriptor.Id, module.DrawSettings);
```

**`ModulesSettingsPage` has zero field-level knowledge of any module's configuration.** A module contributes its Settings surface purely by implementing `IVenueModule.DrawSettings()` — there is no separate "Settings page class" to write or register anywhere. `SettingsScreen.FocusModuleConfiguration(moduleId)` (called by a detached window's Settings gear, §19) jumps straight to this same view via `ModulesSettingsPage.OpenConfigure(moduleId)`.

**Known legacy gap, MUST NOT be copied by a new module:** most existing modules' `DrawSettings()` just calls the same `draw` delegate as `Draw()` — they don't separate persistent configuration from live operation in their Settings view. This predates §9 being written down as a rule and is legacy debt, not precedent. **A new module's `DrawSettings()` MUST render only its persistent configuration** (server URL, credentials, defaults — §9), while `Draw()` renders live operational state and controls. `promotion.partyfinder` is the current reference example of doing this correctly (§21) — its `DrawSettings()` holds only Auto Refresh/Warning Message Override, with every recruitment-criteria field living in `Draw()`. If you find yourself about to write `DrawSettings() => Draw();`, stop and identify what in the module is actually persistent, rarely-changed configuration — nearly every module has *something* that qualifies (at minimum, whatever backend/credential fields it has). The only acceptable exception is a module that genuinely has zero persistent configuration to expose (rare) — document that reasoning inline rather than defaulting to the shortcut silently.

## 9. Settings vs. operational UI — the core rule

**Settings = persistent configuration. The module app = live operation.** Editable does not automatically mean "Settings."

| Belongs in Settings → Modules → *Module* | Belongs in the module's own `Draw()` |
|---|---|
| Server URL, API keys, room/admin keys, passwords | Current guest list, active session |
| Persistent defaults (default game name, scoring rules) | Active DJ preset / greeting queue |
| Rarely-changed technical behavior | VIP roster (add/edit/remove/enable) — see below |
| Appearance/branding preferences that are set-and-forget | Active Party Finder listing state |
| | Running ShoutRunner sequence |
| | Active raffle, active trivia game, tournament bracket state, current Bingo game |

If the operator changes it routinely *during* live venue operation, it's operational, not a setting, no matter how "configurable" it looks. The VIP roster is the clearest existing precedent: it lives entirely inside the VIP app (add/edit/remove/enable, search — see `VipOperatorPanel` in `NativeOperationsPanels.cs`), not in Settings, even though every field in it is "editable persistent data." Only VIP's rarely-changed general behavior (a recognition-message template, say) would belong in Settings.

## 9a. Credential fields — plain text, MUST NOT be masked

This is a deliberate **VenueOS product convention**, not a security oversight — do not "fix" it toward the more conventional masked-password pattern.

Persistent credential fields — API keys, access keys, room keys, admin keys, server passwords — **MUST** remain visible as **plain, selectable, copyable, pasteable text** in Settings (`Forms.TextField` with the default `password: false`), and **MUST NOT** use `ImGuiInputTextFlags.Password` or any other masking. `Forms.TextField` accepts a `password` parameter for exactly this flag; no current call site sets it `true`, and a new module shouldn't be the first. `RaffleOperatorPanel.cs` documents the rationale inline, and it applies universally: the operator must be able to read, copy, and hand the key to whoever deploys/administers the backend — masking a value the operator is expected to recover and communicate defeats its purpose. These fields **MUST** persist across reload/restart like any other setting (§13).

This visible-configuration-UI rule is entirely separate from **redacted diagnostic output** (§26): a credential MUST be readable in Settings and simultaneously MUST NEVER appear in Diagnostics, logs, exception messages, status text, telemetry, or copied debug information. Route anything that might embed a secret through `DiagnosticsService.Redact` (or sanitize it before it ever reaches `RecordFailure`) — see §26 for why a generic `token=`-style regex is not sufficient on its own.

Unless a future product decision explicitly changes this convention, treat "credentials are plain-text in Settings, redacted everywhere else" as fixed VenueOS UX, not an open design choice per module.

## 10. Active Venue Profile is authoritative

There is exactly one active venue, held by `VenueProfileService.Current : VenueProfile` (`src/VenueOS.Venues/VenueProfileService.cs`). A module gets venue identity from this, never from its own field:

```csharp
venues.Current.DisplayName   // the venue name — never a module-local "Venue Name" setting
venues.Current.Theme         // the active VenueTheme
venues.Current.Id            // the venue GUID, used as the config key
```

**Do not add a module-specific editable "Venue Name" field.** (Some current backend modules — `MairsTriviaSettings.VenueName`, `TournamentModuleSettings.VenueName`, `VenueBingoSettings.VenueName` — still have exactly this, sent to their respective backends as a display label. This predates the current architecture and is a known, documented inconsistency (§21), not a pattern to copy in a new module.)

## 11. Per-venue configuration

`VenueProfileService` owns generic per-venue config load/save:

```csharp
T GetModuleConfig<T>(Guid venueId, string moduleId, int schemaVersion, Func<T> createDefault);
void SaveModuleConfig<T>(Guid venueId, string moduleId, int schemaVersion, T config);
```

Payloads are stored as `Dictionary<string, ModulePayload>` keyed by `VenueModuleConfigKey(VenueId, ModuleId, SchemaVersion).ToString()` inside `VenueStoreSnapshot` (`VenueModels.cs`), persisted by `DalamudVenueStore` through Dalamud's plugin-config mechanism. A module never reads/writes this dictionary directly — always through the two methods above, always with its own `Descriptor.Id` and a schema version it owns.

Loading happens in `IVenueModule.OnVenueChangedAsync(VenueContext, CancellationToken)` — called by `ModuleHost` in dependency order whenever the active venue changes (including at startup). A module resets its in-memory state and reloads its config for the new `context.VenueId` here; see `GreeterModule.OnVenueChangedAsync` for the simplest example (`greeter.ResetForVenue(); greeter.Configure(profiles.GetModuleConfig(c.VenueId, Descriptor.Id, 1, GreeterSettings.Default));`).

## 12. Global vs. per-venue — how to decide

Default to **per-venue** whenever a setting could reasonably differ between venues (backend credentials, greeting text, game defaults, a venue's external display colors). Use **global** only for a preference about how the VenueOS *application itself* behaves, independent of which venue is active — the only current example is Auto Pop-Out Modules (§7). If you're unsure, per-venue is almost always the right default; a wrongly-global setting leaks behavior across venues the operator runs, which is a real, hard-to-diagnose bug.

## 12a. Venue-switch isolation checklist

Switching the active venue **MUST NOT** leak any of the following from the old venue into the new one:

- Settings / configuration values
- Active operation state (current game, current round, current session)
- Participants / roster / guest lists
- Backend credentials or tokens
- Timers, schedules, or in-flight countdowns
- Realtime/WebSocket subscriptions (a socket connected under the old venue's credentials MUST be stopped before the new venue's are used — §34b)
- Cached data (a previous fetch's response, a computed summary)
- Detached-window operational context (a detached window keeps rendering the *same* `Draw()`, which reads `venues.Current` fresh every call — §20 — so this is usually automatic, but verify for any module-local cache)

The reference pattern is `Load(Guid nextVenueId)` (see `VenueRaffleService.Load`, `TournamentControlService.Load`, `MairsTriviaService.Load`): stop realtime/cancel in-flight requests first, reset in-memory state, *then* load the new venue's config and credentials — never the reverse order, and never partially. Call this from `OnVenueChangedAsync` (§11, §23). Test this explicitly (§31, §32): create two venues with different configuration for the module and confirm switching between them shows no trace of the other.

## 13a. Persistence contract — UI fields are not storage

**If the operator can edit a value and would reasonably expect it to survive a reload, it MUST be persisted** — through `GetModuleConfig`/`SaveModuleConfig` (§11) for settings-shaped data, or the module's own durable store for bulk content (§42a). This covers, where applicable: module settings, backend URLs, credentials, presets, reusable messages/templates, participant collections intended to survive, archived local objects, and module-specific defaults.

**The service/config model is authoritative — an ImGui field is not storage.** A real bug class found during reconstruction: UI code directly mutates a list or settings object held in memory, the UI *looks* correct because it's reading the same in-memory object back, but no code path ever actually called `SaveModuleConfig`. **Whenever a UI action changes persistent data, it MUST go through a method that actually saves it** — follow the pattern in `VenueRaffleService.AddNote`/`ImportRaffle`-style methods (mutate the settings record, then call `Save()`/`profiles.SaveModuleConfig(...)` in the same method), never mutate `Settings` (or an equivalent field) directly from an operator panel.

**"It survived closing and reopening the panel" is not proof of persistence** — that only proves the in-memory object wasn't replaced. The only tests that actually prove persistence are ones that destroy and reconstruct the owning service/store from a serialized representation (§30), and the only live check that proves it is a full module disable/re-enable or plugin reload (§32) — not merely closing a window.

If a piece of state is intentionally ephemeral (current timer countdown, a transient "connecting..." flag), document that it's deliberately not persisted rather than leaving it ambiguous whether it's a bug.

## 13b. Local lifecycle: Archive vs. Delete vs. Reset

Where a module maintains reusable/local operational objects (a saved raffle, a saved preset, a saved game), these three words mean different things and **MUST NOT** be used interchangeably. `games.raffle`'s `VenueRaffleService` is the current reference implementation of all three together:

- **Archive** (`Archive(id)`/`Unarchive(id)`) — nondestructive, reversible, no confirmation needed, no network call. Hides the object from the normal active list while preserving every field.
- **Reset** (`Reset(id)`) — clears an object's *operational contents* (participants, winner, published state) without deleting the object itself. Confirmed, since it destroys in-progress data, but explicitly does **not** delete the containing record — say so in the confirmation text (§37).
- **Delete** (`DeleteAsync(id)`) — permanent local removal, always confirmed, states plainly that it cannot be undone. For a backend-published object, delete is best-effort cascading cleanup of the remote copy (log/report failure via Diagnostics, but never let a failed remote cleanup block the already-confirmed local deletion).

Not every module needs all three — a module with no reusable local objects (most simple utilities) doesn't need any of this concept. Where a module does have save/reuse semantics, decide upfront (§39) which of these three actually apply and name the operations accordingly rather than inventing a fourth meaning for "clear."

## 13. Config versioning, defaults, and recovery

Every module config type is a plain record with sensible defaults, e.g. `AttendanceSettings(uint? LockedTerritoryId = null, ...)`. Schema version is an `int` passed explicitly to `GetModuleConfig`/`SaveModuleConfig` (currently every module uses `1`). If a stored payload's schema version doesn't match, or the JSON is malformed/unsupported/deserializes to null, `VenueProfileService.Recover<T>` (private) logs a warning to `RecoveryWarnings` (surfaced via `DiagnosticSnapshot.RecoveryWarnings`, shown in Settings → Diagnostics) and falls back to `createDefault()` — **the stored payload itself is never discarded**, only the deserialization for this session is skipped. To evolve a schema, bump the version int; there is no automatic field-level migration mechanism, so an old payload under the old version number is treated as "recoverable" (falls back to defaults) rather than upgraded in place. If in-place migration is ever needed for a specific module, that module owns writing it (read the raw payload at the old version, transform, save at the new version) — there's no shared migration framework to plug into.

## 14. Operational app UI

A module's `Draw()` should: identify itself (handled automatically by `AppFrame`'s icon+name+description header when embedded — don't duplicate this inside `Draw()`), expose live controls prominently, use the UI kit (§15) instead of raw ImGui, and not assume a fixed window size. Modules do **not** need identical internal layouts — Greeter's five-slot hotbar, VIP's searchable list + editor, and a backend module's connection-card-plus-console are all legitimately different shapes using the same component language. See the operator panels in `src/VenueOS.Plugin/*.cs` for current examples of each style.

## 15. No visible stock ImGui — the UI kit

ImGui is the rendering engine; it must not be the visual identity. A module's user-facing controls should go through the existing component library rather than a raw `ImGui.Button`/`InputText`/`Checkbox`/`Combo` call. These actually exist today:

**`Shell/UiKit.cs`** — `BeginCard`/`EndCard`, `BeginSectionCard`/`EndSectionCard` (a titled card), `PrimaryButton`, `GhostButton`, `DangerButton`, `IconButton`, `StatusBadge` (a filled pill for a discrete state), `ConnectionBadge` (dot + label for an ambient connection state), `SectionHeader`, `Divider`, `Toggle` (styled checkbox), `ListRow` (a selectable row with an optional subtitle), `EmptyState`, `WarningState`, `ErrorState`, `LoadingState`, `NavRow` (icon-tile + title + subtitle sidebar entry), `InfoBanner` (tinted explanatory card), `StatCard` (icon + big value + label tile), `Tooltip`, `SafeDraw` (wraps a draw call in the same failure isolation `ModuleHost` gives lifecycle methods), `PushWindowTheme`/`PopWindowTheme`, `DrawVenueFrame`, `DrawChassisBrand`. Plus `ConfirmDialog` and `TextInputModal` — single-instance modal dialogs for a destructive confirmation and a "one field, one confirm" flow, respectively.

**`Shell/Forms.cs`** — `TextField`, `MultilineField`, `NumericField`, `ComboField`, `SearchBox` (all labeled, consistently styled, theme-driven; `ComboField`/`SearchBox` take an optional `width` parameter — don't rely on `ImGui.PushItemWidth` around them, it's silently overridden), `Segmented` (a mutually-exclusive picker, also used as VenueOS's tab strip), `FieldLabel`.

**`Shell/AppTile.cs`**, **`Shell/Hotbar.cs`** — the Home app-icon tile and a live-operation "pick one of a small fixed set" slot button (used by Greeter's DJ hotbar).

There is currently no dedicated `Table`/generic list-with-columns component, no generic `Modal` beyond the two single-purpose ones above, and no `HotbarButton` separate from `Hotbar.Slot`. If a module genuinely needs one of these and no existing component fits, **extend the shared UI kit with a new component**, following the existing style (a static method taking `VenueTheme theme` first, reading only `theme.Tokens`/`theme.Metrics`) — do not build an incompatible one-off.

**`ConfirmDialog`/`TextInputModal` popup identity is per-instance (0.3.0).** Each instance gets its own
GUID-derived popup identity at construction — `new ConfirmDialog()`/`new TextInputModal()` needs no arguments and
no changes for this. Before 0.3.0 the identity was a single class-wide constant shared by every instance in the
plugin; two instances both mid-request in the same frame rendered into the same ImGui popup (found and fixed twice
independently — Giveaways, then Mair's Editor — before being fixed at the class level). A new module using either
component gets this for free; there is nothing to do differently.

**`Shell/DialogHeader.cs` — chrome for a dedicated editor window or larger modal (0.3.0), hard requirement.** No
VenueOS-generated top-level `ImGui.Begin` window or `BeginPopupModal` may show ImGui's own native title bar — every
one must pass `ImGuiWindowFlags.NoTitleBar` and draw custom chrome instead (a live-confirmed defect this release:
Macro's Create/Edit window used `Forms`/`UiKit` correctly throughout yet still looked like a raw native ImGui
window, purely because its window chrome was never converted). Use `ModuleWindowHeader.Draw` for a detached
module's own window (icon + name + Settings gear + Close); use `DialogHeader.Draw(theme, title, onClose)` for
anything else with a title bar worth replacing — a dedicated editor window (`MacroEditorWindow`) or a larger modal
(`GiveawayPresetEditorModal`, `VipEditDialog`). For a small, single-purpose confirmation/text-entry dialog that
already draws its own themed title line as its first line of content (`ConfirmDialog`/`TextInputModal`), just add
`NoTitleBar` with no separate header component — a second header bar above an already-present themed title would
be a redundant double heading in the other direction; see `docs/UI_QUALITY_AUDIT.md` §12 for the exact reasoning.

**`BeginSectionCard`/`EndSectionCard` — never nest one inside another's content.** As of the Settings-rendering correction pass, `BeginSectionCard` auto-sizes to its own content by drawing its background *after* measuring that content, using `ImDrawList.ChannelsSplit`/`ChannelsMerge` on the current window's draw list. That splitter is a single, non-reentrant piece of state owned by the draw list — if a second `BeginSectionCard` call starts while a first one's content is still being drawn (on the same window/child), the two splits collide and can corrupt rendering for both (this is exactly what made `Settings → Modules → <module> → Configure` render as one large empty card: `ModulesSettingsPage` used to wrap a module's whole `DrawSettings()` in its own extra section card, so the module's *own* first section card opened a second, colliding split — removed for this reason). `BeginSectionCard` now degrades safely (header only, no background) if it ever detects it's being called while another one is still open, but treat that as a bug to fix, not a supported layout: call `BeginSectionCard`/`EndSectionCard` pairs **only in a flat, sequential list**, never nested, and never wrap an `IVenueModule.DrawSettings()` (or `Draw()`) call in an extra section card the way the operational path never wraps `module.Draw()` in one either — let the module's own content draw its own card(s) directly.

## 16. Theming

Every color comes from `VenueTheme.Tokens` (`ThemeTokens` record, `src/VenueOS.Venues/VenueModels.cs`): `Primary, Accent, Background, Surface, RaisedSurface, Border, TextPrimary, TextSecondary, Success, Warning, Error, Disabled, Selected`. Convert via `UiKit.Color(hex)` (→ `Vector4`) or `UiKit.ColorU32(hex, alpha)` (→ packed `uint` for draw-list calls). Spacing/rounding come from `ThemeMetrics` (`Rounding, Padding, Spacing, Density`). **Never write a literal hex color or `Vector4`/`uint` color constant in module code** — every existing component and operator panel reads exclusively from these two records, which is what makes a venue's theme change instantly everywhere.

## 17. Built-in themes

`BuiltInThemes.All` (`VenueModels.cs`): **Dark, Light, Neon, Midnight**. A new module's UI must be visually checked under all four (contrast, readability, disabled/selected states) — since everything routes through semantic tokens, correct usage is normally sufficient, but verify rather than assume, especially for anything drawn directly via `ImDrawList` rather than through the UI kit.

## 18. Embedded rendering

`AppFrame.DrawModule(theme, module, windowManager, diagnostics)` (`Shell/AppFrame.cs`) wraps `module.Draw()` for the main tablet: icon + display name + description header, a pop-out icon button (§19), a divider, then `module.Draw()` inside a scrollable child, wrapped in `UiKit.SafeDraw`. **The global toolbar (`TabletHeader`) never names the currently open module** — it is identical on Home, Settings, and every module (this was a deliberate later fix; do not reintroduce a breadcrumb). The module identifies itself through its own `AppFrame` header, not the global chrome.

## 19. Detached / pop-out support — hard requirement

Every normal operational module should support detaching unless there's a documented reason not to (§7 — no such module exists today). `ModuleWindowManager` (`Shell/ModuleWindowManager.cs`) owns which modules are detached (`HashSet<string> open`), exposes `Open(moduleId)` (open-or-focus — idempotent, never a duplicate) and `Close(moduleId)`, and `DrawAll(theme, modules)` renders every open one every frame — called from `Plugin.Draw()` **before** the main tablet's own `if (!open) return`, so detached windows keep working even while the tablet is closed.

**There is exactly one module content implementation.** `ModuleWindowManager.DrawWindow` calls the identical `module.Draw()` `AppFrame.DrawModule` calls, wrapped in the same `UiKit.SafeDraw`. Detaching/re-embedding is a pure UI choice with zero effect on module state.

Detached chrome comes from one shared component, `Shell/ModuleWindowHeader.cs`, used by every module:

```text
[Module Icon] [Module Name]                         [⚙] [X]
```

Left: icon + display name, always fully shown. Right: Settings gear + Close, reserved first at an absolute screen position (same "reserve the critical region before anything else is drawn" approach `TabletHeader` uses) so the module name's length can never push them off-window. The window itself opens with `ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse` — **no native ImGui title bar** — and the header's empty middle is the drag handle. **A detached header never shows Home, the global venue selector, or any other global control** — only the module's own icon/name and its own Settings/Close.

## 20. Detached Settings routing, Close semantics, drag/resize, multi-window, venue context

- **Settings gear:** `onSettings: () => { requestOpenAndFocusTablet(); shell.SelectSettings(); settingsScreen.FocusModuleConfiguration(module.Descriptor.Id); }` — opens/focuses the main tablet if closed (reusing the exact logic `/venueos` uses, `Plugin.RequestOpenAndFocusTablet`) and navigates to Settings → Modules → that module's Configure view. Never opens a second tablet.
- **Close:** sets a local `closeRequested` flag, removed from `ModuleWindowManager`'s open set after `ImGui.End()`. It does **not** touch `IVenueModule.IsEnabled`, module business state, config, the active venue, or the main tablet — the module can be re-detached (or re-embedded) immediately after.
- **Drag/resize:** the header's empty middle drives `ImGui.SetWindowPos` via `GetMouseDragDelta`/`ResetMouseDragDelta`, the same technique the main tablet uses. Resize borders are untouched (no `NoResize` flag) — resizing works with no title bar. Minimum size: `420×320` (`ImGui.SetNextWindowSizeConstraints`), default `560×480` on first appearance.
- **Position/size persistence:** relies on ImGui's own per-window-ID memory. The window's ImGui name is a pure stable ID (`###venueos-detached-{moduleId}`) with **no visible title text and no display-name/venue-name embedded in it**, so a rename or venue switch never resets remembered position/size. This persists for the running session; whether it survives a full plugin/game restart depends on Dalamud's own `imgui.ini` handling and has not been independently verified.
- **Multiple detached modules:** `ModuleWindowManager`'s open set is a plain `HashSet<string>` — any number of different modules can be open simultaneously with no interference; opening one never closes another.
- **Active venue context:** detached windows share the single `VenueProfileService.Current` — there is no per-window venue selector, and none should be added. A module's `Draw()` reads `venues.Current` fresh every call regardless of embedded/detached context, so switching venues updates every open window (main tablet, every detached module, Settings) simultaneously.
- **Theme:** `UiKit.PushWindowTheme`/`PopWindowTheme` wraps the whole detached window exactly like the main tablet — Dark/Light/Neon/Midnight apply identically.

## 20a. Character-session presentation gate (0.3.0, hard requirement — automatic, no module code needed)

No VenueOS-generated UI renders while genuinely logged out (title screen, character select) — enforced centrally
in `Plugin.Draw()` via `SessionPresentationGateService` (`src/VenueOS.Services/SessionPresentationGateService.cs`),
not per-module. A new module's `Draw()`/`DrawSettings()` **never needs its own logged-in check** — if the module
registers normally (§6, §23) and renders through the standard embedded/detached paths (§18–§20), it is already
covered: `AppFrame`/`ModuleWindowManager` are only ever invoked while the gate allows it.

The one exception is ShoutRunner's own operational UI, which additionally stays visible through a temporary
world/Data Center travel transition while it has an active run in progress (`ShoutRunnerService.IsActive`) — see
`docs/SESSION_PRESENTATION_GATE.md` for the full design and the proof that this can't leak UI at startup or
persist indefinitely after a genuine logout. This is a narrow, ShoutRunner-specific hook, not a general "any module
can request to stay visible while logged out" mechanism — a future module should not assume it can opt into an
equivalent exception without a similarly rigorous proof that its own "active operation" signal can only ever
become true while genuinely logged in and self-corrects within a bounded window otherwise.

This is a presentation-only gate: `InitializeAsync`/`OnVenueChangedAsync`/`Tick` are completely unaffected and run
exactly as documented elsewhere in this guide (§23) regardless of login state — a module may still load
configuration and maintain internal state while logged out; it simply doesn't draw anything until the gate allows
it.

## 21. Known current inconsistencies (documented, not fixed here)

Per the task's documentation-first instruction, these are noted rather than silently corrected:

- `VipRecord`, `TournamentModuleSettings.VenueName`, `VenueBingoSettings.VenueName` predate the "Active Venue Profile is authoritative" rule (§10) and still carry their own venue-name field sent to their backend. A future functional-reconstruction pass should supersede these with `venues.Current.DisplayName`. **`MairsTriviaSettings.VenueName` has already been removed** (Mair's Trivia/Editor Phase 2 reconstruction) — `MairsTriviaService` reads `profiles.Current.DisplayName` directly at request time instead; this is the reference example for the remaining two.
- Every current module's `DrawSettings()` currently mirrors `Draw()` exactly (§8) — none yet separates persistent config from live operation in its Settings contribution, **except `promotion.partyfinder`** (reconstructed — see `PARTY_FINDER_RECONSTRUCTION.md`), whose `DrawSettings()` holds only Auto Refresh/Warning Message Override while every recruitment-criteria field lives in `Draw()`. Use it as the reference example for a new module doing this correctly from the start.
- `src/VenueOS.Services/SharedServices.cs` defines `GameContextService` and `VenueHttpClientFactory`, but neither has any caller anywhere in the codebase today — they are unused scaffolding, not an active convention. Backend modules construct `HttpClient` directly (`new HttpClient { Timeout = ... }`) rather than through `VenueHttpClientFactory`.
- `NotificationService`/`VenueUi` (`src/VenueOS.UI/VenueUi.cs`) still exist and `NotificationService.Push` is still called on module failure/recovery, but nothing renders `NotificationService.Toasts` anywhere anymore (the raw toast-line UI was deliberately removed from the main window during shell cleanup) — pushing a toast today has no visible effect. Prefer `DiagnosticsService.RecordFailure` for anything that should actually be visible to the operator (see §25).
- Attendance, Greeter, VIP, ShoutRunner, and Raffle's operator-panel classes are all grouped in one file, `src/VenueOS.Plugin/NativeOperationsPanels.cs`, for historical reasons. Party Finder, Mair's Trivia, TournamentControl, and Bingo each have their own file/folder. **A new module MUST NOT be added to `NativeOperationsPanels.cs`** merely because older modules live there — it MUST get its own file (e.g. `src/VenueOS.Plugin/GuestNotesOperatorPanel.cs`) or, for a non-trivial module, its own folder (e.g. `src/VenueOS.Plugin/Raffle/`), matching the newer precedent.
- Similarly, the four core modules' service/wrapper classes (Attendance/Greeter/VIP/ShoutRunner) all live in one file, `src/VenueOS.Modules.Operations/Operations.cs`. Every backend module already gets its own subfolder (`Modules.Operations/Bingo/`, `Modules.Operations/Raffle/`, `Modules.Operations/Tournament/`). **A new module MUST NOT be added to `Operations.cs`** — it MUST use its own file/folder under `VenueOS.Modules.Operations/`, per the canonical structure in §33.

## 22. Enable/disable

`IVenueModule.IsEnabled` is a plain settable `bool`, toggled from Settings → Modules' `Toggle` (`ModulesSettingsPage.DrawModuleRow`). Disabling a module:

- Removes its Home tile's clickability (`AppTile`'s `enabled` gate) and dims it.
- Excludes it from `ModuleHost.Tick`/lifecycle notification loops (`ModuleHost.Ordered().Where(x => x.IsEnabled)`).
- Closes its detached window if one is open (`ModuleWindowManager.DrawAll` checks `module.IsEnabled` every frame and removes a disabled module from the open set).
- **Does not** touch its saved per-venue config — `GetModuleConfig`/`SaveModuleConfig` are keyed by module ID regardless of enabled state, so re-enabling restores exactly where it left off.

## 22a. `UnderDevelopment` and promotion

`ModuleDescriptor` has a real, currently-used `bool UnderDevelopment = false` field. A module built with it (`new ModuleDescriptor(..., UnderDevelopment: true, ...)`) automatically gets a generic "Under Development" `StatusBadge` in Settings → Modules and an `InfoBanner` warning in its Configure view — no extra code needed, the same way icon/display-name propagation is generic (§4–§6). `games.raffle` and `games.tournament` are the current live examples: both are `UnderDevelopment: true` with `IsEnabled { get; set; } = false` by default.

**A new module SHOULD start `UnderDevelopment: true` with `IsEnabled = false` by default** while it's being built, and stay that way through automated testing and Debug/Release builds — those do not promote it. **Promotion (flipping `UnderDevelopment` to `false` and/or `IsEnabled` to `true` by default) is an explicit product/release decision the user makes after live acceptance in Dalamud**, never something an implementing agent does on its own merely because its own tests passed (§21 — no self-authorized deferrals cuts both ways: don't defer required work, and don't self-promote unfinished work either).

## 23. Module lifecycle

`IVenueModule` (`src/VenueOS.Core/Modules.cs`), extends `IAsyncDisposable`:

```csharp
public interface IVenueModule : IAsyncDisposable
{
    ModuleDescriptor Descriptor { get; }
    bool IsEnabled { get; set; }
    Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken);
    Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken);
    void Tick(DateTimeOffset now);
    void Draw();
    void DrawSettings();
}
```

`ModuleHost.Register(module)` adds it (throws on duplicate ID). `InitializeAsync` runs once at plugin startup, in dependency order, isolated per-module (`ModuleHost.IsolateAsync` catches and reports via `ModuleFailed`, never lets one module's exception stop another's init). `OnVenueChangedAsync` runs whenever the active venue changes (including the very first "activation" at startup) — reset in-memory state and reload config here. `Tick(now)` runs every framework update for enabled modules only, exceptions caught and reported per-module. `Draw()`/`DrawSettings()` are called by `AppFrame`/`ModuleWindowManager`/`ModulesSettingsPage` respectively, always wrapped in `UiKit.SafeDraw` by the caller — a module's own `Draw()` does not need its own try/catch for UI exceptions specifically, though it should still guard against its own known failure modes (a disconnected backend, etc.) with proper empty/error states (§27).

## 24. Disposal

`DisposeAsync()` runs in reverse registration order on plugin unload (`ModuleHost.DisposeAsync`), isolated per-module the same way init is. A module must clean up everything it owns here (or in `OnVenueChangedAsync`/`ResetForVenue`-style methods when switching venues mid-session): event subscriptions to shared services (`PresenceService.Arrived`/`Departed`, `GreeterService.GreetingReadyToFinalize`, etc.), `CancellationTokenSource`s (cancel-and-dispose-and-recreate on every venue switch is the existing pattern — see `GreeterService.ResetForVenue`), scheduler jobs (`SchedulerService.Cancel(id)`), open HTTP/WebSocket state. No module's background work should continue after it's disabled or the plugin unloads, unless that work is genuinely owned by a shared service instead (e.g. `PresenceService`'s single object-table scan keeps running for whichever modules are still enabled — that's shared infrastructure, not per-module state).

## 24a. Stale async / stale realtime guard — hard requirement

Every module with asynchronous work **MUST** own an explicit `CancellationTokenSource` and know exactly when it gets cancelled-and-recreated: at minimum on venue change, module disable, and plugin disposal; also on operation cancellation or a selected-object/session change where the module has one (switching which raffle/game/tournament is "active" mid-session). Cancel-and-dispose-and-recreate on every relevant transition is the existing pattern (`GreeterService.ResetForVenue`, the realtime clients' `Load`/`Stop`).

**A stale callback MUST NEVER mutate current state.** A response that started under venue A must not land on venue B after a switch; a response for a previously-active raffle/game/session must not overwrite a newly-selected one, even if it arrives after the switch. Guard this either by cancelling the token before the switch (preferred — the awaited call throws `OperationCanceledException` and never reaches the mutation) or, if that's not possible, by checking "is this response still for the currently-selected context" immediately before mutating state. An expected cancellation from a normal venue switch or operation cancel is not a scary user-facing failure — don't route `OperationCanceledException` from an intentional cancel through `DiagnosticsService.RecordFailure` as if it were an error.

Avoid casual fire-and-forget `Task.Run`; if a task must run detached (the realtime clients' `RunAsync` is the current precedent), it still needs the token above and a `Stop()`/disposal path that actually waits for or cancels it. Never mutate ImGui/UI state directly from a background thread — a realtime client's receive loop drains into a thread-safe queue (`ConcurrentQueue`, §34b) that the module's `Tick()` (which runs on the framework/UI thread) consumes, rather than applying frames from the socket thread directly.

## 25. Diagnostics — hard requirement

`DiagnosticsService` (`src/VenueOS.Services/DiagnosticsService.cs`):

```csharp
public sealed record DiagnosticEntry(string Message, DateTimeOffset At);
public sealed record DiagnosticSnapshot(string VenueOsVersion, string ActiveVenueId, string ActiveVenueName, IReadOnlyList<string> EnabledModules, IReadOnlyDictionary<string,string> ProtocolPins, IReadOnlyList<DiagnosticEntry> RecentErrors, IReadOnlyList<string> RecoveryWarnings);
public sealed class DiagnosticsService(ModuleHost modules, VenueProfileService venues, IClock clock)
{
    public void RecordFailure(string message);   // redacts secrets, timestamps, keeps last 20
    public void Clear();
    public DiagnosticSnapshot Capture();
    public static string Redact(string message);  // strips token=/accessToken/refreshToken/AdminKey/RoomKey/password onward
}
```

`ModuleHost.ModuleFailed` already routes lifecycle failures (init/tick/venue-change/dispose exceptions) into `diagnostics.RecordFailure` automatically (wired once in `Plugin.cs`) — a module doesn't need to call this itself for lifecycle exceptions. For its own operational failures (a backend request failing, a malformed response), a module's service class should call `diagnostics.RecordFailure($"...: {ex.Message}")` (pass `diagnostics` in via constructor, matching how other services are wired) so the failure is attributable and shows up in Settings → Diagnostics (`DiagnosticsSettingsPage`, with filtering by level/module/search) and Home's compact "N recent error(s)" badge (`VenueOperationsDashboard`). **Never print raw exception text directly onto Home or into the operational screen as primary UI** — show a concise `UiKit.ErrorState`/`EmptyState`/`WarningState` there instead, with detail available in Diagnostics.

## 26. Logging and secrets

`DiagnosticsService.Redact` strips everything from a secret-looking marker (`token=`, `accessToken`, `refreshToken`, `AdminKey`, `RoomKey`, `password`, case-insensitive) onward, replacing it with `[redacted]`. It runs automatically on every `RecordFailure` call and on `RecoveryWarnings`. If a module's own failure messages might embed a credential under a different name than these markers, either route the message through `DiagnosticsService.Redact` explicitly or avoid interpolating raw secret values into failure messages in the first place — don't rely on a marker the redaction list doesn't already cover.

**A `key=value`-shaped marker list does not catch every secret shape.** A capability/access token embedded as a bare URL *path segment* (`https://host/room/{token}/join`, not `?token=...`) will not match any `key=value` marker — a real gap found during Raffle's audit. Before wiring a new backend client's failure messages into Diagnostics, check where its secrets actually live: query parameters, headers, *and* path segments are all fair game, and none of them are safe to assume a generic regex already covers. Backend clients **SHOULD** sanitize/strip a request URL themselves before ever handing it to an exception message or `RecordFailure`, rather than relying solely on the shared redactor to catch it downstream. The browser/display-client side of a backend module (if any) should also never receive an organizer/admin-level secret it doesn't need — a viewer-facing URL should carry only viewer-scoped credentials.

## 27. Shared services — check before writing your own

`src/VenueOS.Services/SharedServices.cs` and `DiagnosticsService.cs`:

- **`PresenceService`** — one `IObjectTable` scan per interval, emits `Arrived`/`Departed` events keyed by `GuestIdentity(Name, HomeWorld)` (normalized "NAME@WORLD" key), filtered by a `PresencePolicy` (territory/radius). Use this for anything needing guest presence — do not independently scan game objects.
- **`ChatCommandService`** — queues and rate-limits outgoing chat/game commands (`Enqueue(new ChatCommand(text, cancellationToken))`, ticked once per frame in `Plugin.Update`). Use this for anything sending `/tell`, `/shout`, `/yell`, etc. — do not build a competing queue.
- **`SchedulerService`** — cancellable, optionally-repeating timed work (`Schedule(delay, action, repeat, cancellationToken)`/`Cancel(id)`), ticked once per frame. Use this instead of a per-module `Timer`/`Task.Delay` loop.
- **`NotificationService`** — still exists and is still pushed to on failure, but has no renderer anymore (§21) — don't rely on it for anything the operator needs to actually see; use `DiagnosticsService` instead.
- **`GameContextService`, `VenueHttpClientFactory`** — defined but currently unused by anything (§21). Don't assume they're wired into the composition root; if you use them, you're the first caller.
- **`ModuleHost`** (`VenueOS.Core`) — the module registry/lifecycle runner (§23).
- **`VenueProfileService`** (`VenueOS.Venues`) — the venue registry, active-venue switching transaction, and per-venue config store (§10, §11).
- **`ModuleWindowManager`** (`VenueOS.Plugin.Shell`) — the detached-window registry (§19).
- **`GlobalSettingsService`** (`VenueOS.Services`) — global (non-venue) preferences (§7).

## 28. Character identity

Guest/venue-participant identity is `GuestIdentity(Name, HomeWorld)` (`SharedServices.cs`) — normalized (whitespace-collapsed, upper-invariant) into a `Key` of the form `"NAME@WORLD"`. This is what `PresenceService`, `AttendanceService`, `GreeterService`, and `VipOrchestrationService` all key on. Don't introduce account-level/content-ID guest tracking; Character Name + Home World is the established identity model throughout the codebase.

**Character Name alone is never assumed unique.** Reconstruction work on Raffle found real merge/mismatch bugs from treating name-only matching as safe — never strip or normalize away a `HomeWorld` suffix to make two records "match." Legacy Name-only data (predating `HomeWorld` being tracked) may remain valid as-is; don't invent a `HomeWorld` value for it. If a legacy Name-only record must be compared against a Name+HomeWorld one, treat it as ambiguous rather than silently merging.

## 28a. Target capture pattern

When a module needs "the player currently targeted in-game" as a `GuestIdentity`, use the existing `ITargetedPlayerProvider` abstraction (`src/VenueOS.Services/SharedServices.cs`) rather than reading `ITargetManager`/`IPlayerCharacter` directly in a module:

```csharp
public sealed record TargetedPlayerLookup(bool Success, string Name, string HomeWorld, string? Error);
public interface ITargetedPlayerProvider { TargetedPlayerLookup GetTargetedPlayer(); }
```

The production implementation, `DalamudTargetedPlayerProvider` (`Plugin.cs`), validates the current target is an `IPlayerCharacter` (not an NPC/nothing selected) and reads `Name`/`HomeWorld` off it, falling back to `CurrentWorld` only if `HomeWorld` isn't resolvable. Construct it once at the composition root and inject it into any operator panel that needs a "Use Current Target" button — see `VipOperatorPanel`, `RaffleOperatorPanel`, and `VenueBingoOperatorPanel` for the current call-site pattern:

```csharp
if (UiKit.GhostButton(theme, "Use Current Target"))
{
    var result = targetProvider.GetTargetedPlayer();
    if (result.Success) { name = result.Name; world = result.HomeWorld; }
    else targetFillError = result.Error; // shown as a WarningState/ErrorState, never silently dropped
}
```

Manual identity entry (a plain text field for Name/HomeWorld) should populate the same `GuestIdentity(Name, HomeWorld)` shape so both paths converge on one model. Never bypass `ITargetManager` with unsafe/direct memory access to read target info.

## 29. Chat/command infrastructure

Covered in §27 (`ChatCommandService`). It enforces a minimum interval between dispatched commands (default 1s) and runs dispatch through an `IFrameworkDispatcher` (the production one, `InlineFrameworkDispatcher`, just invokes synchronously — the abstraction exists for testability). A module should never call `ICommandManager.ProcessCommand` or send a chat message directly; always go through `ChatCommandService.Enqueue`.

## 30. Testing

Existing test projects: `tests/VenueOS.Core.Tests`, `tests/VenueOS.Venues.Tests`, `tests/VenueOS.Services.Tests` — all xUnit, targeting the ImGui-free `VenueOS.Core`/`VenueOS.Venues`/`VenueOS.Services`/`VenueOS.Modules.Operations` projects. **There is no test project for `VenueOS.Plugin`** — none of the ImGui rendering code (operator panels, `Shell/*`) is unit-tested anywhere in this repository; that boundary is deliberate (ImGui needs a live rendering context) and has held for every phase of this project so far. A new module should put as much of its logic as possible below that boundary — a service class with a well-defined public surface, not tangled into its operator panel's `Draw()` method — specifically so it *can* be tested.

Reasonable things to cover: config default/round-trip serialization, per-venue config isolation (two different venue IDs never see each other's payload), venue-switch behavior (`OnVenueChangedAsync` resets and reloads correctly), enable/disable not losing config, cancellation on dispose/venue-switch, and — for a backend module — typed-client request/response (de)serialization against the donor protocol. See `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` and the existing `*ClientTests.cs` files for the current style: a fake `I...Store`/`IClock`/`IObjectSnapshotProvider` implementation, compact single-line `[Fact]` methods.

**A module whose core function is inherently unsafe/FFXIVClientStructs/ECommons-dependent** (game addon manipulation, not an HTTP backend) — precedent: `promotion.partyfinder`'s reconstruction (`PARTY_FINDER_RECONSTRUCTION.md`) — should still separate as much as possible from that boundary: put the plain data model and orchestration/persistence logic in `VenueOS.Modules.Operations/<ModuleName>/` behind a small interface (e.g. `IPartyFinderAutomation`) that the unsafe engine implements; put only the actual unsafe/addon/ECommons code in `VenueOS.Plugin/<ModuleName>/`, implementing that interface. This keeps the orchestration layer (per-venue settings, guard conditions, chat-text parsing, etc.) unit-testable with a fake implementation of the interface standing in for the real game-dependent engine, even though the engine itself can only ever be verified in-game.

**State surviving a window close is not proof of persistence.** A module's config staying correct while its window is closed and reopened only proves the in-memory object didn't get replaced — it says nothing about whether `SaveModuleConfig` was ever actually called, or whether the save reaches durable storage correctly. The only test that actually exercises persistence is one that destroys and reconstructs the owning service/store from a serialized representation of what was saved (see `PartyFinderServiceTests.Every_preset_field_survives_a_full_serialize_deserialize_boundary` for the pattern: serialize the venue snapshot to a JSON string, deserialize it back into a brand new store/service, and assert every field). The equivalent live check is a full Dalamud plugin disable/re-enable (or a game restart), not merely closing and reopening a module's window — a module isn't done until it's been verified across that actual boundary, not the illusion of it.

**A realtime module's client (§34b) is tested through a fake transport**, not a real socket — implement the same transport interface the production `WebSocketXRealtimeTransport` implements (see `IRaffleRealtimeTransport`) with a controllable fake that can simulate connect, disconnect, malformed frames, and a delayed reconnect, and assert the client's backoff/reconnect/reconciliation behavior deterministically. A backend module's HTTP client is tested the same way — a controllable `HttpMessageHandler`/fake handler, never a real network call.

**Random behavior is tested for invariants, not exact output.** A raffle winner draw, a bracket seed shuffle, etc. should be tested for properties that must always hold (the winner is always a member of the eligible pool, every seed appears exactly once) — never for a specific "lucky" output that would make the test flaky or meaningless.

**Automated tests do not prove:** rendered ImGui layout, actual Dalamud runtime behavior, real FFXIV target/teleport/game interaction, or browser-side visual behavior for a module with a web display component. Those require the manual in-game QA pass (§32) and, for anything visual, the Visual Definition of Done (§41a) — don't report a module as "tested" in a way that implies those were covered by the automated suite.

## 31. Auto Pop-Out testing

For any new detachable module, verify both settings states manually (this is UI-flow behavior with no automated coverage, per §30):

**OFF:** Home click opens the module embedded in the main tablet.
**ON:** Home click opens/focuses the module's detached window; the main tablet stays on Home; clicking the tile again focuses the existing detached window rather than creating a second one.

## 32. Manual in-game QA checklist

- [ ] Home tile appears, correct icon, correct display name
- [ ] Enabled/disabled toggle in Settings → Modules works; disabled tile is unclickable
- [ ] Embedded view opens from Home (Auto Pop-Out off) and looks correct
- [ ] Detached view opens from Home (Auto Pop-Out on); main tablet stays on Home
- [ ] Re-clicking the tile with a detached window already open focuses it, doesn't duplicate it
- [ ] Detached header: correct icon, correct name, no global controls, Settings gear routes to Settings → Modules → this module, Close only closes the detached window
- [ ] Detached window drags via its header, resizes, respects its minimum size
- [ ] Multiple different detached modules open simultaneously don't interfere
- [ ] Switching the active venue updates the module (embedded and any detached instance) and its config context
- [ ] Per-venue config isolation: two venues don't see each other's saved settings
- [ ] Dark, Light, Neon, Midnight all readable, no theme-breaking hard-coded colors
- [ ] No overlapping controls at a wide, a normal, and the minimum supported window size
- [ ] A forced failure surfaces in Settings → Diagnostics, attributable to this module, with secrets redacted
- [ ] Plugin reload / venue-config persistence survives as expected
- [ ] Add module-specific QA beyond this list as appropriate

## 33. File/project structure for a new module

A new module **MUST** get coherent ownership of its own files — never expand `Operations.cs` or `NativeOperationsPanels.cs` (§21). This is a rule about ownership, not about mandatory fragmentation: a small module can keep everything in one `<ModuleName>.cs`/one `<ModuleName>OperatorPanel.cs`; a complex one splits by concern the way Raffle and TournamentControl actually do today:

```text
src/VenueOS.Modules.Operations/
  <ModuleName>/                       (own folder — once the module has more than one file's worth of concerns)
    <ModuleName>Service.cs            (business logic, config DTO, IVenueModule wrapper — the module's core)
    <ModuleName>Client.cs             (backend modules only: typed HTTP client + wire/protocol records — §34)
    <ModuleName>RealtimeClient.cs     (backend modules with a push channel only — §34b)
    <ModuleName>Storage.cs            (only if the module owns bulk content outside GetModuleConfig — §42a)
    <ModuleName>Models.cs             (only if the data-model surface is large enough to warrant its own file)
  <ModuleName>.cs                     (a simple, non-backend module: everything above collapsed into one file — see
                                        the Guest Notes example in §41)

src/VenueOS.Plugin/
  <ModuleName>/                                    (own folder if the panel has more than one file's worth of UI —
    <ModuleName>OperatorPanel.cs                    e.g. Raffle/RaffleOperatorPanel.cs)
  <ModuleName>OperatorPanel.cs                     (a simple module: single file, no subfolder needed)
  Plugin.cs                           (composition root: construct the service, construct the panel, wrap in an
                                        IVenueModule, modules.Register(...) — nothing else needs editing for Home,
                                        embedded rendering, detached rendering, or Settings)

tests/VenueOS.Services.Tests/         (or VenueOS.Core.Tests/VenueOS.Venues.Tests as appropriate)
  <ModuleName>Tests.cs
```

Raffle (`Modules.Operations/Raffle/`: `VenueRaffleService.cs`, `VenueRaffleClient.cs`, `RaffleRealtimeClient.cs`, `RaffleXlsx.cs`) and TournamentControl (`Modules.Operations/Tournament/`: `TournamentControlService.cs`, `TournamentControlClient.cs`, `TournamentRealtimeClient.cs`) are the current reference examples of this split for a complex backend module. No project file changes are needed to add a module — `VenueOS.Modules.Operations` and `VenueOS.Plugin` already reference everything required.

## 34. Backend-backed modules

For a module that talks to an external backend (matching the pattern of Raffle/Trivia/TournamentControl/Bingo):

- The backend server itself stays entirely separate from VenueOS; the donor repository is the protocol authority and is read-only.
- Add a typed client class next to the module (`Modules.Operations/<ModuleName>/<ModuleName>Client.cs`), holding wire/protocol records (requests/responses) distinct from any UI-facing type. See `VenueBingoClient.cs`/`VenueRaffleClient.cs`/`MairsTriviaClient.cs`/`TournamentControlClient.cs` for the current shape: a plain `HttpClient`-wrapping class with `async Task<XResult<T>>` methods, a `XResult<T>(bool Success, T? Value, string? Error, ...)` result record, and a static `Failed(...)` helper.
- Connection settings (server URL, credentials) are per-venue config (§11, §12) and belong in the module's Settings contribution (§8, §9) — not the operational screen.
- Handle venue switching explicitly: cancel in-flight requests (a `CancellationTokenSource`, cancelled and replaced in the module's `Load(nextVenueId)`/`OnVenueChangedAsync`, matching `VenueRaffleService.Load`/`MairsTriviaService.Load`/etc.), clear in-memory state, load the new venue's credentials, and never reuse the old venue's tokens/keys after switching.
- Route backend failures through `DiagnosticsService.RecordFailure` (§25); never surface a raw exception as the primary operational UI.
- If a module needs a distinct *player/browser-facing* display theme (Bingo's `BingoColors`: Bg/Card/Header/Text/Daub/Ball, on `VenueBingoSettings.Colors`), keep it as its own config field, separate from `VenueTheme` — the tablet theme and a module's external/web display theme are different concepts and must stay independently configurable, never merged. Bingo's split into "Server" and "Web Display" concerns is the existing precedent for how a complex module's Settings should be organized (currently, this split lives entirely inside the module's own `DrawSettings()`/operator panel — there's no separate Settings sub-navigation to build; a `Forms.Segmented` sub-tab inside the module's own settings content, matching how `SettingsScreen`'s own top-level categories are built, is the natural way to present it).
- If a module needs a shareable browser/viewer link, prefer a short server-resolved alias over embedding a long
  capability token directly in the URL — Bingo's `short_links` pattern (a short, collision-checked, opaque code
  resolved by a server-side redirect; see `VenueBingoService.GetOrCreatePlayerLinkAsync`/`EnsureCurrentPlayerLinkAsync`)
  and Raffle's equivalent (`VenueRaffleService.EnsureShortLinksAsync`, added 0.3.0 — see `docs/RAFFLE_RECONSTRUCTION.md`
  §20-§27) are both live reference implementations. The short code itself is never a secret — the real credential
  (an admin/room/host/viewer key or token) stays exactly where it already was (an HTTP header, or resolved fresh
  server-side at redirect time), and the short-link layer only ever shortens what would otherwise be a long,
  unwieldy-to-paste-into-FFXIV-chat URL.

## 34a. State authority model — write it down before implementing

For every stateful module, identify **which single layer owns each piece of state** before writing the service class. Possible authorities: VenueOS local persistent config, VenueOS active operational (in-memory) state, FFXIV/game state, the backend server, or a browser/display client. **Two layers MUST NOT silently become competing authorities over the same piece of state** — pick one owner per concept and make every other layer either read-only or explicitly reconciled against it.

The codebase currently has two opposite, both-valid authority models, and a new backend module should explicitly say which one it follows:

- **Raffle (local-authoritative):** `VenueRaffleService`'s local per-venue config (`Settings.Raffles`) is the durable, authoritative store — full participant/ticket/winner data lives in VenueOS. The backend only mirrors a *published* copy for the live browser wheel; a raffle can exist, be edited, and be deleted purely locally without ever touching the backend. Because two independent stores (local + published) exist and are never auto-reconciled, the operator needs a visible "unpublished changes" indicator — don't silently let local edits drift from what's already published.
- **TournamentControl (backend-authoritative):** the backend's store is the sole source of truth for bracket state; `TournamentControlService` holds only per-venue connection/credential settings plus the *last-fetched* snapshot — never a competing local notion of bracket state. All mutations go through `TournamentControlClient`; the realtime client is purely a signal to re-fetch, never a second write path.

For a simple, purely local module with no backend, this can be a one-sentence statement ("this module's local per-venue config is the only state, full stop") — the point is to have made the decision explicitly, not to produce a diagram for a Block Letters-style utility.

## 34b. Realtime / WebSocket module contract

Only add a realtime channel if the module genuinely needs live push updates from a backend it doesn't otherwise poll — don't add WebSockets to a module that doesn't need them, and don't aggressively poll as a substitute for a realtime protocol that already exists. `RaffleRealtimeClient` and `TournamentRealtimeClient` (`Modules.Operations/Raffle/`, `Modules.Operations/Tournament/`) are the current reference implementations and are structurally identical — follow their shape for a new realtime-backed module:

- **Separate transport from application logic.** Define a small transport interface (`IRaffleRealtimeTransport`-style) wrapping the actual `ClientWebSocket`; the realtime client itself depends only on that interface, which is what makes it fake-transport-testable (§30).
- **`Start(uri, sessionId, token)` / `Stop()`** — `Start` calls `Stop()` first (idempotent), creates a fresh `CancellationTokenSource`, and fires an unawaited `RunAsync` loop. `Stop()` cancels and disposes that token and drains the inbox.
- **Join as a read-only viewer**, never requesting write/host capability merely because the protocol allows it — this structurally prevents the VenueOS client from becoming an unintended second writer (ties to §34a: the backend stays authoritative for anything the realtime channel observes).
- **Backoff:** the current pattern is a simple **linear** backoff capped at a small multiple of a base unit (`retryBackoffUnit * min(10, attempt)`, default unit ~1s → capped at ~10s), retried indefinitely until explicitly cancelled — not exponential, and not a fixed max-attempt count. Match this unless a specific backend's behavior demands otherwise.
- **Drain into a queue, never mutate state from the socket thread.** The receive loop pushes parsed messages into a `ConcurrentQueue<TMessage>`; the module's `Tick()` (framework/UI thread) drains and applies them — see §24a.
- **Reconcile via REST after every reconnect, not just the first connect.** `ConsumeReconnectSignal()` returns `true` exactly once per successful *reconnect* (never the initial connect) and tells the owning service's `Tick` to re-fetch authoritative state over REST — this is what recovers correctly from frames the socket missed while disconnected. An optimistic client-side mutation made during a disconnect window must be safely overwritten (not merely appended-past) by this reconciliation.
- **Stopped on venue switch** via the module's `Load(nextVenueId)`, before the new venue's settings are loaded (§12a).
- Also consider on top of the above where relevant: server restart, malformed/unexpected frames (don't crash the receive loop on one bad frame), and never putting a secret in a connection URL/frame that a non-privileged viewer shouldn't see (§26).

## 34c. Import / export

Where a module supports import/export (Raffle's XLSX round-trip, Mair's Editor's `.fftrivia` question-set import are the current examples — `RaffleXlsx.cs`, `FileQuestionSetRepository.cs`):

- **Import MUST mint fresh local identity** — a new local ID, and any external/backend linkage (published URL, host/viewer tokens, live session ID) cleared rather than inherited — unless the format is explicitly a backup/restore of a previously-exported VenueOS object rather than a content import. `RaffleXlsxImporter.Import` is the reference: it always returns a raffle with `ExternalId`/`HostUrl`/`ViewerUrl` forced null even if the source file recorded one.
- **Import must actually persist**, through the same `SaveModuleConfig`-backed path as any other mutation (§13a) — whether the importer itself calls `Save()` (Mair's Editor's `ImportReplacing`/`ImportAsNew`) or leaves persistence to an explicit caller method (`VenueRaffleService.ImportRaffle`), pick one pattern per module and be consistent about which layer is responsible.
- **A collision (importing over something that already exists) needs an explicit operator decision** — confirm-replace vs. import-as-new (§37), never a silent overwrite.
- **A legacy export format predating a newly added field** (e.g. `HomeWorld` added after a file format already existed) must import as null/absent for that field, never an invented or guessed value.
- Export/import should round-trip every user-authored field needed for a useful restore; consider backward compatibility for previously-exported files when evolving the format.

## 35. Responsive layout and the no-overlap rule

**No user-facing control may overlap another at any supported window size.** The pattern proven across the main toolbar, the Diagnostics toolbar, and the detached header is the same every time: reserve the region for critical controls first (compute their fixed total width, position them at an absolute screen coordinate derived only from the container's width — never from what secondary content happens to render), then give whatever's left to flexible/secondary content, and reflow or truncate the secondary content rather than letting it push into reserved space. Avoid chaining `ImGui.SameLine()` (no-argument) after a component that draws more than one line internally (e.g. `Forms.ComboField`'s label-then-field) — position the next column from a captured shared row-top instead. Use `ImGui.CalcTextSize` (with a `wrapWidth` argument for pre-wrap height estimation) to make layout decisions before drawing, and `ImGui.GetItemRectMax()` after drawing to get an item's *actual* rendered extent when you need it for placing what comes next.

## 36. Empty, loading, error, warning, and status states

Every operational module **MUST** deliberately handle its visible states rather than leaving any of them to silence or a raw exception message. Consider all of the following where applicable — a module without a backend won't have "disconnected," but every module has at least an empty state and (if it can fail at all) an error state:

| State | Example | Component |
|---|---|---|
| Empty | "No raffle selected." / "No participants yet." | `UiKit.EmptyState` |
| Loading / busy | "Publishing…" / "Connecting…" / "Waiting for game state…" | `UiKit.LoadingState` |
| Success / current status | "Published." / "Connected." / "Running." / "Winner selected." | `UiKit.StatusBadge`/`ConnectionBadge` |
| Warning | "Unpublished changes." / "Backend unavailable, local state preserved." | `UiKit.WarningState` |
| Error | "Authentication failed." / "Import invalid." | `UiKit.ErrorState` |
| Disconnected / degraded | networked modules only — socket down but REST still reachable, etc. | `UiKit.ConnectionBadge`/`WarningState` |

A "not configured" backend module should say so plainly (see `MairsTriviaOperatorPanel`'s "Create a game by supplying a validated .fftrivia question set..." empty state) rather than showing broken/blank controls.

**MUST NOT rely on:** a disabled button with no explanation of *why* it's disabled; silent failure; `DiagnosticsService` as the operator's only feedback (Diagnostics is for diagnostic *detail* — the operational screen still needs an immediately understandable state, per §25); or `NotificationService` for anything the operator needs to actually see, since nothing currently renders its toasts (§21) — a push to `NotificationService` today has no visible effect.

## 37. Destructive actions and confirmations

Use `ConfirmDialog` (`UiKit`, via `confirmDialog.Request(title, body, action)` — instantiate one `private readonly ConfirmDialog confirmDialog = new();` per panel, matching every current operator panel) for anything that destroys persistent data or state the operator can't trivially redo: permanent delete, reset (§13b), cancelling an active event/game where state is lost, redrawing a winner that replaces an existing one, clearing participant state, overwriting imported data. Don't over-confirm harmless, reversible actions (toggling a switch, running a preset, archiving — §13b).

**Confirmation text MUST state what will actually happen**, not a generic "Are you sure?" — a short interrogative title (`"Delete X?"`) plus a body sentence naming exactly what's affected. **Irreversible actions end with a plain statement that it cannot be undone** (`"…will be permanently deleted from VenueOS, and its published copy on the backend will also be deleted if it exists. This cannot be undone."`, `RaffleOperatorPanel`'s delete-raffle dialog). **Reversible-but-still-confirmed actions should say what is *not* affected** — Raffle's Reset dialog explicitly notes "It does NOT delete the raffle itself." Scale the dialog's severity to the actual consequence: if an action would additionally destroy already-completed downstream state (e.g. clearing a bracket that has completed matches), compute that client-side using the same rule the backend independently enforces, and only show the stronger "cannot be undone" framing when it's genuinely true — don't show the scary version of the dialog for a mild action or vice versa.

**A client-side confirmation dialog is a courtesy, not the safety mechanism.** The actual guard against an accidental or malicious destructive call MUST be enforced authoritatively wherever the state actually lives (the service/backend), regardless of what the client sent — never assume a confirmed UI click is itself sufficient authorization server-side.

## 38. Common mistakes to avoid

- Modifying a donor/reference repository.
- Adding a module-specific "Venue Name" field instead of reading `venues.Current.DisplayName`.
- Hard-coding a color instead of a `ThemeTokens` value.
- Raw `ImGui.Button`/`InputText`/`Checkbox`/`Combo` where a `UiKit`/`Forms` equivalent exists.
- Forgetting the icon, or reusing another module's icon key.
- Assuming Home needs module-specific code — it doesn't (§6).
- Writing `if (module == "X")` anywhere in the launch/window infrastructure instead of using the generic routing (§7).
- Two different implementations of the module's content for embedded vs. detached — there is exactly one `Draw()` (§19).
- Duplicating `PresenceService`/`ChatCommandService`/`SchedulerService` instead of using the shared instance.
- Config leaking between venues (always go through `GetModuleConfig`/`SaveModuleConfig`, never a module-level static/singleton holding venue-specific state).
- Logging or displaying a secret (API key, room/admin key, password, bearer/refresh token) outside `DiagnosticsService.Redact`'s coverage.
- Continuing to use a previous venue's backend credentials/connection after a venue switch.
- Not cancelling a `CancellationTokenSource`/scheduler job/event subscription on venue switch or disposal.
- Putting live operational data (a roster, an active game state) into Settings, or persistent setup (credentials) into the operational screen.
- A native ImGui title bar on a detached window (must be `NoTitleBar`).
- A fixed-pixel-offset layout that overlaps at another window size instead of a reserved-region layout (§35).

## 39. New module planning template — complete this before writing code

Answer every line below before writing any code — this is the pre-flight check that prevents architecture from being discovered accidentally mid-implementation. Leaving a line blank should mean "genuinely not applicable to this module," not "hadn't thought about it yet."

```text
Module Name:
Stable Module ID:
Purpose:
Display Name:
Icon:
Per-Venue?: (per-venue is the default — §12; justify if this module is a rare exception)
Operational Workflow: (what stays visible, what's one-click, what's setup-once)
Persistent Settings (Settings → Modules, §9):
Ephemeral / live-only state (never persisted — §13a):
Per-Venue Settings:
Global Settings (rare — justify if any, §12):
Uses FFXIV player identity / GuestIdentity(Name, HomeWorld)?: (§28; if yes, does it need target capture — §28a?)
Backend (if any):
State authority model: (§34a — which layer owns what; one sentence is fine for a simple local module)
Realtime/WebSocket needed?: (§34b; justify — don't default to yes)
What cancels on venue switch?: (§12a, §24a)
Destructive actions and their confirmations: (§37)
Import/export?: (§34c)
Archive/Delete/Reset semantics, if the module has reusable local objects: (§13b)
Detached Support: (yes by default; note if genuinely not possible and why — §19)
Auto Pop-Out Compatibility: (generic — confirm no special-casing was added — §7)
Empty / loading / success / warning / error / disconnected states: (§36)
Diagnostics: (what failures get reported, how — §25, §26)
UnderDevelopment at launch?: (yes by default for a new module — §22a)
Special Risks:
Automated Tests required: (§30)
Behaviors requiring live QA: (§32, §41a)
```

## 40. Backend module planning template (in addition to the above)

```text
Backend Repository (read-only reference):
Protocol Authority: (the donor repo's server/client code)
Server URL Behavior: (per-venue, validated how)
Authentication: (flow, token lifetime)
Secrets: (which fields, confirm Redact coverage)
Polling/WebSocket: (interval, reconnect behavior)
Cancellation: (on venue switch, on disable, on dispose)
Venue-Switch Behavior: (old context torn down before new one starts)
Protocol Tests: (request/response (de)serialization)
Standalone Client Compatibility: (confirmed against the donor repo's actual wire format)
```

## 41. Minimal example: "Guest Notes"

A fictitious, simple, non-backend module — an operator can jot a short note per guest during a session. Simplified but uses real current APIs throughout.

### Config (`src/VenueOS.Modules.Operations/GuestNotes.cs`)

```csharp
namespace VenueOS.Modules.Operations;

public sealed record GuestNote(string GuestKey, string Text);
public sealed record GuestNotesSettings(List<GuestNote> Notes)
{
    public static GuestNotesSettings Default() => new([]);
}

public sealed class GuestNotesService(VenueProfileService profiles)
{
    private Guid venueId;
    public GuestNotesSettings Settings { get; private set; } = GuestNotesSettings.Default();

    public void Load(Guid nextVenueId)
    {
        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, "tools.guestnotes", 1, GuestNotesSettings.Default);
    }

    public void AddNote(string guestKey, string text)
    {
        Settings = Settings with { Notes = [.. Settings.Notes, new GuestNote(guestKey, text)] };
        profiles.SaveModuleConfig(venueId, "tools.guestnotes", 1, Settings);
    }
}

public sealed class GuestNotesModule(GuestNotesService service, Action? draw = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("tools.guestnotes", "Guest Notes", "Short per-guest operator notes.", "message");
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { service.Load(c.VenueId); return Task.CompletedTask; }
    public void Tick(DateTimeOffset now) { }
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => draw?.Invoke(); // Guest Notes has no persistent config to separate — the notes ARE
                                                   // the operational content, not settings. This is the documented
                                                   // exception §8/§9 allow, not the default: a module with any real
                                                   // settings (a backend URL, a template) MUST split DrawSettings().
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

(`"message"` is reused deliberately here rather than inventing a new icon for this example — a real module would pick or add a distinct key per §5.)

### Operator panel (`src/VenueOS.Plugin/GuestNotesOperatorPanel.cs`)

```csharp
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin;

internal sealed class GuestNotesOperatorPanel(GuestNotesService service, VenueProfileService venues)
{
    private string guestKey = "", noteText = "";

    public void Draw()
    {
        var theme = venues.Current.Theme; // active VenueContext — never a module-local venue field
        UiKit.BeginSectionCard("guestnotes-add", theme, "Add a note");
        Forms.TextField(theme, "Guest (Name@World)", ref guestKey, 128);
        Forms.TextField(theme, "Note", ref noteText, 256);
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Save") && !string.IsNullOrWhiteSpace(guestKey) && !string.IsNullOrWhiteSpace(noteText))
        {
            service.AddNote(guestKey.Trim(), noteText.Trim());
            guestKey = ""; noteText = "";
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("guestnotes-list", theme, $"Notes ({service.Settings.Notes.Count})");
        if (service.Settings.Notes.Count == 0) UiKit.EmptyState(theme, "No notes yet", "Add one above during the session.");
        else foreach (var note in service.Settings.Notes) UiKit.ListRow(theme, note.GuestKey, note.Text, false);
        UiKit.EndSectionCard();
    }
}
```

### Registration (`Plugin.cs`, alongside the existing modules)

```csharp
var guestNotesService = new GuestNotesService(venues);
var guestNotesPanel = new GuestNotesOperatorPanel(guestNotesService, venues);
modules.Register(new GuestNotesModule(guestNotesService, guestNotesPanel.Draw));
```

### Everything else, and why it needs no extra code

- **Home tile:** appears automatically — `modules.Register` adds it to `ModuleHost.Modules`, which `HomeScreen.DrawGrid` iterates generically.
- **Embedded rendering:** `AppFrame.DrawModule` wraps `GuestNotesModule.Draw()` automatically once it's selected.
- **Detached rendering:** the pop-out button on the embedded frame and `ModuleWindowManager` both call the same `GuestNotesModule.Draw()` — no separate detached implementation exists or is needed.
- **Auto Pop-Out:** `HomeScreen.LaunchModule` routes this module exactly like every other one — nothing module-specific to add.
- **Settings → Modules:** the module appears in the table automatically; "Configure" calls `GuestNotesModule.DrawSettings()`, which here re-renders the same content only because this module genuinely has no persistent configuration to separate (§8/§9's documented exception, not the default — a module with actual settings MUST split them out).
- **Diagnostics:** none wired in this minimal example since it can't fail (no backend, no external state); a real module with a failure mode would take a `DiagnosticsService` constructor parameter and call `diagnostics.RecordFailure(...)` from `GuestNotesService` on any recoverable error, e.g. `diagnostics.RecordFailure($"tools.guestnotes: failed to save note ({ex.Message})");`.
- **Disposal:** `DisposeAsync() => ValueTask.CompletedTask` is correct here since the service owns no subscriptions, timers, or connections to release.

## 41a. Visual Definition of Done

A module is not visually complete merely because every control exists and the tests pass. **Automated tests cannot satisfy this checklist — it requires looking at the module rendered live in Dalamud** (§32). Before calling a module ready for live acceptance, check:

- [ ] The primary action is visually obvious (not competing for attention with secondary controls)
- [ ] Destructive actions are visually differentiated (`DangerButton`, not a `PrimaryButton`/`GhostButton` indistinguishable from a safe action)
- [ ] Settings and live operation read as genuinely separate (§9), not the same content twice
- [ ] Sections have coherent grouping — related controls are visually grouped, unrelated ones aren't crammed together
- [ ] Labels are understandable without reading the source
- [ ] Fields have enough width for realistic content (a server URL, a long guest name)
- [ ] Current status is visible at a glance, not buried
- [ ] The empty state is an intentional message, not a blank/broken-looking area
- [ ] Errors are visible in the operational UI itself, not only in Diagnostics
- [ ] Warnings explain *why* an action is blocked, not just that it is
- [ ] Confirmation dialogs use the shared `ConfirmDialog`, not a raw ImGui modal
- [ ] Scrolling works and long content doesn't overlap (§35)
- [ ] Tables/lists remain usable at the minimum supported window size
- [ ] The detached view works correctly if the module supports it (§19–§20)
- [ ] All four themes (Dark, Light, Neon, Midnight) are respected — no hard-coded colors breaking the visual language (§16, §17)
- [ ] No important control is clipped at any supported window size
- [ ] Nothing uses raw/default ImGui presentation where a `UiKit`/`Forms` equivalent exists (§15)

**This checklist establishes the bar for a new module now.** It is not the global interface-cleanup pass — do not use it as license to go back and re-polish spacing/typography/layout on existing shipped modules (Attendance, Greeter, VIP, ShoutRunner, Party Finder, Raffle, Mair's Trivia/Editor, TournamentControl, Bingo); that dedicated pass happens later, after Giveaways/Block Letters/Macro, and this guide will get one final sync pass after it to reflect whatever shared UI conventions come out of it.

## 42. Definition of Done

**Architecture**
- [ ] Module has its own dedicated file(s)/folder — not added to `Operations.cs`/`NativeOperationsPanels.cs` (§21, §33)
- [ ] Stable module ID assigned, namespaced, never reused from another module
- [ ] Display name defined and correct everywhere it's read from (there's only one place — §4)
- [ ] Icon key registered in `AppIcons`, distinct from every other module's key
- [ ] State authority model stated (§34a) — which layer owns each piece of state
- [ ] `modules.Register(...)` added in `Plugin.cs`
- [ ] Home tile appears with correct icon/name
- [ ] Enable/disable toggle in Settings → Modules works and disables the tile/detached window correctly

**Venue**
- [ ] `venues.Current` used for venue identity — no module-specific "Venue Name" field (§10)
- [ ] Per-venue config isolated via `GetModuleConfig`/`SaveModuleConfig`, keyed by this module's own ID
- [ ] Venue switch tested with two venues holding different config for this module (§12a, §32)
- [ ] No stale state (settings, operation state, participants, credentials, timers, realtime subscriptions, cached data) crosses venues (§12a)

**Settings**
- [ ] `DrawSettings()` shows only persistent configuration, distinct from `Draw()`'s live operation (§9) — unless a documented exception applies
- [ ] Every editable persistent field actually gets saved (§13a) — not just held in an in-memory object
- [ ] Credentials persist as plain, unmasked, copyable text where applicable (§9a)
- [ ] A global setting was added only if it's genuinely venue-independent (§12)
- [ ] Schema version set; recoverable-payload path doesn't silently discard user data (§13)

**Lifecycle**
- [ ] Cancellation ownership defined — a `CancellationTokenSource` cancelled/recreated on every relevant transition (§24a)
- [ ] Enable/disable preserves config; disable behavior matches §22
- [ ] Venue switching reloads config and resets in-memory state correctly (§11, §12a)
- [ ] Disposal releases every subscription/timer/connection the module owns (§24)
- [ ] Stale async callbacks and stale realtime frames are guarded against (§24a)
- [ ] Realtime channel (if any) is disposed and reconciled correctly on venue switch/reconnect (§34b)

**UI**
- [ ] `UiKit`/`Forms` components used; no raw `ImGui.Button`/`InputText`/`Checkbox`/`Combo` for user-facing controls
- [ ] Only semantic theme tokens used, no hard-coded colors
- [ ] `ConfirmDialog` used for destructive actions, with consequence-stating text (§37)
- [ ] Empty, loading, success/status, warning, error, and disconnected states all handled where applicable (§36)
- [ ] Destructive actions are visually differentiated from safe ones (`DangerButton`)
- [ ] Embedded view works via `AppFrame.DrawModule`
- [ ] Detached view works via `ModuleWindowManager`, identical content to embedded, header shows only icon/name and Settings/Close (§19–§20)
- [ ] Auto Pop-Out off → embedded; on → detached, main tablet stays on Home, re-click focuses not duplicates
- [ ] Resizing works, no overlap at wide/normal/minimum sizes (§35)
- [ ] Dark, Light, Neon, Midnight all tested (§17)
- [ ] Full Visual Definition of Done walked (§41a)

**Security**
- [ ] No credential logged or shown outside Settings' plain-text UI (§9a, §26)
- [ ] Secret-bearing URLs sanitized before reaching Diagnostics/exceptions — checked for query params, headers, *and* path segments, not just a `key=value` marker (§26)
- [ ] Backend authorization enforced server-side, not only via client-side confirmation (§37)
- [ ] A browser/viewer-facing client (if any) never receives an organizer/admin-level secret it doesn't need (§26)

**Guest Identity (where applicable)**
- [ ] `GuestIdentity(Name, HomeWorld)` used for FFXIV player/guest identity, not Name alone (§28)
- [ ] Target capture (if any) goes through `ITargetedPlayerProvider` (§28a)
- [ ] Legacy Name-only data (if any) is handled as an intentional, documented case, not silently merged (§28)

**Data**
- [ ] Persistence verified across an actual reload boundary (disable/re-enable or serialize/deserialize), not just a window close/reopen (§13a, §30)
- [ ] Import/export verified if applicable — fresh identity on import, collision handling explicit (§34c)
- [ ] Archive/Delete/Reset semantics verified if applicable, and kept distinct (§13b)

**Backend (if applicable)**
- [ ] Typed client, protocol models separate from UI
- [ ] Verified against the donor repository's actual current protocol
- [ ] Credentials are per-venue
- [ ] Venue switch cancels/tears down the old backend context before using the new one's credentials
- [ ] In-flight requests are cancellable
- [ ] Realtime reconnect reconciles against REST, not just resumes the socket (§34b)

**Testing**
- [ ] Automated tests added for whatever is pure logic, in the appropriate `tests/VenueOS.*.Tests` project
- [ ] Per-venue isolation tested
- [ ] Error paths tested, not just the happy path
- [ ] Realtime/backend client tests use a fake transport/handler, not a real network call (§30)
- [ ] Import/export tests included if applicable
- [ ] Global config behavior tested if a global setting was added
- [ ] `dotnet build VenueOS.sln` succeeds (Debug and Release); full existing test suite still passes
- [ ] Warnings reviewed, not silently ignored

**Live QA** — cannot be satisfied by automated tests; requires the user actually running the module in Dalamud (§32, §41a)
- [ ] Rendered ImGui behavior verified live
- [ ] Real FFXIV integration verified where applicable (target capture, presence, chat commands)
- [ ] Browser/web display verified where applicable
- [ ] User acceptance actually obtained — do not report a module as "live-tested" unless the user performed it

**Promotion**
- [ ] `UnderDevelopment: true`, disabled by default, until the user explicitly promotes it (§22a)
- [ ] No self-authorized promotion/release merely because automated tests and builds passed (§22a, and the "no self-authorized deferrals" rule at the top of this document — deferring required work and self-promoting unfinished work are the same mistake in opposite directions)

**Documentation**
- [ ] Anything genuinely new about the shared architecture (a new UI-kit component, a new shared service) is reflected here or in `UI_STATUS.md`
- [ ] Backend/protocol notes documented if applicable

## 42a. Shared, module-independent content repositories (new pattern — Mair's Trivia/Editor)

Some content is neither per-venue config nor a single module's operational state: it needs one VenueOS-wide authoritative store, read and/or written by more than one module, with a lifetime independent of any one module's enabled state. Mair's Trivia's/Editor's shared canonical question library (`src/VenueOS.Modules.Operations/QuestionLibrary/`) is the first instance of this and the reference pattern for the next one:

- Define a plain storage interface (`IQuestionSetRepository`) in its own folder under `VenueOS.Modules.Operations`, with no dependency on either consuming module's types. Construct exactly one implementation once at the composition root (`Plugin.cs`), before either consuming module's service — never inside a module's `InitializeAsync`/`OnVenueChangedAsync`, since a disabled module must never affect whether the repository exists or what it contains.
- Physical storage lives under `<PluginInterface.ConfigDirectory>/VenueOS/<feature>/` — a durable location outside the small per-venue module-config payloads (`GetModuleConfig`/`SaveModuleConfig` are for small settings blobs, not bulk content), following the same `ConfigDirectory`-rooted convention `attendance.db` already uses. Use atomic writes (temp file + `File.Move(..., overwrite: true)`), a small recoverable navigation index separate from full content, and a rebuild-from-content-files recovery path for a missing/corrupt index.
- Give every stored item a computed Ready/Incomplete/Invalid-style status rather than a single boolean — see `QuestionSetValidator` for the shape: never conflate "still being authored" with "structurally broken."
- A "does anything else still need this?" check (Mair's Editor's delete-while-in-use guard) is wired as a plain `Func<Guid, bool>` predicate supplied by the composition root, backed by whichever module tracks live usage (`MairsTriviaService.ActiveSourceSetIds`) — never a hard type reference from the repository or the authoring module to the other module's service.

## 42b. Pre-switch venue guard (new pattern — `VenueSwitchCoordinator`)

`VenueProfileService.SwitchAsync` itself must stay exactly as simple as it is today — isolated per-module `OnVenueChangedAsync` failures, no module able to block a switch. But a module can still have a genuinely destructive *known* consequence to leaving its venue (Mair's Trivia ending a live game). For that narrow case, route the UI action through `Shell/VenueSwitchCoordinator.cs` instead of calling `VenueProfileService.SwitchAsync` directly:

- A module contributes an `IVenueSwitchGuard` (`DescribeRisk()` returning a warning string or null, `ResolveAsync()` performing the actual graceful shutdown only after the operator confirms). Register it once at the composition root (see `TriviaVenueSwitchGuard`, constructed in `VenueOS.Plugin`, not `VenueOS.Modules.Operations`, since `IVenueSwitchGuard` is shell/UI-layer).
- Every UI call site that switches venues (`TabletHeader`'s quick-switch combo, `VenueSettingsPage`'s Switch-to button and its create-and-switch flow) calls `coordinator.RequestSwitch(id)`, never `venues.SwitchAsync` directly. The coordinator shows one shared `ConfirmDialog` only when a guard actually reports a risk; with no guard concerned, the switch proceeds immediately with no added friction for every other module.
- This is intentionally a single, narrow hook — not a general "any module can veto a switch" mechanism. Add a guard only when a module has a real, known-destructive, immediately-visible consequence to leaving its venue; anything else stays handled by `OnVenueChangedAsync`'s existing per-module isolation.

## 43. Instructions for an AI coding agent

This guide remains a general engineering document usable by humans too — the rules above apply regardless of who's implementing. This section is the compact checklist specifically for an AI session picking up a "build module X" task.

**Before implementation:**

1. Read this entire document before writing any code.
2. Inspect the actual current source for anything you're about to touch or extend — this guide is a snapshot, not a substitute for reading `Plugin.cs`, `ModuleHost`, `VenueProfileService`, and the UI kit files directly.
3. If the module is based on a donor/reference project, inspect the donor's actual behavior before "simplifying" it — donor behavior is authoritative unless the user explicitly overrides it, and the current VenueOS scaffold is not automatically a complete specification on its own. For a greenfield module (no donor), this step doesn't apply — see the intro's Normative language/donor note.
4. Complete the pre-flight planning template (§39) — establish the authority/persistence/lifecycle model in writing before coding, not while coding.

**During implementation:**

5. **No self-authorized deferrals** (see the top of this document) — do not silently relabel required work as future/optional/Phase 2/TODO because it's inconvenient. If genuinely blocked, stop and report the blocker; the user decides on deferral, not you.
6. Reuse existing shared infrastructure (§27) rather than building a parallel version of it.
7. Never modify a donor/reference repository (listed at the top of this document) unless the user has explicitly authorized it for that specific task; never commit/push/tag/release inside one.
8. Do not modify unrelated modules or silently change product decisions while implementing this one.
9. Implement both embedded and detached rendering as one shared `Draw()` — never two implementations.
10. Verify Auto Pop-Out works correctly in both states without any module-specific branching.
11. Give the module a real Settings contribution, distinct from its operational UI where the content genuinely differs (§9).
12. Read venue identity from `VenueProfileService.Current`, never a module-local field.
13. Keep persistence real (§13a) — every editable field that should survive a reload goes through an actual save call, verified across a real reload boundary, not just a window close/reopen.
14. Use shared UI components (§15) rather than raw ImGui or one-off visual conventions for ordinary confirm/warn/error/empty states.
15. Test incrementally as you go, not only at the end.

**After implementation:**

16. Build (`dotnet build VenueOS.sln`, Debug and Release) and run the existing full test suite before considering the task done; add tests for new pure logic (§30).
17. Walk the Definition of Done (§42) explicitly and report which items are satisfied — including the Visual and Live QA categories, which you cannot personally satisfy from a terminal session.
18. If the task requests a written completion report, write it to disk and report its exact path — don't paste the whole thing into chat only.
19. **Do not claim live QA, manual in-game testing, or user acceptance was performed unless the user actually performed it.** Reporting "tested and working" for something only unit tests covered is a real class of mistake this guide exists partly to prevent (§30, §41a).
20. If something in this guide doesn't match what you find in the repository, or a requirement genuinely can't be met (e.g., a module that truly cannot support detaching), say so explicitly rather than silently deviating or forcing a fit.

## 44. Relationship to `MODULE_DEVELOPMENT.md`

`MODULE_DEVELOPMENT.md` (repository root) is a short, older quick-reference predating the Settings/detached-window/Auto-Pop-Out architecture described here. It has been given a pointer to this document (see its top) rather than being rewritten or deleted — its core module-registration example is still broadly accurate, but it does not cover Home integration, detached windows, Settings contribution, theming, or Auto Pop-Out at all, and its mention of `VenueHttpClientFactory` as a service to use is inaccurate (§21 — that class currently has no callers). **This document, `NEW_MODULE_GUIDE.md`, is authoritative for anything the two disagree on or where `MODULE_DEVELOPMENT.md` is silent.**
