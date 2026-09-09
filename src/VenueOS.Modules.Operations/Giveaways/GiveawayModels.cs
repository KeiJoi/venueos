namespace VenueOS.Modules.Operations.Giveaways;

/// <summary>Which chat channel a preset's announcement lines go out on — sent through the shared
/// <c>ChatCommandService</c> (NEW_MODULE_GUIDE.md §27/§29), never a direct <c>ICommandManager</c> call.</summary>
public enum GiveawayChatChannel { Shout, Yell }

/// <summary>How the current best roll is determined. See <see cref="GiveawayRollBoard"/> for the comparison rule
/// each value maps to.</summary>
public enum GiveawayWinnerMode { Highest, Lowest, Closest }

/// <summary>Which lifecycle stage a running giveaway is currently in. Distinct from whether rolls are being
/// accepted — <see cref="GiveawayService.IsAcceptingRolls"/> is the authoritative gate for that, not this enum
/// alone, since rolls stay open across both <see cref="AcceptingRolls"/> and <see cref="Midpoint"/>.</summary>
public enum GiveawayPhase { Idle, Starting, AcceptingRolls, Midpoint, Closing, Complete, Cancelled }

/// <summary>One of the three announcement blocks (Start/Midpoint/Closing). A blank line is never sent — see
/// <see cref="NonEmptyLines"/> — and a block is capped at <see cref="MaxLines"/> lines (product requirement,
/// GIVEAWAYS spec §7/§29).</summary>
public sealed record GiveawayAnnouncementBlock(IReadOnlyList<string> Lines)
{
    public const int MaxLines = 10;

    public static GiveawayAnnouncementBlock Empty() => new(Array.Empty<string>());

    public IReadOnlyList<string> NonEmptyLines => Lines.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
}

/// <summary>A saved, fully-authored giveaway preset — everything the operator configures ahead of time in
/// Settings → Modules → Giveaways (GIVEAWAYS spec §4/§6/§29). Live operation only ever reads a preset, it never
/// authors one (§4's Settings/Live split).</summary>
public sealed record GiveawayPreset(
    Guid Id,
    string Name,
    GiveawayChatChannel Channel,
    int DelayBetweenLinesSeconds,
    int GiveawayDurationSeconds,
    GiveawayAnnouncementBlock StartBlock,
    GiveawayAnnouncementBlock MidpointBlock,
    GiveawayAnnouncementBlock ClosingBlock,
    GiveawayWinnerMode WinnerMode,
    int ClosestTargetNumber,
    int AllowedRollsPerPerson,
    string SpecialNumbersRaw)
{
    public static GiveawayPreset CreateNew(string name) => new(
        Guid.NewGuid(),
        name,
        GiveawayChatChannel.Shout,
        DelayBetweenLinesSeconds: 2,
        GiveawayDurationSeconds: 60,
        GiveawayAnnouncementBlock.Empty(),
        GiveawayAnnouncementBlock.Empty(),
        GiveawayAnnouncementBlock.Empty(),
        GiveawayWinnerMode.Highest,
        ClosestTargetNumber: 500,
        AllowedRollsPerPerson: 1,
        SpecialNumbersRaw: "");

    public IReadOnlyList<int> SpecialNumbers => GiveawaySpecialNumbers.Parse(SpecialNumbersRaw);

    /// <summary>Special-number highlighting only ever applies when exactly one roll per person is allowed — GIVEAWAYS
    /// spec §26's explicit anti-gaming rule (repeatedly rolling until a special number appears must never be
    /// rewarded when multiple/unlimited rolls are allowed).</summary>
    public bool SpecialNumbersActive => AllowedRollsPerPerson == 1;

    /// <summary>GIVEAWAYS spec §10: warn the preset author when the Midpoint block's own send time could still be
    /// running when Closing is due, i.e. when it would take longer than half the giveaway duration to send every
    /// Midpoint line at the configured delay. This does not change runtime behavior (<see cref="GiveawayService"/>
    /// already guarantees Closing waits for an in-flight Midpoint block rather than interleaving — spec §10) — it's
    /// purely an authoring-time warning so the operator can shorten the block or lengthen the duration.</summary>
    public bool HasMidpointOverlapRisk
    {
        get
        {
            var midpointLines = MidpointBlock.NonEmptyLines.Count;
            if (midpointLines <= 1) return false;
            var midpointSendSeconds = (midpointLines - 1) * Math.Max(1, DelayBetweenLinesSeconds);
            return midpointSendSeconds > GiveawayDurationSeconds / 2.0;
        }
    }
}

/// <summary>Per-venue persisted state: every saved preset plus which one is currently selected. This is the ONLY
/// state <see cref="GiveawayService"/>'s local config owns (GIVEAWAYS spec §3/§39 state-authority statement) — no
/// backend, no in-game state. Live/ephemeral run state (the active roll board, current phase, countdown) is
/// deliberately NOT part of this record and is never persisted (NEW_MODULE_GUIDE.md §13a).</summary>
public sealed record GiveawaySettings(IReadOnlyList<GiveawayPreset> Presets, Guid? ActivePresetId)
{
    public static GiveawaySettings Default() => new(Array.Empty<GiveawayPreset>(), null);
}

/// <summary>Pure parsing for a preset's comma-separated Special Numbers field (GIVEAWAYS spec §6/§26/§29). Blank/
/// non-numeric/duplicate entries are simply dropped rather than rejecting the whole field — this is an operator
/// convenience list, not a strict schema.</summary>
public static class GiveawaySpecialNumbers
{
    public static IReadOnlyList<int> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<int>();
        var seen = new List<int>();
        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, out var value) && !seen.Contains(value))
                seen.Add(value);
        return seen;
    }
}

/// <summary>Pure validation for a preset's numeric/winner-mode settings, run before <see cref="GiveawayService.Start"/>
/// actually starts a run (GIVEAWAYS spec §11 — "validate required numeric settings" / "validate winner-mode
/// settings" before Start). Returns every problem found, not just the first, so the operator sees the complete
/// picture in one pass.</summary>
public static class GiveawayPresetValidator
{
    public static IReadOnlyList<string> Validate(GiveawayPreset preset)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(preset.Name)) errors.Add("Preset has no name.");
        if (preset.DelayBetweenLinesSeconds < 1) errors.Add("Delay Between Lines must be at least 1 second.");
        if (preset.GiveawayDurationSeconds < 2) errors.Add("Giveaway Duration must be at least 2 seconds.");
        if (preset.AllowedRollsPerPerson < 0) errors.Add("Allowed Rolls Per Person cannot be negative.");
        if (preset.WinnerMode == GiveawayWinnerMode.Closest && preset.ClosestTargetNumber < 0) errors.Add("Closest Target Number cannot be negative.");
        return errors;
    }
}
