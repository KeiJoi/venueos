using VenueOS.Core;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Bingo completed live QA in the 0.2.0 pass and graduated out of the old <c>UnfinishedModuleDefaultsTests</c>
/// (since renamed/inverted into <see cref="RaffleAndBracketsReleaseStatusTests"/> when those two modules were
/// promoted in turn) — this asserts the inverse of what that class used to check for Bingo: enabled by default on
/// a fresh install, and no longer flagged
/// <see cref="ModuleDescriptor.UnderDevelopment"/>. Automated payout specifically remains an experimental,
/// conservatively-documented convenience feature (see BINGO_PAYOUT_AUTOMATION_DEFERRED.md and the User Manual's
/// Bingo section) — that caveat is a documentation/UX concern, not a reason to keep the whole module gated.</summary>
public sealed class BingoReleaseStatusTests
{
    private static VenueProfileService FreshProfiles() => new(new InMemoryVenueStore(), new ModuleHost());
    private static DiagnosticsService Diagnostics(VenueProfileService profiles) => new(new ModuleHost(), profiles, new SystemClock());
    private static ChatCommandService BingoChat() => new(new SystemClock(), new InlineFrameworkDispatcher(), _ => true);
    private static VenueBingoModule FreshBingoModule()
    {
        var profiles = FreshProfiles();
        return new VenueBingoModule(new VenueBingoService(new VenueBingoClient(new HttpClient()), profiles, Diagnostics(profiles), BingoChat()));
    }

    [Fact]
    public void Bingo_defaults_enabled_on_a_fresh_install()
    {
        Assert.True(FreshBingoModule().IsEnabled);
    }

    [Fact]
    public void Bingo_is_no_longer_flagged_under_development()
    {
        var module = FreshBingoModule();
        Assert.Equal("games.bingo", module.Descriptor.Id);
        Assert.False(module.Descriptor.UnderDevelopment);
    }

    [Fact]
    public void Bingo_module_id_is_unchanged()
    {
        // The stable ID is also the per-venue config persistence key (NEW_MODULE_GUIDE.md §3) - promoting a
        // module's release status must never touch it, or every existing venue's saved Bingo configuration would
        // orphan.
        Assert.Equal("games.bingo", FreshBingoModule().Descriptor.Id);
    }
}
