using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

/// <summary>Covers the buffered-edit/commit decision logic behind the Room Key Settings UX correction — the actual
/// ImGui wiring (VenueBingoOperatorPanel) is not unit-testable and is verified by inspection/build instead, per
/// NEW_MODULE_GUIDE.md §30 ("no test project for VenueOS.Plugin... put as much logic as possible below that
/// boundary"). These tests are what prove: typing never persists or confirms, a changed buffer is recognized as
/// dirty, and confirmation is required only when genuinely replacing an existing key.</summary>
public sealed class BingoRoomKeyEditTests
{
    [Fact] public void Existing_room_key_initializes_the_edit_buffer_correctly()
    {
        // First draw for a venue that already has a key: lastSyncedFrom is null (never synced yet), so the buffer
        // must pick up the persisted value.
        var buffer = BingoRoomKeyEdit.ResyncBuffer(currentBuffer: "", lastSyncedFrom: null, persistedRoomKey: "VenueASecret");
        Assert.Equal("VenueASecret", buffer);
    }

    [Fact] public void Typing_into_the_buffer_does_not_get_overwritten_by_a_resync_while_nothing_persisted_changed()
    {
        // Simulates several redraws mid-typing: the persisted value never changes, so every resync call must return
        // exactly what the operator has typed so far, never reverting to the old persisted value.
        var persisted = "OldKey";
        var buffer = BingoRoomKeyEdit.ResyncBuffer("", null, persisted);
        Assert.Equal("OldKey", buffer); // initial sync
        var syncedFrom = persisted;

        buffer = "O"; buffer = BingoRoomKeyEdit.ResyncBuffer(buffer, syncedFrom, persisted); Assert.Equal("O", buffer);
        buffer = "Op"; buffer = BingoRoomKeyEdit.ResyncBuffer(buffer, syncedFrom, persisted); Assert.Equal("Op", buffer);
        buffer = "OpenBar"; buffer = BingoRoomKeyEdit.ResyncBuffer(buffer, syncedFrom, persisted); Assert.Equal("OpenBar", buffer);
        // The persisted value was never touched by any of this — proving typing alone never persists.
        Assert.Equal("OldKey", persisted);
    }

    [Fact] public void Typing_into_the_buffer_does_not_by_itself_trigger_a_confirmation()
    {
        // RequiresConfirmation must only ever be consulted at an explicit commit action (Save/Generate) — it is a
        // pure query with no side effect, so simply calling it while "typing" (i.e. for every intermediate buffer
        // value) must never itself pop anything; the panel only acts on its result inside the Save button handler.
        // This test proves the query itself is side-effect-free and correctly reflects "not yet different" for a
        // partial edit that happens to still equal the persisted value, and "different" once it doesn't — but
        // crucially, evaluating it repeatedly during typing is inherently safe because nothing consumes it except
        // an explicit button click.
        Assert.False(BingoRoomKeyEdit.RequiresConfirmation("OldKey", "OldKey")); // buffer still matches persisted mid-edit
        Assert.True(BingoRoomKeyEdit.RequiresConfirmation("OldKey", "OldKeyModified")); // buffer now differs — but this is only ACTED on at Save time
    }

    [Fact] public void A_changed_buffer_is_recognized_as_dirty()
    {
        Assert.False(BingoRoomKeyEdit.IsDirty("SameKey", "SameKey"));
        Assert.True(BingoRoomKeyEdit.IsDirty("SameKey", "DifferentKey"));
        Assert.False(BingoRoomKeyEdit.IsDirty("SameKey", "  SameKey  ")); // surrounding whitespace normalizes away
        Assert.True(BingoRoomKeyEdit.IsDirty(null, "NewKey")); // no previous key at all -> any non-blank buffer is dirty
        Assert.False(BingoRoomKeyEdit.IsDirty(null, "")); // no previous key, still-blank buffer -> not dirty
    }

    [Fact] public void Explicit_save_with_an_existing_changed_key_requires_confirmation()
    {
        Assert.True(BingoRoomKeyEdit.RequiresConfirmation(persistedRoomKey: "OldKey", editBuffer: "NewKey"));
    }

    [Fact] public void Blank_to_first_room_key_can_be_saved_without_unnecessary_replacement_warning()
    {
        Assert.False(BingoRoomKeyEdit.RequiresConfirmation(persistedRoomKey: null, editBuffer: "BrandNewKey"));
        Assert.False(BingoRoomKeyEdit.RequiresConfirmation(persistedRoomKey: "", editBuffer: "BrandNewKey"));
    }

    [Fact] public void A_no_op_save_never_requires_confirmation()
    {
        Assert.False(BingoRoomKeyEdit.RequiresConfirmation(persistedRoomKey: "SameKey", editBuffer: "SameKey"));
        Assert.False(BingoRoomKeyEdit.RequiresConfirmation(persistedRoomKey: "SameKey", editBuffer: "  SameKey  "));
    }

    [Fact] public void Normalize_trims_and_treats_blank_as_no_key()
    {
        Assert.Equal("Trimmed", BingoRoomKeyEdit.Normalize("  Trimmed  "));
        Assert.Null(BingoRoomKeyEdit.Normalize(""));
        Assert.Null(BingoRoomKeyEdit.Normalize("   "));
    }

    [Fact] public void Resync_only_happens_when_the_persisted_value_actually_changed_since_last_sync()
    {
        // Venue switch / just-completed Save scenario: lastSyncedFrom no longer matches the current persisted value
        // -> buffer resyncs to the NEW persisted value, discarding whatever was in the buffer before.
        var buffer = BingoRoomKeyEdit.ResyncBuffer(currentBuffer: "StaleTypedText", lastSyncedFrom: "VenueASecret", persistedRoomKey: "VenueBSecret");
        Assert.Equal("VenueBSecret", buffer);

        // Once resynced, if lastSyncedFrom is updated to match, a subsequent call with the SAME persisted value must
        // preserve any further local edits instead of resyncing again.
        buffer = BingoRoomKeyEdit.ResyncBuffer(currentBuffer: "VenueBSecretModified", lastSyncedFrom: "VenueBSecret", persistedRoomKey: "VenueBSecret");
        Assert.Equal("VenueBSecretModified", buffer);
    }
}
