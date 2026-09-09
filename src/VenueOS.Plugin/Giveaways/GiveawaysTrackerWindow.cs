using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Giveaways;

/// <summary>An additional, independently toggleable window — NOT the module's own embedded/detached content
/// (that's <c>ModuleWindowManager</c>'s one-window-per-module-id system, §19). This is the same kind of extra
/// auxiliary window Bingo offers via <c>BingoCalledNumbersWindow</c> (GIVEAWAYS spec §28's "dedicated pop-out
/// tracker experience"), so the operator can keep just the roll tracker visible/detached separately from the full
/// Giveaways panel. It renders through <see cref="GiveawaysOperatorPanel.DrawTracker"/> — the EXACT SAME tracker
/// rendering the embedded panel uses, reading the same <c>GiveawayService</c> state — never a second, competing
/// implementation (spec §28's explicit requirement).</summary>
internal sealed class GiveawaysTrackerWindow(Action onOpenSettings)
{
    public bool IsOpen;

    public void Draw(VenueTheme theme, GiveawaysOperatorPanel panel)
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(480, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 320), new Vector2(float.MaxValue, float.MaxValue));
        UiKit.PushWindowTheme(theme);
        var closeRequested = false;
        if (ImGui.Begin("###venueos-giveaways-tracker", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            ModuleWindowHeader.Draw(theme, "gift", "Giveaways — Roll Tracker", onOpenSettings, () => closeRequested = true);
            ImGui.BeginChild("giveaways-tracker-window-content", ImGui.GetContentRegionAvail(), false);
            panel.DrawTracker(theme);
            ImGui.EndChild();
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        if (closeRequested) IsOpen = false;
    }
}
