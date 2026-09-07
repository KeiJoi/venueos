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
public sealed class ShoutRunnerService(IShoutRunnerAutomation automation, ChatCommandService chat, VenueProfileService profiles, DiagnosticsService diagnostics, IClock clock, IShoutRunnerRecoveryStore recoveryStore)
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

    // ----- crash-recovery journal state (see ShoutRunnerRecoveryJournal's doc comment) -----
    private Guid? activeRunId;
    private List<ShoutRunnerRecoverySkippedDataCenter> activeSkippedDataCenters = [];
    private List<string> activeCompletedDestinationsInCurrentWorld = [];
    private List<ShoutRunnerDestinationStep>? activeCurrentWorldPlan;
    // Set only by Resume(), for exactly the one World it identified as already in progress; consumed (and cleared)
    // the first time ProcessTransferCompletion's success branch runs afterward — see that method's doc comment.
    private IReadOnlyList<ShoutRunnerDestinationStep>? pendingResumedWorldSteps;
    private int pendingResumedStepIndex;

    public ShoutRunnerState State { get; private set; } = ShoutRunnerState.Stopped;
    public int RunNumber { get; private set; }
    public string StatusText { get; private set; } = "Stopped";
    public DateTimeOffset? NextRunAtUtc => State == ShoutRunnerState.WaitingRepeat ? nextRunAtUtc : null;
    public ShoutRunnerSettings Settings => settings;
    public IReadOnlyList<ShoutRunnerTerminalEvent> TerminalEvents => terminal.Events;

    /// <summary>Loaded once, at construction, from whatever <see cref="IShoutRunnerRecoveryStore"/> already has on
    /// disk — a recovery journal is not tied to any particular venue switch (<see cref="Load"/> never touches it),
    /// since it represents "the one currently interrupted run," independent of which venue is currently being
    /// viewed. Null means no recoverable run exists (either genuinely none, or the file was unreadable — see
    /// <see cref="RecoveredJournalIsCorrupt"/> to distinguish those two cases).</summary>
    public ShoutRunnerRecoveryJournal? RecoveredJournal { get; private set; } = recoveryStore.TryLoad(out _);

    /// <summary>True only when a recovery file exists but could not be safely loaded (malformed JSON, unsupported
    /// schema version, or internally inconsistent route state) — the crash-recovery brief's "CORRUPT / INCOMPATIBLE
    /// RECOVERY FILE" case. VenueOS must never crash over this; the operator is offered Discard Recovery instead.</summary>
    public bool RecoveredJournalIsCorrupt { get; private set; } = DetermineRecoveryCorrupt(recoveryStore);

    /// <summary>Whether the recovered journal (if any) belongs to the venue currently loaded into this service — see
    /// the crash-recovery brief's "VENUE PROFILE SAFETY". A journal from a different venue is never silently offered
    /// for Resume; the operator must be on the owning venue first.</summary>
    public bool RecoveredJournalBelongsToCurrentVenue => RecoveredJournal is { } journal && journal.VenueId == venueId;

    /// <summary>Only meaningful alongside <see cref="RecoveredJournalBelongsToCurrentVenue"/> — Resume itself
    /// re-validates venue ownership regardless (see <see cref="Resume"/>), so this is purely a UI convenience gate,
    /// not the sole enforcement point for cross-venue safety.</summary>
    public bool CanResume => CanStart && RecoveredJournal is not null && !RecoveredJournalIsCorrupt;

    /// <summary>Everything the Resume summary UI needs to show the operator "what will happen" before they click
    /// Resume — see the crash-recovery brief's "RESUME SUMMARY". Null whenever <see cref="RecoveredJournal"/> is.</summary>
    public ShoutRunnerRecoverySummary? RecoveredSummary
    {
        get
        {
            if (RecoveredJournal is not { } journal) return null;
            var venueName = profiles.Profiles.FirstOrDefault(v => v.Id == journal.VenueId)?.DisplayName ?? journal.VenueDisplayNameSnapshot;
            var lastCompleted = journal.CompletedDestinationsInCurrentWorld.Count > 0 ? journal.CompletedDestinationsInCurrentWorld[^1] : null;
            var next = journal.CurrentWorldPlan?.Select(s => s.Destination)
                .FirstOrDefault(d => !journal.CompletedDestinationsInCurrentWorld.Contains(d, StringComparer.OrdinalIgnoreCase));
            return new ShoutRunnerRecoverySummary(journal.VenueId, venueName, journal.RunNumber, journal.CurrentDataCenter, journal.CurrentWorld, lastCompleted, next, journal.SkippedDataCenters, journal.LastCheckpointAtUtc);
        }
    }

    private static bool DetermineRecoveryCorrupt(IShoutRunnerRecoveryStore store) { store.TryLoad(out var corrupt); return corrupt; }

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
        // A brand-new run always supersedes whatever recovery journal was on disk — a deliberate, confirmed operator
        // decision (see the crash-recovery brief's "START NEW RUN WITH RECOVERY PRESENT"; the confirmation itself is
        // the operator panel's job, not this method's — see ShoutRunnerOperatorPanel). WriteCheckpoint's Save() call
        // (triggered inside BeginNextRun below) atomically replaces whatever file was already there. Per-run identity
        // (RunId, skip history) is reset in BeginNextRun itself, not here, since that same reset must also apply to
        // every later Repeat-triggered RUN, not just this first one.
        activeCompletedDestinationsInCurrentWorld = [];
        activeCurrentWorldPlan = null;
        automation.ResetForVenue();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        State = ShoutRunnerState.Starting;
        StatusText = "Starting…";
        // BeginNextRun synchronously drives all the way into the first World's readiness gate (kicking off, but not
        // yet resolving, the first automation call) before returning — AdvanceToNextWorld's own checkpoint write
        // (see its doc comment) captures the initial journal, with the first Data Center/World already known,
        // before any real travel/shout action has actually completed. This satisfies the crash-recovery brief's
        // "create initial journal before the first travel/action begins" without a separate, less-accurate write.
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
        // Crash-recovery brief "STOP / CANCEL SEMANTICS": ShoutRunner has no Pause concept — Stop is the operator's
        // only manual termination action, and represents an explicit decision to abandon this run, not merely an
        // interruption. Cleared immediately (before the Stopping cleanup even runs) so a crash during that cleanup
        // can never leave a stale journal behind either.
        recoveryStore.Delete();
        RecoveredJournal = null;
        RecoveredJournalIsCorrupt = false;
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
        // Each RUN — including one reached via Repeat, not just the very first Start() — is its own independent
        // recovery unit with its own identity and its own clean skip history; a repeat cycle must never inherit the
        // previous RUN's RunId or SkippedDataCenters into its checkpoint (a live-verified gap this fixes: only
        // Start() used to reset these, so RUN 2's journal could silently still list RUN 1's already-resolved skips).
        activeRunId = Guid.NewGuid();
        activeSkippedDataCenters = [];
        pendingResumedWorldSteps = null;
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
        // Crash-recovery brief "RUN-SCOPED DATA CENTER SKIPS": recorded the moment the runner commits to skipping
        // it, so Resume must never re-attempt a Data Center this run already gave up on. Deliberately NOT persisted
        // to disk here, though — CurrentDataCenter in the journal still names THIS (about-to-be-skipped) Data
        // Center at this exact point, so writing now would make a crash right here resume by re-entering the very
        // Data Center just given up on. AdvanceToNextDataCenter (below) moves the cursor past it synchronously and
        // itself triggers the next real checkpoint (via AdvanceToNextWorld, or CompleteRun deleting the journal
        // entirely if this was the last Data Center) with the corrected position and this skip already included.
        activeSkippedDataCenters.Add(new ShoutRunnerRecoverySkippedDataCenter(dc, reason, clock.UtcNow));
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
        // A freshly-entered World always starts with nothing completed yet — see ShoutRunnerRecoveryJournal's doc
        // comment for why a World in this state is always resumed exactly like a fresh entry (nothing to preserve),
        // never by replaying a stale plan. Checkpointed here (not just on each successful destination) so Resume's
        // summary reflects this World immediately, without relying on the self-correcting replay a crash before its
        // first destination would otherwise require (see the type-level remark on WriteCheckpoint).
        activeCompletedDestinationsInCurrentWorld = [];
        activeCurrentWorldPlan = null;
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"{world} — starting.", dc, world));
        StatusText = $"RUN {RunNumber} — {dc} / {world}";
        WriteCheckpoint();
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
                // Resume's one special case: a World that already has a persisted, partially-completed plan from
                // before a crash replays its exact remaining steps (never re-detects location or re-plans) — see
                // ShoutRunnerRecoveryJournal's and Resume's doc comments for why. Consumed exactly once; every other
                // World (resumed-fresh or not) takes the normal locate-then-plan path unchanged.
                if (pendingResumedWorldSteps is { } resumedSteps)
                {
                    currentSteps = resumedSteps;
                    stepIndex = pendingResumedStepIndex - 1;
                    pendingResumedWorldSteps = null;
                    AdvanceToNextStep();
                    return;
                }

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
            // The crash-recovery brief's core checkpoint unit: a destination becomes durably completed only once its
            // shout has actually succeeded — never merely because travel/teleport/a shout attempt started (see
            // ShoutRunnerRecoveryJournal's doc comment). Checkpointed BEFORE the pacing delay/next step begins.
            activeCompletedDestinationsInCurrentWorld.Add(step.Destination);
            activeCurrentWorldPlan = [.. currentSteps];
            WriteCheckpoint();
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
        // Crash-recovery brief "WHEN TO DELETE THE JOURNAL": a RUN — one full pass through every selected Data
        // Center — is this codebase's actual unit of "the run" (see the terminal's own "RUN {n} started"/"RUN {n}
        // complete" language, which predates this feature); a repeating ShoutRunner's *next* RUN is a fresh start
        // using live settings, not a continuation of anything that needs crash recovery, so the journal is cleared
        // here regardless of whether Repeat immediately schedules another RUN. A crash during the WaitingRepeat gap
        // itself is therefore not resumable — the operator starts RUN {n+1} manually (or waits for the timer) like
        // any other fresh Start(); only an ACTIVE, mid-route RUN is ever checkpointed/resumable.
        recoveryStore.Delete();
        RecoveredJournal = null;
        RecoveredJournalIsCorrupt = false;
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
        activeRunId = null; activeSkippedDataCenters = []; activeCompletedDestinationsInCurrentWorld = []; activeCurrentWorldPlan = null; pendingResumedWorldSteps = null;
    }

    /// <summary>Resumes the one interrupted run described by <see cref="RecoveredJournal"/> — see that type's doc
    /// comment for the full reconstruction rationale. Fully validates the journal against its own frozen route
    /// snapshot (Data Center/World names actually exist within it) before committing any state, so a corrupt or
    /// internally-inconsistent journal is reported as such rather than leaving this service half-mutated.
    ///
    /// A World with completed destinations already recorded resumes by replaying the exact remaining tail of its
    /// already-computed plan (see <see cref="ProcessTransferCompletion"/>'s <c>pendingResumedWorldSteps</c> branch);
    /// every other case (no Data Center/World recorded yet, or a World with zero completions) simply continues
    /// forward through the normal <see cref="AdvanceToNextDataCenter"/>/<see cref="AdvanceToNextWorld"/> path exactly
    /// like a fresh Start() would, using the journal's frozen <see cref="ShoutRunnerRecoveryJournal.Route"/> instead
    /// of live Settings — see the crash-recovery brief's "ROUTE CONFIGURATION CHANGED AFTER CRASH".</summary>
    public ShoutRunnerResumeResult Resume()
    {
        if (!CanStart) return ShoutRunnerResumeResult.AlreadyRunning;
        if (RecoveredJournalIsCorrupt) return ShoutRunnerResumeResult.RecoveryCorrupt;
        if (RecoveredJournal is not { } journal) return ShoutRunnerResumeResult.NoRecoveryAvailable;
        // Crash-recovery brief "VENUE PROFILE SAFETY" — re-checked here regardless of what the UI already gated on,
        // so cross-venue contamination is never possible even if a caller's own check is stale or buggy.
        if (journal.VenueId != venueId) return ShoutRunnerResumeResult.DifferentVenue;

        var dataCenters = journal.Route.DataCentersInOrder;
        var dataCenterPosition = journal.CurrentDataCenter is null ? -1 : IndexOfOrdinal(dataCenters, journal.CurrentDataCenter);
        if (journal.CurrentDataCenter is not null && dataCenterPosition < 0) return ShoutRunnerResumeResult.RecoveryCorrupt;

        IReadOnlyList<string> worldsForCurrentDc = [];
        var worldPosition = -1;
        if (dataCenterPosition >= 0)
        {
            worldsForCurrentDc = ShoutRunnerCatalog.WorldsIn(dataCenters[dataCenterPosition]);
            worldPosition = journal.CurrentWorld is null ? -1 : IndexOfOrdinal(worldsForCurrentDc, journal.CurrentWorld);
            if (journal.CurrentWorld is not null && worldPosition < 0) return ShoutRunnerResumeResult.RecoveryCorrupt;
        }

        // Fully validated from here on — safe to commit.
        runConfig = journal.Route;
        RunNumber = journal.RunNumber;
        runStartedAt = journal.RunStartedAtUtc;
        fallbackDirection = journal.FallbackDirection;
        activeRunId = journal.RunId;
        activeSkippedDataCenters = [.. journal.SkippedDataCenters];
        activeCompletedDestinationsInCurrentWorld = [.. journal.CompletedDestinationsInCurrentWorld];
        activeCurrentWorldPlan = journal.CurrentWorldPlan is null ? null : [.. journal.CurrentWorldPlan];
        pendingResumedWorldSteps = null;

        automation.ResetForVenue();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        State = ShoutRunnerState.Starting;
        StatusText = "Starting…";

        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"Recovered interrupted RUN {RunNumber}."));
        if (activeCompletedDestinationsInCurrentWorld.Count > 0)
            terminal.Add(Event(ShoutRunnerEventSeverity.Success, $"Last successful destination: {activeCompletedDestinationsInCurrentWorld[^1]}."));
        foreach (var skip in activeSkippedDataCenters)
            terminal.Add(Event(ShoutRunnerEventSeverity.Warning, $"Skipped Data Centers (recovered): {skip.DataCenter} ({skip.Reason})."));

        if (dataCenterPosition < 0)
        {
            dataCenterIndex = -1;
            AdvanceToNextDataCenter();
            return ShoutRunnerResumeResult.Resumed;
        }

        dataCenterIndex = dataCenterPosition;
        currentWorlds = worldsForCurrentDc;
        currentDataCenterHadSkip = false;

        if (worldPosition < 0)
        {
            worldIndex = -1;
            AdvanceToNextWorld();
            return ShoutRunnerResumeResult.Resumed;
        }

        worldIndex = worldPosition;
        currentWorldHadFailure = false;
        var dc = dataCenters[dataCenterIndex];
        var world = currentWorlds[worldIndex];
        terminal.Add(Event(ShoutRunnerEventSeverity.InProgress, $"{world} — resuming.", dc, world));
        StatusText = $"RUN {RunNumber} — {dc} / {world}";

        if (activeCompletedDestinationsInCurrentWorld.Count > 0 && activeCurrentWorldPlan is { Count: > 0 })
        {
            pendingResumedWorldSteps = activeCurrentWorldPlan;
            pendingResumedStepIndex = activeCompletedDestinationsInCurrentWorld.Count;
        }

        BeginReadinessGate(ReadinessPurpose.EnterWorld);
        return ShoutRunnerResumeResult.Resumed;
    }

    /// <summary>Removes the interrupted-run checkpoint only — never touches saved route/settings (crash-recovery
    /// brief "DISCARD RECOVERY"). Safe to call whether the journal loaded cleanly or was corrupt.</summary>
    public void DiscardRecovery()
    {
        recoveryStore.Delete();
        RecoveredJournal = null;
        RecoveredJournalIsCorrupt = false;
    }

    private static int IndexOfOrdinal(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Persists the current in-progress run to <see cref="recoveryStore"/> — see
    /// <see cref="ShoutRunnerRecoveryJournal"/>'s doc comment for exactly what each field means and
    /// <see cref="ShoutRunnerService"/>'s call sites for exactly when this is called (RUN/World entry, a successful
    /// destination, and a Data Center skip decision — never on a bare timer or every frame). A failure to persist is
    /// reported to Diagnostics, not thrown — a missed checkpoint should never interrupt the live route itself.</summary>
    private void WriteCheckpoint()
    {
        if (runConfig is null || activeRunId is not { } runId) return;
        var dc = dataCenterIndex >= 0 && dataCenterIndex < runConfig.DataCentersInOrder.Count ? runConfig.DataCentersInOrder[dataCenterIndex] : null;
        var world = worldIndex >= 0 && worldIndex < currentWorlds.Count ? currentWorlds[worldIndex] : null;
        var journal = new ShoutRunnerRecoveryJournal(
            ShoutRunnerRecoveryJournal.CurrentSchemaVersion,
            venueId,
            profiles.Current.DisplayName,
            runId,
            RunNumber,
            runStartedAt,
            runConfig,
            dc,
            world,
            [.. activeCompletedDestinationsInCurrentWorld],
            activeCurrentWorldPlan is null ? null : [.. activeCurrentWorldPlan],
            fallbackDirection,
            [.. activeSkippedDataCenters],
            clock.UtcNow);

        try
        {
            recoveryStore.Save(journal);
            RecoveredJournal = journal;
            RecoveredJournalIsCorrupt = false;
        }
        catch (Exception ex)
        {
            diagnostics.RecordFailure($"ShoutRunner: failed to persist recovery checkpoint: {ex.Message}");
        }
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
