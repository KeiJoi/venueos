namespace VenueOS.Modules.Operations.PartyFinder;

// VenueOS-owned mirrors of the donor's AgentLookingForGroup-adjacent enums (venuepartyfinder, Models/PartyFinderPreset.cs
// and Services/PartyFinderAutomation.cs). Kept as plain enums with no FFXIVClientStructs/Dalamud dependency so this
// project stays ImGui/Dalamud-free and testable; the automation boundary (VenueOS.Plugin, which does reference those
// libraries) maps every value here to its real native counterpart by name, never by assumed numeric identity.
public enum PartyFinderCategory
{
    None, Roulette, Dungeons, GuildQuests, Trials, Raids, HighEndDuty, PvP, GoldSaucer, FATEs,
    TreasureHunts, TheHunt, GatheringForays, DeepDungeons, FieldOperations, VCDungeonFinder,
}

public enum PartyFinderObjective { None, DutyCompletion, Practice, Loot }

public enum PartyFinderCompletionStatus { None, DutyComplete, DutyIncomplete, DutyCompleteWeeklyUnclaimed }

[Flags]
public enum PartyFinderDutyFinderSetting
{
    None = 0,
    UnrestrictedParty = 1 << 0,
    MinimumIL = 1 << 1,
    SilenceEcho = 1 << 2,
}

public enum PartyFinderLootRule { Normal, GreedOnly, Lootmaster }

[Flags]
public enum PartyFinderLanguage
{
    None = 0,
    Japanese = 1 << 0,
    English = 1 << 1,
    German = 1 << 2,
    French = 1 << 3,
}

public enum PartyFinderJob
{
    Paladin, Warrior, DarkKnight, Gunbreaker,
    WhiteMage, Scholar, Astrologian, Sage,
    Monk, Dragoon, Ninja, Samurai, Reaper, Viper,
    Bard, Machinist, Dancer,
    BlackMage, Summoner, RedMage, Pictomancer,
    BlueMage,
}

public enum PartyFinderRole { Tank, Healer, Melee, PhysicalRanged, MagicalRanged, Limited }

public sealed record PartyFinderJobOption(PartyFinderJob Job, string Abbreviation, string Name, PartyFinderRole Role);

/// <summary>VenueOS mirror of the donor's <c>JobCatalog</c> (venuepartyfinder, Models/JobCatalog.cs) — same 22 jobs,
/// same abbreviations, same role groupings, same order. The real per-job bit flag (<c>Dalamud.Game.Gui.PartyFinder.Types.JobFlags</c>)
/// is looked up only at the native automation boundary (see <c>PartyFinderNativeMapping</c> in VenueOS.Plugin), never
/// re-derived here, so this catalog carries no risk of a mismatched bit value.</summary>
public static class PartyFinderJobCatalog
{
    public static readonly IReadOnlyList<PartyFinderJobOption> Jobs =
    [
        new(PartyFinderJob.Paladin, "PLD", "Paladin", PartyFinderRole.Tank),
        new(PartyFinderJob.Warrior, "WAR", "Warrior", PartyFinderRole.Tank),
        new(PartyFinderJob.DarkKnight, "DRK", "Dark Knight", PartyFinderRole.Tank),
        new(PartyFinderJob.Gunbreaker, "GNB", "Gunbreaker", PartyFinderRole.Tank),
        new(PartyFinderJob.WhiteMage, "WHM", "White Mage", PartyFinderRole.Healer),
        new(PartyFinderJob.Scholar, "SCH", "Scholar", PartyFinderRole.Healer),
        new(PartyFinderJob.Astrologian, "AST", "Astrologian", PartyFinderRole.Healer),
        new(PartyFinderJob.Sage, "SGE", "Sage", PartyFinderRole.Healer),
        new(PartyFinderJob.Monk, "MNK", "Monk", PartyFinderRole.Melee),
        new(PartyFinderJob.Dragoon, "DRG", "Dragoon", PartyFinderRole.Melee),
        new(PartyFinderJob.Ninja, "NIN", "Ninja", PartyFinderRole.Melee),
        new(PartyFinderJob.Samurai, "SAM", "Samurai", PartyFinderRole.Melee),
        new(PartyFinderJob.Reaper, "RPR", "Reaper", PartyFinderRole.Melee),
        new(PartyFinderJob.Viper, "VPR", "Viper", PartyFinderRole.Melee),
        new(PartyFinderJob.Bard, "BRD", "Bard", PartyFinderRole.PhysicalRanged),
        new(PartyFinderJob.Machinist, "MCH", "Machinist", PartyFinderRole.PhysicalRanged),
        new(PartyFinderJob.Dancer, "DNC", "Dancer", PartyFinderRole.PhysicalRanged),
        new(PartyFinderJob.BlackMage, "BLM", "Black Mage", PartyFinderRole.MagicalRanged),
        new(PartyFinderJob.Summoner, "SMN", "Summoner", PartyFinderRole.MagicalRanged),
        new(PartyFinderJob.RedMage, "RDM", "Red Mage", PartyFinderRole.MagicalRanged),
        new(PartyFinderJob.Pictomancer, "PCT", "Pictomancer", PartyFinderRole.MagicalRanged),
        new(PartyFinderJob.BlueMage, "BLU", "Blue Mage", PartyFinderRole.Limited),
    ];

    public static IReadOnlyList<PartyFinderJob> AllJobs { get; } = Jobs.Select(j => j.Job).ToArray();
    public static IReadOnlyList<PartyFinderJob> JobsInRole(PartyFinderRole role) => Jobs.Where(j => j.Role == role).Select(j => j.Job).ToArray();
    public static string Abbreviation(PartyFinderJob job) => Jobs.First(j => j.Job == job).Abbreviation;
    public static string Name(PartyFinderJob job) => Jobs.First(j => j.Job == job).Name;
}

/// <summary>A per-slot role restriction. An empty <see cref="Jobs"/> list means "any job" — the donor's own sentinel
/// convention (<c>PartyFinderPreset.GetSlotMask</c>: a zero mask always reads back as "all jobs"), reproduced here
/// without needing the raw bitmask at this layer.</summary>
public sealed record PartyFinderSlot(IReadOnlyList<PartyFinderJob> Jobs)
{
    public static PartyFinderSlot AnyJob { get; } = new([]);
    public bool IsAnyJob => Jobs.Count == 0 || Jobs.Count == PartyFinderJobCatalog.AllJobs.Count;
    public bool Allows(PartyFinderJob job) => IsAnyJob || Jobs.Contains(job);

    public static string Describe(PartyFinderSlot slot) => slot.IsAnyJob
        ? "Any job"
        : string.Join(", ", slot.Jobs.Select(PartyFinderJobCatalog.Abbreviation));
}

/// <summary>VenueOS-owned mirror of the donor's <c>PartyFinderPreset</c> (venuepartyfinder, Models/PartyFinderPreset.cs).
/// Field-for-field parity with the donor except representing per-slot role restrictions as <see cref="PartyFinderSlot"/>
/// instead of a raw <c>ulong</c> bitmask (see <see cref="PartyFinderJobCatalog"/>'s doc comment for why).</summary>
public sealed record PartyFinderPreset
{
    public PartyFinderCategory Category { get; init; } = PartyFinderCategory.None;
    public ushort DutyId { get; init; }
    public PartyFinderObjective Objective { get; init; } = PartyFinderObjective.Practice;
    public bool BeginnerFriendly { get; init; }
    public PartyFinderCompletionStatus CompletionStatus { get; init; } = PartyFinderCompletionStatus.None;
    public bool AverageItemLevelEnabled { get; init; }
    public ushort AverageItemLevel { get; init; } = 1;
    public PartyFinderDutyFinderSetting DutyFinderSettings { get; init; } = PartyFinderDutyFinderSetting.None;
    public PartyFinderLootRule LootRule { get; init; } = PartyFinderLootRule.Normal;
    public bool PrivateParty { get; init; }
    public ushort Password { get; init; }
    public PartyFinderLanguage Languages { get; init; } = PartyFinderLanguage.English;
    public byte NumberOfSlotsInMainParty { get; init; } = 8;
    public bool LimitRecruitingToWorld { get; init; }
    public bool OnePlayerPerJob { get; init; }
    public byte NumberOfGroups { get; init; } = 1;
    public string Comment { get; init; } = "Venue open. Please read the description before joining.";
    public IReadOnlyList<PartyFinderSlot> Slots { get; init; } = CreateDefaultSlots();

    /// <summary>Donor: <c>PartyFinderPreset.EffectiveSlotCount</c> — <c>slots × groups</c>, clamped to the 48-slot
    /// native array bound.</summary>
    public int EffectiveSlotCount => Math.Clamp((int)NumberOfSlotsInMainParty * Math.Max(1, (int)NumberOfGroups), 1, 48);

    public static PartyFinderPreset CreateDefault() => new();

    public PartyFinderSlot GetSlot(int index) => index >= 0 && index < Slots.Count ? Slots[index] : PartyFinderSlot.AnyJob;

    public PartyFinderPreset WithSlot(int index, PartyFinderSlot slot)
    {
        if (index < 0 || index >= 48) return this;
        var next = new PartyFinderSlot[48];
        for (var i = 0; i < 48; i++) next[i] = GetSlot(i);
        next[index] = slot;
        return this with { Slots = next };
    }

    public PartyFinderPreset WithAllSlots(PartyFinderSlot slot)
    {
        var next = new PartyFinderSlot[48];
        Array.Fill(next, slot);
        return this with { Slots = next };
    }

    private static IReadOnlyList<PartyFinderSlot> CreateDefaultSlots()
    {
        var slots = new PartyFinderSlot[48];
        Array.Fill(slots, PartyFinderSlot.AnyJob);
        return slots;
    }

    /// <summary>Donor: <c>PartyFinderAutomation.TruncateComment</c> — the native comment buffer is 192 bytes; the
    /// donor truncates to 191 to leave a byte of headroom. UTF-8 byte length, not character count, matching how the
    /// donor's own live counter (<c>MainWindow.cs</c>: "{byteCount}/192 bytes") is computed.</summary>
    public static string TruncateCommentUtf8(string input, int maxBytes = 191)
    {
        var value = input;
        while (System.Text.Encoding.UTF8.GetByteCount(value) > maxBytes && value.Length > 0)
        {
            value = value[..^1];
        }

        return value;
    }

    public static int CommentByteCount(string input) => System.Text.Encoding.UTF8.GetByteCount(input);

    /// <summary>Donor: <c>MainWindow.cs</c>'s private party password field — clamped 0-9999.</summary>
    public static ushort ClampPassword(int value) => (ushort)Math.Clamp(value, 0, 9999);

    /// <summary>Donor: <c>MainWindow.cs</c>'s average item level field — clamped 1-999.</summary>
    public static ushort ClampAverageItemLevel(int value) => (ushort)Math.Clamp(value, 1, 999);

    public static string GetCategoryName(PartyFinderCategory category) => category switch
    {
        PartyFinderCategory.None => "None",
        PartyFinderCategory.Roulette => "Duty Roulette",
        PartyFinderCategory.Dungeons => "Dungeons",
        PartyFinderCategory.GuildQuests => "Guildhests",
        PartyFinderCategory.Trials => "Trials",
        PartyFinderCategory.Raids => "Raids",
        PartyFinderCategory.HighEndDuty => "High-end Duty",
        PartyFinderCategory.PvP => "PvP",
        PartyFinderCategory.GoldSaucer => "Gold Saucer",
        PartyFinderCategory.FATEs => "FATEs",
        PartyFinderCategory.TreasureHunts => "Treasure Hunts",
        PartyFinderCategory.TheHunt => "The Hunt",
        PartyFinderCategory.GatheringForays => "Gathering Forays",
        PartyFinderCategory.DeepDungeons => "Deep Dungeons",
        PartyFinderCategory.FieldOperations => "Field Operations",
        PartyFinderCategory.VCDungeonFinder => "Variant/Criterion",
        _ => category.ToString(),
    };

    public static string GetObjectiveName(PartyFinderObjective objective) => objective switch
    {
        PartyFinderObjective.None => "None",
        PartyFinderObjective.DutyCompletion => "Duty completion",
        PartyFinderObjective.Practice => "Practice",
        PartyFinderObjective.Loot => "Loot",
        _ => objective.ToString(),
    };

    public static string GetCompletionStatusName(PartyFinderCompletionStatus status) => status switch
    {
        PartyFinderCompletionStatus.None => "None",
        PartyFinderCompletionStatus.DutyComplete => "Duty complete",
        PartyFinderCompletionStatus.DutyIncomplete => "Duty incomplete",
        PartyFinderCompletionStatus.DutyCompleteWeeklyUnclaimed => "Weekly reward unclaimed",
        _ => status.ToString(),
    };

    public static string GetLootRuleName(PartyFinderLootRule rule) => rule switch
    {
        PartyFinderLootRule.Normal => "Normal",
        PartyFinderLootRule.GreedOnly => "Greed only",
        PartyFinderLootRule.Lootmaster => "Lootmaster",
        _ => rule.ToString(),
    };
}

/// <summary>The full per-venue Party Finder payload — the donor's preset plus its automation-behavior settings
/// (donor: <c>PluginConfiguration</c>, venuepartyfinder/Configuration.cs), persisted through
/// <c>VenueProfileService.GetModuleConfig</c>/<c>SaveModuleConfig</c> instead of the donor's own
/// <c>Service.PluginInterface.SavePluginConfig</c> call. <see cref="LastRefreshAttemptUtc"/> is per-venue so the
/// 4-minute auto-refresh throttle in one venue can never be affected by another venue's refresh history.</summary>
public sealed record PartyFinderSettings
{
    public PartyFinderPreset Preset { get; init; } = PartyFinderPreset.CreateDefault();
    public bool AutoRefreshEnabled { get; init; } = true;
    public string WarningMessageOverride { get; init; } = string.Empty;
    public DateTime LastRefreshAttemptUtc { get; init; } = DateTime.MinValue;

    public static PartyFinderSettings CreateDefault() => new();
}
