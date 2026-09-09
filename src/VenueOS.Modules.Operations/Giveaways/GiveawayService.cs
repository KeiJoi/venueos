using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Giveaways;

public readonly record struct GiveawayStartResult(bool Success, string? Error)
{
    public static GiveawayStartResult Ok() => new(true, null);
    public static GiveawayStartResult Failed(string error) => new(false, error);
}

/// <summary>
/// State authority (NEW_MODULE_GUIDE.md §34a): Giveaways has no backend and no shared/other-module state. Its
/// per-venue persistent config (<see cref="Settings"/> — the saved preset collection and which one is selected) is
/// the only durable state, saved through <see cref="VenueProfileService.SaveModuleConfig{T}"/> like any other local
/// module. Everything else on this class (<see cref="RunningPreset"/>, <see cref="Phase"/>, the roll board, the
/// countdown) is deliberately ephemeral live-operation state that is never persisted (NEW_MODULE_GUIDE.md §13a) —
/// it always resets to Idle on venue switch, disable, or plugin reload.
///
/// Timeline model (GIVEAWAYS spec §9/§10, revised per the post-implementation QA correction below): the giveaway
/// timer starts the instant the Start block's last non-empty line is enqueued; Midpoint begins at half the
/// configured duration; Closing BEGINS at the full duration. Roll acceptance opens exactly when the timer starts
/// and stays open THROUGH Midpoint and THROUGH Closing — it closes only once Closing's own final non-empty line is
/// enqueued (or immediately at the full duration if the Closing block is empty). This is a deliberate reversal of
/// the module's original behavior (which closed roll acceptance at the raw duration expiry, before Closing ever
/// sent): a Closing block routinely tells participants something like "last chance to roll!", so VenueOS must not
/// stop accepting rolls while its own announcements are still claiming the giveaway is open. Blocks are never
/// allowed to interleave (spec §10) — Closing's own start is still deferred (never skipped, never overlapped) until
/// an in-flight Midpoint block finishes sending, via <see cref="midpointSending"/>/<see cref="closingPending"/>
/// below; this rule is unchanged by the roll-window correction.
///
/// Diagnostics (NEW_MODULE_GUIDE.md §25): this module has no backend, no network, and no unsafe game-state
/// mutation — its only failure mode is an operator starting a run with an invalid preset, which is reported inline
/// via <see cref="Start"/>'s own <see cref="GiveawayStartResult"/> (shown as an operational ErrorState), not routed
/// through <c>DiagnosticsService</c>. This mirrors the documented "genuinely has no failure mode to report"
/// exception the guide's own Guest Notes example calls out explicitly, rather than wiring an unused dependency.
/// </summary>
public sealed class GiveawayService(SchedulerService scheduler, ChatCommandService chat, VenueProfileService profiles, IClock clock)
{
    public const string ModuleId = "events.giveaways";
    private const int SchemaVersion = 1;

    private Guid venueId;
    private CancellationTokenSource runCancellation = new();
    private bool midpointSending;
    private bool closingPending;
    // True from the instant the configured Giveaway Duration elapses, regardless of whether Closing has actually
    // started yet (it may still be waiting on an in-flight Midpoint block — see closingPending). This is distinct
    // from RollsClosed: the duration timer itself is over (so TimeRemaining stops counting down and BeginMidpoint's
    // same-tick-ordering guard uses it), but roll acceptance is NOT gated on this — only on RollsClosed, which now
    // flips true only when Closing's own final non-empty line is enqueued (or immediately, if Closing is empty).
    private bool durationElapsed;
    // A fresh id per run, captured by every phase closure below and compared by reference-free equality — NOT
    // GiveawayPreset comparison, since GiveawayPreset is a record and two DIFFERENT runs on the same unedited
    // preset would otherwise compare structurally equal, defeating the whole point of a stale-run guard
    // (NEW_MODULE_GUIDE.md §24a). The cancellation token already prevents a cancelled run's SCHEDULED callbacks
    // from firing at all (SchedulerService.Tick checks the token before invoking), but this id is the actual
    // "is this callback still for the current run" check used inside those callbacks once they do fire.
    private Guid currentRunId = Guid.Empty;

    public GiveawaySettings Settings { get; private set; } = GiveawaySettings.Default();

    public GiveawayPreset? SelectedPreset => Settings.Presets.FirstOrDefault(x => x.Id == Settings.ActivePresetId);

    // ---- Ephemeral live-run state — never persisted (see class doc comment) ----
    public GiveawayPreset? RunningPreset { get; private set; }
    public GiveawayPhase Phase { get; private set; } = GiveawayPhase.Idle;
    public GiveawayRollBoard? RollBoard { get; private set; }
    public DateTimeOffset? RollWindowClosesAtUtc { get; private set; }
    public bool RollsClosed { get; private set; }

    /// <summary>The single authoritative "is a roll from chat eligible right now" gate — checked by both
    /// <see cref="HandleRollObservation"/> itself and the chat adapter's cheap pre-filter. Rolls stay eligible all
    /// the way through Closing now (QA correction) — <see cref="RollsClosed"/> is what actually flips this off,
    /// exactly when Closing's final non-empty line is enqueued (or immediately, for an empty Closing block).
    /// </summary>
    public bool IsAcceptingRolls => !RollsClosed && RollBoard is not null && Phase is GiveawayPhase.AcceptingRolls or GiveawayPhase.Midpoint or GiveawayPhase.Closing;

    /// <summary>Counts down to the configured Giveaway Duration elapsing (when Closing begins) — NOT to when roll
    /// acceptance actually closes, which now depends on the Closing block's own content/length. Returns null once
    /// the duration has elapsed, so the UI never keeps showing a countdown for a fixed timer that's already over
    /// while Closing announcements (and roll acceptance) are still in progress.</summary>
    public TimeSpan? TimeRemaining
    {
        get
        {
            if (RollWindowClosesAtUtc is not { } deadline) return null;
            var remaining = deadline - clock.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Venue switch (NEW_MODULE_GUIDE.md §12a): stop this venue's in-flight run FIRST, then load the new
    /// venue's own preset collection — never the reverse order, matching every other module's <c>Load</c>.</summary>
    public void Load(Guid nextVenueId)
    {
        CancelRun();
        RunningPreset = null;
        RollBoard = null;
        Phase = GiveawayPhase.Idle;
        RollsClosed = false;
        RollWindowClosesAtUtc = null;

        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, GiveawaySettings.Default);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Preset authoring (Settings-only per GIVEAWAYS spec §4/§29) — every mutation saves immediately (NEW_MODULE_
    // GUIDE.md §13a); none of this may be called while that preset is the RunningPreset of an active run without
    // the running snapshot changing underneath the operator (spec §11) — enforced by CanEditPreset/CanDeletePreset.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A running giveaway is bound to the preset SNAPSHOT captured at Start, not the live saved record
    /// (GIVEAWAYS spec §11), so editing a preset that happens to be running is actually safe — it just wouldn't
    /// retroactively affect the in-flight run. Deletion is still blocked while running (spec §31) since that would
    /// orphan the active run's identity entirely.</summary>
    public bool CanDeletePreset(Guid presetId) => RunningPreset?.Id != presetId;

    public GiveawayPreset CreatePreset(string name)
    {
        var preset = GiveawayPreset.CreateNew(string.IsNullOrWhiteSpace(name) ? "New Giveaway" : name.Trim());
        Settings = Settings with { Presets = [.. Settings.Presets, preset] };
        Save();
        return preset;
    }

    /// <summary>Persists a fully-authored draft as a brand-new preset in ONE atomic save — the preset editor
    /// modal's Save action for "+ New Preset" uses this instead of <see cref="CreatePreset"/> (GIVEAWAYS live-QA
    /// fix #2, §1: the modal must never first persist an empty placeholder preset and then edit it in a second
    /// step — that would mean two separate <c>SaveModuleConfig</c> calls instead of one, and a real, if narrow,
    /// window where a half-authored preset could end up durably saved). Mints a fresh <c>Id</c> regardless of
    /// whatever Id the draft happens to carry, so a draft copied from an existing preset (which this is never
    /// called with — see <see cref="UpdatePreset"/> for editing) can never accidentally collide with it.</summary>
    public GiveawayPreset SaveNewPreset(GiveawayPreset draft)
    {
        var preset = draft with { Id = Guid.NewGuid() };
        Settings = Settings with { Presets = [.. Settings.Presets, preset] };
        Save();
        return preset;
    }

    public void RenamePreset(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        UpdatePreset(id, p => p with { Name = name.Trim() });
    }

    public bool DeletePreset(Guid id)
    {
        if (!CanDeletePreset(id)) return false;
        var remaining = Settings.Presets.Where(x => x.Id != id).ToArray();
        var activeId = Settings.ActivePresetId == id ? null : Settings.ActivePresetId;
        Settings = Settings with { Presets = remaining, ActivePresetId = activeId };
        Save();
        return true;
    }

    public void SelectPreset(Guid? id)
    {
        if (Settings.ActivePresetId == id) return;
        Settings = Settings with { ActivePresetId = id };
        Save();
    }

    public void UpdatePreset(Guid id, Func<GiveawayPreset, GiveawayPreset> update)
    {
        var index = IndexOf(id);
        if (index < 0) return;
        var updated = Settings.Presets.Select((p, i) => i == index ? update(p) : p).ToArray();
        Settings = Settings with { Presets = updated };
        Save();
    }

    private int IndexOf(Guid id) { for (var i = 0; i < Settings.Presets.Count; i++) if (Settings.Presets[i].Id == id) return i; return -1; }

    private void Save() => profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, Settings);

    // ---------------------------------------------------------------------------------------------------------
    // Live operation (GIVEAWAYS spec §11/§12/§19/§37)
    // ---------------------------------------------------------------------------------------------------------

    public GiveawayStartResult Start()
    {
        var preset = SelectedPreset;
        if (preset is null) return GiveawayStartResult.Failed("Select a preset first.");
        var errors = GiveawayPresetValidator.Validate(preset);
        if (errors.Count > 0) return GiveawayStartResult.Failed(string.Join(" ", errors));

        CancelRun();
        var runId = Guid.NewGuid();
        currentRunId = runId;
        RunningPreset = preset; // captured snapshot — spec §11: later edits to the saved preset never affect this run
        RollBoard = new GiveawayRollBoard(preset.WinnerMode, preset.ClosestTargetNumber, preset.AllowedRollsPerPerson, preset.SpecialNumbers);
        RollsClosed = false;
        RollWindowClosesAtUtc = null;
        Phase = GiveawayPhase.Starting;

        RunBlockSequence(preset.StartBlock, preset.Channel, preset.DelayBetweenLinesSeconds, runCancellation.Token, () => BeginAcceptingRolls(preset, runId));
        return GiveawayStartResult.Ok();
    }

    private void BeginAcceptingRolls(GiveawayPreset preset, Guid runId)
    {
        if (currentRunId != runId) return; // cancelled/superseded mid Start-block send
        Phase = GiveawayPhase.AcceptingRolls;
        var duration = TimeSpan.FromSeconds(Math.Max(1, preset.GiveawayDurationSeconds));
        RollWindowClosesAtUtc = clock.UtcNow + duration;
        var token = runCancellation.Token;
        scheduler.Schedule(TimeSpan.FromSeconds(duration.TotalSeconds / 2.0), () => BeginMidpoint(preset, runId), cancellationToken: token);
        scheduler.Schedule(duration, () => BeginClosingWindow(preset, runId), cancellationToken: token);
    }

    private void BeginMidpoint(GiveawayPreset preset, Guid runId)
    {
        if (currentRunId != runId || durationElapsed) return; // defensive same-tick-ordering guard; duration/2 < duration always in practice
        Phase = GiveawayPhase.Midpoint;
        midpointSending = true;
        RunBlockSequence(preset.MidpointBlock, preset.Channel, preset.DelayBetweenLinesSeconds, runCancellation.Token, () =>
        {
            midpointSending = false;
            if (currentRunId != runId) return;
            if (!durationElapsed) Phase = GiveawayPhase.AcceptingRolls; // still counting down to the full duration
            if (closingPending) { closingPending = false; StartClosing(preset, runId); }
        });
    }

    /// <summary>Fires at the configured Giveaway Duration — this begins the CLOSING phase, it no longer closes roll
    /// acceptance by itself (GIVEAWAYS QA correction: roll acceptance now stays open through the entire Closing
    /// sequence — see <see cref="StartClosing"/>'s completion callback for where it actually closes). The fixed-
    /// duration countdown itself is over the instant this fires, so <see cref="RollWindowClosesAtUtc"/> is cleared
    /// here regardless of which branch below runs, so <see cref="TimeRemaining"/> never keeps counting down past
    /// this point even while Closing (and roll acceptance) continue. If Midpoint is still actively sending, Closing
    /// is deferred (never interleaved, never skipped) until Midpoint's own sequence finishes, per spec §10.</summary>
    private void BeginClosingWindow(GiveawayPreset preset, Guid runId)
    {
        if (currentRunId != runId) return;
        durationElapsed = true;
        RollWindowClosesAtUtc = null;
        if (midpointSending) { closingPending = true; return; }
        StartClosing(preset, runId);
    }

    private void StartClosing(GiveawayPreset preset, Guid runId)
    {
        if (currentRunId != runId) return;
        Phase = GiveawayPhase.Closing;
        RunBlockSequence(preset.ClosingBlock, preset.Channel, preset.DelayBetweenLinesSeconds, runCancellation.Token, () =>
        {
            if (currentRunId != runId) return;
            // Roll acceptance closes HERE — exactly when Closing's final non-empty line is enqueued, or
            // immediately if the Closing block has no lines at all (RunBlockSequence calls this synchronously in
            // that case). Never at the raw duration expiry (GIVEAWAYS QA correction).
            RollsClosed = true;
            if (Phase == GiveawayPhase.Closing) Phase = GiveawayPhase.Complete;
        });
    }

    private void RunBlockSequence(GiveawayAnnouncementBlock block, GiveawayChatChannel channel, int delaySeconds, CancellationToken token, Action onComplete)
    {
        var lines = block.NonEmptyLines;
        if (lines.Count == 0) { onComplete(); return; } // spec §9: a block with no lines is skipped cleanly
        SendLine(lines, 0, channel, delaySeconds, token, onComplete);
    }

    private void SendLine(IReadOnlyList<string> lines, int index, GiveawayChatChannel channel, int delaySeconds, CancellationToken token, Action onComplete)
    {
        if (token.IsCancellationRequested) return;
        var prefix = channel == GiveawayChatChannel.Yell ? "/yell " : "/shout ";
        chat.Enqueue(new ChatCommand(prefix + lines[index], token));
        var next = index + 1;
        if (next >= lines.Count) { onComplete(); return; }
        scheduler.Schedule(TimeSpan.FromSeconds(Math.Max(1, delaySeconds)), () => SendLine(lines, next, channel, delaySeconds, token, onComplete), cancellationToken: token);
    }

    /// <summary>Stops every scheduled announcement phase, the countdown, and roll acceptance immediately (GIVEAWAYS
    /// spec §12). Deliberately preserves <see cref="RunningPreset"/> and <see cref="RollBoard"/> (the captured
    /// results) until the next <see cref="Start"/> or an explicit <see cref="ClearResults"/> — Cancel must never
    /// silently throw away a useful roll record (spec §12).</summary>
    public void Cancel()
    {
        if (Phase is GiveawayPhase.Idle or GiveawayPhase.Complete or GiveawayPhase.Cancelled) return;
        CancelRun();
        Phase = GiveawayPhase.Cancelled;
        RollsClosed = true;
        RollWindowClosesAtUtc = null;
    }

    /// <summary>GIVEAWAYS spec §32: clears the visible tracker between events. Never required by Start itself
    /// (Start always resets state on its own), but useful for tidying the screen after a completed/cancelled run.
    /// </summary>
    public void ClearResults()
    {
        RunningPreset = null;
        RollBoard = null;
        Phase = GiveawayPhase.Idle;
        RollsClosed = false;
        RollWindowClosesAtUtc = null;
    }

    private void CancelRun()
    {
        runCancellation.Cancel();
        runCancellation.Dispose();
        runCancellation = new CancellationTokenSource();
        midpointSending = false;
        closingPending = false;
        durationElapsed = false;
        currentRunId = Guid.Empty; // no real run ever uses this id (Start always mints a fresh Guid.NewGuid())
    }

    // ---------------------------------------------------------------------------------------------------------
    // Roll capture (GIVEAWAYS spec §13–§27)
    // ---------------------------------------------------------------------------------------------------------

    public GiveawayRollOutcome HandleRollObservation(ObservedRandomRoll observation)
    {
        if (!IsAcceptingRolls) return GiveawayRollOutcome.Reject(GiveawayRollRejectReason.GiveawayNotAcceptingRolls);
        return RollBoard!.Accept(observation.Player, observation.Value);
    }

    public IReadOnlyList<GiveawayLeaderboardRow> Leaderboard => RollBoard?.GetLeaderboard() ?? Array.Empty<GiveawayLeaderboardRow>();

    public int TotalRolls => RollBoard?.TotalRolls ?? 0;
}

/// <summary>The <see cref="IVenueModule"/> wrapper — own file/folder per NEW_MODULE_GUIDE.md §21/§33. Promoted to
/// production (NEW_MODULE_GUIDE.md §22a) after live acceptance testing.</summary>
public sealed class GiveawaysModule(GiveawayService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        GiveawayService.ModuleId,
        "Giveaways",
        "Run timed venue giveaways and track FFXIV random rolls.",
        "gift",
        DisplayOrder: 12);

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken)
    {
        service.Load(context.VenueId);
        return Task.CompletedTask;
    }

    public void Tick(DateTimeOffset now)
    {
    }

    public void Draw() => draw?.Invoke();

    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();

    public ValueTask DisposeAsync()
    {
        service.Cancel();
        return ValueTask.CompletedTask;
    }
}
