using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Shared custom chrome for every detached module window — icon + name on the left, Settings + Close on
/// the right, with the empty middle doubling as a drag handle. One implementation used by every detachable module
/// (see <see cref="ModuleWindowManager"/>), so a module never needs its own window-chrome code.
///
/// Right region (Settings, Close) is reserved first at an absolute screen position, exactly like
/// <see cref="TabletHeader"/>'s main-toolbar fix — the module name's length can never push these two off-window.
/// Drawn directly into the window (not a child), since the drag handle needs <see cref="ImGui.SetWindowPos"/>,
/// which always targets "the current window".</summary>
internal static class ModuleWindowHeader
{
    private const float HeaderHeight = 44f;
    private const float IconButtonWidth = 32f;

    public static void Draw(VenueTheme theme, string iconKey, string displayName, Action onSettings, Action onClose)
    {
        var start = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(contentWidth, HeaderHeight), UiKit.ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var rowY = start.Y + (HeaderHeight - IconButtonWidth) / 2f;

        // Right region — reserved first, absolute screen position, independent of the module name's length.
        var rightWidth = IconButtonWidth + gap + IconButtonWidth;
        var settingsX = start.X + contentWidth - 4 - rightWidth;
        var closeX = settingsX + IconButtonWidth + gap;

        ImGui.SetCursorScreenPos(new Vector2(settingsX, rowY));
        if (UiKit.IconButton(theme, "detached-settings", "gear", IconButtonWidth, "Module settings")) onSettings();
        ImGui.SetCursorScreenPos(new Vector2(closeX, rowY));
        if (UiKit.IconButton(theme, "detached-close", "close", IconButtonWidth, "Close window")) onClose();

        // Left region — module icon + display name, always fully shown.
        var iconCenter = new Vector2(start.X + 10 + 12, rowY + IconButtonWidth / 2);
        AppIcons.Draw(iconKey, iconCenter, 13, UiKit.ColorU32(theme.Tokens.Accent));
        ImGui.SetCursorScreenPos(new Vector2(start.X + 10, rowY));
        ImGui.Dummy(new Vector2(28, IconButtonWidth));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(displayName);
        ImGui.PopStyleColor();
        var leftEndX = ImGui.GetItemRectMax().X;

        // Drag handle: only the genuinely empty middle, never overlapping Settings/Close.
        var dragMinX = leftEndX + gap;
        var dragMaxX = settingsX - gap;
        if (dragMaxX > dragMinX)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragMinX, start.Y + 4));
            ImGui.InvisibleButton("##detached-drag", new Vector2(dragMaxX - dragMinX, HeaderHeight - 8));
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
        }

        ImGui.SetCursorScreenPos(start + new Vector2(0, HeaderHeight + 4));
    }
}
