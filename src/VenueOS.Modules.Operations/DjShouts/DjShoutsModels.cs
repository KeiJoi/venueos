using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Modules.Operations.DjShouts;

/// <summary>Which chat channel a single DJ Shout line goes out on. Unlike Greeter (one command destination for the
/// whole preset) or Giveaways (one channel per announcement block), DJ Shouts assigns a channel PER LINE — this is
/// the module's one deliberate departure from the Greeter pattern it otherwise mirrors (see
/// docs/DJ_SHOUTS_IMPLEMENTATION.md §9).</summary>
public enum DjShoutChannel { Yell, Shout }

/// <summary>One line of a DJ Shout preset. A blank/whitespace-only <see cref="Text"/> is a valid, deliberate
/// authoring state (an empty editor row) — it is simply never dispatched (see <see cref="DjShoutPreset.NonEmptyLines"/>).
/// Every newly-created line defaults to <see cref="DjShoutChannel.Yell"/> (task requirement).</summary>
public sealed record DjShoutLine(string Text, DjShoutChannel Channel)
{
    public static DjShoutLine Empty() => new("", DjShoutChannel.Yell);
}

/// <summary>The exact chat command a <see cref="DjShoutLine"/> dispatches as, and the FFXIV chat-byte accounting for
/// it. The command prefix ("/yell "/"/shout ") is included in the byte count — the established
/// <see cref="BlockLettersLimits.ChatBytes"/> limit (500) models FFXIV's actual chat INPUT BUFFER, which holds the
/// whole typed command, not just the message portion after it — so a line whose raw text alone is under 500 bytes
/// but whose full "/yell &lt;text&gt;" command exceeds it would still fail to submit correctly in game.</summary>
public static class DjShoutLineBytes
{
    public static string CommandPrefix(DjShoutChannel channel) => channel == DjShoutChannel.Yell ? "/yell " : "/shout ";
    public static string BuildCommand(DjShoutLine line) => CommandPrefix(line.Channel) + line.Text;
    public static int CountBytes(DjShoutLine line) => BlockTextLength.CountBytes(BuildCommand(line));
}

/// <summary>A saved, fully-authored DJ Shout preset — an operator-prepared, ordered set of Yell/Shout lines fired
/// manually as one unit. Authored exclusively in Settings → Modules → DJ Shouts (NEW_MODULE_GUIDE.md §8/§9); the
/// live DJ Shouts module only ever selects and fires one, never edits its content.</summary>
public sealed record DjShoutPreset(Guid Id, string Name, IReadOnlyList<DjShoutLine> Lines)
{
    public static DjShoutPreset CreateNew(string name) => new(Guid.NewGuid(), name, Array.Empty<DjShoutLine>());

    /// <summary>Lines actually eligible to be dispatched — a blank/whitespace-only line is authoring scratch space,
    /// never sent (task requirement §19).</summary>
    public IReadOnlyList<DjShoutLine> NonEmptyLines => Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();

    public bool HasExecutableLines => NonEmptyLines.Count > 0;
}

/// <summary>The five DJ Shout hotbar slots, each independently assignable to any saved preset (or none) — mirrors
/// Greeter's five hotbar slots conceptually, but is DJ Shouts' own independent per-venue state, never Greeter's
/// <c>IVenueDatabase</c>-backed hotbar table. Modeled as five named fields rather than a
/// <c>Dictionary&lt;int, Guid?&gt;</c> so the shape round-trips through JSON with no key-type ambiguity and every
/// slot is always present, never merely absent from a sparse map.</summary>
public sealed record DjShoutSlotAssignments(Guid? Slot1, Guid? Slot2, Guid? Slot3, Guid? Slot4, Guid? Slot5)
{
    public const int SlotCount = 5;

    public static DjShoutSlotAssignments Empty() => new(null, null, null, null, null);

    public Guid? Get(int slot) => slot switch
    {
        1 => Slot1, 2 => Slot2, 3 => Slot3, 4 => Slot4, 5 => Slot5,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, $"DJ Shouts slots are numbered 1-{SlotCount}."),
    };

    public DjShoutSlotAssignments With(int slot, Guid? presetId) => slot switch
    {
        1 => this with { Slot1 = presetId },
        2 => this with { Slot2 = presetId },
        3 => this with { Slot3 = presetId },
        4 => this with { Slot4 = presetId },
        5 => this with { Slot5 = presetId },
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, $"DJ Shouts slots are numbered 1-{SlotCount}."),
    };

    /// <summary>Delete-safety (task §31/NEW_MODULE_GUIDE.md §37): clears every slot referencing a deleted preset in
    /// one pass, so a deleted preset's Id can never be left dangling in a slot assignment.</summary>
    public DjShoutSlotAssignments ClearPreset(Guid presetId) => new(
        Slot1 == presetId ? null : Slot1,
        Slot2 == presetId ? null : Slot2,
        Slot3 == presetId ? null : Slot3,
        Slot4 == presetId ? null : Slot4,
        Slot5 == presetId ? null : Slot5);
}

/// <summary>Per-venue persisted state (NEW_MODULE_GUIDE.md §34a — DJ Shouts' full state-authority statement): the
/// saved preset library, the five slot assignments, which slot is currently selected in the live panel, and the
/// timestamp of the last successfully completed DJ Shout. This local per-venue config is the ONLY state DJ Shouts
/// has — no backend, no shared/other-module state, no dependency on Greeter's runtime. <see cref="LastShoutCompletedAtUtc"/>
/// is deliberately persisted (not ephemeral) so the "Last DJ Shout: …ago" timer survives closing/reopening the live
/// panel, switching modules, and a plugin reload within the same venue (task §10) — the one deliberate exception to
/// this module otherwise following Giveaways' "live-run state is ephemeral" convention, because the task explicitly
/// requires the elapsed timer to outlive a panel close/reopen, which a purely in-memory field cannot do.</summary>
public sealed record DjShoutsSettings(
    IReadOnlyList<DjShoutPreset> Presets,
    DjShoutSlotAssignments SlotAssignments,
    int SelectedSlot,
    DateTimeOffset? LastShoutCompletedAtUtc)
{
    public static DjShoutsSettings Default() => new(
        Array.Empty<DjShoutPreset>(),
        DjShoutSlotAssignments.Empty(),
        SelectedSlot: 1,
        LastShoutCompletedAtUtc: null);
}

/// <summary>Pure validation for a preset draft before it can be saved (mirrors <c>GiveawayPresetValidator</c>'s
/// "collect every problem, not just the first" convention). A preset with zero non-empty lines IS a valid thing to
/// save (an in-progress draft) — it simply can never be executed (see <see cref="DjShoutPreset.HasExecutableLines"/>
/// and <c>DjShoutsService.CanRunDjShout</c>); this validator only blocks a genuinely invalid save (no name, or a
/// line whose full dispatched command would exceed FFXIV's chat byte limit).</summary>
public static class DjShoutPresetValidator
{
    public static IReadOnlyList<string> Validate(DjShoutPreset preset)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(preset.Name)) errors.Add("DJ Shout has no name.");

        for (var i = 0; i < preset.Lines.Count; i++)
        {
            var line = preset.Lines[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue; // a blank editor row is not an error — it's just skipped at execution
            var bytes = DjShoutLineBytes.CountBytes(line);
            if (bytes > BlockLettersLimits.ChatBytes)
                errors.Add($"Line {i + 1} is {bytes} / {BlockLettersLimits.ChatBytes} bytes once its /{(line.Channel == DjShoutChannel.Yell ? "yell" : "shout")} command is included — shorten it.");
        }

        return errors;
    }
}
