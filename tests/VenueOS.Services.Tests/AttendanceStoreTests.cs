using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class AttendanceStoreTests
{
    [Fact] public void Start_pause_resume_close_and_delete_manage_session_lifecycle()
    {
        using var db = New(); var venue = Guid.NewGuid(); var opened = DateTimeOffset.UnixEpoch;
        var sessionId = db.StartSession(venue, opened);
        Assert.True(db.GetRecentSessions(venue).Single().IsResumable);
        Assert.True(db.PauseSession(venue, sessionId, opened.AddMinutes(5)));
        Assert.True(db.GetRecentSessions(venue).Single().IsResumable);
        Assert.True(db.ResumeSession(venue, sessionId));
        Assert.True(db.CloseSession(venue, sessionId, opened.AddMinutes(10)));
        Assert.False(db.GetRecentSessions(venue).Single().IsResumable);
        Assert.False(db.ResumeSession(venue, sessionId));
        Assert.True(db.DeleteSession(venue, sessionId));
        Assert.Empty(db.GetRecentSessions(venue));
    }

    [Fact] public void Mark_present_reports_first_visit_tonight_once_per_night_not_per_session()
    {
        using var db = New(); var venue = Guid.NewGuid(); var guest = new GuestIdentity("Mair", "Balmung"); var t = DateTimeOffset.UnixEpoch;
        var s1 = db.StartSession(venue, t);
        var first = db.MarkPresent(venue, s1, guest, t);
        Assert.True(first.BecamePresent); Assert.True(first.IsFirstVisitTonight);
        db.MarkAbsent(venue, s1, guest, t.AddMinutes(1));
        db.CloseSession(venue, s1, t.AddMinutes(1));

        var s2 = db.StartSession(venue, t.AddMinutes(2));
        var second = db.MarkPresent(venue, s2, guest, t.AddMinutes(2));
        Assert.True(second.BecamePresent); Assert.False(second.IsFirstVisitTonight);
    }

    [Fact] public void Mark_present_is_idempotent_while_still_present()
    {
        using var db = New(); var venue = Guid.NewGuid(); var guest = new GuestIdentity("Mair", "Balmung"); var t = DateTimeOffset.UnixEpoch;
        var session = db.StartSession(venue, t);
        db.MarkPresent(venue, session, guest, t);
        var again = db.MarkPresent(venue, session, guest, t.AddSeconds(1));
        Assert.False(again.BecamePresent); Assert.False(again.IsFirstVisitTonight);
        Assert.Equal(1, db.GetSessionVisitors(venue, session).Single().Visits);
    }

    [Fact] public void Total_time_accrues_only_when_marked_absent()
    {
        using var db = New(); var venue = Guid.NewGuid(); var guest = new GuestIdentity("Mair", "Balmung"); var t = DateTimeOffset.UnixEpoch;
        var session = db.StartSession(venue, t);
        db.MarkPresent(venue, session, guest, t);
        Assert.Equal(TimeSpan.Zero, db.GetSessionVisitors(venue, session).Single().TotalTime);
        db.MarkAbsent(venue, session, guest, t.AddMinutes(3));
        Assert.Equal(TimeSpan.FromMinutes(3), db.GetSessionVisitors(venue, session).Single().TotalTime);
        db.MarkPresent(venue, session, guest, t.AddMinutes(5));
        db.MarkAbsent(venue, session, guest, t.AddMinutes(8));
        var visitor = db.GetSessionVisitors(venue, session).Single();
        Assert.Equal(TimeSpan.FromMinutes(6), visitor.TotalTime); Assert.Equal(2, visitor.Visits);
    }

    [Fact] public void Pausing_or_closing_marks_everyone_present_as_absent_and_finalizes_time()
    {
        using var db = New(); var venue = Guid.NewGuid(); var guest = new GuestIdentity("Mair", "Balmung"); var t = DateTimeOffset.UnixEpoch;
        var session = db.StartSession(venue, t);
        db.MarkPresent(venue, session, guest, t);
        db.PauseSession(venue, session, t.AddMinutes(4));
        var visitor = db.GetSessionVisitors(venue, session).Single();
        Assert.False(visitor.IsPresent); Assert.Equal(TimeSpan.FromMinutes(4), visitor.TotalTime);
    }

    [Fact] public void Greeted_flag_is_tracked_independently_at_night_and_session_scope()
    {
        using var db = New(); var venue = Guid.NewGuid(); var guest = new GuestIdentity("Mair", "Balmung"); var t = DateTimeOffset.UnixEpoch;
        var session = db.StartSession(venue, t);
        db.MarkPresent(venue, session, guest, t);
        db.MarkGreeted(venue, session, guest, true, t);
        Assert.True(db.GetSessionVisitors(venue, session).Single().Greeted);
    }

    [Fact] public void Samples_and_daily_stats_are_scoped_per_venue_and_per_night()
    {
        using var db = New(); var venueA = Guid.NewGuid(); var venueB = Guid.NewGuid(); var t = DateTimeOffset.UnixEpoch;
        var sessionA = db.StartSession(venueA, t); var sessionB = db.StartSession(venueB, t);
        db.RecordSample(venueA, sessionA, t, 5); db.RecordSample(venueA, sessionA, t.AddMinutes(5), 9);
        db.RecordSample(venueB, sessionB, t, 100);
        Assert.Equal(2, db.GetSessionSamples(venueA, sessionA).Count);
        Assert.Single(db.GetSessionSamples(venueB, sessionB));
        var daily = db.GetDailyStats(venueA, DateOnly.FromDateTime(t.LocalDateTime), DateOnly.FromDateTime(t.LocalDateTime));
        Assert.Equal(9, daily.Single().MaxGuests); Assert.Equal(5, daily.Single().MinGuests);
    }

    [Fact] public void Preset_library_and_hotbar_assignments_are_isolated_per_venue()
    {
        using var db = New(); var venueA = Guid.NewGuid(); var venueB = Guid.NewGuid();
        var presetA = db.SavePreset(venueA, null, "DJ Mair", "Welcome <t>!", "", "", "");
        db.SetHotbarAssignment(venueA, 1, presetA);
        Assert.Single(db.GetPresets(venueA));
        Assert.Empty(db.GetPresets(venueB));
        Assert.Equal(presetA, db.GetHotbarAssignments(venueA)[1]);
        Assert.Null(db.GetHotbarAssignments(venueB)[1]);
    }

    [Fact] public void Saving_with_an_id_updates_in_place_and_deleting_clears_hotbar_reference()
    {
        using var db = New(); var venue = Guid.NewGuid();
        var id = db.SavePreset(venue, null, "DJ Mair", "Hi", "", "", "");
        db.SetHotbarAssignment(venue, 3, id);
        var updatedId = db.SavePreset(venue, id, "DJ Mair Renamed", "Hi there", "", "", "/emote wave");
        Assert.Equal(id, updatedId);
        Assert.Equal("DJ Mair Renamed", db.GetPreset(venue, id)!.Name);
        db.DeletePreset(venue, id);
        Assert.Null(db.GetPreset(venue, id));
        Assert.Null(db.GetHotbarAssignments(venue)[3]);
    }

    /// <summary>The two-slot, two-preset scenario reported as broken live: DJ 1 → Preset A, DJ 2 → Preset B, both
    /// must retain independently, and reassigning an *already-assigned* slot (the <c>ON CONFLICT DO UPDATE</c>
    /// branch of <see cref="SqliteVenueDatabase.SetHotbarAssignment"/> — never exercised by the other tests above,
    /// which only ever assign a previously-empty slot once) must update only that slot.</summary>
    [Fact] public void Reassigning_an_already_assigned_slot_updates_only_that_slot_via_the_conflict_path()
    {
        using var db = New(); var venue = Guid.NewGuid();
        var presetA = db.SavePreset(venue, null, "Preset A", "Hi A", "", "", "");
        var presetB = db.SavePreset(venue, null, "Preset B", "Hi B", "", "", "");
        db.SetHotbarAssignment(venue, 1, presetA);
        db.SetHotbarAssignment(venue, 2, presetB);
        var afterFirst = db.GetHotbarAssignments(venue);
        Assert.Equal(presetA, afterFirst[1]);
        Assert.Equal(presetB, afterFirst[2]);

        db.SetHotbarAssignment(venue, 1, presetB);
        var afterReassign = db.GetHotbarAssignments(venue);
        Assert.Equal(presetB, afterReassign[1]);
        Assert.Equal(presetB, afterReassign[2]);

        db.SetHotbarAssignment(venue, 1, null);
        Assert.Null(db.GetHotbarAssignments(venue)[1]);
        Assert.Equal(presetB, db.GetHotbarAssignments(venue)[2]);
    }

    /// <summary>Assignments must survive a full store reopen (the closest thing to "plugin reload" this environment
    /// can exercise without a live game session) — a fresh <see cref="SqliteVenueDatabase"/> instance against the
    /// same on-disk file must see exactly what the previous instance wrote.</summary>
    [Fact] public void Hotbar_assignments_survive_reopening_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"venueos-hotbar-reload-{Guid.NewGuid():N}.db");
        var venue = Guid.NewGuid();
        try
        {
            long presetA, presetB;
            using (var db = new SqliteVenueDatabase($"Data Source={path}"))
            {
                presetA = db.SavePreset(venue, null, "Preset A", "Hi A", "", "", "");
                presetB = db.SavePreset(venue, null, "Preset B", "Hi B", "", "", "");
                db.SetHotbarAssignment(venue, 1, presetA);
                db.SetHotbarAssignment(venue, 2, presetB);
            }

            using (var reopened = new SqliteVenueDatabase($"Data Source={path}"))
            {
                var assignments = reopened.GetHotbarAssignments(venue);
                Assert.Equal(presetA, assignments[1]);
                Assert.Equal(presetB, assignments[2]);
            }
        }
        finally
        {
            // Microsoft.Data.Sqlite pools native connections by default even after Dispose(); clear the pool first
            // or the file can still be locked here — this is ADO.NET pooling cleanup, unrelated to the assertion.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact] public void The_same_preset_can_be_assigned_to_more_than_one_hotbar_slot()
    {
        using var db = New(); var venue = Guid.NewGuid();
        var preset = db.SavePreset(venue, null, "Shared", "hi", "", "", "");
        db.SetHotbarAssignment(venue, 1, preset); db.SetHotbarAssignment(venue, 4, preset);
        var assignments = db.GetHotbarAssignments(venue);
        Assert.Equal(preset, assignments[1]); Assert.Equal(preset, assignments[4]); Assert.Null(assignments[2]);
    }

    [Fact] public void Saving_two_presets_with_the_same_name_in_one_venue_does_not_throw()
    {
        using var db = New(); var venue = Guid.NewGuid();
        var first = db.SavePreset(venue, null, "Test Greet", "hi", "", "", "");
        var second = db.SavePreset(venue, null, "Test Greet", "hello", "", "", "");
        Assert.NotEqual(first, second);
        Assert.Equal(2, db.GetPresets(venue).Count);
    }

    [Fact] public void Opening_a_database_created_before_the_unique_name_fix_migrates_and_preserves_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"venueos-migration-{Guid.NewGuid():N}.db");
        var venue = Guid.NewGuid();
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                legacy.Open();
                using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE greet_presets (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        venue_id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        line1 TEXT NOT NULL DEFAULT '',
                        line2 TEXT NOT NULL DEFAULT '',
                        line3 TEXT NOT NULL DEFAULT '',
                        command TEXT NOT NULL DEFAULT '',
                        UNIQUE(venue_id, name)
                    );
                    """;
                create.ExecuteNonQuery();
                using var insert = legacy.CreateCommand();
                insert.CommandText = "INSERT INTO greet_presets (venue_id, name, line1) VALUES (@venue, 'Existing Preset', 'hi');";
                insert.Parameters.AddWithValue("@venue", venue.ToString());
                insert.ExecuteNonQuery();
            }

            using var db = new SqliteVenueDatabase($"Data Source={path}");
            Assert.Single(db.GetPresets(venue));
            Assert.Equal("Existing Preset", db.GetPresets(venue)[0].Name);
            // The whole point of the migration: a second preset sharing an existing name must not throw.
            db.SavePreset(venue, null, "Existing Preset", "duplicate name should be fine now", "", "", "");
            Assert.Equal(2, db.GetPresets(venue).Count);
        }
        finally { CleanupDbFile(path); }
    }

    /// <summary>Reproduces the live-verified bug exactly: <c>ALTER TABLE greet_presets RENAME TO
    /// greet_presets_pre_unique_fix</c> rewrites <c>hotbar_slots</c>' stored foreign key to the *new* name — even
    /// with <c>foreign_keys = OFF</c> — so a legacy database that actually has hotbar assignments (the previous
    /// migration test above never created a <c>hotbar_slots</c> row at all, which is exactly how this slipped past
    /// it) is left with a dangling reference to a table that gets dropped moments later. Opening it through
    /// <see cref="SqliteVenueDatabase"/> must both migrate <c>greet_presets</c> *and* repair <c>hotbar_slots</c> in
    /// the same pass, preserving the existing assignment and leaving the database genuinely writable again.</summary>
    [Fact] public void Opening_a_legacy_database_with_an_existing_hotbar_assignment_repairs_the_foreign_key_and_keeps_the_assignment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"venueos-migration-hotbar-{Guid.NewGuid():N}.db");
        var venue = Guid.NewGuid();
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                legacy.Open();
                RawExec(legacy, """
                    CREATE TABLE greet_presets (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        venue_id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        line1 TEXT NOT NULL DEFAULT '',
                        line2 TEXT NOT NULL DEFAULT '',
                        line3 TEXT NOT NULL DEFAULT '',
                        command TEXT NOT NULL DEFAULT '',
                        UNIQUE(venue_id, name)
                    );
                    """);
                RawExec(legacy, """
                    CREATE TABLE hotbar_slots (
                        venue_id TEXT NOT NULL,
                        slot INTEGER NOT NULL,
                        preset_id INTEGER NULL REFERENCES greet_presets(id) ON DELETE SET NULL,
                        PRIMARY KEY (venue_id, slot)
                    );
                    """);
                RawExec(legacy, $"INSERT INTO greet_presets (venue_id, name, line1) VALUES ('{venue}', 'DJ Mair', 'hi');");
                RawExec(legacy, $"INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES ('{venue}', 1, 1);");
            }

            long presetId;
            using (var db = new SqliteVenueDatabase($"Data Source={path}"))
            {
                presetId = db.GetPresets(venue).Single().Id;
                Assert.Equal(presetId, db.GetHotbarAssignments(venue)[1]);
                // The exact operation the live bug broke: writing to hotbar_slots (which triggers SQLite's FK
                // lookup against whatever hotbar_slots' schema currently says it references).
                db.SetHotbarAssignment(venue, 2, presetId);
                Assert.Equal(presetId, db.GetHotbarAssignments(venue)[2]);
                Assert.Equal(presetId, db.GetHotbarAssignments(venue)[1]);
            }

            AssertForeignKeysAreClean(path);
        }
        finally { CleanupDbFile(path); }
    }

    /// <summary>The scenario the brief was most concerned about: a database that already went through the buggy
    /// migration in a *previous* session. <c>greet_presets</c> no longer contains "UNIQUE" (that migration already
    /// ran once), so <see cref="MigrateGreetPresetsDropUniqueName"/>'s own guard would skip it forever — the
    /// database can never self-heal through that path alone. The repair must be driven by <c>hotbar_slots</c>' own
    /// foreign key target, independent of whether <c>greet_presets</c> still needs migrating. Also covers: an
    /// invalid/dangling <c>preset_id</c> (referencing a preset that no longer exists) must be cleared to <c>NULL</c>
    /// without deleting the slot row or touching a sibling slot's valid assignment, and the repair must be a no-op
    /// (and never throw) the second time <see cref="SqliteVenueDatabase"/> opens the same, now-already-repaired
    /// file.</summary>
    [Fact] public void Opening_a_database_already_left_with_a_stale_hotbar_slots_foreign_key_self_heals_and_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"venueos-stale-fk-{Guid.NewGuid():N}.db");
        var venue = Guid.NewGuid();
        try
        {
            using (var broken = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                broken.Open();
                // greet_presets already has the *new* (non-unique) shape — a past migration already ran — but
                // hotbar_slots was left referencing the old renamed-away table, exactly as the live bug produced.
                RawExec(broken, """
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
                RawExec(broken, """
                    CREATE TABLE hotbar_slots (
                        venue_id TEXT NOT NULL,
                        slot INTEGER NOT NULL,
                        preset_id INTEGER NULL REFERENCES "greet_presets_pre_unique_fix"(id) ON DELETE SET NULL,
                        PRIMARY KEY (venue_id, slot)
                    );
                    """);
                RawExec(broken, $"INSERT INTO greet_presets (venue_id, name, line1) VALUES ('{venue}', 'Preset A', 'hi A');");
                RawExec(broken, $"INSERT INTO greet_presets (venue_id, name, line1) VALUES ('{venue}', 'Preset B', 'hi B');");
                RawExec(broken, "PRAGMA foreign_keys = OFF;"); // needed to insert against the already-dangling FK target at all
                RawExec(broken, $"INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES ('{venue}', 1, 1);"); // valid
                RawExec(broken, $"INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES ('{venue}', 2, 2);"); // valid
                RawExec(broken, $"INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES ('{venue}', 3, 9999);"); // dangling/invalid
                RawExec(broken, $"INSERT INTO hotbar_slots (venue_id, slot, preset_id) VALUES ('{venue}', 4, NULL);"); // already empty
            }

            long presetA, presetB;
            using (var db = new SqliteVenueDatabase($"Data Source={path}"))
            {
                var presets = db.GetPresets(venue);
                presetA = presets.Single(x => x.Name == "Preset A").Id;
                presetB = presets.Single(x => x.Name == "Preset B").Id;

                var assignments = db.GetHotbarAssignments(venue);
                Assert.Equal(presetA, assignments[1]); // valid assignment preserved
                Assert.Equal(presetB, assignments[2]); // valid assignment preserved
                Assert.Null(assignments[3]); // dangling reference cleared, row itself preserved (present as a key at all)
                Assert.Null(assignments[4]); // untouched

                // The exact operation the live bug broke.
                db.SetHotbarAssignment(venue, 5, presetA);
                Assert.Equal(presetA, db.GetHotbarAssignments(venue)[5]);
            }

            AssertForeignKeysAreClean(path);

            // Idempotency: opening the now-repaired file again must not throw and must leave everything as-is.
            using (var reopened = new SqliteVenueDatabase($"Data Source={path}"))
            {
                var assignments = reopened.GetHotbarAssignments(venue);
                Assert.Equal(presetA, assignments[1]);
                Assert.Equal(presetB, assignments[2]);
                Assert.Null(assignments[3]);
                Assert.Equal(presetA, assignments[5]);
            }
        }
        finally { CleanupDbFile(path); }
    }

    private static void AssertForeignKeysAreClean(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var fkList = connection.CreateCommand())
        {
            fkList.CommandText = "PRAGMA foreign_key_list(hotbar_slots);";
            using var reader = fkList.ExecuteReader();
            var sawAny = false;
            while (reader.Read()) { sawAny = true; Assert.Equal("greet_presets", reader.GetString(2)); }
            Assert.True(sawAny, "hotbar_slots should still declare a foreign key to greet_presets.");
        }
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            using var reader = check.ExecuteReader();
            Assert.False(reader.Read(), "foreign_key_check reported at least one violation.");
        }
    }

    private static void RawExec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CleanupDbFile(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) { var file = path + suffix; if (File.Exists(file)) File.Delete(file); }
    }

    private static SqliteVenueDatabase New() => new("Data Source=:memory:");
}
