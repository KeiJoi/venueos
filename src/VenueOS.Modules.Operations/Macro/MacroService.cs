using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Macro;

/// <summary>Macro's per-venue persistence, library/hotbar mutation, and the single <see cref="Runner"/> every
/// invocation path shares (MACRO spec §42). State authority (NEW_MODULE_GUIDE.md §34a): <see cref="Settings"/> —
/// the saved macro library and the four hotbars' configuration — is the only durable state this module owns, saved
/// through <see cref="VenueProfileService.SaveModuleConfig{T}"/> like any other local module; <see cref="Runner"/>'s
/// stack/phase/status is deliberately ephemeral (NEW_MODULE_GUIDE.md §13a) and always resets on venue switch/disable
/// (see <see cref="Load"/>).</summary>
public sealed class MacroService
{
    public const string ModuleId = "tools.macro";
    private const int SchemaVersion = 1;

    private readonly VenueProfileService profiles;
    private readonly DiagnosticsService diagnostics;
    private Guid venueId;

    public MacroSettings Settings { get; private set; } = MacroSettings.Default();
    public MacroRunner Runner { get; }

    /// <summary>Fired by <see cref="DropMacroOntoSlot"/> on each actual hotbar slot assignment — a low-frequency,
    /// operator-meaningful event, routed to Dalamud's own log at the composition root (<c>Plugin.cs</c>), matching
    /// the established <c>VenueBingoService.DiagnosticEvent</c> pattern (a plain event, not
    /// <see cref="DiagnosticsService"/>, since a successful assignment is routine detail, not an operator-facing
    /// failure). Null-safe to leave unsubscribed (e.g. in tests).</summary>
    public event Action<string>? DiagnosticEvent;

    public MacroService(SchedulerService scheduler, ChatCommandService chat, VenueProfileService profiles, IActionReadyProbe probe, DiagnosticsService diagnostics)
    {
        this.profiles = profiles;
        this.diagnostics = diagnostics;
        Runner = new MacroRunner(scheduler, chat, probe);
        Runner.ProbeFaulted += diagnostics.RecordFailure;
    }

    /// <summary>Venue switch (NEW_MODULE_GUIDE.md §12a): stop any in-flight run FIRST, then load the new venue's own
    /// library/hotbars — never the reverse order, matching every other module's <c>Load</c>.</summary>
    public void Load(Guid nextVenueId)
    {
        Runner.Cancel();
        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, MacroSettings.Default);
    }

    private void Save() => profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, Settings);

    // =============================================================================================================
    // Macro library authoring (Settings-only per MACRO spec §4/§40) — MACRO LIVE QA FIX §7/§14-§19: authoring is
    // transactional. Nothing below mutates or saves persistent state until CreateMacro/SaveMacro is called with a
    // complete draft (name, icon, delay, and multiline body all at once) — there are deliberately no more
    // incremental per-keystroke mutation methods (the old AddLine/UpdateLine/RemoveLine/MoveLine/RenameMacro/
    // SetMacroIcon/SetMacroDelay/UpdateMacro surface is retired along with the one-line-per-field editor UI that
    // was its only caller). The editor window (VenueOS.Plugin.Macro.MacroEditorWindow) owns its OWN local draft
    // fields and calls exactly one of these two methods, exactly once, on Save.
    // =============================================================================================================

    public MacroValidationResult CreateMacro(string name, uint iconId, double delaySeconds, string bodyText, out SavedMacro? created)
    {
        created = null;
        var executableLines = MacroBodyText.ToExecutableLines(bodyText);
        var errors = CollectErrors(MacroNameValidator.Validate(name, null, Settings.Macros), executableLines);
        if (errors.Count > 0) return MacroValidationResult.Failed(errors);

        var macro = new SavedMacro(Guid.NewGuid(), name.Trim(), iconId, Math.Max(0, delaySeconds), executableLines);
        Settings = Settings with { Macros = [.. Settings.Macros, macro] };
        Save();
        created = macro;
        return MacroValidationResult.Ok();
    }

    /// <summary>Validates and, if valid, atomically replaces name/icon/delay/body for an existing macro — MACRO
    /// LIVE QA FIX §16/§17. If the name actually changed, cascades to every OTHER macro's exact parsed nested
    /// references to the OLD name (spec §16/§27) in the SAME save — never a blind string replace, and never applied
    /// if the name didn't change (so an unrelated body/icon/delay edit never re-scans every other macro for no
    /// reason). Renaming/editing the currently-RUNNING macro's saved record is allowed (the run itself is bound to
    /// a name snapshot captured at Start — <c>MacroRunner</c>'s doc comment — so this cannot corrupt it).</summary>
    public MacroValidationResult SaveMacro(Guid id, string name, uint iconId, double delaySeconds, string bodyText)
    {
        var index = IndexOf(id);
        if (index < 0) return MacroValidationResult.Failed(["Macro not found."]);

        var executableLines = MacroBodyText.ToExecutableLines(bodyText);
        var errors = CollectErrors(MacroNameValidator.Validate(name, id, Settings.Macros), executableLines);
        if (errors.Count > 0) return MacroValidationResult.Failed(errors);

        var trimmedName = name.Trim();
        var oldName = Settings.Macros[index].Name;
        var renamed = !string.Equals(oldName, trimmedName, StringComparison.Ordinal);
        var clampedDelay = Math.Max(0, delaySeconds);

        var updated = Settings.Macros.Select(m =>
        {
            if (m.Id == id) return m with { Name = trimmedName, IconId = iconId, DelayBetweenLinesSeconds = clampedDelay, Lines = executableLines };
            return renamed ? m with { Lines = MacroReferenceScanner.RewriteReferences(m.Lines, oldName, trimmedName) } : m;
        }).ToArray();
        Settings = Settings with { Macros = updated };
        Save();
        return MacroValidationResult.Ok();
    }

    private static IReadOnlyList<string> CollectErrors(string? nameError, IReadOnlyList<string> executableLines)
    {
        var lineErrors = MacroLineValidator.FindOversizedLines(executableLines);
        if (nameError is null) return lineErrors;
        return lineErrors.Count == 0 ? [nameError] : new[] { nameError }.Concat(lineErrors).ToArray();
    }

    public MacroLaunchResult DuplicateMacro(Guid id, out SavedMacro? created)
    {
        var source = Settings.Macros.FirstOrDefault(m => m.Id == id);
        if (source is null) { created = null; return MacroLaunchResult.Failed("Macro not found."); }
        var baseName = $"{source.Name} (Copy)";
        var name = baseName;
        var suffix = 2;
        while (MacroNameValidator.Validate(name, null, Settings.Macros) is not null) name = $"{baseName} {suffix++}";
        var copy = new SavedMacro(Guid.NewGuid(), name, source.IconId, source.DelayBetweenLinesSeconds, source.Lines);
        Settings = Settings with { Macros = [.. Settings.Macros, copy] };
        Save();
        created = copy;
        return MacroLaunchResult.Ok();
    }

    /// <summary>MACRO spec §16: never auto-deletes a referencing parent — callers must check
    /// <see cref="FindReferencingMacros"/> and get an explicit operator confirmation (a <c>ConfirmDialog</c>, per
    /// NEW_MODULE_GUIDE.md §37) before calling this. Deleting a macro that is currently running (or nested inside
    /// the current run) is safe even so — <see cref="Runner"/> is bound to a name snapshot captured at
    /// <see cref="MacroRunner.Start(IReadOnlyList{SavedMacro},Guid)"/> (spec §15), so deleting the saved record
    /// never mutates or corrupts an in-flight run.</summary>
    public IReadOnlyList<SavedMacro> FindReferencingMacros(Guid id)
    {
        var target = Settings.Macros.FirstOrDefault(m => m.Id == id);
        return target is null ? Array.Empty<SavedMacro>() : MacroReferenceScanner.FindReferencing(Settings.Macros, target.Name);
    }

    public void DeleteMacro(Guid id)
    {
        var remaining = Settings.Macros.Where(m => m.Id != id).ToArray();
        var clearedHotbars = Settings.Hotbars.Select(h => h with
        {
            SlotMacroIds = h.SlotMacroIds.Select(slot => slot == id ? null : slot).ToArray(),
        }).ToArray();
        Settings = Settings with { Macros = remaining, Hotbars = clearedHotbars };
        Save();
    }

    private int IndexOf(Guid id) { for (var i = 0; i < Settings.Macros.Count; i++) if (Settings.Macros[i].Id == id) return i; return -1; }

    // =============================================================================================================
    // Hotbar configuration (Settings-only per MACRO spec §41) — assignment/layout/position/scale/transparency all
    // persist immediately, mirroring the macro-library mutations above.
    // =============================================================================================================

    public void SetHotbarEnabled(int hotbarIndex, bool enabled) => UpdateHotbar(hotbarIndex, h => h with { Enabled = enabled });
    public void SetHotbarLayout(int hotbarIndex, MacroHotbarLayout layout) => UpdateHotbar(hotbarIndex, h => h with { Layout = layout });
    public void SetHotbarPosition(int hotbarIndex, MacroPosition position) => UpdateHotbar(hotbarIndex, h => h with { Position = position });
    public void SetHotbarScale(int hotbarIndex, float scale) => UpdateHotbar(hotbarIndex, h => h with { Scale = Math.Clamp(scale, MacroHotbar.MinScale, MacroHotbar.MaxScale) });
    public void SetHotbarTransparency(int hotbarIndex, float transparency) => UpdateHotbar(hotbarIndex, h => h with { Transparency = Math.Clamp(transparency, MacroHotbar.MinTransparency, MacroHotbar.MaxTransparency) });

    public void AssignSlot(int hotbarIndex, int slot, Guid? macroId) => UpdateHotbar(hotbarIndex, h =>
    {
        if (slot < 0 || slot >= MacroHotbarLayouts.SlotCount) return h;
        var slots = h.SlotMacroIds.ToArray();
        slots[slot] = macroId;
        return h with { SlotMacroIds = slots };
    });

    /// <summary>MACRO spec §28: moving one occupied slot onto another SWAPS the two assignments (the documented,
    /// predictable choice — never a silent overwrite of the destination).</summary>
    public void SwapSlots(int hotbarIndex, int slotA, int slotB) => UpdateHotbar(hotbarIndex, h =>
    {
        if (slotA < 0 || slotA >= MacroHotbarLayouts.SlotCount || slotB < 0 || slotB >= MacroHotbarLayouts.SlotCount || slotA == slotB) return h;
        var slots = h.SlotMacroIds.ToArray();
        (slots[slotA], slots[slotB]) = (slots[slotB], slots[slotA]);
        return h with { SlotMacroIds = slots };
    });

    /// <summary>MACRO LIVE QA FIX §22/§24: the single "drop a macro onto a slot" policy shared by BOTH the Settings
    /// hotbar editor's drag/drop AND the live faux-hotbar overlay's Edit-mode drag/drop — one entry point so the
    /// two surfaces can never diverge in behavior (spec's explicit "do not invent a separate live-hotbar assignment
    /// subsystem" instruction). If the dragged macro is already assigned to a DIFFERENT slot on the SAME hotbar,
    /// the two assignments are swapped (spec §28's documented, predictable choice); otherwise the target slot is
    /// simply (re)assigned.</summary>
    public void DropMacroOntoSlot(int hotbarIndex, int targetSlot, Guid macroId)
    {
        if (hotbarIndex < 0 || hotbarIndex >= Settings.Hotbars.Count)
        {
            DiagnosticEvent?.Invoke($"DropMacroOntoSlot: hotbarIndex {hotbarIndex} out of range — ignored, nothing persisted.");
            return;
        }
        var sourceSlot = Settings.Hotbars[hotbarIndex].SlotMacroIds.ToList().IndexOf(macroId);
        if (sourceSlot >= 0 && sourceSlot != targetSlot) SwapSlots(hotbarIndex, sourceSlot, targetSlot);
        else AssignSlot(hotbarIndex, targetSlot, macroId);
        DiagnosticEvent?.Invoke($"DropMacroOntoSlot: hotbar={hotbarIndex} targetSlot={targetSlot} macro={macroId} sourceSlot={(sourceSlot >= 0 ? sourceSlot : (int?)null)} -> persisted (SlotMacroIds[{targetSlot}]={Settings.Hotbars[hotbarIndex].SlotMacroIds[targetSlot]}).");
    }

    public void ClearSlot(int hotbarIndex, int slot) => AssignSlot(hotbarIndex, slot, null);

    public void ClearHotbar(int hotbarIndex) => UpdateHotbar(hotbarIndex, h =>
        h with { SlotMacroIds = Enumerable.Repeat((Guid?)null, MacroHotbarLayouts.SlotCount).ToArray() });

    private void UpdateHotbar(int hotbarIndex, Func<MacroHotbar, MacroHotbar> update)
    {
        if (hotbarIndex < 0 || hotbarIndex >= Settings.Hotbars.Count) return;
        var updated = Settings.Hotbars.Select((h, i) => i == hotbarIndex ? update(h) : h).ToArray();
        Settings = Settings with { Hotbars = updated };
        Save();
    }

    // =============================================================================================================
    // Execution entry points — ALL routes into MacroRunner go through these two methods (MACRO spec §42), so a tile
    // click, a hotbar slot click, and /venueos macro "Name" can never diverge in behavior.
    // =============================================================================================================

    public MacroLaunchResult Launch(Guid macroId) => Runner.Start(Settings.Macros, macroId);
    public MacroLaunchResult LaunchByName(string macroName) => Runner.Start(Settings.Macros, macroName);
}

/// <summary>The <see cref="IVenueModule"/> wrapper — own file/folder per NEW_MODULE_GUIDE.md §21/§33. Promoted to
/// production (NEW_MODULE_GUIDE.md §22a) after live acceptance testing. Known non-blocking issue: dragging a Macro
/// tile from the Live launcher directly onto a faux hotbar slot is not yet reliable in the live ImGui runtime —
/// see the User Manual's Known Issues section. Hotbar assignment via Settings → Modules → Macro → Hotbars works.</summary>
public sealed class MacroModule(MacroService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        MacroService.ModuleId,
        "Macro",
        "Create and run extended FFXIV command macros with nesting, action-aware waits, and custom macro hotbars.",
        "macro",
        DisplayOrder: 13);

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken)
    {
        service.Load(context.VenueId);
        return Task.CompletedTask;
    }

    /// <summary>The one place <c>/actionready</c> polling actually happens (MacroRunner.Tick's doc comment) — this
    /// is why Macro, unlike most current local modules, needs a real per-frame Tick body.</summary>
    public void Tick(DateTimeOffset now) => service.Runner.Tick(now);

    public void Draw() => draw?.Invoke();

    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();

    public ValueTask DisposeAsync()
    {
        service.Runner.Cancel();
        return ValueTask.CompletedTask;
    }
}
