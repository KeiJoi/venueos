using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.DjShouts;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.DjShouts;

/// <summary>DJ Shouts' operator surface, split per NEW_MODULE_GUIDE.md §8/§9: <see cref="Draw"/> is live operation
/// only (the five DJ slots, the DJ Shout button, and the Last DJ Shout timer); <see cref="DrawSettings"/> is
/// persistent configuration only (slot assignments and the saved preset library, authored via
/// <see cref="DjShoutPresetEditorModal"/>) — mirrors <c>GreeterOperatorPanel</c>'s hotbar and
/// <c>GiveawaysOperatorPanel</c>'s compact-list-plus-modal split.</summary>
internal sealed class DjShoutsOperatorPanel(DjShoutsService service, VenueProfileService venues)
{
    private readonly ConfirmDialog confirmDialog = new();
    private readonly DjShoutPresetEditorModal presetEditorModal = new();
    private string presetSearch = "";

    // =============================================================================================================
    // Live operation (task §5/§6/§7/§8/§9/§25/§26/§27)
    // =============================================================================================================

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings;

        UiKit.BeginSectionCard("djshouts-slots", theme, "DJ Shout Presets");
        for (var slot = 1; slot <= DjShoutSlotAssignments.SlotCount; slot++)
        {
            var presetId = settings.SlotAssignments.Get(slot);
            var preset = service.ResolvePreset(presetId);
            var name = presetId is null ? "(Empty)" : preset?.Name ?? "(Missing)";
            if (slot > 1) ImGui.SameLine(0, 8);
            if (Hotbar.Slot(theme, $"djshout-slot-{slot}", $"DJ {slot}", name, slot == settings.SelectedSlot, new Vector2(150, 56)))
                service.SelectSlot(slot);
            if (presetId is null) UiKit.Tooltip("Assign a preset in Settings → DJ Shouts → DJ Slot Assignments.");
        }
        ImGui.Spacing();

        var canRun = service.CanRunDjShout;
        ImGui.BeginDisabled(!canRun);
        if (UiKit.PrimaryButton(theme, "DJ Shout", new Vector2(140, 0))) service.RunDjShout();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Last DJ Shout: {DescribeElapsed(service.TimeSinceLastShout)}");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        if (service.IsRunning)
        {
            UiKit.StatusBadge(theme, $"Sending: {service.RunningPreset?.Name}", ToastLevel.Information);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel", new Vector2(80, 0))) service.Cancel();
        }
        else if (!canRun)
        {
            var selected = service.SelectedPreset;
            if (selected is null) UiKit.WarningState(theme, "No preset is assigned to the selected DJ slot — assign one in Settings → Modules → DJ Shouts.");
            else if (!selected.HasExecutableLines) UiKit.WarningState(theme, $"\"{selected.Name}\" has no lines to send — edit it in Settings → Modules → DJ Shouts.");
        }
        UiKit.EndSectionCard();
    }

    /// <summary>"Never" / "MM:SS ago" / "Nh Mm ago" — a concise elapsed format matching the task's examples exactly
    /// (task §8). Recomputed fresh every call from <see cref="DjShoutsService.TimeSinceLastShout"/>, so it updates
    /// live every frame the panel is open with no separate ticking state.</summary>
    private static string DescribeElapsed(TimeSpan? elapsed)
    {
        if (elapsed is not { } span) return "Never";
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m ago"
            : $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2} ago";
    }

    // =============================================================================================================
    // Settings — venue-scoped slot assignments and the saved preset library (task §11/§12/§13/§28/§29/§31)
    // =============================================================================================================

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);
        presetEditorModal.Draw(theme, service);

        UiKit.InfoBanner(theme, "Venue-scoped configuration", "DJ Shout presets, DJ slot assignments, and the Last DJ Shout timer are all saved per Venue Profile — switching venues shows this venue's own DJ Shouts setup.", ToastLevel.Information);
        ImGui.Spacing();

        DrawSlotAssignments(theme);
        ImGui.Spacing();
        DrawPresetBrowser(theme);
    }

    private void DrawSlotAssignments(VenueTheme theme)
    {
        UiKit.BeginSectionCard("djshouts-slot-assign", theme, "DJ Slot Assignments");
        var library = service.Settings.Presets;
        var options = new List<string> { "(None)" };
        options.AddRange(library.Select(x => x.Name));
        for (var slot = 1; slot <= DjShoutSlotAssignments.SlotCount; slot++)
        {
            ImGui.PushID(slot);
            var currentId = service.Settings.SlotAssignments.Get(slot);
            var index = 0;
            if (currentId is { } cid) for (var i = 0; i < library.Count; i++) if (library[i].Id == cid) { index = i + 1; break; }
            if (Forms.ComboField(theme, $"DJ {slot}", options, ref index)) service.AssignSlot(slot, index == 0 ? null : library[index - 1].Id);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    private void DrawPresetBrowser(VenueTheme theme)
    {
        UiKit.BeginSectionCard("djshouts-presets", theme, $"Saved Presets ({service.Settings.Presets.Count})");

        // Reserve "New DJ Shout"'s width first — SearchBox defaults to filling all available width, which would
        // push the button past the visible content region if drawn with SameLine() afterward (the same class of
        // bug documented for Greeter's "New Preset"/VIP's "+ Add VIP" buttons in NativeOperationsPanels.cs).
        const float newButtonWidth = 130f;
        Forms.SearchBox(theme, "djshouts-preset-search", ref presetSearch, "Search DJ Shouts", ImGui.GetContentRegionAvail().X - newButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "New DJ Shout", new Vector2(newButtonWidth, 0))) presetEditorModal.OpenForNew();
        ImGui.Spacing();

        var filtered = service.Settings.Presets.Where(x => string.IsNullOrWhiteSpace(presetSearch) || x.Name.Contains(presetSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0) UiKit.EmptyState(theme, "No DJ Shouts yet", "Use \"New DJ Shout\" above to create one.");
        else foreach (var preset in filtered)
        {
            ImGui.PushID(preset.Id.ToString());
            var subtitle = $"{preset.NonEmptyLines.Count} line(s)" + (preset.Id == service.RunningPreset?.Id ? " · SENDING" : "");
            UiKit.ListRow(theme, preset.Name, subtitle, false);
            if (UiKit.GhostButton(theme, "Edit")) presetEditorModal.OpenForEdit(preset);
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Delete"))
                confirmDialog.Request("Delete DJ Shout?", $"\"{preset.Name}\" will be permanently deleted and cleared from any DJ slot it's assigned to. This cannot be undone.", () => service.DeletePreset(preset.Id));
            UiKit.Divider(theme);
            ImGui.PopID();
        }

        UiKit.EndSectionCard();
    }
}
