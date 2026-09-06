namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>Pure chat-text detection ported from the donor (venuepartyfinder, Services/PartyFinderAutomation.cs:
/// <c>IsFiveMinuteWarning</c>/<c>IsListingEndedMessage</c>). The donor's own method signatures accept an
/// <c>XivChatType</c> parameter that is never actually inspected in either check, so it is dropped here — this stays
/// fully engine/Dalamud-free and unit-testable.</summary>
public static class PartyFinderChatEvents
{
    public static bool IsFiveMinuteWarning(string text, string warningMessageOverride)
    {
        if (!string.IsNullOrWhiteSpace(warningMessageOverride) && text.Contains(warningMessageOverride, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (!normalized.Contains("5 minute") && !normalized.Contains("five minute"))
        {
            return false;
        }

        return normalized.Contains("party") && (normalized.Contains("finder") || normalized.Contains("recruit"));
    }

    public static bool IsListingEndedMessage(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("party recruitment") && (normalized.Contains("has ended") || normalized.EndsWith("ended.") || normalized.EndsWith("ended"));
    }

    /// <summary>Donor: <c>PartyFinderAutomation.HandleChatMessage</c>'s inline throttle check — at most once every
    /// 4 minutes.</summary>
    public static bool ShouldThrottleRefresh(DateTime lastRefreshAttemptUtc, DateTime nowUtc) => nowUtc - lastRefreshAttemptUtc < TimeSpan.FromMinutes(4);
}
