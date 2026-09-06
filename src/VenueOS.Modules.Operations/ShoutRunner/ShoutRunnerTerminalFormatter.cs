using System.Globalization;
using System.Text;

namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>Converts the structured <see cref="ShoutRunnerTerminalEvent"/> history into clean plain text suitable
/// for pasting into Discord, Notepad, or a staff/venue-management log — the "Copy Terminal" feature. Deliberately
/// generated from the structured event model (never by scraping rendered ImGui text), and deliberately pure/ImGui-free
/// so it's fully unit-testable.
///
/// Every line carries its own timestamp and RUN number — the same information already shown per-line in the live
/// terminal (<c>ShoutRunnerOperatorPanel.DrawTerminal</c>'s <c>"[{time}] RUN {n} — {text}"</c> format) — so nothing
/// is lost relative to what the operator already sees on screen; only the color/severity styling is dropped, since
/// plain text has no equivalent. Hierarchy (Data Center → World → Destination) is conveyed the same way the live
/// terminal conveys it visually: two-space indentation per level, plus a blank line whenever the immediate
/// Data-Center/World/Destination context changes, so a pasted log still reads as distinct grouped sections rather
/// than one undifferentiated wall of lines.</summary>
public static class ShoutRunnerTerminalFormatter
{
    public static string ToPlainText(IReadOnlyList<ShoutRunnerTerminalEvent> events)
    {
        if (events.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        (string? DataCenter, string? World, string? Destination)? previousContext = null;

        foreach (var entry in events)
        {
            var context = (entry.DataCenter, entry.World, entry.Destination);
            if (previousContext is not null && previousContext.Value != context) builder.AppendLine();
            previousContext = context;

            var level = entry.Destination is not null ? 3 : entry.World is not null ? 2 : entry.DataCenter is not null ? 1 : 0;
            if (level > 0) builder.Append(' ', level * 2);

            builder.Append('[').Append(entry.At.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.InvariantCulture)).Append("] ");
            builder.Append("RUN ").Append(entry.RunNumber).Append(" — ").Append(entry.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
