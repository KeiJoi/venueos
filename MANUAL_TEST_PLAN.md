# VenueOS manual in-game test plan

Run these checks on a non-production venue first. Record result, game/client version, and any issue in `OPERATOR_FEEDBACK.md`.

1. Start VenueOS, select each venue, then reload Dalamud. Confirm active venue, themes, and each venue's Greeter/VIP/backend credentials remain isolated.
2. Attendance: open a session, have a guest enter/leave scope, confirm arrival/departure and guest count; close/reopen the session.
3. Greeter: configure all five DJ presets, switch each while guests arrive, confirm the active preset/status is correct and a guest is not greeted twice.
4. VIP: use a matching VIP entry and verify order: VIP tell, Greeter tell, then shout/yell. Toggle the VIP off and confirm no repeat public callout.
5. Announcements: run a multi-line preset, inspect pacing/channel/order, cancel between lines, change venues, and confirm old queued lines do not send.
6. Party Finder: only after standalone validation, verify create, refresh warning, listing-end behavior, abort, and no unintended clicks after an API/game update.
7. Raffle/Trivia/Tournament/Bingo: configure distinct backend credentials in two venues; authenticate/load a state in each; switch while a request/poll is active; confirm no old credential is reused.
8. Bingo: host-sync a room, refresh card/player state, call a number, inspect browser link/display values. Do not test automated payouts: they are deliberately absent.
9. Force an unavailable backend and malformed module payload where practical. Confirm the rest of VenueOS remains usable, a safe notification appears, and diagnostics do not disclose secrets.
10. Before live use, exercise all intended workflows once on the exact Dalamud/game build and record results below.

| Date | Venue | Module/workflow | Result | Notes |
|---|---|---|---|---|
| | | | | |
