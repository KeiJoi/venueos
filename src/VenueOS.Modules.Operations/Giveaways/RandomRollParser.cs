using System.Text.RegularExpressions;
using VenueOS.Services;

namespace VenueOS.Modules.Operations.Giveaways;

/// <summary>One parsed <c>/random</c> (or <c>/random 999</c>) chat line, before identity resolution. Purely a text
/// shape — it does NOT decide the final <see cref="GuestIdentity"/> for another player; that combines this parser's
/// <see cref="OtherPlayerNameText"/> (a text fallback only) with structured Dalamud <c>PlayerPayload</c> data where
/// available, in <c>VenueOS.Plugin.Giveaways.GiveawaysRollChatAdapter</c> (GIVEAWAYS spec §13/§14 — "prefer
/// structured SeString/chat payload data" over blindly regex-splitting the rendered line).</summary>
public readonly record struct ParsedRandomRoll(bool IsSelf, string? OtherPlayerNameText, int Value, int? OutOf)
{
    public GiveawayRollKind Kind => OutOf.HasValue ? GiveawayRollKind.RangedOutOf : GiveawayRollKind.Standard;
}

/// <summary>Pure text recognition for the two supported roll message forms — no Dalamud dependency, fully unit-
/// testable against the exact fixtures captured live by the user (GIVEAWAYS spec §14/§42):
/// <list type="bullet">
/// <item>Self, standard: "Random! You Roll a 415."</item>
/// <item>Self, /random 999: "Random! You Roll a 124 (out of 999)."</item>
/// <item>Other, standard: "Random! Poinsettia BloodlilyCuchulainn rolls a 65."</item>
/// <item>Other, /random 999: "Random! Poinsettia BloodlilyCuchulainn rolls a 65 (out of 999)."</item>
/// </list>
/// The self/other distinction is made purely from the verb form the game itself uses ("You Roll" vs. "&lt;name&gt;
/// rolls") — a real character name can never literally be "You" (FFXIV names are always a First-and-Last-name pair),
/// so this text distinction is safe and matches the donor-style precedent already used for Bingo's own random-roll
/// parser (<c>BingoRollTextParser</c>) of relying on the message's own literal text shape rather than chat-type
/// metadata. The trailing "(out of N)" — present only for a <c>/random N</c> roll, never a plain <c>/random</c> —
/// is the ONLY signal used to distinguish the two forms; the resulting roll VALUE is never used to infer this
/// (spec §15/§48).
///
/// <b>LIVE-QA REGRESSION FIX (forensic finding):</b> the self pattern below is now matched CASE-INSENSITIVELY.
/// It previously required a literally-capitalized "You Roll a" (transcribed verbatim from the fixture originally
/// given for this module) with no <see cref="RegexOptions.IgnoreCase"/> flag, while <see cref="OtherPattern"/>
/// already had that flag. Live FFXIV testing showed a cross-world participant's roll was captured correctly (proving
/// the chat-event/text-extraction pipeline itself works) while the HOST's own `/random` was silently dropped — the
/// only material difference between the two code paths was this exact regex, so the self message's actual case
/// never matched. This is corroborated by <c>BingoRollCorrelation.cs</c>'s own <c>RollResultRegex</c> — an
/// independently, already-live-verified regex for this SAME underlying game text family — which matches lowercase
/// "You roll a" with <see cref="RegexOptions.IgnoreCase"/> explicitly for this reason. Matching case-insensitively
/// (rather than hard-coding one assumed case) means this no longer depends on guessing FFXIV's exact casing at
/// all.</summary>
public static class RandomRollParser
{
    private static readonly Regex SelfPattern = new(
        @"^Random!\s*You\s+Roll\s+a\s+(?<value>\d+)\s*(?:\(out of\s*(?<max>\d+)\))?\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OtherPattern = new(
        @"^Random!\s*(?<name>.+?)\s+rolls\s+a\s+(?<value>\d+)\s*(?:\(out of\s*(?<max>\d+)\))?\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ParsedRandomRoll? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        var self = SelfPattern.Match(trimmed);
        if (self.Success && int.TryParse(self.Groups["value"].Value, out var selfValue))
            return new ParsedRandomRoll(true, null, selfValue, ParseMax(self));

        var other = OtherPattern.Match(trimmed);
        if (other.Success && int.TryParse(other.Groups["value"].Value, out var otherValue))
        {
            var name = other.Groups["name"].Value.Trim();
            return string.IsNullOrWhiteSpace(name) ? null : new ParsedRandomRoll(false, name, otherValue, ParseMax(other));
        }

        return null;
    }

    private static int? ParseMax(Match match) => match.Groups["max"].Success && int.TryParse(match.Groups["max"].Value, out var max) ? max : null;
}
