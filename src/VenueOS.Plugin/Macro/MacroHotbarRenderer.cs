using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using VenueOS.Modules.Operations.Macro;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Macro;

/// <summary>Renders up to <see cref="MacroHotbar.MaxHotbars"/> persistent faux FFXIV-style hotbars directly onto
/// the game screen (MACRO spec §24-§39) — HUD overlays, NOT detached VenueOS module windows (spec §24's explicit
/// distinction from <c>ModuleWindowManager</c>). Drawn every frame from <c>Plugin.Draw</c>, gated only on the
/// Macro module's <c>IsEnabled</c> — completely independent of the main tablet's or the Macro module screen's own
/// open/closed state (spec §37/§57), the same "auxiliary window survives its parent screen closing" precedent
/// Bingo's Called Numbers window and Giveaways' tracker window already established.
///
/// VISUAL EXCEPTION (spec §58, NEW_MODULE_GUIDE.md's own visual-language rule intentionally does not apply here):
/// these bars deliberately do NOT use a VenueOS card/section chrome or the active venue theme's accent colors —
/// they use a fixed, native-HUD-like dark translucent panel with square icon slots, so they blend into FFXIV's own
/// HUD rather than reading as another VenueOS window. No proprietary FFXIV texture is copied — every slot icon is
/// requested at render time through <see cref="MacroIconRenderer"/> (Dalamud's <c>ITextureProvider</c>), exactly
/// like every other icon surface in this module.
///
/// LOCK/EDIT model (spec §32-§33, §46): a single GLOBAL "Edit Hotbars" toggle (owned by <c>MacroOperatorPanel</c>,
/// read here via <see cref="EditMode"/>) governs every visible bar at once — simpler than a per-bar toggle and
/// avoids an operator forgetting one bar was left unlocked. Locked: slots are click-to-run only, no drag handle
/// exists at all (so a normal macro click can never accidentally move the bar — spec §46's explicit requirement).
/// Editing: slot click-to-run is disabled and replaced by a single drag layer covering the whole bar (the exact
/// <c>GetMouseDragDelta</c>/<c>ResetMouseDragDelta</c>/<c>SetWindowPos</c> pattern <c>TabletHeader</c> already uses
/// for the borderless main tablet) — slot ASSIGNMENT itself is deliberately not editable from the live overlay at
/// all; that only ever happens in Settings → Modules → Macro → Hotbars (spec §28), so the live bar's only two
/// affordances are "run" and "move".</summary>
internal sealed class MacroHotbarRenderer(MacroService service, ITextureProvider textureProvider)
{
    private const float BaseSlotSize = 40f;
    private const float BaseSpacing = 3f;
    private const float BasePadding = 6f;

    public bool EditMode;
    private readonly bool[] positionInitialized = new bool[MacroHotbar.MaxHotbars];

    public void DrawAll()
    {
        var hotbars = service.Settings.Hotbars;
        for (var i = 0; i < hotbars.Count; i++)
            if (hotbars[i].Enabled)
                DrawBar(i, hotbars[i]);
    }

    private void DrawBar(int index, MacroHotbar hotbar)
    {
        var (columns, rows) = MacroHotbarLayouts.Dimensions(hotbar.Layout);
        var slotSize = BaseSlotSize * hotbar.Scale;
        var spacing = BaseSpacing * hotbar.Scale;
        var padding = BasePadding * hotbar.Scale;
        var size = new Vector2(columns * slotSize + (columns - 1) * spacing + padding * 2, rows * slotSize + (rows - 1) * spacing + padding * 2);

        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        // Position is per-venue persisted state (MacroSettings' doc comment) — only ever pushed into ImGui as a
        // starting point once per (bar, position-value) via FirstUseEver; every subsequent frame is left to ImGui's
        // own window memory / the drag handle below, matching the main tablet's/detached windows' own convention.
        if (!positionInitialized[index] && hotbar.Position is { } pos)
        {
            ImGui.SetNextWindowPos(new Vector2(pos.X, pos.Y), ImGuiCond.FirstUseEver);
            positionInitialized[index] = true;
        }

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var opened = ImGui.Begin($"###venueos-macro-hotbar-{index}", flags);
        if (opened)
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            var alpha = Math.Clamp(hotbar.Transparency, MacroHotbar.MinTransparency, MacroHotbar.MaxTransparency);
            drawList.AddRectFilled(origin, origin + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.04f, 0.05f, 0.72f * alpha)), 4f);
            drawList.AddRect(origin, origin + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f * alpha)), 4f, ImDrawFlags.None, 1.5f);

            // Edit mode's whole-bar drag layer is drawn FIRST (visually underneath), then each slot's own
            // InvisibleButton is drawn on top of it. The layer is marked SetItemAllowOverlap() (see
            // DrawDragLayer's doc comment for why that specific call — not draw order alone — is what lets a slot
            // still receive hover despite the layer covering the same screen region) so a slot can still become the
            // hovered/target item while the drag layer remains active over the gaps between slots. This is what
            // lets "drag the bar" (a plain click-drag on the background) and "drop a macro onto a slot" (ImGui's
            // separate drag-drop-payload state machine, active only while a BeginDragDropSource elsewhere is being
            // dragged) coexist without either stealing the other's input — see DrawSlot's doc comment for the
            // target side.
            if (EditMode) DrawDragLayer(index, origin, size);

            for (var slot = 0; slot < MacroHotbarLayouts.SlotCount; slot++)
            {
                var (col, row) = MacroHotbarLayouts.Position(hotbar.Layout, slot);
                var slotMin = origin + new Vector2(padding + col * (slotSize + spacing), padding + row * (slotSize + spacing));
                DrawSlot(index, hotbar, slot, slotMin, slotSize);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawSlot(int hotbarIndex, MacroHotbar hotbar, int slot, Vector2 slotMin, float slotSize)
    {
        var macroId = hotbar.SlotMacroIds[slot];
        var macro = macroId is { } id ? service.Settings.Macros.FirstOrDefault(m => m.Id == id) : null;
        var slotSizeVec = new Vector2(slotSize, slotSize);

        ImGui.SetCursorScreenPos(slotMin);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(slotMin, slotMin + slotSizeVec, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.06f)), 3f);
        drawList.AddRect(slotMin, slotMin + slotSizeVec, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.35f)), 3f);

        if (macro is not null && macro.IconId != 0)
        {
            var wrap = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(macro.IconId)).GetWrapOrEmpty();
            if (wrap.Handle != 0) drawList.AddImage(wrap.Handle, slotMin + new Vector2(1, 1), slotMin + slotSizeVec - new Vector2(1, 1));
        }

        // Locked: click-to-run only, exactly as before (spec §23's "clicking assigned slot runs macro; live drop
        // assignment should NOT occur" — no BeginDragDropTarget call exists at all in this branch, so a payload
        // dragged over a locked bar is simply never accepted). Editing: the button exists only so the slot has an
        // ImGui item to attach BeginDragDropTarget to (see DrawBar's doc comment on why it's drawn AFTER the
        // whole-bar drag layer) — it deliberately does nothing on a plain click (spec §23's "clicking slot does
        // NOT run macro" while editing).
        ImGui.PushID($"hotbar{hotbarIndex}-slot{slot}");
        ImGui.InvisibleButton("##slot", slotSizeVec);
        if (!EditMode)
        {
            var clicked = ImGui.IsItemClicked();
            var hovered = ImGui.IsItemHovered();
            if (hovered && macro is not null) ImGui.SetTooltip(macro.Name);
            if (clicked && macro is not null && !service.Runner.IsRunning) service.Launch(macro.Id);
        }
        else
        {
            // Hover feedback while editing (spec requirement — "highlight slot border/background" while a drag is
            // over a valid target), independent of whether a drop actually completes this frame. Only reachable
            // now that the drag layer's SetNextItemAllowOverlap() fix (DrawDragLayer's doc comment) lets this
            // slot's own InvisibleButton become hovered at all.
            if (ImGui.IsItemHovered()) drawList.AddRect(slotMin - new Vector2(1, 1), slotMin + slotSizeVec + new Vector2(1, 1), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.8f, 0.2f, 0.95f)), 3f, ImDrawFlags.None, 2.5f);
            if (MacroDragDrop.AcceptTarget() is { } droppedMacroId) service.DropMacroOntoSlot(hotbarIndex, slot, droppedMacroId);
        }
        ImGui.PopID();
    }

    /// <summary>Edit-mode dragging — identical mechanics to <c>TabletHeader.DrawDragHandle</c> (spec §32/§46: never
    /// let a normal macro click risk moving the bar, so this layer only ever exists while <see cref="EditMode"/> is
    /// on, and slot click-to-run is skipped entirely for that same duration above). Persists the final position via
    /// <see cref="MacroService.SetHotbarPosition"/> only on mouse release, not every dragged frame, so an operator
    /// repositioning a bar doesn't trigger a config save on every single frame of the drag.</summary>
    private void DrawDragLayer(int hotbarIndex, Vector2 origin, Vector2 size)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.PushID($"hotbar{hotbarIndex}-drag");
        // LIVE QA FIX — ROOT CAUSE of "drag onto a slot does nothing": Dear ImGui's default overlap rule is the
        // OPPOSITE of what a naive "later-drawn item wins" assumption expects. The FIRST item submitted each frame
        // that the mouse is over claims g.HoveredId; a LATER item overlapping the same screen region is locked out
        // of hover entirely unless the EARLIER item is explicitly marked overlappable via SetItemAllowOverlap()
        // (this binding exposes the older post-hoc form, called immediately AFTER the item it applies to — not the
        // newer SetNextItemAllowOverlap()/pre-item form some other ImGui bindings use, which this one does not
        // expose). Without this call, this full-bar InvisibleButton — drawn first specifically so slots could sit
        // "on top" of it — silently claimed hover for the ENTIRE bar every frame in Edit mode, so no individual
        // slot's own InvisibleButton (drawn after, per-slot, below) ever became hovered. BeginDragDropTarget()
        // requires the target item itself to be hovered/the last item, so it never fired for any slot — drops were
        // silently swallowed while bar-dragging (which only needs THIS item to be active) kept working, exactly
        // matching the live-reported symptom.
        ImGui.InvisibleButton("##drag", size);
        ImGui.SetItemAllowOverlap();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsItemDeactivated())
        {
            var finalPos = ImGui.GetWindowPos();
            service.SetHotbarPosition(hotbarIndex, new MacroPosition(finalPos.X, finalPos.Y));
        }
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(origin + new Vector2(1, 1), origin + size - new Vector2(1, 1), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.8f, 0.2f, 0.9f)), 4f, ImDrawFlags.None, 2f);
        ImGui.PopID();
    }
}
