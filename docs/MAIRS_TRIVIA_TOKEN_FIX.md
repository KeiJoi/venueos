# Mair's Trivia — intermittent expired-token fix

Engineering report for the 0.3.0 post-release quality pass's Mair's Trivia fix. This is the authoritative report for
this specific defect; `docs/reconstruction/audits/MAIRS_TRIVIA_EDITOR_AUDIT.md` remains the historical pre-implementation
audit and is not modified by this fix.

## Symptom

During normal use, Mair's Trivia intermittently reported an expired-token error even though automated tests passed
and the token-refresh/retry machinery appeared correct in isolation.

## Root cause

Two independent, compounding bugs in `src/VenueOS.Modules.Operations/Trivia/MairsTriviaService.cs`, both confirmed
by forensic trace of the actual auth flow (no WebSocket exists in this module's implementation — it is entirely
HTTP, polled every 2 seconds by `Tick`; the `/v1/ws` protocol scaffolding in `MairsTriviaClient.cs` is unused dead
code, confirmed by grep and by `docs/reconstruction/audits/MAIRS_TRIVIA_EDITOR_AUDIT.md`'s own table).

**1. Primary — uncoordinated refresh race.** `Load(nextVenueId)` fired a fire-and-forget `RefreshAsync()` on every
plugin/venue load whenever a stored refresh token existed ("auto-connect"). `RefreshAsync()` called the backend's
`/v1/auth/refresh` directly. Separately, the reactive 401-recovery path (`RunAsync` → `EnsureAuthenticatedAsync` →
`RecoverSessionAsync`) is single-flight-guarded via a shared `authRecovery` task specifically to prevent two
concurrent refresh attempts. `Load()`'s auto-refresh call bypassed that guard entirely — it was a second, completely
independent entry point that could call `/v1/auth/refresh` while a reactive recovery (triggered by the 2-second
background poll hitting a 401) was already in flight. The backend **rotates the refresh token on every use**
(invalidating the previous one), so whichever of the two racing responses' `Configure(...)` call landed second
silently discarded the other's token pair — producing an intermittent, timing-dependent "expired token" symptom
whose reproduction depended on whether a venue/plugin load happened to coincide with a poll discovering an expired
access token. The manual "Refresh session" button (`MairsTriviaOperatorPanel.cs`) called the same unguarded method.

**2. Secondary — disabled module misses venue switch.** `ModuleHost` skips `OnVenueChangedAsync`/`Tick` entirely for
a disabled module (`NEW_MODULE_GUIDE.md` §22). `MairsTriviaService.Load()` — the only place `Settings`/the session
token are re-synced to the active venue — is called exclusively from `MairsTriviaModule.OnVenueChangedAsync`. If the
operator disabled Mair's Trivia, switched venues (possibly more than once), and later re-enabled it without a
further switch, the module resumed using whichever venue's connection/token this process-lifetime singleton service
had last loaded — potentially a different venue's credentials than the one now active.

## Fix

1. `MairsTriviaService.RefreshAsync()` (used by both `Load()`'s auto-connect and the manual "Refresh session"
   button) now routes through the existing single-flight `EnsureAuthenticatedAsync` path instead of calling the
   backend directly. Every refresh entry point in the service now shares exactly one in-flight `authRecovery` task,
   so at most one `/v1/auth/refresh`/login call can be outstanding system-wide, and `RecoverSessionAsync`'s existing
   `IsCurrent(generation, venueId)` check still prevents a venue-switch-stale response from ever landing.
2. `MairsTriviaService.ReloadForCurrentVenue()` (new) forces a fresh `Load(profiles.Current.Id)`.
   `MairsTriviaModule.IsEnabled` gained a custom setter (matching `ShoutRunnerModule.IsEnabled`'s existing
   disable-triggers-HardStop precedent) that calls it on every disabled→enabled transition, so re-enabling always
   loads whichever venue is actually active, never a stale one.

No change to persistent-credential visibility (plain text, unmasked, per `NEW_MODULE_GUIDE.md` §9a — untouched),
no change to token storage location or lifetime, no change to the backend, and no new retry loop — genuine
credential rejections (`invalid_login`/`invalid_server_access`) were already, and remain, excluded from automatic
recovery.

## Tests

Added to `tests/VenueOS.Services.Tests/MairsTriviaServiceTests.cs`:
- `Load_time_auto_refresh_and_a_concurrently_discovered_expired_token_share_one_refresh_attempt` — proves the two
  previously-independent refresh entry points now coalesce onto exactly one backend call.
- `Re_enabling_after_a_venue_switch_while_disabled_loads_the_now_active_venue_not_the_previous_one` — proves the
  disable/switch/re-enable sequence no longer reuses stale venue credentials.

Existing coverage (`Auto_reconnect_refreshes_the_session_on_load_when_a_refresh_token_is_already_stored`,
`Concurrent_expired_requests_trigger_exactly_one_refresh_attempt`, and the login/refresh-failure tests) continues to
pass unchanged, confirming no regression to the already-correct reactive-recovery behavior.

`tests/VenueOS.Services.Tests` (Trivia subset): 33/33 passing after the fix. Full-suite re-verification happens at
the end of this pass (see `docs/UI_QUALITY_AUDIT.md`).

## Remaining limitations

- No WebSocket exists for this module, so there is no WebSocket-reconnect scenario to test — the audit's suggested
  test list included one only if the architecture had one.
- The fix cannot be proven "never intermittent again" by automated tests alone; the two added tests reproduce the
  exact race and stale-reuse conditions identified, but genuine live-session confirmation (extended normal
  operation, including deliberate disable/venue-switch/re-enable) is the user's to perform.
