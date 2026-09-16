using VenueOS.Core;

namespace VenueOS.Core.Tests;

public sealed class LauncherEntriesTests
{
    // ---- FullOrder ------------------------------------------------------------------------------------------------

    [Fact] public void With_no_recorded_order_full_order_falls_back_to_display_order()
    {
        var order = LauncherEntries.FullOrder(["core.attendance", "games.bingo", "games.raffle"], null);
        Assert.Equal(["core.attendance", "games.bingo", "games.raffle"], order);
    }

    [Fact] public void An_explicit_module_order_wins_over_display_order()
    {
        var order = LauncherEntries.FullOrder(["core.attendance", "games.bingo", "games.raffle"], ["games.raffle", "core.attendance", "games.bingo"]);
        Assert.Equal(["games.raffle", "core.attendance", "games.bingo"], order);
    }

    [Fact] public void A_module_with_no_recorded_preference_lands_at_its_natural_display_order_position()
    {
        // "games.bingo" isn't in moduleOrder — it must not be pushed to the very end, just placed after whatever the
        // recorded order already covers.
        var order = LauncherEntries.FullOrder(["core.attendance", "games.bingo", "games.raffle"], ["games.raffle"]);
        Assert.Equal(["games.raffle", "core.attendance", "games.bingo"], order);
    }

    [Fact] public void A_stale_id_no_longer_in_the_module_list_is_silently_skipped()
    {
        var order = LauncherEntries.FullOrder(["core.attendance", "games.bingo"], ["games.removed", "games.bingo", "core.attendance"]);
        Assert.Equal(["games.bingo", "core.attendance"], order);
    }

    [Fact] public void A_duplicate_id_in_module_order_is_only_placed_once()
    {
        var order = LauncherEntries.FullOrder(["core.attendance", "games.bingo"], ["games.bingo", "games.bingo"]);
        Assert.Equal(["games.bingo", "core.attendance"], order);
    }

    // ---- Eligible -------------------------------------------------------------------------------------------------

    [Fact] public void A_fresh_configuration_shows_every_enabled_module_by_default()
    {
        var entries = LauncherEntries.Eligible([new("core.attendance", true), new("games.bingo", true)], null, null);
        Assert.Equal(["core.attendance", "games.bingo"], entries);
    }

    [Fact] public void A_disabled_module_is_never_eligible_regardless_of_show_on_launcher()
    {
        var entries = LauncherEntries.Eligible(
            [new("core.attendance", false)],
            new Dictionary<string, bool> { ["core.attendance"] = true },
            null);
        Assert.Empty(entries);
    }

    [Fact] public void An_explicit_false_hides_an_enabled_module()
    {
        var entries = LauncherEntries.Eligible(
            [new("core.attendance", true), new("games.bingo", true)],
            new Dictionary<string, bool> { ["games.bingo"] = false },
            null);
        Assert.Equal(["core.attendance"], entries);
    }

    [Fact] public void An_explicit_true_keeps_a_module_shown()
    {
        var entries = LauncherEntries.Eligible(
            [new("core.attendance", true)],
            new Dictionary<string, bool> { ["core.attendance"] = true },
            null);
        Assert.Equal(["core.attendance"], entries);
    }

    [Fact] public void No_entry_means_shown_the_default_on_opt_out_contract()
    {
        var entries = LauncherEntries.Eligible([new("games.raffle", true)], new Dictionary<string, bool>(), null);
        Assert.Equal(["games.raffle"], entries);
    }

    [Fact] public void Eligible_respects_the_recorded_module_order()
    {
        var entries = LauncherEntries.Eligible(
            [new("core.attendance", true), new("games.bingo", true)],
            null,
            ["games.bingo", "core.attendance"]);
        Assert.Equal(["games.bingo", "core.attendance"], entries);
    }

    [Fact] public void A_disabled_module_disappears_from_eligible_entries_but_stale_show_preference_is_harmless()
    {
        // Simulates disable → re-enable: the show preference for the disabled module id is simply never consulted
        // while disabled (it isn't in the enabled set), and reappears unchanged once re-enabled.
        var disabled = LauncherEntries.Eligible(
            [new("games.raffle", false), new("core.attendance", true)],
            new Dictionary<string, bool> { ["games.raffle"] = false },
            null);
        Assert.Equal(["core.attendance"], disabled);

        var reenabled = LauncherEntries.Eligible(
            [new("games.raffle", true), new("core.attendance", true)],
            new Dictionary<string, bool> { ["games.raffle"] = false },
            null);
        Assert.Equal(["core.attendance"], reenabled); // still explicitly hidden — the preference survived re-enable
    }
}
