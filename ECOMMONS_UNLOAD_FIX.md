# ECommons unload error — root cause and fix

## Symptom

Disabling/unloading VenueOS in Dalamud logged:

```
Method not found: 'Void Dalamud.Plugin.Services.IClientState.remove_TerritoryChanged(System.Action`1<UInt16>)'.
  at ECommons.GameHelpers.VfxManager.Dispose()
  at ECommons.GenericHelpers.Safe(Action action, Boolean suppressErrors)
```
followed by:
```
The type initializer for 'ECommons.Automation.Callback' threw an exception.
  at ECommons.Automation.Callback.Dispose()
  at ECommons.GenericHelpers.Safe(Action action, Boolean suppressErrors)
```

Both fire from inside `ECommonsMain.Dispose()` (`Plugin.cs`'s `Dispose()`, called last, after every module/service has already been torn down) — VenueOS never calls `VfxManager`/`Callback` directly; they're ECommons' own internal housekeeping.

## Root cause

VenueOS referenced ECommons as a raw assembly `<Reference>` with a `HintPath` hard-coded into one machine's local NuGet cache:

```xml
<Reference Include="ECommons"><HintPath>C:\Users\kyro_\.nuget\packages\ecommons\3.1.0.19\lib\net10.0-windows7.0\ECommons.dll</HintPath></Reference>
```

Because this bypasses NuGet restore entirely, nothing in the build ever validated that ECommons **3.1.0.19**'s compiled IL was compatible with the Dalamud API level VenueOS itself targets (`Dalamud.NET.Sdk/15.0.0`, API level 15). ECommons' own NuGet metadata declares no dependency on any Dalamud package, so this pairing is implicit and unenforced — a plugin can build cleanly against API 15 while linking an ECommons build that predates API 15 support.

That's exactly what happened: ECommons' public commit history shows an "Api15 update" commit to `ECommons/Automation/Callback.cs` (April 2026), which shipped starting with the 3.2.0.x release line. VenueOS's pinned **3.1.0.19** predates that fix by several releases, so its compiled `VfxManager.Dispose()`/`Callback` static initializer still call/expect the pre-API-15 `IClientState.TerritoryChanged` event shape. At actual plugin unload, the *running* Dalamud host (API 15) no longer exposes the exact `remove_TerritoryChanged(Action<ushort>)` accessor that old IL was compiled against, producing `MissingMethodException` (surfaced as "Method not found").

## Fix

`src/VenueOS.Plugin/VenueOS.Plugin.csproj` now references ECommons as a real, restorable `PackageReference` pinned to **3.2.1.18** (the latest release at the time of this pass, well past the Api15 update):

```xml
<PackageReference Include="ECommons" Version="3.2.1.18" />
```

This removes the hard-coded, single-machine `HintPath` (so the dependency now restores identically on any machine/CI, instead of silently depending on whatever happened to be in one developer's NuGet cache) and resolves the actual version-skew bug.

Verified after the change:

- `dotnet restore`/`dotnet build` (Debug and Release) succeed with 0 warnings/errors against the installed Dalamud dev SDK.
- The primary chat transport (`Plugin.ExecuteChatCommand`: `ECommons.Automation.Chat.ExecuteCommand` first, native `RaptureShellModule.ExecuteCommandInner` fallback) and `PartyFinderAutomationService`'s/`ShoutRunnerAutomationService`'s ECommons usage (`GenericHelpers.IsAddonReady`/`.Read`, `Automation`/`NeoTaskManager`) all compiled unchanged — no API surface VenueOS actually calls changed between 3.1.0.19 and 3.2.1.18.
- The full test suite (333 tests across `VenueOS.Core.Tests`/`VenueOS.Venues.Tests`/`VenueOS.Services.Tests`) passes unchanged.

## What still needs live verification

This bug only reproduces on actual plugin unload inside FFXIV — it cannot be exercised by a unit test or a clean build. Before calling the fix confirmed:

1. Load VenueOS in-game.
2. Exercise at least one ECommons-dependent path (send a chat command through Greeter/VIP/ShoutRunner, or open Party Finder).
3. Disable VenueOS from the Dalamud plugin installer.
4. Confirm neither the `IClientState.remove_TerritoryChanged` `MissingMethodException` nor the `Callback` type-initializer exception appears in `/xllog`.
5. Re-enable VenueOS and confirm modules still function.

This is tracked as a required manual acceptance step in `docs/RELEASE_CHECKLIST.md`.
