using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.BlockLetters;

/// <summary>Block Letters' Draw()/DrawSettings() content. DrawSettings holds only the persisted default destination
/// (NEW_MODULE_GUIDE.md §9); everything else — the destination actually in effect this session, the composition
/// text, the palette — is live operational state that belongs in Draw(), never persisted (see
/// BLOCK_LETTERS_IMPLEMENTATION.md for why the composition itself is deliberately ephemeral).
///
/// The editor is real cursor/selection-aware text editing, not append-only (§4/§7). Typed/pasted characters are
/// enforced one at a time through ImGui's <c>InputTextMultiline</c> <c>CallbackCharFilter</c> (which Dear ImGui also
/// uses to feed a paste, one character at a time, so the same per-character budget check naturally implements
/// "accept as much of a paste as fits"). Palette-button insertion is applied DIRECTLY against the authoritative
/// <see cref="composition"/> string the moment the button is clicked — see the LIVE QA FIX section of
/// BLOCK_LETTERS_IMPLEMENTATION.md for why the earlier "queue it and apply inside the next callback" design failed
/// live: <c>ImGuiInputTextFlags.CallbackAlways</c> only fires while the InputText widget is ImGui's active item, and
/// clicking any other widget (a palette button) moves that active-item status away from the text box, so a queued
/// insertion never got applied until the operator clicked back into the box. <see cref="observation"/> is refreshed
/// from the widget's own callback data every frame the operator is actively editing (see <see cref="EditorCallback"/>
/// for exactly which callback fields are trustworthy), and is what a button click reads and then replaces directly —
/// one authoritative cursor/selection model regardless of which input path is driving it. After a palette insertion
/// the panel hands focus back to the box with the caret just after the inserted block (see <see cref="pendingRestore"/>),
/// so the operator can keep typing without clicking into it; the composition itself is still mutated directly, never
/// through that restoration.</summary>
internal sealed class BlockLettersOperatorPanel(BlockLettersService service, VenueProfileService venues, IFontHandle blockGlyphFont)
{
    private static readonly BlockLettersDestination[] DestinationValues =
        [BlockLettersDestination.Chat, BlockLettersDestination.PartyFinderComment, BlockLettersDestination.MacroLine];
    private static readonly string[] DestinationLabels = ["Chat", "Party Finder (Comment)", "Macro Line"];

    // A fixed, generous ImGui buffer capacity, deliberately decoupled from the active destination's real limit
    // (at most 500 bytes today) — BlockTextEditor enforces the real per-destination byte budget on every keystroke,
    // paste character, and palette insertion regardless of this capacity; this just needs enough headroom that a
    // large paste never gets silently hard-truncated by ImGui itself before our own callback can react to it.
    private const int EditorBufferBytes = 4096;
    private const float EditorHeight = 110f;
    private static readonly Vector2 GlyphButtonSize = new(36, 32);

    private readonly ConfirmDialog confirmDialog = new();
    private Guid trackedVenueId = Guid.Empty;
    private string composition = "";
    private int destinationIndex;
    private int activeMaxBytes = BlockLettersLimits.ChatBytes;
    private double? copiedAtImGuiTime;

    // The one authoritative cursor/selection model — C# char indices into `composition`, refreshed from the ImGui
    // callback while the operator is actively typing/clicking/selecting in the box and replaced by a palette-button
    // click (applied immediately, with no dependency on the box regaining focus). It carries the length of the text
    // it describes, and is only used while that still matches `composition`; null (never interacted, cleared, venue
    // switch) or a mismatch means "no trustworthy caret" and a button click appends at the end of the CURRENT text.
    private BlockEditorObservation? observation;

    // One-shot request to give the composition box keyboard focus, with the caret/selection restored, on the frame(s)
    // right after a palette insertion (the click itself moved focus to the button). Frame-bounded so it can never
    // linger and jump the caret later, and consumed exactly once — never a per-frame focus grab.
    private const int RestoreLifetimeFrames = 4;
    private EditorRestore? pendingRestore;

    private sealed record EditorRestore(BlockEditorSelection Selection, int TextLength, int ExpiresAtFrame, bool FocusRequested);

    // --- Live operation: Home → Block Letters ------------------------------------------------------------------

    public void Draw()
    {
        var theme = venues.Current.Theme;
        SyncForActiveVenue();

        var destination = DestinationValues[destinationIndex];
        activeMaxBytes = BlockLettersLimits.MaxBytes(destination);
        var currentBytes = BlockTextLength.CountBytes(composition);
        var overLimit = currentBytes > activeMaxBytes;

        UiKit.BeginSectionCard("blockletters-compose", theme, "Compose");
        Forms.ComboField(theme, "Destination", DestinationLabels, ref destinationIndex, 260);

        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(overLimit ? theme.Tokens.Error : theme.Tokens.TextSecondary));
        ImGui.TextUnformatted($"{currentBytes} / {activeMaxBytes} bytes");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        DrawEditor(theme);
        ImGui.Spacing();

        if (overLimit)
            UiKit.WarningState(theme, $"Over the {BlockLettersLimits.DisplayName(destination)} limit by {currentBytes - activeMaxBytes} byte(s). Your text is unchanged — reduce it to re-enable Copy.");

        DrawActions(theme, overLimit);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawPalette(theme);

        confirmDialog.Draw(theme);
    }

    // --- Settings → Modules → Block Letters: persistent configuration only ------------------------------------

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        UiKit.BeginSectionCard("blockletters-settings", theme, "Block Letters");

        var defaultIndex = Array.IndexOf(DestinationValues, service.Settings.DefaultDestination);
        if (defaultIndex < 0) defaultIndex = 0;
        if (Forms.ComboField(theme, "Default Destination", DestinationLabels, ref defaultIndex, 260))
            service.SetDefaultDestination(DestinationValues[defaultIndex]);
        ImGui.TextWrapped("The destination Block Letters starts on when you open it. The current composition is never saved — it always starts blank.");

        UiKit.EndSectionCard();
    }

    /// <summary>Active operation state (the in-progress composition, and the live destination choice) must not leak
    /// across a venue switch (§12a) — reset on the one venue-identity change Draw() can observe by comparing against
    /// venues.Current.Id every frame, the same "reads venues.Current fresh every call" pattern §20 documents for
    /// detached windows. The live destination re-seeds from the persisted per-venue default; it is NOT written back
    /// to Settings when the operator changes it mid-session (§9 — routine live switching is operational, not a
    /// configuration edit).</summary>
    private void SyncForActiveVenue()
    {
        if (venues.Current.Id == trackedVenueId) return;
        trackedVenueId = venues.Current.Id;
        composition = "";
        observation = null;
        pendingRestore = null;
        var index = Array.IndexOf(DestinationValues, service.Settings.DefaultDestination);
        destinationIndex = Math.Max(0, index);
    }

    private void DrawEditor(VenueTheme theme)
    {
        Forms.FieldLabel(theme, "Composition");
        ImGui.SetNextItemWidth(-1);
        Forms.PushFieldStyle(theme);
        if (pendingRestore is { } restore)
        {
            if (ImGui.GetFrameCount() > restore.ExpiresAtFrame)
                pendingRestore = null;
            else if (!restore.FocusRequested)
            {
                ImGui.SetKeyboardFocusHere(); // one-shot; takes effect on the following frame, where EditorCallback restores the caret
                pendingRestore = restore with { FocusRequested = true };
            }
        }
        ImGui.InputTextMultiline(
            "##blockletters-composition",
            ref composition,
            EditorBufferBytes,
            new Vector2(-1, EditorHeight),
            ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCharFilter,
            EditorCallback);
        Forms.PopFieldStyle();
    }

    /// <summary><c>CallbackCharFilter</c> fires once per accepted character for BOTH typing and paste (Dear ImGui
    /// feeds a paste through the same per-character filter path), so evaluating one character at a time against the
    /// remaining byte budget naturally implements "accept as much of a paste as fits, drop the rest" with no
    /// paste-specific code. <c>CallbackAlways</c> fires every frame the composition box is the active/focused ImGui
    /// item and is used ONLY to keep <see cref="observation"/> in sync with the operator's own typing/clicking/
    /// selecting (and, once, to restore the caret after a palette insertion) — palette-button insertion no longer
    /// waits for this callback at all (see <see cref="InsertGlyph"/>), which is the live-QA fix: the callback is not
    /// guaranteed to fire on the frame(s) right after a button click, since that click moves ImGui's active-item
    /// status to the button. NOTE (0.3.8 caret investigation, unchanged here): verified against the game's ImGui, the
    /// CharFilter callback's data carries only <c>EventChar</c> — <c>BufTextSpan</c> is empty and the selection is 0 —
    /// so the per-character budget check above evaluates against an empty buffer; see BLOCK_LETTERS_CARET_0.3.8_MAINTENANCE.md.</summary>
    private int EditorCallback(ref ImGuiInputTextCallbackData data)
    {
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
        {
            var candidate = ((char)data.EventChar).ToString();
            var current = Encoding.UTF8.GetString(data.BufTextSpan);
            var selStart = Utf8Offsets.ToCharIndex(data.BufTextSpan, data.SelectionStart);
            var selEnd = Utf8Offsets.ToCharIndex(data.BufTextSpan, data.SelectionEnd);
            var result = BlockTextEditor.Insert(current, selStart, selEnd, candidate, activeMaxBytes);
            if (result.Truncated)
            {
                data.EventChar = 0; // primary documented discard mechanism (imgui.h)
                return 1; // belt-and-suspenders: nonzero also discards per the same contract
            }
            return 0;
        }

        if (data.EventFlag == ImGuiInputTextFlags.CallbackAlways)
        {
            if (pendingRestore is { FocusRequested: true } restore
                && ImGui.GetFrameCount() <= restore.ExpiresAtFrame
                && Encoding.UTF8.GetCharCount(data.BufTextSpan) == restore.TextLength)
            {
                // First active frame after a palette insertion: put the caret (or preserved selection) where the
                // insertion left it. A freshly focused widget otherwise starts at byte 0. Set all three fields —
                // ImGui's own selection pair does not follow the caret (see BlockEditorCaret).
                var text = Encoding.UTF8.GetString(data.BufTextSpan);
                var startBytes = BlockEditorCaret.ToByteOffset(text, restore.Selection.Start);
                var endBytes = BlockEditorCaret.ToByteOffset(text, restore.Selection.End);
                data.SelectionStart = startBytes;
                data.SelectionEnd = endBytes;
                data.CursorPos = endBytes;
                pendingRestore = null;
                observation = new BlockEditorObservation(restore.Selection, restore.TextLength);
                return 0;
            }

            // The caret is CursorPos; SelectionStart/SelectionEnd only count while they differ (a real selection).
            // Typing leaves them frozen at the previous click position — reading them alone is exactly what made a
            // block inserted after typing land right after the previous block.
            observation = BlockEditorCaret.Observe(data.BufTextSpan, data.CursorPos, data.SelectionStart, data.SelectionEnd);
        }

        return 0;
    }

    /// <summary>A palette-button click mutates the ONE authoritative <see cref="composition"/> string directly (the
    /// earlier live-QA fix — no dependency on the InputTextMultiline widget's callback firing after the click), at the
    /// editor's current caret/selection as last observed while the operator was editing
    /// (<see cref="BlockEditorCaret.InsertAtEditorPosition"/>). The observation is used only while it still describes
    /// the current text; otherwise the block is appended at the end of the CURRENT composition — never at a position
    /// remembered from an earlier insertion. ImGui's <c>ref string</c> binding re-syncs its displayed content from
    /// <see cref="composition"/> on every frame the widget is not active, so the next rendered frame shows the result.
    /// Afterwards the box is handed focus with the caret just after the block (or the untouched selection, if the
    /// block did not fit the byte budget) so typing can continue without clicking into it.</summary>
    private void InsertGlyph(string value)
    {
        var outcome = BlockEditorCaret.InsertAtEditorPosition(composition, observation, value, activeMaxBytes);
        composition = outcome.Text;
        observation = new BlockEditorObservation(outcome.Selection, composition.Length);
        pendingRestore = new EditorRestore(outcome.Selection, composition.Length, ImGui.GetFrameCount() + RestoreLifetimeFrames, FocusRequested: false);
    }

    private void DrawActions(VenueTheme theme, bool overLimit)
    {
        ImGui.BeginDisabled(overLimit || composition.Length == 0);
        if (UiKit.PrimaryButton(theme, "Copy"))
        {
            ImGui.SetClipboardText(composition);
            copiedAtImGuiTime = ImGui.GetTime();
        }
        ImGui.EndDisabled();

        if (copiedAtImGuiTime is { } copiedAt && ImGui.GetTime() - copiedAt < 2.0)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
            ImGui.TextUnformatted("Copied to clipboard.");
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(composition.Length == 0);
        if (UiKit.DangerButton(theme, "Clear"))
            confirmDialog.Request("Clear composition?", "The current block-letter text will be cleared. This cannot be undone.", () =>
            {
                composition = "";
                observation = null;
                pendingRestore = null;
            });
        ImGui.EndDisabled();
    }

    private void DrawPalette(VenueTheme theme)
    {
        UiKit.BeginSectionCard("blockletters-palette", theme, "Block Letters");
        DrawGlyphGroup(theme, "Letters", BlockLetterCatalog.Letters);
        ImGui.Spacing();
        DrawGlyphGroup(theme, "Numbers", BlockLetterCatalog.Digits);
        ImGui.Spacing();
        DrawGlyphGroup(theme, "Punctuation", BlockLetterCatalog.Punctuation);
        ImGui.Spacing();
        DrawGlyphGroup(theme, "Other Symbols", BlockLetterCatalog.Other);
        UiKit.EndSectionCard();
    }

    /// <summary>Live-QA fix: each button's visible label is the actual FFXIV glyph (<see cref="BlockGlyph.Value"/>)
    /// rendered through the game's own AXIS font handle (<see cref="blockGlyphFont"/>, a Dalamud game-font handle —
    /// no bundled/custom font asset), not the semantic label or hex codepoint — the button must show what will
    /// actually be inserted. The font is pushed once for the whole group (not once per button) since every glyph in
    /// a group shares it. If the handle isn't loaded yet (<see cref="IFontHandle.Available"/> false — font atlases
    /// build asynchronously in Dalamud), buttons fall back to their hex codepoint rather than rendering with the
    /// wrong font or leaving a blank/broken-looking button; this is the "fall back gracefully... and document it"
    /// case, not expected to be the steady state.</summary>
    private void DrawGlyphGroup(VenueTheme theme, string title, IReadOnlyList<BlockGlyph> glyphs)
    {
        UiKit.SectionHeader(theme, title);
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)(avail / (GlyphButtonSize.X + 4)));

        var fontAvailable = blockGlyphFont.Available;
        var fontScope = fontAvailable ? blockGlyphFont.Push() : null;
        try
        {
            for (var i = 0; i < glyphs.Count; i++)
            {
                if (i % perRow != 0) ImGui.SameLine();
                var glyph = glyphs[i];
                ImGui.PushID(glyph.Codepoint);
                var label = fontAvailable ? glyph.Value : $"{glyph.Codepoint:X4}";
                if (UiKit.GhostButton(theme, label, GlyphButtonSize))
                    InsertGlyph(glyph.Value);
                UiKit.Tooltip(glyph.Category == BlockGlyphCategory.Other ? $"U+{glyph.Codepoint:X4}" : $"{glyph.Label}  (U+{glyph.Codepoint:X4})");
                ImGui.PopID();
            }
        }
        finally
        {
            fontScope?.Dispose();
        }
    }
}
