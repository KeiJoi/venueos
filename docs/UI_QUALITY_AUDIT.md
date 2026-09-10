# VenueOS 0.3.0 — UI Quality Audit

Living document for Part II/IV of the 0.3.0 post-release quality pass (see the audit brief). Built from direct
inspection of `Shell/*`, every module's operator panel(s), the faux hotbar renderer, and every auxiliary window —
not from `UI_STATUS.md`'s historical Phase 3G snapshot, which predates Giveaways, Block Letters, Macro, the Raffle
short-link feature, and this pass's session gate. Per the audit's explicit sequencing, this inventory (§1-§6) was
written before any visual cleanup began; §7 records what this pass actually changed as a result.

## 1. Shared UI architecture (the foundation everything else should use)

| Layer | File(s) | Purpose |
|---|---|---|
| `UiKit` | `Shell/UiKit.cs` | Cards, buttons (Primary/Ghost/Danger/Icon), badges, empty/warning/error/loading states, section headers, dividers, `SafeDraw`, window theme push/pop, venue frame/chassis brand, `ConfirmDialog`, `TextInputModal` |
| `Forms` | `Shell/Forms.cs` | `TextField`, `MultilineField`, `NumericField`, `ComboField`, `SearchBox`, `Segmented` (also VenueOS's tab strip), `FieldLabel` |
| `AppTile`/`Hotbar` | `Shell/AppTile.cs`, `Shell/Hotbar.cs` | Home app-icon tile; live-operation fixed-slot picker (Greeter's DJ hotbar) |
| `AppFrame` | `Shell/AppFrame.cs` | Embedded module chrome: icon+name+description header, pop-out button, scrollable content child, `SafeDraw` wrap |
| `ModuleWindowManager`/`ModuleWindowHeader` | `Shell/ModuleWindowManager.cs`, `Shell/ModuleWindowHeader.cs` | Detached-window registry/chrome — one shared header for every module |
| `TabletHeader`/`HomeScreen` | `Shell/TabletHeader.cs`, `Shell/HomeScreen.cs` | Global toolbar; Home grid + operations dashboard |
| `SettingsScreen` + pages | `Shell/SettingsScreen.cs`, `VenueSettingsPage.cs`, `AppearanceSettingsPage.cs`, `ModulesSettingsPage.cs`, `GeneralSettingsPage.cs`, `DiagnosticsSettingsPage.cs` | Settings nav shell + per-category pages, zero per-module field knowledge |
| `VenueSwitchCoordinator` | `Shell/VenueSwitchCoordinator.cs` | Single narrow "does leaving this venue lose something" confirmation hook |

This layer is broadly healthy and is the correct foundation to build on — it is not being replaced. Raw-ImGui usage
outside this layer is the exception, not the rule (§4).

## 2. Modal / confirmation infrastructure

Two single-purpose shared modals exist: `UiKit.ConfirmDialog` (destructive confirmation) and `UiKit.TextInputModal`
("one field, one confirm"). Both are instantiated per-panel as `private readonly ... = new();` fields — roughly 15
`ConfirmDialog` instances and, before this pass's fix, 3 `TextInputModal` instances (`MairsEditorOperatorPanel`
×2, `VenueSettingsPage`'s "+ Add Venue" ×1) across the plugin.

**Confirmed defect (fixed this pass, §7):** `TextInputModal.PopupId`/`ConfirmDialog.PopupId` were each a single
`private const string` — identical across **every** instance of the class, by design ("normally only one such
modal is ever open at a time"). This is the exact bug class the Giveaways preset-authoring pass already found and
fixed once (`docs/GIVEAWAYS_IMPLEMENTATION.md`): two live instances sharing one popup identity render into the
SAME ImGui popup when both have been requested, producing duplicated/merged-looking controls.

- **`MairsEditorOperatorPanel.cs`** declared two `TextInputModal` fields (`newSetModal`, `importAsNewModal`) — a
  live, unfixed instance of the identical bug, undetected until this audit.
- **`ConfirmDialog`** carries the same systemic risk at a wider scope: it is shared across essentially every
  module, and VenueOS explicitly supports multiple simultaneously-open detached module windows. Two different
  modules' `ConfirmDialog`s both being mid-request in the same frame was never actually observed, but nothing
  structurally prevented it — the class's identity was never per-instance.

Custom, single-purpose popups exist outside this shared pair and were checked individually: `GiveawayPresetEditorModal`
(own `PopupId`, instantiated exactly once), `VenueSwitchCoordinator`'s error popup (own `popupId`, single
instance), and a legacy custom modal in `NativeOperationsPanels.cs` (own `PopupId`, single instance) — none of
these currently collide, but each is the same "one shared constant per class" shape and would reproduce the same
bug if a second instance were ever added without noticing.

## 3. Detached windows / overlays / auxiliary windows

| Surface | Owner | Session-gated (post 0.3.0)? | Notes |
|---|---|---|---|
| Main tablet | `Plugin.Draw` | Yes — suppressed unless logged in, or the ShoutRunner travel exception applies | See `docs/SESSION_PRESENTATION_GATE.md` |
| Detached module windows | `ModuleWindowManager` | Yes, per-module via the gate predicate; ShoutRunner's own detached window is the one exception | |
| Bingo Called Numbers / Player Cards / Call Alert | `BingoCalledNumbersWindow`/`BingoPlayerCardViewerWindow`/`BingoCallAlertWindow`, drawn directly from `Plugin.Draw` | Yes (this pass) | Previously gated only on `bingoModule.IsEnabled` |
| Giveaways tracker window | `giveawaysPanel.TrackerWindow`, drawn directly from `Plugin.Draw` | Yes (this pass) | Same prior gap |
| Macro faux hotbars | `MacroHotbarRenderer.DrawAll`, drawn directly from `Plugin.Draw` | Yes (this pass) | The audit's cited example defect — could previously render over the title screen |

Every detached window shares one header (`ModuleWindowHeader`) and one theme push/pop path — no per-module chrome
divergence found. All ImGui `Begin` calls for the main tablet and detached windows use `NoTitleBar`, confirmed by
grep (2 call sites total, both flagged).

## 4. Raw ImGui usage outside `Shell/`

A fresh grep (this audit; `UI_STATUS.md`'s "zero matches" claim predates these files) for
`ImGui.Button/InputText/Checkbox/Combo/InputInt/InputFloat` outside `Shell/` found 6 call sites in 4 files:

| File | Line | Call | Assessment |
|---|---|---|---|
| `Bingo/BingoPlayerCardViewerWindow.cs` | 149, 172 | `ImGui.Button` (card grid header/cell) | Deliberate: renders a literal Bingo card grid, not a VenueOS control — same category of intentional exception as the Macro hotbars' non-VenueOS HUD look. Not a defect; documented here so it's not mistaken for an oversight later. |
| `ShoutRunnerOperatorPanel.cs` | 306 | `ImGui.InputText("##destination", ...)` | An unlabeled inline list-row field (editing one destination in a reorderable list) — `Forms.TextField` always draws a label above the field, which doesn't fit a compact list row. Legitimate gap: `Forms` has no unlabeled/inline field variant. |
| `TournamentControlOperatorPanel.cs` | 209 | `ImGui.InputText("##name", ...)` | Same shape — inline contestant-rename-in-place inside a roster row. |
| `VenueBingoOperatorPanel.cs` | 580 | `ImGui.InputText("##playerlink", ..., ImGuiInputTextFlags.ReadOnly)` | A read-only, copyable display field — `Forms.TextField` has no read-only mode either. |
| `VenueBingoOperatorPanel.cs` | 757 | `ImGui.InputText("##payoutTarget", ...)` | Same unlabeled-inline-field gap as the ShoutRunner/Tournament cases. |

None of these five `InputText` cases are a raw-ImGui-look regression in practice (they render as a plain text box
either way, same as `Forms.TextField`'s own field would) — the actual gap is that `Forms` has no unlabeled/inline
or read-only field variant, so three otherwise-reasonable call sites fall back to raw ImGui instead of a shared
component. Recorded as a real, if minor, `Forms` gap rather than three isolated one-offs.

## 5. Credential fields (§9a / §20 compliance check)

Spot-checked every module with a backend credential (Raffle, Bingo, Trivia, TournamentControl): all use
`Forms.TextField` with the default `password: false` — no call site anywhere sets `ImGuiInputTextFlags.Password`.
Confirmed compliant, no regression found.

## 6. Session/login gating

Was completely absent before this pass (§3 above) — the single largest architectural gap this audit found. Fixed
centrally; see `docs/SESSION_PRESENTATION_GATE.md` for the full design and proof.

## 7. What this pass changed (Stage 4 — proportional, inventory-driven)

Per the audit's explicit "not a module rewrite" instruction, this pass fixed what the inventory above surfaced as
concrete, mechanical defects — not a redesign of any module's structure or visual language:

1. **`ConfirmDialog`/`TextInputModal` given per-instance identity**, closing the whole bug class rather than only
   the one confirmed Mair's Editor instance. Every existing call site updated mechanically (a one-line change
   each); behavior is unchanged for every panel that only ever had one instance in play.
2. **Mair's Editor's two `TextInputModal` fields** (`newSetModal`, `importAsNewModal`) now carry distinct identity
   as a direct consequence of (1) — the duplicate-controls bug this would have eventually reproduced (matching the
   Giveaways precedent exactly) cannot occur.
3. **Macro hotbar visual polish** (already done in Part I/1B): the dedicated drag-handle strip replacing the
   whole-bar overlap region.
4. **Raffle's Publish & Live Links card** (already done in Part I/1C): short link as the primary display, full
   link demoted to an explicit "Show Full Links" toggle, plus a warning state + retry action for the
   short-link-not-minted-yet case.

## 8. What this pass deliberately did NOT do, and why (reported, not silently skipped)

- **No `Forms` unlabeled/inline or read-only field variant was added.** This is a real, minor gap (§4) but adding
  a new shared component not requested by the audit brief, for three low-risk existing call sites that don't
  visually regress anything, is the kind of scope expansion the brief explicitly warns against ("do not invent
  unrelated refactors"). Recorded here as a legitimate candidate for a future `Forms` addition if a new module
  needs the same shape.
- **No line-by-line visual redesign of all 13 modules' spacing/hierarchy/typography was performed** — that
  judgment genuinely requires seeing the rendered UI live in Dalamud (`NEW_MODULE_GUIDE.md` §41a's Visual
  Definition of Done is explicit this can't be satisfied from a terminal session), and remains explicitly left to
  the user's live QA.
- **CORRECTED (see §11-15 below):** this section originally also claimed "no known concrete defect" purely because
  every module already uses `UiKit`/`Forms` consistently. Live inspection proved that reasoning insufficient —
  Macro's Create/Edit window used `Forms`/`UiKit` throughout yet still presented as a raw native ImGui window,
  because using the right field/button components says nothing about a window's own chrome (native title bar vs.
  VenueOS chrome), footer/scroll structure, or redundant/missing headings. §11-15 is the corrected inventory,
  auditing every VenueOS window/popup by its actual construction rather than by its control call sites, and fixing
  what was found. The distinction that still holds: source-level construction (chrome, structure, component
  choice, hierarchy) is verifiable and was verified; rendered pixel-level polish (exact spacing/alignment once
  drawn) is not, and remains the user's to confirm live.

## 9. Suspicious ID patterns checked and found clear

- `ImGui.PushID`/`PopID` pairing: every list-row/tile loop found (`MacroOperatorPanel.DrawMacroTile`,
  `MacroIconPicker`'s palette, `VenueBingoOperatorPanel`'s payout rows, etc.) correctly scopes with the row's
  stable identity (a `Guid`/index), not a name or position that could collide across renders.
- Window IDs: both `ImGui.Begin` call sites for the main tablet and detached windows use a stable `###`-suffixed
  id independent of the visible/venue-dependent label — confirmed no venue-switch-resets-position regression.
- Icon keys (`AppIcons`): grepped `Icons.cs`'s `case` list — no two modules share a key (re-verifying the Phase 3B
  finding still holds after Giveaways/Block Letters/Macro were added).

## 10. Verification

Final re-run after every change in this entire 0.3.0 pass (Trivia auth fix, Macro live-crash fix + confirmed live
drag/drop acceptance, Raffle short links, session gate, and this section's ID-collision fix):

- `dotnet build VenueOS.sln -c Debug` — 0 warnings, 0 errors.
- `dotnet build VenueOS.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test VenueOS.sln -c Debug` — **907 passed, 0 failed, 0 skipped** (4 + 23 + 880 across
  `VenueOS.Core.Tests`/`VenueOS.Venues.Tests`/`VenueOS.Services.Tests`), up from the 878-test 0.3.0 baseline (+29:
  2 Trivia, 10 Raffle-client/service, 6 SessionPresentationGate, 4 ShoutRunner `IsActive`, 7 `MacroDragPayloadCodec`).
- Raffle backend (Node, `C:\FFXIVplugs\ffxivraffle4all\backend`): `node --test` — 36 passed, 0 failed (10 of these
  are the new short-link tests).
- Macro Live tile → faux hotbar drag/drop: **live-QA confirmed working** by the user (`docs/MACRO_IMPLEMENTATION.md`
  §27-30) — the only item in this pass that required the user's own in-game confirmation and received it.

---

# SCOPE CORRECTION — dialog/editor CHROME, not just raw-ImGui call sites

§1-§10 above interpreted "no raw `ImGui.Button`/`InputText`/etc. call sites" as sufficient evidence of visual
compliance. Live inspection found this insufficient: **Macro's Create/Edit Macro window used only `Forms`/`UiKit`
components internally, and still presented as a raw/native ImGui window** — because the window itself never
adopted `NoTitleBar` + custom VenueOS chrome, so ImGui's own default title-bar decoration was the FIRST thing the
operator saw, regardless of how well-styled the content beneath it was. "No raw control call sites" was necessary
but not sufficient evidence of compliance; this section corrects that and inventories every VenueOS-generated
window/popup by its actual chrome, not just its control call sites.

## 11. Every top-level VenueOS window/popup, audited by chrome (exhaustive — confirmed by grepping every
`ImGui.Begin(`/`BeginPopupModal(` call site in `src/VenueOS.Plugin`, not sampled)

| Surface | File | Chrome before this section | Chrome after |
|---|---|---|---|
| Main tablet | `Plugin.cs` | `NoTitleBar` + `TabletHeader` (bespoke) | unchanged — already correct |
| Every detached module window | `ModuleWindowManager.cs` | `NoTitleBar` + `ModuleWindowHeader` | unchanged — already correct |
| Bingo Call Alert | `BingoCallAlertWindow.cs` | `NoTitleBar` + `ModuleWindowHeader` | unchanged — already correct |
| Bingo Called Numbers | `BingoCalledNumbersWindow.cs` | `NoTitleBar` + `ModuleWindowHeader` | unchanged — already correct |
| Bingo Player Cards | `BingoPlayerCardViewerWindow.cs` | `NoTitleBar` + `ModuleWindowHeader` | unchanged — already correct |
| Giveaways Roll Tracker | `GiveawaysTrackerWindow.cs` | `NoTitleBar` + `ModuleWindowHeader` | unchanged — already correct |
| Macro faux hotbars ×4 | `MacroHotbarRenderer.cs` | `NoTitleBar`, deliberately NOT VenueOS-chromed (documented HUD exception — blends into FFXIV's own interface by design, spec-required) | unchanged — intentional, not a defect |
| **Create/Edit Macro** | `MacroEditorWindow.cs` | **Native ImGui title bar** (`ImGui.Begin` with no `NoTitleBar`) — the confirmed live defect | **Fixed**: `NoTitleBar` + new `DialogHeader.Draw` |
| **Giveaway Preset editor** | `GiveawayPresetEditorModal.cs` | Native popup title bar (generic, non-dynamic "Giveaway Preset") | **Fixed**: `NoTitleBar` + `DialogHeader.Draw`, title now dynamic ("New"/"Edit Giveaway Preset") |
| **Add/Edit VIP** | `NativeOperationsPanels.cs` (`VipEditDialog`) | Native popup title bar (generic "VIP") + a SECOND, duplicate themed title drawn as content ("Add VIP"/"Edit VIP") | **Fixed**: `NoTitleBar` + `DialogHeader.Draw` with the dynamic title; the duplicate inline text removed |
| **Venue switch failed** | `VenueSwitchCoordinator.cs` | Native popup title bar was the ONLY heading at all — no themed content title existed | **Fixed**: `NoTitleBar` + `DialogHeader.Draw`, error body switched from plain `TextWrapped` to `UiKit.ErrorState` |
| Confirm dialog (~15 instances across the plugin) | `Shell/UiKit.cs` (`ConfirmDialog`) | Native popup title bar (generic "Confirm") ABOVE its own already-themed title text — a redundant double heading | **Fixed**: `NoTitleBar` (kept its own themed title+divider — proportionate for a small single-purpose dialog, see §12) |
| Text input modal (Add Venue, Mair's Editor New Set/Import As New) | `Shell/UiKit.cs` (`TextInputModal`) | Same redundant double-heading as `ConfirmDialog` | **Fixed**: `NoTitleBar`, same proportionate treatment |

Every one of these is now either using the full `DialogHeader`/`ModuleWindowHeader` chrome, or (for the two small
single-field/single-message dialogs) has had the redundant native chrome removed while keeping their own themed
heading — **there is no longer any VenueOS-generated window or popup anywhere in the plugin showing ImGui's raw
native title bar.**

## 12. New shared component: `DialogHeader`

`src/VenueOS.Plugin/Shell/DialogHeader.cs` — title text + Close button, raised-surface bar, drag handle in the
empty middle: the same visual language as the existing `ModuleWindowHeader`, minus the module-specific icon and
Settings gear (an editor/dialog isn't a registered module, and "Settings" has no meaning for it). Reused by three
of the four fixed surfaces above (the fourth pair, `ConfirmDialog`/`TextInputModal`, are deliberately NOT given
the full header — see below). This is "the smallest appropriate shared component" for the recurring pattern,
per the brief's explicit instruction, rather than three separate one-off header implementations.

**Why `ConfirmDialog`/`TextInputModal` got a lighter fix (`NoTitleBar` only, no `DialogHeader`) while the other
four got the full header:** both already draw their own themed title line as the first thing inside the popup
content (`ImGui.TextUnformatted(title)` immediately followed by `UiKit.Divider`) — adding a second, heavier header
bar above that would be a redundant double-heading in the OTHER direction (themed instead of native, but still
two headings for one ~380px single-purpose confirmation). Removing only the native chrome and keeping their
existing lightweight title is the proportionate fix for a small dialog, matching `NEW_MODULE_GUIDE.md`'s explicit
"proportionate, not ceremonial" principle — the four larger, multi-field editors/dialogs get the heavier header
because they don't already have an equivalent themed heading of their own.

## 13. Additional presentation defect found and fixed: pinned footer buttons

`MacroEditorWindow`'s Save/Cancel buttons could scroll out of view together with the rest of the window's content
(a plain `ImGui.Begin` window scrolls as one unit unless `NoScrollbar` is set, which it wasn't) — unlike
`GiveawayPresetEditorModal`, which already wrapped its content in a `BeginChild` reserving fixed footer space
below it. Fixed: Macro's editor content (Name/Icon/Delay/Divider/Body/errors) now lives inside its own
`BeginChild`, with Save/Cancel drawn afterward outside it — pinned, matching the Giveaways editor's proven pattern,
never obscured or scrolled away regardless of window size or macro body length.

## 14. Surfaces checked and found already compliant (not just "no raw controls" — chrome, hierarchy, and state
presentation all inspected)

- **Settings → Modules → Macro → Hotbars** (enable/layout/scale/transparency, macro palette, slot grid): uses
  `BeginSectionCard`/`SectionHeader`/`Forms`/`Toggle`/`ConfirmDialog` throughout; the palette/slot icon grids use
  the same manual `InvisibleButton` + icon-draw pattern already established and consistent across Bingo's card
  grid and the live tile launcher — this is the established "icon slot" visual language, not raw ImGui.
- **Mair's Editor's "New Set"/"Import As New" flows**: both route through the now-fixed `TextInputModal`, so they
  inherited the chrome fix with no separate change needed. The question-authoring surface itself is inline
  (embedded in the panel, not a separate window), already using `Forms`/`UiKit` throughout.
- **Theme application**: `MacroEditorWindow` correctly does its own `PushWindowTheme`/`PopWindowTheme` (it's a
  genuine separate top-level window, matching every detached window's own pattern); the popup-based dialogs
  correctly do NOT push their own (they render nested inside whatever already-themed parent frame opened them,
  same as every other popup in the plugin) — no theme-application inconsistency found.
- **`Dalamud.Interface.ImGuiFileDialog`-based file pickers** (Raffle's XLSX import/export, if/where used): these
  are a genuine OS-level native file-picker widget from a Dalamud-provided library, not VenueOS-authored UI —
  explicitly exempt from VenueOS chrome for the same reason the Macro faux hotbars are exempt (a native picker is
  *supposed* to look like the OS's own file dialog), not an oversight.

## 15. Verification (this section's changes)

`dotnet build VenueOS.sln` (Debug/Release): 0 warnings, 0 errors, both re-run after §11-§13's changes. No pure
logic changed in this section (ImGui rendering/chrome only), so the test suite total is unchanged from §10's
907/907. Source-level construction (chrome, hierarchy, redundant-heading removal) was verified as described above,
using patterns already proven correct elsewhere in the same codebase (`ModuleWindowHeader`,
`GiveawayPresetEditorModal`'s pinned-footer pattern); rendered pixel-level correctness was explicitly left to the
user's live QA.

## 16. Live QA result — ACCEPTED

The user performed live visual inspection of the updated interfaces (§11-§13's chrome fixes: Create/Edit Macro,
Giveaway Preset editor, Add/Edit VIP, Venue Switch Failed, and the `NoTitleBar` fix to `ConfirmDialog`/
`TextInputModal`) and accepted the result. No further chrome/dialog-construction defects were reported. This closes
the "Cleanup All Interfaces" scope correction — §1-§15 together are the complete, live-accepted state of this UI
quality pass.
