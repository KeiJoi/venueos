# VenueOS 0.3.14 QA — Native World Visit & Data Center Travel: Live Test Checklist

For: the owner's in-game test session. No source code needed. Mark each line **Pass / Fail / Not Tested**.
Build: `release/qa/VenueOS-0.3.14-QA.zip` (unreleased; install steps in §0).

The new travel engine is VenueOS's own. Lifestream is no longer used by ShoutRunner (leave it installed or not; it
doesn't matter).

**One switch matters:** in Settings → Modules → ShoutRunner → **Native Travel QA (diagnostic)**, the toggle **"Allow
selecting another Data Center in the World Visit window (QA, this session only)"**. It is OFF every time the plugin
loads. With it OFF, VenueOS never touches the Data Center selector. Scenarios A and B work with it off; C, D and H need
it ON. Turn it on only after scenario D1 (Inspect) looks right.

## 0. Install / roll back

**Install the QA build (local, not through the repository):**
1. Close the game, or disable VenueOS in `/xlplugins`.
2. Unzip `C:\FFXIVplugs\venueos\release\qa\VenueOS-0.3.14-QA.zip` into a new folder, e.g. `C:\FFXIVplugs\VenueOS-QA\`.
3. `/xlsettings` → Experimental → **Dev Plugin Locations** → add `C:\FFXIVplugs\VenueOS-QA\VenueOS.dll` → Save.
4. `/xlplugins` → **Dev Tools / Installed Dev Plugins** → enable the VenueOS dev plugin. Disable the
   repository-installed VenueOS first, because two copies cannot load at once.
5. Check: VenueOS Settings → About/Changelog shows **0.3.14**.

**Roll back to the published 0.3.13:** disable the dev plugin, remove the dev plugin location, then re-enable the
repository-installed VenueOS (still 0.3.13; nothing was published). Settings, venues and ShoutRunner routes are shared
and unchanged.

## How to report a failure

For every Fail, write down: what you expected; what happened; current World / DC; requested World / DC; the travel
status line in Native Travel QA; then press **Inspect (read-only)** (with the World Visit window open if relevant) and
**Copy Diagnostics**, and paste the result. Add the matching `/xllog` lines (filter "Native travel" / "ShoutRunner"),
plus steps to reproduce.

## A. Same World

| # | Step | Expected | Result |
|---|---|---|---|
| A1 | Open Native Travel QA. | Current World, Data Center (real one, e.g. Dynamis for Cuchulainn) and Home are shown. | ☐ Pass ☐ Fail ☐ Not Tested |
| A2 | Pick your current World, press Start Travel Test. | "Last result: AlreadyThere"; nothing happens in game. | ☐ Pass ☐ Fail ☐ Not Tested |
| A3 | Throughout QA, watch chat. | Native Travel QA never sends `/shout` or `/yell`. | ☐ Pass ☐ Fail ☐ Not Tested |

## B. Same Data Center (switch can stay OFF)

| # | Step | Expected | Result |
|---|---|---|---|
| B1 | Stand in Ul'dah, Gridania or Limsa near the big aetheryte (or anywhere: VenueOS teleports to the chosen city). Pick a World on your current DC (e.g. Adamantoise → Cactuar). Start Travel Test. | The aetheryte menu opens and "Visit Another World Server." is chosen. The window opens on your DC, the World is clicked, and the dialog reads exactly "Travel to Cactuar?". OK is pressed. | ☐ Pass ☐ Fail ☐ Not Tested |
| B2 | Wait. | You arrive on the chosen World; status "Arrived". Never Jenova or any other World. | ☐ Pass ☐ Fail ☐ Not Tested |
| B3 | Start from outside a city (e.g. a field zone). | VenueOS teleports to the city chosen in "World Visit city", then continues as B1. | ☐ Pass ☐ Fail ☐ Not Tested |

## D. Data Center selector (inspection first — before turning the switch on)

| # | Step | Expected | Result |
|---|---|---|---|
| D1 | Open the World Visit window by hand (aetheryte → Visit Another World Server). Press **Inspect (read-only)**. Copy Diagnostics and keep it. | "DC selector (node 2): selected N … click event owned True; items [0] Aether \| [1] … " with the four DCs; "World list (node 7) … click event owned True; rows … → World/DC" for the shown page; nothing was clicked. | ☐ Pass ☐ Fail ☐ Not Tested |
| D2 | Change the DC by hand (dropdown or ◁ ▷), Inspect again. | The selected index and the rows follow your choice. | ☐ Pass ☐ Fail ☐ Not Tested |
| D3 | Click a World by hand (don't confirm), Inspect, then Cancel the dialog. | "SelectYesno: "Travel to <World>?"" and "AgentWorldTravel: DestinationWorldId=<that World's id>". | ☐ Pass ☐ Fail ☐ Not Tested |

If D1 shows "not readable", "click event owned False" or labels that aren't the four DCs: **stop here**. Send the
diagnostics; that's exactly what needs correcting. Don't turn the switch on.

## C. Cross Data Center (switch ON)

| # | Step | Expected | Result |
|---|---|---|---|
| C1 | Turn the switch on. Aether → a Primal World. | The window opens; the selector changes to Primal; the list changes to Primal's Worlds; the World is clicked; "Travel to <World>?"; arrival on that World, Primal. No logout, no character select, no trip home. | ☐ Pass ☐ Fail ☐ Not Tested |
| C2 | Primal → a Crystal World. | Same as C1. | ☐ Pass ☐ Fail ☐ Not Tested |
| C3 | Crystal → a Dynamis World. | Same as C1. | ☐ Pass ☐ Fail ☐ Not Tested |
| C4 | Dynamis → an Aether World. | Same as C1. | ☐ Pass ☐ Fail ☐ Not Tested |
| C5 | From a visited DC (not your home) to a third DC. | Direct, without going home. | ☐ Pass ☐ Fail ☐ Not Tested |
| D4 | During C1–C4, check the dialog. | It always names the intended World; if it ever doesn't, VenueOS must close it (status "SelectionMismatch") and must never press OK. | ☐ Pass ☐ Fail ☐ Not Tested |

## E. Cancellation

| # | Step | Expected | Result |
|---|---|---|---|
| E1 | Start a test, press Cancel Travel Test as soon as the aetheryte menu opens. | Status "Cancelled"; the menus VenueOS opened close; you stay put. | ☐ Pass ☐ Fail ☐ Not Tested |
| E2 | Cancel while the World Visit window is open (before the dialog). | Same as E1; no dialog confirmed. | ☐ Pass ☐ Fail ☐ Not Tested |
| E3 | Cancel after OK was pressed (queue/loading). | Status "Cancelled … the game may still complete it"; VenueOS touches nothing; the game may still move you. | ☐ Pass ☐ Fail ☐ Not Tested |

## G. Congestion (only if it happens naturally)

| # | Step | Expected | Result |
|---|---|---|---|
| G1 | Target a World shown as unavailable/congested. | Status "WorldUnavailable" (or "Congested"); nothing clicked on that row. Take an Inspect of the window first: how the row reads is unverified. | ☐ Pass ☐ Fail ☐ Not Tested |
| G2 | In a ShoutRunner route, a congested World. | Only that World is SKIPPED; the rest of its DC continues. | ☐ Pass ☐ Fail ☐ Not Tested |

## H. Full ShoutRunner route (switch ON; a short message you're happy to shout)

| # | Step | Expected | Result |
|---|---|---|---|
| H1 | Select 2–4 Data Centers, start a run. | DCs in the fixed order (Aether, Crystal, Dynamis, Primal); every World once; three shouts per World. | ☐ Pass ☐ Fail ☐ Not Tested |
| H2 | Watch DC boundaries. | The selector switches DC in the World Visit window; no logout, no character select, no trip home. | ☐ Pass ☐ Fail ☐ Not Tested |
| H3 | Terminal / Diagnostics. | No "SKIPPED" except genuine congestion; any failure has a Diagnostics entry with the full travel trace. | ☐ Pass ☐ Fail ☐ Not Tested |
| H4 | Switch OFF, run a two-DC route. | Your current DC runs; the other is "SKIPPED: … awaiting live verification …", with nothing clicked for it. | ☐ Pass ☐ Fail ☐ Not Tested |

## F. Recovery

| # | Step | Expected | Result |
|---|---|---|---|
| F1 | Stop during travel (before OK). | Travel cancelled, menus closed, Stopped; nothing shouted. | ☐ Pass ☐ Fail ☐ Not Tested |
| F2 | Stop, then Start again. | A fresh run from the first World. | ☐ Pass ☐ Fail ☐ Not Tested |
| F3 | `/xlreload` mid-route, then Resume Run. | Continues at the unfinished World; completed destinations are not shouted again. | ☐ Pass ☐ Fail ☐ Not Tested |
| F4 | Run a QA test while ShoutRunner is running. | QA refuses ("ShoutRunner is running — stop it first."). | ☐ Pass ☐ Fail ☐ Not Tested |

## I. Announcement regression (after travel QA)

| # | Step | Expected | Result |
|---|---|---|---|
| I1 | Line 1 only. | One shout per destination. | ☐ Pass ☐ Fail ☐ Not Tested |
| I2 | Line 1 + Line 2. | Line 2 about 2 s after Line 1, in order. | ☐ Pass ☐ Fail ☐ Not Tested |
| I3 | Travel by hand to another World between destinations. | The next shout is withheld ("… withheld …"). | ☐ Pass ☐ Fail ☐ Not Tested |
| I4 | Stop and Resume. | No repeated shouts; the run continues where it stopped. | ☐ Pass ☐ Fail ☐ Not Tested |

Live QA status for this build: **Pending — owner testing.** Publication requires a separate, explicit authorization.
