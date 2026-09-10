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
    private const float BaseHandleHeight = 16f;

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
        // 0.3.0 UI PASS: a dedicated drag-handle strip, spatially disjoint from every slot rectangle, replaces the
        // previous whole-bar InvisibleButton that visually underlapped the slots (SetItemAllowOverlap()'s hover
        // arbitration was already tried live and did not resolve the reported drop failure — see DrawDragLayer's
        // doc comment). This is a structural UI improvement approved regardless of the drag/drop root cause: only
        // added when EditMode is on, so a locked bar's footprint/appearance is completely unchanged.
        var handleHeight = EditMode ? BaseHandleHeight * hotbar.Scale : 0f;
        var gridSize = new Vector2(columns * slotSize + (columns - 1) * spacing + padding * 2, rows * slotSize + (rows - 1) * spacing + padding * 2);
        var size = new Vector2(gridSize.X, gridSize.Y + handleHeight);

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
            var gridOrigin = origin + new Vector2(0, handleHeight);
            var drawList = ImGui.GetWindowDrawList();
            var alpha = Math.Clamp(hotbar.Transparency, MacroHotbar.MinTransparency, MacroHotbar.MaxTransparency);
            drawList.AddRectFilled(gridOrigin, gridOrigin + gridSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.04f, 0.05f, 0.72f * alpha)), 4f);
            drawList.AddRect(gridOrigin, gridOrigin + gridSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f * alpha)), 4f, ImDrawFlags.None, 1.5f);

            // The handle strip occupies its own reserved region above the slot grid — it never shares screen space
            // with a slot, so there is no overlapping-item hover arbitration to get wrong here (unlike the previous
            // whole-bar layer, which visually underlapped every slot).
            if (EditMode) DrawDragHandle(index, origin, new Vector2(size.X, handleHeight));

            for (var slot = 0; slot < MacroHotbarLayouts.SlotCount; slot++)
            {
                var (col, row) = MacroHotbarLayouts.Position(hotbar.Layout, slot);
                var slotMin = gridOrigin + new Vector2(padding + col * (slotSize + spacing), padding + row * (slotSize + spacing));
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
            // over a valid target), independent of whether a drop actually completes this frame. The drag-handle
            // strip is now spatially disjoint from every slot (DrawBar's doc comment), so this no longer depends on
            // any overlap-hover arbitration with a whole-bar layer.
            if (ImGui.IsItemHovered()) drawList.AddRect(slotMin - new Vector2(1, 1), slotMin + slotSizeVec + new Vector2(1, 1), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.8f, 0.2f, 0.95f)), 3f, ImDrawFlags.None, 2.5f);
            if (MacroDragDrop.AcceptTarget() is { } droppedMacroId) service.DropMacroOntoSlot(hotbarIndex, slot, droppedMacroId);
        }
        ImGui.PopID();
    }

    /// <summary>Edit-mode dragging — 0.3.0 UI PASS: this used to be a whole-bar <c>InvisibleButton</c> drawn
    /// UNDERNEATH every slot with <c>SetItemAllowOverlap()</c> called on it, on the theory that Dear ImGui's
    /// first-item-claims-hover-unless-marked-overlappable rule was silently stealing hover from every slot's own
    /// <c>InvisibleButton</c> (drawn after, per-slot). That fix was applied and shipped, but the live-reported
    /// symptom (dragging a Live tile onto a slot does nothing) persisted — so that theory is NOT treated as proven
    /// here; see <c>docs/MACRO_IMPLEMENTATION.md</c>'s dated fix-pass section and <c>MacroDragDrop</c>'s
    /// instrumentation for the actual live-QA diagnosis. Independent of that unresolved question, this region is
    /// now a dedicated handle strip that never shares screen space with any slot rectangle at all — movement and
    /// slot drop-targets can no longer compete for hover by construction, which is a real improvement regardless of
    /// where the drag/drop failure turns out to be. Persists the final position via
    /// <see cref="MacroService.SetHotbarPosition"/> only on mouse release, not every dragged frame.</summary>
    private void DrawDragHandle(int hotbarIndex, Vector2 origin, Vector2 size)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.PushID($"hotbar{hotbarIndex}-drag");
        ImGui.InvisibleButton("##drag", size);
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
        var hovered = ImGui.IsItemHovered();
        drawList.AddRectFilled(origin, origin + size, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.8f, 0.2f, hovered ? 0.35f : 0.22f)), 4f, ImDrawFlags.RoundCornersTop);
        // A small grip cue (three short horizontal ticks) makes the handle read as "grab here" rather than a plain
        // colored strip — cheap, theme-independent (this HUD overlay intentionally doesn't use VenueOS theme tokens
        // — see this file's class-level doc comment), and consistent regardless of hotbar scale.
        var center = origin + size / 2;
        for (var i = -1; i <= 1; i++)
        {
            var y = center.Y + i * 3f;
            drawList.AddLine(new Vector2(center.X - 8, y), new Vector2(center.X + 8, y), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.6f)), 1.5f);
        }
        ImGui.PopID();
    }
}
