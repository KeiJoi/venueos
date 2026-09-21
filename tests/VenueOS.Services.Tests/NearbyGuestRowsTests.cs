using System.Numerics;
using VenueOS.Modules.Operations.Attendance;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>0.3.8 maintenance: Attendance's "Guests Nearby" list draws one single-line "Name — HomeWorld" row per
/// guest. The ImGui rendering itself is not unit-testable in this architecture (no UI test harness exists, and none
/// was invented for this fix); these tests cover everything below it — the pairing, the text, and the unchanged
/// filter/sort semantics.</summary>
public sealed class NearbyGuestRowsTests
{
    private static PlayerSnapshot Player(string name, string world) => new(name, world, 1, 1, Vector3.Zero);

    [Fact]
    public void A_row_reads_as_one_name_dash_homeworld_line()
    {
        Assert.Equal("Ada Lovelace — Balmung", new NearbyGuestRow("Ada Lovelace", "Balmung", false).Text);
        Assert.DoesNotContain('\n', new NearbyGuestRow("Ada Lovelace", "Balmung", false).Text);
    }

    [Fact]
    public void A_greeted_guest_keeps_the_same_line_and_appends_the_greeted_marker()
    {
        Assert.Equal("Ada Lovelace — Balmung · Greeted", new NearbyGuestRow("Ada Lovelace", "Balmung", true).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_missing_homeworld_renders_the_bare_name_without_a_dangling_separator(string? world)
    {
        Assert.Equal("Ada", NearbyGuestRow.Compose("Ada", world, false));
        Assert.Equal("Ada · Greeted", NearbyGuestRow.Compose("Ada", world, true));
    }

    [Fact]
    public void Every_row_keeps_its_own_guests_world_across_multiple_guests_of_different_worlds()
    {
        var rows = NearbyGuestRows.Build([Player("Cara", "Zalera"), Player("Ada", "Balmung"), Player("Bob", "Gilgamesh")], null, _ => false);

        Assert.Equal(["Ada — Balmung", "Bob — Gilgamesh", "Cara — Zalera"], rows.Select(r => r.Text));
        Assert.All(rows, r => Assert.EndsWith(r.HomeWorld, r.Text));
        Assert.All(rows, r => Assert.StartsWith(r.Name, r.Text));
    }

    [Fact]
    public void Same_name_on_different_worlds_stays_two_distinct_correctly_paired_rows()
    {
        var rows = NearbyGuestRows.Build([Player("Ada", "Gilgamesh"), Player("Ada", "Balmung")], null, _ => false);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Text == "Ada — Balmung");
        Assert.Contains(rows, r => r.Text == "Ada — Gilgamesh");
    }

    [Fact]
    public void Search_filter_is_a_case_insensitive_name_contains_and_ignores_the_world()
    {
        var guests = new[] { Player("Ada Lovelace", "Balmung"), Player("Bob", "Ada-World"), Player("Cara", "Zalera") };

        Assert.Equal(["Ada Lovelace — Balmung"], NearbyGuestRows.Build(guests, "lOVE", _ => false).Select(r => r.Text));
        Assert.Equal(["Ada Lovelace — Balmung"], NearbyGuestRows.Build(guests, "ada", _ => false).Select(r => r.Text)); // Bob's world matches "ada" but search is by name only
        Assert.Equal(3, NearbyGuestRows.Build(guests, "  ", _ => false).Count);
        Assert.Empty(NearbyGuestRows.Build(guests, "zzz", _ => false));
    }

    [Fact]
    public void Ordering_is_by_name_as_before()
    {
        var rows = NearbyGuestRows.Build([Player("Mallory", "A"), Player("Alice", "B"), Player("Eve", "C")], null, _ => false);
        Assert.Equal(["Alice", "Eve", "Mallory"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Greeted_state_is_resolved_per_guest_identity_not_shared_between_rows()
    {
        var greeted = new GuestIdentity("Ada", "Balmung").Key;
        var rows = NearbyGuestRows.Build([Player("Ada", "Balmung"), Player("Ada", "Gilgamesh")], null, g => g.Key == greeted);

        Assert.Equal("Ada — Balmung · Greeted", rows.Single(r => r.HomeWorld == "Balmung").Text);
        Assert.Equal("Ada — Gilgamesh", rows.Single(r => r.HomeWorld == "Gilgamesh").Text);
    }
}
