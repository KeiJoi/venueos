# VenueOS User Manual

**Current version:** 0.1.0
**What it is:** VenueOS is a Dalamud plugin for Final Fantasy XIV — a single tablet-style operations console for running an in-game venue: attendance tracking, automatic guest greeting, VIP recognition, promotional shout routes, Party Finder recruitment, and live host trivia.
**Supported environment:** Windows FFXIV with Dalamud installed (API level 15). VenueOS is unofficial, third-party, and not affiliated with Square Enix or the Dalamud/XIVLauncher project.

**Working modules covered in this manual:** ShoutRunner, Attendance, Greeter, VIP, Party Finder, Mair's Trivia, Mair's Editor.

**Included but Under Development (disabled by default):** Bingo, Raffle, TournamentControl. See [Under Development Modules](#under-development-modules) — do not expect these to work yet.

This manual describes the current release only. It does not describe planned features, and it does not describe how any donor/standalone plugin VenueOS was built from used to behave where VenueOS now differs.

---

## Quick Start

1. Install VenueOS through Dalamud's Experimental Plugin Repository (see [Installation](#1-installation)).
2. Open it with `/venueos` (it does not open automatically on login).
3. Go to **Settings → Venue** and create or select a Venue Profile.
4. Check **Settings → Modules** — enable/disable modules as needed. Bingo, Raffle, and TournamentControl are off by default and marked "Under Development."
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

A **Venue Profile** represents one venue identity: a name and a theme, plus whichever module settings are stored per-venue (most of them are — see the [Persistence](#13-persistence) table).

Manage venues under **Settings → Venue**:

- **+ Add Venue** — creates a new profile and switches to it immediately.
- Each row shows the venue's name, its theme, and (if it's the active one) an **Active Venue** badge.
- **Switch to** — switches to that venue (only shown on non-active rows).
- **Rename** — inline rename with **Save**/**Cancel**.
- **Duplicate** — copies the venue (including its module configuration) under a new "... copy" name.
- **Delete** — permanently deletes the venue and all of its module configuration. Disabled if it's the only remaining venue.

You can also switch venues quickly from the toolbar's dropdown, without going into Settings.

**Switching away from an active Mair's Trivia game:** if Mair's Trivia has a game running in the venue you're leaving, switching shows a confirmation: *"Mair's Trivia has an active game ("<game name>") running in this venue. Switching venues will end this game for all connected players. This will not end the Series it may belong to."* Confirming ends that game (but never the Series it's part of, which stays resumable). No other module currently has a venue-switch warning.

**Global vs. venue-specific:** module enabled/disabled state, the Auto Pop-Out preference, and the Mair's Editor question library are global (shared across every venue). Everything else module-specific — connection settings, presets, rosters, recruitment criteria — is per-venue. See [Persistence](#13-persistence) for the full breakdown.

---

## 4. Global Settings

Found under **Settings → General**:

- **"Open modules in separate windows"** toggle (Auto Pop-Out). Off by default. When on, launching a module from Home opens (or focuses) its own detached window instead of embedding it in the tablet. This applies the same way for every venue and doesn't change when you switch venues. Settings itself is unaffected by this toggle and always opens embedded.
- An **About VenueOS** card showing a version line and a one-line description of what VenueOS is. Note: this in-app version line currently reads "VenueOS Phase 4" rather than the packaged release number (0.1.0) — it's an internal label that hasn't been updated to match the public release numbering yet, so don't rely on it to confirm which release you have installed; check the Dalamud plugin installer instead.

**Settings → Modules** is where you enable/disable modules and jump into each one's own settings — see [Basics](#2-venueos-basics) above and [Under Development Modules](#under-development-modules) below.

**Settings → Diagnostics** shows a filterable log of recent errors and configuration-recovery warnings (filters: **Log Level** — All Levels/Errors/Warnings, **Module**, and a **Search** box), plus **Clear** and **Copy** buttons and small stat tiles (Total Entries, Errors, Warnings, Last Update). Useful for troubleshooting — see [Troubleshooting](#14-troubleshooting).

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

---

## 6. Attendance

**Purpose:** tracks who's present in the venue during an "opening" (a session) and records visit/greeted history.

Guests are detected by presence (a periodic scan of nearby players), not by chat or manual entry.

### Tabs: Live, Visitors, History, Analytics

**Live** — a session card shows **Open**/**Closed** status:
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
- **Presence Filtering:** "Lock to the territory the session starts in" toggle, "Filter by distance" toggle, "Venue area type" (Normal venue area / Outdoor Event Area), "Radius (yalms)" field, "Stats poll interval (seconds)" field.

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

All four fields are plain, visible, readable text — none are password-masked. This is a deliberate product decision so venue staff can copy/share connection configuration easily. Treat your VenueOS configuration accordingly (see [Data / Privacy notes](#15-data--privacy--credential-notes)).

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

## 12. Detached Windows / Auto Pop-Out

Every module can run either **embedded** (inside the main VenueOS tablet) or **detached** (its own separate window) — the content and behavior are identical either way; it's purely a display choice.

- From an embedded module, click the pop-out icon (tooltip: "Open in separate window") in its header to detach it.
- A detached window has its own compact header: the module's icon and name on the left, a Settings gear and a Close (X) on the right. The empty middle doubles as a drag handle.
- Closing a detached window only closes that window — the module stays enabled and any of its automation keeps running.
- The **"Open modules in separate windows"** toggle in Settings → General controls what happens when you launch a module from Home: on, it opens (or focuses, if already open) detached; off, it opens embedded in the tablet. Clicking an already-open detached module's tile again just brings it to front rather than opening a second copy.

---

## 13. Persistence

| Scope | Examples |
|---|---|
| **Global** (shared by every venue) | Which modules are enabled/disabled, Auto Pop-Out preference, the entire Mair's Editor question library |
| **Venue-specific** | ShoutRunner settings, Attendance settings and history, Greeter presets/hotbar, VIP roster, Party Finder recruitment criteria, Mair's Trivia connection settings and scoring defaults |
| **Runtime-only** (does not survive a reload) | ShoutRunner's on-screen terminal history, Mair's Editor's Undo history, Mair's Trivia's reference to "which game is currently open" (though the game itself survives on the backend and can be resumed) |

Disabling a module never erases its saved configuration — re-enabling it picks back up exactly where you left off.

---

## 14. Troubleshooting

**VenueOS doesn't open by itself.** That's expected — it never opens automatically. Run `/venueos`.

**A module I expect is missing from Applications.** Check **Settings → Modules** — it's probably disabled. Bingo, Raffle, and TournamentControl are disabled by default on a fresh install (see [below](#under-development-modules)).

**ShoutRunner won't travel between Worlds.** Confirm Lifestream is installed and working — ShoutRunner depends on it for all world travel but doesn't check for it before letting you press Start. Also check that at least one Data Center and one destination are configured, and check the Run Terminal for the actual failure reason.

**Party Finder isn't refreshing.** Check the module's status text and the Compatibility badge; on a slower system, native UI automation may simply need more time. Confirm Auto Refresh is on in Settings if you expect automatic refreshes.

**Mair's Trivia won't let me pick a question set.** Only sets marked **READY** in Mair's Editor are selectable — open the set in Mair's Editor and check its status/reasons list.

**Mair's Trivia shows a session-expired error.** Automatic recovery normally handles this silently; a visible error means both silent renewal and automatic re-login failed. Sign in again manually in Settings.

**A player doesn't show up in the game right away.** VenueOS polls the backend periodically rather than instantly — give it a moment.

Anything logged as an error or warning also appears in **Settings → Diagnostics**, filterable by module/level, with a **Copy** button if you need to share the log.

---

## 15. Data / Privacy / Credential Notes

- Mair's Trivia's Server-access password, Username, and Password are stored and displayed **in plain, readable text** by design, so venue staff can easily copy/share connection details. Don't casually share your VenueOS configuration file with people you don't want to see them.
- Mair's Trivia player/game/Series data lives on that remote backend, not just locally.
- Your local VenueOS configuration otherwise contains operational data — venue names, rosters, presets, recruitment criteria, and similar.
- This manual makes no telemetry claims; nothing in the source reviewed for this manual indicates VenueOS phones home beyond the Mair's Trivia backend you configure yourself.

---

## 16. Updates

Once VenueOS is installed from the Experimental Plugin Repository, updates arrive the normal Dalamud way — the Plugin Installer checks configured repositories periodically (or via Settings → Experimental → "Check for Updates") and offers an update when a newer version is published. You should not need to manually replace any files for a normal release.

> **Developer note:** if you're building VenueOS from source for development, you load the built DLL directly via Dalamud's dev-plugin workflow instead — see `RELEASE.md`. That workflow is not part of the normal user update path described above.

---

## Under Development Modules

**Bingo, Raffle, and TournamentControl** exist in this release but are **not ready for use**:

- They ship **disabled by default** on a fresh install.
- Because they're disabled, they do **not** appear on the Home/Applications screen.
- They still show up in **Settings → Modules**, clearly labeled **"Under Development"**, so you can see they exist.
- They are not covered by this manual's operational instructions and should not be treated as functional features of this release.

---

*See also: [RELEASE.md](../RELEASE.md) for packaging/versioning details, and [docs/RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) for the release-readiness checklist.*
