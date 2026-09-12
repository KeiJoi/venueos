# Shouts — Generalization Implementation Report

Status: **live-QA PASSED and promoted to production in VenueOS 0.3.5.** The operator tested the generalized Shouts
module in Dalamud and confirmed acceptance ("tested, and it works like it should.") — see §9 for the checklist this
acceptance satisfies. This document records the generalization of the accepted, production "DJ Shouts" module
(VenueOS 0.3.4 — see `docs/DJ_SHOUTS_IMPLEMENTATION.md` for the original build/QA record, preserved unchanged) into
a general-purpose "Shouts" module: renamed, expanded from 5 to 15 assignable slots, and updated so the live module
only ever shows configured slots. The working execution engine (saved preset model, ordered lines, per-line
Yell/Shout, `ChatCommandService` dispatch, 2-second pacing, confirmed-dispatch gating, transactional preset editor,
chat byte-limit validation, timer completion semantics, per-venue persistence) is unchanged from the accepted 0.3.4
baseline — this pass touches naming, slot count, and live-visibility, not the engine.

## 1. What changed, and why

DJ Shouts' execution engine had already passed live QA and shipped in 0.3.4. The only reason to touch it again was
product scope: the module never actually did anything DJ-specific — it fires an operator-authored sequence of
Yell/Shout lines, full stop — so restricting its name and UI to "DJ" undersold it for venue hype, event notices,
reminders, requests, and closing announcements alike. This pass is a rename plus a capacity/visibility change, not a
redesign.

## 2. Module name and ID

- **Visible name:** `DJ Shouts` → `Shouts`.
- **Module ID:** left **unchanged** at `communication.djshouts`.

**Why the ID was not changed to `communication.shouts`.** NEW_MODULE_GUIDE.md §3 is explicit that the module ID is
a persistence key wholly independent of the display name, and §4 gives a live, in-production precedent for exactly
this situation: `communication.announcements`'s display name is "ShoutRunner" — the ID never changed when the
display name did. Renaming the ID here would require inventing a brand-new module-ID migration mechanism (none
exists anywhere in this codebase; `VenueProfileService` keys every payload by the exact
`(venueId, moduleId, schemaVersion)` tuple, with no aliasing or fallback-read-old-key primitive to build on) purely
for cosmetic ID/name symmetry, against every existing 0.3.4 user's already-persisted presets, slot assignments, and
Last Shout timestamp, for zero functional benefit. Keeping the ID eliminates that entire risk surface. The only
migration this generalization actually needs — expanding 5 named slots into 15 — is handled by an ordinary schema
version bump (§4), which is the one migration mechanism NEW_MODULE_GUIDE.md §13 already describes as the normal way
to evolve a module's config shape.

## 3. Source layout

Old (0.3.4):

```
src/VenueOS.Modules.Operations/DjShouts/DjShoutsModels.cs
src/VenueOS.Modules.Operations/DjShouts/DjShoutsService.cs
src/VenueOS.Plugin/DjShouts/DjShoutsOperatorPanel.cs
src/VenueOS.Plugin/DjShouts/DjShoutPresetEditorModal.cs
tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs
```

New:

```
src/VenueOS.Modules.Operations/Shouts/ShoutsModels.cs
src/VenueOS.Modules.Operations/Shouts/ShoutsService.cs
src/VenueOS.Plugin/Shouts/ShoutsOperatorPanel.cs
src/VenueOS.Plugin/Shouts/ShoutPresetEditorModal.cs
tests/VenueOS.Services.Tests/ShoutsServiceTests.cs
```

The old files/folders were deleted outright rather than kept alongside the new ones — this is a rename, not an
addition, and NEW_MODULE_GUIDE.md §21/§33 already establishes that a module owns coherent, non-duplicated file
ownership. Classes were renamed for consistency now that the module isn't DJ-specific: `DjShoutsService` →
`ShoutsService`, `DjShoutsModule` → `ShoutsModule`, `DjShoutPreset`/`DjShoutLine`/`DjShoutChannel` →
`ShoutPreset`/`ShoutLine`/`ShoutChannel`, `DjShoutSlotAssignments` → `ShoutSlotAssignments`,
`DjShoutsSettings` → `ShoutsSettings`, `DjShoutsOperatorPanel` → `ShoutsOperatorPanel`,
`DjShoutPresetEditorModal` → `ShoutPresetEditorModal`, `DjShoutPresetValidator` → `ShoutPresetValidator`,
`DjShoutLineBytes` → `ShoutLineBytes`. None of these renames touch the module ID string or any persisted JSON
property name that mattered for backward compatibility (see §4) — `ShoutPreset`/`ShoutLine`/`ShoutChannel`'s
constructor parameter names and the channel enum's member names (`Yell`/`Shout`) are identical to their
`DjShout*` predecessors, so an old preset library deserializes into the renamed types with no special handling.

`Plugin.cs`'s composition-root block was updated to construct `ShoutsService`/`ShoutsOperatorPanel`/`ShoutsModule`
in place of the `DjShouts*` equivalents; the `modules.Register(...)` call site itself needed no other change (Home,
embedded/detached rendering, and Settings → Modules remain fully generic per NEW_MODULE_GUIDE.md §6–§9). The
`microphone` icon key (`Icons.cs`) is unchanged — it still fits general announcements, per the task's explicit
guidance not to change it without a compelling reason.

## 4. Slot expansion: 5 → 15, and backward compatibility

**Old shape (0.3.4, schema v1):** `DjShoutSlotAssignments(Guid? Slot1, Guid? Slot2, Guid? Slot3, Guid? Slot4, Guid? Slot5)`
— five independently-nullable named properties.

**New shape (schema v2):** `ShoutSlotAssignments(IReadOnlyList<Guid?> Slots)` — a fixed-size, always-15-element
list. A list was chosen over 15 copy-pasted named properties (as the task explicitly permitted) because `Get`/`With`
become a single indexed accessor instead of a 15-armed switch, and the shape still round-trips through JSON as a
plain array with no key-type ambiguity.

**Migration mechanism.** `ShoutsSettings` moved from schema version 1 to schema version 2 specifically because its
`SlotAssignments` shape changed. `VenueProfileService.GetModuleConfig`/`SaveModuleConfig` already key every payload
by `(venueId, moduleId, schemaVersion)` — bumping the schema version means the old 5-slot payload and the new
15-slot payload live at two different keys under the same module ID, which is exactly what makes an explicit,
in-place migration possible without any new shared infrastructure. A new `VenueProfileService.HasModuleConfig`
method was added (returns whether a payload was actually saved at an exact key, with no default-fallback) so the
migration can tell "never saved" apart from "saved and happens to equal the default" — this is the single piece of
new shared infrastructure this pass added, and it is deliberately narrow and reusable for the next module that ever
needs the same kind of one-time schema migration.

`ShoutsService.Load(venueId)` calls a private `LoadOrMigrate`:

1. If a schema-v2 payload already exists for this venue, read and return it directly. (Every load after the first
   migration takes this branch — the v1 payload is never consulted again.)
2. Otherwise, if a schema-v1 payload exists (an un-migrated 0.3.4 user), read it via a dedicated
   `LegacyShoutsSettingsV1`/`LegacyShoutSlotAssignmentsV1` record pair that mirrors the exact old wire shape,
   transform it (`MigrateFromV1`), save the result under schema v2, and return it.
3. Otherwise (a brand-new venue that never had either), return `ShoutsSettings.Default()`.

**Transform:** old `Slot1`..`Slot5` map straight across to new slot numbers 1-5 (list indices 0-4, unchanged
meaning); new slots 6-15 default to unassigned (`null`). Presets, the selected-slot number, and the Last Shout
timestamp all carry over unchanged — none of their shapes differ between v1 and v2.

**Idempotency and safety.** Because step 1 above short-circuits on the new schema key, migration runs at most once
per venue, ever: no duplicate presets, no lost data, no way for a post-migration edit to be clobbered by a later
re-migration. The old v1 payload is deliberately left in place rather than deleted — `VenueProfileService` has no
delete-single-payload primitive, and there's no need to add one: an inert, superseded payload sitting alongside the
current one is harmless, and matches the "never discard a stored payload" caution already built into
`GetModuleConfig`'s own schema-mismatch recovery path (NEW_MODULE_GUIDE.md §13).

## 5. Live-visibility, ordering, and selection-fallback behavior

- **`ShoutsService.VisibleSlots`** is the single source of truth for what the live module renders: every slot 1-15
  that currently resolves to an existing preset, in ascending slot-number order. It is computed fresh from
  `Settings` on every access — there is no separate cached/stale visibility list to keep in sync, so a Settings
  change (assign/unassign a slot, delete a preset) is reflected the next time `Draw()` runs, with no restart and no
  explicit "refresh" step.
- **Order is never renumbered.** Configuring slots 1, 4, 7, and 12 shows exactly "Shout 1", "Shout 4", "Shout 7",
  "Shout 12" in that order — not "1st through 4th."
- **`ShoutsOperatorPanel.Draw()`** iterates `VisibleSlots` only. With zero visible slots it renders
  `UiKit.EmptyState(theme, "No Shout presets are assigned.", "Configure Shout Slot Assignments in Settings.")`
  instead of any slot buttons; the Shout button itself remains visible in the layout but stays disabled (it was
  already gated on `CanRunShout`, which is false with nothing selected).
- **`SelectSlot(slot)`** now refuses to select a slot with no assignment — the service-level enforcement of "hidden
  slots cannot become selected." `AssignSlot`/`DeletePreset` both call a private `ApplySelectionFallback()` after
  mutating slot assignments: if the currently selected slot is no longer in `VisibleSlots`, selection falls back to
  the first remaining visible slot; if none remain, the stale selection number is left in place but is inert (it
  resolves to no preset, nothing renders it as active, and `CanRunShout` is already false) — never a "hidden invalid
  selection" in the sense that matters, since "invalid" here means exactly "not in `VisibleSlots`," which is also
  exactly what the live module renders.
- **Settings (`DrawSettings`)** deliberately does the opposite: all 15 dropdown rows are always shown, since the
  operator needs to be able to configure any of them regardless of what's currently assigned elsewhere.

## 6. What did not change

- Preset model (`ShoutPreset`/`ShoutLine`/`ShoutChannel`), chat-byte validation, and the transactional preset editor's
  working-copy discipline — all carried over field-for-field from DJ Shouts.
- Execution: `/yell`/`/shout` dispatch through `ChatCommandService`, 2-second pacing between confirmed lines,
  dispatch-confirmation gating before advancing, and cancellation semantics.
- Last Shout timer semantics: updates only once the run's final non-empty line is confirmed dispatched; never on
  button press, never on failure, never on cancellation; persists per Venue Profile.
- Delete-preset safety: still a single atomic mutation that removes the preset and clears every (now up to 15)
  slot referencing it in the same save.
- Per-venue isolation: unchanged, and explicitly re-verified across all 15 slots (§7).

## 7. Tests

`tests/VenueOS.Services.Tests/ShoutsServiceTests.cs` replaces `DjShoutsServiceTests.cs` (renamed identifiers
throughout) and adds coverage for everything new in this pass:

- **Migration:** an existing 0.3.4-shaped (schema v1) config loads; old slot 1 and slot 5 assignments become new
  Shout 1/Shout 5; new slots 6-15 default to None; existing presets and the Last Shout timestamp survive; per-venue
  isolation holds for a venue that never had legacy config; migration is idempotent across a second and third fresh
  `ShoutsService` load (no duplicate presets, a post-migration mutation is never clobbered); a brand-new venue with
  no legacy payload at all gets plain defaults.
- **15 slots:** exactly 15 logical slots exist; slots across the full range (1, 2, 5, 8, 14, 15) assign and persist
  independently; assigning one slot never modifies another; venue isolation holds across slots 1 and 15
  simultaneously.
- **Live visibility:** zero assignments → zero visible slots; assigning slot 1 → only slot 1 visible; slots
  1/4/7/12 → exactly those four, in that numeric order; unassigning slot 7 → it disappears; assigning slot 15 →
  it appears in correct order; deleting the assigned preset immediately empties `VisibleSlots`.
- **Selection fallback:** unassigning the selected slot falls back to the first remaining visible slot; deleting
  the only assigned preset leaves selection resolving to nothing, safely, with `CanRunShout` false; an unassigned
  slot cannot become selected in the first place.
- **Existing engine regression:** every DJ Shouts-era test (Yell/Shout dispatch, mixed channels, pacing, final-line
  completion timestamp, failed/cancelled-run non-updates, byte-limit validation boundary/UTF-8/no-truncation
  behavior, transactional editor cancel-new/cancel-edit, per-venue persistence, module descriptor/dispose/venue-change
  behavior) was carried over renamed, not weakened or removed.

**Result:** `dotnet test VenueOS.sln -c Debug` — **1061/1061 tests passed** (4 VenueOS.Core.Tests + 23
VenueOS.Venues.Tests + 1034 VenueOS.Services.Tests, including 77 Shouts tests), 0 failures.

## 8. Build results

- `dotnet build VenueOS.sln -c Debug`: **Build succeeded, 0 Warning(s), 0 Error(s).**
- `dotnet build VenueOS.sln -c Release`: **Build succeeded, 0 Warning(s), 0 Error(s).**
- The implementation pass itself made no version bump, package, tag, release, or git commit, per its own explicit
  instruction — the user live-tested the generalized module first (see §9). Promotion/packaging/publishing as
  VenueOS 0.3.5 is recorded in `RELEASE.md`'s 0.3.5 entry and the corresponding release commit/tag/GitHub Release.

## 9. Live QA — PASSED

The operator ran this module live in Dalamud after the implementation pass above and confirmed acceptance
("tested, and it works like it should."), the same acceptance-reporting convention used for the original DJ Shouts
release (`docs/DJ_SHOUTS_IMPLEMENTATION.md` §22's "Alright, it works."). Recorded here as the checklist this
acceptance satisfies, per NEW_MODULE_GUIDE.md §30/§41a — automated tests cannot substitute for this, and this
report does not claim any more granular a result than what the operator actually confirmed:

1. **Upgrade path:** load a venue that has real 0.3.4 DJ Shouts data, confirm existing presets, slots 1-5, and the
   Last Shout timestamp all appear correctly under the new Shouts UI with no manual fix-up.
2. **Settings:** confirm the module is now labeled "Shouts," confirm all 15 assignment rows render, confirm slots
   6-15 start at "(None)."
3. **Live hidden-slot behavior:** assign presets to a non-contiguous set (e.g. 1, 4, 7, 12, 15), confirm the live
   screen shows only those five, in that numeric order, with no blank placeholders for the other ten.
4. **Dynamic visibility:** with Live open, go to Settings and unassign one of the visible slots — confirm it
   disappears from Live without a restart; assign a new slot — confirm it appears in correct order.
5. **Execution:** select a configured slot, press Shout, confirm line order/channel/pacing match the preset, and
   confirm Last Shout resets only after the final line.
6. **Empty state:** unassign all 15 slots, confirm Live shows the "No Shout presets are assigned" message (not 15
   blank controls) and the Shout button stays disabled.
7. **Selection fallback:** with a slot selected, delete its preset (or set it to None) from Settings, return to
   Live, confirm selection didn't get stuck on a hidden slot and the screen behaves safely.
8. **Persistence:** reassign several slots, `/xlrestart`, confirm presets/assignments/Last Shout all survive.
9. General checks per NEW_MODULE_GUIDE.md §32/§41a: Home tile shows "Shouts" with the microphone icon; embedded and
   detached rendering both work; Auto Pop-Out routes correctly in both settings states; all four themes render
   legibly; no control overlap at wide/normal/minimum window sizes.

## 10. Files changed (this pass)

**New:**
- `src/VenueOS.Modules.Operations/Shouts/ShoutsModels.cs`
- `src/VenueOS.Modules.Operations/Shouts/ShoutsService.cs`
- `src/VenueOS.Plugin/Shouts/ShoutsOperatorPanel.cs`
- `src/VenueOS.Plugin/Shouts/ShoutPresetEditorModal.cs`
- `tests/VenueOS.Services.Tests/ShoutsServiceTests.cs`
- `docs/SHOUTS_IMPLEMENTATION.md` (this file)

**Deleted:**
- `src/VenueOS.Modules.Operations/DjShouts/DjShoutsModels.cs`
- `src/VenueOS.Modules.Operations/DjShouts/DjShoutsService.cs`
- `src/VenueOS.Plugin/DjShouts/DjShoutsOperatorPanel.cs`
- `src/VenueOS.Plugin/DjShouts/DjShoutPresetEditorModal.cs`
- `tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs`

**Modified:**
- `src/VenueOS.Plugin/Plugin.cs` — composition root now constructs `ShoutsService`/`ShoutsOperatorPanel`/`ShoutsModule`.
- `src/VenueOS.Venues/VenueProfileService.cs` — added `HasModuleConfig(venueId, moduleId, schemaVersion)`, used by
  the migration above.
- `docs/USER_MANUAL.md` — §18 rewritten for Shouts (15 slots, hidden-unassigned behavior, upgrade note); module
  list, Module Quick Reference table, Persistence table, and one Troubleshooting entry updated.
- `README.md` — module lists updated from "DJ Shouts" to "Shouts."

**Not modified by the implementation pass (deliberately):** `docs/DJ_SHOUTS_IMPLEMENTATION.md` (preserved as the
historical record of the original 0.3.4 build and its live QA acceptance). The 0.3.5 release pass (after live QA
passed, §9) subsequently updated `RELEASE.md` (new 0.3.5 entry), `src/VenueOS.Plugin/VenueOS.Plugin.csproj`,
`src/VenueOS.Plugin/VenueOS.json`, and `repo.json` (version bump and, after the GitHub Release asset existed,
download links) — see `RELEASE.md`'s 0.3.5 entry for the full release record.

## 11. Confirmation

Community Presets was not started. No global UI cleanup was performed. No other module was touched. The
generalization implementation itself staged/committed/pushed/tagged/released/version-bumped nothing — that all
happened in the separate 0.3.5 release pass once live QA (§9) had passed, exactly as the release commit/tag/GitHub
Release for VenueOS 0.3.5 records.
