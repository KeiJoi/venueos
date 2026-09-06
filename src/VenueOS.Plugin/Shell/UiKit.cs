using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Reusable VenueOS component library. Every color comes from <see cref="VenueTheme"/> tokens — nothing
/// here hard-codes a hex value, so a venue's theme change is reflected everywhere these helpers are used.</summary>
internal static class UiKit
{
    public static Vector4 Color(string hex)
    {
        var text = hex.TrimStart('#');
        return text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1)
            : Vector4.One;
    }

    public static uint ColorU32(string hex, float alpha = 1f)
    {
        var color = Color(hex); color.W = alpha; return ImGui.ColorConvertFloat4ToU32(color);
    }

    public static Vector4 WithAlpha(string hex, float alpha) { var color = Color(hex); color.W = alpha; return color; }

    /// <summary>The theme-to-ImGui-style push every VenueOS-owned window (main tablet, detached module windows)
    /// applies before drawing, so every window looks like the same product regardless of how many are open.</summary>
    public static void PushWindowTheme(VenueTheme theme)
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Color(theme.Tokens.Background));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Color(theme.Tokens.Surface));
        ImGui.PushStyleColor(ImGuiCol.Button, Color(theme.Tokens.Primary));
        ImGui.PushStyleColor(ImGuiCol.Header, Color(theme.Tokens.Selected));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, theme.Metrics.Rounding + 4);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Metrics.Rounding);
    }
    public static void PopWindowTheme() { ImGui.PopStyleVar(2); ImGui.PopStyleColor(4); }

    /// <summary>A subtle, theme-derived frame drawn at the tablet's actual live window bounds — recomputed every
    /// frame from <see cref="ImGui.GetWindowPos"/>/<see cref="ImGui.GetWindowSize"/>, so it tracks resizing and
    /// movement automatically with no per-venue code. Purely a decorative overlay on the window's own draw list, so
    /// it doesn't block a future image-based venue background from sharing the same window bounds.</summary>
    public static void DrawVenueFrame(VenueTheme theme)
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = theme.Metrics.Rounding + 4;
        drawList.AddRect(min, max, ColorU32(theme.Tokens.Accent, 0.85f), rounding, ImDrawFlags.None, 2.5f);
        drawList.AddRect(min + new Vector2(3, 3), max - new Vector2(3, 3), ColorU32(theme.Tokens.Primary, 0.45f), rounding, ImDrawFlags.None, 1f);
    }

    /// <summary>Restrained "VENUEOS" chassis branding along the bottom edge. The caller must reserve a short strip
    /// below its content (see the negative content-child height in <c>Plugin.Draw</c>) so this never sits on top of
    /// module content.</summary>
    public static void DrawChassisBrand(VenueTheme theme)
    {
        const string brand = "VENUEOS";
        ImGui.PushStyleColor(ImGuiCol.Text, WithAlpha(theme.Tokens.TextSecondary, 0.5f));
        var textWidth = ImGui.CalcTextSize(brand).X;
        // Content-region width in SetCursorPosX's own coordinate frame — GetWindowWidth() would include both
        // window paddings and center against the wrong (larger) width, the same mistake fixed in TabletHeader.
        var contentWidth = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(MathF.Max(0, (contentWidth - textWidth) / 2f));
        ImGui.TextUnformatted(brand);
        ImGui.PopStyleColor();
    }

    /// <summary>Runs a module's <c>Draw()</c> with the same failure isolation <see cref="VenueOS.Core.ModuleHost"/>
    /// already applies to Tick/Init/VenueChanged — a UI exception in one module must not take down the window
    /// hosting it (the main tablet or a detached window) or any other module.</summary>
    public static void SafeDraw(VenueTheme theme, DiagnosticsService diagnostics, string moduleId, Action draw)
    {
        try { draw(); }
        catch (Exception ex)
        {
            diagnostics.RecordFailure($"UI render failed in {moduleId}: {ex.Message}");
            ErrorState(theme, "This screen hit an error and could not render. See Settings → Diagnostics.");
        }
    }

    public static void BeginCard(string id, VenueTheme theme, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Color(theme.Tokens.RaisedSurface));
        ImGui.PushStyleColor(ImGuiCol.Border, Color(theme.Tokens.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, theme.Metrics.Rounding + 4);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(theme.Metrics.Padding, theme.Metrics.Padding));
        ImGui.BeginChild(id, size, true);
        ImGui.PopStyleVar(2); ImGui.PopStyleColor(2);
    }
    public static void EndCard() => ImGui.EndChild();

    public static bool PrimaryButton(VenueTheme theme, string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Color(theme.Tokens.Primary));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Color(theme.Tokens.Accent));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Color(theme.Tokens.Selected));
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextPrimary));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Metrics.Rounding);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool GhostButton(VenueTheme theme, string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Color(theme.Tokens.RaisedSurface));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Color(theme.Tokens.Border));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Color(theme.Tokens.Selected));
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextSecondary));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Metrics.Rounding);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool DangerButton(VenueTheme theme, string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Color(theme.Tokens.Error));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, WithAlpha(theme.Tokens.Error, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, WithAlpha(theme.Tokens.Error, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Metrics.Rounding);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool IconButton(VenueTheme theme, string id, string iconKey, float size = 32, string? tooltip = null, bool active = false)
    {
        ImGui.PushID(id);
        var start = ImGui.GetCursorScreenPos();
        var dimensions = new Vector2(size, size);
        var clicked = ImGui.InvisibleButton("##iconbtn", dimensions);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var background = active ? theme.Tokens.Selected : hovered ? theme.Tokens.RaisedSurface : theme.Tokens.Surface;
        drawList.AddRectFilled(start, start + dimensions, ColorU32(background), theme.Metrics.Rounding);
        if (active || hovered) drawList.AddRect(start, start + dimensions, ColorU32(theme.Tokens.Border), theme.Metrics.Rounding, ImDrawFlags.None, 1f);
        AppIcons.Draw(iconKey, start + dimensions / 2, size * 0.7f, ColorU32(theme.Tokens.TextPrimary));
        if (hovered && tooltip is not null) ImGui.SetTooltip(tooltip);
        ImGui.PopID();
        return clicked;
    }

    public static void StatusBadge(VenueTheme theme, string text, ToastLevel level)
    {
        var color = level switch { ToastLevel.Success => theme.Tokens.Success, ToastLevel.Warning => theme.Tokens.Warning, ToastLevel.Error => theme.Tokens.Error, _ => theme.Tokens.Accent };
        var padding = new Vector2(theme.Metrics.Padding * 0.6f, theme.Metrics.Padding * 0.25f);
        var textSize = ImGui.CalcTextSize(text);
        var start = ImGui.GetCursorScreenPos();
        var size = textSize + padding * 2;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + size, ColorU32(color, 0.22f), theme.Metrics.Rounding);
        ImGui.GetWindowDrawList().AddText(start + padding, ColorU32(color), text);
        ImGui.Dummy(size);
    }

    public static void SectionHeader(VenueTheme theme, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
        Divider(theme);
    }

    public static void Divider(VenueTheme theme)
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, Color(theme.Tokens.Border));
        ImGui.Separator();
        ImGui.PopStyleColor();
    }

    public static bool Toggle(VenueTheme theme, string label, ref bool value)
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Color(theme.Tokens.RaisedSurface));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Color(theme.Tokens.Accent));
        var changed = ImGui.Checkbox(label, ref value);
        ImGui.PopStyleColor(2);
        return changed;
    }

    public static bool ListRow(VenueTheme theme, string label, string? subtitle, bool selected)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, Color(theme.Tokens.Selected));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Color(theme.Tokens.RaisedSurface));
        var clicked = ImGui.Selectable($"{label}##row", selected, ImGuiSelectableFlags.None, new Vector2(0, subtitle is null ? 0 : 34));
        if (subtitle is not null) { ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextSecondary)); ImGui.TextUnformatted(subtitle); ImGui.PopStyleColor(); }
        ImGui.PopStyleColor(2);
        return clicked;
    }

    public static void EmptyState(VenueTheme theme, string title, string subtitle)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextSecondary));
        var region = ImGui.GetContentRegionAvail();
        ImGui.Dummy(new Vector2(0, region.Y * 0.3f));
        CenteredText(title);
        CenteredText(subtitle);
        ImGui.PopStyleColor();
    }

    public static void WarningState(VenueTheme theme, string message) => InlineState(theme, message, theme.Tokens.Warning);
    public static void ErrorState(VenueTheme theme, string message) => InlineState(theme, message, theme.Tokens.Error);
    public static void LoadingState(VenueTheme theme, string message) => InlineState(theme, message, theme.Tokens.Accent);
    private static void InlineState(VenueTheme theme, string message, string color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Color(color));
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }

    /// <summary>A dot-plus-label connection indicator (backend URL/room/session style status), distinct from
    /// <see cref="StatusBadge"/>'s filled pill — used where the state is ambient rather than a discrete event.</summary>
    public static void ConnectionBadge(VenueTheme theme, string label, bool connected)
    {
        var start = ImGui.GetCursorScreenPos(); var radius = 4f;
        ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(radius, radius + 2), radius, ColorU32(connected ? theme.Tokens.Success : theme.Tokens.TextSecondary));
        ImGui.Dummy(new Vector2(radius * 2 + 6, radius * 2 + 4));
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Color(connected ? theme.Tokens.TextPrimary : theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    private enum SectionCardMode { ChildWindow, Split, Nested }

    /// <summary>Bookkeeping captured at <see cref="BeginSectionCard"/> time and consumed at
    /// <see cref="EndSectionCard"/> — lets <c>EndSectionCard()</c> keep its existing no-argument signature (every
    /// call site across the codebase calls it bare) while still knowing what theme colors/rounding to draw with and
    /// where the card actually started.</summary>
    private readonly record struct SectionCardFrame(SectionCardMode Mode, Vector2 Start, float Width, float Padding, uint BgColor, uint BorderColor, float Rounding);
    private static readonly Stack<SectionCardFrame> sectionCardFrames = new();

    /// <summary>A card that opens with its own section heading — the common "Titled Card" shape used across every
    /// module screen. Auto-sizes to its own content's real height instead of using a bordered child window: this
    /// ImGui binding's <c>BeginChild(id, size, ...)</c> treats a size axis of exactly 0 as "fill 100% of whatever
    /// space remains in the parent," not "auto-fit to content" (the same finding already noted on
    /// <see cref="InfoBanner"/>'s doc comment, which works around it for a single measurable text block). Every
    /// current call site passes the default size, and several operator/settings screens stack multiple section
    /// cards in a row (Attendance's Venue Details + Presence Filtering, Greeter's Behavior + Hotbar Slot
    /// Assignments + Saved Presets + the preset editor). With the old child-window implementation, the *first* card
    /// in such a stack greedily claimed effectively all the remaining vertical space, leaving every card after it
    /// squeezed into a near-zero-height, practically unreachable sliver.
    ///
    /// The fix draws the card's background *after* its content is drawn and its real extent is known, via
    /// <see cref="ImDrawListPtr.ChannelsSplit"/> (background on channel 0, content on channel 1, merged back at
    /// <see cref="EndSectionCard"/>) — content is never placed inside a clipping child window, so nothing drawn
    /// inside a section card can be cut off by its own card. <c>ChannelsSplit</c>/<c>ChannelsMerge</c> operate on
    /// one non-reentrant splitter *owned by the current window's draw list* — two section cards can never have an
    /// active split on the *same* draw list at once. Every leaf-level call site only ever draws one section card at
    /// a time (verified by grep), but <see cref="ModulesSettingsPage.DrawConfigureDetail"/> used to wrap a module's
    /// entire <c>DrawSettings()</c> in its own extra section card — meaning the module's *own* first section card
    /// (Attendance's "Venue Details", Greeter's "Behavior", VIP's "Recognition Message") started a *second* split on
    /// the same draw list while the outer wrapper's split was still open, corrupting both: content silently drawn
    /// to the wrong/discarded channel or reordered behind a later background fill, which is why every affected
    /// screen rendered as one big empty card. That outer wrapper card has been removed (see
    /// <c>ModulesSettingsPage.cs</c>) — a module's <c>DrawSettings()</c> now renders directly under the "← Back to
    /// Modules" button, exactly like the operational <c>Draw()</c> path never wraps <c>module.Draw()</c> in an extra
    /// card either. As a second line of defense, if <see cref="BeginSectionCard"/> is ever called again while
    /// another one is still open (<paramref name="id"/> nested inside another card's content), it degrades to a
    /// plain header with no background/split instead of corrupting the outer card's rendering — a purely cosmetic
    /// fallback for a configuration that should never occur, not a supported layout. An explicit non-default
    /// <paramref name="size"/> still uses the original bordered-child-window behavior (no current call site passes
    /// one, but the option is kept for a section that genuinely wants its own fixed or independently-scrolling
    /// region).</summary>
    public static void BeginSectionCard(string id, VenueTheme theme, string title, Vector2 size = default)
    {
        if (size.X > 0 || size.Y > 0)
        {
            BeginCard(id, theme, size);
            DrawSectionCardHeader(theme, title);
            sectionCardFrames.Push(new(SectionCardMode.ChildWindow, default, 0, 0, 0, 0, 0));
            return;
        }

        if (sectionCardFrames.Count > 0)
        {
            ImGui.PushID(id);
            DrawSectionCardHeader(theme, title);
            sectionCardFrames.Push(new(SectionCardMode.Nested, default, 0, 0, 0, 0, 0));
            return;
        }

        ImGui.PushID(id);
        var padding = new Vector2(theme.Metrics.Padding, theme.Metrics.Padding);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + padding);
        ImGui.BeginGroup();
        DrawSectionCardHeader(theme, title);
        sectionCardFrames.Push(new(SectionCardMode.Split, start, width, theme.Metrics.Padding, ColorU32(theme.Tokens.RaisedSurface), ColorU32(theme.Tokens.Border), theme.Metrics.Rounding + 4));
    }

    public static void EndSectionCard()
    {
        var frame = sectionCardFrames.Pop();
        switch (frame.Mode)
        {
            case SectionCardMode.ChildWindow: EndCard(); return;
            case SectionCardMode.Nested: ImGui.PopID(); return;
        }

        ImGui.Dummy(new Vector2(0, frame.Padding));
        ImGui.EndGroup();
        var contentMax = ImGui.GetItemRectMax();

        var boxMin = frame.Start;
        var boxMax = new Vector2(frame.Start.X + frame.Width, contentMax.Y);
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(boxMin, boxMax, frame.BgColor, frame.Rounding);
        drawList.AddRect(boxMin, boxMax, frame.BorderColor, frame.Rounding);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(boxMin.X, boxMax.Y));
        ImGui.PopID();
    }

    private static void DrawSectionCardHeader(VenueTheme theme, string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        Divider(theme);
        ImGui.Spacing();
    }

    public static void Tooltip(string text) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(text); }

    /// <summary>A sidebar navigation entry: icon tile + title + short description, with an unmistakable selected
    /// state — the Settings app's navigation language (and reusable anywhere else a similar left-nav appears).</summary>
    public static bool NavRow(VenueTheme theme, string id, string iconKey, string title, string subtitle, bool selected)
    {
        ImGui.PushID(id);
        var height = 56f;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.InvisibleButton("##navrow", new Vector2(width, height));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        if (selected)
        {
            drawList.AddRectFilled(start, start + new Vector2(width, height), ColorU32(theme.Tokens.Selected), theme.Metrics.Rounding + 2);
            drawList.AddRectFilledMultiColor(start, start + new Vector2(width, height), ColorU32(theme.Tokens.Primary, 0.9f), ColorU32(theme.Tokens.Accent, 0.9f), ColorU32(theme.Tokens.Accent, 0.9f), ColorU32(theme.Tokens.Primary, 0.9f));
        }
        else if (hovered) drawList.AddRectFilled(start, start + new Vector2(width, height), ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);

        var iconBox = new Vector2(36, 36);
        var iconStart = start + new Vector2(10, (height - iconBox.Y) / 2);
        drawList.AddRectFilled(iconStart, iconStart + iconBox, ColorU32(selected ? theme.Tokens.Selected : theme.Tokens.RaisedSurface), theme.Metrics.Rounding);
        AppIcons.Draw(iconKey, iconStart + iconBox / 2, iconBox.X * 0.6f, ColorU32(selected ? WhiteHex : theme.Tokens.TextPrimary));

        var textX = iconStart.X + iconBox.X + 12;
        drawList.AddText(new Vector2(textX, start.Y + 8), ColorU32(selected ? WhiteHex : theme.Tokens.TextPrimary), title);
        drawList.AddText(new Vector2(textX, start.Y + 28), ColorU32(selected ? WhiteHex : theme.Tokens.TextSecondary, selected ? 0.85f : 1f), subtitle);

        ImGui.PopID();
        return clicked;
    }
    private const string WhiteHex = "#FFFFFF";

    /// <summary>A tinted, icon-led informational/warning card — for "here's what this page does" and similar
    /// non-error explanatory text, distinct from <see cref="ErrorState"/>/<see cref="WarningState"/> which report
    /// an actual problem. Sized from measured wrapped-text height rather than <c>BeginChild</c> auto-sizing, which
    /// this ImGui binding doesn't support for children (a 0-height child fills its parent instead of shrinking).</summary>
    public static void InfoBanner(VenueTheme theme, string title, string message, ToastLevel level = ToastLevel.Information)
    {
        var color = level switch { ToastLevel.Warning => theme.Tokens.Warning, ToastLevel.Error => theme.Tokens.Error, _ => theme.Tokens.Accent };
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(theme.Metrics.Padding, theme.Metrics.Padding * 0.8f);
        const float iconColumn = 28f;
        var textX = start.X + padding.X + iconColumn;
        var wrapWidth = MathF.Max(80f, width - padding.X - iconColumn - padding.X);
        var titleLineHeight = ImGui.GetTextLineHeightWithSpacing();
        var messageSize = ImGui.CalcTextSize(message, false, wrapWidth);
        var contentHeight = padding.Y * 2 + titleLineHeight + messageSize.Y;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + new Vector2(width, contentHeight), ColorU32(color, 0.12f), theme.Metrics.Rounding + 2);
        drawList.AddRect(start, start + new Vector2(width, contentHeight), ColorU32(color, 0.35f), theme.Metrics.Rounding + 2, ImDrawFlags.None, 1f);
        AppIcons.Draw("circle-question", start + new Vector2(padding.X + 9, padding.Y + titleLineHeight / 2), 10, ColorU32(color));

        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y + padding.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Color(color));
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();

        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y + padding.Y + titleLineHeight));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(message);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(start + new Vector2(0, contentHeight + 4));
    }

    /// <summary>A compact icon + big value + label tile, for a row of at-a-glance counters (Diagnostics' summary
    /// strip: total/info/warning/error counts, last-update time).</summary>
    public static void StatCard(VenueTheme theme, string iconKey, string value, string label, string accent)
    {
        var size = new Vector2(150, 60);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ColorU32(theme.Tokens.RaisedSurface), theme.Metrics.Rounding + 2);
        AppIcons.Draw(iconKey, start + new Vector2(20, size.Y / 2), 12, ColorU32(accent));
        drawList.AddText(start + new Vector2(36, 10), ColorU32(theme.Tokens.TextPrimary), value);
        drawList.AddText(start + new Vector2(36, 32), ColorU32(theme.Tokens.TextSecondary), label);
        ImGui.Dummy(size);
    }

    /// <summary>A minimal two-series line chart drawn directly on the window's draw list — no external charting
    /// library, matching the donor (<c>venuestatusandgreet</c>'s <c>MainWindow.DrawLineChart</c>) and this
    /// codebase's existing "reserve the region, draw with absolute screen coordinates" pattern rather than pulling
    /// in a dependency for two polylines. Used by Attendance's guest-sample and Max/Min comparison charts.</summary>
    public static void LineChart(VenueTheme theme, string id, string legend, IReadOnlyList<float> primary, string primaryColorHex, IReadOnlyList<float>? secondary = null, string? secondaryColorHex = null, float height = 160f)
    {
        var size = new Vector2(MathF.Max(120f, ImGui.GetContentRegionAvail().X), height);
        ImGui.InvisibleButton(id, size);
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        drawList.AddRectFilled(min, max, ColorU32(theme.Tokens.RaisedSurface, 0.6f), theme.Metrics.Rounding);
        drawList.AddRect(min, max, ColorU32(theme.Tokens.Border), theme.Metrics.Rounding, ImDrawFlags.None, 1f);
        drawList.AddText(min + new Vector2(8, 6), ColorU32(theme.Tokens.TextPrimary, 0.9f), legend);

        var combined = secondary is null ? primary : primary.Concat(secondary).ToArray();
        if (combined.Count == 0) { drawList.AddText(min + new Vector2(8, 28), ColorU32(theme.Tokens.TextSecondary), "No data yet"); return; }

        var lowValue = MathF.Min(0f, combined.Min());
        var highValue = MathF.Max(1f, combined.Max());
        LineChartSeries(drawList, min, max, primary, lowValue, highValue, ColorU32(primaryColorHex));
        if (secondary is { Count: > 0 } && secondaryColorHex is not null) LineChartSeries(drawList, min, max, secondary, lowValue, highValue, ColorU32(secondaryColorHex));
    }

    private static void LineChartSeries(ImDrawListPtr drawList, Vector2 graphMin, Vector2 graphMax, IReadOnlyList<float> values, float lowValue, float highValue, uint color)
    {
        if (values.Count == 0) return;
        var left = graphMin.X + 8f; var right = graphMax.X - 8f; var top = graphMin.Y + 24f; var bottom = graphMax.Y - 10f;
        var width = MathF.Max(1f, right - left); var height = MathF.Max(1f, bottom - top); var span = MathF.Max(1f, highValue - lowValue);
        Vector2? previous = null;
        for (var i = 0; i < values.Count; i++)
        {
            var x = left + (values.Count == 1 ? 0f : i / (float)(values.Count - 1)) * width;
            var y = bottom - (values[i] - lowValue) / span * height;
            var point = new Vector2(x, y);
            if (previous is { } p) drawList.AddLine(p, point, color, 2f);
            drawList.AddCircleFilled(point, 2f, color);
            previous = point;
        }
    }

    private static void CenteredText(string text)
    {
        var width = ImGui.GetContentRegionAvail().X; var textWidth = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (width - textWidth) / 2));
        ImGui.TextUnformatted(text);
    }
}

/// <summary>Single-instance confirmation modal, reused wherever a destructive action (venue deletion) needs an
/// explicit yes/no gate instead of firing immediately on click.</summary>
internal sealed class ConfirmDialog
{
    private const string PopupId = "Confirm##venueos-confirm-dialog";
    private string title = ""; private string message = ""; private Action? onConfirm; private bool openRequested; private bool windowOpen = true;

    public void Request(string title, string message, Action onConfirm)
    {
        this.title = title; this.message = message; this.onConfirm = onConfirm; openRequested = true; windowOpen = true;
    }

    public void Draw(VenueTheme theme)
    {
        if (openRequested) { ImGui.OpenPopup(PopupId); openRequested = false; }
        ImGui.SetNextWindowSize(new Vector2(380, 0));
        if (ImGui.BeginPopupModal(PopupId, ref windowOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(title);
            UiKit.Divider(theme);
            ImGui.TextWrapped(message);
            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, "Confirm")) { onConfirm?.Invoke(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}

/// <summary>Single-instance "one field, one confirm" modal — used for "+ Add Venue" so venue creation is an
/// intentional dialog flow instead of a permanently-visible bare input field sitting in the venue list.</summary>
internal sealed class TextInputModal
{
    private const string PopupId = "Add##venueos-text-input-modal";
    private string title = ""; private string label = ""; private string hint = ""; private string value = ""; private Action<string>? onSubmit; private bool openRequested; private bool windowOpen = true;

    public void Request(string title, string label, string hint, Action<string> onSubmit)
    {
        this.title = title; this.label = label; this.hint = hint; this.onSubmit = onSubmit; value = ""; openRequested = true; windowOpen = true;
    }

    public void Draw(VenueTheme theme)
    {
        if (openRequested) { ImGui.OpenPopup(PopupId); openRequested = false; }
        ImGui.SetNextWindowSize(new Vector2(360, 0));
        if (ImGui.BeginPopupModal(PopupId, ref windowOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(title);
            UiKit.Divider(theme);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            Forms.TextField(theme, label, ref value, 64, hint);
            ImGui.Spacing();
            var canSubmit = !string.IsNullOrWhiteSpace(value);
            if (UiKit.PrimaryButton(theme, "Create") && canSubmit) { onSubmit?.Invoke(value.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}
