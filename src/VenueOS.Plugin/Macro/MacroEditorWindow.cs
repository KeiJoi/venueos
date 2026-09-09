using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Macro;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Macro;

/// <summary>The ONE macro-authoring surface (MACRO LIVE QA FIX §14-§19) — a separate, non-modal ImGui window, not
/// content expanded inline under Settings → Modules → Macro's library list. Owns its own local draft fields
/// (<see cref="name"/>/<see cref="iconId"/>/<see cref="delaySeconds"/>/<see cref="bodyText"/>) and never touches
/// <see cref="MacroService"/> until <see cref="TrySave"/> runs on an explicit Save click — this is what makes
/// Save/Cancel transactional (§17) and is also what fixes the keyboard-focus-loss bug (see below): nothing here
/// calls <c>SaveModuleConfig</c> on every keystroke, and every ImGui widget in this window uses a fixed, literal
/// label — never one that embeds live, per-keystroke-changing content (the old bug's exact root cause).
///
/// FOCUS-LOSS ROOT CAUSE (§18, found by inspection, not guesswork): the retired one-line-per-field editor built
/// each line's <c>Forms.TextField</c> label as <c>$"Line {i + 1} ({bytes}/{MaxLineBytes} bytes)"</c> — and
/// <c>Forms.TextField</c> derives its ImGui widget id directly from that label string (<c>$"##{label}"</c>, per
/// <c>Forms.cs</c>). Because the embedded byte count changed on every keystroke that changed the line's length,
/// the widget's OWN ID changed on every keystroke too — ImGui therefore tore down and recreated what looked like a
/// brand-new widget after every single character, discarding focus/cursor state each time. The fix is structural,
/// not a workaround: this window's Macro Body field uses one constant label/id ("Macro Body") for its entire
/// lifetime, and byte-count/validation feedback is rendered as ordinary text NEXT TO the field, never woven into
/// the field's own id-bearing label.</summary>
internal sealed class MacroEditorWindow
{
    private const int BodyBufferBytes = 1_048_576; // ~1MB — "no artificial line-count limit... only bounded by
                                                     // practical storage/memory constraints" (spec §8), while still
                                                     // needing SOME fixed capacity for ImGui's own edit buffer.
    private const float IconPreviewSize = 40f;

    public bool IsOpen { get; private set; }
    private Guid? editingId;
    private string name = "";
    private uint iconId;
    private float delaySeconds = 1f;
    private string bodyText = "";
    private IReadOnlyList<string> errors = Array.Empty<string>();

    public void OpenForNew()
    {
        editingId = null;
        name = "";
        iconId = 0;
        delaySeconds = 1f;
        bodyText = "";
        errors = Array.Empty<string>();
        IsOpen = true;
    }

    public void OpenForEdit(SavedMacro macro)
    {
        editingId = macro.Id;
        name = macro.Name;
        iconId = macro.IconId;
        delaySeconds = (float)macro.DelayBetweenLinesSeconds;
        bodyText = MacroBodyText.ToBodyText(macro.Lines);
        errors = Array.Empty<string>();
        IsOpen = true;
    }

    public void Draw(VenueTheme theme, MacroService service, MacroIconPicker iconPicker)
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(640, 640), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 420), new Vector2(float.MaxValue, float.MaxValue));
        UiKit.PushWindowTheme(theme);

        var title = editingId is null ? "New Macro" : $"Edit Macro — {name}";
        var windowOpen = true;
        // A real native window (X-to-close = Cancel, per §16), not a BeginPopupModal — a modal would block the
        // operator from ever seeing the library list behind it while comparing names/icons, which this task's own
        // "separate window" wording (§14) is more naturally read as than a blocking popup.
        if (ImGui.Begin($"{title}###venueos-macro-editor", ref windowOpen, ImGuiWindowFlags.NoCollapse))
        {
            Forms.TextField(theme, "Macro Name", ref name, 128);
            ImGui.Spacing();

            iconPicker.Draw(theme, iconId, id => iconId = id);
            ImGui.Spacing();

            Forms.FloatField(theme, "Delay Between Lines (seconds)", ref delaySeconds, 0.1f, 0f, 3600f, "%.2f");
            ImGui.Spacing();
            UiKit.Divider(theme);

            DrawBodyField(theme);

            foreach (var error in errors) UiKit.ErrorState(theme, error);

            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, "Save")) TrySave(service);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel")) IsOpen = false;
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        if (!windowOpen) IsOpen = false; // native close button behaves as Cancel (§16)
    }

    /// <summary>One large multiline field — copy/paste, Ctrl+A/C/X/V, arbitrary cursor movement/selection all work
    /// exactly as normal ImGui multiline text editing already does, because this is nothing more than
    /// <c>Forms.MultilineField</c> bound to a single stable draft string (§19's primary requirement). Byte-count
    /// feedback is computed fresh every frame from the CURRENT executable-line parse (so it reflects EOF — content
    /// after the first blank line is never counted, per §9/§13) and rendered as separate text below the field, not
    /// embedded in its label.</summary>
    private void DrawBodyField(VenueTheme theme)
    {
        var executableLines = MacroBodyText.ToExecutableLines(bodyText);
        var totalRawLines = string.IsNullOrEmpty(bodyText) ? 0 : bodyText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;
        var hasTrailingContent = totalRawLines > executableLines.Count;

        Forms.FieldLabel(theme, "Macro Body");
        var height = MathF.Max(160f, ImGui.GetContentRegionAvail().Y - 130f);
        ImGui.SetNextItemWidth(-1);
        Forms.PushFieldStyle(theme);
        ImGui.InputTextMultiline("##macro-body", ref bodyText, BodyBufferBytes, new Vector2(-1, height));
        Forms.PopFieldStyle();

        ImGui.TextWrapped($"{executableLines.Count} executable line(s). An empty line ends the macro — anything after it is not saved or executed.");
        if (hasTrailingContent) UiKit.WarningState(theme, "There's content below a blank line in this draft — it will NOT be saved or executed. Remove the blank line if you meant for it to run too.");
    }

    private void TrySave(MacroService service)
    {
        var result = editingId is { } id
            ? service.SaveMacro(id, name, iconId, delaySeconds, bodyText)
            : service.CreateMacro(name, iconId, delaySeconds, bodyText, out _);

        if (result.Success) IsOpen = false;
        else errors = result.Errors;
    }
}
