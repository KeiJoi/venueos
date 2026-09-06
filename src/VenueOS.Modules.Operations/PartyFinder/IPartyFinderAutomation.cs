namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>The boundary around unsafe, game-version-sensitive Party Finder automation (donor:
/// venuepartyfinder/Services/IPartyFinderAutomation.cs). Implemented by the real, unsafe FFXIVClientStructs/ECommons
/// engine in <c>VenueOS.Plugin</c> (<c>PartyFinderAutomationService</c>) so that <see cref="PartyFinderService"/> and
/// its tests never need a live Dalamud/game context — a test double implements this interface instead.</summary>
public interface IPartyFinderAutomation
{
    string Status { get; }

    /// <summary>Only becomes true after a known Party Finder addon has actually been observed visible and ready —
    /// donor: <c>PartyFinderAutomation.IsCompatibilityVerified</c>. Automation refuses to click anything before this
    /// is true.</summary>
    bool IsCompatibilityVerified { get; }

    bool HasOwnListing { get; }

    /// <summary>True while a Create/Edit/Refresh task chain is in flight.</summary>
    bool IsBusy { get; }

    /// <summary>True while the End Party Finder workflow is executing. Create/Edit/Refresh must not start (or
    /// restart) while this is true.</summary>
    bool IsEnding { get; }

    /// <summary>Called on every venue activation (including the first one at startup) — resets all per-operation
    /// runtime state (compatibility-verification flag, retry counters, observed-listing flag) exactly like the
    /// donor's adapter recreating <c>PartyFinderAutomation</c> on every venue switch.</summary>
    void ResetForVenue();

    /// <summary>Cancels the current in-flight task chain. Does not withdraw the native listing and does not affect
    /// Auto Refresh — a distinct operation from <see cref="EndPartyFinder"/>.</summary>
    void Abort();

    void QueueCreateOrUpdate(PartyFinderPreset preset, string reason);

    void QueueRefresh(PartyFinderPreset preset, string reason);

    /// <summary>The VenueOS-added shutdown workflow: aborts any in-flight chain, then withdraws the active native
    /// listing (if any) and verifies recruitment actually ended. Auto Refresh is disabled by the caller
    /// (<see cref="PartyFinderService.EndPartyFinder"/>) before this is ever invoked.</summary>
    void EndPartyFinder(string reason);

    /// <summary>Donor: the "Party recruitment ... has ended" chat-text detection — clears the observed-active-listing
    /// flag. Detected by <see cref="PartyFinderService.HandleChatText"/>, which owns chat-text parsing.</summary>
    void NotifyListingEnded();
}
