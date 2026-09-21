# Attendance Nearby Guests + Raffle Active-Run Authority — 0.3.8 Maintenance (Work Package 4)

Status: **IMPLEMENTED — awaiting live QA.** Automated tests and Debug/Release builds pass; neither is FFXIV live
acceptance. Nothing here is staged, committed, pushed, tagged, version-bumped or packaged (see the release rules in
[`POST_0.3.7_TRAINING_AUDIT.md`](POST_0.3.7_TRAINING_AUDIT.md)).

---

## Part A — Attendance "Guests Nearby" layout (ledger item 5)

### Root cause

`AttendanceOperatorPanel.DrawLive` rendered each guest with `UiKit.ListRow(theme, guest.Name, subtitle, false)`.
`ListRow`'s two-line form draws an `ImGui.Selectable` with a **fixed 34 px height** (name at its top) and then draws
the subtitle with `TextUnformatted` **after** the selectable, i.e. below the whole 34 px box. The result was:

```text
Name
                <- blank gap left by the 34 px selectable
World Name      <- subtitle
Next Name       <- next row starts immediately under the previous world
```

The world sat visually closer to the *following* guest's name than to its own. **Presentation defect only.**

### Data path (verified, unchanged)

`IObjectSnapshotProvider.Snapshot()` → `PresenceService` → `AttendanceService.Guests`
(`Dictionary<string, PlayerSnapshot>` keyed by normalized `GuestIdentity.Key`) → panel. Each guest is a
`PlayerSnapshot(Name, HomeWorld, ObjectId, TerritoryId, Position)` — name and world come from the same game object
and travel in the same record from source to render, so **the pairing was already correct**. The panel's filter
(case-insensitive name-contains) and ordering (`OrderBy(Name)`) only ever operate on whole snapshots and cannot
separate a name from its world.

### Fix

- New pure helper `src/VenueOS.Modules.Operations/Attendance/NearbyGuestRows.cs`:
  `NearbyGuestRow(Name, HomeWorld, Greeted)` with a single `Text` property, and `NearbyGuestRows.Build(...)`, which
  contains exactly the filter and ordering the panel previously did inline (moved, not changed).
- The panel now draws **one single-line `UiKit.ListRow` per guest** (subtitle = `null`, so `Selectable` height 0):
  `Name — HomeWorld`, or `Name — HomeWorld · Greeted` once Attendance reports the guest greeted. A blank/missing world
  renders as the bare name (no dangling dash). Greeted state is still read from `AttendanceService.IsGreeted`.
- `UiKit.ListRow` itself was **not** changed (other screens use its two-line form; general UI cleanup is a later
  phase).

No change to attendance tracking, greeting state, presence semantics, VIP integration, venue isolation or
persistence.

### Tests / proof

`tests/VenueOS.Services.Tests/NearbyGuestRowsTests.cs` (10 test cases): row text is one line; greeted marker; missing
world (3 cases); many guests of different worlds each keep their own world; same name on two worlds stays two
correctly-paired rows; search is case-insensitive name-contains and ignores the world; ordering by name; greeted state
resolved per identity.

**Not automated:** the actual ImGui drawing (the single `ListRow` call). The repo has no UI test harness and none was
invented; everything below the draw call is covered, and the draw call is a one-line use of an existing component.

### Live QA — see checklist A–C below.

---

## Part B — Raffle active-run configuration authority (ledger item 8)

### Old authority model (proven from source)

There is **no separate "Start Raffle" action** in VenueOS's Raffle: the run model is the persisted `LocalRaffle`, and
**Create raffle** is the start. Before this change:

| Aspect | Old behaviour |
|---|---|
| Pre-start storage | `VenueRaffleSettings.Defaults` (`RaffleSettings`: `StartingPot`, `TicketCost`, `PrizePercentage`, `PaidTicketsForFree`, `FreeTicketsPerBlock`), per venue under module config `games.raffle` v1. Edited in Settings → Raffle → "New Raffle Defaults" via `SaveDefaults`. |
| At Create | `VenueRaffleService.Create` copied `Settings.Defaults` into the new `LocalRaffle.Settings`. `RaffleSettings` is an immutable record, so this was already a true **value snapshot** — editing Settings after Create did **not** leak into an existing raffle. |
| Duplicated editable copy | `RaffleOperatorPanel.DrawSettingsSection` (live screen) rendered five editable fields bound to `current.Settings` and wrote them through the **public** `VenueRaffleService.UpdateRaffleSettings(raffleId, settings)`. |
| Consumers | `LocalRaffle.RunningPot` / `PrizePot` / `HouseTake` and `RaffleTicketMath.CalculateFreeTickets(r.Settings, …)` inside `AddPaidTickets`, plus the panel's bonus preview — all read the **raffle's own** `Settings`. |
| Backend | `VenueRaffleClient` sends only the flattened ticket pool; the economics are **not** transmitted, so there is no backend copy. |
| Export/import | XLSX export writes the raffle's settings; import builds a *new* `LocalRaffle` whose settings come from the file (not the defaults). |

**The defect, precisely:** the only route by which any of the five values of an existing raffle could change was the
live-screen editor → `UpdateRaffleSettings`. Editing there retroactively changed `RunningPot`, `PrizePot`,
`HouseTake` and the bonus applied to every *later* ticket purchase of the running raffle, mid-run. (Editing Settings
did not — that was already correct.)

Every mutation path found for a raffle's settings: (1) `UpdateRaffleSettings` (public, live screen only) — **removed**;
(2) the private `Update(raffleId, transform)` funnel used by rename/archive/reset/tickets/publish/realtime-mirror,
which never set `Settings` but *could* through a `with` transform — **now guarded**; (3) `ImportRaffle` inserting a
whole new raffle — legitimate, new raffle; (4) `Create` — the snapshot itself. `Settings.Raffles` is a public mutable
list (existing test seam and persisted DTO); no production code outside `VenueRaffleService` writes to it.

### New authority model

**Settings → Raffle → New Raffle Defaults → *Create raffle* → the raffle's frozen `LocalRaffle.Settings`.**

- `SaveDefaults` is the **only** public service method that accepts `RaffleSettings`. It changes what the *next*
  raffle starts with and nothing else.
- `Create` captures the snapshot (value copy) into `LocalRaffle.Settings`. Documented on `Create`, `SaveDefaults` and
  the `LocalRaffle` record (`<param name="Settings">`).
- `UpdateRaffleSettings` no longer exists.
- `VenueRaffleService.Update` — the single funnel for every per-raffle mutation — now re-imposes the raffle's
  original `Settings` after any transform (`transform(existing) with { Settings = existing.Settings }`). No current or
  future caller can alter an existing raffle's rules through it. This is the structural fix rather than just hiding
  controls.
- All calculations already read `LocalRaffle.Settings`: ticket purchase bonus (`AddPaidTickets` →
  `RaffleTicketMath.CalculateFreeTickets`), `RunningPot`, `PrizePot`, `HouseTake`, and the bonus preview. There is one
  source of truth per raffle; the defaults are never consulted after Create.

### Live screen

`DrawSettingsSection` is now a read-only card, **"This Raffle's Rules (Locked)"**, with the five values as
label/value rows (the same secondary-label / primary-value pattern Attendance uses for its locked "Venue Area Type")
plus the note *"Captured when this raffle was created. Changing Settings → Raffle → New Raffle Defaults only affects
raffles you create afterwards, never this one."* The Settings card gained a matching note. No editable control for
any of the five values remains on the live screen.

### Lifecycle semantics (verified by tests)

- **Before Create:** Settings editable; they determine the next raffle.
- **At Create:** the raffle receives a snapshot of all five values.
- **While running:** all figures use the snapshot; Settings edits do not affect it.
- **Rename / Archive / Unarchive / Reset / ticket edits / publish / realtime winner mirroring:** unchanged; none alter
  the frozen values. (`Reset` still keeps the raffle and its settings, exactly as before.)
- **Next raffle:** `Create` reads whatever the defaults are at that moment.

### Persistence / recovery / migration

`LocalRaffle.Settings` was already a required, persisted member of every stored raffle (inside
`VenueRaffleSettings.Raffles`, module config `games.raffle` v1). A reload / `/xlreload` / plugin reconstruction
therefore restores each raffle with the values it had; a fresh service `Load`s the raffle *and* the (possibly newer)
defaults separately, so a recovered raffle uses its own values and the next `Create` uses the new defaults. **No schema
change, no migration, no fabricated values.** Raffles created under the old build keep whatever their settings were
at upgrade time (including any value an operator had edited live before this fix); those values are simply now
locked. Which raffle is "active" (`SelectedRaffleId`) is persisted per venue, as before.

### Multi-venue

Defaults and raffles live under each venue's own `games.raffle` config and `Load(venueId)` swaps both wholesale (the
existing pattern; unchanged). A test proves venue B never sees A's defaults or raffles, B's edits never touch A's
raffle, and vice versa after switching back and forth.

### Tests

`tests/VenueOS.Services.Tests/RaffleFrozenConfigurationTests.cs` — 15 test cases (11 `[Fact]` + a 5-case `[Theory]`):
Create snapshots all five values; each of the five fields individually stays frozen when its default is edited; bonus
math uses the frozen rule; pot/prize/house-take use frozen Starting Pot/Ticket Cost/Prize %; a later raffle takes the
new defaults while the earlier keeps its own; rename/archive/tickets/reset never alter the frozen values; the `Update`
funnel re-imposes frozen settings against a transform that tries to change them; reflection proof that the only public
method accepting `RaffleSettings` is `SaveDefaults` and `UpdateRaffleSettings` is gone; a fresh service over the same
store restores frozen values and the next `Create` uses current defaults; two-venue isolation; an imported raffle owns
its file's settings.

One existing test, `Adding_paid_tickets_merges_by_name_and_homeworld_and_applies_the_bonus_rule`, previously set the
bonus rule through the removed `UpdateRaffleSettings`; it now sets the same rule via `SaveDefaults` before `Create`
(same assertions, same intent — the rule is now captured at Create). No other existing test was changed.

### Remaining limitations

- A raffle created with the wrong values cannot be corrected in place; the operator creates a new raffle (and
  archives/deletes the old one). This is the direct consequence of "immutable for the raffle's lifetime"; the manual
  says so. No "re-snapshot before first ticket" affordance was added (not requested).
- `Settings.Raffles` remains a public mutable list (existing architecture/test seam). Only `VenueRaffleService`
  mutates it in production code; the guard and reflection tests protect the service boundary, not the raw list.
- The backend never received the economics, so nothing changed on the wire.
- The rendered card and the settings-vs-live behaviour are not covered by automated UI tests (no UI harness).

### Docs updated

`docs/USER_MANUAL.md` (Attendance Guests Nearby line; Raffle defaults/locked-rules wording; removed "change settings"
from the Unpublished Changes explanations, since settings can no longer change after Create) and
`docs/POST_0.3.7_TRAINING_AUDIT.md` rows 5 and 8.

---

## Live QA checklist

### Attendance
- **A — Multiple nearby guests.** Stand near several characters from different Home Worlds. Confirm each row is a
  single line `Name — HomeWorld` and no world looks attached to another guest.
- **B — Guest list changes.** Have characters enter/leave range. Rows update normally; Name/World stay paired; search
  by name still filters.
- **C — Existing behaviour.** Attendance opening/presence/greeting works as before; a greeted guest shows
  `Name — HomeWorld · Greeted`.

### Raffle
- **D — Pre-start settings.** Set distinctive values for all five defaults in Settings → Raffle; confirm they persist.
- **E — Start snapshot.** Create a raffle. The live screen shows "This Raffle's Rules (Locked)" with those values;
  confirm there is no editable field for any of the five.
- **F — Settings changed while active.** Change all five defaults to very different values. Return to the raffle: its
  rules card and behaviour are unchanged.
- **G — Operational calculations.** Add paid tickets (check the bonus preview and awarded free tickets), free tickets;
  confirm Running Pot uses the original Starting Pot / Ticket Cost.
- **H — Prize.** Confirm Prize Pot / House Take use the original Prize % / Starting Pot.
- **I — Reload.** `/xlreload`; the raffle returns (if selected) with its original values, not the edited defaults.
- **J — Next raffle.** Create another raffle; it uses the defaults changed in F. The earlier raffle still shows its own.
- **K — Multi-venue.** With two venues having different defaults, switch back and forth; defaults and each venue's
  raffle values stay separate.
