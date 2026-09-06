using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

internal sealed class VenueBingoOperatorPanel(VenueBingoService service, VenueProfileService venues)
{
    private int number;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings; var connection = settings.Connection; var state = service.Dashboard;

        UiKit.BeginSectionCard("bingo-status", theme, "Bingo");
        UiKit.ConnectionBadge(theme, state.IsConnected ? "Connected" : "Not connected", state.IsConnected);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("bingo-connection", theme, "Connection");
        var server = connection.ServerUrl; if (Forms.TextField(theme, "Server URL", ref server, 256, "https://")) Save(settings with { Connection = connection with { ServerUrl = server } });
        var room = service.Settings.RoomCode; if (Forms.TextField(theme, "Room code", ref room, 64)) Save(service.Settings with { RoomCode = room });
        var roomKey = service.Settings.Connection.RoomKey ?? ""; if (Forms.TextField(theme, "Room key", ref roomKey, 256, password: true)) Save(service.Settings with { Connection = service.Settings.Connection with { RoomKey = roomKey } });
        var adminKey = service.Settings.Connection.AdminKey ?? ""; if (Forms.TextField(theme, "Admin key", ref adminKey, 256, password: true)) Save(service.Settings with { Connection = service.Settings.Connection with { AdminKey = adminKey } });
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Host sync")) service.SyncAsync().GetAwaiter().GetResult();
        ImGui.SameLine(); if (UiKit.GhostButton(theme, "Refresh room")) service.PollAsync().GetAwaiter().GetResult();
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("bingo-room", theme, "Room");
        UiKit.StatusBadge(theme, $"{state.PlayerCount} players", ToastLevel.Information); ImGui.SameLine(); UiKit.StatusBadge(theme, $"{state.NumbersCalled} called", ToastLevel.Information);
        ImGui.Spacing();
        Forms.NumericField(theme, "Number", ref number, 1, 1, 75);
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Call") && number is >= 1 and <= 75) service.CallNumberAsync(number).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(state.Status)) { ImGui.Spacing(); UiKit.WarningState(theme, state.Status); }
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Automatic trade and payout automation are intentionally not included in VenueOS.");
        ImGui.PopStyleColor();
        UiKit.EndSectionCard();
    }
    private void Save(VenueBingoSettings settings) => service.Configure(settings);
}
