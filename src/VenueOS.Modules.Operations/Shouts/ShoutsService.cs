using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Shouts;

/// <summary>
/// Shouts (module ID unchanged from its original release, <c>communication.djshouts</c> — see the module ID note
/// below) — an operator-triggered announcement utility generalized from the original "DJ Shouts" (VenueOS 0.3.4) to
/// cover any reusable venue announcement: DJ introductions, hype, event notices, reminders, requests, closing
/// messages, or any other manually-fired message. An operator prepares reusable Shout presets ahead of time (in
/// Settings) and fires one manually during an event (in the live module) — one preset, one button press, in order.
///
/// State authority: Shouts has no backend and no shared/other-module state. Its per-venue persistent config
/// (<see cref="Settings"/> — the saved preset library, the 15 slot assignments, the selected slot, and the
/// last-successful-shout timestamp) is the ONLY durable state, saved through
/// <see cref="VenueProfileService.SaveModuleConfig{T}"/>. The one in-flight run (<see cref="IsRunning"/>/
/// <see cref="RunningPreset"/>) is ephemeral and always resets to idle on venue switch, disable, or plugin reload.
///
/// Execution model, pacing, and dispatch-confirmation gating are unchanged from the original DJ Shouts
/// implementation (this generalization pass touches naming, slot count, and live-visibility, not the execution
/// engine) — see <see cref="RunShout"/>/<see cref="OnLineDispatched"/>.
/// </summary>
public sealed class ShoutsService(SchedulerService scheduler, ChatCommandService chat, VenueProfileService profiles, IClock clock, DiagnosticsService diagnostics)
{
    /// <summary>Module ID deliberately left unchanged from the original DJ Shouts release rather than renamed to
    /// e.g. <c>communication.shouts</c>. NEW_MODULE_GUIDE.md §3/§4 establishes — and ShoutRunner (module ID
    /// <c>communication.announcements</c>, display name "ShoutRunner") already proves in production — that the
    /// module ID is a persistence key wholly independent of the display name; renaming a display name has never
    /// required (or benefited from) also renaming the ID. Renaming the ID here would require a brand-new,
    /// bespoke module-ID migration path (none exists anywhere in this codebase — VenueProfileService keys config
    /// strictly by the exact (venueId, moduleId, schemaVersion) tuple, §3), purely for cosmetic ID/name symmetry,
    /// against every existing 0.3.4 user's already-persisted presets/slots/timestamp for zero functional benefit.
    /// Keeping the ID avoids that entire risk surface; the schema-version-based migration below (five named slots →
    /// fifteen) is the only migration this generalization actually needs.</summary>
    public const string ModuleId = "communication.djshouts";

    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;

    /// <summary>The delay between successive dispatched lines — unchanged from the original DJ Shouts/Greeter
    /// pacing.</summary>
    public const int DelayBetweenLinesSeconds = 2;

    private Guid venueId;
    private CancellationTokenSource runCancellation = new();
    private Guid currentRunId = Guid.Empty;

    public ShoutsSettings Settings { get; private set; } = ShoutsSettings.Default();

    // ---- Ephemeral live-run state — never persisted ----
    public bool IsRunning { get; private set; }
    public ShoutPreset? RunningPreset { get; private set; }

    /// <summary>Venue switch: stop this venue's in-flight run FIRST, then load (and, if needed, migrate) the new
    /// venue's own settings — never the reverse order.</summary>
    public void Load(Guid nextVenueId)
    {
        CancelRun();
        IsRunning = false;
        RunningPreset = null;

        venueId = nextVenueId;
        Settings = LoadOrMigrate(venueId);
    }

    /// <summary>Reads the current-schema (v2, 15 slots) config if one has actually been saved. Otherwise, if a
    /// pre-generalization (v1, 5 slots) payload exists for this venue, migrates it in place — one atomic read of the
    /// old payload, an in-memory transform, one save under the new schema version — and returns the migrated result.
    /// Brand-new venues with neither payload simply get <see cref="ShoutsSettings.Default"/>.
    ///
    /// <b>Idempotency:</b> <see cref="VenueProfileService.HasModuleConfig"/> is checked for the NEW schema version
    /// first and short-circuits every subsequent load — once migration has run once for a venue, the v1 payload is
    /// never read or re-migrated again, so a preset can never be duplicated and a v1-only edit made after migration
    /// (which cannot happen, since nothing here ever writes v1) could never resurrect stale data. The v1 payload
    /// itself is deliberately left in place rather than deleted — VenueProfileService has no delete-single-payload
    /// primitive, and there is no need for one here: an inert, superseded payload sitting alongside the current one
    /// is harmless and matches the "never discard a stored payload" caution GetModuleConfig itself already
    /// follows (NEW_MODULE_GUIDE.md §13).</summary>
    private ShoutsSettings LoadOrMigrate(Guid id)
    {
        if (profiles.HasModuleConfig(id, ModuleId, SchemaVersion))
            return profiles.GetModuleConfig(id, ModuleId, SchemaVersion, ShoutsSettings.Default);

        if (profiles.HasModuleConfig(id, ModuleId, LegacySchemaVersion))
        {
            var legacy = profiles.GetModuleConfig(id, ModuleId, LegacySchemaVersion, LegacyShoutsSettingsV1.Default);
            var migrated = MigrateFromV1(legacy);
            profiles.SaveModuleConfig(id, ModuleId, SchemaVersion, migrated);
            return migrated;
        }

        return ShoutsSettings.Default();
    }

    /// <summary>Old slots 1-5 map straight across to new slots 1-5 (indices 0-4); new slots 6-15 default
    /// unassigned. Presets, the selected slot number, and the last-shout timestamp all carry over unchanged — none
    /// of their shapes changed between v1 and v2.</summary>
    internal static ShoutsSettings MigrateFromV1(LegacyShoutsSettingsV1 legacy)
    {
        var slots = new Guid?[ShoutSlotAssignments.SlotCount];
        slots[0] = legacy.SlotAssignments.Slot1;
        slots[1] = legacy.SlotAssignments.Slot2;
        slots[2] = legacy.SlotAssignments.Slot3;
        slots[3] = legacy.SlotAssignments.Slot4;
        slots[4] = legacy.SlotAssignments.Slot5;

        return new ShoutsSettings(legacy.Presets, new ShoutSlotAssignments(slots), legacy.SelectedSlot, legacy.LastShoutCompletedAtUtc);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Preset authoring (Settings-only) — every mutation saves immediately.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Persists a fully-authored draft as a brand-new preset in one atomic save — mints a fresh Id
    /// regardless of whatever Id the draft carries.</summary>
    public ShoutPreset SaveNewPreset(ShoutPreset draft)
    {
        var preset = draft with { Id = Guid.NewGuid() };
        Settings = Settings with { Presets = [.. Settings.Presets, preset] };
        Save();
        return preset;
    }

    public void UpdatePreset(Guid id, Func<ShoutPreset, ShoutPreset> update)
    {
        var index = IndexOf(id);
        if (index < 0) return;
        var updated = Settings.Presets.Select((p, i) => i == index ? update(p) : p).ToArray();
        Settings = Settings with { Presets = updated };
        Save();
    }

    /// <summary>Permanently removes a preset and clears it from any of the 15 Shout slots referencing it, in the
    /// SAME atomic save — a deleted preset's Id can never be left dangling in a slot assignment. If the deleted
    /// preset's slot was the currently selected one, selection falls back safely (see
    /// <see cref="ApplySelectionFallback"/>). A preset that happens to be the currently RUNNING preset can still be
    /// deleted safely: <see cref="RunningPreset"/> is a captured value-copy snapshot taken at <see cref="RunShout"/>
    /// time, so deleting the saved library entry never affects an already-in-flight run.</summary>
    public bool DeletePreset(Guid id)
    {
        if (IndexOf(id) < 0) return false;
        var remaining = Settings.Presets.Where(x => x.Id != id).ToArray();
        Settings = Settings with { Presets = remaining, SlotAssignments = Settings.SlotAssignments.ClearPreset(id) };
        ApplySelectionFallback();
        Save();
        return true;
    }

    private int IndexOf(Guid id) { for (var i = 0; i < Settings.Presets.Count; i++) if (Settings.Presets[i].Id == id) return i; return -1; }

    // ---------------------------------------------------------------------------------------------------------
    // Slots — 15 independently assignable slots, only configured ones ever visible in the live module.
    // ---------------------------------------------------------------------------------------------------------

    public void AssignSlot(int slot, Guid? presetId)
    {
        if (slot < 1 || slot > ShoutSlotAssignments.SlotCount) return;
        Settings = Settings with { SlotAssignments = Settings.SlotAssignments.With(slot, presetId) };
        ApplySelectionFallback();
        Save();
    }

    /// <summary>Selecting a slot never executes anything by itself — it only changes which preset the Shout button
    /// will run next. Only a slot that currently has a preset assigned (i.e. one the live module actually renders)
    /// may become selected — this is the service-level enforcement of "hidden slots cannot become selected."</summary>
    public void SelectSlot(int slot)
    {
        if (slot < 1 || slot > ShoutSlotAssignments.SlotCount || slot == Settings.SelectedSlot) return;
        if (Settings.SlotAssignments.Get(slot) is null) return;
        Settings = Settings with { SelectedSlot = slot };
        Save();
    }

    public ShoutPreset? ResolvePreset(Guid? presetId) => presetId is { } id ? Settings.Presets.FirstOrDefault(x => x.Id == id) : null;

    public ShoutPreset? SelectedPreset => ResolvePreset(Settings.SlotAssignments.Get(Settings.SelectedSlot));

    /// <summary>The ordered set of slots the live module should actually render — every slot 1-15 that currently has
    /// a resolvable preset assigned, in ascending slot-number order (never renumbered: a configuration of slots
    /// 1/4/7/12 stays "Shout 1", "Shout 4", "Shout 7", "Shout 12", not "1st, 2nd, 3rd, 4th").</summary>
    public IReadOnlyList<(int Slot, ShoutPreset Preset)> VisibleSlots =>
        Enumerable.Range(1, ShoutSlotAssignments.SlotCount)
            .Select(slot => (Slot: slot, Preset: ResolvePreset(Settings.SlotAssignments.Get(slot))))
            .Where(x => x.Preset is not null)
            .Select(x => (x.Slot, Preset: x.Preset!))
            .ToArray();

    /// <summary>If the currently selected slot is no longer visible (its assignment was cleared, or its preset was
    /// deleted), fall back to the first remaining visible slot; if none remain, the selection number is left as-is
    /// since it is inert either way (nothing is visible to be "selected" against, <see cref="SelectedPreset"/>
    /// already resolves to null, and <see cref="CanRunShout"/> is already false) — no hidden invalid selection is
    /// ever retained because "invalid" here means exactly "not present in <see cref="VisibleSlots"/>," which is also
    /// exactly what the live module renders. Mutates <see cref="Settings"/> in memory only; callers are responsible
    /// for the actual <see cref="Save"/>.</summary>
    private void ApplySelectionFallback()
    {
        var visible = VisibleSlots;
        if (visible.Any(v => v.Slot == Settings.SelectedSlot)) return;
        if (visible.Count == 0) return;
        Settings = Settings with { SelectedSlot = visible[0].Slot };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Live operation — the Shout button and the Last Shout timer.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The Shout button's single authoritative enable/disable gate: disabled with no preset assigned, no
    /// executable lines, or a run already in progress.</summary>
    public bool CanRunShout => !IsRunning && SelectedPreset is { } preset && preset.HasExecutableLines;

    /// <summary>Elapsed time since the last SUCCESSFULLY COMPLETED Shout, or null if none has ever completed
    /// (rendered as "Never"). Recomputed fresh from <see cref="Settings"/> on every call, so the live panel's timer
    /// updates every frame it's drawn with no separate ticking state to keep in sync.</summary>
    public TimeSpan? TimeSinceLastShout => Settings.LastShoutCompletedAtUtc is { } at ? (clock.UtcNow - at) : null;

    /// <summary>Fires the currently selected slot's preset. Sends every non-empty line in order, gated on the
    /// established pacing delay (see <see cref="DelayBetweenLinesSeconds"/>) between each; <see cref="ShoutsSettings.LastShoutCompletedAtUtc"/>
    /// only updates once the FINAL line is confirmed actually dispatched — never on button press, never on merely
    /// enqueuing a line, and never if execution is cancelled or a dispatch fails.</summary>
    public bool RunShout()
    {
        if (!CanRunShout) return false;

        CancelRun();
        var preset = SelectedPreset!;
        var runId = Guid.NewGuid();
        currentRunId = runId;
        RunningPreset = preset;
        IsRunning = true;

        SendLine(preset.NonEmptyLines, 0, runCancellation.Token, runId);
        return true;
    }

    private void SendLine(IReadOnlyList<ShoutLine> lines, int index, CancellationToken token, Guid runId)
    {
        if (token.IsCancellationRequested) return;
        var command = ShoutLineBytes.BuildCommand(lines[index]);
        chat.Enqueue(new ChatCommand(command, token, (success, error) => OnLineDispatched(lines, index, token, runId, success, error)));
    }

    /// <summary>The real acceptance gate for a Shout line: a stale callback for a run that has since been
    /// cancelled/superseded is ignored via the <paramref name="runId"/> guard. An intentional cancellation (an
    /// <see cref="OperationCanceledException"/> from <see cref="Cancel"/>/venue switch/disable) is never reported
    /// through <see cref="DiagnosticsService"/> — only a genuine transport failure is.</summary>
    private void OnLineDispatched(IReadOnlyList<ShoutLine> lines, int index, CancellationToken token, Guid runId, bool success, Exception? error)
    {
        if (currentRunId != runId) return; // cancelled or superseded — Cancel() already reset IsRunning/RunningPreset

        if (!success)
        {
            if (error is not OperationCanceledException)
                diagnostics.RecordFailure($"{ModuleId}: Shout line {index + 1} failed to send ({error?.Message ?? "no confirmation from the transport"}).");
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
    /// never touched — a cancelled run leaves the timer exactly as it was before the button was pressed.</summary>
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

/// <summary>The <see cref="IVenueModule"/> wrapper. Generalized from "DJ Shouts" to "Shouts" — display name and
/// description only; module ID, icon, and production/enabled-by-default status are unchanged from the accepted
/// 0.3.4 baseline (see <see cref="ShoutsService.ModuleId"/>'s doc comment for why the ID itself stayed put).</summary>
public sealed class ShoutsModule(ShoutsService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        ShoutsService.ModuleId,
        "Shouts",
        "Manually fire prepared announcement presets to Yell or Shout.",
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
