namespace VenueOS.Modules.Operations.Bingo;

/// <summary>Pure decision logic for the Room Key Settings buffered-edit/commit flow, separated from ImGui so it's
/// unit-testable (see VenueBingoOperatorPanel for the actual UI wiring). Fixes a UX defect where the previous
/// implementation called <c>Save</c>/showed a replace-confirmation directly inside the text field's per-keystroke
/// change handler — every single character typed was treated as a committed edit, so a real edit (which changes the
/// buffer many times before the operator is done) triggered a confirmation dialog on the very first keystroke,
/// stealing focus and making the field effectively unusable.
///
/// The correct model: the field edits a local buffer freely (no persistence, no confirmation, no interruption while
/// typing/pasting/selecting); only an explicit "Save Room Key" click commits it, and only THEN — and only when a
/// previous key already existed and the buffer actually differs from it — does a confirmation appear, exactly once
/// per explicit Save/Generate action.</summary>
public static class BingoRoomKeyEdit
{
    /// <summary>Blank/whitespace-only normalizes to no key (null), matching how <see cref="BingoConnectionSettings.RoomKey"/>
    /// itself represents "no key configured" elsewhere in this codebase.</summary>
    public static string? Normalize(string editBuffer) => string.IsNullOrWhiteSpace(editBuffer) ? null : editBuffer.Trim();

    /// <summary>True when the edit buffer represents a different value than what's currently persisted — this is
    /// what gates whether "Save Room Key" is enabled at all. A buffer that merely differs in surrounding whitespace
    /// from the persisted value is NOT dirty (whitespace-only edits normalize away).</summary>
    public static bool IsDirty(string? persistedRoomKey, string editBuffer) => Normalize(editBuffer) != persistedRoomKey;

    /// <summary>True when committing the current edit buffer requires an explicit replace-confirmation before
    /// saving — only when a PREVIOUS key already existed AND the buffer genuinely differs from it. A first-time save
    /// (no previous key configured yet) or a no-op save (buffer unchanged) never requires confirmation, matching the
    /// product rule that only a genuine replacement of an existing credential carries a room-discovery consequence
    /// worth warning about.</summary>
    public static bool RequiresConfirmation(string? persistedRoomKey, string editBuffer) =>
        !string.IsNullOrWhiteSpace(persistedRoomKey) && IsDirty(persistedRoomKey, editBuffer);

    /// <summary>Decides the edit buffer's value for this draw call. Resyncs from the persisted value ONLY when it
    /// changed since the buffer was last synced (a venue switch, or a just-completed Save/Generate elsewhere in this
    /// same frame or an earlier one) — never merely because the operator is mid-edit, since the persisted value does
    /// not change until an explicit commit. This is what lets a full continuous typing/pasting session complete
    /// without any redraw silently resetting keyboard focus or discarding uncommitted keystrokes.</summary>
    public static string ResyncBuffer(string currentBuffer, string? lastSyncedFrom, string? persistedRoomKey) =>
        lastSyncedFrom == persistedRoomKey ? currentBuffer : (persistedRoomKey ?? "");
}
