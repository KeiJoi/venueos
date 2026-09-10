using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Raffle;

internal sealed class RaffleOperatorPanel(VenueRaffleService raffle, VenueProfileService venues, ITargetedPlayerProvider targetProvider)
{
    private readonly ConfirmDialog confirmDialog = new();
    private readonly FileDialogManager fileDialogManager = new();

    private string newRaffleName = "";
    private string renameBuffer = "";
    private bool showArchived;
    private bool showFullLinks;
    private string newParticipantName = "";
    private string newParticipantWorld = "";
    private string? targetError;
    private int newPaidCount = 1;
    private int newFreeCount = 1;
    private bool clearExclusionsOnPublish;
    private string statusMessage = "";

    // --- Settings → Modules → Raffle: persistent configuration only -------------------------------------------

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;

        UiKit.BeginSectionCard("raffle-connection", theme, "Backend Connection");
        var backendUrl = raffle.Settings.Connection.BackendBaseUrl;
        if (Forms.TextField(theme, "Backend URL", ref backendUrl, 256, "https://your-raffle-backend.example.com"))
            raffle.SaveConnection(raffle.Settings.Connection with { BackendBaseUrl = backendUrl });

        var accessKey = raffle.Settings.Connection.AccessKey;
        // Intentionally NOT password-masked: the operator must be able to read, copy, and hand this key to
        // whoever deploys/administers the backend. Masking a value the operator is expected to recover/communicate
        // defeats that. DiagnosticsService.RecordFailure/Redact still keep it out of logs and error messages.
        if (Forms.TextField(theme, "Backend Access Key", ref accessKey, 256, "shared secret configured on the backend"))
            raffle.SaveConnection(raffle.Settings.Connection with { AccessKey = accessKey });
        UiKit.Tooltip("Protects organizer-only backend operations (publish, delete). Never sent to the browser wheel. Shown in plain text on purpose so it can be copied/verified.");
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("raffle-defaults", theme, "New Raffle Defaults");
        var defaults = raffle.Settings.Defaults;
        var startingPot = defaults.StartingPot;
        if (Forms.FloatField(theme, "Starting Pot", ref startingPot, 1f, 0f)) raffle.SaveDefaults(defaults with { StartingPot = startingPot });
        var ticketCost = defaults.TicketCost;
        if (Forms.FloatField(theme, "Ticket Cost", ref ticketCost, 0.5f, 0f)) raffle.SaveDefaults(defaults with { TicketCost = ticketCost });
        var prizePct = defaults.PrizePercentage;
        if (Forms.FloatField(theme, "Prize %", ref prizePct, 1f, 0f, 100f)) raffle.SaveDefaults(defaults with { PrizePercentage = prizePct });
        var paidForFree = defaults.PaidTicketsForFree;
        if (Forms.NumericField(theme, "Paid Tickets For Free (bonus rule)", ref paidForFree, 1, 0)) raffle.SaveDefaults(defaults with { PaidTicketsForFree = paidForFree });
        var freePerBlock = defaults.FreeTicketsPerBlock;
        if (Forms.NumericField(theme, "Free Tickets Per Block", ref freePerBlock, 1, 0)) raffle.SaveDefaults(defaults with { FreeTicketsPerBlock = freePerBlock });
        UiKit.EndSectionCard();
    }

    // --- Live operational screen --------------------------------------------------------------------------------

    public void Draw()
    {
        var theme = venues.Current.Theme;
        fileDialogManager.Draw();
        confirmDialog.Draw(theme);

        DrawRaffleSelector(theme);
        ImGui.Spacing();

        var current = raffle.Selected;
        if (current is null)
        {
            UiKit.EmptyState(theme, "No raffle selected", "Create a raffle above to get started.");
            return;
        }

        DrawOverviewSection(theme, current);
        ImGui.Spacing();
        DrawSettingsSection(theme, current);
        ImGui.Spacing();
        DrawParticipantsSection(theme, current);
        ImGui.Spacing();
        DrawPublishSection(theme, current);
        ImGui.Spacing();
        DrawImportExportSection(theme, current);

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            ImGui.Spacing();
            UiKit.InfoBanner(theme, "Status", statusMessage, ToastLevel.Information);
        }
    }

    private void DrawRaffleSelector(VenueTheme theme)
    {
        UiKit.BeginSectionCard("raffle-selector", theme, $"Raffles ({raffle.Dashboard.ActiveRaffleCount} active, {raffle.Dashboard.ArchivedRaffleCount} archived)");

        var active = raffle.Settings.Raffles.Where(x => !x.IsArchived).ToList();
        if (active.Count == 0) UiKit.EmptyState(theme, "No active raffles", "Create one below.");
        foreach (var item in active)
        {
            ImGui.PushID(item.Id);
            var selected = item.Id == raffle.Settings.SelectedRaffleId;
            if (UiKit.ListRow(theme, item.Name, $"{item.TotalTickets} ticket(s){(item.WinnerName is null ? "" : $" · Winner: {item.WinnerName}")}", selected))
                raffle.Select(item.Id);
            ImGui.PopID();
        }

        ImGui.Spacing();
        Forms.TextField(theme, "New raffle name", ref newRaffleName, 128, "e.g. Weekend giveaway");
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Create raffle") && !string.IsNullOrWhiteSpace(newRaffleName)) { raffle.Create(newRaffleName); newRaffleName = ""; }

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, showArchived ? "Hide archived raffles" : "Show archived raffles")) showArchived = !showArchived;
        if (showArchived)
        {
            var archived = raffle.Settings.Raffles.Where(x => x.IsArchived).ToList();
            if (archived.Count == 0) UiKit.EmptyState(theme, "No archived raffles", "Archived raffles stay here until restored or permanently deleted.");
            foreach (var item in archived)
            {
                ImGui.PushID("archived-" + item.Id);
                UiKit.ListRow(theme, item.Name, $"{item.TotalTickets} ticket(s){(item.WinnerName is null ? "" : $" · Winner: {item.WinnerName}")}", false);
                if (UiKit.GhostButton(theme, "Restore")) raffle.Unarchive(item.Id);
                ImGui.SameLine();
                if (UiKit.DangerButton(theme, "Delete Permanently"))
                    confirmDialog.Request("Permanently delete this raffle?", $"\"{item.Name}\" and its entire history (participants, winner, links) will be permanently deleted from VenueOS, and its published copy on the backend will also be deleted if it exists. This cannot be undone.", () => _ = raffle.DeleteAsync(item.Id));
                ImGui.PopID();
            }
        }
        UiKit.EndSectionCard();
    }

    private void DrawOverviewSection(VenueTheme theme, LocalRaffle current)
    {
        UiKit.BeginSectionCard("raffle-overview", theme, current.Name);

        renameBuffer = renameBuffer.Length == 0 ? current.Name : renameBuffer;
        if (Forms.TextField(theme, "Rename", ref renameBuffer, 128)) { /* live edit only */ }
        if (UiKit.GhostButton(theme, "Save Name") && !string.IsNullOrWhiteSpace(renameBuffer)) raffle.Rename(current.Id, renameBuffer);

        ImGui.Spacing();
        if (current.ExternalId is null) UiKit.StatusBadge(theme, "Not Published", ToastLevel.Warning);
        else if (current.HasUnpublishedChanges) UiKit.StatusBadge(theme, "Unpublished Changes", ToastLevel.Warning);
        else UiKit.StatusBadge(theme, "Published", ToastLevel.Success);
        ImGui.SameLine();
        UiKit.ConnectionBadge(theme, raffle.IsRealtimeConnected ? "Live" : "Not Connected", raffle.IsRealtimeConnected);

        ImGui.Spacing();
        ImGui.TextUnformatted($"Tickets: {current.TotalTickets} (Paid {current.TotalPaidTickets} / Free {current.TotalFreeTickets})");
        ImGui.TextUnformatted($"Running Pot: {current.RunningPot:0.00}   Prize Pot: {current.PrizePot:0.00}   House Take: {current.HouseTake:0.00}");

        ImGui.Spacing();
        if (string.IsNullOrWhiteSpace(current.WinnerName)) UiKit.EmptyState(theme, "No winner yet", "Open the host link in a browser and click Spin.");
        else UiKit.StatusBadge(theme, $"Winner: {current.WinnerName}", ToastLevel.Success);

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Archive")) raffle.Archive(current.Id);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Reset"))
            confirmDialog.Request("Reset this raffle?", "This clears participants, tickets, exclusions, and the locally-known winner. It does NOT delete the raffle itself, and does NOT touch the backend until you Publish again.", () => raffle.Reset(current.Id));
        ImGui.SameLine();
        if (UiKit.DangerButton(theme, "Delete Permanently"))
            confirmDialog.Request("Permanently delete this raffle?", $"\"{current.Name}\" and its entire history will be permanently deleted from VenueOS, and its published copy on the backend will also be deleted if it exists. This cannot be undone.", () => _ = raffle.DeleteAsync(current.Id));

        UiKit.EndSectionCard();
    }

    private void DrawSettingsSection(VenueTheme theme, LocalRaffle current)
    {
        UiKit.BeginSectionCard("raffle-settings", theme, "Pot, Tickets & Bonus Rule");
        var settings = current.Settings;
        var startingPot = settings.StartingPot;
        if (Forms.FloatField(theme, "Starting Pot", ref startingPot, 1f, 0f)) raffle.UpdateRaffleSettings(current.Id, settings with { StartingPot = startingPot });
        var ticketCost = settings.TicketCost;
        if (Forms.FloatField(theme, "Ticket Cost", ref ticketCost, 0.5f, 0f)) raffle.UpdateRaffleSettings(current.Id, settings with { TicketCost = ticketCost });
        var prizePct = settings.PrizePercentage;
        if (Forms.FloatField(theme, "Prize %", ref prizePct, 1f, 0f, 100f)) raffle.UpdateRaffleSettings(current.Id, settings with { PrizePercentage = prizePct });
        var paidForFree = settings.PaidTicketsForFree;
        if (Forms.NumericField(theme, "Paid Tickets For Free (bonus rule)", ref paidForFree, 1, 0)) raffle.UpdateRaffleSettings(current.Id, settings with { PaidTicketsForFree = paidForFree });
        var freePerBlock = settings.FreeTicketsPerBlock;
        if (Forms.NumericField(theme, "Free Tickets Per Block", ref freePerBlock, 1, 0)) raffle.UpdateRaffleSettings(current.Id, settings with { FreeTicketsPerBlock = freePerBlock });
        UiKit.EndSectionCard();
    }

    private void DrawParticipantsSection(VenueTheme theme, LocalRaffle current)
    {
        UiKit.BeginSectionCard("raffle-participants", theme, $"Participants ({current.Participants.Count})");

        Forms.TextField(theme, "Name", ref newParticipantName, 64);
        Forms.TextField(theme, "Home World", ref newParticipantWorld, 64, "optional - leave blank for a legacy Name-only entrant");
        if (UiKit.GhostButton(theme, "Use Current Target"))
        {
            var lookup = targetProvider.GetTargetedPlayer();
            if (lookup.Success) { newParticipantName = lookup.Name; newParticipantWorld = lookup.HomeWorld; targetError = null; }
            else targetError = lookup.Error;
        }
        if (targetError is not null) UiKit.WarningState(theme, targetError);

        ImGui.Spacing();
        Forms.NumericField(theme, "Paid tickets to add", ref newPaidCount, 1, 0);
        var bonusPreview = RaffleTicketMath.CalculateFreeTickets(current.Settings, Math.Max(0, newPaidCount));
        if (bonusPreview > 0) ImGui.TextUnformatted($"+ {bonusPreview} bonus free ticket(s) under the current rule");
        if (UiKit.PrimaryButton(theme, "Add Paid Tickets") && !string.IsNullOrWhiteSpace(newParticipantName) && newPaidCount > 0)
            raffle.AddPaidTickets(current.Id, newParticipantName, NullIfEmpty(newParticipantWorld), newPaidCount);

        ImGui.Spacing();
        Forms.NumericField(theme, "Free tickets to add", ref newFreeCount, 1, 0);
        if (UiKit.GhostButton(theme, "Add Free Tickets") && !string.IsNullOrWhiteSpace(newParticipantName) && newFreeCount > 0)
            raffle.AddFreeTickets(current.Id, newParticipantName, NullIfEmpty(newParticipantWorld), newFreeCount);

        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Add Participant Only") && !string.IsNullOrWhiteSpace(newParticipantName))
            raffle.AddParticipant(current.Id, newParticipantName, NullIfEmpty(newParticipantWorld));

        ImGui.Spacing();
        UiKit.Divider(theme);

        if (current.Participants.Count == 0) UiKit.EmptyState(theme, "No participants yet", "Add one above.");
        foreach (var participant in current.Participants.OrderByDescending(p => p.PaidTickets + p.FreeTickets))
        {
            ImGui.PushID(participant.IdentityKey);
            var excluded = current.ExcludedParticipants.Contains(participant.TicketKey);
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(excluded ? theme.Tokens.TextSecondary : theme.Tokens.TextPrimary));
            ImGui.TextUnformatted(participant.DisplayName);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted($"· Paid {participant.PaidTickets} · Free {participant.FreeTickets}");
            ImGui.PopStyleColor();
            if (excluded) { ImGui.SameLine(); UiKit.StatusBadge(theme, "Excluded (previous winner)", ToastLevel.Warning); }

            if (UiKit.GhostButton(theme, "+Paid")) raffle.AdjustTickets(current.Id, participant.IdentityKey, 1, 0);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "-Paid")) raffle.AdjustTickets(current.Id, participant.IdentityKey, -1, 0);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "+Free")) raffle.AdjustTickets(current.Id, participant.IdentityKey, 0, 1);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "-Free")) raffle.AdjustTickets(current.Id, participant.IdentityKey, 0, -1);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Remove")) raffle.RemoveParticipant(current.Id, participant.IdentityKey);
            ImGui.PopID();
        }
        UiKit.EndSectionCard();
    }

    private void DrawPublishSection(VenueTheme theme, LocalRaffle current)
    {
        UiKit.BeginSectionCard("raffle-publish", theme, "Publish & Live Links");

        UiKit.Toggle(theme, "Also clear previously-excluded winners on publish", ref clearExclusionsOnPublish);
        if (UiKit.PrimaryButton(theme, "Publish / Update Raffle")) _ = PublishAsync(current.Id, clearExclusionsOnPublish);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Refresh From Backend")) _ = RefreshAsync(current.Id);

        if (current.HostUrl is not null && current.ViewerUrl is not null)
        {
            ImGui.Spacing();
            // 0.3.0 short-link feature: the short "/l/:code" form is the primary displayed/copied link (practical
            // to paste into FFXIV chat, matching Bingo's proven behavior) — DisplayHostUrl/DisplayViewerUrl fall
            // back to the full long link automatically if a short code hasn't been minted yet (e.g. no Access Key
            // configured), so this never shows a blank field.
            var shortHostUrl = current.DisplayHostUrl(raffle.Settings.Connection.BackendBaseUrl);
            Forms.TextField(theme, "Host Link", ref shortHostUrl, 512);
            if (UiKit.GhostButton(theme, "Copy Host Link")) ImGui.SetClipboardText(shortHostUrl);

            var shortViewerUrl = current.DisplayViewerUrl(raffle.Settings.Connection.BackendBaseUrl);
            Forms.TextField(theme, "Viewer Link", ref shortViewerUrl, 512);
            if (UiKit.GhostButton(theme, "Copy Viewer Link")) ImGui.SetClipboardText(shortViewerUrl);

            if (string.IsNullOrWhiteSpace(current.HostLinkCode) || string.IsNullOrWhiteSpace(current.ViewerLinkCode))
            {
                ImGui.Spacing();
                UiKit.WarningState(theme, "Short links aren't available yet — showing the full link above. Confirm the Access Key is set in Settings, then Publish again.");
                if (UiKit.GhostButton(theme, "Retry Short Links")) _ = raffle.EnsureShortLinksAsync(current.Id);
            }

            ImGui.Spacing();
            if (UiKit.GhostButton(theme, showFullLinks ? "Hide Full Links" : "Show Full Links")) showFullLinks = !showFullLinks;
            if (showFullLinks)
            {
                var hostUrl = current.HostUrl;
                Forms.TextField(theme, "Full Host Link", ref hostUrl, 512);
                var viewerUrl = current.ViewerUrl;
                Forms.TextField(theme, "Full Viewer Link", ref viewerUrl, 512);
            }
        }

        UiKit.EndSectionCard();
    }

    private void DrawImportExportSection(VenueTheme theme, LocalRaffle current)
    {
        UiKit.BeginSectionCard("raffle-import-export", theme, "Import / Export");
        if (UiKit.GhostButton(theme, "Export to XLSX"))
        {
            fileDialogManager.SaveFileDialog("Export Raffle", ".xlsx", $"raffle_{current.Name}_{DateTime.Now:yyyyMMdd_HHmmss}", ".xlsx", (ok, path) =>
            {
                if (!ok) return;
                try { using var stream = File.Create(path); RaffleXlsxExporter.Export(current, stream); statusMessage = $"Exported to {path}"; }
                catch (Exception ex) { statusMessage = $"Export failed: {ex.Message}"; }
            });
        }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Import from XLSX"))
        {
            fileDialogManager.OpenFileDialog("Import Raffle", ".xlsx", (ok, paths) =>
            {
                if (!ok || paths.Count == 0) return;
                try
                {
                    using var stream = File.OpenRead(paths[0]);
                    var imported = RaffleXlsxImporter.Import(stream);
                    raffle.ImportRaffle(imported);
                    statusMessage = $"Imported \"{imported.Name}\" as a new local raffle.";
                }
                catch (Exception ex) { statusMessage = $"Import failed: {ex.Message}"; }
            }, 1);
        }
        UiKit.EndSectionCard();
    }

    private async Task PublishAsync(string raffleId, bool clearExclusions)
    {
        var result = await raffle.PublishAsync(raffleId, clearExclusions).ConfigureAwait(false);
        statusMessage = result.Success ? "Published successfully." : $"Publish failed: {result.Error}";
    }

    private async Task RefreshAsync(string raffleId)
    {
        var result = await raffle.FetchAsync(raffleId).ConfigureAwait(false);
        statusMessage = result.Success ? "Refreshed from backend." : $"Refresh failed: {result.Error}";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
