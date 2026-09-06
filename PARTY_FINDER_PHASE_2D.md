# Phase 2D Party Finder status

## Standalone repair

`C:\FFXIVplugs\venuepartyfinder\VenuePartyFinder.csproj` now targets `Dalamud.NET.Sdk/15.0.0`, `.NET 10 Windows`, `ECommons 3.1.0.19`, and `Dalamud.Bindings.ImGui`. The hard-coded SDK 14 props/targets and missing Hooks path were removed. The standalone build succeeded on 2026-09-03 with zero warnings/errors.

The project contains pre-existing local `.tmpinspect` and `tools` projects. They are explicitly excluded from plugin compile items; without the exclusions their generated assembly attributes were incorrectly compiled into the plugin.

`IPartyFinderAutomation` now provides the boundary around unsafe Party Finder operations. The existing adapter implements it. It verifies a visible, ready, known `LookingForGroup` addon before allowing click automation; otherwise it aborts rather than clicking an unknown layout.

## Manual game validation gate

VenueOS integration is intentionally not started. The repaired standalone plugin still requires manual in-game validation of create/update, all preset fields, manual/automatic refresh, five-minute warning, listing-ended recognition, abort paths, and no unintended clicks. Until that has been performed and reported, `promotion.partyfinder` must not be migrated—the standalone project remains the behavioral source of truth.

## Known version-sensitive areas

The adapter uses `AgentLookingForGroup`, `LookingForGroup*` addon names, button/node lookup, checkbox matching, and FFXIV client structs. Any FFXIV UI/layout update requires revalidation before enabling automation. The compatibility gate protects unknown click paths but cannot replace manual gameplay testing.
