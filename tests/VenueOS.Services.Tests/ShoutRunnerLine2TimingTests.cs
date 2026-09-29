using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>0.3.9 live-QA correction for the optional second shout line. Live FFXIV testing showed Line 2 going out
/// ~1 s after Line 1 (only the shared chat service's minimum interval separated them) and the game dropping it. The
/// rule is now: Line 2 becomes eligible only once <see cref="ShoutRunnerShoutLines.InterLineDelay"/> (2.0 s) has
/// elapsed since Line 1's CONFIRMED dispatch — the moment its <see cref="ChatCommand.OnDispatched"/> callback reported
/// success. Every test drives the real <see cref="ShoutRunnerService"/> and the real <see cref="ChatCommandService"/>
/// with a controllable clock; the clock is moved by hand so each threshold is hit exactly.</summary>
public sealed class ShoutRunnerLine2TimingTests
{
    private const string L1 = "TEST LINE ONE";
    private const string L2 = "TEST LINE TWO";
    private static readonly string Cmd1 = "/shout " + L1;
    private static readonly string Cmd2 = "/shout " + L2;

    // ---------- pure timing rule ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1000, false)]
    [InlineData(1500, false)]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    [InlineData(2500, true)]
    public void The_inter_line_rule_is_never_eligible_before_exactly_two_seconds(int elapsedMs, bool eligible)
    {
        var anchor = DateTimeOffset.UnixEpoch.AddHours(3);
        Assert.Equal(TimeSpan.FromSeconds(2), ShoutRunnerShoutLines.InterLineDelay);
        Assert.Equal(eligible, ShoutRunnerShoutLines.IsLine2Eligible(anchor, anchor + TimeSpan.FromMilliseconds(elapsedMs)));
    }

    // ---------- 1. single line ----------

    [Fact]
    public void A_single_line_shout_never_enters_WaitingForLine2_and_adds_no_delay()
    {
        var h = Ready(line2: "", delaySeconds: 0);
        h.Service.Start();
        h.StepUntil(() => h.Sent.Count == 1);
        var line1At = h.Clock.UtcNow;

        // Without moving the clock at all, the runner goes straight to the existing post-shout pacing and on to the
        // next destination's Line 1 — no 2 s wait was inserted anywhere.
        for (var i = 0; i < 50 && h.Sent.Count < 2; i++)
        {
            h.Service.Tick(h.Clock.UtcNow);
            Assert.NotEqual(ShoutRunnerState.WaitingForLine2, h.Service.State);
            h.TickChat();
        }

        Assert.Equal([Cmd1, Cmd1], h.Sent);
        Assert.Equal(line1At, h.Clock.UtcNow);
    }

    [Fact]
    public void A_whole_single_line_run_never_observes_WaitingForLine2_and_sends_exactly_one_line_per_destination()
    {
        var h = Ready(line2: "   ");
        h.Service.Start();
        var sawWait = false;
        for (var i = 0; i < 4000 && h.Service.State is not (ShoutRunnerState.Stopped or ShoutRunnerState.Faulted); i++)
        {
            h.Step();
            sawWait |= h.Service.State == ShoutRunnerState.WaitingForLine2;
        }

        Assert.False(sawWait);
        Assert.Equal(16, h.Sent.Count); // Aether: 8 Worlds x 2 destinations
        Assert.All(h.Sent, c => Assert.Equal(Cmd1, c));
    }

    // ---------- 2-5. two lines, threshold by threshold ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1500)]
    [InlineData(1999)]
    public void Line_2_is_not_dispatched_before_two_seconds_after_Line_1_confirmation(int elapsedMs)
    {
        // Production chat pacing (1 s) — the previous implementation sent Line 2 at the 1000 ms mark.
        var h = Ready(line2: L2, chatInterval: TimeSpan.FromSeconds(1));
        var anchor = DriveToLine1Confirmed(h);

        h.Clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
        TickBoth(h, 3);

        Assert.Equal([Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);
        Assert.True(h.Clock.UtcNow - anchor < ShoutRunnerShoutLines.InterLineDelay);
    }

    [Fact]
    public void Line_2_is_not_dispatched_immediately_after_Line_1_confirmation_even_with_no_chat_pacing()
    {
        var h = Ready(line2: L2); // chat interval 0: only ShoutRunner's own rule can hold Line 2 back
        DriveToLine1Confirmed(h);
        TickBoth(h, 10);
        Assert.Equal([Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(3500)]
    public void Line_2_becomes_eligible_at_two_seconds_and_dispatches_through_the_normal_chat_transport(int elapsedMs)
    {
        var h = Ready(line2: L2, chatInterval: TimeSpan.FromSeconds(1));
        DriveToLine1Confirmed(h);

        h.Clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
        h.Service.Tick(h.Clock.UtcNow); // eligible -> Line 2 enqueued on the shared ChatCommandService
        Assert.Equal(ShoutRunnerState.SendingShout, h.Service.State);
        Assert.Equal([Cmd1], h.Sent);   // not sent by ShoutRunner directly — only via the chat service
        h.TickChat();

        Assert.Equal([Cmd1, Cmd2], h.Sent);
    }

    [Fact]
    public void Stepping_one_millisecond_at_a_time_Line_2_first_appears_at_exactly_two_seconds()
    {
        var h = Ready(line2: L2);
        var anchor = DriveToLine1Confirmed(h);

        TimeSpan? firstSeen = null;
        for (var ms = 1; ms <= 2100 && firstSeen is null; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            TickBoth(h, 1);
            if (h.Sent.Count == 2) firstSeen = h.Clock.UtcNow - anchor;
        }

        Assert.Equal(ShoutRunnerShoutLines.InterLineDelay, firstSeen);
    }

    // ---------- 6. anchor = confirmation, not enqueue ----------

    [Fact]
    public void The_2_second_timer_starts_at_Line_1_confirmation_not_at_Line_1_enqueue()
    {
        var h = Ready(line2: L2);
        h.Service.Start();
        // Drive ONLY the service (no chat ticks) until Line 1 has been handed to the chat queue but not dispatched.
        for (var i = 0; i < 50 && h.Service.State != ShoutRunnerState.SendingShout; i++) h.Service.Tick(h.Clock.UtcNow);
        Assert.Equal(ShoutRunnerState.SendingShout, h.Service.State);
        Assert.Empty(h.Sent);
        var enqueuedAt = h.Clock.UtcNow;

        // Artificially delay the transport's confirmation by 5 s.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.TickChat();
        Assert.Equal([Cmd1], h.Sent);
        var confirmedAt = h.Clock.UtcNow;
        h.Service.Tick(h.Clock.UtcNow);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);

        // 6.999 s after enqueue (an enqueue-anchored timer would long since have fired) but only 1.999 s after confirmation.
        h.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        TickBoth(h, 3);
        Assert.Equal([Cmd1], h.Sent);
        Assert.True(h.Clock.UtcNow - enqueuedAt > ShoutRunnerShoutLines.InterLineDelay);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        TickBoth(h, 1);
        Assert.Equal([Cmd1, Cmd2], h.Sent);
        Assert.Equal(ShoutRunnerShoutLines.InterLineDelay, h.Clock.UtcNow - confirmedAt);
    }

    [Fact]
    public void The_anchor_is_the_confirmation_callback_time_not_the_later_tick_that_observes_it()
    {
        var h = Ready(line2: L2);
        var anchor = DriveToLine1Confirmed(h, observe: false);

        // The runner only observes Line 1's completion 1.5 s later (e.g. a slow frame). The wait must still end at
        // anchor + 2 s — never be pushed out to observation + 2 s, and never shortened below anchor + 2 s.
        h.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        h.Service.Tick(h.Clock.UtcNow);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);

        h.Clock.Advance(TimeSpan.FromMilliseconds(499));
        TickBoth(h, 2);
        Assert.Equal([Cmd1], h.Sent);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        TickBoth(h, 1);
        Assert.Equal([Cmd1, Cmd2], h.Sent);
        Assert.Equal(ShoutRunnerShoutLines.InterLineDelay, h.Clock.UtcNow - anchor);
    }

    // ---------- 7-9. cancellation during the gap ----------

    [Fact]
    public void Stop_during_WaitingForLine2_means_Line_2_never_sends()
    {
        var h = Ready(line2: L2, chatInterval: TimeSpan.FromSeconds(1));
        DriveToLine1Confirmed(h);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        TickBoth(h, 1);

        h.Service.Stop();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Pump(200);

        Assert.Equal([Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
    }

    [Fact]
    public void A_venue_change_during_WaitingForLine2_never_delivers_a_stale_Line_2()
    {
        var h = Ready(line2: L2);
        var other = h.Profiles.Create("Other venue");
        DriveToLine1Confirmed(h);

        h.Service.Load(other.Id);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Pump(200);

        Assert.Equal([Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
    }

    [Fact]
    public void Disabling_ShoutRunner_during_WaitingForLine2_never_delivers_Line_2()
    {
        var h = Ready(line2: L2);
        var module = new ShoutRunnerModule(h.Service);
        DriveToLine1Confirmed(h);

        module.IsEnabled = false;
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Pump(200);

        Assert.Equal([Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.Stopped, h.Service.State);
    }

    [Fact]
    public async Task Disposing_the_module_during_WaitingForLine2_never_delivers_Line_2()
    {
        var h = Ready(line2: L2);
        var module = new ShoutRunnerModule(h.Service);
        DriveToLine1Confirmed(h);

        await module.DisposeAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        h.Pump(200);

        Assert.Equal([Cmd1], h.Sent);
    }

    [Fact]
    public void A_new_run_after_stopping_in_the_gap_starts_with_Line_1_and_never_inherits_the_stale_wait()
    {
        var h = Ready(line2: L2);
        DriveToLine1Confirmed(h);
        h.Service.Stop();
        h.PumpUntilStopped();
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        TickBoth(h, 50); // no clock movement: nothing time-gated may fire, and the fresh run must begin with Line 1

        Assert.Equal([Cmd1, Cmd1], h.Sent);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);
    }

    // ---------- 10-11. failure semantics ----------

    [Fact]
    public void A_Line_1_failure_starts_no_timer_and_never_sends_Line_2()
    {
        var h = Ready(line2: L2, failWhen: c => c == Cmd1);
        DriveToLine1Confirmed(h, observe: false);

        h.Service.Tick(h.Clock.UtcNow);
        Assert.Equal(ShoutRunnerState.WaitingActionDelay, h.Service.State);
        Assert.Contains(h.Service.TerminalEvents, e => e.Text.Contains("Line 1 failed") && e.Text.Contains("Line 2 not sent"));

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        TickBoth(h, 1);
        Assert.DoesNotContain(Cmd2, h.Sent);

        h.PumpUntilStopped();
        Assert.DoesNotContain(Cmd2, h.Sent);
        Assert.All(h.Sent, c => Assert.Equal(Cmd1, c));
    }

    [Fact]
    public void A_Line_2_failure_after_the_gap_is_reported_as_incomplete_and_Line_1_is_not_resent()
    {
        var h = Ready(line2: L2, failWhen: c => c == Cmd2);
        DriveToLine1Confirmed(h);
        h.Clock.Advance(ShoutRunnerShoutLines.InterLineDelay);
        TickBoth(h, 1);                       // Line 2 enqueued + dispatched (transport rejects it)
        h.Service.Tick(h.Clock.UtcNow);       // outcome observed

        Assert.Equal([Cmd1, Cmd2], h.Sent);
        Assert.Equal(ShoutRunnerState.WaitingActionDelay, h.Service.State);
        Assert.Contains(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT INCOMPLETE: Line 1 sent, Line 2 FAILED"));
        Assert.DoesNotContain(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT SENT"));
    }

    // ---------- 12. route progression ----------

    [Fact]
    public void The_route_does_not_advance_past_the_shout_until_Line_2_has_been_processed()
    {
        var h = Ready(line2: L2);
        var teleports = new List<string>();
        var inner = h.Automation.OnTeleport;
        h.Automation.OnTeleport = (name, token) => { teleports.Add(name); return inner(name, token); };

        DriveToLine1Confirmed(h);
        var teleportsAtLine1 = teleports.Count;

        h.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        TickBoth(h, 20);
        Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);
        Assert.Equal(teleportsAtLine1, teleports.Count); // no next-destination travel during the gap
        Assert.DoesNotContain(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT SENT"));

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        TickBoth(h, 1);
        h.Service.Tick(h.Clock.UtcNow);
        Assert.Equal([Cmd1, Cmd2], h.Sent);
        Assert.Contains(h.Service.TerminalEvents, e => e.Text.Contains("SHOUT SENT (2 lines)"));
        Assert.Equal(ShoutRunnerState.WaitingActionDelay, h.Service.State); // existing post-shout pacing only now
        Assert.Equal(teleportsAtLine1, teleports.Count);
    }

    // ---------- helpers ----------

    /// <summary>Starts a run and drives it until Line 1 has just been dispatched by the chat transport (its
    /// OnDispatched callback has fired). Returns that confirmation instant. With <paramref name="observe"/>, also runs
    /// the one service tick that observes the outcome — without advancing the clock.</summary>
    private static DateTimeOffset DriveToLine1Confirmed(ShoutRunnerHarness h, bool observe = true)
    {
        Assert.Equal(ShoutRunnerStartResult.Started, h.Service.Start());
        h.StepUntil(() => h.Sent.Count == 1);
        var confirmedAt = h.Clock.UtcNow;
        if (observe)
        {
            h.Service.Tick(h.Clock.UtcNow);
            if (h.Service.Settings.ShoutMessageLine2.Trim().Length > 0) Assert.Equal(ShoutRunnerState.WaitingForLine2, h.Service.State);
        }
        return confirmedAt;
    }

    /// <summary>Service tick then chat tick, <paramref name="times"/> times, WITHOUT moving the clock.</summary>
    private static void TickBoth(ShoutRunnerHarness h, int times)
    {
        for (var i = 0; i < times; i++) { h.Service.Tick(h.Clock.UtcNow); h.TickChat(); }
    }

    private static ShoutRunnerHarness Ready(string line2, TimeSpan? chatInterval = null, Func<string, bool>? failWhen = null, int delaySeconds = 2)
    {
        var h = ShoutRunnerHarness.New(failWhen: failWhen, chatInterval: chatInterval);
        while (h.Service.Settings.Destinations.Count > 0) h.Service.RemoveDestinationAt(0);
        h.Service.AddDestination("A");
        h.Service.AddDestination("B");
        h.Service.SetDataCenterSelected("Aether", true);
        h.Service.SetRepeatEnabled(false);
        h.Service.SetDelaySeconds(delaySeconds);
        h.Service.UpdateShoutMessage(L1);
        h.Service.UpdateShoutMessageLine2(line2);
        return h;
    }
}
