# Party Finder Reconstruction

Module ID: `promotion.partyfinder`. This document records the full reconstruction of Party Finder from the read-only donor (`C:\FFXIVplugs\venuepartyfinder`) into a VenueOS-native implementation, following the completed audit and the user's explicit implementation decisions. It supersedes `PARTY_FINDER_PHASE_2D.md`'s "integration is intentionally not started" statement — integration is now complete and this file is the current source of truth for Party Finder's status.

## What changed architecturally

The previous integration held a live `ProjectReference` to the donor's `.csproj`, reused the donor's own static `Service` locator and `PluginConfiguration` (whose `Save()` writes through `Service.PluginInterface.SavePluginConfig(this)`), and referenced a `SaveOverride` property that did not exist anywhere — a compile error, and, had it existed, a mechanism that could not have intercepted the donor's own hardcoded `Save()` calls, which would have written a `VenuePartyFinder.PluginConfiguration` object into VenueOS's own plugin config file (the one holding every venue and every module's data), since `Service.PluginInterface` was populated with VenueOS's own `IDalamudPluginInterface`.

All of that is gone. VenueOS now owns:

- **Models** (`src/VenueOS.Modules.Operations/PartyFinder/`): `PartyFinderPreset`, `PartyFinderSettings`, `PartyFinderJobCatalog`, `PartyFinderDutyCatalog`, `PartyFinderChatEvents` — plain, Dalamud/FFXIVClientStructs-free records and pure functions, unit-testable in `tests/VenueOS.Services.Tests/`.
- **The automation contract** (`IPartyFinderAutomation`, same folder) — the seam a fake implementation can stand in for in tests.
- **Orchestration/persistence** (`PartyFinderService`, same folder) — owns per-venue settings via `VenueProfileService.GetModuleConfig`/`SaveModuleConfig`, schema version 1, key `promotion.partyfinder`.
- **The `IVenueModule` wrapper** (`PartyFinderModule`, same folder) — its own file per `NEW_MODULE_GUIDE.md` §21/§33, and the first module in this codebase to actually separate `Draw()` from `DrawSettings()` (§8's documented gap).
- **The unsafe automation engine** (`src/VenueOS.Plugin/PartyFinder/PartyFinderAutomationService.cs`) — a faithful, line-for-line port of the donor's `PartyFinderAutomation` (same addon names, node IDs 46/111, text-match fallbacks, task-chain shape, and every timing constant), implementing `IPartyFinderAutomation`. Lives in `VenueOS.Plugin` because it requires FFXIVClientStructs/ECommons/Dalamud game services, matching where this codebase's other unsafe code (`DalamudVenueAddressProvider`) already lives.
- **Native enum/job-flag mapping** (`PartyFinderNativeMapping.cs`) — maps VenueOS's own enums onto the real `AgentLookingForGroup`/`Dalamud.Game.Gui.PartyFinder.Types.JobFlags` values *by name*, transcribed directly from the donor's source, never by assumed numeric identity.
- **The operator panel** (`src/VenueOS.Plugin/PartyFinderOperatorPanel.cs`) — the full operational UI plus the Settings split.

`VenueOS.Plugin.csproj` no longer references `C:\FFXIVplugs\venuepartyfinder\VenuePartyFinder.csproj`. It references ECommons directly (the same third-party library the donor depends on, via the same NuGet-cache `HintPath` pattern the donor itself uses) — a dependency on a shared library, not on the donor's own source. Building VenueOS no longer compiles the donor project or writes into its `bin`/`obj`; verified by rebuilding VenueOS and confirming `git status` inside the donor repository stays clean and no donor build artifact's timestamp changes.

## Intentional differences from the donor (all explicit, user-directed)

| Donor behavior | VenueOS |
|---|---|
| `/vpf`, `/vpf create`, `/vpf refresh` commands | Not implemented. VenueOS uses `/venueos`, Home, embedded/detached module windows uniformly for every module — a Party Finder-specific command would be exactly the kind of module-specific special-casing `NEW_MODULE_GUIDE.md` §7/§38 forbids. |
| `AutoOpenWindowOnLoad` (donor's own floating window opens on plugin load) | Not implemented. Superseded by the generic, global "Open modules in separate windows" (Auto Pop-Out) preference that already applies to every module uniformly. |
| Block Letter Generator (fullwidth/circled/shape/symbol glyph palette for the comment field) | Removed from Party Finder's scope by explicit user direction. The normal Comment editor, 192-byte limit, live UTF-8 byte counter, and native truncation-to-191-bytes behavior are all preserved. The generator itself is deferred to a future dedicated VenueOS module — not built in this pass. |
| No shutdown operation beyond Abort | **End Party Finder** (new) — see below. |
| `DrawSettings()` mirrors `Draw()` | Split: Settings → Modules → Party Finder holds only Auto Refresh and Warning Message Override; every recruitment-criteria field lives in the operational screen, per `NEW_MODULE_GUIDE.md`'s own example table ("Active Party Finder listing state" is explicitly operational). |

No other functionality was omitted. Every donor field (category, duty search, objective, beginner-friendly, comment, world limit, private party + password, completion status, avg item level, the three Duty Finder Setting flags, loot rule, all four language flags, slots/groups/one-player-per-job, all 48 per-slot job masks with the six quick-mask buttons and all 22 individual job checkboxes) is reconstructed in the operator panel.

*(Correction to the original audit: the donor's job catalog has 22 jobs, not 21 — Blue Mage was undercounted. `PartyFinderJobCatalog` in this reconstruction correctly has 22 entries, matching the donor exactly; the audit report's prose count was wrong, not the implementation.)*

## End Party Finder

A new, explicit shutdown operation distinct from Abort:

- **Abort** cancels the in-flight task chain only. It does not withdraw the listing and does not touch Auto Refresh.
- **End Party Finder**: disables and *persists* Auto Refresh for the active venue first (before anything else runs), then aborts any in-flight automation, then withdraws the active native listing (skipped as an already-successful no-op if there is no listing), verifies withdrawal against the authoritative native listing state (`AgentLookingForGroup.OwnListingId`) with a bounded timeout, and finally closes the Party Finder window if it's still open. Auto Refresh is never re-enabled automatically — only an explicit future toggle in Settings turns it back on, and this holds even if withdrawal fails (the failure path leaves Auto Refresh disabled and reports through Diagnostics, it never restarts anything).

**Navigation is shared with the live-verified working Refresh/Edit path**, not a separately-implemented route. `PartyFinderAutomationService.NavigateToOwnListingDetail` is the single implementation of "open the main `LookingForGroup` addon (via `EnsureMainAddonReady`), click node 46 (Recruitment Criteria) if `LookingForGroupDetail` isn't visible yet, and invoke a caller-supplied continuation once it is" — extracted, unchanged in behavior, from what `ClickMainAction`'s `requireExistingListing` branch (Refresh's own navigation) already did inline. Refresh's continuation clicks Edit; End's continuation (`ClickEndButton`) clicks End. The two flows can never navigate differently by accident because there is only one navigation implementation.

End's button search (`ClickEndButton`) is its own narrowly scoped lookup on the reached `LookingForGroupDetail` screen — it does **not** reuse or weaken `ScoreButton`'s reject list (which still excludes "End"/"Withdraw"/"Leave" for normal Create/Edit/Refresh automation, exactly as the donor's `RejectPrimaryTexts` does). The user's screenshot of the listing-detail screen (Edit / End / Back) confirms the real label is literally **"End"** — now the first candidate in `EndButtonTexts`, with a few other plausible labels kept as defensive fallbacks for a different game version/locale.

### Root cause of the original failure (first correction pass)

The first End Party Finder implementation had its own separate `EnsureMainAddonReadyForEnd`/`OpenOwnListingDetailsForEnd` navigation, largely mirroring `EnsureMainAddonReady`/`ClickMainAction` but not identical to them. Critically, `EndPartyFinder()` reset three of the five per-attempt runtime fields (`mainAddonOpenStartedUtc`, `nextMainAddonAttemptUtc`, `submissionVerificationStartedUtc`) but — unlike `QueueOperation`, which Create/Edit/Refresh use — **not** `mainAddonOpenAttempts` or `usedSlashCommandFallback`. Those two fields are shared instance state on `PartyFinderAutomationService`. If a prior Refresh had already run (as it does live) and had already consumed the one-time `/partyfinder` slash-command fallback (`ShowAddon`/`FocusAddon` alone doesn't reliably reopen an already-closed Party Finder window once a listing exists — this is exactly why the fallback exists), then when End's own `EnsureMainAddonReady` call ran, `usedSlashCommandFallback` was already `true` from that earlier Refresh, so the fallback command was never sent again for End — the window was never reopened, and after the 8-second timeout the exact reported diagnostic fired: "Failed to detect a visible Party Finder window. Open Party Finder manually once, then retry." Fixed by resetting all five fields in `EndPartyFinder()`, matching `QueueOperation` exactly, and by additionally removing the separate navigation implementation so this class of drift can't recur.

### Root cause of the "Cannot Find End button" first-attempt failure (second correction pass)

After the first fix, End correctly reached the `LookingForGroupDetail` screen, but still failed with "Cannot Find End button" whenever VenueOS had to navigate there itself (Case A), while working when the operator had already manually had that screen open for a while (Case B). The reached addon reporting `IsReady`/`IsVisible` only means the screen instance exists and is on-screen — its button/label nodes can still be unpopulated for a frame or more afterward. Refresh/Edit's own button search (inside `ClickMainAction`) already tolerated this correctly: when it doesn't find "Edit" yet, it returns `false` ("not yet, try again"), and the shared `NavigateToOwnListingDetail` helper naturally re-invokes it on the next tick — so Edit's search silently retries until the label populates. `ClickEndButton`, by contrast, called `FailAndAbort` (a hard, immediate, non-retrying failure) the instant its first single-shot scan — taken on the very frame the detail screen became ready — didn't find "End". Manually pre-opening the screen (Case B) masked this because the screen had already been populated for many frames by the time End's search ran.

**Fix:** `ClickEndButton` now retries (returns `false`) exactly like Edit's search does when "End" isn't found yet, bounded by a new explicit `EndButtonDiscoveryTimeout` (5 seconds) so a genuinely missing/relabeled button still fails safely with a specific diagnostic message instead of hanging or silently retrying forever. This only changes `ClickEndButton` (End-only code); Refresh/Edit's own search inside `ClickMainAction` is untouched.

## Party Finder preset persistence

### Confirmed defect: two independently-cached copies of the same Dalamud plugin config

`DalamudVenueStore` (backing all per-venue/per-module config, including Party Finder's) and `DalamudGlobalSettingsStore` (backing the global Auto Pop-Out Modules preference) each independently called `pluginInterface.GetPluginConfig() as VenueOsPluginConfiguration ?? new VenueOsPluginConfiguration()` at their own construction time in `Plugin.cs`. If `IDalamudPluginInterface.GetPluginConfig()` deserializes fresh from the on-disk JSON on each call rather than returning one shared cached instance, these were two **independent** `VenueOsPluginConfiguration` objects for the plugin's entire lifetime. Either store's `Write` re-serializes and saves *its own* copy — including whatever the property it doesn't own (`.Global` for the venue store, `.State` for the global-settings store) happened to hold at THAT store's own construction time. If the global-settings store's `Write` ever fired after the venue store had already saved newer per-venue data (e.g. the operator touches Settings → General → "Open modules in separate windows" even once during the session), it would silently overwrite the on-disk file with a `.State` frozen back to plugin-startup time — discarding every venue's every module's edits made since, Party Finder's preset included. This is architecture shared by every module, not Party-Finder-specific, but it is the most concrete, demonstrable defect found while tracing the pipeline, and precisely matches "runtime memory holds it correctly, a fresh plugin instance does not."

**Fix:** `Plugin()` now loads `VenueOsPluginConfiguration` exactly once and passes that single instance into both `DalamudVenueStore` and `DalamudGlobalSettingsStore`. A write through either store always includes the other's latest in-memory value, because they are now the same object. This cannot be unit-tested directly (it requires a live `IDalamudPluginInterface`), so it remains a live-verification item — see the acceptance checklist below.

*(The exhaustive round-trip test added this pass, `Every_preset_field_survives_a_full_serialize_deserialize_boundary`, proves the `PartyFinderPreset`/`PartyFinderSettings` **model and JSON schema** round-trip correctly through real serialization — every field, including role masks, flags, and enums. It was not the source of the reported bug; the defect was in the surrounding Dalamud config wiring, not the Party Finder data shape.)*

### What "persistent" means for the operational form

The Party Finder listing editor stays in the operational module (per `NEW_MODULE_GUIDE.md`'s own guidance that "Active Party Finder listing state" is operational, not Settings) — but operational does not mean transient. Every field change calls `PartyFinderService.UpdatePreset`/`SetAutoRefreshEnabled`/`SetWarningMessageOverride`, each of which saves immediately through `VenueProfileService.SaveModuleConfig`. No separate Save button exists or is needed. Settings → Modules → Party Finder continues to hold only Auto Refresh and Warning Message Override, unchanged.

## Live FFXIV verification required

This cannot be proven by unit tests — the same acceptance standard as the rest of Party Finder's automation:

1. **End Party Finder's first-attempt success** with all native PF windows closed beforehand (the exact scenario that previously failed both ways — see the "First-attempt acceptance test" below).
2. The failure path (a forced disconnect/close mid-withdrawal) and the race-protection scenario (queue a Refresh, then immediately End, and confirm the listing does not come back).
3. Create/Edit/Refresh — already live-verified working by the user; only reconfirm that the `NavigateToOwnListingDetail` extraction didn't change their behavior (it's designed to be identical, but this is the first live check since the extraction).
4. **Preset persistence across a genuine plugin lifecycle boundary** — VenueOS disable/re-enable, and ideally a full FFXIV restart — with a distinctive configured listing (see "Durable preset acceptance test" below). The model/JSON layer is now proven correct by an automated test; only the live Dalamud config wiring (the shared-config-object fix) needs live confirmation.
5. All 22 job checkboxes and 6 quick-mask buttons against the native role-restriction UI, to confirm `PartyFinderNativeMapping`'s per-job `JobFlags` values write into slots the game actually displays as expected.
6. Dark/Light/Neon/Midnight visual check of the operator panel.

### First-attempt acceptance test (Issue 1)

1. Create an active listing; ensure ALL native Party Finder windows are closed.
2. Click End Party Finder in VenueOS.
3. Observe `/partyfinder` open, Recruitment Criteria activate, and VenueOS wait for the detail screen (Edit / End / Back) to appear.
4. VenueOS must locate and click End **on this first attempt** — no manual pre-opening of the detail screen required.
5. Confirmation (if any) is handled, the listing ends, the PF UI refreshes, VenueOS verifies no own listing, `/partyfinder` closes the remaining window, and Auto Refresh remains disabled.
6. Repeat more than once, including immediately after a Refresh (the exact sequence that originally failed).

### Durable preset acceptance test (Issue 2)

1. Select a Venue Profile, open Party Finder, configure a distinctive listing (recognizable category/duty, distinctive comment, non-default conditions/languages/roles).
2. Close and reopen Party Finder — values remain (this alone is **not** proof of persistence; it's the same in-memory object).
3. Disable VenueOS in Dalamud, then re-enable it. Open the same Venue Profile → Party Finder. **Every** value must remain — this is the real test, since it destroys and reconstructs the entire plugin (and therefore `PartyFinderService`) from scratch.
4. End Party Finder, then disable/re-enable VenueOS again — the listing preset must still be there, and Auto Refresh must still be disabled (not silently re-enabled at startup).
5. Restart FFXIV entirely; same Venue Profile → Party Finder → preset still there.
6. Repeat with two venues (distinctly different presets) and confirm no leakage after disable/re-enable.

## Manual QA checklist

### Form / parity
- [ ] Category, Duty search (filter narrows results, selection persists), Objective, Beginner Friendly
- [ ] Comment: 192-byte limit, live UTF-8 byte counter, no Block Letter Generator present
- [ ] World limit, Private party + password (disabled until Private party is on)
- [ ] Completion status enable + value, Avg item level enable + value
- [ ] All three Duty Finder Setting flags
- [ ] Loot rule (Normal / Greed Only / Lootmaster)
- [ ] All four language flags (JP/EN/DE/FR)
- [ ] Slots (1–8), Groups (1–6), One Player Per Job
- [ ] Every quick role mask (All/Tank/Heal/Melee/Phys Ranged/Caster) and all 22 individual job checkboxes, across multiple slots

### Normal automation
- [ ] Create listing, Edit listing, Refresh, Abort
- [ ] Auto-refresh fires on the native 5-minute warning; respects `WarningMessageOverride`; throttled to once per 4 minutes
- [ ] Listing-ended chat detection clears active-listing state
- [ ] Compatibility gate: automation refuses to click before a known `LookingForGroup` addon is observed
- [ ] Confirmation popups (SelectYesno/SelectOk) handled correctly
- [ ] Repeated Create/Refresh aborts the previous chain and starts fresh, never runs two chains at once

### End Party Finder — mandatory sequence
- [ ] 1. Enable Auto Refresh
- [ ] 2. Create a Party Finder listing
- [ ] 3. Confirm Recruit works
- [ ] 4. Click Refresh
- [ ] 5. Confirm Refresh works
- [ ] 6. Click End Party Finder
- [ ] 7. Verify Auto Refresh immediately becomes disabled
- [ ] 8. Observe `/partyfinder` opening if PF was closed (this is the exact step that previously failed — confirm it now opens correctly even after a prior Refresh has already run)
- [ ] 9. Observe navigation through Recruitment Criteria (node 46 on the main PF window)
- [ ] 10. Observe the listing-detail screen (Edit / End / Back)
- [ ] 11. Observe **End** being selected (not Edit, not Back) — must succeed on the **first attempt**, with no native PF windows manually pre-opened beforehand (this is the exact defect fixed this pass)
- [ ] 12. Accept/handle native confirmation if the game presents one
- [ ] 13. Verify the listing disappears
- [ ] 14. Verify the main PF screen refreshes/reflects no active listing
- [ ] 15. Verify `/partyfinder` closes the PF window
- [ ] 16. Verify VenueOS shows the listing inactive
- [ ] 17. Verify Auto Refresh remains disabled

### End Party Finder — additional scenarios
- [ ] Re-enable Auto Refresh manually, create another listing, queue/begin a Refresh, then immediately click End Party Finder — End must win; the listing must not come back
- [ ] Click End Party Finder with no active listing — treated as already-ended, no false failure, Auto Refresh still disabled
- [ ] Force a failure (e.g. close the Party Finder window mid-withdrawal) — Auto Refresh remains disabled, failure appears in Settings → Diagnostics with a stage-specific message, nothing auto-restarts

### Durable preset — mandatory sequence (Issue 2)
- [ ] 1. Enable VenueOS, select a Venue Profile, open Party Finder
- [ ] 2. Configure a distinctive listing (category/duty, comment, non-default conditions, languages, role layout)
- [ ] 3. Close Party Finder, reopen it — values remain (same-instance check only, not proof of real persistence)
- [ ] 4. Disable VenueOS in Dalamud, then re-enable it
- [ ] 5. Open the same Venue Profile → Party Finder — **every** configured value must remain; any field reverting to default means persistence is still broken
- [ ] 6. End Party Finder, then disable/re-enable VenueOS again — listing preset still present, Auto Refresh still disabled
- [ ] 7. Restart FFXIV entirely; same Venue Profile → Party Finder → preset remains
- [ ] 8. Two venues, distinctly different presets — disable/re-enable VenueOS, confirm each venue restores its own preset with no leakage

### VenueOS integration
- [ ] Venue switching: per-venue preset and Auto Refresh state isolated, no leakage; switching does not withdraw a native listing on its own
- [ ] Enable/disable module: disabling aborts in-flight automation only, never withdraws a listing; config survives
- [ ] Embedded mode, detached mode, Auto Pop-Out (off → embedded, on → detached, re-click focuses not duplicates)
- [ ] Dark, Light, Neon, Midnight
- [ ] A forced failure surfaces in Settings → Diagnostics, attributable to `promotion.partyfinder`, secrets N/A (none handled by this module)
