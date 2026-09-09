using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>The Home screen's overview card. Shows a compact status glance only — the full, redacted error list
/// lives in Settings → Diagnostics so loose error text never collides with the app grid below this card.</summary>
internal sealed class VenueOperationsDashboard(VenueProfileService venues, ModuleHost modules, AttendanceService attendance, GreeterService greeter, VipOrchestrationService vip, ShoutRunnerService shoutRunner, VenueRaffleService raffle, MairsTriviaService trivia, TournamentControlService tournament, VenueBingoService bingo, DiagnosticsService diagnostics)
{
    public void Draw()
    {
        var theme = venues.Current.Theme;
        var isSessionActive = attendance.CurrentSessionId is not null;
        UiKit.StatusBadge(theme, isSessionActive ? "Opening active" : "Closed", isSessionActive ? ToastLevel.Success : ToastLevel.Information);
        ImGui.SameLine();
        UiKit.StatusBadge(theme, $"{attendance.Guests.Count} guests", ToastLevel.Information);
        ImGui.Spacing();

        Badge(theme, "Greeter", greeter.IsEnabled ? $"{greeter.ActivePresetName} · {greeter.PendingCount} queued" : "disabled", greeter.IsEnabled);
        Badge(theme, "VIP", vip.IsEnabled ? $"{vip.EnabledCount} enabled" : "disabled", vip.IsEnabled);
        var shoutRunnerActive = shoutRunner.State is not (ShoutRunnerState.Stopped or ShoutRunnerState.Faulted);
        Badge(theme, "ShoutRunner", shoutRunnerActive ? $"RUN {shoutRunner.RunNumber} — {shoutRunner.State}" : shoutRunner.State == ShoutRunnerState.Faulted ? "faulted" : "idle", shoutRunnerActive);
        ImGui.Spacing();

        Badge(theme, "Bingo", Bingo(bingo.Dashboard), bingo.Dashboard.IsConnected);
        Badge(theme, "Raffle", $"{raffle.Dashboard.ActiveRaffleCount} local raffle(s)", raffle.Dashboard.ActiveRaffleCount > 0);
        Badge(theme, "Mair's Trivia", trivia.Dashboard.IsAuthenticated ? trivia.Dashboard.GameState ?? "connected" : trivia.Dashboard.IsConfigured ? "sign-in required" : "not configured", trivia.Dashboard.IsAuthenticated);
        Badge(theme, "Brackets", tournament.Dashboard.IsAuthenticated ? tournament.Dashboard.State ?? "connected" : tournament.Dashboard.IsConfigured ? "sign-in required" : "not configured", tournament.Dashboard.IsAuthenticated);

        ImGui.Spacing();
        var errorCount = diagnostics.Capture().RecentErrors.Count;
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted($"Enabled modules: {modules.Modules.Count(x => x.IsEnabled)} / {modules.Modules.Count}");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        UiKit.StatusBadge(theme, errorCount == 0 ? "All systems normal" : $"{errorCount} recent error(s) — see Settings", errorCount == 0 ? ToastLevel.Success : ToastLevel.Error);
    }

    private static void Badge(VenueTheme theme, string label, string value, bool healthy)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.SameLine(160);
        UiKit.StatusBadge(theme, value, healthy ? ToastLevel.Success : ToastLevel.Information);
    }

    private static string Bingo(BingoDashboard status) => status.IsConnected ? $"connected · {status.PlayerCount} players · {status.NumbersCalled} called" : status.IsConfigured ? "connecting/offline" : "not configured";
}
