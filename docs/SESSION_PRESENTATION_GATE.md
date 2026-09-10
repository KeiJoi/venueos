# Central character-session presentation gate (0.3.0 post-release pass)

Engineering report for Part III of the 0.3.0 post-release quality pass: a single, centralized policy for whether
VenueOS-generated UI may render this frame, replacing what was previously no gating at all.

## Confirmed gap

`Plugin.Draw()` had zero session/login gating anywhere before this change. `ModuleWindowManager.DrawAll`
(detached module windows), Bingo's three auxiliary windows, Giveaways' tracker window, and
`MacroHotbarRenderer.DrawAll()` (the faux hotbars) all rendered unconditionally, gated only on their owning
module's `IsEnabled` — not on whether a character was logged in at all. This is exactly the audit's cited example:
a persisted, enabled Macro hotbar could render over the FFXIV title/intro screen before character login.

## Design

`SessionPresentationGateService` (`src/VenueOS.Services/SessionPresentationGateService.cs`) — pure logic, testable
via `ISessionStateProvider` (the same thin-Dalamud-seam pattern as `ITargetedPlayerProvider`):

```csharp
public bool CanRenderGeneralUi => session.IsLoggedIn;
public bool CanRenderShoutRunnerUi(bool shoutRunnerActive) => CanRenderGeneralUi || shoutRunnerActive;
```

`ISessionStateProvider.IsLoggedIn` is backed in production by `DalamudSessionStateProvider` (`Plugin.cs`), reading
`IClientState.IsLoggedIn` — the same property this codebase already uses elsewhere for exactly this distinction
(`ShoutRunnerAutomationService`'s own `GameState.IsLoggedIn`, tracked separately from momentary local-player
availability). The gate deliberately does **not** use `LocalPlayer is null` as its signal — that is also
transiently true for one ordinary frame during a normal zone transition while genuinely still logged in, which the
audit explicitly warned against conflating with "logged out."

### Why the ShoutRunner exception doesn't need a second travel state machine

`ShoutRunnerService` gained one new computed property:

```csharp
public bool IsActive => State is not (ShoutRunnerState.Stopped or ShoutRunnerState.Faulted);
```

Three independent facts, each verified against the actual code (not assumed), together prove `IsActive` cannot
leak ShoutRunner UI at startup/title/character-select, and cannot keep it visible indefinitely after a genuine
logout, with no additional state:

1. **Guaranteed false at startup.** `State` initializes to `Stopped`, and `Load(nextVenueId)` — called for the very
   first venue activation at plugin startup, before any login is possible this session — calls `HardStop()` first,
   forcing `State` back to `Stopped`.
2. **Can only become true from a logged-in operator action.** `IsActive` only ever transitions to true via
   `Start()`/`Resume()`, both called exclusively from ShoutRunner's own panel — which, while `IsActive` is false,
   is only reachable under the same `CanRenderGeneralUi` rule as every other module. So the very first time
   `IsActive` becomes true in a plugin session, the operator was already genuinely logged in.
3. **Self-corrects within an existing bound if the character never returns.** `ShoutRunnerAutomationService`'s
   pre-existing travel timeouts (`ReadinessTimeout` 30s, `LoggedOutRecoveryGrace` 8s, `SameDataCenterTimeout` 3min,
   `CrossDataCenterTimeout` 5min) already cause `ShoutRunnerService.EnterFaulted` to set `State = Faulted` if
   readiness/transfer never completes — including a scenario where the operator genuinely logged out mid-run
   rather than merely traveling. Worst case, ShoutRunner's UI can remain visible during a fully abandoned logout for
   up to the system's own existing definition of "this travel is no longer proceeding normally" (5 minutes), never
   indefinitely.

Building a second, parallel travel-tracking state machine on top of this would duplicate a bound the system already
enforces for an unrelated reason (giving up on a stuck automation).

### Wiring (`Plugin.Draw()`)

Both flags are computed once, at the very top of `Draw()`, before anything renders:

```csharp
var canRenderGeneral = sessionGate.CanRenderGeneralUi;
var canRenderShoutRunner = sessionGate.CanRenderShoutRunnerUi(shoutRunnerServiceRef.IsActive);
```

- `ModuleWindowManager.DrawAll` gained a `Func<string, bool> canRenderModule` parameter — ShoutRunner's own
  detached window (if popped out) renders under `canRenderShoutRunner`; every other detached module renders under
  `canRenderGeneral`. A suppressed window is skipped for the frame only — it stays in the manager's `open` set and
  resumes the moment the gate allows it again; suppression is never confused with the operator closing it.
- Bingo's three auxiliary windows, Giveaways' tracker window, and the Macro faux hotbars are now gated on
  `canRenderGeneral && <module>.IsEnabled` — no ShoutRunner-style exception applies to any of them.
- The main tablet itself: if `!canRenderGeneral`, it only continues rendering when the operator currently has it on
  the ShoutRunner screen **and** `canRenderShoutRunner` — otherwise it returns before `ImGui.Begin`. This is what
  keeps "Stop" reachable for an embedded (not detached) ShoutRunner session through a travel blip, without ever
  leaking Home/Settings/another module through the exception.
- Nothing below this point in `Draw()` is gated by anything but these two flags — internal service state (config
  load, persistence, `Tick`, already independently gated by `IsEnabled`) is completely untouched. This is a
  presentation-only gate, per the audit's explicit instruction.

## What was deliberately NOT changed

- No module's config/persistence path was touched. Logging out never erases settings, Macro hotbar
  configuration, positions, assignments, scale/transparency, or venue configs.
- `ModuleHost.Tick`/`InitializeAsync`/`OnVenueChangedAsync` are untouched — modules may still load configuration
  and maintain non-character-specific internal state while logged out, per audit §11.

## Tests

- `tests/VenueOS.Services.Tests/SessionPresentationGateServiceTests.cs` (6 tests): logged-out → general false;
  logged-in → general true; logged-in → ShoutRunner surface true regardless of activity; logged-out + active
  ShoutRunner → only the ShoutRunner surface true; logged-out + inactive ShoutRunner → everything false; the gate
  never touches anything I/O-shaped.
- `tests/VenueOS.Services.Tests/ShoutRunnerTests.cs` (4 new `IsActive`-specific tests): a freshly-loaded service is
  not active; starting a run marks it active before any `Tick`; a run that settles into `Faulted` is not active;
  `Load()` (venue activation) forces `Stopped`/not-active even mid-run — this is the exact mechanism the startup
  proof above depends on.

Rendered behavior (does the tablet/hotbar/detached window actually appear/disappear correctly in Dalamud, does the
ShoutRunner exception actually keep working through a real DC travel) cannot be proven by these tests — it requires
the live test below.

## Live acceptance checklist (audit §14)

1. Start FFXIV at the title screen. Confirm NO VenueOS UI (including a previously-enabled Macro hotbar) appears.
2. Log in a character. Confirm expected enabled UI (main tablet if it was open, Macro hotbar if enabled) restores.
3. Confirm the Macro hotbar specifically restores only after login, not before.
4. Log out. Confirm all VenueOS UI disappears.
5. Log in again. Confirm persistent state (hotbar position/assignments, module settings) restores unchanged.
6. Separately: start a ShoutRunner run that includes a genuine Data Center travel leg.
7. Confirm ShoutRunner's operational UI (embedded or detached, whichever the operator had it as) remains visible
   and Stop remains clickable through the travel transition.
8. Confirm no unrelated VenueOS UI (Home, another module, a different detached window) appears during that same
   transition — only ShoutRunner's own surface.
9. Let the run finish or click Stop. Confirm ShoutRunner's UI returns to normal suppression behavior (i.e., logging
   out afterward hides it like any other module) once it is no longer active.

## Remaining limitations

This is a presentation-layer fix; it does not change what any module actually does while logged out, and it
cannot be verified as visually correct without the live test above.
