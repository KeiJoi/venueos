using VenueOS.Modules.Operations.Macro;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary><see cref="MacroRunner"/>: the execution stack (nesting, delay-per-frame, parent-resumes-with-its-own-
/// delay — MACRO spec §12/§13), cycle/missing-reference safety (§14/§16), cancellation (§21/§22), one-active-run
/// enforcement (§21), and <c>/actionready</c> (§17-§20). Drives <see cref="SchedulerService"/>/<see cref="ChatCommandService"/>
/// with a controllable <see cref="Clock"/>, mirroring <c>GiveawayServiceTests</c>' style for this shared
/// infrastructure.</summary>
public sealed class MacroRunnerTests
{
    [Fact]
    public void Starting_an_unknown_macro_id_fails_without_mutating_state()
    {
        var t = Create();
        var result = t.Runner.Start([], Guid.NewGuid());
        Assert.False(result.Success);
        Assert.False(t.Runner.IsRunning);
    }

    [Fact]
    public void A_plain_line_is_sent_immediately_on_start()
    {
        var t = Create();
        var macro = Macro("A", 1.0, "/say Hello");
        var result = t.Runner.Start([macro], macro.Id);

        Assert.True(result.Success);
        DrainChat(t);
        Assert.Equal(["/say Hello"], t.Sent);
    }

    [Fact]
    public void Blank_lines_are_skipped_with_no_delay_cost()
    {
        var t = Create();
        var macro = Macro("A", 100.0, "", "   ", "/say Hello");
        t.Runner.Start([macro], macro.Id);
        DrainChat(t);

        // No clock advance needed — blank lines never schedule a wait, so the third (real) line sends synchronously.
        Assert.Single(t.Sent);
    }

    [Fact]
    public void Second_line_waits_for_the_configured_delay_before_sending()
    {
        var t = Create();
        var macro = Macro("A", 2.0, "/say One", "/say Two");
        t.Runner.Start([macro], macro.Id);
        DrainChat(t);
        Assert.Single(t.Sent);

        Tick(t, 1); // not yet at the 2-second delay
        Assert.Single(t.Sent);

        Tick(t, 1); // now at 2 seconds total
        Assert.Equal(2, t.Sent.Count);
    }

    [Fact]
    public void Fractional_delays_are_supported()
    {
        var t = Create();
        var macro = Macro("A", 0.5, "/say One", "/say Two");
        t.Runner.Start([macro], macro.Id);
        t.Clock.Advance(0.5);
        t.Scheduler.Tick();
        DrainChat(t);
        Assert.Equal(2, t.Sent.Count);
    }

    [Fact]
    public void Nested_macro_runs_to_completion_using_its_own_delay_then_parent_resumes_with_its_own_delay()
    {
        var t = Create();
        var child = Macro("Child", 5.0, "/say ChildLine1", "/say ChildLine2");
        var parent = Macro("Parent", 2.0, "/say ParentLine1", MacroDirectiveParser.FormatNestedInvocation("Child"), "/say ParentLine2");

        t.Runner.Start([parent, child], parent.Id);
        DrainChat(t);
        Assert.Equal(["/say ParentLine1"], t.Sent);

        // Parent's own 2s delay elapses -> its next line is the nested invocation -> the child is entered and its
        // first line sends immediately, with no separate "delay before entering child". The child now has a second
        // line still pending, so it's genuinely still the active frame — observable via CurrentMacroName.
        Tick(t, 2);
        Assert.Equal(["/say ParentLine1", "/say ChildLine1"], t.Sent);
        Assert.Equal("Child", t.Runner.CurrentMacroName);

        // The child's SECOND (and last) line waits for the CHILD's own 5s delay, not the parent's 2s.
        Tick(t, 5);
        Assert.Equal(["/say ParentLine1", "/say ChildLine1", "/say ChildLine2"], t.Sent);

        // The child is now fully finished (its last line never gets a trailing delay — see AfterLineExecuted's doc
        // comment) and control has already returned to the parent, which applies its OWN 2s delay — not the
        // child's 5s — before its next line. This is the exact spec §13 assertion: "parent's delay controls
        // spacing before its next line".
        Tick(t, 2);
        Assert.Equal(["/say ParentLine1", "/say ChildLine1", "/say ChildLine2", "/say ParentLine2"], t.Sent);
        Assert.False(t.Runner.IsRunning);
    }

    [Fact]
    public void Self_cycle_is_detected_and_stops_safely()
    {
        var t = Create();
        var a = Macro("A", 1.0, MacroDirectiveParser.FormatNestedInvocation("A"));
        var result = t.Runner.Start([a], a.Id);

        Assert.True(result.Success); // Start() itself succeeds; the cycle is caught during execution, synchronously
        Assert.False(t.Runner.IsRunning);
        Assert.Contains("cycle", t.Runner.LastOutcomeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A → A", t.Runner.LastOutcomeMessage);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public void Indirect_A_B_A_cycle_is_detected()
    {
        var t = Create();
        var a = Macro("A", 1.0, MacroDirectiveParser.FormatNestedInvocation("B"));
        var b = Macro("B", 1.0, MacroDirectiveParser.FormatNestedInvocation("A"));
        t.Runner.Start([a, b], a.Id);

        Assert.False(t.Runner.IsRunning);
        Assert.Contains("A → B → A", t.Runner.LastOutcomeMessage);
    }

    [Fact]
    public void Missing_nested_macro_fails_safely_with_a_clear_message()
    {
        var t = Create();
        var a = Macro("A", 1.0, MacroDirectiveParser.FormatNestedInvocation("Ghost"));
        t.Runner.Start([a], a.Id);

        Assert.False(t.Runner.IsRunning);
        Assert.Contains("Ghost", t.Runner.LastOutcomeMessage);
        Assert.Contains("not found", t.Runner.LastOutcomeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancel_stops_the_whole_tree_and_no_further_lines_are_ever_sent()
    {
        var t = Create();
        var macro = Macro("A", 5.0, "/say One", "/say Two", "/say Three");
        t.Runner.Start([macro], macro.Id);
        DrainChat(t);
        Assert.Single(t.Sent);

        t.Runner.Cancel();
        Assert.False(t.Runner.IsRunning);

        Tick(t, 20); // long past every remaining delay
        Assert.Single(t.Sent); // still just the one line sent before Cancel
    }

    [Fact]
    public void Only_one_top_level_run_is_allowed_at_a_time()
    {
        var t = Create();
        var a = Macro("A", 100.0, "/say A1", "/say A2");
        var b = Macro("B", 1.0, "/say B1");
        t.Runner.Start([a, b], a.Id);

        var second = t.Runner.Start([a, b], b.Id);
        Assert.False(second.Success);
        Assert.Equal("A", t.Runner.RootMacroName); // the original run is untouched
    }

    [Fact]
    public void Actionready_directive_is_never_sent_to_the_game()
    {
        var t = Create(ActionReadyState.Ready);
        var macro = Macro("A", 1.0, "/actionready", "/say After");
        t.Runner.Start([macro], macro.Id);

        t.Runner.Tick(t.Clock.UtcNow); // probe reports Ready -> schedules the delay
        Tick(t, 1);

        Assert.DoesNotContain(t.Sent, s => s.Contains("actionready", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/say After", t.Sent);
    }

    [Fact]
    public void Busy_action_readiness_blocks_advancement_until_ready()
    {
        var t = Create(ActionReadyState.Busy);
        var macro = Macro("A", 1.0, "/actionready", "/say After");
        t.Runner.Start([macro], macro.Id);

        for (var i = 0; i < 5; i++) t.Runner.Tick(t.Clock.UtcNow);
        Assert.DoesNotContain(t.Sent, s => s == "/say After");
        Assert.True(t.Runner.IsRunning);

        t.Probe.State = ActionReadyState.Ready;
        t.Runner.Tick(t.Clock.UtcNow);
        Tick(t, 1);
        Assert.Contains("/say After", t.Sent);
    }

    [Fact]
    public void Unknown_action_readiness_never_auto_advances()
    {
        var t = Create(ActionReadyState.Unknown);
        var macro = Macro("A", 1.0, "/actionready", "/say After");
        t.Runner.Start([macro], macro.Id);

        for (var i = 0; i < 50; i++) t.Runner.Tick(t.Clock.UtcNow);
        Assert.True(t.Runner.IsRunning);
        Assert.DoesNotContain(t.Sent, s => s == "/say After");
    }

    [Fact]
    public void Cancel_while_waiting_on_action_readiness_stops_the_run()
    {
        var t = Create(ActionReadyState.Busy);
        var macro = Macro("A", 1.0, "/actionready", "/say After");
        t.Runner.Start([macro], macro.Id);
        t.Runner.Tick(t.Clock.UtcNow);

        t.Runner.Cancel();
        t.Probe.State = ActionReadyState.Ready;
        for (var i = 0; i < 5; i++) t.Runner.Tick(t.Clock.UtcNow);
        Assert.DoesNotContain(t.Sent, s => s == "/say After");
    }

    [Fact]
    public void A_run_is_immune_to_edits_made_to_a_separately_held_copy_of_the_library_after_start()
    {
        var t = Create();
        var childV1 = Macro("Child", 1.0, "/say V1");
        var parent = Macro("Parent", 1.0, MacroDirectiveParser.FormatNestedInvocation("Child"));
        var library = new List<SavedMacro> { parent, childV1 };

        t.Runner.Start(library, parent.Id);
        Tick(t, 1); // parent's only line is the nested call -> enters Child v1 immediately

        Assert.Contains("/say V1", t.Sent);
        // A caller-side edit to a DIFFERENT list object (simulating Settings being replaced by later authoring)
        // must never reach back into the already-snapshotted run.
        var childV2 = childV1 with { Lines = ["/say V2"] };
        library[1] = childV2;
        Assert.DoesNotContain(t.Sent, s => s == "/say V2");
    }

    // =========================================================================================================
    // Test infrastructure
    // =========================================================================================================

    private sealed record Fixture(MacroRunner Runner, Clock Clock, SchedulerService Scheduler, ChatCommandService Chat, List<string> Sent, FakeProbe Probe);

    private static Fixture Create(ActionReadyState probeState = ActionReadyState.Ready)
    {
        var clock = new Clock();
        var scheduler = new SchedulerService(clock);
        var sent = new List<string>();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { sent.Add(command); return true; }, TimeSpan.FromSeconds(0));
        var probe = new FakeProbe { State = probeState };
        var runner = new MacroRunner(scheduler, chat, probe);
        return new Fixture(runner, clock, scheduler, chat, sent, probe);
    }

    private static SavedMacro Macro(string name, double delaySeconds, params string[] lines) =>
        new(Guid.NewGuid(), name, IconId: 0, delaySeconds, lines);

    /// <summary>Fully drains whatever is currently queued in <see cref="ChatCommandService"/> — the fixture's
    /// zero minimum interval makes repeated immediate dispatch safe, so this loops until a call dispatches nothing
    /// new rather than relying on exactly one command being queued per call (<c>MacroRunner</c> can enqueue more
    /// than one command inside a single scheduler callback, e.g. entering a nested macro).</summary>
    private static void DrainChat(Fixture t)
    {
        while (true)
        {
            var before = t.Sent.Count;
            t.Chat.TickAsync().GetAwaiter().GetResult();
            if (t.Sent.Count == before) break;
        }
    }

    private static void Tick(Fixture t, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            t.Clock.Advance(1);
            t.Scheduler.Tick();
        }
        DrainChat(t);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    private sealed class FakeProbe : IActionReadyProbe
    {
        public ActionReadyState State { get; set; }
        public ActionReadyState Query() => State;
    }
}
