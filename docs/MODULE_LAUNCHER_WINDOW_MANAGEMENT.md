# Module Launcher & Window Management

**Status: LIVE QA PASSED.** Released as part of VenueOS 0.3.7.

Implementation report for the Module Launcher + shared module window presentation-state pass, built against the
approved plan (`piped-stargazing-starlight.md`).

## 0. Live QA results

The user verified the following in-game, in Dalamud:

- Icon-only launcher presentation.
- Module-name hover tooltip on each launcher icon.
- Compact launcher layout.
- Detached module window Collapse/Expand.
- Detached-window independent positioning.
- Main VenueOS tablet Collapse/Expand.
- The main tablet operating independently from detached windows.
- The launcher remaining usable throughout.
- General visual/functionality acceptance of the pass as a whole.

This covers the general presentation and window-management behavior described throughout this document. The
exhaustive per-item checklists in §16 and §22.6 below are kept as the detailed implementation-time test plan this
pass was built against; they are not individually re-itemized as separately confirmed beyond the summary above.

## 1. Executive summary

VenueOS previously had exactly one detached-window concept: a module's window was either open or closed
(`ModuleWindowManager`'s `HashSet<string>`), with no minimize/collapse, no quick-access hotbar independent of the
main tablet, and no persisted window geometry across a reload. This pass adds:

- A **Module Launcher** — a small, always-available hotbar (`ModuleLauncherWindow`) that opens/focuses/restores any
  eligible module's detached window, independent of whether the main tablet is open.
- A shared **Expanded / Collapsed / Hidden** presentation-state system for every normal module's detached window,
  with Collapse (compact header-only strip), Minimize (hide while continuing to run), and Restore, alongside the
  existing Expand/Close.
- **Persisted window geometry and Launcher configuration**, global (not per-venue), read/written through
  `GlobalSettingsService`.

This was built as pure UI infrastructure. It never touches module/service operational lifecycle — Party Finder,
ShoutRunner, Mair's Trivia, and every other module keep running exactly as before while their window is Collapsed or
Hidden. No Cleanup-All-Interfaces visual redesign and no Community Presets were started; both remain explicitly out
of scope for this pass.

## 2. Existing architecture discovered (before this pass)

- `src/VenueOS.Plugin/Shell/ModuleWindowManager.cs` mixed state (`HashSet<string> open`, `HashSet<string>
  focusRequested`) and ImGui rendering (`DrawAll`/`DrawWindow`) in one class, with only two states per module: open
  or closed.
- `src/VenueOS.Plugin/Shell/ModuleWindowHeader.cs` drew a fixed two-button chrome (Settings gear, Close) shared by
  every detached window and by four auxiliary hand-rolled popouts (`BingoCalledNumbersWindow`,
  `BingoPlayerCardViewerWindow`, `BingoCallAlertWindow`, `GiveawaysTrackerWindow`).
- `src/VenueOS.Services/GlobalSettingsService.cs` held exactly one global preference, `AutoPopOutModules`, plus
  `ModuleEnabledOverrides` — the established, and previously only, home for app-wide UI preferences independent of
  the active venue.
- No test project exists for `VenueOS.Plugin` (ImGui code is never unit-tested in this repository, by established,
  deliberate convention — see NEW_MODULE_GUIDE.md §30) — so all new pure logic went into `VenueOS.Core`, which had
  no dependency on ImGui, `VenueOS.Services`, or `VenueOS.Plugin` before this pass (and still doesn't).

## 3. Window inventory & normal-vs-special classification

**Participate** (normal — hosted through the generic `AppFrame`/`ModuleWindowManager` path; gets Launcher +
Collapse/Hide support automatically, zero content changes, since none of them own their own `ImGui.Begin`): Party
Finder, ShoutRunner, Macro (main panel only), Mair's Trivia, Giveaways (main panel only), Bingo (main panel only),
Raffle, Shouts, Attendance, Greeter, VIP, TournamentControl, Mair's Editor.

**Opt out** (unchanged — already independent, hand-rolled `IsOpen`-pattern popouts, not registered with
`ModuleWindowManager`, and staying that way per the plan's explicit boundary): `BingoCalledNumbersWindow`,
`BingoPlayerCardViewerWindow`, `BingoCallAlertWindow`, `GiveawaysTrackerWindow`, `MacroEditorWindow`,
`GiveawayPresetEditorModal`, `ShoutPresetEditorModal`. No launcher entries, no collapse/hide. The four that share
`ModuleWindowHeader` for chrome consistency (the three Bingo windows and the Giveaways tracker) were updated only to
pass a stable literal id for their button-id strings (the header's signature changed) — their behavior is otherwise
byte-for-byte identical to before (still exactly Settings gear + Close, no collapse/minimize).

**Macro's faux hotbars** (`MacroHotbarRenderer.cs`) — explicitly out of scope; zero changes made to this file. They
remain HUD overlays independent of the Macro module's own window lifecycle, driven by their own per-hotbar `Enabled`
setting, rendered directly from `Plugin.Draw` exactly as before.

## 4. The shared state model and API

New enum + pure state machine, in `VenueOS.Core` (ImGui-free, unit-testable):

```csharp
public enum ModulePresentationState { Expanded, Collapsed, Hidden }
public sealed record ModuleWindowPreference(ModulePresentationState LastState, float PosX, float PosY, float Width, float Height);

public sealed class ModuleWindowManager // VenueOS.Core
{
    public bool Open(string moduleId, ModuleWindowPreference? preference = null); // returns true only on a fresh open
    public ModuleWindowPreference? Evict(string moduleId);                        // Close or disabled-eviction
    public void Collapse(string moduleId);
    public void Expand(string moduleId);
    public void Hide(string moduleId);     // remembers PreHideState internally
    public void Restore(string moduleId);  // returns to whichever of Expanded/Collapsed it was
    public void RequestFocus(string moduleId);
    public bool ConsumeFocusRequest(string moduleId);
    public bool IsOpen(string moduleId);
    public ModulePresentationState? GetState(string moduleId);
    public void ReportPosition(string moduleId, float x, float y);        // every frame, any state
    public void ReportExpandedSize(string moduleId, float w, float h);    // only while Expanded
    public ModuleWindowPreference? GetSnapshot(string moduleId);
    public IReadOnlyList<string> OpenModuleIds { get; }
}
```

A module id absent from the tracked set means **Closed** — this matches the original `HashSet<string>` semantics
exactly; there is no separate `IsClosed` bool. `ModuleWindowPreference.LastState` is always `Expanded` or
`Collapsed`, **never `Hidden`** — `GetSnapshot`/`Evict` automatically report whichever state the module was showing
right before it was hidden (`PreHideState`), since Hidden is never a valid "opening default."

**Layer 2**, `VenueOS.Plugin.Shell.ModuleWindowManager` — deliberately the same class name, in a different
namespace, so every existing call site (`windowManager.Open(id)` in `HomeScreen.LaunchModule`, `AppFrame.DrawModule`,
`Plugin.cs`) stayed valid unchanged. It wraps one `VenueOS.Core.ModuleWindowManager` instance with the actual
`ImGui.Begin`/`End` calls and `GlobalSettingsService` persistence. Per open module id, every frame:

- `Hidden` → skipped with **no `ImGui.Begin` call at all**.
- `Collapsed` → `ImGui.SetNextWindowSize(compactSize, ImGuiCond.Always)` plus `NoResize`, draws only
  `ModuleWindowHeader` (icon + name, Expand, Minimize, Close — no module content), remains draggable via the
  header's own drag handle.
- `Expanded` → today's full `ImGui.Begin` + content, plus continuous `ReportPosition`/`ReportExpandedSize`.
- On the single frame a Collapsed window transitions to Expanded (or a fresh `Open` seeds from a persisted
  preference), position + the remembered expanded size are forced once (`ImGuiCond.Always`), then ImGui resumes
  normal free move/resize for every subsequent frame — verified by `ModuleWindowManagerTests`
  `.Moving_a_collapsed_window_then_expanding_uses_the_new_position_with_the_old_expanded_size`.

**Disabled-module eviction** keeps today's rule (a disabled module's detached window is force-closed every frame
it's disabled) but now also preserves its remembered geometry: `DrawAll` calls `Evict` (not a plain remove) and
persists the returned snapshot to `GlobalSettingsService` before discarding the in-memory entry.

## 5. Header controls: Collapse/Expand, Minimize, and how they differ from Close

`Shell/ModuleWindowHeader.cs`'s `Draw` method grew a right-hand control cluster, built generically as a list of
`(id, icon, tooltip, action)` tuples so the two call shapes (full detached-module chrome vs. the simpler opt-out
popout chrome) share one implementation:

- **Expanded**: Collapse, Minimize, Settings, Close (4 buttons).
- **Collapsed**: Expand, Minimize, Close (3 buttons) — **no Settings gear**, since a collapsed header draws no
  module content and there is nothing to configure from it.
- **An opt-out auxiliary popout** (passing no `onToggleCollapse`/`onMinimize`): Settings, Close only — byte-identical
  to the pre-pass behavior.

Every control's id is built from the module's stable `Descriptor.Id`, never the display name
(`##detached-collapse-{moduleId}`, `##detached-minimize-{moduleId}`, etc.), matching the existing
`###venueos-detached-{moduleId}` window-id convention.

**Close vs. Minimize — a genuinely new, additional control, not a redefinition of existing Close semantics.** Close
evicts the module from the tracked set entirely (today's exact behavior, unchanged: the module can be re-detached
immediately after, its config/state untouched). Minimize (Hide) keeps the module tracked with `State = Hidden` so
the Launcher can restore it later and its remembered position/size stays live in memory the whole time it's hidden.

## 6. Launcher architecture, metadata, persistence, and the global-vs-per-venue decision

`ModuleLauncherWindow.Draw` is a small, always-available `ImGui.Begin` window (`###venueos-launcher`), drawn
unconditionally every frame from `Plugin.Draw`, gated only on `sessionGate.CanRenderGeneralUi` (the exact same
central gate every other VenueOS window already uses) and on `GlobalSettings.Launcher.Enabled`. It is explicitly
ambient/persistent, never opened/closed like the tablet.

**Eligibility and ordering** are pure, unit-tested functions in `VenueOS.Core.LauncherEntries`:

- `FullOrder(allModuleIds, moduleOrder)` — ids explicitly present in the persisted `ModuleOrder` come first, in that
  order; any remaining id (a brand-new or never-reordered module) lands at its natural `ModuleHost.Modules` display
  position rather than at the end; a stale id no longer present in the current module list is silently skipped.
- `Eligible(modules, showOnLauncher, moduleOrder)` — `FullOrder`, filtered to enabled modules whose explicit
  `ShowOnLauncher[id]` isn't `false`. **Absence of an entry means shown** — default-on/opt-out, so a fresh launcher
  configuration shows every eligible module immediately. A disabled module is never shown regardless of its
  `ShowOnLauncher` value, matching `HomeScreen.DrawGrid`'s existing precedent of fully omitting disabled modules'
  tiles. An explicit hide/show preference, once recorded, survives disable/re-enable and reload —
  `GlobalSettingsServiceTests.Show_on_launcher_preference_survives_a_reload_regardless_of_the_modules_enabled_state`
  proves this the same way `ModuleEnabledOverrides`' own preservation guarantee is already proven.

**Persistence** — `GlobalSettings` (Services) gained two new members, matching the plan's shape exactly:

```csharp
public sealed record GlobalSettings(
    bool AutoPopOutModules = false,
    IReadOnlyDictionary<string, bool>? ModuleEnabledOverrides = null,
    LauncherSettings? Launcher = null,
    IReadOnlyDictionary<string, ModuleWindowPreference>? ModuleWindowPreferences = null);

public sealed record LauncherSettings(
    bool Enabled = true, float PositionX = 24, float PositionY = 24, float Width = 360, float Height = 72,
    float Scale = 1.0f, int ButtonsPerRow = 6, bool Locked = true, bool CompactIconOnly = false,
    IReadOnlyList<string>? ModuleOrder = null, IReadOnlyDictionary<string, bool>? ShowOnLauncher = null);
```

Both are plain POCO records — they serialize fine through Dalamud's config round-trip (unlike the `JsonElement`
corruption documented elsewhere on `ModulePayload`; nothing here is a raw `JsonElement`), proven by
`GlobalSettingsServiceTests.Launcher_and_module_window_preferences_survive_a_full_serialize_deserialize_boundary`,
which serializes the whole `GlobalSettings` object to a JSON string and rebuilds a fresh `GlobalSettingsService` from
that string alone — the same pattern `PartyFinderServiceTests` already established for the venue store.

**Global, not per-venue** — confirmed by architecture and consistent with the existing "Auto Pop-Out Modules"
precedent (§12 of NEW_MODULE_GUIDE.md): the Launcher and per-module window geometry are preferences about how the
VenueOS *application* behaves, not venue data. `GlobalSettingsService` has zero reference to `VenueProfileService` or
any venue type, so switching the active venue never reads, writes, or resets them.

## 7. Buttons Per Row, ordering, lock, resize, and reset behavior

- **Buttons Per Row** (`VenueOS.Core.LauncherLayout`, mirroring `ShoutsLiveLayout`'s pattern exactly but
  deliberately independent of it — `ShoutsLiveLayout.MaxSlotsPerRow` was not touched and stays fixed at 5) is
  authoritative and independent of pixel size: `ContinuesRow`/`RowSizes` are pure row-wrapping math, tested for
  1 → single column, a mid-value → wraps, and ≥ count → one row
  (`LauncherLayoutTests`). Resizing the launcher window changes available pixel width/height only; if the window is
  narrower than the buttons need at the configured `ButtonsPerRow`, the button row scrolls horizontally
  (`ImGuiWindowFlags.HorizontalScrollbar`) rather than silently changing the column count.
- **Ordering** — up/down buttons on `LauncherSettingsPage`, not in-launcher drag-reorder. The one existing
  drag/drop precedent in this codebase, `MacroDragDrop`, is a fixed id-onto-slot payload (assigning a macro to a
  specific hotbar slot) and is not shaped for list reordering; building a second, different drag/drop mechanism for
  this one page was judged not worth the risk of a fragile, one-off interaction versus two reliable icon buttons.
- **Lock/Edit** — Locked (the default) passes `ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize` and forces
  position/size from `GlobalSettings.Launcher` every frame (`ImGuiCond.Always`) — the same technique used for a
  Collapsed module window's own fixed size, so it can never drift. Unlocked removes both flags for genuinely free
  native drag/resize (the identical mechanism every other module window already uses — no new ImGui capability
  needed), reading `ImGui.GetWindowPos()`/`GetWindowSize()` back into `GlobalSettings.Launcher` every frame they
  actually differ from the last persisted value (not an unconditional write every single frame, which would spam
  Dalamud's `SavePluginConfig` while idle-but-unlocked).
- **Scale** is a separate, secondary density control — `ImGui.SetWindowFontScale` plus explicit icon/button-size
  scaling in `ModuleLauncherWindow`'s own draw code — independent of `Width`/`Height`: resizing the window never
  changes `Scale`, and changing `Scale` never changes `Width`/`Height`. Proven by
  `GlobalSettingsServiceTests.Reset_launcher_position_resets_only_position_size_and_scale`, which also confirms
  `ButtonsPerRow`/`Locked`/`CompactIconOnly` are untouched by a reset.
- **Reset Launcher Position** (`LauncherSettingsPage`) resets position, size, and scale together in one action,
  computed from `ImGui.GetMainViewport()` at click time — never `ButtonsPerRow`, `Locked`, `CompactIconOnly`,
  `ModuleOrder`, or `ShowOnLauncher`. No general offscreen-detection subsystem was built; this is exactly the manual
  recovery action the plan scoped it as.

## 8. `/venueos launcher` behavior

Extends the existing `OnVenueOsCommand` regex-based handler (already extended once for `/venueos macro "Name"`) with
a new branch: `trimmed.Equals("launcher", ...)` toggles `GlobalSettings.Launcher.Enabled`. When turning it on, it
also applies the same offscreen-safe default-position fallback "Reset Launcher Position" uses
(`LauncherLayout.LooksOffscreen`, a generous-bounds NaN/Infinity/wildly-out-of-range check) if the stored position
looks invalid — this command runs **outside** an ImGui frame (it's a chat command handler), so it can't call
`ImGui.GetMainViewport()` the way the in-frame Reset button does; it falls back to `LauncherSettings`'s own record
defaults instead. Bare `/venueos` and `/venueos macro "..."` are unaffected — verified by inspection (the new branch
is checked only after the macro-pattern match fails, and falls through to the existing
`RequestOpenAndFocusTablet()` for every other input, exactly as before). Help text was updated to mention both
subcommands.

## 9. Operational/presentation separation and how it's enforced

This is the single hardest constraint in the plan — Party Finder, ShoutRunner, Mair's Trivia, and every other
module's actual operation must be completely unaffected by Collapse/Hide/Minimize. It's enforced structurally, not
just by convention:

- `VenueOS.Core.ModuleWindowManager` (the pure state machine) has a **zero-parameter constructor** and no field of
  any type outside `System`/its own enum/record — it is structurally incapable of holding a reference to
  `IVenueModule`, a service, or anything operational. `ModuleWindowManagerTests
  .The_manager_takes_no_dependency_on_any_module_or_service_by_construction` asserts this via reflection.
  `.Collapse_hide_restore_and_evict_touch_only_this_modules_own_tracked_entry` runs every transition on one module
  and confirms a second, untouched module's own snapshot never moves as a side effect.
- `Shell.ModuleWindowManager.DrawWindow` calls `module.Draw()` **only** inside the `Expanded` branch. A Hidden
  module skips `ImGui.Begin` entirely; a Collapsed module draws only the header. Neither branch calls anything on
  `module` besides reading `Descriptor.Icon`/`Descriptor.DisplayName` (identity, not operation) and, while Expanded,
  `module.Draw()` itself.
- `ModuleHost.Tick` (the module's actual periodic work — Party Finder's refresh cadence, ShoutRunner's automation,
  Trivia's game loop) is driven entirely by `Plugin.Update`/`ModuleHost.Tick(now)`, which has no dependency on
  presentation state at all — it ticks every enabled module regardless of whether its window is Expanded, Collapsed,
  Hidden, or Closed. Collapsing/hiding a window never disables a module and never touches `IVenueModule.IsEnabled`.
- ShoutRunner's existing travel-exception gate (`SessionPresentationGateService.CanRenderShoutRunnerUi`,
  `Plugin.cs`'s `canRenderShoutRunner`) is layered **underneath** presentation state, completely unmodified: the
  gate predicate passed into `DrawAll` decides whether ShoutRunner's window is even considered for drawing *this
  frame at all* (independent of Expanded/Collapsed/Hidden); a Hidden ShoutRunner window still just means "skip
  `ImGui.Begin`," which never touches the gate, `ShoutRunnerService.IsActive`, or the automation service.

## 10. Module enable/disable reconciliation

Disabling a module while its detached window is open now evicts it (`Evict`, not a plain remove) and persists the
returned snapshot as its `ModuleWindowPreference` before discarding the in-memory Core entry — so a module that was
Collapsed at position (X, Y) when disabled reopens Collapsed, at (X, Y), when re-enabled and re-opened, mirroring
the identical preservation principle `GlobalSettingsService.SetModuleEnabled`/`GetModuleEnabledOverride` already
established for the enable/disable toggle itself, now applied to window geometry too. The module's
`ShowOnLauncher`/`ModuleOrder` launcher preferences are keyed by module id independently of enabled state and are
never touched by disable/re-enable at all (they simply aren't consulted while the module is excluded from the
enabled set) — proven by
`LauncherEntriesTests.A_disabled_module_disappears_from_eligible_entries_but_stale_show_preference_is_harmless`.

## 11. Party Finder, Mair's Trivia, Macro, ShoutRunner — confirmed preserved

- **Party Finder** (`PartyFinderService.cs`, `PartyFinderAutomationService.cs`, `PartyFinderOperatorPanel.cs`,
  `PartyFinderServiceTests.cs`) — the pre-existing, reviewed-but-uncommitted reliability hardening diff from before
  this pass is **byte-for-byte unchanged**. Diff sizes before and after this pass are identical:
  `PartyFinderService.cs` (+24/-…), `PartyFinderAutomationService.cs` (+71/-…), `PartyFinderOperatorPanel.cs`
  (+7/-…), `PartyFinderServiceTests.cs` (+74 new lines). See §16 below for the exact confirmation. `Plugin.cs`'s own
  Party Finder wiring (`partyFinderServiceRef`, `OnPartyFinderChatMessage`, the `Condition` parameter added to
  `PartyFinderAutomationService`'s constructor) was read first and layered on top of, never reverted — this pass's
  edits to `Plugin.cs` touch only the composition-root lines needed to construct the new
  `Shell.ModuleWindowManager(..., globalSettings)` and wire the Launcher/command handler, nowhere near the Party
  Finder lines.
- **Mair's Trivia** — `MairsTriviaOperatorPanel.cs` was **not edited at all** (it doesn't even appear in `git
  status`). It already has zero `ImGui.Begin`/`End` of its own, per the plan's confirmation, so it inherits Launcher
  + Collapse/Hide support for free through the generic host, exactly as predicted — no code changes were needed or
  made. `docs/MAIRS_TRIVIA_GAMEPLAY_HARDENING.md` (untracked) was not touched.
- **Macro's faux hotbars** (`MacroHotbarRenderer.cs`) — zero changes, confirmed by `git status` (the file does not
  appear as modified).
- **ShoutRunner** — its operator panel participates like any normal module (Launcher entry, Collapse/Hide all work);
  its travel-exception gate in `Plugin.cs` is unmodified — the same `canRenderShoutRunner` predicate is threaded
  through `windowManager.DrawAll`'s `canRenderModule` callback exactly as it was before this pass, with the launcher
  itself gated only on the general session gate (it has no ShoutRunner-specific exception, since the launcher itself
  has no "active operation" of its own to preserve visibility for).

## 12. The future Global UI Login Gate — attachment point confirmed, not built

Per the plan's explicit non-goal, no new login gate was built. `ModuleLauncherWindow.Draw` is gated on
`sessionGate.CanRenderGeneralUi` — the exact same `SessionPresentationGateService`-backed predicate every other
VenueOS window (`windowManager.DrawAll`'s default branch, Bingo's/Giveaways' auxiliary windows, the main tablet)
already uses. This **is** the attachment point a future, more general Global UI Login Gate would extend; this pass
confirms it exists and reuses it, and adds no parallel/competing gate.

## 13. `NEW_MODULE_GUIDE.md` changes

Added **§20b "Module Launcher & Window Management"** (a new subsection after §20a, following the guide's existing
lettered-subsection convention — §9a, §12a, §13a/b, §22a, §34a/b/c, §41a, §42a/b), documenting the real, as-built
state model, the two-layer API, header controls and Close-vs-Minimize semantics, persistence/reload resolution,
Launcher architecture/metadata/click-routing, Settings → Launcher, the `/venueos launcher` command, the
global-vs-per-venue decision, and the full normal/opt-out/faux-hotbar classification. Extended the Definition of
Done (§42, **UI** category) with two new checklist items for the Launcher button and Collapse/Minimize behavior, and
the Visual Definition of Done (§41a) with a note that "the detached view works correctly" now includes
Collapse/Expand and Minimize/Restore. Written **after** implementation, tests, and both builds passed — never
aspirationally beforehand, per the guide's own "no self-authorized deferrals"/documentation-last-for-code rule.

## 14. Exact test list and results

New test files:

- `tests/VenueOS.Core.Tests/ModuleWindowManagerTests.cs` — 31 facts covering: default/closed state (3), Open
  behavior including preference-seeding and idempotency (5), Collapse/Expand including geometry preservation and the
  moved-while-collapsed-then-expanded case (5), Hide/Restore including the PreHideState round-trip and repeated-Hide
  safety (7), Evict/Close (5), focus requests (4), multi-module independence (2), and the operational-separation
  proof (2).
- `tests/VenueOS.Core.Tests/LauncherLayoutTests.cs` — 15 facts: `ContinuesRow` (5, including the 1→single-column and
  a non-positive-value-treated-as-1 case), `RowSizes` (5, including the exact 1/mid-value/≥count cases the plan
  specifies), `LooksOffscreen` (5).
- `tests/VenueOS.Core.Tests/LauncherEntriesTests.cs` — 12 facts: `FullOrder` (5, including stale-id skipping and
  duplicate-id collapsing), `Eligible` (7, including the default-on/opt-out contract, disabled-module exclusion
  regardless of `ShowOnLauncher`, and the disable/re-enable preference-preservation scenario).
- `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` extended with 15 new facts covering `Launcher`
  defaults/persistence/reset, `ShowOnLauncher`/`ModuleOrder` persistence and per-id isolation, per-module
  `ModuleWindowPreference` persistence and isolation, and the full serialize/deserialize-boundary test for both new
  record types together.

**Final counts** (`dotnet test VenueOS.sln -c Debug`):

```
VenueOS.Core.Tests.dll     — Passed: 62,   Failed: 0, Skipped: 0  (baseline 4;   +58 new)
VenueOS.Venues.Tests.dll   — Passed: 23,   Failed: 0, Skipped: 0  (unchanged)
VenueOS.Services.Tests.dll — Passed: 1080, Failed: 0, Skipped: 0  (baseline 1065; +15 new)
-------------------------------------------------------------------------------------------
Total                      — Passed: 1165, Failed: 0, Skipped: 0  (baseline 1092; +73 new)
```

0 failed, 0 skipped, higher than the 1092 baseline, exactly as required.

## 15. Exact Debug/Release build results

```
dotnet build VenueOS.sln -c Debug    → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet build VenueOS.sln -c Release  → Build succeeded. 0 Warning(s), 0 Error(s).
```

Both ran clean after the implementation was complete — including `VenueOS.Plugin` itself (which only builds against
the real Dalamud SDK/API, so a clean build here is the strongest signal short of live Dalamud loading).

## 16. Live-QA checklist (to be performed by the user in Dalamud)

**Launcher setup, movement, lock, reload**
- [ ] `/venueos launcher` toggles the launcher on/off; toggling on with a corrupted/offscreen stored position
      recovers to a sane default instead of appearing unreachable
- [ ] Launcher appears at its persisted position/size on plugin load, with no window auto-opening otherwise
- [ ] Unlock → drag the strip to move the launcher; resize via native grips; re-lock → position/size stay put and
      can no longer be moved/resized
- [ ] Buttons Per Row changes column count without changing the window's own pixel size; a too-narrow unlocked
      window scrolls its button row horizontally instead of silently changing the column count
- [ ] Scale changes icon/text/button size without changing Width/Height
- [ ] Compact (icon-only) mode shows icon-only buttons, each with a hover tooltip carrying the full module name
- [ ] Reset Launcher Position (Settings → Launcher) restores position/size/scale to sensible defaults without
      touching Buttons Per Row, Lock, Compact, or which modules are shown
- [ ] Hiding every module from Settings → Launcher shows the exact empty-state text ("No modules are shown on the
      launcher. Configure Launcher in VenueOS Settings.") instead of a blank/zero-size window
- [ ] `/xlreload` (or a plugin disable/re-enable): launcher config, per-module window geometry, and Show-on-Launcher/
      order preferences all survive; nothing auto-opens

**Module window open/collapse/move/expand/minimize/restore/close** — repeat for **Party Finder, ShoutRunner, Macro
(main panel), Mair's Trivia, Giveaways (main panel), and one game module (e.g. Bingo)**:
- [ ] Launcher click on a Closed module opens its detached window
- [ ] Launcher click again while Expanded/Collapsed focuses the existing window rather than duplicating it
- [ ] Collapse button shrinks the window to a compact header-only strip (icon + name + Expand/Minimize/Close); no
      Settings gear while collapsed; the collapsed strip is still draggable
- [ ] Expand restores full content at the window's *current* position with the *previously remembered* expanded
      size — including after dragging the collapsed strip to a new spot first
- [ ] Minimize hides the window entirely (no longer drawn) while the module keeps operating in the background —
      confirm via the module's own live state (Party Finder's listing stays active/refreshing, ShoutRunner's run
      keeps advancing, Trivia's game keeps ticking, a Giveaways roll window stays open to rolls) while its detached
      window is hidden
- [ ] Launcher click on a Hidden module restores it to whichever of Expanded/Collapsed it was before hiding
- [ ] Close removes the window from the Launcher's "active" indicator and from tracking entirely; a subsequent
      Launcher click reopens fresh (or from the last persisted preference if one exists)
- [ ] Disabling the module in Settings → Modules while its window is open force-closes it; re-enabling and reopening
      restores its last remembered Collapsed/Expanded state and position

**Mandatory "active operation continues while hidden" checks**
- [ ] Party Finder: start recruitment, Minimize the window, confirm auto-refresh/warning-message handling still
      fires (check Diagnostics or restore the window to observe state) while hidden
- [ ] ShoutRunner: start a run, Minimize the window, confirm the run continues advancing (shouts still fire on
      schedule) while hidden, and that the existing travel-exception behavior is unaffected
- [ ] Mair's Trivia: start a game, Minimize the window, confirm the game/timer continues while hidden
- [ ] Confirm in all three cases that Collapse/Hide never appears in Diagnostics as a failure and never disables the
      module

## 17. Files changed by this pass

**Created:**
- `src/VenueOS.Core/ModuleWindowManager.cs`
- `src/VenueOS.Core/LauncherLayout.cs`
- `src/VenueOS.Core/LauncherEntries.cs`
- `src/VenueOS.Plugin/Shell/ModuleLauncherWindow.cs`
- `src/VenueOS.Plugin/Shell/LauncherSettingsPage.cs`
- `tests/VenueOS.Core.Tests/ModuleWindowManagerTests.cs`
- `tests/VenueOS.Core.Tests/LauncherLayoutTests.cs`
- `tests/VenueOS.Core.Tests/LauncherEntriesTests.cs`
- `docs/MODULE_LAUNCHER_WINDOW_MANAGEMENT.md` (this file)

**Modified:**
- `src/VenueOS.Services/GlobalSettingsService.cs` — added `LauncherSettings`, extended `GlobalSettings`, added
  Launcher/per-module-window-preference read/write methods.
- `src/VenueOS.Plugin/Shell/ModuleWindowManager.cs` — rewritten as the ImGui-rendering layer over
  `VenueOS.Core.ModuleWindowManager`.
- `src/VenueOS.Plugin/Shell/ModuleWindowHeader.cs` — extended with Collapse/Expand and Minimize buttons.
- `src/VenueOS.Plugin/Shell/Icons.cs` — added `chevron-down`, `chevron-up`, `minimize`, `lock`, `unlock`, `launcher`
  icon keys.
- `src/VenueOS.Plugin/Shell/SettingsScreen.cs` — added the Launcher nav category.
- `src/VenueOS.Plugin/Plugin.cs` — composition root wiring (Launcher construction/draw, `/venueos launcher`, help
  text, `windowManager.PersistAllOpenGeometry()` in `Dispose`); fully qualified `VenueOS.Plugin.Shell.ModuleWindowManager`
  at its two use sites to resolve the new namespace ambiguity with `VenueOS.Core.ModuleWindowManager`. **No lines
  belonging to the pre-existing Party Finder diff were touched.**
- `src/VenueOS.Plugin/Bingo/BingoCallAlertWindow.cs`, `BingoCalledNumbersWindow.cs`, `BingoPlayerCardViewerWindow.cs`,
  `src/VenueOS.Plugin/Giveaways/GiveawaysTrackerWindow.cs` — one-line call-site update each, passing a stable id
  literal to `ModuleWindowHeader.Draw`'s new signature; behavior unchanged (still exactly Settings + Close).
- `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` — extended with the new Launcher/window-preference
  tests (appended after the existing tests; nothing pre-existing removed or altered).
- `NEW_MODULE_GUIDE.md` — new §20b, extended §41a and §42.

## 18. Pre-existing files confirmed preserved untouched

- `src/VenueOS.Modules.Operations/PartyFinder/PartyFinderService.cs` — diff unchanged (+24/-… from before this pass).
- `src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs` — diff unchanged (+71/-… from before this pass).
- `src/VenueOS.Plugin/PartyFinderOperatorPanel.cs` — diff unchanged (+7/-… from before this pass); **zero content
  edits** in this pass, as the plan required.
- `tests/VenueOS.Services.Tests/PartyFinderServiceTests.cs` — diff unchanged (+74 new lines from before this pass).
- `src/VenueOS.Plugin/MairsTriviaOperatorPanel.cs` — **not modified at all** (does not appear in `git status`);
  **zero content edits**, as the plan required.
- `docs/MAIRS_TRIVIA_GAMEPLAY_HARDENING.md`, `docs/PARTY_FINDER_HARDENING.md` — untracked, untouched.
- `src/VenueOS.Plugin/Macro/MacroHotbarRenderer.cs` — not modified at all.

## 19. Final `git status`

```
 M NEW_MODULE_GUIDE.md
 M src/VenueOS.Modules.Operations/PartyFinder/PartyFinderService.cs
 M src/VenueOS.Plugin/Bingo/BingoCallAlertWindow.cs
 M src/VenueOS.Plugin/Bingo/BingoCalledNumbersWindow.cs
 M src/VenueOS.Plugin/Bingo/BingoPlayerCardViewerWindow.cs
 M src/VenueOS.Plugin/Giveaways/GiveawaysTrackerWindow.cs
 M src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs
 M src/VenueOS.Plugin/PartyFinderOperatorPanel.cs
 M src/VenueOS.Plugin/Plugin.cs
 M src/VenueOS.Plugin/Shell/Icons.cs
 M src/VenueOS.Plugin/Shell/ModuleWindowHeader.cs
 M src/VenueOS.Plugin/Shell/ModuleWindowManager.cs
 M src/VenueOS.Plugin/Shell/SettingsScreen.cs
 M src/VenueOS.Services/GlobalSettingsService.cs
 M tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs
 M tests/VenueOS.Services.Tests/PartyFinderServiceTests.cs
?? docs/MAIRS_TRIVIA_GAMEPLAY_HARDENING.md
?? docs/MODULE_LAUNCHER_WINDOW_MANAGEMENT.md
?? docs/PARTY_FINDER_HARDENING.md
?? src/VenueOS.Core/LauncherEntries.cs
?? src/VenueOS.Core/LauncherLayout.cs
?? src/VenueOS.Core/ModuleWindowManager.cs
?? src/VenueOS.Plugin/Shell/LauncherSettingsPage.cs
?? src/VenueOS.Plugin/Shell/ModuleLauncherWindow.cs
?? tests/VenueOS.Core.Tests/LauncherEntriesTests.cs
?? tests/VenueOS.Core.Tests/LauncherLayoutTests.cs
?? tests/VenueOS.Core.Tests/ModuleWindowManagerTests.cs
```

## 20. Explicit confirmations

- **Nothing was staged, committed, pushed, tagged, released, or version-bumped.** `git add`/`git commit` were never
  run; `repo.json` was never opened; `VenueOS.Plugin.csproj`'s `<Version>`/`<AssemblyVersion>`/`<FileVersion>`
  (currently `0.3.6`) were never touched.
- **Community Presets was not started** — no code, no config shape, no UI for it exists anywhere in this pass.
- **Cleanup-All-Interfaces (a global visual redesign) was not started** — every existing module's own interior UI
  (`Draw()`/`DrawSettings()` content) is byte-for-byte unchanged; the only visual additions are the new Launcher
  window and the new header buttons, both new surfaces rather than a redesign of existing ones.
- `ShoutsLiveLayout.MaxSlotsPerRow` was not changed and remains `5`; the Launcher's `ButtonsPerRow` is a fully
  independent setting with its own default (`6`) and its own pure math (`LauncherLayout`), never sharing a code path
  with Shouts.
- The full Global UI Login Gate was not implemented — only the existing `SessionPresentationGateService`/
  `CanRenderGeneralUi` attachment point was confirmed and reused for the Launcher, exactly as scoped.

## 21. Known limitations / deferred to a future pass

- **Launcher position persistence across a full game/Dalamud restart while Unlocked** relies partly on ImGui's own
  per-window-id memory (`imgui.ini`) in addition to this pass's own explicit `GlobalSettings.Launcher` persistence —
  the same class of ambiguity already documented and left unverified for detached module windows in
  NEW_MODULE_GUIDE.md §20 ("whether it persists across a full plugin/game restart depends on Dalamud's own
  `imgui.ini` handling and has not been independently verified"). This pass's own explicit position/size write-back
  is authoritative whenever it runs (every frame the window is actually being dragged/resized, plus a one-shot force
  on load when Locked or on a fresh appearance when Unlocked); only the exact interaction with a stale `imgui.ini`
  entry from a much older session is unverified, matching the existing precedent rather than introducing a new one.
- **No general offscreen-detection/multi-monitor recovery** was built for either the Launcher or module windows —
  `LauncherLayout.LooksOffscreen`'s generous NaN/Infinity/wildly-out-of-range check plus the manual "Reset Launcher
  Position" action are the only recovery mechanisms, exactly matching the plan's explicit scoping (§38).
- The Settings → Launcher module list orders/hides only currently **enabled** modules (a disabled module is omitted
  from that list, matching the Launcher's own omission rule) — its `ShowOnLauncher`/order preference is preserved
  underneath and simply not editable from Settings while the module is disabled; re-enabling it makes it editable
  again with its prior preference intact.

## 22. Live QA Follow-Up

This section covers two scoped corrections made **after** the pass above was live-tested in Dalamud, in response to
two live-QA findings. Like the pass above, this follow-up is also **not staged, committed, pushed, tagged, or
version-bumped** — everything is left uncommitted for the next round of live QA, and `repo.json` was not touched.

### 22.1 Correction 1 — the Launcher is now icon-only, always

**Finding:** icon+name buttons consumed too much screen space in-game.

**Change:** `ModuleLauncherWindow.DrawButton` no longer branches on a `compact` flag — it always renders the
original `compact: true` path (a square-ish button, centered icon, no name text drawn). The `compact ?` ternary in
`DrawButtons` is gone; every button now uses one fixed width (`ButtonWidth = 44f`, scaled by `LauncherSettings.Scale`
exactly like before — the renamed former `IconOnlyButtonWidth`). The now-unused `MinButtonWidth`/non-compact text
layout constants and code were removed.

**The hover tooltip is unchanged and still carries the full name** — `ModuleLauncherWindow.DrawButton` still calls
`ImGui.SetTooltip(module.Descriptor.DisplayName)` on hover, exactly as before; it was never conditional on the
compact flag in the first place, so nothing had to change there.

**`Buttons Per Row` is unaffected** — it was already independent of pixel size (it only decides when
`LauncherLayout.ContinuesRow` wraps to a new row, never the button's own width), so removing the icon+name mode
changes nothing about column-count behavior, resize behavior, or the horizontal-scrollbar fallback for a too-narrow
window.

**The obsolete `CompactIconOnly` setting was removed, not retained.** Before removing it, the round-trip safety was
verified rather than assumed:

- This repo's Dalamud config round-trip (`VenueOsPluginConfiguration`, `DalamudGlobalSettingsStore.Read`/`Write` in
  `Plugin.cs`) goes through `IDalamudPluginInterface.SavePluginConfig`/`GetPluginConfig` — confirmed
  Newtonsoft.Json-based by this codebase's own existing documentation (`VenueOS.Venues.VenueModels.cs`'s
  `ModulePayload` doc comment, which explains a *different*, already-known Newtonsoft `JsonElement` corruption issue
  found on a live installation — but nothing in `LauncherSettings`/`GlobalSettings` is a raw `JsonElement`, so that
  specific issue does not apply here).
- Newtonsoft.Json's default `JsonSerializerSettings` (no `MissingMemberHandling` override) silently **ignores**
  unknown JSON properties when deserializing into a POCO/record — an old persisted blob still carrying
  `"CompactIconOnly": true/false` simply has that property skipped.
- A repo-wide search confirmed **no `JsonSerializerSettings`/`MissingMemberHandling` override exists anywhere** in
  this codebase — Dalamud owns `SavePluginConfig`/`GetPluginConfig`'s serializer internally, and this plugin has no
  hook into or override of it. Nothing here could have set `MissingMemberHandling.Error`.
- Therefore removing the C# property is safe and non-breaking: a pre-existing config file with the old property
  loads fine, the value is simply discarded on next save. No migration step was needed or written.

`CompactIconOnly` was removed from: `LauncherSettings` (`GlobalSettingsService.cs`, plus its
`ResetLauncherPosition` doc-comment reference), the "Compact (icon-only) buttons" toggle in
`LauncherSettingsPage.DrawGeneral` (and its Reset-position description text), `ModuleLauncherWindow`'s draw code, and
every test reference in `GlobalSettingsServiceTests.cs` (the toggle's own persistence/reset/serialize-boundary
assertions were adjusted to drop `CompactIconOnly`, not removed wholesale — the surrounding assertions for
`ButtonsPerRow`/`Locked`/`Scale`/etc. are all still exercised).

**Everything else about button behavior is unchanged:** `ButtonsPerRow` stays authoritative and independent of pixel
size; stable module-ID-based ImGui IDs (`##launcher-{module.Descriptor.Id}`); `active`/`hovered` background
highlighting tied to `windowManager.GetState(...)`; disabled-module exclusion (still `LauncherEntries.Eligible`,
untouched); click routing (`RouteClick`, untouched); move/resize/lock (`DrawStrip`, `Locked` flag handling in
`Draw`, untouched); default `ShowOnLauncher` behavior (untouched).

### 22.2 Correction 2 — the main VenueOS tablet gets Collapse/Expand

**Finding:** the main tablet had no way to shrink itself out of the way while still tracking that an operation
(Party Finder, a running Macro, etc.) is active — every detached module window already had Collapse via this pass's
own work; the tablet itself did not.

**What was reused vs. newly added:**

| Piece | Reused as-is | New |
|---|---|---|
| State machine (Expanded/Collapsed, geometry) | `VenueOS.Core.ModuleWindowManager` — the exact same pure, ImGui-free class every module's detached window uses | — |
| Persistence | `GlobalSettingsService.GetModuleWindowPreference`/`SetModuleWindowPreference`, the exact same per-id `GlobalSettings.ModuleWindowPreferences` dictionary every module uses | A reserved key, `"__venueos.tablet__"` |
| Geometry-forcing technique | The "force position+size once on the transition frame via `ImGuiCond.Always`, otherwise let ImGui free-resize/move" technique from `Shell.ModuleWindowManager.DrawWindow` | Mirrored, not copied-and-diverged, in `Plugin.Draw`'s tablet block |
| Collapsed chrome | `ModuleWindowHeader.Draw` — the same shared collapsed/expanded header component every detached module window already uses | Called with `onMinimize` omitted (tablet gets no Minimize) |
| Expanded chrome | `TabletHeader.Draw` — the tablet's own bespoke header, unchanged in its normal (Expanded) form | One new Collapse icon button added to its right region, before Settings |
| Orchestration | — | `Shell.ModuleWindowManager`'s new "Main tablet Collapse/Expand" region: `EnsureTabletTracked`, `TabletState`, `CollapseTablet`, `ExpandTablet`, `ReportTabletPosition`, `ReportTabletExpandedSize`, `GetTabletSnapshot`, `ConsumeTabletForcedGeometryFrame`, `PersistTabletGeometry` |

**One deliberate divergence from the plan's suggested reuse, and why:** the plan's framing suggested seeding the
tablet directly into the *same* `VenueOS.Core.ModuleWindowManager` instance (`core`) that `Shell.ModuleWindowManager`
already uses for modules, on the theory that nothing iterates `core.OpenModuleIds` and cross-checks it against known
module ids. On inspection, that's not quite true: `Shell.ModuleWindowManager.DrawAll` **does** exactly that —
```csharp
foreach (var moduleId in core.OpenModuleIds)
{
    var module = modules.FirstOrDefault(x => x.Descriptor.Id == moduleId);
    if (module is null || !module.IsEnabled) { EvictAndForget(moduleId); continue; } // <-- would evict the tablet
    ...
}
```
Seeding the reserved key into `core` would have `DrawAll` force-evict it on the very next frame (`DrawAll` runs
every frame from `Plugin.Draw`, before the tablet's own `open` check). Rather than special-casing `DrawAll` to
recognize and skip the reserved key (which would mean touching shared logic used by every real module, just to carve
out one exception), a **second, separate instance** of the identical `VenueOS.Core.ModuleWindowManager` class
(`tabletCore`) is used instead. This is still "reuse the same machinery, not duplicated logic" — zero new
state-machine code was written, it's the same pure class with the same guarantees — while keeping `DrawAll`/the
Launcher completely untouched and the tablet structurally invisible to anything that iterates `core`/
`modules.Modules`. This divergence is called out explicitly per the task's own "adjust if you find something
cleaner... just document why" allowance.

**Geometry/persistence semantics** (all mirroring a module's own, exactly):

- `EnsureTabletTracked()` — called once from `Plugin`'s constructor, right after `windowManager` is constructed.
  Seeds the tracked entry from any persisted preference; never opens/shows anything on its own (the tablet's
  pre-existing `open`/`focusRequested` fields remain the sole, unchanged gate on visibility — Collapse/Expand is a
  new, additional, separate axis, only meaningful while `open == true`).
- Every frame the tablet is drawn: `ReportTabletPosition` runs unconditionally (both Collapsed and Expanded,
  mirroring `core.ReportPosition`'s own "every frame regardless of state" contract) so dragging the *collapsed*
  header still updates position; `ReportTabletExpandedSize` runs only while Expanded (mirroring
  `ReportExpandedSize`'s own Expanded-only guard), so collapsing never overwrites the remembered expanded size with
  the compact header's own fixed size.
- `ConsumeTabletForcedGeometryFrame()` mirrors `DrawWindow`'s own `wasCollapsedLastFrame`/`justExpanded`/
  `forceGeometryThisFrame` bookkeeping exactly, scoped to the one tablet entry — on the single frame a Collapsed
  tablet transitions to Expanded (or a fresh construction seeds from a persisted Collapsed preference), position +
  the remembered expanded size are forced once via `ImGuiCond.Always`, then ImGui resumes normal free move/resize.
  This is what makes "drag the collapsed header to a new spot, then Expand" restore at the new position with the old
  remembered expanded size, identical to a module.
- `CollapseTablet()`/`ExpandTablet()` each persist immediately (`PersistTabletGeometry()`), matching
  `Shell.ModuleWindowManager`'s own `PersistTransition` call at every explicit module Collapse/Expand.
- `PersistTabletGeometry()` is also called once more from `Plugin.Dispose()`, alongside the existing
  `PersistAllOpenGeometry()` call, so a tablet that was only ever dragged/resized this session (never explicitly
  Collapsed/Expanded) still remembers its true last geometry across `/xlreload` — the exact reasoning
  `PersistAllOpenGeometry` already documents for modules.
- Persistence is **global, not per-venue** — the reserved key goes through the exact same
  `GlobalSettings.ModuleWindowPreferences` dictionary every module's window preference already uses, which is
  already global (§6/§11 above); switching venues never resets it.

**`Plugin.Draw`'s tablet block**, restructured to branch on `windowManager.TabletState`:

- **Collapsed:** `ImGuiWindowFlags.NoResize` added; size forced every frame to
  `ModuleWindowManager.TabletCollapsedWidth`/`TabletCollapsedHeight` (aliases of the same `CollapsedWidth`/
  `CollapsedHeight` constants module windows use); position forced only on the transition frame. Renders **only**
  `ModuleWindowHeader.Draw(...)` — icon ("home"), "VenueOS", a drag handle, Expand (`onToggleCollapse`), and Close
  (`onClose`, reusing the tablet's existing `() => open = false` close semantics unchanged). `onSettings` is a
  no-op (harmless — `ModuleWindowHeader` skips rendering the Settings button while collapsed). `onMinimize` is
  omitted entirely — **the tablet deliberately gets no Minimize**, per the live-QA finding that a launcher-dependent
  Minimize would be symmetry for its own sake, not a real requirement; the tablet already has its own pre-existing
  Close. No tablet content, no venue frame, no `TabletHeader` — all skipped while collapsed.
- **Expanded:** renders exactly as before — `UiKit.DrawVenueFrame` (if enabled), `TabletHeader.Draw` (now with the
  new `onCollapse` parameter wired to `windowManager.CollapseTablet()`), the full content child (Settings/Manual/
  Home/module), and the branding footer — with the one addition that position/expanded-size are now continuously
  reported into the tracked geometry every frame, exactly like a module.

**`TabletHeader.Draw`** gained one new required parameter, `Action onCollapse`, and one new button in its
already-reserved right region (venue selector → **Collapse** → Settings → Close, in that left-to-right order) —
`rightWidth`'s reservation math was extended by one more `IconButtonWidth + gap` so the new button can never be
pushed off by a long venue name, matching the header's own existing "reserve critical controls first" convention.

**Hard requirements confirmed:**

- Collapsing/expanding the tablet **never calls anything on `ModuleHost`, any `IVenueModule`,
  `VenueProfileService`, or any per-module service** — by construction, since `VenueOS.Core.ModuleWindowManager` has
  a zero-parameter constructor and no field of any such type (already asserted by
  `ModuleWindowManagerTests.The_manager_takes_no_dependency_on_any_module_or_service_by_construction`, which applies
  identically to `tabletCore` since it's the same class). The new `Shell.ModuleWindowManager` tablet methods
  (`CollapseTablet`, `ExpandTablet`, `ReportTabletPosition`, etc.) touch only `tabletCore`/`globalSettings` — never
  `modules`, `shell.Select*`, or any operator-panel/service reference the class also happens to hold for module
  chrome.
- Detached module windows (`Shell.ModuleWindowManager.DrawAll`) and the Module Launcher
  (`ModuleLauncherWindow.Draw`) remain **fully independent of the tablet's collapsed/expanded state** — both are
  separate, unconditional calls in `Plugin.Draw` that run and return before the tablet block is ever reached, and
  neither reads `windowManager.TabletState` or any tablet-specific member. This was already true before this
  follow-up (closing the tablet never affected them) and remains true now.

### 22.3 Tests added/changed

- `tests/VenueOS.Core.Tests/ModuleWindowManagerTests.cs` — 7 new facts under "Reserved-key reuse (Live QA
  follow-up)", exercising the exact way the tablet uses the shared state machine (opened once via a reserved key,
  never evicted): reserved-key parity with a normal module id, Collapse→Expand round-trip, expanded geometry
  retained across a collapse/expand round trip, moving while collapsed then expanding uses the new position with the
  old expanded size (the literal "drag the collapsed header, then expand" live-QA sequence), the reserved key is
  never evicted across repeated idempotent `Open` calls, preference seeding through `Open` round-trips exactly like
  a module's, and an explicit isolation proof that the reserved key and a real module id never interfere sharing one
  manager instance.
- `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` — 1 new fact
  (`The_reserved_tablet_key_persists_and_isolates_exactly_like_a_modules_own_window_preference`) proving the
  reserved key's `ModuleWindowPreference` round-trips through `GlobalSettingsService` the same way a module's does,
  and never collides with a real module id's own preference; plus every existing `CompactIconOnly` reference across
  6 existing facts was removed (not the facts themselves — the surrounding `ButtonsPerRow`/`Locked`/`Scale`/
  persistence/reset/serialize-boundary assertions in those facts are all still exercised).
- No ImGui rendering (button pixel size/icon centering, the tablet's collapsed-header pixel layout) was
  unit-tested, matching this repo's established, deliberate convention (no test project exists for
  `VenueOS.Plugin`) — that part is live-QA-only, covered by the checklist below.

**Final test results** (`dotnet test VenueOS.sln -c Debug`):

```
VenueOS.Core.Tests.dll     — Passed: 69,   Failed: 0, Skipped: 0  (baseline 62;   +7 new)
VenueOS.Venues.Tests.dll   — Passed: 23,   Failed: 0, Skipped: 0  (unchanged)
VenueOS.Services.Tests.dll — Passed: 1081, Failed: 0, Skipped: 0  (baseline 1080; +1 new)
-------------------------------------------------------------------------------------------
Total                      — Passed: 1173, Failed: 0, Skipped: 0  (baseline 1165; +8 new)
```

0 failed, 0 skipped, higher than the 1165 baseline this follow-up started from, exactly as required.

### 22.4 Build results

```
dotnet build VenueOS.sln -c Debug    → Build succeeded. 0 Warning(s), 0 Error(s).
dotnet build VenueOS.sln -c Release  → Build succeeded. 0 Warning(s), 0 Error(s).
```

Both include `VenueOS.Plugin` itself (the real Dalamud-SDK-dependent project), so a clean build here covers the
`TabletHeader`/`Plugin.Draw`/`ModuleWindowHeader` ImGui call sites this follow-up touches, not just the pure
`VenueOS.Core`/`VenueOS.Services` logic.

### 22.5 Files changed by this follow-up

**Modified (beyond the pre-existing uncommitted Module Launcher & Window Management pass and Party Finder
hardening, both left untouched — see §11/§18 above, still true):**

- `src/VenueOS.Plugin/Shell/ModuleLauncherWindow.cs` — icon-only always; removed the `compact` branch/parameter and
  the now-unused `MinButtonWidth` constant (renamed `IconOnlyButtonWidth` → `ButtonWidth`).
- `src/VenueOS.Plugin/Shell/LauncherSettingsPage.cs` — removed the "Compact (icon-only) buttons" toggle and its
  Reset-position description-text reference.
- `src/VenueOS.Services/GlobalSettingsService.cs` — removed `LauncherSettings.CompactIconOnly` and its
  `ResetLauncherPosition` doc-comment reference.
- `src/VenueOS.Plugin/Shell/ModuleWindowManager.cs` — added the "Main tablet Collapse/Expand" region (reserved-key
  orchestration over a second `VenueOS.Core.ModuleWindowManager` instance).
- `src/VenueOS.Plugin/Shell/TabletHeader.cs` — added the `onCollapse` parameter and its right-region Collapse
  button.
- `src/VenueOS.Plugin/Plugin.cs` — `EnsureTabletTracked()` call in the constructor, `PersistTabletGeometry()` call
  in `Dispose()`, and the tablet block in `Draw()` restructured to branch on Collapsed/Expanded.
- `tests/VenueOS.Services.Tests/GlobalSettingsServiceTests.cs` — `CompactIconOnly` references removed from existing
  facts; one new fact added for the reserved tablet key's persistence.
- `tests/VenueOS.Core.Tests/ModuleWindowManagerTests.cs` — 7 new reserved-key facts appended.
- `NEW_MODULE_GUIDE.md` — the Module Launcher paragraph and Settings → Launcher line updated for icon-only-always
  and the removed Compact setting; a new paragraph documenting the tablet's reserved-key reuse of the shared
  Collapse/Expand primitive.
- `docs/MODULE_LAUNCHER_WINDOW_MANAGEMENT.md` — this section.

### 22.6 Live-QA checklist — Live QA Follow-Up

**Icon-only Launcher**
- [ ] Every launcher button now shows only an icon (no name text), at a reasonably-sized square (not microscopic)
- [ ] Hovering any button still shows a tooltip with that module's full display name
- [ ] Settings → Launcher no longer shows a "Compact (icon-only) buttons" toggle
- [ ] Buttons Per Row = 1 → single column of icon-only buttons; a mid-value (e.g. 3-4 with 8+ eligible modules) →
      wraps into multiple rows; a value ≥ the eligible-module count → single row — all with icon-only buttons, and
      the column count never changes on window resize (only the horizontal-scrollbar fallback engages if the window
      is too narrow for the configured count)
- [ ] Move/resize/lock still work exactly as before (Lock/Edit toggle, drag strip, native resize grips while
      unlocked, position/size persistence across `/xlreload`)
- [ ] An existing `/xlreload` with a config saved *before* this follow-up (still carrying the old
      `"CompactIconOnly"` JSON property) loads cleanly with no error/warning — confirms the safe-removal analysis
      above

**Main tablet Collapse/Expand**
- [ ] `TabletHeader`'s right region shows a new Collapse button (before Settings); clicking it shrinks the tablet to
      a compact header-only strip (icon + "VenueOS" + Expand + Close — no Settings gear, no Minimize button)
- [ ] The collapsed strip is draggable via its own drag handle
- [ ] Drag the collapsed strip to a new position, then click Expand — the tablet restores full content at the *new*
      position, at the *previously remembered* expanded size (not the collapsed strip's own compact size)
- [ ] `/xlreload` (or a plugin disable/re-enable) while the tablet is Collapsed — it reopens Collapsed, at its last
      position, the next time it's shown; while Expanded, its position/size are remembered the same way module
      windows already are
- [ ] Close (from either the Expanded `TabletHeader` or the Collapsed `ModuleWindowHeader`) behaves exactly like
      today's existing Close — `/venueos` reopens it
- [ ] **Collapse the tablet while an active operation runs (e.g. Party Finder recruitment, or a running Macro) and
      confirm it's completely unaffected** — the operation keeps running/advancing while the tablet is collapsed
      (check Diagnostics or Expand the tablet again to observe it), exactly like a module's own Collapse/Minimize
      already doesn't touch its operational state
- [ ] Confirm the Module Launcher and any detached module windows keep working normally — opening/closing/
      collapsing them — regardless of whether the main tablet is currently Collapsed, Expanded, or closed entirely
- [ ] Confirm Collapse/Expand never appears in Diagnostics as a failure and never disables anything
