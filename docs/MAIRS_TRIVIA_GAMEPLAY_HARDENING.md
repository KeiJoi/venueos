# Mair's Trivia — Gameplay Hardening Pass (No-Answer Scoring, Answer Change, Series Reveal)

**Status: LIVE QA PASSED.** Released as part of VenueOS 0.3.7.

Companion document to `C:\FFXIVplugs\mairstrivia\docs\GAMEPLAY-HARDENING.md`, which contains the full technical
detail for all three fixes. This document records what was investigated and (not) changed on the VenueOS side,
and why.

## 0. Live QA results

The user verified all three backend-authoritative fixes live, end to end, through VenueOS's Mair's Trivia operator
console:

1. **No-answer scoring** — a player who lets a question's timer expire without answering now receives the
   configured incorrect-answer penalty; they can no longer dodge scoring by not answering.
2. **Answer changing** — the first answer submits immediately; **Keep Current** preserves the existing answer;
   changing from correct to wrong scores wrong; changing from wrong to correct scores correct; repeated changes
   throughout the question's open window all work correctly; the final authoritative answer at question close is
   what gets scored.
3. **Series Answer Reveal** — during a Series, the Answer Reveal screen now correctly displays the **current
   game's** standings rather than the cumulative Series standings.

Gameplay-hardening acceptance for Mair's Trivia is complete. As documented below, none of these fixes required a
VenueOS source change — VenueOS's own display/consumption of the corrected, authoritative backend data was already
correct, and live QA confirms it.

## 1. Executive summary

Three production gameplay defects were fixed in the Mair's Trivia backend/web repository (`C:\FFXIVplugs\mairstrivia`):

1. A player who let a question's timer expire without answering was never scored at all, letting them dodge the
   configured incorrect-answer penalty — a real, observed exploit.
2. The player web page locked every answer choice the instant a first answer was submitted, so a player could
   never correct a misclick or change their mind before time expired.
3. During a multi-game Series, the per-question Answer Reveal screen on the player web page showed the
   cumulative Series standings instead of the current game's standings.

**All three fixes are entirely contained in the backend/web repository.** After a full architectural trace of
VenueOS's Mair's Trivia module (`src/VenueOS.Modules.Operations/Trivia/`, `src/VenueOS.Plugin/MairsTriviaOperatorPanel.cs`),
none of the three defects originated in, or required a change to, VenueOS. This document exists to record that
finding precisely, rather than leave it undocumented that "nothing changed here."

## 2. Why VenueOS needed no code changes

- **No-answer scoring (issue 1):** this is purely server-authoritative scoring logic (`settleAnswers()` in the
  backend's `service.ts`). VenueOS never scores anything itself — it only displays `TriviaLeaderboardEntry`/
  `TriviaPlayerScore.CorrectCount`/`IncorrectCount`, which are plain aggregate counters. A no-answer now
  increments `IncorrectCount` and adjusts `Score` on the backend exactly as a wrong answer would; VenueOS's
  existing `TriviaLeaderboardEntry`/`TriviaPlayerScore`/`TriviaSeriesParticipant` records (`MairsTriviaClient.cs`)
  already carry exactly the fields needed to reflect this with zero shape changes.
- **Answer change with confirmation (issue 2):** this is exclusively a player-facing browser feature. VenueOS's
  Trivia module is the **host/operator console** — it never calls the backend's `/v1/player/answer` endpoint
  (confirmed by a repository-wide search: `MairsTriviaClient.cs` has no reference to `player/answer` anywhere).
  The confirmation dialog, click-handling, and answer-change UX all live in the backend repo's player web page
  (`server/public/app.js`/`client-logic.js`), which VenueOS does not host, embed, or otherwise touch.
- **Series Answer Reveal standings (issue 3):** VenueOS's own `MairsTriviaOperatorPanel.DrawGameConsole` reveal
  path (the `LastQuestionResult` info banner, `MairsTriviaOperatorPanel.cs`) was traced in detail and found to
  already be correct: it renders `service.CurrentGame.Leaderboard` (game-scoped, `TriviaHostGameState.Leaderboard`)
  for the "Game standings" section directly below the reveal banner, and a structurally separate "Series
  standings" card reads `service.CurrentSeries.Standings` (`TriviaSeriesState.Standings`) — these are two
  independent nullable fields on two independent DTOs, never merged or chosen between ambiguously. The reported
  bug was reproduced and fixed entirely inside the backend's player web page (`server/public/app.js`'s
  `resultSection()`), which had a single conditional (`state.seriesId ? seriesStandings : leaderboard`) that
  substituted the wrong payload field — a defect that could not exist in VenueOS's structurally separate
  rendering, and did not.

## 3. DTO/contract review

Both wire-protocol DTO shapes were checked against the backend's actual changes:

- `POST /v1/player/answer`'s response shape changed from `{ accepted, locked }` to `{ accepted, changed }` on the
  backend. **VenueOS never calls this endpoint**, so this has zero effect on `MairsTriviaClient.cs`.
- Every other DTO VenueOS deserializes — `TriviaHostGameState`, `TriviaQuestionResult`/`TriviaCloseResult`,
  `TriviaGameCompleteResult`, `TriviaSeriesCompleteResult`, `TriviaSeriesState`, `TriviaLeaderboardEntry`,
  `TriviaPlayerScore`, `TriviaSeriesParticipant` — kept their existing field shapes. No VenueOS-side DTO edit was
  required, and none was made.
- `TriviaScoringRequest.AllowAnswerChange` (`MairsTriviaClient.cs:33`) is sent by VenueOS on game creation but was
  never surfaced as an operator-configurable setting anywhere in `MairsTriviaOperatorPanel.cs` — it always went
  out as its default (`false`). The backend has stopped reading this field to gate answer changes (answer changes
  are now always permitted while a question is open, an unconditional server capability rather than an
  opt-in one), but keeps the field in its own wire type for backward compatibility with already-persisted data
  and existing request shapes. VenueOS's `TriviaScoringRequest` is therefore left exactly as-is: the field still
  round-trips correctly, it is simply now inert on the backend. No VenueOS UI ever exposed it, so no operator-
  facing behavior changes.

## 4. Testing

No VenueOS test changes were needed or made — none of the three fixes touch any VenueOS-side logic. The existing
VenueOS test suite (`tests/VenueOS.Services.Tests/MairsTriviaClientTests.cs`, `MairsTriviaServiceTests.cs`, and
every other test project) was re-run in full as part of validating this pass and passes unchanged.

## 5. Test/build results (VenueOS)

```
dotnet test VenueOS.sln -c Debug
  VenueOS.Core.Tests.dll     : 4 passed, 0 failed
  VenueOS.Venues.Tests.dll   : 23 passed, 0 failed
  VenueOS.Services.Tests.dll : 1065 passed, 0 failed
  TOTAL: 1092 passed, 0 failed   (matches the stated pre-existing baseline exactly, as expected since no
                                   VenueOS source file changed)

dotnet build VenueOS.sln -c Debug     -> Build succeeded. 0 Warning(s), 0 Error(s)
dotnet build VenueOS.sln -c Release   -> Build succeeded. 0 Warning(s), 0 Error(s)
```

## 6. Live QA — PASSED

The scenarios below were exercised live against the backend live-QA scenarios described in
`C:\FFXIVplugs\mairstrivia\docs\GAMEPLAY-HARDENING.md` §21 (no-answer penalty, answer change, Series reveal), and
confirmed from the VenueOS operator console:

- Game standings shown in `MairsTriviaOperatorPanel`'s live game console correctly reflect no-answer penalties as
  they're applied (via its existing 2-second HTTP poll — no VenueOS change was needed for this to work).
- Series standings shown in the separate Series card continue to update correctly as games complete, exactly as
  before this pass.
- During a Series, the Answer Reveal banner correctly shows current-game standings rather than cumulative Series
  standings (see §0).

See §0 for the full user-confirmed scenario list.

## 7. Files changed (VenueOS repository)

**None**, other than this document. `src/VenueOS.Modules.Operations/Trivia/`, `src/VenueOS.Plugin/MairsTriviaOperatorPanel.cs`,
`src/VenueOS.Plugin/TriviaVenueSwitchGuard.cs`, and both existing Trivia test files were investigated but not
modified, because investigation established none of the three defects, nor their fixes, touch VenueOS.

## 8. Git status (VenueOS repository)

At the time this pass was authored, unaffected by it: the pre-existing, unrelated Party Finder hardening work and
the Module Launcher/Window Management pass were both left exactly as found. This document, along with the rest of
the accumulated maintenance batch, was released together as part of VenueOS 0.3.7 — see `RELEASE.md`.
