using VenueOS.Core;
using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Persistence, per-venue isolation, and module-descriptor coverage for Block Letters — the parts of
/// NEW_MODULE_GUIDE.md §30/§42's Definition of Done that don't require ImGui.</summary>
public sealed class BlockLettersServiceTests
{
    [Fact]
    public void New_venue_gets_the_default_destination()
    {
        var (service, _, venueId) = Create();
        service.Load(venueId);
        Assert.Equal(BlockLettersDestination.Chat, service.Settings.DefaultDestination);
    }

    [Fact]
    public void Default_destination_persists_through_the_venue_store()
    {
        var (service, profiles, venueId) = Create();
        service.Load(venueId);
        service.SetDefaultDestination(BlockLettersDestination.MacroLine);

        var reloaded = new BlockLettersService(profiles);
        reloaded.Load(venueId);
        Assert.Equal(BlockLettersDestination.MacroLine, reloaded.Settings.DefaultDestination);
    }

    [Fact]
    public void Two_venue_profiles_do_not_leak_the_default_destination()
    {
        var (service, profiles, venueA) = Create();
        var venueB = profiles.Create("Second Venue").Id;

        service.Load(venueA);
        service.SetDefaultDestination(BlockLettersDestination.PartyFinderComment);

        service.Load(venueB);
        Assert.Equal(BlockLettersDestination.Chat, service.Settings.DefaultDestination); // venue B's own default, untouched

        service.Load(venueA);
        Assert.Equal(BlockLettersDestination.PartyFinderComment, service.Settings.DefaultDestination);
    }

    [Fact]
    public void Setting_the_same_destination_again_does_not_re_save()
    {
        // Guards against a redundant SaveModuleConfig call whenever the operator reopens Settings without changing
        // anything — mirrors the no-op guard every other module's setter uses (e.g. PartyFinderService.SetAutoRefreshEnabled).
        var (service, profiles, venueId) = Create();
        service.Load(venueId);
        service.SetDefaultDestination(BlockLettersDestination.Chat); // already the default — should be a no-op
        var reloaded = new BlockLettersService(profiles);
        reloaded.Load(venueId);
        Assert.Equal(BlockLettersDestination.Chat, reloaded.Settings.DefaultDestination);
    }

    [Fact]
    public void Module_id_is_tools_blockletters()
    {
        var module = new BlockLettersModule(Create().Service);
        Assert.Equal("tools.blockletters", module.Descriptor.Id);
    }

    [Fact]
    public void Display_name_is_block_letters()
    {
        var module = new BlockLettersModule(Create().Service);
        Assert.Equal("Block Letters", module.Descriptor.DisplayName);
    }

    [Fact]
    public void Module_is_no_longer_flagged_under_development()
    {
        // Promoted out of UnderDevelopment (NEW_MODULE_GUIDE.md §22a) after live acceptance testing — see
        // docs/BLOCK_LETTERS_IMPLEMENTATION.md and the release-preparation report.
        var module = new BlockLettersModule(Create().Service);
        Assert.False(module.Descriptor.UnderDevelopment);
    }

    [Fact]
    public void Module_defaults_enabled_on_a_fresh_install()
    {
        var module = new BlockLettersModule(Create().Service);
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Module_icon_key_is_registered_and_distinct()
    {
        var module = new BlockLettersModule(Create().Service);
        Assert.Equal("block-letters", module.Descriptor.Icon);
    }

    [Fact]
    public void Draw_and_draw_settings_use_distinct_delegates()
    {
        var drawCalls = 0; var settingsCalls = 0;
        var module = new BlockLettersModule(Create().Service, () => drawCalls++, () => settingsCalls++);
        module.Draw();
        module.DrawSettings();
        Assert.Equal(1, drawCalls);
        Assert.Equal(1, settingsCalls);
    }

    [Fact]
    public async Task Venue_change_loads_that_venues_settings()
    {
        var (service, profiles, venueA) = Create();
        var venueB = profiles.Create("Second Venue").Id;
        service.Load(venueA);
        service.SetDefaultDestination(BlockLettersDestination.MacroLine);

        var module = new BlockLettersModule(service);
        await module.OnVenueChangedAsync(new VenueContext(venueB, "Second Venue", profiles.Current.Theme), CancellationToken.None);
        Assert.Equal(BlockLettersDestination.Chat, service.Settings.DefaultDestination);
    }

    private static (BlockLettersService Service, VenueProfileService Profiles, Guid VenueId) Create()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var service = new BlockLettersService(profiles);
        return (service, profiles, profiles.Current.Id);
    }
}
