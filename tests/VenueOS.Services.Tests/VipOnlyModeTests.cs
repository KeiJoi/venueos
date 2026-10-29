using System.Numerics;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>VenueOS 0.3.11 VIP Only Mode: Attendance still tracks everyone; only the greeting decision changes, and
/// only for the local opening that was started in that mode. Every test wires the real Attendance → Greeter → VIP →
/// coordinator pipeline the same way <c>Plugin.cs</c> does (see <see cref="Rig"/>), not isolated helpers.</summary>
public sealed class VipOnlyModeTests
{
    private static readonly VipRecord Ada = new("Ada", "Balmung", true, "VIP <name>", "Ada arrived", VipAnnouncementChannel.Yell);

    // ---- Defaults and state plumbing -------------------------------------------------------------------------

    [Fact] public void Vip_only_mode_defaults_off_for_a_fresh_service_and_a_default_start()
    {
        using var rig = new Rig();
        Assert.False(rig.Attendance.VipOnlyMode); Assert.False(rig.Coordinator.IsVipOnlyMode);
        rig.Attendance.StartSession(false, PresenceAreaMode.FollowOperator, rig.Clock.UtcNow); // no mode argument — every pre-0.3.11 caller
        Assert.False(rig.Attendance.VipOnlyMode); Assert.False(rig.Coordinator.IsVipOnlyMode);
    }

    [Fact] public void Selected_mode_reaches_the_attendance_session_and_the_greeting_coordinator()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: true);
        Assert.True(rig.Attendance.VipOnlyMode); Assert.True(rig.Coordinator.IsVipOnlyMode);
    }

    [Fact] public void Mode_is_applied_before_session_opened_listeners_run()
    {
        using var rig = new Rig();
        bool? seen = null; rig.Attendance.SessionOpened += () => seen = rig.Attendance.VipOnlyMode;
        rig.Start(vipOnly: true);
        Assert.True(seen);
    }

    // ---- Scenario C: normal mode unchanged ---------------------------------------------------------------------

    [Fact] public async Task Normal_mode_still_greets_ordinary_guests_with_the_greeter_preset()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Mair", "Balmung"));
        await rig.Pump(4);
        Assert.Equal(["/tell Mair@Balmung Welcome Mair!"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(new("Mair", "Balmung")));
    }

    [Fact] public async Task Normal_mode_vip_flow_is_unchanged_tell_then_greeter_then_public()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(5);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/tell Ada@Balmung Welcome Ada!", "/yell Ada arrived"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Normal_mode_ignores_the_vip_module_flag_exactly_as_before()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Coordinator.VipModuleEnabled = false;
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(5);
        Assert.Equal(3, rig.Sent.Count); // normal-mode routing never consulted the VIP module's enabled state
    }

    // ---- Scenario A: ordinary guest in VIP Only Mode -----------------------------------------------------------

    [Fact] public async Task Vip_only_mode_records_ordinary_guests_but_sends_and_queues_nothing()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Mair", "Balmung"), Player("Zeta", "Coeurl"));

        Assert.Equal(2, rig.Attendance.Guests.Count);
        Assert.Equal(2, rig.Attendance.GetTonightVisitors().Count);
        Assert.Equal(0, rig.Greeter.PendingCount); // never queued — not queued-then-cancelled
        await rig.Pump(6);
        Assert.Empty(rig.Sent);
        Assert.Equal(0, rig.Greeter.PendingCount);
        Assert.False(rig.Attendance.IsGreeted(new("Mair", "Balmung")));
        Assert.Equal(GreetingProgress.None, rig.Coordinator.GetProgress(new("Mair", "Balmung")));
        Assert.Equal(2, rig.Attendance.GetTonightSummary()!.UniqueGuests);
    }

    [Fact] public async Task Vip_only_mode_records_departures_and_visits_exactly_like_normal_mode()
    {
        async Task<(int Visits, bool Present, int Unique)> Run(bool vipOnly)
        {
            using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
            rig.Start(vipOnly);
            rig.Arrive(Player("Mair", "Balmung"), Player("Ada", "Balmung"));
            rig.Arrive(Player("Ada", "Balmung"));
            rig.Arrive(Player("Mair", "Balmung"), Player("Ada", "Balmung"));
            await rig.Pump(6);
            var mair = rig.Attendance.GetTonightVisitors().Single(v => v.CharacterName == "Mair");
            return (mair.Visits, mair.IsPresent, rig.Attendance.GetTonightSummary()!.UniqueGuests);
        }
        Assert.Equal(await Run(false), await Run(true));
    }

    [Fact] public void Manual_greet_is_not_offered_or_accepted_for_an_ordinary_guest_in_vip_only_mode()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Mair", "Balmung"));
        var mair = new GuestIdentity("Mair", "Balmung");
        Assert.False(rig.Coordinator.CanGreet(mair));
        Assert.False(rig.Coordinator.TryGreet(mair, GreetingSource.ManualAttendance));
        Assert.Equal(0, rig.Greeter.PendingCount);
        Assert.True(rig.Coordinator.CanGreet(new("Ada", "Balmung")));
    }

    [Fact] public void Manual_greet_is_offered_for_everyone_in_normal_mode()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: false);
        Assert.True(rig.Coordinator.CanGreet(new("Mair", "Balmung")));
        Assert.True(rig.Coordinator.CanGreet(new("Ada", "Balmung")));
    }

    [Fact] public void Mark_greeted_still_works_for_an_ordinary_guest_in_vip_only_mode()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Mair", "Balmung"));
        rig.Attendance.MarkGreeted(new("Mair", "Balmung"), true, rig.Clock.UtcNow);
        Assert.True(rig.Attendance.IsGreeted(new("Mair", "Balmung")));
        Assert.Empty(rig.Sent);
    }

    // ---- Scenario B: saved VIP in VIP Only Mode ----------------------------------------------------------------

    [Fact] public async Task Vip_only_mode_sends_the_vip_tell_then_public_and_bypasses_the_greeter_preset()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        Assert.Single(rig.Attendance.Guests);
        Assert.Equal(GreetingProgress.Queued, rig.Coordinator.GetProgress(new("Ada", "Balmung")));

        await rig.Pump(8);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], rig.Sent);
        Assert.DoesNotContain(rig.Sent, x => x.Contains("Welcome", StringComparison.Ordinal));
        Assert.Equal(0, rig.Greeter.PendingCount);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
        Assert.True(rig.Attendance.GetTonightVisitors().Single().Greeted);
        Assert.Equal(GreetingProgress.None, rig.Coordinator.GetProgress(new("Ada", "Balmung")));
    }

    [Fact] public async Task Vip_only_mode_uses_shout_when_the_vip_record_says_shout()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { Channel = VipAnnouncementChannel.Shout }]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Equal("/shout Ada arrived", rig.Sent[1]);
    }

    [Fact] public async Task Vip_only_tell_alone_is_sent_and_marks_greeted_for_a_private_only_vip()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { PublicAnnouncement = "" }]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Equal(["/tell Ada@Balmung VIP Ada"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Vip_only_public_only_vip_is_greeted_once_the_announcement_is_confirmed()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { CustomTell = "" }]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        Assert.False(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
        await rig.Pump(4);
        Assert.Equal(["/yell Ada arrived"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Vip_only_vip_with_no_messages_is_skipped_without_sending()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { CustomTell = "", PublicAnnouncement = "" }]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Empty(rig.Sent); Assert.Equal(0, rig.Greeter.PendingCount);
        Assert.False(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Vip_only_tell_failure_aborts_without_public_or_greeted_and_allows_a_later_retry()
    {
        var fail = true;
        using var rig = new Rig(x => fail && x.StartsWith("/tell", StringComparison.Ordinal)); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
        Assert.False(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
        Assert.Equal(GreetingProgress.None, rig.Coordinator.GetProgress(new("Ada", "Balmung")));

        fail = false;
        Assert.True(rig.Coordinator.TryGreet(new("Ada", "Balmung"), GreetingSource.ManualAttendance));
        await rig.Pump(4);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Vip_only_mode_greets_a_vip_even_when_greeter_auto_greet_is_off()
    {
        using var rig = new Rig(autoGreetEnabled: false); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"), Player("Mair", "Balmung"));
        await rig.Pump(4);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], rig.Sent); // same as normal mode: Auto Greet gates only ordinary guests
    }

    [Fact] public async Task Vip_only_mode_does_not_depend_on_the_greeter_preset_or_greeter_enabled()
    {
        using var rig = new Rig(greeterEnabled: false); rig.Vip.Configure(new([Ada]));
        rig.Greeter.SelectPreset(null);
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Equal(2, rig.Sent.Count);
        Assert.True(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task Seeded_vip_already_present_at_opening_start_is_not_auto_greeted_in_vip_only_mode()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Provider.Items = [Player("Ada", "Balmung")];
        rig.Start(vipOnly: true); // the seed scan sees Ada
        Assert.Single(rig.Attendance.Guests);
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
    }

    // ---- VIP identity matching -----------------------------------------------------------------------------------

    [Fact] public async Task Vip_only_matching_requires_the_same_home_world()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Coeurl")); // same name, different home world — not the saved VIP
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
        Assert.Single(rig.Attendance.Guests);
    }

    [Fact] public async Task Vip_only_matching_uses_the_existing_normalized_identity_rules()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("ada", " BALMUNG ")); // GuestIdentity normalizes case and whitespace, exactly like normal mode
        await rig.Pump(4);
        Assert.Equal(2, rig.Sent.Count);
    }

    [Fact] public async Task Disabled_vip_record_is_treated_as_an_ordinary_guest_in_vip_only_mode()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { Enabled = false }]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(4);
        Assert.Empty(rig.Sent); Assert.Equal(0, rig.Greeter.PendingCount);
    }

    // ---- Scenario E: repeated presence / duplicate prevention ---------------------------------------------------

    [Fact] public async Task Repeated_presence_events_for_a_vip_never_repeat_the_vip_greeting()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        for (var i = 0; i < 4; i++)
        {
            rig.Arrive(Player("Ada", "Balmung")); await rig.Pump(3);
            rig.Arrive(); await rig.Pump(1); // leaves
        }
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], rig.Sent);
        Assert.Equal(4, rig.Attendance.GetTonightVisitors().Single().Visits);
    }

    [Fact] public async Task A_second_greet_while_the_vip_only_attempt_is_in_flight_is_rejected()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        var ada = new GuestIdentity("Ada", "Balmung");
        rig.Arrive(Player("Ada", "Balmung")); // automatic attempt in flight
        Assert.False(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance));
        await rig.Pump(4);
        Assert.False(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance)); // already greeted
        Assert.Equal(2, rig.Sent.Count);
    }

    // ---- Scenario F: stop during pending ------------------------------------------------------------------------

    [Fact] public async Task Closing_a_vip_only_opening_with_a_pending_greeting_sends_nothing_and_leaks_nothing_into_the_next_normal_opening()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung")); // VIP Only tell enqueued, not yet dispatched
        Assert.True(rig.Attendance.CloseSession(rig.Clock.UtcNow));
        Assert.False(rig.Attendance.VipOnlyMode);
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
        Assert.Equal(GreetingProgress.None, rig.Coordinator.GetProgress(new("Ada", "Balmung")));

        rig.Start(vipOnly: false); // next opening, normal mode
        rig.Arrive(); rig.Arrive(Player("Ada", "Balmung"));
        await rig.Pump(6);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/tell Ada@Balmung Welcome Ada!", "/yell Ada arrived"], rig.Sent);
    }

    [Fact] public async Task Pausing_after_the_tell_confirms_drops_the_pending_public_announcement()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        await rig.Chat.TickAsync(); // tell confirmed → public enqueued, guest marked greeted
        Assert.True(rig.Attendance.PauseSession(rig.Clock.UtcNow));
        rig.Clock.Advance(1);
        await rig.Pump(4);
        Assert.Equal(["/tell Ada@Balmung VIP Ada"], rig.Sent);
    }

    [Fact] public async Task Pausing_a_public_only_attempt_never_marks_the_guest_greeted_in_a_later_opening()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { CustomTell = "" }]));
        rig.Start(vipOnly: true);
        var sessionId = rig.Attendance.CurrentSessionId!.Value;
        rig.Arrive(Player("Ada", "Balmung"));
        rig.Attendance.PauseSession(rig.Clock.UtcNow);
        Assert.True(rig.Attendance.ResumeSession(sessionId)); // resumed in normal mode
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
        Assert.False(rig.Attendance.IsGreeted(new("Ada", "Balmung")));
    }

    [Fact] public async Task A_cancelled_tell_attempts_late_callback_cannot_clear_a_newer_attempt_and_allow_a_duplicate()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { PublicAnnouncement = "" }]));
        rig.Start(vipOnly: true);
        var sessionId = rig.Attendance.CurrentSessionId!.Value; var ada = new GuestIdentity("Ada", "Balmung");
        rig.Arrive(Player("Ada", "Balmung"));
        rig.Attendance.PauseSession(rig.Clock.UtcNow);
        Assert.True(rig.Attendance.ResumeSession(sessionId, vipOnlyMode: true));
        Assert.True(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance));
        await rig.Chat.TickAsync();
        Assert.Equal(GreetingProgress.Queued, rig.Coordinator.GetProgress(ada));
        Assert.False(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance));
        rig.Clock.Advance(1); await rig.Pump(4);
        Assert.Equal(["/tell Ada@Balmung VIP Ada"], rig.Sent);
    }

    [Fact] public async Task A_cancelled_attempts_late_callback_cannot_clear_a_newer_attempt_and_allow_a_duplicate()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada with { CustomTell = "" }]));
        rig.Start(vipOnly: true);
        var sessionId = rig.Attendance.CurrentSessionId!.Value; var ada = new GuestIdentity("Ada", "Balmung");
        rig.Arrive(Player("Ada", "Balmung")); // attempt 1 enqueued
        rig.Attendance.PauseSession(rig.Clock.UtcNow); // attempt 1 cancelled, still sitting in the chat queue
        Assert.True(rig.Attendance.ResumeSession(sessionId, vipOnlyMode: true));
        Assert.True(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance)); // attempt 2
        await rig.Chat.TickAsync(); // attempt 1's cancelled callback arrives
        Assert.Equal(GreetingProgress.Queued, rig.Coordinator.GetProgress(ada));
        Assert.False(rig.Coordinator.TryGreet(ada, GreetingSource.ManualAttendance));
        rig.Clock.Advance(1); await rig.Pump(4);
        Assert.Equal(["/yell Ada arrived"], rig.Sent);
        Assert.True(rig.Attendance.IsGreeted(ada));
    }

    [Fact] public async Task Starting_a_vip_only_opening_drops_ordinary_greetings_left_over_from_the_previous_opening()
    {
        using var rig = new Rig(delaySeconds: 30);
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Mair", "Balmung"));
        rig.Greeter.Tick(); // Mair's greeting has started (waiting on its 30 s delay)
        Assert.Equal(1, rig.Greeter.PendingCount);
        rig.Attendance.CloseSession(rig.Clock.UtcNow);
        rig.Start(vipOnly: true);
        Assert.Equal(0, rig.Greeter.PendingCount);
        rig.Clock.Advance(60);
        await rig.Pump(6);
        Assert.Empty(rig.Sent);
    }

    [Fact] public async Task An_unconfirmed_normal_mode_vip_tell_cannot_hand_off_to_greeter_inside_a_vip_only_opening()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Ada", "Balmung")); // normal-mode VIP tell enqueued, awaiting confirmation
        rig.Attendance.CloseSession(rig.Clock.UtcNow);
        rig.Start(vipOnly: true);
        await rig.Pump(6);
        Assert.DoesNotContain(rig.Sent, x => x.Contains("Welcome", StringComparison.Ordinal));
        Assert.Equal(0, rig.Greeter.PendingCount);
    }

    [Fact] public async Task Ending_a_normal_opening_leaves_its_pending_greeter_work_exactly_as_before()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: false);
        rig.Arrive(Player("Mair", "Balmung"));
        Assert.Equal(1, rig.Greeter.PendingCount);
        rig.Attendance.CloseSession(rig.Clock.UtcNow);
        Assert.Equal(1, rig.Greeter.PendingCount); // pre-0.3.11 behavior: closing never touched Greeter's queue
        await rig.Pump(4);
        Assert.Equal(["/tell Mair@Balmung Welcome Mair!"], rig.Sent);
    }

    // ---- Scenario G / restart -------------------------------------------------------------------------------------

    [Fact] public async Task Restarting_in_normal_mode_after_vip_only_restores_ordinary_greeting_with_untouched_configuration()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        var greeterBefore = rig.Greeter.Settings; var vipBefore = rig.Vip.Settings; var presetsBefore = rig.Greeter.GetPresetLibrary().Count;
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Mair", "Balmung")); await rig.Pump(4);
        Assert.Empty(rig.Sent);
        rig.Attendance.CloseSession(rig.Clock.UtcNow);

        Assert.Equal(greeterBefore, rig.Greeter.Settings); Assert.Same(vipBefore, rig.Vip.Settings); Assert.Equal(presetsBefore, rig.Greeter.GetPresetLibrary().Count);
        rig.Start(vipOnly: false);
        rig.Arrive(); rig.Arrive(Player("Mair", "Balmung"));
        await rig.Pump(4);
        Assert.Equal(["/tell Mair@Balmung Welcome Mair!"], rig.Sent);
    }

    [Fact] public async Task Restarting_in_vip_only_mode_after_a_normal_opening_applies_vip_only_rules()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: false);
        rig.Attendance.CloseSession(rig.Clock.UtcNow);
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Mair", "Balmung"), Player("Ada", "Balmung"));
        await rig.Pump(6);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], rig.Sent);
    }

    [Fact] public void Pause_close_and_detach_all_clear_vip_only_mode()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: true); rig.Attendance.PauseSession(rig.Clock.UtcNow); Assert.False(rig.Attendance.VipOnlyMode);
        rig.Start(vipOnly: true); rig.Attendance.CloseSession(rig.Clock.UtcNow); Assert.False(rig.Attendance.VipOnlyMode);
        rig.Start(vipOnly: true); rig.Attendance.DetachSession(); Assert.False(rig.Attendance.VipOnlyMode);
        Assert.False(rig.Coordinator.IsVipOnlyMode);
    }

    [Fact] public void Session_ended_fires_for_pause_close_and_detach_but_not_for_detach_without_a_session()
    {
        using var rig = new Rig(); var ended = 0; rig.Attendance.SessionEnded += () => ended++;
        rig.Attendance.DetachSession(); Assert.Equal(0, ended);
        rig.Start(vipOnly: false); rig.Attendance.PauseSession(rig.Clock.UtcNow); Assert.Equal(1, ended);
        rig.Start(vipOnly: false); rig.Attendance.CloseSession(rig.Clock.UtcNow); Assert.Equal(2, ended);
        rig.Start(vipOnly: false); rig.Attendance.DetachSession(); Assert.Equal(3, ended);
    }

    [Fact] public void Resume_defaults_to_normal_mode_and_honors_an_explicit_vip_only_choice()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: true); var id = rig.Attendance.CurrentSessionId!.Value;
        rig.Attendance.PauseSession(rig.Clock.UtcNow);
        Assert.True(rig.Attendance.ResumeSession(id)); Assert.False(rig.Attendance.VipOnlyMode); // never silently restored
        rig.Attendance.PauseSession(rig.Clock.UtcNow);
        Assert.True(rig.Attendance.ResumeSession(id, vipOnlyMode: true)); Assert.True(rig.Attendance.VipOnlyMode);
    }

    [Fact] public void Reload_never_restores_vip_only_mode_from_storage()
    {
        using var rig = new Rig();
        rig.Start(vipOnly: true); var id = rig.Attendance.CurrentSessionId!.Value;
        // Simulates a plugin reload: a brand-new AttendanceService over the same attendance database.
        var reloaded = new AttendanceService(rig.Presence, rig.Db, rig.Clock); reloaded.AttachVenue(rig.Venue);
        Assert.Null(reloaded.CurrentSessionId); Assert.False(reloaded.VipOnlyMode);
        Assert.True(reloaded.ResumeSession(id)); Assert.False(reloaded.VipOnlyMode);
    }

    // ---- Module disable --------------------------------------------------------------------------------------------

    [Fact] public async Task Disabling_the_vip_module_stops_vip_only_greetings_and_cancels_one_in_flight()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada, new("Bo", "Balmung", true, "Hi Bo", "", VipAnnouncementChannel.Shout)]));
        var module = new VipModule(rig.Vip, rig.Coordinator, new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung")); // in flight
        module.IsEnabled = false;
        Assert.False(rig.Coordinator.VipModuleEnabled);
        rig.Arrive(Player("Ada", "Balmung"), Player("Bo", "Balmung"));
        Assert.False(rig.Coordinator.CanGreet(new("Bo", "Balmung")));
        await rig.Pump(6);
        Assert.Empty(rig.Sent);
        Assert.Equal(2, rig.Attendance.Guests.Count); // tracking continues

        module.IsEnabled = true;
        Assert.True(rig.Coordinator.TryGreet(new("Bo", "Balmung"), GreetingSource.ManualAttendance));
        await rig.Pump(3);
        Assert.Equal(["/tell Bo@Balmung Hi Bo"], rig.Sent);
    }

    [Fact] public void Vip_module_enabled_flag_defaults_on()
    {
        using var rig = new Rig();
        var module = new VipModule(rig.Vip, rig.Coordinator, new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()));
        Assert.True(module.IsEnabled); Assert.True(rig.Coordinator.VipModuleEnabled);
    }

    // ---- Scenario D / venue switching -----------------------------------------------------------------------------

    [Fact] public async Task Venue_switch_clears_vip_only_mode_and_vip_eligibility_follows_the_active_venue()
    {
        var clock = new Clock(); var sent = new List<string>(); var provider = new Provider(); var presence = new PresenceService(clock, provider);
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), x => { sent.Add(x); return true; }, TimeSpan.Zero);
        using var db = new SqliteVenueDatabase("Data Source=:memory:");
        AttendanceService? attendanceRef = null;
        var greeter = new GreeterService(clock, chat, db, g => presence.IsPresent(g), isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, clock.UtcNow));
        var attendance = new AttendanceService(presence, db, clock, greeter: greeter); attendanceRef = attendance;
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendance.IsGreeted(g), greeter, vip, chat); coordinator.AttachSession(attendance);
        attendance.AutomaticGreetingEligible += g => coordinator.TryGreet(g, GreetingSource.AutomaticArrival);
        var host = new ModuleHost(); var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        host.Register(new AttendanceModule(attendance, presence, profiles)); host.Register(new GreeterModule(greeter, profiles)); host.Register(new VipModule(vip, coordinator, profiles));
        var a = profiles.Current; var b = profiles.Create("B");
        profiles.SaveModuleConfig(a.Id, "core.vip", 1, new VipSettings([Ada])); // Ada is a VIP of Venue A only
        await host.InitializeAsync(); await profiles.InitializeAsync();

        attendance.StartSession(false, PresenceAreaMode.FollowOperator, clock.UtcNow, vipOnlyMode: true);
        Assert.True(coordinator.IsVipOnlyMode);
        Assert.True((await profiles.SwitchAsync(b.Id)).Success);
        Assert.False(attendance.VipOnlyMode); Assert.False(coordinator.IsVipOnlyMode); // never leaks into Venue B

        attendance.StartSession(false, PresenceAreaMode.FollowOperator, clock.UtcNow, vipOnlyMode: true);
        presence.Tick(new(null, null, null));
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));
        for (var i = 0; i < 4; i++) { greeter.Tick(); await chat.TickAsync(); clock.Advance(1); }
        Assert.Empty(sent); // not a VIP in Venue B
        Assert.Single(attendance.GetTonightVisitors()); // but tracked in Venue B

        Assert.True((await profiles.SwitchAsync(a.Id)).Success);
        attendance.StartSession(false, PresenceAreaMode.FollowOperator, clock.UtcNow, vipOnlyMode: true);
        provider.Items = []; presence.Tick(new(null, null, null));
        provider.Items = [Player("Ada", "Balmung")]; clock.Advance(1); presence.Tick(new(null, null, null));
        for (var i = 0; i < 4; i++) { greeter.Tick(); await chat.TickAsync(); clock.Advance(1); }
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], sent); // recognized in Venue A
    }

    [Fact] public async Task Venue_switch_cancels_a_pending_vip_only_greeting()
    {
        using var rig = new Rig(); rig.Vip.Configure(new([Ada]));
        rig.Start(vipOnly: true);
        rig.Arrive(Player("Ada", "Balmung"));
        rig.Attendance.DetachSession(); rig.Coordinator.ResetForVenue(); // the module host's venue-switch sequence
        await rig.Pump(4);
        Assert.Empty(rig.Sent);
    }

    // ---- Multi-operator (separate clients) ----------------------------------------------------------------------

    [Fact] public async Task Two_independent_clients_one_normal_one_vip_only_do_not_affect_each_other()
    {
        using var normal = new Rig(); using var vipHost = new Rig(venue: normal.Venue);
        normal.Vip.Configure(new([Ada])); vipHost.Vip.Configure(new([Ada]));
        normal.Start(vipOnly: false); vipHost.Start(vipOnly: true);
        normal.Arrive(Player("Mair", "Balmung"), Player("Ada", "Balmung")); vipHost.Arrive(Player("Mair", "Balmung"), Player("Ada", "Balmung"));
        await normal.Pump(8); await vipHost.Pump(8);

        Assert.False(normal.Attendance.VipOnlyMode);
        Assert.Contains("/tell Mair@Balmung Welcome Mair!", normal.Sent);
        Assert.Contains("/tell Ada@Balmung Welcome Ada!", normal.Sent);
        Assert.Equal(["/tell Ada@Balmung VIP Ada", "/yell Ada arrived"], vipHost.Sent);
        Assert.Equal(2, normal.Attendance.Guests.Count); Assert.Equal(2, vipHost.Attendance.Guests.Count);
        // Documented limitation: clients share no dispatch state, so the VIP tell is sent by both. Nothing here
        // coordinates across clients — operators decide who greets VIPs.
        Assert.Contains("/tell Ada@Balmung VIP Ada", normal.Sent);
    }

    // ---- Harness ---------------------------------------------------------------------------------------------------

    /// <summary>Production wiring from <c>Plugin.cs</c>: Attendance owns greeted state, Greeter reports back to it,
    /// the coordinator is the single greeting entry point and follows Attendance's session lifecycle.</summary>
    private sealed class Rig : IDisposable
    {
        public Clock Clock { get; } = new();
        public List<string> Sent { get; } = [];
        public Provider Provider { get; } = new();
        public PresenceService Presence { get; }
        public ChatCommandService Chat { get; }
        public SqliteVenueDatabase Db { get; } = new("Data Source=:memory:");
        public Guid Venue { get; }
        public AttendanceService Attendance { get; }
        public GreeterService Greeter { get; }
        public VipOrchestrationService Vip { get; } = new();
        public GreetingCoordinator Coordinator { get; }

        public Rig(Func<string, bool>? fail = null, bool autoGreetEnabled = true, bool greeterEnabled = true, int delaySeconds = 0, Guid? venue = null)
        {
            Venue = venue ?? Guid.NewGuid();
            Presence = new PresenceService(Clock, Provider);
            Chat = new ChatCommandService(Clock, new InlineFrameworkDispatcher(), x => { if (fail?.Invoke(x) ?? false) return false; Sent.Add(x); return true; }, TimeSpan.Zero);
            AttendanceService? attendanceRef = null;
            Greeter = new GreeterService(Clock, Chat, Db, g => Presence.IsPresent(g), isGreeted: g => attendanceRef?.IsGreeted(g) ?? false, onGreetingCompleted: g => attendanceRef?.MarkGreeted(g, true, Clock.UtcNow));
            Greeter.AttachVenue(Venue);
            var presetId = Db.SavePreset(Venue, null, "DJ", "Welcome <name>!", "", "", "");
            Greeter.Configure(new GreeterSettings(AutoGreetEnabled: autoGreetEnabled, GreetDelaySeconds: delaySeconds, Enabled: greeterEnabled, ActivePresetId: presetId));
            Attendance = new AttendanceService(Presence, Db, Clock, greeter: Greeter); attendanceRef = Attendance;
            Attendance.AttachVenue(Venue); Attendance.Attach();
            Coordinator = new GreetingCoordinator(g => Attendance.IsGreeted(g), Greeter, Vip, Chat);
            Coordinator.Attach(); Coordinator.AttachSession(Attendance);
            Attendance.AutomaticGreetingEligible += g => Coordinator.TryGreet(g, GreetingSource.AutomaticArrival);
            Attendance.SessionOpened += () => Presence.ResetKnownPresence();
        }

        /// <summary>Starts an opening and runs its seed scan (whoever is already present).</summary>
        public void Start(bool vipOnly) { Clock.Advance(1); Attendance.StartSession(false, PresenceAreaMode.FollowOperator, Clock.UtcNow, vipOnly); Presence.Tick(new(null, null, null)); }
        /// <summary>Sets who is present and runs one presence scan — arrivals and departures are the diff.</summary>
        public void Arrive(params PlayerSnapshot[] players) { Provider.Items = players; Clock.Advance(1); Presence.Tick(new(null, null, null)); }
        public async Task Pump(int count) { for (var i = 0; i < count; i++) { Greeter.Tick(); await Chat.TickAsync(); Clock.Advance(1); } }
        public void Dispose() => Db.Dispose();
    }

    private static PlayerSnapshot Player(string name, string world) => new(name, world, 1, 1, Vector3.Zero);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch; public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds); }
    private sealed class Provider : IObjectSnapshotProvider { public IReadOnlyList<PlayerSnapshot> Items { get; set; } = []; public IReadOnlyList<PlayerSnapshot> Snapshot() => Items; }
}
