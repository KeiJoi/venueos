using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace VenueOS.Services;

/// <summary>Result of recording a guest's arrival for the night — <see cref="IsFirstVisitTonight"/> drives
/// Greeter's "Auto Greet First Visit Tonight" (donor: <c>VisitorPresenceChange</c>/<c>MarkVisitorPresent</c> in
/// <c>venuestatusandgreet</c>'s <c>DatabaseService</c>). Scoped to the calendar night, not the current session, so a
/// guest who already visited earlier the same night (even in a since-closed opening) is not "first visit" again.</summary>
public readonly record struct VisitorPresenceChange(bool BecamePresent, bool IsFirstVisitTonight);

/// <summary>One venue "opening" — donor terminology preserved (<c>VenueSessionEntry</c>). <see cref="IsResumable"/>
/// mirrors the donor's Start New Opening / Pause / Resume Selected / Close Opening lifecycle: pausing never sets
/// <see cref="ClosedAtUtc"/>, so a paused opening remains selectable from history and resumable; only Close does.
/// <see cref="AreaMode"/>/<see cref="TerritoryLock"/>/<see cref="FixedCenter"/> are a live-verified product addition:
/// the Venue Area Type an opening actually used is snapshotted onto the opening itself at
/// <see cref="IVenueDatabase.StartSession"/> time, so history keeps the *real* mode a past opening ran under and
/// <see cref="IVenueDatabase.ResumeSession"/> callers can restore it exactly (including an Outdoor opening's fixed
/// origin) instead of re-deriving it from whatever the operator's current settings/position happen to be.</summary>
public sealed record AttendanceSessionRecord(long SessionId, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc, DateOnly NightDate, PresenceAreaMode AreaMode = PresenceAreaMode.FollowOperator, uint? TerritoryLock = null, Vector3? FixedCenter = null)
{
    public bool IsResumable => ClosedAtUtc is null;
}

/// <summary>Donor: <c>NightSummary</c>. Definitions preserved exactly — Max/Min come from recorded guest-count
/// samples (falling back to the current guest count if no sample exists yet), Total/Unique/AverageGuestTime come
/// from the visitor-stats aggregate, not re-derived differently.</summary>
public sealed record AttendanceNightSummary(DateOnly NightDate, int CurrentGuests, int MaxGuests, int MinGuests, int UniqueGuests, int TotalVisits, TimeSpan TotalGuestTime)
{
    public TimeSpan AverageGuestTime => UniqueGuests == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(TotalGuestTime.TotalSeconds / UniqueGuests);
}

/// <summary>Donor: <c>VisitorNightSummary</c> (tonight/session-scoped) and <c>VisitorRangeRow</c> (historical range)
/// merged into one shape — a presentation simplification, not a capability loss: every field either source exposed
/// is still here, range queries just leave <see cref="IsPresent"/> false (a past night's visitor isn't "present" now).</summary>
public sealed record AttendanceVisitorRecord(DateOnly NightDate, string CharacterName, string HomeWorld, int Visits, TimeSpan TotalTime, bool IsPresent, bool Greeted)
{
    public GuestIdentity Identity => new(CharacterName, HomeWorld);
}

/// <summary>Donor: <c>DailyStatRow</c> — one row per calendar night, used for the Max/Min comparison chart/table and export.</summary>
public sealed record AttendanceDailyStat(DateOnly NightDate, int MaxGuests, int MinGuests, int UniqueGuests, int TotalVisits, TimeSpan TotalGuestTime);

/// <summary>Donor: <c>GuestSampleRow</c> — one 5-minute-bucketed guest-count sample.</summary>
public sealed record AttendanceGuestSample(DateTimeOffset SampleAtUtc, int GuestCount, DateOnly NightDate);

/// <summary>Donor: <c>GreetPreset</c>, now persisted with an identity so it can live in a library independent of
/// the five hotbar slots (donor: <c>greet_presets</c> + <c>hotbar_slots</c>). Line4 is a raw command, never a
/// fourth tell — preserved from the donor's exact semantics.</summary>
public sealed record GreetPresetRecord(long Id, string Name, string Line1, string Line2, string Line3, string Command)
{
    public IReadOnlyList<string> MessageLines => new[] { Line1, Line2, Line3 }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    public bool HasActions => MessageLines.Count > 0 || !string.IsNullOrWhiteSpace(Command);
}

/// <summary>Persistence for Attendance's session/night/visitor/sample history and Greeter's saved-preset
/// library/hotbar assignments — one store because the donor kept both in a single SQLite database
/// (<c>venuestatusandgreet</c>'s <c>DatabaseService</c>), and there's no reason for VenueOS to split them.
/// Every method takes a venue id: the donor was single-venue, so every table here adds venue scoping the donor
/// never needed. A "night" is a calendar date (local time) — potentially spanning several openings if the venue is
/// closed and reopened the same night; a "session" is one specific opening, independently pausable/resumable/closable.</summary>
public interface IVenueDatabase
{
    long StartSession(Guid venueId, DateTimeOffset openedAt, PresenceAreaMode areaMode, uint? territoryLock, Vector3? fixedCenter);
    bool ResumeSession(Guid venueId, long sessionId);
    bool PauseSession(Guid venueId, long sessionId, DateTimeOffset at);
    bool CloseSession(Guid venueId, long sessionId, DateTimeOffset at);
    bool DeleteSession(Guid venueId, long sessionId);
    IReadOnlyList<AttendanceSessionRecord> GetRecentSessions(Guid venueId, int maxRows = 100);
    /// <summary>One specific opening's own record, including its snapshotted Venue Area Type/territory lock/fixed
    /// origin — the lookup Attendance's own <c>ResumeSession</c> uses to restore exactly what that opening started
    /// with. Null if it doesn't exist for this venue.</summary>
    AttendanceSessionRecord? GetSession(Guid venueId, long sessionId);

    VisitorPresenceChange MarkPresent(Guid venueId, long sessionId, GuestIdentity guest, DateTimeOffset at);
    void MarkAbsent(Guid venueId, long sessionId, GuestIdentity guest, DateTimeOffset at);
    void MarkGreeted(Guid venueId, long sessionId, GuestIdentity guest, bool greeted, DateTimeOffset at);
    void RecordSample(Guid venueId, long sessionId, DateTimeOffset at, int guestCount);

    AttendanceNightSummary GetSessionSummary(Guid venueId, long sessionId);
    IReadOnlyList<AttendanceVisitorRecord> GetSessionVisitors(Guid venueId, long sessionId);
    IReadOnlyList<AttendanceGuestSample> GetSessionSamples(Guid venueId, long sessionId, int maxRows = 288);

    IReadOnlyList<AttendanceDailyStat> GetDailyStats(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive);
    IReadOnlyList<AttendanceVisitorRecord> GetVisitorsForRange(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive);
    IReadOnlyList<AttendanceGuestSample> GetSamplesForRange(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive);

    IReadOnlyList<GreetPresetRecord> GetPresets(Guid venueId);
    GreetPresetRecord? GetPreset(Guid venueId, long presetId);
    long SavePreset(Guid venueId, long? presetId, string name, string line1, string line2, string line3, string command);
    void DeletePreset(Guid venueId, long presetId);
    IReadOnlyDictionary<int, long?> GetHotbarAssignments(Guid venueId);
    void SetHotbarAssignment(Guid venueId, int slot, long? presetId);
}

/// <summary>Real persistence, backed by a single long-lived <see cref="SqliteConnection"/> (kept open for the
/// store's lifetime rather than one connection per call as the donor did) so the same connection string works for
/// production (a file path) and tests (<c>Data Source=:memory:</c>) alike — an in-memory database only survives as
/// long as its one connection stays open. Uses Microsoft.Data.Sqlite 10.x (donor was pinned to the older,
/// advisory-flagged 9.0.3) and ClosedXML 0.105.x for export, both current as of this reconstruction pass.</summary>
public sealed class SqliteVenueDatabase : IVenueDatabase, IDisposable
{
    private readonly object sync = new();
    private readonly SqliteConnection connection;

    public SqliteVenueDatabase(string connectionString)
    {
        connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var journal = connection.CreateCommand()) { journal.CommandText = "PRAGMA journal_mode = WAL;"; journal.ExecuteNonQuery(); }
        // Migrating away from greet_presets' old UNIQUE(venue_id, name) constraint (see the migration method's own
        // doc comment) needs a table rename with an incoming foreign-key reference from hotbar_slots — SQLite's own
        // guidance for that is to run it with foreign-key enforcement off, before turning it on for normal operation.
        MigrateGreetPresetsDropUniqueName();
        using (var keys = connection.CreateCommand()) { keys.CommandText = "PRAGMA foreign_keys = ON;"; keys.ExecuteNonQuery(); }
        EnsureSchema();
        RepairHotbarSlotsForeignKeyIfStale();
        MigrateVenueSessionsAddAreaColumns();
    }

    public void Dispose() => connection.Dispose();

    private void EnsureSchema()
    {
        using var transaction = connection.BeginTransaction();
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS venue_nights (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                venue_id TEXT NOT NULL,
                night_date_local TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(venue_id, night_date_local)
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS visitor_night_stats (
                night_id INTEGER NOT NULL REFERENCES venue_nights(id) ON DELETE CASCADE,
                character_name TEXT NOT NULL,
                home_world TEXT NOT NULL,
                visits INTEGER NOT NULL DEFAULT 0,
                total_seconds INTEGER NOT NULL DEFAULT 0,
                last_visit_start_utc TEXT NULL,
                greeted INTEGER NOT NULL DEFAULT 0,
                currently_present INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (night_id, character_name, home_world)
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS guest_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                night_id INTEGER NOT NULL REFERENCES venue_nights(id) ON DELETE CASCADE,
                sample_time_utc TEXT NOT NULL,
                guest_count INTEGER NOT NULL
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS venue_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                venue_id TEXT NOT NULL,
                night_id INTEGER NOT NULL REFERENCES venue_nights(id) ON DELETE CASCADE,
                opened_at_utc TEXT NOT NULL,
                closed_at_utc TEXT NULL,
                area_mode INTEGER NOT NULL DEFAULT 0,
                territory_lock INTEGER NULL,
                fixed_center_x REAL NULL,
                fixed_center_y REAL NULL,
                fixed_center_z REAL NULL
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS session_visitors (
                session_id INTEGER NOT NULL REFERENCES venue_sessions(id) ON DELETE CASCADE,
                character_name TEXT NOT NULL,
                home_world TEXT NOT NULL,
                visits INTEGER NOT NULL DEFAULT 0,
                total_seconds INTEGER NOT NULL DEFAULT 0,
                last_visit_start_utc TEXT NULL,
                greeted INTEGER NOT NULL DEFAULT 0,
                currently_present INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (session_id, character_name, home_world)
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS session_guest_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL REFERENCES venue_sessions(id) ON DELETE CASCADE,
                sample_time_utc TEXT NOT NULL,
                guest_count INTEGER NOT NULL
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS greet_presets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                venue_id TEXT NOT NULL,
                name TEXT NOT NULL,
                line1 TEXT NOT NULL DEFAULT '',
                line2 TEXT NOT NULL DEFAULT '',
                line3 TEXT NOT NULL DEFAULT '',
                command TEXT NOT NULL DEFAULT ''
            );
            """);
        Exec(transaction, """
            CREATE TABLE IF NOT EXISTS hotbar_slots (
                venue_id TEXT NOT NULL,
                slot INTEGER NOT NULL,
                preset_id INTEGER NULL REFERENCES greet_presets(id) ON DELETE SET NULL,
                PRIMARY KEY (venue_id, slot)
            );
            """);
        transaction.Commit();
    }

    /// <summary>One-time migration for databases created before this fix: <c>greet_presets</c> originally had a
    /// <c>UNIQUE(venue_id, name)</c> constraint, but <see cref="SavePreset"/>'s insert path never handled that
    /// conflict — saving a second preset with a name already used in the same venue threw an unhandled
    /// <c>SqliteException</c> straight through the Greeter Settings UI (caught by <c>UiKit.SafeDraw</c>, which
    /// then showed an error card in place of the entire preset library/editor — a real "the library doesn't work"
    /// symptom with a database bug as its root cause). The preset's <c>Id</c> is the real stable identity; nothing
    /// requires names to be unique, so the constraint is dropped rather than worked around. <c>CREATE TABLE IF NOT
    /// EXISTS</c> in <see cref="EnsureSchema"/> is a no-op against an existing table, so this migration — run before
    /// <see cref="EnsureSchema"/>, with foreign keys off per SQLite's own guidance for renaming a table that has an
    /// incoming reference (<c>hotbar_slots.preset_id</c>) — is what actually removes the constraint for anyone who
    /// already ran an earlier build. A no-op for a fresh database (no <c>greet_presets</c> table yet).</summary>
    private void MigrateGreetPresetsDropUniqueName()
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'greet_presets';";
            if (check.ExecuteScalar() is not string sql || !sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return;
        }

        using (var keysOff = connection.CreateCommand()) { keysOff.CommandText = "PRAGMA foreign_keys = OFF;"; keysOff.ExecuteNonQuery(); }
        using (var transaction = connection.BeginTransaction())
        {
            Exec(transaction, "ALTER TABLE greet_presets RENAME TO greet_presets_pre_unique_fix;");
            Exec(transaction, """
                CREATE TABLE greet_presets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    venue_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    line1 TEXT NOT NULL DEFAULT '',
                    line2 TEXT NOT NULL DEFAULT '',
                    line3 TEXT NOT NULL DEFAULT '',
                    command TEXT NOT NULL DEFAULT ''
                );
                """);
            Exec(transaction, "INSERT INTO greet_presets (id, venue_id, name, line1, line2, line3, command) SELECT id, venue_id, name, line1, line2, line3, command FROM greet_presets_pre_unique_fix;");
            Exec(transaction, "DROP TABLE greet_presets_pre_unique_fix;");
            transaction.Commit();
        }
    }

    /// <summary>Repairs a real, confirmed defect in <see cref="MigrateGreetPresetsDropUniqueName"/>: contrary to
    /// that method's own doc comment (and contrary to what "foreign keys off" was assumed to prevent),
    /// <c>ALTER TABLE greet_presets RENAME TO greet_presets_pre_unique_fix</c> rewrites <c>hotbar_slots</c>'s stored
    /// foreign key definition to point at the *new* name regardless of the <c>foreign_keys</c> pragma state —
    /// verified directly against the exact Microsoft.Data.Sqlite version this project uses, not assumed. Once the
    /// temporary table is dropped moments later, <c>hotbar_slots</c> is left referencing a table that no longer
    /// exists, so any future write to it throws <c>SQLite Error 1: 'no such table: main.greet_presets_pre_unique_fix'</c>
    /// — the exact symptom this method fixes. <see cref="MigrateGreetPresetsDropUniqueName"/>'s own guard (checking
    /// whether <c>greet_presets</c>'s stored schema still contains "UNIQUE") only detects whether *that* migration
    /// itself still needs to run; it says nothing about whether an *earlier* run of it already left
    /// <c>hotbar_slots</c> broken, so a database that already hit this bug once would never self-heal on a later
    /// launch. This check is independent of that guard, runs unconditionally on every construction after
    /// <see cref="EnsureSchema"/> (so <c>hotbar_slots</c> is guaranteed to exist in some form), and is a no-op
    /// (confirmed by test) once <c>hotbar_slots</c> already references <c>greet_presets</c> correctly — safe to run
    /// on every plugin load, including a brand-new database that never needed the legacy migration at all.
    /// Preserves every existing row: only a <c>preset_id</c> that no longer corresponds to any row in
    /// <c>greet_presets</c> (a dangling/invalid reference) is cleared to <c>NULL</c>; the slot assignment row itself,
    /// and every valid <c>preset_id</c>, survive untouched.</summary>
    private void RepairHotbarSlotsForeignKeyIfStale()
    {
        var needsRepair = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_list(hotbar_slots);";
            using var reader = check.ExecuteReader();
            // Column 2 ("table") is the parent table the foreign key currently targets.
            while (reader.Read()) if (!string.Equals(reader.GetString(2), "greet_presets", StringComparison.Ordinal)) needsRepair = true;
        }
        if (!needsRepair) return;

        using (var keysOff = connection.CreateCommand()) { keysOff.CommandText = "PRAGMA foreign_keys = OFF;"; keysOff.ExecuteNonQuery(); }
        using (var transaction = connection.BeginTransaction())
        {
            // Built under a fresh name (never renamed into or out of) so this repair itself cannot trigger the same
            // rename-rewrite behavior on any other table — nothing else has a foreign key referencing hotbar_slots.
            Exec(transaction, """
                CREATE TABLE hotbar_slots_fk_repair (
                    venue_id TEXT NOT NULL,
                    slot INTEGER NOT NULL,
                    preset_id INTEGER NULL REFERENCES greet_presets(id) ON DELETE SET NULL,
                    PRIMARY KEY (venue_id, slot)
                );
                """);
            Exec(transaction, """
                INSERT INTO hotbar_slots_fk_repair (venue_id, slot, preset_id)
                SELECT venue_id, slot,
                       CASE WHEN preset_id IS NOT NULL AND EXISTS (SELECT 1 FROM greet_presets gp WHERE gp.id = hotbar_slots.preset_id)
                            THEN preset_id ELSE NULL END
                FROM hotbar_slots;
                """);
            Exec(transaction, "DROP TABLE hotbar_slots;");
            Exec(transaction, "ALTER TABLE hotbar_slots_fk_repair RENAME TO hotbar_slots;");
            transaction.Commit();
        }
        using (var keysOn = connection.CreateCommand()) { keysOn.CommandText = "PRAGMA foreign_keys = ON;"; keysOn.ExecuteNonQuery(); }
    }

    /// <summary>Live-verified product addition: Venue Area Type (and, for Outdoor, the fixed origin) moved from a
    /// mutable global setting to a value snapshotted per-opening, so <c>venue_sessions</c> needs columns to hold it.
    /// <c>CREATE TABLE IF NOT EXISTS</c> in <see cref="EnsureSchema"/> already includes them for a brand-new
    /// database, but is a no-op against an existing table from before this column existed — this idempotently adds
    /// them via <c>ALTER TABLE ... ADD COLUMN</c> (checked against <c>PRAGMA table_info</c> first, and skipped
    /// entirely if already present, so it's safe to run unconditionally on every launch) without touching a single
    /// existing row: every historical opening keeps its actual data, and simply reads back as
    /// Normal/no-territory-lock/no-fixed-center (the safe default) for this one previously-unrecorded dimension,
    /// since that's genuinely all that's recoverable for openings that predate this feature.</summary>
    private void MigrateVenueSessionsAddAreaColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(venue_sessions);";
            using var reader = check.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1)); // column 1 is "name"
        }
        void AddColumnIfMissing(string name, string definition)
        {
            if (existing.Contains(name)) return;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE venue_sessions ADD COLUMN {name} {definition};";
            alter.ExecuteNonQuery();
        }
        AddColumnIfMissing("area_mode", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("territory_lock", "INTEGER NULL");
        AddColumnIfMissing("fixed_center_x", "REAL NULL");
        AddColumnIfMissing("fixed_center_y", "REAL NULL");
        AddColumnIfMissing("fixed_center_z", "REAL NULL");
    }

    // ---- Sessions / nights ----------------------------------------------------------------

    public long StartSession(Guid venueId, DateTimeOffset openedAt, PresenceAreaMode areaMode, uint? territoryLock, Vector3? fixedCenter)
    {
        lock (sync)
        {
            var nightId = EnsureNight(venueId, openedAt);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO venue_sessions (venue_id, night_id, opened_at_utc, area_mode, territory_lock, fixed_center_x, fixed_center_y, fixed_center_z)
                VALUES (@venue, @night, @opened, @areaMode, @territoryLock, @fixedX, @fixedY, @fixedZ);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@venue", venueId.ToString());
            insert.Parameters.AddWithValue("@night", nightId);
            insert.Parameters.AddWithValue("@opened", Iso(openedAt));
            insert.Parameters.AddWithValue("@areaMode", (int)areaMode);
            insert.Parameters.AddWithValue("@territoryLock", (object?)territoryLock ?? DBNull.Value);
            insert.Parameters.AddWithValue("@fixedX", (object?)fixedCenter?.X ?? DBNull.Value);
            insert.Parameters.AddWithValue("@fixedY", (object?)fixedCenter?.Y ?? DBNull.Value);
            insert.Parameters.AddWithValue("@fixedZ", (object?)fixedCenter?.Z ?? DBNull.Value);
            return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public bool ResumeSession(Guid venueId, long sessionId)
    {
        lock (sync)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE venue_sessions SET closed_at_utc = NULL WHERE id = @id AND venue_id = @venue AND closed_at_utc IS NULL;";
            update.Parameters.AddWithValue("@id", sessionId);
            update.Parameters.AddWithValue("@venue", venueId.ToString());
            return update.ExecuteNonQuery() > 0 || SessionExists(venueId, sessionId, requireResumable: true);
        }
    }

    public bool PauseSession(Guid venueId, long sessionId, DateTimeOffset at)
    {
        lock (sync)
        {
            if (!SessionExists(venueId, sessionId, requireResumable: true)) return false;
            CloseAllPresentForSession(sessionId, at);
            CloseAllPresentForNight(GetNightId(sessionId), at);
            return true;
        }
    }

    public bool CloseSession(Guid venueId, long sessionId, DateTimeOffset at)
    {
        lock (sync)
        {
            if (!SessionExists(venueId, sessionId, requireResumable: false)) return false;
            CloseAllPresentForSession(sessionId, at);
            CloseAllPresentForNight(GetNightId(sessionId), at);
            using var close = connection.CreateCommand();
            close.CommandText = "UPDATE venue_sessions SET closed_at_utc = @closed WHERE id = @id AND venue_id = @venue;";
            close.Parameters.AddWithValue("@closed", Iso(at));
            close.Parameters.AddWithValue("@id", sessionId);
            close.Parameters.AddWithValue("@venue", venueId.ToString());
            return close.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteSession(Guid venueId, long sessionId)
    {
        lock (sync)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM venue_sessions WHERE id = @id AND venue_id = @venue;";
            delete.Parameters.AddWithValue("@id", sessionId);
            delete.Parameters.AddWithValue("@venue", venueId.ToString());
            return delete.ExecuteNonQuery() > 0;
        }
    }

    private const string SessionRecordColumns = "s.id, s.opened_at_utc, s.closed_at_utc, n.night_date_local, s.area_mode, s.territory_lock, s.fixed_center_x, s.fixed_center_y, s.fixed_center_z";

    private static AttendanceSessionRecord ReadSessionRecord(SqliteDataReader reader)
    {
        Vector3? fixedCenter = reader.IsDBNull(6) ? null : new Vector3((float)reader.GetDouble(6), (float)reader.GetDouble(7), (float)reader.GetDouble(8));
        return new(
            reader.GetInt64(0),
            ParseUtc(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
            ParseDate(reader.GetString(3)),
            (PresenceAreaMode)reader.GetInt32(4),
            reader.IsDBNull(5) ? null : (uint)reader.GetInt64(5),
            fixedCenter);
    }

    public IReadOnlyList<AttendanceSessionRecord> GetRecentSessions(Guid venueId, int maxRows = 100)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = $"""
                SELECT {SessionRecordColumns}
                FROM venue_sessions s INNER JOIN venue_nights n ON n.id = s.night_id
                WHERE s.venue_id = @venue
                ORDER BY s.opened_at_utc DESC LIMIT @max;
                """;
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@max", maxRows);
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceSessionRecord>();
            while (reader.Read()) rows.Add(ReadSessionRecord(reader));
            return rows;
        }
    }

    public AttendanceSessionRecord? GetSession(Guid venueId, long sessionId)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = $"""
                SELECT {SessionRecordColumns}
                FROM venue_sessions s INNER JOIN venue_nights n ON n.id = s.night_id
                WHERE s.venue_id = @venue AND s.id = @id;
                """;
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@id", sessionId);
            using var reader = select.ExecuteReader();
            return reader.Read() ? ReadSessionRecord(reader) : null;
        }
    }

    // ---- Presence / stats -------------------------------------------------------------------

    public VisitorPresenceChange MarkPresent(Guid venueId, long sessionId, GuestIdentity guest, DateTimeOffset at)
    {
        lock (sync)
        {
            var nightId = GetNightId(sessionId);
            var nightChange = UpsertPresent("visitor_night_stats", "night_id", nightId, guest, at);
            UpsertPresent("session_visitors", "session_id", sessionId, guest, at);
            return nightChange;
        }
    }

    public void MarkAbsent(Guid venueId, long sessionId, GuestIdentity guest, DateTimeOffset at)
    {
        lock (sync)
        {
            MarkAbsentInternal("visitor_night_stats", "night_id", GetNightId(sessionId), guest, at);
            MarkAbsentInternal("session_visitors", "session_id", sessionId, guest, at);
        }
    }

    public void MarkGreeted(Guid venueId, long sessionId, GuestIdentity guest, bool greeted, DateTimeOffset at)
    {
        lock (sync)
        {
            SetGreeted("visitor_night_stats", "night_id", GetNightId(sessionId), guest, greeted);
            SetGreeted("session_visitors", "session_id", sessionId, guest, greeted);
        }
    }

    public void RecordSample(Guid venueId, long sessionId, DateTimeOffset at, int guestCount)
    {
        lock (sync)
        {
            InsertSample("guest_samples", "night_id", GetNightId(sessionId), at, guestCount);
            InsertSample("session_guest_samples", "session_id", sessionId, at, guestCount);
        }
    }

    public AttendanceNightSummary GetSessionSummary(Guid venueId, long sessionId)
    {
        lock (sync)
        {
            var nightDate = GetNightDate(sessionId);
            using var totals = connection.CreateCommand();
            totals.CommandText = """
                SELECT SUM(CASE WHEN currently_present = 1 THEN 1 ELSE 0 END), COUNT(*), COALESCE(SUM(visits), 0), COALESCE(SUM(total_seconds), 0)
                FROM session_visitors WHERE session_id = @session;
                """;
            totals.Parameters.AddWithValue("@session", sessionId);
            using var reader = totals.ExecuteReader();
            var current = 0; var unique = 0; var visits = 0; var seconds = 0L;
            if (reader.Read())
            {
                current = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                unique = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                visits = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                seconds = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            }
            reader.Close();

            using var minMax = connection.CreateCommand();
            minMax.CommandText = "SELECT MAX(guest_count), MIN(guest_count) FROM session_guest_samples WHERE session_id = @session;";
            minMax.Parameters.AddWithValue("@session", sessionId);
            using var minMaxReader = minMax.ExecuteReader();
            var max = current; var min = current;
            if (minMaxReader.Read() && !minMaxReader.IsDBNull(0) && !minMaxReader.IsDBNull(1)) { max = minMaxReader.GetInt32(0); min = minMaxReader.GetInt32(1); }

            return new(nightDate, current, max, min, unique, visits, TimeSpan.FromSeconds(seconds));
        }
    }

    public IReadOnlyList<AttendanceVisitorRecord> GetSessionVisitors(Guid venueId, long sessionId)
    {
        lock (sync)
        {
            var nightDate = GetNightDate(sessionId);
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT character_name, home_world, visits, total_seconds, currently_present, greeted FROM session_visitors WHERE session_id = @session;";
            select.Parameters.AddWithValue("@session", sessionId);
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceVisitorRecord>();
            while (reader.Read())
                rows.Add(new(nightDate, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), TimeSpan.FromSeconds(reader.GetInt64(3)), reader.GetInt32(4) == 1, reader.GetInt32(5) == 1));
            return rows.OrderByDescending(x => x.IsPresent).ThenBy(x => x.CharacterName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<AttendanceGuestSample> GetSessionSamples(Guid venueId, long sessionId, int maxRows = 288)
    {
        lock (sync)
        {
            var nightDate = GetNightDate(sessionId);
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT sample_time_utc, guest_count FROM session_guest_samples WHERE session_id = @session ORDER BY sample_time_utc DESC LIMIT @max;";
            select.Parameters.AddWithValue("@session", sessionId);
            select.Parameters.AddWithValue("@max", maxRows);
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceGuestSample>();
            while (reader.Read()) rows.Add(new(ParseUtc(reader.GetString(0)), reader.GetInt32(1), nightDate));
            rows.Reverse();
            return rows;
        }
    }

    public IReadOnlyList<AttendanceDailyStat> GetDailyStats(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT n.night_date_local,
                    COALESCE((SELECT MAX(guest_count) FROM guest_samples WHERE night_id = n.id), 0),
                    COALESCE((SELECT MIN(guest_count) FROM guest_samples WHERE night_id = n.id), 0),
                    COALESCE((SELECT COUNT(*) FROM visitor_night_stats WHERE night_id = n.id), 0),
                    COALESCE((SELECT SUM(visits) FROM visitor_night_stats WHERE night_id = n.id), 0),
                    COALESCE((SELECT SUM(total_seconds) FROM visitor_night_stats WHERE night_id = n.id), 0)
                FROM venue_nights n
                WHERE n.venue_id = @venue AND n.night_date_local BETWEEN @from AND @to
                ORDER BY n.night_date_local;
                """;
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@from", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            select.Parameters.AddWithValue("@to", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceDailyStat>();
            while (reader.Read())
                rows.Add(new(ParseDate(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), TimeSpan.FromSeconds(reader.GetInt64(5))));
            return rows;
        }
    }

    public IReadOnlyList<AttendanceVisitorRecord> GetVisitorsForRange(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT n.night_date_local, v.character_name, v.home_world, v.visits, v.total_seconds, v.greeted
                FROM visitor_night_stats v INNER JOIN venue_nights n ON n.id = v.night_id
                WHERE n.venue_id = @venue AND n.night_date_local BETWEEN @from AND @to
                ORDER BY n.night_date_local, v.character_name;
                """;
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@from", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            select.Parameters.AddWithValue("@to", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceVisitorRecord>();
            while (reader.Read())
                rows.Add(new(ParseDate(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), TimeSpan.FromSeconds(reader.GetInt64(4)), false, reader.GetInt32(5) == 1));
            return rows;
        }
    }

    public IReadOnlyList<AttendanceGuestSample> GetSamplesForRange(Guid venueId, DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT n.night_date_local, s.sample_time_utc, s.guest_count
                FROM guest_samples s INNER JOIN venue_nights n ON n.id = s.night_id
                WHERE n.venue_id = @venue AND n.night_date_local BETWEEN @from AND @to
                ORDER BY s.sample_time_utc;
                """;
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@from", fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            select.Parameters.AddWithValue("@to", toInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = select.ExecuteReader();
            var rows = new List<AttendanceGuestSample>();
            while (reader.Read()) rows.Add(new(ParseUtc(reader.GetString(1)), reader.GetInt32(2), ParseDate(reader.GetString(0))));
            return rows;
        }
    }

    // ---- Greeter preset library / hotbar ------------------------------------------------------

    public IReadOnlyList<GreetPresetRecord> GetPresets(Guid venueId)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT id, name, line1, line2, line3, command FROM greet_presets WHERE venue_id = @venue ORDER BY name;";
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            using var reader = select.ExecuteReader();
            var rows = new List<GreetPresetRecord>();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            return rows;
        }
    }

    public GreetPresetRecord? GetPreset(Guid venueId, long presetId)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT id, name, line1, line2, line3, command FROM greet_presets WHERE venue_id = @venue AND id = @id;";
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            select.Parameters.AddWithValue("@id", presetId);
            using var reader = select.ExecuteReader();
            return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)) : null;
        }
    }

    public long SavePreset(Guid venueId, long? presetId, string name, string line1, string line2, string line3, string command)
    {
        lock (sync)
        {
            using var upsert = connection.CreateCommand();
            if (presetId is long id)
            {
                upsert.CommandText = "UPDATE greet_presets SET name = @name, line1 = @l1, line2 = @l2, line3 = @l3, command = @cmd WHERE id = @id AND venue_id = @venue;";
                upsert.Parameters.AddWithValue("@id", id);
            }
            else
            {
                upsert.CommandText = "INSERT INTO greet_presets (venue_id, name, line1, line2, line3, command) VALUES (@venue, @name, @l1, @l2, @l3, @cmd); SELECT last_insert_rowid();";
            }
            upsert.Parameters.AddWithValue("@venue", venueId.ToString());
            upsert.Parameters.AddWithValue("@name", name.Trim());
            upsert.Parameters.AddWithValue("@l1", line1.Trim());
            upsert.Parameters.AddWithValue("@l2", line2.Trim());
            upsert.Parameters.AddWithValue("@l3", line3.Trim());
            upsert.Parameters.AddWithValue("@cmd", command.Trim());
            var result = upsert.ExecuteScalar();
            return presetId ?? Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    public void DeletePreset(Guid venueId, long presetId)
    {
        lock (sync)
        {
            using var transaction = connection.BeginTransaction();
            using (var clearSlots = connection.CreateCommand())
            {
                clearSlots.Transaction = transaction;
                clearSlots.CommandText = "UPDATE hotbar_slots SET preset_id = NULL WHERE venue_id = @venue AND preset_id = @id;";
                clearSlots.Parameters.AddWithValue("@venue", venueId.ToString());
                clearSlots.Parameters.AddWithValue("@id", presetId);
                clearSlots.ExecuteNonQuery();
            }
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM greet_presets WHERE venue_id = @venue AND id = @id;";
                delete.Parameters.AddWithValue("@venue", venueId.ToString());
                delete.Parameters.AddWithValue("@id", presetId);
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyDictionary<int, long?> GetHotbarAssignments(Guid venueId)
    {
        lock (sync)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT slot, preset_id FROM hotbar_slots WHERE venue_id = @venue;";
            select.Parameters.AddWithValue("@venue", venueId.ToString());
            using var reader = select.ExecuteReader();
            var result = new Dictionary<int, long?>();
            while (reader.Read()) result[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            for (var slot = 1; slot <= 5; slot++) result.TryAdd(slot, null);
            return result;
        }
    }

    public void SetHotbarAssignment(Guid venueId, int slot, long? presetId)
    {
        lock (sync)
        {
            using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES (@venue, @slot, @preset)
                ON CONFLICT(venue_id, slot) DO UPDATE SET preset_id = excluded.preset_id;
                """;
            upsert.Parameters.AddWithValue("@venue", venueId.ToString());
            upsert.Parameters.AddWithValue("@slot", slot);
            upsert.Parameters.AddWithValue("@preset", (object?)presetId ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }
    }

    // ---- internals --------------------------------------------------------------------------

    private long EnsureNight(Guid venueId, DateTimeOffset at)
    {
        var localDate = DateOnly.FromDateTime(at.LocalDateTime);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO venue_nights (venue_id, night_date_local, created_at_utc) VALUES (@venue, @date, @created);";
        insert.Parameters.AddWithValue("@venue", venueId.ToString());
        insert.Parameters.AddWithValue("@date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("@created", Iso(at));
        insert.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM venue_nights WHERE venue_id = @venue AND night_date_local = @date;";
        select.Parameters.AddWithValue("@venue", venueId.ToString());
        select.Parameters.AddWithValue("@date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long GetNightId(long sessionId)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT night_id FROM venue_sessions WHERE id = @id;";
        select.Parameters.AddWithValue("@id", sessionId);
        var result = select.ExecuteScalar() ?? throw new InvalidOperationException($"Unknown attendance session {sessionId}.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private DateOnly GetNightDate(long sessionId)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT n.night_date_local FROM venue_sessions s INNER JOIN venue_nights n ON n.id = s.night_id WHERE s.id = @id;";
        select.Parameters.AddWithValue("@id", sessionId);
        var result = select.ExecuteScalar();
        return result is string text ? ParseDate(text) : DateOnly.FromDateTime(DateTime.Now);
    }

    private bool SessionExists(Guid venueId, long sessionId, bool requireResumable)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT 1 FROM venue_sessions WHERE id = @id AND venue_id = @venue {(requireResumable ? "AND closed_at_utc IS NULL" : string.Empty)};";
        select.Parameters.AddWithValue("@id", sessionId);
        select.Parameters.AddWithValue("@venue", venueId.ToString());
        return select.ExecuteScalar() is not null;
    }

    private VisitorPresenceChange UpsertPresent(string table, string scopeColumn, long scopeId, GuestIdentity guest, DateTimeOffset at)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT visits, currently_present FROM {table} WHERE {scopeColumn} = @scope AND character_name = @name AND home_world = @world;";
        select.Parameters.AddWithValue("@scope", scopeId);
        select.Parameters.AddWithValue("@name", guest.Name);
        select.Parameters.AddWithValue("@world", guest.HomeWorld);
        using var reader = select.ExecuteReader();

        if (!reader.Read())
        {
            reader.Close();
            using var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO {table} ({scopeColumn}, character_name, home_world, visits, total_seconds, last_visit_start_utc, greeted, currently_present)
                VALUES (@scope, @name, @world, 1, 0, @now, 0, 1);
                """;
            insert.Parameters.AddWithValue("@scope", scopeId);
            insert.Parameters.AddWithValue("@name", guest.Name);
            insert.Parameters.AddWithValue("@world", guest.HomeWorld);
            insert.Parameters.AddWithValue("@now", Iso(at));
            insert.ExecuteNonQuery();
            return new(true, true);
        }

        var visits = reader.GetInt32(0);
        var currentlyPresent = reader.GetInt32(1) == 1;
        reader.Close();
        if (currentlyPresent) return new(false, false);

        var nextVisits = visits + 1;
        using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE {table} SET visits = @visits, currently_present = 1, last_visit_start_utc = @now
            WHERE {scopeColumn} = @scope AND character_name = @name AND home_world = @world;
            """;
        update.Parameters.AddWithValue("@visits", nextVisits);
        update.Parameters.AddWithValue("@now", Iso(at));
        update.Parameters.AddWithValue("@scope", scopeId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        update.ExecuteNonQuery();
        return new(true, nextVisits == 1);
    }

    private void MarkAbsentInternal(string table, string scopeColumn, long scopeId, GuestIdentity guest, DateTimeOffset at)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT currently_present, total_seconds, last_visit_start_utc FROM {table} WHERE {scopeColumn} = @scope AND character_name = @name AND home_world = @world;";
        select.Parameters.AddWithValue("@scope", scopeId);
        select.Parameters.AddWithValue("@name", guest.Name);
        select.Parameters.AddWithValue("@world", guest.HomeWorld);
        using var reader = select.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1) return;

        var totalSeconds = reader.GetInt64(1);
        var lastStartRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
        var addSeconds = lastStartRaw is not null ? (long)Math.Max(0, (at - ParseUtc(lastStartRaw)).TotalSeconds) : 0L;
        reader.Close();

        using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE {table} SET currently_present = 0, total_seconds = @total, last_visit_start_utc = NULL
            WHERE {scopeColumn} = @scope AND character_name = @name AND home_world = @world;
            """;
        update.Parameters.AddWithValue("@total", totalSeconds + addSeconds);
        update.Parameters.AddWithValue("@scope", scopeId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        update.ExecuteNonQuery();
    }

    private void SetGreeted(string table, string scopeColumn, long scopeId, GuestIdentity guest, bool greeted)
    {
        using var update = connection.CreateCommand();
        update.CommandText = $"UPDATE {table} SET greeted = @greeted WHERE {scopeColumn} = @scope AND character_name = @name AND home_world = @world;";
        update.Parameters.AddWithValue("@greeted", greeted ? 1 : 0);
        update.Parameters.AddWithValue("@scope", scopeId);
        update.Parameters.AddWithValue("@name", guest.Name);
        update.Parameters.AddWithValue("@world", guest.HomeWorld);
        update.ExecuteNonQuery();
    }

    private void InsertSample(string table, string scopeColumn, long scopeId, DateTimeOffset at, int guestCount)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({scopeColumn}, sample_time_utc, guest_count) VALUES (@scope, @at, @count);";
        insert.Parameters.AddWithValue("@scope", scopeId);
        insert.Parameters.AddWithValue("@at", Iso(at));
        insert.Parameters.AddWithValue("@count", guestCount);
        insert.ExecuteNonQuery();
    }

    private void CloseAllPresentForSession(long sessionId, DateTimeOffset at)
    {
        foreach (var guest in PresentGuests("session_visitors", "session_id", sessionId))
            MarkAbsentInternal("session_visitors", "session_id", sessionId, guest, at);
    }

    private void CloseAllPresentForNight(long nightId, DateTimeOffset at)
    {
        foreach (var guest in PresentGuests("visitor_night_stats", "night_id", nightId))
            MarkAbsentInternal("visitor_night_stats", "night_id", nightId, guest, at);
    }

    private List<GuestIdentity> PresentGuests(string table, string scopeColumn, long scopeId)
    {
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT character_name, home_world FROM {table} WHERE {scopeColumn} = @scope AND currently_present = 1;";
        select.Parameters.AddWithValue("@scope", scopeId);
        using var reader = select.ExecuteReader();
        var guests = new List<GuestIdentity>();
        while (reader.Read()) guests.Add(new(reader.GetString(0), reader.GetString(1)));
        return guests;
    }

    private static void Exec(SqliteTransaction transaction, string sql)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => new(DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc), TimeSpan.Zero);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
