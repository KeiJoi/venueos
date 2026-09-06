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
    /// throttle. Reproduced here identically.</summary>
    public void CreateOrUpdate(string reason)
    {
        if (automation.IsEnding) return;
        MarkRefreshAttempt();
        automation.QueueCreateOrUpdate(Settings.Preset, reason);
    }

    public void Refresh(string reason)
    {
        if (automation.IsEnding) return;
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

        if (!Settings.AutoRefreshEnabled || automation.IsEnding)
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
