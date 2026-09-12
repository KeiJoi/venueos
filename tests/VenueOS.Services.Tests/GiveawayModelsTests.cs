using VenueOS.Modules.Operations.Giveaways;

namespace VenueOS.Services.Tests;

/// <summary>Pure model/validation tests: <see cref="GiveawaySpecialNumbers"/>, <see cref="GiveawayPresetValidator"/>,
/// <see cref="GiveawayAnnouncementBlock"/>, and <see cref="GiveawayPreset.HasMidpointOverlapRisk"/> (GIVEAWAYS spec
/// §6/§10/§11/§26/§29).</summary>
public sealed class GiveawayModelsTests
{
    [Fact]
    public void Special_numbers_parse_a_comma_separated_list()
    {
        Assert.Equal([69, 420, 777], GiveawaySpecialNumbers.Parse("69,420,777"));
    }

    [Fact]
    public void Special_numbers_tolerate_whitespace_blanks_and_duplicates()
    {
        Assert.Equal([69, 420], GiveawaySpecialNumbers.Parse(" 69 , , 420, sixty-nine, 69"));
    }

    [Fact]
    public void Special_numbers_of_an_empty_string_is_an_empty_list()
    {
        Assert.Empty(GiveawaySpecialNumbers.Parse(""));
        Assert.Empty(GiveawaySpecialNumbers.Parse(null));
    }

    [Fact]
    public void New_preset_has_sensible_defaults()
    {
        var preset = GiveawayPreset.CreateNew("Friday Night 1M Giveaway");
        Assert.Equal("Friday Night 1M Giveaway", preset.Name);
        Assert.Equal(GiveawayChatChannel.Shout, preset.Channel);
        Assert.Equal(1, preset.AllowedRollsPerPerson);
        Assert.Equal(GiveawayWinnerMode.Highest, preset.WinnerMode);
        Assert.Empty(preset.StartBlock.Lines);
        Assert.Empty(preset.MidpointBlock.Lines);
        Assert.Empty(preset.ClosingBlock.Lines);
        Assert.Equal(GiveawayChatChannel.Yell, preset.WinnerAnnouncementChannel);
        Assert.Equal("Congratulations <name>! You won the giveaway!", preset.WinnerAnnouncementTemplate);
    }

    [Fact]
    public void Blank_lines_are_excluded_from_non_empty_lines()
    {
        var block = new GiveawayAnnouncementBlock(["Line one", "", "   ", "Line two"]);
        Assert.Equal(["Line one", "Line two"], block.NonEmptyLines);
    }

    [Fact]
    public void Validator_rejects_a_preset_with_no_selected_name()
    {
        var preset = GiveawayPreset.CreateNew("Valid") with { Name = "" };
        Assert.Contains(GiveawayPresetValidator.Validate(preset), e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_delay_below_one_second()
    {
        var preset = GiveawayPreset.CreateNew("Valid") with { DelayBetweenLinesSeconds = 0 };
        Assert.Contains(GiveawayPresetValidator.Validate(preset), e => e.Contains("Delay", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duration_below_two_seconds()
    {
        var preset = GiveawayPreset.CreateNew("Valid") with { GiveawayDurationSeconds = 1 };
        Assert.Contains(GiveawayPresetValidator.Validate(preset), e => e.Contains("Duration", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_negative_allowed_rolls()
    {
        var preset = GiveawayPreset.CreateNew("Valid") with { AllowedRollsPerPerson = -1 };
        Assert.Contains(GiveawayPresetValidator.Validate(preset), e => e.Contains("Allowed Rolls", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_accepts_a_well_formed_preset()
    {
        var preset = GiveawayPreset.CreateNew("Valid");
        Assert.Empty(GiveawayPresetValidator.Validate(preset));
    }

    [Fact]
    public void Overlap_risk_is_flagged_when_midpoint_would_still_be_sending_when_closing_is_due()
    {
        // 4 lines at 5s delay = 15s to finish sending; half of a 20s duration is only 10s — Closing would be due
        // before Midpoint finishes (GIVEAWAYS spec §10).
        var preset = GiveawayPreset.CreateNew("Risky") with
        {
            DelayBetweenLinesSeconds = 5,
            GiveawayDurationSeconds = 20,
            MidpointBlock = new GiveawayAnnouncementBlock(["1", "2", "3", "4"]),
        };
        Assert.True(preset.HasMidpointOverlapRisk);
    }

    [Fact]
    public void Overlap_risk_is_not_flagged_for_a_short_midpoint_block()
    {
        var preset = GiveawayPreset.CreateNew("Safe") with
        {
            DelayBetweenLinesSeconds = 2,
            GiveawayDurationSeconds = 60,
            MidpointBlock = new GiveawayAnnouncementBlock(["Only one line"]),
        };
        Assert.False(preset.HasMidpointOverlapRisk);
    }
}
