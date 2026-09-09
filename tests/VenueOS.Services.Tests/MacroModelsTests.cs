using VenueOS.Modules.Operations.Macro;

namespace VenueOS.Services.Tests;

/// <summary><see cref="MacroNameValidator"/> and the hotbar layout/slot model (MACRO spec §7, §25-§30).</summary>
public sealed class MacroModelsTests
{
    [Fact]
    public void Blank_name_is_rejected()
    {
        Assert.NotNull(MacroNameValidator.Validate("   ", null, []));
    }

    [Fact]
    public void Duplicate_name_is_rejected_case_insensitively()
    {
        var existing = new[] { new SavedMacro(Guid.NewGuid(), "Craft HQ Widget", 0, 1, []) };
        Assert.NotNull(MacroNameValidator.Validate("craft hq widget", null, existing));
    }

    [Fact]
    public void Renaming_a_macro_to_its_own_current_name_is_allowed_via_ignoreId()
    {
        var id = Guid.NewGuid();
        var existing = new[] { new SavedMacro(id, "Craft HQ Widget", 0, 1, []) };
        Assert.Null(MacroNameValidator.Validate("Craft HQ Widget", id, existing));
    }

    [Fact]
    public void Distinct_name_is_accepted()
    {
        var existing = new[] { new SavedMacro(Guid.NewGuid(), "A", 0, 1, []) };
        Assert.Null(MacroNameValidator.Validate("B", null, existing));
    }

    [Fact]
    public void Default_settings_has_exactly_four_hotbars_each_with_twelve_empty_slots()
    {
        var settings = MacroSettings.Default();
        Assert.Equal(MacroHotbar.MaxHotbars, settings.Hotbars.Count);
        Assert.All(settings.Hotbars, h =>
        {
            Assert.False(h.Enabled);
            Assert.Equal(MacroHotbarLayouts.SlotCount, h.SlotMacroIds.Count);
            Assert.All(h.SlotMacroIds, slot => Assert.Null(slot));
            Assert.Equal(1f, h.Scale);
            Assert.Equal(1f, h.Transparency);
            Assert.Null(h.Position);
        });
        Assert.Equal([0, 1, 2, 3], settings.Hotbars.Select(h => h.Index));
    }

    [Theory]
    [InlineData(MacroHotbarLayout.Grid12x1, 12, 1)]
    [InlineData(MacroHotbarLayout.Grid6x2, 6, 2)]
    [InlineData(MacroHotbarLayout.Grid4x3, 4, 3)]
    [InlineData(MacroHotbarLayout.Grid3x4, 3, 4)]
    [InlineData(MacroHotbarLayout.Grid2x6, 2, 6)]
    [InlineData(MacroHotbarLayout.Grid1x12, 1, 12)]
    public void Every_supported_layout_has_the_expected_dimensions_covering_all_twelve_slots(MacroHotbarLayout layout, int columns, int rows)
    {
        var (c, r) = MacroHotbarLayouts.Dimensions(layout);
        Assert.Equal(columns, c);
        Assert.Equal(rows, r);
        Assert.Equal(MacroHotbarLayouts.SlotCount, c * r);

        // Every logical slot maps to a distinct, in-bounds (column, row) position — no overlap, no gaps.
        var positions = Enumerable.Range(0, MacroHotbarLayouts.SlotCount).Select(i => MacroHotbarLayouts.Position(layout, i)).ToArray();
        Assert.Equal(MacroHotbarLayouts.SlotCount, positions.Distinct().Count());
        Assert.All(positions, p => Assert.InRange(p.Column, 0, c - 1));
        Assert.All(positions, p => Assert.InRange(p.Row, 0, r - 1));
    }

    [Fact]
    public void Layout_change_never_alters_logical_slot_assignment()
    {
        // Layout is purely presentational (spec §29) — MacroService.SetHotbarLayout only ever replaces the Layout
        // field, never touches SlotMacroIds. Asserted directly at the model level: the same MacroHotbar record
        // carries its assignments across a `with { Layout = ... }` unchanged.
        var macroId = Guid.NewGuid();
        var slots = new Guid?[MacroHotbarLayouts.SlotCount];
        slots[5] = macroId;
        var hotbar = MacroHotbar.CreateDefault(0) with { SlotMacroIds = slots, Layout = MacroHotbarLayout.Grid12x1 };

        var relaidOut = hotbar with { Layout = MacroHotbarLayout.Grid3x4 };
        Assert.Equal(macroId, relaidOut.SlotMacroIds[5]);
        Assert.Equal(hotbar.SlotMacroIds, relaidOut.SlotMacroIds);
    }
}
