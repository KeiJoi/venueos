using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.UI;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>The tablet home screen: a compact overview card carrying forward every status line the original flat
/// dashboard showed, and a responsive grid of application icons. Venue identity is already persistent in
/// <see cref="TabletHeader"/> — Home does not repeat it in a large banner, so this space goes to actual content.</summary>
internal static class HomeScreen
{
    private const float Spacing = 16f;

    public static void Draw(VenueTheme theme, ModuleHost modules, VenueShell shell, DiagnosticsService diagnostics, GlobalSettingsService globalSettings, ModuleWindowManager windowManager, Action drawOverview)
    {
        UiKit.BeginCard("home-overview", theme, new Vector2(0, 260));
        drawOverview();
        UiKit.EndCard();
        ImGui.Spacing(); ImGui.Spacing();

        UiKit.SectionHeader(theme, "Applications");
        ImGui.Spacing();
        DrawGrid(theme, modules, shell, diagnostics, globalSettings, windowManager);
    }

    private static void DrawGrid(VenueTheme theme, ModuleHost modules, VenueShell shell, DiagnosticsService diagnostics, GlobalSettingsService globalSettings, ModuleWindowManager windowManager)
    {
        var errors = diagnostics.Capture().RecentErrors;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((availableWidth + Spacing) / (AppTile.Width + Spacing)));
        var column = 0;
        // A disabled module's tile is omitted entirely, not merely dimmed — this is the documented contract
        // (Settings → Modules' own banner already promises "Disabled modules will not appear on the Home screen")
        // and is what lets an unfinished, default-disabled module (Raffle/TournamentControl) stay invisible to a
        // fresh install while still being reachable and clearly marked under Settings → Modules.
        foreach (var module in modules.Modules.Where(x => x.IsEnabled))
        {
            if (column > 0) ImGui.SameLine(0, Spacing);
            var hasBadge = errors.Any(x => x.Message.Contains(module.Descriptor.Id, StringComparison.OrdinalIgnoreCase));
            if (AppTile.Draw(theme, module.Descriptor.Id, module.Descriptor.Icon, module.Descriptor.DisplayName, module.IsEnabled, hasBadge)) LaunchModule(module, shell, globalSettings, windowManager);
            UiKit.Tooltip(module.Descriptor.Description);
            column = (column + 1) % columns;
            if (column == 0) ImGui.Dummy(new Vector2(0, Spacing));
        }
        // User Manual sits immediately before Settings — both are fixed, non-module tiles outside the modules.Modules
        // loop above, following the same one-off AppTile.Draw pattern Settings itself already uses.
        if (column > 0) ImGui.SameLine(0, Spacing);
        if (AppTile.Draw(theme, "__manual__", "book", "User Manual")) shell.SelectManual();
        column = (column + 1) % columns;
        if (column == 0) ImGui.Dummy(new Vector2(0, Spacing));
        if (column > 0) ImGui.SameLine(0, Spacing);
        if (AppTile.Draw(theme, "__settings__", "gear", "Settings")) shell.SelectSettings();
    }

    /// <summary>The one place a module launch from Home is routed embedded vs. detached — every module funnels
    /// through <see cref="ModuleLaunchRouting.Resolve"/>, so a future module inherits the Auto Pop-Out preference
    /// automatically instead of needing its own branch here. Settings is a separate tile/code path above and is
    /// never affected by this preference, matching the requirement that only operational modules auto-detach.</summary>
    private static void LaunchModule(IVenueModule module, VenueShell shell, GlobalSettingsService globalSettings, ModuleWindowManager windowManager)
    {
        switch (ModuleLaunchRouting.Resolve(globalSettings.AutoPopOutModules))
        {
            case ModuleLaunchTarget.Detached: windowManager.Open(module.Descriptor.Id); break;
            default: shell.SelectModule(module.Descriptor.Id); break;
        }
    }
}
