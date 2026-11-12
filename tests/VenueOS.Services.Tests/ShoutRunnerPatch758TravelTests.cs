using VenueOS.Modules.Operations.ShoutRunner;

namespace VenueOS.Services.Tests;

/// <summary>0.3.13 Patch 7.58 travel-migration tests (<c>docs/SHOUTRUNNER_PATCH_758_TRAVEL_MIGRATION.md</c>). These share
/// the harness of the 0.3.12 compatibility tests. The fake's reachability answers are computed exactly as current
/// Lifestream source defines them (<c>DataStore.BuildWorlds</c>: <c>CanVisitSameDC</c> = target in the character's
/// current logical DC; <c>CanVisitCrossDC</c> = target in another logical DC of the home region, or Oceania), and are
/// fed through the REAL <see cref="ShoutRunnerTravelClassification"/>. No Lifestream API is invented. Data Center and
/// region ids below are test-local values, not game row ids.</summary>
public sealed partial class ShoutRunnerLifestreamCompatibilityTests
{
    private const uint RegionNorthAmerica = 2, RegionOceania = 4;

    private static readonly Dictionary<string, (uint Id, uint Region)> TestDataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aether"] = (4, RegionNorthAmerica),
        ["Primal"] = (5, RegionNorthAmerica),
        ["Crystal"] = (8, RegionNorthAmerica),
        ["Dynamis"] = (11, RegionNorthAmerica),
        ["Materia"] = (9, RegionOceania),
    };

    private static readonly string[] MateriaWorlds = ["Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"];

    private static string? TestDataCenterOf(string? world) =>
        world is null ? null : MateriaWorlds.Contains(world, StringComparer.OrdinalIgnoreCase) ? "Materia" : ShoutRunnerCatalog.DataCenterFor(world);

    /// <summary>A World's placement as the game's World sheet would report it (test-local ids).</summary>
    internal static ShoutRunnerWorldPlacement? TestPlacement(string? world)
    {
        var dc = TestDataCenterOf(world);
        if (world is null || dc is null) return null;
        var (id, region) = TestDataCenters[dc];
        return new ShoutRunnerWorldPlacement(world, (uint)Math.Abs(world.GetHashCode() % 1000) + 1, dc, id, region);
    }

    /// <summary>Lifestream's two lists, exactly as its source builds them (home region: North America).</summary>
    private static (bool SameDc, bool CrossDc) LifestreamLists(ShoutRunnerWorldPlacement current, ShoutRunnerWorldPlacement target) =>
        (target.DataCenterId == current.DataCenterId,
         target.DataCenterId != current.DataCenterId && (target.RegionId == RegionNorthAmerica || target.RegionId == RegionOceania));

    /// <summary>The fake engine's classification: Lifestream's real list semantics through the real 7.58 classifier.</summary>
    private static ShoutRunnerCrossDataCenterCheck Patch758Classify(string? currentWorld, string targetWorld)
    {
        var current = TestPlacement(currentWorld);
        var target = TestPlacement(targetWorld);
        if (current is null || target is null) return ShoutRunnerTravelClassification.Classify(null, null, current, target, ShoutRunnerCatalog.DataCenterFor(targetWorld));
        var (sameDc, crossDc) = LifestreamLists(current, target);
        return ShoutRunnerTravelClassification.Classify(sameDc, crossDc, current, target, ShoutRunnerCatalog.DataCenterFor(targetWorld));
    }

    // =====================================================================================================
    // Capability rule (pure)
    // =====================================================================================================

    [Fact]
    public void Same_logical_DC_World_Visit_is_supported()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.SameDataCenter, Patch758Classify("Adamantoise", "Cactuar"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.SameDataCenter, Patch758Classify("Balmung", "Zalera"));
    }

    [Theory]
    [InlineData("Adamantoise", "Behemoth")]   // Aether → Primal
    [InlineData("Siren", "Balmung")]          // Aether → Crystal
    [InlineData("Ultros", "Cuchulainn")]      // Primal → Dynamis
    [InlineData("Seraph", "Adamantoise")]     // Dynamis → Aether (a "guest to guest" or "guest to home" move under the old rules)
    public void Another_logical_DC_in_the_same_region_is_the_7_58_in_game_travel_and_is_reported_unsupported(string from, string to)
    {
        // Lifestream answers CanVisitCrossDC = true here, which 0.3.12 turned into its logout-based Data Center Travel.
        var current = TestPlacement(from)!;
        var target = TestPlacement(to)!;
        Assert.Equal((false, true), LifestreamLists(current, target));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.InGameDataCenterTravelUnsupported, Patch758Classify(from, to));
        Assert.True(ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(current, target));
    }

    [Fact]
    public void Same_region_other_DC_is_unsupported_whatever_Lifestream_answers()
    {
        var current = TestPlacement("Adamantoise");
        var target = TestPlacement("Behemoth");
        foreach (bool? same in new bool?[] { true, false, null })
            foreach (bool? cross in new bool?[] { true, false, null })
                Assert.Equal(ShoutRunnerCrossDataCenterCheck.InGameDataCenterTravelUnsupported, ShoutRunnerTravelClassification.Classify(same, cross, current, target, "Primal"));
    }

    [Fact]
    public void Cross_region_travel_keeps_Data_Center_Travel_which_is_still_the_official_method()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.CrossDataCenter, Patch758Classify("Ravana", "Adamantoise"));
        Assert.False(ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(TestPlacement("Ravana")!, TestPlacement("Adamantoise")!));
        // ...but only when Lifestream confirms it can reach the World.
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(false, false, TestPlacement("Ravana"), TestPlacement("Adamantoise"), "Aether"));
    }

    [Fact]
    public void Same_DC_target_that_Lifestream_does_not_list_is_unknown_not_guessed() =>
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(false, false, TestPlacement("Adamantoise"), TestPlacement("Cactuar"), "Aether"));

    [Fact]
    public void Missing_placements_or_a_stale_catalog_are_unknown()
    {
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(true, false, null, TestPlacement("Cactuar"), "Aether"));
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(true, false, TestPlacement("Adamantoise"), null, "Aether"));
        // The game data files Cactuar under Aether; a catalog claiming Primal must not be trusted.
        Assert.Equal(ShoutRunnerCrossDataCenterCheck.Unknown, ShoutRunnerTravelClassification.Classify(true, false, TestPlacement("Adamantoise"), TestPlacement("Cactuar"), "Primal"));
    }

    [Fact]
    public void The_travel_time_guard_only_blocks_other_logical_DCs_of_the_same_region()
    {
        Assert.False(ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(TestPlacement("Adamantoise")!, TestPlacement("Cactuar")!));
        Assert.True(ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(TestPlacement("Adamantoise")!, TestPlacement("Behemoth")!));
        Assert.False(ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(TestPlacement("Adamantoise")!, TestPlacement("Ravana")!));
    }

    [Fact]
    public void Catalog_keeps_the_four_North_American_Data_Centers_distinct_with_their_eight_Worlds()
    {
        Assert.Equal(["Aether", "Crystal", "Dynamis", "Primal"], ShoutRunnerCatalog.CanonicalDataCenterOrder);
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
        {
            var worlds = ShoutRunnerCatalog.WorldsIn(dc);
            Assert.Equal(8, worlds.Count);
            Assert.All(worlds, w => Assert.Equal(dc, ShoutRunnerCatalog.DataCenterFor(w)));
        }
    }

    // =====================================================================================================
    // Data Center verification (pure)
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
    // Service scenarios
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
    public void Multi_DC_route_from_Aether_completes_Aether_and_skips_other_NA_DCs_without_any_logout_or_travel_command()
    {
        var rig = MultiDcRig("Siren", "Aether", "Crystal", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        Assert.Equal(ShoutRunnerCatalog.WorldsIn("Aether"), rig.Automation.TravelRequests);   // nothing outside Aether requested
        Assert.DoesNotContain(rig.Automation.TravelMethods, m => m.CrossDataCenter);          // no Data Center Travel (logout) at all
        Assert.Equal(8 * 3, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal("Aether", ShoutRunnerCatalog.DataCenterFor(s.World!)));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Crystal — SKIPPED: travel to another Data Center now happens in game (Patch 7.58", StringComparison.Ordinal));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Primal — SKIPPED: travel to another Data Center now happens in game (Patch 7.58", StringComparison.Ordinal));
        Assert.Contains("Aether — completed.", rig.Texts());
        Assert.Contains("RUN 1 complete.", rig.Texts());
        Assert.Contains(rig.Diagnostics.Capture().RecentErrors, e => e.Message.Contains("Crystal skipped at Balmung") && e.Message.Contains("no travel command was issued"));
    }

    [Fact]
    public void Starting_on_Primal_skips_Aether_and_runs_Primal_so_the_route_still_works_from_the_current_DC()
    {
        var rig = MultiDcRig("Ultros", "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        Assert.Equal(ShoutRunnerCatalog.WorldsIn("Primal"), rig.Automation.TravelRequests);
        Assert.Equal(8 * 3, rig.Sent.Count(s => ShoutRunnerCatalog.DataCenterFor(s.World!) == "Primal"));
        Assert.Contains(rig.Texts(), t => t.StartsWith("Aether — SKIPPED: travel to another Data Center", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Automation.TravelMethods, m => m.CrossDataCenter);
    }

    [Fact]
    public void Cross_region_start_uses_Data_Center_Travel_once_then_World_Visit_inside_the_DC()
    {
        var rig = MultiDcRig("Ravana", "Aether");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Service.State == ShoutRunnerState.Stopped);

        Assert.Equal(("Adamantoise", true), rig.Automation.TravelMethods[0]);
        Assert.All(rig.Automation.TravelMethods.Skip(1), m => Assert.False(m.CrossDataCenter));
        Assert.Equal(8 * 3, rig.Sent.Count);
    }

    [Fact]
    public void One_congested_or_wrong_world_does_not_skip_its_Data_Center()
    {
        var rig = MultiDcRig("Siren", "Aether");
        rig.Automation.Travel = (target, _) => target switch
        {
            "Cactuar" => Task.FromResult(ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.")),
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
        Assert.DoesNotContain(rig.Texts(), t => t.StartsWith("Aether — SKIPPED", StringComparison.Ordinal));
        Assert.Equal(1, rig.Automation.TravelRequests.Count(w => w == "Gilgamesh"));   // no retry loop
    }

    [Fact]
    public void After_a_skipped_world_the_next_world_gets_a_fresh_plan_and_its_own_checkpoint()
    {
        var rig = MultiDcRig("Siren", "Aether");
        var faerie = new TaskCompletionSource<ShoutRunnerTransferOutcome>();
        rig.Automation.Travel = (target, _) => target switch
        {
            "Cactuar" => SimulatedLifestreamTravel(rig.Automation, target, "Jenova"),
            "Faerie" => faerie.Task,
            _ => rig.Automation.ArriveAt(target),
        };
        rig.Service.Start();
        rig.PumpUntil(() => rig.Automation.TravelRequests.Contains("Faerie"));
        Assert.Equal("Faerie", rig.Store.Current!.CurrentWorld);
        Assert.Empty(rig.Store.Current.CompletedDestinationsInCurrentWorld);
        Assert.Null(rig.Store.Current.CurrentWorldPlan);

        rig.Automation.CurrentWorld = "Faerie";
        faerie.SetResult(ShoutRunnerTransferOutcome.Success());
        rig.PumpUntil(() => rig.Sent.Count(s => s.World == "Faerie") == 3);
        Assert.Equal(new[] { Uldah, Gridania, Limsa }.Order(), rig.Automation.Teleports.TakeLast(3).Order());   // every Faerie stop, each once
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
        Assert.Equal(ShoutRunnerService.MaxConsecutiveWrongDestinations, 2);
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
    public void Resume_after_an_unsupported_DC_skip_never_retries_that_DC_or_repeats_announcements()
    {
        var rig = MultiDcRig("Siren", "Aether", "Crystal", "Dynamis");
        // Crystal is skipped as unsupported; crash right after, while the route has moved on to Dynamis.
        rig.Service.Start();
        rig.PumpUntil(() => rig.Texts().Any(t => t.StartsWith("Crystal — SKIPPED", StringComparison.Ordinal)));
        Assert.Contains(rig.Store.Current!.SkippedDataCenters, s => s.DataCenter == "Crystal");
        var sentBefore = rig.Sent.Count;

        var rig2 = rig.Restart();
        rig2.Automation.CurrentWorld = "Siren";
        Assert.Equal(ShoutRunnerResumeResult.Resumed, rig2.Service.Resume());
        rig2.PumpUntil(() => rig2.Service.State is ShoutRunnerState.Stopped or ShoutRunnerState.WaitingRepeat);
        Assert.Contains(rig2.Texts(), t => t.StartsWith("Skipped Data Centers (recovered): Crystal", StringComparison.Ordinal));
        Assert.DoesNotContain(rig2.Automation.TravelRequests, w => ShoutRunnerCatalog.DataCenterFor(w) is "Crystal" or "Aether");
        Assert.Contains(rig2.Texts(), t => t.StartsWith("Dynamis — SKIPPED: travel to another Data Center", StringComparison.Ordinal));
        Assert.Empty(rig2.Sent);
        Assert.Equal(24, sentBefore);
    }

    [Fact]
    public void Resume_inside_a_DC_after_a_crash_continues_at_the_unfinished_world_without_repeats()
    {
        var rig = MultiDcRig("Siren", "Aether", "Primal");
        rig.Service.Start();
        rig.PumpUntil(() => rig.Store.Current!.CurrentWorld == "Gilgamesh" && rig.Store.Current.CompletedDestinationsInCurrentWorld.Count == 1);
        Assert.Equal(10, rig.Sent.Count);                                                  // Adamantoise, Cactuar, Faerie + one Gilgamesh stop

        var rig2 = rig.Restart();
        rig2.Automation.CurrentWorld = "Gilgamesh";
        rig2.Service.Resume();
        rig2.PumpUntil(() => rig2.Service.State is ShoutRunnerState.Stopped or ShoutRunnerState.WaitingRepeat);
        Assert.Equal(2, rig2.Sent.Count(s => s.World == "Gilgamesh"));                    // the two unfinished stops only
        Assert.Equal(8 * 3 - 10, rig2.Sent.Count);
        Assert.DoesNotContain(rig2.Automation.TravelRequests, w => ShoutRunnerCatalog.DataCenterFor(w) == "Primal");
    }
}
