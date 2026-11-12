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
    /// with nothing having told it to stop, unlike the donor's <c>Stop()</c> (familiarization report §14). Only ever
    /// called while a run is actually in progress — never as an idle "make sure everything is quiet" call — and must
    /// never synthesize an Escape key while the character is in the world (see <see cref="ShoutRunnerEscapePolicy"/>).</summary>
    void Abort();

    /// <summary>The one shared readiness/recovery gate — see <see cref="ShoutRunnerReadinessOutcome"/>'s doc
    /// comment. Must have an internal bounded timeout; must never be an unconditional <c>while(true)</c> like the
    /// donor's <c>WaitUntilChatReadyAsync</c> (familiarization report §13).</summary>
    Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token);

    /// <summary>How travel to <paramref name="targetWorld"/> would work from the character's current World, from GAME
    /// DATA (0.3.14: logical Data Center and region, see <see cref="ShoutRunnerTravelClassification"/>). Returns
    /// <see cref="ShoutRunnerCrossDataCenterCheck.Unknown"/> when it cannot be determined; the caller treats that as an
    /// explicit, reported skip, never as a guess.</summary>
    Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token);

    /// <summary>0.3.14: native World Visit to <paramref name="targetWorld"/> through
    /// <see cref="VenueOS.Modules.Operations.Travel.NativeTravelService"/> (aetheryte → Visit Another World Server → Data
    /// Center dropdown → World row → verified confirmation), waiting with bounded timeouts for VERIFIED arrival on the
    /// target World and logical Data Center. The result is mapped by <see cref="ShoutRunnerNativeTravelMapping"/>: a
    /// wrong destination is <see cref="ShoutRunnerTransferResult.WrongDestination"/>; an unavailable or congested World is
    /// <see cref="ShoutRunnerTransferResult.WorldCongestedSkip"/>; an unavailable Data Center or an unsupported
    /// combination is <see cref="ShoutRunnerTransferResult.DataCenterUnavailableSkip"/>; anything unverified is
    /// <see cref="ShoutRunnerTransferResult.Failed"/>. No logout, character select, Home World detour or Lifestream call is
    /// involved. <paramref name="crossDataCenter"/> is informational (the engine decides from the live window).</summary>
    Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token);

    /// <summary>Best-effort current-location lookup (attuned Aetheryte/zone name), for the caller to match
    /// case-insensitively against the configured destination list — see
    /// <see cref="ShoutRunnerRoutePlanner.PlanWorldRoute"/>. Returns null if no reliable current location could be
    /// determined; the planner's own documented fallback applies in that case.</summary>
    Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token);

    /// <summary>The character's current World and its logical Data Center, read on the framework thread from the local
    /// player and the game's World sheet, or null if no character is present. Used as the last guard before every shout
    /// (0.3.12: World; 0.3.13: World and Data Center) so an announcement meant for one destination is never sent on
    /// another: for example after manual or external travel between arrival and the shout.</summary>
    Task<ShoutRunnerObservedLocation?> TryGetCurrentLocationAsync(CancellationToken token);

    /// <summary>Teleports to an attuned Aetheryte matching <paramref name="destinationName"/> and waits, internally
    /// and with a bounded timeout, for the transition to actually begin and complete — never trusting a bare
    /// "teleport call returned true" as proof of arrival the way the donor did (familiarization report §8).</summary>
    Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token);
}
