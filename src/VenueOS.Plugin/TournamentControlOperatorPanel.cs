using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

internal sealed class TournamentControlOperatorPanel(TournamentControlService service, VenueProfileService venues)
{
    private string tournamentId = "";

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings; var connection = settings.Connection; var dashboard = service.Dashboard;

        UiKit.BeginSectionCard("tournament-status", theme, "TournamentControl");
        UiKit.ConnectionBadge(theme, dashboard.IsAuthenticated ? "Organizer session active" : "Not authenticated", dashboard.IsAuthenticated);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("tournament-connection", theme, "Connection");
        var endpoint = connection.BaseUrl; if (Forms.TextField(theme, "Endpoint", ref endpoint, 256, "https://")) Save(settings with { Connection = connection with { BaseUrl = endpoint } });
        var password = connection.ServerAccessPassword ?? ""; if (Forms.TextField(theme, "Server password", ref password, 256, password: true)) Save(service.Settings with { Connection = service.Settings.Connection with { ServerAccessPassword = password } });
        var userKey = service.Settings.Connection.UserKey ?? ""; if (Forms.TextField(theme, "Organizer key", ref userKey, 256, password: true)) Save(service.Settings with { Connection = service.Settings.Connection with { UserKey = userKey } });
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Authenticate")) service.AuthenticateAsync().GetAwaiter().GetResult();
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("tournament-load", theme, "Tournament");
        Forms.TextField(theme, "Tournament ID", ref tournamentId, 128);
        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Load state") && !string.IsNullOrWhiteSpace(tournamentId)) service.LoadStateAsync(tournamentId).GetAwaiter().GetResult();
        ImGui.Spacing();
        if (service.Current is null) UiKit.EmptyState(theme, "No tournament loaded", "Load a tournament by ID to begin.");
        else
        {
            UiKit.StatusBadge(theme, dashboard.TournamentName ?? "Unnamed tournament", ToastLevel.Information); ImGui.SameLine();
            UiKit.StatusBadge(theme, dashboard.State ?? "Unknown state", ToastLevel.Success); ImGui.SameLine();
            UiKit.StatusBadge(theme, $"Revision {dashboard.Revision?.ToString() ?? "—"}", ToastLevel.Information);
            if (!string.IsNullOrWhiteSpace(dashboard.Notice)) { ImGui.Spacing(); UiKit.WarningState(theme, dashboard.Notice); }
            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, "Start")) service.StartAsync().GetAwaiter().GetResult();
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Call first ready match"))
            {
                var match = service.Current.Matches.FirstOrDefault(x => x.Status == "READY");
                if (match is not null) { var first = service.Current.Contestants.FirstOrDefault(x => x.Id == match.Player1Id)?.DisplayName ?? ""; var second = service.Current.Contestants.FirstOrDefault(x => x.Id == match.Player2Id)?.DisplayName ?? ""; service.SendMatchCallout(match.Id, first, second); }
            }
        }
        UiKit.EndSectionCard();
    }
    private void Save(TournamentModuleSettings settings) => service.Configure(settings);
}
