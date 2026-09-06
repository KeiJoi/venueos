# VenueOS New Module Guide

The authoritative, current-implementation guide for adding a new module to VenueOS. Every class, interface, method, and file path named below was verified against the actual repository at `C:\FFXIVplugs\venueos` as of the end of the Phase 3 shell/Settings/detached-window/Auto-Pop-Out work. Where something described in an earlier architecture document (`VENUEOS_ARCHITECTURE.md`, `ARCHITECTURE.md`) no longer matches the code, this guide follows the code and flags the discrepancy in §21 ("Known inconsistencies").

This document is written for a developer or an AI coding agent picking up a "build module X" task with no other context. Read it fully before writing code. If anything below turns out not to match the repository by the time you're reading it, trust the repository and treat this file as needing an update, not the other way around.

Donor/reference repositories — `venuepartyfinder`, `venuestatusandgreet`, `shoutrunner`, `ffxivraffle4all`, `mairstrivia`, `tournamentcontrol`, `ffxivbingo4all` — are **read-only**. They are the functional/protocol reference when reconstructing a backend-compatible module; never edit, format, or otherwise modify them.

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

**Known current gap, not a pattern to copy:** every existing module's `DrawSettings()` currently just calls the same `draw` delegate as `Draw()` — none of them yet separate persistent configuration from live operation in their Settings view. A new module should do better: `DrawSettings()` should render only the module's persistent configuration (server URL, credentials, defaults — §9), while `Draw()` renders live operational state and controls.

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

## 21. Known current inconsistencies (documented, not fixed here)

Per the task's documentation-first instruction, these are noted rather than silently corrected:

- `VipRecord`, `TournamentModuleSettings.VenueName`, `VenueBingoSettings.VenueName` predate the "Active Venue Profile is authoritative" rule (§10) and still carry their own venue-name field sent to their backend. A future functional-reconstruction pass should supersede these with `venues.Current.DisplayName`. **`MairsTriviaSettings.VenueName` has already been removed** (Mair's Trivia/Editor Phase 2 reconstruction) — `MairsTriviaService` reads `profiles.Current.DisplayName` directly at request time instead; this is the reference example for the remaining two.
- Every current module's `DrawSettings()` currently mirrors `Draw()` exactly (§8) — none yet separates persistent config from live operation in its Settings contribution, **except `promotion.partyfinder`** (reconstructed — see `PARTY_FINDER_RECONSTRUCTION.md`), whose `DrawSettings()` holds only Auto Refresh/Warning Message Override while every recruitment-criteria field lives in `Draw()`. Use it as the reference example for a new module doing this correctly from the start.
- `src/VenueOS.Services/SharedServices.cs` defines `GameContextService` and `VenueHttpClientFactory`, but neither has any caller anywhere in the codebase today — they are unused scaffolding, not an active convention. Backend modules construct `HttpClient` directly (`new HttpClient { Timeout = ... }`) rather than through `VenueHttpClientFactory`.
- `NotificationService`/`VenueUi` (`src/VenueOS.UI/VenueUi.cs`) still exist and `NotificationService.Push` is still called on module failure/recovery, but nothing renders `NotificationService.Toasts` anywhere anymore (the raw toast-line UI was deliberately removed from the main window during shell cleanup) — pushing a toast today has no visible effect. Prefer `DiagnosticsService.RecordFailure` for anything that should actually be visible to the operator (see §25).
- Attendance, Greeter, VIP, ShoutRunner, and Raffle's operator-panel classes are all grouped in one file, `src/VenueOS.Plugin/NativeOperationsPanels.cs`, for historical reasons. Party Finder, Mair's Trivia, TournamentControl, and Bingo each have their own file. **A new module should use its own file** (e.g. `src/VenueOS.Plugin/GuestNotesOperatorPanel.cs`), matching the newer precedent, not extend `NativeOperationsPanels.cs`.
- Similarly, the four core modules' service/wrapper classes (Attendance/Greeter/VIP/ShoutRunner) all live in one file, `src/VenueOS.Modules.Operations/Operations.cs` (along with the four backend modules' service+module wrapper classes — their protocol clients get their own subfolder, e.g. `Modules.Operations/Bingo/VenueBingoClient.cs`). **A new module should use its own file/folder** under `VenueOS.Modules.Operations/`, not grow `Operations.cs` further.

## 22. Enable/disable

`IVenueModule.IsEnabled` is a plain settable `bool`, toggled from Settings → Modules' `Toggle` (`ModulesSettingsPage.DrawModuleRow`). Disabling a module:

- Removes its Home tile's clickability (`AppTile`'s `enabled` gate) and dims it.
- Excludes it from `ModuleHost.Tick`/lifecycle notification loops (`ModuleHost.Ordered().Where(x => x.IsEnabled)`).
- Closes its detached window if one is open (`ModuleWindowManager.DrawAll` checks `module.IsEnabled` every frame and removes a disabled module from the open set).
- **Does not** touch its saved per-venue config — `GetModuleConfig`/`SaveModuleConfig` are keyed by module ID regardless of enabled state, so re-enabling restores exactly where it left off.

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

## 29. Chat/command infrastructure

Covered in §27 (`ChatCommandService`). It enforces a minimum interval between dispatched commands (default 1s) and runs dispatch through an `IFrameworkDispatcher` (the production one, `InlineFrameworkDispatcher`, just invokes synchronously — the abstraction exists for testability). A module should never call `ICommandManager.ProcessCommand` or send a chat message directly; always go through `ChatCommandService.Enqueue`.

## 30. Testing

Existing test projects: `tests/VenueOS.Core.Tests`, `tests/VenueOS.Venues.Tests`, `tests/VenueOS.Services.Tests` — all xUnit, targeting the ImGui-free `VenueOS.Core`/`VenueOS.Venues`/`VenueOS.Services`/`VenueOS.Modules.Operations` projects. **There is no test project for `VenueOS.Plugin`** — none of the ImGui rendering code (operator panels, `Shell/*`) is unit-tested anywhere in this repository; that boundary is deliberate (ImGui needs a live rendering context) and has held for every phase of this project so far. A new module should put as much of its logic as possible below that boundary — a service class with a well-defined public surface, not tangled into its operator panel's `Draw()` method — specifically so it *can* be tested.

Reasonable things to cover: config default/round-trip serialization, per-venue config isolation (two different venue IDs never see each other's payload), venue-switch behavior (`OnVenueChangedAsync` resets and reloads correctly), enable/disable not losing config, cancellation on dispose/venue-switch, and — for a backend module — typed-client request/response (de)serialization against the donor protocol. See `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` and the existing `*ClientTests.cs` files for the current style: a fake `I...Store`/`IClock`/`IObjectSnapshotProvider` implementation, compact single-line `[Fact]` methods.

**A module whose core function is inherently unsafe/FFXIVClientStructs/ECommons-dependent** (game addon manipulation, not an HTTP backend) — precedent: `promotion.partyfinder`'s reconstruction (`PARTY_FINDER_RECONSTRUCTION.md`) — should still separate as much as possible from that boundary: put the plain data model and orchestration/persistence logic in `VenueOS.Modules.Operations/<ModuleName>/` behind a small interface (e.g. `IPartyFinderAutomation`) that the unsafe engine implements; put only the actual unsafe/addon/ECommons code in `VenueOS.Plugin/<ModuleName>/`, implementing that interface. This keeps the orchestration layer (per-venue settings, guard conditions, chat-text parsing, etc.) unit-testable with a fake implementation of the interface standing in for the real game-dependent engine, even though the engine itself can only ever be verified in-game.

**State surviving a window close is not proof of persistence.** A module's config staying correct while its window is closed and reopened only proves the in-memory object didn't get replaced — it says nothing about whether `SaveModuleConfig` was ever actually called, or whether the save reaches durable storage correctly. The only test that actually exercises persistence is one that destroys and reconstructs the owning service/store from a serialized representation of what was saved (see `PartyFinderServiceTests.Every_preset_field_survives_a_full_serialize_deserialize_boundary` for the pattern: serialize the venue snapshot to a JSON string, deserialize it back into a brand new store/service, and assert every field). The equivalent live check is a full Dalamud plugin disable/re-enable (or a game restart), not merely closing and reopening a module's window — a module isn't done until it's been verified across that actual boundary, not the illusion of it.

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

```text
src/VenueOS.Modules.Operations/
  <ModuleName>/                       (new folder — only needed if backend-backed or the service is non-trivial)
    <ModuleName>Client.cs             (backend modules only: typed client + wire/protocol records)
  Operations.cs                       (existing core modules live here — do NOT add a new module's classes to
                                        this file; give your module its own file, e.g. GuestNotes.cs, per §21)

src/VenueOS.Plugin/
  <ModuleName>OperatorPanel.cs        (the Draw()/DrawSettings() content — its own file, per §21)
  Plugin.cs                           (composition root: construct the service, construct the panel, wrap in an
                                        IVenueModule, modules.Register(...) — nothing else needs editing for Home,
                                        embedded rendering, detached rendering, or Settings)

tests/VenueOS.Services.Tests/         (or VenueOS.Core.Tests/VenueOS.Venues.Tests as appropriate)
  <ModuleName>Tests.cs
```

No project file changes are needed to add a module — `VenueOS.Modules.Operations` and `VenueOS.Plugin` already reference everything required.

## 34. Backend-backed modules

For a module that talks to an external backend (matching the pattern of Raffle/Trivia/TournamentControl/Bingo):

- The backend server itself stays entirely separate from VenueOS; the donor repository is the protocol authority and is read-only.
- Add a typed client class next to the module (`Modules.Operations/<ModuleName>/<ModuleName>Client.cs`), holding wire/protocol records (requests/responses) distinct from any UI-facing type. See `VenueBingoClient.cs`/`VenueRaffleClient.cs`/`MairsTriviaClient.cs`/`TournamentControlClient.cs` for the current shape: a plain `HttpClient`-wrapping class with `async Task<XResult<T>>` methods, a `XResult<T>(bool Success, T? Value, string? Error, ...)` result record, and a static `Failed(...)` helper.
- Connection settings (server URL, credentials) are per-venue config (§11, §12) and belong in the module's Settings contribution (§8, §9) — not the operational screen.
- Handle venue switching explicitly: cancel in-flight requests (a `CancellationTokenSource`, cancelled and replaced in the module's `Load(nextVenueId)`/`OnVenueChangedAsync`, matching `VenueRaffleService.Load`/`MairsTriviaService.Load`/etc.), clear in-memory state, load the new venue's credentials, and never reuse the old venue's tokens/keys after switching.
- Route backend failures through `DiagnosticsService.RecordFailure` (§25); never surface a raw exception as the primary operational UI.
- If a module needs a distinct *player/browser-facing* display theme (Bingo's `BingoColors`: Bg/Card/Header/Text/Daub/Ball, on `VenueBingoSettings.Colors`), keep it as its own config field, separate from `VenueTheme` — the tablet theme and a module's external/web display theme are different concepts and must stay independently configurable, never merged. Bingo's split into "Server" and "Web Display" concerns is the existing precedent for how a complex module's Settings should be organized (currently, this split lives entirely inside the module's own `DrawSettings()`/operator panel — there's no separate Settings sub-navigation to build; a `Forms.Segmented` sub-tab inside the module's own settings content, matching how `SettingsScreen`'s own top-level categories are built, is the natural way to present it).

## 35. Responsive layout and the no-overlap rule

**No user-facing control may overlap another at any supported window size.** The pattern proven across the main toolbar, the Diagnostics toolbar, and the detached header is the same every time: reserve the region for critical controls first (compute their fixed total width, position them at an absolute screen coordinate derived only from the container's width — never from what secondary content happens to render), then give whatever's left to flexible/secondary content, and reflow or truncate the secondary content rather than letting it push into reserved space. Avoid chaining `ImGui.SameLine()` (no-argument) after a component that draws more than one line internally (e.g. `Forms.ComboField`'s label-then-field) — position the next column from a captured shared row-top instead. Use `ImGui.CalcTextSize` (with a `wrapWidth` argument for pre-wrap height estimation) to make layout decisions before drawing, and `ImGui.GetItemRectMax()` after drawing to get an item's *actual* rendered extent when you need it for placing what comes next.

## 36. Empty, loading, and error states

Use `UiKit.EmptyState` ("no data yet"), `UiKit.LoadingState`, `UiKit.WarningState`/`ErrorState` for concise, theme-colored operational messages — not a raw exception message, not silence. A "not configured" backend module should say so plainly (see `MairsTriviaOperatorPanel`'s "Create a game by supplying a validated .fftrivia question set..." empty state) rather than showing broken/blank controls.

## 37. Destructive actions

Use `ConfirmDialog` (`UiKit`) for anything that destroys persistent data or state the operator can't trivially redo (deleting a venue, deleting a VIP record — both existing examples). Don't over-confirm harmless, reversible actions (toggling a switch, running a preset).

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

## 39. New module planning template

```text
Module Name:
Stable Module ID:
Purpose:
Display Name:
Icon:
Operational Workflow:
Persistent Settings (Settings → Modules):
Per-Venue Settings:
Global Settings (rare — justify if any):
Shared Services Used:
Backend (if any):
Detached Support: (yes by default; note if genuinely not possible and why)
Auto Pop-Out Compatibility: (generic — confirm no special-casing was added)
Diagnostics: (what failures get reported, how)
Special Risks:
Automated Tests:
Manual QA:
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
    public void DrawSettings() => draw?.Invoke(); // TODO for a real module: separate persistent config here (§8)
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
- **Settings → Modules:** the module appears in the table automatically; "Configure" calls `GuestNotesModule.DrawSettings()`, which here just re-renders the same content as a placeholder (a real module should split this, per §8's noted gap).
- **Diagnostics:** none wired in this minimal example since it can't fail (no backend, no external state); a real module with a failure mode would take a `DiagnosticsService` constructor parameter and call `diagnostics.RecordFailure(...)` from `GuestNotesService` on any recoverable error, e.g. `diagnostics.RecordFailure($"tools.guestnotes: failed to save note ({ex.Message})");`.
- **Disposal:** `DisposeAsync() => ValueTask.CompletedTask` is correct here since the service owns no subscriptions, timers, or connections to release.

## 42. Definition of Done

**Identity**
- [ ] Stable module ID assigned, namespaced, never reused from another module
- [ ] Display name defined and correct everywhere it's read from (there's only one place — §4)
- [ ] Icon key registered in `AppIcons`, distinct from every other module's key

**Registration**
- [ ] `modules.Register(...)` added in `Plugin.cs`
- [ ] Home tile appears with correct icon/name
- [ ] Enable/disable toggle in Settings → Modules works and disables the tile/detached window correctly

**Configuration**
- [ ] `DrawSettings()` exists and (ideally) shows persistent configuration distinct from `Draw()`'s live operation
- [ ] Per-venue config isolated via `GetModuleConfig`/`SaveModuleConfig`, keyed by this module's own ID
- [ ] No module-specific "Venue Name" field — reads `venues.Current.DisplayName`
- [ ] A global setting was added only if it's genuinely venue-independent (§12)
- [ ] Schema version set; recoverable-payload path doesn't silently discard user data (§13)

**UI**
- [ ] `UiKit`/`Forms` components used; no raw `ImGui.Button`/`InputText`/`Checkbox`/`Combo` for user-facing controls
- [ ] Only semantic theme tokens used, no hard-coded colors
- [ ] Embedded view works via `AppFrame.DrawModule`
- [ ] Detached view works via `ModuleWindowManager`, identical content to embedded
- [ ] Detached header shows only icon/name (left) and Settings/Close (right) — no global controls
- [ ] Detached Settings gear routes to Settings → Modules → this module
- [ ] Detached Close only closes the detached window
- [ ] Auto Pop-Out off → embedded; on → detached, main tablet stays on Home, re-click focuses not duplicates
- [ ] Resizing works, no overlap at wide/normal/minimum sizes
- [ ] Dark, Light, Neon, Midnight all tested

**Lifecycle**
- [ ] Enable/disable preserves config
- [ ] Venue switching reloads config and resets in-memory state correctly
- [ ] Disposal releases every subscription/timer/connection the module owns
- [ ] Background work cancels on venue switch/disable/disposal as appropriate

**Diagnostics**
- [ ] Recoverable failures reported via `DiagnosticsService.RecordFailure`, attributable to this module
- [ ] Operational screen shows a concise state, not a raw exception
- [ ] No secret value appears in a diagnostic message outside `Redact`'s coverage

**Backend (if applicable)**
- [ ] Typed client, protocol models separate from UI
- [ ] Verified against the donor repository's actual current protocol
- [ ] Credentials are per-venue
- [ ] Venue switch cancels/tears down the old backend context before using the new one's credentials
- [ ] In-flight requests are cancellable

**Testing**
- [ ] Automated tests added for whatever is pure logic, in the appropriate `tests/VenueOS.*.Tests` project
- [ ] Per-venue isolation tested
- [ ] Global config behavior tested if a global setting was added
- [ ] `dotnet build VenueOS.sln` succeeds (Debug and Release)
- [ ] Manual in-game QA (§32) completed

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

1. Read this entire document before writing any code.
2. Inspect the actual current source for anything you're about to touch or extend — this guide is a snapshot, not a substitute for reading `Plugin.cs`, `ModuleHost`, `VenueProfileService`, and the UI kit files directly.
3. Reuse existing shared infrastructure (§27) rather than building a parallel version of it.
4. Never modify a donor/reference repository (listed at the top of this document) unless the user has explicitly authorized it for that specific task.
5. Implement both embedded and detached rendering as one shared `Draw()` — never two implementations.
6. Verify Auto Pop-Out works correctly in both states without any module-specific branching.
7. Give the module a real Settings contribution, distinct from its operational UI where the content genuinely differs (§9).
8. Read venue identity from `VenueProfileService.Current`, never a module-local field.
9. Build (`dotnet build VenueOS.sln`, Debug and Release) and run the existing test suite before considering the task done; add tests for new pure logic.
10. Walk the Definition of Done (§42) explicitly and report which items are satisfied.
11. If something in this guide doesn't match what you find in the repository, or a requirement genuinely can't be met (e.g., a module that truly cannot support detaching), say so explicitly rather than silently deviating or forcing a fit.

## 44. Relationship to `MODULE_DEVELOPMENT.md`

`MODULE_DEVELOPMENT.md` (repository root) is a short, older quick-reference predating the Settings/detached-window/Auto-Pop-Out architecture described here. It has been given a pointer to this document (see its top) rather than being rewritten or deleted — its core module-registration example is still broadly accurate, but it does not cover Home integration, detached windows, Settings contribution, theming, or Auto Pop-Out at all, and its mention of `VenueHttpClientFactory` as a service to use is inaccurate (§21 — that class currently has no callers). **This document, `NEW_MODULE_GUIDE.md`, is authoritative for anything the two disagree on or where `MODULE_DEVELOPMENT.md` is silent.**
