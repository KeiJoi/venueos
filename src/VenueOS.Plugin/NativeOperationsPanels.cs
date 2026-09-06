using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

internal sealed class AttendanceOperatorPanel(AttendanceService attendance, VenueProfileService venues, AttendanceExportService exportService, GreetingCoordinator coordinator, IVenueAddressProvider? addressProvider = null, Action<GuestIdentity>? tryTarget = null)
{
    private int tab;
    private string guestSearch = "";
    private string visitorSearch = "";
    private long? summarySessionId;
    private string exportDirectoryBuffer = "";
    private bool exportDirectoryInitialized;
    private string? exportStatus;
    private VenueAddressSnapshot? detectedAddress;
    private readonly ConfirmDialog confirmDialog = new();
    private Guid settingsVenueId;
    private AttendanceSettings settingsCache = new();

    /// <summary>Live operation only, split into the donor's own sections (Live/Visitors/History/Analytics) rather
    /// than one long scrolling page. Presence-filtering and Venue Details setup live in <see cref="DrawSettings"/>.</summary>
    public void Draw()
    {
        var theme = venues.Current.Theme;
        var tabIndex = tab;
        Forms.Segmented(theme, "attendance-tabs", ["Live", "Visitors", "History", "Analytics"], ref tabIndex);
        tab = tabIndex;
        ImGui.Spacing();
        switch (tab)
        {
            case 1: DrawVisitors(theme); break;
            case 2: DrawHistory(theme); break;
            case 3: DrawAnalytics(theme); break;
            default: DrawLive(theme); break;
        }
        confirmDialog.Draw(theme);
    }

    private void DrawLive(VenueTheme theme)
    {
        var settings = ReadSettings();
        var isActive = attendance.CurrentSessionId is not null;

        UiKit.BeginSectionCard("attendance-session", theme, "Session");
        UiKit.StatusBadge(theme, isActive ? "Open" : "Closed", isActive ? ToastLevel.Success : ToastLevel.Information);
        ImGui.SameLine();
        if (isActive)
        {
            if (UiKit.GhostButton(theme, "Pause Opening")) attendance.PauseSession(DateTimeOffset.UtcNow);
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Close Opening")) attendance.CloseSession(DateTimeOffset.UtcNow);
        }
        else
        {
            if (UiKit.PrimaryButton(theme, "Start New Opening")) attendance.StartSession(settings.LockToOpenTerritory, settings.AreaMode == PresenceAreaMode.FixedPoint, DateTimeOffset.UtcNow);
            var resumable = attendance.GetRecentSessions().FirstOrDefault(x => x.IsResumable);
            ImGui.SameLine();
            if (resumable is null) ImGui.BeginDisabled();
            if (UiKit.GhostButton(theme, "Resume Latest") && resumable is not null) attendance.ResumeSession(resumable.SessionId, settings.LockToOpenTerritory, settings.AreaMode == PresenceAreaMode.FixedPoint);
            if (resumable is null) ImGui.EndDisabled();
        }
        if (isActive && attendance.ActiveTerritoryLock is { } territory)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted($"Locked to territory {territory}.");
            ImGui.PopStyleColor();
        }
        else if (!isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped("Pick a specific past opening to resume, close, or delete from the History tab.");
            ImGui.PopStyleColor();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        var summary = attendance.GetTonightSummary();
        UiKit.BeginSectionCard("attendance-summary", theme, "Tonight Summary");
        if (summary is null) UiKit.EmptyState(theme, "No opening yet", "Start a new opening to begin tracking tonight's attendance.");
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted($"Date: {summary.NightDate:yyyy-MM-dd}");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            StatRow(theme, ("Current", summary.CurrentGuests.ToString()), ("Max", summary.MaxGuests.ToString()), ("Min", summary.MinGuests.ToString()));
            ImGui.Spacing();
            StatRow(theme, ("Unique", summary.UniqueGuests.ToString()), ("Visits", summary.TotalVisits.ToString()), ("Avg / Guest", FormatDuration(summary.AverageGuestTime)));
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("attendance-guests", theme, $"Guests Nearby ({attendance.Guests.Count})");
        Forms.SearchBox(theme, "attendance-search", ref guestSearch, "Search guests by name");
        ImGui.Spacing();
        var guests = attendance.Guests.Values.Where(g => string.IsNullOrWhiteSpace(guestSearch) || g.Name.Contains(guestSearch, StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Name).ToArray();
        if (guests.Length == 0) UiKit.EmptyState(theme, "No guests yet", "Guests will appear here as they're detected nearby.");
        else foreach (var guest in guests)
        {
            ImGui.PushID(guest.Name + guest.HomeWorld);
            // Attendance is the authoritative greeted-state source (see AttendanceService.IsGreeted's doc comment) —
            // this reads it directly rather than a Greeter-owned flag.
            var subtitle = attendance.IsGreeted(new GuestIdentity(guest.Name, guest.HomeWorld)) ? $"{guest.HomeWorld} · Greeted" : guest.HomeWorld;
            UiKit.ListRow(theme, guest.Name, subtitle, false);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    /// <summary>Row actions are state-driven, not just disabled/enabled — Greet and Mark Greeted are only ever
    /// shown for a genuinely Not Greeted, not-currently-queued visitor; once greeted-state is authoritative
    /// (Attendance says Greeted) or a greeting attempt is already in flight (transient, UI-only — see
    /// <see cref="GreeterService.GetProgress"/>'s doc comment), those actions are hidden entirely, not merely
    /// grayed out. "Greet" invokes <see cref="GreetingCoordinator.TryGreet"/> — the same shared orchestration
    /// entry point automatic arrivals use (VIP recognition first if applicable, then the normal Greeter sequence) —
    /// never a direct <see cref="GreeterService.QueueGreeting"/> call that would bypass VIP. "Mark Greeted" is the
    /// separate, explicit "don't send anything, this person is already handled" action — it still calls
    /// <see cref="AttendanceService.MarkGreeted"/> directly, never the coordinator.</summary>
    private void DrawVisitors(VenueTheme theme)
    {
        UiKit.BeginSectionCard("attendance-visitors", theme, "Tonight Visitors");
        Forms.SearchBox(theme, "attendance-visitor-search", ref visitorSearch, "Search visitors");
        ImGui.Spacing();
        var visitors = attendance.GetTonightVisitors().Where(v => string.IsNullOrWhiteSpace(visitorSearch) || v.CharacterName.Contains(visitorSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (visitors.Length == 0) UiKit.EmptyState(theme, "No visitors yet", "Visitors appear here once an opening starts tracking arrivals.");
        else foreach (var visitor in visitors)
        {
            ImGui.PushID(visitor.Identity.Key);
            var progress = coordinator.GetProgress(visitor.Identity);
            var actionable = !visitor.Greeted && progress == GreetingProgress.None;
            var nameColor = visitor.Greeted ? theme.Tokens.Success : visitor.IsPresent ? theme.Tokens.Warning : theme.Tokens.TextPrimary;
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(nameColor));
            ImGui.TextUnformatted(visitor.IsPresent ? visitor.CharacterName : $"({visitor.CharacterName})");
            ImGui.PopStyleColor();
            if (ImGui.BeginPopupContextItem("##visitor-context"))
            {
                if (tryTarget is not null && visitor.IsPresent && ImGui.MenuItem("Target")) tryTarget(visitor.Identity);
                if (actionable)
                {
                    if (visitor.IsPresent && ImGui.MenuItem("Greet")) coordinator.TryGreet(visitor.Identity, GreetingSource.ManualAttendance);
                    if (ImGui.MenuItem("Mark as Greeted")) attendance.MarkGreeted(visitor.Identity, true, DateTimeOffset.UtcNow);
                }
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted($"@ {visitor.HomeWorld} · {visitor.Visits} visit(s) · {FormatDuration(visitor.TotalTime)}");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (visitor.Greeted) UiKit.StatusBadge(theme, "Greeted", ToastLevel.Success);
            else if (progress == GreetingProgress.InProgress) UiKit.StatusBadge(theme, "Greeting...", ToastLevel.Information);
            else if (progress == GreetingProgress.Queued) UiKit.StatusBadge(theme, "Queued", ToastLevel.Information);
            else UiKit.StatusBadge(theme, "Not Greeted", ToastLevel.Warning);
            if (tryTarget is not null && visitor.IsPresent) { ImGui.SameLine(); if (UiKit.GhostButton(theme, "Target", new Vector2(60, 0))) tryTarget(visitor.Identity); }
            if (actionable)
            {
                if (visitor.IsPresent) { ImGui.SameLine(); if (UiKit.PrimaryButton(theme, "Greet", new Vector2(70, 0))) coordinator.TryGreet(visitor.Identity, GreetingSource.ManualAttendance); }
                ImGui.SameLine();
                if (UiKit.GhostButton(theme, "Mark Greeted", new Vector2(110, 0))) attendance.MarkGreeted(visitor.Identity, true, DateTimeOffset.UtcNow);
            }
            ImGui.Spacing(); UiKit.Divider(theme); ImGui.Spacing();
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    private void DrawHistory(VenueTheme theme)
    {
        var settings = ReadSettings();
        UiKit.BeginSectionCard("attendance-sessions", theme, "Openings");
        var sessions = attendance.GetRecentSessions();
        if (sessions.Count == 0) UiKit.EmptyState(theme, "No openings yet", "Start a new opening from the Live tab.");
        else foreach (var session in sessions)
        {
            ImGui.PushID(session.SessionId.ToString());
            var isCurrent = attendance.CurrentSessionId == session.SessionId;
            var label = session.ClosedAtUtc is { } closed
                ? $"{session.NightDate:yyyy-MM-dd} {session.OpenedAtUtc.LocalDateTime:t} – {closed.LocalDateTime:t}"
                : $"{session.NightDate:yyyy-MM-dd} {session.OpenedAtUtc.LocalDateTime:t} – (resumable)";
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (isCurrent) UiKit.StatusBadge(theme, "Current", ToastLevel.Success);
            else if (session.IsResumable)
            {
                if (UiKit.GhostButton(theme, "Resume", new Vector2(70, 0))) attendance.ResumeSession(session.SessionId, settings.LockToOpenTerritory, settings.AreaMode == PresenceAreaMode.FixedPoint);
                ImGui.SameLine();
                if (UiKit.GhostButton(theme, "Close", new Vector2(60, 0))) attendance.CloseSession(session.SessionId, DateTimeOffset.UtcNow);
            }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Summary", new Vector2(70, 0))) summarySessionId = session.SessionId;
            if (!isCurrent)
            {
                ImGui.SameLine();
                if (UiKit.DangerButton(theme, "Delete", new Vector2(60, 0))) confirmDialog.Request("Delete opening?", $"Permanently delete the opening from {label}. This cannot be undone.", () => { attendance.DeleteSession(session.SessionId); if (summarySessionId == session.SessionId) summarySessionId = null; });
            }
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawSelectedOpeningSummary(theme, sessions);

        ImGui.Spacing();
        UiKit.BeginSectionCard("attendance-comparison", theme, "Max / Min Guest Comparison");
        var days = Math.Clamp(settings.StatsRangeDays, 1, 30);
        if (Forms.NumericField(theme, "Comparison days", ref days, 1, 1, 30)) SaveSettings(settings with { StatsRangeDays = Math.Clamp(days, 1, 30) });
        var to = DateOnly.FromDateTime(DateTime.Now);
        var from = to.AddDays(-(Math.Clamp(settings.StatsRangeDays, 1, 30) - 1));
        var daily = attendance.GetDailyStats(from, to);
        ImGui.Spacing();
        UiKit.LineChart(theme, "##attendance-daily-chart", "Max Guests / Min Guests", [.. daily.Select(x => (float)x.MaxGuests)], theme.Tokens.Accent, [.. daily.Select(x => (float)x.MinGuests)], theme.Tokens.Warning);
        ImGui.Spacing();
        if (daily.Count == 0) UiKit.EmptyState(theme, "No history yet", "Daily stats appear here after your first opening.");
        else foreach (var row in daily)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted($"{row.NightDate:MM-dd}   Max {row.MaxGuests}   Min {row.MinGuests}   Unique {row.UniqueGuests}   Visits {row.TotalVisits}");
            ImGui.PopStyleColor();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("attendance-export", theme, "Export");
        if (!exportDirectoryInitialized) { exportDirectoryBuffer = settings.ExportDirectory; exportDirectoryInitialized = true; }
        Forms.TextField(theme, "Export folder", ref exportDirectoryBuffer, 260);
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Export Range to Excel"))
        {
            try
            {
                SaveSettings(ReadSettings() with { ExportDirectory = exportDirectoryBuffer.Trim() });
                var path = attendance.ExportRange(exportService, from, to, exportDirectoryBuffer.Trim());
                exportStatus = $"Exported: {path}";
            }
            catch (Exception ex) { exportStatus = $"Export failed: {ex.Message}"; }
        }
        if (exportStatus is not null) { ImGui.Spacing(); ImGui.TextWrapped(exportStatus); }
        UiKit.EndSectionCard();
    }

    /// <summary>The confirmed-missing per-opening summary: unlike the night-level Max/Min Guest Comparison below
    /// (which aggregates every opening on a calendar date together), this shows one specific opening's own Max
    /// Guests / Min Guests / Unique Visitors / Total Visits — donor parity (<c>MainWindow</c>'s per-selected-session
    /// "Openings" combo). Backed by <see cref="AttendanceService.GetSessionSummary"/>, a thin pass-through to the
    /// exact same <c>IVenueDatabase.GetSessionSummary</c> query the Live tab's "Tonight Summary" already uses for
    /// the *current* session — no database change was needed, only a caller for a historical session id.</summary>
    private void DrawSelectedOpeningSummary(VenueTheme theme, IReadOnlyList<AttendanceSessionRecord> sessions)
    {
        UiKit.BeginSectionCard("attendance-session-summary", theme, "Opening Summary");
        var selected = summarySessionId is long id ? sessions.FirstOrDefault(x => x.SessionId == id) : null;
        if (selected is null) UiKit.EmptyState(theme, "No opening selected", "Choose \"Summary\" on an opening above to see its Max/Min guests, unique visitors, and total visits.");
        else
        {
            var summary = attendance.GetSessionSummary(selected.SessionId);
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted(selected.ClosedAtUtc is { } closed
                ? $"{selected.NightDate:yyyy-MM-dd} {selected.OpenedAtUtc.LocalDateTime:t} – {closed.LocalDateTime:t}"
                : $"{selected.NightDate:yyyy-MM-dd} {selected.OpenedAtUtc.LocalDateTime:t} – (resumable)");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            StatRow(theme, ("Max Guests", summary.MaxGuests.ToString()), ("Min Guests", summary.MinGuests.ToString()), ("Unique Visitors", summary.UniqueGuests.ToString()));
            ImGui.Spacing();
            StatRow(theme, ("Total Visits", summary.TotalVisits.ToString()), ("Avg / Guest", FormatDuration(summary.AverageGuestTime)));
        }
        UiKit.EndSectionCard();
    }

    private void DrawAnalytics(VenueTheme theme)
    {
        UiKit.BeginSectionCard("attendance-samples", theme, "Guest Samples (Tonight)");
        var samples = attendance.GetTonightSamples();
        UiKit.LineChart(theme, "##attendance-sample-chart", "Guests per sample", [.. samples.Select(x => (float)x.GuestCount)], theme.Tokens.Success);
        ImGui.Spacing();
        if (samples.Count == 0) UiKit.EmptyState(theme, "No samples yet", "Samples are recorded periodically while an opening is active.");
        else foreach (var sample in samples.TakeLast(20))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted($"{sample.SampleAtUtc.LocalDateTime:HH:mm}   {sample.GuestCount} guest(s)");
            ImGui.PopStyleColor();
        }
        UiKit.EndSectionCard();
    }

    /// <summary>Persistent Venue Details (address/tracking) and presence-filtering setup. Territory lock and the
    /// distance-filter's "follow me" vs. "fixed point" behavior are configured here; the actual territory/position
    /// are captured live when a session starts (<see cref="AttendanceService.StartSession"/>), never typed in as a
    /// coordinate. The venue's *name* is never duplicated here — only location/address text.</summary>
    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var settings = ReadSettings();

        UiKit.BeginSectionCard("attendance-address", theme, "Venue Details");
        var address = settings.VenueAddress;
        if (Forms.TextField(theme, "In-game address", ref address, 180)) SaveSettings(settings with { VenueAddress = address });
        var autoDetect = settings.AutoDetectVenueAddress;
        if (UiKit.Toggle(theme, "Auto-detect address in-game", ref autoDetect)) SaveSettings(ReadSettings() with { AutoDetectVenueAddress = autoDetect });
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Detect Now"))
        {
            if (addressProvider is null) UiKit.WarningState(theme, "Address detection is not available.");
            else if (addressProvider.TryGetCurrentAddress(out var snapshot))
            {
                detectedAddress = snapshot;
                if (snapshot.IsHousingArea) SaveSettings(ReadSettings() with { VenueAddress = snapshot.ToAddressString() });
            }
        }
        if (detectedAddress is { } detected)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped($"DC: {detected.DataCenter}   Server: {detected.Server}   District: {detected.District}   Ward: {(detected.Ward?.ToString() ?? "-")}   Plot: {(detected.Plot?.ToString() ?? "-")}");
            ImGui.PopStyleColor();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("attendance-filtering", theme, "Presence Filtering");
        var lockTerritory = settings.LockToOpenTerritory;
        if (UiKit.Toggle(theme, "Lock to the territory the session starts in", ref lockTerritory)) SaveSettings(ReadSettings() with { LockToOpenTerritory = lockTerritory });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Only players in the same territory the operator was in when the session started count as guests.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var useDistance = settings.UseDistanceFilter;
        if (UiKit.Toggle(theme, "Filter by distance", ref useDistance)) SaveSettings(ReadSettings() with { UseDistanceFilter = useDistance });
        if (useDistance)
        {
            ImGui.Spacing();
            Forms.FieldLabel(theme, "Venue area type");
            // Labels only — PresenceAreaMode/FollowOperator/FixedPoint (the persisted enum and its stored value)
            // are unchanged; this is purely clearer user-facing wording for the same two behaviors.
            var areaIndex = settings.AreaMode == PresenceAreaMode.FixedPoint ? 1 : 0;
            if (Forms.Segmented(theme, "attendance-area-mode", ["Normal venue area", "Outdoor Event Area (fixed origin)"], ref areaIndex)) SaveSettings(ReadSettings() with { AreaMode = areaIndex == 1 ? PresenceAreaMode.FixedPoint : PresenceAreaMode.FollowOperator });
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped(settings.AreaMode == PresenceAreaMode.FixedPoint
                ? "Outdoor Event Area: the radius center is captured once, from the operator's position when the session starts — for open-world venues where the operator may move around."
                : "Normal venue area: the radius always follows the operator's current position — appropriate for a housing instance.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            var radius = (int)settings.RadiusYalms;
            if (Forms.NumericField(theme, "Radius (yalms)", ref radius, 5, 5, 150)) SaveSettings(ReadSettings() with { RadiusYalms = radius });
        }
        ImGui.Spacing();
        var poll = settings.TrackingPollIntervalSeconds;
        if (Forms.NumericField(theme, "Stats poll interval (seconds)", ref poll, 30, 5, 3600)) SaveSettings(ReadSettings() with { TrackingPollIntervalSeconds = Math.Clamp(poll, 5, 3600) });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped($"Guest-count samples are recorded on a 5-minute grid, but never more often than this interval. Current: every {Math.Clamp(settings.TrackingPollIntervalSeconds, 5, 3600) / 60.0:F1} minute(s).");
        ImGui.PopStyleColor();
        UiKit.EndSectionCard();
    }

    private static void StatRow(VenueTheme theme, params (string Label, string Value)[] stats)
    {
        for (var i = 0; i < stats.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 8);
            UiKit.StatCard(theme, "users", stats[i].Value, stats[i].Label, theme.Tokens.Accent);
        }
    }
    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}m" : $"{duration.Minutes}m {duration.Seconds:D2}s";
    /// <summary>Config loading must be event-driven, not per-frame: <c>DrawLive</c>/<c>DrawHistory</c>/<c>DrawSettings</c>
    /// all call this every single frame they're on screen, so it must never itself call
    /// <see cref="VenueProfileService.GetModuleConfig{T}"/> more than once per venue — reloading a genuinely
    /// malformed persisted payload on every frame turned one bad payload into thousands of "payload is empty"
    /// Diagnostics warnings per minute instead of one. Reloads only when the active venue actually changed since the
    /// last read; <see cref="SaveSettings"/> updates the cache directly so an edit is reflected immediately without
    /// waiting on a redundant re-read of what was just written.</summary>
    private AttendanceSettings ReadSettings()
    {
        var venueId = venues.Current.Id;
        if (venueId != settingsVenueId)
        {
            settingsCache = venues.GetModuleConfig(venueId, "core.attendance", 1, () => new AttendanceSettings());
            settingsVenueId = venueId;
        }
        return settingsCache;
    }
    private void SaveSettings(AttendanceSettings settings)
    {
        var venueId = venues.Current.Id;
        venues.SaveModuleConfig(venueId, "core.attendance", 1, settings);
        settingsCache = settings; settingsVenueId = venueId;
    }
}

internal sealed class GreeterOperatorPanel(GreeterService greeter, VenueProfileService venues, AttendanceService attendance)
{
    private long? editingPresetId;
    private string editName = "", editLine1 = "", editLine2 = "", editLine3 = "", editCommand = "";
    private string presetSearch = "";
    private readonly ConfirmDialog confirmDialog = new();

    /// <summary>Live operation only: the five-slot DJ hotbar, active preset, and queue status. One click on a
    /// non-empty slot switches the active preset immediately; slot assignment and preset content editing both live
    /// in <see cref="DrawSettings"/> per the Settings-vs-operational split for this reconstruction.</summary>
    public void Draw()
    {
        var theme = venues.Current.Theme;
        var assignments = greeter.GetHotbarAssignments();

        UiKit.BeginSectionCard("greeter-hotbar", theme, "Greeting Presets");
        for (var slot = 1; slot <= 5; slot++)
        {
            var presetId = assignments.TryGetValue(slot, out var id) ? id : null;
            var name = presetId is long pid ? greeter.GetPreset(pid)?.Name ?? "(Missing)" : "(Empty)";
            if (slot > 1) ImGui.SameLine(0, 8);
            if (Hotbar.Slot(theme, $"dj-{slot}", $"DJ {slot}", name, presetId is not null && presetId == greeter.ActivePresetId, new Vector2(150, 56)) && presetId is not null)
            {
                greeter.SelectPreset(presetId);
                Save(greeter.Settings with { ActivePresetId = presetId });
            }
            if (presetId is null) UiKit.Tooltip("Assign a preset in Settings → Greeter → Hotbar Slot Assignments.");
        }
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        var autoGreetStatus = greeter.Settings.AutoGreetEnabled ? "Auto Greet ON" : "Auto Greet OFF";
        // Derived from Attendance's authoritative greeted state for the current opening — never a Greeter-owned
        // count — so this can never disagree with what Attendance's own Visitors tab shows (see
        // AttendanceService.IsGreeted's doc comment for the live bug this replaces: a stale, never-reset local
        // HashSet that could show a "greeted" count left over from an earlier opening in the same venue visit).
        var greetedCount = attendance.GetTonightVisitors().Count(v => v.Greeted);
        ImGui.TextUnformatted($"Active: {greeter.ActivePresetName} · {greeter.PendingCount} queued · {greetedCount} greeted this session · {autoGreetStatus}");
        ImGui.PopStyleColor();
        UiKit.EndSectionCard();
    }

    /// <summary>Persistent configuration: Auto Greet/timing, the five-slot hotbar's preset assignments, and the
    /// full saved-preset library (create/edit/delete any number of presets, independent of the five hotbar slots)
    /// — donor parity, replacing the previous reconstruction's fixed-five-presets simplification.</summary>
    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var settings = greeter.Settings;

        UiKit.BeginSectionCard("greeter-behavior", theme, "Behavior");
        var autoGreet = settings.AutoGreetEnabled;
        if (UiKit.Toggle(theme, "Auto Greet First Visit Tonight", ref autoGreet)) Save(greeter.Settings with { AutoGreetEnabled = autoGreet });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Automatically queues the active preset's greeting the first time each guest is seen this calendar night. Repeat arrivals the same night are never auto-greeted again; VIPs are always greeted on arrival regardless of this setting.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        var enabled = settings.Enabled;
        if (UiKit.Toggle(theme, "Greeter enabled", ref enabled)) Save(greeter.Settings with { Enabled = enabled });
        var delay = settings.GreetDelaySeconds;
        if (Forms.NumericField(theme, "Delay before first line (seconds)", ref delay, 1, 0, 20)) Save(greeter.Settings with { GreetDelaySeconds = delay });
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("greeter-hotbar-assign", theme, "Hotbar Slot Assignments");
        var library = greeter.GetPresetLibrary();
        var options = new List<string> { "(Empty)" }; options.AddRange(library.Select(x => x.Name));
        var assignments = greeter.GetHotbarAssignments();
        for (var slot = 1; slot <= 5; slot++)
        {
            ImGui.PushID(slot);
            var currentId = assignments.TryGetValue(slot, out var id) ? id : null;
            var index = 0;
            if (currentId is long cid) for (var i = 0; i < library.Count; i++) if (library[i].Id == cid) { index = i + 1; break; }
            if (Forms.ComboField(theme, $"DJ {slot}", options, ref index)) greeter.SetHotbarAssignment(slot, index == 0 ? null : library[index - 1].Id);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("greeter-library", theme, $"Saved Presets ({library.Count})");
        // Reserve the button's width first — SearchBox defaults to filling all available width, which would push
        // "New Preset" past the visible content region if drawn with SameLine() afterward (the same class of bug
        // documented in UI_STATUS.md's header/toolbar fixes: never chain SameLine() after a full-width field).
        const float newPresetButtonWidth = 120f;
        Forms.SearchBox(theme, "greeter-preset-search", ref presetSearch, "Search presets", ImGui.GetContentRegionAvail().X - newPresetButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "New Preset", new Vector2(newPresetButtonWidth, 0))) { editingPresetId = null; editName = editLine1 = editLine2 = editLine3 = editCommand = ""; }
        ImGui.Spacing();
        var filtered = library.Where(x => string.IsNullOrWhiteSpace(presetSearch) || x.Name.Contains(presetSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0) UiKit.EmptyState(theme, "No saved presets yet", "Use \"New Preset\" below to create one.");
        else foreach (var preset in filtered)
        {
            ImGui.PushID(preset.Id.ToString());
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted(preset.Name);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Load into editor", new Vector2(120, 0))) { editingPresetId = preset.Id; editName = preset.Name; editLine1 = preset.Line1; editLine2 = preset.Line2; editLine3 = preset.Line3; editCommand = preset.Command; }
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Delete", new Vector2(70, 0))) confirmDialog.Request("Delete preset?", $"Remove '{preset.Name}' and clear it from any hotbar slot. This cannot be undone.", () => { greeter.DeletePreset(preset.Id); if (editingPresetId == preset.Id) editingPresetId = null; });
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("greeter-editor", theme, editingPresetId is null ? "New Preset" : "Edit Preset");
        Forms.TextField(theme, "Preset name", ref editName, 64);
        Forms.TextField(theme, "Greeting line 1", ref editLine1, 450, "Welcome <name>!");
        Forms.TextField(theme, "Greeting line 2 (optional)", ref editLine2, 450);
        Forms.TextField(theme, "Greeting line 3 (optional)", ref editLine3, 450);
        Forms.TextField(theme, "Command after greeting (optional)", ref editCommand, 450, "e.g. /micon emote");
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("<name> is replaced with the guest's character name. Lines 1-3 are sent as separate /tell messages in order; the command runs last and is not a fourth tell.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, editingPresetId is null ? "Save New Preset" : "Update Preset") && !string.IsNullOrWhiteSpace(editName))
            editingPresetId = greeter.SavePreset(editingPresetId, editName, editLine1, editLine2, editLine3, editCommand);
        UiKit.EndSectionCard();

        confirmDialog.Draw(theme);
    }

    private void Save(GreeterSettings settings) => venues.SaveModuleConfig(venues.Current.Id, "core.greeter", 1, settings);
}

internal sealed class VipOperatorPanel(VipOrchestrationService vip, VenueProfileService venues, ITargetedPlayerProvider? targetProvider = null)
{
    private string search = "";
    private readonly ConfirmDialog confirmDialog = new();
    private readonly VipEditDialog editDialog = new();

    /// <summary>Live operation only: the searchable roster. Add/Edit open <see cref="VipEditDialog"/>, which owns
    /// every piece of per-VIP communication (custom private tell, public announcement) — there is no venue-wide
    /// recognition template anymore (see <see cref="DrawSettings"/>'s doc comment for why).</summary>
    public void Draw()
    {
        var theme = venues.Current.Theme;
        var settings = vip.Settings;

        UiKit.BeginSectionCard("vip-list", theme, $"VIPs ({vip.EnabledCount} enabled of {settings.Records.Count})");
        // Reserve "+ Add VIP"'s width first — SearchBox defaults to filling all available width, which pushed this
        // button past the visible content region when drawn with SameLine() afterward. This was the actual root
        // cause of the reported "no visible + Add VIP control" bug (the button existed and worked; it just rendered
        // off the right edge of the card). Same fix applied to Greeter's "New Preset" button.
        const float addVipButtonWidth = 110f;
        Forms.SearchBox(theme, "vip-search", ref search, "Search VIPs", ImGui.GetContentRegionAvail().X - addVipButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "+ Add VIP", new Vector2(addVipButtonWidth, 0))) editDialog.OpenForAdd();
        ImGui.Spacing();
        var records = settings.Records.Where(r => string.IsNullOrWhiteSpace(search) || r.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (records.Length == 0) UiKit.EmptyState(theme, "No VIPs yet", "Use \"+ Add VIP\" to enable entrance recognition.");
        foreach (var record in records)
        {
            ImGui.PushID(record.Key);
            UiKit.StatusBadge(theme, record.Enabled ? "ON" : "OFF", record.Enabled ? ToastLevel.Success : ToastLevel.Information);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted($"{record.CharacterName} @ {record.HomeWorld}");
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(record.CustomTell) ? "(no private tell)" : $"Tell: {record.CustomTell}");
            ImGui.TextWrapped($"{record.Channel} · {(string.IsNullOrWhiteSpace(record.PublicAnnouncement) ? "(no public announcement)" : record.PublicAnnouncement)}");
            ImGui.PopStyleColor();
            if (UiKit.GhostButton(theme, "Edit", new Vector2(70, 0))) editDialog.OpenForEdit(record);
            ImGui.SameLine();
            var disableLabel = record.Enabled ? "Disable" : "Enable";
            if (UiKit.GhostButton(theme, disableLabel, new Vector2(70, 0))) ReplaceOne(record, record with { Enabled = !record.Enabled });
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Remove", new Vector2(70, 0))) confirmDialog.Request("Remove VIP?", $"Permanently remove {record.CharacterName} @ {record.HomeWorld} from the VIP roster. This cannot be undone.", () => Replace(settings.Records.Where(x => x.Key != record.Key).ToList()));
            ImGui.Spacing(); UiKit.Divider(theme); ImGui.Spacing();
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        editDialog.Draw(theme, targetProvider, record => Replace([.. vip.Settings.Records, record]), (originalKey, record) => Replace(vip.Settings.Records.Select(x => x.Key == originalKey ? record : x).ToList()));
        confirmDialog.Draw(theme);
    }

    /// <summary>Live-verified correction: VIP recognition used to be one venue-wide tell template shared by every
    /// VIP; the operator wanted a distinct private message per VIP instead, so that template is gone — each VIP's
    /// own custom tell and public message are edited on their own roster record in the operational app (see
    /// <see cref="Draw"/>/<see cref="VipEditDialog"/>), not here. Nothing venue-wide is left to configure for this
    /// module, so Settings only says so, rather than leaving a stale/disabled control behind.</summary>
    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        UiKit.BeginSectionCard("vip-settings", theme, "VIP Settings");
        UiKit.EmptyState(theme, "Nothing to configure here", "Each VIP's private tell and public announcement are set on their own record in the VIP app.");
        UiKit.EndSectionCard();
    }

    private void ReplaceOne(VipRecord original, VipRecord updated) => Replace(vip.Settings.Records.Select(x => x.Key == original.Key ? updated : x).ToList());
    private void Replace(List<VipRecord> records) { var updated = vip.Settings with { Records = records }; vip.Configure(updated); venues.SaveModuleConfig(venues.Current.Id, "core.vip", 1, updated); }
}

/// <summary>Single-instance Add/Edit VIP modal — one field-set for both flows, distinguished by whether an original
/// record (and its original <see cref="VipRecord.Key"/>, tracked separately so an identity edit doesn't orphan the
/// record) was supplied. Kept local to VIP rather than generalized into the shared UI kit: a multi-field record
/// editor isn't yet a pattern any other module needs (see <c>NEW_MODULE_GUIDE.md</c> §15's guidance on when to
/// extend the shared kit vs. keep something local).</summary>
internal sealed class VipEditDialog
{
    private const string PopupId = "VIP##venueos-vip-edit-dialog";
    private static readonly string[] ChannelLabels = ["Shout", "Yell"];
    private bool openRequested; private bool windowOpen = true; private bool isEditing;
    private string? originalKey;
    private string name = "", world = "", customTell = "", announcement = "", notes = ""; private bool enabled = true; private int channelIndex;
    private string? targetFillError;

    public void OpenForAdd() { isEditing = false; originalKey = null; name = world = customTell = announcement = notes = ""; enabled = true; channelIndex = 0; targetFillError = null; openRequested = true; windowOpen = true; }
    public void OpenForEdit(VipRecord record)
    {
        isEditing = true; originalKey = record.Key; name = record.CharacterName; world = record.HomeWorld; customTell = record.CustomTell; announcement = record.PublicAnnouncement; notes = record.Notes ?? ""; enabled = record.Enabled; channelIndex = record.Channel == VipAnnouncementChannel.Yell ? 1 : 0; targetFillError = null; openRequested = true; windowOpen = true;
    }

    /// <summary><see cref="ITargetedPlayerProvider.GetTargetedPlayer"/>'s result only ever touches
    /// <see cref="name"/>/<see cref="world"/> — never <see cref="customTell"/>/<see cref="announcement"/>, so
    /// "Use Current Target" never clobbers a custom message already typed while editing an existing VIP.</summary>
    public void Draw(VenueTheme theme, ITargetedPlayerProvider? targetProvider, Action<VipRecord> onAdd, Action<string, VipRecord> onEdit)
    {
        if (openRequested) { ImGui.OpenPopup(PopupId); openRequested = false; }
        ImGui.SetNextWindowSize(new Vector2(420, 0));
        if (!ImGui.BeginPopupModal(PopupId, ref windowOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextUnformatted(isEditing ? "Edit VIP" : "Add VIP");
        UiKit.Divider(theme);

        if (UiKit.GhostButton(theme, "Use Current Target"))
        {
            if (targetProvider is null) targetFillError = "Target lookup is not available.";
            else
            {
                var result = targetProvider.GetTargetedPlayer();
                if (result.Success) { name = result.Name; world = result.HomeWorld; targetFillError = null; } else targetFillError = result.Error;
            }
        }
        UiKit.Tooltip("Fill Character Name and Home World from whatever you currently have targeted. Other fields are left untouched.");
        if (targetFillError is not null) UiKit.WarningState(theme, targetFillError);
        ImGui.Spacing();

        Forms.TextField(theme, "Character name", ref name, 64);
        Forms.TextField(theme, "Home world", ref world, 64);
        UiKit.Toggle(theme, "Enabled", ref enabled);
        ImGui.Spacing();
        Forms.MultilineField(theme, "Custom private tell", ref customTell, 450, 60);
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Sent as a private /tell to this VIP on arrival, before the normal Greeter message. <name> is replaced with their character name. Leave empty to skip the private tell for this VIP entirely.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        Forms.FieldLabel(theme, "Public entrance announcement channel");
        Forms.Segmented(theme, "vip-edit-channel", ChannelLabels, ref channelIndex);
        Forms.TextField(theme, "Public entrance announcement", ref announcement, 450, "Welcome back, <name>!");
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Leave empty to skip the public announcement for this VIP.");
        ImGui.PopStyleColor();
        Forms.TextField(theme, "Notes", ref notes, 256);
        ImGui.Spacing();

        var canSave = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world);
        if (UiKit.PrimaryButton(theme, isEditing ? "Save" : "Add") && canSave)
        {
            var record = new VipRecord(name.Trim(), world.Trim(), enabled, customTell, announcement, channelIndex == 1 ? VipAnnouncementChannel.Yell : VipAnnouncementChannel.Shout, notes);
            if (isEditing && originalKey is not null) onEdit(originalKey, record); else onAdd(record);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Cancel")) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}

internal sealed class RaffleOperatorPanel(VenueRaffleService raffle, VenueProfileService venues)
{
    private string name = "";

    public void Draw()
    {
        var theme = venues.Current.Theme;
        UiKit.BeginSectionCard("raffle-create", theme, "Raffle");
        Forms.TextField(theme, "New raffle name", ref name, 128, "e.g. Weekend giveaway");
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Create raffle") && !string.IsNullOrWhiteSpace(name)) { raffle.Create(name); name = ""; }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("raffle-list", theme, $"Raffles ({raffle.Dashboard.RaffleCount})");
        if (raffle.Settings.Raffles.Count == 0) UiKit.EmptyState(theme, "No raffles yet", "Create a raffle above to get started.");
        foreach (var item in raffle.Settings.Raffles)
        {
            ImGui.PushID(item.Id);
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary)); ImGui.TextUnformatted(item.Name); ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary)); ImGui.TextUnformatted($"· {item.TotalTickets} ticket(s)"); ImGui.PopStyleColor();
            if (!string.IsNullOrWhiteSpace(item.WinnerName)) { ImGui.SameLine(); UiKit.StatusBadge(theme, $"Winner: {item.WinnerName}", ToastLevel.Success); }
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }
}
