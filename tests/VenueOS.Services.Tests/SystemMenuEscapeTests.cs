using VenueOS.Core;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>0.3.8 maintenance WP6 — "VenueOS opens the FFXIV System Menu". Root cause: ShoutRunner's
/// <c>HardStop()</c> (run on every venue activation/switch, module disable and plugin dispose/<c>/xlreload</c>)
/// called <see cref="IShoutRunnerAutomation.Abort"/> unconditionally, and the real engine's Abort synthesized a real
/// Escape keypress to the game window — whose native meaning, in-world with nothing to cancel, is "open the System
/// Menu". See <c>docs/SYSTEM_MENU_0.3.8_INVESTIGATION.md</c>.
///
/// What this proves: the lifecycle paths no longer reach the game-facing <c>Abort</c> when nothing is running (the
/// service-level half, reproduced with a counting fake through the REAL module/host/venue-switch chain), a real run is
/// still aborted, and the pure Escape rule refuses to send Escape while the character is in the world. What it cannot
/// prove: that the native game no longer opens the menu — that needs FFXIV live QA.</summary>
public sealed class SystemMenuEscapeTests
{
    private sealed class CountingAutomation : IShoutRunnerAutomation
    {
        public int AbortCount;
        public void ResetForVenue() { }
        public void Abort() => AbortCount++;
        public Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token) => Task.FromResult(ShoutRunnerReadinessOutcome.ReadyNow());
        public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token) => Task.FromResult(ShoutRunnerCrossDataCenterCheck.SameDataCenter);
        public Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token) => Task.FromResult(ShoutRunnerTransferOutcome.Success());
        public Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token) => Task.FromResult(ShoutRunnerTeleportOutcome.Success());
    }

    /// <summary>The real module registered with the real <see cref="ModuleHost"/> and <see cref="VenueProfileService"/>
    /// — the same chain <c>Plugin.cs</c> wires — around a service whose automation counts Abort calls.</summary>
    private sealed class Rig
    {
        public readonly CountingAutomation Automation = new();
        public readonly ModuleHost Host = new();
        public readonly VenueProfileService Profiles;
        public readonly ShoutRunnerService Service;
        public readonly ShoutRunnerModule Module;
        public readonly TestClock Clock = new();

        public Rig()
        {
            Profiles = new VenueProfileService(new InMemoryVenueStore(), Host);
            Service = new ShoutRunnerService(Automation, new ChatCommandService(Clock, new InlineFrameworkDispatcher(), _ => true, TimeSpan.Zero), Profiles, new DiagnosticsService(Host, Profiles, Clock), Clock, new CapturingRecoveryStore());
            Module = new ShoutRunnerModule(Service);
            Host.Register(Module);
        }

        public void ConfigureRunnable()
        {
            while (Service.Settings.Destinations.Count > 0) Service.RemoveDestinationAt(0);
            Service.AddDestination("A");
            Service.SetDataCenterSelected("Aether", true);
            Service.UpdateShoutMessage("hello");
        }
    }

    [Fact] public async Task Startup_venue_activation_does_not_reach_the_game_automation()
    {
        var rig = new Rig();

        await rig.Profiles.InitializeAsync();

        Assert.Equal(0, rig.Automation.AbortCount);
    }

    [Fact] public async Task Plugin_reload_dispose_does_not_reach_the_game_automation_when_nothing_is_running()
    {
        var rig = new Rig();
        await rig.Profiles.InitializeAsync();

        await rig.Host.DisposeAsync(); // exactly what Plugin.Dispose() runs on /xlreload

        Assert.Equal(0, rig.Automation.AbortCount);
    }

    [Fact] public async Task Switching_venue_through_the_real_switch_path_does_not_reach_the_game_automation()
    {
        var rig = new Rig();
        await rig.Profiles.InitializeAsync();
        var other = rig.Profiles.Create("Other venue");

        for (var i = 0; i < 4; i++)
        {
            Assert.True((await rig.Profiles.SwitchAsync(other.Id)).Success);
            Assert.True((await rig.Profiles.SwitchAsync(rig.Profiles.Profiles.First(v => v.Id != other.Id).Id)).Success);
        }

        Assert.Equal(0, rig.Automation.AbortCount);
    }

    [Fact] public async Task Deleting_the_active_venue_which_switches_away_does_not_reach_the_game_automation()
    {
        var rig = new Rig();
        await rig.Profiles.InitializeAsync();
        rig.Profiles.Create("Second venue");

        Assert.True((await rig.Profiles.DeleteAsync(rig.Profiles.Current.Id, confirmed: true)).Success);

        Assert.Equal(0, rig.Automation.AbortCount);
    }

    [Fact] public void Disabling_and_re_enabling_the_module_does_not_reach_the_game_automation_when_nothing_is_running()
    {
        var rig = new Rig();

        rig.Module.IsEnabled = false;
        rig.Module.IsEnabled = true;
        rig.Module.IsEnabled = false;

        Assert.Equal(0, rig.Automation.AbortCount);
    }

    [Fact] public void A_run_that_reached_Stopped_or_Faulted_never_triggers_a_later_Abort_from_HardStop()
    {
        var rig = new Rig();
        rig.ConfigureRunnable();
        Assert.Equal(ShoutRunnerStartResult.Started, rig.Service.Start());
        rig.Service.HardStop(); // aborts the live run: one legitimate Abort
        Assert.Equal(1, rig.Automation.AbortCount);

        rig.Service.HardStop();
        rig.Service.Load(rig.Profiles.Current.Id);

        Assert.Equal(1, rig.Automation.AbortCount); // the idle repeats add none
    }

    [Fact] public void A_run_in_progress_is_still_aborted_by_HardStop_and_by_module_disable()
    {
        var rig = new Rig();
        rig.ConfigureRunnable();
        Assert.Equal(ShoutRunnerStartResult.Started, rig.Service.Start());
        Assert.True(rig.Service.IsActive);

        rig.Module.IsEnabled = false;

        Assert.Equal(1, rig.Automation.AbortCount);
        Assert.Equal(ShoutRunnerState.Stopped, rig.Service.State);
    }

    [Fact] public void A_run_in_progress_is_still_aborted_by_the_operator_Stop()
    {
        var rig = new Rig();
        rig.ConfigureRunnable();
        Assert.Equal(ShoutRunnerStartResult.Started, rig.Service.Start());

        rig.Service.Stop();

        Assert.Equal(1, rig.Automation.AbortCount);
    }

    [Fact] public async Task Switching_venue_while_a_run_is_in_progress_still_hard_stops_it_and_aborts_the_automation()
    {
        var rig = new Rig();
        await rig.Profiles.InitializeAsync();
        rig.ConfigureRunnable();
        var other = rig.Profiles.Create("Other venue");
        Assert.Equal(ShoutRunnerStartResult.Started, rig.Service.Start());

        Assert.True((await rig.Profiles.SwitchAsync(other.Id)).Success);

        Assert.Equal(ShoutRunnerState.Stopped, rig.Service.State);
        Assert.Equal(1, rig.Automation.AbortCount);
    }

    // ----- The pure Escape rule -----

    [Theory]
    [InlineData(true, true, false)]   // in the world: Escape would open the System Menu — never send
    [InlineData(false, false, true)]  // title screen / character select: the case dismissal exists for
    [InlineData(false, true, true)]   // logged out but a stale local player: still not in the world
    [InlineData(true, false, true)]   // logged in but no local player yet (zoning/login): not controllable
    public void Escape_is_only_permitted_when_the_character_is_not_in_the_world(bool isLoggedIn, bool hasLocalPlayer, bool expected) =>
        Assert.Equal(expected, ShoutRunnerEscapePolicy.ShouldSendEscape(isLoggedIn, hasLocalPlayer));

    // ----- Source guard: no other synthetic key path may appear unnoticed -----

    [Fact] public void The_only_synthetic_game_keypress_in_the_plugin_is_the_policy_gated_ShoutRunner_Escape()
    {
        var pluginRoot = FindPluginSourceRoot();
        var offenders = new List<string>();
        var gatedSites = 0;

        foreach (var file in Directory.EnumerateFiles(pluginRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///")) continue;
                var sendsKey = line.Contains("SendKeypress(") || line.Contains("SendInput(") || line.Contains("keybd_event(") || line.Contains("PostMessage(") || line.Contains("SendMessage(");
                if (!sendsKey) continue;

                var previous = i > 0 ? lines[i - 1] : string.Empty;
                if (previous.Contains("ShoutRunnerEscapePolicy.ShouldSendEscape(")) gatedSites++;
                else offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {trimmed}");
            }
        }

        Assert.Empty(offenders);
        Assert.Equal(1, gatedSites);
    }

    private static string FindPluginSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VenueOS.Plugin");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate src/VenueOS.Plugin from the test output directory.");
    }
}
