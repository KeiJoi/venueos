using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>ShoutRunner's <c>Draw()</c>/<c>DrawSettings()</c> content — its own file per <c>NEW_MODULE_GUIDE.md</c>
/// §21/§33 (not grown into <c>NativeOperationsPanels.cs</c>). <c>Draw()</c> is deliberately compact: Shout Message,
/// Status + Start/Stop, then the Run Terminal — no macro editor, no preset page, no giant settings copy (per the
/// reconstruction brief's "SHOUTRUNNER LIVE MODULE UI").</summary>
internal sealed class ShoutRunnerOperatorPanel(ShoutRunnerService service, VenueProfileService venues)
{
    private Guid bufferVenueId;
    private string shoutMessageBuffer = string.Empty;
    private bool forceScrollToBottom;
    private ShoutRunnerStartResult? lastStartResult;
    private double? copiedAtImGuiTime;
    private readonly ConfirmDialog confirmDialog = new();

    // Settings-only scratch state for the destination editor.
    private string newDestinationBuffer = string.Empty;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        SyncBuffer();

        DrawRecoveryArea(theme);

        UiKit.BeginSectionCard("shoutrunner-message", theme, "Shout Message");
        if (Forms.TextField(theme, "Message sent with /shout at each destination", ref shoutMessageBuffer, 500))
        {
            service.UpdateShoutMessage(shoutMessageBuffer);
            lastStartResult = null;
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("shoutrunner-status", theme, "Status");
        DrawStatusAndControls(theme);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawTerminal(theme);

        confirmDialog.Draw(theme);
    }

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings;

        UiKit.BeginSectionCard("shoutrunner-timing", theme, "Run Timing");
        var repeat = settings.RepeatEnabled;
        if (UiKit.Toggle(theme, "Repeat automatically", ref repeat)) service.SetRepeatEnabled(repeat);
        ImGui.Spacing();
        var hours = settings.IntervalHours;
        var minutes = settings.IntervalMinutes;
        var seconds = settings.IntervalSeconds;
        var intervalChanged = false;
        intervalChanged |= Forms.NumericField(theme, "Interval hours", ref hours, 1, 0, 999);
        intervalChanged |= Forms.NumericField(theme, "Interval minutes", ref minutes, 1, 0, 59);
        intervalChanged |= Forms.NumericField(theme, "Interval seconds", ref seconds, 1, 0, 59);
        if (intervalChanged) service.SetInterval(hours, minutes, seconds);
        UiKit.InfoBanner(theme, "Repeat scheduling", $"A RUN's next occurrence is anchored to when the previous RUN started, not when it finished — a minimum of {ShoutRunnerSettings.MinimumIntervalSeconds / 60} minute is enforced. If a RUN overruns its next scheduled time, the schedule advances to the next future occurrence rather than starting immediately or repeating a missed one.");
        ImGui.Spacing();
        var delay = settings.DelayBetweenActionsSeconds;
        if (Forms.NumericField(theme, "Delay between route actions (seconds)", ref delay, 1, 0, 120)) service.SetDelaySeconds(delay);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("shoutrunner-datacenters", theme, "Data Centers");
        UiKit.InfoBanner(theme, "Fixed order", "Selected Data Centers are always visited in this order — Aether, Crystal, Dynamis, Primal — and cannot be reordered. Every World in a selected Data Center is visited automatically.");
        ImGui.Spacing();
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
        {
            var selected = settings.SelectedDataCenters.Contains(dc, StringComparer.OrdinalIgnoreCase);
            if (UiKit.Toggle(theme, dc, ref selected)) service.SetDataCenterSelected(dc, selected);
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawDestinationsEditor(theme, settings);
    }

    private void DrawStatusAndControls(VenueTheme theme)
    {
        var (label, level) = service.State switch
        {
            ShoutRunnerState.Stopped => ("Stopped", ToastLevel.Information),
            ShoutRunnerState.Faulted => ("Faulted", ToastLevel.Error),
            ShoutRunnerState.Stopping => ("Stopping…", ToastLevel.Warning),
            ShoutRunnerState.WaitingRepeat => ("Waiting for next RUN", ToastLevel.Information),
            ShoutRunnerState.RecoveringTravel => ("Recovering", ToastLevel.Warning),
            _ => ("Running", ToastLevel.Success),
        };
        UiKit.StatusBadge(theme, label, level);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(service.StatusText);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        if (service.State is ShoutRunnerState.Stopped or ShoutRunnerState.Faulted)
        {
            // Crash-recovery brief "START NEW RUN WITH RECOVERY PRESENT": starting fresh while an interrupted run
            // could still be resumed requires deliberate confirmation, since Start() unconditionally overwrites the
            // recovery checkpoint the moment it actually runs.
            var hasRecovery = service.RecoveredJournal is not null;
            if (UiKit.PrimaryButton(theme, hasRecovery ? "Start New Run" : "Start Run", new Vector2(160, 32)))
            {
                if (hasRecovery)
                {
                    confirmDialog.Request("Start a new run?",
                        "An unfinished ShoutRunner run can still be resumed.\n\nStarting a new run will discard its recovery checkpoint.\n\nStart new run?",
                        () => lastStartResult = service.Start());
                }
                else
                {
                    lastStartResult = service.Start();
                }
            }
        }
        else
        {
            var canStop = service.State != ShoutRunnerState.Stopping;
            if (canStop && UiKit.DangerButton(theme, "Stop", new Vector2(140, 32))) service.Stop();
        }

        if (lastStartResult is { } result and not ShoutRunnerStartResult.Started)
        {
            ImGui.Spacing();
            UiKit.WarningState(theme, result switch
            {
                ShoutRunnerStartResult.ShoutMessageRequired => "Enter a Shout Message before starting.",
                ShoutRunnerStartResult.NoDataCenterSelected => "Select at least one Data Center in Settings → Modules → ShoutRunner before starting.",
                ShoutRunnerStartResult.NoDestinationConfigured => "Configure at least one destination in Settings → Modules → ShoutRunner before starting.",
                _ => "ShoutRunner is already running.",
            });
        }
    }

    /// <summary>Crash-recovery brief "START VS RESUME UI"/"RESUME SUMMARY"/"CORRUPT / INCOMPATIBLE RECOVERY FILE"/
    /// "VENUE PROFILE SAFETY" — an operational action on ShoutRunner's own main screen, never hidden in Settings.
    /// Shown only while genuinely idle (<see cref="ShoutRunnerState.Stopped"/>/<see cref="ShoutRunnerState.Faulted"/>);
    /// an active or already-resumed run's own journal is just its live checkpoint, not a separate "available to
    /// resume" offer.</summary>
    private void DrawRecoveryArea(VenueTheme theme)
    {
        if (service.State is not (ShoutRunnerState.Stopped or ShoutRunnerState.Faulted)) return;

        if (service.RecoveredJournalIsCorrupt)
        {
            UiKit.BeginSectionCard("shoutrunner-recovery", theme, "Interrupted Run");
            UiKit.ErrorState(theme, "ShoutRunner recovery data could not be loaded.");
            ImGui.Spacing();
            if (UiKit.DangerButton(theme, "Discard Recovery", new Vector2(160, 0)))
                confirmDialog.Request("Discard recovery data?", "This removes the unreadable interrupted-run checkpoint. Your saved route/settings are not affected.", service.DiscardRecovery);
            UiKit.EndSectionCard();
            ImGui.Spacing();
            return;
        }

        if (service.RecoveredJournal is null) return;
        var summary = service.RecoveredSummary!;

        UiKit.BeginSectionCard("shoutrunner-recovery", theme, "Interrupted Run Available");

        if (!service.RecoveredJournalBelongsToCurrentVenue)
        {
            UiKit.WarningState(theme, $"This interrupted run belongs to venue \"{summary.VenueDisplayName}\". Switch to that venue to resume it.");
            ImGui.Spacing();
            if (UiKit.DangerButton(theme, "Discard Recovery", new Vector2(160, 0)))
                confirmDialog.Request("Discard recovery data?", $"This removes the interrupted run belonging to \"{summary.VenueDisplayName}\". Your saved route/settings are not affected.", service.DiscardRecovery);
            UiKit.EndSectionCard();
            ImGui.Spacing();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted($"RUN {summary.RunNumber} — {summary.DataCenter ?? "?"} / {summary.World ?? "?"}");
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        if (summary.LastCompletedDestination is not null) ImGui.TextUnformatted($"Last completed: {summary.LastCompletedDestination}");
        ImGui.TextUnformatted($"Next: {summary.NextDestination ?? "will be determined on resume"}");
        ImGui.PopStyleColor();

        if (summary.SkippedDataCenters.Count > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Warning));
            ImGui.TextUnformatted("Skipped this run:");
            foreach (var skip in summary.SkippedDataCenters) ImGui.TextUnformatted($"  {skip.DataCenter} — {skip.Reason}");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Resume Run", new Vector2(140, 0))) service.Resume();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Discard Recovery", new Vector2(160, 0)))
            confirmDialog.Request("Discard recovery data?", "This removes the interrupted-run checkpoint only. Your saved route/settings are not affected.", service.DiscardRecovery);

        UiKit.EndSectionCard();
        ImGui.Spacing();
    }

    /// <summary>Exports the complete retained terminal history — never just the visible/scrolled viewport, the
    /// current RUN, or the current Data Center — as clean plain text, generated from the structured
    /// <see cref="ShoutRunnerTerminalEvent"/> model (see <see cref="ShoutRunnerTerminalFormatter"/>), and places it
    /// on the clipboard via the same <c>ImGui.SetClipboardText</c> mechanism already used by Settings → Diagnostics'
    /// own "Copy" button. Purely a read-only export: <paramref name="events"/> is the same immutable snapshot
    /// <see cref="ShoutRunnerService.TerminalEvents"/> already returns on every call, so this never pauses the
    /// route, touches route state, or mutates the terminal.</summary>
    private void DrawCopyTerminalButton(VenueTheme theme, IReadOnlyList<ShoutRunnerTerminalEvent> events)
    {
        var canCopy = events.Count > 0;
        ImGui.BeginDisabled(!canCopy);
        if (UiKit.GhostButton(theme, "Copy Terminal", new Vector2(140, 0)))
        {
            ImGui.SetClipboardText(ShoutRunnerTerminalFormatter.ToPlainText(events));
            copiedAtImGuiTime = ImGui.GetTime();
        }
        ImGui.EndDisabled();

        if (copiedAtImGuiTime is { } copiedAt && ImGui.GetTime() - copiedAt < 2.0)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
            ImGui.TextUnformatted("Copied");
            ImGui.PopStyleColor();
        }
    }

    private void DrawTerminal(VenueTheme theme)
    {
        UiKit.BeginSectionCard("shoutrunner-terminal", theme, "Run Terminal");
        var events = service.TerminalEvents;

        DrawCopyTerminalButton(theme, events);
        ImGui.Spacing();

        if (events.Count == 0)
        {
            UiKit.EmptyState(theme, "No route history yet", "Start ShoutRunner to see RUN progress here.");
            UiKit.EndSectionCard();
            return;
        }

        var childHeight = MathF.Max(220f, ImGui.GetContentRegionAvail().Y - 12f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiKit.Color(theme.Tokens.Background));
        ImGui.BeginChild("shoutrunner-terminal-scroll", new Vector2(0, childHeight), true);
        var wasAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;

        foreach (var entry in events)
        {
            var depth = entry.Destination is not null ? 3 : entry.World is not null ? 2 : entry.DataCenter is not null ? 1 : 0;
            var color = entry.Severity switch
            {
                ShoutRunnerEventSeverity.Success => theme.Tokens.Success,
                ShoutRunnerEventSeverity.Failure => theme.Tokens.Error,
                ShoutRunnerEventSeverity.Warning => theme.Tokens.Warning,
                _ => theme.Tokens.Accent,
            };

            if (depth > 0) ImGui.Indent(depth * 16f);
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(color));
            ImGui.TextUnformatted($"[{entry.At.ToLocalTime():T}] RUN {entry.RunNumber} — {entry.Text}");
            ImGui.PopStyleColor();
            if (depth > 0) ImGui.Unindent(depth * 16f);
        }

        if (wasAtBottom || forceScrollToBottom)
        {
            ImGui.SetScrollHereY(1f);
            forceScrollToBottom = false;
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (!wasAtBottom)
        {
            ImGui.Spacing();
            if (UiKit.GhostButton(theme, "Jump to latest")) forceScrollToBottom = true;
        }

        UiKit.EndSectionCard();
    }

    private void DrawDestinationsEditor(VenueTheme theme, ShoutRunnerSettings settings)
    {
        UiKit.BeginSectionCard("shoutrunner-destinations", theme, "Destinations");
        UiKit.InfoBanner(theme, "Route topology, not a fixed order", "The character's actual current location (when identifiable) determines where a World's traversal starts; the list below only defines the path and its direction — see the reconstruction notes for the ping-pong algorithm.");
        ImGui.Spacing();

        for (var i = 0; i < settings.Destinations.Count; i++)
        {
            ImGui.PushID(i);
            var value = settings.Destinations[i];
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 220);
            Forms.PushFieldStyle(theme);
            var changed = ImGui.InputText("##destination", ref value, 128);
            Forms.PopFieldStyle();
            if (changed) service.RenameDestinationAt(i, value);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Up", new Vector2(48, 0))) service.MoveDestination(i, -1);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Down", new Vector2(56, 0))) service.MoveDestination(i, 1);
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Remove", new Vector2(70, 0))) service.RemoveDestinationAt(i);
            ImGui.PopID();
        }

        if (settings.Destinations.Count == 0) UiKit.EmptyState(theme, "No destinations configured", "Add at least one below.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90);
        Forms.PushFieldStyle(theme);
        ImGui.InputTextWithHint("##new-destination", "e.g. Ul'dah - Steps of Nald", ref newDestinationBuffer, 128);
        Forms.PopFieldStyle();
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "Add", new Vector2(70, 0)) && !string.IsNullOrWhiteSpace(newDestinationBuffer))
        {
            service.AddDestination(newDestinationBuffer);
            newDestinationBuffer = string.Empty;
        }

        UiKit.EndSectionCard();
    }

    private void SyncBuffer()
    {
        var venueId = venues.Current.Id;
        if (bufferVenueId == venueId) return;
        bufferVenueId = venueId;
        shoutMessageBuffer = service.Settings.ShoutMessage;
        lastStartResult = null;
    }
}
