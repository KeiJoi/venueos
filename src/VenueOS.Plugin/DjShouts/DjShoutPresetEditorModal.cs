using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Modules.Operations.DjShouts;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.DjShouts;

/// <summary>
/// The single, dedicated authoring surface for a DJ Shout preset — mirrors <c>GiveawayPresetEditorModal</c>'s
/// working-copy discipline exactly (NEW_MODULE_GUIDE.md §13/§30's "state surviving a window close is not proof of
/// persistence" rule applies to the DRAFT here too, in the opposite direction: the draft must never leak into
/// persisted state before Save).
///
/// <b>State rule:</b> this modal owns a local, ephemeral <see cref="draft"/> — never
/// <see cref="DjShoutsService.Settings"/> directly. Every field edit only replaces <c>draft</c> with a new
/// <c>with</c>-expression copy; nothing is saved until Save is pressed.
/// <list type="bullet">
/// <item><b>New:</b> <see cref="OpenForNew"/> seeds <c>draft</c> from <see cref="DjShoutPreset.CreateNew"/>. Save
/// calls <see cref="DjShoutsService.SaveNewPreset"/> (one atomic persist, mints a fresh Id) — Cancel/closing via the
/// popup's own X discards <c>draft</c> with no call into the service at all, so nothing is ever created.</item>
/// <item><b>Edit:</b> <see cref="OpenForEdit"/> copies the existing persisted preset's field values into
/// <c>draft</c> (a record, so this is a genuine value copy). Save calls
/// <c>service.UpdatePreset(existingId, _ =&gt; draft with { Id = existingId })</c> — Cancel/X discards <c>draft</c>,
/// leaving the persisted preset exactly as it was.</item>
/// </list>
///
/// Per-line layout deliberately avoids <c>Forms.TextField</c>'s own embedded label for the line text box (unlike
/// <c>GiveawayPresetEditorModal.DrawBlockEditor</c>'s single-purpose text-only lines): <c>TextField</c> sizes its
/// input to fill the ENTIRE remaining row width (<c>ImGui.SetNextItemWidth(-1)</c>), which leaves no room for the
/// per-line channel selector and Up/Down/Remove controls this module also needs on the same row. Per
/// NEW_MODULE_GUIDE.md §35's reserved-region layout rule, the trailing controls' fixed width is computed first and
/// subtracted from the available width before the text box is drawn, using <c>Forms.PushFieldStyle</c>/<c>PopFieldStyle</c>
/// directly (the "rare inline case" those two are explicitly exposed for) instead of the full labeled
/// <c>TextField</c> component.
/// </summary>
internal sealed class DjShoutPresetEditorModal
{
    private const string PopupId = "DJ Shout##venueos-djshouts-preset-editor";
    private bool openRequested;
    private bool windowOpen = true;
    private Guid? editingExistingId; // null while authoring a brand-new preset
    private DjShoutPreset draft = DjShoutPreset.CreateNew("");
    private string? validationError;

    public void OpenForNew()
    {
        editingExistingId = null;
        draft = DjShoutPreset.CreateNew("");
        validationError = null;
        openRequested = true;
        windowOpen = true;
    }

    public void OpenForEdit(DjShoutPreset existing)
    {
        editingExistingId = existing.Id;
        draft = existing; // a working COPY by value (record) — never the list's own instance
        validationError = null;
        openRequested = true;
        windowOpen = true;
    }

    public void Draw(VenueTheme theme, DjShoutsService service)
    {
        if (openRequested) { ImGui.OpenPopup(PopupId); openRequested = false; }

        ImGui.SetNextWindowSize(new Vector2(640, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 360), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.BeginPopupModal(PopupId, ref windowOpen, ImGuiWindowFlags.NoTitleBar))
        {
            DialogHeader.Draw(theme, editingExistingId is null ? "New DJ Shout" : "Edit DJ Shout", ImGui.CloseCurrentPopup);

            ImGui.BeginChild("djshouts-preset-editor-content", new Vector2(0, -60f), false);
            DrawContent(theme);
            ImGui.EndChild();

            UiKit.Divider(theme);
            DrawFooter(theme, service);

            ImGui.EndPopup();
        }

        // draft is simply never persisted regardless of how the popup is dismissed (Close button or Escape) — the
        // next OpenForNew/OpenForEdit call resets draft fresh regardless of how the previous instance was dismissed.
    }

    private void DrawContent(VenueTheme theme)
    {
        var name = draft.Name;
        if (Forms.TextField(theme, "DJ Shout Name", ref name, 128)) draft = draft with { Name = name };

        ImGui.Spacing();
        UiKit.Divider(theme);
        DrawLinesEditor(theme);
    }

    private void DrawLinesEditor(VenueTheme theme)
    {
        // A stable snapshot for THIS frame's loop, mirroring GiveawayPresetEditorModal.DrawBlockEditor's `block`
        // parameter: a Remove/Move mid-loop reassigns `draft` immediately, but this local list keeps the remainder
        // of the current Draw() call's iteration consistent, deferring the visible reflow to the next frame.
        var lines = draft.Lines;
        UiKit.SectionHeader(theme, $"Lines ({lines.Count})");

        for (var i = 0; i < lines.Count; i++)
        {
            ImGui.PushID(i);
            DrawLineRow(theme, lines, i);
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "+ Line")) AddLine();
    }

    private void DrawLineRow(VenueTheme theme, IReadOnlyList<DjShoutLine> lines, int i)
    {
        var line = lines[i];

        const float upDownWidth = 34f;
        const float channelSegmentWidth = 130f; // two Segmented buttons ("Yell"/"Shout") plus their inner gap
        const float removeWidth = 70f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var trailingWidth = upDownWidth * 2 + channelSegmentWidth + removeWidth + spacing * 4;
        var textWidth = MathF.Max(160f, ImGui.GetContentRegionAvail().X - trailingWidth);

        Forms.PushFieldStyle(theme);
        ImGui.SetNextItemWidth(textWidth);
        var text = line.Text;
        // 600-char buffer (deliberately above the 500-BYTE chat limit): the operator must be able to type past the
        // limit to see the byte-count warning below rather than being silently capped mid-keystroke — task §20's
        // "do not silently truncate" requirement. Save/execution are what actually enforce the limit
        // (DjShoutPresetValidator), not this input box.
        var textChanged = ImGui.InputText("##line-text", ref text, 600);
        Forms.PopFieldStyle();
        if (textChanged) UpdateLine(i, line with { Text = text });

        ImGui.SameLine();
        var channelIndex = line.Channel == DjShoutChannel.Shout ? 1 : 0;
        if (Forms.Segmented(theme, "channel", ["Yell", "Shout"], ref channelIndex))
            UpdateLine(i, line with { Channel = channelIndex == 0 ? DjShoutChannel.Yell : DjShoutChannel.Shout });

        ImGui.SameLine();
        ImGui.BeginDisabled(i == 0);
        if (UiKit.GhostButton(theme, "Up", new Vector2(upDownWidth, 0))) MoveLine(i, i - 1);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(i == lines.Count - 1);
        if (UiKit.GhostButton(theme, "Dn", new Vector2(upDownWidth, 0))) MoveLine(i, i + 1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        // A per-LINE Remove inside an unsaved draft is fully reversible via the modal's own Cancel — GhostButton,
        // not DangerButton (DangerButton is reserved for the actually-persisted preset Delete in Settings' list).
        if (UiKit.GhostButton(theme, "Remove", new Vector2(removeWidth, 0))) RemoveLine(i);

        var bytes = DjShoutLineBytes.CountBytes(line);
        if (!string.IsNullOrWhiteSpace(line.Text) && bytes > BlockLettersLimits.ChatBytes)
        {
            var channelWord = line.Channel == DjShoutChannel.Yell ? "yell" : "shout";
            UiKit.WarningState(theme, $"{bytes} / {BlockLettersLimits.ChatBytes} bytes once its /{channelWord} command is included — shorten this line.");
        }

        ImGui.Spacing();
    }

    private void UpdateLine(int index, DjShoutLine updated)
    {
        var lines = draft.Lines.ToArray();
        lines[index] = updated;
        draft = draft with { Lines = lines };
    }

    private void RemoveLine(int index) => draft = draft with { Lines = draft.Lines.Where((_, idx) => idx != index).ToArray() };

    private void MoveLine(int from, int to)
    {
        if (to < 0 || to >= draft.Lines.Count) return;
        var list = draft.Lines.ToList();
        (list[from], list[to]) = (list[to], list[from]);
        draft = draft with { Lines = list };
    }

    private void AddLine() => draft = draft with { Lines = [.. draft.Lines, DjShoutLine.Empty()] };

    private void DrawFooter(VenueTheme theme, DjShoutsService service)
    {
        if (!string.IsNullOrEmpty(validationError)) UiKit.ErrorState(theme, validationError);

        if (UiKit.PrimaryButton(theme, "Save")) TrySave(service);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Cancel")) ImGui.CloseCurrentPopup(); // draft is simply discarded — nothing was ever persisted
    }

    private void TrySave(DjShoutsService service)
    {
        var errors = DjShoutPresetValidator.Validate(draft);
        if (errors.Count > 0) { validationError = string.Join(" ", errors); return; }

        if (editingExistingId is { } id) service.UpdatePreset(id, _ => draft with { Id = id });
        else service.SaveNewPreset(draft);

        ImGui.CloseCurrentPopup();
    }
}
