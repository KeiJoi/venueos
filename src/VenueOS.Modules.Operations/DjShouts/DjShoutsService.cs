using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.DjShouts;

/// <summary>
/// DJ Shouts — a specialized sibling of Greeter (NEW_MODULE_GUIDE.md's own "closest existing reference" framing for
/// this module), reusing Greeter's five-slot hotbar CONCEPT and its established chat-dispatch/pacing conventions,
/// but with entirely independent per-venue configuration and runtime state — it never reads or writes Greeter's
/// <c>GreeterSettings</c>/<c>IVenueDatabase</c> preset table, and Greeter's own runtime (queue, Auto Greet, VIP
/// handoff) is completely untouched by this module.
///
/// State authority (NEW_MODULE_GUIDE.md §34a): DJ Shouts has no backend and no shared/other-module state. Its
/// per-venue persistent config (<see cref="Settings"/> — the saved preset library, the five slot assignments, the
/// selected slot, and the last-successful-shout timestamp) is the ONLY durable state, saved through
/// <see cref="VenueProfileService.SaveModuleConfig{T}"/> exactly like Giveaways/Macro/Block Letters. The one
/// in-flight run (<see cref="IsRunning"/>/<see cref="RunningPreset"/>) is ephemeral and always resets to idle on
/// venue switch, disable, or plugin reload — it is never persisted.
///
/// Execution model: mirrors <c>GiveawayService</c>'s SchedulerService+ChatCommandService sequencing (the closest
/// existing "send N lines in order with a safe pacing delay" reference in this codebase), but — per the task's
/// explicit dispatch-confirmation requirement — gates advancing to the next line, and updating
/// <see cref="LastShoutCompletedAtUtc"/>, on <see cref="ChatCommandService.ChatCommand.OnDispatched"/> actually
/// confirming success, the same way <c>GreeterService.OnStepDispatched</c> does — never merely on having called
/// <see cref="ChatCommandService.Enqueue"/>, which is not itself proof anything was sent.
/// </summary>
public sealed class DjShoutsService(SchedulerService scheduler, ChatCommandService chat, VenueProfileService profiles, IClock clock, DiagnosticsService diagnostics)
{
    public const string ModuleId = "communication.djshouts";
    private const int SchemaVersion = 1;

    /// <summary>The delay between successive dispatched lines — reused verbatim from Greeter's own established,
    /// live-verified line-to-line pacing (<c>GreeterService.OnStepDispatched</c>'s <c>NextAt = clock.UtcNow.AddSeconds(2)</c>),
    /// per the task's explicit instruction to reuse Greeter's safe pacing rather than invent a new configurable
    /// delay setting DJ Shouts doesn't need.</summary>
    public const int DelayBetweenLinesSeconds = 2;

    private Guid venueId;
    private CancellationTokenSource runCancellation = new();
    private Guid currentRunId = Guid.Empty;

    public DjShoutsSettings Settings { get; private set; } = DjShoutsSettings.Default();

    // ---- Ephemeral live-run state — never persisted (NEW_MODULE_GUIDE.md §13a) ----
    public bool IsRunning { get; private set; }
    public DjShoutPreset? RunningPreset { get; private set; }

    /// <summary>Venue switch (NEW_MODULE_GUIDE.md §12a): stop this venue's in-flight run FIRST, then load the new
    /// venue's own settings — never the reverse order.</summary>
    public void Load(Guid nextVenueId)
    {
        CancelRun();
        IsRunning = false;
        RunningPreset = null;

        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, DjShoutsSettings.Default);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Preset authoring (Settings-only, NEW_MODULE_GUIDE.md §9) — every mutation saves immediately (§13a).
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Persists a fully-authored draft as a brand-new preset in one atomic save — mints a fresh Id
    /// regardless of whatever Id the draft carries, matching <c>GiveawayService.SaveNewPreset</c>'s "New" path.</summary>
    public DjShoutPreset SaveNewPreset(DjShoutPreset draft)
    {
        var preset = draft with { Id = Guid.NewGuid() };
        Settings = Settings with { Presets = [.. Settings.Presets, preset] };
        Save();
        return preset;
    }

    public void UpdatePreset(Guid id, Func<DjShoutPreset, DjShoutPreset> update)
    {
        var index = IndexOf(id);
        if (index < 0) return;
        var updated = Settings.Presets.Select((p, i) => i == index ? update(p) : p).ToArray();
        Settings = Settings with { Presets = updated };
        Save();
    }

    /// <summary>Permanently removes a preset and clears it from any DJ slot it was assigned to, in the SAME
    /// atomic save (task §31/NEW_MODULE_GUIDE.md §37) — a deleted preset's Id can never be left dangling in
    /// <see cref="DjShoutsSettings.SlotAssignments"/>. A preset that happens to be the currently RUNNING preset can
    /// still be deleted safely: <see cref="RunningPreset"/> is a captured value-copy snapshot (see
    /// <see cref="RunDjShout"/>), so deleting the saved library entry never affects an already-in-flight run.</summary>
    public bool DeletePreset(Guid id)
    {
        if (IndexOf(id) < 0) return false;
        var remaining = Settings.Presets.Where(x => x.Id != id).ToArray();
        Settings = Settings with { Presets = remaining, SlotAssignments = Settings.SlotAssignments.ClearPreset(id) };
        Save();
        return true;
    }

    private int IndexOf(Guid id) { for (var i = 0; i < Settings.Presets.Count; i++) if (Settings.Presets[i].Id == id) return i; return -1; }

    // ---------------------------------------------------------------------------------------------------------
    // Slots (task §12/§26/§27/§28)
    // ---------------------------------------------------------------------------------------------------------

    public void AssignSlot(int slot, Guid? presetId)
    {
        if (slot < 1 || slot > DjShoutSlotAssignments.SlotCount) return;
        Settings = Settings with { SlotAssignments = Settings.SlotAssignments.With(slot, presetId) };
        Save();
    }

    /// <summary>Selecting a slot never executes anything by itself (task §26) — it only changes which preset the
    /// DJ Shout button will run next.</summary>
    public void SelectSlot(int slot)
    {
        if (slot < 1 || slot > DjShoutSlotAssignments.SlotCount || slot == Settings.SelectedSlot) return;
        Settings = Settings with { SelectedSlot = slot };
        Save();
    }

    public DjShoutPreset? ResolvePreset(Guid? presetId) => presetId is { } id ? Settings.Presets.FirstOrDefault(x => x.Id == id) : null;

    public DjShoutPreset? SelectedPreset => ResolvePreset(Settings.SlotAssignments.Get(Settings.SelectedSlot));

    // ---------------------------------------------------------------------------------------------------------
    // Live operation — the DJ Shout button and the Last DJ Shout timer (task §7/§8/§9/§21/§22/§25)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The DJ Shout button's single authoritative enable/disable gate (task §7): disabled with no
    /// preset assigned, no executable lines, or a run already in progress.</summary>
    public bool CanRunDjShout => !IsRunning && SelectedPreset is { } preset && preset.HasExecutableLines;

    /// <summary>Elapsed time since the last SUCCESSFULLY COMPLETED DJ Shout, or null if none has ever completed
    /// (rendered as "Never" — task §8/§9). Recomputed fresh from <see cref="Settings"/> on every call, so the live
    /// panel's timer updates every frame it's drawn with no separate ticking state to keep in sync.</summary>
    public TimeSpan? TimeSinceLastShout => Settings.LastShoutCompletedAtUtc is { } at ? (clock.UtcNow - at) : null;

    /// <summary>Fires the currently selected slot's preset. Sends every non-empty line in order, gated on the
    /// established Greeter pacing delay (see <see cref="DelayBetweenLinesSeconds"/>) between each; <see cref="LastShoutCompletedAtUtc"/>
    /// only updates once the FINAL line is confirmed actually dispatched (task §9) — never on button press, never on
    /// merely enqueuing a line, and never if execution is cancelled or a dispatch fails (task §21/§25/§38).</summary>
    public bool RunDjShout()
    {
        if (!CanRunDjShout) return false;

        CancelRun();
        var preset = SelectedPreset!;
        var runId = Guid.NewGuid();
        currentRunId = runId;
        RunningPreset = preset;
        IsRunning = true;

        SendLine(preset.NonEmptyLines, 0, runCancellation.Token, runId);
        return true;
    }

    private void SendLine(IReadOnlyList<DjShoutLine> lines, int index, CancellationToken token, Guid runId)
    {
        if (token.IsCancellationRequested) return;
        var command = DjShoutLineBytes.BuildCommand(lines[index]);
        chat.Enqueue(new ChatCommand(command, token, (success, error) => OnLineDispatched(lines, index, token, runId, success, error)));
    }

    /// <summary>The real acceptance gate for a DJ Shout line (mirrors <c>GreeterService.OnStepDispatched</c>): a
    /// stale callback for a run that has since been cancelled/superseded is ignored via the <paramref name="runId"/>
    /// guard (NEW_MODULE_GUIDE.md §24a). An intentional cancellation (an <see cref="OperationCanceledException"/>
    /// from <see cref="Cancel"/>/venue switch/disable) is never reported through <see cref="DiagnosticsService"/> —
    /// only a genuine transport failure is.</summary>
    private void OnLineDispatched(IReadOnlyList<DjShoutLine> lines, int index, CancellationToken token, Guid runId, bool success, Exception? error)
    {
        if (currentRunId != runId) return; // cancelled or superseded — Cancel() already reset IsRunning/RunningPreset

        if (!success)
        {
            if (error is not OperationCanceledException)
                diagnostics.RecordFailure($"{ModuleId}: DJ Shout line {index + 1} failed to send ({error?.Message ?? "no confirmation from the transport"}).");
            FinishRun(runId, completedSuccessfully: false);
            return;
        }

        var next = index + 1;
        if (next >= lines.Count) { FinishRun(runId, completedSuccessfully: true); return; }
        scheduler.Schedule(TimeSpan.FromSeconds(DelayBetweenLinesSeconds), () => SendLine(lines, next, token, runId), cancellationToken: token);
    }

    private void FinishRun(Guid runId, bool completedSuccessfully)
    {
        if (currentRunId != runId) return;
        IsRunning = false;
        RunningPreset = null;
        currentRunId = Guid.Empty;

        if (completedSuccessfully)
        {
            Settings = Settings with { LastShoutCompletedAtUtc = clock.UtcNow };
            Save();
        }
    }

    /// <summary>Stops a run immediately: no further lines are sent, and the last-successful-shout timestamp is
    /// never touched (task §25/§38) — a cancelled run leaves the timer exactly as it was before the button was
    /// pressed.</summary>
    public void Cancel()
    {
        if (!IsRunning) return;
        CancelRun();
        IsRunning = false;
        RunningPreset = null;
    }

    private void CancelRun()
    {
        runCancellation.Cancel();
        runCancellation.Dispose();
        runCancellation = new CancellationTokenSource();
        currentRunId = Guid.Empty;
    }

    private void Save() => profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, Settings);
}

/// <summary>The <see cref="IVenueModule"/> wrapper — own file/folder per NEW_MODULE_GUIDE.md §21/§33. Promoted to
/// production (NEW_MODULE_GUIDE.md §22a) in VenueOS 0.3.4 after live acceptance testing in Dalamud — see
/// docs/DJ_SHOUTS_IMPLEMENTATION.md's Live QA section.</summary>
public sealed class DjShoutsModule(DjShoutsService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        DjShoutsService.ModuleId,
        "DJ Shouts",
        "Manually fire prepared DJ announcement presets to Yell or Shout.",
        "microphone",
        DisplayOrder: 14);

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
