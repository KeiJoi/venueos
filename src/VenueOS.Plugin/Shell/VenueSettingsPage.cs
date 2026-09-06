using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Venue profile management: switch, create, rename, duplicate, delete. Simpler than Appearance/Modules/
/// Diagnostics by nature (there's genuinely less to configure here), but uses the exact same card/row/button
/// language rather than falling back to a plain form.</summary>
internal sealed class VenueSettingsPage(VenueProfileService venues, VenueSwitchCoordinator switchCoordinator)
{
    private Guid renameTargetId;
    private string renameBuffer = "";
    private readonly ConfirmDialog confirmDialog = new();
    private readonly TextInputModal addVenueModal = new();

    public void Draw(VenueTheme theme)
    {
        AppFrame.Draw(theme, "home", "Venue", "Manage venue profiles for this VenueOS install.", () => DrawContent(theme));
    }

    private void DrawContent(VenueTheme theme)
    {
        if (UiKit.PrimaryButton(theme, "+ Add Venue")) addVenueModal.Request("Add venue", "Venue name", "e.g. The Aetheryte Lounge", name =>
        {
            var created = venues.Create(name); switchCoordinator.RequestSwitch(created.Id);
        });
        ImGui.Spacing(); ImGui.Spacing();

        foreach (var venue in venues.Profiles)
        {
            ImGui.PushID(venue.Id.ToString());
            DrawVenueRow(theme, venue);
            ImGui.Spacing();
            ImGui.PopID();
        }

        addVenueModal.Draw(theme);
        confirmDialog.Draw(theme);
    }

    private void DrawVenueRow(VenueTheme theme, VenueProfile venue)
    {
        var isActive = venue.Id == venues.Current.Id;
        UiKit.BeginCard($"venue-{venue.Id}", theme, new Vector2(0, renameTargetId == venue.Id ? 96 : 78));

        if (renameTargetId == venue.Id)
        {
            Forms.TextField(theme, "Rename venue", ref renameBuffer, 64);
            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, "Save") && !string.IsNullOrWhiteSpace(renameBuffer)) { venues.Rename(venue.Id, renameBuffer); renameTargetId = Guid.Empty; }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel")) renameTargetId = Guid.Empty;
            UiKit.EndCard();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(venue.DisplayName);
        ImGui.PopStyleColor();
        if (isActive) { ImGui.SameLine(); UiKit.StatusBadge(theme, "Active Venue", ToastLevel.Success); }
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted($"Current theme: {venue.Theme.BuiltInThemeId}");
        ImGui.PopStyleColor();

        if (!isActive) { ImGui.SameLine(ImGui.GetContentRegionAvail().X - 90 + ImGui.GetCursorPosX()); if (UiKit.GhostButton(theme, "Switch to", new Vector2(90, 0))) switchCoordinator.RequestSwitch(venue.Id); }

        if (UiKit.GhostButton(theme, "Rename", new Vector2(70, 0))) { renameTargetId = venue.Id; renameBuffer = venue.DisplayName; }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Duplicate", new Vector2(80, 0))) venues.Duplicate(venue.Id, $"{venue.DisplayName} copy");
        ImGui.SameLine();
        if (UiKit.DangerButton(theme, "Delete", new Vector2(70, 0)) && venues.Profiles.Count > 1)
            confirmDialog.Request("Delete venue?", $"Permanently delete '{venue.DisplayName}' and all of its module configuration. This cannot be undone.", () => venues.DeleteAsync(venue.Id, true).GetAwaiter().GetResult());
        if (venues.Profiles.Count == 1) { ImGui.SameLine(); UiKit.Tooltip("The last remaining venue cannot be deleted."); }

        UiKit.EndCard();
    }
}
