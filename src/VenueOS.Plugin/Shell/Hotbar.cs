using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>A live-operation slot button: a wide tile showing a short top label ("DJ 1"), a main label (the preset
/// name), and an unmistakable ACTIVE state. Built for Greeter's five-preset hotbar, but generic enough for any
/// future "pick one of a small fixed set live" control.</summary>
internal static class Hotbar
{
    public static bool Slot(VenueTheme theme, string id, string topLabel, string mainLabel, bool active, Vector2 size)
    {
        ImGui.PushID(id);
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##slot", size);
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        var background = active ? theme.Tokens.Selected : hovered ? theme.Tokens.RaisedSurface : theme.Tokens.Surface;
        drawList.AddRectFilled(start, start + size, UiKit.ColorU32(background), theme.Metrics.Rounding + 2);
        drawList.AddRect(start, start + size, UiKit.ColorU32(active ? theme.Tokens.Accent : theme.Tokens.Border), theme.Metrics.Rounding + 2, ImDrawFlags.None, active ? 2f : 1f);

        var padding = new Vector2(10, 8);
        drawList.AddText(start + padding, UiKit.ColorU32(theme.Tokens.TextSecondary), topLabel);
        drawList.AddText(start + new Vector2(padding.X, size.Y * 0.48f), UiKit.ColorU32(theme.Tokens.TextPrimary), Truncate(mainLabel, size.X - padding.X * 2));

        if (active)
        {
            const string badge = "ACTIVE";
            var badgeSize = ImGui.CalcTextSize(badge);
            drawList.AddText(start + new Vector2(size.X - badgeSize.X - padding.X, padding.Y), UiKit.ColorU32(theme.Tokens.Accent), badge);
        }

        ImGui.PopID();
        return clicked;
    }

    private static string Truncate(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        var truncated = text;
        while (truncated.Length > 1 && ImGui.CalcTextSize(truncated + "…").X > maxWidth) truncated = truncated[..^1];
        return truncated + "…";
    }
}
