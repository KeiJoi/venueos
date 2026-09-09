using VenueOS.Core;
using VenueOS.Modules.Operations.Macro;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary><see cref="MacroService"/>: per-venue persistence/isolation (NEW_MODULE_GUIDE.md §13a/§30), the
/// module-enable activation lifecycle bug and its fix (MACRO LIVE QA FIX §2-§6), transactional macro-body
/// create/edit and its name-uniqueness/line-length validation (fix spec §17), rename reference-cascade and delete
/// reference-detection (spec §16/§27), and hotbar configuration persistence/independence including the shared
/// drag/drop assignment policy (spec §30/§41, fix spec §22/§24). Uses a real <see cref="VenueProfileService"/>/
/// <see cref="InMemoryVenueStore"/> and reconstructs the service from the same store to prove persistence across an
/// actual reload boundary, matching every other module's test style (<c>GiveawayServiceTests</c>,
/// <c>BlockLettersServiceTests</c>).</summary>
public sealed class MacroServiceTests
{
    // =========================================================================================================
    // MACRO LIVE QA FIX §2-§6: the release-blocking module-enable activation bug. A module that starts
    // UnderDevelopment/disabled by default (NEW_MODULE_GUIDE.md §22a) never receives its first OnVenueChangedAsync
    // call — VenueProfileService.InitializeAsync's one NotifyVenueChangedAsync pass, and every SwitchAsync call
    // after it, are both gated by ModuleHost.IsolateAsync's `if (!module.IsEnabled) return;` (NEW_MODULE_GUIDE.md
    // §23). Before the fix (ModulesSettingsPage.ActivateNewlyEnabledModule), enabling a module live via the
    // Settings toggle only flipped IsEnabled with no further effect, so MacroService.Load was never called and its
    // private venue-id field stayed at its default (Guid.Empty) — every macro saved that session was written under
    // the WRONG per-venue key. These tests exercise the exact broken order, reproducing why the OLD reload-boundary
    // tests (which always called Load() immediately after construction) never caught this: they never modeled a
    // service that had NEVER been loaded before its first save.
    // =========================================================================================================

    [Fact]
    public void Bug_reproduction_a_service_that_is_never_loaded_before_saving_writes_under_the_wrong_venue_and_loses_data_on_reload()
    {
        var t = CreateWithoutLoading();
        // service.Load(...) is deliberately never called — this is the exact pre-fix state a newly-enabled module's
        // service was left in for the rest of its session.
        t.Service.CreateMacro("Ghost Macro", 0, 1.0, "/say ghost", out var created);
        Assert.NotNull(created); // the mutation "succeeds" and is visible in-memory, exactly like the live symptom

        var reloadedAgainstRealVenue = ReloadAgainstVenue(t, t.VenueId);
        Assert.DoesNotContain(reloadedAgainstRealVenue.Settings.Macros, m => m.Name == "Ghost Macro"); // proves the mechanism: saved under Guid.Empty, invisible to the real venue
    }

    [Fact]
    public async Task Fix_activating_a_never_loaded_service_for_the_current_venue_before_any_macro_is_created_makes_persistence_work_correctly()
    {
        var t = CreateWithoutLoading();
        var module = new MacroModule(t.Service);

        // This is exactly what ModulesSettingsPage.ActivateNewlyEnabledModule now does the moment a module is
        // toggled on: call OnVenueChangedAsync once for the CURRENTLY active venue.
        await module.OnVenueChangedAsync(new VenueContext(t.Profiles.Current.Id, t.Profiles.Current.DisplayName, t.Profiles.Current.Theme), CancellationToken.None);

        t.Service.CreateMacro("Macro A", 0, 1.0, "/say A", out _);
        t.Service.CreateMacro("Macro B", 0, 1.0, "/say B", out _);

        var reloaded = ReloadAgainstVenue(t, t.Profiles.Current.Id);
        Assert.Equal(2, reloaded.Settings.Macros.Count);
        Assert.Contains(reloaded.Settings.Macros, m => m.Name == "Macro A");
        Assert.Contains(reloaded.Settings.Macros, m => m.Name == "Macro B");
    }

    // =========================================================================================================
    // Reload-boundary persistence — every saved field survives a real service reconstruction (spec §5/§6/§32)
    // =========================================================================================================

    [Fact]
    public void Create_macro_persists_every_field_across_a_reload()
    {
        var t = Create();
        var result = t.Service.CreateMacro("Craft HQ Widget", 62583, 2.5, "/ac \"Basic Synthesis\"\n/echo done", out var created);
        Assert.True(result.Success);

        var reloaded = Reload(t);
        var saved = reloaded.Settings.Macros.Single(m => m.Id == created!.Id);
        Assert.Equal("Craft HQ Widget", saved.Name);
        Assert.Equal(62583u, saved.IconId);
        Assert.Equal(2.5, saved.DelayBetweenLinesSeconds);
        Assert.Equal(["/ac \"Basic Synthesis\"", "/echo done"], saved.Lines);
    }

    [Fact]
    public void Multiple_macros_all_survive_a_reload_with_stable_ids()
    {
        var t = Create();
        t.Service.CreateMacro("A", 1, 1.0, "/say a", out var a);
        t.Service.CreateMacro("B", 2, 2.0, "/say b", out var b);
        t.Service.CreateMacro("C", 3, 3.0, "/say c", out var c);

        var reloaded = Reload(t);
        Assert.Equal(3, reloaded.Settings.Macros.Count);
        Assert.Equal(a!.Id, reloaded.Settings.Macros.Single(m => m.Name == "A").Id);
        Assert.Equal(b!.Id, reloaded.Settings.Macros.Single(m => m.Name == "B").Id);
        Assert.Equal(c!.Id, reloaded.Settings.Macros.Single(m => m.Name == "C").Id);
    }

    [Fact]
    public void Disable_then_reenable_does_not_lose_macros()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out _);
        var module = new MacroModule(t.Service);

        module.IsEnabled = false; // §22: disabling never touches saved config
        var reloaded = Reload(t);
        Assert.Single(reloaded.Settings.Macros);
    }

    // =========================================================================================================
    // Transactional create/edit + validation (fix spec §12/§17)
    // =========================================================================================================

    [Fact]
    public void Blank_name_is_rejected_and_nothing_is_saved()
    {
        var t = Create();
        var result = t.Service.CreateMacro("   ", 0, 1.0, "/say hi", out var created);
        Assert.False(result.Success);
        Assert.Null(created);
        Assert.Empty(t.Service.Settings.Macros);
    }

    [Fact]
    public void Duplicate_name_is_rejected_case_insensitively_on_create()
    {
        var t = Create();
        t.Service.CreateMacro("Craft HQ Widget", 0, 1.0, "/say hi", out _);
        var result = t.Service.CreateMacro("craft hq widget", 0, 1.0, "/say hi", out var created);
        Assert.False(result.Success);
        Assert.Null(created);
    }

    [Fact]
    public void An_oversized_line_is_rejected_with_its_line_number_and_nothing_is_saved()
    {
        var t = Create();
        var tooLong = new string('a', MacroLineLimits.MaxLineBytes + 28);
        var result = t.Service.CreateMacro("A", 0, 1.0, $"/say ok\n{tooLong}", out var created);

        Assert.False(result.Success);
        Assert.Null(created);
        Assert.Contains(result.Errors, e => e.StartsWith("Line 2 is", StringComparison.Ordinal));
        Assert.Empty(t.Service.Settings.Macros); // never silently truncated and saved (fix spec §12)
    }

    [Fact]
    public void A_line_at_exactly_the_limit_is_accepted()
    {
        var t = Create();
        var exact = new string('a', MacroLineLimits.MaxLineBytes);
        var result = t.Service.CreateMacro("A", 0, 1.0, exact, out var created);
        Assert.True(result.Success);
        Assert.Equal(exact, created!.Lines[0]);
    }

    [Fact]
    public void Save_edit_preserves_stable_id_and_persists_the_new_body()
    {
        var t = Create();
        t.Service.CreateMacro("A", 1, 1.0, "/say old", out var original);

        var result = t.Service.SaveMacro(original!.Id, "A Renamed", 9, 3.0, "/say new1\n/say new2");
        Assert.True(result.Success);

        var reloaded = Reload(t);
        var saved = reloaded.Settings.Macros.Single();
        Assert.Equal(original.Id, saved.Id); // stable id preserved across an edit
        Assert.Equal("A Renamed", saved.Name);
        Assert.Equal(9u, saved.IconId);
        Assert.Equal(3.0, saved.DelayBetweenLinesSeconds);
        Assert.Equal(["/say new1", "/say new2"], saved.Lines);
    }

    [Fact]
    public void Save_edit_rejects_a_rename_to_a_duplicate_name_and_leaves_the_original_untouched()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out _);
        t.Service.CreateMacro("B", 0, 1.0, "/say b", out var b);

        var result = t.Service.SaveMacro(b!.Id, "a", 0, 1.0, "/say b");
        Assert.False(result.Success);
        Assert.Equal("B", t.Service.Settings.Macros.Single(m => m.Id == b.Id).Name);
    }

    [Fact]
    public void Save_edit_updates_exact_nested_references_in_other_macros_but_not_ordinary_text()
    {
        var t = Create();
        t.Service.CreateMacro("Child", 0, 1.0, "/say child", out var child);
        t.Service.CreateMacro("Parent", 0, 1.0, $"{MacroDirectiveParser.FormatNestedInvocation("Child")}\n/say Child is just a mention here", out var parent);

        t.Service.SaveMacro(child!.Id, "Renamed Child", 0, 1.0, "/say child");

        var updatedParent = t.Service.Settings.Macros.Single(m => m.Id == parent!.Id);
        Assert.Equal(MacroDirectiveParser.FormatNestedInvocation("Renamed Child"), updatedParent.Lines[0]);
        Assert.Equal("/say Child is just a mention here", updatedParent.Lines[1]); // untouched
    }

    [Fact]
    public void Save_edit_without_a_name_change_does_not_touch_other_macros_bodies()
    {
        var t = Create();
        t.Service.CreateMacro("Child", 0, 1.0, "/say child", out var child);
        var originalLine = MacroDirectiveParser.FormatNestedInvocation("Child");
        t.Service.CreateMacro("Parent", 0, 1.0, originalLine, out var parent);

        t.Service.SaveMacro(child!.Id, "Child", 0, 5.0, "/say child v2"); // same name, only delay/body changed

        Assert.Equal(originalLine, t.Service.Settings.Macros.Single(m => m.Id == parent!.Id).Lines[0]);
    }

    [Fact]
    public void Save_edit_reports_not_found_for_an_unknown_id()
    {
        var t = Create();
        var result = t.Service.SaveMacro(Guid.NewGuid(), "X", 0, 1.0, "/say x");
        Assert.False(result.Success);
    }

    [Fact]
    public void Duplicate_macro_creates_an_independent_copy_with_a_generated_distinct_name()
    {
        var t = Create();
        t.Service.CreateMacro("Original", 0, 1.0, "/say Hello", out var original);
        var result = t.Service.DuplicateMacro(original!.Id, out var copy);

        Assert.True(result.Success);
        Assert.NotEqual(original.Id, copy!.Id);
        Assert.NotEqual(original.Name, copy.Name);
        Assert.Equal(["/say Hello"], t.Service.Settings.Macros.Single(m => m.Id == copy.Id).Lines);

        t.Service.SaveMacro(copy.Id, copy.Name, copy.IconId, copy.DelayBetweenLinesSeconds, "/say Only in copy");
        Assert.Equal(["/say Hello"], t.Service.Settings.Macros.Single(m => m.Id == original.Id).Lines); // original untouched
    }

    [Fact]
    public void Delete_removes_the_macro_and_clears_hotbar_assignments_referencing_it()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.AssignSlot(0, 3, macro!.Id);

        t.Service.DeleteMacro(macro.Id);

        Assert.Empty(t.Service.Settings.Macros);
        Assert.Null(t.Service.Settings.Hotbars[0].SlotMacroIds[3]);
    }

    [Fact]
    public void FindReferencingMacros_reports_parents_before_a_delete_so_the_caller_can_confirm()
    {
        var t = Create();
        t.Service.CreateMacro("Child", 0, 1.0, "/say child", out var child);
        t.Service.CreateMacro("Parent", 0, 1.0, MacroDirectiveParser.FormatNestedInvocation("Child"), out var parent);

        var referencing = t.Service.FindReferencingMacros(child!.Id);
        Assert.Equal([parent!.Id], referencing.Select(m => m.Id));
    }

    [Fact]
    public void Arbitrary_line_counts_are_supported()
    {
        var t = Create();
        var body = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"/say Line {i}"));
        var result = t.Service.CreateMacro("A", 0, 1.0, body, out var created);
        Assert.True(result.Success);
        Assert.Equal(200, created!.Lines.Count);
    }

    // =========================================================================================================
    // Hotbar configuration (spec §30/§41) + shared drag/drop assignment policy (fix spec §22/§24)
    // =========================================================================================================

    [Fact]
    public void Hotbar_slot_assignment_persists_and_clearing_a_slot_never_deletes_the_macro()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.AssignSlot(2, 5, macro!.Id);

        var reloaded = Reload(t);
        Assert.Equal(macro.Id, reloaded.Settings.Hotbars[2].SlotMacroIds[5]);

        t.Service.ClearSlot(2, 5);
        Assert.Null(t.Service.Settings.Hotbars[2].SlotMacroIds[5]);
        Assert.Contains(t.Service.Settings.Macros, m => m.Id == macro.Id);
    }

    [Fact]
    public void DropMacroOntoSlot_assigns_an_empty_slot()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.DropMacroOntoSlot(0, 4, macro!.Id);
        Assert.Equal(macro.Id, t.Service.Settings.Hotbars[0].SlotMacroIds[4]);
    }

    [Fact]
    public void DropMacroOntoSlot_swaps_when_the_macro_is_already_on_the_same_hotbar()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var a);
        t.Service.CreateMacro("B", 0, 1.0, "/say b", out var b);
        t.Service.AssignSlot(0, 0, a!.Id);
        t.Service.AssignSlot(0, 1, b!.Id);

        t.Service.DropMacroOntoSlot(0, 1, a.Id); // A (currently in slot 0) dropped onto slot 1 (currently B)

        Assert.Equal(b.Id, t.Service.Settings.Hotbars[0].SlotMacroIds[0]);
        Assert.Equal(a.Id, t.Service.Settings.Hotbars[0].SlotMacroIds[1]);
    }

    [Fact]
    public void DropMacroOntoSlot_from_a_different_hotbar_just_assigns_not_swaps()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var a);
        t.Service.AssignSlot(0, 0, a!.Id); // A lives on hotbar 0

        t.Service.DropMacroOntoSlot(1, 5, a.Id); // dropped onto hotbar 1 — no existing A on THIS hotbar to swap with

        Assert.Equal(a.Id, t.Service.Settings.Hotbars[1].SlotMacroIds[5]);
        Assert.Equal(a.Id, t.Service.Settings.Hotbars[0].SlotMacroIds[0]); // hotbar 0's assignment is untouched
    }

    [Fact]
    public void DropMacroOntoSlot_with_a_macro_id_that_does_not_exist_fails_safely()
    {
        // A slot CAN legitimately reference a macro id that no longer resolves (e.g. deleted later) — the renderer
        // already handles that safely (renders an empty slot, never crashes). The assignment mutation itself must
        // not validate/crash either — it only ever stores the id.
        var t = Create();
        var ghostId = Guid.NewGuid();
        t.Service.DropMacroOntoSlot(0, 0, ghostId);

        Assert.Equal(ghostId, t.Service.Settings.Hotbars[0].SlotMacroIds[0]);
        var reloaded = Reload(t);
        Assert.Equal(ghostId, reloaded.Settings.Hotbars[0].SlotMacroIds[0]); // round-trips safely, no exception
    }

    [Fact]
    public void Assigning_a_macro_to_a_hotbar_slot_never_deletes_the_macro_itself()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.DropMacroOntoSlot(0, 0, macro!.Id);
        t.Service.DropMacroOntoSlot(1, 5, macro.Id);
        t.Service.SwapSlots(0, 0, 1);

        Assert.Contains(t.Service.Settings.Macros, m => m.Id == macro.Id);
        Assert.Single(t.Service.Settings.Macros);
    }

    [Fact]
    public void The_four_hotbars_remain_fully_independent()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var a);
        t.Service.CreateMacro("B", 0, 1.0, "/say b", out var b);

        t.Service.SetHotbarEnabled(0, true);
        t.Service.SetHotbarLayout(0, MacroHotbarLayout.Grid6x2);
        t.Service.AssignSlot(0, 0, a!.Id);

        t.Service.SetHotbarEnabled(2, true);
        t.Service.SetHotbarLayout(2, MacroHotbarLayout.Grid4x3);
        t.Service.AssignSlot(2, 0, b!.Id);

        var hotbars = t.Service.Settings.Hotbars;
        Assert.True(hotbars[0].Enabled);
        Assert.False(hotbars[1].Enabled);
        Assert.True(hotbars[2].Enabled);
        Assert.False(hotbars[3].Enabled);
        Assert.Equal(MacroHotbarLayout.Grid6x2, hotbars[0].Layout);
        Assert.Equal(MacroHotbarLayout.Grid12x1, hotbars[1].Layout); // untouched default
        Assert.Equal(MacroHotbarLayout.Grid4x3, hotbars[2].Layout);
        Assert.Equal(a.Id, hotbars[0].SlotMacroIds[0]);
        Assert.Equal(b.Id, hotbars[2].SlotMacroIds[0]);
        Assert.Null(hotbars[1].SlotMacroIds[0]);
        Assert.Null(hotbars[3].SlotMacroIds[0]);
    }

    [Fact]
    public void Changing_layout_never_changes_logical_slot_assignments()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.AssignSlot(0, 7, macro!.Id);
        t.Service.SetHotbarLayout(0, MacroHotbarLayout.Grid3x4);

        Assert.Equal(macro.Id, t.Service.Settings.Hotbars[0].SlotMacroIds[7]);
        Assert.Equal(MacroHotbarLayout.Grid3x4, t.Service.Settings.Hotbars[0].Layout);
    }

    [Fact]
    public void Position_scale_and_transparency_persist()
    {
        var t = Create();
        t.Service.SetHotbarPosition(0, new MacroPosition(120, 340));
        t.Service.SetHotbarScale(0, 1.5f);
        t.Service.SetHotbarTransparency(0, 0.6f);

        var reloaded = Reload(t);
        var hotbar = reloaded.Settings.Hotbars[0];
        Assert.Equal(new MacroPosition(120, 340), hotbar.Position);
        Assert.Equal(1.5f, hotbar.Scale);
        Assert.Equal(0.6f, hotbar.Transparency);
    }

    [Fact]
    public void Hotbar_config_survives_the_same_reload_boundary_as_the_macro_library()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 1.0, "/say a", out var macro);
        t.Service.SetHotbarEnabled(1, true);
        t.Service.SetHotbarLayout(1, MacroHotbarLayout.Grid6x2);
        t.Service.AssignSlot(1, 3, macro!.Id);

        var reloaded = Reload(t);
        var hotbar = reloaded.Settings.Hotbars[1];
        Assert.True(hotbar.Enabled);
        Assert.Equal(MacroHotbarLayout.Grid6x2, hotbar.Layout);
        Assert.Equal(macro.Id, hotbar.SlotMacroIds[3]);
        // The assignment resolves back to a real, restored macro record, not a dangling id.
        Assert.Contains(reloaded.Settings.Macros, m => m.Id == hotbar.SlotMacroIds[3]);
    }

    // =========================================================================================================
    // Venue isolation (NEW_MODULE_GUIDE.md §12a)
    // =========================================================================================================

    [Fact]
    public void Two_venues_never_see_each_others_macros_or_hotbar_config()
    {
        var t = Create();
        t.Service.CreateMacro("Venue A Macro", 0, 1.0, "/say a", out _);
        t.Service.SetHotbarEnabled(0, true);

        var venueB = t.Profiles.Create("Second Venue").Id;
        t.Service.Load(venueB);
        Assert.Empty(t.Service.Settings.Macros);
        Assert.False(t.Service.Settings.Hotbars[0].Enabled);

        t.Service.Load(t.VenueId);
        Assert.Single(t.Service.Settings.Macros);
        Assert.True(t.Service.Settings.Hotbars[0].Enabled);
    }

    [Fact]
    public async Task Venue_switch_cancels_an_in_flight_run_via_the_module()
    {
        var t = Create();
        t.Service.CreateMacro("A", 0, 100.0, "/say One\n/say Two", out var macro);
        t.Service.Launch(macro!.Id);
        Assert.True(t.Service.Runner.IsRunning);

        var module = new MacroModule(t.Service);
        await module.OnVenueChangedAsync(new VenueContext(t.Profiles.Create("Another").Id, "Another", t.Profiles.Current.Theme), CancellationToken.None);
        Assert.False(t.Service.Runner.IsRunning);
    }

    // =========================================================================================================
    // Module descriptor
    // =========================================================================================================

    [Fact]
    public void Module_is_promoted_to_production_and_enabled_by_default()
    {
        // Promoted out of UnderDevelopment (NEW_MODULE_GUIDE.md §22a) after live acceptance testing — see
        // docs/MACRO_IMPLEMENTATION.md and the release-preparation report. The live tile → faux hotbar drag/drop
        // known issue is documented as non-blocking and does not gate promotion.
        var t = Create();
        var module = new MacroModule(t.Service);
        Assert.Equal("tools.macro", module.Descriptor.Id);
        Assert.False(module.Descriptor.UnderDevelopment);
        Assert.True(module.IsEnabled);
    }

    // =========================================================================================================
    // Test infrastructure
    // =========================================================================================================

    private sealed record Fixture(MacroService Service, VenueProfileService Profiles, InMemoryVenueStore Store, Guid VenueId, DiagnosticsService Diagnostics);

    private static Fixture Create()
    {
        var t = CreateWithoutLoading();
        t.Service.Load(t.VenueId);
        return t;
    }

    private static Fixture CreateWithoutLoading()
    {
        var clock = new SystemClock();
        var scheduler = new SchedulerService(clock);
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true);
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, clock);
        var service = new MacroService(scheduler, chat, profiles, new AlwaysReadyProbe(), diagnostics);
        var venueId = profiles.Current.Id;
        return new Fixture(service, profiles, store, venueId, diagnostics);
    }

    private static MacroService Reload(Fixture t) => ReloadAgainstVenue(t, t.VenueId);

    private static MacroService ReloadAgainstVenue(Fixture t, Guid venueId)
    {
        var clock = new SystemClock();
        var reloaded = new MacroService(new SchedulerService(clock), new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true), t.Profiles, new AlwaysReadyProbe(), t.Diagnostics);
        reloaded.Load(venueId);
        return reloaded;
    }

    private sealed class AlwaysReadyProbe : IActionReadyProbe
    {
        public ActionReadyState Query() => ActionReadyState.Ready;
    }
}
