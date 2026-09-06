using VenueOS.Modules.Operations.PartyFinder;

namespace VenueOS.Services.Tests;

public sealed class PartyFinderModelTests
{
    [Theory]
    [InlineData(PartyFinderCategory.None, "None")]
    [InlineData(PartyFinderCategory.Dungeons, "Dungeons")]
    [InlineData(PartyFinderCategory.VCDungeonFinder, "Variant/Criterion")]
    public void Category_names_match_donor_labels(PartyFinderCategory category, string expected) => Assert.Equal(expected, PartyFinderPreset.GetCategoryName(category));

    [Fact] public void Every_category_has_a_distinct_name()
    {
        var names = Enum.GetValues<PartyFinderCategory>().Select(PartyFinderPreset.GetCategoryName).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Theory]
    [InlineData(PartyFinderObjective.Practice, "Practice")]
    [InlineData(PartyFinderObjective.DutyCompletion, "Duty completion")]
    public void Objective_names_match_donor_labels(PartyFinderObjective objective, string expected) => Assert.Equal(expected, PartyFinderPreset.GetObjectiveName(objective));

    [Theory]
    [InlineData(8, 1, 8)]
    [InlineData(8, 6, 48)]
    [InlineData(1, 1, 1)]
    [InlineData(20, 10, 48)] // clamped to the 48-slot native array bound
    public void Effective_slot_count_is_slots_times_groups_clamped_to_48(byte slots, byte groups, int expected)
    {
        var preset = PartyFinderPreset.CreateDefault() with { NumberOfSlotsInMainParty = slots, NumberOfGroups = groups };
        Assert.Equal(expected, preset.EffectiveSlotCount);
    }

    [Fact] public void New_preset_defaults_every_slot_to_any_job()
    {
        var preset = PartyFinderPreset.CreateDefault();
        for (var i = 0; i < 48; i++) Assert.True(preset.GetSlot(i).IsAnyJob);
    }

    [Fact] public void Restricting_a_slot_to_tank_only_allows_tank_jobs()
    {
        var tanks = PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Tank);
        var slot = new PartyFinderSlot(tanks);
        Assert.True(slot.Allows(PartyFinderJob.Paladin));
        Assert.False(slot.Allows(PartyFinderJob.WhiteMage));
        Assert.False(slot.IsAnyJob);
    }

    [Fact] public void Set_all_slots_to_any_job_clears_every_restriction()
    {
        var preset = PartyFinderPreset.CreateDefault().WithSlot(0, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Healer)));
        Assert.False(preset.GetSlot(0).IsAnyJob);
        var reset = preset.WithAllSlots(PartyFinderSlot.AnyJob);
        for (var i = 0; i < 48; i++) Assert.True(reset.GetSlot(i).IsAnyJob);
    }

    [Theory]
    [InlineData(PartyFinderRole.Tank, 4)]
    [InlineData(PartyFinderRole.Healer, 4)]
    [InlineData(PartyFinderRole.Melee, 6)]
    [InlineData(PartyFinderRole.PhysicalRanged, 3)]
    [InlineData(PartyFinderRole.MagicalRanged, 4)]
    public void Quick_role_masks_contain_the_expected_job_count(PartyFinderRole role, int expectedCount) => Assert.Equal(expectedCount, PartyFinderJobCatalog.JobsInRole(role).Count);

    [Fact] public void Job_catalog_has_all_22_current_jobs_with_unique_abbreviations()
    {
        Assert.Equal(22, PartyFinderJobCatalog.Jobs.Count);
        Assert.Equal(22, PartyFinderJobCatalog.Jobs.Select(j => j.Abbreviation).Distinct().Count());
    }

    [Fact] public void Only_setting_a_single_slot_leaves_the_others_untouched()
    {
        var preset = PartyFinderPreset.CreateDefault().WithSlot(5, new PartyFinderSlot([PartyFinderJob.Paladin]));
        Assert.False(preset.GetSlot(5).IsAnyJob);
        Assert.True(preset.GetSlot(4).IsAnyJob);
        Assert.True(preset.GetSlot(6).IsAnyJob);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(9999, 9999)]
    [InlineData(20000, 9999)]
    public void Password_clamps_to_0_9999(int input, int expected) => Assert.Equal((ushort)expected, PartyFinderPreset.ClampPassword(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(500, 500)]
    [InlineData(5000, 999)]
    public void Average_item_level_clamps_to_1_999(int input, int expected) => Assert.Equal((ushort)expected, PartyFinderPreset.ClampAverageItemLevel(input));

    [Fact] public void Comment_byte_count_uses_utf8_not_character_count()
    {
        // Each of these three characters is a 3-byte UTF-8 sequence — 1 character, 3 bytes.
        Assert.Equal(3, PartyFinderPreset.CommentByteCount("★"));
        Assert.Equal(9, PartyFinderPreset.CommentByteCount("★★★"));
    }

    [Fact] public void Truncate_comment_stops_at_191_bytes_never_splitting_a_multibyte_character()
    {
        var longComment = new string('★', 100); // 300 bytes
        var truncated = PartyFinderPreset.TruncateCommentUtf8(longComment);
        Assert.True(PartyFinderPreset.CommentByteCount(truncated) <= 191);
        // A correct truncation drops whole characters only — the round-tripped string must not contain a partial
        // multi-byte sequence, which would corrupt on re-encoding.
        Assert.Equal(truncated, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(truncated)));
    }

    [Fact] public void Truncate_comment_leaves_a_short_comment_untouched()
    {
        Assert.Equal("Venue open.", PartyFinderPreset.TruncateCommentUtf8("Venue open."));
    }
}

public sealed class PartyFinderChatEventsTests
{
    [Theory]
    [InlineData("The recruitment for your party will close in five minutes.", true)]
    [InlineData("Your Party Finder recruitment closes in 5 minutes.", true)]
    [InlineData("A monster draws near.", false)]
    public void Detects_the_five_minute_warning(string text, bool expected) => Assert.Equal(expected, PartyFinderChatEvents.IsFiveMinuteWarning(text, string.Empty));

    [Fact] public void Warning_override_matches_regardless_of_the_built_in_phrase()
    {
        Assert.True(PartyFinderChatEvents.IsFiveMinuteWarning("custom server message", "custom server message"));
        Assert.False(PartyFinderChatEvents.IsFiveMinuteWarning("unrelated text", "custom server message"));
    }

    [Theory]
    [InlineData("Your party recruitment has ended.", true)]
    [InlineData("The party recruitment has ended", true)]
    [InlineData("Nothing relevant happened.", false)]
    public void Detects_listing_ended_messages(string text, bool expected) => Assert.Equal(expected, PartyFinderChatEvents.IsListingEndedMessage(text));

    [Fact] public void Throttle_blocks_refresh_within_four_minutes()
    {
        var last = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(PartyFinderChatEvents.ShouldThrottleRefresh(last, last.AddMinutes(3)));
        Assert.False(PartyFinderChatEvents.ShouldThrottleRefresh(last, last.AddMinutes(4).AddSeconds(1)));
    }
}
