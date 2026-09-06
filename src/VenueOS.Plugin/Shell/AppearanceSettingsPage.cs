using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Per-venue theme selection and the shell's two real appearance toggles. Only exposes controls actually
/// backed by data: theme choice (<see cref="VenueProfileService.SetTheme"/>) and the frame/branding visibility
/// flags on <see cref="VenueBranding"/>. Deliberately does not include a "use venue accent colors" switch (there is
/// no second color source to switch between) or a UI-scale slider (no scaling mechanism exists) — see
/// <c>UI_STATUS.md</c> for why those are omitted rather than faked.</summary>
internal sealed class AppearanceSettingsPage(VenueProfileService venues)
{
    private static readonly (string Description, string Blurb)[] ThemeCopy =
    [
        ("dark", "Focused, low-light."),
        ("light", "Bright, daylight-ready."),
        ("neon", "Vibrant nightlife colors."),
        ("midnight", "Sleek, deep blues."),
    ];

    public void Draw(VenueTheme theme)
    {
        AppFrame.Draw(theme, "palette", "Appearance", "Customize the look and feel of VenueOS for the active venue.", () => DrawContent(theme));
    }

    private void DrawContent(VenueTheme theme)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted($"Active venue: {venues.Current.DisplayName} · Theme: {Capitalize(theme.BuiltInThemeId)}");
        ImGui.PopStyleColor();
        ImGui.Spacing(); ImGui.Spacing();

        UiKit.BeginSectionCard("appearance-theme", theme, "Theme Selection");
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Choose the theme for the active venue. This controls the in-game VenueOS interface.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        DrawThemeCards(theme);
        UiKit.EndSectionCard();

        ImGui.Spacing(); ImGui.Spacing();

        UiKit.BeginSectionCard("appearance-options", theme, "Theme Options");
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Fine-tune how the venue-themed tablet chrome presents itself.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        DrawOptions(theme);
        UiKit.EndSectionCard();
    }

    private void DrawThemeCards(VenueTheme theme)
    {
        var cardSize = new Vector2(190, 160);
        var gap = 12f;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (cardSize.X + gap)));
        var column = 0;
        foreach (var candidate in BuiltInThemes.All.Values)
        {
            if (column > 0) ImGui.SameLine(0, gap);
            DrawThemeCard(theme, candidate, cardSize);
            column = (column + 1) % columns;
            if (column == 0) ImGui.Dummy(new Vector2(0, gap));
        }
    }

    private void DrawThemeCard(VenueTheme theme, VenueTheme candidate, Vector2 size)
    {
        ImGui.PushID(candidate.BuiltInThemeId);
        var isActive = candidate.BuiltInThemeId == theme.BuiltInThemeId;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##card", size);
        if (ImGui.IsItemClicked()) venues.SetTheme(venues.Current.Id, candidate);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(start, start + size, UiKit.ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);
        drawList.AddRect(start, start + size, UiKit.ColorU32(isActive ? theme.Tokens.Accent : hovered ? theme.Tokens.Border : theme.Tokens.Border, isActive ? 1f : 0.6f), theme.Metrics.Rounding + 2, ImDrawFlags.None, isActive ? 2.5f : 1f);

        var previewMin = start + new Vector2(10, 10);
        var previewMax = start + new Vector2(size.X - 10, 88);
        drawList.AddRectFilled(previewMin, previewMax, UiKit.ColorU32(candidate.Tokens.Background), 4);
        drawList.AddRectFilled(previewMin, new Vector2(previewMax.X, previewMin.Y + 14), UiKit.ColorU32(candidate.Tokens.Primary));
        drawList.AddRectFilled(new Vector2(previewMin.X + 8, previewMin.Y + 24), new Vector2(previewMin.X + 34, previewMin.Y + 34), UiKit.ColorU32(candidate.Tokens.Surface));
        for (var line = 0; line < 3; line++)
        {
            var lineY = previewMin.Y + 24 + line * 14;
            drawList.AddRectFilled(new Vector2(previewMin.X + 8, lineY), new Vector2(previewMax.X - 8, lineY + 8), UiKit.ColorU32(line == 1 ? candidate.Tokens.Accent : candidate.Tokens.RaisedSurface));
        }

        drawList.AddText(start + new Vector2(10, 94), UiKit.ColorU32(theme.Tokens.TextPrimary), Capitalize(candidate.BuiltInThemeId));
        var copy = ThemeCopy.FirstOrDefault(x => x.Description == candidate.BuiltInThemeId).Blurb ?? "";
        ImGui.SetCursorScreenPos(start + new Vector2(10, 112));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + size.X - 34);
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(copy);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        var radiusCenter = start + new Vector2(size.X - 16, 118);
        drawList.AddCircle(radiusCenter, 6, UiKit.ColorU32(isActive ? theme.Tokens.Accent : theme.Tokens.TextSecondary), 0, 1.5f);
        if (isActive) drawList.AddCircleFilled(radiusCenter, 3, UiKit.ColorU32(theme.Tokens.Accent));

        UiKit.Tooltip($"Switch to {Capitalize(candidate.BuiltInThemeId)}");
        ImGui.PopID();
    }

    private void DrawOptions(VenueTheme theme)
    {
        var branding = theme.Branding;
        var showFrame = branding.ShowVenueFrame;
        if (UiKit.Toggle(theme, "Colored window frame", ref showFrame)) ApplyBranding(theme, branding with { ShowVenueFrame = showFrame });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Show the venue-colored border around the VenueOS tablet.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var showBranding = branding.ShowVenueOsBranding;
        if (UiKit.Toggle(theme, "Show VenueOS branding", ref showBranding)) ApplyBranding(theme, branding with { ShowVenueOsBranding = showBranding });
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextWrapped("Display the small \"VenueOS\" label on the bottom of the tablet frame.");
        ImGui.PopStyleColor();
    }

    private void ApplyBranding(VenueTheme theme, VenueBranding branding) => venues.SetTheme(venues.Current.Id, theme with { Branding = branding });
    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
