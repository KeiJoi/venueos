# VenueOS architecture

`VenueOS.Plugin` is the Dalamud composition root and shell. `VenueOS.Core` owns stable module metadata, lifecycle, dependency ordering, enabled state, failure reporting, and update dispatch. `VenueOS.Venues` owns profiles, themes, module-keyed payloads, persistence snapshots and transactional switching. `VenueOS.Services` provides testable clock/scheduler, paced chat command dispatch, game context, presence snapshots, notifications and typed HTTP construction. `VenueOS.UI` exposes semantic component styles and shell state; modules must not hard-code venue colors.

The runtime transition is validate destination, persist destination as active, notify enabled modules in dependency order, then publish the new active profile. A notification failure restores the previous snapshot and returns a clear failure result. Tick failures are isolated and reported, so one future module cannot take down the shell.

The plugin stores `VenueStoreSnapshot` through Dalamud plugin configuration. It contains profile registry, active GUID and serialized module payloads—never a central DTO containing module-specific fields.
