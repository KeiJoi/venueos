# Brackets first-time Organizer authentication / HTTP 429 (0.3.8 maintenance, WP2 Subtask B)

Status: **IMPLEMENTED — awaiting live QA.** One deployment step by the backend owner is required for the main fix to take effect (§6). Nothing was committed, pushed, deployed, or version-bumped in any repository.

Backend detail lives in `C:\FFXIVplugs\tournamentcontrol\docs\auth-rate-limit-investigation.md`; this report summarizes it.

## 1. Symptom

New test Venue Profile → Brackets settings → Create Organizer (reported success) → Authenticate → **HTTP 429**. Later, back in that venue: "key/session expired"; Authenticate then worked.

## 2. Architecture / request trace (from source)

Files: `VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs`, `TournamentControlService.cs`, `VenueOS.Plugin/TournamentControlOperatorPanel.cs`; backend `apps/server/src/app.ts`, `auth/auth-service.ts`.

| Operator action | Requests **before** the fix | Requests **after** |
| --- | --- | --- |
| Create Organizer (once, after confirm) | `POST /api/controller/organizers` — **1** | same — **1** |
| Authenticate, no session | `POST /api/controller/sessions` + `GET /api/controller/tournaments` — **2** | same — **2** |
| Authenticate right after Create Organizer | 2 (a second login into the throttled endpoint) | **1** (`GET` tournaments only; the login is skipped) |
| Authenticate while the server has said "wait" | another `POST` each click | **0** until the cooldown ends |
| Rendering / re-drawing any Brackets UI | 0 | 0 (tested with 200 redraws) |

### Does Create Organizer already authenticate? — **Yes.**
The backend's `POST /api/controller/organizers` returns `201 { accessToken, expiresAt (8 h), organizer }`, the same session a login returns. VenueOS already saved it (so the badge flipped to "Organizer session active"), but showed no message, did not reconcile the tournament browser (it kept saying "Authenticate to load tournaments."), and so the natural next click, Authenticate, sent a redundant login into the rate-limited endpoint.

### Duplicate requests?
None found at source level: one handler per button; `ConfirmDialog` invokes its callback once; `HttpClient` is a plain client (no retry handler, no polling, nothing authenticates on a timer or from `Draw()`); only the two panel buttons call `AuthenticateAsync`. **One real gap:** the buttons run `service.AuthenticateAsync().GetAwaiter().GetResult()` on the UI thread with no in-flight guard, so clicks queued while the UI is blocked (Argon2 hashing on the server takes noticeable time) could each send a request. That is now refused inside the service (`operation_in_progress`) — but I could not observe whether staff actually double-clicked.

## 3. Exact source of the 429

`tournamentcontrol/apps/server/src/app.ts`: `isLoginRateLimited(..., controllerLoginAttempts, 10)` on **both** `/organizers` and `/sessions`. Limit 10 **failures**, fixed 10-minute window from the first failure, keyed by `request.ip`, one shared counter, cleared by a success. No other 429 exists in the server.

## 4. Root cause

1. **`request.ip` was the proxy, not the caller.** No `trust proxy` was set, and behind Render's load balancer every request has the same `request.ip` ([Render community](https://community.render.com/t/accessing-client-ips-in-a-node-express-app/36282), [Express docs](https://expressjs.com/en/guide/behind-proxies/)). The "10 failures per IP" limit was one global bucket for every venue, organizer, staff member and internet scanner.
2. **Key-strength rejections were counted as guesses.** At Create Organizer a too-weak key (after the correct Server Access Password) counted toward the same budget — easy to burn in a training class.
3. **The 429 said nothing useful** (no `Retry-After`), VenueOS reported it as the generic "Tournament backend returned 429.", and a click could retry immediately.

A single caller's *create → login* cannot trip this limiter by itself (a success clears it; it takes ≥10 failures in between). So the production trigger was other traffic on the shared bucket, most plausibly other staff/attempts during training. **That exact event was not observed** (no server logs), so treat it as the best source-consistent explanation, not a proof.

## 5. Exact repair

**Backend (uncommitted, tournamentcontrol):** `Retry-After` header + `retryAfterSeconds` on every 429; a `KeyPolicyError` (strength policy only) is no longer counted — duplicate key and wrong password **still are**; login counters are per-app; new validated `TRUST_PROXY_HOPS` setting (default `0`). Limit, window, shared counter, "success clears", "blocked even with correct credentials" are unchanged and pinned by tests.

**VenueOS:**
- `TournamentControlClient`: HTTP 429 → `rate_limited` with `RetryAfter` (header seconds or HTTP-date, else body `retryAfterSeconds`, else 60 s; clamped 1 s–15 min); 401 at **login/create** is now `invalid_credentials` (it used to be mislabelled "Organizer session expired."); backend `INVALID_USER_KEY` → `invalid_key` with the server's reason, worded "Organizer Key"; 502/503/504 → `backend_unavailable`; no-token vs lapsed-token messages separated.
- `TournamentControlService`:
  - **Create Organizer** stores the session, clears any old tournament/browser state, and reconciles the browser to "Loaded 0 tournament(s)." locally — **no extra request** — and shows "you do not need to press Authenticate now."
  - **Authenticate** when this run already holds a valid session for the same server + Organizer Key (in-memory hash only, never persisted/logged): sends one confirming `GET` instead of a throttled login; if the server rejects the session it falls through to exactly one real login. Editing the key invalidates that shortcut (tested).
  - **429 handling:** a per-**backend-origin** "do not ask before" instant; while active, Create/Authenticate refuse **without any request** and show the countdown. Never auto-retries. After the cooldown, one deliberate retry works (tested).
  - **In-flight guard**, and a **stale-context guard**: a sign-in response that lands after a venue switch is discarded (cancelled, not reported) and cannot write into the new venue.
  - `AuthStatus` (Success/Info/Warning/Error, title + message) drives a banner in Settings → Brackets and on the not-authenticated operational screen; diagnostics use the existing `games.tournament:` convention with secrets excluded.
- `TournamentControlOperatorPanel`: banner + pause countdown; Create Organizer confirmation text now says the server signs the venue in.

## 6. Owner action required (backend)

Set **`TRUST_PROXY_HOPS=1`** in the Render dashboard and redeploy. Without it the shared-bucket keying is unchanged (the other fixes still apply). Verify from two networks (backend report, "Live QA"). I did not edit `render.yaml` or any environment value.

*Release note (0.3.8):* the owner has since set `TRUST_PROXY_HOPS=1` in the Render environment and deployed the updated backend. Live QA (§10) is still pending.

## 7. Key / token terminology (audited)

- **Organizer Key** — long-lived credential. It does not expire on the backend (only a master admin can revoke it).
- **Organizer session** — the 8-hour bearer token issued by Create Organizer or Authenticate. This is what "expired" in training.
- VenueOS now says "Organizer session expired … your Organizer Key is still saved and has not expired", shows it when returning to a venue whose saved session lapsed, and no longer calls a rejected *login* an expired session.

## 8. Venue isolation (audited)

Credentials, tokens, session expiry, tournaments and realtime state are per-venue (`GetModuleConfig`/`SaveModuleConfig`, reset in `Load()`); nothing of Venue A reaches B (tested A → B → A). Backend Organizer identity and Venue Profile identity are **not** mapped: two venues may hold different keys or the same key. The one deliberately shared item is the throttle pause, keyed by **server origin** (it mirrors the server's per-caller limit; a venue on another server is unaffected; it holds no credential). Residual, not changed (out of scope): the other Brackets methods use the same "continue after await" pattern without the stale-context guard added here to sign-in.

## 9. Tests

VenueOS: **1231 / 1231** (`dotnet test VenueOS.sln -c Debug`: Core 69, Venues 23, Services 1139; baseline was 1206), 0 failed, 0 skipped. Debug build 0 warnings / 0 errors; Release build 0 warnings / 0 errors. New: `tests/VenueOS.Services.Tests/TournamentAuthFlowTests.cs` (25 cases incl. theory rows; service + client through a counting fake `HttpMessageHandler`): request count per click (Create = 1, Authenticate = 2, Authenticate-after-Create = 1), state after Create (signed in, persisted across a rebuilt service), edited key → real login, rejected session → one login, 200 redraws = 0 requests, in-flight refusal, invalid credentials ≠ expired session, key-policy message, unavailable server, expired-session wording, returning to a lapsed venue, 429 + `Retry-After` (header/date/body/default/clamps), no request during cooldown, recovery after cooldown, 429 on Create pauses Authenticate, per-venue isolation A→B→A, throttle per origin, stale response after venue switch, no secrets in diagnostics/status.

Backend: `apps/server` **85 / 85** (69 + 16); root `npm test`, `typecheck`, `lint`, `build` exit 0.

## 10. Live QA (new temporary Venue Profile)

A. Set Server URL/password and a strong Organizer Key → Create Organizer → confirm banner "Organizer created… signed in" and badge "Organizer session active" → press Authenticate **once** → "Already signed in". No 429.
B. Switch to a known venue, then back → only the test venue's credentials; still signed in (or the "session expired" banner if 8 h passed) → Authenticate → OK.
C. (Only if safe; after `TRUST_PROXY_HOPS=1`) from a second network, ~10 wrong-key attempts → "Temporarily rate limited… try again in N s"; further clicks send nothing until the countdown ends.
D. Reload the plugin, return to the test venue → organizer key/session behave as saved (session still valid → signed in; lapsed → banner).

## 11. Files changed

VenueOS: `M src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs`, `M …/TournamentControlService.cs`, `M src/VenueOS.Plugin/TournamentControlOperatorPanel.cs`, `?? tests/VenueOS.Services.Tests/TournamentAuthFlowTests.cs`, `?? docs/BRACKETS_FIRST_AUTH_INVESTIGATION.md`, `?? docs/BINGO_CALLED_BALL_WEB_INVESTIGATION.md`; ledger `docs/POST_0.3.7_TRAINING_AUDIT.md`.
Backend (tournamentcontrol): `apps/server/src/{app,config,index}.ts`, `auth/auth-service.ts`, `docs/{render-setup,security}.md` modified; `apps/server/test/auth-rate-limit.test.ts`, `docs/auth-rate-limit-investigation.md` new.

## 12. Remaining uncertainty

The production 429 trigger was not observed; the fixes remove every source-provable cause. The standalone TournamentControl Dalamud client (`apps/dalamud`) was not changed and does not read `Retry-After`. Live behaviour of the ImGui banner is unverified until QA.
