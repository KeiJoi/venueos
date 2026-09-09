using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>Brackets' operational screen (module ID <c>games.tournament</c>, display name "Brackets"). Draw()
/// renders live operation only — the tournament browser, setup (players/seeding), round-by-round match control, and
/// champion display; <see cref="DrawSettings"/> holds only persistent configuration (connection, credentials,
/// callout templates, creation defaults), per NEW_MODULE_GUIDE.md §8/§9. Donor workflow parity: create → players →
/// seed/reorder/randomize → start → operate rounds → record/correct winners → champion (see
/// TOURNAMENT_CONTROL_BRACKETS_AUDIT.md for the donor trace this mirrors).</summary>
internal sealed class TournamentControlOperatorPanel(TournamentControlService service, VenueProfileService venues)
{
    private readonly ConfirmDialog confirmDialog = new();
    private string search = "";
    private int statusFilterIndex;
    private static readonly string[] StatusFilters = ["All", "SETUP", "ACTIVE", "COMPLETED", "CANCELLED"];
    private bool showCreateForm;
    private string createGameName = "", createTournamentName = "", createEventDate = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    private string createStatus = "";
    private string singlePlayerName = "";
    private string bulkPlayers = "";
    private int selectedRoundIndex;
    private (string MatchId, string Message)? calloutFeedback;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var dashboard = service.Dashboard;

        UiKit.ConnectionBadge(theme, dashboard.IsAuthenticated ? "Organizer session active" : dashboard.IsConfigured ? "Not authenticated" : "Not configured", dashboard.IsAuthenticated);
        if (service.IsRealtimeConnected) { ImGui.SameLine(); UiKit.StatusBadge(theme, "Live updates connected", ToastLevel.Success); }

        if (!dashboard.IsAuthenticated)
        {
            ImGui.Spacing();
            UiKit.WarningState(theme, dashboard.IsConfigured ? "Not authenticated. Configure or refresh credentials in Settings → Modules → Brackets." : "Not configured yet. Set a server endpoint and credentials in Settings → Modules → Brackets.");
            if (dashboard.IsConfigured) { ImGui.Spacing(); if (UiKit.GhostButton(theme, "Retry Authentication")) service.AuthenticateAsync().GetAwaiter().GetResult(); }
            confirmDialog.Draw(theme);
            return;
        }

        ImGui.Spacing();
        if (service.Current is null) DrawBrowser(theme);
        else DrawTournament(theme, service.Current);

        confirmDialog.Draw(theme);
    }

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings;
        var connection = settings.Connection;

        UiKit.BeginSectionCard("brackets-connection", theme, "Connection");
        var endpoint = connection.BaseUrl;
        if (Forms.TextField(theme, "Server URL", ref endpoint, 256, "https://")) Save(settings with { Connection = connection with { BaseUrl = endpoint } });
        ImGui.Spacing();
        ImGui.TextWrapped("Server Access Password and Organizer Key are the standalone backend's shared setup password and this organizer's own login credential. Every credential below is a plain, readable, selectable/copyable text field — no password masking. The operator needs to read and copy these (to move between machines, or hand off), and masking a value the operator is expected to recover defeats that. This is a UI-presentation decision only — logs and Settings → Diagnostics still redact these exactly as before; \"visible in Settings\" is not \"safe to log\".");
        var password = connection.ServerAccessPassword ?? "";
        if (Forms.TextField(theme, "Server access password", ref password, 256)) Save(service.Settings with { Connection = service.Settings.Connection with { ServerAccessPassword = string.IsNullOrWhiteSpace(password) ? null : password } });
        var userKey = service.Settings.Connection.UserKey ?? "";
        if (Forms.TextField(theme, "Organizer key", ref userKey, 256)) Save(service.Settings with { Connection = service.Settings.Connection with { UserKey = string.IsNullOrWhiteSpace(userKey) ? null : userKey } });
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Authenticate")) service.AuthenticateAsync().GetAwaiter().GetResult();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Create Organizer")) confirmDialog.Request("Create a new organizer?", "This registers a brand-new organizer identity on the backend using the Server Access Password and Organizer Key entered above. Only do this once per organizer — creating another organizer does not migrate any existing tournaments.", () => service.CreateOrganizerAsync().GetAwaiter().GetResult());
        if (!string.IsNullOrWhiteSpace(service.Dashboard.Notice)) { ImGui.Spacing(); UiKit.WarningState(theme, service.Dashboard.Notice!); }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("brackets-defaults", theme, "New Tournament Defaults");
        ImGui.TextWrapped($"Venue name is taken from the active Venue Profile ({venues.Current.DisplayName}) at creation time and is never a separate setting here.");
        var defaultGame = settings.DefaultGameName;
        if (Forms.TextField(theme, "Default game name", ref defaultGame, 200)) Save(settings with { DefaultGameName = defaultGame });
        var defaultTournament = settings.DefaultTournamentName;
        if (Forms.TextField(theme, "Default tournament name", ref defaultTournament, 200)) Save(settings with { DefaultTournamentName = defaultTournament });
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("brackets-callouts", theme, "Match Callouts");
        var callouts = settings.Callouts;
        var channelIndex = (int)callouts.Channel;
        if (Forms.Segmented(theme, "brackets-callout-channel", ["Shout", "Yell"], ref channelIndex)) Save(settings with { Callouts = callouts with { Channel = (TournamentCalloutChannel)channelIndex } });
        var line1 = callouts.Line1;
        if (Forms.MultilineField(theme, "Line 1", ref line1, 512, 48)) Save(service.Settings with { Callouts = service.Settings.Callouts with { Line1 = line1 } });
        var line2 = callouts.Line2;
        if (Forms.MultilineField(theme, "Line 2", ref line2, 512, 48)) Save(service.Settings with { Callouts = service.Settings.Callouts with { Line2 = line2 } });
        var delay = callouts.DelaySeconds;
        if (Forms.NumericField(theme, "Delay between lines (seconds)", ref delay, 1, 1, 10)) Save(service.Settings with { Callouts = service.Settings.Callouts with { DelaySeconds = delay } });
        ImGui.TextDisabled("<1> = first player in match    <2> = second player in match");
        UiKit.EndSectionCard();

        confirmDialog.Draw(theme);
    }

    private void Save(TournamentModuleSettings settings) => service.Configure(settings);

    private void DrawBrowser(VenueTheme theme)
    {
        UiKit.BeginSectionCard("brackets-browser", theme, "Tournaments");
        Forms.SearchBox(theme, "brackets-search", ref search, "Search tournaments…", 240);
        ImGui.SameLine();
        var status = statusFilterIndex;
        if (Forms.ComboField(theme, "Status", StatusFilters, ref status, 140)) statusFilterIndex = status;
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Refresh")) service.RefreshTournamentListAsync(string.IsNullOrWhiteSpace(search) ? null : search, statusFilterIndex == 0 ? null : StatusFilters[statusFilterIndex]).GetAwaiter().GetResult();
        ImGui.TextDisabled(service.BrowserStatus);
        ImGui.Spacing();

        if (service.Tournaments.Count == 0) UiKit.EmptyState(theme, "No tournaments yet", "Create one below to get started.");
        else foreach (var item in service.Tournaments)
        {
            ImGui.PushID(item.Id);
            if (UiKit.ListRow(theme, $"{item.TournamentName} — {item.GameName}", $"{item.VenueName} · {item.EventDate.LocalDateTime:g} · {item.Status} · {item.PlayerCount} players", false))
                service.LoadStateAsync(item.Id).GetAwaiter().GetResult();
            var canDelete = TournamentDeleteEligibility.CanDelete(item.Status);
            ImGui.BeginDisabled(!canDelete);
            if (UiKit.DangerButton(theme, "Delete"))
            {
                var deleteId = item.Id; var deleteName = item.TournamentName;
                confirmDialog.Request("Delete Tournament?", $"\"{deleteName}\"\n\nThis permanently deletes the tournament and its bracket data. This cannot be undone.", () => service.DeleteTournamentAsync(deleteId).GetAwaiter().GetResult());
            }
            if (!canDelete) UiKit.Tooltip("Cancel this tournament before deleting it.");
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, showCreateForm ? "Cancel" : "+ New Tournament")) { showCreateForm = !showCreateForm; if (showCreateForm && string.IsNullOrWhiteSpace(createGameName)) createGameName = service.Settings.DefaultGameName; if (showCreateForm && string.IsNullOrWhiteSpace(createTournamentName)) createTournamentName = service.Settings.DefaultTournamentName; }
        if (showCreateForm)
        {
            ImGui.Spacing();
            UiKit.BeginSectionCard("brackets-create", theme, "New Tournament");
            ImGui.TextWrapped($"Venue: {venues.Current.DisplayName}");
            Forms.TextField(theme, "Game name", ref createGameName, 200);
            Forms.TextField(theme, "Tournament name", ref createTournamentName, 200);
            Forms.TextField(theme, "Event date (ISO 8601)", ref createEventDate, 64);
            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, "Create"))
            {
                if (!DateTimeOffset.TryParse(createEventDate, out var eventDate) || string.IsNullOrWhiteSpace(createGameName) || string.IsNullOrWhiteSpace(createTournamentName)) createStatus = "Game name, tournament name, and a valid ISO event date are required.";
                else
                {
                    var result = service.CreateTournamentAsync(createGameName.Trim(), createTournamentName.Trim(), eventDate).GetAwaiter().GetResult();
                    if (result.Success) { createStatus = ""; showCreateForm = false; service.LoadStateAsync(result.Value!.Tournament.Id).GetAwaiter().GetResult(); }
                    else createStatus = "Could not create the tournament; check the connection and try again.";
                }
            }
            if (!string.IsNullOrEmpty(createStatus)) { ImGui.Spacing(); UiKit.WarningState(theme, createStatus); }
            UiKit.EndSectionCard();
        }
    }

    private void DrawTournament(VenueTheme theme, TournamentControllerState state)
    {
        if (UiKit.GhostButton(theme, "← Back to browser")) { service.CloseCurrent(); return; }
        ImGui.SameLine();
        UiKit.StatusBadge(theme, state.Tournament.TournamentName, ToastLevel.Information);
        ImGui.SameLine();
        UiKit.StatusBadge(theme, state.Tournament.Status, state.Tournament.Status switch { "COMPLETED" => ToastLevel.Success, "CANCELLED" => ToastLevel.Warning, "ACTIVE" => ToastLevel.Success, _ => ToastLevel.Information });
        ImGui.SameLine();
        UiKit.StatusBadge(theme, $"Rev {state.Tournament.Revision}", ToastLevel.Information);
        if (!string.IsNullOrWhiteSpace(service.Notice)) { ImGui.Spacing(); UiKit.WarningState(theme, service.Notice!); }
        ImGui.Spacing();

        switch (state.Tournament.Status)
        {
            case "SETUP": DrawSetup(theme, state); break;
            case "ACTIVE": DrawActive(theme, state); break;
            case "COMPLETED": DrawCompleted(theme, state); break;
            default: UiKit.InfoBanner(theme, "Cancelled", "This tournament was cancelled.", ToastLevel.Warning); break;
        }

        if (state.Tournament.Status is "SETUP" or "ACTIVE")
        {
            ImGui.Spacing();
            if (UiKit.DangerButton(theme, "Cancel Tournament")) confirmDialog.Request("Cancel this tournament?", "This stops the tournament permanently. It cannot be resumed afterward.", () => service.CancelAsync().GetAwaiter().GetResult());
        }
        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Copy Public Bracket URL")) ImGui.SetClipboardText(PublicUrl(state.Tournament.PublicCode));
    }

    private void DrawSetup(VenueTheme theme, TournamentControllerState state)
    {
        UiKit.BeginSectionCard("brackets-players", theme, $"Players ({state.Contestants.Count})");
        Forms.TextField(theme, "Player name", ref singlePlayerName, 200);
        ImGui.SameLine();
        if (UiKit.PrimaryButton(theme, "Add") && !string.IsNullOrWhiteSpace(singlePlayerName)) { service.AddContestantAsync(singlePlayerName.Trim()).GetAwaiter().GetResult(); singlePlayerName = ""; }
        ImGui.Spacing();
        Forms.MultilineField(theme, "Bulk entry (one name per line)", ref bulkPlayers, 16000, 80);
        if (UiKit.GhostButton(theme, "Add Bulk Entries") && !string.IsNullOrWhiteSpace(bulkPlayers)) { service.BulkAddContestantsAsync(bulkPlayers).GetAwaiter().GetResult(); bulkPlayers = ""; }
        ImGui.Spacing();

        var ordered = state.Contestants.OrderBy(c => c.Seed).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var contestant = ordered[i];
            ImGui.PushID(contestant.Id);
            ImGui.TextUnformatted($"#{contestant.Seed}");
            ImGui.SameLine();
            var name = contestant.DisplayName;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("##name", ref name, 200) && name != contestant.DisplayName && !string.IsNullOrWhiteSpace(name)) service.RenameContestantAsync(contestant.Id, name.Trim()).GetAwaiter().GetResult();
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Up") && i > 0) { var ids = ordered.Select(c => c.Id).ToList(); (ids[i - 1], ids[i]) = (ids[i], ids[i - 1]); service.ReorderAsync(ids).GetAwaiter().GetResult(); }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Down") && i < ordered.Count - 1) { var ids = ordered.Select(c => c.Id).ToList(); (ids[i], ids[i + 1]) = (ids[i + 1], ids[i]); service.ReorderAsync(ids).GetAwaiter().GetResult(); }
            ImGui.SameLine();
            if (UiKit.DangerButton(theme, "Remove")) { var removedId = contestant.Id; var removedName = contestant.DisplayName; confirmDialog.Request("Remove this player?", $"\"{removedName}\" will be removed and remaining seeds renumbered. This cannot be undone.", () => service.RemoveContestantAsync(removedId).GetAwaiter().GetResult()); }
            ImGui.PopID();
        }
        UiKit.EndSectionCard();

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Randomize Seeds")) confirmDialog.Request("Randomize seed order?", "This replaces the current seed order for every player with a new random order.", () => service.RandomizeAsync().GetAwaiter().GetResult());
        ImGui.SameLine();
        ImGui.BeginDisabled(state.Contestants.Count < 2);
        if (UiKit.PrimaryButton(theme, "Start Tournament")) confirmDialog.Request("Start this tournament?", "The bracket will be generated from the current seed order shown above. Players cannot be added, removed, or reseeded after this.", () => service.StartAsync().GetAwaiter().GetResult());
        ImGui.EndDisabled();
        if (state.Contestants.Count < 2) { ImGui.Spacing(); UiKit.WarningState(theme, "At least two players are required to start."); }
    }

    private void DrawActive(VenueTheme theme, TournamentControllerState state)
    {
        var people = state.Contestants.ToDictionary(c => c.Id);
        var orderedRounds = state.Rounds.OrderBy(r => r.RoundNumber).ToList();
        if (orderedRounds.Count == 0) { UiKit.EmptyState(theme, "No rounds yet", "The bracket has not been generated."); return; }
        selectedRoundIndex = Math.Clamp(selectedRoundIndex, 0, orderedRounds.Count - 1);
        var roundNames = orderedRounds.Select(r => r.Name).ToArray();
        Forms.Segmented(theme, "brackets-rounds", roundNames, ref selectedRoundIndex);
        ImGui.Spacing();

        var round = orderedRounds[selectedRoundIndex];
        var matches = state.Matches.Where(m => m.RoundId == round.Id).OrderBy(m => m.Position).ToList();
        foreach (var match in matches) DrawMatch(theme, match, people);
    }

    private void DrawMatch(VenueTheme theme, TournamentMatch match, IReadOnlyDictionary<string, TournamentContestant> people)
    {
        ImGui.PushID(match.Id);
        UiKit.BeginSectionCard("match-" + match.Id, theme, $"Match {match.Position} · {match.Status}");
        people.TryGetValue(match.Player1Id ?? "", out var first);
        people.TryGetValue(match.Player2Id ?? "", out var second);
        ImGui.TextUnformatted((first is null ? "—" : $"#{first.Seed} {first.DisplayName}") + "  vs  " + (second is null ? "—" : $"#{second.Seed} {second.DisplayName}"));

        var actionable = match.Status is "READY" or "IN_PROGRESS" && first is not null && second is not null;
        if (actionable)
        {
            ImGui.Spacing();
            if (UiKit.GhostButton(theme, "Call Players")) calloutFeedback = (match.Id, service.SendMatchCallout(match.Id, first!.DisplayName, second!.DisplayName) ? "Callout sent." : "Could not send callout (check callout templates, or wait for the cooldown).");
            if (calloutFeedback is { } feedback && feedback.MatchId == match.Id) { ImGui.SameLine(); ImGui.TextDisabled(feedback.Message); }
            ImGui.Spacing();
            if (UiKit.PrimaryButton(theme, first!.DisplayName + " Wins")) { var matchId = match.Id; var winnerId = first.Id; var winnerName = first.DisplayName; confirmDialog.Request("Confirm winner?", $"Record {winnerName} as the winner of Match {match.Position}?", () => service.RecordWinnerAsync(matchId, winnerId).GetAwaiter().GetResult()); }
            ImGui.SameLine();
            if (UiKit.PrimaryButton(theme, second!.DisplayName + " Wins")) { var matchId = match.Id; var winnerId = second.Id; var winnerName = second.DisplayName; confirmDialog.Request("Confirm winner?", $"Record {winnerName} as the winner of Match {match.Position}?", () => service.RecordWinnerAsync(matchId, winnerId).GetAwaiter().GetResult()); }
        }
        else if (match.Status == "BYE" && match.WinnerId is { } byeWinnerId && people.TryGetValue(byeWinnerId, out var byePlayer))
        {
            UiKit.StatusBadge(theme, $"Auto-advance: {byePlayer.DisplayName}", ToastLevel.Information);
        }
        else if (match.WinnerId is { } winnerIdValue && first is not null && second is not null)
        {
            var winner = winnerIdValue == first.Id ? first : second;
            var other = winnerIdValue == first.Id ? second : first;
            UiKit.StatusBadge(theme, $"Winner: {winner.DisplayName}", ToastLevel.Success);
            ImGui.SameLine();
            if (service.RequiresRollbackConfirmation(match.Id))
            {
                if (UiKit.DangerButton(theme, "Correct Result (clears completed later matches)"))
                {
                    var matchId = match.Id; var otherId = other.Id; var otherName = other.DisplayName;
                    confirmDialog.Request("Correct this result?", $"Changing the winner to {otherName} will clear every already-completed match downstream of this one back to waiting, so their results can be replayed correctly. This cannot be undone.", () => service.CorrectAsync(matchId, otherId, true).GetAwaiter().GetResult());
                }
            }
            else if (UiKit.GhostButton(theme, "Correct Result"))
            {
                var matchId = match.Id; var otherId = other.Id; var otherName = other.DisplayName;
                confirmDialog.Request("Correct this result?", $"Change the winner of Match {match.Position} to {otherName}?", () => service.CorrectAsync(matchId, otherId, false).GetAwaiter().GetResult());
            }
        }
        else UiKit.StatusBadge(theme, "Waiting", ToastLevel.Information);

        UiKit.EndSectionCard();
        ImGui.PopID();
    }

    private void DrawCompleted(VenueTheme theme, TournamentControllerState state)
    {
        var final = state.Matches.FirstOrDefault(m => m.NextWinnerMatchId is null);
        var champion = final?.WinnerId is { } winnerId ? state.Contestants.FirstOrDefault(c => c.Id == winnerId) : null;
        if (champion is null) { UiKit.EmptyState(theme, "No champion recorded", "This tournament completed without a recorded winner."); return; }
        UiKit.StatCard(theme, "trophy", champion.DisplayName, $"Seed #{champion.Seed} · Champion", theme.Tokens.Success);
    }

    private string PublicUrl(string publicCode) => (service.Settings.Connection.BaseUrl ?? "").TrimEnd('/') + "/t/" + publicCode;
}
