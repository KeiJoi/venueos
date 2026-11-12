using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Services.Tests;

/// <summary>0.3.14 pure travel policy: confirmation matching against the game's own templates (Addon rows 12519/12624 as
/// read from the installed 7.58 game data, and the owner's confirmation screenshot "Travel to Behemoth?"), label cleaning
/// for the decorations visible in the screenshots, and the region rule.</summary>
public sealed class NativeTravelPolicyTests
{
    private static readonly IReadOnlyList<(string, string)> GameTemplates = [("Travel to ", "?"), ("Travel to ", "?")];

    [Theory]
    [InlineData("Travel to Behemoth?", "Behemoth", true)]                       // the screenshot's exact prompt
    [InlineData("Travel to Behemoth?\nPlease be advised you must be playing solo or in a cross-world party for your transfer to be processed.", "Behemoth", true)]   // row 12624
    [InlineData("  Travel   to Behemoth?  ", "Behemoth", true)]                 // whitespace only
    [InlineData("travel to behemoth?", "Behemoth", true)]                       // case
    [InlineData("Travel to Jenova?", "Cactuar", false)]                         // the incident shape
    [InlineData("Travel to Cactuar? Or Jenova?", "Cactuar", false)]             // extra text
    [InlineData("Travel to Cactuarius?", "Cactuar", false)]                     // prefix-only match
    [InlineData("Return to Ul'dah?", "Behemoth", false)]
    [InlineData("", "Behemoth", false)]
    [InlineData(null, "Behemoth", false)]
    public void Confirmation_matches_only_the_exact_template_for_the_destination(string? prompt, string destination, bool expected) =>
        Assert.Equal(expected, NativeTravelPolicy.IsConfirmationFor(prompt, destination, GameTemplates));

    [Fact]
    public void Without_game_templates_the_English_template_is_used() =>
        Assert.True(NativeTravelPolicy.IsConfirmationFor("Travel to Behemoth?", "Behemoth", []));

    [Theory]
    [InlineData("▶ Dynamis", "Dynamis")]          // dropdown marker in the screenshots
    [InlineData(" Cuchulainn", "Cuchulainn")]    // home icon before the current World
    [InlineData("  Behemoth ", "Behemoth")]
    [InlineData("Ul'dah - Steps of Nald", "Ul'dah - Steps of Nald")]
    [InlineData(" Visit Another World Server.", "Visit Another World Server.")]
    [InlineData("", "")]
    public void Labels_lose_decorations_but_keep_the_name(string raw, string expected) =>
        Assert.Equal(expected, NativeTravelPolicy.CleanLabel(raw));

    [Fact]
    public void The_World_Visit_menu_entry_is_found_by_the_game_text_including_the_screenshot_entries()
    {
        Assert.True(NativeTravelPolicy.IsWorldVisitMenuEntry("Visit Another World Server.", NativeTravelPolicy.EnglishWorldVisitOption));
        Assert.True(NativeTravelPolicy.IsWorldVisitMenuEntry(" Visit Another World Server.", " Visit Another World Server."));
        Assert.False(NativeTravelPolicy.IsWorldVisitMenuEntry("Aethernet.", NativeTravelPolicy.EnglishWorldVisitOption));
        Assert.False(NativeTravelPolicy.IsWorldVisitMenuEntry("Travel to Instanced Area.", NativeTravelPolicy.EnglishWorldVisitOption));
    }

    [Fact]
    public void World_Visit_reaches_every_logical_DC_of_the_home_region_and_nothing_else()
    {
        var c = new TestTravelWorldCatalog();
        Assert.True(NativeTravelPolicy.CanVisit(c["Cuchulainn"], c["Cuchulainn"], c["Behemoth"]));   // the screenshot case
        Assert.True(NativeTravelPolicy.CanVisit(c["Behemoth"], c["Cuchulainn"], c["Adamantoise"]));  // visited DC → third DC, no home detour
        Assert.False(NativeTravelPolicy.CanVisit(c["Cuchulainn"], c["Cuchulainn"], c["Ravana"]));    // another region
        Assert.False(NativeTravelPolicy.CanVisit(c["Ravana"], c["Cuchulainn"], c["Adamantoise"]));   // visiting another region
        Assert.False(NativeTravelPolicy.CanVisit(null, c["Cuchulainn"], c["Adamantoise"]));
    }

    [Fact]
    public void The_three_World_Visit_cities_match_the_installed_game_data()
    {
        Assert.Equal([(8u, 129u), (2u, 132u), (9u, 130u)], WorldVisitCities.All.Select(c => (c.AetheryteId, c.TerritoryId)));
        Assert.Equal(WorldVisitCities.Uldah, WorldVisitCities.ForName("Ul'dah - Steps of Nald"));
        Assert.Null(WorldVisitCities.ForName("Old Sharlayan"));
    }
}
