# Macro — Implementation Report

## 1. Executive Summary

Macro is a new, greenfield, purely local VenueOS module: a persistent macro library authored in Settings, a live
tile-launcher module that behaves like one large macro hotbar, up to four persistent faux FFXIV-style hotbars that
render on the game screen independently of the VenueOS tablet, nested macro invocation, cycle detection, an
action-readiness wait directive (`/actionready`), and a `/venueos macro "Name"` chat-command entry point. It has no
backend, no WebSocket, and no browser component.

The module is complete and builds/tests clean (Debug and Release, 0 warnings/0 errors, full existing suite plus 69
new Macro tests all passing — §60). It ships `UnderDevelopment: true`, disabled by default, per §48/NEW_MODULE_GUIDE.md
§22a — **no live QA has been performed**; everything reported here is what automated tests and two clean full-solution
builds can prove, not what a Dalamud session has verified.

## 2. Module ID / Descriptor

- ID: `tools.macro` (namespaced, unique, never reused)
- Display Name: **Macro**
- Icon key: `"macro"` — a new procedural glyph added to `AppIcons` (`src/VenueOS.Plugin/Shell/Icons.cs`): three
  small hotbar-slot squares in a row, the first holding a filled play triangle. Distinct from `"grid"` (Bingo's even
  3×3 grid) and `"terminal"` (a command prompt, not a launcher).
- `DisplayOrder: 13` (after Giveaways' 12 — purely additive, doesn't reorder any existing module)
- `UnderDevelopment: true`, `IsEnabled = false` by default (§48)

This module icon (`AppIcons.Draw("macro", ...)`) is entirely separate from a saved macro's own FFXIV icon — see §7.

## 3. State Authority Model (NEW_MODULE_GUIDE.md §34a)

One sentence: **Macro's local per-venue config — the saved macro library and the four hotbars' configuration — is
the only durable state this module owns, full stop.** No backend, no shared/other-module state. Active execution
(`MacroRunner`'s stack/phase/status) is in-memory-only VenueOS operational state, reset on venue switch/disable/
dispose. Game command execution goes through the existing `ChatCommandService`. Action readiness is a live,
uncached probe of actual game state behind a narrow interface (§17-§20).

## 4. Macro Persistence

`MacroSettings(Macros, Hotbars)` — one per-venue payload (`VenueProfileService.GetModuleConfig`/`SaveModuleConfig`,
schema version 1, module id `tools.macro`), saved by `MacroService` after every mutation (NEW_MODULE_GUIDE.md
§13a — every UI action that changes persistent data routes through a service method that actually calls `Save()`).
Both the macro library AND the hotbar configuration (enabled/layout/assignments/position/scale/transparency) live
in this same per-venue payload — see §19 for why hotbar position specifically is per-venue too, not global.

A live-verified persistence bug was caught by this module's OWN tests before it ever reached the UI: `MacroHotbar`
originally stored its screen position as `System.Numerics.Vector2`, whose `X`/`Y` are public **fields**, not
properties. `VenueProfileService` serializes every module config through `System.Text.Json` with its default
options, which only serialize public *properties* — a `Vector2?` therefore round-tripped as an empty object,
silently discarding the position every reload. Fixed by introducing `MacroPosition(float X, float Y)`, a plain
record with real properties, used for storage; `System.Numerics.Vector2` is only ever used at the ImGui rendering
boundary. `MacroServiceTests.Position_scale_and_transparency_persist` is the regression test.

## 5. Settings Editor

`MacroOperatorPanel.DrawSettings()` — a `Forms.Segmented` two-tab surface:

- **Library**: macro browser (`ListRow` list) + selected-macro editor — name, icon picker, delay, and a full
  add/remove/reorder (Up/Down) line editor, each line showing its live byte count against the real limit (§8).
- **Hotbars**: a segmented picker across the four hotbars, then per-hotbar Enabled/Layout/Scale/Transparency,
  Clear Hotbar (confirmed), a macro-icon palette, and a 12-slot assignment grid supporting both native ImGui
  drag-and-drop (palette icon → slot; slot → slot swaps two assignments per spec §28) and a click-to-select-then-
  click-to-place fallback.

`Draw()` (live) never authors a macro or a hotbar slot — only launches and (optionally) repositions bars in Edit
Hotbars mode (§19).

## 6. Live Tile Launcher

`MacroOperatorPanel.Draw()`: a status card (RUNNING: name / nested macro if any / line X of Y / status message /
Cancel, or "No macro running." plus the last outcome), the four Hotbar visibility toggles + a global Edit Hotbars
toggle, then a responsive grid of large clickable macro tiles (`MacroIconRenderer`-drawn icon + name). The tile
itself is the Run action — no select-then-run step. Tiles disable while a macro is already running; Cancel stays
available. Same `Draw()` content is used for both embedded and detached rendering (there is exactly one
implementation — NEW_MODULE_GUIDE.md §19).

## 7. FFXIV Macro Icon Source/API

Runtime-only, exactly as required (§6 — no exported/copied Square Enix textures, no bundled icon packs, no emoji):

- **Rendering**: `Dalamud.Plugin.Services.ITextureProvider.GetFromGameIcon(new GameIconLookup(iconId))` returns an
  `ISharedImmediateTexture`; `.GetWrapOrEmpty()` yields an `IDalamudTextureWrap` (never throws — an unresolved id
  simply renders nothing that frame) whose `.Handle` is drawn via `ImDrawListPtr.AddImage`. One shared helper,
  `MacroIconRenderer.Draw`, is used by the icon picker, the tile launcher, and the faux hotbar slots — one rendering
  path, one appearance everywhere.
- **Browsable catalog** (Settings icon picker, `MacroIconPicker`): Lumina's `GeneralAction` Excel sheet, read via
  `IDataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>()` — the same job-independent, generic icon set
  (Sprint, Teleport, Return, Duty Action, Repair, Desynthesis, etc.) FFXIV's own macro editor's General icon
  category is built from, per §6's explicit instruction to prefer that subset rather than dumping every game icon
  or every job's combat-action icons (a VenueOS macro is never tied to one job). If the sheet can't be read for any
  reason, the picker degrades to an empty catalog with an explanatory `WarningState` rather than throwing.
- Only the numeric icon id is ever stored (`SavedMacro.IconId`, `uint`) — never a copied image.

This exact combination (`GetFromGameIcon`/`GetWrapOrEmpty`, `GeneralAction` sheet) was verified by getting the
whole plugin, including this code, through a clean `dotnet build` against the actual installed Dalamud SDK — not
guessed from documentation alone.

## 8. Actual Command-Line Limit Used

**500 UTF-8 bytes** — `BlockLettersLimits.ChatBytes`, the real FFXIV chat/command input limit, reused directly as
`MacroLineLimits.MaxLineBytes` (`src/VenueOS.Modules.Operations/Macro/MacroModels.cs`). This is deliberately **not**
`BlockLettersLimits.MacroLineBytes` (181 bytes) — FFXIV's own built-in macro editor's shorter per-line limit, which
is exactly the constraint this module exists to let an operator escape (task spec §8's explicit instruction).

## 9. Block Letters Limit-Code Reuse

Yes, in full — no duplicated magic number or counting logic:

- `MacroLineLimits.MaxLineBytes` is a direct alias of `BlockLettersLimits.ChatBytes`.
- `MacroService.Truncate` (used by every line-mutating method) calls `BlockLetters.BlockTextEditor.Insert(...)`, the
  same UTF-8-byte-budget, whole-scalar-safe insert/truncate primitive Block Letters' composer uses — so a macro
  line can never be silently split mid-glyph, exactly like Block Letters' own verified guarantee.
- The line editor in `MacroOperatorPanel` shows a live `{bytes}/{MaxLineBytes} bytes` counter on every line field,
  the same presentation convention Block Letters uses.

## 10. ChatCommandService Integration

Every actual game command Macro sends — a plain line, and nothing else (`/actionready` and nested-macro lines are
intercepted, never forwarded) — goes through `chat.Enqueue(new ChatCommand(text, runCancellation.Token))`. No
competing dispatcher, no direct `ICommandManager.ProcessCommand` call for anything that talks to the server.

## 11. SchedulerService Integration

`MacroRunner` uses `SchedulerService.Schedule(delay, callback, cancellationToken: ...)` for every inter-line delay —
never a `Timer`/`Task.Delay` loop. Delay is per-macro (`SavedMacro.DelayBetweenLinesSeconds`, a `double`, so
fractional seconds are fully supported) and is only ever applied BETWEEN two lines within the same frame — the last
line of any frame (root or nested) completes/pops immediately with no trailing wait, matching
`GiveawayService.SendLine`'s existing "delay is between sends, never after the final one" convention.

## 12. `/venueos macro` Behavior

`/venueos macro "Macro Name"` extends the existing `/venueos` `ICommandManager` handler (`Plugin.OnVenueOsCommand`)
with a regex match on the trimmed argument string; any other/blank argument preserves the pre-existing
open-tablet behavior exactly, so this can never regress plain `/venueos`. Because it's the same command handler
FFXIV always used for `/venueos`, it inherently works with the tablet open or closed, the Macro window open or
closed, from ordinary chat, and from inside a built-in FFXIV macro (a built-in macro just plays back chat/slash
commands — no special-casing needed). If the Macro module is disabled, it does **not** run anything silently — it
prints `"[VenueOS] The Macro module is disabled — enable it in Settings → Modules → Macro to use /venueos macro."`
via `ChatGui.Print`. A macro-not-found or already-running failure similarly prints `"[VenueOS] {error}"`.

## 13. Nested Semantics

A line that is exactly `/venueos macro "Child Name"` (whole-line match — see §14) is intercepted by `MacroRunner`
and never forwarded to FFXIV. Execution uses a real stack (`Stack<RunFrame>` + a parallel `HashSet<Guid>` of the
macro ids currently on it): entering a child pushes a frame and runs it to completion using the CHILD's own
`DelayBetweenLinesSeconds`; when the child frame is exhausted, it pops and the PARENT resumes using the PARENT's
own delay before its next line — no interleaving, ever (verified explicitly by
`MacroRunnerTests.Nested_macro_runs_to_completion_using_its_own_delay_then_parent_resumes_with_its_own_delay`,
which uses two DIFFERENT delay values specifically so the wrong-delay case would fail the test).

## 14. Cycle Detection

`A→A`, `A→B→A`, `A→B→C→A`, etc. are all caught by the same on-stack `HashSet<Guid>` check before a push ever
happens — no infinite loop, no stack overflow, no runaway dispatch is possible by construction (nothing is ever
sent to chat for a line that triggers a cycle). A readable message is produced by joining the stack's macro names,
e.g. `"Nested macro cycle detected: A → B → A"`, recorded as `MacroRunner.LastOutcomeMessage`/`LastOutcomeKind` and
shown as an `ErrorState` in the live module. A generous secondary backstop (`MaxStackDepth = 64`) guards against
pathological non-cyclic depth without punishing legitimate nesting.

## 15. Snapshot Semantics

`MacroRunner.Start` captures `library.ToDictionary(m => m.Name, ..., OrdinalIgnoreCase)` **once**, at launch — every
nested-name resolution for that entire run reads only this snapshot dictionary, never `MacroService.Settings`
directly. Editing macros in Settings during an active run therefore cannot mutate the running tree; regression-
tested explicitly by `MacroRunnerTests.A_run_is_immune_to_edits_made_to_a_separately_held_copy_of_the_library_after_start`.

## 16. `/actionready`

A VenueOS-only directive, recognized only as a whole trimmed line (`^/actionready\s*$`, case-insensitive) —
never forwarded to FFXIV (regression-tested: `MacroRunnerTests.Actionready_directive_is_never_sent_to_the_game`).
When `MacroRunner` reaches this "line", it does not schedule anything itself — it sets a `waitingOnActionReady`
frame reference and returns; `MacroRunner.Tick(now)` (called every frame via `MacroModule.Tick`, which is why Macro,
unlike most current local modules, has a real per-frame `Tick` body) polls the probe:

- **Ready** → the exact same "start the macro's own delay, then move to the next line" path a normal line uses.
- **Busy** → nothing happens; `StatusMessage` shows "Waiting for action readiness…".
- **Unknown** (state genuinely can't be determined) → nothing happens either — `StatusMessage` shows "Unable to
  determine action readiness…"; **never** auto-advances (spec §20's hard requirement, regression-tested by
  `Unknown_action_readiness_never_auto_advances`, which polls 50 times and asserts no progress).

Cancel stops a wait immediately (`Cancel_while_waiting_on_action_readiness_stops_the_run`); a venue switch cancels
the run the same way every other stale-async transition does (`MacroServiceTests.Venue_switch_cancels_an_in_flight_run_via_the_module`).

## 17. Readiness Research

Because Macro sends arbitrary, unparsed operator-authored text (spec §9 — VenueOS never reimplements FFXIV command
parsing), the probe does **not** know which specific action id the next line will invoke, so it cannot use the
per-action `ActionManager.GetActionStatus(actionType, actionId, ...)` pattern a dedicated rotation plugin uses for a
KNOWN action (confirmed pattern researched: `UnknownX7/ReAction`'s `ActionStackManager`, which gates on
`GetActionStatus(...) == 0`). Instead the researched signals answer the more general question `/actionready`
actually needs — "is the player currently busy doing something" — and were confirmed against primary/documented
sources, not guesswork:

- **Animation lock** — `FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->AnimationLock` (a `float`,
  confirmed field via reflection over the FFXIVClientStructs types this repo already references). Per xiv.dev's
  Animation Lock documentation, this is "an internal timer that player has to wait certain amount of time before
  they are allowed to use any actions" — nonzero means genuinely busy, and it covers general
  abilities/weaponskills/items uniformly regardless of job.
- **Casting** — `IPlayerCharacter.IsCasting` (`Dalamud.Game.ClientState.Objects.Types.IBattleChara`, a fully-managed
  Dalamud API — confirmed via Dalamud's own published API reference), backed up by
  `ConditionFlag.Casting`/`ConditionFlag.Casting87` for redundancy.
- **Crafting/gathering** — `ConditionFlag.ExecutingCraftingAction` (documented value 40, "an action is currently
  resolving" during a craft step — the busy window while progress/quality is animating) and
  `ConditionFlag.ExecutingGatheringAction` (value 42), plus `ConditionFlag.PreparingToCraft` (the synthesis screen
  is still opening — no craft action can be sent yet). These exact flag names/values were confirmed directly from
  `goatcorp/Dalamud`'s own `ConditionFlag.cs` source, not assumed.

Crafting and general actions are deliberately treated uniformly — the underlying signals differ per game system,
but the readiness QUESTION does not, so no crafting-vs-general branch exists in the probe.

**This exact signal combination has NOT been exercised against a live game/crafting session in this environment.**
It compiles and is logically sound against documented Dalamud/FFXIVClientStructs behavior, but live QA (§22 below)
is required before it's trusted for real crafting macros.

## 18. ECommons/Game API Boundary

The probe uses only Dalamud's own supported services (`IObjectTable.LocalPlayer`, `ICondition`) plus one unsafe
FFXIVClientStructs pointer dereference (`ActionManager.Instance()`), matching the exact unsafe-access pattern
already established elsewhere in this codebase (`DalamudVenueAddressProvider`'s `HousingManager` access,
`Plugin.ExecuteChatCommand`'s `RaptureShellModule` fallback) — no ECommons dependency was needed for this probe.
Per NEW_MODULE_GUIDE.md §30, this entire unsafe/game-specific surface is isolated in one class,
`DalamudActionReadyProbe` (`src/VenueOS.Plugin/Macro/`), implementing the small `IActionReadyProbe` interface
`MacroRunner` (in `VenueOS.Modules.Operations`, no FFXIVClientStructs/ECommons reference at all) depends on — the
pure runner is tested with a fake probe (`MacroRunnerTests.FakeProbe`) that never touches game memory.

## 19. Faux-Hotbar Architecture

`MacroHotbarRenderer` (`src/VenueOS.Plugin/Macro/`) draws up to four independent ImGui windows (ids
`###venueos-macro-hotbar-{index}`), one per enabled `MacroHotbar`, directly from `Plugin.Draw()` — gated only on
`macroModule.IsEnabled`, entirely independent of the main tablet's or the Macro screen's own open/closed state
(the same "auxiliary window outlives its parent screen" precedent Bingo's Called Numbers window and Giveaways'
tracker window already established). They are explicitly **not** `ModuleWindowManager`-owned detached windows —
no module header, no Settings gear, no VenueOS chrome at all (§26).

All four bars, the live tile launcher, and every other invocation path read and mutate the exact same
`MacroService`/`MacroRunner` — there is no second state copy for the faux hotbars.

## 20. Hotbar Renderer

Rendering is a plain `ImGuiWindowFlags.NoTitleBar | NoCollapse | NoScrollbar | NoResize | NoMove | NoBackground`
window with a hand-drawn dark translucent panel (native-HUD look, §26/§58 — see §28 for why this deliberately
breaks from the VenueOS visual language) sized from the active layout's column/row count and the hotbar's `Scale`.
Each of the 12 logical slots is drawn at a position computed by `MacroHotbarLayouts.Position(layout, slotIndex)`
(row-major within the layout's column count) and shows only its assigned macro's icon via the shared
`MacroIconRenderer` — a hover tooltip carries the macro name (§27).

## 21. 12-Slot Layout System

Every hotbar always has exactly 12 logical slots (`MacroHotbarLayouts.SlotCount`), stored as a fixed 12-element
`IReadOnlyList<Guid?>` (`MacroHotbar.SlotMacroIds`) indexed by logical slot — never by row/column. Layout only ever
changes `MacroHotbar.Layout`, which is purely presentational; `MacroHotbarLayouts.Position(layout, slotIndex)` is
the single function both the live renderer and the Settings slot-grid editor call to turn a logical index into a
(column, row) position, so there is exactly one mapping to get right (regression-tested per arrangement:
`MacroModelsTests.Every_supported_layout_has_the_expected_dimensions_covering_all_twelve_slots`, and
`MacroServiceTests.Changing_layout_never_changes_logical_slot_assignments`).

## 22. Supported Arrangements

All six requested: **12×1, 6×2, 4×3, 3×4, 2×6, 1×12** (`MacroHotbarLayout` enum). Each was verified (test-level) to
cover exactly the 12 slots with distinct, in-bounds positions and no gaps/overlap.

## 23. Hotbar Drag/Drop Assignment

Implemented in Settings → Modules → Macro → Hotbars (`MacroOperatorPanel.DrawHotbarSettings`), **not** on the live
overlay bars (the live bars only support click-to-run and, in Edit mode, drag-to-reposition — see §24 — slot
CONTENT is never editable from the live HUD):

- **Native ImGui drag/drop**: a macro-palette icon is a `BeginDragDropSource`/`SetDragDropPayload` source (the
  macro's `Guid`, sent as a raw 16-byte payload via `ReadOnlySpan<byte>`); each of the 12 slot previews is a
  `BeginDragDropTarget`/`AcceptDragDropPayload` target. Dropping onto an empty slot assigns; dropping a
  currently-assigned macro (found elsewhere in the SAME hotbar) onto another slot **swaps** the two assignments —
  the documented, predictable choice from spec §28 (never a silent overwrite of the destination's prior
  assignment) — the source's origin slot is otherwise just reassigned directly (moved/replaced) if the dragged
  macro wasn't already present in the same hotbar.
- **Reliable non-drag fallback**: clicking a palette icon selects it (highlighted); clicking a slot then assigns
  the selected macro there and clears the selection. Clicking an occupied slot with nothing selected clears it.

`SwapSlots`/`AssignSlot`/`ClearSlot`/`ClearHotbar` are the only mutation entry points, all persisting immediately
via `MacroService.Save()`; clearing never deletes the underlying macro (regression-tested).

## 24. Lock/Edit Behavior

A single **global** "Edit Hotbars" toggle (`MacroHotbarRenderer.EditMode`, flipped from the live module's own
`MacroOperatorPanel.DrawHotbarControls`) governs every visible bar at once — deliberately simpler than a per-bar
toggle, and it avoids an operator forgetting to re-lock one bar. **Locked** (normal/default): slots are
click-to-run only; there is no drag-handle code path at all, so a normal macro click can structurally never move
the bar (spec §46's explicit requirement). **Editing**: slot click-to-run is skipped entirely and replaced by one
full-window `InvisibleButton` drag layer using the exact `GetMouseDragDelta`/`ResetMouseDragDelta`/`SetWindowPos`
pattern `TabletHeader` already uses for the borderless main tablet, plus a thin highlighted border so the operator
can see which bars are currently draggable. Position is persisted only once, on mouse release
(`ImGui.IsMouseReleased`), not on every dragged frame, to avoid a config save per frame during a drag.

## 25. Hotbar Persistence Behavior

Enabled/Layout/SlotMacroIds/Position/Scale/Transparency are all part of `MacroHotbar`, part of the per-venue
`MacroSettings` payload, saved through the same `SaveModuleConfig` path as the macro library — verified across an
actual reconstruct-from-store reload boundary (`MacroServiceTests`, not merely a window close/reopen, per
NEW_MODULE_GUIDE.md §13a/§30's explicit distinction). Disabling the module (`IsEnabled = false`) never touches this
saved config (`MacroServiceTests.Disabling_the_module_never_clears_saved_config`); disabling only stops the faux
bars from rendering/executing (`Plugin.Draw`'s `if (macroModule.IsEnabled)` gate) and re-enabling restores exactly
where it left off.

## 26. Scale/Transparency Implementation Status

**Both implemented**, not skipped: `MacroHotbar.Scale` (clamped 0.5–2.0) scales slot size/spacing/padding directly;
`MacroHotbar.Transparency` (clamped 0.2–1.0) modulates the hand-drawn background/border panel's alpha. Both persist
per-hotbar, per-venue, and are editable from Settings → Hotbars via `Forms.FloatField`.

## 27. Files Changed

**New — `VenueOS.Modules.Operations` (pure, ImGui-free):**
- `src/VenueOS.Modules.Operations/Macro/MacroModels.cs`
- `src/VenueOS.Modules.Operations/Macro/MacroDirectiveParser.cs`
- `src/VenueOS.Modules.Operations/Macro/MacroRunner.cs`
- `src/VenueOS.Modules.Operations/Macro/MacroService.cs`

**New — `VenueOS.Plugin` (ImGui + the one unsafe boundary):**
- `src/VenueOS.Plugin/Macro/DalamudActionReadyProbe.cs`
- `src/VenueOS.Plugin/Macro/MacroIconPicker.cs`
- `src/VenueOS.Plugin/Macro/MacroHotbarRenderer.cs`
- `src/VenueOS.Plugin/Macro/MacroOperatorPanel.cs`

**New — tests:**
- `tests/VenueOS.Services.Tests/MacroDirectiveParserTests.cs`
- `tests/VenueOS.Services.Tests/MacroModelsTests.cs`
- `tests/VenueOS.Services.Tests/MacroRunnerTests.cs`
- `tests/VenueOS.Services.Tests/MacroServiceTests.cs`

**Modified:**
- `src/VenueOS.Plugin/Plugin.cs` — `ITextureProvider` PluginService, Macro composition/registration, faux-hotbar
  rendering call, `/venueos macro "Name"` command extension. No other module's wiring was touched.
- `src/VenueOS.Plugin/Shell/Icons.cs` — added the `"macro"` icon key/glyph. No existing key was changed.

Nothing under `Operations.cs`/`NativeOperationsPanels.cs` was touched or extended (NEW_MODULE_GUIDE.md §21/§33).

## 28. Why the Faux Hotbar Intentionally Differs from VenueOS Visual Language

Per task spec §58/§26 and NEW_MODULE_GUIDE.md's own general rule (which explicitly still applies to Settings, the
live module, confirmations, and status — only the faux hotbar itself is exempted): the faux hotbar is a
purpose-built HUD element meant to visually blend into FFXIV's own interface, not read as another VenueOS window.
It therefore does not use `UiKit.BeginSectionCard`/theme accent colors/VenueOS chrome — it uses a fixed dark
translucent panel, square icon slots, and no title/header at all, regardless of the active venue theme (Dark,
Light, Neon, Midnight). This is a deliberate, documented exception, not an oversight: Settings, the live tile
launcher, confirmations, and all other Macro UI still fully use the shared `UiKit`/`Forms` component language and
theme tokens. No proprietary FFXIV UI texture is copied anywhere — icons are the same runtime-requested game icons
described in §7; "indistinguishable at a glance" is the goal, not a claim of pixel-identical reproduction (see §35).

## 29-35. Definition-of-Done items covered inline above

See §3 (state authority), §4 (persistence — including the caught-and-fixed Vector2 bug), §12-§17 (command
ownership/nesting/cycle/snapshot/actionready), §15-§17 (cancellation and stale-async guards — every scheduled
callback and `ChatCommand` carries the run's own `CancellationTokenSource` token plus a `currentRunId` guard,
exactly like `GiveawayService`'s established pattern; a venue switch cancels via `MacroModule.OnVenueChangedAsync`
→ `MacroService.Load` → `Runner.Cancel()`, tested explicitly), §7/§9 (icon/limit persistence and reuse), §19-§26
(hotbar persistence and the documented UI exception), and §48 (UnderDevelopment true, disabled by default — never
self-promoted).

## 36. Known Limitations

1. **`/actionready`'s real-game behavior is unverified.** The signals it reads are documented/researched and the
   code compiles and passes unit tests against a fake probe, but no live crafting/combat session has exercised it.
2. **The icon catalog is the `GeneralAction` sheet's job-independent set**, not a literal reproduction of FFXIV's
   full per-job macro icon picker (which pulls from job-specific action sheets FFXIV's own UI resolves contextually
   per class). This matches spec §6's explicit instruction ("prefer the icon subset... rather than dumping every
   game icon in existence") but means a job-specific combat action icon is not currently selectable — only the
   generic/general set is. If broader coverage is wanted later, additional curated sheets could be added to
   `MacroIconPicker.BuildCatalog` without changing anything else.
3. **Native drag-and-drop's actual feel (hit-testing, visual drag preview) is unverified live** — automated tests
   cannot exercise ImGui drag/drop; the click-to-place fallback is always available regardless.
4. **Faux-hotbar visual fidelity to native FFXIV hotbars is unverified live** — no screenshot/rendering comparison
   was possible from this session.
5. Whether an operator's window position (main tablet) or a hotbar's ImGui-remembered ImGui id state survives a
   full FFXIV restart, rather than just a plugin reload, was not independently re-verified for the faux hotbars
   specifically (matches the same caveat already documented for detached windows in NEW_MODULE_GUIDE.md §19).

## 37. Exact Live QA Required

From spec §56/§57, none of which has been performed by this session:

1. Enable Macro; create several harmless `/echo` macros with different icons; reload plugin, verify persistence.
2. Open the live module; verify the tile launcher, immediate tile-click execution, and Cancel.
3. Test a nested macro and recursion protection live.
4. Test `/venueos macro "Name"` with the UI closed, and from inside a real built-in FFXIV macro.
5. Test `/actionready` against real crafting; verify the delay only begins after readiness.
6. Enable each of the four hotbars; assign macros; verify correct icons render; click a slot and verify the right
   macro runs.
7. Drag a bar, reload the plugin/game, verify the position survives.
8. Switch through all six layouts and verify assignments never scramble.
9. Run all four hotbars simultaneously with different assignments/layouts and verify independence.
10. Toggle hotbars on/off from the live module and verify assignments remain.
11. Verify Locked mode never lets a macro click drag the bar, and Edit mode repositions cleanly.
12. Visually confirm the faux hotbar reads as HUD-like, not as a VenueOS window, and check Dark/Light/Neon/Midnight
    only where they actually apply (Settings/live module — the faux hotbar intentionally does not theme-shift).

## 38. Hardened-Guide Definition-of-Done Walk

Architecture — own folder/files (not `Operations.cs`/`NativeOperationsPanels.cs`) ✅; stable unique id ✅; single
display-name source ✅; unique icon key ✅; state authority stated (§3) ✅; `modules.Register` added ✅; Home tile
generic (no code needed) ✅; enable/disable generic ✅.

Venue — `venues.Current` used, no module-local venue field ✅; per-venue config via `GetModuleConfig`/
`SaveModuleConfig` ✅; venue-switch tested with two venues (macros, hotbar enabled) never leaking ✅; in-flight run
cancelled on venue switch (tested) ✅.

Settings — `DrawSettings()` shows only persistent configuration (macro library + hotbar config), distinct from
`Draw()`'s live launcher ✅; every editable field actually saves (verified via reload-boundary tests, not just
window close/reopen) ✅; no credentials exist in this module (n/a for §9a) ✅; schema version set (1) ✅.

Lifecycle — `CancellationTokenSource` owned by `MacroRunner`, cancelled/recreated every relevant transition ✅;
enable/disable preserves config ✅; venue switch resets/reloads correctly ✅; disposal (`MacroModule.DisposeAsync`)
cancels the runner ✅; stale async/stale callback guards via `currentRunId` + token, exactly like `GiveawayService`'s
established pattern ✅; no realtime channel exists (n/a).

UI — `UiKit`/`Forms` used throughout Settings/live module (the faux hotbar's documented, narrow exception is §28)
✅; only theme tokens used outside that exception ✅; `ConfirmDialog` used for delete-macro/clear-hotbar, with
consequence-stating text including the referencing-macros warning (§16's requirement) ✅; empty/running/idle/error/
warning states all handled (§36's checklist) ✅; destructive actions use `DangerButton` ✅; embedded/detached share
one `Draw()` ✅; Auto Pop-Out needs no module-specific code (generic routing, unmodified) ✅; Visual Definition of
Done cannot be fully walked without a live Dalamud session — see §37.

Security — no credentials exist in this module; nothing routes through Diagnostics that could embed a secret;
`MacroRunner.ProbeFaulted` routes probe exceptions through `DiagnosticsService.RecordFailure` (§25) with only a
plain exception message, never game state.

Data — persistence verified across a real reload boundary (service reconstructed from the same store, not a
window close/reopen) ✅; no import/export in this module (n/a) ✅; no archive/reset concept — only Delete (with
reference detection) and hotbar Clear/ClearSlot, kept semantically distinct per §13b's definitions ✅.

Testing — 69 new tests across parser/model/runner/service layers, covering per-venue isolation, error paths (cycle,
missing reference, over-limit line, duplicate/blank names), and the probe via a fake (never a real game call) ✅;
`dotnet build`/`dotnet test` both clean, 0 warnings ✅.

Live QA — **not performed by this session** (§37); this report does not claim otherwise anywhere.

Promotion — `UnderDevelopment: true`, `IsEnabled = false` by default; nothing in this session flips either flag.

---

# LIVE QA FIX — PERSISTENCE / MULTILINE EDITOR / LIVE HOTBAR ASSIGNMENT

A follow-up targeted correction pass, after live QA in actual FFXIV/Dalamud confirmed the execution engine works
(tile launcher, faux hotbars rendering, `/venueos macro "Name"`, and a real Ninja `/actionready` combat sequence all
succeeded live) but found one release-blocking persistence bug and several authoring/hotbar UX defects. **None of
`MacroRunner`'s sequencing, `MacroDirectiveParser`, nested execution, cycle detection, `/venueos macro` routing,
`/actionready`, `SchedulerService`/`ChatCommandService` integration, hotbar layout mapping, the faux-hotbar
renderer's core drawing, or FFXIV icon rendering was touched in this pass** — every change below is narrowly scoped
to the defect it fixes.

## 1. Exact Root Cause of Macros Disappearing on Plugin Reload

`MacroService.Load(Guid nextVenueId)` is the ONLY place that sets the service's private `venueId` field and loads
`Settings` from `VenueProfileService.GetModuleConfig`. It is called from exactly one place: `MacroModule.OnVenueChangedAsync`.
That method, in turn, is invoked by `ModuleHost` from exactly two places — `VenueProfileService.InitializeAsync`'s
one startup pass, and `SwitchAsync` on every venue switch — and BOTH are gated by `ModuleHost.IsolateAsync`'s
`if (!module.IsEnabled) return;` (NEW_MODULE_GUIDE.md §23).

Macro ships `UnderDevelopment: true`, `IsEnabled = false` by default (every current local module does). So at
plugin startup, `Load()` is never called for Macro — its `venueId` field stays at its C# default, `Guid.Empty`.
Enabling Macro from Settings → Modules' toggle (`ModulesSettingsPage.DrawModuleRow`) previously did nothing but
`module.IsEnabled = enabled; globalSettings.SetModuleEnabled(...)` — it never called `OnVenueChangedAsync`. So a
module enabled LIVE, mid-session, had its `Load()` skipped entirely for the rest of that session. Every macro the
operator then created was saved via `profiles.SaveModuleConfig(venueId, ...)` with `venueId` still `Guid.Empty` —
written under the WRONG per-venue key. The bug was invisible during the session because `Draw()`/Settings read the
SAME in-memory `Settings` object being mutated; it only surfaced on the next plugin load, when `Load()` finally ran
correctly for the REAL active venue id and found nothing saved under that key.

Giveaways happened not to exhibit this in its own live QA pass for a mundane reason, not a code difference: it was
already enabled (and therefore already correctly `Load()`-ed) from a PRIOR session by the time its presets were
authored. `GiveawayService`/`BlockLettersService` use the byte-for-byte identical `Load()`-only-called-from-
`OnVenueChangedAsync` pattern (confirmed by direct inspection — `grep` for `private Guid venueId` / `public void Load`
/ `OnVenueChangedAsync` across all three services shows the same shape) — this was a latent, systemic gap in every
current local module's activation lifecycle, not something specific to Macro's own code.

## 2. Exact Persistence Fix

`ModulesSettingsPage.cs` (`src/VenueOS.Plugin/Shell/`), the one shared "Settings → Modules" toggle handler every
module already routes through, now calls the module's own `OnVenueChangedAsync` the moment it transitions from
disabled to enabled:

```csharp
if (UiKit.Toggle(theme, "##enabled", ref enabled))
{
    var wasEnabled = module.IsEnabled;
    module.IsEnabled = enabled;
    globalSettings.SetModuleEnabled(module.Descriptor.Id, enabled);
    if (enabled && !wasEnabled) ActivateNewlyEnabledModule(module);
}

private void ActivateNewlyEnabledModule(IVenueModule module)
{
    try
    {
        var current = venues.Current;
        module.OnVenueChangedAsync(new VenueContext(current.Id, current.DisplayName, current.Theme), CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (Exception ex) { diagnostics.RecordFailure($"Activation failed in {module.Descriptor.Id}: {ex.Message}"); }
}
```

`ModulesSettingsPage`'s constructor gained a `VenueProfileService venues` parameter (wired through `SettingsScreen`,
which already held one) to resolve the current venue. This is a **shared-code fix** — it touches the one generic
Settings → Modules page every module already uses genericially, not any individual module's own file — made because
it is the true, minimal, safe root-cause fix: `OnVenueChangedAsync` is specifically designed to be safely re-invoked
(it already runs on every ordinary venue switch — reset in-memory state, reload config for the given venue), so
calling it once more, at the moment of enabling, changes nothing for an already-loaded module and correctly performs
the FIRST load for a module that skipped it at startup. No individual module's own file (Giveaways', Block Letters',
Macro's) was touched to fix this — per the task's explicit instruction not to touch Giveaways except for shared code
proven necessary and safe.

## 3. Why Previous Tests Missed the Bug

Every existing Macro persistence test (`MacroServiceTests`'s `Create()` fixture, and the equivalent pattern in
`GiveawayServiceTests`/`BlockLettersServiceTests`) called `service.Load(venueId)` immediately after constructing the
service, before ever creating any data — modeling a service that HAD already been correctly activated. That is
exactly the state a service is in after a plugin restart with the module already enabled (Giveaways' actual live QA
path), and it is a completely valid, still-necessary thing to test — but it never modeled the ACTUAL broken state: a
service that had macros saved through it **before** `Load()` was ever called even once. No amount of testing the
already-loaded case can catch a bug that only manifests in the never-loaded case.

## 4. New Reload-Boundary Regression Tests (`MacroServiceTests.cs`)

Two tests now reproduce the exact broken order directly, using a new `CreateWithoutLoading()` fixture helper that
constructs `MacroService` and deliberately never calls `Load()` — modeling a freshly-enabled-this-session module
exactly as it existed before the fix:

- `Bug_reproduction_a_service_that_is_never_loaded_before_saving_writes_under_the_wrong_venue_and_loses_data_on_reload`
  — creates a macro on a never-loaded service (mutation "succeeds" in-memory, exactly like the live symptom), then
  reloads a fresh service against the REAL active venue id and asserts the macro is absent — pinning the bug's exact
  mechanism as a permanent regression guard.
- `Fix_activating_a_never_loaded_service_for_the_current_venue_before_any_macro_is_created_makes_persistence_work_correctly`
  — calls `MacroModule.OnVenueChangedAsync` once for the current venue (exactly what `ActivateNewlyEnabledModule` now
  does), THEN creates macros, THEN reloads against the real venue id, and asserts both macros are present.

`Multiple_macros_all_survive_a_reload_with_stable_ids` and `Disable_then_reenable_does_not_lose_macros` cover the
already-activated case (stable IDs across reload; disable/re-enable never clears saved config) — the "normal"
reload-boundary tests, kept and updated for the new authoring API (see §7 below). `Hotbar_config_survives_the_same_
reload_boundary_as_the_macro_library` additionally proves a restored hotbar slot assignment resolves back to a real,
reloaded macro record, not a dangling id, across the SAME reload as the library.

## 5. Old One-Line-at-a-Time Editor Behavior (Retired)

Each macro line previously had its own `Forms.TextField`, with per-line Up/Down/Remove buttons and a page-level
"+ Add Line" button, all inline directly under Settings → Modules → Macro's library list. This made pasting an
existing FFXIV macro or any multi-line command sequence impractical.

## 6. New Multiline Editor Behavior

A single `Forms.MultilineField`-style large text area (`MacroEditorWindow.DrawBodyField`, ~1MB ImGui buffer — "no
artificial line-count limit... only bounded by practical storage/memory constraints") replaces it entirely. Normal
text editing — type, paste, Ctrl+A/C/X/V, arbitrary cursor movement and selection, insert/delete newlines anywhere —
all work exactly as ordinary ImGui multiline input already provides, because the field is bound to one plain
`string` draft with no custom keystroke interception.

## 7. Exact EOF Semantics

`MacroBodyText.ToExecutableLines` (`src/VenueOS.Modules.Operations/Macro/MacroBodyText.cs`) reads the body top to
bottom; the FIRST logical line that is empty, or whitespace-only once trimmed, ends the macro. Every line at or
after that point is excluded from the executable/saved result. A whitespace-only line is deliberately treated
identically to a truly empty one, specifically so an accidental stray-space line can never create an invisible
mid-macro stop that behaves differently from a visibly-blank one. If the body contains no blank line at all, the
entire text is executable (implicit EOF at end of input). A body that is empty/null produces zero executable lines,
never a crash.

## 8. CRLF/LF Normalization

`bodyText.Replace("\r\n", "\n").Replace('\r', '\n')`, applied once before splitting on `\n` — CRLF is normalized
first specifically so a subsequent lone-`\r` pass can't double-convert it, then any remaining lone `\r` (old
Mac-style endings) is normalized too. Line CONTENT itself is never trimmed or otherwise altered by this step — only
a temporary `Trim()` is used to test each line for the EOF condition in §7.

## 9. Per-Logical-Line 500-Byte Validation

`MacroLineValidator.FindOversizedLines` (same file) reuses Block Letters' already-verified UTF-8 byte counter
(`BlockTextLength.CountBytes` — no second implementation) against every EXECUTABLE line (content after EOF is never
validated, since it's never saved). A line at exactly 500 bytes passes; one byte over fails with an error in the
exact format the fix spec requires: `"Line 17 is 528 / 500 bytes."` (1-based line number). **Nothing is ever
truncated** — `MacroService.CreateMacro`/`SaveMacro` return every such error (plus a name-validation error, if any)
without saving anything at all when any executable line is oversized; the editor window disables nothing directly
but simply never closes/persists until `Save` succeeds, showing every returned error as an `ErrorState`.

## 10. Transactional Save/Cancel Editor

`MacroEditorWindow` (`src/VenueOS.Plugin/Macro/MacroEditorWindow.cs`) is the ONE macro-authoring surface — a
separate, non-modal ImGui window, never expanded inline under the library list. It owns its own local draft fields
(`name`/`iconId`/`delaySeconds`/`bodyText`) and calls into `MacroService` exactly once, on an explicit Save click:
`CreateMacro(...)` for a new macro, `SaveMacro(id, ...)` for an edit (preserving the stable `Id`, cascading a
rename's exact nested-reference update in the same atomic call — see §16 below). Cancel, or closing the window via
its native X button, simply sets `IsOpen = false` and discards the draft — `MacroService.Settings` was never touched.
The old one-line-per-field editor's incremental mutation methods (`AddLine`/`UpdateLine`/`RemoveLine`/`MoveLine`/
`RenameMacro`/`SetMacroIcon`/`SetMacroDelay`/`UpdateMacro`) are removed entirely — nothing calls `SaveModuleConfig`
per keystroke any more.

## 11. Exact Focus-Loss Root Cause

Found by direct code inspection, not trial and error: the retired per-line editor built each line's `Forms.TextField`
label as `$"Line {i + 1} ({bytes}/{MaxLineBytes} bytes)"`. `Forms.TextField` derives its ImGui widget id straight
from that label (`$"##{label}"`, in `Shell/Forms.cs`). Because the embedded byte count changed on every keystroke
that changed the line's length, the widget's OWN ImGui id changed on every keystroke — ImGui therefore tore down and
recreated what looked like a brand-new widget after every single character typed, discarding keyboard focus and
cursor position each time. This is exactly why it manifested as "one character, then focus drops."

## 12. Focus Fix

Structural, not a `SetKeyboardFocusHere()` workaround (which the fix spec explicitly forbade): every ImGui widget in
`MacroEditorWindow` uses one constant, literal label for its entire lifetime ("Macro Name", "Delay Between Lines
(seconds)", "Macro Body", `"##macro-body"`). Byte-count/executable-line-count/validation feedback is rendered as
ordinary text NEXT TO the field via `ImGui.TextWrapped`/`UiKit.ErrorState`, never woven into a widget's own
id-bearing label. Since the entire per-line editor (the only place this pattern existed) is retired, the bug's root
cause is structurally eliminated, not patched around.

## 13. Live Tile → Faux Hotbar Drag/Drop Implementation

`MacroDragDrop` (`src/VenueOS.Plugin/Macro/MacroIconPicker.cs`) is the one shared drag/drop payload helper — a
16-byte macro `Guid`, sent via `ImGui.SetDragDropPayload`/read via `ImGui.AcceptDragDropPayload` — now used
identically by FOUR call sites: the Settings hotbar editor's macro palette (source) and slot grid (target, as
before), and (new this pass) the LIVE module's own macro tiles (`MacroOperatorPanel.DrawMacroTile`, source — added
right after the tile's existing click handling, coexisting with it since ImGui only starts a drag once the mouse
moves past its own drag threshold while held) and the LIVE faux hotbar overlay's slots (`MacroHotbarRenderer.DrawSlot`,
target, only while that bar is in Edit mode). ImGui's drag-drop payload mechanism is global across windows by
design, so this works across the separate Macro module window and a separate faux-hotbar overlay window with no
extra plumbing. `MacroService.DropMacroOntoSlot` is the one shared assignment policy both the Settings slot grid and
the live overlay call — swap if the dragged macro is already on a DIFFERENT slot of the SAME hotbar, otherwise plain
(re)assign (spec §28's documented, predictable choice) — so live and Settings drag/drop can never diverge.

**A z-order conflict was identified while implementing this** between the Edit-mode whole-bar drag layer and the
individual slots, and the drag layer was drawn FIRST (underneath) with the slots drawn after. **The reasoning
written here at the time — "ImGui gives the later-drawn item hit-test priority where they overlap" — was WRONG, and
is exactly why live drag/drop onto a slot did not actually work despite this code compiling and every automated
test passing.** See the LIVE QA FIX section below for the corrected mechanism and the actual fix.

Locked mode is unaffected: no `BeginDragDropTarget` call exists in that code path at all, so a payload dragged over
a locked bar is never accepted (spec §23's explicit requirement), and clicking an assigned slot still runs its
macro exactly as before.

## 14. Persistence of Live Hotbar Assignments

Unaffected in mechanism — `DropMacroOntoSlot` calls the same `AssignSlot`/`SwapSlots` → `SaveModuleConfig` path
every other hotbar mutation already used, whether triggered from Settings or the live overlay. Explicitly
re-verified after the persistence fix (§1/§2) via `Hotbar_config_survives_the_same_reload_boundary_as_the_macro_library`,
`DropMacroOntoSlot_assigns_an_empty_slot`, `DropMacroOntoSlot_swaps_when_the_macro_is_already_on_the_same_hotbar`,
and `DropMacroOntoSlot_from_a_different_hotbar_just_assigns_not_swaps`. A hotbar slot referencing a macro id that no
longer exists was already, and remains, handled safely by construction — every renderer/editor call site does
`Settings.Macros.FirstOrDefault(m => m.Id == id)` and guards on the result being non-null before touching the icon
or tooltip, so a stale reference renders as an empty slot rather than crashing; `DeleteMacro` also already actively
clears any hotbar slot referencing the deleted macro (unchanged from the prior pass).

## 15. Confirmation `/actionready` Was Preserved

Not touched. `MacroRunner.Tick`, `ActionReadyState`, `IActionReadyProbe`, and `DalamudActionReadyProbe` are
byte-for-byte unchanged from the prior pass. Ready → the macro's configured delay → next line; Busy → wait; Unknown
→ wait, never auto-advance. Live QA already confirmed a real Ninja ability sequence (`/ac` lines + `/actionready` +
a 0.10s delay) executes correctly with this exact logic.

## 16. Confirmation `/venueos macro` Was Preserved

Not touched. `Plugin.OnVenueOsCommand`/`HandleVenueOsMacroCommand`/`VenueOsMacroCommandPattern` are unchanged.
UI-open/closed operation, built-in-FFXIV-macro invocation, the disabled-module chat message, the macro-not-found
chat message, and nesting interception via `MacroDirectiveParser` all behave exactly as before. Live QA already
confirmed `/venueos macro "Simple ability test"` launches correctly with this exact code.

## 17. Tests Added/Updated

- **New**: `tests/VenueOS.Services.Tests/MacroBodyTextTests.cs` (17 facts) — EOF/CRLF/LF/whitespace-only-line
  semantics, content-after-EOF exclusion (including a nested-macro-directive-after-EOF case and the exact
  Ninja-macro-with-a-trailing-line example from the fix spec), no-blank-line-means-whole-body, empty/null body,
  `/actionready`/nested-directive text surviving parsing unchanged, `ToBodyText` round-trip, and per-line byte
  validation (exact limit, one-over, multiple offending lines, UTF-8 multibyte glyph counting).
- **Rewritten**: `tests/VenueOS.Services.Tests/MacroServiceTests.cs` — the two lifecycle/activation regression tests
  (§4 above) are new; every macro-authoring test now exercises the transactional `CreateMacro(name, icon, delay,
  body, out created)`/`SaveMacro(id, name, icon, delay, body)` API instead of the retired per-line methods; three new
  facts cover `DropMacroOntoSlot`'s assign/swap/different-hotbar semantics; one new fact
  (`Hotbar_config_survives_the_same_reload_boundary_as_the_macro_library`) strengthens the existing hotbar
  persistence coverage.
- **Unchanged**: `MacroDirectiveParserTests.cs`, `MacroModelsTests.cs`, `MacroRunnerTests.cs` — none of the code they
  cover was touched in this pass, so they were left as-is and still pass.

## 18. Final Full Test Count

**875 tests, 0 failures** (`VenueOS.Core.Tests`: 4, `VenueOS.Venues.Tests`: 23, `VenueOS.Services.Tests`: 848 —
94 of which are Macro-specific, up from 69 before this pass).

## 19. Debug Build Result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 20. Release Build Result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 21. Remaining Live QA

Per the fix spec's §36 targeted retest, none of which has been performed by this session:

1. **Persistence first** — enable Macro, create Macro A and B, `/xlrestart` (or plugin reload), reopen Macro
   Settings, confirm both still exist. If this fails, Macro remains release-blocked.
2. **Authoring** — click + New Macro, confirm one separate editor window opens; paste a multi-line macro in one
   Ctrl+V; type continuously for several seconds and confirm focus never drops; put an empty line in the middle
   with more commands after it; Save; reopen; confirm only the content before the empty line is present; Cancel an
   edit and confirm it's discarded.
3. **Hotbar** — open Macro Live, turn Edit Hotbars ON, drag a macro tile directly onto a visible hotbar, confirm the
   icon appears; turn Edit Hotbars OFF; click the slot; confirm the macro runs; reload the plugin; confirm the
   assignment remains.
4. **Regression** — `/venueos macro "Simple ability test"` still works; the Ninja `/actionready` sequence still
   works; a nested macro still works; an A→B→A cycle still stops safely.
5. **Full restart, if time allows** — restart FFXIV entirely; confirm macros and hotbar assignment/position both
   survive.

## 22. Files Changed

**New (this pass):**
- `src/VenueOS.Modules.Operations/Macro/MacroBodyText.cs`
- `src/VenueOS.Plugin/Macro/MacroEditorWindow.cs`
- `tests/VenueOS.Services.Tests/MacroBodyTextTests.cs`

**Modified (this pass):**
- `src/VenueOS.Modules.Operations/Macro/MacroService.cs` — transactional `CreateMacro`/`SaveMacro` replace the
  retired per-line/per-field mutation methods; new `DropMacroOntoSlot` shared assignment policy.
- `src/VenueOS.Plugin/Macro/MacroOperatorPanel.cs` — compact library browser (Edit/Duplicate/Delete) replacing the
  inline per-line editor; tile drag source added; hotbar palette/slot grid now use the shared `MacroDragDrop` helper.
- `src/VenueOS.Plugin/Macro/MacroHotbarRenderer.cs` — Edit-mode slots now accept drag/drop (with the drag-layer
  z-order fix from §13); locked-mode behavior unchanged.
- `src/VenueOS.Plugin/Macro/MacroIconPicker.cs` — added the shared `MacroDragDrop` helper class.
- `src/VenueOS.Plugin/Shell/ModulesSettingsPage.cs` — the persistence-lifecycle fix (§2); shared code, not
  Giveaways- or Macro-specific.
- `src/VenueOS.Plugin/Shell/SettingsScreen.cs` — threads `VenueProfileService` into `ModulesSettingsPage`'s
  constructor for the fix above.
- `tests/VenueOS.Services.Tests/MacroServiceTests.cs` — rewritten per §17.

**Untouched in this pass** (verified unchanged, per the task's explicit preservation list): `MacroRunner.cs`,
`MacroDirectiveParser.cs`, `MacroModels.cs`, `DalamudActionReadyProbe.cs`, `Plugin.cs`'s `/venueos macro` handling
and Macro composition wiring, `Icons.cs`, and every other module's own files (Giveaways, Block Letters, Raffle,
Tournament, etc.).

## 23. Git Status

No `git add`/`commit`/`push`/`tag`/`release`/`branch`/`reset`/`clean` was run at any point in this session (initial
pass or this fix pass). All new/modified files exist only in the working tree, exactly as `git status` shows below
(captured after the final green build):

```
 M MODULE_DEVELOPMENT.md                                               (pre-existing, unrelated)
 M NEW_MODULE_GUIDE.md                                                 (pre-existing, unrelated)
 M README.md                                                           (pre-existing, unrelated)
 M docs/USER_MANUAL.md                                                 (pre-existing, unrelated)
 M repo.json                                                           (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Operations.cs                        (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Raffle/VenueRaffleClient.cs          (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/VenueOS.Modules.Operations.csproj    (pre-existing, unrelated)
 M src/VenueOS.Plugin/NativeOperationsPanels.cs                        (pre-existing, unrelated)
 M src/VenueOS.Plugin/Plugin.cs                                        (this module, prior pass)
 M src/VenueOS.Plugin/Shell/Forms.cs                                   (pre-existing, unrelated)
 M src/VenueOS.Plugin/Shell/Icons.cs                                   (this module, prior pass)
 M src/VenueOS.Plugin/Shell/ModulesSettingsPage.cs                     *** THIS FIX PASS ***
 M src/VenueOS.Plugin/Shell/SettingsScreen.cs                          *** THIS FIX PASS ***
 M src/VenueOS.Plugin/TournamentControlOperatorPanel.cs                (pre-existing, unrelated)
 M src/VenueOS.Plugin/VenueOperationsDashboard.cs                      (pre-existing, unrelated)
 M src/VenueOS.Plugin/packages.lock.json                               (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/ModuleDisplayOrderTests.cs             (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/RaffleClientTests.cs                   (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/TournamentControlTests.cs              (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/UnfinishedModuleDefaultsTests.cs       (pre-existing, unrelated)
?? (many pre-existing untracked files from Giveaways/Block Letters/Raffle/Tournament reconstruction — unrelated)
?? src/VenueOS.Modules.Operations/Macro/                               (this module; MacroBodyText.cs is new this pass)
?? src/VenueOS.Plugin/Macro/                                           (this module; MacroEditorWindow.cs is new this pass)
?? tests/VenueOS.Services.Tests/MacroBodyTextTests.cs                  *** THIS FIX PASS ***
?? tests/VenueOS.Services.Tests/MacroDirectiveParserTests.cs           (prior pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroModelsTests.cs                    (prior pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroRunnerTests.cs                    (prior pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroServiceTests.cs                   (rewritten this pass)
```

**Confirmation: nothing has been staged, committed, pushed, tagged, branched, or released.** The pre-existing
modified/untracked files listed above were already present before this task and were not touched by it. Giveaways'
own files were not touched at all in this pass — the only shared-code change is `ModulesSettingsPage.cs`/
`SettingsScreen.cs`, justified in §2 above as absolutely required and safe.

---

# LIVE QA FIX — LIVE TILE → FAUX HOTBAR DRAG/DROP

A narrow, targeted correction for one specific live-verified defect: dragging a macro from the Macro Live tile
launcher onto a visible faux hotbar slot did not work in actual FFXIV/Dalamud, despite the code compiling and every
automated test passing. **`MacroRunner`, the multiline editor, `/actionready`, `/venueos macro`, and Giveaways were
not touched in this pass** — the fix is confined to one file, `MacroHotbarRenderer.cs`.

## 1. Exact Root Cause

`MacroHotbarRenderer.DrawBar` draws, in Edit Hotbars mode, a full-bar `InvisibleButton` ("the drag layer" —
`DrawDragLayer`, used to reposition the whole bar) FIRST, then each of the 12 slots' own smaller `InvisibleButton`
on top of it, at the same screen positions the drag layer already covers. The previous pass's implementation (and
its documentation) assumed **"the later-drawn item wins hit-testing where items overlap."** That assumption is
backwards for Dear ImGui. The actual rule: **the FIRST item submitted each frame that the mouse is over claims
`ImGui`'s internal hovered-item state; a LATER item overlapping the same screen region is locked out of hover
entirely, unless the EARLIER item is explicitly marked as overlappable.** Because the drag layer was never marked
that way, it silently claimed mouse hover for the ENTIRE bar every single frame while Edit Hotbars was on — no
individual slot's `InvisibleButton` could ever become the hovered item, no matter where the mouse was within the
bar.

`ImGui.BeginDragDropTarget()` requires the CURRENTLY HOVERED item to be the one it's called against. Since a slot
could never become hovered, `BeginDragDropTarget()` never returned true for any slot, so `AcceptDragDropPayload`
never had a chance to run, and every drop was silently swallowed. Meanwhile bar-repositioning (dragging the
background) kept working perfectly, because that only ever needed the drag layer ITSELF to be active — which it
still was. This is exactly the reported symptom: the hotbar renders, the tile launcher works, clicking runs
macros, but dragging a tile onto a slot does nothing.

## 2. Which Layer Was Responsible

**The faux-hotbar window/interaction layer (`MacroHotbarRenderer`) — specifically the TARGET side.** The drag
SOURCE (`MacroOperatorPanel.DrawMacroTile` → `MacroDragDrop.BeginSource`), the payload encoding/lifetime
(`MacroDragDrop`, ImGui's own cross-frame payload buffer), and the assignment/persistence layer
(`MacroService.DropMacroOntoSlot` → `SaveModuleConfig`) were all already correct — none of them needed to change.
The bug was entirely that the target slots could never become the ImGui-hovered item while the drag layer was
active over them.

## 3. ImGui Window-Flag Investigation

The faux hotbar's `ImGuiWindowFlags` (`NoTitleBar | NoCollapse | NoScrollbar | NoScrollWithMouse | NoResize |
NoMove | NoBackground | NoFocusOnAppearing`) were inspected specifically for anything that could suppress mouse
input — `NoInputs`, `NoMouseInputs`, `NoNavInputs`, or an equivalent click-through mechanism. **None is present or
was ever present.** The window itself receives mouse input normally in both locked and Edit modes (this is also
why locked-mode slot clicks already worked correctly, and why the drag layer's own bar-move dragging already
worked correctly — both are single-item interactions unaffected by the overlap bug). The root cause was the
ITEM-level overlap rule described in §1, not a window-level input-suppression flag.

## 4. Hotbar-Movement-vs-Slot-Target Conflict

Confirmed present, exactly as the task's own hint suspected: the whole-bar drag layer and the 12 slot targets DO
compete for the same screen region while editing. The fix (§5) resolves it by marking the drag layer explicitly
overlappable, which lets a slot win hover over its own sub-rectangle while the drag layer still owns hover (and
therefore still supports bar-repositioning) over every other pixel of the bar — the gaps between slots. No
separate "move region" vs "slot region" restructuring was needed; one API call resolves the priority correctly
while both interactions keep their full existing extent.

## 5. Exact Fix

`MacroHotbarRenderer.DrawDragLayer` (`src/VenueOS.Plugin/Macro/MacroHotbarRenderer.cs`):

```csharp
ImGui.InvisibleButton("##drag", size);
ImGui.SetItemAllowOverlap();
```

`SetItemAllowOverlap()` (this Dalamud ImGui binding exposes the older, post-hoc form of the API — called
immediately AFTER the item it applies to, not a newer pre-item `SetNextItemAllowOverlap()`, which this binding does
not expose; confirmed by inspecting the actual binding assembly, not assumed) marks the drag layer as safe to be
overridden by a later-submitted overlapping item. With this one call in place, each slot's own `InvisibleButton`
(still drawn after the drag layer, exactly as before) can now correctly become the hovered item whenever the mouse
is directly over it, letting `BeginDragDropTarget()`/`AcceptDragDropPayload()` fire for that slot.

A second, smaller improvement was made alongside it: slots now draw a visible highlighted border
(`ImGui.IsItemHovered()`, now correctly reachable) whenever a drag is hovering them in Edit mode — the "visible
hover/drop feedback" the original drag/drop requirements called for, which was never actually reachable before
this fix since slots could never register as hovered in the first place.

## 6. Click vs. Drag

Re-verified, not changed — `MacroOperatorPanel.DrawMacroTile`'s tile is a plain `ImGui.InvisibleButton`, whose
`clicked` return value uses Dear ImGui's default `PressedOnClickRelease` button behavior: a click registers only
if the mouse is RELEASED while still hovering the SAME item that was originally pressed. If the operator presses
the tile and drags away before releasing (over the hotbar window instead), the tile is no longer the hovered item
at release time, so `clicked` is `false` and the macro is never launched — this was already correct by construction
(standard ImGui button semantics), not something this fix needed to add. `MacroDragDrop.BeginSource` is called
unconditionally right after the button; ImGui only actually begins a drag once the mouse moves past its own drag
threshold while held, so an ordinary click-and-release still runs the macro exactly as before, coexisting with the
drag path with no interaction changes needed.

## 7. Payload Type

Unchanged — the same canonical `MacroDragDrop` helper (`src/VenueOS.Plugin/Macro/MacroIconPicker.cs`) used
identically by the Settings hotbar editor's palette/slot grid AND the live tile/hotbar overlay: a raw 16-byte macro
`Guid`, via `ImGui.SetDragDropPayload`/`ImGui.AcceptDragDropPayload`. No second, Live-only payload type exists or
was considered.

## 8. Cross-Window Drag/Drop

Confirmed working by design, unchanged by this fix: Dear ImGui's drag-drop payload is stored in the ImGui context
itself (not any VenueOS-owned object), and persists automatically across frames and across separate `ImGui::Begin`
windows for as long as the mouse button remains held — this is standard, intentional Dear ImGui behavior (the same
mechanism the engine's own drag-drop demo relies on), used here with no extra plumbing to make the Macro Live
window (source) and a separate faux-hotbar window (target) cooperate. The actual defect was never about
cross-window payload delivery — it was that the target slot could never become "hovered" in the first place (§1),
which would have blocked acceptance even for a SINGLE-window drag.

## 9. Logical Slot Resolution

Unchanged — `MacroHotbarLayouts.Position(layout, slotIndex)` still maps a logical slot index to its (column, row)
draw position, and `MacroService.DropMacroOntoSlot(hotbarIndex, targetSlot, macroId)` still assigns/swaps purely by
logical slot index, never by row/column. Verified with both `Grid12x1` and `Grid4x3` at the test level (`Changing_
layout_never_changes_logical_slot_assignments`) — a layout change never scrambles which macro occupies which
logical slot.

## 10. Persistence Behavior

Unchanged in mechanism, re-verified after this fix: `DropMacroOntoSlot` still calls the same `AssignSlot`/
`SwapSlots` → `SaveModuleConfig` path every other hotbar mutation uses. Three new tests strengthen this pass's
coverage specifically for the checklist items the task called out: a slot referencing a macro id that doesn't
resolve to any saved macro is accepted and round-trips safely across a reload without throwing
(`DropMacroOntoSlot_with_a_macro_id_that_does_not_exist_fails_safely`); assigning/swapping a macro onto hotbar
slots never touches or deletes the macro's own record
(`Assigning_a_macro_to_a_hotbar_slot_never_deletes_the_macro_itself`); and all four hotbars remain fully
independent in enabled state, layout, and slot assignments (`The_four_hotbars_remain_fully_independent`).

## 11. Tests Added/Updated

`tests/VenueOS.Services.Tests/MacroServiceTests.cs` gained three new facts (§10 above) covering exactly the
service-layer checklist items from the fix request that were not already covered by the prior pass's
`DropMacroOntoSlot_*`/`Changing_layout_never_changes_logical_slot_assignments`/`Hotbar_config_survives_the_same_
reload_boundary_as_the_macro_library` tests, which already proved items 2-5 of the checklist and were left
unchanged (adding a duplicate would have been meaningless). **No automated test attempts to prove rendered ImGui
drag/drop or the `SetItemAllowOverlap()` hover-priority fix itself works — that is fundamentally a live-rendering
concern outside what `VenueOS.Services.Tests` can exercise (NEW_MODULE_GUIDE.md §30: there is no `VenueOS.Plugin`
test project, deliberately). ACTUAL IMGUI DRAG/DROP STILL REQUIRES LIVE QA.**

## 12. Final Total Test Count

**878 tests, 0 failures** (`VenueOS.Core.Tests`: 4, `VenueOS.Venues.Tests`: 23, `VenueOS.Services.Tests`: 851 — 97
of which are Macro-specific, up from 94 before this pass).

## 13. Debug Build Result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 14. Release Build Result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 15. Exact Live Retest Still Required

Per the fix request's own tiny, intentionally minimal retest — none of which has been performed by this session:

1. Open Macro Live, make Hotbar 1 visible, turn Edit Hotbars ON.
2. Left-click, hold, and drag a saved macro tile; confirm a drag visibly begins (the small text-label tooltip
   `MacroDragDrop.BeginSource` shows).
3. Drag it outside the VenueOS tablet window and hover Hotbar 1 Slot 1; confirm the slot now visibly highlights
   (the border added in §5).
4. Drop; confirm Slot 1 immediately shows the macro's icon.
5. Turn Edit Hotbars OFF; click Slot 1; confirm the correct macro executes.
6. Turn Edit Hotbars back ON; drag a different macro onto another slot; confirm correct assignment.
7. Try a multi-row layout (e.g. 4×3); drop onto a slot in a different row; confirm the correct LOGICAL slot
   receives it.
8. Reload the plugin; confirm the assignment remains.
9. Confirm a plain click on a Live tile still executes its macro, and that starting a drag never executes one.
10. Confirm the hotbar can still be repositioned in Edit mode (drag an empty/background area), and that a locked
    hotbar never moves accidentally.

## 16. Files Changed

**Modified (this pass):**
- `src/VenueOS.Plugin/Macro/MacroHotbarRenderer.cs` — the one-line `SetItemAllowOverlap()` fix plus the slot hover
  highlight; corrected the stale/incorrect doc comments describing the old (wrong) z-order assumption.
- `tests/VenueOS.Services.Tests/MacroServiceTests.cs` — three new facts (§10/§11).
- `docs/MACRO_IMPLEMENTATION.md` — this section, plus a correction to §13's prior-pass claim.

**Untouched in this pass** (verified unchanged): `MacroRunner.cs`, `MacroDirectiveParser.cs`, `MacroBodyText.cs`,
`MacroEditorWindow.cs`, `MacroService.cs`, `MacroOperatorPanel.cs`, `MacroIconPicker.cs`/`MacroDragDrop`,
`DalamudActionReadyProbe.cs`, `Plugin.cs`, `ModulesSettingsPage.cs`, `SettingsScreen.cs`, and every other module's
own files (Giveaways included).

## 17. Git Status

No `git add`/`commit`/`push`/`tag`/`release`/`branch`/`reset`/`clean` was run at any point in this session (or any
prior Macro session). All changes exist only in the working tree. `git status --porcelain` immediately before this
report was written:

```
 M MODULE_DEVELOPMENT.md                                               (pre-existing, unrelated)
 M NEW_MODULE_GUIDE.md                                                 (pre-existing, unrelated)
 M README.md                                                           (pre-existing, unrelated)
 M docs/USER_MANUAL.md                                                 (pre-existing, unrelated)
 M repo.json                                                           (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Operations.cs                        (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Raffle/VenueRaffleClient.cs          (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs (pre-existing, unrelated)
 M src/VenueOS.Modules.Operations/VenueOS.Modules.Operations.csproj    (pre-existing, unrelated)
 M src/VenueOS.Plugin/NativeOperationsPanels.cs                        (pre-existing, unrelated)
 M src/VenueOS.Plugin/Plugin.cs                                        (Macro, earlier pass)
 M src/VenueOS.Plugin/Shell/Forms.cs                                   (pre-existing, unrelated)
 M src/VenueOS.Plugin/Shell/Icons.cs                                   (Macro, earlier pass)
 M src/VenueOS.Plugin/Shell/ModulesSettingsPage.cs                     (Macro, earlier pass)
 M src/VenueOS.Plugin/Shell/SettingsScreen.cs                          (Macro, earlier pass)
 M src/VenueOS.Plugin/TournamentControlOperatorPanel.cs                (pre-existing, unrelated)
 M src/VenueOS.Plugin/VenueOperationsDashboard.cs                      (pre-existing, unrelated)
 M src/VenueOS.Plugin/packages.lock.json                               (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/ModuleDisplayOrderTests.cs             (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/RaffleClientTests.cs                   (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/TournamentControlTests.cs              (pre-existing, unrelated)
 M tests/VenueOS.Services.Tests/UnfinishedModuleDefaultsTests.cs       (pre-existing, unrelated)
?? (many pre-existing untracked files from Giveaways/Block Letters/Raffle/Tournament reconstruction — unrelated)
?? src/VenueOS.Modules.Operations/Macro/                               (Macro, earlier passes)
?? src/VenueOS.Plugin/Macro/                                           (contains this pass's MacroHotbarRenderer.cs fix)
?? tests/VenueOS.Services.Tests/MacroBodyTextTests.cs                  (earlier pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroDirectiveParserTests.cs           (earlier pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroModelsTests.cs                    (earlier pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroRunnerTests.cs                    (earlier pass, unchanged)
?? tests/VenueOS.Services.Tests/MacroServiceTests.cs                   (this pass — 3 new facts)
```

## 18. Confirmation

Nothing has been staged, committed, pushed, tagged, branched, or released — this pass, or any prior Macro pass.
Giveaways' own files were not touched at all. No global interface cleanup was performed. Macro was not promoted
out of Under Development.
