using Dalamud.Bindings.ImGui;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>General holds venue-independent, application-wide VenueOS behavior. Per §35 of the Phase 3D brief, pure
/// status information (module counts, error counts, active venue) is diagnostic/about data, not a "setting" — it
/// lives on Home and in Diagnostics, not duplicated here. This page now has its first genuine global preference
/// (auto pop-out); it will grow further only as more real global options exist, never with invented filler.</summary>
internal sealed class GeneralSettingsPage(DiagnosticsService diagnostics, GlobalSettingsService globalSettings)
{
    public void Draw(VenueTheme theme)
    {
        AppFrame.Draw(theme, "gear", "General", "System-wide VenueOS information and behavior.", () =>
        {
            UiKit.BeginSectionCard("general-window-behavior", theme, "Window & Module Behavior");
            var autoPopOut = globalSettings.AutoPopOutModules;
            if (UiKit.Toggle(theme, "Open modules in separate windows", ref autoPopOut)) globalSettings.SetAutoPopOutModules(autoPopOut);
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped("When enabled, selecting a module from the Home screen opens or focuses its detached window instead of opening it inside the main VenueOS tablet. This preference is global — it applies the same way for every venue and does not change when you switch venues.");
            ImGui.PopStyleColor();
            UiKit.EndSectionCard();

            ImGui.Spacing(); ImGui.Spacing();

            UiKit.BeginSectionCard("general-about", theme, "About VenueOS");
            var snapshot = diagnostics.Capture();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted($"VenueOS {snapshot.VenueOsVersion}");
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped("A tablet-style operations console for running an FFXIV venue: attendance, greeting, VIP recognition, promotion, and backend-compatible games, unified under one per-venue theme.");
            ImGui.PopStyleColor();
            UiKit.EndSectionCard();

            ImGui.Spacing(); ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextWrapped("Module counts, recent errors, and per-module diagnostics live in Settings → Diagnostics, not here.");
            ImGui.PopStyleColor();
        });
    }
}
