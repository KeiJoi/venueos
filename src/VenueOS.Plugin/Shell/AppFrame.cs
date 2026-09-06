using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>The consistent per-application frame every module (and Settings) renders inside: an app-identifying
/// top bar over a flexible, independently scrollable content region. The frame imposes no internal layout on its
/// content so future operator consoles (tables, queues, multi-column controls) can grow without fighting it.</summary>
internal static class AppFrame
{
    public static void Draw(VenueTheme theme, string iconKey, string appName, string description, Action content, Action? headerActions = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        var iconStart = ImGui.GetCursorScreenPos();
        AppIcons.Draw(iconKey, iconStart + new Vector2(12, 12), 16, UiKit.ColorU32(theme.Tokens.Accent));
        ImGui.Dummy(new Vector2(24, 24));
        ImGui.SameLine();
        ImGui.TextUnformatted(appName);
        ImGui.PopStyleColor();
        if (headerActions is not null) { ImGui.SameLine(ImGui.GetContentRegionAvail().X - 34 + ImGui.GetCursorPosX()); headerActions(); }
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped(description);
            ImGui.PopStyleColor();
        }
        UiKit.Divider(theme);
        ImGui.Spacing();
        ImGui.BeginChild("app-content", ImGui.GetContentRegionAvail(), false);
        content();
        ImGui.EndChild();
    }

    /// <summary>Embeds a module inside the tablet frame, with a pop-out control that opens the exact same
    /// <c>module.Draw()</c> in an independent window via <see cref="ModuleWindowManager"/>, and the same failure
    /// isolation detached windows get, so one broken module screen can't take the whole tablet down.</summary>
    public static void DrawModule(VenueTheme theme, IVenueModule module, ModuleWindowManager windowManager, DiagnosticsService diagnostics) =>
        Draw(theme, module.Descriptor.Icon, module.Descriptor.DisplayName, module.Descriptor.Description,
            () => UiKit.SafeDraw(theme, diagnostics, module.Descriptor.Id, module.Draw),
            () =>
            {
                if (UiKit.IconButton(theme, $"popout-{module.Descriptor.Id}", "popout", 28, "Open in separate window", windowManager.IsOpen(module.Descriptor.Id)))
                    windowManager.Open(module.Descriptor.Id);
            });
}
