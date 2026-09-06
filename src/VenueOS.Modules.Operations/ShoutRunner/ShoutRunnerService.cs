using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The ShoutRunner route engine — settings/persistence, the runtime terminal, and the RUN → Data Center →
/// World → Destination state machine. Deliberately <b>not</b> the donor's shape (one long-lived background
/// <c>Task.Run</c> looping with raw <c>await</c>s throughout, driven by unsynchronized <c>bool</c>/<c>CancellationTokenSource</c>
/// fields — familiarization report §11/§14/§17). Instead this follows the same idiom every other VenueOS service
/// already uses for time-driven behavior (<c>SchedulerService.Tick</c>, <c>GreeterService.Tick</c>,
/// <c>AttendanceService.Tick</c>): <see cref="Tick(DateTimeOffset)"/> is called once per frame from
/// <c>ShoutRunnerModule.Tick</c> and advances the state machine by exactly one step — a purely-time-based
/// transition (<c>WaitingActionDelay</c>/<c>WaitingRepeat</c>) compares <paramref name="now"/> against a stored due
/// time with no <c>Task</c> involved at all, while a transition that depends on live game state
/// (<see cref="IShoutRunnerAutomation"/>) starts that automation call once, stores the returned
/// <see cref="Task{T}"/> in exactly one field for that phase, and polls <c>IsCompleted</c> on subsequent ticks — this
/// class never itself contains an <c>await</c> and never starts an untracked background <c>Task.Run</c>. Exactly one
/// automation <see cref="Task"/> is ever in flight at a time, matching exactly one RUN ever being in flight at a
/// time (<see cref="CanStart"/>) — see the type-level remarks on <see cref="Stop"/>/<see cref="HardStop"/> for how
/// this removes the donor's Start/Stop race entirely rather than papering over it with a lock.
///
/// Fully unit-testable with a fake <see cref="IShoutRunnerAutomation"/> (returning already-completed
/// <see cref="Task{T}"/>s) and a fake <see cref="IClock"/> — no real waiting, no live Dalamud/game context, exactly
/// like <c>VenueOS.Modules.Operations.PartyFinder.PartyFinderService</c>.</summary>
public sealed class ShoutRunnerService(IShoutRunnerAutomation automation, ChatCommandService chat, VenueProfileService profiles, DiagnosticsService diagnostics, IClock clock)
{
    public const string ModuleId = "communication.announcements";

    /// <summary>Schema version 2 — the donor-shaped "Announcements" schema (scheduled multi-channel message presets,
    /// <c>Operations.cs</c>'s <c>AnnouncementsSettings</c>) was schema version 1 under this same module ID. Bumping
    /// the version changes the persistence key (<c>VenueModuleConfigKey</c> includes the schema version), so an old
    /// v1 payload is simply never looked up under v2 — <see cref="VenueProfileService.GetModuleConfig{T}"/> falls
    /// straight through to <see cref="ShoutRunnerSettings.Default"/> with no exception, no forced/invented
    /// conversion, and the old v1 payload is left completely untouched in storage (not deleted, not read) rather
    /// than "recovered" — there is nothing incompatible to recover from since the two schemas are never compared.
    /// See the reconstruction report's migration notes for exactly what this does and doesn't preserve.</summary>
    public const int SchemaVersion = 2;

    private static readonly TimeSpan StopRecoveryTimeout = TimeSpan.FromSeconds(15);

    private Guid venueId;
    private ShoutRunnerSettings settings = ShoutRunnerSettings.Default();
    private readonly ShoutRunnerTerminal terminal = new();

    private ShoutRunnerRunConfig? runConfig;
    private CancellationTokenSource? runCts;
    private CancellationTokenSource? stoppingCts;
    private ShoutRunnerRoutePlanner.TraversalDirection fallbackDirection = ShoutRunnerRoutePlanner.TraversalDirection.Forward;

    private int dataCenterIndex = -1;
    private IReadOnlyList<string> currentWorlds = [];
    private int worldIndex = -1;
    private bool currentDataCenterHadSkip;
    private IReadOnlyList<ShoutRunnerDestinationStep> currentSteps = [];
    private int stepIndex = -1;
    private bool currentWorldHadFailure;
    private DateTimeOffset runStartedAt;
    private DateTimeOffset delayUntil;
    private DateTimeOffset nextRunAtUtc;

    private enum ReadinessPurpose { EnterWorld, Teleport, Shout }
    private ReadinessPurpose pendingReadinessPurpose;

    // Exactly one of these is ever non-null at a time — see the type-level remark on the Tick-driven polling model.
    private Task<ShoutRunnerReadinessOutcome>? readinessTask;
    private Task<ShoutRunnerCrossDataCenterCheck>? classifyTask;
    private Task<ShoutRunnerTransferOutcome>? transferTask;
    private Task<string?>? locateTask;
    private Task<ShoutRunnerTeleportOutcome>? teleportTask;
    private Task<bool>? shoutTask;
    private Task<ShoutRunnerReadinessOutcome>? stoppingTask;

    public ShoutRunnerState State { get; private set; } = ShoutRunnerState.Stopped;
    public int RunNumber { get; private set; }
    public string StatusText { get; private set; } = "Stopped";
    public DateTimeOffset? NextRunAtUtc => State == ShoutRunnerState.WaitingRepeat ? nextRunAtUtc : null;
    public ShoutRunnerSettings Settings => settings;
    public IReadOnlyList<ShoutRunnerTerminalEvent> TerminalEvents => terminal.Events;

    /// <summary>Only <see cref="ShoutRunnerState.Stopped"/> and <see cref="ShoutRunnerState.Faulted"/> allow a new
    /// RUN to begin — every other state means a RUN or its Stop cleanup is already in flight, which is the entire
    /// "no overlapping RUN"/"Start unavailable while stopping" requirement: there is no separate lock or boolean to
    /// keep in sync with this, since <see cref="State"/> is the single source of truth for what's currently allowed.</summary>
    public bool CanStart => State is ShoutRunnerState.Stopped or ShoutRunnerState.Faulted;

    /// <summary>Called from <c>ShoutRunnerModule.OnVenueChangedAsync</c> — every venue switch. Always hard-stops
    /// first (a different venue's Shout Message/route policy must never continue running under the new venue
    /// context — reconstruction brief "VENUE SWITCHING"), then loads the new venue's settings and clears the
    /// terminal, since the runtime history is explicitly session-scoped to one venue's activation, not carried
    /// across a venue switch (see <see cref="ShoutRunnerTerminal"/>'s doc comment).</summary>
    public void Load(Guid nextVenueId)
    {
        HardStop();
        venueId = nextVenueId;
        settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, ShoutRunnerSettings.Default);
        terminal.Clear();
        RunNumber = 0;
        automation.ResetForVenue();
    }

    public void UpdateShoutMessage(string message)
    {
        if (settings.ShoutMessage == message) return;
        Mutate(s => s with { ShoutMessage = message });
    }

    public void SetRepeatEnabled(bool enabled) => Mutate(s => s with { RepeatEnabled = enabled });
    public void SetInterval(int hours, int minutes, int seconds) => Mutate(s => s with { IntervalHours = Math.Max(0, hours), IntervalMinutes = Math.Max(0, minutes), IntervalSeconds = Math.Max(0, seconds) });
    public void SetDelaySeconds(int seconds) => Mutate(s => s with { DelayBetweenActionsSeconds = Math.Clamp(seconds, 0, 120) });

    public void SetDataCenterSelected(string dataCenter, bool selected)
    {
        if (!ShoutRunnerCatalog.IsKnownDataCenter(dataCenter)) return;
        var set = new HashSet<string>(settings.SelectedDataCenters, StringComparer.OrdinalIgnoreCase);
        if (selected) set.Add(dataCenter); else set.Remove(dataCenter);
        Mutate(s => s with { SelectedDataCenters = [.. ShoutRunnerCatalog.OrderSelectedDataCenters(set)] });
    }

    public void AddDestination(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Mutate(s => s with { Destinations = [.. s.Destinations, name.Trim()] });
    }

    public void RemoveDestinationAt(int index)
    {
        if (index < 0 || index >= settings.Destinations.Count) return;
        Mutate(s => s with { Destinations = [.. s.Destinations.Where((_, i) => i != index)] });
    }

    public void RenameDestinationAt(int index, string name)
    {
        if (index < 0 || index >= settings.Destinations.Count || string.IsNullOrWhiteSpace(name)) return;
        Mutate(s => s with { Destinations = [.. s.Destinations.Select((d, i) => i == index ? name.Trim() : d)] });
    }

    public void MoveDestination(int index, int direction)
    {
        var target = index + direction;
        if (index < 0 || index >= settings.Destinations.Count || target < 0 || target >= settings.Destinations.Count) return;
        Mutate(s =>
        {
            var list = new List<string>(s.Destinations);
            (list[index], list[target]) = (list[target], list[index]);
            return s with { Destinations = list };
        });
    }

    private void Mutate(Func<ShoutRunnerSettings, ShoutRunnerSettings> update)
    {
        settings = update(settings);
        profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, settings);
    }

    /// <summary>Starts RUN 1 immediately (per the reconstruction brief's START BUTTON requirements) after validating
    /// the minimum viable configuration — refuses silently starting with an empty Shout Message, no selected Data
    /// Center, or no configured destination rather than beginning a RUN that could never do anything. The route
    /// policy (selected Data Centers, in canonical order; destinations, in configured order; action delay) is
    /// snapshotted into <see cref="ShoutRunnerRunConfig"/> here and used for the entire RUN even if Settings are
    /// edited mid-flight — see that type's doc comment.</summary>
    public ShoutRunnerStartResult Start()
    {
        if (!CanStart) return ShoutRunnerStartResult.AlreadyRunning;
        if (string.IsNullOrWhiteSpace(settings.ShoutMessage)) return ShoutRunnerStartResult.ShoutMessageRequired;
        var dataCenters = ShoutRunnerCatalog.OrderSelectedDataCenters(settings.SelectedDataCenters);
        if (dataCenters.Count == 0) return ShoutRunnerStartResult.NoDataCenterSelected;
        if (settings.Destinations.Count == 0) return ShoutRunnerStartResult.NoDestinationConfigured;

        runConfig = new ShoutRunnerRunConfig(dataCenters, [.. settings.Destinations], settings.ClampedDelaySeconds());
        RunNumber = 0;
        fallbackDirection = ShoutRunnerRoutePlanner.TraversalDirection.Forward;
        automation.ResetForVenue();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        State = ShoutRunnerState.Starting;
        StatusText = "Starting…";
        BeginNextRun();
        return ShoutRunnerStartResult.Started;
    }

    /// <summary>The graceful, operator-facing Stop — see the reconstruction brief's "STOP BUTTON — STRONG STOP".
    /// Cancels the run token immediately (which, combined with <see cref="Tick(DateTimeOffset)"/> no longer polling
    /// any run-phase task once <see cref="State"/> leaves those phases, is what makes a stale completion from before
    /// Stop unable to resurrect route state — nothing ever reads those abandoned <see cref="Task"/>s again), tells
    /// automation to abort any in-flight Lifestream transfer and dismiss transfer UI, and — because a chat command
    /// already in <see cref="ChatCommandService"/>'s queue is enqueued with this same <paramref name="runCts"/>
    /// token — guarantees a not-yet-dispatched <c>/shout</c> is skipped rather than fired after Stop (see
    /// <see cref="ChatCommandService.TickAsync"/>'s own cancellation check; this is the exact mechanism
    /// Greeter/VIP already rely on for the same guarantee, not a new capability added to that shared service).
    /// Then attempts one bounded readiness/recovery pass before settling into <see cref="ShoutRunnerState.Stopped"/>
    /// regardless of whether that recovery actually succeeded — per the brief's "STOP RECOVERY FAILURE", ShoutRunner
    /// must end up Stopped either way, never hang retrying forever.</summary>
    public void Stop()
    {
        if (State is ShoutRunnerState.Stopped or ShoutRunnerState.Stopping) return;
        runCts?.Cancel();
        automation.Abort();
        ClearPendingTasks();
        terminal.Add(Event(ShoutRunnerEventSeverity.Warning, "Stop requested."));
        State = ShoutRunnerState.Stopping;
        StatusText = "Stopping…";
        stoppingCts?.Dispose();
        stoppingCts = new CancellationTokenSource(StopRecoveryTimeout);
        stoppingTask = automation.EnsureReadyAsync(stoppingCts.Token);
    }

    /// <summary>The non-graceful teardown used for venue switch (<see cref="Load"/>), module disable
    /// (<c>ShoutRunnerModule.IsEnabled</c>'s setter), and plugin dispose (<c>ShoutRunnerModule.DisposeAsync</c>) —
    /// none of which can await a bounded recovery pass (a venue switch must complete synchronously; disable/dispose
    /// must never hang the plugin). Cancels everything immediately and unconditionally settles into
    /// <see cref="ShoutRunnerState.Stopped"/> with no further waiting — safe to call at any time, including when
    /// already stopped (idempotent), and safe to call twice in a row (e.g. disable followed by plugin dispose).</summary>
    public void HardStop()
    {
        runCts?.Cancel(); runCts?.Dispose(); runCts = null;
        stoppingCts?.Cancel(); stoppingCts?.Dispose(); stoppingCts = null;
        automation.Abort();
        ClearPendingTasks();
        stoppingTask = null;
        State = ShoutRunnerState.Stopped;
        StatusText = "Stopped";
        ClearCursor();
    }

    /// <summary>Advances the state machine by exactly one step for whatever phase <see cref="State"/> currently is
    /// in — see the type-level remark for the overall polling model. Called once per frame from
    /// <c>ShoutRunnerModule.Tick</c>.</summary>
    public void Tick(DateTimeOffset now)
    {
        switch (State)
        {
            case ShoutRunnerState.Stopped:
            case ShoutRunnerState.Faulted:
            case ShoutRunnerState.Starting:
                return;
            case ShoutRunnerState.Stopping:
                if (stoppingTask is { IsCompleted: true }) FinishStopping();
                return;
            case ShoutRunnerState.WaitingRepeat:
                if (now >= nextRunAtUtc) BeginNextRun();
                return;
            case ShoutRunnerState.WaitingActionDelay:
                if (now >= delayUntil) AdvanceToNextStep();
                return;
            case ShoutRunnerState.RecoveringTravel:
                if (readinessTask is { IsCompleted: true }) ProcessReadinessCompletion();
                return;
            case ShoutRunnerState.TravelingWorld:
                if (classifyTask is { IsCompleted: true }) ProcessClassifyCompletion();
                else if (transferTask is { IsCompleted: true }) ProcessTransferCompletion();
                else if (locateTask is { IsCompleted: true }) ProcessLocateCompletion();
                return;
            case ShoutRunnerState.Teleporting:
                if (teleportTask is { IsCompleted: true }) ProcessTeleportCompletion();
                return;
            case ShoutRunnerState.SendingShout:
                if (shoutTask is { IsCompleted: true }) ProcessShoutCompletion();
                return;
        }
    }

    private void BeginNextRun()
    {
        RunNumber++;
        runStartedAt = clock.UtcNow;
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"RUN {RunNumber} started."));
        dataCenterIndex = -1;
        AdvanceToNextDataCenter();
    }

    private void AdvanceToNextDataCenter()
    {
        dataCenterIndex++;
        if (dataCenterIndex >= runConfig!.DataCentersInOrder.Count) { CompleteRun(); return; }

        var dc = runConfig.DataCentersInOrder[dataCenterIndex];
        currentWorlds = ShoutRunnerCatalog.WorldsIn(dc);
        worldIndex = -1;
        currentDataCenterHadSkip = false;
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"{dc} — starting.", dc));
        AdvanceToNextWorld();
    }

    private void SkipRemainingDataCenter(string reason)
    {
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"{dc} — SKIPPED: {reason}", dc));
        AdvanceToNextDataCenter();
    }

    private void AdvanceToNextWorld()
    {
        worldIndex++;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        if (worldIndex >= currentWorlds.Count)
        {
            terminal.Add(Event(currentDataCenterHadSkip ? ShoutRunnerEventSeverity.Warning : ShoutRunnerEventSeverity.Success,
                currentDataCenterHadSkip ? $"{dc} — completed with skips." : $"{dc} — completed.", dc));
            AdvanceToNextDataCenter();
            return;
        }

        var world = currentWorlds[worldIndex];
        currentWorldHadFailure = false;
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"{world} — starting.", dc, world));
        StatusText = $"RUN {RunNumber} — {dc} / {world}";
        BeginReadinessGate(ReadinessPurpose.EnterWorld);
    }

    private void BeginReadinessGate(ReadinessPurpose purpose)
    {
        pendingReadinessPurpose = purpose;
        State = ShoutRunnerState.RecoveringTravel;
        readinessTask = automation.EnsureReadyAsync(runCts!.Token);
    }

    private void ProcessReadinessCompletion()
    {
        var outcome = SafeResult(readinessTask!, ShoutRunnerReadinessOutcome.RecoveryFailed("the automation task failed unexpectedly"));
        readinessTask = null;
        if (outcome.RecoveryAttempted)
        {
            terminal.Add(Event(ShoutRunnerEventSeverity.Warning, "Recovery started — confirming the character is logged in and controllable."));
            terminal.Add(outcome.Ready
                ? Event(ShoutRunnerEventSeverity.Success, $"Recovery completed. {outcome.Detail}".Trim())
                : Event(ShoutRunnerEventSeverity.Failure, $"Recovery failed. {outcome.Detail}".Trim()));
        }

        if (!outcome.Ready) { EnterFaulted(outcome.Detail ?? "The character could not be confirmed logged in and playable."); return; }

        State = pendingReadinessPurpose switch
        {
            ReadinessPurpose.EnterWorld => ShoutRunnerState.TravelingWorld,
            ReadinessPurpose.Teleport => ShoutRunnerState.Teleporting,
            _ => ShoutRunnerState.SendingShout,
        };
        switch (pendingReadinessPurpose)
        {
            case ReadinessPurpose.EnterWorld: BeginClassifyTransfer(); break;
            case ReadinessPurpose.Teleport: BeginTeleport(); break;
            case ReadinessPurpose.Shout: BeginShout(); break;
        }
    }

    private void BeginClassifyTransfer() => classifyTask = automation.ClassifyTransferAsync(currentWorlds[worldIndex], runCts!.Token);

    private void ProcessClassifyCompletion()
    {
        var check = SafeResult(classifyTask!, ShoutRunnerCrossDataCenterCheck.Unknown);
        classifyTask = null;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        if (check == ShoutRunnerCrossDataCenterCheck.Unknown)
        {
            // Familiarization report §9: the donor silently defaulted an unclassifiable transfer to same-Data-Center
            // behavior. Treated here as an explicit World-level failure instead — never a silent guess.
            currentDataCenterHadSkip = true;
            terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"{world} — SKIPPED: could not determine same-Data-Center vs cross-Data-Center travel.", dc, world));
            AdvanceToNextWorld();
            return;
        }

        transferTask = automation.TravelToWorldAsync(world, check == ShoutRunnerCrossDataCenterCheck.CrossDataCenter, runCts!.Token);
    }

    private void ProcessTransferCompletion()
    {
        var outcome = SafeResult(transferTask!, ShoutRunnerTransferOutcome.Failed("the automation task failed unexpectedly"));
        transferTask = null;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        switch (outcome.Result)
        {
            case ShoutRunnerTransferResult.Success:
                locateTask = automation.TryGetCurrentPlaceNameAsync(runCts!.Token);
                return;
            case ShoutRunnerTransferResult.WorldCongestedSkip:
                currentDataCenterHadSkip = true;
                terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"{world} — SKIPPED: congested. {outcome.Detail}".Trim(), dc, world));
                AdvanceToNextWorld();
                return;
            case ShoutRunnerTransferResult.DataCenterUnavailableSkip:
                SkipRemainingDataCenter($"data center unavailable. {outcome.Detail}".Trim());
                return;
            default:
                // Reserved for a genuine infrastructure failure (Lifestream unavailable, or recovery-after-failure
                // itself could not restore a playable state) — see IShoutRunnerAutomation.TravelToWorldAsync's doc
                // comment. Ending the whole automation here (rather than skipping just this World) avoids silently
                // failing through every remaining World/Data Center one at a time for a condition that will not
                // resolve itself.
                EnterFaulted(outcome.Detail ?? "Travel failed and the character's state could not be confirmed safe.");
                return;
        }
    }

    private void ProcessLocateCompletion()
    {
        var place = SafeResult(locateTask!, (string?)null);
        locateTask = null;
        currentSteps = ShoutRunnerRoutePlanner.PlanWorldRoute(runConfig!.Destinations, place, ref fallbackDirection);
        stepIndex = -1;
        AdvanceToNextStep();
    }

    private void AdvanceToNextStep()
    {
        stepIndex++;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        if (stepIndex >= currentSteps.Count)
        {
            terminal.Add(Event(currentWorldHadFailure ? ShoutRunnerEventSeverity.Warning : ShoutRunnerEventSeverity.Success,
                currentWorldHadFailure ? $"{world} — completed with errors." : $"{world} — completed.", dc, world));
            AdvanceToNextWorld();
            return;
        }

        var step = currentSteps[stepIndex];
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"{step.Destination} — starting.", dc, world, step.Destination));
        BeginReadinessGate(step.RequiresTeleport ? ReadinessPurpose.Teleport : ReadinessPurpose.Shout);
    }

    private void BeginTeleport() => teleportTask = automation.TeleportToDestinationAsync(currentSteps[stepIndex].Destination, runCts!.Token);

    private void ProcessTeleportCompletion()
    {
        var outcome = SafeResult(teleportTask!, ShoutRunnerTeleportOutcome.Failed("the automation task failed unexpectedly"));
        teleportTask = null;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        var step = currentSteps[stepIndex];
        if (outcome.Result == ShoutRunnerTeleportResult.Failed)
        {
            currentWorldHadFailure = true;
            terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"{step.Destination} — SKIPPED: teleport failed. {outcome.Detail}".Trim(), dc, world, step.Destination));
            BeginActionDelay();
            return;
        }

        BeginReadinessGate(ReadinessPurpose.Shout);
    }

    /// <summary>The Shout Message is read live here — not from the RUN-start snapshot — so a small wording edit made
    /// mid-route takes effect on the next unsent shout without restarting the route (reconstruction brief "SHOUT
    /// MESSAGE"). A shout already handed to <see cref="ChatCommandService"/> is never retroactively altered.</summary>
    private void BeginShout()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = settings.ShoutMessage.Trim();
        if (string.IsNullOrWhiteSpace(message)) tcs.SetResult(false);
        else chat.Enqueue(new($"/shout {message}", runCts!.Token, (success, _) => tcs.TrySetResult(success)));
        shoutTask = tcs.Task;
    }

    private void ProcessShoutCompletion()
    {
        var success = SafeResult(shoutTask!, false);
        shoutTask = null;
        var dc = runConfig!.DataCentersInOrder[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        var step = currentSteps[stepIndex];
        if (!success)
        {
            currentWorldHadFailure = true;
            terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"{step.Destination} — SHOUT FAILED.", dc, world, step.Destination));
        }
        else
        {
            terminal.Add(Event(ShoutRunnerEventSeverity.Success, $"{step.Destination} — SHOUT SENT.", dc, world, step.Destination));
        }

        BeginActionDelay();
    }

    private void BeginActionDelay()
    {
        State = ShoutRunnerState.WaitingActionDelay;
        delayUntil = clock.UtcNow + TimeSpan.FromSeconds(runConfig!.DelayBetweenActionsSeconds);
    }

    /// <summary>RUN-start-anchored, overrun-safe repeat scheduling — a deliberate departure from the donor's
    /// completion-anchored interval (familiarization report §15/reconstruction brief "REPEAT SCHEDULING"). The next
    /// nominal boundary is <c>runStartedAt + interval</c>; if that boundary has already passed by the time the RUN
    /// finishes, the schedule advances forward one interval at a time until it lands on a boundary still in the
    /// future — it never runs a catch-up RUN for a missed boundary and never overlaps a new RUN with one still
    /// finishing (see <see cref="CanStart"/>).</summary>
    private void CompleteRun()
    {
        terminal.Add(Event(ShoutRunnerEventSeverity.Success, $"RUN {RunNumber} complete."));
        if (!settings.RepeatEnabled)
        {
            State = ShoutRunnerState.Stopped;
            StatusText = "Stopped";
            runCts?.Dispose(); runCts = null;
            ClearCursor();
            return;
        }

        var interval = settings.GetInterval();
        var boundary = runStartedAt + interval;
        var now = clock.UtcNow;
        while (boundary <= now) boundary += interval;
        nextRunAtUtc = boundary;
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"Next RUN scheduled for {boundary:t}."));
        State = ShoutRunnerState.WaitingRepeat;
        StatusText = $"Waiting for next RUN — {boundary:t}";
    }

    private void EnterFaulted(string reason)
    {
        diagnostics.RecordFailure($"ShoutRunner: {reason}");
        terminal.Add(Event(ShoutRunnerEventSeverity.Failure, $"RUN {RunNumber} stopped — {reason}"));
        State = ShoutRunnerState.Faulted;
        StatusText = $"Faulted — {reason}";
        runCts?.Cancel(); runCts?.Dispose(); runCts = null;
        ClearPendingTasks();
        ClearCursor();
    }

    private void FinishStopping()
    {
        var outcome = SafeResult(stoppingTask!, ShoutRunnerReadinessOutcome.RecoveryFailed("the stop recovery task failed unexpectedly"));
        stoppingTask = null;
        stoppingCts?.Dispose(); stoppingCts = null;
        if (outcome.Ready)
        {
            terminal.Add(Event(ShoutRunnerEventSeverity.Success, "Stopped."));
        }
        else
        {
            terminal.Add(Event(ShoutRunnerEventSeverity.Warning, "Stopped — manual recovery may be required."));
            diagnostics.RecordFailure("ShoutRunner: Stop cleanup could not confirm the character returned to a playable state.");
        }

        State = ShoutRunnerState.Stopped;
        StatusText = "Stopped";
        runCts?.Dispose(); runCts = null;
        ClearCursor();
    }

    private void ClearPendingTasks()
    {
        readinessTask = null; classifyTask = null; transferTask = null; locateTask = null; teleportTask = null; shoutTask = null;
    }

    private void ClearCursor()
    {
        dataCenterIndex = -1; currentWorlds = []; worldIndex = -1; currentDataCenterHadSkip = false;
        currentSteps = []; stepIndex = -1; currentWorldHadFailure = false; runConfig = null;
    }

    private ShoutRunnerTerminalEvent Event(ShoutRunnerEventSeverity severity, string text, string? dc = null, string? world = null, string? destination = null) =>
        new(clock.UtcNow, RunNumber, severity, text, dc, world, destination);

    /// <summary>Reads a completed automation <see cref="Task{T}"/>'s result without ever throwing — a faulted task
    /// (the real automation implementation threw instead of catching its own exception, which it should never do,
    /// mirroring <c>PartyFinderAutomationService</c>'s own contract) is reported to Diagnostics as an infrastructure
    /// problem rather than propagating out of <see cref="Tick(DateTimeOffset)"/>; a canceled task (Stop was pressed
    /// mid-operation) is treated as ordinary, silent fallback with no diagnostic noise.</summary>
    private T SafeResult<T>(Task<T> task, T fallback)
    {
        if (task.IsCompletedSuccessfully) return task.Result;
        if (task.IsFaulted) diagnostics.RecordFailure($"ShoutRunner: automation task failed unexpectedly: {task.Exception?.GetBaseException().Message}");
        return fallback;
    }
}
