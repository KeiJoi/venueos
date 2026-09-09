using VenueOS.Services;

namespace VenueOS.Modules.Operations.Macro;

/// <summary>The result of probing whether the game is currently ready for the next action — MACRO spec §17-§20.
/// <see cref="Unknown"/> covers "state cannot safely be determined" (spec §20): the runner MUST NOT treat this as
/// ready and auto-advance; it stays waiting, exactly like <see cref="Busy"/>, until either the state resolves or the
/// operator cancels.</summary>
public enum ActionReadyState { Ready, Busy, Unknown }

/// <summary>The narrow, game-specific boundary <c>MacroRunner</c> depends on (NEW_MODULE_GUIDE.md §30's "small
/// interface the unsafe engine implements" pattern). The real implementation, <c>DalamudActionReadyProbe</c>, lives
/// in <c>VenueOS.Plugin.Macro</c> and is documented there — see docs/MACRO_IMPLEMENTATION.md §17 for the exact
/// researched game-state signals it reads. Kept here, in the pure project, purely as an interface so this file and
/// every other piece of <c>MacroRunner</c> stay unit-testable with a fake.</summary>
public interface IActionReadyProbe
{
    ActionReadyState Query();
}

public enum MacroRunPhase { Idle, Running, Complete, Cancelled, Failed }

public readonly record struct MacroLaunchResult(bool Success, string? Error)
{
    public static MacroLaunchResult Ok() => new(true, null);
    public static MacroLaunchResult Failed(string error) => new(false, error);
}

/// <summary>The single execution engine every invocation path shares (MACRO spec §42 — live tile, faux hotbar slot,
/// <c>/venueos macro "Name"</c>, and nested-macro invocation all route through the SAME <see cref="Start"/>/internal
/// stack, never four separate runners). Owns a real execution STACK (spec §12): a nested <c>/venueos macro "Child"</c>
/// line pushes a frame, runs the child to completion using the CHILD's own delay, then pops back and resumes the
/// parent using the PARENT's delay before the parent's next line (spec §13) — lines are never interleaved between
/// frames.
///
/// State authority: this class owns only in-memory execution state (NEW_MODULE_GUIDE.md §34a) — the macro
/// LIBRARY it reads from is a caller-supplied snapshot captured once at <see cref="Start"/> (spec §15): later edits
/// to a running macro's saved definition never affect an in-flight run.
///
/// Cancellation (spec §21/§22/§45): every <see cref="ChatCommandService.Enqueue"/> and
/// <see cref="SchedulerService.Schedule"/> call carries this run's own <see cref="CancellationTokenSource"/> token,
/// recreated on every <see cref="Start"/>/<see cref="Cancel"/>; a stale callback from a run already superseded is
/// additionally guarded by comparing against <see cref="currentRunId"/> (NEW_MODULE_GUIDE.md §24a), the same
/// reference-free "fresh id per run" pattern <c>GiveawayService</c> uses.</summary>
public sealed class MacroRunner(SchedulerService scheduler, ChatCommandService chat, IActionReadyProbe probe)
{
    /// <summary>A generous defensive backstop against runaway/pathological nesting (spec §14 — "a very high
    /// defensive max-depth may exist as secondary protection, but do not use an arbitrary tiny depth that harms
    /// legitimate nesting"). The primary protection is the exact on-stack cycle check below; this only guards
    /// against non-cyclic but absurdly deep chains.</summary>
    private const int MaxStackDepth = 64;

    private CancellationTokenSource runCancellation = new();
    private Guid currentRunId = Guid.Empty;
    private readonly Stack<RunFrame> stack = new();
    private readonly HashSet<Guid> stackIds = new();
    private IReadOnlyDictionary<string, SavedMacro> snapshotByName = new Dictionary<string, SavedMacro>(StringComparer.OrdinalIgnoreCase);
    private RunFrame? waitingOnActionReady;

    public MacroRunPhase Phase { get; private set; } = MacroRunPhase.Idle;
    public string? RootMacroName { get; private set; }
    public string? CurrentMacroName => stack.Count > 0 ? stack.Peek().MacroName : null;
    public int CurrentLineNumber => stack.Count > 0 ? stack.Peek().Index + 1 : 0;
    public int CurrentLineCount => stack.Count > 0 ? stack.Peek().Lines.Count : 0;
    public string StatusMessage { get; private set; } = "";
    /// <summary>Feedback for the LAST completed/cancelled/failed run, shown alongside the idle state (spec §4's
    /// simple "No macro running." idle view) until the next <see cref="Start"/> overwrites or clears it.</summary>
    public string? LastOutcomeMessage { get; private set; }
    /// <summary>The kind of the LAST finished run (Complete/Cancelled/Failed) — kept distinct from the live
    /// <see cref="Phase"/>, which always resets to <see cref="MacroRunPhase.Idle"/> once a run finishes, so the
    /// operator panel can style <see cref="LastOutcomeMessage"/> appropriately (success/warning/error).</summary>
    public MacroRunPhase LastOutcomeKind { get; private set; } = MacroRunPhase.Idle;
    public bool IsRunning => Phase == MacroRunPhase.Running;

    /// <summary>Raised when <see cref="IActionReadyProbe.Query"/> itself throws — never treated as "ready" (see
    /// <see cref="SafeQuery"/>), but still worth a Diagnostics entry (NEW_MODULE_GUIDE.md §25) since a probe that
    /// keeps faulting means <c>/actionready</c> can never resolve for this session. <c>MacroService</c> wires this
    /// to <c>DiagnosticsService.RecordFailure</c>.</summary>
    public event Action<string>? ProbeFaulted;

    public MacroLaunchResult Start(IReadOnlyList<SavedMacro> library, Guid macroId)
    {
        var macro = library.FirstOrDefault(m => m.Id == macroId);
        if (macro is null) return MacroLaunchResult.Failed("Macro not found.");
        return StartInternal(library, macro);
    }

    public MacroLaunchResult Start(IReadOnlyList<SavedMacro> library, string macroName)
    {
        var macro = library.FirstOrDefault(m => string.Equals(m.Name, macroName, StringComparison.OrdinalIgnoreCase));
        if (macro is null) return MacroLaunchResult.Failed($"Macro \"{macroName}\" not found.");
        return StartInternal(library, macro);
    }

    private MacroLaunchResult StartInternal(IReadOnlyList<SavedMacro> library, SavedMacro macro)
    {
        // Spec §21: only one active execution tree at a time — a second top-level launch while one is already
        // running is rejected rather than interleaved. Callers (tile grid, hotbar slots) additionally disable their
        // own launch controls while running; this is the authoritative backstop.
        if (IsRunning) return MacroLaunchResult.Failed("A macro is already running. Cancel it first.");

        CancelRun(setPhase: false);
        snapshotByName = library.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);
        currentRunId = Guid.NewGuid();
        RootMacroName = macro.Name;
        LastOutcomeMessage = null;
        Phase = MacroRunPhase.Running;

        if (!TryPush(macro, out var error))
        {
            Finish(MacroRunPhase.Failed, error!);
            return MacroLaunchResult.Failed(error!);
        }

        var runId = currentRunId;
        ExecuteCurrentLine(runId);
        return MacroLaunchResult.Ok();
    }

    public void Cancel()
    {
        if (Phase != MacroRunPhase.Running) return;
        CancelRun(setPhase: false);
        Finish(MacroRunPhase.Cancelled, $"\"{RootMacroName}\" was cancelled.");
    }

    /// <summary>Sets both <see cref="LastOutcomeMessage"/>/<see cref="LastOutcomeKind"/> for the UI and resets the
    /// live <see cref="Phase"/> back to <see cref="MacroRunPhase.Idle"/> — every run always ends up Idle (spec §4's
    /// simple two-state live view: Running or "No macro running."), with the outcome kept only as a transient
    /// message alongside the idle state.</summary>
    private void Finish(MacroRunPhase kind, string message)
    {
        LastOutcomeKind = kind;
        LastOutcomeMessage = message;
        Phase = MacroRunPhase.Idle;
    }

    /// <summary>Polled every frame by <c>MacroService.Tick</c> (only meaningful work happens while genuinely waiting
    /// on <c>/actionready</c> — spec §17/§20). Ready proceeds through the exact same "delay then next line" path a
    /// normal line uses; Busy/Unknown do nothing and leave <see cref="StatusMessage"/> as-is so the operator sees
    /// which state it's in. Never auto-advances on Unknown (spec §20's hard requirement).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (waitingOnActionReady is not { } frame || Phase != MacroRunPhase.Running) return;
        var runId = currentRunId;

        var state = SafeQuery();
        switch (state)
        {
            case ActionReadyState.Ready:
                waitingOnActionReady = null;
                StatusMessage = "Action ready — advancing.";
                AfterLineExecuted(frame, runId);
                break;
            case ActionReadyState.Busy:
                StatusMessage = "Waiting for action readiness…";
                break;
            case ActionReadyState.Unknown:
                StatusMessage = "Unable to determine action readiness. Waiting — cancel if this doesn't resolve.";
                break;
        }
    }

    private ActionReadyState SafeQuery()
    {
        try { return probe.Query(); }
        catch (Exception ex)
        {
            ProbeFaulted?.Invoke($"tools.macro: action-readiness probe failed ({ex.Message})");
            return ActionReadyState.Unknown; // a throwing probe must never be treated as "ready" (spec §20)
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Execution engine
    // -----------------------------------------------------------------------------------------------------------

    private bool TryPush(SavedMacro macro, out string? error)
    {
        if (stackIds.Contains(macro.Id))
        {
            var chain = string.Join(" → ", stack.Reverse().Select(f => f.MacroName).Append(macro.Name));
            error = $"Nested macro cycle detected: {chain}";
            return false;
        }
        if (stack.Count >= MaxStackDepth)
        {
            error = $"Macro nesting exceeded the maximum supported depth ({MaxStackDepth}).";
            return false;
        }

        stack.Push(new RunFrame(macro.Id, macro.Name, macro.Lines, macro.DelayBetweenLinesSeconds));
        stackIds.Add(macro.Id);
        error = null;
        return true;
    }

    private void Pop()
    {
        var frame = stack.Pop();
        stackIds.Remove(frame.MacroId);
    }

    /// <summary>Acts on the frame's CURRENT line (does not advance the index itself — that only happens once the
    /// line is fully "done": sent (plain), fully satisfied (/actionready), or fully returned-from (nested child) —
    /// see <see cref="AfterLineExecuted"/>).</summary>
    private void ExecuteCurrentLine(Guid runId)
    {
        if (runId != currentRunId) return; // stale callback from a superseded/cancelled run — never mutate state
        if (stack.Count == 0) { CompleteRun(); return; }
        var frame = stack.Peek();

        if (frame.Index >= frame.Lines.Count) { PopAndContinue(runId); return; }

        var directive = MacroDirectiveParser.Parse(frame.Lines[frame.Index]);
        switch (directive.Kind)
        {
            case MacroDirectiveKind.Blank:
                // A blank line carries no chat command and is not a meaningful FFXIV command — it costs no delay
                // slot (MacroDirectiveParser's doc comment) and is simply skipped.
                frame.Index++;
                ExecuteCurrentLine(runId);
                return;

            case MacroDirectiveKind.PlainLine:
                StatusMessage = "Sending line…";
                chat.Enqueue(new ChatCommand(directive.RawLine, runCancellation.Token));
                AfterLineExecuted(frame, runId);
                return;

            case MacroDirectiveKind.ActionReady:
                StatusMessage = "Waiting for action readiness…";
                waitingOnActionReady = frame; // consumed by the next Tick() — see Tick's doc comment
                return;

            case MacroDirectiveKind.NestedMacro:
                if (!snapshotByName.TryGetValue(directive.MacroName!, out var child))
                {
                    FailRun($"Nested macro \"{directive.MacroName}\" not found.");
                    return;
                }
                if (!TryPush(child, out var error)) { FailRun(error!); return; }
                StatusMessage = $"Running nested macro \"{child.Name}\"…";
                ExecuteCurrentLine(runId);
                return;
        }
    }

    /// <summary>The line at the top of the current frame just finished (sent / satisfied / returned-into). If
    /// there's another line waiting in this SAME frame, starts the frame's own configured delay before moving to
    /// it — this is the one place "the macro's delay" is applied, so a normal line, a just-satisfied
    /// <c>/actionready</c>, and a just-returned-from nested child all get the identical spacing behavior their
    /// owning frame configures (spec §10/§13/§17's "MACRO GLOBAL DELAY STARTS → NEXT LINE EXECUTES" sequence).
    /// If this was the frame's LAST line, there is no "next line" to space before — completion/pop happens
    /// immediately with no trailing delay, matching <c>GiveawayService.SendLine</c>'s existing "delay is only ever
    /// between two sends, never after the final one" convention.</summary>
    private void AfterLineExecuted(RunFrame frame, Guid runId)
    {
        if (frame.Index >= frame.Lines.Count - 1)
        {
            frame.Index = frame.Lines.Count;
            ExecuteCurrentLine(runId);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, frame.DelaySeconds));
        scheduler.Schedule(delay, () =>
        {
            if (runId != currentRunId) return;
            frame.Index++;
            ExecuteCurrentLine(runId);
        }, cancellationToken: runCancellation.Token);
    }

    private void PopAndContinue(Guid runId)
    {
        Pop();
        if (stack.Count == 0) { CompleteRun(); return; }
        // Spec §13: the parent's delay controls spacing before ITS next line, applied here — after the child frame
        // that was just popped has fully finished, not before it started.
        AfterLineExecuted(stack.Peek(), runId);
    }

    private void CompleteRun()
    {
        StatusMessage = "";
        waitingOnActionReady = null;
        Finish(MacroRunPhase.Complete, $"\"{RootMacroName}\" completed.");
    }

    private void FailRun(string message)
    {
        CancelRun(setPhase: false);
        Finish(MacroRunPhase.Failed, message);
    }

    private void CancelRun(bool setPhase)
    {
        runCancellation.Cancel();
        runCancellation.Dispose();
        runCancellation = new CancellationTokenSource();
        stack.Clear();
        stackIds.Clear();
        waitingOnActionReady = null;
        currentRunId = Guid.Empty;
        StatusMessage = "";
        if (setPhase) Phase = MacroRunPhase.Idle;
    }

    private sealed class RunFrame(Guid macroId, string macroName, IReadOnlyList<string> lines, double delaySeconds)
    {
        public Guid MacroId { get; } = macroId;
        public string MacroName { get; } = macroName;
        public IReadOnlyList<string> Lines { get; } = lines;
        public double DelaySeconds { get; } = delaySeconds;
        public int Index { get; set; }
    }
}
