using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Covers the release requirement that Raffle and TournamentControl — neither of which has completed
/// reconstruction or live QA — ship disabled on a brand-new VenueOS installation and are flagged as under
/// development in their <see cref="ModuleDescriptor"/> (surfaced by Settings → Modules). Bingo's equivalent,
/// now-inverted assertions (release-ready, enabled by default, no longer flagged) live in
/// <see cref="BingoReleaseStatusTests"/> since it graduated out of this "still unfinished" group in the 0.2.0 pass.
/// Home/Settings rendering itself isn't covered here — no VenueOS.Plugin test project exists, deliberately, since
/// ImGui needs a live rendering context (see NEW_MODULE_GUIDE.md §30) — only the underlying data those surfaces
/// read.</summary>
public sealed class UnfinishedModuleDefaultsTests
{
    private static VenueProfileService FreshProfiles() => new(new InMemoryVenueStore(), new ModuleHost());
    private static TournamentCalloutService Callouts() => new(new SchedulerService(new SystemClock()), new ChatCommandService(new SystemClock(), new InlineFrameworkDispatcher(), _ => true));

    [Fact]
    public void Raffle_defaults_disabled_on_a_fresh_install()
    {
        var module = new VenueRaffleModule(new VenueRaffleService(new VenueRaffleClient(new HttpClient()), FreshProfiles()));
        Assert.False(module.IsEnabled);
    }

    [Fact]
    public void TournamentControl_defaults_disabled_on_a_fresh_install()
    {
        var module = new TournamentControlModule(new TournamentControlService(new TournamentControlClient(new HttpClient()), FreshProfiles(), Callouts()));
        Assert.False(module.IsEnabled);
    }

    [Fact]
    public void Raffle_is_flagged_under_development()
    {
        var module = new VenueRaffleModule(new VenueRaffleService(new VenueRaffleClient(new HttpClient()), FreshProfiles()));
        Assert.Equal("games.raffle", module.Descriptor.Id);
        Assert.True(module.Descriptor.UnderDevelopment);
    }

    [Fact]
    public void TournamentControl_is_flagged_under_development()
    {
        var module = new TournamentControlModule(new TournamentControlService(new TournamentControlClient(new HttpClient()), FreshProfiles(), Callouts()));
        Assert.Equal("games.tournament", module.Descriptor.Id);
        Assert.True(module.Descriptor.UnderDevelopment);
    }
}
