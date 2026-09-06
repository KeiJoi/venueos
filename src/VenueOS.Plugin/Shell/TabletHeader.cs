using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.UI;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Persistent tablet status/header bar — global application chrome only. It communicates VenueOS
/// navigation, active venue identity, venue switching, Settings and Close; it deliberately does NOT name whichever
/// module/Settings screen happens to be open right now (that screen's own content already identifies itself — see
/// <see cref="AppFrame"/>), so the header is identical regardless of what's currently open. It also does not
/// duplicate error/warning state — that's redundant with the Home overview card and Settings → Diagnostics, both of
/// which already surface it.
///
/// Built as three explicit, structurally separated regions so overlap is impossible by construction:
/// <list type="bullet">
/// <item><b>Left</b> — Home, brand mark, active venue name (truncated with a tooltip if it can't fit).</item>
/// <item><b>Right</b> — venue selector, Settings, Close, in that left-to-right order. Reserved and positioned in
/// absolute screen space <i>before</i> anything else is drawn, so nothing in Left can ever push or overlap them.</item>
/// <item><b>Flex</b> — just the drag handle, filling whatever's left between Left and Right.</item>
/// </list>
///
/// Drawn directly into the main window (not a <c>BeginChild</c>) so its empty middle region can double as a drag
/// handle for the borderless tablet — <see cref="ImGui.SetWindowPos(Vector2, ImGuiCond)"/> always targets "the
/// current window", so the drag logic could not live inside a child without losing the ability to move the real
/// window.</summary>
internal static class TabletHeader
{
    private const float HeaderHeight = 64f;
    private const float ComboWidth = 110f;
    private const float IconButtonWidth = 40f;
    private const float RightMargin = 4f;
    private const float MinVenueNameWidth = 40f;
    private const float MinDragGap = 8f;

    public static void Draw(VenueTheme theme, VenueProfileService venues, VenueShell shell, Action requestClose, VenueSwitchCoordinator switchCoordinator)
    {
        var current = venues.Current;
        var start = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(contentWidth, HeaderHeight), UiKit.ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 4);

        var padding = theme.Metrics.Padding;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var rowY = start.Y + (HeaderHeight - 28) / 2f;

        // ================= RIGHT REGION — reserved and positioned first, in absolute screen space. =================
        var rightWidth = ComboWidth + gap + IconButtonWidth + gap + IconButtonWidth;
        var rightRegionStartX = start.X + contentWidth - RightMargin - rightWidth;
        var comboX = rightRegionStartX;
        var settingsX = comboX + ComboWidth + gap;
        var closeX = settingsX + IconButtonWidth + gap;

        ImGui.SetCursorScreenPos(new Vector2(comboX, rowY));
        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##venue-quickswitch", current.DisplayName))
        {
            foreach (var profile in venues.Profiles)
                if (ImGui.Selectable(profile.DisplayName, profile.Id == current.Id)) switchCoordinator.RequestSwitch(profile.Id);
            ImGui.EndCombo();
        }
        ImGui.SetCursorScreenPos(new Vector2(settingsX, rowY));
        if (UiKit.IconButton(theme, "settings", "gear", IconButtonWidth, "Settings", shell.IsSettingsSelected)) shell.SelectSettings();
        ImGui.SetCursorScreenPos(new Vector2(closeX, rowY));
        if (UiKit.IconButton(theme, "close", "close", IconButtonWidth, "Close VenueOS (reopen with /venueos)")) requestClose();

        // ================= LEFT REGION — Home, brand mark, venue name (truncated only if it must be). =================
        var leftFixed = IconButtonWidth + gap + 28 + gap;
        var venueNameMaxWidth = rightRegionStartX - MinDragGap - (start.X + padding + leftFixed);

        ImGui.SetCursorScreenPos(new Vector2(start.X + padding, rowY));
        ImGui.AlignTextToFramePadding();
        if (UiKit.IconButton(theme, "home", "home", IconButtonWidth, "Home", shell.IsHome)) shell.SelectHome();
        ImGui.SameLine();
        DrawBrandMark(theme);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        var venueNameFullWidth = ImGui.CalcTextSize(current.DisplayName).X;
        if (venueNameFullWidth <= venueNameMaxWidth || venueNameMaxWidth < MinVenueNameWidth) ImGui.TextUnformatted(current.DisplayName);
        else { ImGui.TextUnformatted(Truncate(current.DisplayName, venueNameMaxWidth)); UiKit.Tooltip(current.DisplayName); }
        ImGui.PopStyleColor();
        var leftEndX = ImGui.GetItemRectMax().X;

        // ================= FLEX REGION — drag handle only; no content lives here. =================
        DrawDragHandle(new Vector2(leftEndX + gap, rowY), MathF.Max(leftEndX + gap, rightRegionStartX - gap));

        ImGui.SetCursorScreenPos(start + new Vector2(0, HeaderHeight + 4));
        switchCoordinator.Draw(theme); // global chrome — must render regardless of which screen/module is currently open
    }

    private static void DrawDragHandle(Vector2 topLeft, float maxX)
    {
        var width = MathF.Max(1f, maxX - topLeft.X);
        ImGui.SetCursorScreenPos(new Vector2(topLeft.X, topLeft.Y - 4));
        ImGui.InvisibleButton("##header-drag", new Vector2(width, HeaderHeight - 20));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
        }
    }

    private static void DrawBrandMark(VenueTheme theme)
    {
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(28, 28);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilledMultiColor(start, start + size, UiKit.ColorU32(theme.Tokens.Primary), UiKit.ColorU32(theme.Tokens.Accent), UiKit.ColorU32(theme.Tokens.Accent), UiKit.ColorU32(theme.Tokens.Primary));
        drawList.AddRect(start, start + size, UiKit.ColorU32(theme.Tokens.Border), 6f);
        ImGui.Dummy(size);
    }

    private static string Truncate(string text, float maxWidth)
    {
        if (maxWidth <= 0 || text.Length <= 1) return text;
        var truncated = text;
        while (truncated.Length > 1 && ImGui.CalcTextSize(truncated + "…").X > maxWidth) truncated = truncated[..^1];
        return truncated + "…";
    }
}
