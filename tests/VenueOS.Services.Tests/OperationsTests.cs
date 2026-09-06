using System.Numerics;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class OperationsTests
{
    [Fact] public void Attendance_records_arrival_and_departure()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        provider.Items = [Player("A", "Balmung")]; presence.Tick(new(null, null, null));
        Assert.Single(attendance.Guests); Assert.True(attendance.GetTonightVisitors().Single().IsPresent);
        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null));
        Assert.Empty(attendance.Guests); Assert.False(attendance.GetTonightVisitors().Single().IsPresent);
    }

    [Fact] public void Attendance_normalizes_identity_case_and_whitespace_so_it_is_not_double_counted()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        provider.Items = [Player("  mair   hatsuki ", "Balmung")]; presence.Tick(new(null, null, null));
        provider.Items = [Player("Mair Hatsuki", "balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));
        Assert.Single(attendance.Guests); Assert.Single(attendance.GetTonightVisitors());
    }

    [Fact] public void Attendance_captures_territory_and_center_only_when_requested_at_session_start()
    {
        var clock = new Clock(); using var db = NewDb(); var presence = new PresenceService(clock, new Provider());
        var attendance = new AttendanceService(presence, db, clock, () => 42u, () => new Vector3(5, 0, 0)); attendance.AttachVenue(Guid.NewGuid());
        attendance.StartSession(lockToOpenTerritory: true, captureFixedCenter: true, clock.UtcNow);
        Assert.Equal(42u, attendance.ActiveTerritoryLock); Assert.Equal(new Vector3(5, 0, 0), attendance.ActiveFixedCenter);
        attendance.CloseSession(clock.UtcNow);
        Assert.Null(attendance.ActiveTerritoryLock); Assert.Null(attendance.ActiveFixedCenter);
        attendance.StartSession(false, false, clock.UtcNow);
        Assert.Null(attendance.ActiveTerritoryLock); Assert.Null(attendance.ActiveFixedCenter);
    }

    /// <summary>The confirmed-missing History feature: <see cref="AttendanceService.GetSessionSummary(long)"/> must
    /// report a *specific* historical opening's own Max Guests/Unique Visitors, not whatever the currently active
    /// session happens to be — it's a thin pass-through to <c>IVenueDatabase.GetSessionSummary</c>, which already
    /// supported an arbitrary session id; this only confirms the service-level pass-through is actually wired up.</summary>
    [Fact] public void GetSessionSummary_reports_a_specific_historical_opening_not_just_the_current_one()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach();

        attendance.StartSession(false, false, clock.UtcNow);
        var firstSessionId = attendance.CurrentSessionId!.Value;
        provider.Items = [Player("Mair", "Balmung"), Player("Ada", "Balmung")];
        presence.Tick(new(null, null, null));
        db.RecordSample(venue, firstSessionId, clock.UtcNow, 2);
        attendance.CloseSession(clock.UtcNow);

        clock.Advance(60);
        attendance.StartSession(false, false, clock.UtcNow);
        var secondSessionId = attendance.CurrentSessionId!.Value;
        provider.Items = [Player("Zeta", "Balmung")];
        presence.Tick(new(null, null, null));
        db.RecordSample(venue, secondSessionId, clock.UtcNow, 1);

        var firstSummary = attendance.GetSessionSummary(firstSessionId);
        Assert.Equal(2, firstSummary.MaxGuests); Assert.Equal(2, firstSummary.UniqueGuests);

        var secondSummary = attendance.GetSessionSummary(secondSessionId);
        Assert.Equal(1, secondSummary.MaxGuests); Assert.Equal(1, secondSummary.UniqueGuests);
    }

    [Fact] public void Attendance_seeds_guests_already_present_when_a_session_starts_without_duplicating_on_rescan()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach();
        provider.Items = [Player("Mair", "Balmung"), Player("Ada", "Balmung")];
        presence.Tick(new(null, null, null)); // both already known-present before any opening exists — the reported bug
        attendance.StartSession(false, false, clock.UtcNow);
        Assert.Empty(attendance.GetTonightVisitors()); // seeding needs the next scan to actually observe them again
        clock.Advance(1); presence.Tick(new(null, null, null));
        var visitors = attendance.GetTonightVisitors();
        Assert.Equal(2, visitors.Count);
        Assert.All(visitors, v => Assert.True(v.IsPresent));
        Assert.All(visitors, v => Assert.Equal(1, v.Visits));

        clock.Advance(1); presence.Tick(new(null, null, null)); // still present — must not double-count as a new visit
        Assert.All(attendance.GetTonightVisitors(), v => Assert.Equal(1, v.Visits));
    }

    [Fact] public void Attendance_reseeds_a_still_present_guest_after_pause_and_resume_as_a_fresh_visit()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach();
        provider.Items = [Player("Mair", "Balmung")];
        attendance.StartSession(false, false, clock.UtcNow);
        clock.Advance(1); presence.Tick(new(null, null, null));
        Assert.Equal(1, attendance.GetTonightVisitors().Single().Visits);

        var sessionId = attendance.CurrentSessionId!.Value;
        Assert.True(attendance.PauseSession(clock.UtcNow.AddSeconds(2)));
        Assert.False(db.GetSessionVisitors(venue, sessionId).Single().IsPresent);

        Assert.True(attendance.ResumeSession(sessionId, false, false));
        clock.Advance(1); presence.Tick(new(null, null, null)); // Mair never left — rediscovered as visit #2
        Assert.Equal(2, attendance.GetTonightVisitors().Single().Visits);
    }

    /// <summary>Attendance is the single authoritative greeted-state source: wired here exactly as production does
    /// (a captured-reference pair breaking the constructor cycle — see <c>Plugin.cs</c>), not the local fake
    /// <see cref="NewGreeterWithPreset"/> normally uses. A manual "Mark as Greeted" must immediately stop Greeter
    /// from queueing that guest again, and un-marking must make them eligible again — read directly from
    /// Attendance, never a Greeter-owned flag.</summary>
    [Fact] public void Attendance_mark_as_greeted_updates_greeters_authoritative_state_and_blocks_future_auto_greet()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, Chat(clock, []), db, isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", "Welcome <name>!", "", "", "");
        greeter.Configure(new GreeterSettings(ActivePresetId: presetId));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));

        attendance.MarkGreeted(guest, true, clock.UtcNow);
        Assert.False(greeter.QueueGreeting(guest)); // Greeter recognizes the manual mark via Attendance, not its own flag
        Assert.True(attendance.GetTonightVisitors().Single(v => v.CharacterName == "Mair").Greeted);

        attendance.MarkGreeted(guest, false, clock.UtcNow);
        Assert.True(greeter.QueueGreeting(guest)); // unmarking makes them eligible again
    }

    /// <summary>Greeter's own queue cleanup (used when Attendance tells it a guest was just marked greeted some
    /// other way) is a best-effort local operation only — it removes the guest from the pending queue but, unlike
    /// the old <c>MarkGreeted</c>, does not set any "greeted" flag of Greeter's own (there isn't one anymore).</summary>
    [Fact] public async Task Greeter_cancel_pending_removes_a_pending_guest_from_the_queue()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, _) = NewGreeterWithPreset(clock, chat, db, venue, "hi <name>");
        var a = new GuestIdentity("A", "Balmung"); var b = new GuestIdentity("B", "Balmung");
        greeter.QueueGreeting(a); greeter.QueueGreeting(b);
        Assert.Equal(2, greeter.PendingCount);
        greeter.CancelPending(b);
        Assert.Equal(1, greeter.PendingCount);
        greeter.Tick(); await chat.TickAsync();
        Assert.Single(sent); Assert.Contains("hi A", sent[0]);
    }

    [Fact] public void Presence_distance_filter_can_follow_the_operator_live_or_use_a_fixed_captured_point()
    {
        var clock = new Clock(); var provider = new Provider { Items = [Player("A", "Balmung") with { Position = new Vector3(10, 0, 0) }] }; var presence = new PresenceService(clock, provider);
        var follow = new PresencePolicy(null, null, 5f, PresenceAreaMode.FollowOperator); presence.Tick(follow, new Vector3(11, 0, 0)); Assert.Single(presence.Current);
        clock.Advance(1); presence.Tick(follow, new Vector3(100, 0, 0)); Assert.Empty(presence.Current);
        clock.Advance(1); var fixedPoint = new PresencePolicy(null, new Vector3(10, 0, 0), 5f, PresenceAreaMode.FixedPoint); presence.Tick(fixedPoint, new Vector3(999, 0, 0)); Assert.Single(presence.Current);
    }

    [Fact] public void Attendance_first_visit_tonight_fires_once_per_night_and_can_drive_greeter_auto_queue()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, Chat(clock, []), db, venue, "Welcome <name>!", autoGreetEnabled: true);
        var firstVisitCount = 0;
        attendance.FirstVisitTonight += guest => { firstVisitCount++; if (greeter.Settings.AutoGreetEnabled) greeter.QueueGreeting(guest); };

        presence.Tick(new(null, null, null)); // the opening-start seed scan — nobody present yet, consumes it with an empty batch
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // a genuine arrival after the opening is already active
        Assert.Equal(1, firstVisitCount); Assert.Equal(1, greeter.PendingCount);

        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null));
        attendance.CloseSession(clock.UtcNow);
        attendance.StartSession(false, false, clock.UtcNow);
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));
        Assert.Equal(1, firstVisitCount);
    }

    [Fact] public void Attendance_first_visit_tonight_does_not_auto_queue_when_auto_greet_is_disabled()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, Chat(clock, []), db, venue, "Welcome <name>!", autoGreetEnabled: false);
        attendance.FirstVisitTonight += guest => { if (greeter.Settings.AutoGreetEnabled) greeter.QueueGreeting(guest); };
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));
        Assert.Equal(0, greeter.PendingCount);
    }

    [Fact] public async Task Greeter_is_unique_and_honors_the_active_preset()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "two <name>");
        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.True(greeter.QueueGreeting(guest)); Assert.False(greeter.QueueGreeting(guest));
        greeter.Tick(); await chat.TickAsync();
        Assert.Contains("two Mair", sent.Single());
        Assert.True(isGreetedFake(guest)); // completion reported back through onGreetingCompleted, not a Greeter-owned flag
        Assert.False(greeter.QueueGreeting(guest));
    }

    /// <summary>Hard requirement, donor-proven live-acceptance format: an unquoted <c>Name@World</c> recipient with
    /// no spaces around <c>@</c>, and the character name's own internal space preserved exactly (never collapsed to
    /// "KeiJoi"). Asserts the exact generated command string, not just a substring.</summary>
    [Fact] public async Task Greeter_tell_uses_the_donor_proven_unquoted_name_at_world_recipient_format()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, _) = NewGreeterWithPreset(clock, chat, db, venue, "Hi!");
        var guest = new GuestIdentity("Kei Joi", "Halicarnassus");
        Assert.True(greeter.QueueGreeting(guest));
        greeter.Tick(); await chat.TickAsync();
        Assert.Equal("/tell Kei Joi@Halicarnassus Hi!", sent.Single());
    }

    /// <summary>Same hard requirement, for the VIP recognition tell specifically — a separate code path
    /// (<see cref="GreetingCoordinator.TryGreet"/>) from Greeter's own lines, so it needs its own exact-string
    /// assertion rather than assuming the two paths share formatting logic.</summary>
    [Fact] public async Task Vip_recognition_tell_uses_the_donor_proven_unquoted_name_at_world_recipient_format()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var greeter = new GreeterService(clock, chat, db); greeter.AttachVenue(venue); // no active preset — irrelevant to this assertion, the tell dispatches first regardless
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Kei Joi", "Halicarnassus", true, "Welcome back, <name>!", "", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(_ => false, greeter, vip, chat);
        Assert.True(coordinator.TryGreet(new GuestIdentity("Kei Joi", "Halicarnassus"), GreetingSource.AutomaticArrival));
        await chat.TickAsync();
        Assert.Equal("/tell Kei Joi@Halicarnassus Welcome back, Kei Joi!", sent.Single());
    }

    /// <summary>Mandatory VIP-orchestration regression: VIP A and VIP B never cross custom tells — each guest
    /// receives exactly their own record's message.</summary>
    [Fact] public async Task Vip_a_and_vip_b_send_their_own_distinct_custom_tells_never_crossed()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([
            new("Ada", "Balmung", true, "Message A", "", VipAnnouncementChannel.Shout),
            new("Zeta", "Balmung", true, "Message B", "", VipAnnouncementChannel.Shout),
        ]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);

        Assert.True(coordinator.TryGreet(new GuestIdentity("Ada", "Balmung"), GreetingSource.AutomaticArrival));
        await chat.TickAsync();
        Assert.Equal("/tell Ada@Balmung Message A", sent[0]);

        Assert.True(coordinator.TryGreet(new GuestIdentity("Zeta", "Balmung"), GreetingSource.AutomaticArrival));
        await chat.TickAsync();
        Assert.Equal("/tell Zeta@Balmung Message B", sent[1]);
    }

    /// <summary>Mandatory requirement: an enabled VIP with an empty custom tell is a deliberate "public-only"
    /// configuration — the private /tell step is skipped entirely (never sent with empty content), but the
    /// workflow still continues to the normal Greeter and completes successfully.</summary>
    [Fact] public async Task Empty_vip_custom_tell_skips_private_tell_and_continues_to_greeter()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "", "Ada's public message", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        var guest = new GuestIdentity("Ada", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        greeter.Tick(); await Drain(chat, clock, 2);

        Assert.Equal(2, sent.Count); // no private tell — only the Greeter line, then the public announcement
        Assert.Contains("Welcome Ada", sent[0]); Assert.Equal("/shout Ada's public message", sent[1]);
        Assert.True(isGreetedFake(guest));
    }

    /// <summary>Mandatory requirement: an enabled VIP with an empty public message is a deliberate "private-only"
    /// configuration — the public /shout or /yell step is skipped entirely, but the workflow still completes
    /// successfully (Attendance greeted).</summary>
    [Fact] public async Task Empty_vip_public_message_skips_public_announcement_but_still_completes()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "Ada's private tell", "", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Ada", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        await DrainWithGreeterTicks(chat, greeter, clock, 2);

        Assert.Equal(2, sent.Count); // the private tell, then the Greeter line — no public announcement
        Assert.Equal("/tell Ada@Balmung Ada's private tell", sent[0]); Assert.Contains("Welcome Ada", sent[1]);
        Assert.True(isGreetedFake(guest));
    }

    /// <summary>Mandatory migration regression: the removed global recognition template backfills any record whose
    /// own custom tell is still empty, and clears itself so the migration is a one-time event.</summary>
    [Fact] public void Migration_backfills_empty_custom_tell_from_legacy_template_and_clears_it()
    {
        var settings = new VipSettings([new("Ada", "Balmung", true, "", "Welcome", VipAnnouncementChannel.Shout)]) with { LegacyRecognitionTemplate = "Welcome back, <name>!" };
        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.True(changed);
        Assert.Equal("Welcome back, <name>!", migrated.Records.Single().CustomTell);
        Assert.Null(migrated.LegacyRecognitionTemplate);
    }

    /// <summary>Mandatory migration regression: an already-populated custom tell is never overwritten by the legacy
    /// template, even though the legacy value is still cleared (so migration doesn't linger forever).</summary>
    [Fact] public void Migration_never_overwrites_an_already_populated_custom_tell()
    {
        var settings = new VipSettings([new("Ada", "Balmung", true, "Already set by the operator", "Welcome", VipAnnouncementChannel.Shout)]) with { LegacyRecognitionTemplate = "Legacy template" };
        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.True(changed); // the legacy value is still cleared even though nothing needed backfilling
        Assert.Equal("Already set by the operator", migrated.Records.Single().CustomTell);
        Assert.Null(migrated.LegacyRecognitionTemplate);
    }

    /// <summary>Mandatory migration regression: a fresh install or an already-migrated venue (no legacy template
    /// present) is a true no-op — the exact same settings instance is returned, never re-saved.</summary>
    [Fact] public void Migration_is_a_no_op_when_there_is_no_legacy_template()
    {
        var settings = VipSettings.Default() with { Records = [new("Ada", "Balmung", true, "", "Welcome", VipAnnouncementChannel.Shout)] };
        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.False(changed);
        Assert.Same(settings, migrated);
    }

    /// <summary>Mandatory migration regression: running migration twice never re-applies — once the legacy value is
    /// cleared, a second run is correctly reported as "nothing to do," which is what makes it safe for the operator
    /// to later deliberately empty a VIP's custom tell without it silently reappearing.</summary>
    [Fact] public void Migration_is_idempotent_across_repeated_calls()
    {
        var settings = new VipSettings([new("Ada", "Balmung", true, "", "Welcome", VipAnnouncementChannel.Shout)]) with { LegacyRecognitionTemplate = "Legacy" };
        var (once, changedOnce) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.True(changedOnce);
        var (twice, changedTwice) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(once);
        Assert.False(changedTwice);
        Assert.Equal(once, twice);
    }

    /// <summary>Mandatory migration regression: with several records, only the ones with an empty custom tell are
    /// backfilled — never the ones the operator (or an earlier partial migration) already populated.</summary>
    [Fact] public void Migration_only_backfills_the_still_empty_records_among_several()
    {
        var settings = new VipSettings([
            new("Ada", "Balmung", true, "", "Welcome A", VipAnnouncementChannel.Shout),
            new("Zeta", "Balmung", true, "Custom Zeta tell", "Welcome Z", VipAnnouncementChannel.Yell),
        ]) with { LegacyRecognitionTemplate = "Legacy template" };
        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.True(changed);
        Assert.Equal("Legacy template", migrated.Records[0].CustomTell);
        Assert.Equal("Custom Zeta tell", migrated.Records[1].CustomTell);
    }

    /// <summary>Mandatory migration regression: the old global setting must still bind from real persisted JSON under
    /// its original property name (not just via an in-memory <c>with</c> expression), since that is exactly the shape
    /// existing users' saved config is in.</summary>
    [Fact] public void Legacy_recognition_template_deserializes_from_the_old_json_property_name()
    {
        const string json = """{"RecognitionTemplate":"Welcome back, <name>!","Records":[{"CharacterName":"Ada","HomeWorld":"Balmung","Enabled":true,"PublicAnnouncement":"Welcome","Channel":0}]}""";
        var settings = System.Text.Json.JsonSerializer.Deserialize<VipSettings>(json)!;
        Assert.Equal("Welcome back, <name>!", settings.LegacyRecognitionTemplate);
        Assert.True(string.IsNullOrEmpty(settings.Records.Single().CustomTell)); // the old payload never had this field

        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(settings);
        Assert.True(changed);
        Assert.Equal("Welcome back, <name>!", migrated.Records.Single().CustomTell);
    }

    /// <summary>The exact live-verified Greeter failure, traced to its root: with no preset assigned to the active
    /// hotbar slot (<c>GreeterSettings.ActivePresetId</c> null — which is precisely what the configuration-storm bug
    /// silently forced it to on every reload, discarding whatever DJ slot the operator had actually selected),
    /// <see cref="GreeterService.QueueGreeting"/> must fail with an explicit, discoverable reason — not merely
    /// return false with no trace of why, which is indistinguishable from every other failure mode. Attendance
    /// detecting a guest while this silently fails is exactly what looked like "Attendance works, Greeter doesn't."</summary>
    [Fact] public void Greeter_with_no_active_preset_assigned_skips_with_an_explicit_reason()
    {
        var clock = new Clock(); using var db = NewDb(); var venue = Guid.NewGuid();
        var reasons = new List<string>();
        var greeter = new GreeterService(clock, Chat(clock, []), db, logDecision: reasons.Add); greeter.AttachVenue(venue);
        greeter.Configure(new GreeterSettings(Enabled: true, ActivePresetId: null));
        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.False(greeter.QueueGreeting(guest));
        Assert.Contains(reasons, r => r.Contains(guest.Key, StringComparison.Ordinal) && r.Contains("no active preset", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reading a module's persisted config (what every Settings page does, and what used to happen every
    /// single frame before the configuration-storm fix) must never disturb an already-running module's own live
    /// state — <see cref="GreeterService"/>'s in-flight queue is owned entirely by the service instance, and
    /// <see cref="VenueProfileService.GetModuleConfig{T}"/> is a pure read with no reference back to it.</summary>
    [Fact] public void Reading_greeter_config_via_VenueProfileService_does_not_disturb_an_in_flight_queue()
    {
        var clock = new Clock(); var chat = Chat(clock, []); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "hi <name>");
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        profiles.SaveModuleConfig(venue, "core.greeter", 1, greeter.Settings);
        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.True(greeter.QueueGreeting(guest));

        for (var i = 0; i < 500; i++) profiles.GetModuleConfig(venue, "core.greeter", 1, () => new GreeterSettings());

        Assert.Equal(1, greeter.PendingCount);
    }

    [Fact] public void Greeter_preset_library_can_hold_more_presets_than_the_five_hotbar_slots_and_slots_assign_from_it()
    {
        var clock = new Clock(); using var db = NewDb(); var venue = Guid.NewGuid();
        var greeter = new GreeterService(clock, Chat(clock, []), db); greeter.AttachVenue(venue);
        for (var i = 0; i < 8; i++) greeter.SavePreset(null, $"Preset {i}", "hi", "", "", "");
        Assert.Equal(8, greeter.GetPresetLibrary().Count);
        var first = greeter.GetPresetLibrary()[0];
        greeter.SetHotbarAssignment(1, first.Id);
        Assert.Equal(first.Id, greeter.GetHotbarAssignments()[1]);
        Assert.Null(greeter.GetHotbarAssignments()[2]);
    }

    /// <summary>The exact mandatory scenario reported broken live: DJ 1 assigned to one preset and DJ 2 to a
    /// *different* preset must both retain independently through <see cref="GreeterService"/> (the same service the
    /// Settings UI's hotbar ComboFields call), changing DJ 1 to a new preset must not affect DJ 2, "(Empty)" must
    /// clear a slot, and per-venue isolation must hold — a second <see cref="GreeterService"/> instance attached to
    /// a different venue id, sharing the same underlying database, must see none of it.</summary>
    [Fact] public void Greeter_two_slots_assigned_to_two_different_presets_retain_independently_and_stay_isolated_per_venue()
    {
        var clock = new Clock(); using var db = NewDb(); var venueA = Guid.NewGuid(); var venueB = Guid.NewGuid();
        var greeter = new GreeterService(clock, Chat(clock, []), db); greeter.AttachVenue(venueA);
        var presetA = greeter.SavePreset(null, "Preset A", "Hi A", "", "", "");
        var presetB = greeter.SavePreset(null, "Preset B", "Hi B", "", "", "");

        greeter.SetHotbarAssignment(1, presetA);
        greeter.SetHotbarAssignment(2, presetB);
        var afterAssign = greeter.GetHotbarAssignments();
        Assert.Equal(presetA, afterAssign[1]); Assert.Equal(presetB, afterAssign[2]);

        greeter.SetHotbarAssignment(1, presetB);
        var afterReassign = greeter.GetHotbarAssignments();
        Assert.Equal(presetB, afterReassign[1]); Assert.Equal(presetB, afterReassign[2]);

        greeter.SetHotbarAssignment(1, null);
        var afterClear = greeter.GetHotbarAssignments();
        Assert.Null(afterClear[1]); Assert.Equal(presetB, afterClear[2]);

        var otherVenueGreeter = new GreeterService(clock, Chat(clock, []), db); otherVenueGreeter.AttachVenue(venueB);
        Assert.Empty(otherVenueGreeter.GetPresetLibrary());
        Assert.Null(otherVenueGreeter.GetHotbarAssignments()[2]);
    }

    [Fact] public async Task Greeter_waits_the_inter_guest_delay_before_starting_the_next_guest()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "hi <name>");
        greeter.QueueGreeting(new("A", "Balmung")); greeter.QueueGreeting(new("B", "Balmung"));
        greeter.Tick(); await chat.TickAsync(); Assert.Single(sent);
        greeter.Tick(); await chat.TickAsync(); Assert.Single(sent);
        clock.Advance(2); greeter.Tick(); await chat.TickAsync(); Assert.Equal(2, sent.Count); Assert.Contains("hi B", sent[1]);
    }

    /// <summary>Mandatory requirement: the preset's raw "Line 4"/command slot is dispatched as a raw command (auto-
    /// prefixed with a leading slash if the operator forgot it) through the exact same transport and confirmation
    /// gating as the tell lines above it — never a second, unconfirmed path.</summary>
    [Fact] public async Task Greeter_dispatches_the_raw_line_4_command_through_the_same_confirmed_transport()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!", command: "dance");
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.True(greeter.QueueGreeting(guest));
        greeter.Tick(); await chat.TickAsync(); Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
        Assert.False(isGreetedFake(guest)); // Line 1 confirmed sent, but the raw command step hasn't run yet

        clock.Advance(2); greeter.Tick(); await chat.TickAsync();
        Assert.Equal(2, sent.Count); Assert.Equal("/dance", sent[1]); // auto-prefixed with a leading slash
        Assert.True(isGreetedFake(guest)); // only now, after both steps are confirmed sent, is the greeting complete
    }

    [Fact] public async Task Greeter_drops_a_guest_who_leaves_mid_sequence_without_marking_them_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var present = true;
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "one <name>", "two <name>", isPresent: _ => present);
        var guest = new GuestIdentity("Mair", "Balmung"); greeter.QueueGreeting(guest);
        greeter.Tick(); await chat.TickAsync(); Assert.Single(sent);
        present = false; clock.Advance(2); greeter.Tick();
        Assert.False(isGreetedFake(guest)); Assert.Equal(0, greeter.PendingCount);
        await chat.TickAsync(); Assert.Single(sent);
    }

    /// <summary>The coordinator's own idempotence — once <c>isGreeted</c> reports true, a second
    /// <see cref="GreetingSource.AutomaticArrival"/> attempt for the same guest is a no-op. (Whether the coordinator
    /// is even invoked twice for the same physical arrival is a separate concern, owned entirely by
    /// <see cref="AttendanceService.AutomaticGreetingEligible"/>'s own once-per-opening bookkeeping — see the
    /// mandatory regression tests for that.)</summary>
    [Fact] public async Task Vip_match_is_world_specific_disabled_safe_and_idempotent()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell), new("Off", "Balmung", false, "VIP <name>", "never", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        var guest = new GuestIdentity("Ada", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        await DrainWithGreeterTicks(chat, greeter, clock, 3);
        Assert.Equal(3, sent.Count); Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]); Assert.Contains("Welcome Ada", sent[1]); Assert.Equal("/yell Ada arrived", sent[2]);

        Assert.False(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival)); // already greeted — no duplicate
        await DrainWithGreeterTicks(chat, greeter, clock, 2); Assert.Equal(3, sent.Count);
    }

    [Fact] public async Task Vip_same_name_on_another_world_does_not_match()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "VIP public", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        // Ada@Mateus does not match the VIP record for Ada@Balmung — falls straight through to the normal Greeter,
        // with no VIP-only content leaking into the ordinary greeting.
        Assert.True(coordinator.TryGreet(new GuestIdentity("Ada", "Mateus"), GreetingSource.AutomaticArrival));
        greeter.Tick(); await Drain(chat, clock, 2);
        Assert.Single(sent); Assert.DoesNotContain("VIP Ada", sent[0], StringComparison.Ordinal);
    }

    [Fact] public async Task Vip_public_announcement_depends_on_greeter_finishing_not_a_competing_greeted_flag()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!", enabled: false);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        Assert.True(coordinator.TryGreet(new GuestIdentity("Ada", "Balmung"), GreetingSource.AutomaticArrival));
        greeter.Tick(); await Drain(chat, clock, 3);
        Assert.Single(sent); Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]);
    }

    [Fact] public async Task Vip_is_greeted_on_arrival_even_when_auto_greet_is_disabled_for_regular_guests()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!", autoGreetEnabled: false);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        Assert.True(coordinator.TryGreet(new GuestIdentity("Ada", "Balmung"), GreetingSource.AutomaticArrival));
        await DrainWithGreeterTicks(chat, greeter, clock, 3);
        Assert.Equal(3, sent.Count);
    }

    /// <summary>Mirrors the ONE consolidated production trigger exactly: <see cref="AttendanceService.AutomaticGreetingEligible"/>
    /// calling <see cref="GreetingCoordinator.TryGreet"/> unconditionally — no external VIP/non-VIP gating.</summary>
    [Fact] public async Task Full_arrival_pipeline_runs_attendance_then_vip_tell_then_greeter_then_vip_public_in_order()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var attendance = new AttendanceService(presence, db, clock); attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach();
        attendance.AutomaticGreetingEligible += guest => coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);

        presence.Tick(new(null, null, null)); // the opening-start seed scan — nobody present yet
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // a genuine post-open arrival
        Assert.Single(attendance.Guests);
        await DrainWithGreeterTicks(chat, greeter, clock, 3);
        Assert.Equal(3, sent.Count);
        Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]); Assert.Contains("Welcome Ada", sent[1]); Assert.Equal("/yell Ada arrived", sent[2]);
        Assert.True(isGreetedFake(new GuestIdentity("Ada", "Balmung")));
    }

    /// <summary>Mandatory requirement: VIP and normal guests must use the exact same Attendance greeted authority —
    /// no competing "VIP greeted" state. Wires a real <see cref="AttendanceService"/> (not the local fake) so a VIP
    /// arrival's eventual greeted state is verified through Attendance's own <see cref="AttendanceService.IsGreeted"/>,
    /// exactly like a normal guest.</summary>
    [Fact] public async Task Vip_guest_ends_up_greeted_through_the_same_attendance_authority_as_a_normal_guest()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, chat, db, isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", "Welcome <name>!", "", "", "");
        greeter.Configure(new GreeterSettings(ActivePresetId: presetId));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell)]));
        var coordinator = new GreetingCoordinator(g => attendanceRef!.IsGreeted(g), greeter, vip, chat);
        coordinator.Attach();
        attendance.AutomaticGreetingEligible += guest => coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);

        var guest = new GuestIdentity("Ada", "Balmung");
        Assert.False(attendance.IsGreeted(guest));
        presence.Tick(new(null, null, null)); // the opening-start seed scan — nobody present yet
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // a genuine post-open arrival
        await DrainWithGreeterTicks(chat, greeter, clock, 3);

        Assert.True(attendance.IsGreeted(guest));
        Assert.True(attendance.GetTonightVisitors().Single(v => v.CharacterName == "Ada").Greeted);
    }

    [Fact] public void A_new_visitor_begins_not_greeted()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, _) = NewWiredAttendanceAndGreeter(clock, presence, Chat(clock, []), db, venue);
        attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));

        Assert.False(attendance.IsGreeted(guest));
        Assert.False(attendance.GetTonightVisitors().Single().Greeted);
    }

    /// <summary>Greeter must never mark a guest greeted merely because they were queued — only a completed
    /// sequence may do that (see <see cref="GreeterService.Complete"/>'s doc comment). Queuing alone must leave
    /// Attendance's authoritative state untouched.</summary>
    [Fact] public void Queueing_a_guest_does_not_itself_mark_them_greeted()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, Chat(clock, []), db, venue);
        attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));

        Assert.True(greeter.QueueGreeting(guest));
        Assert.False(attendance.IsGreeted(guest)); // queued, but not yet greeted
        Assert.Equal(1, greeter.PendingCount);
    }

    /// <summary>A guest who leaves before their greeting starts, or mid-sequence, must be left Not Greeted in
    /// Attendance so a legitimate later greeting remains possible — donor semantics, unchanged by this fix, now
    /// verified against the real authoritative store instead of a local flag.</summary>
    [Fact] public async Task Aborted_greeting_leaves_attendance_not_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var present = true;
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue, isPresent: _ => present);
        attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));

        Assert.True(greeter.QueueGreeting(guest));
        present = false; // leaves before their turn comes up
        greeter.Tick();

        Assert.False(attendance.IsGreeted(guest));
        Assert.Empty(sent); // no tell was ever sent
    }

    /// <summary>The exact scenario behind the live-reported bug: a guest greeted in one opening, then a *new*
    /// opening starts in the same venue visit. Attendance's own per-opening scoping (a fresh <c>session_visitors</c>
    /// row) must show them Not Greeted again, and — critically — Greeter must not block a fresh auto-greet with a
    /// stale in-memory flag, since it no longer has one; it always asks Attendance for the *current* opening.</summary>
    [Fact] public async Task A_new_opening_resets_greeted_scope_and_greeter_does_not_block_on_stale_state()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var guest = new GuestIdentity("Rabid Squirrel", "Halicarnassus");

        attendance.StartSession(false, false, clock.UtcNow);
        provider.Items = [Player("Rabid Squirrel", "Halicarnassus")]; presence.Tick(new(null, null, null));
        Assert.True(greeter.QueueGreeting(guest));
        greeter.Tick(); await chat.TickAsync();
        Assert.True(attendance.IsGreeted(guest));
        attendance.CloseSession(clock.UtcNow);

        clock.Advance(60);
        attendance.StartSession(false, false, clock.UtcNow);
        Assert.False(attendance.IsGreeted(guest)); // fresh opening, fresh greeted scope
        provider.Items = []; presence.Tick(new(null, null, null));
        clock.Advance(1); provider.Items = [Player("Rabid Squirrel", "Halicarnassus")]; presence.Tick(new(null, null, null));

        Assert.True(greeter.QueueGreeting(guest)); // NOT blocked by a stale flag left over from the previous opening
    }

    /// <summary>Leaving and re-entering the *same* opening must not duplicate a normal auto-greet — Attendance still
    /// reports Greeted (per-opening, not per-presence-tick), so Greeter's authority check continues to reject it.</summary>
    [Fact] public async Task Guest_leaving_and_returning_within_the_same_opening_stays_greeted_and_is_not_re_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));
        Assert.True(greeter.QueueGreeting(guest));
        greeter.Tick(); await chat.TickAsync();
        Assert.True(attendance.IsGreeted(guest)); Assert.Single(sent);

        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null)); // leaves
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // re-enters, same opening

        Assert.True(attendance.IsGreeted(guest));
        Assert.False(greeter.QueueGreeting(guest)); // still rejected — no duplicate normal auto-greet
        Assert.Single(sent); // nothing new was sent
    }

    /// <summary>A fresh <see cref="GreeterService"/> instance (simulating a plugin reload) has no state of its own
    /// to go stale — it always asks whatever <c>isGreeted</c> delegate it's given, so reconnecting it to the same
    /// Attendance session immediately sees the correct, already-persisted greeted state with nothing to reconcile.</summary>
    [Fact] public void Reconnecting_a_fresh_greeter_instance_to_the_same_session_sees_correct_state_immediately()
    {
        var clock = new Clock(); var provider = new Provider(); var presence = new PresenceService(clock, provider); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, _) = NewWiredAttendanceAndGreeter(clock, presence, Chat(clock, []), db, venue);
        attendance.StartSession(false, false, clock.UtcNow);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));
        attendance.MarkGreeted(guest, true, clock.UtcNow);

        // A brand-new GreeterService, as if the plugin had just reloaded — no shared state with the one above,
        // only the same isGreeted delegate reading the same persisted Attendance/database session.
        var reloadedGreeter = new GreeterService(clock, Chat(clock, []), db, isGreeted: g => attendance.IsGreeted(g));
        reloadedGreeter.AttachVenue(venue);
        reloadedGreeter.Configure(new GreeterSettings(ActivePresetId: db.GetPresets(venue).Single().Id));

        Assert.False(reloadedGreeter.QueueGreeting(guest)); // correctly sees "already greeted" with zero reconciliation step
    }

    /// <summary>Live-verified correction, mandatory regression (test #1 and #2): a normal guest already standing in
    /// the venue when an opening starts must be seeded Not Greeted — counted, tracked — with zero automatic
    /// coordinator calls. Critically, this must NOT permanently consume their automatic-greeting eligibility: once
    /// they genuinely leave and return, that IS an eligible arrival — exactly one coordinator call, reaching
    /// Greeter, completing normally. This is the exact live-verified bug this pass fixes: opening-start seeding
    /// still called <see cref="IVenueDatabase.MarkPresent"/>, which silently consumed the guest's calendar-night
    /// "first visit" database flag even though the (now purely informational) <see cref="AttendanceService.FirstVisitTonight"/>
    /// event itself was correctly suppressed for the seed — so their later real arrival came back as "visit #2," and
    /// the OLD design (which used that same event as the automatic-greeting trigger) could never fire again for them
    /// for the rest of the night. <see cref="AttendanceService.AutomaticGreetingEligible"/> is immune to this because
    /// it is tracked entirely in memory, session-scoped, independent of the database's visit count.</summary>
    [Fact] public async Task Already_present_non_vip_is_seeded_not_greeted_then_becomes_eligible_on_genuine_re_entry()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider { Items = [Player("Mair", "Balmung")] }; var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var eligibleCount = 0;
        attendance.AutomaticGreetingEligible += guest => { eligibleCount++; coordinator.TryGreet(guest, GreetingSource.AutomaticArrival); };

        attendance.StartSession(false, false, clock.UtcNow); // Mair is already standing there before this call
        presence.Tick(new(null, null, null)); // the seed scan rediscovers her

        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.Single(attendance.GetTonightVisitors()); // seeded — present, counted
        Assert.False(attendance.GetTonightVisitors().Single().Greeted);
        Assert.Equal(1, attendance.GetTonightVisitors().Single().Visits);
        Assert.Equal(0, eligibleCount); // never treated as an eligible arrival
        Assert.Equal(0, greeter.PendingCount);
        Assert.Empty(sent);

        // She genuinely leaves and returns — this IS an eligible automatic-greeting arrival.
        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null));
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));

        Assert.Equal(1, eligibleCount);
        Assert.Equal(2, attendance.GetTonightVisitors().Single().Visits); // Attendance's own visit count is unaffected by this fix
        greeter.Tick(); await chat.TickAsync();
        Assert.True(attendance.IsGreeted(guest));
        Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
    }

    /// <summary>Live-verified correction, mandatory regression (test #3): applies equally to a VIP — being
    /// enabled-VIP does not exempt someone from opening-start seeding semantics, and a genuine re-entry afterward
    /// runs the full VIP recognition → Greeter → public announcement sequence exactly once.</summary>
    [Fact] public async Task Already_present_vip_is_seeded_not_greeted_then_full_vip_sequence_on_genuine_re_entry()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider { Items = [Player("Ada", "Balmung")] }; var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        coordinator.Attach();
        var eligibleCount = 0;
        attendance.AutomaticGreetingEligible += guest => { eligibleCount++; coordinator.TryGreet(guest, GreetingSource.AutomaticArrival); }; // the ONE consolidated automatic trigger, exactly like production

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan rediscovers Ada

        var guest = new GuestIdentity("Ada", "Balmung");
        Assert.False(attendance.IsGreeted(guest));
        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest));
        Assert.Equal(0, eligibleCount);
        Assert.Equal(0, greeter.PendingCount);
        Assert.Empty(sent);

        // She genuinely leaves and returns — this IS an eligible automatic-greeting arrival.
        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null));
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));

        Assert.Equal(1, eligibleCount);
        await DrainWithGreeterTicks(chat, greeter, clock, 3);
        Assert.Equal(3, sent.Count);
        Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]); Assert.Contains("Welcome Ada", sent[1]); Assert.Equal("/shout Ada arrived", sent[2]);
        Assert.True(attendance.IsGreeted(guest));
    }

    /// <summary>Mandatory regression (test #5): a seeded guest the operator marks handled via "Mark Greeted" sends
    /// nothing, and a subsequent genuine re-entry — even though it IS an eligible arrival — must not auto-greet,
    /// since Attendance already reports them Greeted (<see cref="GreetingCoordinator.TryGreet"/>'s own idempotence,
    /// unrelated to whether the eligibility event itself fires).</summary>
    [Fact] public void Seeded_guest_manually_mark_greeted_then_genuine_re_entry_does_not_auto_greet()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider { Items = [Player("Mair", "Balmung")] }; var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        attendance.AutomaticGreetingEligible += guest => coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan rediscovers Mair

        var guest = new GuestIdentity("Mair", "Balmung");
        attendance.MarkGreeted(guest, true, clock.UtcNow); // operator marks the seeded guest handled — sends nothing

        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null)); // leaves
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // genuinely re-enters

        Assert.True(attendance.IsGreeted(guest));
        Assert.Empty(sent); // no automatic greeting despite the genuine, eligible re-entry
        Assert.Equal(0, greeter.PendingCount);
    }

    /// <summary>Mandatory regression (test #6): a seeded guest the operator successfully greets manually (the full
    /// "Greet" pipeline, not "Mark Greeted") must not receive a duplicate greeting on a later genuine re-entry.</summary>
    [Fact] public async Task Seeded_guest_manually_greeted_successfully_then_genuine_re_entry_does_not_duplicate()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider { Items = [Player("Mair", "Balmung")] }; var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        attendance.AutomaticGreetingEligible += guest => coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan rediscovers Mair

        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance)); // operator clicks Greet on the seeded guest
        greeter.Tick(); await chat.TickAsync();
        Assert.True(attendance.IsGreeted(guest));
        Assert.Single(sent);

        provider.Items = []; clock.Advance(1); presence.Tick(new(null, null, null)); // leaves
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // genuinely re-enters

        Assert.Single(sent); // no duplicate greeting
    }

    /// <summary>Mandatory regression, the counterpart to the two seeded-population tests above: a guest who genuinely
    /// walks in only after the opening is already active remains fully eligible for automatic greeting — seeding
    /// suppresses exactly one scan, not the whole opening.</summary>
    [Fact] public void A_guest_arriving_after_the_opening_has_started_is_eligible_for_automatic_greeting()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        // The ONE consolidated automatic trigger — no external VIP/non-VIP gating here; GreetingCoordinator.TryGreet
        // decides that internally (see its type-level remark for why a second gating layer out here was the bug).
        attendance.AutomaticGreetingEligible += guest => coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan — nobody present at opening start

        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // a genuine post-open arrival
        Assert.Equal(1, greeter.PendingCount);
    }

    /// <summary>Mandatory regression: a genuine post-start non-VIP arrival must invoke
    /// <see cref="GreetingCoordinator.TryGreet"/> exactly once (never zero, never two competing event chains — the
    /// exact bug this consolidation pass fixes), reach Greeter, and Attendance must become Greeted only after
    /// Greeter's sequence actually completes.</summary>
    [Fact] public async Task Post_start_non_vip_arrival_triggers_exactly_one_coordinator_call_and_completes()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var callCount = 0;
        attendance.AutomaticGreetingEligible += guest => { callCount++; coordinator.TryGreet(guest, GreetingSource.AutomaticArrival); };

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan — nobody present

        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // genuine post-open arrival

        Assert.Equal(1, callCount);
        greeter.Tick(); await chat.TickAsync();
        Assert.True(attendance.IsGreeted(guest));
        Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
    }

    /// <summary>Mandatory regression: a genuine post-start enabled-VIP arrival must invoke
    /// <see cref="GreetingCoordinator.TryGreet"/> exactly once, running the full VIP recognition → Greeter → public
    /// announcement sequence, ending with Attendance Greeted.</summary>
    [Fact] public async Task Post_start_enabled_vip_arrival_triggers_exactly_one_coordinator_call_and_completes_full_sequence()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell)]));
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        coordinator.Attach();
        var callCount = 0;
        attendance.AutomaticGreetingEligible += guest => { callCount++; coordinator.TryGreet(guest, GreetingSource.AutomaticArrival); };

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan — nobody present

        var guest = new GuestIdentity("Ada", "Balmung");
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // genuine post-open arrival

        Assert.Equal(1, callCount);
        await DrainWithGreeterTicks(chat, greeter, clock, 3);
        Assert.Equal(3, sent.Count);
        Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]); Assert.Contains("Welcome Ada", sent[1]); Assert.Equal("/yell Ada arrived", sent[2]);
        Assert.True(attendance.IsGreeted(guest));
    }

    /// <summary>Mandatory regression: a disabled VIP record must still invoke the coordinator exactly once and be
    /// treated as an ordinary guest — never dropped, never given VIP treatment.</summary>
    [Fact] public async Task Post_start_disabled_vip_record_triggers_exactly_one_coordinator_call_and_greets_normally()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", false, "VIP <name>", "never sent", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var callCount = 0;
        attendance.AutomaticGreetingEligible += guest => { callCount++; coordinator.TryGreet(guest, GreetingSource.AutomaticArrival); };

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seed scan — nobody present

        var guest = new GuestIdentity("Ada", "Balmung");
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null)); // genuine post-open arrival

        Assert.Equal(1, callCount);
        greeter.Tick(); await chat.TickAsync();
        Assert.Single(sent); Assert.Contains("Welcome Ada", sent[0]); // only the normal greeting — no VIP tell, no public announcement
        Assert.True(attendance.IsGreeted(guest));
    }

    /// <summary>Mandatory regression: <see cref="GreetingCoordinator.Attach"/> no longer subscribes to
    /// <see cref="PresenceService.Arrived"/> at all — a VIP's own presence arrival, with no
    /// <see cref="AttendanceService.AutomaticGreetingEligible"/> wiring in the picture, must produce no automatic
    /// greeting whatsoever. There is exactly one automatic route (through Attendance), not a second competing one.</summary>
    [Fact] public void Coordinator_attach_no_longer_reacts_to_presence_arrivals_directly()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        coordinator.Attach(); // no PresenceService parameter — nothing left to subscribe to Arrived with

        provider.Items = [Player("Ada", "Balmung")]; presence.Tick(new(null, null, null)); // a VIP arrives, with no AttendanceService.AutomaticGreetingEligible wiring at all
        Assert.Empty(sent);
        Assert.Equal(0, greeter.PendingCount);
    }

    /// <summary>Mandatory regression: the manual "Greet" action must still run the exact same shared orchestrator
    /// for a seeded guest as it would for anyone else — no leave/re-enter required to make them reachable.</summary>
    [Fact] public async Task Manual_greet_on_a_seeded_guest_runs_the_full_pipeline()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider { Items = [Player("Mair", "Balmung")] }; var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);

        attendance.StartSession(false, false, clock.UtcNow);
        presence.Tick(new(null, null, null)); // seeded, not auto-greeted

        var guest = new GuestIdentity("Mair", "Balmung");
        Assert.False(attendance.IsGreeted(guest));

        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        greeter.Tick(); await chat.TickAsync();

        Assert.True(attendance.IsGreeted(guest));
        Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
    }

    /// <summary>Mandatory requirement: a non-VIP guest's lookup returning "not VIP" must not terminate the pipeline —
    /// <see cref="GreetingCoordinator.TryGreet"/> must continue straight to the normal Greeter and actually send.</summary>
    [Fact] public async Task GreetingCoordinator_lets_a_non_vip_guest_continue_to_the_normal_greeter()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService(); // no VIP records configured at all
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        greeter.Tick(); await chat.TickAsync();
        Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
        Assert.True(isGreetedFake(guest));
    }

    /// <summary>Mandatory requirement: a disabled VIP record must be treated exactly like no VIP record at all — the
    /// guest still receives the normal greeting, never dropped, and never receives the VIP tell/public announcement.</summary>
    [Fact] public async Task GreetingCoordinator_treats_a_disabled_vip_record_exactly_like_no_vip_record()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Mair", "Balmung", false, "VIP <name>", "never sent", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        greeter.Tick(); await chat.TickAsync();
        Assert.Single(sent); // only the normal greeting — no VIP tell, no public announcement
        Assert.Contains("Welcome Mair", sent[0]);
    }

    /// <summary>An already-greeted guest must short-circuit before any VIP lookup side effect — no tell, no queue,
    /// nothing sent, matching "Mark Greeted disables Greet" from the operator's perspective.</summary>
    [Fact] public void GreetingCoordinator_skips_an_already_greeted_guest_without_sending_anything()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, _) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Mair", "Balmung", true, "VIP <name>", "hi", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(_ => true, greeter, vip, chat);

        Assert.False(coordinator.TryGreet(new GuestIdentity("Mair", "Balmung"), GreetingSource.AutomaticArrival));
        Assert.Empty(sent);
    }

    /// <summary>The manual "Greet" action (Attendance Visitors) must invoke the same shared orchestrator as automatic
    /// arrival — not a direct <see cref="GreeterService.QueueGreeting"/> call — and Attendance may only flip to
    /// Greeted once Greeter's sequence actually completes, never merely because the attempt was queued.</summary>
    [Fact] public async Task Manual_greet_runs_the_full_workflow_for_a_normal_guest_and_only_then_marks_attendance_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var (attendance, greeter) = NewWiredAttendanceAndGreeter(clock, presence, chat, db, venue);
        attendance.StartSession(false, false, clock.UtcNow);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");
        provider.Items = [Player("Mair", "Balmung")]; presence.Tick(new(null, null, null));

        Assert.False(attendance.IsGreeted(guest));
        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        Assert.False(attendance.IsGreeted(guest)); // queued, not yet completed
        greeter.Tick(); await chat.TickAsync();

        Assert.True(attendance.IsGreeted(guest));
        Assert.Single(sent); Assert.Contains("Welcome Mair", sent[0]);
    }

    /// <summary>Same manual-Greet entry point, but for a VIP guest: the recognition tell, the normal Greeter
    /// greeting, and the public announcement must all run in the approved order, and Attendance becomes Greeted only
    /// after that full sequence completes — never merely because VIP processing started (the exact live-reported bug
    /// this task exists to fix).</summary>
    [Fact] public async Task Manual_greet_runs_the_full_vip_sequence_and_only_marks_attendance_greeted_after_greeter_completes()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, chat, db, isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", "Welcome <name>!", "", "", "");
        greeter.Configure(new GreeterSettings(ActivePresetId: presetId));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell)]));
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var guest = new GuestIdentity("Ada", "Balmung");
        provider.Items = [Player("Ada", "Balmung")]; presence.Tick(new(null, null, null)); // seeds attendance's presence — no automatic trigger wired in this test, so the manual click below is the only trigger
        coordinator.Attach(); // wires GreeterService.GreetingReadyToFinalize -> the VIP public-announcement step, exactly as production does once at startup

        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        Assert.False(attendance.IsGreeted(guest)); // VIP tell only enqueued so far — not yet confirmed sent, let alone greeted

        await DrainWithGreeterTicks(chat, greeter, clock, 3);

        Assert.Equal(3, sent.Count);
        Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]); Assert.Contains("Welcome Ada", sent[1]); Assert.Equal("/yell Ada arrived", sent[2]);
        Assert.True(attendance.IsGreeted(guest));
    }

    /// <summary>If the VIP tell goes out but Greeter's own handoff fails (here: no active preset assigned), the
    /// attempt must count as failed — no in-flight state left stranded, and critically, never a greeted flag set.</summary>
    [Fact] public async Task Failed_greeter_handoff_after_a_vip_tell_does_not_mark_attendance_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        var greeter = new GreeterService(clock, chat, db); greeter.AttachVenue(venue); // no active preset configured
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(_ => false, greeter, vip, chat);
        var guest = new GuestIdentity("Ada", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival)); // the VIP tell was submitted — confirmation (and the Greeter handoff it gates) hasn't happened yet
        await chat.TickAsync(); // confirms the VIP tell was sent, which is what triggers (and then fails) the Greeter handoff
        Assert.Single(sent); // the VIP tell alone was sent
        Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]);
        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest)); // nothing left in flight to strand
    }

    /// <summary>Backs the Attendance Visitors row state machine: Not Greeted → Queued (while pending) → Greeting...
    /// (once Greeter dequeues it) → back to None once completed (at which point Attendance itself is now Greeted, so
    /// the row switches to the Greeted state and hides Greet/Mark Greeted entirely).</summary>
    [Fact] public async Task GreetingCoordinator_getprogress_reflects_queued_then_in_progress_then_clears_after_completion()
    {
        var clock = new Clock(); var chat = Chat(clock, []); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "hi <name>", delay: 5);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest));
        coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);
        Assert.Equal(GreetingProgress.Queued, coordinator.GetProgress(guest));

        greeter.Tick(); // dequeues and starts the delay countdown
        Assert.Equal(GreetingProgress.InProgress, coordinator.GetProgress(guest));

        clock.Advance(5); greeter.Tick(); await chat.TickAsync();
        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest));
        Assert.True(isGreetedFake(guest));
    }

    /// <summary>A second manual-Greet click (or an automatic trigger racing a manual one) while an attempt is already
    /// queued must not duplicate it — the shared orchestrator defers entirely to Greeter's own idempotence guard.</summary>
    [Fact] public void GreetingCoordinator_does_not_duplicate_an_already_queued_attempt()
    {
        var clock = new Clock(); var chat = Chat(clock, []); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "hi <name>");
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        Assert.False(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        Assert.Equal(1, greeter.PendingCount);
    }

    /// <summary>Mandatory transport-failure requirement: a step the transport itself rejects (the exact class of
    /// live-verified bug — a command can appear to be dispatched with no exception thrown, yet never actually reach
    /// FFXIV) must abort the greeting cleanly, not silently complete it. The guest is left Not Greeted and not
    /// stranded in a transient "Greeting..." state — see <see cref="GreeterService.Drop"/>'s effect via <see cref="GreeterService.GetProgress"/>.</summary>
    [Fact] public async Task Failing_chat_transport_aborts_a_normal_greeting_and_leaves_attendance_not_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = ChatWithFailure(clock, sent, _ => true); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Mair", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival));
        greeter.Tick(); await chat.TickAsync();

        Assert.Empty(sent); // the transport never actually accepted the message
        Assert.False(isGreetedFake(guest));
        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest)); // dropped cleanly, never stranded "Greeting..."
        Assert.Equal(0, greeter.PendingCount);
    }

    /// <summary>Mandatory requirement: a VIP recognition tell the transport rejects must abort before Greeter is ever
    /// handed the guest — the exact live-verified bug where a VIP arrival ended up marked Greeted despite neither the
    /// recognition tell nor the normal greeting ever actually transmitting.</summary>
    [Fact] public async Task Failing_vip_recognition_tell_aborts_before_reaching_greeter_and_never_marks_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var chat = ChatWithFailure(clock, sent, text => text.StartsWith("/tell", StringComparison.Ordinal)); using var db = NewDb(); var venue = Guid.NewGuid();
        var (greeter, _, isGreetedFake) = NewGreeterWithPreset(clock, chat, db, venue, "Welcome <name>!");
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Shout)]));
        var coordinator = new GreetingCoordinator(isGreetedFake, greeter, vip, chat);
        var guest = new GuestIdentity("Ada", "Balmung");

        Assert.True(coordinator.TryGreet(guest, GreetingSource.AutomaticArrival)); // an attempt started — the tell was only just submitted
        await chat.TickAsync(); // the transport rejects the VIP tell

        Assert.Empty(sent);
        Assert.Equal(0, greeter.PendingCount); // Greeter was never handed the guest
        Assert.False(isGreetedFake(guest));
        Assert.Equal(GreetingProgress.None, coordinator.GetProgress(guest));
    }

    /// <summary>Mandatory requirement: the VIP recognition tell being confirmed sent is not itself completion — Attendance
    /// must remain Not Greeted until Greeter's own sequence actually finishes afterward.</summary>
    [Fact] public async Task Vip_recognition_tell_success_alone_does_not_mark_attendance_greeted()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider); var chat = Chat(clock, sent); using var db = NewDb(); var venue = Guid.NewGuid();
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, chat, db, isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", "Welcome <name>!", "", "", "");
        greeter.Configure(new GreeterSettings(ActivePresetId: presetId));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        attendance.AttachVenue(venue); attendance.Attach(); attendance.StartSession(false, false, clock.UtcNow);
        var vip = new VipOrchestrationService();
        vip.Configure(new([new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell)]));
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat);
        var guest = new GuestIdentity("Ada", "Balmung");
        provider.Items = [Player("Ada", "Balmung")]; presence.Tick(new(null, null, null));

        Assert.True(coordinator.TryGreet(guest, GreetingSource.ManualAttendance));
        await chat.TickAsync(); // confirms the VIP recognition tell was sent — Greeter is only just now being handed the guest

        Assert.Single(sent); Assert.StartsWith("/tell Ada@Balmung VIP Ada", sent[0]);
        Assert.False(attendance.IsGreeted(guest)); // the tell succeeding is not completion — Greeter hasn't even started yet
        Assert.Equal(1, greeter.PendingCount);
    }

    private static (AttendanceService Attendance, GreeterService Greeter) NewWiredAttendanceAndGreeter(Clock clock, PresenceService presence, ChatCommandService chat, SqliteVenueDatabase db, Guid venue, string line1 = "Welcome <name>!", bool autoGreetEnabled = true, Func<GuestIdentity, bool>? isPresent = null)
    {
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, chat, db, isPresent, isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", line1, "", "", "");
        greeter.Configure(new GreeterSettings(AutoGreetEnabled: autoGreetEnabled, ActivePresetId: presetId));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        attendance.AttachVenue(venue); attendance.Attach();
        return (attendance, greeter);
    }

    [Fact] public void TargetedPlayerLookup_helpers_carry_identity_on_success_and_a_reason_on_failure()
    {
        var found = TargetedPlayerLookup.Found("Ada", "Balmung"); Assert.True(found.Success); Assert.Equal("Ada", found.Name); Assert.Equal("Balmung", found.HomeWorld); Assert.Null(found.Error);
        var failed = TargetedPlayerLookup.Failed("No target selected."); Assert.False(failed.Success); Assert.Equal("No target selected.", failed.Error);
    }

    /// <summary>Wires a minimal, self-contained fake "greeted" store (a plain <see cref="HashSet{T}"/>, populated
    /// only via the same <c>onGreetingCompleted</c> callback production code uses) so tests exercising Greeter's own
    /// mechanics (timing, presets, hotbar, VIP orchestration ordering) don't each need a full
    /// <see cref="AttendanceService"/>/<see cref="SqliteVenueDatabase"/> session just to answer "is this guest
    /// greeted" — Greeter itself no longer knows or cares that the real answer normally comes from Attendance
    /// (see <see cref="GreeterService"/>'s type-level doc comment); it only ever asks whatever <c>isGreeted</c>
    /// delegate it was given. Tests that specifically verify the Attendance/Greeter integration wire a real
    /// <see cref="AttendanceService"/> directly instead (see e.g.
    /// <see cref="Attendance_mark_as_greeted_updates_greeters_authoritative_state_and_blocks_future_auto_greet"/>).</summary>
    private static (GreeterService Greeter, long PresetId, Func<GuestIdentity, bool> IsGreetedFake) NewGreeterWithPreset(Clock clock, ChatCommandService chat, SqliteVenueDatabase db, Guid venue, string line1, string line2 = "", string line3 = "", string command = "", bool enabled = true, int delay = 0, bool autoGreetEnabled = true, Func<GuestIdentity, bool>? isPresent = null)
    {
        var greetedFake = new HashSet<string>(StringComparer.Ordinal);
        var greeter = new GreeterService(clock, chat, db, isPresent, isGreeted: g => greetedFake.Contains(g.Key), onGreetingCompleted: g => greetedFake.Add(g.Key)); greeter.AttachVenue(venue);
        var presetId = db.SavePreset(venue, null, "DJ", line1, line2, line3, command);
        greeter.Configure(new GreeterSettings(AutoGreetEnabled: autoGreetEnabled, GreetDelaySeconds: delay, Enabled: enabled, ActivePresetId: presetId));
        return (greeter, presetId, g => greetedFake.Contains(g.Key));
    }
    private static SqliteVenueDatabase NewDb() => new("Data Source=:memory:");
    private static async Task Drain(ChatCommandService chat, Clock clock, int count) { for (var i = 0; i < count; i++) { await chat.TickAsync(); clock.Advance(1); } }
    /// <summary>Since a Greeter step now only advances once <see cref="ChatCommandService"/> confirms it was actually
    /// sent (see <see cref="GreeterService.Tick"/>'s doc comment), a multi-message sequence (VIP tell → Greeter line
    /// → VIP public announcement) needs <see cref="GreeterService.Tick"/> interleaved with each dispatch, not just
    /// called once up front — mirrors production's per-frame <c>chat.TickAsync(); ...; greeter.Tick();</c> ordering
    /// closely enough for tests (call order between the two doesn't matter here since <see cref="GreeterService.Tick"/>
    /// is a no-op whenever nothing new is ready).</summary>
    private static async Task DrainWithGreeterTicks(ChatCommandService chat, GreeterService greeter, Clock clock, int count) { for (var i = 0; i < count; i++) { greeter.Tick(); await chat.TickAsync(); clock.Advance(1); } }
    private static ChatCommandService Chat(Clock clock, List<string> sent) => new(clock, new InlineFrameworkDispatcher(), x => { sent.Add(x); return true; }, TimeSpan.Zero);
    /// <summary>Simulates a real chat-transport failure (the exact live-verified failure mode this task exists to
    /// guard against: a transport call that reports it did not send) for any command matching <paramref name="shouldFail"/>
    /// — everything else dispatches normally and is recorded in <paramref name="sent"/>.</summary>
    private static ChatCommandService ChatWithFailure(Clock clock, List<string> sent, Func<string, bool> shouldFail) => new(clock, new InlineFrameworkDispatcher(), x => { if (shouldFail(x)) return false; sent.Add(x); return true; }, TimeSpan.Zero);
    private static PlayerSnapshot Player(string name, string world) => new(name, world, 1, 1, Vector3.Zero);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch; public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds); }
    private sealed class Provider : IObjectSnapshotProvider { public IReadOnlyList<PlayerSnapshot> Items { get; set; } = []; public IReadOnlyList<PlayerSnapshot> Snapshot() => Items; }
}
