using System.Net;
using System.Reflection;
using VenueOS.Core;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>0.3.8 maintenance: a raffle's Starting Pot, Ticket Cost, Prize %, Paid Tickets For Free and Free Tickets
/// Per Block are captured from Settings → New Raffle Defaults when the raffle is created and are immutable for that
/// raffle afterwards. Settings changes only ever affect the next raffle.</summary>
public sealed class RaffleFrozenConfigurationTests
{
    private static readonly RaffleSettings Original = new(StartingPot: 1000, TicketCost: 10, PrizePercentage: 80, PaidTicketsForFree: 5, FreeTicketsPerBlock: 1);
    private static readonly RaffleSettings Changed = new(StartingPot: 9000, TicketCost: 20, PrizePercentage: 90, PaidTicketsForFree: 2, FreeTicketsPerBlock: 3);

    private static (VenueRaffleService Service, VenueProfileService Profiles, DiagnosticsService Diagnostics) Build()
    {
        var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost());
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new SystemClock());
        var service = NewService(profiles, diagnostics);
        service.Load(profiles.Current.Id);
        return (service, profiles, diagnostics);
    }

    private static VenueRaffleService NewService(VenueProfileService profiles, DiagnosticsService diagnostics) =>
        new(new VenueRaffleClient(new HttpClient(new StubHandler())), profiles, diagnostics);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public void Create_snapshots_all_five_values_from_the_current_defaults()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);

        var raffle = service.Create("Friday");

        Assert.Equal(1000f, raffle.Settings.StartingPot);
        Assert.Equal(10f, raffle.Settings.TicketCost);
        Assert.Equal(80f, raffle.Settings.PrizePercentage);
        Assert.Equal(5, raffle.Settings.PaidTicketsForFree);
        Assert.Equal(1, raffle.Settings.FreeTicketsPerBlock);
    }

    [Theory]
    [InlineData("StartingPot")]
    [InlineData("TicketCost")]
    [InlineData("PrizePercentage")]
    [InlineData("PaidTicketsForFree")]
    [InlineData("FreeTicketsPerBlock")]
    public void Editing_a_single_default_after_create_never_changes_the_active_raffle(string field)
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);
        var raffle = service.Create("Friday");

        service.SaveDefaults(field switch
        {
            "StartingPot" => Original with { StartingPot = Changed.StartingPot },
            "TicketCost" => Original with { TicketCost = Changed.TicketCost },
            "PrizePercentage" => Original with { PrizePercentage = Changed.PrizePercentage },
            "PaidTicketsForFree" => Original with { PaidTicketsForFree = Changed.PaidTicketsForFree },
            "FreeTicketsPerBlock" => Original with { FreeTicketsPerBlock = Changed.FreeTicketsPerBlock },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        });

        Assert.Equal(Original, service.Selected!.Settings);
        Assert.Equal(raffle.Id, service.Selected!.Id);
    }

    [Fact]
    public void Ticket_bonus_calculations_use_the_frozen_rule_not_the_edited_defaults()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);              // 5 paid -> 1 free
        var raffle = service.Create("Friday");
        service.SaveDefaults(Changed);               // would be 2 paid -> 3 free if it leaked

        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 10);

        var participant = Assert.Single(service.Selected!.Participants);
        Assert.Equal(10, participant.PaidTickets);
        Assert.Equal(2, participant.FreeTickets);    // 10/5 = 2 blocks x 1 free (frozen), not 10/2 x 3 = 15
    }

    [Fact]
    public void Pot_prize_and_house_take_use_the_frozen_values_not_the_edited_defaults()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 10);

        service.SaveDefaults(Changed);
        service.AddPaidTickets(raffle.Id, "Bob", "Balmung", 10);

        var current = service.Selected!;
        Assert.Equal(1000f + 10f * 20, current.RunningPot);           // 1000 start + 20 paid x 10 each
        Assert.Equal(1200f * 0.8f, current.PrizePot, 3);              // 80%, not 90%
        Assert.Equal(current.RunningPot - current.PrizePot, current.HouseTake, 3);
    }

    [Fact]
    public void A_later_raffle_uses_the_newly_configured_values_while_the_earlier_one_keeps_its_own()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);
        var first = service.Create("First");

        service.SaveDefaults(Changed);
        var second = service.Create("Second");

        Assert.Equal(Original, service.Settings.Raffles.Single(x => x.Id == first.Id).Settings);
        Assert.Equal(Changed, service.Settings.Raffles.Single(x => x.Id == second.Id).Settings);
    }

    [Fact]
    public void Lifecycle_operations_never_alter_a_raffles_frozen_values()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);
        var raffle = service.Create("Friday");
        service.SaveDefaults(Changed);

        service.Rename(raffle.Id, "Renamed");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 5);
        service.AddFreeTickets(raffle.Id, "Ada", "Balmung", 1);
        service.AdjustTickets(raffle.Id, service.Selected!.Participants.Single().IdentityKey, 1, 0);
        service.Archive(raffle.Id);
        service.Unarchive(raffle.Id);
        service.Reset(raffle.Id);

        Assert.Equal(Original, service.Selected!.Settings);
    }

    [Fact]
    public void The_per_raffle_update_funnel_re_imposes_the_frozen_settings_even_if_a_transform_tries_to_change_them()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Original);
        var raffle = service.Create("Friday");

        var update = typeof(VenueRaffleService).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!;
        update.Invoke(service, [raffle.Id, new Func<LocalRaffle, LocalRaffle>(r => r with { Name = "Edited", Settings = Changed })]);

        Assert.Equal("Edited", service.Selected!.Name);          // the legitimate part of the transform still applies
        Assert.Equal(Original, service.Selected!.Settings);      // the settings part does not
    }

    [Fact]
    public void The_service_exposes_no_public_way_to_set_an_existing_raffles_settings()
    {
        var methods = typeof(VenueRaffleService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, m => m.Name == "UpdateRaffleSettings");
        // The only public method that accepts RaffleSettings is the next-raffle defaults setter.
        var accepting = methods.Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(RaffleSettings))).Select(m => m.Name);
        Assert.Equal(["SaveDefaults"], accepting);
    }

    [Fact]
    public void A_reloaded_service_restores_the_frozen_values_and_the_next_create_uses_the_current_defaults()
    {
        var (service, profiles, diagnostics) = Build();
        service.SaveDefaults(Original);
        var raffle = service.Create("Friday");
        service.AddPaidTickets(raffle.Id, "Ada", "Balmung", 5);
        service.SaveDefaults(Changed);

        var reloaded = NewService(profiles, diagnostics);        // simulates /xlreload: a fresh service over the persisted store
        reloaded.Load(profiles.Current.Id);

        var restored = reloaded.Settings.Raffles.Single(x => x.Id == raffle.Id);
        Assert.Equal(Original, restored.Settings);
        Assert.Equal(Changed, reloaded.Settings.Defaults);
        Assert.Equal(1000f + 10f * 5, restored.RunningPot);
        Assert.Equal(Changed, reloaded.Create("Next").Settings);
    }

    [Fact]
    public void Venues_keep_independent_defaults_and_frozen_raffle_values()
    {
        var (service, profiles, _) = Build();
        var venueA = profiles.Current;
        var venueB = profiles.Create("Venue B");

        service.Load(venueA.Id);
        service.SaveDefaults(Original);
        var raffleA = service.Create("A raffle");

        service.Load(venueB.Id);
        Assert.Equal(new RaffleSettings(), service.Settings.Defaults);      // B never sees A's defaults
        service.SaveDefaults(Changed);
        var raffleB = service.Create("B raffle");
        Assert.Equal(Changed, raffleB.Settings);

        service.Load(venueA.Id);
        Assert.Equal(Original, service.Settings.Defaults);
        Assert.Equal(Original, service.Settings.Raffles.Single(x => x.Id == raffleA.Id).Settings);   // B's edits never touched A's raffle
        Assert.DoesNotContain(service.Settings.Raffles, x => x.Id == raffleB.Id);

        service.SaveDefaults(Original with { TicketCost = 77 });
        service.Load(venueB.Id);
        Assert.Equal(Changed, service.Settings.Raffles.Single(x => x.Id == raffleB.Id).Settings);    // and vice versa
    }

    [Fact]
    public void An_imported_raffle_owns_the_settings_from_its_file_regardless_of_the_current_defaults()
    {
        var (service, _, _) = Build();
        service.SaveDefaults(Changed);
        var imported = new LocalRaffle("imp", "Imported", DateTime.UtcNow, Original, [], []);

        service.ImportRaffle(imported);
        service.SaveDefaults(Changed with { TicketCost = 1 });

        Assert.Equal(Original, service.Selected!.Settings);
    }
}
