using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.UI;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Owns which modules currently have an independent, detached operator window open. The main tablet stays
/// the launcher; detaching/re-embedding a module is a pure UI choice that never touches module lifecycle, and every
/// detached window renders the exact same <c>IVenueModule.Draw()</c> the embedded <see cref="AppFrame"/> does — there
/// is only ever one module UI implementation, wrapped by two different chrome layers. Chrome itself is
/// <see cref="ModuleWindowHeader"/>, shared by every module rather than reimplemented per window.</summary>
internal sealed class ModuleWindowManager(DiagnosticsService diagnostics, VenueShell shell, SettingsScreen settingsScreen, Action requestOpenAndFocusTablet)
{
    private readonly HashSet<string> open = new(StringComparer.Ordinal);
    private readonly HashSet<string> focusRequested = new(StringComparer.Ordinal);

    public bool IsOpen(string moduleId) => open.Contains(moduleId);

    /// <summary>Opens the module's detached window, or focuses it if already open — never a second instance.</summary>
    public void Open(string moduleId) { open.Add(moduleId); focusRequested.Add(moduleId); }
    public void Close(string moduleId) => open.Remove(moduleId);

    public void DrawAll(VenueTheme theme, IReadOnlyList<IVenueModule> modules)
    {
        if (open.Count == 0) return;
        foreach (var moduleId in open.ToArray())
        {
            var module = modules.FirstOrDefault(x => x.Descriptor.Id == moduleId);
            if (module is null || !module.IsEnabled) { open.Remove(moduleId); continue; }
            DrawWindow(theme, module);
        }
    }

    private void DrawWindow(VenueTheme theme, IVenueModule module)
    {
        if (focusRequested.Remove(module.Descriptor.Id)) ImGui.SetNextWindowFocus();
        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 320), new Vector2(float.MaxValue, float.MaxValue));

        UiKit.PushWindowTheme(theme);
        // No visible title text — the header below is the only chrome — so the window name is a pure stable ID.
        // It never embeds the module/venue display name, so a rename or venue switch can't reset remembered
        // position/size the way the main tablet's id had to be fixed to avoid in Phase 3.
        var closeRequested = false;
        if (ImGui.Begin($"###venueos-detached-{module.Descriptor.Id}", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            ModuleWindowHeader.Draw(theme, module.Descriptor.Icon, module.Descriptor.DisplayName,
                onSettings: () => { requestOpenAndFocusTablet(); shell.SelectSettings(); settingsScreen.FocusModuleConfiguration(module.Descriptor.Id); },
                onClose: () => closeRequested = true);

            ImGui.BeginChild("detached-content", ImGui.GetContentRegionAvail(), false);
            UiKit.SafeDraw(theme, diagnostics, module.Descriptor.Id, module.Draw);
            ImGui.EndChild();
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        if (closeRequested) open.Remove(module.Descriptor.Id);
    }
}
