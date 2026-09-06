using VenueOS.Core;
using VenueOS.Venues;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;

namespace VenueOS.Venues.Tests;

public sealed class VenueProfileTests
{
    [Fact] public void Malformed_or_unsupported_module_payload_recovers_without_overwriting_it()
    {
        var snapshot = VenueProfileService.CreateInitialSnapshot(); var key = new VenueModuleConfigKey(snapshot.ActiveVenueId, "test.module", 1).ToString();
        // Verify recovery from an incompatible saved schema without overwriting it.
        snapshot.ModulePayloads[key] = new ModulePayload(99, "[]"); var store = new InMemoryVenueStore(snapshot); var service = new VenueProfileService(store, new ModuleHost());
        var value = service.GetModuleConfig(snapshot.ActiveVenueId, "test.module", 1, () => new Setting("default"));
        Assert.Equal("default", value.Value); Assert.NotEmpty(service.RecoveryWarnings); Assert.Equal(99, store.Read().ModulePayloads[key].SchemaVersion);
    }
    [Fact] public void Valid_payload_never_triggers_recovery()
    {
        var service = New(out _); var venueId = service.Current.Id;
        service.SaveModuleConfig(venueId, "test.module", 1, new Setting("real value"));
        var value = service.GetModuleConfig(venueId, "test.module", 1, () => new Setting("default"));
        Assert.Equal("real value", value.Value); Assert.Empty(service.RecoveryWarnings);
    }

    /// <summary>The actual configuration-storm bug, reproduced directly: before the fix, a module reading its own
    /// persisted config from <c>Draw()</c> every frame (confirmed in <c>AttendanceOperatorPanel</c>/
    /// <c>AnnouncementsOperatorPanel</c>) turned one genuinely malformed payload into one Diagnostics warning per
    /// call — thousands per minute. <see cref="VenueProfileService.Recover{T}"/> now records a given
    /// module/schema/reason combination once, not once per read, regardless of how many times
    /// <see cref="VenueProfileService.GetModuleConfig{T}"/> is called for it.</summary>
    [Fact] public void Repeated_reads_of_the_same_bad_payload_record_the_recovery_once_not_per_read()
    {
        var snapshot = VenueProfileService.CreateInitialSnapshot(); var key = new VenueModuleConfigKey(snapshot.ActiveVenueId, "core.attendance", 1).ToString();
        snapshot.ModulePayloads[key] = new ModulePayload(1, ""); // exactly what a corrupted-on-save payload looks like
        var service = new VenueProfileService(new InMemoryVenueStore(snapshot), new ModuleHost());

        for (var i = 0; i < 500; i++) service.GetModuleConfig(snapshot.ActiveVenueId, "core.attendance", 1, () => new AttendanceSettings());

        Assert.Single(service.RecoveryWarnings);
    }

    /// <summary>Deduplication must never hide a genuinely different failure — a different module, or the same
    /// module failing for a different reason, both still show up distinctly.</summary>
    [Fact] public void Distinct_recovery_conditions_are_all_recorded()
    {
        var snapshot = VenueProfileService.CreateInitialSnapshot();
        snapshot.ModulePayloads[new VenueModuleConfigKey(snapshot.ActiveVenueId, "core.attendance", 1).ToString()] = new ModulePayload(1, ""); // empty payload
        snapshot.ModulePayloads[new VenueModuleConfigKey(snapshot.ActiveVenueId, "core.greeter", 1).ToString()] = new ModulePayload(1, ""); // different module, same reason
        snapshot.ModulePayloads[new VenueModuleConfigKey(snapshot.ActiveVenueId, "core.vip", 1).ToString()] = new ModulePayload(99, ""); // same-shaped key, different reason (schema mismatch)
        var service = new VenueProfileService(new InMemoryVenueStore(snapshot), new ModuleHost());

        service.GetModuleConfig(snapshot.ActiveVenueId, "core.attendance", 1, () => new AttendanceSettings());
        service.GetModuleConfig(snapshot.ActiveVenueId, "core.greeter", 1, () => new GreeterSettings());
        service.GetModuleConfig(snapshot.ActiveVenueId, "core.vip", 1, VipSettings.Default);

        Assert.Equal(3, service.RecoveryWarnings.Count);
        Assert.Contains(service.RecoveryWarnings, w => w.Contains("core.attendance", StringComparison.Ordinal) && w.Contains("payload is empty", StringComparison.Ordinal));
        Assert.Contains(service.RecoveryWarnings, w => w.Contains("core.greeter", StringComparison.Ordinal) && w.Contains("payload is empty", StringComparison.Ordinal));
        Assert.Contains(service.RecoveryWarnings, w => w.Contains("core.vip", StringComparison.Ordinal) && w.Contains("schema version is unsupported", StringComparison.Ordinal));
    }

    [Fact] public void SaveModuleConfig_produces_a_non_empty_valid_json_payload()
    {
        var store = new InMemoryVenueStore(); var service = new VenueProfileService(store, new ModuleHost()); var venueId = service.Current.Id;
        service.SaveModuleConfig(venueId, "core.greeter", 1, new GreeterSettings(ActivePresetId: 42));
        var raw = store.Read().ModulePayloads[new VenueModuleConfigKey(venueId, "core.greeter", 1).ToString()];
        Assert.False(string.IsNullOrEmpty(raw.Json));
        Assert.Contains("42", raw.Json, StringComparison.Ordinal);
    }

    /// <summary>The decisive regression test for the actual root cause: <c>ModulePayload</c> is a field inside
    /// <c>VenueOS.Plugin.VenueOsPluginConfiguration</c>, which Dalamud persists via a Newtonsoft.Json round trip with
    /// <c>TypeNameHandling.Auto</c> (confirmed directly against a live installation's on-disk <c>VenueOS.json</c>,
    /// which showed every module payload degraded to just <c>{"ValueKind": 0}</c> — the real bug this fixes). This
    /// test exercises that exact serializer, not <see cref="InMemoryVenueStore"/>'s in-memory copy, so it would have
    /// failed against the old <c>JsonElement</c>-typed payload and passes against the current <see cref="string"/>-typed
    /// one.</summary>
    [Fact] public void Module_config_survives_a_real_Newtonsoft_TypeNameHandling_round_trip()
    {
        var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto };
        var snapshot = VenueProfileService.CreateInitialSnapshot();
        var key = new VenueModuleConfigKey(snapshot.ActiveVenueId, "core.greeter", 1).ToString();
        snapshot.ModulePayloads[key] = new ModulePayload(1, System.Text.Json.JsonSerializer.Serialize(new GreeterSettings(ActivePresetId: 99)));

        var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, jsonSettings);
        var roundTripped = Newtonsoft.Json.JsonConvert.DeserializeObject<VenueStoreSnapshot>(serialized, jsonSettings)!;

        var service = new VenueProfileService(new InMemoryVenueStore(roundTripped), new ModuleHost());
        var settings = service.GetModuleConfig(snapshot.ActiveVenueId, "core.greeter", 1, () => new GreeterSettings());
        Assert.Empty(service.RecoveryWarnings);
        Assert.Equal(99, settings.ActivePresetId);
    }

    /// <summary>All four modules named in the live bug report (core.attendance, core.greeter, core.vip,
    /// promotion.partyfinder) round-trip through the same real Newtonsoft boundary — a fresh
    /// <see cref="VenueProfileService"/>/store pair reading what an earlier one wrote, simulating a plugin reload,
    /// with no recovery warnings and every saved value intact.</summary>
    [Fact] public void Attendance_greeter_vip_and_partyfinder_configs_all_survive_a_simulated_plugin_reload()
    {
        var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto };
        var firstStore = new InMemoryVenueStore(); var first = new VenueProfileService(firstStore, new ModuleHost());
        var venueId = first.Current.Id;
        first.SaveModuleConfig(venueId, "core.attendance", 1, new AttendanceSettings(VenueAddress: "Balmung, The Vat"));
        first.SaveModuleConfig(venueId, "core.greeter", 1, new GreeterSettings(ActivePresetId: 7));
        first.SaveModuleConfig(venueId, "core.vip", 1, new VipSettings([new("Ada", "Balmung", true, "Welcome back!", "Hi!", VipAnnouncementChannel.Shout)]));
        first.SaveModuleConfig(venueId, "promotion.partyfinder", 1, new Setting("party finder config"));

        var persisted = firstStore.Read();
        var reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<VenueStoreSnapshot>(Newtonsoft.Json.JsonConvert.SerializeObject(persisted, jsonSettings), jsonSettings)!;

        var second = new VenueProfileService(new InMemoryVenueStore(reloaded), new ModuleHost());
        Assert.Equal("Balmung, The Vat", second.GetModuleConfig(venueId, "core.attendance", 1, () => new AttendanceSettings()).VenueAddress);
        Assert.Equal(7, second.GetModuleConfig(venueId, "core.greeter", 1, () => new GreeterSettings()).ActivePresetId);
        Assert.Single(second.GetModuleConfig(venueId, "core.vip", 1, VipSettings.Default).Records);
        Assert.Equal("party finder config", second.GetModuleConfig(venueId, "promotion.partyfinder", 1, () => new Setting("default")).Value);
        Assert.Empty(second.RecoveryWarnings);
    }

    [Fact] public void Create_rename_and_schema_config_are_isolated() { var store = new InMemoryVenueStore(); var service = new VenueProfileService(store, new ModuleHost()); var a = service.Current; var b = service.Create("B", BuiltInThemes.Neon); service.SaveModuleConfig(a.Id, "test.module", 1, new Setting("A")); service.SaveModuleConfig(b.Id, "test.module", 2, new Setting("B")); Assert.True(service.Rename(b.Id, "Renamed").Success); Assert.Equal("A", service.GetModuleConfig(a.Id, "test.module", 1, () => new Setting("x")).Value); Assert.Equal("B", service.GetModuleConfig(b.Id, "test.module", 2, () => new Setting("x")).Value); Assert.Equal("Renamed", store.Read().Venues.Single(x => x.Id == b.Id).DisplayName); }
    [Fact] public void Duplicate_deep_copies_theme_and_payloads() { var service = New(out _); var source = service.Current; service.SaveModuleConfig(source.Id, "test.module", 1, new Setting("original")); var copy = service.Duplicate(source.Id, "Copy"); service.SaveModuleConfig(copy.Id, "test.module", 1, new Setting("copy")); Assert.NotEqual(source.Id, copy.Id); Assert.Equal("original", service.GetModuleConfig(source.Id, "test.module", 1, () => new Setting("x")).Value); Assert.Equal("copy", service.GetModuleConfig(copy.Id, "test.module", 1, () => new Setting("x")).Value); }
    [Fact] public async Task Switching_changes_context_and_theme_without_leakage() { var service = New(out var module); var a = service.Current; var b = service.Create("B", BuiltInThemes.Light); service.SaveModuleConfig(a.Id, "fake", 1, new Setting("one")); service.SaveModuleConfig(b.Id, "fake", 1, new Setting("two")); Assert.True((await service.SwitchAsync(b.Id)).Success); Assert.Equal("B", module.Last?.DisplayName); Assert.Equal("light", ((VenueTheme)module.Last!.Theme).BuiltInThemeId); Assert.Equal("one", service.GetModuleConfig(a.Id, "fake", 1, () => new Setting("x")).Value); }
    /// <summary>Core venue-switch resilience: a module throwing from <c>OnVenueChangedAsync</c> (the exact live bug —
    /// promotion.partyfinder's incomplete third-party initialization throwing a NullReferenceException) must be
    /// isolated, not treated as an all-or-nothing transaction. The switch must still succeed, the active Venue
    /// Profile must still become the destination, the failure must be recorded via <see cref="ModuleHost.ModuleFailed"/>,
    /// an unrelated healthy module must still receive the new venue context, and a subsequent switch back must still
    /// work normally — none of this was true before this fix (the old <c>NotifyVenueChangedTransactionalAsync</c>
    /// re-threw on any module failure, rolling the whole switch back).</summary>
    [Fact] public async Task Failing_module_does_not_block_the_core_venue_switch_or_unrelated_modules()
    {
        var host = new ModuleHost();
        var failing = new FailingModule("promotion.partyfinder-fake");
        var healthy = new TrackingModule("core.attendance-fake");
        host.Register(failing); host.Register(healthy);
        var failureMessages = new List<string>();
        host.ModuleFailed += (message, _) => failureMessages.Add(message);
        var service = new VenueProfileService(new InMemoryVenueStore(), host);
        await service.InitializeAsync();
        var venueB = service.Create("Venue B");

        failing.Fail = true;
        var toB = await service.SwitchAsync(venueB.Id);
        Assert.True(toB.Success); // the core switch succeeds despite the failing module
        Assert.Equal(venueB.Id, service.Current.Id); // the active Venue Profile still became the destination
        Assert.Equal(venueB.Id, healthy.Last?.VenueId); // an unrelated healthy module still received the new venue
        Assert.Contains(failureMessages, m => m.Contains(failing.Descriptor.Id, StringComparison.Ordinal)); // the failure was recorded, not swallowed silently

        // Venue B -> Venue A must still work normally, including after a prior failed module notification.
        var venueA = service.Profiles.Single(x => x.Id != venueB.Id);
        failing.Fail = false;
        var backToA = await service.SwitchAsync(venueA.Id);
        Assert.True(backToA.Success);
        Assert.Equal(venueA.Id, service.Current.Id);
        Assert.Equal(venueA.Id, healthy.Last?.VenueId);
    }

    /// <summary>Module dependency *ordering* is still respected after this fix (modules still run in the same
    /// topological order — a dependency's <c>OnVenueChangedAsync</c> is still invoked before its dependents'), but
    /// a dependency's failure does not skip its dependents: promotion.partyfinder has no declared dependents, and
    /// this codebase's dependency graph (<c>core.greeter</c> depends on <c>core.attendance</c>, <c>core.vip</c>
    /// depends on <c>core.greeter</c>) is only ever used for *ordering*, never for "skip on parent failure" — adding
    /// that semantics was considered and deliberately not introduced by this fix, since no current module actually
    /// requires it and it would be a materially larger, unrelated change to core dispatch behavior. This test proves
    /// the actual (ordering-preserved, isolation-per-module) behavior rather than asserting an unverified theory.</summary>
    [Fact] public async Task A_failing_dependency_does_not_prevent_its_dependent_from_being_notified()
    {
        var host = new ModuleHost();
        var dependency = new FailingModule("dependency.fake") { Fail = true };
        var dependent = new TrackingModule("dependent.fake", ["dependency.fake"]);
        host.Register(dependency); host.Register(dependent);
        var service = new VenueProfileService(new InMemoryVenueStore(), host);
        await service.InitializeAsync();
        var venueB = service.Create("Venue B");

        var result = await service.SwitchAsync(venueB.Id);
        Assert.True(result.Success);
        Assert.Equal(venueB.Id, dependent.Last?.VenueId); // the dependent still transitioned despite its dependency failing
    }

    /// <summary>Global (non-venue) preferences share no code path with venue switching at all
    /// (<see cref="VenueOS.Services.GlobalSettingsService"/> holds no reference to <see cref="VenueProfileService"/>
    /// or <see cref="ModuleHost"/>) — this proves it rather than asserting it from the architecture alone.</summary>
    [Fact] public async Task Global_settings_are_unaffected_by_a_venue_switch()
    {
        var globalStore = new FakeGlobalSettingsStore();
        var globalSettings = new GlobalSettingsService(globalStore);
        globalSettings.SetAutoPopOutModules(true);
        var service = New(out _);
        var b = service.Create("B");
        Assert.True((await service.SwitchAsync(b.Id)).Success);
        Assert.True(globalSettings.AutoPopOutModules);
    }
    [Fact] public async Task Delete_requires_confirmation_switches_and_preserves_final() { var service = New(out _); var a = service.Current; var b = service.Create("B"); Assert.False((await service.DeleteAsync(a.Id, false)).Success); Assert.True((await service.DeleteAsync(a.Id, true)).Success); Assert.Equal(b.Id, service.Current.Id); Assert.False((await service.DeleteAsync(b.Id, true)).Success); }
    [Fact] public void Vip_and_greeter_payloads_are_isolated_per_venue() { var service = New(out _); var a = service.Current; var b = service.Create("B"); service.SaveModuleConfig(a.Id, "core.greeter", 1, new GreeterSettings(AutoGreetEnabled: false, ActivePresetId: 7)); service.SaveModuleConfig(b.Id, "core.vip", 1, new VipSettings([new("Ada", "Balmung", true, "Hello <name>", "Welcome", VipAnnouncementChannel.Shout)])); Assert.False(service.GetModuleConfig(a.Id, "core.greeter", 1, () => new GreeterSettings()).AutoGreetEnabled); Assert.True(service.GetModuleConfig(b.Id, "core.greeter", 1, () => new GreeterSettings()).AutoGreetEnabled); Assert.Empty(service.GetModuleConfig(a.Id, "core.vip", 1, VipSettings.Default).Records); Assert.Single(service.GetModuleConfig(b.Id, "core.vip", 1, VipSettings.Default).Records); }
    /// <summary>Mandatory VIP-database regression: each VIP's own custom private tell and public message persist
    /// through a save/reload cycle exactly like any other per-venue config, and identity remains Name+HomeWorld.</summary>
    [Fact] public void Vip_record_custom_tell_and_public_message_survive_reload()
    {
        var service = New(out _); var venue = service.Current;
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([new("Rabid Squirrel", "Halicarnassus", true, "Welcome back, <name>!", "Everyone welcome back <name>!", VipAnnouncementChannel.Shout)]));

        var reloaded = service.GetModuleConfig(venue.Id, "core.vip", 1, VipSettings.Default);
        var record = reloaded.Records.Single();
        Assert.Equal("Rabid Squirrel", record.CharacterName); Assert.Equal("Halicarnassus", record.HomeWorld);
        Assert.Equal("Welcome back, <name>!", record.CustomTell);
        Assert.Equal("Everyone welcome back <name>!", record.PublicAnnouncement);
        Assert.Equal("RABID SQUIRREL@HALICARNASSUS", record.Key);
    }

    /// <summary>Mandatory VIP-database regression: two VIPs never share a custom tell or public message — each
    /// record's content is entirely independent.</summary>
    [Fact] public void Vip_a_and_vip_b_have_independent_custom_tells_and_public_messages()
    {
        var service = New(out _); var venue = service.Current;
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([
            new("Ada", "Balmung", true, "Message A", "Public A", VipAnnouncementChannel.Shout),
            new("Zeta", "Balmung", true, "Message B", "Public B", VipAnnouncementChannel.Yell),
        ]));

        var records = service.GetModuleConfig(venue.Id, "core.vip", 1, VipSettings.Default).Records;
        Assert.Equal("Message A", records.Single(r => r.CharacterName == "Ada").CustomTell);
        Assert.Equal("Message B", records.Single(r => r.CharacterName == "Zeta").CustomTell);
        Assert.Equal("Public A", records.Single(r => r.CharacterName == "Ada").PublicAnnouncement);
        Assert.Equal("Public B", records.Single(r => r.CharacterName == "Zeta").PublicAnnouncement);
    }

    /// <summary>Mandatory VIP-database regression: toggling Enabled/Disabled never touches the stored custom
    /// tell/public message.</summary>
    [Fact] public void Vip_enable_disable_preserves_custom_tell_and_public_message()
    {
        var service = New(out _); var venue = service.Current;
        var original = new VipRecord("Ada", "Balmung", true, "My Tell", "My Public", VipAnnouncementChannel.Shout);
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([original]));

        var disabled = original with { Enabled = false };
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([disabled]));

        var reloaded = service.GetModuleConfig(venue.Id, "core.vip", 1, VipSettings.Default).Records.Single();
        Assert.False(reloaded.Enabled);
        Assert.Equal("My Tell", reloaded.CustomTell); Assert.Equal("My Public", reloaded.PublicAnnouncement);
    }

    /// <summary>Mandatory VIP-database regression: editing one VIP's record never touches another's, and removing
    /// one VIP never touches another's.</summary>
    [Fact] public void Vip_edit_and_remove_only_affect_the_selected_record()
    {
        var service = New(out _); var venue = service.Current;
        var ada = new VipRecord("Ada", "Balmung", true, "Tell A", "Public A", VipAnnouncementChannel.Shout);
        var zeta = new VipRecord("Zeta", "Balmung", true, "Tell Z", "Public Z", VipAnnouncementChannel.Yell);
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([ada, zeta]));

        // Edit only Ada's tell.
        var editedAda = ada with { CustomTell = "Updated Tell A" };
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([editedAda, zeta]));
        var afterEdit = service.GetModuleConfig(venue.Id, "core.vip", 1, VipSettings.Default).Records;
        Assert.Equal("Updated Tell A", afterEdit.Single(r => r.CharacterName == "Ada").CustomTell);
        Assert.Equal("Tell Z", afterEdit.Single(r => r.CharacterName == "Zeta").CustomTell); // untouched

        // Remove only Ada.
        service.SaveModuleConfig(venue.Id, "core.vip", 1, new VipSettings([zeta]));
        var afterRemove = service.GetModuleConfig(venue.Id, "core.vip", 1, VipSettings.Default).Records;
        Assert.Single(afterRemove); Assert.Equal("Zeta", afterRemove.Single().CharacterName);
    }

    [Fact] public void ShoutRunner_settings_are_isolated_per_venue() { var service = New(out _); var a = service.Current; var b = service.Create("B"); service.SaveModuleConfig(a.Id, "communication.announcements", 2, new ShoutRunnerSettings("alpha", true, 1, 0, 0, 2, ["Aether"], ["A"])); service.SaveModuleConfig(b.Id, "communication.announcements", 2, new ShoutRunnerSettings("beta", true, 1, 0, 0, 2, ["Primal"], ["B"])); Assert.Equal("alpha", service.GetModuleConfig(a.Id, "communication.announcements", 2, ShoutRunnerSettings.Default).ShoutMessage); Assert.Equal("beta", service.GetModuleConfig(b.Id, "communication.announcements", 2, ShoutRunnerSettings.Default).ShoutMessage); }
    [Fact] public async Task Attendance_greeter_and_vip_live_state_reloads_per_venue_without_leaking_on_switch()
    {
        var clock = new SystemClock(); var presence = new PresenceService(clock, new EmptyProvider()); var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true);
        using var db = new SqliteVenueDatabase("Data Source=:memory:");
        var attendanceService = new AttendanceService(presence, db, clock); var greeterService = new GreeterService(clock, chat, db); var vipService = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendanceService.IsGreeted(g), greeterService, vipService, chat);
        var host = new ModuleHost(); var profiles = new VenueProfileService(new InMemoryVenueStore(), host);
        host.Register(new AttendanceModule(attendanceService, presence, profiles)); host.Register(new GreeterModule(greeterService, profiles)); host.Register(new VipModule(vipService, coordinator, profiles));
        var a = profiles.Current; var b = profiles.Create("B");
        var presetA = db.SavePreset(a.Id, null, "Venue A DJ", "hi", "", "", "");
        profiles.SaveModuleConfig(a.Id, "core.greeter", 1, new GreeterSettings(ActivePresetId: presetA));
        profiles.SaveModuleConfig(a.Id, "core.vip", 1, new VipSettings([new("Ada", "Balmung", true, "Hi <name>", "Welcome", VipAnnouncementChannel.Shout)]));
        await profiles.InitializeAsync();
        Assert.Equal("Venue A DJ", greeterService.ActivePresetName); Assert.Single(vipService.Settings.Records);

        Assert.True((await profiles.SwitchAsync(b.Id)).Success);
        Assert.Equal("None", greeterService.ActivePresetName); Assert.Empty(vipService.Settings.Records); Assert.Empty(greeterService.GetPresetLibrary());

        var presetB = db.SavePreset(b.Id, null, "Venue B DJ", "hi", "", "", "");
        profiles.SaveModuleConfig(b.Id, "core.greeter", 1, new GreeterSettings(ActivePresetId: presetB));

        Assert.True((await profiles.SwitchAsync(a.Id)).Success);
        Assert.Equal("Venue A DJ", greeterService.ActivePresetName); Assert.Single(vipService.Settings.Records); Assert.Single(greeterService.GetPresetLibrary());
    }
    [Fact] public void Attendance_greeter_and_vip_modules_route_draw_and_drawsettings_to_distinct_delegates()
    {
        var clock = new SystemClock(); var presence = new PresenceService(clock, new EmptyProvider()); var chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), _ => true);
        using var db = new SqliteVenueDatabase("Data Source=:memory:");
        var attendanceService = new AttendanceService(presence, db, clock); var greeterService = new GreeterService(clock, chat, db); var vipService = new VipOrchestrationService();
        var coordinator = new GreetingCoordinator(g => attendanceService.IsGreeted(g), greeterService, vipService, chat);
        var profiles = New(out _);
        var attendanceCalls = new List<string>(); var greeterCalls = new List<string>(); var vipCalls = new List<string>();
        var attendanceModule = new AttendanceModule(attendanceService, presence, profiles, () => attendanceCalls.Add("draw"), () => attendanceCalls.Add("settings"));
        var greeterModule = new GreeterModule(greeterService, profiles, () => greeterCalls.Add("draw"), () => greeterCalls.Add("settings"));
        var vipModule = new VipModule(vipService, coordinator, profiles, () => vipCalls.Add("draw"), () => vipCalls.Add("settings"));
        attendanceModule.Draw(); attendanceModule.DrawSettings();
        greeterModule.Draw(); greeterModule.DrawSettings();
        vipModule.Draw(); vipModule.DrawSettings();
        Assert.Equal(["draw", "settings"], attendanceCalls);
        Assert.Equal(["draw", "settings"], greeterCalls);
        Assert.Equal(["draw", "settings"], vipCalls);
    }
    [Fact] public void Attendance_tracking_settings_are_isolated_per_venue() { var service = New(out _); var a = service.Current; var b = service.Create("B"); service.SaveModuleConfig(a.Id, "core.attendance", 1, new AttendanceSettings(RadiusYalms: 15f, VenueAddress: "Venue A address")); service.SaveModuleConfig(b.Id, "core.attendance", 1, new AttendanceSettings(RadiusYalms: 60f, VenueAddress: "Venue B address")); Assert.Equal(15f, service.GetModuleConfig(a.Id, "core.attendance", 1, () => new AttendanceSettings()).RadiusYalms); Assert.Equal("Venue A address", service.GetModuleConfig(a.Id, "core.attendance", 1, () => new AttendanceSettings()).VenueAddress); Assert.Equal("Venue B address", service.GetModuleConfig(b.Id, "core.attendance", 1, () => new AttendanceSettings()).VenueAddress); }
    private static VenueProfileService New(out TrackingModule module) { var host = new ModuleHost(); module = new TrackingModule(); host.Register(module); return new VenueProfileService(new InMemoryVenueStore(), host); }
    private sealed record Setting(string Value); private class TrackingModule(string id = "fake", IReadOnlyList<string>? dependencies = null) : IVenueModule { public ModuleDescriptor Descriptor { get; } = new(id, "Fake", "", "", dependencies); public bool IsEnabled { get; set; } = true; public VenueContext? Last { get; private set; } public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask; public virtual Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { Last = c; return Task.CompletedTask; } public void Tick(DateTimeOffset n) { } public void Draw() { } public void DrawSettings() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class FailingModule(string id = "fake.failing") : TrackingModule(id) { public bool Fail { get; set; } public override Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { if (Fail) throw new InvalidOperationException("injected"); return base.OnVenueChangedAsync(c, t); } }
    private sealed class EmptyProvider : IObjectSnapshotProvider { public IReadOnlyList<PlayerSnapshot> Snapshot() => []; }
    private sealed class FakeGlobalSettingsStore : IGlobalSettingsStore { private GlobalSettings settings = new(); public GlobalSettings Read() => settings; public void Write(GlobalSettings value) => settings = value; }
}
