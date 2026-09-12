using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Modules.Operations.Shouts;

/// <summary>Which chat channel a single Shout line goes out on. Unlike Greeter (one command destination for the
/// whole preset) or Giveaways (one channel per announcement block), Shouts assigns a channel PER LINE — this is
/// the module's one deliberate departure from the Greeter pattern it otherwise mirrors (carried over unchanged from
/// the original DJ Shouts design — see docs/SHOUTS_IMPLEMENTATION.md §9).</summary>
public enum ShoutChannel { Yell, Shout }

/// <summary>One line of a Shout preset. A blank/whitespace-only <see cref="Text"/> is a valid, deliberate authoring
/// state (an empty editor row) — it is simply never dispatched (see <see cref="ShoutPreset.NonEmptyLines"/>). Every
/// newly-created line defaults to <see cref="ShoutChannel.Yell"/>.</summary>
public sealed record ShoutLine(string Text, ShoutChannel Channel)
{
    public static ShoutLine Empty() => new("", ShoutChannel.Yell);
}

/// <summary>The exact chat command a <see cref="ShoutLine"/> dispatches as, and the FFXIV chat-byte accounting for
/// it. The command prefix ("/yell "/"/shout ") is included in the byte count — the established
/// <see cref="BlockLettersLimits.ChatBytes"/> limit (500) models FFXIV's actual chat INPUT BUFFER, which holds the
/// whole typed command, not just the message portion after it.</summary>
public static class ShoutLineBytes
{
    public static string CommandPrefix(ShoutChannel channel) => channel == ShoutChannel.Yell ? "/yell " : "/shout ";
    public static string BuildCommand(ShoutLine line) => CommandPrefix(line.Channel) + line.Text;
    public static int CountBytes(ShoutLine line) => BlockTextLength.CountBytes(BuildCommand(line));
}

/// <summary>A saved, fully-authored Shout preset — an operator-prepared, ordered set of Yell/Shout lines fired
/// manually as one unit. Authored exclusively in Settings → Modules → Shouts; the live Shouts module only ever
/// selects and fires one, never edits its content.</summary>
public sealed record ShoutPreset(Guid Id, string Name, IReadOnlyList<ShoutLine> Lines)
{
    public static ShoutPreset CreateNew(string name) => new(Guid.NewGuid(), name, Array.Empty<ShoutLine>());

    /// <summary>Lines actually eligible to be dispatched — a blank/whitespace-only line is authoring scratch space,
    /// never sent.</summary>
    public IReadOnlyList<ShoutLine> NonEmptyLines => Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();

    public bool HasExecutableLines => NonEmptyLines.Count > 0;
}

/// <summary>The 15 Shout slots, each independently assignable to any saved preset (or none). Modeled as a fixed-size
/// list (rather than 15 copy-pasted named properties, or a sparse <c>Dictionary&lt;int, Guid?&gt;</c>) — every slot
/// is always present at a stable index, and the shape round-trips through JSON as a plain array with no key-type
/// ambiguity. Slot numbers are 1-based externally (matching the product's "Shout 1"..."Shout 15" naming); index 0 of
/// <see cref="Slots"/> is Shout 1.
///
/// <b>Backward compatibility:</b> this is schema v2 of the module's settings. The original release (VenueOS 0.3.4)
/// persisted exactly five slots as named properties (Slot1..Slot5) at schema v1 — see
/// <see cref="LegacyShoutSlotAssignmentsV1"/> and <c>ShoutsService.MigrateFromV1</c> for the one-time, idempotent
/// migration that expands an existing v1 payload into this shape (old slots 1-5 map straight across; slots 6-15
/// default unassigned).</summary>
public sealed record ShoutSlotAssignments(IReadOnlyList<Guid?> Slots)
{
    public const int SlotCount = 15;

    public static ShoutSlotAssignments Empty() => new(new Guid?[SlotCount]);

    public Guid? Get(int slot)
    {
        ValidateSlot(slot);
        return Slots[slot - 1];
    }

    public ShoutSlotAssignments With(int slot, Guid? presetId)
    {
        ValidateSlot(slot);
        var updated = Slots.ToArray();
        updated[slot - 1] = presetId;
        return new ShoutSlotAssignments(updated);
    }

    /// <summary>Delete-safety: clears every slot (of all 15) referencing a deleted preset in one pass, so a deleted
    /// preset's Id can never be left dangling in a slot assignment.</summary>
    public ShoutSlotAssignments ClearPreset(Guid presetId) => new(Slots.Select(x => x == presetId ? null : x).ToArray());

    private static void ValidateSlot(int slot)
    {
        if (slot < 1 || slot > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Shout slots are numbered 1-{SlotCount}.");
    }
}

/// <summary>Per-venue persisted state: the saved preset library, the 15 slot assignments, which slot is currently
/// selected in the live panel, and the timestamp of the last successfully completed Shout. This local per-venue
/// config is the ONLY state Shouts has — no backend, no shared/other-module state. <see cref="LastShoutCompletedAtUtc"/>
/// is deliberately persisted (not ephemeral) so the "Last Shout: …ago" timer survives closing/reopening the live
/// panel, switching modules, and a plugin reload within the same venue.
///
/// This is schema version 2 (<c>ShoutsService.SchemaVersion</c>) — version 1 was the original five-named-slot shape
/// shipped as DJ Shouts in 0.3.4; see <see cref="ShoutSlotAssignments"/> and <see cref="LegacyShoutSlotAssignmentsV1"/>
/// for the migration.</summary>
public sealed record ShoutsSettings(
    IReadOnlyList<ShoutPreset> Presets,
    ShoutSlotAssignments SlotAssignments,
    int SelectedSlot,
    DateTimeOffset? LastShoutCompletedAtUtc)
{
    public static ShoutsSettings Default() => new(
        Array.Empty<ShoutPreset>(),
        ShoutSlotAssignments.Empty(),
        SelectedSlot: 1,
        LastShoutCompletedAtUtc: null);
}

/// <summary>The exact wire shape of the original five-slot DJ Shouts assignment record (0.3.4, schema v1) — kept
/// solely so <c>ShoutsService</c> can deserialize an existing user's old payload for one-time migration. Never
/// written by current code; read-only, migration-only.</summary>
public sealed record LegacyShoutSlotAssignmentsV1(Guid? Slot1, Guid? Slot2, Guid? Slot3, Guid? Slot4, Guid? Slot5);

/// <summary>The exact wire shape of the original DJ Shouts settings record (0.3.4, schema v1). <see cref="Presets"/>
/// deserializes directly into the current <see cref="ShoutPreset"/>/<see cref="ShoutLine"/>/<see cref="ShoutChannel"/>
/// types unchanged — only the field NAMES on those types were ever renamed from "DjShout*", never their JSON
/// property names or the channel enum's member names, so the preset library round-trips with no special handling.
/// Only <see cref="SlotAssignments"/>'s shape actually changed (five named properties → a 15-element list), which is
/// what this legacy record exists to bridge. Never written by current code; read-only, migration-only.</summary>
public sealed record LegacyShoutsSettingsV1(
    IReadOnlyList<ShoutPreset> Presets,
    LegacyShoutSlotAssignmentsV1 SlotAssignments,
    int SelectedSlot,
    DateTimeOffset? LastShoutCompletedAtUtc)
{
    public static LegacyShoutsSettingsV1 Default() => new(
        Array.Empty<ShoutPreset>(),
        new LegacyShoutSlotAssignmentsV1(null, null, null, null, null),
        SelectedSlot: 1,
        LastShoutCompletedAtUtc: null);
}

/// <summary>Pure validation for a preset draft before it can be saved. A preset with zero non-empty lines IS a valid
/// thing to save (an in-progress draft) — it simply can never be executed (see <see cref="ShoutPreset.HasExecutableLines"/>
/// and <c>ShoutsService.CanRunShout</c>); this validator only blocks a genuinely invalid save (no name, or a line
/// whose full dispatched command would exceed FFXIV's chat byte limit).</summary>
public static class ShoutPresetValidator
{
    public static IReadOnlyList<string> Validate(ShoutPreset preset)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(preset.Name)) errors.Add("Shout has no name.");

        for (var i = 0; i < preset.Lines.Count; i++)
        {
            var line = preset.Lines[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue; // a blank editor row is not an error — it's just skipped at execution
            var bytes = ShoutLineBytes.CountBytes(line);
            if (bytes > BlockLettersLimits.ChatBytes)
                errors.Add($"Line {i + 1} is {bytes} / {BlockLettersLimits.ChatBytes} bytes once its /{(line.Channel == ShoutChannel.Yell ? "yell" : "shout")} command is included — shorten it.");
        }

        return errors;
    }
}
