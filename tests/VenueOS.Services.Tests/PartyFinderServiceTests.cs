using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class PartyFinderServiceTests
{
    [Fact] public void New_venue_gets_default_settings()
    {
        var (service, _, venueId, _) = Create();
        service.Load(venueId);
        Assert.True(service.Settings.AutoRefreshEnabled);
        Assert.Equal(string.Empty, service.Settings.WarningMessageOverride);
        Assert.Equal(DateTime.MinValue, service.Settings.LastRefreshAttemptUtc);
    }

    /// <summary>DIAGNOSTIC / acceptance test for the live-reported "values revert to defaults across a VenueOS
    /// disable/re-enable" bug. Crosses a genuine persistence boundary: the snapshot is serialized to a raw JSON
    /// string and a brand new <see cref="InMemoryVenueStore"/>/<see cref="VenueProfileService"/>/
    /// <see cref="PartyFinderService"/> triple is constructed from that string — no object reference from the
    /// "before" side survives. If any field fails to round-trip through real JSON (de)serialization — exactly what
    /// <c>VenueProfileService.GetModuleConfig</c>/<c>SaveModuleConfig</c> and the real Dalamud-backed store do — this
    /// test fails, which a same-instance-only test (reading the same in-memory object back) cannot detect.</summary>
    [Fact] public void Every_preset_field_survives_a_full_serialize_deserialize_boundary()
    {
        var (service, profiles, venueId, _, store) = CreateWithStore();
        service.Load(venueId);

        var distinctive = new PartyFinderPreset
        {
            Category = PartyFinderCategory.Trials,
            DutyId = 777,
            Objective = PartyFinderObjective.Loot,
            BeginnerFriendly = true,
            CompletionStatus = PartyFinderCompletionStatus.DutyIncomplete,
            AverageItemLevelEnabled = true,
            AverageItemLevel = 650,
            DutyFinderSettings = PartyFinderDutyFinderSetting.UnrestrictedParty | PartyFinderDutyFinderSetting.SilenceEcho,
            LootRule = PartyFinderLootRule.Lootmaster,
            PrivateParty = true,
            Password = 4242,
            Languages = PartyFinderLanguage.Japanese | PartyFinderLanguage.French,
            NumberOfSlotsInMainParty = 4,
            LimitRecruitingToWorld = true,
            OnePlayerPerJob = true,
            NumberOfGroups = 3,
            Comment = "Distinctive persisted comment — survive me!",
        };
        distinctive = distinctive.WithSlot(0, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Tank)));
        distinctive = distinctive.WithSlot(1, new PartyFinderSlot([PartyFinderJob.WhiteMage, PartyFinderJob.Sage]));
        distinctive = distinctive.WithSlot(2, new PartyFinderSlot([PartyFinderJob.Ninja]));

        service.UpdatePreset(distinctive);
        service.SetAutoRefreshEnabled(false);
        service.SetWarningMessageOverride("Custom five minute text");
        service.Refresh("test"); // sets LastRefreshAttemptUtc to a non-default value

        // Cross a real persistence boundary: serialize the entire venue snapshot to text and rebuild every layer
        // from that text alone, exactly as a fresh Dalamud plugin instance would after deserializing its config file.
        var snapshotJson = JsonSerializer.Serialize(store.Read());
        var rebuiltSnapshot = JsonSerializer.Deserialize<VenueStoreSnapshot>(snapshotJson)!;
        var freshProfiles = new VenueProfileService(new InMemoryVenueStore(rebuiltSnapshot), new ModuleHost());
        var freshService = new PartyFinderService(new FakeAutomation(), freshProfiles, new FakeClock());
        freshService.Load(venueId);

        var reloaded = freshService.Settings.Preset;
        Assert.Equal(PartyFinderCategory.Trials, reloaded.Category);
        Assert.Equal((ushort)777, reloaded.DutyId);
        Assert.Equal(PartyFinderObjective.Loot, reloaded.Objective);
        Assert.True(reloaded.BeginnerFriendly);
        Assert.Equal(PartyFinderCompletionStatus.DutyIncomplete, reloaded.CompletionStatus);
        Assert.True(reloaded.AverageItemLevelEnabled);
        Assert.Equal((ushort)650, reloaded.AverageItemLevel);
        Assert.Equal(PartyFinderDutyFinderSetting.UnrestrictedParty | PartyFinderDutyFinderSetting.SilenceEcho, reloaded.DutyFinderSettings);
        Assert.Equal(PartyFinderLootRule.Lootmaster, reloaded.LootRule);
        Assert.True(reloaded.PrivateParty);
        Assert.Equal((ushort)4242, reloaded.Password);
        Assert.Equal(PartyFinderLanguage.Japanese | PartyFinderLanguage.French, reloaded.Languages);
        Assert.Equal((byte)4, reloaded.NumberOfSlotsInMainParty);
        Assert.True(reloaded.LimitRecruitingToWorld);
        Assert.True(reloaded.OnePlayerPerJob);
        Assert.Equal((byte)3, reloaded.NumberOfGroups);
        Assert.Equal("Distinctive persisted comment — survive me!", reloaded.Comment);
        Assert.Equal(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Tank).ToHashSet(), reloaded.GetSlot(0).Jobs.ToHashSet());
        Assert.Equal(new HashSet<PartyFinderJob> { PartyFinderJob.WhiteMage, PartyFinderJob.Sage }, reloaded.GetSlot(1).Jobs.ToHashSet());
        Assert.Equal(new HashSet<PartyFinderJob> { PartyFinderJob.Ninja }, reloaded.GetSlot(2).Jobs.ToHashSet());
        Assert.True(reloaded.GetSlot(10).IsAnyJob);

        Assert.False(freshService.Settings.AutoRefreshEnabled);
        Assert.Equal("Custom five minute text", freshService.Settings.WarningMessageOverride);
        Assert.NotEqual(DateTime.MinValue, freshService.Settings.LastRefreshAttemptUtc);
    }

    /// <summary>Per-venue counterpart to the boundary test above — two venues' presets must not merge or leak into
    /// each other even after a full serialize/deserialize round trip into brand new service instances.</summary>
    [Fact] public void Per_venue_isolation_survives_a_full_serialize_deserialize_boundary()
    {
        var (service, profiles, venueA, _, store) = CreateWithStore();
        var venueB = profiles.Create("Second Venue").Id;

        service.Load(venueA);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Venue A listing", Category = PartyFinderCategory.Dungeons });
        service.Load(venueB);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Venue B listing", Category = PartyFinderCategory.Raids });

        var rebuiltSnapshot = JsonSerializer.Deserialize<VenueStoreSnapshot>(JsonSerializer.Serialize(store.Read()))!;
        var freshProfiles = new VenueProfileService(new InMemoryVenueStore(rebuiltSnapshot), new ModuleHost());

        var freshA = new PartyFinderService(new FakeAutomation(), freshProfiles, new FakeClock());
        freshA.Load(venueA);
        Assert.Equal("Venue A listing", freshA.Settings.Preset.Comment);
        Assert.Equal(PartyFinderCategory.Dungeons, freshA.Settings.Preset.Category);

        var freshB = new PartyFinderService(new FakeAutomation(), freshProfiles, new FakeClock());
        freshB.Load(venueB);
        Assert.Equal("Venue B listing", freshB.Settings.Preset.Comment);
        Assert.Equal(PartyFinderCategory.Raids, freshB.Settings.Preset.Category);
    }

    [Fact] public void Preset_changes_round_trip_through_the_venue_store()
    {
        var (service, profiles, venueId, automation) = Create();
        service.Load(venueId);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Come on in!", DutyId = 42 });

        var reloaded = new PartyFinderService(automation, profiles, new FakeClock());
        reloaded.Load(venueId);
        Assert.Equal("Come on in!", reloaded.Settings.Preset.Comment);
        Assert.Equal((ushort)42, reloaded.Settings.Preset.DutyId);
    }

    [Fact] public void Two_venues_never_see_each_others_settings()
    {
        var (service, profiles, venueA, automation) = Create();
        var venueB = profiles.Create("Second Venue").Id;

        service.Load(venueA);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Venue A open" });
        service.SetAutoRefreshEnabled(false);

        service.Load(venueB);
        Assert.NotEqual("Venue A open", service.Settings.Preset.Comment);
        Assert.True(service.Settings.AutoRefreshEnabled); // venue B's own default, untouched by venue A's change

        service.Load(venueA);
        Assert.Equal("Venue A open", service.Settings.Preset.Comment);
        Assert.False(service.Settings.AutoRefreshEnabled);
    }

    [Fact] public void Switching_venues_resets_the_automation_engines_runtime_state()
    {
        var (service, _, venueA, automation) = Create();
        service.Load(venueA);
        Assert.Equal(1, automation.ResetForVenueCalls);
        service.Load(venueA); // a second activation (e.g. plugin restart) still resets
        Assert.Equal(2, automation.ResetForVenueCalls);
    }

    [Fact] public void Last_refresh_attempt_is_isolated_per_venue()
    {
        var (service, profiles, venueA, automation) = Create();
        var venueB = profiles.Create("Second Venue").Id;
        automation.HasOwnListing = true;

        service.Load(venueA);
        service.Refresh("manual");
        Assert.NotEqual(DateTime.MinValue, service.Settings.LastRefreshAttemptUtc);

        service.Load(venueB);
        Assert.Equal(DateTime.MinValue, service.Settings.LastRefreshAttemptUtc);
    }

    [Fact] public void Manual_create_or_refresh_resets_the_auto_refresh_throttle()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        service.CreateOrUpdate("manual");
        Assert.NotEqual(DateTime.MinValue, service.Settings.LastRefreshAttemptUtc);
    }

    [Fact] public void Auto_refresh_is_ignored_while_disabled()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        service.SetAutoRefreshEnabled(false);
        service.HandleChatText("Your party recruitment closes in five minutes.");
        Assert.Empty(automation.RefreshCalls);
    }

    [Fact] public void Auto_refresh_fires_on_the_five_minute_warning_when_enabled()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        service.HandleChatText("Your party recruitment closes in five minutes.");
        Assert.Single(automation.RefreshCalls);
    }

    [Fact] public void Recruitment_ended_chat_text_notifies_the_automation_engine_without_touching_auto_refresh()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        service.HandleChatText("Your party recruitment has ended.");
        Assert.Equal(1, automation.NotifyListingEndedCalls);
        Assert.True(service.Settings.AutoRefreshEnabled);
    }

    // ---- End Party Finder -------------------------------------------------------------------------------------

    [Fact] public void End_party_finder_enters_the_ending_state_before_the_automation_engine_reports_completion()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        var observedEnding = false;
        automation.OnEndPartyFinderInvoked = () => observedEnding = automation.IsEnding;
        service.EndPartyFinder("operator panel");
        Assert.True(observedEnding);
    }

    [Fact] public void End_party_finder_disables_and_persists_auto_refresh_before_touching_automation()
    {
        var (service, profiles, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        automation.OnEndPartyFinderInvoked = () => Assert.False(service.Settings.AutoRefreshEnabled); // already false by the time automation is asked to end
        service.EndPartyFinder("operator panel");

        Assert.False(service.Settings.AutoRefreshEnabled);
        var reloaded = new PartyFinderService(automation, profiles, new FakeClock());
        reloaded.Load(venueId);
        Assert.False(reloaded.Settings.AutoRefreshEnabled); // persisted, not just in-memory
    }

    /// <summary>Hard requirement: End Party Finder changes operational recruitment/Auto Refresh state only — it must
    /// never reset, clear, or replace the persisted listing preset. The next venue night should reopen Party Finder
    /// and find last night's configuration still there.</summary>
    [Fact] public void End_party_finder_does_not_clear_or_reset_the_preset()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        var distinctive = PartyFinderPreset.CreateDefault() with { Comment = "Keep me across End Party Finder", DutyId = 999, PrivateParty = true, Password = 1234 };
        service.UpdatePreset(distinctive);
        automation.HasOwnListing = true;

        service.EndPartyFinder("operator panel");

        Assert.Equal("Keep me across End Party Finder", service.Settings.Preset.Comment);
        Assert.Equal((ushort)999, service.Settings.Preset.DutyId);
        Assert.True(service.Settings.Preset.PrivateParty);
        Assert.Equal((ushort)1234, service.Settings.Preset.Password);
    }

    /// <summary>The actual re-entrancy guard against a duplicate/simultaneous End is the automation engine's own
    /// <c>state == Ending</c> check (unsafe engine code, live-verified only — see PARTY_FINDER_RECONSTRUCTION.md).
    /// This asserts the service layer itself stays safe regardless: a second End call never resurrects Auto Refresh
    /// or disturbs the persisted preset.</summary>
    [Fact] public void Repeated_end_calls_never_resurrect_auto_refresh_or_disturb_the_preset()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        service.UpdatePreset(PartyFinderPreset.CreateDefault() with { Comment = "Unchanged" });
        automation.HasOwnListing = true;

        service.EndPartyFinder("first click");
        Assert.False(service.Settings.AutoRefreshEnabled);

        service.EndPartyFinder("second click");
        Assert.False(service.Settings.AutoRefreshEnabled);
        Assert.Equal("Unchanged", service.Settings.Preset.Comment);
    }

    [Fact] public void End_party_finder_leaves_auto_refresh_disabled_even_if_withdrawal_fails()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        automation.FailEndPartyFinder = true;
        service.EndPartyFinder("operator panel");
        Assert.False(service.Settings.AutoRefreshEnabled);
    }

    [Fact] public void Stale_auto_refresh_cannot_run_once_ending_has_begun()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        service.EndPartyFinder("operator panel");
        automation.IsEnding = true; // the real engine keeps this true until its own shutdown chain finishes

        service.HandleChatText("Your party recruitment closes in five minutes.");
        Assert.Empty(automation.RefreshCalls);
    }

    [Fact] public void Create_and_refresh_are_blocked_while_ending()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.IsEnding = true;

        service.CreateOrUpdate("operator panel");
        service.Refresh("operator panel");

        Assert.Empty(automation.CreateOrUpdateCalls);
        Assert.Empty(automation.RefreshCalls);
    }

    [Fact] public void Ending_one_venue_does_not_disable_auto_refresh_for_another()
    {
        var (service, profiles, venueA, automation) = Create();
        var venueB = profiles.Create("Second Venue").Id;

        service.Load(venueA);
        automation.HasOwnListing = true;
        service.EndPartyFinder("operator panel");

        service.Load(venueB);
        Assert.True(service.Settings.AutoRefreshEnabled);
    }

    [Fact] public void Ending_with_no_active_listing_still_disables_auto_refresh_and_calls_the_engine()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = false;
        service.EndPartyFinder("operator panel");
        Assert.False(service.Settings.AutoRefreshEnabled);
        Assert.Single(automation.EndPartyFinderCalls);
    }

    [Fact] public void Successful_end_reports_an_inactive_listing()
    {
        var (service, _, venueId, automation) = Create();
        service.Load(venueId);
        automation.HasOwnListing = true;
        service.EndPartyFinder("operator panel"); // fake automation clears HasOwnListing on success
        Assert.False(service.HasOwnListing);
    }

    private static (PartyFinderService Service, VenueProfileService Profiles, Guid VenueId, FakeAutomation Automation) Create()
    {
        var (service, profiles, venueId, automation, _) = CreateWithStore();
        return (service, profiles, venueId, automation);
    }

    private static (PartyFinderService Service, VenueProfileService Profiles, Guid VenueId, FakeAutomation Automation, InMemoryVenueStore Store) CreateWithStore()
    {
        var store = new InMemoryVenueStore();
        var profiles = new VenueProfileService(store, new ModuleHost());
        var automation = new FakeAutomation();
        var service = new PartyFinderService(automation, profiles, new FakeClock());
        return (service, profiles, profiles.Current.Id, automation, store);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class FakeAutomation : IPartyFinderAutomation
    {
        public string Status { get; private set; } = "Idle";
        public bool IsCompatibilityVerified { get; set; }
        public bool HasOwnListing { get; set; }
        public bool IsBusy { get; private set; }
        public bool IsEnding { get; set; }
        public bool FailEndPartyFinder { get; set; }
        public Action? OnEndPartyFinderInvoked { get; set; }

        public int ResetForVenueCalls { get; private set; }
        public int NotifyListingEndedCalls { get; private set; }
        public List<(PartyFinderPreset Preset, string Reason)> CreateOrUpdateCalls { get; } = [];
        public List<(PartyFinderPreset Preset, string Reason)> RefreshCalls { get; } = [];
        public List<string> EndPartyFinderCalls { get; } = [];

        public void ResetForVenue() { ResetForVenueCalls++; IsBusy = false; IsEnding = false; }

        public void Abort() => IsBusy = false;

        public void QueueCreateOrUpdate(PartyFinderPreset preset, string reason) { CreateOrUpdateCalls.Add((preset, reason)); IsBusy = true; }

        public void QueueRefresh(PartyFinderPreset preset, string reason) { RefreshCalls.Add((preset, reason)); IsBusy = true; }

        public void EndPartyFinder(string reason)
        {
            EndPartyFinderCalls.Add(reason);
            IsEnding = true; // matches the real engine: Ending is entered synchronously, before the (here: also
                              // synchronous, for test simplicity) shutdown work runs.
            OnEndPartyFinderInvoked?.Invoke();
            if (FailEndPartyFinder)
            {
                IsEnding = false;
                return;
            }

            HasOwnListing = false;
            IsEnding = false;
        }

        public void NotifyListingEnded() { NotifyListingEndedCalls++; HasOwnListing = false; }
    }
}
