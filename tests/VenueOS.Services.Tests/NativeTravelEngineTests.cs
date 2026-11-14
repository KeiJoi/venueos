using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Services.Tests;

/// <summary>0.3.14 native World Visit / Data Center travel: the REAL <see cref="NativeTravelService"/> and
/// <see cref="NativeWorldVisitDriver"/> against <see cref="NativeTravelSimulator"/>, a deterministic model of the Patch
/// 7.58 window built from the owner's screenshots. Passing these proves the engine's logic and safety rules, NOT
/// compatibility with the real FFXIV UI (that needs the live QA in docs/SHOUTRUNNER_NATIVE_TRAVEL_LIVE_QA.md).</summary>
public sealed class NativeTravelEngineTests
{
    private static (NativeTravelService Service, NativeTravelSimulator Sim) Rig(string current, string? home = null, bool allowDc = true)
    {
        var sim = new NativeTravelSimulator(current, home);
        var service = new NativeTravelService(sim, sim.Catalog) { AllowDataCenterSelection = allowDc };
        return (service, sim);
    }

    private static NativeTravelResult Travel(NativeTravelService service, string target, NativeTravelOwner owner = NativeTravelOwner.QaTest, CancellationToken token = default) =>
        service.TravelAsync(new NativeTravelRequest(target, owner, [WorldVisitCities.Uldah]), token).GetAwaiter().GetResult();

    private static NativeTravelActionKind[] Kinds(NativeTravelSimulator sim) => [.. sim.Executed.Select(a => a.Kind)];

    // ----- successful travel -----

    [Fact]
    public void Same_DC_World_Visit_Adamantoise_to_Cactuar_selects_Cactuar_by_name_confirms_and_verifies_arrival()
    {
        var (service, sim) = Rig("Adamantoise");
        var result = Travel(service, "Cactuar");

        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal("Cactuar", sim.Current.Name);
        Assert.Equal([NativeTravelActionKind.InteractWithAetheryte, NativeTravelActionKind.SelectMenuEntry, NativeTravelActionKind.SelectWorld, NativeTravelActionKind.ConfirmTravel], Kinds(sim));
        Assert.Equal(1, sim.Executed.Single(a => a.Kind == NativeTravelActionKind.SelectMenuEntry).Index);   // "Visit Another World Server."
        Assert.Equal(sim.Catalog["Cactuar"].Id, sim.Executed.Single(a => a.Kind == NativeTravelActionKind.SelectWorld).Id);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void Screenshot_scenario_Cuchulainn_Dynamis_to_Behemoth_selects_Primal_then_Behemoth_then_confirms_Travel_to_Behemoth()
    {
        var (service, sim) = Rig("Cuchulainn");
        var result = Travel(service, "Behemoth");

        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal("Behemoth", sim.Current.Name);
        Assert.Equal("Primal", sim.Current.LogicalDataCenter);
        var dc = sim.Executed.Single(a => a.Kind == NativeTravelActionKind.SelectDataCenter);
        Assert.Equal("Primal", dc.Label);
        Assert.Equal(Array.IndexOf(sim.DcOrder, "Primal"), dc.Index);
        Assert.Equal("Behemoth", sim.Executed.Single(a => a.Kind == NativeTravelActionKind.SelectWorld).Label);
        Assert.Contains("confirmation verified: \"Travel to Behemoth?\"", result.Diagnostic);
        Assert.True(sim.Executed.FindIndex(a => a.Kind == NativeTravelActionKind.SelectDataCenter) < sim.Executed.FindIndex(a => a.Kind == NativeTravelActionKind.SelectWorld));
    }

    [Theory]
    [InlineData("Siren", "Balmung")]       // Aether → Crystal
    [InlineData("Balmung", "Cuchulainn")]  // Crystal → Dynamis
    [InlineData("Cuchulainn", "Adamantoise")] // Dynamis → Aether (no Home World detour, no logout)
    [InlineData("Adamantoise", "Ultros")]  // Aether → Primal
    public void Cross_DC_travel_between_every_NA_pair_uses_the_dropdown_and_never_anything_else(string from, string to)
    {
        var (service, sim) = Rig(from, home: "Cuchulainn");
        var result = Travel(service, to);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(to, sim.Current.Name);
        Assert.All(sim.Executed, a => Assert.Contains(a.Kind, new[] { NativeTravelActionKind.InteractWithAetheryte, NativeTravelActionKind.SelectMenuEntry, NativeTravelActionKind.SelectDataCenter, NativeTravelActionKind.SelectWorld, NativeTravelActionKind.ConfirmTravel }));
        Assert.False(sim.LoggedOut);
    }

    [Fact]
    public void The_Data_Center_item_is_chosen_by_label_even_when_the_dropdown_order_is_different()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DcOrder = ["Primal", "Dynamis", "Aether", "Crystal"];
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Gilgamesh").Outcome);
        Assert.Equal(2, sim.Executed.Single(a => a.Kind == NativeTravelActionKind.SelectDataCenter).Index);
    }

    [Fact]
    public void Already_on_the_World_does_nothing()
    {
        var (service, sim) = Rig("Cactuar");
        Assert.Equal(NativeTravelOutcome.AlreadyThere, Travel(service, "Cactuar").Outcome);
        Assert.Empty(sim.Executed);
    }

    [Theory]
    [InlineData(new[] { 9u, 8u }, 9u)]   // Ul'dah first on the list: Ul'dah
    [InlineData(new[] { 8u, 2u }, 2u)]   // Limsa first, but that is where the character stands: the next one, Gridania
    [InlineData(new[] { 8u }, 2u)]       // only Limsa on the list: another World Visit city (Gridania)
    public async Task Inside_a_World_Visit_city_but_too_far_to_walk_it_teleports_to_a_listed_city_other_than_the_one_it_is_in(uint[] listed, uint expectedAetheryte)
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;   // in Limsa, but far from the aetheryte
        sim.AetheryteDistance = 60f;
        var cities = listed.Select(id => WorldVisitCities.All.Single(c => c.AetheryteId == id)).ToList();
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, cities), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(NativeTravelActionKind.TeleportToCity, sim.Executed[0].Kind);
        Assert.Equal(expectedAetheryte, sim.Executed[0].Id);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.TeleportToCity));
    }

    /// <summary>Owner, 2026-11-13: "it shouldn't teleport back to one city then world transfer, it should world transfer
    /// from the last place it was." A ShoutRunner World that ended in Limsa (landing out of reach) or in-reach in Gridania
    /// travels from there, whatever city is first on the list.</summary>
    [Theory]
    [InlineData(129u, 13f)]   // Limsa Lominsa Lower Decks: the teleport landing, out of reach, so it walks
    [InlineData(129u, 29f)]   // still within the 30 y walking limit
    [InlineData(132u, 4f)]    // New Gridania, in reach
    public async Task Inside_a_World_Visit_city_it_travels_from_there_instead_of_teleporting_to_the_first_city_on_the_list(uint territoryId, float distance)
    {
        var (service, sim) = Rig("Adamantoise");
        var here = WorldVisitCities.ForTerritory(territoryId)!;
        sim.Territory = here.TerritoryId;
        sim.AetheryteDistance = distance;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.Uldah, WorldVisitCities.Gridania, WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.TeleportToCity));
        Assert.Equal(here.TerritoryId, sim.Territory);
        Assert.Equal(distance > NativeWorldVisitDriver.AetheryteReach ? 1 : 0, sim.Count(NativeTravelActionKind.ApproachAetheryte));
    }

    // ----- walking to an aetheryte the teleport left out of reach (owner, 2026-11-13: "Limsa is bad for that") -----

    [Fact]
    public async Task A_teleport_that_lands_out_of_reach_walks_to_the_aetheryte_then_stops_before_interacting()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 1186;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 13f;   // beyond the 8y reach
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        var kinds = sim.Executed.Select(a => a.Kind).ToList();
        Assert.Equal([NativeTravelActionKind.TeleportToCity, NativeTravelActionKind.ApproachAetheryte, NativeTravelActionKind.StopApproach, NativeTravelActionKind.InteractWithAetheryte], kinds.Take(4));
        Assert.False(sim.Walking);
        Assert.False(sim.WalkingStoppedByEndOperation);   // the driver itself stopped walking
        Assert.Equal(1, sim.Approaches);
    }

    [Fact]
    public async Task Live_QA_field_zone_to_Limsa_waits_through_the_teleport_cast_then_walks_and_travels()
    {
        // Live QA 2026-11-13: started in Western La Noscea (territory 138), teleported to Limsa; 0.3.14 build c9da804
        // gave up 0.2 s into the cast ("not in a World Visit city (territory 138)").
        var (service, sim) = Rig("Balmung", home: "Halicarnassus");
        sim.Territory = 138;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 13f;
        var result = await service.TravelAsync(new NativeTravelRequest("Halicarnassus", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Contains("arrived in Limsa Lominsa Lower Decks", result.Diagnostic);
        Assert.Equal([NativeTravelActionKind.TeleportToCity, NativeTravelActionKind.ApproachAetheryte, NativeTravelActionKind.StopApproach, NativeTravelActionKind.InteractWithAetheryte], sim.Executed.Select(a => a.Kind).Take(4));
        Assert.Equal(1, sim.Count(NativeTravelActionKind.TeleportToCity));
    }

    [Fact]
    public async Task Live_QA_an_obstacle_at_10_y_is_within_reach_so_it_stops_walking_and_uses_the_aetheryte()
    {
        // Live QA 2026-11-13 runs #3/#4: arrival 12.7 y, blocked by objects at 10.1 to 10.2 y. The owner confirmed the menu
        // opens from there; build 8175884 failed with "stopped getting closer … (10.2y away)".
        var (service, sim) = Rig("Halicarnassus");
        sim.Territory = 138;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 12.7f;
        sim.ApproachBlockedAt = 10.2f;
        var result = await service.TravelAsync(new NativeTravelRequest("Kraken", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Contains("blocked at 10.2y, which is within the aetheryte's reach", result.Diagnostic);
        Assert.Equal([NativeTravelActionKind.TeleportToCity, NativeTravelActionKind.ApproachAetheryte, NativeTravelActionKind.StopApproach, NativeTravelActionKind.InteractWithAetheryte], sim.Executed.Select(a => a.Kind).Take(4));
        Assert.False(sim.Walking);
    }

    [Fact]
    public async Task Landing_within_reach_uses_the_aetheryte_without_walking()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 138;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 10.5f;
        Assert.Equal(NativeTravelOutcome.Arrived, (await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default)).Outcome);
        Assert.Equal(0, sim.Approaches);
    }

    [Fact]
    public async Task Blocked_out_of_reach_still_fails_cleanly()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 138;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 14f;
        sim.ApproachBlockedAt = 11.5f;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.NotReady, result.Outcome);
        Assert.Contains("stopped getting closer", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.InteractWithAetheryte));
        Assert.False(sim.Walking);
    }

    [Theory]
    [InlineData(10.9f, 0f, true)]
    [InlineData(11.0f, 0f, false)]   // horizontal reach is strictly under 11 y (Lifestream)
    [InlineData(5f, 13.9f, true)]    // 3D 14.8 y
    [InlineData(5f, 14.2f, false)]   // 3D 15.05 y: too far above or below
    public void Aetheryte_reach_matches_Lifestreams_thresholds(float horizontal, float height, bool expected) =>
        Assert.Equal(expected, NativeWorldVisitDriver.InReach(new AetheryteObservation(1, 8, horizontal, true, height)));

    [Fact]
    public async Task An_aetheryte_well_above_or_below_is_not_in_reach_even_when_horizontally_close()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;
        sim.AetheryteDistance = 4f;
        sim.AetheryteHeight = 20f;
        sim.ApproachBlocked = true;   // walking cannot change the height
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default);
        Assert.NotEqual(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.InteractWithAetheryte));
    }

    [Fact]
    public async Task A_teleport_cast_that_never_completes_fails_after_the_teleport_timeout_without_touching_anything()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 138;
        sim.AetheryteDistance = 100f;
        sim.TeleportCastInterrupted = true;
        var started = sim.Now;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.NotReady, result.Outcome);
        Assert.Contains("did not arrive within 60s", result.Detail);
        Assert.True(sim.Now - started >= NativeWorldVisitDriver.TeleportTimeout);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.TeleportToCity));
        Assert.Equal(0, sim.Approaches);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.InteractWithAetheryte));
    }

    [Fact]
    public async Task Standing_in_the_destination_city_but_out_of_reach_walks_instead_of_teleporting()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;
        sim.AetheryteDistance = 20f;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.TeleportToCity));
        Assert.Equal(1, sim.Approaches);
    }

    [Fact]
    public async Task A_blocked_walk_stops_moving_and_fails_without_interacting()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;
        sim.AetheryteDistance = 15f;
        sim.ApproachBlocked = true;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.NotReady, result.Outcome);
        Assert.Contains("stopped getting closer", result.Detail);
        Assert.Equal(NativeTravelActionKind.StopApproach, sim.Executed[^1].Kind);
        Assert.False(sim.Walking);
        Assert.False(sim.WalkingStoppedByEndOperation);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.InteractWithAetheryte));
    }

    [Fact]
    public async Task An_aetheryte_beyond_the_walk_limit_is_never_walked_to()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 1186;
        sim.AetheryteDistance = 100f;
        sim.TeleportArrivalDistance = 40f;   // even after the teleport, too far to walk
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.LimsaLominsa]), default);
        Assert.Equal(NativeTravelOutcome.NotReady, result.Outcome);
        Assert.Equal(0, sim.Approaches);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.InteractWithAetheryte));
    }

    [Fact]
    public async Task Cancelling_while_walking_stops_the_character()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;
        sim.AetheryteDistance = 25f;
        using var cts = new CancellationTokenSource();
        sim.DelayHook = token =>
        {
            if (sim.Walking) cts.Cancel();
            token.ThrowIfCancellationRequested();
            sim.Now += NativeTravelService.PollInterval;
            return Task.CompletedTask;
        };
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.LimsaLominsa]), cts.Token);
        Assert.Equal(NativeTravelOutcome.Cancelled, result.Outcome);
        Assert.Equal(NativeTravelActionKind.StopApproach, sim.Executed[^1].Kind);
        Assert.False(sim.Walking);
        Assert.False(sim.WalkingStoppedByEndOperation);
    }

    [Fact]
    public async Task Already_at_a_World_Visit_aetheryte_it_uses_that_one_without_teleporting()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = WorldVisitCities.LimsaLominsa.TerritoryId;
        sim.AetheryteDistance = 4f;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, [WorldVisitCities.Uldah]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.TeleportToCity));
        Assert.Equal(NativeTravelActionKind.InteractWithAetheryte, sim.Executed[0].Kind);
    }

    [Fact]
    public async Task Without_a_World_Visit_city_on_the_list_it_falls_back_to_Limsa_Lominsa()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 1186;
        sim.AetheryteDistance = 100f;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.ShoutRunner, []), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(WorldVisitCities.LimsaLominsa.AetheryteId, sim.Executed[0].Id);
    }

    [Fact]
    public void Route_cities_are_the_World_Visit_cities_on_the_list_in_order_with_tolerant_names()
    {
        var cities = WorldVisitCities.FromRouteDestinations(["The Gold Saucer", " new gridania ", "Ul'dah – Steps of Nald", "New Gridania", null, "Limsa Lominsa  Lower Decks"]);
        Assert.Equal([WorldVisitCities.Gridania, WorldVisitCities.Uldah, WorldVisitCities.LimsaLominsa], cities);
        Assert.Empty(WorldVisitCities.FromRouteDestinations(["Mist", "The Goblet"]));
    }

    [Fact]
    public async Task Away_from_a_World_Visit_aetheryte_it_teleports_to_the_preferred_city_first()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Territory = 1186;           // not a World Visit city
        sim.AetheryteDistance = 100f;
        var result = await service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, [WorldVisitCities.Gridania]), default);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(NativeTravelActionKind.TeleportToCity, sim.Executed[0].Kind);
        Assert.Equal(WorldVisitCities.Gridania.AetheryteId, sim.Executed[0].Id);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.TeleportToCity));
    }

    [Fact]
    public void Rows_lagging_behind_the_dropdown_are_never_used()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.StaleRowSnapshotsAfterDcChange = 5;   // the Dynamis rows stay on screen for 5 reads after choosing Primal
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.Equal("Behemoth", sim.Current.Name);
    }

    // ----- verification before confirming -----

    [Fact]
    public void A_misclick_that_produces_Travel_to_Jenova_is_never_confirmed()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.SelectWorldRowOffset = 3;   // Cactuar's row → Jenova's (the reported incident's shape)
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.SelectionMismatch, result.Outcome);
        Assert.Contains("Travel to Jenova?", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.False(sim.ConfirmOpen || sim.WindowOpen);   // closed, nothing left open
        Assert.Equal("Adamantoise", sim.Current.Name);
    }

    [Fact]
    public void A_correct_prompt_with_a_different_game_destination_id_is_never_confirmed()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.BeforeExecute = () => { if (sim.ConfirmOpen) sim.AgentDestination = sim.Catalog["Jenova"].Id; };
        sim.PromptOverride = "Travel to Cactuar?";
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.SelectionMismatch, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    [Fact]
    public void A_prompt_that_changes_between_the_decision_and_the_click_is_never_confirmed()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.BeforeExecute = () => { if (sim.ConfirmOpen) sim.PromptOverride = "Travel to Jenova?"; };
        var result = Travel(service, "Cactuar");
        Assert.Contains(sim.Refused, a => a.Kind == NativeTravelActionKind.ConfirmTravel);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Equal(NativeTravelOutcome.SelectionMismatch, result.Outcome);
        Assert.Equal("Adamantoise", sim.Current.Name);
    }

    [Fact]
    public void Confirmation_requires_the_exact_template_not_a_substring()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.PromptOverride = "Travel to Cactuar? Or maybe Jenova?";
        Assert.Equal(NativeTravelOutcome.SelectionMismatch, Travel(service, "Cactuar").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    [Fact]
    public void An_action_is_skipped_when_the_game_changed_between_decision_and_action()
    {
        var (service, sim) = Rig("Adamantoise");
        var flipped = false;
        sim.BeforeExecute = () => { if (sim.WindowOpen && !flipped) { flipped = true; sim.HiddenWorlds.Add("Cactuar"); } };
        var result = Travel(service, "Cactuar");
        Assert.Contains(sim.Refused, a => a.Kind == NativeTravelActionKind.SelectWorld);
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    // ----- unexpected UI: stop safely, click nothing speculative -----

    [Fact]
    public void A_dropdown_whose_click_event_is_not_owned_by_it_is_never_clicked()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DcEventUnverified = true;
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void A_dropdown_with_labels_that_are_not_this_regions_Data_Centers_is_never_clicked()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DcLabelsCorrupt = true;
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, Travel(service, "Behemoth").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    // ----- live QA 2026-11-13 (Diabolos [Crystal], home Halicarnassus [Dynamis]) -----

    [Fact]
    public void Live_layout_same_Data_Center_travel_never_touches_the_dropdown()
    {
        // As observed: the dropdown reports index -1 and its item click is not owned by it; the rows show Crystal.
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.DropdownReportsNoSelection = true;
        sim.DcEventUnverified = true;
        var result = Travel(service, "Goblin");
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal("Goblin", sim.Current.Name);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(1, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Empty(sim.UiCaptures);
    }

    [Fact]
    public void Live_layout_cross_Data_Center_travel_stops_without_touching_the_unverified_dropdown()
    {
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.DropdownReportsNoSelection = true;
        sim.DcEventUnverified = true;
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("arrow buttons are not wired", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.Single(sim.UiCaptures);
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void Aether_to_Primal_steps_the_arrows_forward_one_Data_Center_at_a_time()
    {
        var (service, sim) = Rig("Adamantoise");
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal([1, 2, 3], sim.Executed.Where(a => a.Kind == NativeTravelActionKind.SelectDataCenter).Select(a => a.Index));
        Assert.Contains(result.Diagnostic.Split('\n'), l => l.Contains("▷ from Aether to Crystal"));
        Assert.Equal(1, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    [Fact]
    public void Primal_to_Aether_steps_the_arrows_back_one_Data_Center_at_a_time()
    {
        var (service, sim) = Rig("Behemoth");
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Adamantoise").Outcome);
        Assert.Equal([2, 1, 0], sim.Executed.Where(a => a.Kind == NativeTravelActionKind.SelectDataCenter).Select(a => a.Index));
    }

    [Fact]
    public void A_selector_whose_shown_Data_Center_cannot_be_read_is_never_stepped()
    {
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.DropdownReportsNoSelection = true;   // arrows verified, but the displayed text does not name a Data Center
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("does not show which Data Center is selected", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void An_arrow_step_is_refused_when_the_page_changed_between_decision_and_press()
    {
        var (service, sim) = Rig("Cuchulainn");
        var flipped = false;
        sim.BeforeExecute = () => { if (sim.WindowOpen && !flipped) { flipped = true; sim.SelectedDc = "Primal"; } };
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Contains(sim.Refused, a => a.Kind == NativeTravelActionKind.SelectDataCenter);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void An_arrow_step_that_would_now_lead_away_from_the_target_is_refused()
    {
        // Decided on Dynamis (step ◁ to Crystal toward Aether); the page is Aether by the time of the press: ▷ would go away.
        var (service, sim) = Rig("Cuchulainn");
        var flipped = false;
        sim.BeforeExecute = () => { if (sim.WindowOpen && !flipped) { flipped = true; sim.SelectedDc = "Aether"; } };
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Adamantoise").Outcome);
        Assert.Contains(sim.Refused, a => a.Kind == NativeTravelActionKind.SelectDataCenter);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void A_selector_that_never_converges_is_stopped_after_the_step_limit_and_no_World_is_clicked()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.ArrowsReversed = true;
        var result = Travel(service, "Balmung");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("within 4 steps", result.Detail);
        Assert.Equal(NativeWorldVisitDriver.MaxDataCenterSteps, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void A_Data_Center_section_that_appears_a_moment_after_the_window_opens_is_waited_for()
    {
        // Live attempt #2 ended "Unsupported … no Data Center selector" 0.0s after the window opened.
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.SectionHiddenSnapshots = 3;
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void A_Data_Center_section_that_never_appears_is_Unsupported_only_after_the_settle_time_and_nothing_is_clicked()
    {
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.SectionHiddenSnapshots = int.MaxValue;
        var started = sim.Now;
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.Unsupported, result.Outcome);
        Assert.True(sim.Now - started >= NativeWorldVisitDriver.ListSettleTimeout);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void A_legacy_single_Data_Center_window_visits_a_World_on_the_same_Data_Center()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.LegacyWindow = true;
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Cactuar").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void A_legacy_window_cannot_reach_another_Data_Center()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.LegacyWindow = true;
        Assert.Equal(NativeTravelOutcome.Unsupported, Travel(service, "Behemoth").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void Stale_rows_with_no_dropdown_selection_never_lead_to_a_World_click()
    {
        // Rows lag the page forever and the dropdown reports nothing: the target page is never "shown", so no World is clicked.
        var (service, sim) = Rig("Diabolos", home: "Halicarnassus");
        sim.DropdownReportsNoSelection = true;
        sim.StaleRowsForever = true;
        var result = Travel(service, "Adamantoise");
        Assert.NotEqual(NativeTravelOutcome.Arrived, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    // ----- a World whose click opens no confirmation (expected for congested Worlds; owner, 2026-11-13) -----

    [Fact]
    public void A_World_whose_click_opens_no_confirmation_is_unavailable_with_a_UI_capture_and_nothing_confirmed()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.UnselectableWorlds.Add("Cactuar");
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, result.Outcome);
        Assert.Contains("opened no confirmation", result.Detail);
        Assert.Single(sim.UiCaptures);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.False(sim.WindowOpen || sim.ConfirmOpen || sim.MenuOpen);
        Assert.Equal("Adamantoise", sim.Current.Name);
    }

    [Fact]
    public void Two_unaccepted_World_selections_in_a_row_stop_travel_as_unexpected_UI()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.UnselectableWorlds.UnionWith(["Cactuar", "Faerie"]);
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, Travel(service, "Cactuar").Outcome);
        var second = Travel(service, "Faerie");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, second.Outcome);
        Assert.Contains("2 World selections in a row opened no confirmation", second.Detail);
    }

    [Fact]
    public void A_confirmed_travel_in_between_resets_the_unaccepted_selection_count()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.UnselectableWorlds.UnionWith(["Cactuar", "Faerie"]);
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, Travel(service, "Cactuar").Outcome);
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Gilgamesh").Outcome);
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, Travel(service, "Faerie").Outcome);
    }

    [Fact]
    public void An_unexpected_UI_stop_captures_the_window_read_only_before_closing_it()
    {
        var (service, sim) = Rig("Diabolos");
        sim.DcEventUnverified = true;
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Equal([true], sim.UiCaptures);                 // exactly one capture, taken while the window was still open
        Assert.Contains("Travel UI at the moment it stopped", result.Diagnostic);
        Assert.Contains("SIMULATED UI: window open", result.Diagnostic);
        Assert.Equal(NativeTravelActionKind.CloseTravelUi, sim.Executed[^1].Kind);
        Assert.False(sim.WindowOpen);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void A_selection_mismatch_also_captures_the_UI_and_still_never_confirms()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.PromptOverride = "Travel to Jenova?";
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.SelectionMismatch, result.Outcome);
        Assert.Single(sim.UiCaptures);
        Assert.Contains("SIMULATED UI", result.Diagnostic);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    [Fact]
    public void Successful_travel_and_ordinary_skips_take_no_UI_capture()
    {
        var (service, sim) = Rig("Adamantoise");
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Cactuar").Outcome);
        sim.DisabledWorlds.Add("Faerie");
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, Travel(service, "Faerie").Outcome);
        Assert.Empty(sim.UiCaptures);
    }

    [Fact]
    public void A_failing_UI_capture_never_changes_the_outcome_or_skips_the_close()
    {
        var (service, sim) = Rig("Diabolos");
        sim.DcEventUnverified = true;
        sim.UiCaptureThrows = true;
        var result = Travel(service, "Adamantoise");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("UI capture failed: addon vanished", result.Diagnostic);
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void A_dropdown_with_distinct_labels_including_another_regions_Data_Center_is_never_clicked()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DcLabelsOverride = ["Aether", "Crystal", "Light", "Primal"];   // no duplicates: only the region check can catch it
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("Light", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void A_World_list_whose_click_event_is_not_owned_by_the_window_is_never_clicked()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.ListEventUnverified = true;
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, Travel(service, "Cactuar").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void A_dropdown_that_ignores_the_selection_is_tried_a_bounded_number_of_times()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DropdownIgnoresSelection = true;
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Equal(NativeWorldVisitDriver.MaxDataCenterSelectAttempts, sim.Count(NativeTravelActionKind.SelectDataCenter));
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void A_World_list_that_never_refreshes_to_the_target_DC_is_never_clicked()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.StaleRowsForever = true;
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, result.Outcome);
        Assert.Contains("does not belong to Primal", result.Detail);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void A_World_Visit_window_that_never_opens_ends_as_unexpected_UI()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.WindowNeverOpens = true;
        Assert.Equal(NativeTravelOutcome.UnexpectedUi, Travel(service, "Cactuar").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
    }

    [Fact]
    public void A_menu_left_open_by_someone_else_is_closed_once_and_never_driven()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.MenuOpen = true;
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelActionKind.CloseTravelUi, sim.Executed[0].Kind);
        Assert.Equal(NativeTravelOutcome.Arrived, result.Outcome);
    }

    // ----- availability and capability -----

    [Fact]
    public void A_disabled_congested_World_row_is_unavailable_and_not_clicked()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.DisabledWorlds.Add("Cactuar");
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.False(sim.WindowOpen);
    }

    [Fact]
    public void A_World_missing_from_its_page_is_unavailable()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.HiddenWorlds.Add("Cactuar");
        Assert.Equal(NativeTravelOutcome.WorldUnavailable, Travel(service, "Cactuar").Outcome);
    }

    [Fact]
    public void A_disabled_Data_Center_is_unavailable_as_a_whole()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.DisabledDcs.Add("Primal");
        Assert.Equal(NativeTravelOutcome.DataCenterUnavailable, Travel(service, "Behemoth").Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.SelectDataCenter));
    }

    [Fact]
    public void With_Data_Center_selection_switched_off_another_DC_stops_before_any_interaction()
    {
        var (service, sim) = Rig("Cuchulainn", allowDc: false);
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.Unsupported, result.Outcome);
        Assert.Contains("switched off in Native Travel QA", result.Detail);
        Assert.Empty(sim.Executed);
    }

    [Fact]
    public void With_Data_Center_selection_switched_off_same_DC_travel_still_works()
    {
        var (service, sim) = Rig("Adamantoise", allowDc: false);
        Assert.Equal(NativeTravelOutcome.Arrived, Travel(service, "Cactuar").Outcome);
    }

    [Fact]
    public void Another_region_is_unsupported_and_nothing_is_attempted()
    {
        var (service, sim) = Rig("Ravana", home: "Ravana");
        Assert.Equal(NativeTravelOutcome.Unsupported, Travel(service, "Adamantoise").Outcome);
        Assert.Empty(sim.Executed);
    }

    [Fact]
    public void A_character_visiting_another_region_cannot_World_Visit_back()
    {
        var (service, sim) = Rig("Ravana", home: "Cuchulainn");
        Assert.Equal(NativeTravelOutcome.Unsupported, Travel(service, "Adamantoise").Outcome);
        Assert.Empty(sim.Executed);
    }

    [Fact]
    public void An_unknown_World_is_unsupported()
    {
        var (service, _) = Rig("Adamantoise");
        Assert.Equal(NativeTravelOutcome.Unsupported, Travel(service, "Atlantis").Outcome);
    }

    // ----- character not ready -----

    [Fact]
    public void In_combat_the_engine_refuses_before_any_interaction()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.InCombat = true;
        Assert.Equal(NativeTravelOutcome.NotReady, Travel(service, "Cactuar").Outcome);
        Assert.Empty(sim.Executed);
    }

    [Fact]
    public void Mounted_at_the_aetheryte_the_engine_refuses_before_any_interaction()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.Mounted = true;
        Assert.Equal(NativeTravelOutcome.NotReady, Travel(service, "Cactuar").Outcome);
        Assert.Empty(sim.Executed);
    }

    [Fact]
    public void Logged_out_the_engine_waits_a_bounded_time_and_reports_not_ready()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.LoggedOut = true;
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.NotReady, result.Outcome);
        Assert.Empty(sim.Executed);
        Assert.True(sim.Now - DateTimeOffset.UnixEpoch <= NativeWorldVisitDriver.StepTimeout + TimeSpan.FromSeconds(1));
    }

    // ----- after confirmation -----

    [Fact]
    public void Wrong_World_arrival_is_a_wrong_destination()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.ArriveAt = sim.Catalog["Jenova"];
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.WrongDestination, result.Outcome);
        Assert.StartsWith("Arrived at Jenova instead of Cactuar", result.Detail);
    }

    [Fact]
    public void Wrong_Data_Center_arrival_is_a_wrong_destination()
    {
        var (service, sim) = Rig("Cuchulainn");
        sim.ArriveAt = sim.Catalog["Behemoth"] with { LogicalDataCenter = "Crystal", LogicalDataCenterId = 8 };   // right name, wrong DC
        var result = Travel(service, "Behemoth");
        Assert.Equal(NativeTravelOutcome.WrongDestination, result.Outcome);
    }

    [Fact]
    public void Two_congestion_messages_end_the_operation_as_congested()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.CongestionMessagesAfterConfirm = 2;
        sim.NeverDeparts = true;
        Assert.Equal(NativeTravelOutcome.Congested, Travel(service, "Cactuar").Outcome);
    }

    [Fact]
    public void A_confirmed_travel_that_never_starts_is_reported_not_started()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.NeverDeparts = true;
        var result = Travel(service, "Cactuar");
        Assert.Equal(NativeTravelOutcome.NotStarted, result.Outcome);
        Assert.Equal(1, sim.Count(NativeTravelActionKind.ConfirmTravel));
    }

    [Fact]
    public void A_transfer_that_never_arrives_times_out()
    {
        var (service, sim) = Rig("Adamantoise");
        sim.NeverArrives = true;
        Assert.Equal(NativeTravelOutcome.TimedOut, Travel(service, "Cactuar").Outcome);
    }

    [Fact]
    public void Exactly_one_World_selection_and_one_confirmation_per_operation()
    {
        var (service, sim) = Rig("Cuchulainn");
        Travel(service, "Behemoth");
        Assert.Equal(1, sim.Count(NativeTravelActionKind.SelectWorld));
        Assert.Equal(1, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Equal(1, sim.Count(NativeTravelActionKind.SelectMenuEntry));
    }

    // ----- ownership, cancellation -----

    [Fact]
    public async Task A_second_concurrent_operation_is_refused_as_busy()
    {
        var (service, sim) = Rig("Adamantoise");
        var release = new TaskCompletionSource();
        sim.DelayHook = token => release.Task.WaitAsync(token);
        using var cts = new CancellationTokenSource();
        try
        {
            var first = service.TravelAsync(new NativeTravelRequest("Cactuar", NativeTravelOwner.QaTest, []), cts.Token);
            Assert.True(service.IsBusy);
            // Bounded: if a second operation were (wrongly) admitted it would block on the same gate; fail, never hang.
            var second = await service.TravelAsync(new NativeTravelRequest("Faerie", NativeTravelOwner.ShoutRunner, []), cts.Token).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeTravelOutcome.Busy, second.Outcome);
            Assert.Equal(NativeTravelOwner.QaTest, service.Status.Owner);
            service.CancelActive("test over");
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeTravelOutcome.Cancelled, firstResult.Outcome);
            Assert.False(service.IsBusy);
        }
        finally
        {
            cts.Cancel();   // never leave an operation parked on the gate
        }
    }

    [Fact]
    public void Cancel_before_confirmation_closes_the_menus_and_never_confirms()
    {
        var (service, sim) = Rig("Cuchulainn");
        using var cts = new CancellationTokenSource();
        sim.BeforeExecute = () => { if (sim.WindowOpen) cts.Cancel(); };
        var result = Travel(service, "Behemoth", token: cts.Token);
        Assert.Equal(NativeTravelOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, sim.Count(NativeTravelActionKind.ConfirmTravel));
        Assert.Equal(NativeTravelActionKind.CloseTravelUi, sim.Executed[^1].Kind);
        Assert.False(sim.WindowOpen || sim.MenuOpen || sim.ConfirmOpen);
    }

    [Fact]
    public void Cancel_during_the_transition_touches_nothing_and_says_the_game_may_still_complete_it()
    {
        var (service, sim) = Rig("Adamantoise");
        using var cts = new CancellationTokenSource();
        var confirmed = false;
        sim.BeforeExecute = () => { };
        sim.DelayHook = token =>
        {
            if (sim.Executed.Any(a => a.Kind == NativeTravelActionKind.ConfirmTravel)) { if (confirmed) cts.Cancel(); confirmed = true; }
            token.ThrowIfCancellationRequested();
            sim.Now += NativeTravelService.PollInterval;
            return Task.CompletedTask;
        };
        var result = Travel(service, "Cactuar", token: cts.Token);
        Assert.Equal(NativeTravelOutcome.Cancelled, result.Outcome);
        Assert.Contains("may still complete", result.Detail);
        Assert.Equal(NativeTravelActionKind.ConfirmTravel, sim.Executed[^1].Kind);   // nothing after the confirmation
    }

    [Fact]
    public void Operations_get_distinct_ids_and_the_service_is_idle_afterwards()
    {
        var (service, sim) = Rig("Adamantoise");
        var a = Travel(service, "Cactuar");
        var b = Travel(service, "Faerie");
        Assert.NotEqual(a.OperationId, b.OperationId);
        Assert.False(service.IsBusy);
        Assert.Equal(NativeTravelStatus.Idle, service.Status);
        Assert.Equal(b.OperationId, service.LastResult!.OperationId);
    }

    [Fact]
    public void The_flow_has_no_logout_character_select_or_home_return_step_at_all()
    {
        var kinds = Enum.GetNames<NativeTravelActionKind>();
        Assert.DoesNotContain(kinds, k => k.Contains("Logout", StringComparison.OrdinalIgnoreCase) || k.Contains("Character", StringComparison.OrdinalIgnoreCase) || k.Contains("Home", StringComparison.OrdinalIgnoreCase));
    }
}
