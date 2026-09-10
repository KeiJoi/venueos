using VenueOS.Modules.Operations.Macro;

namespace VenueOS.Services.Tests;

/// <summary>Covers only the pure encode/decode half of the Live tile -&gt; faux hotbar drag/drop payload — NOT the
/// live ImGui drag/drop pipeline itself (BeginDragDropSource/AcceptDragDropPayload, payload lifetime, the
/// null-Handle-wrapper crash fixed in MacroIconPicker.cs). This proves the Guid survives the exact byte shape used
/// on the wire; it proves nothing about whether a drop is actually delivered live.</summary>
public sealed class MacroDragPayloadCodecTests
{
    [Fact] public void A_macro_id_survives_an_encode_decode_round_trip()
    {
        var id = Guid.NewGuid();
        var bytes = MacroDragPayloadCodec.Encode(id);
        Assert.True(MacroDragPayloadCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(id, decoded);
    }

    [Fact] public void Encoded_payload_is_exactly_16_bytes() =>
        Assert.Equal(16, MacroDragPayloadCodec.Encode(Guid.NewGuid()).Length);

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(4)]
    public void A_payload_of_any_length_other_than_16_bytes_fails_to_decode_rather_than_guessing(int length)
    {
        var wrongSize = new byte[length];
        Assert.False(MacroDragPayloadCodec.TryDecode(wrongSize, out var decoded));
        Assert.Equal(default, decoded);
    }

    [Fact] public void The_empty_default_guid_still_round_trips_correctly() =>
        Assert.True(MacroDragPayloadCodec.TryDecode(MacroDragPayloadCodec.Encode(Guid.Empty), out var decoded) && decoded == Guid.Empty);
}
