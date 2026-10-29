# VIP Only Mode — Implementation & QA Report (VenueOS 0.3.11)

**Status:** CODE PASS · AUTOMATED PASS (1451 / 1451) · **LIVE QA PENDING operator acceptance**

**Product decision (final):** *Option A — track everyone, greet only VIPs.* VIP Only Mode changes greeting eligibility and dispatch only. Guest detection, arrival/departure records, visit counts, statistics, persistence, history and the guest list are unchanged.

## 1. What the operator sees

- **Attendance → Live → Session card → Greeting Mode → `VIP Only Mode`** checkbox, directly under *Venue Area Type* and above **Start New Opening** / **Resume Latest**. It uses the themed `UiKit.Toggle` checkbox (no stock dialog). It is unchecked by default and has this tooltip: *"Track all guests normally, but only send greeting messages to guests saved in this venue's VIP list. Standard Greeter messages are bypassed."* A line of help text under it describes the selected mode.
- While an opening is active the checkbox is replaced by read-only status: **Greeting Mode:** *Normal (Greeter + VIP)* or a **VIP Only Mode** badge. Hovering it explains that the mode is locked until Pause/Close.
- **Every start path uses the checkbox:** Start New Opening, Live → Resume Latest, and History → Resume. History → Resume has a tooltip saying which mode it will use.
- **Visitors tab:** in a VIP Only opening, **Greet** (button and context menu) is shown only for enabled VIPs of the current venue. **Mark Greeted** is unchanged.

## 2. Architecture trace (as found in 0.3.10)

| # | Path | Where | Finding |
|---|---|---|---|
| 1 | Attendance startup | `AttendanceService.StartSession` ← `AttendanceOperatorPanel.DrawLive` | Area mode is chosen per opening in UI working state. This was the pattern to copy. |
| 2 | Shutdown | `PauseSession`, `CloseSession`, `DetachSession` (venue switch via `AttendanceModule.OnVenueChangedAsync`) | No end-of-session event existed. Closing never touched Greeter's queue. |
| 3–4 | Guest detection / presence | `PresenceService.Tick` → `Arrived`/`Departed` → `AttendanceService.Arrive`/`Depart` | Shared, continuous scan. Not touched. |
| 5 | Attendance recording | `IVenueDatabase.MarkPresent`/`MarkAbsent`/`RecordSample` | Not touched. |
| 6 | Greeter eligibility | `AttendanceService.AutomaticGreetingEligible` (once per guest per opening, never for the opening-start seed) → `Plugin.cs` handler (local-player exclusion) → `GreetingCoordinator.TryGreet` | **Integration boundary.** `TryGreet` is the only entry point for both automatic and manual greetings. |
| 7 | Greeter message generation | `GreeterService.QueueGreeting` → `Tick` (preset lines, delay, presence re-check) | Reached only from `TryGreet` / `HandOffVipToGreeter`. |
| 8 | VIP lookup | `VipOrchestrationService.TryFindEnabledVip` (`GuestIdentity.Key` = normalized name@world) | Per-venue roster loaded by `VipModule.OnVenueChangedAsync`. Reused as-is. |
| 9 | VIP greeting generation | `TryGreet` VIP branch: custom tell → (confirmed) Greeter handoff → public announcement on `GreetingReadyToFinalize` | In normal mode a VIP greeting always includes the Greeter preset. |
| 10–11 | Queueing / dispatch | `ChatCommandService.Enqueue` / `TickAsync` with per-command `OnDispatched` confirmation and cancellation token | Cancelled commands are skipped and reported as failed. Reused. |
| 12 | Deduplication | `AutomaticGreetingEligible` once-per-opening set, `isGreeted` (Attendance), Greeter `queued` set, coordinator `awaitingVipHandoff` | All reused. VIP Only adds its own in-flight set. |
| 13 | Session lifecycle | As 1–2 | Added `SessionEnded`. |
| 14 | Venue switch | `ModuleHost.NotifyVenueChangedAsync` → Attendance `DetachSession`, Greeter `ResetForVenue`, VIP `coordinator.ResetForVenue` + roster reload | VIP Only state is cleared by both `DetachSession` and `ResetForVenue`. |
| 15 | Plugin reload | Attendance never auto-resumes an opening (`DetachSession` on load) | No restoration added. Mode is not persisted, so a reload can't restore it silently. |

## 3. Design

**State.** `AttendanceService.VipOnlyMode` is set by `StartSession(..., vipOnlyMode)` / `ResumeSession(id, vipOnlyMode)`, before `SessionOpened` fires. It is cleared by Pause, Close (current) and `DetachSession`. It is **never written to the database, venue profile, Greeter config or VIP config**, so it can't become venue-global or leak to another operator. Unlike Venue Area Type it is not stored on the opening, because it describes this operator's role and not the opening. A resume therefore uses the checkbox as it is at resume time, and the checkbox defaults to off.

**Gate.** `GreetingCoordinator.TryGreet` → after the existing "already greeted" check → `if (IsVipOnlyMode) return TryGreetVipOnly(...)`. This is the earliest boundary with the information needed. Ordinary guests return `false` before Greeter is called, so nothing is queued and then cancelled.

**VIP Only greeting.** It uses the same `TryFindEnabledVip` record and the same tell format (`/tell Name@World`, `<name>` substitution) and channel selection as normal mode. Order: custom tell → (confirmed) public announcement. The guest is marked greeted via `AttendanceService.MarkGreeted(guest, true)` (Attendance stays the only greeted-state authority) when the tell is confirmed sent. For a public-only VIP it is marked when the announcement is confirmed sent. A failed tell aborts and nothing is marked greeted, the same as normal mode. A VIP with no messages is skipped. The Greeter preset sequence is bypassed, so a VIP gets no Greeter message from this client. Greeter's Auto Greet toggle still gates only ordinary guests (unchanged rule, so VIPs are greeted with it off). Greeter's active preset and its Enabled flag are not used.

**Lifecycle (`GreetingCoordinator.AttachSession(AttendanceService)`, wired in `Plugin.cs`).**
- `SessionEnded` → cancels VIP Only work: a dedicated `CancellationTokenSource` (queued commands are skipped by `ChatCommandService`), the in-flight set is cleared, and a generation counter is bumped so a late callback from a cancelled attempt can't clear a newer attempt's in-flight marker or mark anyone greeted.
- `SessionOpened` → the same cancellation. If the new opening is VIP Only, it also drops normal-mode work left from the previous opening (`GreeterService.CancelAllPending`, the coordinator's normal-mode token/`awaitingVipHandoff`/`awaitingPublic`). This stops a queued ordinary greeting from being sent inside the VIP Only opening.
- Normal openings starting or ending leave normal-mode work exactly as in 0.3.10 (a test pins this).
- Venue switch: `ResetForVenue` also cancels VIP Only work, and `DetachSession` clears the mode. The UI checkbox resets when the venue changes, and after Start/Resume/Close.

**Module disable.** `VipModule.IsEnabled` now forwards to `GreetingCoordinator.VipModuleEnabled` (default `true`, same as before). With VIP disabled, a VIP Only opening sends nothing, and disabling cancels any in-flight VIP Only attempt. This guard also prevents the use of a roster left over from another venue, because a disabled VIP module skips the per-venue reload. Normal-mode routing ignores the flag, as it did before. The Attendance and Greeter module toggles keep their existing lifecycle semantics.

**Multi-operator.** All state is per-plugin instance. There is no networking and no shared configuration. Separate clients don't share greeting history or dispatch state, so **VIP Only Mode does not prevent cross-client duplicates**. A normal-mode client still sends its own VIP tell, Greeter message and announcement. Operators should agree who greets VIPs, for example by keeping VIP records disabled or empty on the normal client.

## 4. Files changed

- `src/VenueOS.Modules.Operations/Operations.cs`: `AttendanceService.VipOnlyMode`, `SessionEnded`, the `vipOnlyMode` parameters on Start/Resume, the `MarkGreeted(guest, greeted)` overload; `GreeterService.CancelAllPending`; `GreetingCoordinator.AttachSession`/`CanGreet`/`IsVipOnlyMode`/`VipModuleEnabled`/`TryGreetVipOnly` and helpers; `VipModule.IsEnabled` forwarding.
- `src/VenueOS.Plugin/NativeOperationsPanels.cs`: the checkbox, read-only status, resume paths, Visitors Greet gating.
- `src/VenueOS.Plugin/Plugin.cs`: `coordinator.AttachSession(attendanceService)`.
- `tests/VenueOS.Services.Tests/VipOnlyModeTests.cs` (new, 44 tests); `ChangelogTests.cs` (version pin 0.3.10 → 0.3.11, and 0.3.10 must stay in the history).
- Version: `VenueOS.Plugin.csproj` and `VenueOS.json` → 0.3.11 / 0.3.11.0. Dalamud API level unchanged.
- Docs: `docs/USER_MANUAL.md`, `CHANGELOG.md`, `RELEASE.md`, `RECONSTRUCTION_ATTENDANCE_GREETER_VIP.md`, `docs/POST_0.3.7_TRAINING_AUDIT.md` (item 12), this report.

## 5. Automated tests (`VipOnlyModeTests`, 44)

They use the production wiring from `Plugin.cs` (real `AttendanceService` + in-memory `SqliteVenueDatabase` + `GreeterService` + `VipOrchestrationService` + `GreetingCoordinator` + `ChatCommandService`), plus a `ModuleHost`/`VenueProfileService` venue-switch test.

| Requirement | Tests |
|---|---|
| Default OFF / state reaches session logic | `Vip_only_mode_defaults_off…`, `Selected_mode_reaches…`, `Mode_is_applied_before_session_opened_listeners_run` |
| Normal mode unchanged | `Normal_mode_still_greets_ordinary_guests…`, `Normal_mode_vip_flow_is_unchanged…`, `Normal_mode_ignores_the_vip_module_flag…`, `Ending_a_normal_opening_leaves_its_pending_greeter_work…`, `Manual_greet_is_offered_for_everyone_in_normal_mode` |
| Ordinary guests recorded, not greeted, never queued | `Vip_only_mode_records_ordinary_guests_but_sends_and_queues_nothing`, `…records_departures_and_visits_exactly_like_normal_mode`, `Manual_greet_is_not_offered_or_accepted…`, `Mark_greeted_still_works…` |
| VIPs recorded and greeted with VIP messages only | `…sends_the_vip_tell_then_public_and_bypasses_the_greeter_preset`, `…uses_shout…`, `…private_only_vip`, `…public_only_vip…`, `…no_messages_is_skipped…`, `…even_when_greeter_auto_greet_is_off`, `…does_not_depend_on_the_greeter_preset_or_greeter_enabled` |
| Safeguards | `…tell_failure_aborts…allows_a_later_retry`, `Seeded_vip_already_present…`, `Disabled_vip_record_is_treated_as_an_ordinary_guest…` |
| Name + home world matching | `…requires_the_same_home_world`, `…uses_the_existing_normalized_identity_rules` |
| Duplicates / repeated presence | `Repeated_presence_events…`, `A_second_greet_while…in_flight…`, `A_cancelled_attempts_late_callback…`, `A_cancelled_tell_attempts_late_callback…` |
| Stop / cleanup | `Closing_a_vip_only_opening_with_a_pending_greeting…`, `Pausing_after_the_tell_confirms…`, `Pausing_a_public_only_attempt…`, `Starting_a_vip_only_opening_drops_ordinary_greetings_left_over…`, `An_unconfirmed_normal_mode_vip_tell_cannot_hand_off…` |
| Restart / resume / reload | `Restarting_in_normal_mode_after_vip_only…untouched_configuration`, `Restarting_in_vip_only_mode_after_a_normal_opening…`, `Pause_close_and_detach_all_clear_vip_only_mode`, `Session_ended_fires…`, `Resume_defaults_to_normal_mode…`, `Reload_never_restores_vip_only_mode_from_storage` |
| Module disable | `Disabling_the_vip_module_stops_vip_only_greetings_and_cancels_one_in_flight`, `Vip_module_enabled_flag_defaults_on` |
| Venue isolation / switching | `Venue_switch_clears_vip_only_mode_and_vip_eligibility_follows_the_active_venue`, `Venue_switch_cancels_a_pending_vip_only_greeting` |
| Multi-operator | `Two_independent_clients_one_normal_one_vip_only_do_not_affect_each_other` |

**Mutation testing.** Ten source mutants were applied one at a time and the file was restored byte-for-byte after each. All ten are killed by `VipOnlyModeTests`:

| Mutant | Tests failing |
|---|---|
| M1 remove the VIP Only gate in `TryGreet` (ordinary Greeter dispatch) | 22 |
| M2 hand VIP Only VIPs to Greeter (normal preset sent) | 15 |
| M3 ordinary guest falls back to `greeter.QueueGreeting` | 8 |
| M4 skip the stale Greeter purge on VIP Only open | 1 |
| M5 no cancel on `SessionEnded` | 2 |
| M6 mode not cleared on Close | 2 |
| M7 ignore the VIP module flag | 1 |
| M8 remove the generation guard on the public callback | 1 |
| M9 `CanGreet` always true | 2 |
| M10 remove the generation guard on the tell callback | 1 |

M8 and M10 survived the first pass. The two `A_cancelled_…late_callback…` tests were added to cover them.

**Full validation:** `dotnet test VenueOS.sln -c Debug` → 1451 / 1451 (Core 69, Venues 23, Services 1359), 0 skipped (0.3.10 had 1407). `dotnet build VenueOS.sln -c Debug` and `-c Release` → 0 warnings, 0 errors. The existing Attendance, Greeter and VIP tests in `OperationsTests`/`VenueProfileTests`/`AttendanceStoreTests` are unmodified and green.

## 6. Live QA checklist — PENDING operator acceptance

Nothing here has been exercised in game. Use two clients (A = normal, B = VIP host) or one client in turn.

1. Fresh install/update: Attendance → Live shows **VIP Only Mode** unchecked, and the tooltip is shown on hover.
2. Client B: check VIP Only Mode → **Start New Opening**. The Session card shows the **VIP Only Mode** badge and the checkbox is gone.
3. A non-VIP walks in: they appear in Guests Nearby and Visitors and the counts go up. No `/tell` is sent from B, the status stays **Not Greeted**, and there is no Greet button for them.
4. A saved, enabled VIP walks in: B sends their custom tell, then their public `/shout` or `/yell`. No Greeter preset tell is sent, and the status becomes **Greeted**.
5. The same VIP leaves and returns: nothing more is sent.
6. A VIP with the same name on a different home world gets nothing.
7. Pause the opening, then check that the badge is gone and the checkbox is unchecked. Resume without checking it: ordinary guests are greeted normally again.
8. Close, switch to another venue where the VIP isn't saved, and start in VIP Only Mode: the VIP is tracked but not greeted.
9. Confirm Greeter presets/Auto Greet and the VIP list look exactly as before.
10. Settings → Modules → disable VIP during a VIP Only opening: nothing is sent. Re-enable it.
11. Reload VenueOS during a VIP Only opening: no opening is active, and Resume Latest resumes in normal mode unless VIP Only Mode is checked first.
12. Optional: with client A in normal mode at the same time, note that A still greets everyone (VIPs included). This is the documented cross-client limitation.
