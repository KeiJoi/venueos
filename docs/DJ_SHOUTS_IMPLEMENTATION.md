# DJ Shouts — Implementation Report

Status: **live-QA PASSED and promoted to production in VenueOS 0.3.4.** The operator confirmed live in Dalamud
("Alright, it works.") — settings/preset creation, slot assignment, plugin-reload persistence, and full live
execution (five DJ slots, Yell/Shout dispatch in order, correct pacing, and the Last DJ Shout timer resetting only
on final-line success and surviving a panel close/reopen) all verified. `UnderDevelopment` removed and
`IsEnabled` now defaults to `true`, matching every other completed production module. This document is the record
of what was built and subsequently accepted, per NEW_MODULE_GUIDE.md §43's "after implementation" reporting
requirement.

## 1. Module purpose

DJ Shouts is a simple, operator-triggered announcement utility for venue DJs. An operator prepares reusable DJ
Shout presets ahead of time (in Settings) and fires the currently selected one manually during an event (in the
live module) — one preset, one button press, in order. It is **not** an automated scheduler, **not** ShoutRunner,
and **not** a Greeter behavior replacement: there is no automatic triggering, no target/guest identity logic, no
"Command After Greeting" equivalent, and no Behavior container.

## 2. Module ID

`communication.djshouts` — a new, independent persistence key. Never reuses Greeter's `core.greeter` ID or config,
and has no `Dependencies` on any other module (unlike Greeter, which depends on `core.attendance`).

## 3. Architecture

Following NEW_MODULE_GUIDE.md §33's canonical structure for a module owning coherent file ownership:

```
src/VenueOS.Modules.Operations/DjShouts/
  DjShoutsModels.cs    — DjShoutChannel, DjShoutLine, DjShoutPreset, DjShoutSlotAssignments, DjShoutsSettings,
                          DjShoutLineBytes, DjShoutPresetValidator
  DjShoutsService.cs   — DjShoutsService (business logic) + DjShoutsModule (IVenueModule wrapper)

src/VenueOS.Plugin/DjShouts/
  DjShoutsOperatorPanel.cs      — Draw() (live) / DrawSettings() (persistent config)
  DjShoutPresetEditorModal.cs   — dedicated transactional preset editor

tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs
```

New icon key `microphone` added to `src/VenueOS.Plugin/Shell/Icons.cs` (§21 below). `Plugin.cs` gained one
composition-root block constructing `DjShoutsService`/`DjShoutsOperatorPanel`/`DjShoutsModule` and one
`modules.Register(djShoutsModule)` call — nothing else needed editing for Home, embedded rendering, detached
rendering, Settings → Modules, or Auto Pop-Out, all of which are fully generic per NEW_MODULE_GUIDE.md §6–§9.

**State authority (NEW_MODULE_GUIDE.md §34a):** DJ Shouts has no backend and no shared/other-module state. Its
per-venue persistent config (`DjShoutsSettings` — the saved preset library, the five slot assignments, the
selected slot, and the last-successful-shout timestamp) is the ONLY durable state, saved through
`VenueProfileService.SaveModuleConfig`. The one in-flight run (`IsRunning`/`RunningPreset`) is ephemeral and always
resets to idle on venue switch, disable, or plugin reload.

## 4. Greeter patterns reused

- **Five-slot hotbar concept and UI component.** DJ Shouts uses the same `Shell/Hotbar.Slot` component Greeter's
  live panel uses for its five hotbar buttons, in the same "click a slot to select it, a separate button executes
  the selection" interaction shape.
- **Settings layout convention.** "Venue-scoped configuration" framing, a slot-assignment section above a saved-
  preset library section, search/filter, New/Edit/Delete — mirrors Greeter's Settings structure (and, more directly,
  Giveaways' compact-list-plus-dedicated-editor-modal split, since Greeter's own preset editor is still inline
  rather than a separate modal — NEW_MODULE_GUIDE.md §21 flags this as legacy debt not to copy forward).
- **Line-send pacing.** The 2-second delay between dispatched lines is reused verbatim from
  `GreeterService.OnStepDispatched`'s established, live-verified pacing (`NextAt = clock.UtcNow.AddSeconds(2)`) —
  DJ Shouts does not introduce a new configurable delay setting.
- **Dispatch-confirmation gating.** Advancing to the next line, and updating the completion timestamp, is gated on
  `ChatCommandService`'s `OnDispatched` callback actually confirming success — the same real-acceptance-gate pattern
  `GreeterService.OnStepDispatched` uses — never merely on having called `ChatCommandService.Enqueue`.
- **Chat dispatch itself** goes through the shared `ChatCommandService`, exactly like Greeter/VIP/Giveaways/Macro —
  no ad hoc chat mechanism.

## 5. Greeter behavior deliberately NOT reused

- **No Behavior container, no Auto Greet, no Repeat Greet Delay, no Command After Greeting.** DJ Shouts consists
  only of its configured Yell/Shout lines — there is no equivalent settings section at all.
- **No target/guest identity logic, no `GuestIdentity`, no Attendance/VIP integration.** DJ Shouts has no concept of
  "who" — it is a pure announcement fired at nobody in particular.
- **No automatic triggering of any kind** (no proximity trigger, no repeat timer, no visitor detection). The DJ
  Shout button is the only execution trigger.
- **No `IVenueDatabase`/SQLite-backed preset storage.** Greeter's preset library and hotbar assignments live in the
  shared `IVenueDatabase` (donor-parity SQLite table with a fixed Line1/Line2/Line3/Command shape). DJ Shouts needs
  an ORDERED list of lines with PER-LINE metadata (channel), which doesn't fit that fixed-column shape, and the task
  explicitly required independence from Greeter's runtime — so DJ Shouts uses the standard
  `VenueProfileService.GetModuleConfig`/`SaveModuleConfig` per-venue JSON config instead, the same pattern
  Giveaways/Macro/Block Letters/VIP's roster already use for a saved-object list.
- **No dependency on `core.attendance` or any other module** (`ModuleDescriptor.Dependencies` is empty).

## 6. Settings layout

`DrawSettings()` (persistent configuration only, per NEW_MODULE_GUIDE.md §8/§9):

1. An `InfoBanner` stating the venue-scoped configuration description.
2. **DJ Slot Assignments** — five `Forms.ComboField` rows ("DJ 1" … "DJ 5"), each assigning one saved preset or
   "(None)".
3. **Saved Presets** — search box, "New DJ Shout" button, a list of presets (name, line count, Edit/Delete), and
   the delete confirmation dialog. No Behavior section exists anywhere in this file.

## 7. Five DJ slots

`DjShoutSlotAssignments` holds five independently-nullable slots (`Slot1`…`Slot5`, not a `Dictionary<int,Guid?>`,
so every slot is always present and the shape round-trips through JSON with no key-type ambiguity). Each slot may
be assigned to one saved preset or left unassigned. Selecting a slot (`DjShoutsService.SelectSlot`) only changes
which preset the DJ Shout button will run next — it never executes anything by itself. Deleting an assigned preset
clears every slot referencing it in the same atomic save (`DjShoutSlotAssignments.ClearPreset`).

## 8. Preset model

```csharp
public sealed record DjShoutLine(string Text, DjShoutChannel Channel); // Channel defaults to Yell for every new line
public sealed record DjShoutPreset(Guid Id, string Name, IReadOnlyList<DjShoutLine> Lines);
```

A blank/whitespace-only line is a valid authoring state (an empty editor row) — it is simply never dispatched
(`DjShoutPreset.NonEmptyLines`/`HasExecutableLines`). A preset with zero non-empty lines CAN be saved (a legitimate
work-in-progress state, matching Giveaways' "an empty block is valid to save" convention) but CANNOT be executed.

## 9. Per-line Yell/Shout (the one deliberate departure from Greeter)

Every `DjShoutLine` carries its own `DjShoutChannel` (`Yell` or `Shout`) — unlike Greeter (one command destination
for the whole preset) or Giveaways (one channel per announcement block). The channel selector sits directly to the
right of each line's text field in the preset editor, using `Forms.Segmented`.

## 10. Default channel

Every newly created line defaults to `DjShoutChannel.Yell` — enforced by `DjShoutLine.Empty()` (used by the "+ Line"
button) and by `DjShoutPreset.CreateNew` starting with zero lines (so the first line added is always the default).
Covered by `Default_new_line_channel_is_yell`.

## 11. Execution sequence

On pressing **DJ Shout**: resolve the preset assigned to the currently selected slot, take its non-empty lines in
order, and for each one:

- `Channel == Yell` → dispatch `/yell <text>` through `ChatCommandService`.
- `Channel == Shout` → dispatch `/shout <text>` through `ChatCommandService`.

Advancing to the next line is gated on `ChatCommandService`'s `OnDispatched` callback confirming the previous line
actually sent (never merely on having enqueued it) — see §4. A failed dispatch stops the run immediately, reports
through `DiagnosticsService` (unless the failure is an intentional cancellation), and leaves the last-successful
timestamp untouched.

## 12. Delay/pacing

A fixed **2-second delay** between successive lines (`DjShoutsService.DelayBetweenLinesSeconds`), reused verbatim
from Greeter's own established pacing rather than a new configurable setting — see §4. Lines are never burst into
the same frame; each is scheduled via the shared `SchedulerService`.

## 13. Last DJ Shout timer semantics

`LastShoutCompletedAtUtc` updates **only** when the run's FINAL non-empty line is confirmed successfully dispatched
— never on button press, never on merely enqueuing a line, and never if the run is cancelled or a dispatch fails.
For a single-line preset, the timestamp updates as soon as that one line is confirmed sent. The live panel shows:
`Never` (no completed run yet), `MM:SS ago` (under an hour), or `Nh Mm ago` (an hour or more) — recomputed fresh
every `Draw()` call from the current clock, so it updates live while the panel is open with no separate ticking
state.

## 14. Timer persistence

`LastShoutCompletedAtUtc` is part of `DjShoutsSettings` and persists through the same
`GetModuleConfig`/`SaveModuleConfig` path as every other field — it survives closing/reopening the live panel,
switching modules, switching venues (and back), and a plugin reload, per the task's explicit requirement. This is
DJ Shouts' one deliberate exception to the otherwise-standard "live-run state is ephemeral" convention
(NEW_MODULE_GUIDE.md §13a): the task explicitly required the elapsed timer to outlive a panel close/reopen, which a
purely in-memory field cannot do.

## 15. Per-venue persistence

Everything — saved presets, the five slot assignments, the selected slot, and the last-shout timestamp — lives in
one `DjShoutsSettings` record keyed by `(VenueId, "communication.djshouts", schemaVersion: 1)` via
`VenueProfileService`. Venue A and Venue B never see each other's DJ Shouts data (`Venue_a_and_venue_b_slot_
assignments_remain_isolated`, `Venue_isolation_applies_to_the_persisted_timestamp`).

## 16. Transactional editor behavior

`DjShoutPresetEditorModal` mirrors `GiveawayPresetEditorModal`'s working-copy discipline exactly: it owns a local
`draft` (never `DjShoutsService.Settings` directly). **New:** seeds from `DjShoutPreset.CreateNew`; Save calls
`SaveNewPreset` (mints a fresh Id); Cancel/closing discards the draft with no service call at all — nothing is ever
created. **Edit:** seeds `draft` from a value-copy of the persisted preset (a record, so a `with`-edit can never
reach back into the original); Save calls `UpdatePreset(existingId, _ => draft with { Id = existingId })`; Cancel/X
discards the draft, leaving the persisted preset untouched. No field is saved per keystroke.

Per-line rows deliberately avoid `Forms.TextField`'s own embedded label (unlike Giveaways' single-purpose text-only
block lines): `TextField` fills the entire remaining row width, leaving no room for the trailing channel
selector/Up/Down/Remove controls DJ Shouts also needs on the same row. Per NEW_MODULE_GUIDE.md §35's reserved-region
rule, the trailing controls' fixed width is computed first and subtracted from the available width before the text
box is drawn (via `Forms.PushFieldStyle`/`PopFieldStyle` directly — the documented "rare inline case" they exist
for).

## 17. Delete / slot cleanup

Deleting a preset (`DjShoutsService.DeletePreset`) is a single atomic mutation: it removes the preset from the
library AND clears it from any DJ slot referencing it, in the same save — a deleted preset's Id can never be left
dangling in a slot assignment. Always confirmed via the shared `ConfirmDialog`, with consequence-stating text
("will be permanently deleted and cleared from any DJ slot it's assigned to. This cannot be undone."). A preset
that happens to be the currently RUNNING preset can still be deleted safely: `RunningPreset` is a captured
value-copy snapshot taken at `RunDjShout()` time, so deleting the saved library entry never affects an
already-in-flight run (mirrors `GiveawayService.RunningPreset`'s snapshot isolation).

## 18. Chat-limit validation

Every line's FULL dispatched command (`"/yell " + text` or `"/shout " + text`, not just the raw text) is measured
in UTF-8 bytes via the shared `BlockTextLength.CountBytes`/`BlockLettersLimits.ChatBytes` (500) — deliberately
including the command prefix, since the established 500-byte limit models FFXIV's actual chat INPUT BUFFER, which
holds the whole typed command. Validation happens at Save time (`DjShoutPresetValidator.Validate`): an oversized
line blocks Save with an inline error naming the exact line number and byte counts, and is also flagged live in the
editor (`WarningState` under the offending row) — never silently truncated. The ImGui input box itself allows typing
up to 600 characters (deliberately above the byte limit) precisely so the operator can see this warning rather than
being silently capped mid-keystroke.

## 19. Diagnostics / error handling

A failed chat dispatch during execution routes through `DiagnosticsService.RecordFailure` (message includes the
module ID and the failing line number), UNLESS the failure is an intentional cancellation
(`OperationCanceledException` from `Cancel()`/venue switch/disable), which is never reported as a scary user-facing
error. A failed dispatch stops the run immediately, never updates the last-successful timestamp, never corrupts
preset/slot state, and leaves the operator free to simply press DJ Shout again to retry.

## 20. Tests

58 automated tests added in `tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs`, covering: preset model/
persistence (create/edit/delete/cancel-new/cancel-edit, stable Id, default channel, multi-preset persistence, a
full serialize/deserialize round trip), line channels (Yell/Shout dispatch, mixed-order preservation, reorder/remove
safety), slots (per-slot persistence, independence, None handling, selected-slot resolution, venue isolation),
execution (single/multi-line order, blank-line skipping, empty-preset rejection, concurrent-run rejection, cancel,
failed-dispatch handling), the Last DJ Shout timer (button-press-alone, first-line, final-line, failure, cancel,
second-run replacement, reload/venue-isolation persistence), chat-byte-limit validation (boundary behavior, over-
limit rejection, UTF-8 multibyte counting, no silent truncation), and the module descriptor (ID, display name,
production/enabled-by-default defaults, icon key, Draw/DrawSettings delegate separation, venue-change reload,
dispose cancelling an in-flight run).

## 21. Build results

- `dotnet test VenueOS.sln -c Debug`: **1042/1042 tests passed** (4 VenueOS.Core.Tests + 23 VenueOS.Venues.Tests +
  1015 VenueOS.Services.Tests, including the 58 new DJ Shouts tests), 0 failures.
- `dotnet build VenueOS.sln -c Debug`: **Build succeeded, 0 Warning(s), 0 Error(s).**
- `dotnet build VenueOS.sln -c Release`: **Build succeeded, 0 Warning(s), 0 Error(s).**

## 22. Live QA — PASSED

The operator ran this checklist live in Dalamud and confirmed acceptance ("Alright, it works."). Recorded here as
the checklist that was actually exercised, per NEW_MODULE_GUIDE.md §30/§41a (automated tests cannot substitute for
this).

**Settings:**
1. Enable the DJ Shouts development module (Settings → Modules → DJ Shouts → toggle Enabled).
2. Open Settings → Modules → DJ Shouts → Configure.
3. Confirm no Behavior section exists.
4. Confirm exactly five DJ slot assignments (DJ 1–5).
5. Confirm a Saved Presets section exists.
6. Create a preset named "Friday DJ".
7. Add three lines: line 1 Yell, line 2 Shout, line 3 Yell.
8. Save.
9. Assign it to DJ 1.
10. `/xlrestart`.
11. Confirm the preset and DJ 1 assignment survive.

**Live:**
12. Open DJ Shouts Live.
13. Confirm five DJ slots are visible.
14. Select DJ 1.
15. Confirm the DJ Shout button is enabled.
16. Confirm the timer initially says "Never" (no prior successful run).
17. Press DJ Shout.
18. Confirm line 1 goes to `/yell`, line 2 to `/shout`, line 3 to `/yell`.
19. Confirm correct pacing/order (roughly 2 seconds apart).
20. Confirm the timer resets only after the final line.
21. Wait briefly and confirm the timer increments live.
22. Close/reopen the Live module.
23. Confirm the elapsed timer remains valid (does not reset to "Never" or zero).

**Other:**
24. Select an unassigned DJ slot.
25. Confirm the DJ Shout button disables.
26. Switch back to DJ 1.
27. Run again.
28. Confirm the new successful completion resets the timer.
29. Edit the preset (change text/channel on a line).
30. Confirm the changed text/channel persists after a reload.

Also verify, per NEW_MODULE_GUIDE.md §32/§41a generally: Home tile appears with the correct icon/name; embedded and
detached rendering both work; Auto Pop-Out routes correctly in both settings states; Dark/Light/Neon/Midnight all
render the microphone icon and every control legibly; no control overlap at wide/normal/minimum window sizes; a
forced chat-dispatch failure surfaces in Settings → Diagnostics with no secrets involved (none exist for this
module).

## 23. Files changed

**Implementation pass (new files):**
- `src/VenueOS.Modules.Operations/DjShouts/DjShoutsModels.cs`
- `src/VenueOS.Modules.Operations/DjShouts/DjShoutsService.cs`
- `src/VenueOS.Plugin/DjShouts/DjShoutsOperatorPanel.cs`
- `src/VenueOS.Plugin/DjShouts/DjShoutPresetEditorModal.cs`
- `tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs`
- `docs/DJ_SHOUTS_IMPLEMENTATION.md` (this file)

**Implementation pass (modified files):**
- `src/VenueOS.Plugin/Shell/Icons.cs` — added the `microphone` icon key and `DrawMicrophone`.
- `src/VenueOS.Plugin/Plugin.cs` — constructed `DjShoutsService`/`DjShoutsOperatorPanel`/`DjShoutsModule` in the
  composition root and added one `modules.Register(djShoutsModule)` call.

**0.3.4 release pass (additional modified files):**
- `src/VenueOS.Modules.Operations/DjShouts/DjShoutsService.cs` — promoted `DjShoutsModule` (`UnderDevelopment`
  removed, `IsEnabled` now defaults to `true`).
- `tests/VenueOS.Services.Tests/DjShoutsServiceTests.cs` — updated the descriptor test for the promoted defaults.
- `src/VenueOS.Plugin/VenueOS.Plugin.csproj`, `src/VenueOS.Plugin/VenueOS.json` — version bump to 0.3.4.
- `docs/USER_MANUAL.md` — added a DJ Shouts section (§18), renumbered §19–§24, updated the Module Quick Reference
  table, the Persistence table, and one Troubleshooting entry.
- `RELEASE.md` — added the 0.3.4 release entry.
- `README.md` — added DJ Shouts to the module lists.
- `repo.json` — added DJ Shouts to the Description module list (AssemblyVersion/download links/LastUpdate updated
  separately, after the release asset exists — see §26).

## 24. Git status

See the release report for the exact final `git status`, commit SHA, and tag — this implementation document
records the module itself, not the release mechanics.

## 25. Confirmation

DJ Shouts passed live QA and was promoted to production as part of VenueOS 0.3.4 (see §22/§3 above). Community
Presets was not started. No global UI cleanup was performed. No existing module was redesigned.
