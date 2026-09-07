using VenueOS.Core;
using VenueOS.UI;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

/// <summary>Covers the pure navigation-state part of the User Manual launcher: <see cref="VenueShell.SelectManual"/>
/// puts the shell into a state distinct from Home/Settings/any module, and <see cref="VenueShell.IsManualSelected"/>
/// reflects it correctly. The actual on-screen tile position ("immediately before Settings") is a fact about
/// HomeScreen.DrawGrid's draw order, which needs a live ImGui context and is not unit-testable — see
/// NEW_MODULE_GUIDE.md §30; that placement was verified by direct code review instead.</summary>
public sealed class VenueShellManualNavigationTests
{
    private static VenueShell FreshShell() => new(new ModuleHost(), new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()));

    [Fact]
    public void Selecting_the_manual_is_distinct_from_home_and_settings()
    {
        var shell = FreshShell();
        shell.SelectManual();
        Assert.True(shell.IsManualSelected);
        Assert.False(shell.IsHome);
        Assert.False(shell.IsSettingsSelected);
    }

    [Fact]
    public void Selecting_settings_after_the_manual_deselects_the_manual()
    {
        var shell = FreshShell();
        shell.SelectManual();
        shell.SelectSettings();
        Assert.False(shell.IsManualSelected);
        Assert.True(shell.IsSettingsSelected);
    }

    [Fact]
    public void Selecting_home_after_the_manual_deselects_the_manual()
    {
        var shell = FreshShell();
        shell.SelectManual();
        shell.SelectHome();
        Assert.False(shell.IsManualSelected);
        Assert.True(shell.IsHome);
    }

    [Fact]
    public void The_manual_is_reachable_with_zero_modules_registered()
    {
        // The manual reader is not a module and must never depend on any module being registered/enabled.
        var shell = FreshShell();
        shell.SelectManual();
        Assert.True(shell.IsManualSelected);
    }
}
