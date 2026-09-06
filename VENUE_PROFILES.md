# Venue profiles

Each venue is a `VenueProfile` with an immutable GUID, editable display name, complete theme/branding data and independently stored module payloads. Configuration is addressed by `VenueModuleConfigKey(VenueId, ModuleId, SchemaVersion)`.

Create and rename modify only registry metadata. Duplicate allocates a new GUID and JSON-deep-copies all source module payloads plus the profile theme/branding. Delete requires an explicit confirmation flag, refuses the final venue, and switches to another venue before removing the active profile. Payloads for the deleted GUID are removed only after that safe switch.

Changing active venue is atomic at the profile-store boundary. On a module transition failure, the old active profile snapshot remains in effect. Future modules must obtain configuration through `VenueProfileService.GetModuleConfig` and `SaveModuleConfig`, not store venue settings in global config.
