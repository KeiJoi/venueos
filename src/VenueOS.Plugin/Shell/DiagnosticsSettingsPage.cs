using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Diagnostics, rebuilt to look like a real log console instead of loose colored text — but every row is
/// real data. There is no "Information" level here: <see cref="DiagnosticsService"/> only ever records warnings
/// (config recovery) and errors (module/UI failures), so a fabricated INFO stream was deliberately not added just
/// to visually match a log-console mockup. The "Module" filter is a best-effort substring match against each
/// entry's message (module IDs already appear in most failure messages) since no structured per-entry module
/// attribution exists yet — honest about being approximate rather than pretending to be exact.</summary>
internal sealed class DiagnosticsSettingsPage(ModuleHost modules, DiagnosticsService diagnostics)
{
    private int levelFilter; // 0 = All, 1 = Errors, 2 = Warnings
    private int moduleFilterIndex; // 0 = All Modules
    private string search = "";
    private static readonly string[] LevelOptions = ["All Levels", "Errors", "Warnings"];

    public void Draw(VenueTheme theme)
    {
        AppFrame.Draw(theme, "terminal", "Diagnostics", "View logs, monitor module status, and troubleshoot issues.", () => DrawContent(theme));
    }

    private void DrawContent(VenueTheme theme)
    {
        var snapshot = diagnostics.Capture();
        var entries = BuildEntries(snapshot);

        DrawToolbar(theme, entries);
        ImGui.Spacing();

        var filtered = ApplyFilters(entries).ToArray();
        UiKit.BeginCard("diagnostics-log", theme, new Vector2(0, 300));
        if (filtered.Length == 0) UiKit.EmptyState(theme, "No matching entries", "Nothing to show for the current filters.");
        else foreach (var entry in filtered) DrawEntry(theme, entry);
        UiKit.EndCard();

        ImGui.Spacing(); ImGui.Spacing();
        DrawStats(theme, snapshot, entries);
    }

    /// <summary>Built the same "reserve regions first, position everything in absolute screen space" way
    /// <see cref="TabletHeader"/> is — the previous version mixed <c>Forms.SearchBox</c> (a single-line field) at
    /// the same Y as <c>Forms.ComboField</c>'s label row (two lines: label, then field) and then let normal
    /// auto-flow place Clear/Copy. Since auto-flow tracks only the last item's own height, not the taller combo
    /// columns beside it, Clear/Copy ended up positioned against the label row's height and rendered on top of the
    /// Module combo's actual field. Every element here is now placed with an explicit absolute Y, so that can't
    /// recur regardless of how tall any one column is.</summary>
    private void DrawToolbar(VenueTheme theme, IReadOnlyList<Entry> entries)
    {
        var moduleOptions = new[] { "All Modules" }.Concat(modules.Modules.Select(x => x.Descriptor.DisplayName)).ToArray();

        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        const float logLevelWidth = 120f, moduleWidth = 170f, clearWidth = 64f, copyWidth = 64f, minSearchWidth = 120f;

        // RIGHT ACTION REGION — reserved first, exactly like the main toolbar's fix.
        var rightWidth = clearWidth + gap + copyWidth;
        var rightStartX = start.X + width - rightWidth;

        // LEFT FILTER REGION — fixed width; Log Level and Module are never dropped or shrunk.
        var searchMinX = start.X + logLevelWidth + gap + moduleWidth + gap;
        var searchMaxX = rightStartX - gap;
        var singleRow = searchMaxX - searchMinX >= minSearchWidth;

        var labelHeight = ImGui.GetTextLineHeightWithSpacing();
        var fieldHeight = ImGui.GetFrameHeight();
        var fieldRowY = start.Y + labelHeight;

        ImGui.SetCursorScreenPos(start);
        Forms.ComboField(theme, "Log Level", LevelOptions, ref levelFilter, logLevelWidth);
        ImGui.SetCursorScreenPos(new Vector2(start.X + logLevelWidth + gap, start.Y));
        Forms.ComboField(theme, "Module", moduleOptions, ref moduleFilterIndex, moduleWidth);

        float endY;
        if (singleRow)
        {
            ImGui.SetCursorScreenPos(new Vector2(searchMinX, start.Y));
            Forms.FieldLabel(theme, "Search");
            ImGui.SetCursorScreenPos(new Vector2(searchMinX, fieldRowY));
            Forms.SearchBox(theme, "diagnostics-search", ref search, "Search logs...", searchMaxX - searchMinX);

            DrawActions(theme, entries, rightStartX, fieldRowY, clearWidth, copyWidth, gap);
            endY = fieldRowY + fieldHeight;
        }
        else
        {
            // Not enough room for Search beside the filters — reflow to a second row rather than overlap.
            DrawActions(theme, entries, rightStartX, fieldRowY, clearWidth, copyWidth, gap);

            var secondRowLabelY = fieldRowY + fieldHeight + gap;
            ImGui.SetCursorScreenPos(new Vector2(start.X, secondRowLabelY));
            Forms.FieldLabel(theme, "Search");
            var secondRowFieldY = secondRowLabelY + labelHeight;
            ImGui.SetCursorScreenPos(new Vector2(start.X, secondRowFieldY));
            Forms.SearchBox(theme, "diagnostics-search", ref search, "Search logs...", width);
            endY = secondRowFieldY + fieldHeight;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, endY + gap));
    }

    private void DrawActions(VenueTheme theme, IReadOnlyList<Entry> entries, float rightStartX, float fieldRowY, float clearWidth, float copyWidth, float gap)
    {
        ImGui.SetCursorScreenPos(new Vector2(rightStartX, fieldRowY));
        if (UiKit.GhostButton(theme, "Clear", new Vector2(clearWidth, 0))) diagnostics.Clear();
        ImGui.SetCursorScreenPos(new Vector2(rightStartX + clearWidth + gap, fieldRowY));
        if (UiKit.GhostButton(theme, "Copy", new Vector2(copyWidth, 0))) ImGui.SetClipboardText(string.Join('\n', ApplyFilters(entries).Select(Format)));
    }

    private void DrawStats(VenueTheme theme, DiagnosticSnapshot snapshot, IReadOnlyList<Entry> entries)
    {
        var lastUpdate = entries.Where(e => e.At is not null).Select(e => e.At!.Value).DefaultIfEmpty().Max();
        UiKit.StatCard(theme, "terminal", (snapshot.RecentErrors.Count + snapshot.RecoveryWarnings.Count).ToString(), "Total Entries", theme.Tokens.Accent);
        ImGui.SameLine();
        UiKit.StatCard(theme, "close", snapshot.RecentErrors.Count.ToString(), "Errors", theme.Tokens.Error);
        ImGui.SameLine();
        UiKit.StatCard(theme, "circle-question", snapshot.RecoveryWarnings.Count.ToString(), "Warnings", theme.Tokens.Warning);
        ImGui.SameLine();
        UiKit.StatCard(theme, "terminal", lastUpdate == default ? "—" : lastUpdate.LocalDateTime.ToString("t"), "Last Update", theme.Tokens.TextSecondary);
    }

    private void DrawEntry(VenueTheme theme, Entry entry)
    {
        UiKit.StatusBadge(theme, entry.Level, entry.Level == "Error" ? ToastLevel.Error : ToastLevel.Warning);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(entry.At is null ? "—" : entry.At.Value.LocalDateTime.ToString("HH:mm:ss"));
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(120, ImGui.GetContentRegionAvail().X - 8));
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(entry.Message);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.Spacing(); UiKit.Divider(theme); ImGui.Spacing();
    }

    private List<Entry> BuildEntries(DiagnosticSnapshot snapshot)
    {
        var entries = new List<Entry>(snapshot.RecentErrors.Count + snapshot.RecoveryWarnings.Count);
        entries.AddRange(snapshot.RecentErrors.Select(e => new Entry("Error", e.Message, e.At)));
        entries.AddRange(snapshot.RecoveryWarnings.Select(w => new Entry("Warning", w, null)));
        return [.. entries.OrderByDescending(e => e.At ?? DateTimeOffset.MinValue)];
    }

    private IEnumerable<Entry> ApplyFilters(IReadOnlyList<Entry> entries)
    {
        var moduleOptions = new[] { "All Modules" }.Concat(modules.Modules.Select(x => x.Descriptor.DisplayName)).ToArray();
        var selectedModule = moduleFilterIndex > 0 && moduleFilterIndex < modules.Modules.Count + 1 ? modules.Modules[moduleFilterIndex - 1] : null;
        return entries
            .Where(e => levelFilter == 0 || (levelFilter == 1 ? e.Level == "Error" : e.Level == "Warning"))
            .Where(e => selectedModule is null || e.Message.Contains(selectedModule.Descriptor.Id, StringComparison.OrdinalIgnoreCase) || e.Message.Contains(selectedModule.Descriptor.DisplayName, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(search) || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string Format(Entry entry) => $"[{(entry.At is null ? "—" : entry.At.Value.LocalDateTime.ToString("HH:mm:ss"))}] {entry.Level.ToUpperInvariant()} {entry.Message}";

    private readonly record struct Entry(string Level, string Message, DateTimeOffset? At);
}
