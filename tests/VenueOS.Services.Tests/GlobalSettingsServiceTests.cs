using System.Text.Json;
using VenueOS.Core;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class GlobalSettingsServiceTests
{
    [Fact] public void Auto_pop_out_defaults_to_off() { var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore()); Assert.False(service.AutoPopOutModules); }
    [Fact] public void Setting_auto_pop_out_updates_the_in_memory_value() { var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore()); service.SetAutoPopOutModules(true); Assert.True(service.AutoPopOutModules); }
    [Fact] public void Auto_pop_out_persists_through_the_store_across_a_reload() { var store = new InMemoryGlobalSettingsStore(); new GlobalSettingsService(store).SetAutoPopOutModules(true); var reloaded = new GlobalSettingsService(store); Assert.True(reloaded.AutoPopOutModules); }
    [Fact] public void Turning_auto_pop_out_off_again_persists_too() { var store = new InMemoryGlobalSettingsStore(); var service = new GlobalSettingsService(store); service.SetAutoPopOutModules(true); service.SetAutoPopOutModules(false); Assert.False(new GlobalSettingsService(store).AutoPopOutModules); }
    [Fact] public void Setting_the_same_value_does_not_write_to_the_store() { var store = new InMemoryGlobalSettingsStore(); var service = new GlobalSettingsService(store); service.SetAutoPopOutModules(false); Assert.Equal(0, store.WriteCount); }

    [Fact] public void Launch_routing_resolves_embedded_when_auto_pop_out_is_off() => Assert.Equal(ModuleLaunchTarget.Embedded, ModuleLaunchRouting.Resolve(autoPopOutModules: false));
    [Fact] public void Launch_routing_resolves_detached_when_auto_pop_out_is_on() => Assert.Equal(ModuleLaunchTarget.Detached, ModuleLaunchRouting.Resolve(autoPopOutModules: true));

    [Fact] public void A_module_with_no_explicit_toggle_has_no_override() { var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore()); Assert.Null(service.GetModuleEnabledOverride("games.raffle")); }
    [Fact] public void Explicitly_enabling_a_module_records_an_override() { var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore()); service.SetModuleEnabled("games.raffle", true); Assert.True(service.GetModuleEnabledOverride("games.raffle")); }
    [Fact] public void Explicitly_disabling_a_module_records_an_override() { var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore()); service.SetModuleEnabled("core.attendance", false); Assert.False(service.GetModuleEnabledOverride("core.attendance")); }
    [Fact] public void A_module_override_survives_a_reload_and_is_not_lost_by_a_changed_code_default()
    {
        // Simulates exactly the scenario a release-default change must not break: an operator explicitly enabled an
        // unfinished module before this release shipped it disabled by default. Their choice must still win after
        // reload, regardless of what the module's own IsEnabled initializer now defaults to.
        var store = new InMemoryGlobalSettingsStore();
        new GlobalSettingsService(store).SetModuleEnabled("games.bingo", true);
        var reloaded = new GlobalSettingsService(store);
        Assert.True(reloaded.GetModuleEnabledOverride("games.bingo"));
    }
    [Fact] public void Overrides_for_different_modules_do_not_collide()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        service.SetModuleEnabled("games.raffle", true);
        service.SetModuleEnabled("games.bingo", false);
        Assert.True(service.GetModuleEnabledOverride("games.raffle"));
        Assert.False(service.GetModuleEnabledOverride("games.bingo"));
        Assert.Null(service.GetModuleEnabledOverride("games.tournament"));
    }

    // ---- Module Launcher --------------------------------------------------------------------------------------

    [Fact] public void Launcher_defaults_to_enabled_and_locked()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        Assert.True(service.Launcher.Enabled);
        Assert.True(service.Launcher.Locked);
        Assert.Equal(6, service.Launcher.ButtonsPerRow);
    }

    [Fact] public void Setting_the_launcher_updates_the_in_memory_value()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        service.SetLauncher(service.Launcher with { Enabled = false, ButtonsPerRow = 4 });
        Assert.False(service.Launcher.Enabled);
        Assert.Equal(4, service.Launcher.ButtonsPerRow);
    }

    [Fact] public void Launcher_persists_through_the_store_across_a_reload()
    {
        var store = new InMemoryGlobalSettingsStore();
        new GlobalSettingsService(store).SetLauncher(new LauncherSettings(Enabled: false, ButtonsPerRow: 3, Locked: false, Scale: 1.5f));
        var reloaded = new GlobalSettingsService(store);
        Assert.False(reloaded.Launcher.Enabled);
        Assert.Equal(3, reloaded.Launcher.ButtonsPerRow);
        Assert.False(reloaded.Launcher.Locked);
        Assert.Equal(1.5f, reloaded.Launcher.Scale);
    }

    [Fact] public void Setting_the_same_launcher_value_twice_only_writes_once()
    {
        var store = new InMemoryGlobalSettingsStore();
        var service = new GlobalSettingsService(store);
        var launcher = service.Launcher with { ButtonsPerRow = 4 };
        service.SetLauncher(launcher);
        service.SetLauncher(launcher);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact] public void Reset_launcher_position_resets_only_position_size_and_scale()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        service.SetLauncher(service.Launcher with { ButtonsPerRow = 8, Locked = false, PositionX = 999, PositionY = 999, Width = 1, Height = 1, Scale = 3 });
        service.ResetLauncherPosition(24, 24, 360, 72, 1.0f);

        Assert.Equal(24, service.Launcher.PositionX);
        Assert.Equal(24, service.Launcher.PositionY);
        Assert.Equal(360, service.Launcher.Width);
        Assert.Equal(72, service.Launcher.Height);
        Assert.Equal(1.0f, service.Launcher.Scale);
        // Everything else untouched:
        Assert.Equal(8, service.Launcher.ButtonsPerRow);
        Assert.False(service.Launcher.Locked);
    }

    [Fact] public void A_module_with_no_explicit_launcher_preference_is_shown_by_default() =>
        Assert.True(new GlobalSettingsService(new InMemoryGlobalSettingsStore()).IsShownOnLauncher("games.raffle"));

    [Fact] public void Explicitly_hiding_a_module_from_the_launcher_persists()
    {
        var store = new InMemoryGlobalSettingsStore();
        new GlobalSettingsService(store).SetShownOnLauncher("games.raffle", false);
        Assert.False(new GlobalSettingsService(store).IsShownOnLauncher("games.raffle"));
    }

    [Fact] public void Explicitly_showing_a_module_persists_too()
    {
        var store = new InMemoryGlobalSettingsStore();
        var service = new GlobalSettingsService(store);
        service.SetShownOnLauncher("games.raffle", false);
        service.SetShownOnLauncher("games.raffle", true);
        Assert.True(new GlobalSettingsService(store).IsShownOnLauncher("games.raffle"));
    }

    [Fact] public void Show_on_launcher_preferences_for_different_modules_do_not_collide()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        service.SetShownOnLauncher("games.raffle", false);
        service.SetShownOnLauncher("games.bingo", true);
        Assert.False(service.IsShownOnLauncher("games.raffle"));
        Assert.True(service.IsShownOnLauncher("games.bingo"));
        Assert.True(service.IsShownOnLauncher("games.tournament")); // untouched — still default-shown
    }

    /// <summary>A disabled module's explicit Show-on-Launcher preference must never be silently lost while it's
    /// disabled — mirrors the identical preservation guarantee already proven for
    /// <see cref="GlobalSettingsService.GetModuleEnabledOverride"/> above.</summary>
    [Fact] public void Show_on_launcher_preference_survives_a_reload_regardless_of_the_modules_enabled_state()
    {
        var store = new InMemoryGlobalSettingsStore();
        new GlobalSettingsService(store).SetShownOnLauncher("games.tournament", false);
        var reloaded = new GlobalSettingsService(store);
        Assert.False(reloaded.IsShownOnLauncher("games.tournament"));
    }

    [Fact] public void Setting_the_launcher_module_order_persists()
    {
        var store = new InMemoryGlobalSettingsStore();
        new GlobalSettingsService(store).SetLauncherModuleOrder(["games.bingo", "core.attendance"]);
        Assert.Equal(["games.bingo", "core.attendance"], new GlobalSettingsService(store).Launcher.ModuleOrder!);
    }

    // ---- Per-module detached-window preference -----------------------------------------------------------------

    [Fact] public void A_module_with_no_recorded_window_preference_returns_null() =>
        Assert.Null(new GlobalSettingsService(new InMemoryGlobalSettingsStore()).GetModuleWindowPreference("games.raffle"));

    [Fact] public void A_module_window_preference_persists_through_the_store_across_a_reload()
    {
        var store = new InMemoryGlobalSettingsStore();
        var preference = new ModuleWindowPreference(ModulePresentationState.Collapsed, 120, 240, 640, 480);
        new GlobalSettingsService(store).SetModuleWindowPreference("games.raffle", preference);
        var reloaded = new GlobalSettingsService(store).GetModuleWindowPreference("games.raffle");
        Assert.Equal(preference, reloaded);
    }

    [Fact] public void Module_window_preferences_for_different_modules_do_not_collide()
    {
        var service = new GlobalSettingsService(new InMemoryGlobalSettingsStore());
        service.SetModuleWindowPreference("games.raffle", new ModuleWindowPreference(ModulePresentationState.Expanded, 1, 1, 1, 1));
        service.SetModuleWindowPreference("games.bingo", new ModuleWindowPreference(ModulePresentationState.Collapsed, 2, 2, 2, 2));
        Assert.Equal(ModulePresentationState.Expanded, service.GetModuleWindowPreference("games.raffle")!.LastState);
        Assert.Equal(ModulePresentationState.Collapsed, service.GetModuleWindowPreference("games.bingo")!.LastState);
    }

    /// <summary>Live QA follow-up: the main tablet's own Collapse/Expand geometry is persisted through this exact
    /// same per-id preference path, under a reserved key (<c>"__venueos.tablet__"</c>) — nothing about this API is
    /// module-specific, so a reserved key round-trips exactly like a real module id, and never collides with one.</summary>
    [Fact] public void The_reserved_tablet_key_persists_and_isolates_exactly_like_a_modules_own_window_preference()
    {
        const string tabletKey = "__venueos.tablet__";
        var store = new InMemoryGlobalSettingsStore();
        var preference = new ModuleWindowPreference(ModulePresentationState.Collapsed, 24, 24, 980, 650);
        new GlobalSettingsService(store).SetModuleWindowPreference(tabletKey, preference);
        var reloaded = new GlobalSettingsService(store);
        Assert.Equal(preference, reloaded.GetModuleWindowPreference(tabletKey));
        Assert.Null(reloaded.GetModuleWindowPreference("games.raffle")); // no collision with a real module id
    }

    /// <summary>Crosses a real persistence boundary (same pattern as <c>PartyFinderServiceTests</c>'
    /// <c>Every_preset_field_survives_a_full_serialize_deserialize_boundary</c>): a plain POCO record round-trips
    /// through real JSON (de)serialization, exactly like Dalamud's own plugin-config file would, unlike the
    /// <c>JsonElement</c> corruption documented on <c>ModulePayload</c> — that issue doesn't apply here since
    /// nothing in <see cref="GlobalSettings"/> is a raw <c>JsonElement</c>.</summary>
    [Fact] public void Launcher_and_module_window_preferences_survive_a_full_serialize_deserialize_boundary()
    {
        var store = new InMemoryGlobalSettingsStore();
        var service = new GlobalSettingsService(store);
        service.SetLauncher(new LauncherSettings(Enabled: true, PositionX: 111, PositionY: 222, Width: 480, Height: 96, Scale: 1.25f, ButtonsPerRow: 7, Locked: false,
            ModuleOrder: ["games.bingo", "core.attendance"], ShowOnLauncher: new Dictionary<string, bool> { ["games.raffle"] = false }));
        service.SetModuleWindowPreference("games.bingo", new ModuleWindowPreference(ModulePresentationState.Collapsed, 50, 60, 700, 500));

        var json = JsonSerializer.Serialize(store.Read());
        var rebuilt = JsonSerializer.Deserialize<GlobalSettings>(json)!;
        var freshStore = new InMemoryGlobalSettingsStore(); freshStore.Write(rebuilt);
        var freshService = new GlobalSettingsService(freshStore);

        Assert.True(freshService.Launcher.Enabled);
        Assert.Equal(111, freshService.Launcher.PositionX);
        Assert.Equal(222, freshService.Launcher.PositionY);
        Assert.Equal(480, freshService.Launcher.Width);
        Assert.Equal(96, freshService.Launcher.Height);
        Assert.Equal(1.25f, freshService.Launcher.Scale);
        Assert.Equal(7, freshService.Launcher.ButtonsPerRow);
        Assert.False(freshService.Launcher.Locked);
        Assert.Equal(["games.bingo", "core.attendance"], freshService.Launcher.ModuleOrder!);
        Assert.False(freshService.IsShownOnLauncher("games.raffle"));
        Assert.True(freshService.IsShownOnLauncher("core.attendance"));

        var preference = freshService.GetModuleWindowPreference("games.bingo")!;
        Assert.Equal(ModulePresentationState.Collapsed, preference.LastState);
        Assert.Equal(50, preference.PosX);
        Assert.Equal(60, preference.PosY);
        Assert.Equal(700, preference.Width);
        Assert.Equal(500, preference.Height);
    }

    private sealed class InMemoryGlobalSettingsStore : IGlobalSettingsStore
    {
        private GlobalSettings settings = new();
        public int WriteCount { get; private set; }
        public GlobalSettings Read() => settings;
        public void Write(GlobalSettings value) { settings = value; WriteCount++; }
    }
}
