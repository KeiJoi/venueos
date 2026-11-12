# ShoutRunner — Patch 7.58 Travel Migration (VenueOS 0.3.13)

**Live QA: Pending — owner unavailable; release authorized based on automated verification.**
Nothing here was run in FFXIV. This is a follow-up investigation to
[`SHOUTRUNNER_LIFESTREAM_COMPATIBILITY_AUDIT.md`](SHOUTRUNNER_LIFESTREAM_COMPATIBILITY_AUDIT.md) (0.3.12). That report
was written against Lifestream source from 16 October 2026, before Patch 7.58. Its findings are preserved as written;
corrections are made here.

## 1. Official game changes (Patch 7.58, 10 November 2026)

Source: the Lodestone topic *"Rolling Out Same-region Player Matching for the North American and European Data
Centers!"* (https://na.finalfantasyxiv.com/lodestone/topics/detail/50458eeb98ca9466c69b85074f5326be0b3f3a94), linked
from the Patch 7.58 Notes (https://na.finalfantasyxiv.com/lodestone/topics/detail/27a249e737d7b041332c7018949035b03d62c081).
The maintenance notices are https://na.finalfantasyxiv.com/lodestone/news/detail/319982c490718c2f355b87bbb1e61fb455c7d286
and the follow-up …/611e992461bd620df45700041e46569377ee2972. Japan received the same change in 7.57 on 13 October
(…/topics/detail/44da654e65895ba03bedcf08303976b7a9630973).

| Rule | Official wording (NA/EU topic) |
|---|---|
| Cross-logical-DC travel inside a region now uses World Visit | "you will be able to play on another logical data center via the "Visit Another World Server" feature" |
| No Home World detour | "This removes the need to travel via your Home World." |
| New UI | "A dropdown menu will be added to the window displayed after selecting the "Visit Another World Server" option … This menu will allow you to select the logical DC and world you wish to travel to." |
| Data Center Travel is no longer used inside NA or EU | "The Data Center Travel system will no longer allow travel between logical DCs within North America, nor between logical DCs within Europe." |
| Data Center Travel remains for Oceania | "However, it will still allow travel from the North American and European DCs to the Oceania DC." |
| Rules on the destination | "the rules for what can and cannot be carried out on a world in another logical DC will be the same as those currently in place for the World Visit system" |
| Effective | Patch 7.58, Tuesday 10 November 2026 (maintenance ended about 1:15 a.m. PST) |

From the World Visit guide (https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/, undated):
"You will be unable to select Worlds that are unavailable due to congestion"; the option is only at the central
aetheryte of Limsa Lominsa, Gridania and Ul'dah; once the queue window appears "the transfer cannot be canceled".

**Not stated officially:** whether a character visiting a World in a second logical DC can jump straight to a third
(the dropdown wording and "no Home World detour" imply yes). Also not stated: any new error text, the dropdown's
default selection, or how congested Worlds appear in it.

## 2. Current Lifestream compatibility

Inspected: `NightmareXIV/Lifestream` `main` @ `b363201` (2026-10-16). Its newest tag, `2.6.0.9`, points at that same
commit. There are **no commits after 16 October 2026** (verified with `git fetch` and the GitHub API on 2026-11-11);
the only other branch, `tw13`, is a July Taiwan-client branch. **Lifestream has not been updated for Patch 7.58.**

| Area | Current Lifestream behavior | Effect under 7.58 |
|---|---|---|
| IPC names/signatures | `IsBusy`, `Abort`, `CanVisitSameDC`, `CanVisitCrossDC`, `ChangeWorld`, `TPAndChangeWorld` unchanged | Usable |
| `CanVisitSameDC(w)` | `DataStore.Worlds` = public Worlds in the character's **current logical DC** | Still means "same logical DC" |
| `CanVisitCrossDC(w)` | `DataStore.DCWorlds` = Worlds in **other** logical DCs of the home region, plus Oceania | **Now mixes two cases with different travel methods** (in-game World Visit vs Data Center Travel) |
| `/li <World>` for a same-logical-DC World | `TPAndChangeWorld(w, isDcTransfer:false)` → aetheryte → Visit Another World → `SelectWorldToVisit` (reads `StringArrayType.WorldTranslate` entries 3–10 and clicks list index + 2) → `ConfirmWorldVisit` (does not check the World name) | Works if the World list Lifestream reads still lines up with the rows on screen. **Unverified with the new dropdown.** |
| `/li <World>` for another NA/EU logical DC | `TPAndChangeWorld(w, isDcTransfer:true)` → `DCVType.HomeToGuest`: World Visit **home first** (`TaskTPAndChangeWorld(Player.HomeWorld…)`, `TaskWaitUntilInHomeWorld`), then `TaskLogoutAndRelog` → character select → "Visit Another Data Center" (`CharaSelectVisit.HomeToGuest/GuestToGuest`) | **Obsolete.** 7.58 no longer allows Data Center Travel between NA (or EU) logical DCs. The character is sent home and logged out, then the lobby step cannot complete. This matches the owner's "must log out" errors and unnecessary trips home. |
| In-game cross-logical-DC World Visit (DC dropdown) | **Not implemented.** No code selects a logical DC in `WorldTravelSelect`. | Not available through Lifestream |
| FFXIVClientStructs 7.58 update (`1aaea81`…`80cb4a7`, 2026-11-10) | Signatures and struct sizes only. `AgentWorldTravel` is unchanged since April 2026, and nothing touching World travel was changed. | No documented interface for the new dropdown |

## 3. Old versus new travel architecture

| | VenueOS ≤ 0.3.12 | VenueOS 0.3.13 |
|---|---|---|
| Logical routing | Data Center → World → Destination (fixed catalog order) | **Unchanged.** Data Centers stay distinct, with Worlds grouped under them. |
| Travel capability | `CanVisitSameDC`/`CanVisitCrossDC` only → "same DC" or "cross DC" | Game data decides it: logical DC and region of the current and target World (`World.DataCenter`, `WorldDCGroupType.Region`), confirmed by Lifestream's reachability answer: **same logical DC**, **another logical DC in the same region (7.58 in-game travel)**, or **another region**. |
| Travel execution | Same DC → `/li` (World Visit); any other DC → `/li` (Lifestream Data Center Travel: logout, character select) | Same logical DC → `/li` (World Visit). Same region, other DC → **refused, never `/li`**: the only Lifestream path for it is obsolete, and no supported in-game executor exists. Another region (e.g. Oceania) → `/li` Data Center Travel, which is still the official method there. |
| Arrival verification | Stable World match + Lifestream idle (0.3.12) | Same, plus **Data Center match** |
| Pre-shout guard | World | **World + Data Center** |
| Wrong World | Fault (run stops) | **That World is skipped and the route continues.** A second consecutive wrong-World arrival faults the run. |

## 4. Supported and unsupported combinations (VenueOS 0.3.13)

| From → to | Official method (7.58) | VenueOS 0.3.13 |
|---|---|---|
| Same World, between Aetherytes | Teleport | Supported (native `Telepo`, unchanged) |
| Same logical DC (e.g. Adamantoise → Cactuar) | World Visit | **Supported** via Lifestream `/li`, with verified arrival |
| Another logical DC in the same region (e.g. Aether → Primal) | World Visit with the DC dropdown | **Unavailable**: no supported automation (Lifestream 2.6.0.9 offers only obsolete Data Center Travel). The whole target DC is skipped with an explicit reason, and **nothing logs out**. |
| Another region (e.g. Materia → Aether) | Data Center Travel | Supported via Lifestream Data Center Travel (legacy logout path, unchanged, unverified live). Only reachable when the character starts outside NA. |

## 5. Confirmed integration defects (0.3.12 under 7.58)

| ID | Defect | Evidence |
|---|---|---|
| **M1** | Travel to another NA logical DC was sent down Lifestream's Data Center Travel path (Home World detour, logout, character select), which 7.58 removed for NA/EU | `ShoutRunnerTransferClassification.Classify`: `CanVisitCrossDC == true` → `CrossDataCenter` → `TravelToWorldAsync(crossDataCenter: true)` → `/li <World>`; Lifestream `Lifestream.cs` `TPAndChangeWorld` `DCVType.HomeToGuest`; official NA/EU topic |
| **M2** | `CanVisitCrossDC` treated as one travel method although 7.58 splits it into two (same-region in-game, cross-region DC Travel) | Lifestream `DataStore.BuildWorlds`; official topic |
| **M3** | Arrival and the pre-shout guard compared World names only; Data Center was never verified | `ShoutRunnerArrivalVerifier`, `ProcessShoutWorldCheckCompletion` (0.3.12) |
| **M4** | A wrong-World arrival faulted the whole run instead of skipping that World (product requirement: one failed World must not end the route) | `ProcessTransferCompletion` → `EnterFaulted` (0.3.12) |
| **M5** | No travel-time guard against issuing `/li` for a World in another logical DC of the same region, so a character moved between classification and travel could still hit the obsolete path | `TravelToWorldAsync` |
| **M6** | The catalog was never checked against game data | `ShoutRunnerCatalog` is a static list |

## 6. Jenova incident re-evaluated

The report arrived just before 0.3.12 (published 2026-11-11 23:54 UTC). Patch 7.58 went live on 2026-11-10. The
incident therefore very likely happened on the 7.58 client: a same-logical-DC World Visit through a
`WorldTravelSelect` window that now has a DC dropdown, driven by a Lifestream build written before that dropdown
existed. Lifestream selects by **index** into a string array it reads from the game and confirms without checking
the World name. A changed array layout or row offset would select a different row, with no error.

This makes the 0.3.12 hypothesis L1 **more likely, but it is still unverified**. Nothing proves the array changed;
it would need the game client. It is also consistent with the index arithmetic: on Adamantoise, Cactuar is row 0
and Jenova is row 3 of the remaining Aether Worlds, a shift a dropdown or extra header rows could produce. That is
speculation, not proof. The other possibilities in the 0.3.12 report (§6) are not excluded.

Consequences for 0.3.13: wrong-World arrivals are now expected to be possible on any same-DC visit until Lifestream
is updated. VenueOS's protections are: stable verdict, no announcement on a wrong World or Data Center, skip the
World and continue, and fault after two consecutive wrong arrivals so a systematically broken selector cannot wander
the route.

## 7. Required migration work (implemented in 0.3.13)

1. Game-data placement (World id, logical DC id and name, region) for the current and target World, resolved on the
   framework thread.
2. A pure capability classifier (`ShoutRunnerTravelClassification`): same logical DC / same-region other DC (7.58,
   unsupported) / cross-region / Unknown, plus a catalog-vs-game-data check.
3. The route skips a Data Center whose travel capability is unsupported, with an explicit reason, recorded in the
   checkpoint's skipped Data Centers and in Diagnostics. It never issues `/li` for it.
4. A travel-time guard in the engine: `/li` is refused when the target is in another logical DC of the current region.
5. Data Center verification in arrival and in the pre-shout guard.
6. Wrong-World arrival → World skip + continue, with a fault on the second consecutive one.
7. Diagnostics: requested/observed Data Center, transfer method, capability result, Lifestream version.

## 8. Automated test strategy

Deterministic doubles only: a scripted game/Lifestream timeline through the real verifier and the real
`ShoutRunnerService` with real chat. The capability rules are tested against the facts current Lifestream source
actually computes (`CanVisitSameDC`/`CanVisitCrossDC` semantics). No invented Lifestream API is mocked. Mutation
checks cover the capability rule, the travel-time guard, DC verification and the wrong-World skip bound. Results are
in §10.

## 9. Remaining live QA limitations

- In-game cross-logical-DC travel cannot be automated by VenueOS 0.3.13. It is blocked on an external dependency
  (Lifestream) or on a documented interface for the new dropdown.
- Same-logical-DC World Visit depends on Lifestream's pre-7.58 selector working with the new window. Unverified;
  protected as described in §6.
- The cross-region (Oceania) Data Center Travel path is unchanged and unverified on 7.58.

## 10. Implementation results

### Compatibility decision

**B — partial.** Lifestream supports same-logical-DC World Visit (selection unverified with the new window, protected
by verification). It does **not** support the 7.58 in-game cross-logical-DC travel: its only path for that is the
obsolete one. A VenueOS-native implementation was considered and rejected. It would mean automating a brand-new
native window (`WorldTravelSelect` with a DC dropdown) whose callback layout is documented nowhere (Lifestream,
FFXIVClientStructs 7.58), with no way to test it live. That would be invented automation. **In-game cross-DC
automation is therefore an external-dependency blocker.** The rest of the route is shipped safely around it, and
nothing ever falls back to the obsolete logout path.

### Changes

| File | Change |
|---|---|
| `ShoutRunnerModels.cs` | `ShoutRunnerCrossDataCenterCheck.InGameDataCenterTravelUnsupported`; `ShoutRunnerWorldPlacement`, `ShoutRunnerObservedLocation`; `ShoutRunnerTravelClassification` (`Classify` from game data + Lifestream confirmation + catalog check; `WouldUseObsoleteDataCenterTravel`); `ShoutRunnerTransferResult.WrongDestination` |
| `ShoutRunnerArrivalVerifier.cs` | Optional target Data Center: arrival needs a matching DC, an unknown DC is never arrival, and the right World on the wrong DC is a wrong destination; DC shown in the history |
| `IShoutRunnerAutomation.cs` | `TryGetCurrentLocationAsync` (World + DC) replaces `TryGetCurrentWorldAsync`; `TravelToWorldAsync` contract: `crossDataCenter` = cross-region only, and same-region other-DC targets must be refused |
| `ShoutRunnerService.cs` | Unsupported DC → `SkipRemainingDataCenter` with reason + Diagnostics, no travel command; `WrongDestination` → World skip, continue; `MaxConsecutiveWrongDestinations = 2` → fault; count reset on success, new RUN and Resume; pre-shout guard checks World **and** DC |
| `ShoutRunnerAutomationService.cs` | Game-data placements (World id, DC id/name, region) on the framework thread; classification logged with all facts and the Lifestream version; **travel-time guard** before `/li` (other logical DC in the same region, or a method/region mismatch → refused, no command); verifier gets the target DC; `WrongDestination` outcome; transfer diagnostics carry the requested DC, method and Lifestream version; the cross-DC wait loop is documented as cross-region only |
| `ShoutRunnerOperatorPanel.cs` | One info banner on the Data Centers card explaining the 7.58 limitation |

**Obsolete logout paths:** for travel inside a region, no VenueOS path now issues `/li` for another logical DC. The
classifier never returns the Data Center Travel method for it, and the travel-time guard refuses it even if asked.
The logout-tolerant wait loop and readiness/login recovery stay, but only for cross-region Data Center Travel and
for genuine disconnects or reloads.

### Tests

`ShoutRunnerPatch758TravelTests.cs` (a partial of the 0.3.12 compatibility test class, sharing its harness): **25
tests**. They cover the capability rule for same DC, four same-region other-DC pairs, every Lifestream answer for
same-region other DC, cross-region, an unlisted same-DC World, missing placements and a stale catalog; the
travel-time guard; the catalog's four DCs × 8 Worlds; DC verification (match, wrong DC, unknown DC); and service
scenarios:
- the full multi-DC route from Aether: Crystal/Primal skipped, no travel command outside Aether, no Data Center Travel;
- starting on Primal;
- a cross-region start using Data Center Travel once;
- one congested World plus one wrong World without a DC skip, and no retry loop;
- a fresh plan and checkpoint after a skipped World;
- the consecutive count resetting after a success;
- two consecutive wrong arrivals faulting with the checkpoint kept;
- the pre-shout DC mismatch;
- Stop between DCs;
- Resume after an unsupported-DC skip (never retried, nothing repeated);
- Resume mid-DC without repeats.

Updated 0.3.12 tests (product-requirement change, safety assertions kept): the reported Jenova sequence now expects
"Cactuar — SKIPPED" and the route continuing (plus a new crash-after-skip resume test), and the fault/resume and
reload tests reach their fault through two consecutive wrong arrivals. The test fakes implement
`TryGetCurrentLocationAsync`. One diagnostic wording was kept unchanged so an existing assertion still holds.

**Mutation checks (9/9 killed):** same-region other DC as Data Center Travel (9 failures); guard disabled (5);
catalog mismatch ignored (1); verifier ignores DC (2); pre-shout ignores DC (1); immediate fault on a wrong arrival,
the 0.3.12 behavior (8); no consecutive bound (3); count never reset (1); unsupported DC treated as a one-World skip (4).

### Validation

| Check | Result |
|---|---|
| `dotnet test VenueOS.sln -c Debug` | **1518 / 1518 passed**, 0 failed, 0 skipped (Core 69, Venues 23, Services 1426) |
| `dotnet build VenueOS.sln -c Debug` | 0 warnings, 0 errors |
| `dotnet build VenueOS.sln -c Release` | 0 warnings, 0 errors |

### Manual QA checklist (future)

Live QA: **Pending — owner unavailable; release authorized based on automated verification.**

1. **Same-DC route (Aether only):** every World shows `starting` → shouts → `completed`. Diagnostics show
   `World Visit (same logical DC)`.
2. **Adamantoise → Cactuar:** watch the World Visit window (with its new DC dropdown). If Lifestream picks the wrong
   row, expect `Cactuar — SKIPPED: Arrived at <World> instead of Cactuar…` and the route moving on, with nothing
   shouted there.
3. **Multi-DC selection (Aether + Primal) while on Aether:** Aether runs; `Primal — SKIPPED: travel to another Data
   Center now happens in game…`. **Confirm the character is never logged out, never sent home and never shown
   character select.**
4. **Start on Primal with Aether + Primal selected:** Aether is skipped and Primal runs.
5. **Manual DC change:** travel by hand to another DC (Visit Another World Server → DC dropdown), start a run with
   that DC selected, and confirm it runs.
6. **Congested World:** if one is congested, it is skipped and the route continues within the DC.
7. **Wrong DC / manual travel mid-run:** the next shout is withheld (`… withheld …`).
8. **Stop / Resume / crash:** as in the 0.3.12 checklist. Also crash after a DC skip, then Resume: the skipped DC is
   listed and not retried.
9. **Line 1 / Line 2:** unchanged (2 s gap).

If something goes wrong, send Copy Terminal, the Diagnostics entry, the `/xllog` lines with `ShoutRunner` and the
Lifestream lines from that minute, and the Lifestream version (now also in the diagnostics).

Release artifact details are recorded in the 0.3.13 publication commit message.
