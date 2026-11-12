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
        Assert.Contains("awaiting live verification", result.Detail);
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
