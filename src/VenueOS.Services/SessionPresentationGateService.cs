namespace VenueOS.Services;

/// <summary>The Dalamud-boundary seam this gate reads from — matches the established
/// <c>ITargetedPlayerProvider</c>/<c>IObjectSnapshotProvider</c> pattern (a thin interface over one or two Dalamud
/// properties) so <see cref="SessionPresentationGateService"/>'s policy is unit-testable without a live game
/// session. <see cref="IsLoggedIn"/> is deliberately the SAME property this codebase already uses elsewhere for
/// exactly this distinction (e.g. <c>ShoutRunnerAutomationService</c>'s own <c>GameState.IsLoggedIn</c>, tracked
/// separately from momentary local-player/object-table availability) — this gate must never be built from
/// "LocalPlayer is null this frame" alone, since that is also true for one ordinary frame during a zone transition
/// while genuinely still logged in.</summary>
public interface ISessionStateProvider
{
    bool IsLoggedIn { get; }
}

/// <summary>Central, authoritative "should VenueOS draw anything right now" policy — the single place this
/// decision is made, per the 0.3.0 post-release requirement that no VenueOS-generated UI (main tablet, detached
/// module windows, the Macro faux hotbars, Bingo's/Giveaways' auxiliary windows) render while genuinely logged out
/// (title screen, character select), with exactly one deliberate exception for an already-active ShoutRunner
/// operation's own UI during a temporary world/Data Center travel transition.
///
/// This is a PRESENTATION gate only — it says nothing about whether services may load configuration, keep
/// persistent state, or otherwise run while logged out (they may; see <c>Plugin.Draw</c>'s call sites, all of
/// which are draw calls, never lifecycle/service calls).
///
/// <b>Why <see cref="CanRenderShoutRunnerUi"/> does not need a second travel-tracking state machine</b> (proven,
/// not assumed — see <c>ShoutRunnerService</c>):
/// <list type="number">
/// <item><description><c>ShoutRunnerService.State</c> starts at <c>Stopped</c> on construction, and
/// <c>Load(nextVenueId)</c> — called for the very first venue activation at plugin startup, before any character
/// login this session is possible — calls <c>HardStop()</c> first, forcing <c>State</c> back to <c>Stopped</c>.
/// So <c>IsActive</c> (below) is guaranteed false at title screen/character select before the operator has ever
/// logged in this session.</description></item>
/// <item><description><c>IsActive</c> can only ever become true via an operator-triggered <c>Start()</c>/<c>Resume()</c>
/// call from ShoutRunner's own panel — and while <c>IsActive</c> is false, that panel is only reachable under the
/// SAME <see cref="CanRenderGeneralUi"/> rule as every other module (no exception applies yet). So <c>IsActive</c>
/// can only ever begin its "true" life while the operator was genuinely logged in.</description></item>
/// <item><description>If <see cref="ISessionStateProvider.IsLoggedIn"/> later goes false while <c>IsActive</c> is
/// true (a real world/DC travel disconnect, or — the risk this design must not ignore — a genuine logout while an
/// operation was left running), <c>ShoutRunnerService</c>'s OWN pre-existing travel-timeout machinery
/// (<c>ReadinessTimeout</c> 30s, <c>LoggedOutRecoveryGrace</c> 8s, <c>SameDataCenterTimeout</c> 3min,
/// <c>CrossDataCenterTimeout</c> 5min — <c>ShoutRunnerAutomationService.cs</c>) bounds how long that can persist:
/// if the character never becomes controllable again within whichever of those windows applies, the automation
/// reports failure and <c>ShoutRunnerService.EnterFaulted</c> sets <c>State</c> to <c>Faulted</c> — an inactive
/// state — automatically, with no new code. A genuine, indefinite logout is therefore bounded to at most the
/// system's own existing definition of "this travel is no longer proceeding normally" (currently 5 minutes, the
/// longest of those timeouts), not indefinite exposure.</description></item>
/// </list>
/// Building a SEPARATE travel/session state machine on top of this would duplicate a bound the system already
/// enforces for an unrelated reason (giving up on a stuck automation) — exactly the "unnecessarily complicated
/// second state machine" this design deliberately avoids.</summary>
public sealed class SessionPresentationGateService(ISessionStateProvider session)
{
    /// <summary>Ordinary VenueOS UI — the main tablet, Settings, every module's embedded/detached rendering other
    /// than ShoutRunner's exception below, the faux Macro hotbars, Bingo's auxiliary windows, Giveaways' tracker
    /// window. False at title screen/character select/genuine logout; true during normal logged-in play, including
    /// ordinary zoning (this reads <see cref="ISessionStateProvider.IsLoggedIn"/>, never local-player presence).</summary>
    public bool CanRenderGeneralUi => session.IsLoggedIn;

    /// <summary>ShoutRunner's own operational UI additionally stays visible while
    /// <paramref name="shoutRunnerActive"/> (<c>ShoutRunnerService.IsActive</c>) is true, even during a temporary
    /// logged-out window — see this class's doc comment for why that can't leak UI at startup/title/character
    /// select or persist indefinitely after a genuine logout. Once the active operation stops/completes/faults,
    /// this collapses back to exactly <see cref="CanRenderGeneralUi"/> like every other module.</summary>
    public bool CanRenderShoutRunnerUi(bool shoutRunnerActive) => CanRenderGeneralUi || shoutRunnerActive;
}
