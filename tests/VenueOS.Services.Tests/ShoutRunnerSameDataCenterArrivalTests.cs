using VenueOS.Modules.Operations.ShoutRunner;

namespace VenueOS.Services.Tests;

/// <summary>Regression tests for the live-verified same-Data-Center World Visit bug: a character on Halicarnassus
/// routed toward Cuchulainn (both Dynamis) was moved by Lifestream to a World-Visit-capable city (Ul'dah) first —
/// an ordinary intermediate step — and the old logic mistook that intermediate settle for the transfer's own
/// completion, faulting the RUN with "Arrived at Halicarnassus instead of Cuchulainn" before the actual World Visit
/// ever ran. See <see cref="ShoutRunnerSameDataCenterArrival"/>'s doc comment for the full analysis.</summary>
public sealed class ShoutRunnerSameDataCenterArrivalTests
{
    [Fact] public void The_exact_live_scenario_does_not_fault_while_lifestream_is_still_busy_at_the_intermediate_city()
    {
        // Halicarnassus -> Ul'dah (a transition happened, then settled) while Lifestream is still busy performing
        // the actual World Visit to Cuchulainn.
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(isLoggedIn: true, hasLocalPlayer: true, transitioning: false, seenTransition: true, currentWorld: "Halicarnassus", targetWorld: "Cuchulainn", lifestreamBusy: true);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, decision);
    }

    [Fact] public void An_unknown_lifestream_busy_state_never_trusts_a_wrong_world_result()
    {
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, false, true, "Halicarnassus", "Cuchulainn", lifestreamBusy: null);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, decision);
    }

    [Fact] public void Wrong_world_after_lifestream_confirms_it_is_finished_is_a_genuine_failure()
    {
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, false, true, "Halicarnassus", "Cuchulainn", lifestreamBusy: false);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Failed, decision);
    }

    [Fact] public void Reaching_the_target_world_while_lifestream_is_still_busy_does_not_yet_succeed()
    {
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, false, true, "Cuchulainn", "Cuchulainn", lifestreamBusy: true);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, decision);
    }

    [Fact] public void Reaching_the_target_world_once_lifestream_confirms_finished_succeeds()
    {
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, false, true, "Cuchulainn", "Cuchulainn", lifestreamBusy: false);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Success, decision);
    }

    [Fact] public void Still_mid_transition_never_decides_either_way()
    {
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, transitioning: true, seenTransition: true, "Cuchulainn", "Cuchulainn", lifestreamBusy: false);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, decision);
    }

    [Fact] public void No_transition_observed_yet_never_decides_either_way_even_at_the_wrong_world()
    {
        // Covers "current World mismatch BEFORE World Visit completion does not immediately fault" at its most
        // literal: nothing has even started moving yet.
        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(true, true, false, seenTransition: false, "Halicarnassus", "Cuchulainn", lifestreamBusy: false);
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, decision);
    }

    [Fact] public void Not_logged_in_or_no_local_player_never_decides_either_way()
    {
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, ShoutRunnerSameDataCenterArrival.Evaluate(false, true, false, true, "Cuchulainn", "Cuchulainn", false));
        Assert.Equal(ShoutRunnerSameDataCenterArrival.Decision.Continue, ShoutRunnerSameDataCenterArrival.Evaluate(true, false, false, true, "Cuchulainn", "Cuchulainn", false));
    }
}
