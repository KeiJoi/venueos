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

    private sealed class InMemoryGlobalSettingsStore : IGlobalSettingsStore
    {
        private GlobalSettings settings = new();
        public int WriteCount { get; private set; }
        public GlobalSettings Read() => settings;
        public void Write(GlobalSettings value) { settings = value; WriteCount++; }
    }
}
