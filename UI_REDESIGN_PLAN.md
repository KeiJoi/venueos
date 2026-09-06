# VenueOS UI Reconstruction — Phase 1: Tablet-OS Design Architecture

## 1. Current VenueOS UI architecture

VenueOS today is a single `ImGui.Begin` window (`src/VenueOS.Plugin/Plugin.cs:Draw`) with a hard-coded header row, a fixed-width left `BeginChild` navigation list, and a right content pane. Structure:

- **`VenueOS.UI` project** (`VenueUi.cs`) is a *style-token* layer only — it has no ImGui dependency. `VenueUi` maps `VenueTheme.Tokens` to a small `UiStyle` record (background/foreground/rounding/padding) for four semantic roles: `Button`, `Card`, `StatusBadge`, `NavItem`. `VenueShell` holds only `SelectedModuleId` (null = nothing selected) and exposes the module list for navigation.
- **`VenueOS.Plugin` project** does all real ImGui drawing:
  - `Plugin.Draw()` — pushes 4 style colors + 2 style vars from the active theme, draws a text banner (`VENUEOS` / `LIVE OPERATIONS`), a right-aligned venue combo, a "New venue" button, two hard-coded theme buttons (`Dark`, `Neon` only — `Light`/`Midnight` are unreachable from the UI), a `Separator`, then a 210px-wide `Selectable` list of modules, then a content child that shows either `VenueOperationsDashboard` (when nothing is selected) or the selected module's `Draw()`.
  - `VenueOperationsDashboard.cs` — a flat list of `TextUnformatted` status lines for every module plus recent diagnostics errors.
  - `NativeOperationsPanels.cs`, `MairsTriviaOperatorPanel.cs`, `TournamentControlOperatorPanel.cs`, `VenueBingoOperatorPanel.cs`, `VenuePartyFinderAdapter.cs` — one operator panel per module, each an unstyled sequence of `TextUnformatted`/`InputText`/`Button`/`BulletText` calls.
  - `VenueVisuals.cs` — a tiny, mostly-unused helper (`Color`, `BeginCard`/`EndCard`, `Status`) with no callers outside itself.
- Module identity already carries a semantic icon key (`ModuleDescriptor.Icon`, e.g. `"users"`, `"star"`, `"megaphone"`) that nothing currently renders — it exists in the data model but has no visual representation today.
- There is no Home screen, no app grid, no per-app frame, and no Settings application. Theme switching, venue creation, and navigation are all crammed into one header row.

## 2. Problems with the current UI

- **No spatial hierarchy.** Everything (branding, venue switcher, theme buttons, nav list, content, toasts) lives in one undifferentiated window — it reads as a debug panel, not a product.
- **Dead-end module list.** A permanent 210px `Selectable` sidebar is exactly the "giant sidebar" pattern the brief asks to avoid, and it doesn't scale — Bingo/Trivia/Tournament will need much more horizontal room than this leaves.
- **No icon system.** `ModuleDescriptor.Icon` is unused; every module is identified by a text label only.
- **Incomplete theme access.** Only 2 of 4 built-in themes are reachable, and there is no per-venue visual identity beyond raw color swaps — no branding surface, no way to tell venues apart at a glance beyond the combo text.
- **No Settings surface.** Venue rename/duplicate/delete already exist in `VenueProfileService` but have *zero* UI. Diagnostics (`DiagnosticsService`) is only ever shown as three lines buried at the bottom of the dashboard.
- **Unstyled controls.** Every operator panel uses raw `ImGui.Button`/`InputText` with no card grouping, no spacing rhythm, no hover/active affordance beyond ImGui's stock look.
- **No reusable component library.** Every panel reimplements its own layout by hand; there's no shared `Card`, `IconButton`, `StatusBadge`, etc. that new modules (Bingo/Trivia/Tournament operator consoles) can build on.

## 3. Proposed tablet-OS architecture

Keep the existing project boundaries (`VenueOS.UI` = theme/state, `VenueOS.Plugin` = ImGui rendering) — this separation is already correct and lets `VenueOS.UI` stay engine-agnostic and unit-testable. Add one rendering layer inside `VenueOS.Plugin`:

```
VenueOS.UI/                     (unchanged responsibility: theme tokens + shell navigation state)
  VenueUi.cs                    + Home/Settings navigation sentinel on VenueShell (additive)

VenueOS.Plugin/
  Shell/
    UiKit.cs                    Card, PrimaryButton/GhostButton, IconButton, StatusBadge,
                                 SectionHeader, Divider, Toggle, ListRow, Toolbar, EmptyState,
                                 Tooltip, ConfirmDialog  — the reusable component library
    Icons.cs                    Procedural vector icon painter, keyed by ModuleDescriptor.Icon
    AppTile.cs                  Home-screen application icon (hover/press/disabled/badge states)
    TabletHeader.cs             Persistent status/header bar
    HomeScreen.cs                Venue identity banner + responsive app grid + overview card
    AppFrame.cs                  Per-application frame: Home button, breadcrumb, content slot
    SettingsScreen.cs           Venue management (rename/create/duplicate/delete) + theme picker
                                 + diagnostics, replacing the header's ad-hoc controls
  Plugin.cs                     Composition only: theme push, window, header, Home/App routing
  VenueOperationsDashboard.cs   Unchanged content, now rendered inside a Home-screen card
  NativeOperationsPanels.cs,
  MairsTriviaOperatorPanel.cs,
  TournamentControlOperatorPanel.cs,
  VenueBingoOperatorPanel.cs,
  VenuePartyFinderAdapter.cs    Unchanged this phase — every existing control is preserved verbatim
```

`VenueVisuals.cs` is superseded by `Shell/UiKit.cs` (its two callers-in-waiting were never wired up) and removed.

## 4. Home-screen design

- **Venue identity banner** at the top: the venue's display name at large type size, the active theme name, and a two-color gradient strip (`Primary` → `Accent`, drawn with `AddRectFilledMultiColor`) standing in for the venue's branding until image/logo loading exists (see §10).
- **Overview card**: the existing `VenueOperationsDashboard` content, unchanged, inside a `Card` — preserves every line of current status information (session state, per-module status, recent diagnostics errors) without deleting it, just re-homing it (per the "don't silently drop functionality" rule).
- **App grid**: one `AppTile` per registered module (in `ModuleHost.Modules` order) plus a trailing `Settings` tile. Each tile shows the module's procedural icon, display name, a small status/error badge dot when `DiagnosticsService` has a recent failure attributable to it, and is visibly disabled (desaturated, non-interactive) when `IsEnabled == false`. Party Finder's icon key changes from the generic `"list"` to `"search"` (a one-line data tweak in `ModuleDescriptor`, not a logic change) to match the brief's magnifying-glass icon.
- **Responsive reflow**: the grid computes `columns = max(1, floor(availableWidth / (tileWidth + spacing)))` every frame from `ImGui.GetContentRegionAvail().X`, so resizing the window reflows tiles instead of scaling them.

## 5. Application-frame design

A single `AppFrame` wraps every module and the Settings screen:

- Top bar: a `Home` icon button (always returns to the Home screen), a small app icon + app name breadcrumb (`VenueOS · <Venue> · <App>`), and the module's one-line description as a `TextDisabled` subtitle.
- Below the top bar, a `Divider`, then a scrollable `BeginChild` content region that hosts the module's existing `Draw()` call unmodified.
- The frame imposes no internal column/layout constraints on module content — it is a chrome-only wrapper, so Bingo/Trivia/Tournament can later grow into multi-column operator consoles, tables, or queues without fighting the frame.
- Per-module `DrawSettings()` is intentionally *not* separately exposed yet: every current module implementation routes `DrawSettings()` to the same callback as `Draw()`, so a second "Settings" tab per app would currently show identical content and mislead operators. This is flagged in §13 as next-pass work rather than faked in this pass.

## 6. Navigation model

- `VenueShell` (in `VenueOS.UI`, additive change only) gains two navigation states on top of the existing `SelectedModuleId`: Home (`SelectedModuleId is null`, unchanged) and Settings (a private sentinel id, exposed as `IsSettingsSelected` / `SelectSettings()` / `SelectHome()`). This keeps navigation state in the engine-agnostic project and rendering entirely in `VenueOS.Plugin`.
- The persistent `TabletHeader` always shows a `Home` icon button and a `Settings` icon button, so both are reachable from anywhere (Home, any app, or Settings itself).
- A compact venue combo + "New venue" quick action stays in the header for fast switching mid-session; full venue lifecycle management (rename, duplicate, delete-with-confirmation) moves into the Settings app, which is where the brief's "switch venue where appropriate" and "access module settings where appropriate" guidance points for anything heavier than a quick switch.
- No permanent sidebar remains. The 210px `Selectable` nav list is removed; the app grid + Home button + header combo fully replace it.

## 7. Component system

New `Shell/UiKit.cs` in `VenueOS.Plugin` (styled purely from `VenueTheme.Tokens`/`ThemeMetrics`, never hard-coded colors):

| Component | Status this phase |
|---|---|
| `Card` | Implemented (supersedes `VenueVisuals.BeginCard/EndCard`) |
| `PrimaryButton` / `GhostButton` | Implemented |
| `IconButton` | Implemented (drives Home/Settings/header actions) |
| `StatusBadge` | Implemented (module tile badges, connection indicator) |
| `SectionHeader` | Implemented |
| `Divider` | Implemented |
| `Toggle` | Implemented (styled checkbox wrapper) |
| `ListRow` | Implemented (venue list rows in Settings) |
| `Toolbar` | Implemented (header action row layout helper) |
| `EmptyState` | Implemented (e.g. "no venues yet" — defensive, not currently reachable) |
| `Tooltip` | Implemented (thin `IsItemHovered`/`SetTooltip` wrapper) |
| `ConfirmationDialog` | Implemented as `ConfirmDialog` (gates venue deletion) |
| `AppIcon` / `AppBadge` | Implemented as `AppTile` (icon + badge combined; badge is a property, not a separate draw call, since it never appears without its tile) |
| `TabletHeader` | Implemented |
| `AppHeader` | Implemented as part of `AppFrame`'s top bar |
| `TextInput` / `NumericInput` / `ComboBox` / `SearchBox` / `Table` / `Tabs` / `Modal` / `Toast` / `ErrorState` / `LoadingState` / `HotbarButton` | **Deferred.** These are needed for the operator-panel redesign (module content), which this phase explicitly excludes. Module panels keep using stock ImGui `InputText`/`Combo`/etc. this phase; only the shell chrome (header, home, frame, settings) is restyled. `Toast` keeps its current rendering (a themed line per notification) but moves from an ad-hoc footer loop into a small `Toast`-labelled helper in `UiKit.cs` so the next pass can extend it without touching `Plugin.cs`. |

## 8. Icon strategy

No bitmap/texture pipeline exists in this repo today (no `ISharedImmediateTexture` usage, no embedded image resources, no icon-font wiring), and adding one is out of scope for a visual-architecture pass. Unicode emoji are explicitly disallowed as primary icons. The chosen approach: **procedural vector icons**, drawn directly with `ImGui.GetWindowDrawList()` primitives (`AddCircle`, `AddLine`, `AddTriangleFilled`, `AddRect`, `AddNgon`) inside `Shell/Icons.cs`, keyed off the icon string already present on `ModuleDescriptor.Icon` (`"users"`, `"message"`, `"star"`, `"megaphone"`, `"search"`, `"ticket"`, `"circle-question"`, `"trophy"`, `"grid"`, plus a new `"gear"` for the Settings app). This is dependency-free, theme-colorable (icons are drawn in `TextPrimary`/`Accent`, never a fixed color), resolution-independent, and technically appropriate for a Dalamud ImGui plugin with no asset pipeline. A follow-up pass can replace individual glyphs with bitmap/SVG art without changing any call site, since every consumer goes through `AppIcons.Draw(iconKey, ...)`.

## 9. Theme integration

Unchanged theming *contract* — `VenueTheme.Tokens`/`ThemeMetrics` remain the only source of color/spacing, and `VenueProfileService.SetTheme` remains the only mutator. What changes is *coverage*: all four built-in themes (`Dark`, `Light`, `Neon`, `Midnight`) become reachable as swatches in the new Settings screen (today only `Dark`/`Neon` were wired to buttons), and every new component in `UiKit.cs`/`Icons.cs` reads its colors from `theme.Tokens` exclusively — no hard-coded hex anywhere in the new shell code, matching the existing (and preserved) rule in `Plugin.Draw()`'s theme-push block.

## 10. Venue branding

`VenueBranding` (`LogoPath`, `BackgroundImagePath`, `BackgroundOpacity`) already exists on `VenueTheme` but nothing loads or renders it, because no texture-loading path exists in the plugin yet (`IDalamudPluginInterface` texture provider is not currently referenced anywhere in the repo). Implementing real image loading is a meaningful new capability, not a visual-architecture change, so this phase does not add it — doing so silently would risk build/runtime issues with no way to validate them without a live game session. Instead: the Home-screen banner uses the venue's own `Primary`/`Accent` tokens as a gradient stand-in for branding, and `VenueBranding` is left completely untouched so a future pass can wire real logo/background rendering into the same banner slot without any shell rework. This gap is called out explicitly in §13.

## 11. Responsive behavior

- `Plugin.Draw()` now calls `ImGui.SetNextWindowSizeConstraints` with a sensible minimum (900×560, matching the existing `FirstUseEver` default of 980×650 scaled down modestly) so the window cannot be resized into an unusable state.
- The Home-screen app grid recomputes its column count every frame from available content width (see §4) rather than fixing a column count or scaling tiles.
- The `AppFrame` content region uses `ImGui.GetContentRegionAvail()` for its child size (as the content pane already does today), so module content — however wide or tall it grows in future passes — always gets full available space, not a fixed box.
- Nothing scales font size or DPI-scales the whole interface; only layout reflows, per the brief's explicit instruction.

## 12. Files/classes expected to change

**New:**
- `src/VenueOS.Plugin/Shell/UiKit.cs`
- `src/VenueOS.Plugin/Shell/Icons.cs`
- `src/VenueOS.Plugin/Shell/AppTile.cs`
- `src/VenueOS.Plugin/Shell/TabletHeader.cs`
- `src/VenueOS.Plugin/Shell/HomeScreen.cs`
- `src/VenueOS.Plugin/Shell/AppFrame.cs`
- `src/VenueOS.Plugin/Shell/SettingsScreen.cs`

**Changed:**
- `src/VenueOS.Plugin/Plugin.cs` — `Draw()` rewritten to compose the new shell; module wiring, service construction, and lifecycle (`Update`, `Dispose`) untouched.
- `src/VenueOS.UI/VenueUi.cs` — `VenueShell` gains Home/Settings navigation state (additive).
- `src/VenueOS.Modules.Operations/Operations.cs` — one-line icon key fix for `PartyFinderGatedModule` (`"list"` → `"search"`).

**Removed:**
- `src/VenueOS.Plugin/VenueVisuals.cs` — superseded by `Shell/UiKit.cs`; had no external callers.

**Untouched (by design):** every operator panel (`NativeOperationsPanels.cs`, `MairsTriviaOperatorPanel.cs`, `TournamentControlOperatorPanel.cs`, `VenueBingoOperatorPanel.cs`, `VenuePartyFinderAdapter.cs`), `VenueOperationsDashboard.cs`'s content, all of `VenueOS.Core`, `VenueOS.Venues`, `VenueOS.Services`, `VenueOS.Modules.Operations` (aside from the one icon-key literal), and every backend protocol client.

## 13. Architectural changes required / follow-up

- No solution/project-reference changes are required — `Shell/*` lives inside the existing `VenueOS.Plugin` project, which already references `Dalamud.Bindings.ImGui`.
- No persisted-schema changes: `VenueStoreSnapshot`, `VenueTheme`, `ModulePayload`, etc. are untouched, so existing saved configs load exactly as before.
- Left for the next UI pass (explicitly out of scope here, called out per the brief's "document it" rule rather than silently dropped):
  1. Real venue logo/background image rendering (needs a Dalamud texture-loading path — see §10).
  2. A distinct `DrawSettings()` view per module (currently identical to `Draw()` in every module; needs module-level work, not shell work).
  3. Restyled *module content* (operator panels) — `TextInput`/`NumericInput`/`ComboBox`/`SearchBox`/`Table`/`Tabs`/`Modal`/`ErrorState`/`LoadingState`/`HotbarButton` from §7's deferred list, needed once individual modules (especially Bingo/Trivia/Tournament) are redesigned.
  4. The separate functional-gap audit mentioned in the brief (standalone-plugin controls not yet carried into VenueOS) is unaffected by this pass and remains fully future work.
