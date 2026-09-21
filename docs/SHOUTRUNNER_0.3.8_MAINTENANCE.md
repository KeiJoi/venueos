# ShoutRunner 0.3.8 Maintenance — Add Aetheryte + Optional Second Shout Line

Work Package 3 of the post-0.3.7 maintenance cycle (see [`POST_0.3.7_TRAINING_AUDIT.md`](POST_0.3.7_TRAINING_AUDIT.md), items 3 and 4).
Status of both items: **IMPLEMENTED — awaiting live QA.** Nothing here has been run in FFXIV; automated tests cover the
service/model layer only (see §16 for exactly what that does and does not prove).

Scope: VenueOS only. No version bump, packaging, `repo.json` change, commit, push, tag or release.

---

## Part A — "Add Aetheryte"

### 1. Old behavior

Settings → Modules → ShoutRunner → **Destinations** had a free-text box (hint "e.g. Ul'dah - Steps of Nald") and a small
button labelled just **"Add"** — the words "Add Aetheryte" appeared nowhere in the UI. The click path was:

`ShoutRunnerOperatorPanel.DrawDestinationsEditor` → `UiKit.PrimaryButton("Add") && !string.IsNullOrWhiteSpace(buffer)` →
`ShoutRunnerService.AddDestination(string)` (a `void`) → `Mutate(...)` → `profiles.SaveModuleConfig(...)`.

### 2. Root cause (what the trace established, and what it could not)

The forensic trace found **no broken persistence or routing path**: `AddDestination` already appended to the saved list, and
an existing test (`Shout_message_..._persist_across_reload`) already proved a custom name persisted. The runtime never used a
hard-coded three-city list — `Destinations` (a `List<string>`, seeded with the three defaults) is the only route source, and
`Start()` snapshots it into the run.

What made the control behave as "does nothing" / unreliable, per the code:

1. **Silent no-op.** With an empty (or whitespace) box the click was swallowed by `&& !IsNullOrWhiteSpace(...)` with no
   message of any kind. An operator who clicked "Add" expecting to *pick* an Aetheryte (there was no picker) got nothing.
2. **No validation, no feedback.** Any typed text was accepted. A name that is not a real Aetheryte name (the route engine
   resolves destinations by exact place name → exact territory name → unique substring against the game's Aetheryte sheet)
   was stored and looked fine, then failed only at run time (*"No attuned Aetheryte matched…"*) — or, for a near-miss, was
   ambiguous.
3. **No discoverability.** The operator had to already know the exact in-game place name.

**Honest limit:** I could not run the game, so I could not reproduce a click that did nothing *with text present*. Nothing in
the code path explains that case (no ID collision — `"Add"` is unique in that window; the service call is synchronous and
saves). If live QA still shows a click with valid text doing nothing, that is a new, ImGui-level finding to investigate
separately; the new control at least reports every outcome on screen, which will make it immediately visible.

### 3. Destination model discovered (unchanged, and kept)

* `ShoutRunnerSettings.Destinations : List<string>` — ordered place names; the three city defaults are simply its seeded
  contents (`ShoutRunnerSettings.Default()`), **not** special enum values, not a separate set, and already removable /
  reorderable / renameable (existing tests and manual).
* Routing (`ShoutRunnerRoutePlanner.PlanWorldRoute`) is generic over any count ≥ 1; the engine
  (`ShoutRunnerAutomationService.TryFindTeleportInfo`) resolves each name against the game's Aetheryte sheet at teleport time.
* No per-entry metadata (ID, territory, expansion) is stored, deliberately: the name **is** the identity the engine already
  resolves, so storing an ID alongside would create a second source of truth that could disagree with it.

### 4. New UX

Under the destination rows: **Add Aetheryte** — a search box, a **[Add Aetheryte]** button, and a short suggestion list
(up to 8, already-configured names hidden) drawn from the game's own Aetheryte data.

* Type to narrow the list, click a suggestion (fills the box) or type an exact name, press **Add Aetheryte**.
* Every outcome is shown under the control: `Added <name>.`, `Enter or pick an Aetheryte first.`,
  `<name> is already in the route.`, `"<name>" is not a known Aetheryte — pick one from the list.`, or a too-long message.
* If the game's list can't be loaded, a warning says so and the box accepts an exact typed name (unverified) rather than
  making Add impossible.
* Built entirely from `Forms.SearchBox`, `Forms.FieldLabel`, `UiKit.PrimaryButton`, `UiKit.ListRow`, `UiKit.WarningState` —
  no raw ImGui inputs. (Existing rows keep their rename field + Up/Down/Remove, unchanged.)

### 5. Data required / validation

One thing: the Aetheryte **name**. `ShoutRunnerService.AddDestination(name)` now returns `ShoutRunnerAddDestinationResult`:

| Result | When |
|---|---|
| `Blank` | empty / whitespace |
| `TooLong` | > 128 characters (`MaxDestinationNameLength`, the field's own buffer) |
| `UnknownAetheryte` | game data available **and** the name isn't in it (case-insensitive match) |
| `Duplicate` | already present (case-insensitive) — a duplicate would only shout twice at one place |
| `Added` | stored, trimmed, in the game's **canonical** spelling, saved immediately |

Authoritative source: the game's `Aetheryte` Excel sheet, real Aetherytes only (`IsAetheryte`, not Aethernet shards) — the
same sheet the route engine resolves against. Provided through a new `IShoutRunnerAetheryteCatalog` (Operations layer, no
Lumina dependency) implemented by `DalamudAetheryteCatalog` (Plugin layer, lazy, retries every 5 s if the sheet wasn't ready).
The catalog is an **optional** constructor argument of `ShoutRunnerService`, so every existing construction site and test
compiles unchanged. It validates only at **add** time; an already-saved destination is never re-checked, so old configs
keep working exactly as before. In-place **rename** of an existing row is unchanged (free text, saved per keystroke) — it is
not validated, because per-keystroke validation would block typing.

### 6. Persistence scope

**Per venue** — unchanged. Destinations live in the existing per-venue payload
(`communication.announcements`, schema version 2). Adding a custom Aetheryte in venue A does not appear in venue B; switching
venues (`Load`) reloads that venue's list. Not global, not per preset, not per route. No schema-version bump: the change is
additive and the `Destinations` property is untouched.

### 7. Runtime routing integration

Nothing needed to change: `Start()` copies `settings.Destinations` into `ShoutRunnerRunConfig`, the planner plans over it,
`TeleportToDestinationAsync(name)` resolves it. A test runs a full route with a custom stop and asserts it is teleported to
and shouted at. **Behavior kept:** a run keeps the destinations it started with — adding/removing mid-run applies from the
next run (tested).

### 8. Remove / edit semantics

Already existed and is unchanged: per-row rename, Up/Down, Remove. Custom entries are ordinary rows, so a mistaken add is
removable. **The three defaults are not protected** — they never were (existing tests remove all three; the manual says the
list is reorderable). I did not introduce a "fixed built-ins" concept, per "do not allow deletion of mandatory defaults *unless
that is already supported and intentional*" — it is. A removed default can be re-added via Add Aetheryte.

### 9. Existing defaults compatibility

`Default()` still seeds exactly `Ul'dah - Steps of Nald`, `New Gridania`, `Limsa Lominsa Lower Decks`. Old saved configs load
unchanged (tested through real serialized storage, with no recovery warning).

---

## Part B — Optional second shout line

### 10. Where the lines live

The Shout Message is edited on ShoutRunner's **operational screen** (the "Shout Message" card), not in Settings — that is the
module's existing design (edits apply live, no Apply step). I kept it there and made the card two fields:
**Shout Line 1** and **Shout Line 2 (Optional)**, same `Forms.TextField` component, with one help line: *"If both lines are
filled, Line 1 is sent first, followed by Line 2."* No new live UI beyond that.

### 11. Data model and backward compatibility

`ShoutRunnerSettings` gains `string ShoutMessageLine2 = ""` (last positional parameter, defaulted). `ShoutMessage` keeps its
name and **is** Line 1. An old payload has no `ShoutMessageLine2`; System.Text.Json fills the constructor default → Line 1 =
old text, Line 2 = empty (tested from a literal 0.3.7-shaped JSON string and through real storage). No schema-version bump.

### 12. Semantics (decisions made explicitly)

* **Blank Line 2** (empty or whitespace-only after trim): no second line, no second enqueue, no pacing gap.
* **Line 2 filled, Line 1 blank — DECISION: refuse.** `Start()` returns `ShoutMessageRequired` ("Enter Shout Line 1 before
  starting."). Rationale: the field is labelled Optional, the existing rule already requires the message, and silently
  promoting Line 2 to "the" message would surprise an operator who half-cleared the wrong field. The live panel also warns
  ("Line 2 is only sent together with Line 1"). Mid-run, blanking Line 1 fails that shout as before (never sends Line 2 alone).
* Whitespace is trimmed from both lines before sending.

### 13. Execution, ordering, pacing

Per destination: read Line 1 live → enqueue `/shout <line1>` on the shared `ChatCommandService` with the run's cancellation
token → when the transport **accepts** it (`OnDispatched(success)`), the next `Tick` reads Line 2 live and, if non-blank,
enqueues `/shout <line2>` the same way. Strictly sequential, so order is deterministic (Line 1 always first).

* **Pacing:** no new delay. Line 2 waits for Line 1's accepted dispatch (the existing gate) **and** the shared service's
  minimum dispatch interval (default 1 s) — tested with a 1 s interval: Line 2 does not dispatch in the same instant.
  `DelayBetweenActionsSeconds` still paces route actions *after* the whole shout.
* Both lines use the identical `/shout` channel, destination loop, schedule, world travel, repeat, enable/disable, preset/config
  and diagnostics path — there is no per-line channel or route control.

### 14. Chat-byte validation

One implementation for both lines: `ShoutRunnerShoutLines` measures UTF-8 bytes of the **full command including `/shout `**
via `BlockTextLength.CountBytes` against `BlockLettersLimits.ChatBytes` (500) — the same rule the Shouts module uses. Notes:

* **Line 1 previously had no byte validation** (only a 500-character field buffer, which would still let
  `"/shout "` + text exceed 500 bytes, and counted characters, not bytes). Both lines are now validated; this is a small,
  deliberate tightening of Line 1, not just parity for Line 2.
* At `Start()`: `ShoutMessageTooLong` / `ShoutMessageLine2TooLong` refuse the run. In the UI each field shows
  `N / 500 bytes once its /shout command is included — shorten this line.`
* At send time (the text is read live, so it can change mid-run): an over-limit line is **refused, never truncated or sent**
  (`BeginShoutLine` completes it as failed with the byte count in the terminal line).

### 15. Partial failure and cancellation

* **Line 1 fails** → Line 2 is not sent for that destination; terminal: `SHOUT FAILED: Line 1 failed (…); Line 2 not sent.`
  (a single-line config still prints the old `SHOUT FAILED.`).
* **Line 1 sent, Line 2 fails** → terminal: `SHOUT INCOMPLETE: Line 1 sent, Line 2 FAILED (…).` — never reported as success;
  Line 1 is **not** resent; the run continues to the next destination as it does for any failed shout.
* **Both sent** → `SHOUT SENT (2 lines).` (a one-line shout still prints `SHOUT SENT.`).
* **Recovery journal:** a destination is checkpointed as completed only when the whole shout (both lines) succeeded. A partial
  shout is a failed destination like any other, so Resume may retry it — which would resend Line 1. That is the journal's
  existing contract for any non-successful destination; I left it alone. (Separately observed, *not changed*: the journal
  identifies completed steps by count, so any earlier failed step can shift where Resume replays from — pre-existing, one-line
  shouts included.)
* **Cancellation:** Line 2 is enqueued only from `Tick`, which polls the shout task only while `State == SendingShout`. Stop,
  `HardStop` (venue switch, module disable, dispose) and fault all leave that state and cancel the run token, so (a) Line 2 is
  never enqueued afterward, and (b) a Line 2 already queued but not yet dispatched is skipped by the shared service's own
  token check. No dispatch-callback can enqueue Line 2, so there is no stale delayed callback. All three cases tested.

---

## 16. Tests

`tests/VenueOS.Services.Tests/ShoutRunnerMaintenanceTests.cs` — **42 new tests** (full suite: 1231 → **1273**). They drive the
real `ShoutRunnerService`, the real `ChatCommandService` (capturing transport, controllable clock), and an in-memory venue
store that is genuinely destroyed and rebuilt from its serialized snapshot for the persistence checks.

*Add Aetheryte (`ShoutRunnerAetheryteTests`):* defaults intact; literal old-JSON loads; old payload via real storage with no
recovery warning; valid add + canonical spelling; persistence across service reconstruction; plain JSON round trip; a full
run teleports to and shouts at the custom stop; planner treats it as any destination; blank/whitespace/unknown/too-long
rejected with no change; duplicate (vs default and vs custom, case-insensitive) rejected; no-catalog fallback accepts a
typed name; custom removal persists and is not routed; defaults remain ordinary/removable; a run keeps its starting route;
per-venue isolation, venue switch reload, and reload after both.

*Second line (`ShoutRunnerSecondLineTests`):* old one-line config → Line 1 + empty Line 2; Line 1 only → exactly one dispatch
per destination; both → exactly two, alternating Line 1 / Line 2 order; blank/whitespace Line 2 → one; Line 1 blank + Line 2
filled refused; trimming; UTF-8 byte counting (2-byte chars, exact-limit boundary); independent Line 1/Line 2 validation at
Start; oversized Line 2 refused with nothing sent; Line 2 made oversized mid-run is never truncated/sent and Line 1 not
resent; Stop after Line 1 prevents Line 2; queued-but-undispatched Line 2 skipped on Stop; venue switch and module disable
cannot deliver a stale Line 2; Line 2 transport failure → partial failure, no duplicate Line 1; Line 1 failure → no Line 2;
partial shout not checkpointed as completed; shared 1 s chat pacing; same `/shout` channel/semantics for both lines; both
lines persist across reconstruction; Line 2 is per-venue.

**Not covered by automation** (no ImGui test project exists — `NEW_MODULE_GUIDE.md` §30): the rendered picker/fields, the
`DalamudAetheryteCatalog` Excel read, real teleport/Lifestream, real `/shout` delivery, and real-time cancellation timing.

## 17. Live QA checklist (not yet performed)

**Add Aetheryte**
* **A —** Settings → Modules → ShoutRunner: the three defaults are listed. Under them, "Add Aetheryte" shows suggestions.
  Type "Foun" → pick **Foundation** → **Add Aetheryte** → "Added Foundation." and a new row appears. Also try: press with an
  empty box (message, no silent nothing), a made-up name (rejected), a name already listed (rejected). Close/reopen Settings:
  still there.
* **B —** Start a run with the custom stop (character has that Aetheryte unlocked). Confirm the terminal shows the teleport
  to it and `SHOUT SENT` there.
* **C —** `/xlreload`: the destination is still listed. Switch venue: the other venue does not have it; switch back: it does.
* **D —** Remove the custom row: it disappears, and the next run does not visit it.

**Two lines**
* **E —** Line 1 only → exactly one `/shout` per destination.
* **F —** Both lines (visibly different text) → two `/shout`s per destination, Line 1 then Line 2, ~1 s apart, no duplicates or
  reversal.
* **G —** Line 2 = spaces only → one message only.
* **H —** Line 1 empty, Line 2 filled → Start refused ("Enter Shout Line 1 before starting."), warning under the fields.
* **I —** Paste an over-long line into either field → byte warning, Start refused, nothing truncated.
* **J —** Press Stop in the ~1 s between the two lines if you can → Line 2 does not appear.
* **K —** `/xlreload`: both lines are restored.

## 18. Files changed by this work package

Modified:
* `src/VenueOS.Modules.Operations/ShoutRunner/ShoutRunnerModels.cs` — `ShoutMessageLine2`, `ShoutRunnerShoutLines`,
  `IShoutRunnerAetheryteCatalog`, `ShoutRunnerAddDestinationResult`, two new `ShoutRunnerStartResult` values.
* `src/VenueOS.Modules.Operations/ShoutRunner/ShoutRunnerService.cs` — validated `AddDestination`, `UpdateShoutMessageLine2`,
  optional catalog, Start validation, two-line shout sequencing.
* `src/VenueOS.Plugin/ShoutRunnerOperatorPanel.cs` — two-line card, Add Aetheryte picker, byte warnings, Start messages.
* `src/VenueOS.Plugin/Plugin.cs` — one line: passes `DalamudAetheryteCatalog` into `ShoutRunnerService`.
* `docs/USER_MANUAL.md` — ShoutRunner section (bundled into the plugin as the in-game manual).
* `docs/POST_0.3.7_TRAINING_AUDIT.md` — ledger rows 3 and 4.

New:
* `src/VenueOS.Plugin/ShoutRunner/DalamudAetheryteCatalog.cs`
* `tests/VenueOS.Services.Tests/ShoutRunnerMaintenanceTests.cs`
* `docs/SHOUTRUNNER_0.3.8_MAINTENANCE.md` (this file)

Untouched: everything from WP1 (Party Finder) and WP2 (Brackets/Tournament, Bingo companion docs); no backend/donor repo.
