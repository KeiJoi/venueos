using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Macro;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Macro;

/// <summary>Macro's operator surface, split per NEW_MODULE_GUIDE.md §8/§9: <see cref="DrawSettings"/> is the ONLY
/// place macros and hotbars are authored/configured (MACRO spec §4/§40/§41); <see cref="Draw"/> is a pure launcher
/// — the live module never edits a macro's body/icon/delay or a hotbar's slot assignments (spec §4/§5), it only
/// runs macros and (MACRO LIVE QA FIX §21) lets a tile be dragged onto a visible faux hotbar while Edit Hotbars is
/// on.</summary>
internal sealed class MacroOperatorPanel(MacroService service, VenueProfileService venues, MacroIconPicker iconPicker, MacroHotbarRenderer hotbarRenderer, Dalamud.Plugin.Services.ITextureProvider textureProvider)
{
    private static readonly Vector2 TileSize = new(120, 96);
    private static readonly Vector2 TileIconSize = new(48, 48);

    private readonly ConfirmDialog confirmDialog = new();
    private readonly MacroEditorWindow editorWindow = new();
    private int settingsTabIndex;
    private int editingHotbarIndex;
    private Guid? paletteSelectionForAssignment;

    // =============================================================================================================
    // Live operation (Draw) — MACRO spec §4/§5/§23/§31 — UNCHANGED from the prior pass except for the tile drag
    // source added at the bottom of DrawMacroTile (MACRO LIVE QA FIX §20/§21).
    // =============================================================================================================

    public void Draw()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);

        DrawRunStatus(theme);
        ImGui.Spacing();
        DrawHotbarControls(theme);
        ImGui.Spacing();
        DrawTileLauncher(theme);
    }

    private void DrawRunStatus(VenueTheme theme)
    {
        UiKit.BeginSectionCard("macro-status", theme, "Macro");
        var runner = service.Runner;

        if (runner.IsRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Accent));
            ImGui.SetWindowFontScale(1.3f);
            ImGui.TextUnformatted($"RUNNING: {runner.RootMacroName}");
            ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();

            if (!string.IsNullOrEmpty(runner.CurrentMacroName) && runner.CurrentMacroName != runner.RootMacroName)
                ImGui.TextUnformatted($"Nested macro: {runner.CurrentMacroName}");
            if (runner.CurrentLineCount > 0)
                ImGui.TextUnformatted($"Line {runner.CurrentLineNumber} / {runner.CurrentLineCount}");
            if (!string.IsNullOrEmpty(runner.StatusMessage))
                UiKit.StatusBadge(theme, runner.StatusMessage, ToastLevel.Information);

            ImGui.Spacing();
            if (UiKit.DangerButton(theme, "Cancel Macro")) runner.Cancel();
        }
        else
        {
            ImGui.TextUnformatted("No macro running.");
            if (runner.LastOutcomeMessage is { } last)
            {
                ImGui.Spacing();
                switch (runner.LastOutcomeKind)
                {
                    case MacroRunPhase.Failed: UiKit.ErrorState(theme, last); break;
                    case MacroRunPhase.Cancelled: UiKit.WarningState(theme, last); break;
                    default:
                        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
                        ImGui.TextUnformatted(last);
                        ImGui.PopStyleColor();
                        break;
                }
            }
        }

        UiKit.EndSectionCard();
    }

    /// <summary>Compact per-hotbar visibility toggles (spec §31) plus the single global lock/edit toggle (spec
    /// §32/§33, see <see cref="MacroHotbarRenderer"/>'s doc comment for why editing is one global switch rather than
    /// four independent ones). Toggling visibility here only flips <see cref="MacroHotbar.Enabled"/> — it never
    /// touches saved slot assignments/layout/position (spec §31's explicit "does NOT delete assignments").</summary>
    private void DrawHotbarControls(VenueTheme theme)
    {
        UiKit.BeginSectionCard("macro-hotbar-controls", theme, "Hotbars");
        for (var i = 0; i < service.Settings.Hotbars.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            var hotbar = service.Settings.Hotbars[i];
            var enabled = hotbar.Enabled;
            ImGui.PushID($"hotbar-toggle-{i}");
            if (UiKit.Toggle(theme, $"Hotbar {i + 1}", ref enabled)) service.SetHotbarEnabled(i, enabled);
            ImGui.PopID();
        }

        ImGui.Spacing();
        var editing = hotbarRenderer.EditMode;
        if (editing) UiKit.WarningState(theme, "Edit Hotbars is ON — drag a macro tile below onto a visible hotbar to assign it, or drag a bar itself to reposition it. Clicking a slot no longer runs its macro until you turn this off.");
        if (UiKit.GhostButton(theme, editing ? "Done Editing Hotbars" : "Edit Hotbars")) hotbarRenderer.EditMode = !hotbarRenderer.EditMode;
        UiKit.EndSectionCard();
    }

    /// <summary>The live module = one giant macro hotbar (spec §5/§23): every saved macro as a large clickable
    /// tile, the tile itself being the Run action — no separate select-then-run step. Tiles are disabled while a
    /// macro is already running (spec §21), with Cancel (above) staying the only active control.</summary>
    private void DrawTileLauncher(VenueTheme theme)
    {
        UiKit.BeginSectionCard("macro-tiles", theme, $"Macros ({service.Settings.Macros.Count})");

        if (service.Settings.Macros.Count == 0)
        {
            UiKit.EmptyState(theme, "No macros yet", "Create one in Settings → Modules → Macro.");
            UiKit.EndSectionCard();
            return;
        }

        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)(avail / (TileSize.X + 10)));
        var running = service.Runner.IsRunning;

        for (var i = 0; i < service.Settings.Macros.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine();
            DrawMacroTile(theme, service.Settings.Macros[i], running);
        }

        UiKit.EndSectionCard();
    }

    private void DrawMacroTile(VenueTheme theme, SavedMacro macro, bool disabled)
    {
        ImGui.PushID(macro.Id.ToString());
        var start = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(disabled);
        var clicked = ImGui.InvisibleButton("##tile", TileSize);
        ImGui.EndDisabled();
        var hovered = !disabled && ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var bg = disabled ? theme.Tokens.Disabled : hovered ? theme.Tokens.RaisedSurface : theme.Tokens.Surface;
        drawList.AddRectFilled(start, start + TileSize, UiKit.ColorU32(bg), theme.Metrics.Rounding + 2);
        drawList.AddRect(start, start + TileSize, UiKit.ColorU32(theme.Tokens.Border), theme.Metrics.Rounding + 2, ImDrawFlags.None, hovered ? 2f : 1f);

        var iconMin = start + new Vector2((TileSize.X - TileIconSize.X) / 2, 8);
        MacroIconRenderer.Draw(textureProvider, theme, macro.IconId, iconMin, TileIconSize);

        // Draw-list AddText's own wrapWidth parameter (distinct from ImGui.Text*'s push/pop wrap-pos state, which
        // has no effect on draw-list calls) keeps a long macro name from overrunning the tile at its fixed size.
        var nameSize = ImGui.CalcTextSize(macro.Name, false, TileSize.X - 12);
        var nameStart = start + new Vector2(6, TileSize.Y - nameSize.Y - 8);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), nameStart, UiKit.ColorU32(theme.Tokens.TextPrimary), macro.Name, TileSize.X - 12);

        // MACRO LIVE QA FIX §21/§22: the same drag source every other macro icon in this module uses, so a tile can
        // be dragged directly onto a visible faux hotbar (only actually accepted while that hotbar is in Edit mode
        // — see MacroHotbarRenderer.DrawSlot). Coexists with the ordinary click-to-run handling below it unchanged:
        // ImGui only treats this as a drag once the mouse moves past its own drag threshold while held down, so a
        // plain click still runs the macro exactly as before.
        MacroDragDrop.BeginSource(macro.Id, macro.Name);

        ImGui.PopID();
        if (clicked && !disabled) service.Launch(macro.Id);
    }

    // =============================================================================================================
    // Settings — macro library + hotbar configuration (DrawSettings) — MACRO spec §40/§41, MACRO LIVE QA FIX §14-19
    // =============================================================================================================

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);
        editorWindow.Draw(theme, service, iconPicker);

        Forms.Segmented(theme, "macro-settings-tabs", ["Library", "Hotbars"], ref settingsTabIndex);
        ImGui.Spacing();

        if (settingsTabIndex == 0) DrawLibrarySettings(theme);
        else DrawHotbarSettings(theme);
    }

    /// <summary>MACRO LIVE QA FIX §14: the main Settings page is compact — a library list with New/Edit/Duplicate/
    /// Delete, nothing else. All actual authoring (name/icon/delay/body) happens in <see cref="MacroEditorWindow"/>,
    /// a separate window opened by New/Edit, never expanded inline here.</summary>
    private void DrawLibrarySettings(VenueTheme theme)
    {
        UiKit.BeginSectionCard("macro-library-browser", theme, $"Macro Library ({service.Settings.Macros.Count})");

        if (service.Settings.Macros.Count == 0) UiKit.EmptyState(theme, "No macros yet", "Create one below.");
        foreach (var macro in service.Settings.Macros)
        {
            ImGui.PushID(macro.Id.ToString());
            var subtitle = $"{macro.Lines.Count} line(s) · {macro.DelayBetweenLinesSeconds:0.##}s delay";
            if (UiKit.ListRow(theme, macro.Name, subtitle, false)) editorWindow.OpenForEdit(macro);

            if (UiKit.GhostButton(theme, "Edit")) editorWindow.OpenForEdit(macro);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Duplicate")) service.DuplicateMacro(macro.Id, out _);
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Delete")) RequestDelete(macro);

            ImGui.Spacing();
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "+ New Macro")) editorWindow.OpenForNew();

        UiKit.EndSectionCard();
    }

    private void RequestDelete(SavedMacro macro)
    {
        var referencing = service.FindReferencingMacros(macro.Id);
        var body = referencing.Count == 0
            ? $"\"{macro.Name}\" will be permanently deleted, including from any hotbar slots it's assigned to. This cannot be undone."
            : $"\"{macro.Name}\" will be permanently deleted. It is referenced by: {string.Join(", ", referencing.Select(m => m.Name))}. Those macros will be left with a broken nested reference that fails safely at runtime — it will NOT be automatically removed from them. This cannot be undone.";
        confirmDialog.Request($"Delete \"{macro.Name}\"?", body, () => service.DeleteMacro(macro.Id));
    }

    // =============================================================================================================
    // Hotbar configuration (spec §41) — enable/layout/scale/transparency + 12-slot assignment editor with
    // drag/drop from the macro palette (spec §28) and a reliable click-to-place fallback. Drag mechanics now go
    // through the shared MacroDragDrop helper (MACRO LIVE QA FIX §22) instead of a private duplicate.
    // =============================================================================================================

    private void DrawHotbarSettings(VenueTheme theme)
    {
        UiKit.BeginSectionCard("macro-hotbar-select", theme, "Hotbars");
        var names = Enumerable.Range(1, MacroHotbar.MaxHotbars).Select(n => $"Hotbar {n}").ToArray();
        Forms.Segmented(theme, "macro-hotbar-picker", names, ref editingHotbarIndex);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawHotbarEditor(theme, service.Settings.Hotbars[editingHotbarIndex]);
    }

    private void DrawHotbarEditor(VenueTheme theme, MacroHotbar hotbar)
    {
        UiKit.BeginSectionCard("macro-hotbar-editor", theme, $"Hotbar {hotbar.Index + 1}");

        var enabled = hotbar.Enabled;
        if (UiKit.Toggle(theme, "Enabled", ref enabled)) service.SetHotbarEnabled(hotbar.Index, enabled);

        var layoutIndex = MacroHotbarLayouts.Values.ToList().IndexOf(hotbar.Layout);
        if (Forms.ComboField(theme, "Layout", MacroHotbarLayouts.DisplayNames, ref layoutIndex, 160))
            service.SetHotbarLayout(hotbar.Index, MacroHotbarLayouts.Values[layoutIndex]);

        var scale = hotbar.Scale;
        if (Forms.FloatField(theme, "Scale", ref scale, 0.05f, MacroHotbar.MinScale, MacroHotbar.MaxScale, "%.2f"))
            service.SetHotbarScale(hotbar.Index, scale);

        var transparency = hotbar.Transparency;
        if (Forms.FloatField(theme, "Transparency", ref transparency, 0.05f, MacroHotbar.MinTransparency, MacroHotbar.MaxTransparency, "%.2f"))
            service.SetHotbarTransparency(hotbar.Index, transparency);

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Clear Hotbar"))
            confirmDialog.Request($"Clear Hotbar {hotbar.Index + 1}?", "Every slot on this hotbar will be emptied. Saved macros themselves are not affected. This cannot be undone.", () => service.ClearHotbar(hotbar.Index));

        ImGui.Spacing();
        UiKit.Divider(theme);
        UiKit.SectionHeader(theme, "Assign Macros");
        ImGui.TextWrapped("Drag a macro from the palette below onto a slot, or click a macro then click a slot to assign it. Dragging one occupied slot onto another swaps them.");
        ImGui.Spacing();

        DrawMacroPalette(theme);
        ImGui.Spacing();
        DrawSlotGrid(theme, hotbar);

        UiKit.EndSectionCard();
    }

    private void DrawMacroPalette(VenueTheme theme)
    {
        UiKit.SectionHeader(theme, "Macro Palette");
        if (service.Settings.Macros.Count == 0) { UiKit.EmptyState(theme, "No macros yet", "Create macros in the Library tab first."); return; }

        var slotSize = new Vector2(40, 40);
        var perRow = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (slotSize.X + 6)));
        for (var i = 0; i < service.Settings.Macros.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine();
            var macro = service.Settings.Macros[i];
            ImGui.PushID($"palette-{macro.Id}");
            var start = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##paletteslot", slotSize);
            var clicked = ImGui.IsItemClicked();
            if (paletteSelectionForAssignment == macro.Id)
                ImGui.GetWindowDrawList().AddRect(start - new Vector2(2, 2), start + slotSize + new Vector2(2, 2), UiKit.ColorU32(theme.Tokens.Accent), theme.Metrics.Rounding, ImDrawFlags.None, 2f);
            MacroIconRenderer.Draw(textureProvider, theme, macro.IconId, start, slotSize);
            UiKit.Tooltip(macro.Name);

            MacroDragDrop.BeginSource(macro.Id, macro.Name);
            if (clicked) paletteSelectionForAssignment = paletteSelectionForAssignment == macro.Id ? null : macro.Id;

            ImGui.PopID();
        }
    }

    private void DrawSlotGrid(VenueTheme theme, MacroHotbar hotbar)
    {
        UiKit.SectionHeader(theme, "Slots");
        var slotSize = new Vector2(40, 40);
        var spacing = 4f;

        for (var slot = 0; slot < MacroHotbarLayouts.SlotCount; slot++)
        {
            var (col, _) = MacroHotbarLayouts.Position(hotbar.Layout, slot);
            if (col > 0) ImGui.SameLine(0, spacing);

            var macroId = hotbar.SlotMacroIds[slot];
            var macro = macroId is { } id ? service.Settings.Macros.FirstOrDefault(m => m.Id == id) : null;

            ImGui.PushID($"slot-{slot}");
            var start = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##slot", slotSize);
            var clicked = ImGui.IsItemClicked();
            MacroIconRenderer.Draw(textureProvider, theme, macro?.IconId ?? 0, start, slotSize);
            if (macro is not null) UiKit.Tooltip(macro.Name);

            if (MacroDragDrop.AcceptTarget() is { } droppedId) service.DropMacroOntoSlot(hotbar.Index, slot, droppedId);

            if (clicked)
            {
                if (paletteSelectionForAssignment is { } selected)
                {
                    service.DropMacroOntoSlot(hotbar.Index, slot, selected);
                    paletteSelectionForAssignment = null;
                }
                else if (macro is not null)
                {
                    service.ClearSlot(hotbar.Index, slot);
                }
            }

            ImGui.PopID();
        }
    }
}
