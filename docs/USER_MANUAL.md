# VenueOS User Manual

**Current version:** 0.3.5
**What it is:** VenueOS is a Dalamud plugin for Final Fantasy XIV — a single tablet-style operations console for running an in-game venue: attendance tracking, automatic guest greeting, VIP recognition, promotional shout routes, Party Finder recruitment, live host trivia, Bingo, raffles, tournament brackets, block-letter text composition, timed giveaways, extended macros, and manual Shout announcements.
**Supported environment:** Windows FFXIV with Dalamud installed (API level 15). VenueOS is unofficial, third-party, and not affiliated with Square Enix or the Dalamud/XIVLauncher project.

**Modules covered in this manual:** ShoutRunner, Attendance, Greeter, VIP, Party Finder, Mair's Trivia, Mair's Editor, Bingo, Raffle, Brackets, Block Letters, Giveaways, Macro, Shouts. Every module in this release ships enabled by default and appears on Home.

You can also read this manual inside VenueOS itself — click the **User Manual** tile on Home (just before Settings), no internet connection required.

This manual describes the current release only. It does not describe planned features, and it does not describe how any donor/standalone plugin VenueOS was built from used to behave where VenueOS now differs. See [Known Issues](#24-known-issues) for the small number of documented, non-blocking caveats in this release.

---

## Quick Start

1. Install VenueOS through Dalamud's Experimental Plugin Repository (see [Installation](#1-installation)).
2. Open it with `/venueos` (it does not open automatically on login).
3. Go to **Settings → Venue** and create or select a Venue Profile.
4. Check **Settings → Modules** — enable/disable modules as needed. Every module ships enabled by default.
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
| Raffle | Runs a raffle with paid/free tickets and a live browser wheel, backed by a remote server | Yes — remote Raffle backend | Yes | Yes (realtime mirroring continues) |
| Brackets | Runs a single-elimination tournament bracket, backed by a remote server | Yes — remote Brackets backend | Yes | Yes (realtime mirroring continues) |
| Block Letters | Composes FFXIV block-letter text within real per-destination character limits | No | Default destination only | N/A (pure composing surface; the composition itself is not persisted) |
| Giveaways | Runs timed venue giveaways with automated announcements and `/random` roll tracking | No | Yes | Yes (the announcement/roll timeline keeps running) |
| Macro | Runs extended, nestable FFXIV macros from a live launcher and up to four faux hotbars | No | Yes | Yes (a running macro and the faux hotbars keep working) |
| Shouts | Fires a saved, reusable announcement preset manually to Yell/Shout, across up to 15 configurable slots | No | Yes | Yes (the Last Shout timer keeps counting) |

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

**Enabled vs. disabled modules:** a disabled module does not appear on Home at all — it's simply not there, not just grayed out. It still appears in **Settings → Modules**, where you can re-enable it. Toggling a module on/off in Settings → Modules is remembered — it's not reset by future updates unless you've never touched that toggle.

**Settings vs. the module itself:** where a module has both, its **Settings → Modules → *Module*** page holds persistent configuration (server URLs, credentials, defaults), while the module's own app screen (opened from Home) holds live operational controls (start/stop, current guest list, active game state, and so on). The two are intentionally separate.

**VenueOS only shows itself while you're logged into a character.** Nothing VenueOS draws — the tablet, a detached module window, a faux Macro hotbar, Bingo's auxiliary windows — appears at the title screen or character select. Everything you've configured (settings, Macro hotbar assignments/position, venue profiles) is untouched by logging out and simply reappears the next time you log a character in. The one exception is ShoutRunner: if a route is already running when a world/Data Center travel step temporarily interrupts your session, ShoutRunner's own screen (including **Stop**) stays available through that transition instead of disappearing and reappearing — every other VenueOS window stays hidden during that same window.

---

## 3. Venue Profiles

A **Venue Profile** represents one venue identity: a name and a theme, plus whichever module settings are stored per-venue (most of them are — see the [Persistence](#20-persistence) table).

Manage venues under **Settings → Venue**:

- **+ Add Venue** — creates a new profile and switches to it immediately.
- Each row shows the venue's name, its theme, and (if it's the active one) an **Active Venue** badge.
- **Switch to** — switches to that venue (only shown on non-active rows).
- **Rename** — inline rename with **Save**/**Cancel**.
- **Duplicate** — copies the venue (including its module configuration) under a new "... copy" name.
- **Delete** — permanently deletes the venue and all of its module configuration. Disabled if it's the only remaining venue.

You can also switch venues quickly from the toolbar's dropdown, without going into Settings.

**Switching away from an active Mair's Trivia game:** if Mair's Trivia has a game running in the venue you're leaving, switching shows a confirmation: *"Mair's Trivia has an active game ("<game name>") running in this venue. Switching venues will end this game for all connected players. This will not end the Series it may belong to."* Confirming ends that game (but never the Series it's part of, which stays resumable). No other module currently has a venue-switch warning.

**Global vs. venue-specific:** module enabled/disabled state, the Auto Pop-Out preference, and the Mair's Editor question library are global (shared across every venue). Everything else module-specific — connection settings, presets, rosters, recruitment criteria — is per-venue. See [Persistence](#20-persistence) for the full breakdown.

---

## 4. Global Settings

Found under **Settings → General**:

- **"Open modules in separate windows"** toggle (Auto Pop-Out). Off by default. When on, launching a module from Home opens (or focuses) its own detached window instead of embedding it in the tablet. This applies the same way for every venue and doesn't change when you switch venues. Settings itself is unaffected by this toggle and always opens embedded.
- An **About VenueOS** card showing a version line (the actual installed release version, e.g. "VenueOS 0.3.4") and a one-line description of what VenueOS is.

**Settings → Modules** is where you enable/disable modules and jump into each one's own settings — see [Basics](#2-venueos-basics) above.

**Settings → Diagnostics** shows a filterable log of recent errors and configuration-recovery warnings (filters: **Log Level** — All Levels/Errors/Warnings, **Module**, and a **Search** box), plus **Clear** and **Copy** buttons and small stat tiles (Total Entries, Errors, Warnings, Last Update). Useful for troubleshooting — see [Troubleshooting](#21-troubleshooting).

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

All four fields are plain, visible, readable text — none are password-masked. This is a deliberate product decision so venue staff can copy/share connection configuration easily. Treat your VenueOS configuration accordingly (see [Data / Privacy notes](#22-data--privacy--credential-notes)).

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

**Automatic payout uses server-backed transaction tracking and has completed successful live end-to-end testing** — target verification, the real Trade window, gil staging, Ready/Confirm, confirmation handling, actual gil transfer, multi-chunk payouts, and the correctly-derived final remainder chunk all passed live QA (see `docs/BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md`). Pressing **Attempt Payout** still requires first checking an "I understand this requires live testing" acknowledgment each session and entering a target (with its own Use Current Target button) — payout automation always moves real gil, so this deliberate per-session confirmation stays in place regardless of how well-tested the engine is. An **Ambiguous** result — the automation being honest that it could not confirm the outcome either way — is explicitly *not* the same as unpaid and always needs manual reconciliation, never an automatic retry. Use **Mark Paid** / **Mark Not Paid** (each behind its own confirmation) once you've independently confirmed what actually happened, or simply hand out winnings through a normal in-game trade and reconcile the ledger manually — either is a supported way to run payouts today.

### Leaving / Resuming / Closing

- **Leave Game** — a purely local action: it makes no request to the backend at all. The room keeps running and stays fully resumable; use the room list or Resume by code to come back to it later.
- **Close Room** — permanently deletes the room and its game state from the backend. It cannot be resumed afterward, and you're asked to confirm first. You can't Close a room you're still actively in — Leave it first.

---

## 13. Raffle

**Purpose:** runs a venue raffle with paid/free ticket tracking and a live browser wheel, backed by a remote Raffle server. VenueOS is the organizer console; the wheel itself (spinning, redraw, winner reveal) lives on a webpage the host and viewers open in a browser.

### Settings (Settings → Modules → Raffle)

- **Backend URL** and **Backend Access Key** — the raffle server's address and its organizer secret. This is VenueOS's own credential (separate from any host/viewer link token) and, per VenueOS's standard credential convention, is shown as plain, selectable, copyable text — never masked.
- **Defaults** applied to every new raffle you create: **Starting Pot**, **Ticket Cost**, **Prize %**, **Paid Tickets For Free (bonus rule)** (e.g. buy N paid tickets, get one free), and **Free Tickets Per Block**. Each raffle can override its own copy of these after creation.

### Creating and managing raffles

- **New raffle name** + **Create raffle** — creates a local raffle you can then configure and publish.
- Per raffle: **Rename**, **Archive** (nondestructive — hides it from the active list, fully restorable), **Reset** (clears participants/tickets/winner but keeps the raffle and its backend link — confirmed, since it destroys in-progress data), and **Delete Permanently** (confirmed, cannot be undone — if the raffle was ever published, VenueOS also best-effort deletes the backend's copy; a failed backend cleanup is logged to Diagnostics but never blocks the local deletion you already confirmed).
- Archived raffles are hidden from the normal list; a **Show archived raffles** view offers **Restore** per row.

### Participants and tickets

- **Name** and **Home World** (optional — leave blank for a legacy Name-only entrant) identify a participant; **Use Current Target** fills both from whatever you have targeted in-game.
- **Paid tickets to add** / **Add Paid Tickets**, **Free tickets to add** / **Add Free Tickets**, or **Add Participant Only** with zero tickets.
- Existing participants: **+Paid** / **-Paid** / **+Free** / **-Free** adjust their ticket counts; **Remove** deletes them from this raffle entirely. Counts never go negative.
- Two characters with the same name on different Home Worlds are always tracked as distinct participants; a legacy Name-only entrant (imported from an older export, or entered with no Home World) is also tracked as its own distinct identity rather than being guessed at.

### Publishing and the live wheel

- A status badge shows **Not Published**, **Unpublished Changes**, or **Published**.
- **Publish / Update Raffle** sends the current roster and settings to the backend; **Refresh From Backend** pulls the backend's current state back into VenueOS.
- Once published, **Host Link** and **Viewer Link** appear (each with its own **Copy** button) — share the Host Link with whoever will spin the wheel, and the Viewer Link with the audience. These are now short links (`.../l/AB23CD`) that are practical to paste directly into FFXIV chat, resolved by the backend to the full link automatically — if a short link hasn't been minted yet (e.g. no Access Key configured in Settings), the field falls back to showing the full link instead, with a **Retry Short Links** button once the key is set. A **Show Full Links** toggle reveals the original full-length links if you ever need them. VenueOS itself never spins the wheel; it's a read-only observer of the backend's spin state and updates automatically the moment a result comes in.
- **"Unpublished Changes"** appears the moment you adjust tickets, add a participant, or change settings after a Publish — a reminder that the live wheel hasn't seen your latest edits yet until you Publish again.

### Redraw and exclusion

The browser wheel's Spin button becomes **Redraw** once a winner already exists, and shows a confirmation before actually sending a redraw — the backend enforces this the same way regardless of what the browser does, so a redraw can never happen silently. A confirmed redraw removes **every** ticket belonging to the previous winner from the pool going forward, and VenueOS marks that participant **"Excluded (previous winner)"** in the roster — they cannot win again even if you add more tickets for other participants and republish, unless you explicitly clear the exclusion. To do that, check **"Also clear previously-excluded winners on publish"** before your next Publish.

### Import / Export

**Export to XLSX** and **Import from XLSX** round-trip a raffle's full settings and participant roster (including Home World and exclusion state) as a spreadsheet. Importing always creates a brand-new local raffle with no backend link yet — publish it again to put it live. Importing an older, pre-Home-World export brings every participant in as a legacy Name-only entrant rather than inventing a Home World for them.

---

## 14. Brackets

**Purpose:** runs a single-elimination tournament bracket, backed by a remote Brackets server (module ID `games.tournament`, internally still named TournamentControl — this never affects anything you see).

### Settings (Settings → Modules → Brackets)

- **Server URL**, **Server access password**, and **Organizer key** — plain, selectable, copyable text, per VenueOS's standard credential convention.
- **Authenticate** signs in with the password/key above; **Create Organizer** registers a brand-new organizer identity on the backend (confirmed first — do this once per organizer; it does not migrate any existing tournaments).
- **Default game name** / **Default tournament name** — pre-filled when you create a new tournament.
- Callout **Delay between lines (seconds)** — timing for the Call Players announcement (see below).

### Browsing and creating tournaments

The live screen opens on a browser: search, a **Status** filter, **Refresh**, and a list of this organizer's tournaments with a **Delete** action per row (disabled, with a tooltip, while a tournament is Active — cancel it first). While authenticated and nothing is loaded, an inline form lets you enter a **Game name**, **Tournament name**, and **Event date** and click **Create**.

### Setup phase

Once a tournament is loaded and still in Setup: add players one at a time (**Player name** / **Add**) or in bulk (**Add Bulk Entries**), reorder seeding with **Up**/**Down** per row, **Remove** a player (confirmed — remaining seeds renumber), or **Randomize Seeds** (confirmed). **Start Tournament** generates the bracket from the current seed order and is confirmed, since players can no longer be added, removed, or reseeded afterward.

### Running the bracket

Once Active, matches are shown round by round. Each match card shows both contestants; **Call Players** sends the configured announcement template to your Shout/Yell channel with the configured delay. Recording a winner is confirmed. Byes (from an odd number of entrants) auto-advance automatically — you'll never see a bye match waiting to be called.

### Correcting a result

A completed match always offers **Correct Result**, which flips the winner to the other contestant:
- If no later match has been played yet, this is a plain confirmation.
- If a later match already completed using the wrong winner, VenueOS shows a red **"Correct Result (clears completed later matches)"** button instead — its confirmation states plainly that every already-completed downstream match will be reset back to waiting, and this cannot be undone.

### Completion and cleanup

Once every match is decided, a champion card appears. **Copy Public Bracket URL** copies a link to the tournament's public (read-only, no login needed) bracket view. **Cancel Tournament** (confirmed) is available any time before completion. **Delete** (from the browser) permanently removes a tournament and its full bracket/history from the backend once it's no longer Active.

### Multiple controllers / realtime

Brackets stays in sync automatically if more than one controller (or the public web page) is watching the same tournament — a result recorded on one is reflected on the others without a manual reopen. If VenueOS briefly loses its connection, it reconnects and refreshes automatically once the connection returns.

---

## 15. Block Letters

**Purpose:** a text/glyph composer for FFXIV's built-in "block letter" characters — the large stylized letters/digits/symbols the game itself renders in chat — kept within the real character limit of wherever you intend to paste the result.

### Composing

- **Destination** selector: **Chat**, **Party Finder (Comment)**, or **Macro Line** — each has its own real limit (Chat and Macro Line are measured in bytes, matching how FFXIV itself counts them; Party Finder Comment matches VenueOS's own Party Finder module).
- The composition box behaves like an ordinary text editor: type, paste, select, and move the cursor normally. Clicking a glyph button in the palette inserts it immediately at your current cursor position (or replaces your current selection) — no need to click back into the box first.
- The palette renders each button using the actual in-game glyph (via FFXIV's own font), not a placeholder label, so what you see is what will appear in-game. Letters, digits, and a curated set of additional symbols are all available.
- A live byte counter shows how much room is left against the selected destination's limit, and input simply stops accepting more once you're at the limit — exactly like typing directly into that field in-game would.

### Switching destinations

Changing the Destination never discards or truncates your composition. If your current text is over the newly selected (smaller) destination's limit, a warning appears and **Copy** is disabled until you shorten it back down — nothing is silently cut.

### Copying

**Copy** puts the exact composed text on your clipboard, ready to paste into FFXIV chat, a Party Finder comment, or a macro line. VenueOS does not send anything on your behalf — you paste it wherever you need it yourself.

### Settings (Settings → Modules → Block Letters)

Only one thing is persisted per venue: the **Default Destination** the composer opens to. The composition text itself is intentionally not saved anywhere — it's cleared on venue switch, module disable, or a plugin reload, since it's meant as a one-off compose-and-copy tool rather than a saved document.

---

## 16. Giveaways

**Purpose:** runs a timed venue giveaway with automated Shout/Yell announcements at Start, Midpoint, and Closing, and an FFXIV `/random` roll tracker that decides a winner automatically by your chosen rule.

### Authoring presets (Settings → Modules → Giveaways)

Settings shows a compact list of saved presets (name, channel/winner-mode summary, a **RUNNING** flag if one is currently active) with **Edit**/**Delete** per row and a **+ New Preset** button. All authoring happens in one dedicated editor window — there is no separate inline editor to get confused with:

- **Preset Name**
- **Shout / Yell** — which channel every announcement line goes to.
- **Delay Between Lines (seconds)** and **Giveaway Duration (seconds)**.
- **Start**, **Midpoint**, and **Closing** blocks — up to 10 lines each, with **+ Add Line**/**Remove** per line. Blank lines are simply skipped when sending.
- **Winner Mode** — **Highest**, **Lowest**, or **Closest** (with a **Closest Target Number** field when Closest is selected).
- **Allowed Rolls Per Person** — `1` accepts only a player's first roll; a higher number accepts up to that many rolls per person and keeps their best; `0` means unlimited rolls, taking the best of however many they make.
- **Special Numbers (comma-separated)** — rolls that land on one of these are highlighted with a distinct "SPECIAL" badge. Special Numbers only take effect when Allowed Rolls Per Person is exactly `1` — with multiple or unlimited rolls allowed, special-number highlighting is turned off entirely, since letting someone re-roll for it would defeat the point.
- **Winner Announcement** — the **Yell/Shout** channel and one-line template used by the **Announce Winner** button (see below). Default channel is **Yell**, default template is `Congratulations <name>! You won the giveaway!`.

**Save** validates the preset and only closes/persists on success; **Cancel** (or closing the window's X) discards whatever you were editing with no effect on the saved preset. Starting a giveaway snapshots the currently selected preset — editing that same preset afterward in Settings never changes the giveaway already in progress.

### Running a giveaway

The live screen shows the active (or, if none is running, currently selected) preset's name in large, unmistakable text, a preset selector (locked while a giveaway is running), **Start**, **Cancel** (confirmed — stops remaining announcements and closes roll acceptance immediately, but never deletes the preset, and captured rolls stay visible until you Clear Results), and **Clear Results**. A separate, independently opened **tracker window** shows the exact same roll tracker outside the main module window, if you want it detached.

**Timeline:** Start's lines send in order; once the last one goes out, the countdown begins and roll acceptance opens. At the halfway point, Midpoint sends. At the full duration, Closing begins sending — but **roll acceptance does not close yet**. Rolls remain accepted through the entire Closing sequence (including between lines), closing only the instant Closing's last non-empty line has actually gone out. If Closing has no lines configured, roll acceptance closes immediately once the duration expires instead. This is deliberate: a Closing announcement that says "last chance to roll!" would otherwise be a lie.

### Announce Winner

Between **Controls** and the **Roll Tracker**, a compact **Announce Winner** card lets you send a personalized winner call-out once the giveaway is done — no need to type the winner's name yourself.

- **Channel selector** (Yell/Shout) and **Announce Winner** button, side by side.
- Below them, a one-line **announcement template** — write anything you like, using the placeholder `<name>` anywhere you want the winner's name(s) inserted. A template doesn't have to use `<name>` at all if you'd rather send a fixed line.

**The `<name>` placeholder:** it's replaced with the current winner(s), grammatically formatted, and never includes a Home World:

| Winners | `<name>` becomes |
|---|---|
| One | `Kei Joi` |
| Two (tied) | `Kei Joi and Rabid Squirrel` |
| Three or more (tied) | `Kei Joi, Rabid Squirrel, and Mairwen Kor` |

Ties are never something you have to resolve by hand — every tied participant is included automatically, in the order they rolled. VenueOS only formats the name list itself correctly; it doesn't rewrite the rest of your sentence. For example, the template `Congratulations <name>! You are our winners!` sent to a two-way tie becomes `Congratulations Kei Joi and Rabid Squirrel! You are our winners!` — matching the plural "winners" in that example is up to how you word the template.

**The channel selector and template field are always editable** — before, during, and after a giveaway, whether or not one is even running. What happens to an edit depends on the moment:

- **No giveaway currently running** (including before you've ever started one, or after **Clear Results**): your edit is saved to the **selected preset**, exactly like editing it in the Preset Editor — it's still there next time you open Giveaways, switch presets and back, switch venues and back, or reload the plugin.
- **A giveaway is running, has completed, or was cancelled:** your edit is a one-off touch-up for *this* giveaway only (e.g. fixing a typo, or personalizing the message right before sending) — it does not change the saved preset. **Clear Results** drops this override; the field then goes back to showing the selected preset's saved channel/template.

**The Announce Winner button** stays visible at all times, but only becomes clickable once:

- the giveaway has reached **Complete** (roll acceptance has genuinely closed — during Start, active rolling, Midpoint, or Closing while rolls are still being accepted, it stays disabled), and
- there is at least one winner.

You can press it as many times as you like — announcing doesn't consume or clear the winner, so re-sending (or sending on a different channel) is fine. It disables again the moment you **Clear Results** or **Start** a new giveaway.

Pressing it sends exactly one line — `/yell <your resolved message>` or `/shout <your resolved message>` — through VenueOS's normal chat dispatch. The resolved message (after `<name>` is filled in) has to fit FFXIV's normal chat length limit; with a large tie, a long name list can push a short template over that limit. If it does, VenueOS won't truncate names, drop winners, or split the message into multiple lines — it tells you the message is too long so you can shorten the template instead.

### Rolls and the tracker

Players use FFXIV's own `/random` (or `/random 999`) — VenueOS reads the result straight from your chat. Both the host's own roll and other players' rolls (same-world or cross-world) are captured and resolved to a Name + Home World identity. One row is shown per participant, holding their best accepted roll; **Total Rolls** counts every accepted roll, and the current leader (by Highest/Lowest/Closest, as configured) is highlighted and sorted to the top automatically, with ties shown as multiple leaders rather than an arbitrary pick.

**Roll visibility is limited by FFXIV's normal `/random` message range** — a player has to be close enough to you for their roll to actually appear in your own chat. VenueOS cannot capture a roll your game client never received; this note is shown directly under the tracker as a reminder.

---

## 17. Macro

**Purpose:** a persistent, per-venue library of extended FFXIV command macros — with no 15-line limit, nested macro invocation, an action-readiness wait, and up to four faux hotbars that render on the game screen independently of the VenueOS tablet.

### Creating and editing a macro (Settings → Modules → Macro → Library)

**+ New Macro** opens a dedicated editor window (separate from the browser list, never inline):

- **Macro Name**
- An **FFXIV icon** picker — a curated, job-independent set of general game icons (the same category FFXIV's own macro editor calls "General").
- **Delay Between Lines (seconds)** — how long to wait between each line this macro sends, including fractional seconds.
- One large **Macro Body** text area — paste or type a complete multi-line macro exactly like you would into FFXIV's own macro editor. Normal editing (type, paste, Ctrl+A/C/X/V, arbitrary cursor movement) all work as expected.

**Save** validates every line and only closes/persists on success; **Cancel**, or closing the window's native X, discards the draft with no effect on the saved macro.

**Where the macro "ends":** VenueOS reads your macro body top to bottom. The **first line that is empty (or contains only whitespace)** ends the executable macro — everything after it is ignored and not saved as part of the runnable macro. If there's no blank line at all, the entire body runs. This lets you keep scratch notes below a macro in the same box without them accidentally executing.

**Line limit:** each individual line may be up to **500 UTF-8 bytes** — the real FFXIV chat/command limit, not FFXIV's own shorter 181-byte macro-editor limit — so lines a normal in-game macro couldn't hold will still work here. A line over the limit is reported by exact line number and byte count, and nothing is saved until every line fits.

### Running macros

Open Macro from Home: a status card shows **RUNNING: `<name>`** (plus a nested macro name and line progress, if applicable) with **Cancel Macro**, or "No macro running." with the outcome of the last run. Below it, a grid of large macro tiles — click one to run it immediately; tiles disable while something is already running, but Cancel always stays available.

You can also run a macro from anywhere with **`/venueos macro "Macro Name"`** — this works whether the VenueOS tablet is open or closed, and works from inside a real, built-in FFXIV macro (since a built-in macro just plays back chat/slash commands). If the Macro module is disabled, this prints a chat message telling you to enable it instead of silently doing nothing.

### Nesting and `/actionready`

A macro line that is exactly `/venueos macro "Child Name"` runs that other macro to completion (using the child's own Delay Between Lines) before returning to the parent — never sent to FFXIV as a literal command. A macro that would call itself, directly or through a chain (A→B→A, etc.), is refused immediately with a clear error rather than looping forever.

`/actionready` on its own line pauses the macro until VenueOS's game-state probe reports you're no longer busy (animation lock, casting, or actively resolving a crafting/gathering step), then applies the macro's configured delay before moving to the next line. If readiness genuinely can't be determined, the macro waits rather than guessing and advancing — it never sends the next line "just in case." This has been live-verified against a real combat ability sequence with a short Delay Between Lines.

### Faux hotbars

Up to four independent hotbar overlays can render directly on the game screen, entirely separate from the VenueOS tablet — they keep working whether the tablet or the Macro module window is open or closed. Each hotbar has:

- **Enabled** — whether it's visible at all.
- **Layout** — one of six arrangements (12×1, 6×2, 4×3, 3×4, 2×6, 1×12), all covering the same 12 logical slots.
- **Scale** and **Transparency**.
- 12 slot assignments, set from **Settings → Modules → Macro → Hotbars**: drag a macro from the palette onto a slot to assign it (dropping onto an already-assigned slot swaps the two), or click a palette icon then click a slot as a non-drag fallback. **Clear Hotbar** (confirmed) empties every slot without deleting the macros themselves.

A single global **Edit Hotbars** toggle (on the live Macro screen) switches every visible bar between **Locked** (clicking a slot runs its macro; the bar cannot be accidentally dragged) and **Editing** (a small grip strip appears above the slots — drag that strip to reposition the bar; the slots themselves are no longer part of the drag region, so a drag can never be started from on top of a slot). The faux hotbars deliberately look like part of FFXIV's own interface — a dark HUD-style panel with no VenueOS window chrome, title bar, or theme colors — rather than another VenueOS window, so they blend into the game screen.

### Persistence

The macro library and all four hotbars' configuration (enabled state, layout, slot assignments, position, scale, transparency) are saved per Venue Profile and survive a plugin reload or `/xlrestart`. Disabling the Macro module never clears any of this — re-enabling it restores exactly where you left off.

---

## 18. Shouts

**Purpose:** reusable, manually-triggered announcement presets for any venue operator — DJ introductions, event hype, requests/reminders, general venue announcements, closing messages, or any other message you want ready to fire on demand. Originally released as "DJ Shouts," generalized in this release since nothing about it is actually DJ-specific — DJ use remains a perfectly good example, just not the only one. It is **not** automatic (unlike Greeter), it is **not** ShoutRunner, and it is **not** a scheduled announcement system — the **Shout** button is the only thing that ever sends anything.

### Up to 15 Shout slots — only the ones you've configured appear

Shouts has 15 numbered slots (**Shout 1**–**Shout 15**), each independently assignable to a saved preset under **Settings → Modules → Shouts → Shout Slot Assignments**. The live Shouts screen only ever shows the slots that actually have a preset assigned — leave a slot on "(None)" in Settings and it simply never appears live, instead of cluttering the screen with 15 empty buttons. Configured slots always appear in their own numeric order (assigning slots 1, 4, 7, and 12 shows **Shout 1**, **Shout 4**, **Shout 7**, **Shout 12** in that order — they're never renumbered to 1-4), and each button shows the assigned preset's name (e.g. **Shout 4 — Requests**). Clicking a slot only **selects** it — it does not send anything by itself; only a slot you've actually assigned can be selected. If you unassign or delete the preset behind the currently selected slot, selection safely falls back to another configured slot (or clears, if none remain) — it never gets stuck on something no longer there. If every slot is unassigned, the live screen shows a plain "No Shout presets are assigned" message instead of any slot buttons, and the Shout button stays disabled. Below the slots: the **Shout** button and, to its right, the **Last Shout** timer.

### Settings (Settings → Modules → Shouts)

- **Shout Slot Assignments** — all 15 dropdowns (**Shout 1**–**Shout 15**) are always shown here (unlike the live screen, which hides unassigned ones), each assigning one saved Shout preset or "(None)".
- **Saved Presets** — a search box, a **New Shout** button, and a list of your saved presets with **Edit**/**Delete** per row. Deleting a preset (confirmed) also clears it from any of the 15 Shout slots it was assigned to.

There is deliberately no Behavior section here — no Auto Greet, no repeat-greet timer, no Command After Greeting, and no guest/target logic of any kind.

### The preset editor

**New Shout**/**Edit** opens a dedicated editor window. Each preset has:

- **Shout Name**
- Any number of ordered **lines**, each with its own text and an independent **Yell** / **Shout** channel selector directly beside it. Every newly added line defaults to **Yell**. Lines can be reordered (**Up**/**Dn**) or removed individually, and **+ Line** adds another.

**Save** validates the preset and only closes/persists on success; **Cancel** (or closing the window's X) discards whatever you were editing with no effect on the saved preset.

### Running a Shout

Select the slot you want, then press **Shout**. Every non-empty line in that preset's saved order is sent, each through its own line's channel (**Yell** → `/yell`, **Shout** → `/shout`), about two seconds apart — the same established pacing Greeter uses between its own lines, so nothing bursts into the game in a single frame. Blank lines are simply skipped.

The **Shout** button is unavailable when:
- no configured slot is selected,
- the assigned preset has no non-empty lines to send, or
- a Shout is already in progress.

### Last Shout timer

To the right of the button: **Last Shout: Never** until you've completed one, then an elapsed readout like **Last Shout: 00:42 ago**, **03:18 ago**, or **1h 12m ago**, updating live while the screen is open.

This timer resets **only** once the preset's **final** non-empty line has been confirmed actually sent — never merely from pressing the button, and never from a run that fails partway through or that you cancel. It's saved per Venue Profile, so it keeps its value across closing/reopening the module, switching to another module and back, and a plugin reload or `/xlrestart`.

### Chat length

Each line's full outgoing command (including its `/yell `/`/shout `) has to fit FFXIV's normal chat length limit, using the same byte-accurate check VenueOS uses everywhere else. An oversized line is flagged right in the editor with its exact byte count and blocks Save — VenueOS never silently truncates it.

### Upgrading from DJ Shouts (0.3.4)

If you used DJ Shouts before this release, your saved presets, your original slots 1–5 assignments, and your Last Shout timer all carry over automatically the first time this venue loads after upgrading — there is nothing to redo. The new slots 6–15 simply start unassigned, same as any other blank slot.

---

## 19. Detached Windows / Auto Pop-Out

Every module can run either **embedded** (inside the main VenueOS tablet) or **detached** (its own separate window) — the content and behavior are identical either way; it's purely a display choice.

- From an embedded module, click the pop-out icon (tooltip: "Open in separate window") in its header to detach it.
- A detached window has its own compact header: the module's icon and name on the left, a Settings gear and a Close (X) on the right. The empty middle doubles as a drag handle.
- Closing a detached window only closes that window — the module stays enabled and any of its automation keeps running.
- The **"Open modules in separate windows"** toggle in Settings → General controls what happens when you launch a module from Home: on, it opens (or focuses, if already open) detached; off, it opens embedded in the tablet. Clicking an already-open detached module's tile again just brings it to front rather than opening a second copy.
- A few modules also have their own independent auxiliary windows that are not part of this pop-out system at all — Bingo's Called Numbers/Card Viewer/Bingo Call Alert, Giveaways' tracker window, and Macro's faux hotbars all render on their own, gated only on the module being enabled, regardless of whether the module's own window or the main tablet is open.

---

## 20. Persistence

| Scope | Examples |
|---|---|
| **Global** (shared by every venue) | Which modules are enabled/disabled, Auto Pop-Out preference, the entire Mair's Editor question library |
| **Venue-specific** | ShoutRunner settings (and its recovery checkpoint for an interrupted run), Attendance settings and history (including each opening's own Venue Area Type), Greeter presets/hotbar, VIP roster, Party Finder recruitment criteria, Mair's Trivia connection settings and scoring defaults, Bingo room key and default game settings, Raffle connection settings/defaults/raffles, Brackets connection settings, Block Letters' default destination, Giveaways presets, Macro's macro library and hotbar configuration, Shouts' presets, its 15 slot assignments, and its Last Shout timestamp |
| **Runtime-only** (does not survive a reload) | ShoutRunner's on-screen terminal history, Mair's Editor's Undo history, Mair's Trivia's/Bingo's/Raffle's/Brackets' reference to "which game/room/raffle/tournament is currently open" (though the game/room/raffle/tournament itself survives on its backend and can be resumed), Block Letters' composition text, Giveaways' in-progress timeline/roll board (including any Announce Winner channel/template touch-up made while a giveaway was running — see [Giveaways](#16-giveaways)), Macro's currently-running execution state, Shouts' in-progress send state (which line it's currently on) |

Disabling a module never erases its saved configuration — re-enabling it picks back up exactly where you left off.

---

## 21. Troubleshooting

**VenueOS doesn't open by itself.** That's expected — it never opens automatically. Run `/venueos`.

**A module I expect is missing from Applications.** Check **Settings → Modules** — it's probably disabled.

**ShoutRunner won't travel between Worlds.** Confirm Lifestream is installed and working — ShoutRunner depends on it for all world travel but doesn't check for it before letting you press Start. Also check that at least one Data Center and one destination are configured, and check the Run Terminal for the actual failure reason.

**ShoutRunner shows an "Interrupted Run Available" area I don't expect.** A previous run didn't get a chance to finish cleanly (a crash, a forced close). Review the recovery summary and either **Resume Run** to continue it or **Discard Recovery** to clear it — see [ShoutRunner's Crash Recovery section](#5-shoutrunner).

**Bingo's automatic payout came back Ambiguous.** That's the automation being honest that it couldn't confirm the trade completed — it is not the same as unpaid, and never means a payout happened without confirmation. Check in-game whether the trade actually went through, then use **Mark Paid**/**Mark Not Paid** in the Payout Ledger to reconcile it manually.

**Party Finder isn't refreshing.** Check the module's status text and the Compatibility badge; on a slower system, native UI automation may simply need more time. Confirm Auto Refresh is on in Settings if you expect automatic refreshes.

**Mair's Trivia won't let me pick a question set.** Only sets marked **READY** in Mair's Editor are selectable — open the set in Mair's Editor and check its status/reasons list.

**Mair's Trivia shows a session-expired error.** Automatic recovery normally handles this silently; a visible error means both silent renewal and automatic re-login failed. Sign in again manually in Settings.

**A player doesn't show up in the game right away.** VenueOS polls the backend periodically rather than instantly — give it a moment.

**Raffle says "Unpublished Changes" and won't go away.** That's expected until you click **Publish / Update Raffle** again — any ticket/participant/setting change after your last publish sets this until you republish.

**A Raffle redraw didn't let the previous winner roll again — that's intentional.** They're marked "Excluded (previous winner)" and stay excluded across republishes until you check "Also clear previously-excluded winners on publish" and publish again.

**Brackets won't let me Delete a tournament.** Active tournaments can't be deleted directly — Cancel it first, then Delete becomes available.

**Block Letters' Copy button is disabled.** Your composition is over the selected destination's limit — switch to a longer-limit destination, or shorten the text; nothing is ever truncated automatically.

**A Giveaways roll didn't count.** Check whether the giveaway's roll window was actually open (before Start finishes sending, or after Closing's last line has gone out, rolls are ignored), whether the roller was within `/random` chat range of you, and whether they'd already used up their Allowed Rolls Per Person.

**Giveaways' Announce Winner button won't activate.** It only becomes clickable once the giveaway has reached Complete (roll acceptance genuinely closed) AND at least one winner exists — check the status line above the button for the current phase. If the resolved message (after `<name>` is filled in) is too long for chat, sending is blocked with an on-screen message instead of the button disabling — shorten the template and try again.

**Dragging a Macro tile onto a faux hotbar slot doesn't assign it.** Make sure **Edit Hotbars** is enabled first — assignment by drag only works while the bar is in Edit mode (locked bars are click-to-run only, by design, so a plain click can never accidentally move or reassign anything).

**The Shout button is disabled.** Either no configured slot is currently selected (assign a preset to a slot in Settings → Modules → Shouts, then select it live), the assigned preset has no non-empty lines, or a Shout is already sending — wait for it to finish or use **Cancel**.

Anything logged as an error or warning also appears in **Settings → Diagnostics**, filterable by module/level, with a **Copy** button if you need to share the log.

---

## 22. Data / Privacy / Credential Notes

- Mair's Trivia's Server-access password/Username/Password, Bingo's Room Key, Raffle's Backend Access Key, and Brackets' Server access password/Organizer key are all stored and displayed **in plain, readable text** by design, so venue staff can easily copy/share connection details. Don't casually share your VenueOS configuration file with people you don't want to see them.
- Mair's Trivia player/game/Series data, Bingo room/player/payout data, Raffle roster/spin data, and Brackets tournament/bracket data all live on their respective remote backends, not just locally.
- Your local VenueOS configuration otherwise contains operational data — venue names, rosters, presets, recruitment criteria, macro bodies, and similar.
- This manual makes no telemetry claims; nothing in the source reviewed for this manual indicates VenueOS phones home beyond the backends you configure yourself (Mair's Trivia, Bingo, Raffle, Brackets).

---

## 23. Updates

Once VenueOS is installed from the Experimental Plugin Repository, updates arrive the normal Dalamud way — the Plugin Installer checks configured repositories periodically (or via Settings → Experimental → "Check for Updates") and offers an update when a newer version is published. You should not need to manually replace any files for a normal release.

> **Developer note:** if you're building VenueOS from source for development, you load the built DLL directly via Dalamud's dev-plugin workflow instead — see `RELEASE.md`. That workflow is not part of the normal user update path described above.

---

## 24. Known Issues

These are documented, non-blocking caveats in the current release — none of them require the affected module to be disabled or treated as unfinished.

- **Bingo's automated payout has completed live end-to-end testing** and uses server-backed transaction tracking as its source of truth for paid/outstanding — see the [Bingo](#12-bingo) section and [Troubleshooting](#21-troubleshooting) above. An ambiguous outcome is still not the same as unpaid and always requires manual reconciliation, never an automatic retry; Mark Paid/Mark Not Paid, or a normal in-game trade, remain fully supported.

---

*See also: [RELEASE.md](../RELEASE.md) for packaging/versioning details, and [docs/RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) for the release-readiness checklist.*
