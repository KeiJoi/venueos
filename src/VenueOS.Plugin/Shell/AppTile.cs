using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>A single tablet-home-screen application icon: recognizable icon, name, hover/pressed/disabled states,
/// and an optional small status badge dot.</summary>
internal static class AppTile
{
    public const float Width = 108f;
    public const float Height = 140f;

    public static bool Draw(VenueTheme theme, string id, string iconKey, string label, bool enabled = true, bool hasBadge = false, string badgeColorToken = "Error")
    {
        ImGui.PushID(id);
        var size = new Vector2(Width, Height);
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##tile", size);
        var clicked = enabled && ImGui.IsItemClicked();
        var hovered = enabled && ImGui.IsItemHovered();
        var active = enabled && ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();

        var iconBoxMin = start;
        var iconBoxMax = start + new Vector2(Width, Width);
        var background = !enabled ? theme.Tokens.Disabled : active ? theme.Tokens.Selected : hovered ? theme.Tokens.RaisedSurface : theme.Tokens.Surface;
        drawList.AddRectFilled(iconBoxMin, iconBoxMax, UiKit.ColorU32(background), theme.Metrics.Rounding + 4);
        if (hovered && enabled) drawList.AddRect(iconBoxMin, iconBoxMax, UiKit.ColorU32(theme.Tokens.Accent), theme.Metrics.Rounding + 4, ImDrawFlags.None, 1.5f);

        var iconColor = UiKit.ColorU32(enabled ? theme.Tokens.TextPrimary : theme.Tokens.TextSecondary);
        AppIcons.Draw(iconKey, (iconBoxMin + iconBoxMax) / 2, Width * 0.42f, iconColor);

        if (hasBadge)
        {
            var badgeCenter = new Vector2(iconBoxMax.X - 8, iconBoxMin.Y + 8);
            drawList.AddCircleFilled(badgeCenter, 6f, UiKit.ColorU32(badgeColorToken == "Error" ? theme.Tokens.Error : theme.Tokens.Warning));
        }

        var labelColor = enabled ? theme.Tokens.TextPrimary : theme.Tokens.TextSecondary;
        var textSize = ImGui.CalcTextSize(label);
        var labelPos = new Vector2(start.X + MathF.Max(0, (Width - textSize.X) / 2), iconBoxMax.Y + 6);
        drawList.AddText(labelPos, UiKit.ColorU32(labelColor), label);

        ImGui.PopID();
        return clicked;
    }
}
