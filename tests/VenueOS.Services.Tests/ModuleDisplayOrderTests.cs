using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Verifies the final, user-selected module display order (ShoutRunner, Attendance, Greeter, VIP, Party
/// Finder, Mair's Trivia, Mair's Editor, Bingo, Raffle, Brackets) end to end, using the real production
/// <see cref="IVenueModule"/> wrapper classes and their real <see cref="ModuleDescriptor.DisplayOrder"/> values —
/// not a synthetic stand-in — so a future accidental change to any module's <c>DisplayOrder</c> argument breaks
/// this test, not just a generic-mechanism test. Both Home and Settings → Modules iterate the same
/// <see cref="ModuleHost.Modules"/> property this test reads, so there is exactly one ordering to get right.
/// ShoutRunner and Party Finder need their unsafe automation engines only through the small interfaces
/// (<see cref="IShoutRunnerAutomation"/>/<see cref="IPartyFinderAutomation"/>) NEW_MODULE_GUIDE.md §30 designed
/// specifically so tests never need a live Dalamud/game context — the no-op fakes below stand in for them.
/// Raffle and Brackets were promoted out of UnderDevelopment in the release-preparation pass (see
/// <see cref="RaffleAndBracketsReleaseStatusTests"/>) and are now enabled by default like every other module this
/// test registers. Block Letters, Giveaways, and Macro are also production modules as of this release, but are
/// covered by their own dedicated descriptor/default tests (<c>BlockLettersServiceTests</c>,
/// <c>GiveawayServiceTests</c>, <c>MacroServiceTests</c>) rather than this comprehensive cross-module ordering
/// fixture, which predates them.</summary>
public sealed class ModuleDisplayOrderTests
{
    private static readonly string[] ExpectedDisplayNamesInOrder =
    [
        "ShoutRunner", "Attendance", "Greeter", "VIP", "Party Finder", "Mair's Trivia", "Mair's Editor",
        "Bingo", "Raffle", "Brackets",
    ];

    [Fact]
    public void All_ten_modules_sort_into_the_exact_authoritative_order_regardless_of_registration_order()
    {
        var host = BuildRealModuleHost();
        Assert.Equal(ExpectedDisplayNamesInOrder, host.Modules.Select(x => x.Descriptor.DisplayName));
    }

    [Fact]
    public void Fresh_install_home_order_includes_every_module_now_that_all_ten_are_enabled_by_default()
    {
        // Every module registered in this fixture — including the now-promoted Raffle/Brackets — defaults to
        // IsEnabled = true on a fresh install, so HomeScreen's `modules.Modules.Where(x => x.IsEnabled)` filter
        // (see HomeScreen.DrawGrid) is a no-op here and the Home-visible order matches the full authoritative order.
        var host = BuildRealModuleHost();
        var homeVisible = host.Modules.Where(x => x.IsEnabled).Select(x => x.Descriptor.DisplayName);
        Assert.Equal(ExpectedDisplayNamesInOrder, homeVisible);
        Assert.All(host.Modules, m => Assert.False(m.Descriptor.UnderDevelopment));
    }

    private static ModuleHost BuildRealModuleHost()
    {
        var clock = new SystemClock();
        var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true);
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var db = new SqliteVenueDatabase("Data Source=:memory:");
        var presence = new PresenceService(clock, new EmptySnapshotProvider());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, clock);

        var attendanceService = new AttendanceService(presence, db, clock);
        var greeterService = new GreeterService(clock, chat, db);
        var vip = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(_ => false, greeterService, vip, chat);
        var shoutRunnerService = new ShoutRunnerService(new NoOpShoutRunnerAutomation(), chat, profiles, diagnostics, clock, new NoOpShoutRunnerRecoveryStore());
        var partyFinderService = new PartyFinderService(new NoOpPartyFinderAutomation(), profiles, clock);
        var library = new FileQuestionSetRepository(Path.Combine(Path.GetTempPath(), "venueos-order-" + Guid.NewGuid()));
        var triviaService = new MairsTriviaService(new MairsTriviaClient(new HttpClient()), profiles, library, diagnostics);
        var raffleService = new VenueRaffleService(new VenueRaffleClient(new HttpClient()), profiles, diagnostics);
        var bingoService = new VenueBingoService(new VenueBingoClient(new HttpClient()), profiles, diagnostics, chat);
        var tournamentService = new TournamentControlService(new TournamentControlClient(new HttpClient()), profiles, new TournamentCalloutService(new SchedulerService(clock), chat), diagnostics);

        // Registered deliberately out of the expected display order, to prove sorting (not registration order)
        // decides the outcome.
        var host = new ModuleHost();
        host.Register(new TournamentControlModule(tournamentService));
        host.Register(new VenueRaffleModule(raffleService));
        host.Register(new MairsEditorModule());
        host.Register(new AttendanceModule(attendanceService, presence, profiles));
        host.Register(new VenueBingoModule(bingoService));
        host.Register(new VipModule(vip, coordinator, profiles));
        host.Register(new MairsTriviaModule(triviaService));
        host.Register(new ShoutRunnerModule(shoutRunnerService));
        host.Register(new GreeterModule(greeterService, profiles));
        host.Register(new PartyFinderModule(partyFinderService));
        return host;
    }

    private sealed class EmptySnapshotProvider : IObjectSnapshotProvider
    {
        public IReadOnlyList<PlayerSnapshot> Snapshot() => [];
    }

    private sealed class NoOpPartyFinderAutomation : IPartyFinderAutomation
    {
        public string Status => "";
        public bool IsCompatibilityVerified => false;
        public bool HasOwnListing => false;
        public bool IsBusy => false;
        public bool IsEnding => false;
        public void ResetForVenue() { }
        public void Abort() { }
        public void QueueCreateOrUpdate(PartyFinderPreset preset, string reason) { }
        public void QueueRefresh(PartyFinderPreset preset, string reason) { }
        public void EndPartyFinder(string reason) { }
        public void NotifyListingEnded() { }
    }

    private sealed class NoOpShoutRunnerAutomation : IShoutRunnerAutomation
    {
        public void ResetForVenue() { }
        public void Abort() { }
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => Task.FromResult(ShoutRunnerCrossDataCenterCheck.Unknown);
        public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) => Task.FromResult(ShoutRunnerTransferOutcome.Success());
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());
    }

    private sealed class NoOpShoutRunnerRecoveryStore : IShoutRunnerRecoveryStore
    {
        public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt) { corrupt = false; return null; }
        public void Save(ShoutRunnerRecoveryJournal journal) { }
        public void Delete() { }
    }
}
