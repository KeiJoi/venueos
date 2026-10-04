# VenueOS Changelog

A short summary of each VenueOS release. Detailed notes are in `RELEASE.md`, and full investigation reports are under `docs/`. Dates are release dates (UTC). Entries before 0.3.10 were reconstructed from the repository's tags, GitHub Releases, `RELEASE.md` and release commits.

## 0.3.10 — 2026-10-04

- **Party Finder:** **End Party Finder** no longer turns off your **Auto Refresh on native 5 minute warning** setting. Whether the setting was on or off, it stays that way for the next recruitment.
- **Party Finder:** Auto Refresh does nothing when VenueOS has no active listing. It never refreshes while you are not recruiting.
- **Party Finder:** **End Party Finder** is only available while a listing is active, and its tooltip and status text no longer mention Auto Refresh being disabled.
- **Changelog:** this changelog now ships with VenueOS and can be read offline from **Settings → Changelog**.

## 0.3.9 — 2026-09-29

- **Mair's Editor:** a **Browse…** button opens a VenueOS-styled file picker for importing `.fftrivia` question sets. You no longer need to type the file's full path.
- **ShoutRunner:** the optional Shout Line 2 now waits at least 2 seconds after Line 1 is confirmed sent. Before this, FFXIV could drop it.

## 0.3.8 — 2026-09-21

- **Party Finder:** the first refresh after you start recruiting is more reliable. **Apply Changes** must be usable before it is clicked, and success is confirmed by the editor closing.
- **Bingo (web):** clicking a called ball marks every matching number on all cards and never un-marks one.
- **Brackets:** first-time Organizer sign-in no longer logs in twice in a row. If the server rate-limits you (HTTP 429), you see a clear "try again in N s" message.
- **ShoutRunner:** **Add Aetheryte** uses a searchable picker built from the game's Aetheryte data. There is also a new optional second shout line.
- **Attendance:** each Nearby Guests entry shows on one line as `Name — Home World`.
- **Raffle:** a running raffle's rules are locked on its live screen. Settings controls the defaults for future raffles.
- **Block Letters:** palette blocks are inserted at the real caret or replace the current selection, even after you have typed.
- **System Menu:** VenueOS no longer opens the FFXIV System Menu by sending a synthetic Escape on reload, venue switch or module disable.

## 0.3.7 — 2026-09-16

- **Party Finder:** refresh handling hardened. Buttons are disabled while a refresh is running, manual and automatic refreshes can no longer collide, and refreshes behave better while you are zoning or logged out.
- **Module Launcher (new):** a compact, icon-only hotbar for opening, focusing and restoring module windows. You can change its order, visibility and layout in **Settings → Launcher**, or toggle it with `/venueos launcher`.
- **Windows:** detached module windows can be collapsed, minimized and restored, and the main tablet can be collapsed and expanded.
- **Mair's Trivia:** the backend now applies scoring for unanswered questions and lets players change answers. Series Answer Reveal shows standings for the current game.

## 0.3.6 — 2026-09-12

- **Shouts:** the Live screen wraps hotbar slots at five per row.

## 0.3.5 — 2026-09-12

- **Shouts:** DJ Shouts was renamed Shouts and now has 15 slots. The Live screen shows only configured slots, and your existing DJ Shouts data migrates automatically.

## 0.3.4 — 2026-09-12

- **DJ Shouts (new):** reusable announcement presets with five slots, a Yell/Shout channel for each line, paced manual sending, and a "Last DJ Shout" timer.

## 0.3.3 — 2026-09-12

- **Giveaways:** a new **Announce Winner** button with a `<name>` template that handles ties grammatically and checks the chat length limit.

## 0.3.2 — 2026-09-12

- **Bingo:** automated payout now works end to end in a live test. This includes fixes for main-thread dispatch, gil entry, and the Trade Ready/Confirm step.

## 0.3.1 — 2026-09-10

- **Mair's Trivia:** fixed an intermittent "expired token" error.
- **Macro:** dragging a tile from the Live screen onto a faux hotbar now works.
- **Raffle:** publishing a raffle also creates short viewer and player links.
- No VenueOS windows appear while you are logged out.
- Dialogs and editors now share consistent VenueOS window styling.

## 0.3.0 — 2026-09-09

- Every module is now production-ready and enabled by default.
- Raffle and Brackets (formerly TournamentControl) were rebuilt.
- New modules: **Block Letters**, **Giveaways** and **Macro**.
- The User Manual now covers every module.

## 0.2.2 — 2026-09-07

- **User Manual:** long lines in the built-in reader now wrap.

## 0.2.1 — 2026-09-07

- **User Manual:** fixed "User Manual could not be loaded" on installed copies of the plugin.

## 0.2.0 — 2026-09-07

- **Bingo** is release-ready and enabled by default.
- New built-in offline **User Manual**.
- **ShoutRunner:** you can resume a run that was interrupted by a crash, and same-Data-Center World Visits are more reliable.
- **Attendance:** Venue Area Type moved to the Live screen.
- The About panel shows the real version number.

## 0.1.0 — 2026-09-06

- First public release. Working modules: ShoutRunner, Attendance, Greeter, VIP, Party Finder, Mair's Trivia and Mair's Editor.
- Bingo, Raffle and TournamentControl were included but disabled while still under development.
