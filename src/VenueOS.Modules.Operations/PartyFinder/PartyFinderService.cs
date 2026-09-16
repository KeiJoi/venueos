using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>Owns Party Finder's per-venue settings (persisted through <see cref="VenueProfileService"/>, never
/// through the donor's own <c>PluginConfiguration.Save()</c>/<c>Service.PluginInterface.SavePluginConfig</c>) and
/// orchestrates the unsafe automation engine behind <see cref="IPartyFinderAutomation"/>. Fully testable with a fake
/// <see cref="IPartyFinderAutomation"/> — no Dalamud/FFXIVClientStructs dependency here.</summary>
public sealed class PartyFinderService(IPartyFinderAutomation automation, VenueProfileService profiles, IClock clock)
{
    public const string ModuleId = "promotion.partyfinder";
    private const int SchemaVersion = 1;
    private Guid venueId;

    public PartyFinderSettings Settings { get; private set; } = PartyFinderSettings.CreateDefault();

    public string Status => automation.Status;
    public bool HasOwnListing => automation.HasOwnListing;
    public bool IsCompatibilityVerified => automation.IsCompatibilityVerified;
    public bool IsBusy => automation.IsBusy;
    public bool IsEnding => automation.IsEnding;

    public void Load(Guid nextVenueId)
    {
        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, PartyFinderSettings.CreateDefault);
        automation.ResetForVenue();
    }

    public void UpdatePreset(PartyFinderPreset preset)
    {
        Settings = Settings with { Preset = preset };
        Save();
    }

    public void SetAutoRefreshEnabled(bool enabled)
    {
        if (Settings.AutoRefreshEnabled == enabled) return;
        Settings = Settings with { AutoRefreshEnabled = enabled };
        Save();
    }

    public void SetWarningMessageOverride(string value)
    {
        if (Settings.WarningMessageOverride == value) return;
        Settings = Settings with { WarningMessageOverride = value };
        Save();
    }

    /// <summary>Donor: <c>PartyFinderAutomation.QueueOperation</c> updates <c>LastRefreshAttemptUtc</c> on every
    /// Create/Edit/Refresh call — manual or automatic — so a manual action also resets the 4-minute auto-refresh
    /// throttle. Reproduced here identically.
    ///
    /// Reliability hardening: ignored while <see cref="IPartyFinderAutomation.IsBusy"/> — the single-active-refresh
    /// ownership guard. The automation engine previously accepted a second Create/Edit/Refresh request at any time
    /// by aborting whatever chain was already in flight and starting over, which meant repeatedly clicking the
    /// operator panel's action button (a natural response to no immediate visual feedback) could restart the chain
    /// indefinitely and never let a single attempt reach completion — a plausible, timing-dependent explanation for
    /// production reports of Party Finder refresh "never working." Both requests apply the identical current preset,
    /// so skipping a redundant request loses nothing: the attempt already in flight already carries the latest data.
    /// <see cref="Abort"/> remains available at all times as the explicit, immediate way to cancel and start over.</summary>
    public void CreateOrUpdate(string reason)
    {
        if (automation.IsEnding || automation.IsBusy) return;
        MarkRefreshAttempt();
        automation.QueueCreateOrUpdate(Settings.Preset, reason);
    }

    /// <summary>See <see cref="CreateOrUpdate"/>'s doc comment for why a request is ignored (not queued as a second
    /// attempt) while one is already in flight.</summary>
    public void Refresh(string reason)
    {
        if (automation.IsEnding || automation.IsBusy) return;
        MarkRefreshAttempt();
        automation.QueueRefresh(Settings.Preset, reason);
    }

    /// <summary>Cancels the in-flight task chain only. Does not withdraw the listing, does not disable Auto Refresh,
    /// and is not "done for the night" — see <see cref="EndPartyFinder"/> for that.</summary>
    public void Abort() => automation.Abort();

    /// <summary>The VenueOS-added shutdown operation. Auto Refresh is disabled and persisted for the ACTIVE venue
    /// FIRST, before the automation engine begins withdrawing the native listing, so a 5-minute-warning chat event
    /// arriving mid-shutdown can never queue another refresh (see <see cref="HandleChatText"/>'s own
    /// <c>IsEnding</c>/<c>AutoRefreshEnabled</c> guard). Auto Refresh is never re-enabled automatically afterward —
    /// only an explicit future call to <see cref="SetAutoRefreshEnabled"/> (Settings → Modules → Party Finder) turns
    /// it back on.</summary>
    public void EndPartyFinder(string reason)
    {
        SetAutoRefreshEnabled(false);
        automation.EndPartyFinder(reason);
    }

    public void HandleChatText(string text)
    {
        if (PartyFinderChatEvents.IsListingEndedMessage(text))
        {
            automation.NotifyListingEnded();
            return;
        }

        // The IsBusy guard here is the other half of CreateOrUpdate/Refresh's single-active-refresh ownership: the
        // native 5-minute warning can arrive while the operator is mid-manual Create/Edit/Refresh. Skipping it in
        // that case (rather than aborting the operator's in-flight action to restart an automatic refresh with the
        // exact same preset data) never loses the renewal — the manual action in flight already re-submits the
        // current preset, which resets the listing's visibility exactly like a refresh would.
        if (!Settings.AutoRefreshEnabled || automation.IsEnding || automation.IsBusy)
        {
            return;
        }

        if (!PartyFinderChatEvents.IsFiveMinuteWarning(text, Settings.WarningMessageOverride))
        {
            return;
        }

        if (PartyFinderChatEvents.ShouldThrottleRefresh(Settings.LastRefreshAttemptUtc, clock.UtcNow.UtcDateTime))
        {
            return;
        }

        MarkRefreshAttempt();
        automation.QueueRefresh(Settings.Preset, "5 minute warning");
    }

    private void MarkRefreshAttempt()
    {
        Settings = Settings with { LastRefreshAttemptUtc = clock.UtcNow.UtcDateTime };
        Save();
    }

    private void Save() => profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, Settings);
}
