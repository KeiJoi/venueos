# System Menu opening unexpectedly — 0.3.8 investigation (WP6)

Status: **IMPLEMENTED — awaiting FFXIV live QA.** The root cause is proven at source level (decompiled dependency +
full call-chain trace + a deterministic lifecycle reproduction). That the native game *stops* opening the menu cannot be
proven outside FFXIV and is listed under §18.

## 1. Observed defect

VenueOS causes the FFXIV System Menu to appear unexpectedly:

1. On every plugin reload (`/xlreload`) — highly reproducible.
2. On changing the active venue.
3. During training, "a couple of other" unrecorded VenueOS actions.

Hard invariant: no VenueOS action may open the System Menu (VenueOS has no feature whose purpose is to open it).

## 2. What native UI is it?

**Not observed at runtime** (no live game in this environment). What the source establishes is the *mechanism*, which
identifies the menu without needing the addon name: the trigger is a synthesized **Escape key event**, not an addon call.
Nothing in VenueOS references a System Menu addon, agent or command (searched: `SystemMenu`, `System Menu`, addon names,
`AtkUnitBase`, `ReceiveEvent`, `FireCallback`, `ExecuteCommand`). FFXIV's default response to Escape, with the character
in the world and nothing to cancel, is to open the System Menu — which matches the operator's visual identification. The
menu is therefore opening as a **consequence of an Escape input**, not by an explicit open call. I did not verify the
addon's internal name and do not claim one.

## 3. Trigger paths investigated

| Hypothesis from the brief | Result |
|---|---|
| Escape / synthetic key dispatch | **Found — the cause** (§10). One site in the whole codebase. |
| SendInput / keybd_event / PostMessage / SendMessage in VenueOS source | None directly. `SendMessage` is reached only through ECommons' `WindowsKeypress.SendKeypress` (decompiled, §9). |
| Leaked/stale key state, Dalamud key APIs (`IKeyState`, `IsKeyDown`, …) | Not used anywhere. |
| ImGui focus release / `SetKeyboardFocusHere` / `SetNextWindowFocus` | Present (Block Letters caret, `TextInputModal`, tablet/detached focus) but ImGui-only; none touches game input or Escape. |
| Popup/modal dismissal (`CloseCurrentPopup`) | ImGui-only, triggered by VenueOS buttons. Cannot emit a game key. |
| Party Finder / Bingo payout native calls | Targeted `AtkUnitBase.ReceiveEvent` / `FireCallback` at specific addons; no key input, no System Menu. |
| `AgentWorldTravel.HideAddon()/Hide()` (ShoutRunner) | Hides the World Travel addon only; not a System Menu path. |
| Game commands (`ChatCommandService`) | Only `/shout`, `/li`, etc.; none open the menu. |
| Shared lifecycle callback | **Yes — the funnel that reaches the cause** (§5–§8). |

## 4. Source inventory (menu/input/focus)

- **Synthetic key**: `ShoutRunnerAutomationService.DismissTransferUiOnceUnsafe` → `WindowsKeypress.SendKeypress(0x1B)`. The
  only occurrence of `WindowsKeypress`, `SendKeypress`, or `EscapeVirtualKey` in `src/`.
- **Native addon/agent**: `PartyFinderAutomationService` (LookingForGroup* addons), `BingoPayoutAutomationService`
  (trade addons), `ShoutRunnerAutomationService` (`AgentWorldTravel`). None sends keys.
- **ImGui focus/popup**: `BlockLettersOperatorPanel` (one-shot `SetKeyboardFocusHere`, WP5), `UiKit.TextInputModal`,
  `ConfirmDialog`, `VenueSwitchCoordinator` error popup, `Giveaway/ShoutPresetEditorModal`, VIP edit dialog,
  `Plugin.cs` / `ModuleWindowManager` `SetNextWindowFocus`. All ImGui-internal.
- **Comments mentioning Escape** (`GiveawayPresetEditorModal`, `ShoutPresetEditorModal`): ImGui popup dismissal notes only.

## 5. Plugin reload trace (`/xlreload`)

1. Dalamud calls `Plugin.Dispose()`: removes command/draw/update hooks, persists window geometry, then
   **`modules.DisposeAsync()`** (`Plugin.cs`).
2. `ModuleHost.DisposeAsync()` disposes modules in reverse order, unconditionally (no `IsEnabled` filter).
3. `ShoutRunnerModule.DisposeAsync()` → **`service.HardStop()`**.
4. `ShoutRunnerService.HardStop()` (before the fix) ran **`automation.Abort()` unconditionally**, even in state `Stopped`.
5. `ShoutRunnerAutomationService.Abort()` → `TryLifestreamAbort()` and
   `framework.RunOnFrameworkThread(DismissTransferUiOnceUnsafe)`.
6. `DismissTransferUiOnceUnsafe` hides `AgentWorldTravel` *if shown*, then **always** called
   `WindowsKeypress.SendKeypress(0x1B)` — an Escape delivered to the game window. Nothing to cancel → System Menu.
7. Construction of the new plugin instance: `venues.InitializeAsync()` → `NotifyVenueChangedAsync` →
   `ShoutRunnerModule.OnVenueChangedAsync` → `service.Load()` → **`HardStop()`** → the same Escape again (a second one,
   from the new instance). ShoutRunner is enabled by default, so this always runs.
8. UI registration/persisted-state restoration (`Plugin.cs`) performs no game input: windows never auto-open (§7 of the
   guide), and no focus request touches the game.

The trigger therefore fires from **disposal** (last moment of the old instance) and again from **first venue
activation** (construction of the new one); it does not depend on any VenueOS window being visible or any frame having
drawn. I did not observe the exact ordering of the two Escapes against the game's frame loop (no live runtime); see §19.

## 6. Venue-switch trace

`VenueSwitchCoordinator` → `VenueProfileService.SwitchAsync` → `ModuleHost.NotifyVenueChangedAsync` →
`ShoutRunnerModule.OnVenueChangedAsync` → `ShoutRunnerService.Load` → **`HardStop()`** → `automation.Abort()` → Escape.
Same call, same function. (`Load` does not reconstruct services; it hard-stops, reloads settings, clears the terminal.)

## 7. Do reload and venue switching share a cause?

**Yes — the same defect, not two independent triggers.** Both reach `ShoutRunnerService.HardStop()`, whose unconditional
`Abort()` issues the Escape. Fixed once at that funnel (plus the engine-level rule that guards the key itself).

## 8. Other callers of the same path

All reach `HardStop()`/`Abort()` and were fixed by the same change:

| Caller | Path |
|---|---|
| First venue activation at startup / login | `VenueProfileService.InitializeAsync` → `Load` → `HardStop` |
| Venue switch | `SwitchAsync` → `Load` → `HardStop` |
| Delete the *active* venue (auto-switches) | `DeleteAsync` → `SwitchAsync` |
| Venue-switch failure compensation (re-notify) | `SwitchAsync` catch → `NotifyVenueChangedAsync` |
| Disable ShoutRunner in Settings → Modules | `ShoutRunnerModule.IsEnabled = false` → `HardStop` |
| Plugin dispose / `/xlreload` / plugin disable / Dalamud unload | `Plugin.Dispose` → `HardStop` |
| Operator **Stop** while a run is active | `ShoutRunnerService.Stop` → `Abort` |
| In-run recovery loops (`DismissTransferUiAsync`, 5 presses 250 ms apart): logged-out limbo, congestion skip, failed transfer, DC-unavailable | `EnsureReadyAsync` / `TravelToWorldAsync` / `WaitForCrossDataCenterTransferAsync` |

The unrecorded "couple of other actions" are most plausibly members of this list (module toggle, delete-venue,
Stop, a failed/congested transfer). That is an inference from the trace, not an observation. Note the five-press loop
alternates the menu open/closed/open… so it can leave the menu open on an odd count.

## 9. Runtime/native instrumentation performed

- **Decompiled** ECommons 3.2.1.18 `WindowsKeypress.SendKeypress(int)` (ilspycmd): finds the game window and does
  `SendMessage(hwnd, WM_KEYDOWN=256, key, 0)` then `SendMessage(hwnd, WM_KEYUP=257, key, 0)`. So `SendKeypress(0x1B)` is a
  real `VK_ESCAPE` press delivered to the FFXIV window; nothing about it is scoped to a particular addon.
- **Deterministic lifecycle reproduction** (`SystemMenuEscapeTests`): the real `ShoutRunnerModule` registered on the real
  `ModuleHost` + `VenueProfileService`, with a counting `IShoutRunnerAutomation`. Before the fix an idle startup/switch/
  dispose/disable produced `Abort()` calls (proved by the mutation run, §16).
- **Not performed:** live FFXIV probing (frame-by-frame key logging, observing the addon). Not available here.

## 10. Exact root cause

`ShoutRunnerService.HardStop()` — the "make sure nothing is running" teardown — invoked `IShoutRunnerAutomation.Abort()`
**even when ShoutRunner was idle**. The real `Abort()` ends in `DismissTransferUiOnceUnsafe`, which **always** posts a real
Escape key to the game window, regardless of whether a transfer UI existed or whether the character was in the world.
`HardStop` is called on every venue activation, venue switch, module disable and plugin dispose, so every one of them sent
an Escape into an idle, logged-in game.

## 11. Why that opens the System Menu

FFXIV routes Escape to "close the top-most cancellable UI"; with none open it opens the System Menu. The Escape was
intended for a stuck World Travel/login-queue dialog (donor-derived recovery), but was sent unconditionally. Because it
is a genuine window key message, the game cannot tell it from the player's own keypress.

Pre-dates WP5: the code is from the ShoutRunner reconstruction (`git log` for the file predates the maintenance cycle;
`ShoutRunnerAutomationService.cs` had no working-tree modifications going into WP6). Block Letters' focus code is
unrelated (ImGui-only).

## 12. Exact implementation

1. `ShoutRunnerService.HardStop()`: capture `wasActive = IsActive` on entry; call `automation.Abort()` only if
   `wasActive`. Everything else (token cancel/dispose, task clearing, state → Stopped, cursor clear) is unchanged and
   still runs unconditionally, so `HardStop` remains idempotent and safe. `Stop()` already returned early when idle.
2. New pure `ShoutRunnerEscapePolicy.ShouldSendEscape(isLoggedIn, hasLocalPlayer)` = `!isLoggedIn || !hasLocalPlayer`.
3. `ShoutRunnerAutomationService.DismissTransferUiOnceUnsafe` sends Escape only when the policy allows it, reading
   `clientState.IsLoggedIn` and `objectTable.LocalPlayer` on the framework thread it already runs on; otherwise a Debug
   log line. The `AgentWorldTravel` hide (a direct addon call, no key) is unchanged.
4. Doc comments updated on `IShoutRunnerAutomation.Abort` and `HardStop`.

Side effect worth stating: an idle `HardStop` also no longer calls `Lifestream.Abort` IPC. It was aborting any Lifestream
operation the player had started manually on every reload/venue switch; that is now left alone. (Not the reported
defect; a consequence of the correct fix.)

## 13. Why this prevents the trigger rather than closing the menu

Nothing detects or closes the System Menu, and there is no polling. The keypress that opens it is simply **not sent**:
(a) the lifecycle paths no longer call the game-facing `Abort` at all when nothing is running; (b) even during a real run,
Escape is withheld while the character is in the world, which is exactly when Escape means "System Menu".

## 14. Input-consumption implications

None. No input is consumed, suppressed, hooked or swallowed. VenueOS never handled Escape; it *emitted* one. The player's
own Escape/back is untouched.

## 15. Focus implications

None. No ImGui, OS or game focus behaviour changed. Block Letters' WP5 one-shot caret/focus restoration, the Module
Launcher, detached windows and tablet focus requests are not touched.

## 16. Tests/probes added — `tests/VenueOS.Services.Tests/SystemMenuEscapeTests.cs` (14 tests, all new)

- Startup activation, plugin-reload dispose (`ModuleHost.DisposeAsync`), repeated venue switches through the real
  `SwitchAsync`, deleting the active venue, and module disable/re-enable each reach `Abort()` **zero** times when idle.
- Idle `HardStop`/`Load` after an earlier real abort add no further `Abort()`.
- The guard is not over-broad: a running run is still aborted by `HardStop`, module disable, operator `Stop`, and a venue
  switch mid-run (which still hard-stops it).
- `ShoutRunnerEscapePolicy` truth table (4 cases).
- Source guard: the only `SendKeypress`/`SendInput`/`keybd_event`/`PostMessage`/`SendMessage` in `src/VenueOS.Plugin` is
  the one directly preceded by the policy check — a new ungated synthetic key anywhere fails the suite.

**Mutation proof:** with the old unconditional `automation.Abort()` and the ungated `SendKeypress` temporarily restored,
**8 of the 14 tests failed** (7 lifecycle + the source guard); with the fix restored all 14 pass. Only WP6 lines were
mutated and both were restored byte-for-byte.

## 17. What automated validation proves

The reload, venue-switch, startup, delete-venue and module-disable paths no longer invoke the game-facing abort/dismiss
when nothing is running; a real run is still stopped; the Escape rule is as specified; and no other ungated synthetic key
exists in the plugin. Full suite **1343 passed / 0 failed / 0 skipped** (1329 baseline + 14). Debug and Release builds:
0 warnings, 0 errors.

## 18. What still requires FFXIV live QA

That the real client no longer opens the menu on reload, venue switch, etc. — the final link (game reaction to a key
message) is not automatable. See §20.

## 19. Remaining limitations

- Root cause proven by source/decompilation and lifecycle reproduction, **not** by live key logging. The exact ordering of
  the reload's two Escapes against the game frame loop was not observed.
- While the character is in the world, a *genuine* leftover transfer/congestion dialog is no longer dismissed by Escape
  (the `AgentWorldTravel` hide still runs). Escape's in-world default is the System Menu, so it cannot be sent safely; if
  live QA shows a stuck dialog in that case, it needs a targeted addon close, not a key.
- While logged out (title/character select) Escape is still sent by the recovery paths, as designed; this was not
  live-verified either way.
- Unrecorded training actions are attributed by inference (§8).

## 20. Live QA checklist

**A — `/xlreload`.** System Menu closed → `/xlreload` → let VenueOS reload. PASS: no System Menu. Repeat several times.
**B — Venue switching.** Switch between two venues repeatedly. PASS: no menu; selected venue and per-venue state change
correctly. Also delete the *active* venue (with a second one present) — PASS: no menu.
**C — Open/close.** `/venueos`, tablet close/open, collapse/expand, launcher open, launcher module buttons, detached
module windows. PASS: no menu.
**D — Shared-path actions.** Settings → Modules: disable then re-enable ShoutRunner (idle). Start a ShoutRunner run and
press **Stop** while in-world. PASS: no menu at any point; Stop still stops the run and Lifestream (if used) is aborted.
Optionally let a run hit a failed/congested transfer. PASS: no menu while in the world.
**E — Intentional System Menu.** Open it normally, with VenueOS open and closed; close it normally. PASS: works as usual.
**F — Escape/back.** Use Escape/back throughout the game and VenueOS. PASS: unchanged.
**G — Login/logout.** Log out → log in (and a character change if practical) with VenueOS loaded. PASS: no unintended
menu, no lifecycle regression.
**H — Block Letters.** `block → type → block → type`. PASS: WP5 caret/focus restoration still works.
**I — Smoke.** Open several modules, Settings, launcher and detached windows. PASS: no menu, no focus/input regression.
