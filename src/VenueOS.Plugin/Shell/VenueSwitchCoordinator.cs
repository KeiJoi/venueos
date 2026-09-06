using Dalamud.Bindings.ImGui;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>
/// A narrowly-scoped pre-switch confirmation hook — NOT a general transactional module-lifecycle gate. Registered
/// guards may only describe a risk and perform their own graceful shutdown after explicit confirmation; they can
/// never silently block a switch or make an unrelated module's failure prevent one, which is exactly the pattern
/// NEW_MODULE_GUIDE.md warns against reintroducing. Today's only guard is Mair's Trivia's active-game warning.
/// </summary>
public interface IVenueSwitchGuard
{
    /// <summary>A short warning to show the operator if leaving the CURRENT venue right now would be destructive, or null if there is nothing to warn about.</summary>
    string? DescribeRisk();
    /// <summary>Runs only after the operator explicitly confirms the switch. Perform whatever graceful shutdown the risk described (e.g. ending an active Trivia game) before the venue actually changes.</summary>
    Task ResolveAsync();
}

public sealed class VenueSwitchCoordinator(VenueProfileService venues, IReadOnlyList<IVenueSwitchGuard> guards)
{
    private readonly Shell.ConfirmDialog dialog = new();
    private Guid? pendingDestination;
    private string? pendingError;
    private bool errorPopupOpen = true;

    /// <summary>The operator's UI action funnels through here instead of calling VenueProfileService.SwitchAsync directly.</summary>
    public void RequestSwitch(Guid destinationId)
    {
        if (destinationId == venues.Current.Id) return;
        var risk = guards.Select(g => g.DescribeRisk()).FirstOrDefault(r => r is not null);
        if (risk is null) { Switch(destinationId); return; }
        pendingDestination = destinationId;
        dialog.Request("Switch venues?", risk, () => Confirm(destinationId));
    }

    private void Confirm(Guid destinationId)
    {
        try { foreach (var guard in guards) guard.ResolveAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { pendingError = $"Could not gracefully end the active session: {ex.Message}. Venue was not switched — try again."; pendingDestination = null; return; }
        Switch(destinationId);
    }

    private void Switch(Guid destinationId)
    {
        var result = venues.SwitchAsync(destinationId).GetAwaiter().GetResult();
        pendingError = result.Success ? null : result.Error; // never silently claim success on a real switch failure
        pendingDestination = null;
    }

    public void Draw(VenueTheme theme)
    {
        dialog.Draw(theme);
        if (pendingError is null) return;
        const string popupId = "Venue switch failed##venueos-switch-error";
        ImGui.OpenPopup(popupId); errorPopupOpen = true;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(380, 0));
        if (ImGui.BeginPopupModal(popupId, ref errorPopupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(pendingError);
            if (UiKit.PrimaryButton(theme, "OK")) { pendingError = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }
}
