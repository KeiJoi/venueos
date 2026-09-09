using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Modules.Operations.Macro;

/// <summary>The one line-length budget Macro uses: the REAL FFXIV chat/command input limit, NOT the FFXIV built-in
/// macro editor's shorter per-line limit (<see cref="BlockLettersLimits.MacroLineBytes"/>, 181 bytes — that number
/// is explicitly the constraint this module exists to escape, per the MACRO task spec §8). Reuses Block Letters'
/// already-verified <see cref="BlockLettersLimits.ChatBytes"/> constant and its UTF-8-byte-based counting/editing
/// primitives (<see cref="BlockTextLength"/>, <see cref="BlockTextEditor"/>) rather than duplicating the magic
/// number or the counting logic — see docs/MACRO_IMPLEMENTATION.md §8/§9 for the full sourcing writeup.</summary>
public static class MacroLineLimits
{
    public const int MaxLineBytes = BlockLettersLimits.ChatBytes;
}

/// <summary>A single saved VenueOS macro. <see cref="IconId"/> is a runtime FFXIV game-icon identifier (requested
/// through Dalamud's <c>ITextureProvider</c> at render time — see <c>MacroIconPicker</c>/<c>DalamudActionReadyProbe</c>'s
/// doc comments in VenueOS.Plugin.Macro), never a bundled/exported image — 0 means "no icon chosen yet".
/// <see cref="DelayBetweenLinesSeconds"/> is this macro's own global delay, applied by <c>MacroRunner</c> between
/// every line it sends/executes, including after a nested child macro fully returns (MACRO spec §10/§13).</summary>
public sealed record SavedMacro(Guid Id, string Name, uint IconId, double DelayBetweenLinesSeconds, IReadOnlyList<string> Lines)
{
    public static SavedMacro CreateNew(string name) => new(Guid.NewGuid(), name, IconId: 0, DelayBetweenLinesSeconds: 1.0, Lines: Array.Empty<string>());
}

/// <summary>Pure name-uniqueness/validity rules (MACRO spec §7): nonblank, case-insensitively unique within the
/// venue. Centralized here so <c>MacroService</c>'s Create/Rename/Duplicate all enforce the exact same rule.</summary>
public static class MacroNameValidator
{
    public static string? Validate(string name, Guid? ignoreId, IReadOnlyList<SavedMacro> existing)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Macro name cannot be blank.";
        if (existing.Any(m => m.Id != ignoreId && string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"A macro named \"{trimmed}\" already exists.";
        return null;
    }
}

/// <summary>The 12-slot logical arrangement a faux hotbar presents itself in (MACRO spec §25/§29). The logical slot
/// index (0-11) is always the assignment key — layout only changes how those 12 slots are laid out on screen, and
/// switching layout MUST NOT rearrange which macro sits in which logical slot.</summary>
public enum MacroHotbarLayout { Grid12x1, Grid6x2, Grid4x3, Grid3x4, Grid2x6, Grid1x12 }

public static class MacroHotbarLayouts
{
    public const int SlotCount = 12;

    public static (int Columns, int Rows) Dimensions(MacroHotbarLayout layout) => layout switch
    {
        MacroHotbarLayout.Grid12x1 => (12, 1),
        MacroHotbarLayout.Grid6x2 => (6, 2),
        MacroHotbarLayout.Grid4x3 => (4, 3),
        MacroHotbarLayout.Grid3x4 => (3, 4),
        MacroHotbarLayout.Grid2x6 => (2, 6),
        MacroHotbarLayout.Grid1x12 => (1, 12),
        _ => (12, 1),
    };

    /// <summary>Row-major mapping from logical slot index (0-11, the authoritative assignment key) to a (column,
    /// row) grid position for the given layout — the same mapping used to both draw and hit-test a hotbar.</summary>
    public static (int Column, int Row) Position(MacroHotbarLayout layout, int slotIndex)
    {
        var (columns, _) = Dimensions(layout);
        return (slotIndex % columns, slotIndex / columns);
    }

    public static IReadOnlyList<string> DisplayNames { get; } = ["12x1", "6x2", "4x3", "3x4", "2x6", "1x12"];
    public static IReadOnlyList<MacroHotbarLayout> Values { get; } =
        [MacroHotbarLayout.Grid12x1, MacroHotbarLayout.Grid6x2, MacroHotbarLayout.Grid4x3, MacroHotbarLayout.Grid3x4, MacroHotbarLayout.Grid2x6, MacroHotbarLayout.Grid1x12];
}

/// <summary>A hotbar's saved screen position. A plain record with public PROPERTIES (X/Y), deliberately NOT
/// <c>System.Numerics.Vector2</c> — <c>System.Text.Json</c>'s default options (which <c>VenueProfileService</c>
/// serializes every module config through, unmodified) only serialize public properties, and <c>Vector2</c>'s X/Y
/// are public FIELDS, so a <c>Vector2?</c> stored directly here would silently round-trip as an empty object /
/// default(0,0) — a live-verified persistence bug caught by this module's own tests. Converted to/from
/// <c>System.Numerics.Vector2</c> only at the ImGui boundary in <c>VenueOS.Plugin.Macro</c>.</summary>
public sealed record MacroPosition(float X, float Y);

/// <summary>One of up to four persistent faux FFXIV-style hotbars (MACRO spec §24/§30). <see cref="SlotMacroIds"/>
/// always has exactly <see cref="MacroHotbarLayouts.SlotCount"/> entries, indexed by logical slot — a null entry is
/// an empty slot. <see cref="Position"/>/<see cref="Scale"/>/<see cref="Transparency"/> are all per-venue, matching
/// every other piece of this module's state (see MacroSettings' doc comment for why — MACRO spec §34's "preferred
/// first interpretation").</summary>
public sealed record MacroHotbar(int Index, bool Enabled, MacroHotbarLayout Layout, IReadOnlyList<Guid?> SlotMacroIds, MacroPosition? Position, float Scale, float Transparency)
{
    public const int MaxHotbars = 4;
    public const float MinScale = 0.5f, MaxScale = 2.0f;
    public const float MinTransparency = 0.2f, MaxTransparency = 1.0f;

    public static MacroHotbar CreateDefault(int index) => new(index, Enabled: false, MacroHotbarLayout.Grid12x1,
        SlotMacroIds: Enumerable.Repeat((Guid?)null, MacroHotbarLayouts.SlotCount).ToArray(), Position: null, Scale: 1f, Transparency: 1f);
}

/// <summary>Macro's entire per-venue persistent state (MACRO spec §3/§40/§41 — Settings authors both the library and
/// the hotbar configuration). State authority (NEW_MODULE_GUIDE.md §34a): this module's local per-venue config is
/// the ONLY durable state it owns — no backend, no shared/other-module state. The saved macro LIBRARY and the
/// HOTBAR configuration (enabled/layout/assignments/position/scale/transparency) are both per-venue, per the task's
/// explicit instruction (§34: "all macro assignments are per venue") extended consistently to hotbar position/scale/
/// transparency too, since nothing in this codebase's architecture (NEW_MODULE_GUIDE.md §12) suggests a window-
/// position preference should be global — Auto Pop-Out Modules (§7) is the one documented global exception, and it
/// is a fundamentally different kind of preference (how VenueOS itself launches modules, not a piece of drawn
/// content). Active/running macro-execution state (<c>MacroRunner</c>'s stack, phase, status message) is
/// deliberately NOT part of this record and is never persisted (NEW_MODULE_GUIDE.md §13a) — it always resets to
/// idle on venue switch, module disable, or plugin reload.</summary>
public sealed record MacroSettings(IReadOnlyList<SavedMacro> Macros, IReadOnlyList<MacroHotbar> Hotbars)
{
    public static MacroSettings Default() => new(
        Macros: Array.Empty<SavedMacro>(),
        Hotbars: Enumerable.Range(0, MacroHotbar.MaxHotbars).Select(MacroHotbar.CreateDefault).ToArray());
}
