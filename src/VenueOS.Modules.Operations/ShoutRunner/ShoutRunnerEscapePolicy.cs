namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>Decides whether ShoutRunner's transfer-UI dismissal may synthesize an Escape keypress. Pure and
/// ImGui/Dalamud-free so the rule itself is unit-testable even though the keypress it guards is not.
///
/// Why this exists: <c>ShoutRunnerAutomationService</c> dismisses a stuck World Travel / login-queue UI by (a) hiding
/// the <c>AgentWorldTravel</c> addon directly and (b) posting a real <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> for
/// <c>VK_ESCAPE</c> to the game window. (b) is indistinguishable from the player pressing Escape, and while a
/// character is in the world with nothing to cancel, the game's own response to Escape is to open the System Menu.
/// Before 0.3.8 that keypress was sent on every <c>Abort()</c> — including the ones VenueOS itself issues on plugin
/// reload/dispose, venue switch and module disable — which is the "VenueOS opens the System Menu" defect (see
/// <c>docs/SYSTEM_MENU_0.3.8_INVESTIGATION.md</c>).
///
/// The only situation Escape dismissal was ever meant for is a character that is <i>not</i> in the world
/// (transfer stuck at the login screen / character select). While the character is logged in and controllable, the
/// keypress can only ever do the wrong thing, so it is never sent.</summary>
public static class ShoutRunnerEscapePolicy
{
    public static bool ShouldSendEscape(bool isLoggedIn, bool hasLocalPlayer) => !isLoggedIn || !hasLocalPlayer;
}
