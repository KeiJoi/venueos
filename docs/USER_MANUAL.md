# VenueOS User Manual

**Current version:** 0.2.0
**What it is:** VenueOS is a Dalamud plugin for Final Fantasy XIV — a single tablet-style operations console for running an in-game venue: attendance tracking, automatic guest greeting, VIP recognition, promotional shout routes, Party Finder recruitment, live host trivia, and Bingo.
**Supported environment:** Windows FFXIV with Dalamud installed (API level 15). VenueOS is unofficial, third-party, and not affiliated with Square Enix or the Dalamud/XIVLauncher project.

**Working modules covered in this manual:** ShoutRunner, Attendance, Greeter, VIP, Party Finder, Mair's Trivia, Mair's Editor, Bingo.

**Included but Under Development (disabled by default):** Raffle, TournamentControl. See [Under Development Modules](#under-development-modules) — do not expect these to work yet.

You can also read this manual inside VenueOS itself — click the **User Manual** tile on Home (just before Settings), no internet connection required.

This manual describes the current release only. It does not describe planned features, and it does not describe how any donor/standalone plugin VenueOS was built from used to behave where VenueOS now differs.

---

## Quick Start

1. Install VenueOS through Dalamud's Experimental Plugin Repository (see [Installation](#1-installation)).
2. Open it with `/venueos` (it does not open automatically on login).
3. Go to **Settings → Venue** and create or select a Venue Profile.
4. Check **Settings → Modules** — enable/disable modules as needed. Raffle and TournamentControl are off by default and marked "Under Development."
5. Configure the modules you intend to use, in their own Settings sections.
6. Launch applications from the **Home** screen.
7. Use a module's pop-out icon if you want it in its own window instead of embedded in the tablet.

---

## Module Quick Reference

| Module | Purpose | Requires external plugin/backend? | Venue-specific? | Keeps working with its window closed? |
|---|---|---|---|---|
| ShoutRunner | Automated `/shout` route across selected Data Centers/Worlds | Lifestream (for world travel) | Yes | Yes |
| Attendance | Tracks who's in the venue during an "opening" and their visit/greeted history | No | Yes | Yes |
| Greeter | Sends the venue's welcome message(s) to new guests | No | Yes | Yes |
| VIP | Private/public recognition for specific named characters on arrival | No | Yes | Reactive (fires from Attendance/Greeter events; no independent background loop) |
| Party Finder | Publishes/edits/refreshes a native FFXIV Party Finder recruitment listing | No | Yes | Yes |
| Mair's Trivia | Live host trivia backed by a remote server | Yes — remote Mair's Trivia backend | Yes (connection settings); the question library it reads from is not | Yes (polling continues; see [Lifecycle / Resume](#lifecycle--resume)) |
| Mair's Editor | Authors trivia question sets used by Mair's Trivia | No | **No — global, shared library** | N/A (no background process; it's a pure editing surface) |
| Bingo | Hosts a Bingo room: cards, calling, and payout tracking, backed by a remote server | Yes — remote Bingo backend | Yes | Yes (calling/alerts continue; see its own section) |

---

## 1. Installation

Add VenueOS through Dalamud's Experimental Plugin Repository:

1. Open the Dalamud settings (`/xlsettings`) → **Experimental** tab → **Custom Plugin Repositories**.
2. Add:
   ```
   https://raw.githubusercontent.com/KeiJoi/venueos/main/repo.json
   ```
3. Save, then open the Plugin Installer and search for **VenueOS**.
4. Install it like any other plugin.

Enabling/disabling VenueOS itself (not individual modules) is done the normal Dalamud way, from the Installed Plugins list.

**Opening VenueOS:** the command is `/venueos`. VenueOS does **not** open automatically when you log in or when the plugin loads — you (or the operator) must explicitly open it with `/venueos` each session. Running `/venueos` again while it's already open just brings it back to front.

---

## 2. VenueOS Basics

VenueOS presents itself as a tablet with one persistent toolbar and a content area underneath.

**Toolbar** (always visible): a **Home** button, the current Venue's name, a venue quick-switch dropdown, a **Settings** gear, and a **Close** button (its tooltip reads "Close VenueOS (reopen with /venueos)"). The toolbar never shows which module/screen is currently open — that's identified by the screen's own header instead.

**Home** shows an overview card and a grid of application tiles under an "Applications" heading — one tile per enabled module, plus a Settings tile. Clicking a tile opens that module.

**Settings** has five sections, reached from its own sidebar:

| Section | Subtitle |
|---|---|
| Venue | Profile & identity |
| Appearance | Themes & colors |
| Modules | Enable & configure |
| General | System behavior |
| Diagnostics | Logs & troubleshooting |

**Enabled vs. disabled modules:** a disabled module does not appear on Home at all — it's simply not there, not just grayed out. It still appears in **Settings → Modules**, where you can re-enable it. Toggling a module on/off in Settings → Modules is remembered — it's not reset by future updates unless you've never touched that toggle (see [Under Development Modules](#under-development-modules)).

**Settings vs. the module itself:** where a module has both, its **Settings → Modules → *Module*** page holds persistent configuration (server URLs, credentials, defaults), while the module's own app screen (opened from Home) holds live operational controls (start/stop, current guest list, active game state, and so on). The two are intentionally separate.

---

## 3. Venue Profiles

A **Venue Profile** represents one venue identity: a name and a theme, plus whichever module settings are stored per-venue (most of them are — see the [Persistence](#14-persistence) table).

Manage venues under **Settings → Venue**:

- **+ Add Venue** — creates a new profile and switches to it immediately.
- Each row shows the venue's name, its theme, and (if it's the active one) an **Active Venue** badge.
- **Switch to** — switches to that venue (only shown on non-active rows).
- **Rename** — inline rename with **Save**/**Cancel**.
- **Duplicate** — copies the venue (including its module configuration) under a new "... copy" name.
- **Delete** — permanently deletes the venue and all of its module configuration. Disabled if it's the only remaining venue.

You can also switch venues quickly from the toolbar's dropdown, without going into Settings.

**Switching away from an active Mair's Trivia game:** if Mair's Trivia has a game running in the venue you're leaving, switching shows a confirmation: *"Mair's Trivia has an active game ("<game name>") running in this venue. Switching venues will end this game for all connected players. This will not end the Series it may belong to."* Confirming ends that game (but never the Series it's part of, which stays resumable). No other module currently has a venue-switch warning.

**Global vs. venue-specific:** module enabled/disabled state, the Auto Pop-Out preference, and the Mair's Editor question library are global (shared across every venue). Everything else module-specific — connection settings, presets, rosters, recruitment criteria — is per-venue. See [Persistence](#14-persistence) for the full breakdown.

---

## 4. Global Settings

Found under **Settings → General**:

- **"Open modules in separate windows"** toggle (Auto Pop-Out). Off by default. When on, launching a module from Home opens (or focuses) its own detached window instead of embedding it in the tablet. This applies the same way for every venue and doesn't change when you switch venues. Settings itself is unaffected by this toggle and always opens embedded.
- An **About VenueOS** card showing a version line (the actual installed release version, e.g. "VenueOS 0.2.0") and a one-line description of what VenueOS is.

**Settings → Modules** is where you enable/disable modules and jump into each one's own settings — see [Basics](#2-venueos-basics) above and [Under Development Modules](#under-development-modules) below.

**Settings → Diagnostics** shows a filterable log of recent errors and configuration-recovery warnings (filters: **Log Level** — All Levels/Errors/Warnings, **Module**, and a **Search** box), plus **Clear** and **Copy** buttons and small stat tiles (Total Entries, Errors, Warnings, Last Update). Useful for troubleshooting — see [Troubleshooting](#15-troubleshooting).

---

## 5. ShoutRunner

**Purpose:** automatically travels to a route of destinations across selected Data Centers and Worlds, sending your configured `/shout` message at each stop.

### Message and Start/Stop

The operational screen has a **"Message sent with `/shout` at each destination"** field, a status line, and a **Start**/**Stop** button.

Status values you'll see: Stopped, Faulted, Recovering, Running, Waiting for next RUN, Stopping…

**Start** is refused (with an on-screen message) if:
- ShoutRunner is already running.
- The Shout Message is empty → *"Enter a Shout Message before starting."*
- No Data Center is selected → *"Select at least one Data Center in Settings → Modules → ShoutRunner before starting."*
- No destination is configured → *"Configure at least one destination in Settings → Modules → ShoutRunner before starting."*

**Stop** immediately cancels the current action and attempts to bring your character back to a safe, logged-in, controllable state (up to ~15 seconds). It always ends in the Stopped state either way — if that recovery couldn't be confirmed, the terminal shows *"Stopped — manual recovery may be required."* and you should check your character's state yourself.

### Settings (Settings → Modules → ShoutRunner)

**Run Timing:**
- **"Repeat automatically"** toggle (default **on**).
- **Interval hours / Interval minutes / Interval seconds** — default **1 hour, 0 minutes, 0 seconds**. Minimum enforced interval is 60 seconds; maximum is 30 days.
- **"Delay between route actions (seconds)"** — default **2**, clamped 0–120.
- An info note explains repeat scheduling: the next run is timed from when the *previous* run started, not when it finished, and never runs a "catch-up" pass if you were away.

**Data Centers:** one checkbox per Data Center — **Aether, Crystal, Dynamis, Primal**. None are selected by default. Data Center visiting order is fixed (always Aether → Crystal → Dynamis → Primal for whichever ones you select) and cannot be reordered — every World in a selected Data Center is visited automatically, you don't pick individual Worlds.

**Destinations:** default list is **Ul'dah - Steps of Nald, New Gridania, Limsa Lominsa Lower Decks**. Each row has a rename field plus **Up** / **Down** / **Remove** buttons — this list *is* reorderable. Add a new one with the text field (example placeholder "e.g. Ul'dah - Steps of Nald") and **Add**.

### How the route works

Within a World, ShoutRunner checks your character's actual current location; if it matches one of your configured destinations, it starts there (no teleport needed) and works outward — forward through the rest of the list, or backward if you happened to be at the last one. If your current location can't be matched, it continues in whichever direction it was already alternating, which is what gives the route its "ping-pong" pattern across Worlds. Every configured destination is visited exactly once per World.

Moving between Worlds in the same Data Center is a same-Data-Center transfer; moving to a different Data Center is a cross-Data-Center transfer — both go through Lifestream.

**Example:** select only **Aether**, keep the default three destinations. ShoutRunner will visit every World in Aether, and at each one walk Ul'dah → New Gridania → Limsa Lominsa Lower Decks (or the reverse, alternating per World), shouting your message at each stop, with your configured delay between actions.

### When something goes wrong

- **World congested twice in a row:** that World is skipped; the route continues to the next World.
- **Destination Data Center unreachable** (e.g. stuck at character select mid-transfer): the rest of that Data Center is skipped and the route moves to the next selected Data Center — *if* recovery to a playable state succeeds. If recovery itself fails, the whole run stops (Faulted).
- ShoutRunner does not check whether Lifestream is installed before letting you press Start — if Lifestream isn't available, you'll see a generic failure/fault message rather than a dedicated "Lifestream not found" warning. Lifestream is required for any world travel to work.

### Terminal

A running log organized **RUN → Data Center → World → Destination**, each line timestamped, color-coded by outcome (green = success, red = failure, yellow = warning, accent = in progress). **Copy Terminal** copies the full history as plain text to your clipboard; a **Jump to latest** button appears if you've scrolled up. The terminal is cleared on venue switch and does not survive a plugin/game reload — only your settings (message, timing, Data Centers, destinations) persist.

### Crash Recovery / Resume Run

ShoutRunner saves its progress through an active RUN to disk as it goes, so an unexpected exit — FFXIV crashing, Dalamud/VenueOS being closed, a forced termination — doesn't necessarily lose that run. This has been live-tested against an actual forced FFXIV termination mid-transfer: after restarting the game, VenueOS detected the interrupted run, Resume picked it up correctly, and the route continued — including sending the shout it had been about to send when interrupted.

**What you'll see:** the next time you open ShoutRunner after an interrupted run, an **"Interrupted Run Available"** area appears above the normal Start controls, showing:
- Which RUN was interrupted and where (Data Center / World).
- The last destination that successfully completed, if any.
- The next destination it will pick up from.
- Any Data Centers that were already being skipped in that run, and why.

Two buttons: **Resume Run** and **Discard Recovery**.

- **Resume Run** continues from exactly where it left off: a destination whose shout already went out is never repeated, but a destination that was interrupted before its shout completed is retried. Any Data Center the run had already skipped (congestion, an unreachable destination Data Center) stays skipped rather than being retried. Resume always uses that run's own original route — Data Centers/destinations you've edited in Settings since the interruption are not picked up until the *next* run.
- **Discard Recovery** removes just the interrupted-run checkpoint; your saved route/settings are never affected.
- Starting a fresh run instead (**Start New Run**) asks you to confirm first, since doing so discards the still-resumable interrupted run.

A normal **Stop** is treated as an intentional decision to abandon that run, so it clears the recovery checkpoint too — there's nothing to resume afterward. A RUN that finishes normally also clears its own checkpoint (repeat's next RUN is a fresh start, not a continuation). If the saved recovery data itself can't be read, ShoutRunner tells you plainly ("ShoutRunner recovery data could not be loaded.") and offers only Discard Recovery — it never crashes over it.

---

## 6. Attendance

**Purpose:** tracks who's present in the venue during an "opening" (a session) and records visit/greeted history.

Guests are detected by presence (a periodic scan of nearby players), not by chat or manual entry.

### Tabs: Live, Visitors, History, Analytics

**Live** — the Session card, top to bottom:

- **Venue Area Type** — a two-way choice right above the Start/Resume buttons: **"Normal Venue Area"** or **"Outdoor Event Area"**. A line underneath explains whichever one is currently selected:
  - *Normal Venue Area:* "the radius always follows the operator's current position — appropriate for a housing instance. This is the safe default for every new opening."
  - *Outdoor Event Area:* "the radius center is captured once when this opening starts and stays fixed — for open-world venues where the operator may move around."

  This choice only matters **before** you start/resume an opening — pick it here, not in Settings. Once an opening is active, this becomes read-only status text ("Venue Area Type: Normal Venue Area" or "...Outdoor Event Area (fixed origin)") for as long as that opening runs; you can't switch modes mid-session. **Resuming** a past opening restores whichever mode it was originally started with. After you close or complete an Outdoor opening, the selector here resets to **Normal Venue Area** for the next one — Outdoor is a deliberate, per-opening choice, never a lingering default.
- Status badge: **Open**/**Closed**.
- While open: **Pause Opening** / **Close Opening**.
- While closed: **Start New Opening** / **Resume Latest** (disabled if nothing is resumable).

Pausing keeps the opening resumable; Closing ends it. Anyone already in the venue when you Start or Resume is counted as present but is *not* treated as a fresh arrival for auto-greeting purposes (see [Greeter](#7-greeter)).

Also on the Live tab: a "Tonight Summary" card (Current/Max/Min guest counts, Unique/Visits/Avg per Guest) and a searchable "Guests Nearby" list.

**Visitors** — searchable list of tonight's visitors. Each shows a status badge: **Greeted**, **Greeting...**, **Queued**, or **Not Greeted**. Right-click (or the inline buttons) for **Target**, **Greet**, **Mark Greeted**.

**History** — past openings, each with **Resume** / **Close** / **Summary** / **Delete**. Also: an "Opening Summary" for the selected opening, a "Max / Min Guest Comparison" chart (with a **Comparison days** field), and an **Export** card.

**Analytics** — a chart of the last 20 guest-count samples taken tonight.

### Export

Under History → Export: an **"Export folder"** field and an **Export Range to Excel** button. Produces a real `.xlsx` file (via ClosedXML) named `venue-stats-<timestamp>.xlsx` with three sheets — Daily Stats, Visitors, and Guest Samples — covering the range set by "Comparison days" (1–30). Shows "Exported: `<path>`" on success or "Export failed: `<message>`" on failure.

### Settings

- **Venue Details:** "In-game address" field, "Auto-detect address in-game" toggle, "Detect Now" button.
- **Presence Filtering:** "Lock to the territory the session starts in" toggle, "Filter by distance" toggle (with a "Radius (yalms)" field when on), and "Stats poll interval (seconds)". Venue Area Type is **not** set here — a pointer note in this card says so — it's chosen per-opening on the Live tab, right above Start New Opening (see above).

### Relationship with Greeter/VIP

Attendance is the single source of truth for "greeted" — Greeter and VIP both check with Attendance rather than tracking it themselves. A guest's first genuine arrival each opening (not someone already there when you opened) is what triggers automatic greeting.

All attendance data is per-venue.

---

## 7. Greeter

**Purpose:** sends your venue's welcome message(s) to guests, automatically on arrival or manually from Attendance.

### Automatic greeting

The first time a guest genuinely arrives during an open session (not someone already present when the session started), they're automatically queued for greeting. Repeat arrivals the same calendar night are not auto-greeted again. VIPs are always greeted on arrival regardless of this behavior.

### Manual greeting

There's no "Greet" button in Greeter itself — it's triggered from **Attendance → Visitors**, via the right-click menu or inline **Greet** button.

### Presets and the hotbar

Greeter uses a five-slot hotbar (**DJ 1**–**DJ 5**) on its operational screen; clicking a slot switches the active preset immediately. Below it: *"Active: `<preset>` · N queued · N greeted this session · Auto Greet ON/OFF"*. You can have any number of saved presets, independent of the five hotbar slots — assign which preset sits in which slot under **Settings → Modules → Greeter → Hotbar Slot Assignments**.

Each preset has:
- **Preset name**
- **Greeting line 1**, **Greeting line 2 (optional)**, **Greeting line 3 (optional)** — sent as separate `/tell` messages in order
- **Command after greeting (optional)** — runs last, not a fourth tell

`<name>` in any line or the command is replaced with the guest's character name. Save with **Save New Preset** or **Update Preset**.

### Notes

There's no dedicated Stop/Cancel button — marking a guest greeted (manually) cancels their queued greeting, and switching venues cancels everything pending for that venue.

A successful greeting is what marks the guest "Greeted" in Attendance. If a `/tell` fails to send at the chat-transport level, the attempt is dropped and the guest stays eligible to be greeted again; VenueOS has no special detection for "recipient is offline/has you blocked" beyond that.

---

## 8. VIP

**Purpose:** recognizes specific named characters on arrival with an optional private tell and/or public announcement, in addition to the normal Greeter message.

**There is no venue-wide VIP message template.** Each VIP record owns its own private tell and public announcement — Settings → Modules → VIP explicitly says there's nothing to configure there beyond enabling the module; everything else lives in the VIP app itself.

### Managing VIPs

**+ Add VIP** opens a record with:

| Field | Notes |
|---|---|
| Character name | |
| Home world | |
| Enabled | toggle |
| Custom private tell | multiline; sent as a private `/tell` before the normal Greeter message; `<name>` is replaced with the character's name; **leave blank to skip the private tell entirely** for this VIP |
| Public entrance announcement channel | Shout / Yell |
| Public entrance announcement | sent to the chosen channel; **note:** despite the example placeholder text, `<name>` is **not** currently substituted here — whatever you type is sent exactly as written; leave blank to skip the public announcement entirely |
| Notes | free text, for your own reference only — not used anywhere in the greeting flow |

**Use Current Target** fills Character Name and Home World from whatever you currently have targeted in-game; it does not touch the other fields.

Per record: **Edit**, **Enable**/**Disable**, **Remove**. A **Search VIPs** box filters the list by character name.

### How it fits together

On a guest's arrival, VenueOS checks whether they match an enabled VIP record:
- If they do and Custom Tell is set, that private tell is sent first; only once it's confirmed sent does the normal Greeter sequence follow.
- If Custom Tell is empty, it goes straight to the normal Greeter sequence.
- Once the Greeter sequence finishes, if a Public Announcement is set, it's sent to the chosen channel.
- Non-VIPs (or disabled VIP records) just get the normal Greeter sequence.

VIP records are usable for any "recognize this person specially" workflow you want — regular VIPs, staff, DJs, whatever fits your venue; VenueOS doesn't prescribe how you use them. VIP records are per-venue.

---

## 9. Party Finder

**Purpose:** publishes, edits, refreshes, and withdraws a native FFXIV Party Finder recruitment listing for your venue.

### Settings vs. the operational screen

**Settings → Modules → Party Finder** holds just two things:
- **"Auto Refresh on native 5 minute warning"** toggle — default **on**.
- **"Warning Message Override"** field, for matching a non-default in-game warning phrase.

Every recruitment field (category, duty, comment, party settings, roles, etc.) lives on the operational screen instead, and saves as you type — there's no separate Save button there.

### Creating/editing a listing

Fill in: Category, Duty, Objective, "Beginner friendly," Comment (byte-limited, shows a live counter), Search Area (world-only / private party + password), Conditions (completion status, average item level), Duty Finder Settings, Loot Rule, Languages, and Roles (slots, groups, per-slot job requirements with quick-mask buttons for Tank/Heal/Melee/Phys Ranged/Caster, or "Set all slots to any job").

The main action button reads **Recruit Members** when nothing is posted yet, and **Edit / Apply Changes** once a listing is already active (editing reuses the same listing rather than creating a second one).

### Refresh and end

- **Refresh Active Listing** — manual refresh at any time.
- Auto Refresh (if on) listens for the game's native "5 minute" warning in chat and refreshes automatically, throttled to at most once every 4 minutes.
- **Abort** — cancels whatever automation is currently doing, without withdrawing your listing or touching Auto Refresh.
- **End Party Finder** (red button) — turns off Auto Refresh for this venue *and* withdraws the active listing. Auto Refresh stays off afterward until you turn it back on yourself in Settings.

### What you'll see happen

Party Finder automation visibly opens the native FFXIV Party Finder window (and its sub-screens) to do its work, then closes it again when done — this is expected, not an error. A status line shows what's happening ("Opening Party Finder.", etc.), and a **Compatibility verified / Compatibility pending** badge indicates whether VenueOS has confirmed it can see the Party Finder UI correctly yet. It keeps working (refreshing, ending) even if you close the Party Finder module's own window.

Recoverable issues surface as inline status text (e.g. "Failed to detect a visible Party Finder window. Open Party Finder manually once, then retry.") and are also logged to **Settings → Diagnostics**.

Everything here — the recruitment criteria, Auto Refresh, and the Warning Message Override — is saved automatically, per venue.

---

## 10. Mair's Editor

**Purpose:** the authoring workspace for trivia question sets. **The question library is shared across all venues, not per-venue** — editing it in one venue affects every venue, and switching venues never resets or touches it.

### Creating and organizing sets

- **+ New Set** — prompts for a Title (defaults to "Untitled Question Set" if left blank).
- Set metadata fields: **Title**, **Description**, **Author**, **Version**, **Categories (comma-separated)**, **Tags (comma-separated)**.
- The library list (all your sets) can be manually reordered with **▲**/**▼** buttons per row — this only affects display order, nothing else.
- **Duplicate Set** — makes a copy titled "`<original>` - Copy" with a brand-new ID for the set and every question in it.

### Editing questions

- **+ Add Question** — starts with 3 blank wrong-answer slots.
- Each question has a **Correct answer** field and numbered **Wrong answer 1/2/...** fields, with a running count "Wrong answers (N / 3–9):". You need at least 3 (or 9, for an older-format set) and at most 9; the correct answer and every wrong answer must all be different from each other.
- **+ Add wrong answer** / **Remove last** — add/remove wrong-answer slots.
- **Move Up** / **Move Down** — reorder questions within the set.
- **Duplicate Question** / **Delete Question**.

### Save and Undo

There is exactly **one** save step: the **Save** button. There's no separate Draft/Publish flow — Save writes whatever's currently in the editor, complete or not.

**Undo** reverts your last change (label reads "Undo (nothing to undo)" when there's nothing to revert). Saving does **not** clear your undo history, so Save → notice a mistake → Undo → Save again works as expected. An **"Unsaved changes"** badge appears next to the set's status whenever it differs from what was last saved; trying to switch to a different set, or Close, while dirty asks you to confirm discarding those changes.

### Import / Export

- **Import** — enter a path to a `.fftrivia` file and click **Import**. If the file's ID doesn't collide with anything already in your library, it imports immediately. If it does collide, you'll see two prompts: first a dialog asking to overwrite (its buttons are **Confirm**/**Cancel** — Confirm replaces the existing set), and if you don't want to overwrite, a separate "Import as new set" prompt lets you give the incoming set a new title and import it as a distinct copy.
- **Export** — writes the currently open set to a `.fftrivia` file in your system temp folder (the panel tells you the exact path after export).

### Deleting a set

**Delete Set** is blocked outright — before any confirmation — if the set is currently in use by an active or resumable Mair's Trivia game: *"This set is in use by an active or resumable Trivia game and cannot be deleted. Editing remains available — active games use an immutable snapshot."* Otherwise you'll be asked to confirm a permanent deletion.

### Status: READY / INCOMPLETE / INVALID

Every set shows one of three statuses:

- **INVALID** — something is structurally broken: duplicate categories/tags, a question with a duplicate answer among its choices, too many wrong answers, or similar data problems. Invalid always takes priority over Incomplete.
- **INCOMPLETE** — nothing broken, but not filled in yet: blank title/author/version, zero questions, a question with a blank prompt/correct answer, or too few wrong answers.
- **READY** — passes every check.

**Only a READY set can be attached to a Mair's Trivia game** — Mair's Trivia refuses to create or start a game with anything else, both in the UI (only READY sets are offered) and again as a hard check when the request would be sent.

---

## 11. Mair's Trivia

**Purpose:** runs live, host-controlled trivia against a remote backend server — standalone one-off Games, or multi-Game Series with a cumulative leaderboard.

### Connection / Sign In

Configured in **Settings → Modules → Mair's Trivia**:

- **Backend URL**
- **Server-access password**
- **Username**
- **Password**

All four fields are plain, visible, readable text — none are password-masked. This is a deliberate product decision so venue staff can copy/share connection configuration easily. Treat your VenueOS configuration accordingly (see [Data / Privacy notes](#16-data--privacy--credential-notes)).

These settings are saved per venue. If you've previously signed in and a stored session token exists, VenueOS reconnects automatically when you switch to that venue — no manual sign-in needed. If your session expires while working, VenueOS tries to silently renew it, and if that fails, quietly falls back to signing in again with your stored username/password. Only if *both* fail do you see an error badge (*"Session expired and automatic sign-in failed."*) — at that point sign in again manually via Settings.

### Choosing a question set

Only sets marked **READY** in Mair's Editor can be selected. When you create a game, VenueOS sends the backend a full copy of the set's questions at that moment — later edits to the same set in Mair's Editor never change a game that's already running. Note: while Mair's Trivia can re-attach a different set to an in-progress game internally, there is currently no button in the operator panel to do this — question set choice effectively happens only when you create a Game (or start the next Game in a Series).

### Standalone Game — step by step

1. Sign in (above), if not already connected.
2. On the Setup screen, pick a READY set from the "READY question sets" list.
3. Enter a **Game name** and click **Create Game**.
4. The live console opens, showing a **Join: `<code>`** badge and a **Copy Link** button — share that link with players.
5. **Preview** a question (host-only — shows you the question and correct answer before opening it).
6. **Open** it for players to answer, or **Skip** it while still in preview.
7. **Close** it once you're ready — shows the correct answer and who answered first correctly (or "No one answered correctly.").
8. **Repeat / Revisit this question** if you want to run that same question again for more points.
9. Continue through your questions, then **End Game** (available any time except while a question is actually open) — ends the game for everyone; a confirmation reminds you this does not end any Series it's part of.
10. When finished, **Back to Setup** returns you to the setup screen — this is local navigation only; it doesn't erase anything from the backend.

**Players** list (per game): name, score, and a correct✓/incorrect✗ count, with **Kick** and **Adjust** actions per player.

**Game standings:** rank, name, correct/answered, and points.

**Timer:** a pre-game "Question timer (0–20s, 0 = untimed)" setting controls how long a question stays open once set at game creation; there's no live countdown display in the operator panel itself, and exactly how answer-locking works at timer expiry is backend behavior VenueOS doesn't expose further detail on.

### Scoring

Configured under **Settings → Modules → Mair's Trivia → Defaults**, applied to a game at the moment you create it (changing these afterward doesn't affect a game already running):

| Setting | Default |
|---|---|
| Correct answer points | 100 |
| Incorrect answer points | 0 |
| First-correct bonus | 50 |
| Time bonus multiplier | 5 |

"Answered" is always **correct + incorrect** — never a raw question count, so a player who joined late isn't penalized for questions they never saw.

Rank, and the exact arithmetic behind the time bonus, are computed by the backend — VenueOS just displays whatever rank/score numbers the server returns.

### Game Series

A Series is a persistent, multi-Game competition: each Game inside it keeps its own name/state/standings, while the Series itself tracks cumulative standings and an overall champion separate from any single Game's winner.

- **Create New Series** — from the Setup screen.
- **Copy Series Link** — the **one** join link players use for the entire Series, from the first Game through every later one. Don't hand out a Game's own internal code — inside a Series, that's shown only as reference text ("Internal game code: `<code>` (players use the Series link above)").
- Run a Game the same way as a standalone one; **End Game** finishes just that Game.
- **Start Next Game In Series** — begins the next Game under the same Series and join link.
- **End Series** — finishes the whole competition and crowns the Series Champion(s); confirmation warns this can't be undone.
- **Back to Setup** on a finished Series is local navigation only, same as for a standalone game.

**Series Players** vs. **Game Players**: Series Players is the full roster of everyone who's joined the Series (including someone who hasn't played a Game yet); Game Players is only whoever's in the currently active Game. **Series standings** are cumulative across every Game in the Series; **Game standings** cover only the current Game. Both show rank, name, correct/answered, and points.

### Player management

- **Kick** (per Game player) — stops them answering in *this* Game only; their score in this Game's standings is kept, and Series participation is unaffected.
- **Remove** (per Series player) — blocks them from joining *future* Games in the Series; their historical results are preserved.

### Manual score adjustment

Click **Adjust** next to a player, enter **Points (+/-)** and a **Reason** (required — the adjustment is rejected without one), then **Apply adjustment** (or **Cancel**). There's no on-screen history of past adjustments in the current UI — only the player's running total is shown.

### Lifecycle / Resume

- **Closing the module's window** doesn't stop anything — polling for game updates keeps running in the background regardless.
- **Disabling the Mair's Trivia module** stops VenueOS's own polling, but the Game/Series on the backend keeps running and stays resumable.
- **A plugin reload or game crash** loses VenueOS's local reference to whatever Game/Series was open (it doesn't remember across a reload) — but nothing is lost on the backend. Use **"Refresh resumable games/series"** on the Setup screen and click the listing to pick back up where you left off.
- **Switching the active venue** while a Game is running prompts a confirmation and ends that Game for all players — but never ends its Series, which stays resumable.
- **Back to Setup** never deletes anything server-side — it's purely local navigation.

---

## 12. Bingo

**Purpose:** hosts a live Bingo room — cards, calling, a player-facing browser view, and payout tracking — backed by a remote Bingo server, the same way Mair's Trivia is.

### Initial Setup

Under **Settings → Modules → Bingo** (and near Create Game, for what's specific to one game):

- **Room key** — a per-venue key identifying your room to the backend. Edit it with **Save Room Key**, or generate one with **Generate Random Room Key**. Replacing an existing key (rather than setting one for the first time) asks you to confirm first.
- **Announce channel** — **Shout**, **Yell**, **Party**, or **None** (sends nothing).
- **Roll command** — **Random (/random 75)** or **Dice (/dice 75)**. Dice mode carries an on-screen warning that its exact chat output format and party requirement haven't been verified in-game — prefer Random unless you've confirmed Dice works for you.
- **Game Type** and other default game settings, set here and reused as the starting point for a new game (see below).

### Room listing / resume

A **"This venue's rooms (host handoff / resume)"** list shows this venue's known rooms with a **Refresh room list** button and a per-room **Resume** button — useful for picking a room back up (e.g. after a plugin reload, or handing hosting to another operator). You can also resume a specific room directly by typing its code into **Room code** and clicking **Resume by code**.

### Creating a Game

Near Create Game: **Game Type** (Single Line, Two Lines, Four Corners, or Blackout — see [Game Types](#game-types) below), Cost Per Card, Starting Pot, Prize Percentage, and letters/labels if you're customizing them. The venue name sent to the backend is read automatically from your active Venue Profile — there's no separate field for it.

1. **Create Game** — creates the room (a Draft) with these settings.
2. **Start Game** — locks in the economics (cost/pot/prize split) and moves the room from Draft to Active. Once started, Game Type is locked for that game — changing the Settings default afterward never retroactively changes a game already in progress.

### Players

- **New player name** field, plus **Use Current Target** to fill it (and their home world) from whatever you have targeted in-game — this never touches the backend by itself.
- **Paid cards** / **Comp cards** fields (0–16 each) set how many of each a new player gets; **Add Player** creates them. Existing players can be adjusted with **+ Paid** / **- Paid** / **+ Comp** / **- Comp**.
- **Complimentary cards never increase the pot** — the backend's pot math only counts paid cards; comp cards are display-only for that purpose.
- Each player gets an automatic short link — VenueOS looks up an existing one first and only mints a new one if none exists yet. The link is shown as a read-only, selectable field on their row, with **Copy Link** and **New Link** buttons (New Link mints an additional working link without invalidating the old one).

### Player Browser

Players open their own short link in a browser to see their cards and daub (mark) called numbers themselves. VenueOS has no way to confirm what happens in a player's own browser across sessions/devices (whether their daubs are still there if they close and reopen the page, or open the link on a different browser) — that's backend/browser behavior outside what the host client can observe. What VenueOS *can* show you is the current server-confirmed state of every card at any time, via the Card Viewer below.

### Calling Numbers

- **Roll & Call** — the same action available both on the main Bingo screen and on the detached **Called Numbers** window; it sends your configured roll command (`/random 75` or `/dice 75`) and records the result once seen.
- The **Called Numbers** window shows the complete 1–75 board from the very start (not just numbers called so far), and keeps working even if you close the main Bingo window — it's a fully independent detached window.
- Numbers actually get "called" from the backend's confirmation, not merely from what the dice command printed — this is what keeps the Card Viewer, Called Numbers board, and every player's browser in agreement.

### Card Verification (Card Viewer)

The host Card Viewer shows every card for every player, laid out in a responsive tiled grid that adapts to window width. Each cell is colored to make its state obvious at a glance:
- **Called, not yet daubed** — a distinct warning color.
- **Daubed by the player** — a distinct success color.
- **Free center** — its own neutral color, always "filled" for scoring purposes.
- **Any cell in a currently-complete winning pattern** — highlighted over everything else.

Hovering a cell explains which state it's in. Card contents themselves are generated by the same proven card-generation algorithm as the player browser, so what you see in the Card Viewer matches what the player sees card-for-card.

### Game Types

- **Single Line**, **Two Lines**, **Four Corners**, **Blackout** — chosen at game creation (see above) and locked for the life of that game.

### Balls to Bingo

Next to each player, a number in parentheses (e.g. "Kei Joi (1)") shows **Balls to Bingo** — the fewest additional numbers that still need to be *called* before that player's best card completes the current Game Type's pattern. This is calculated by the backend from called numbers only, not from whether the player has actually daubed those numbers yet — so it tells you how close a card mathematically is, independent of whether its owner is keeping up with daubing.

### Bingo Calls

When a player's card completes the pattern, a detached **Bingo Call Alert** window pops up on its own — independent of whether the main Bingo window is even open — with a large **"BINGO CALLED!"** heading. From it: **View Cards** (jumps straight to that player's cards in the Card Viewer), **Payout Details** (a compact summary plus a link back into the main Bingo screen), and **Dismiss**. If more than one caller is pending, they're all listed; VenueOS tracks a full caller history, not just the most recent one.

### Payout Ledger

**Sync Payouts** is the only way a payout obligation gets created — VenueOS never lets you manually pick a "winner" to pay. Once synced, each obligation shows who's owed what, how much has been confirmed paid, and how much remains outstanding.

**Automatic payout is an optional, experimental convenience feature — not a fully verified path.** Pressing **Attempt Payout** requires first checking an "I understand this requires live testing" acknowledgment and entering a target (with its own Use Current Target button). A live self-trade test intentionally tried to abuse this: the attempt did **not** falsely report success — it came back **Ambiguous**, and no unintended payout occurred. An ambiguous result is explicitly *not* the same as unpaid — it always needs manual reconciliation, never an automatic retry. Use **Mark Paid** / **Mark Not Paid** (each behind its own confirmation) once you've independently confirmed what actually happened, or simply hand out winnings through a normal in-game trade and reconcile the ledger manually — either is a supported way to run payouts today.

### Leaving / Resuming / Closing

- **Leave Game** — a purely local action: it makes no request to the backend at all. The room keeps running and stays fully resumable; use the room list or Resume by code to come back to it later.
- **Close Room** — permanently deletes the room and its game state from the backend. It cannot be resumed afterward, and you're asked to confirm first. You can't Close a room you're still actively in — Leave it first.

---

## 13. Detached Windows / Auto Pop-Out

Every module can run either **embedded** (inside the main VenueOS tablet) or **detached** (its own separate window) — the content and behavior are identical either way; it's purely a display choice.

- From an embedded module, click the pop-out icon (tooltip: "Open in separate window") in its header to detach it.
- A detached window has its own compact header: the module's icon and name on the left, a Settings gear and a Close (X) on the right. The empty middle doubles as a drag handle.
- Closing a detached window only closes that window — the module stays enabled and any of its automation keeps running.
- The **"Open modules in separate windows"** toggle in Settings → General controls what happens when you launch a module from Home: on, it opens (or focuses, if already open) detached; off, it opens embedded in the tablet. Clicking an already-open detached module's tile again just brings it to front rather than opening a second copy.

---

## 14. Persistence

| Scope | Examples |
|---|---|
| **Global** (shared by every venue) | Which modules are enabled/disabled, Auto Pop-Out preference, the entire Mair's Editor question library |
| **Venue-specific** | ShoutRunner settings (and its recovery checkpoint for an interrupted run), Attendance settings and history (including each opening's own Venue Area Type), Greeter presets/hotbar, VIP roster, Party Finder recruitment criteria, Mair's Trivia connection settings and scoring defaults, Bingo room key and default game settings |
| **Runtime-only** (does not survive a reload) | ShoutRunner's on-screen terminal history, Mair's Editor's Undo history, Mair's Trivia's and Bingo's reference to "which game/room is currently open" (though the game/room itself survives on its backend and can be resumed) |

Disabling a module never erases its saved configuration — re-enabling it picks back up exactly where you left off.

---

## 15. Troubleshooting

**VenueOS doesn't open by itself.** That's expected — it never opens automatically. Run `/venueos`.

**A module I expect is missing from Applications.** Check **Settings → Modules** — it's probably disabled. Raffle and TournamentControl are disabled by default on a fresh install (see [below](#under-development-modules)).

**ShoutRunner won't travel between Worlds.** Confirm Lifestream is installed and working — ShoutRunner depends on it for all world travel but doesn't check for it before letting you press Start. Also check that at least one Data Center and one destination are configured, and check the Run Terminal for the actual failure reason.

**ShoutRunner shows an "Interrupted Run Available" area I don't expect.** A previous run didn't get a chance to finish cleanly (a crash, a forced close). Review the recovery summary and either **Resume Run** to continue it or **Discard Recovery** to clear it — see [ShoutRunner's Crash Recovery section](#5-shoutrunner).

**Bingo's automatic payout came back Ambiguous.** That's the automation being honest that it couldn't confirm the trade completed — it is not the same as unpaid, and never means a payout happened without confirmation. Check in-game whether the trade actually went through, then use **Mark Paid**/**Mark Not Paid** in the Payout Ledger to reconcile it manually.

**Party Finder isn't refreshing.** Check the module's status text and the Compatibility badge; on a slower system, native UI automation may simply need more time. Confirm Auto Refresh is on in Settings if you expect automatic refreshes.

**Mair's Trivia won't let me pick a question set.** Only sets marked **READY** in Mair's Editor are selectable — open the set in Mair's Editor and check its status/reasons list.

**Mair's Trivia shows a session-expired error.** Automatic recovery normally handles this silently; a visible error means both silent renewal and automatic re-login failed. Sign in again manually in Settings.

**A player doesn't show up in the game right away.** VenueOS polls the backend periodically rather than instantly — give it a moment.

Anything logged as an error or warning also appears in **Settings → Diagnostics**, filterable by module/level, with a **Copy** button if you need to share the log.

---

## 16. Data / Privacy / Credential Notes

- Mair's Trivia's Server-access password, Username, and Password are stored and displayed **in plain, readable text** by design, so venue staff can easily copy/share connection details. Don't casually share your VenueOS configuration file with people you don't want to see them.
- Mair's Trivia player/game/Series data, and Bingo room/player/payout data, live on their respective remote backends, not just locally.
- Your local VenueOS configuration otherwise contains operational data — venue names, rosters, presets, recruitment criteria, and similar.
- This manual makes no telemetry claims; nothing in the source reviewed for this manual indicates VenueOS phones home beyond the Mair's Trivia backend you configure yourself.

---

## 17. Updates

Once VenueOS is installed from the Experimental Plugin Repository, updates arrive the normal Dalamud way — the Plugin Installer checks configured repositories periodically (or via Settings → Experimental → "Check for Updates") and offers an update when a newer version is published. You should not need to manually replace any files for a normal release.

> **Developer note:** if you're building VenueOS from source for development, you load the built DLL directly via Dalamud's dev-plugin workflow instead — see `RELEASE.md`. That workflow is not part of the normal user update path described above.

---

## Under Development Modules

**Raffle and TournamentControl** exist in this release but are **not ready for use**:

- They ship **disabled by default** on a fresh install.
- Because they're disabled, they do **not** appear on the Home/Applications screen.
- They still show up in **Settings → Modules**, clearly labeled **"Under Development"**, so you can see they exist.
- They are not covered by this manual's operational instructions and should not be treated as functional features of this release.

---

*See also: [RELEASE.md](../RELEASE.md) for packaging/versioning details, and [docs/RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) for the release-readiness checklist.*
