using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Settings → Launcher: Enable/Show, Buttons Per Row, Lock/Edit, Scale, Reset Launcher Position, and the
/// per-eligible-module Show-on-Launcher checkboxes with up/down ordering — no in-launcher drag-reorder (the one
/// existing drag/drop precedent, <c>MacroDragDrop</c>, is a fixed id-onto-slot payload not suited to list
/// reordering). Live QA follow-up: the launcher is icon-only by convention now, not a toggleable mode, so there is
/// no Compact/icon-only setting here any more — see <c>ModuleLauncherWindow</c>'s own doc comment. Mirrors
/// <see cref="ModulesSettingsPage"/>'s exact iteration/row pattern. All changes take effect immediately since they
/// write straight through <see cref="GlobalSettingsService"/> and the launcher reads it live every frame — no
/// <c>/xlreload</c> required.</summary>
internal sealed class LauncherSettingsPage(ModuleHost modules, GlobalSettingsService globalSettings)
{
    public void Draw(VenueTheme theme)
    {
        AppFrame.Draw(theme, "launcher", "Launcher", "Quick-access hotbar for opening and reaching modules.", () =>
        {
            DrawGeneral(theme);
            ImGui.Spacing(); ImGui.Spacing();
            DrawModules(theme);
        });
    }

    private void DrawGeneral(VenueTheme theme)
    {
        var launcher = globalSettings.Launcher;

        UiKit.BeginSectionCard("launcher-general", theme, "Launcher");
        var enabled = launcher.Enabled;
        if (UiKit.Toggle(theme, "Show launcher", ref enabled)) globalSettings.SetLauncher(globalSettings.Launcher with { Enabled = enabled });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("A small, always-available hotbar for opening and reaching modules, independent of the main tablet.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var locked = launcher.Locked;
        if (UiKit.Toggle(theme, "Lock position & size", ref locked)) globalSettings.SetLauncher(globalSettings.Launcher with { Locked = locked });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Unlock to freely drag and resize the launcher window itself (same quick-lock toggle as the launcher's own strip).");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var buttonsPerRow = launcher.ButtonsPerRow;
        if (Forms.NumericField(theme, "Buttons per row", ref buttonsPerRow, 1, 1, 20)) globalSettings.SetLauncher(globalSettings.Launcher with { ButtonsPerRow = buttonsPerRow });

        var scale = launcher.Scale;
        if (Forms.FloatField(theme, "Scale", ref scale, 0.05f, 0.5f, 2.5f)) globalSettings.SetLauncher(globalSettings.Launcher with { Scale = scale });

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Reset Launcher Position"))
        {
            var defaults = new LauncherSettings();
            var viewport = ImGui.GetMainViewport();
            globalSettings.ResetLauncherPosition(viewport.WorkPos.X + defaults.PositionX, viewport.WorkPos.Y + defaults.PositionY, defaults.Width, defaults.Height, defaults.Scale);
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Resets position and size/scale to sensible defaults. Buttons-per-row, lock, and which modules are shown are untouched.");
        ImGui.PopStyleColor();
        UiKit.EndSectionCard();
    }

    private void DrawModules(VenueTheme theme)
    {
        UiKit.BeginSectionCard("launcher-modules", theme, "Modules on Launcher");
        UiKit.InfoBanner(theme, "Choose what appears on the launcher", "Every enabled module is shown by default. Hide the ones you don't want quick access to, and reorder with the arrows.");
        ImGui.Spacing(); ImGui.Spacing();

        var enabledIds = modules.Modules.Where(m => m.IsEnabled).Select(m => m.Descriptor.Id).ToArray();
        if (enabledIds.Length == 0)
        {
            UiKit.EmptyState(theme, "No enabled modules", "Enable a module in Settings → Modules first.");
            UiKit.EndSectionCard();
            return;
        }

        var order = LauncherEntries.FullOrder(enabledIds, globalSettings.Launcher.ModuleOrder);
        DrawColumnHeaders(theme);
        UiKit.Divider(theme);
        for (var i = 0; i < order.Count; i++)
        {
            var module = modules.Modules.FirstOrDefault(x => x.Descriptor.Id == order[i]);
            if (module is null) continue;
            ImGui.PushID(module.Descriptor.Id);
            DrawModuleRow(theme, module, i, order.Count);
            UiKit.Divider(theme);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    private static void DrawColumnHeaders(VenueTheme theme)
    {
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted("Module");
        ImGui.SameLine(width - 150); ImGui.TextUnformatted("Order");
        ImGui.SameLine(width - 40); ImGui.TextUnformatted("Shown");
        ImGui.PopStyleColor();
    }

    private void DrawModuleRow(VenueTheme theme, IVenueModule module, int index, int count)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var rowStart = ImGui.GetCursorScreenPos();
        const float rowHeight = 40f;

        var iconCenter = rowStart + new Vector2(16, rowHeight / 2);
        AppIcons.Draw(module.Descriptor.Icon, iconCenter, 13, UiKit.ColorU32(theme.Tokens.TextPrimary));
        ImGui.SetCursorScreenPos(rowStart + new Vector2(34, rowHeight / 2 - 8));
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(module.Descriptor.DisplayName);
        ImGui.PopStyleColor();

        // Right region — Up, Down, Show toggle — reserved at an absolute position, same "reserve critical controls
        // first" rule every other VenueOS row/header uses (§35), so a long display name can never push these off.
        var moduleId = module.Descriptor.Id;
        var upX = rowStart.X + width - 150;
        var downX = upX + 36;
        var showX = rowStart.X + width - 34;
        var buttonY = rowStart.Y + rowHeight / 2 - 16;

        ImGui.SetCursorScreenPos(new Vector2(upX, buttonY));
        ImGui.BeginDisabled(index == 0);
        if (UiKit.IconButton(theme, $"launcher-order-up-{moduleId}", "chevron-up", 32, "Move up")) MoveModule(moduleId, -1);
        ImGui.EndDisabled();

        ImGui.SetCursorScreenPos(new Vector2(downX, buttonY));
        ImGui.BeginDisabled(index == count - 1);
        if (UiKit.IconButton(theme, $"launcher-order-down-{moduleId}", "chevron-down", 32, "Move down")) MoveModule(moduleId, 1);
        ImGui.EndDisabled();

        ImGui.SetCursorScreenPos(new Vector2(showX, rowStart.Y + rowHeight / 2 - 10));
        var shown = globalSettings.IsShownOnLauncher(moduleId);
        if (UiKit.Toggle(theme, "##show", ref shown)) globalSettings.SetShownOnLauncher(moduleId, shown);
        UiKit.Tooltip(shown ? "Shown on launcher" : "Hidden from launcher");

        ImGui.SetCursorScreenPos(rowStart + new Vector2(0, rowHeight));
    }

    private void MoveModule(string moduleId, int direction)
    {
        var enabledIds = modules.Modules.Where(m => m.IsEnabled).Select(m => m.Descriptor.Id).ToArray();
        var order = LauncherEntries.FullOrder(enabledIds, globalSettings.Launcher.ModuleOrder).ToList();
        var index = order.IndexOf(moduleId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        globalSettings.SetLauncherModuleOrder(order);
    }
}
