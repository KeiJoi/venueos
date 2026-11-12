using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Services.Tests;

/// <summary>Patch 7.58 travel rules for ShoutRunner. History: 0.3.13 added these tests for "skip other Data Centers"
/// (Lifestream could not do in-game Data Center travel); 0.3.14 replaces that with native World Visit across every
/// logical Data Center of the region, so the expectations below changed by owner requirement. The safety assertions
/// (no shout on a wrong World or Data Center, verified Data Center, bounded wrong-arrival skips, no repeats on Resume)
/// are kept. Classification uses the REAL <see cref="ShoutRunnerTravelClassification"/> over a catalog whose logical Data
/// Centers match the installed 7.58 game data (<see cref="TestTravelWorldCatalog"/>).</summary>
public sealed partial class ShoutRunnerLifestreamCompatibilityTests
{
    private static readonly TestTravelWorldCatalog Worlds = new();

    /// <summary>The fake engine's classification: the real rule, with an NA Home World (Cuchulainn) by default.</summary>
    private static ShoutRunnerCrossDataCenterCheck Patch758Classify(string? currentWorld, string targetWorld, string home = "Cuchulainn") =>
        ShoutRunnerTravelClassification.Classify(currentWorld is null ? null : Worlds.Resolve(currentWorld), Worlds.Resolve(home), Worlds.Resolve(targetWorld), ShoutRunnerCatalog.DataCenterFor(targetWorld));

    // =====================================================================================================
    // Capability rule (pure)
    // =====================================================================================================

    [Fact]
    public void Same_logical_DC_is_World_Visit()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.SameDataCenter, Patch758Classify("Adamantoise", "Cactuar"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.SameDataCenter, Patch758Classify("Balmung", "Zalera"));
    }

    [Theory]
    [InlineData("Adamantoise", "Behemoth")]   // Aether → Primal
    [InlineData("Siren", "Balmung")]          // Aether → Crystal
    [InlineData("Ultros", "Cuchulainn")]      // Primal → Dynamis
    [InlineData("Seraph", "Adamantoise")]     // Dynamis → Aether
    public void Another_logical_DC_of_the_region_is_native_cross_DC_World_Visit(string from, string to) =>
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossDataCenter, Patch758Classify(from, to));

    [Fact]
    public void Logical_DC_comes_from_the_real_logical_column_not_the_merged_DataCenter_column()
    {
        // In the installed 7.58 game data every NA World has DataCenter = 4 (Aether); only World.Unknown0 tells them apart.
        // A World resolved with the merged value would look like the same DC; the real logical DC must decide.
        var current = Worlds["Adamantoise"];
        var mergedLookingTarget = Worlds["Behemoth"] with { LogicalDataCenterId = 4, LogicalDataCenter = "Aether" };
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(current, current, mergedLookingTarget, "Primal"));   // catalog disagrees → never guessed
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossDataCenter, ShoutRunnerTravelClassification.Classify(current, current, Worlds["Behemoth"], "Primal"));
    }

    [Fact]
    public void Another_region_or_being_outside_the_home_region_is_unsupported()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossRegionUnsupported, Patch758Classify("Ravana", "Adamantoise"));                  // visiting Oceania, NA home
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossRegionUnsupported, Patch758Classify("Ravana", "Adamantoise", home: "Ravana"));  // OC character → NA
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossRegionUnsupported, Patch758Classify("Adamantoise", "Ravana"));
    }

    [Fact]
    public void Missing_data_or_a_stale_catalog_is_unknown()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(null, Worlds["Siren"], Worlds["Cactuar"], "Aether"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(Worlds["Siren"], null, Worlds["Cactuar"], "Aether"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(Worlds["Siren"], Worlds["Siren"], null, "Aether"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(Worlds["Siren"], Worlds["Siren"], Worlds["Cactuar"], "Primal"));
    }

    [Fact]
    public void Catalog_keeps_the_four_North_American_Data_Centers_distinct_and_matches_game_data_membership()
    {
        Assert.Equal(["Aether", "Crystal", "Dynamis", "Primal"], ShoutRunnerCatalog.CanonicalDataCenterOrder);
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
        {
            var worlds = ShoutRunnerCatalog.WorldsIn(dc);
            Assert.Equal(8, worlds.Count);
            Assert.All(worlds, w => Assert.Equal(dc, ShoutRunnerCatalog.DataCenterFor(w)));
        }

        // Membership as read from the installed 7.58 World sheet (World.Unknown0) — spot checks from the owner's screenshots.
        Assert.Equal(["Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros"], ShoutRunnerCatalog.WorldsIn("Primal"));
        Assert.Equal(["Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"], ShoutRunnerCatalog.WorldsIn("Dynamis"));
    }

    [Fact]
    public void Native_travel_results_map_to_the_right_route_outcome()
    {
        static ShoutRunnerTransferResult Map(NativeTravelOutcome o) =>
            ShoutRunnerNativeTravelMapping.ToTransferOutcome(new NativeTravelResult(1, o, "detail", null, null, null, "diag")).Result;
        Assert.Equal(ShoutRunnerTransferResult.Success, Map(NativeTravelOutcome.Arrived));
        Assert.Equal(ShoutRunnerTransferResult.Success, Map(NativeTravelOutcome.AlreadyThere));
        Assert.Equal(ShoutRunnerTransferResult.WorldCongestedSkip, Map(NativeTravelOutcome.Congested));
        Assert.Equal(ShoutRunnerTransferResult.WorldCongestedSkip, Map(NativeTravelOutcome.WorldUnavailable));
        Assert.Equal(ShoutRunnerTransferResult.DataCenterUnavailableSkip, Map(NativeTravelOutcome.DataCenterUnavailable));
        Assert.Equal(ShoutRunnerTransferResult.DataCenterUnavailableSkip, Map(NativeTravelOutcome.Unsupported));
        Assert.Equal(ShoutRunnerTransferResult.WrongDestination, Map(NativeTravelOutcome.WrongDestination));
        Assert.Equal(ShoutRunnerTransferResult.WrongDestination, Map(NativeTravelOutcome.NotStarted));
        foreach (var o in new[] { NativeTravelOutcome.SelectionMismatch, NativeTravelOutcome.UnexpectedUi, NativeTravelOutcome.TimedOut, NativeTravelOutcome.NotReady, NativeTravelOutcome.Busy, NativeTravelOutcome.Cancelled, NativeTravelOutcome.Failed })
            Assert.Equal(ShoutRunnerTransferResult.Failed, Map(o));
    }

    // =====================================================================================================
    // Data Center verification (pure; unchanged from 0.3.13)
    // =====================================================================================================

    [Fact]
    public void Arrival_requires_the_Data_Center_to_match_too()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise", "Aether");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (30.0, S("Cactuar", busy: false) with { CurrentDataCenter = "Aether" }),
            (31.0, S("Cactuar", busy: false) with { CurrentDataCenter = "Aether" }));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Arrived, verdicts[^1]);
        Assert.Contains("Adamantoise → Cactuar [Aether]", v.DescribeHistory());
    }

    [Fact]
    public void Right_World_name_on_the_wrong_Data_Center_is_a_wrong_destination()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise", "Aether");
        var verdicts = Feed(v,
            (0.5, S("Adamantoise", busy: true, transitioning: true)),
            (30.0, S("Cactuar", busy: false) with { CurrentDataCenter = "Primal" }),
            (33.0, S("Cactuar", busy: false) with { CurrentDataCenter = "Primal" }));
        Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.WrongWorld, verdicts[^1]);
        Assert.Contains("on Data Center Primal instead of Aether", v.DescribeWrongWorld("Cactuar"));
    }

    [Fact]
    public void An_unknown_Data_Center_is_never_accepted_as_arrival()
    {
        var v = new ShoutRunnerArrivalVerifier("Cactuar", "Adamantoise", "Aether");
        var verdicts = Feed(v, (0.5, S("Adamantoise", busy: true, transitioning: true)), (30, S("Cactuar", busy: false)), (60, S("Cactuar", busy: false)));
        Assert.All(verdicts, x => Assert.Equal(ShoutRunnerArrivalVerifier.Verdict.Continue, x));
    }

    // =====================================================================================================
    // Service scenarios (fake engine)
    // =====================================================================================================

    private static Rig MultiDcRig(string startWorld, params string[] dataCenters)
    {
        var rig = new Rig(repeat: false);
        rig.Service.SetDataCenterSelected("Aether", false);
        foreach (var dc in dataCenters) rig.Service.SetDataCenterSelected(dc, true);
        rig.Automation.CurrentWorld = startWorld;
        return rig;
    }

    [Fact]
    public void Full_route_across_all_four_NA_Data_Centers_visits_every_World_once_in_canonical_order()
    {
        var rig = MultiDcRig("Siren", "Aether", "Primal", "Crystal", "Dynamis");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        var expected = ShoutRunnerCatalog.CanonicalDataCenterOrder.SelectMany(ShoutRunnerCatalog.WorldsIn).ToList();
        Assert.Equal(expected, rig.Automation.TravelRequests);
        Assert.Equal(32 * 3, rig.Sent.Count);
        Assert.Equal(expected, rig.Sent.Select(s => s.World).Distinct());
        // Cross-DC travel happens exactly at the Data Center boundaries (Aether→Crystal, Crystal→Dynamis, Dynamis→Primal).
        Assert.Equal(["Balmung", "Cuchulainn", "Behemoth"], rig.Automation.TravelMethods.Where(m => m.CrossDataCenter).Select(m => m.World));
        Assert.Contains("RUN 1 complete.", rig.Texts());
        Assert.DoesNotContain(rig.Texts(), t => t.Contains("SKIPPED"));
    }

    [Fact]
    public void Starting_on_Primal_goes_to_Aether_first_because_the_order_is_fixed_and_then_returns_to_Primal_last()
    {
        var rig = MultiDcRig("Ultros", "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Equal([.. ShoutRunnerCatalog.WorldsIn("Aether"), .. ShoutRunnerCatalog.WorldsIn("Primal")], rig.Automation.TravelRequests);
        Assert.Equal(("Adamantoise", true), rig.Automation.TravelMethods[0]);
        Assert.Equal(16 * 3, rig.Sent.Count);
    }

    [Fact]
    public void A_Data_Center_whose_travel_is_unavailable_is_skipped_and_the_next_one_still_runs()
    {
        var rig = MultiDcRig("Siren", "Aether", "Crystal", "Primal");
        rig.Automation.Travel = (target, _) => ShoutRunnerCatalog.DataCenterFor(target) == "Crystal"
            ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("Crystal is disabled in the Data Center selector", "diag"))
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Crystal — SKIPPED: data center unavailable. Crystal is disabled", StringComparison.Ordinal));
        Assert.Equal(1, rig.Automation.TravelRequests.Count(w => ShoutRunnerCatalog.DataCenterFor(w) == "Crystal"));   // one attempt, not 8
        Assert.Equal(8 * 3, rig.Sent.Count(s => ShoutRunnerCatalog.DataCenterFor(s.World!) == "Primal"));
    }

    [Fact]
    public void Cross_region_start_skips_NA_Data_Centers_without_any_travel_attempt()
    {
        var rig = MultiDcRig("Ravana", "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Empty(rig.Automation.TravelRequests);
        Assert.Empty(rig.Sent);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Aether — SKIPPED: this Data Center is in another region", StringComparison.Ordinal));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Primal — SKIPPED: this Data Center is in another region", StringComparison.Ordinal));
    }

    [Fact]
    public void One_congested_or_wrong_world_does_not_skip_its_Data_Center()
    {
        var rig = MultiDcRig("Siren", "Aether");
        rig.Automation.Travel = (target, _) => target switch
        {
            "Cactuar" => Task.FromResult(ShoutRunnerTransferOutcome.WorldCongested("Cactuar is listed but cannot be selected (congested or unavailable)")),
            "Gilgamesh" => SimulatedLifestreamTravel(rig.Automation, target, "Jenova"),
            _ => rig.Automation.ArriveAt(target),
        };
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        var visited = rig.Sent.Select(s => s.World).Distinct().ToList();
        Assert.Equal(["Adamantoise", "Faerie", "Jenova", "Midgardsormr", "Sargatanas", "Siren"], visited);
        Assert.Contains(rig.Texts(), t => t.StartsWith("Cactuar — SKIPPED: congested", StringComparison.Ordinal));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Gilgamesh — SKIPPED: Arrived at Jenova instead of Gilgamesh", StringComparison.Ordinal));
        Assert.Contains("Aether — completed with skips.", rig.Texts());
        Assert.Equal(1, rig.Automation.TravelRequests.Count(w => w == "Gilgamesh"));
    }

    [Fact]
    public void A_wrong_arrival_followed_by_a_success_resets_the_consecutive_count()
    {
        var rig = MultiDcRig("Siren", "Aether");
        rig.Automation.Travel = (target, _) => target is "Cactuar" or "Gilgamesh" or "Midgardsormr"
            ? SimulatedLifestreamTravel(rig.Automation, target, "Siren")
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        Assert.Equal(3, rig.Texts().Count(t => t.Contains("— SKIPPED:")));
        Assert.DoesNotContain(rig.Texts(), t => t.Contains("stopped —"));
    }

    [Fact]
    public void Two_consecutive_wrong_arrivals_fault_the_run_and_keep_the_checkpoint()
    {
        var rig = MultiDcRig("Siren", "Aether");
        rig.Automation.Travel = (target, _) => target is "Cactuar" or "Faerie"
            ? SimulatedLifestreamTravel(rig.Automation, target, "Jenova")
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);
        Assert.Equal(2, ShoutRunnerService.MaxConsecutiveWrongDestinations);
        Assert.Equal("Faerie", rig.Store.Current!.CurrentWorld);
        Assert.Equal(3, rig.Sent.Count);
        Assert.DoesNotContain(rig.Sent, s => s.World is "Jenova" or "Cactuar" or "Faerie");
    }

    [Fact]
    public void Pre_shout_guard_withholds_when_the_Data_Center_does_not_match()
    {
        var rig = MultiDcRig("Siren", "Aether");
        rig.Automation.CurrentDataCenterOverride = "Primal";
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Faulted);
        Assert.Empty(rig.Sent);
        Assert.Contains(rig.Texts(), t => t.Contains("Data Center (Primal) could not be confirmed as Aether"));
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("expected Adamantoise [Aether], observed Adamantoise [Primal]"));
    }

    [Fact]
    public void Stop_between_Data_Centers_issues_nothing_further()
    {
        var rig = MultiDcRig("Siren", "Aether", "Dynamis");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Texts().Contains("Aether — completed."));
        var requests = rig.Automation.TravelRequests.Count;
        var sent = rig.Sent.Count;
        rig.Service.Stop();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);
        for (var i = 0; i < 20; i++) rig.TickOnce();
        Assert.Equal(requests, rig.Automation.TravelRequests.Count);
        Assert.Equal(sent, rig.Sent.Count);
        Assert.Null(rig.Store.Current);
    }

    [Fact]
    public void Resume_across_a_Data_Center_boundary_continues_in_the_next_DC_without_repeats()
    {
        var rig = MultiDcRig("Siren", "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Store.Current!.CurrentDataCenter == "Primal" && rig.Store.Current.CompletedDestinationsInCurrentWorld.Count == 1);
        Assert.Equal(8 * 3 + 1, rig.Sent.Count);

        var rig2 = rig.Restart();                       // crash mid-Behemoth
        rig2.Automation.CurrentWorld = "Behemoth";
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig2.Service.Resume());
        rig2.PumpUntil(() => rig2.Service.State is ShoutRunnerState.Stopped or ShoutRunnerState.WaitingRepeat);
        Assert.DoesNotContain(rig2.Automation.TravelRequests, w => ShoutRunnerCatalog.DataCenterFor(w) == "Aether");
        Assert.Equal(2, rig2.Sent.Count(s => s.World == "Behemoth"));
        Assert.Equal(8 * 3 - 1, rig2.Sent.Count);
    }

    [Fact]
    public void Resume_after_a_Data_Center_skip_never_retries_that_DC_or_repeats_announcements()
    {
        var rig = MultiDcRig("Siren", "Aether", "Crystal", "Dynamis");
        rig.Automation.Travel = (target, _) => ShoutRunnerCatalog.DataCenterFor(target) == "Crystal"
            ? Task.FromResult(ShoutRunnerTransferOutcome.DataCenterUnavailable("Crystal is disabled in the Data Center selector"))
            : rig.Automation.ArriveAt(target);
        rig.Service.Start();
        rig.PumpUntil(() => rig.Texts().Any(t => t.StartsWith("Crystal — SKIPPED", StringComparison.Ordinal)));
        Assert.Contains(rig.Store.Current!.SkippedDataCenters, s => s.DataCenter == "Crystal");
        Assert.Equal(24, rig.Sent.Count);

        var rig2 = rig.Restart();
        rig2.Automation.CurrentWorld = "Balmung";
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig2.Service.Resume());
        rig2.PumpUntil(() => rig2.Service.State is ShoutRunnerState.Stopped or ShoutRunnerState.WaitingRepeat);
        Assert.Contains(rig2.Texts(), t => t.StartsWith("Skipped Data Centers (recovered): Crystal", StringComparison.Ordinal));
        Assert.DoesNotContain(rig2.Automation.TravelRequests, w => ShoutRunnerCatalog.DataCenterFor(w) is "Crystal" or "Aether");
        Assert.Equal(8 * 3, rig2.Sent.Count);
        Assert.All(rig2.Sent, s => Assert.Equal("Dynamis", ShoutRunnerCatalog.DataCenterFor(s.World!)));
    }
}
