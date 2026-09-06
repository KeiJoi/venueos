# Phase 2B persistence decision

VenueOS does not import `Microsoft.Data.Sqlite`, `SQLitePCLRaw.lib.e_sqlite3`, or the advisory-affected `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 dependency used by the source plugin. Attendance/Greeter/VIP settings and small venue-session data are stored as JSON module payloads through the existing per-venue `VenueProfileService` store.

This is intentionally a clean replacement for Phase 2B: it avoids shipping a native SQLite bundle, keeps records profile-scoped, and gives module DTOs schema-versioned ownership. It is suitable for the modest operator history currently implemented. A future high-volume reporting feature may adopt a maintained database layer only after dependency/security review, explicit migration design, backup strategy, and tests.
