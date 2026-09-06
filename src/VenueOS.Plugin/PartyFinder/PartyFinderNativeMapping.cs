using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using VenueOS.Modules.Operations.PartyFinder;
// Dalamud.Game.Gui.PartyFinder.Types also declares its own PartyFinderSlot (a native listing's slot info) — an
// unrelated type from VenueOS's own per-slot role-restriction model. Alias to disambiguate.
using PartyFinderSlot = VenueOS.Modules.Operations.PartyFinder.PartyFinderSlot;

namespace VenueOS.Plugin.PartyFinder;

/// <summary>Maps VenueOS's engine-agnostic Party Finder model (<c>VenueOS.Modules.Operations.PartyFinder</c>) onto
/// the real FFXIVClientStructs/Dalamud native types, by name — never by assumed numeric identity. Every case here is
/// transcribed directly from the donor's own source (venuepartyfinder, Models/JobCatalog.cs and
/// Services/PartyFinderAutomation.cs), so no enum/bit value is invented or guessed.</summary>
internal static class PartyFinderNativeMapping
{
    public static AgentLookingForGroup.DutyCategory ToNative(this PartyFinderCategory category) => category switch
    {
        PartyFinderCategory.None => AgentLookingForGroup.DutyCategory.None,
        PartyFinderCategory.Roulette => AgentLookingForGroup.DutyCategory.Roulette,
        PartyFinderCategory.Dungeons => AgentLookingForGroup.DutyCategory.Dungeons,
        PartyFinderCategory.GuildQuests => AgentLookingForGroup.DutyCategory.GuildQuests,
        PartyFinderCategory.Trials => AgentLookingForGroup.DutyCategory.Trials,
        PartyFinderCategory.Raids => AgentLookingForGroup.DutyCategory.Raids,
        PartyFinderCategory.HighEndDuty => AgentLookingForGroup.DutyCategory.HighEndDuty,
        PartyFinderCategory.PvP => AgentLookingForGroup.DutyCategory.PvP,
        PartyFinderCategory.GoldSaucer => AgentLookingForGroup.DutyCategory.GoldSaucer,
        PartyFinderCategory.FATEs => AgentLookingForGroup.DutyCategory.FATEs,
        PartyFinderCategory.TreasureHunts => AgentLookingForGroup.DutyCategory.TreasureHunts,
        PartyFinderCategory.TheHunt => AgentLookingForGroup.DutyCategory.TheHunt,
        PartyFinderCategory.GatheringForays => AgentLookingForGroup.DutyCategory.GatheringForays,
        PartyFinderCategory.DeepDungeons => AgentLookingForGroup.DutyCategory.DeepDungeons,
        PartyFinderCategory.FieldOperations => AgentLookingForGroup.DutyCategory.FieldOperations,
        PartyFinderCategory.VCDungeonFinder => AgentLookingForGroup.DutyCategory.VCDungeonFinder,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static AgentLookingForGroup.Objective ToNative(this PartyFinderObjective objective) => objective switch
    {
        PartyFinderObjective.None => AgentLookingForGroup.Objective.None,
        PartyFinderObjective.DutyCompletion => AgentLookingForGroup.Objective.DutyCompletion,
        PartyFinderObjective.Practice => AgentLookingForGroup.Objective.Practice,
        PartyFinderObjective.Loot => AgentLookingForGroup.Objective.Loot,
        _ => throw new ArgumentOutOfRangeException(nameof(objective), objective, null),
    };

    public static AgentLookingForGroup.CompletionStatus ToNative(this PartyFinderCompletionStatus status) => status switch
    {
        PartyFinderCompletionStatus.None => AgentLookingForGroup.CompletionStatus.None,
        PartyFinderCompletionStatus.DutyComplete => AgentLookingForGroup.CompletionStatus.DutyComplete,
        PartyFinderCompletionStatus.DutyIncomplete => AgentLookingForGroup.CompletionStatus.DutyIncomplete,
        PartyFinderCompletionStatus.DutyCompleteWeeklyUnclaimed => AgentLookingForGroup.CompletionStatus.DutyCompleteWeeklyUnclaimed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static AgentLookingForGroup.LootRule ToNative(this PartyFinderLootRule rule) => rule switch
    {
        PartyFinderLootRule.Normal => AgentLookingForGroup.LootRule.Normal,
        PartyFinderLootRule.GreedOnly => AgentLookingForGroup.LootRule.GreedOnly,
        PartyFinderLootRule.Lootmaster => AgentLookingForGroup.LootRule.Lootmaster,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };

    public static AgentLookingForGroup.DutyFinderSetting ToNative(this PartyFinderDutyFinderSetting flags)
    {
        var result = AgentLookingForGroup.DutyFinderSetting.None;
        if ((flags & PartyFinderDutyFinderSetting.UnrestrictedParty) != 0) result |= AgentLookingForGroup.DutyFinderSetting.UnrestrictedParty;
        if ((flags & PartyFinderDutyFinderSetting.MinimumIL) != 0) result |= AgentLookingForGroup.DutyFinderSetting.MinimumIL;
        if ((flags & PartyFinderDutyFinderSetting.SilenceEcho) != 0) result |= AgentLookingForGroup.DutyFinderSetting.SilenceEcho;
        return result;
    }

    public static AgentLookingForGroup.Language ToNative(this PartyFinderLanguage flags)
    {
        var result = default(AgentLookingForGroup.Language); // zero of the correct enum type without assuming a None member exists
        if ((flags & PartyFinderLanguage.Japanese) != 0) result |= AgentLookingForGroup.Language.Japanese;
        if ((flags & PartyFinderLanguage.English) != 0) result |= AgentLookingForGroup.Language.English;
        if ((flags & PartyFinderLanguage.German) != 0) result |= AgentLookingForGroup.Language.German;
        if ((flags & PartyFinderLanguage.French) != 0) result |= AgentLookingForGroup.Language.French;
        return result;
    }

    /// <summary>Donor: <c>JobCatalog.cs</c> — the real per-job bit, looked up by job identity, never re-derived.</summary>
    public static ulong ToNativeMask(this PartyFinderJob job) => (ulong)(job switch
    {
        PartyFinderJob.Paladin => JobFlags.Paladin,
        PartyFinderJob.Warrior => JobFlags.Warrior,
        PartyFinderJob.DarkKnight => JobFlags.DarkKnight,
        PartyFinderJob.Gunbreaker => JobFlags.Gunbreaker,
        PartyFinderJob.WhiteMage => JobFlags.WhiteMage,
        PartyFinderJob.Scholar => JobFlags.Scholar,
        PartyFinderJob.Astrologian => JobFlags.Astrologian,
        PartyFinderJob.Sage => JobFlags.Sage,
        PartyFinderJob.Monk => JobFlags.Monk,
        PartyFinderJob.Dragoon => JobFlags.Dragoon,
        PartyFinderJob.Ninja => JobFlags.Ninja,
        PartyFinderJob.Samurai => JobFlags.Samurai,
        PartyFinderJob.Reaper => JobFlags.Reaper,
        PartyFinderJob.Viper => JobFlags.Viper,
        PartyFinderJob.Bard => JobFlags.Bard,
        PartyFinderJob.Machinist => JobFlags.Machinist,
        PartyFinderJob.Dancer => JobFlags.Dancer,
        PartyFinderJob.BlackMage => JobFlags.BlackMage,
        PartyFinderJob.Summoner => JobFlags.Summoner,
        PartyFinderJob.RedMage => JobFlags.RedMage,
        PartyFinderJob.Pictomancer => JobFlags.Pictomancer,
        PartyFinderJob.BlueMage => JobFlags.BlueMage,
        _ => throw new ArgumentOutOfRangeException(nameof(job), job, null),
    });

    public static readonly ulong AllJobsMask = PartyFinderJobCatalog.AllJobs.Aggregate(0UL, (mask, job) => mask | job.ToNativeMask());

    /// <summary>Donor: <c>PartyFinderPreset.GetSlotMask</c> — a zero/empty mask always reads back as "any job."</summary>
    public static ulong ToNativeMask(this PartyFinderSlot slot)
    {
        if (slot.IsAnyJob) return AllJobsMask;
        var mask = slot.Jobs.Aggregate(0UL, (current, job) => current | job.ToNativeMask());
        return mask == 0 ? AllJobsMask : mask;
    }
}
