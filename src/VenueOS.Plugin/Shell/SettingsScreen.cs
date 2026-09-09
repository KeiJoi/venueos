using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>The Settings application shell: navigation, layout, and frame only. Each category is its own page class
/// that owns its own state and content — Settings itself carries no field-level knowledge of any module or venue
/// data beyond what's needed to route to a page, matching the "modules contribute their own settings surface"
/// architecture (see <see cref="ModulesSettingsPage"/>).</summary>
internal sealed class SettingsScreen
{
    private readonly record struct Category(string Id, string Icon, string Title, string Subtitle);
    private static readonly Category[] Categories =
    [
        new("venue", "home", "Venue", "Profile & identity"),
        new("appearance", "palette", "Appearance", "Themes & colors"),
        new("modules", "grid", "Modules", "Enable & configure"),
        new("general", "gear", "General", "System behavior"),
        new("diagnostics", "terminal", "Diagnostics", "Logs & troubleshooting"),
    ];
    private const float NarrowThreshold = 640f;
    private const float SidebarWidth = 240f;

    private readonly VenueSettingsPage venuePage;
    private readonly AppearanceSettingsPage appearancePage;
    private readonly ModulesSettingsPage modulesPage;
    private readonly GeneralSettingsPage generalPage;
    private readonly DiagnosticsSettingsPage diagnosticsPage;
    private int activeCategory;

    public SettingsScreen(VenueProfileService venues, ModuleHost modules, DiagnosticsService diagnostics, GlobalSettingsService globalSettings, VenueSwitchCoordinator switchCoordinator)
    {
        venuePage = new VenueSettingsPage(venues, switchCoordinator);
        appearancePage = new AppearanceSettingsPage(venues);
        modulesPage = new ModulesSettingsPage(modules, diagnostics, globalSettings, venues);
        generalPage = new GeneralSettingsPage(diagnostics, globalSettings);
        diagnosticsPage = new DiagnosticsSettingsPage(modules, diagnostics);
    }

    public void FocusDiagnostics() => activeCategory = Array.FindIndex(Categories, c => c.Id == "diagnostics");

    /// <summary>Jumps to Settings → Modules → &lt;module&gt; — the destination for a detached module window's
    /// Settings gear. Reuses the exact same navigation the embedded Modules page's own "Configure" button uses.</summary>
    public void FocusModuleConfiguration(string moduleId)
    {
        activeCategory = Array.FindIndex(Categories, c => c.Id == "modules");
        modulesPage.OpenConfigure(moduleId);
    }

    public void Draw(VenueTheme theme)
    {
        if (ImGui.GetContentRegionAvail().X >= NarrowThreshold) DrawWide(theme); else DrawNarrow(theme);
    }

    private void DrawWide(VenueTheme theme)
    {
        UiKit.BeginCard("settings-nav", theme, new Vector2(SidebarWidth, 0));
        DrawNavHeader(theme);
        ImGui.Spacing();
        for (var i = 0; i < Categories.Length; i++)
        {
            var category = Categories[i];
            if (UiKit.NavRow(theme, category.Id, category.Icon, category.Title, category.Subtitle, i == activeCategory)) activeCategory = i;
            ImGui.Spacing();
        }
        UiKit.EndCard();

        ImGui.SameLine();
        ImGui.BeginChild("settings-content", new Vector2(0, 0), false);
        DrawActivePage(theme);
        ImGui.EndChild();
    }

    private void DrawNarrow(VenueTheme theme)
    {
        DrawNavHeader(theme);
        ImGui.Spacing();
        var options = Categories.Select(c => c.Title).ToArray();
        Forms.Segmented(theme, "settings-tabs-narrow", options, ref activeCategory);
        ImGui.Spacing(); UiKit.Divider(theme); ImGui.Spacing();
        DrawActivePage(theme);
    }

    private static void DrawNavHeader(VenueTheme theme)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted("Settings");
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Configure VenueOS for your venue.");
        ImGui.PopStyleColor();
    }

    private void DrawActivePage(VenueTheme theme)
    {
        switch (Categories[activeCategory].Id)
        {
            case "venue": venuePage.Draw(theme); break;
            case "appearance": appearancePage.Draw(theme); break;
            case "modules": modulesPage.Draw(theme); break;
            case "general": generalPage.Draw(theme); break;
            default: diagnosticsPage.Draw(theme); break;
        }
    }
}
