using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Shouts;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shouts;

/// <summary>Shouts' operator surface: <see cref="Draw"/> is live operation only (only the CONFIGURED slots — never
/// all 15 — the Shout button, and the Last Shout timer); <see cref="DrawSettings"/> is persistent configuration
/// only (all 15 slot assignments and the saved preset library, authored via <see cref="ShoutPresetEditorModal"/>).
/// Live-visibility filtering/order and empty-state handling are read directly from
/// <see cref="ShoutsService.VisibleSlots"/> — the panel never decides which slots are visible itself.</summary>
internal sealed class ShoutsOperatorPanel(ShoutsService service, VenueProfileService venues)
{
    private readonly ConfirmDialog confirmDialog = new();
    private readonly ShoutPresetEditorModal presetEditorModal = new();
    private string presetSearch = "";

    // =============================================================================================================
    // Live operation
    // =============================================================================================================

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var visible = service.VisibleSlots;

        UiKit.BeginSectionCard("shouts-slots", theme, "Shout Presets");
        if (visible.Count == 0)
        {
            UiKit.EmptyState(theme, "No Shout presets are assigned.", "Configure Shout Slot Assignments in Settings.");
        }
        else
        {
            for (var i = 0; i < visible.Count; i++)
            {
                var (slot, preset) = visible[i];
                if (i > 0) ImGui.SameLine(0, 8);
                if (Hotbar.Slot(theme, $"shout-slot-{slot}", $"Shout {slot}", preset.Name, slot == service.Settings.SelectedSlot, new Vector2(150, 56)))
                    service.SelectSlot(slot);
            }
        }
        ImGui.Spacing();

        var canRun = service.CanRunShout;
        ImGui.BeginDisabled(!canRun);
        if (UiKit.PrimaryButton(theme, "Shout", new Vector2(140, 0))) service.RunShout();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Last Shout: {DescribeElapsed(service.TimeSinceLastShout)}");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        if (service.IsRunning)
        {
            UiKit.StatusBadge(theme, $"Sending: {service.RunningPreset?.Name}", ToastLevel.Information);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel", new Vector2(80, 0))) service.Cancel();
        }
        else if (!canRun && visible.Count > 0)
        {
            var selected = service.SelectedPreset;
            if (selected is null) UiKit.WarningState(theme, "No preset is assigned to the selected Shout slot — assign one in Settings → Modules → Shouts.");
            else if (!selected.HasExecutableLines) UiKit.WarningState(theme, $"\"{selected.Name}\" has no lines to send — edit it in Settings → Modules → Shouts.");
        }
        UiKit.EndSectionCard();
    }

    /// <summary>"Never" / "MM:SS ago" / "Nh Mm ago" — a concise elapsed format. Recomputed fresh every call from
    /// <see cref="ShoutsService.TimeSinceLastShout"/>, so it updates live every frame the panel is open with no
    /// separate ticking state.</summary>
    private static string DescribeElapsed(TimeSpan? elapsed)
    {
        if (elapsed is not { } span) return "Never";
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m ago"
            : $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2} ago";
    }

    // =============================================================================================================
    // Settings — venue-scoped slot assignments (all 15, always shown) and the saved preset library.
    // =============================================================================================================

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);
        presetEditorModal.Draw(theme, service);

        UiKit.InfoBanner(theme, "Venue-scoped configuration", "Shout presets, Shout slot assignments, and the Last Shout timer are all saved per Venue Profile — switching venues shows this venue's own Shouts setup.", ToastLevel.Information);
        ImGui.Spacing();

        DrawSlotAssignments(theme);
        ImGui.Spacing();
        DrawPresetBrowser(theme);
    }

    private void DrawSlotAssignments(VenueTheme theme)
    {
        UiKit.BeginSectionCard("shouts-slot-assign", theme, "Shout Slot Assignments");
        var library = service.Settings.Presets;
        var options = new List<string> { "(None)" };
        options.AddRange(library.Select(x => x.Name));
        for (var slot = 1; slot <= ShoutSlotAssignments.SlotCount; slot++)
        {
            ImGui.PushID(slot);
            var currentId = service.Settings.SlotAssignments.Get(slot);
            var index = 0;
            if (currentId is { } cid) for (var i = 0; i < library.Count; i++) if (library[i].Id == cid) { index = i + 1; break; }
            if (Forms.ComboField(theme, $"Shout {slot}", options, ref index)) service.AssignSlot(slot, index == 0 ? null : library[index - 1].Id);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    private void DrawPresetBrowser(VenueTheme theme)
    {
        UiKit.BeginSectionCard("shouts-presets", theme, $"Saved Presets ({service.Settings.Presets.Count})");

        // Reserve "New Shout"'s width first — SearchBox defaults to filling all available width, which would push
        // the button past the visible content region if drawn with SameLine() afterward.
        const float newButtonWidth = 110f;
        Forms.SearchBox(theme, "shouts-preset-search", ref presetSearch, "Search Shouts", ImGui.GetContentRegionAvail().X - newButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "New Shout", new Vector2(newButtonWidth, 0))) presetEditorModal.OpenForNew();
        ImGui.Spacing();

        var filtered = service.Settings.Presets.Where(x => string.IsNullOrWhiteSpace(presetSearch) || x.Name.Contains(presetSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0) UiKit.EmptyState(theme, "No Shouts yet", "Use \"New Shout\" above to create one.");
        else foreach (var preset in filtered)
        {
            ImGui.PushID(preset.Id.ToString());
            var subtitle = $"{preset.NonEmptyLines.Count} line(s)" + (preset.Id == service.RunningPreset?.Id ? " · SENDING" : "");
            UiKit.ListRow(theme, preset.Name, subtitle, false);
            if (UiKit.GhostButton(theme, "Edit")) presetEditorModal.OpenForEdit(preset);
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Delete"))
                confirmDialog.Request("Delete Shout?", $"\"{preset.Name}\" will be permanently deleted and cleared from any Shout slot it's assigned to. This cannot be undone.", () => service.DeletePreset(preset.Id));
            UiKit.Divider(theme);
            ImGui.PopID();
        }

        UiKit.EndSectionCard();
    }
}
