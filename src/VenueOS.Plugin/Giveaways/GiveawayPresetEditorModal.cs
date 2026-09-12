using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Giveaways;

/// <summary>
/// The single, dedicated authoring surface for a Giveaways preset (GIVEAWAYS live-QA fix #2, Issue 1). Replaces the
/// previous design, which rendered the ENTIRE preset editor (general/winner settings + up to 30 announcement lines)
/// permanently inline underneath the preset list in Settings → Modules → Giveaways — live QA reported this as far
/// too large for that page and the wrong workflow. The compact preset list now stays the only thing shown on the
/// main Settings page; this modal is the only place a preset's fields are ever edited.
///
/// <b>State rule (explicit product requirement):</b> this modal owns a local, ephemeral <see cref="draft"/> — never
/// the persisted <see cref="GiveawayService.Settings"/> record directly, and it is NOT authoritative persistent
/// state. Every field edit inside the modal only replaces <c>draft</c> with a new <c>with</c>-expression copy; it
/// never calls <c>GiveawayService.UpdatePreset</c>/<c>SaveModuleConfig</c> per keystroke, which is deliberately
/// different from every other immediately-persistent Settings control in this codebase (NEW_MODULE_GUIDE.md §13a
/// still applies — persistence is real — but it happens exactly once, on Save, not on every field change):
/// <list type="bullet">
/// <item><b>New:</b> <see cref="OpenForNew"/> seeds <c>draft</c> from <see cref="GiveawayPreset.CreateNew"/>. Save
/// calls <see cref="GiveawayService.SaveNewPreset"/> (one atomic persist, mints a fresh Id) — Cancel/closing via the
/// popup's own X discards <c>draft</c> with no call into the service at all, so nothing is ever created.</item>
/// <item><b>Edit:</b> <see cref="OpenForEdit"/> copies the existing persisted preset's field values into
/// <c>draft</c> (a record, so this is a genuine value copy — subsequent `draft = draft with {...}` edits can never
/// reach back and mutate the original persisted instance). Save calls
/// <c>service.UpdatePreset(existingId, _ =&gt; draft with { Id = existingId })</c> (one atomic persist, preserving
/// the stable Id) — Cancel/X discards <c>draft</c>, leaving the persisted preset exactly as it was.</item>
/// </list>
///
/// Save always runs <see cref="GiveawayPresetValidator.Validate"/> first; on any validation failure the modal
/// stays open and shows the error(s) inline rather than persisting an invalid preset (GIVEAWAYS spec §11's
/// pre-Start validation and this modal's Save both now go through the same validator).
/// </summary>
internal sealed class GiveawayPresetEditorModal
{
    private const string PopupId = "Giveaway Preset##venueos-giveaways-preset-editor";
    private bool openRequested;
    private bool windowOpen = true;
    private Guid? editingExistingId; // null while authoring a brand-new preset
    private GiveawayPreset draft = GiveawayPreset.CreateNew("");
    private string? validationError;

    public void OpenForNew()
    {
        editingExistingId = null;
        draft = GiveawayPreset.CreateNew("");
        validationError = null;
        openRequested = true;
        windowOpen = true;
    }

    public void OpenForEdit(GiveawayPreset existing)
    {
        editingExistingId = existing.Id;
        draft = existing; // a working COPY by value (record) — never the list's own instance
        validationError = null;
        openRequested = true;
        windowOpen = true;
    }

    public void Draw(VenueTheme theme, GiveawayService service)
    {
        if (openRequested) { ImGui.OpenPopup(PopupId); openRequested = false; }

        ImGui.SetNextWindowSize(new Vector2(620, 680), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 420), new Vector2(float.MaxValue, float.MaxValue));
        // 0.3.0 UI pass: NoTitleBar + DialogHeader replaces ImGui's raw native popup title bar, matching every
        // other VenueOS window/modal's chrome.
        if (ImGui.BeginPopupModal(PopupId, ref windowOpen, ImGuiWindowFlags.NoTitleBar))
        {
            DialogHeader.Draw(theme, editingExistingId is null ? "New Giveaway Preset" : "Edit Giveaway Preset", ImGui.CloseCurrentPopup);

            ImGui.BeginChild("giveaways-preset-editor-content", new Vector2(0, -60f), false);
            DrawContent(theme);
            ImGui.EndChild();

            UiKit.Divider(theme);
            DrawFooter(theme, service);

            ImGui.EndPopup();
        }

        // draft is simply never persisted regardless of how the popup is dismissed (Close button or Escape) —
        // "closing must behave like Cancel" holds with no extra bookkeeping needed here. The next
        // OpenForNew/OpenForEdit call resets draft fresh regardless of how the previous instance was dismissed.
    }

    private void DrawContent(VenueTheme theme)
    {
        var name = draft.Name;
        if (Forms.TextField(theme, "Preset Name", ref name, 128)) draft = draft with { Name = name };

        ImGui.Spacing();
        UiKit.Divider(theme);
        UiKit.SectionHeader(theme, "General");
        DrawGeneralSection(theme);

        ImGui.Spacing();
        UiKit.Divider(theme);
        UiKit.SectionHeader(theme, "Winner");
        DrawWinnerSection(theme);

        ImGui.Spacing();
        UiKit.Divider(theme);
        UiKit.SectionHeader(theme, "Winner Announcement");
        DrawWinnerAnnouncementSection(theme);

        ImGui.Spacing();
        UiKit.Divider(theme);
        DrawBlockEditor(theme, "Giveaway Start", draft.StartBlock, b => draft = draft with { StartBlock = b });

        ImGui.Spacing();
        UiKit.Divider(theme);
        DrawBlockEditor(theme, "Midpoint Announcement", draft.MidpointBlock, b => draft = draft with { MidpointBlock = b });

        ImGui.Spacing();
        UiKit.Divider(theme);
        DrawBlockEditor(theme, "Giveaway Closing", draft.ClosingBlock, b => draft = draft with { ClosingBlock = b });

        if (draft.HasMidpointOverlapRisk)
        {
            ImGui.Spacing();
            UiKit.WarningState(theme, "With this many Midpoint lines, this delay, and this duration, the Midpoint block may still be sending when Closing is due. Closing will wait for it to finish rather than overlap, but consider shortening Midpoint, reducing the delay, or lengthening the duration.");
        }
    }

    private void DrawGeneralSection(VenueTheme theme)
    {
        var channelIndex = (int)draft.Channel;
        if (Forms.Segmented(theme, "giveaways-modal-channel", ["Shout", "Yell"], ref channelIndex))
            draft = draft with { Channel = (GiveawayChatChannel)channelIndex };

        var delay = draft.DelayBetweenLinesSeconds;
        if (Forms.NumericField(theme, "Delay Between Lines (seconds)", ref delay, 1, 1, 300))
            draft = draft with { DelayBetweenLinesSeconds = delay };

        var duration = draft.GiveawayDurationSeconds;
        if (Forms.NumericField(theme, "Giveaway Duration (seconds)", ref duration, 5, 2, 86_400))
            draft = draft with { GiveawayDurationSeconds = duration };
    }

    private void DrawWinnerSection(VenueTheme theme)
    {
        var modeIndex = (int)draft.WinnerMode;
        if (Forms.Segmented(theme, "giveaways-modal-winner-mode", ["Highest", "Lowest", "Closest"], ref modeIndex))
            draft = draft with { WinnerMode = (GiveawayWinnerMode)modeIndex };

        if (draft.WinnerMode == GiveawayWinnerMode.Closest)
        {
            var target = draft.ClosestTargetNumber;
            if (Forms.NumericField(theme, "Closest Target Number", ref target, 1, 0, int.MaxValue))
                draft = draft with { ClosestTargetNumber = target };
        }

        var allowed = draft.AllowedRollsPerPerson;
        if (Forms.NumericField(theme, "Allowed Rolls Per Person (0 = unlimited)", ref allowed, 1, 0, 999))
            draft = draft with { AllowedRollsPerPerson = allowed };

        var special = draft.SpecialNumbersRaw;
        if (Forms.TextField(theme, "Special Numbers (comma-separated)", ref special, 256, "e.g. 69,420,777"))
            draft = draft with { SpecialNumbersRaw = special };

        if (!draft.SpecialNumbersActive && !string.IsNullOrWhiteSpace(draft.SpecialNumbersRaw))
            UiKit.WarningState(theme, "Special Numbers only take effect when Allowed Rolls Per Person is exactly 1 — with multiple or unlimited rolls, they're ignored so the special prize can't be gamed by re-rolling.");
    }

    /// <summary>The durable, reusable-next-time Winner Announcement configuration (GIVEAWAYS Winner Announcement
    /// feature spec §22/§23) — the ONLY place these two fields are persisted; the live Giveaways module's own
    /// channel selector/template field (<see cref="GiveawaysOperatorPanel.DrawAnnounceWinner"/>) is a deliberately
    /// ephemeral, running-snapshot-only override that never writes back here (see that method's own doc comment).
    /// </summary>
    private void DrawWinnerAnnouncementSection(VenueTheme theme)
    {
        var channelIndex = draft.WinnerAnnouncementChannel == GiveawayChatChannel.Yell ? 0 : 1;
        if (Forms.Segmented(theme, "giveaways-modal-winner-channel", ["Yell", "Shout"], ref channelIndex))
            draft = draft with { WinnerAnnouncementChannel = channelIndex == 0 ? GiveawayChatChannel.Yell : GiveawayChatChannel.Shout };

        var template = draft.WinnerAnnouncementTemplate;
        if (Forms.TextField(theme, "Announcement (use <name> for the winner list)", ref template, 500))
            draft = draft with { WinnerAnnouncementTemplate = template };
    }

    private void DrawBlockEditor(VenueTheme theme, string title, GiveawayAnnouncementBlock block, Action<GiveawayAnnouncementBlock> setBlock)
    {
        UiKit.SectionHeader(theme, $"{title} ({block.Lines.Count}/{GiveawayAnnouncementBlock.MaxLines})");

        // Pushed once for the whole block — Start/Midpoint/Closing all render a "Line 1", "Line 2", ... sequence,
        // and without this outer scope their per-index PushID(i) below would collide across all three blocks.
        ImGui.PushID(title);

        for (var i = 0; i < block.Lines.Count; i++)
        {
            ImGui.PushID(i);
            var line = block.Lines[i];
            if (Forms.TextField(theme, $"Line {i + 1}", ref line, 500))
            {
                var updated = block.Lines.ToArray();
                updated[i] = line;
                setBlock(block with { Lines = updated });
            }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Remove"))
                setBlock(block with { Lines = block.Lines.Where((_, idx) => idx != i).ToArray() });
            ImGui.PopID();
        }

        ImGui.BeginDisabled(block.Lines.Count >= GiveawayAnnouncementBlock.MaxLines);
        if (UiKit.GhostButton(theme, "+ Add Line"))
            setBlock(block with { Lines = block.Lines.Append("").ToArray() });
        ImGui.EndDisabled();

        ImGui.PopID();
    }

    private void DrawFooter(VenueTheme theme, GiveawayService service)
    {
        if (!string.IsNullOrEmpty(validationError)) UiKit.ErrorState(theme, validationError);

        if (UiKit.PrimaryButton(theme, "Save")) TrySave(service);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Cancel")) ImGui.CloseCurrentPopup(); // draft is simply discarded — nothing was ever persisted
    }

    private void TrySave(GiveawayService service)
    {
        var errors = GiveawayPresetValidator.Validate(draft);
        if (errors.Count > 0) { validationError = string.Join(" ", errors); return; }

        if (editingExistingId is { } id) service.UpdatePreset(id, _ => draft with { Id = id });
        else service.SaveNewPreset(draft);

        ImGui.CloseCurrentPopup();
    }
}
