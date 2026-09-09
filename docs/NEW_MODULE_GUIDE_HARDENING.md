# NEW_MODULE_GUIDE.md Hardening Report

## 1. Executive Summary

This pass hardened `NEW_MODULE_GUIDE.md` from a well-verified but incomplete architecture reference into an implementation contract strong enough that a fresh Claude session can build Giveaways, Block Letters, or Macro without rediscovering rules through donor archaeology or repeating mistakes already paid for in Raffle's and TournamentControl's reconstructions. The guide grew from 652 to 899 lines (+247, ~38%). No module implementation work was started, no existing module was redesigned, and no source control state was changed — this was a documentation-only pass, exactly as scoped.

The approach was additive, not a rewrite: every new rule was inserted as a new lettered subsection (matching the guide's own existing `42a`/`42b` convention — e.g. `9a`, `13a`, `34b`) so that every existing `(§N)` cross-reference in the 652-line original stayed valid. Two genuine self-contradictions introduced by the hardening itself were found and fixed during the required self-consistency pass (§13 below).

## 2. Current guide strengths preserved

The guide was already unusually rigorous: every class/method/file path was previously verified against the live repository, it already used strong prescriptive language in most sections, it already had a Definition of Done, a minimal worked example, and an explicit "known inconsistencies" section (§21) rather than pretending legacy debt didn't exist. None of that was rewritten. All existing section numbers, existing code examples, and the existing `§21` legacy-inconsistency list were kept intact and built on rather than replaced.

## 3. Repository patterns inspected

Via two parallel research passes (verified against source, not guessed):

- **Credential UI**: `Forms.TextField`'s `password` flag (never set `true` anywhere), and the explicit rationale comment in `RaffleOperatorPanel.cs`.
- **Target capture**: `ITargetedPlayerProvider`/`TargetedPlayerLookup` (`SharedServices.cs`) and `DalamudTargetedPlayerProvider` (`Plugin.cs`), plus call sites in `NativeOperationsPanels.cs` (VIP), `RaffleOperatorPanel.cs`, `VenueBingoOperatorPanel.cs`.
- **Confirmations**: every `ConfirmDialog`/`confirmDialog.Request` call site across the Plugin project and their text patterns.
- **Import/export**: `RaffleXlsx.cs` (`RaffleXlsxExporter`/`RaffleXlsxImporter`) and `FileQuestionSetRepository.cs`/`MairsEditorService.cs`'s `CheckImportCollision`/`ImportReplacing`/`ImportAsNew`.
- **Archive/Delete/Reset**: `VenueRaffleService.Archive`/`Unarchive`/`Reset`/`DeleteAsync`, contrasted with TournamentControl's and Bingo's delete-only model.
- **Realtime clients**: `RaffleRealtimeClient.cs` and `TournamentRealtimeClient.cs` — transport abstraction, `Start`/`Stop`, linear-backoff-capped-at-10x pattern, `ConsumeReconnectSignal`, queue-draining.
- **Authority model**: Raffle (local-authoritative) vs. TournamentControl (backend-authoritative, per its own doc comment in `TournamentControlService.cs`) as the two live opposite examples.
- **`VenueProfileService`**: exact current public signatures read directly from `src/VenueOS.Venues/VenueProfileService.cs`.
- **`ModuleDescriptor.UnderDevelopment`**: confirmed as a real, currently-used field (`src/VenueOS.Core/Modules.cs`), with generic `StatusBadge`/`InfoBanner` wiring in `ModulesSettingsPage.cs`, and live usage on `games.raffle`/`games.tournament`.

## 4. Reconstruction lessons incorporated

Read in full: `docs/RAFFLE_FORENSIC_AUDIT.md`, `docs/RAFFLE_RECONSTRUCTION.md`, `BRACKETS_RECONSTRUCTION.md`, `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md`, plus spot-checked `docs/BINGO_FORENSIC_AUDIT.md`, `PARTY_FINDER_RECONSTRUCTION.md`, and the Mair's Trivia/Editor audit for generalizable rules. Concrete lessons folded into the guide (with attribution kept out of the guide itself, per the task's "not a history book" instruction):

- A URL secret embedded as a path segment defeats a `key=value` redaction marker list → §26 now explicitly calls this out.
- A UI-editable field that never round-trips to a save call is a real, previously-shipped bug class → §13a.
- An in-memory-only ledger gating "did this already happen" caused real double-action risk → folded into §13a's persistence-contract rationale.
- A realtime client that only appends state on reconcile can silently lose an optimistic update made during a disconnect → §34b's reconciliation bullet.
- "Close and reopen the panel" is not proof of persistence, only a disable/re-enable or serialize/deserialize round trip is → already partly in §30, reinforced in §13a and the new Data/Live QA Definition-of-Done categories.
- Import must mint fresh identity, never inherit backend linkage → §34c.
- Archive/Reset/Delete are three distinct, previously-conflated operations → §13b.
- Confirmation dialogs are a client-side courtesy; the backend must enforce authoritatively regardless → §37.
- Never truncate/strip HomeWorld for "matching" purposes → strengthened in §28.

## 5. Sections added (new content, not present before)

`9a` Credential fields — plain text, MUST NOT be masked · `12a` Venue-switch isolation checklist · `13a` Persistence contract · `13b` Archive vs. Delete vs. Reset · `22a` UnderDevelopment and promotion · `24a` Stale async/stale realtime guard · `28a` Target capture pattern · `34a` State authority model · `34b` Realtime/WebSocket module contract · `34c` Import/export · `41a` Visual Definition of Done. Plus a new unnumbered front-matter block (normative-language legend, the "no self-authorized deferrals" rule, and the proportionality/"don't over-harden" rule) placed before §1 so a fresh session sees it before implementation begins, per the task's explicit placement requirement.

## 6. Sections substantially changed (existing section, materially strengthened)

- **§8/§9** (Settings vs. operational UI): the `DrawSettings() => Draw()` pattern went from "known gap, should do better" to an explicit MUST-split rule with a narrow, must-be-documented exception.
- **§21** (legacy inconsistencies): the two file-organization bullets (`Operations.cs`, `NativeOperationsPanels.cs`) went from "should use its own file" to "MUST NOT be added to."
- **§26** (logging/secrets): added the URL-secret-shape warning and a SHOULD for backend clients to sanitize before ever reaching Diagnostics.
- **§30** (testing): added fake-transport testing for realtime/HTTP clients, random-invariant testing guidance, and an explicit "tests do not prove ImGui/Dalamud/target/browser behavior" statement.
- **§33** (file/project structure): upgraded to MUST language and the canonical structure now includes the `RealtimeClient.cs`/`Storage.cs`/`Models.cs` shape, citing Raffle/TournamentControl's actual current folder layout as precedent instead of a hypothetical one.
- **§36** (states): expanded from Empty/Loading/Warning/Error into a full Empty/Loading/Success-status/Warning/Error/Disconnected table with an explicit "MUST NOT rely on" list.
- **§37** (destructive actions): expanded into a full confirmation-text policy (state the consequence, "cannot be undone" vs. "does NOT affect X"), severity-scales-with-consequence, and the client-courtesy-not-the-guard rule.
- **§39** (planning template): reframed as a mandatory pre-flight checklist and expanded with the previously-missing questions (authority model, realtime justification, GuestIdentity/target capture, confirmations, import/export, archive/delete/reset, UnderDevelopment).
- **§42** (Definition of Done): reorganized from 9 categories to 12 (added Architecture, Venue, Security, Guest Identity, Data, Live QA, Promotion) while keeping every existing checkbox.
- **§43** (AI agent instructions): reorganized into Before/During/After, with explicit no-self-authorized-deferral, completion-report-path, and don't-claim-live-QA-you-didn't-do rules.

## 7. Legacy inconsistencies explicitly marked "do not copy"

All pre-existing §21 items were kept and, where relevant, upgraded to MUST NOT language (see §6 above). No new legacy inconsistency was discovered during this pass beyond what §21 already documented; the `MODULE_DEVELOPMENT.md` contradiction (below) was the one new stale-reference find.

## 8. UI/confirmation rules added

§37 confirmation-text policy; §41a Visual Definition of Done (17-item checklist, explicitly not satisfiable by automated tests); §36's expanded state table; §9a's credential-field visual convention.

## 9. Persistence/security rules added

§9a (plain-text credentials, product convention, distinct from diagnostic redaction); §13a (persistence contract, UI-fields-are-not-storage); §13b (archive/delete/reset); §26's strengthened secret-shape warning (query param / header / path segment, not just `key=value`).

## 10. Lifecycle/realtime rules added

§12a (venue-switch isolation checklist); §24a (stale async/stale realtime guard, cancellation ownership); §34a (authority model); §34b (full realtime contract: transport abstraction, viewer-only join, linear backoff capped at 10x, queue-draining, reconnect-triggers-REST-reconciliation); §22a (UnderDevelopment/promotion lifecycle).

## 11. Testing/live-QA rules added

§30 additions (fake transport, invariant testing for randomness, explicit list of what automated tests cannot prove); §41a (Visual Definition of Done); §42's new Live QA and Data Definition-of-Done categories; §43's "don't claim live QA you didn't perform" rule.

## 12. Definition-of-Done changes

Reorganized §42 into 12 categories (Architecture, Venue, Settings, Lifecycle, UI, Security, Guest Identity, Data, Backend, Testing, Live QA, Promotion) — see §6 above for the full delta. No existing checkbox was removed; items were redistributed into more precise categories and new ones added.

## 13. Any current repository behavior that could NOT safely be generalized

- The realtime backoff pattern (linear, capped at 10x a base unit) is documented as "match this unless a specific backend's behavior demands otherwise" rather than an absolute MUST, since a future backend's own rate-limiting could reasonably require different behavior.
- Archive/Delete/Reset (§13b) is documented as a model to follow *where a module has reusable local objects*, not a universal requirement — Bingo and TournamentControl's delete-only models remain valid for modules with no local-authoritative store.
- `IVenueSwitchGuard` (§42b, pre-existing) was deliberately left as a narrow, single-purpose hook per its own text — not generalized into a broader mechanism, matching the existing guide's own stated intent.

## 14. Unresolved architectural inconsistency documented rather than "fixed"

Two were found and deliberately left as documented facts rather than corrected in source (per the task's documentation-first scope):

- `MairsTriviaSettings`/`TournamentModuleSettings`/`VenueBingoSettings` still carrying their own `VenueName` field (pre-existing §21 item, left as-is).
- `GameContextService`/`VenueHttpClientFactory` remaining unused scaffolding (pre-existing §21 item, left as-is; the one actual fix made was to `MODULE_DEVELOPMENT.md`'s contradictory advice to use it — see §15).

## 15. Exact files modified

- `C:\FFXIVplugs\venueos\NEW_MODULE_GUIDE.md` — the hardening pass (652 → 899 lines).
- `C:\FFXIVplugs\venueos\MODULE_DEVELOPMENT.md` — one-paragraph correction: it told readers to use `PresenceService`/`ChatCommandService`/`SchedulerService`/`NotificationService`/`VenueHttpClientFactory`, directly contradicting both its own earlier correction note and `NEW_MODULE_GUIDE.md` §21/§27, which state `VenueHttpClientFactory` has no callers anywhere and `NotificationService` has no renderer. This is the one small documentation-only correction the task explicitly permitted to prevent an authoritative document from contradicting the hardened guide.
- `C:\FFXIVplugs\venueos\docs\NEW_MODULE_GUIDE_HARDENING.md` — this report (new file).

No other file was modified, staged, or touched.

## 16. Git status

At task start, the working tree already had unrelated uncommitted work from the Brackets and Raffle reconstructions (modified: `Operations.cs`, `VenueRaffleClient.cs`, `TournamentControlClient.cs`, several test files, `Plugin.cs`, etc.; untracked: `RaffleRealtimeClient.cs`, `VenueRaffleService.cs`, `TournamentControlService.cs`, `TournamentRealtimeClient.cs`, several new test files, `BRACKETS_RECONSTRUCTION.md`, `TOURNAMENT_CONTROL_BRACKETS_AUDIT.md`, `docs/RAFFLE_FORENSIC_AUDIT.md`, `docs/RAFFLE_RECONSTRUCTION.md`, `src/VenueOS.Plugin/Raffle/`). All of that was left untouched — `git diff --stat` after this pass shows only `MODULE_DEVELOPMENT.md` and `NEW_MODULE_GUIDE.md` changed beyond what was already modified, plus this new report file is untracked.

## 17. Confirmation

Nothing was staged, committed, pushed, tagged, branched, or released. No `git add`, `git commit`, `git push`, `git tag`, or branch-creation command was run at any point during this task.
