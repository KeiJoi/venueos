using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Giveaways;

/// <summary>Giveaways' operator surface, split per NEW_MODULE_GUIDE.md §8/§9: <see cref="DrawSettings"/> is the ONLY
/// place presets are authored (GIVEAWAYS spec §4/§29 — name, channel, timing, blocks, winner rules); <see cref="Draw"/>
/// is deliberately simple live operation (active preset, Start/Cancel, phase, roll tracker) and never lets the
/// operator edit announcement text mid-run (spec §4).
///
/// <b>Live-QA fix #2 (Issue 1):</b> the Settings page used to render the ENTIRE preset editor (general/winner
/// settings + up to 30 announcement lines) permanently inline underneath the preset list — reported as far too
/// large for that page. All authoring now happens in the dedicated <see cref="GiveawayPresetEditorModal"/>; this
/// page only ever shows the compact preset list plus New/Edit/Delete actions. The old "+ New Preset" flow used a
/// shared <c>UiKit.TextInputModal</c> for both new-preset naming AND rename — that class's popup id is a single
/// <c>private const string</c> shared by every instance, so having two separate <c>TextInputModal</c> fields
/// (<c>newPresetModal</c>/<c>renamePresetModal</c>, both drawn every frame) meant BOTH were, unintentionally,
/// the SAME ImGui popup identity — the reported "two Name/Create/Cancel areas" defect. Both fields are gone now;
/// there is exactly one authoring surface.</summary>
internal sealed class GiveawaysOperatorPanel(GiveawayService service, VenueProfileService venues, Action onOpenGiveawaysSettings)
{
    private readonly ConfirmDialog confirmDialog = new();
    private readonly GiveawayPresetEditorModal presetEditorModal = new();
    internal readonly GiveawaysTrackerWindow TrackerWindow = new(onOpenGiveawaysSettings);

    // =============================================================================================================
    // Live operation (Draw) — GIVEAWAYS spec §4/§5/§11/§12/§37
    // =============================================================================================================

    public void Draw()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);

        DrawActivePresetHeader(theme);
        ImGui.Spacing();
        DrawControls(theme);
        ImGui.Spacing();
        DrawTracker(theme);
    }

    /// <summary>GIVEAWAYS spec §5 — the active preset must be "impossible to miss": large, bold, clearly separated
    /// from the selector, using only theme tokens. The dropdown selector still exists (it's how the operator changes
    /// the active preset) but the name is ALSO rendered prominently here, independent of it.</summary>
    private void DrawActivePresetHeader(VenueTheme theme)
    {
        UiKit.BeginSectionCard("giveaways-active-preset", theme, "Active Preset");

        var running = service.RunningPreset;
        var selected = service.SelectedPreset;
        var displayed = running ?? selected;

        if (displayed is null)
        {
            UiKit.EmptyState(theme, "No preset selected", "Choose one below, or create one in Settings → Modules → Giveaways.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Accent));
            ImGui.SetWindowFontScale(1.6f);
            ImGui.TextUnformatted(displayed.Name);
            ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();
            if (running is not null && running.Id != selected?.Id)
                UiKit.WarningState(theme, "This is the RUNNING preset. It stays locked in for this giveaway even though a different preset is now selected below.");
        }

        ImGui.Spacing();
        DrawPresetSelector(theme);
        UiKit.EndSectionCard();
    }

    private void DrawPresetSelector(VenueTheme theme)
    {
        var presets = service.Settings.Presets;
        var names = presets.Select(x => x.Name).ToArray();
        var index = presets.ToList().FindIndex(x => x.Id == service.Settings.ActivePresetId);

        ImGui.BeginDisabled(service.Phase is not (GiveawayPhase.Idle or GiveawayPhase.Complete or GiveawayPhase.Cancelled));
        if (Forms.ComboField(theme, "Selected Preset", names, ref index, 320))
            service.SelectPreset(index >= 0 && index < presets.Count ? presets[index].Id : null);
        ImGui.EndDisabled();
        if (service.Phase is not (GiveawayPhase.Idle or GiveawayPhase.Complete or GiveawayPhase.Cancelled))
            UiKit.Tooltip("Preset selection is locked while a giveaway is running.");
    }

    private void DrawControls(VenueTheme theme)
    {
        UiKit.BeginSectionCard("giveaways-controls", theme, "Controls");

        var phase = service.Phase;
        var canStart = phase is GiveawayPhase.Idle or GiveawayPhase.Complete or GiveawayPhase.Cancelled;
        var canCancel = !canStart;

        if (canStart)
        {
            if (UiKit.PrimaryButton(theme, "Start")) DoStart();
        }
        else
        {
            ImGui.BeginDisabled(!canCancel);
            if (UiKit.DangerButton(theme, "Cancel"))
                confirmDialog.Request("Cancel this giveaway?", "This stops all remaining scheduled announcements and closes roll acceptance immediately. It does NOT delete the preset, and the rolls already captured stay visible until you Clear Results or Start again.", service.Cancel);
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Clear Results")) service.ClearResults();

        ImGui.SameLine();
        if (UiKit.GhostButton(theme, TrackerWindow.IsOpen ? "Close Tracker Window" : "Open Tracker Window")) TrackerWindow.IsOpen = !TrackerWindow.IsOpen;

        ImGui.Spacing();
        if (!string.IsNullOrEmpty(startError)) UiKit.ErrorState(theme, startError);
        DrawStatusLine(theme);

        UiKit.EndSectionCard();
    }

    private string? startError;

    private void DoStart()
    {
        var result = service.Start();
        startError = result.Success ? null : result.Error;
    }

    private void DrawStatusLine(VenueTheme theme)
    {
        var label = service.Phase switch
        {
            GiveawayPhase.Idle => "No giveaway running.",
            GiveawayPhase.Starting => "Sending Start announcements…",
            GiveawayPhase.AcceptingRolls => "Accepting rolls.",
            GiveawayPhase.Midpoint => "Sending Midpoint announcement — rolls still accepted.",
            // Roll acceptance now stays open through the whole Closing sequence, closing only once the final
            // Closing line is sent (or immediately, if Closing has no lines) — see GiveawayService.StartClosing.
            GiveawayPhase.Closing => service.IsAcceptingRolls ? "Sending Closing announcements — last chance, rolls still accepted." : "Closing announcements sent — rolls closed.",
            GiveawayPhase.Complete => "Giveaway complete.",
            GiveawayPhase.Cancelled => "Giveaway cancelled.",
            _ => "",
        };
        UiKit.StatusBadge(theme, label, service.Phase switch
        {
            GiveawayPhase.AcceptingRolls or GiveawayPhase.Midpoint => ToastLevel.Success,
            GiveawayPhase.Closing => service.IsAcceptingRolls ? ToastLevel.Success : ToastLevel.Information,
            GiveawayPhase.Cancelled => ToastLevel.Warning,
            _ => ToastLevel.Information,
        });

        // Time Remaining now tracks the fixed Giveaway Duration only (when Closing begins), not when roll
        // acceptance actually closes — it intentionally goes blank once Closing begins, rather than misleadingly
        // showing "00:00" as if the timer were still counting something down.
        if (service.TimeRemaining is { } remaining)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"Time remaining: {remaining:mm\\:ss}");
        }
    }

    // =============================================================================================================
    // Roll tracker — shared rendering used by both the embedded panel and GiveawaysTrackerWindow (GIVEAWAYS spec §28)
    // =============================================================================================================

    internal void DrawTracker(VenueTheme theme)
    {
        UiKit.BeginSectionCard("giveaways-tracker", theme, "Roll Tracker");

        var preset = service.RunningPreset;
        if (preset is null) { UiKit.EmptyState(theme, "No active giveaway", "Start a giveaway to begin tracking rolls."); UiKit.EndSectionCard(); return; }

        ImGui.TextUnformatted($"Winner Mode: {preset.WinnerMode}" + (preset.WinnerMode == GiveawayWinnerMode.Closest ? $" (target {preset.ClosestTargetNumber})" : ""));
        ImGui.SameLine();
        UiKit.StatusBadge(theme, $"Total Rolls: {service.TotalRolls}", ToastLevel.Information);
        // GIVEAWAYS live-QA fix #2: a concise, always-visible note (not just a hover tooltip) so the operator isn't
        // surprised when a distant participant's roll never appears — this is FFXIV's own /random range, not a
        // Giveaways bug.
        ImGui.TextDisabled("Roll visibility is limited by FFXIV's normal /random message range — players must be close enough to the host for their roll to appear in the host's chat.");

        var rows = service.Leaderboard;
        if (rows.Count == 0)
        {
            UiKit.EmptyState(theme, "No rolls yet", "Waiting for /random results.");
            UiKit.EndSectionCard();
            return;
        }

        ImGui.Spacing();
        var showDistance = preset.WinnerMode == GiveawayWinnerMode.Closest;
        if (ImGui.BeginTable("giveaways-roll-table", showDistance ? 4 : 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("Roll");
            if (showDistance) ImGui.TableSetupColumn("Distance");
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                var textColor = row.IsLeader ? theme.Tokens.Success : theme.Tokens.TextPrimary;
                ImGui.TableNextColumn(); ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(textColor)); ImGui.TextUnformatted(row.Player.Name); ImGui.PopStyleColor();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(string.IsNullOrEmpty(row.Player.HomeWorld) ? "—" : row.Player.HomeWorld);
                ImGui.TableNextColumn(); ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(textColor)); ImGui.TextUnformatted(row.Value.ToString()); ImGui.PopStyleColor();
                if (showDistance) { ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Distance.ToString()); }

                if (row.IsLeader) { ImGui.SameLine(); UiKit.StatusBadge(theme, "LEADER", ToastLevel.Success); }
                if (row.IsSpecialHit) { ImGui.SameLine(); UiKit.StatusBadge(theme, "SPECIAL", ToastLevel.Warning); }
            }
            ImGui.EndTable();
        }

        if (preset.SpecialNumbers.Count > 0 && !preset.SpecialNumbersActive)
            UiKit.WarningState(theme, "Special Numbers are configured on this preset but inactive for this run (Allowed Rolls Per Person is not exactly 1).");

        UiKit.EndSectionCard();
    }

    // =============================================================================================================
    // Settings — compact preset list only; all authoring happens in GiveawayPresetEditorModal (live-QA fix #2,
    // Issue 1). GIVEAWAYS spec §4/§6/§29/§30/§31.
    // =============================================================================================================

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);
        presetEditorModal.Draw(theme, service);

        UiKit.InfoBanner(theme, "Presets are authored here", "Create and edit giveaway presets in Settings. The live Giveaways module only ever selects and runs a saved preset — it never authors announcement text.", ToastLevel.Information);
        ImGui.Spacing();

        DrawPresetBrowser(theme);
    }

    private void DrawPresetBrowser(VenueTheme theme)
    {
        UiKit.BeginSectionCard("giveaways-preset-browser", theme, $"Presets ({service.Settings.Presets.Count})");

        if (service.Settings.Presets.Count == 0) UiKit.EmptyState(theme, "No presets yet", "Click + New Preset below to create one.");
        foreach (var preset in service.Settings.Presets)
        {
            ImGui.PushID(preset.Id.ToString());
            var subtitle = $"{preset.Channel} · {preset.WinnerMode}" + (preset.Id == service.RunningPreset?.Id ? " · RUNNING" : "");
            UiKit.ListRow(theme, preset.Name, subtitle, false);

            if (UiKit.GhostButton(theme, "Edit")) presetEditorModal.OpenForEdit(preset);
            ImGui.SameLine();
            var canDelete = service.CanDeletePreset(preset.Id);
            ImGui.BeginDisabled(!canDelete);
            if (UiKit.DangerButton(theme, "Delete"))
                confirmDialog.Request("Delete this preset?", $"\"{preset.Name}\" will be permanently deleted. This cannot be undone.", () => service.DeletePreset(preset.Id));
            ImGui.EndDisabled();
            if (!canDelete) UiKit.Tooltip("This preset is currently running and cannot be deleted until the giveaway is cancelled or completes.");
            UiKit.Divider(theme);

            ImGui.PopID();
        }

        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "+ New Preset")) presetEditorModal.OpenForNew();

        UiKit.EndSectionCard();
    }
}
