using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Macro;

/// <summary>The one drag/drop payload mechanism for "assign this macro to this hotbar slot" — used identically by
/// the Settings hotbar editor's macro palette/slot grid AND (MACRO LIVE QA FIX §21/§22) the live Macro module's
/// tile launcher as a drag SOURCE and the live faux-hotbar overlay's Edit-mode slots as a drag TARGET. One shared
/// helper so every drag source/target pair in this module always agrees on the payload type/shape — never a
/// separate live-vs-Settings assignment subsystem (fix spec §22's explicit instruction).</summary>
internal static class MacroDragDrop
{
    private const string PayloadId = "VENUEOS_MACRO_ID";

    /// <summary>Call immediately after drawing a macro's clickable icon/tile — a no-op unless the operator actually
    /// drags past ImGui's own drag threshold, so it coexists safely with the SAME item's ordinary click handling
    /// (this is the exact pattern already proven by the Settings palette's icon buttons).</summary>
    public static void BeginSource(Guid macroId, string label)
    {
        if (!ImGui.BeginDragDropSource()) return;
        SetPayload(macroId);
        ImGui.TextUnformatted(label);
        ImGui.EndDragDropSource();
    }

    private static void SetPayload(Guid id) => ImGui.SetDragDropPayload(PayloadId, id.ToByteArray());

    /// <summary>Call immediately after drawing a slot that should accept a drop. Returns the dropped macro's id
    /// only on the frame a drop actually completes; returns null every other frame (including while a drag is
    /// merely hovering, and always when nothing is being dragged at all).</summary>
    public static unsafe Guid? AcceptTarget()
    {
        if (!ImGui.BeginDragDropTarget()) return null;
        Guid? result = null;
        var payload = ImGui.AcceptDragDropPayload(PayloadId);
        if (payload.Data != null && payload.DataSize == 16) result = new Guid(new ReadOnlySpan<byte>(payload.Data, (int)payload.DataSize));
        ImGui.EndDragDropTarget();
        return result;
    }
}

/// <summary>Renders one FFXIV game icon at runtime, shared by the icon picker, the macro tile launcher, and the
/// faux hotbar slots — one rendering path so every surface that shows a macro's icon looks identical (MACRO spec
/// §6). Icon source/API (documented per spec §6's requirement, full writeup in docs/MACRO_IMPLEMENTATION.md §7):
/// Dalamud's <see cref="ITextureProvider.GetFromGameIcon(in GameIconLookup)"/>, which returns an
/// <see cref="ISharedImmediateTexture"/> requesting the game's OWN icon texture by numeric id at render time —
/// nothing is exported, copied, or bundled; Dalamud loads/caches/evicts the texture itself. <c>GetWrapOrEmpty()</c>
/// never throws (an unresolved id — 0, or one with no texture — simply renders nothing this frame), matching this
/// module's "never crash on a bad icon id" requirement.</summary>
internal static class MacroIconRenderer
{
    public static void Draw(ITextureProvider textureProvider, VenueTheme theme, uint iconId, Vector2 min, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, UiKit.ColorU32(theme.Tokens.Surface), theme.Metrics.Rounding * 0.5f);
        if (iconId != 0)
        {
            var wrap = textureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            if (wrap.Handle != 0) drawList.AddImage(wrap.Handle, min, min + size);
        }
        drawList.AddRect(min, min + size, UiKit.ColorU32(theme.Tokens.Border), theme.Metrics.Rounding * 0.5f);
    }
}

/// <summary>Settings-only icon selector (MACRO spec §6): preview current icon, browse a curated subset, click to
/// choose — never a raw numeric-id text field. Icon SOURCE for the browsable list: Lumina's <c>GeneralAction</c>
/// sheet (via <see cref="IDataManager.GetExcelSheet{T}"/>), the same catalog of job-independent generic icons
/// (Sprint, Teleport, Return, Duty Action, Repair, etc.) FFXIV's own macro editor's "General" icon category is built
/// from — deliberately NOT "every game icon in existence" (spec §6's explicit instruction), and not job-specific
/// combat action icons either, since a VenueOS macro is never tied to a particular job. If the sheet can't be read
/// for any reason, the picker degrades to an empty catalog with an explanatory empty state rather than
/// throwing/crashing Settings.</summary>
internal sealed class MacroIconPicker(ITextureProvider textureProvider, IDataManager dataManager)
{
    private const float ButtonSize = 40f;
    private IReadOnlyList<(uint IconId, string Name)>? catalog;

    private IReadOnlyList<(uint IconId, string Name)> Catalog => catalog ??= BuildCatalog();

    private IReadOnlyList<(uint IconId, string Name)> BuildCatalog()
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.GeneralAction>();
            if (sheet is null) return Array.Empty<(uint, string)>();
            return sheet
                .Select(row => ((uint)row.Icon, row.Name.ExtractText().Trim()))
                .Where(x => x.Item1 != 0 && x.Item2.Length > 0)
                .GroupBy(x => x.Item1)
                .Select(g => g.First())
                .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<(uint, string)>();
        }
    }

    public void Draw(VenueTheme theme, uint currentIconId, Action<uint> onPicked)
    {
        Forms.FieldLabel(theme, "Icon");
        var previewMin = ImGui.GetCursorScreenPos();
        MacroIconRenderer.Draw(textureProvider, theme, currentIconId, previewMin, new Vector2(ButtonSize, ButtonSize));
        ImGui.Dummy(new Vector2(ButtonSize, ButtonSize));
        if (currentIconId == 0) { ImGui.SameLine(); UiKit.Tooltip("No icon chosen yet"); }

        ImGui.Spacing();
        if (Catalog.Count == 0)
        {
            UiKit.WarningState(theme, "The game's icon list could not be read this session — icon selection is unavailable, but everything else about this macro is unaffected.");
            return;
        }

        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)(avail / (ButtonSize + 6)));
        for (var i = 0; i < Catalog.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine();
            var (iconId, name) = Catalog[i];
            ImGui.PushID((int)iconId);
            var start = ImGui.GetCursorScreenPos();
            var size = new Vector2(ButtonSize, ButtonSize);
            var clicked = ImGui.InvisibleButton("##pick", size);
            var selected = iconId == currentIconId;
            if (selected) ImGui.GetWindowDrawList().AddRect(start - new Vector2(2, 2), start + size + new Vector2(2, 2), UiKit.ColorU32(theme.Tokens.Accent), theme.Metrics.Rounding, ImDrawFlags.None, 2f);
            MacroIconRenderer.Draw(textureProvider, theme, iconId, start, size);
            UiKit.Tooltip(name);
            if (clicked) onPicked(iconId);
            ImGui.PopID();
        }
    }
}
