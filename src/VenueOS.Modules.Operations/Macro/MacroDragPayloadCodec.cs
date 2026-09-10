namespace VenueOS.Modules.Operations.Macro;

/// <summary>The pure encode/decode half of the Live tile -&gt; faux hotbar drag/drop payload
/// (<c>VenueOS.Plugin.Macro.MacroDragDrop</c>) — factored out specifically so it can be unit-tested independent of
/// ImGui (this project has no ImGui dependency; <c>VenueOS.Plugin</c> deliberately has no test project at all, per
/// <c>NEW_MODULE_GUIDE.md</c> §30). This is the raw 16-byte Guid payload shape the drag source sets and the drop
/// target decodes — nothing about ImGui's own drag/drop plumbing (BeginDragDropSource/AcceptDragDropPayload,
/// payload lifetime, null-Handle wrappers, etc.) is exercised or provable here; only whether a Guid survives the
/// exact byte encoding used on the wire between them.</summary>
public static class MacroDragPayloadCodec
{
    public const int ByteLength = 16;

    public static byte[] Encode(Guid macroId) => macroId.ToByteArray();

    /// <summary>False for any length other than exactly <see cref="ByteLength"/> — the live drop handler must
    /// treat that as "not a valid drop" rather than guess/truncate/pad, matching
    /// <c>MacroDragDrop.AcceptTarget</c>'s existing <c>payload.DataSize == 16</c> guard.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Guid macroId)
    {
        if (bytes.Length != ByteLength) { macroId = default; return false; }
        macroId = new Guid(bytes);
        return true;
    }
}
