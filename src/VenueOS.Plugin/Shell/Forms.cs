using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>VenueOS form-field language: a label above a consistently styled frame, instead of raw ImGui's
/// "[field] Label" layout. Callers wrap repeated groups (list rows, per-preset editors) in <c>ImGui.PushID</c> as
/// usual — these helpers key their internal widgets off the visible label, same as the rest of the codebase.</summary>
internal static class Forms
{
    public static bool TextField(VenueTheme theme, string label, ref string value, int maxLength = 256, string? hint = null, bool password = false)
    {
        FieldLabel(theme, label);
        ImGui.SetNextItemWidth(-1);
        PushFieldStyle(theme);
        var flags = password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None;
        var changed = hint is null ? ImGui.InputText($"##{label}", ref value, maxLength, flags) : ImGui.InputTextWithHint($"##{label}", hint, ref value, maxLength, flags);
        PopFieldStyle();
        return changed;
    }

    public static bool MultilineField(VenueTheme theme, string label, ref string value, int maxLength = 1024, float height = 72)
    {
        FieldLabel(theme, label);
        ImGui.SetNextItemWidth(-1);
        PushFieldStyle(theme);
        var changed = ImGui.InputTextMultiline($"##{label}", ref value, maxLength, new Vector2(-1, height));
        PopFieldStyle();
        return changed;
    }

    public static bool NumericField(VenueTheme theme, string label, ref int value, int step = 1, int min = int.MinValue, int max = int.MaxValue)
    {
        FieldLabel(theme, label);
        ImGui.SetNextItemWidth(-1);
        PushFieldStyle(theme);
        var changed = ImGui.InputInt($"##{label}", ref value, step);
        PopFieldStyle();
        if (changed) value = Math.Clamp(value, min, max);
        return changed;
    }

    public static bool FloatField(VenueTheme theme, string label, ref float value, float step = 1f, float min = float.NegativeInfinity, float max = float.PositiveInfinity, string format = "%.2f")
    {
        FieldLabel(theme, label);
        ImGui.SetNextItemWidth(-1);
        PushFieldStyle(theme);
        var changed = ImGui.InputFloat($"##{label}", ref value, step, step * 10, format);
        PopFieldStyle();
        if (changed) value = Math.Clamp(value, min, max);
        return changed;
    }

    public static bool ComboField(VenueTheme theme, string label, IReadOnlyList<string> options, ref int index, float width = -1)
    {
        FieldLabel(theme, label);
        ImGui.SetNextItemWidth(width);
        PushFieldStyle(theme);
        var changed = false;
        if (ImGui.BeginCombo($"##{label}", index >= 0 && index < options.Count ? options[index] : ""))
        {
            for (var i = 0; i < options.Count; i++)
                // The widget id is derived from the *position* (##opt{i}), not the display text alone — two options
                // that happen to share the same visible text (e.g. two Greeter presets deliberately allowed to share
                // a name, per this codebase's own dropped UNIQUE(venue_id, name) constraint) must never collide on
                // ImGui's item id, which would otherwise make one of them unselectable/indistinguishable from the
                // other. The visible label is unaffected — everything before "##" is still shown, unchanged.
                if (ImGui.Selectable($"{options[i]}##opt{i}", i == index)) { index = i; changed = true; }
            ImGui.EndCombo();
        }
        PopFieldStyle();
        return changed;
    }

    public static bool SearchBox(VenueTheme theme, string id, ref string query, string hint = "Search", float width = -1)
    {
        PushFieldStyle(theme);
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.InputTextWithHint($"##{id}", hint, ref query, 128);
        PopFieldStyle();
        return changed;
    }

    /// <summary>Mutually-exclusive segmented picker. Used both as a lightweight radio group (channel choice) and
    /// as VenueOS's tab strip (Settings categories) — one component instead of two near-identical ones.</summary>
    public static bool Segmented(VenueTheme theme, string id, IReadOnlyList<string> options, ref int index)
    {
        ImGui.PushID(id);
        var changed = false;
        for (var i = 0; i < options.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, 4);
            var active = i == index;
            if (active ? UiKit.PrimaryButton(theme, options[i]) : UiKit.GhostButton(theme, options[i])) { index = i; changed = true; }
        }
        ImGui.PopID();
        return changed;
    }

    public static void FieldLabel(VenueTheme theme, string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    /// <summary>Exposed for the rare inline case (a compact same-line list-row edit) where the full labeled
    /// <see cref="TextField"/> layout would break a row's shape — callers should still prefer <see cref="TextField"/>.</summary>
    internal static void PushFieldStyle(VenueTheme theme)
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiKit.Color(theme.Tokens.RaisedSurface));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, UiKit.Color(theme.Tokens.Border));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, UiKit.Color(theme.Tokens.Selected));
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, theme.Metrics.Rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(theme.Metrics.Padding * 0.7f, theme.Metrics.Padding * 0.5f));
    }
    internal static void PopFieldStyle() { ImGui.PopStyleVar(2); ImGui.PopStyleColor(4); }
}
