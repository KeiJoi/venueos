using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Shared custom chrome used by every VenueOS-generated top-level window that isn't the main tablet — icon
/// + name on the left, a control cluster on the right, with the empty middle doubling as a drag handle. One
/// implementation, two call shapes:
///
/// - A normal detached module window (<see cref="ModuleWindowManager"/>) passes <paramref name="onToggleCollapse"/>
///   and <paramref name="onMinimize"/>, and gets the Module Launcher & Window Management pass's full control set: an
///   Expanded window shows Collapse, Minimize, Settings, Close; a Collapsed one shows only Expand, Minimize, Close —
///   no Settings gear, matching the Collapsed contract that only the header itself draws, never module content.
/// - An "opt out" auxiliary popout (Bingo's Called Numbers/Player Cards/Call Alert, Giveaways' Roll Tracker — see
///   NEW_MODULE_GUIDE-style classification in the Module Launcher plan) leaves both null and gets exactly the
///   original two-button Settings+Close chrome — these windows are deliberately NOT registered with
///   <see cref="ModuleWindowManager"/> and have no collapse/hide state to back new buttons for, so none are drawn.
///
/// The right region is always reserved first at an absolute screen position, exactly like
/// <see cref="TabletHeader"/>'s main-toolbar fix — the window's title text can never push these off-window. Every
/// control's id is built from <paramref name="moduleId"/> (a module's stable <c>Descriptor.Id</c> for a real
/// detached module window, or any other stable literal for an auxiliary popout — each popout is its own top-level
/// ImGui window, so identical id strings across different popouts never collide), never the display name.
///
/// Drawn directly into the window (not a child), since the drag handle needs <see cref="ImGui.SetWindowPos"/>,
/// which always targets "the current window".</summary>
internal static class ModuleWindowHeader
{
    public const float HeaderHeight = 44f;
    private const float IconButtonWidth = 32f;

    public static void Draw(VenueTheme theme, string iconKey, string displayName, string moduleId, Action onSettings, Action onClose,
        bool collapsed = false, Action? onToggleCollapse = null, Action? onMinimize = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(contentWidth, HeaderHeight), UiKit.ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var rowY = start.Y + (HeaderHeight - IconButtonWidth) / 2f;

        // Right region — reserved first, absolute screen position, independent of the window title's length.
        var buttons = new List<(string Id, string Icon, string Tooltip, Action OnClick)>();
        if (onToggleCollapse is not null) buttons.Add(($"detached-collapse-{moduleId}", collapsed ? "chevron-up" : "chevron-down", collapsed ? "Expand" : "Collapse", onToggleCollapse));
        if (onMinimize is not null) buttons.Add(($"detached-minimize-{moduleId}", "minimize", "Minimize (hide, keep running)", onMinimize));
        if (!collapsed) buttons.Add(($"detached-settings-{moduleId}", "gear", "Module settings", onSettings));
        buttons.Add(($"detached-close-{moduleId}", "close", "Close window", onClose));

        var rightWidth = buttons.Count * IconButtonWidth + (buttons.Count - 1) * gap;
        var rightStartX = start.X + contentWidth - 4 - rightWidth;
        var cursorX = rightStartX;
        foreach (var button in buttons)
        {
            ImGui.SetCursorScreenPos(new Vector2(cursorX, rowY));
            if (UiKit.IconButton(theme, button.Id, button.Icon, IconButtonWidth, button.Tooltip)) button.OnClick();
            cursorX += IconButtonWidth + gap;
        }

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

        // Drag handle: only the genuinely empty middle, never overlapping the right control cluster.
        var dragMinX = leftEndX + gap;
        var dragMaxX = rightStartX - gap;
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
