using VenueOS.Modules.Operations.BlockLetters;
using VenueOS.Services;

namespace VenueOS.Modules.Operations.Giveaways;

/// <summary>Pure, Dalamud-free winner-name grammar formatting for the Announce Winner feature. Kept entirely outside
/// ImGui/<see cref="GiveawayService"/> so it is directly unit-testable and so a tie is always rendered by the SAME
/// rule everywhere — the operator panel never re-derives or re-formats this itself (NEW_MODULE_GUIDE.md §28's "keep
/// winner/tie logic outside ImGui" pattern, mirrored here for the resulting name list).</summary>
public static class GiveawayWinnerNameFormatter
{
    /// <summary>Formats a winner-identity list into grammatically correct English: 0 names -&gt; <c>null</c>
    /// (invalid/empty); 1 -&gt; <c>"Name"</c>; exactly 2 -&gt; <c>"A and B"</c> (no comma before "and"); 3+ -&gt;
    /// an Oxford-comma list, <c>"A, B, and C"</c>. Only <see cref="GuestIdentity.Name"/> is ever used — HomeWorld is
    /// never included. A duplicate identity (by <see cref="GuestIdentity.Key"/>) is collapsed to its first
    /// occurrence, preserving the input's own order — this board's own leaderboard order (first-accepted-roll order,
    /// stable across repeated Announce presses) is what callers pass in, never a re-sort.</summary>
    public static string? Format(IReadOnlyList<GuestIdentity> winners)
    {
        var names = Deduplicate(winners).Select(x => x.Name).ToArray();
        return names.Length switch
        {
            0 => null,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names[..^1]) + ", and " + names[^1],
        };
    }

    private static IReadOnlyList<GuestIdentity> Deduplicate(IReadOnlyList<GuestIdentity> winners)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GuestIdentity>(winners.Count);
        foreach (var winner in winners)
            if (seen.Add(winner.Key))
                result.Add(winner);
        return result;
    }
}

/// <summary>The outcome of resolving a winner-announcement template against the current winner set. <see cref="Message"/>
/// is populated only when <see cref="Success"/> is true; otherwise <see cref="Error"/> names a concrete, actionable
/// reason (empty template, no winner yet, or the resolved text exceeding FFXIV's chat byte limit) so the operator
/// panel can show it directly instead of silently doing nothing.</summary>
public readonly record struct GiveawayWinnerAnnouncementResult(bool Success, string? Message, string? Error)
{
    public static GiveawayWinnerAnnouncementResult Ok(string message) => new(true, message, null);
    public static GiveawayWinnerAnnouncementResult Failed(string error) => new(false, null, error);
}

/// <summary>Resolves a winner-announcement template (the <c>&lt;name&gt;</c> token) against the current winner set and
/// validates the FINAL resolved text — never just the template's own length — against FFXIV's shared chat byte
/// limit, reusing the same <see cref="BlockTextLength"/>/<see cref="BlockLettersLimits"/> measurement Block Letters
/// already established for exactly this game constraint, rather than duplicating a second byte-counting rule.</summary>
public static class GiveawayWinnerAnnouncementResolver
{
    private const string NameToken = "<name>";

    /// <summary>The template is not required to contain <see cref="NameToken"/> at all — a template with none is a
    /// valid, sendable announcement. Every occurrence present is replaced with the same grammatically formatted list.
    /// </summary>
    public static GiveawayWinnerAnnouncementResult Resolve(string template, IReadOnlyList<GuestIdentity> winners)
    {
        if (string.IsNullOrWhiteSpace(template)) return GiveawayWinnerAnnouncementResult.Failed("The winner announcement template is empty.");

        var formattedNames = GiveawayWinnerNameFormatter.Format(winners);
        if (formattedNames is null) return GiveawayWinnerAnnouncementResult.Failed("No winner is available yet.");

        var resolved = template.Replace(NameToken, formattedNames);
        var byteCount = BlockTextLength.CountBytes(resolved);
        if (byteCount > BlockLettersLimits.ChatBytes)
            return GiveawayWinnerAnnouncementResult.Failed(
                $"The resolved winner announcement is too long for chat ({byteCount}/{BlockLettersLimits.ChatBytes} bytes). Shorten the template — all winner names must remain represented.");

        return GiveawayWinnerAnnouncementResult.Ok(resolved);
    }
}
