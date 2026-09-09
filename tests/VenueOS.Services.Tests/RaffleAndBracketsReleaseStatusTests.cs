using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Raffle and Brackets (formerly TournamentControl) completed reconstruction and live QA and were
/// promoted out of UnderDevelopment in the release-preparation pass — this file is the renamed/inverted
/// successor to the old <c>UnfinishedModuleDefaultsTests</c>, which asserted the opposite (disabled-by-default,
/// flagged under development) before that promotion. It follows the same pattern
/// <see cref="BingoReleaseStatusTests"/> established when Bingo graduated in the 0.2.0 pass. Home/Settings
/// rendering itself isn't covered here — no VenueOS.Plugin test project exists, deliberately, since ImGui needs
/// a live rendering context (see NEW_MODULE_GUIDE.md §30) — only the underlying data those surfaces read.</summary>
public sealed class RaffleAndBracketsReleaseStatusTests
{
    private static VenueProfileService FreshProfiles() => new(new InMemoryVenueStore(), new ModuleHost());
    private static VenueRaffleService FreshRaffleService() { var profiles = FreshProfiles(); return new VenueRaffleService(new VenueRaffleClient(new HttpClient()), profiles, new DiagnosticsService(new ModuleHost(), profiles, new SystemClock())); }
    private static TournamentCalloutService Callouts() => new(new SchedulerService(new SystemClock()), new ChatCommandService(new SystemClock(), new InlineFrameworkDispatcher(), _ => true));

    [Fact]
    public void Raffle_defaults_enabled_on_a_fresh_install()
    {
        var module = new VenueRaffleModule(FreshRaffleService());
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Brackets_defaults_enabled_on_a_fresh_install()
    {
        var profiles = FreshProfiles(); var module = new TournamentControlModule(new TournamentControlService(new TournamentControlClient(new HttpClient()), profiles, Callouts(), new DiagnosticsService(new ModuleHost(), profiles, new SystemClock())));
        Assert.True(module.IsEnabled);
    }

    [Fact]
    public void Raffle_is_no_longer_flagged_under_development()
    {
        var module = new VenueRaffleModule(FreshRaffleService());
        Assert.Equal("games.raffle", module.Descriptor.Id);
        Assert.False(module.Descriptor.UnderDevelopment);
    }

    [Fact]
    public void Brackets_is_no_longer_flagged_under_development()
    {
        var profiles = FreshProfiles(); var module = new TournamentControlModule(new TournamentControlService(new TournamentControlClient(new HttpClient()), profiles, Callouts(), new DiagnosticsService(new ModuleHost(), profiles, new SystemClock())));
        Assert.Equal("games.tournament", module.Descriptor.Id);
        Assert.False(module.Descriptor.UnderDevelopment);
    }
}
