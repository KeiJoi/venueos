namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The boundary around unsafe, game-version-sensitive travel automation (native teleport, Lifestream IPC,
/// live game state, transfer-UI dismissal) — the same pattern as Party Finder's
/// <c>VenueOS.Modules.Operations.PartyFinder.IPartyFinderAutomation</c> (per <c>NEW_MODULE_GUIDE.md</c> §30).
/// Implemented by the real FFXIVClientStructs/ECommons/Lifestream-IPC engine in <c>VenueOS.Plugin</c>
/// (<c>ShoutRunnerAutomationService</c>) so <see cref="ShoutRunnerService"/> and its tests never need a live
/// Dalamud/game context — a fake implements this interface instead. Every method here does its own internal,
/// bounded waiting/polling (mirroring the donor's actual wait loops, hardened — see the familiarization report
/// §8/§9/§12/§13) and returns a single terminal outcome; the orchestration layer never polls raw game state itself
/// and never awaits inside a per-frame <c>Tick</c> — see <see cref="ShoutRunnerService"/>'s type-level remark for how
/// it polls these returned <see cref="Task"/>s without blocking.</summary>
public interface IShoutRunnerAutomation
{
    /// <summary>Called on every venue activation (including the first one at startup) and whenever a RUN starts
    /// fresh — resets any per-operation runtime state (congestion counters, teleport-data-load flag, observed
    /// transfer-monitoring state) the same way the donor's fields would be reset by reconstructing
    /// <c>MacroRunner</c>, without actually reconstructing anything.</summary>
    void ResetForVenue();

    /// <summary>Hard-cancels whatever the engine is currently doing: aborts Lifestream if a transfer is in flight,
    /// dismisses any visible transfer UI, and lets any in-flight bounded wait observe cancellation. Called from
    /// <c>ShoutRunnerService</c>'s Stop sequence — must return quickly and must never leave Lifestream mid-transfer
    /// with nothing having told it to stop, unlike the donor's <c>Stop()</c> (familiarization report §14).</summary>
    void Abort();

    /// <summary>The one shared readiness/recovery gate — see <see cref="ShoutRunnerReadinessOutcome"/>'s doc
    /// comment. Must have an internal bounded timeout; must never be an unconditional <c>while(true)</c> like the
    /// donor's <c>WaitUntilChatReadyAsync</c> (familiarization report §13).</summary>
    Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token);

    /// <summary>Whether transferring to <paramref name="targetWorld"/> from the character's current World is a
    /// same-Data-Center or cross-Data-Center Lifestream operation. Returns
    /// <see cref="ShoutRunnerCrossDataCenterCheck.Unknown"/> if Lifestream's own classification IPC call fails —
    /// the caller must treat that as an explicit failure, never silently assume same-Data-Center the way the donor
    /// did (familiarization report §9).</summary>
    Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token);

    /// <summary>Drives Lifestream (<c>/li</c>) to <paramref name="targetWorld"/> and waits, internally and with a
    /// bounded timeout, for arrival, a two-strike congestion skip, or (cross-Data-Center only) detection that the
    /// target Data Center is unreachable. For the Data-Center-unavailable case this method itself performs the full
    /// recovery sequence (abort Lifestream, dismiss transfer UI, confirm the same character is logged in and
    /// controllable again) before returning
    /// <see cref="ShoutRunnerTransferResult.DataCenterUnavailableSkip"/> — if that recovery itself cannot restore a
    /// playable state within its bounded timeout, this returns <see cref="ShoutRunnerTransferResult.Failed"/>
    /// instead, which the caller treats as a fatal recovery failure ending the entire automation.</summary>
    Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token);

    /// <summary>Best-effort current-location lookup (attuned Aetheryte/zone name), for the caller to match
    /// case-insensitively against the configured destination list — see
    /// <see cref="ShoutRunnerRoutePlanner.PlanWorldRoute"/>. Returns null if no reliable current location could be
    /// determined; the planner's own documented fallback applies in that case.</summary>
    Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token);

    /// <summary>Teleports to an attuned Aetheryte matching <paramref name="destinationName"/> and waits, internally
    /// and with a bounded timeout, for the transition to actually begin and complete — never trusting a bare
    /// "teleport call returned true" as proof of arrival the way the donor did (familiarization report §8).</summary>
    Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token);
}
