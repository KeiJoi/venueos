using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Shared chrome for a dedicated editor window or modal dialog that isn't a module's own detached window
/// (<see cref="ModuleWindowHeader"/> covers that case, with its module-specific icon + Settings gear) — title text
/// + Close only, same raised-surface bar / drag-handle visual language. Added during the 0.3.0 UI quality pass:
/// every VenueOS-generated top-level window/modal except a handful of editor/dialog surfaces already used
/// <c>ImGuiWindowFlags.NoTitleBar</c> with custom chrome (the main tablet, every detached module window, Bingo's
/// and Giveaways' auxiliary windows) — this is the missing piece for the surfaces that still showed ImGui's raw
/// native title bar (<c>MacroEditorWindow</c>, <c>GiveawayPresetEditorModal</c>, <c>VipEditDialog</c>).</summary>
internal static class DialogHeader
{
    private const float HeaderHeight = 40f;
    private const float CloseButtonWidth = 32f;

    public static void Draw(VenueTheme theme, string title, Action onClose)
    {
        var start = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(contentWidth, HeaderHeight), UiKit.ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);

        var gap = ImGui.GetStyle().ItemSpacing.X;
        var rowY = start.Y + (HeaderHeight - CloseButtonWidth) / 2f;

        // Right — reserved first, absolute screen position, same "reserve the critical region first" approach
        // ModuleWindowHeader/TabletHeader already use, so a long title can never push Close off-window.
        var closeX = start.X + contentWidth - 4 - CloseButtonWidth;
        ImGui.SetCursorScreenPos(new Vector2(closeX, rowY));
        if (UiKit.IconButton(theme, "dialog-header-close", "close", CloseButtonWidth, "Close")) onClose();

        // Left — title, always fully shown.
        ImGui.SetCursorScreenPos(new Vector2(start.X + 12, rowY));
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        var leftEndX = ImGui.GetItemRectMax().X;

        // Drag handle: only the genuinely empty middle, never overlapping Close.
        var dragMinX = leftEndX + gap;
        var dragMaxX = closeX - gap;
        if (dragMaxX > dragMinX)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragMinX, start.Y + 4));
            ImGui.InvisibleButton("##dialog-header-drag", new Vector2(dragMaxX - dragMinX, HeaderHeight - 8));
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
        }

        ImGui.SetCursorScreenPos(start + new Vector2(0, HeaderHeight + 4));
    }
}
