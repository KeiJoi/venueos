# Phase 2B: Attendance, Greeter, and VIP

The three operational modules use the one `PresenceService`; none owns an object-table scan. The plugin's object snapshot adapter uses character name + home world and falls back to current world only when home-world data is unavailable, matching the audited source behavior.

`core.attendance` subscribes first, keeps the current guest list, arrival/departure history, and an operator-controlled session. `core.greeter` owns greeting queue uniqueness and greeted state, with five per-venue preset slots and an active DJ index. `core.vip` matches only enabled `Name + HomeWorld` records and does not maintain greeted state.

The arrival ordering is: Presence/Attendance -> VIP private tell (when matched) -> Greeter active-preset tells -> VIP public shout/yell -> Greeter greeted state. VIP keeps a per-session processed key set, so repeated scans/events do not duplicate the recognition sequence. Switching venue clears pending Greeter/VIP runtime state before loading the destination's configuration.

The real game UI remains deliberately basic pending the later visual pass. Manual in-game validation is still required for `/tell`, `/shout`, `/yell`, game object arrival/departure, preset switching, and chat rate behavior.
