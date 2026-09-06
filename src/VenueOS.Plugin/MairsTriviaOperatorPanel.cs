using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Services;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>Mair's Trivia's live operator workspace. Every action fires an async call and returns immediately —
/// nothing here blocks ImGui rendering on HTTP. Settings (connection, credentials, defaults) live in
/// <see cref="DrawSettings"/> only; this is exclusively live operation.</summary>
internal sealed class MairsTriviaOperatorPanel(MairsTriviaService service, VenueProfileService venues, IQuestionSetRepository library)
{
    private string newGameName = "";
    private string newSeriesName = "";
    private Guid selectedLibrarySetId;
    private readonly ConfirmDialog confirmDialog = new();
    private string adjustReason = "";
    private int adjustDelta;
    private Guid adjustPlayerId;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var dashboard = service.Dashboard;

        UiKit.BeginSectionCard("trivia-status", theme, "Mair's Trivia");
        UiKit.ConnectionBadge(theme, dashboard.IsConfigured ? (dashboard.IsAuthenticated ? "Connected" : "Configured — sign in via Settings") : "Not configured — set backend URL in Settings", dashboard.IsAuthenticated);
        if (dashboard.Busy) { ImGui.SameLine(); UiKit.StatusBadge(theme, "Working…", ToastLevel.Information); }
        if (service.LastError is not null) { ImGui.SameLine(); UiKit.StatusBadge(theme, service.LastError, ToastLevel.Error); }
        UiKit.EndSectionCard();
        confirmDialog.Draw(theme);

        if (!dashboard.IsAuthenticated) { ImGui.Spacing(); UiKit.WarningState(theme, "Sign in required — configure and sign in to the backend from Settings → Modules → Mair's Trivia before starting a game."); return; }

        ImGui.Spacing();
        if (service.CurrentGame is null && service.CurrentSeries is null) DrawSetup(theme);
        else DrawLiveConsole(theme);
    }

    /// <summary>Persistent configuration only (Settings = configuration, Draw = live operation, per NEW_MODULE_GUIDE.md §9). Never a module-local Venue Name — the active Venue Profile's display name is authoritative.</summary>
    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var settings = service.Settings; var connection = settings.Connection;

        UiKit.BeginSectionCard("trivia-settings-connection", theme, "Backend connection");
        var url = connection.BaseUrl; if (Forms.TextField(theme, "Backend URL", ref url, 256, "https://")) service.Configure(settings with { Connection = connection with { BaseUrl = url } });
        // Product decision: both credential fields below are deliberately plain, readable, selectable text — venue
        // staff need to copy/share connection configuration easily. Values still never appear in diagnostics/logs/
        // exception messages (DiagnosticsService.Redact and this service's error paths never interpolate them) —
        // this is a UI presentation choice only, not a change to how the values are otherwise treated as secrets.
        var accessPassword = connection.ServerAccessPassword ?? ""; if (Forms.TextField(theme, "Server-access password", ref accessPassword, 256)) service.Configure(service.Settings with { Connection = service.Settings.Connection with { ServerAccessPassword = accessPassword } });
        var username = service.Settings.Connection.Username ?? ""; if (Forms.TextField(theme, "Username", ref username, 128)) service.Configure(service.Settings with { Connection = service.Settings.Connection with { Username = username } });
        // Bound directly to persisted settings, exactly like the three fields above — not a local UI-only buffer.
        // That was the actual bug: a scratch variable that was never part of MairsTriviaSettings at all, so it could
        // never survive a reload no matter what Sign In did with it, and was explicitly cleared after every use.
        var password = connection.Password ?? ""; if (Forms.TextField(theme, "Password", ref password, 256)) service.Configure(service.Settings with { Connection = service.Settings.Connection with { Password = password } });
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Sign in") && !string.IsNullOrWhiteSpace(password)) _ = service.LoginAsync(service.Settings.Connection.Username ?? "", password);
        ImGui.SameLine(); if (UiKit.GhostButton(theme, "Create host account") && !string.IsNullOrWhiteSpace(password)) _ = service.RegisterAsync(service.Settings.Connection.Username ?? "", password);
        ImGui.SameLine(); if (UiKit.GhostButton(theme, "Refresh session")) _ = service.RefreshAsync();
        ImGui.SameLine(); if (UiKit.GhostButton(theme, "Sign out")) _ = service.LogoutAsync();
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("trivia-settings-defaults", theme, "Defaults");
        var gameName = service.Settings.DefaultGameName; if (Forms.TextField(theme, "Default game name", ref gameName, 128)) service.Configure(service.Settings with { DefaultGameName = gameName });
        string[] orderingOptions = ["inOrder", "shuffleOnce"]; var orderingIndex = Array.IndexOf(orderingOptions, service.Settings.OrderingMode); if (orderingIndex < 0) orderingIndex = 0;
        if (Forms.ComboField(theme, "Question order", orderingOptions, ref orderingIndex)) service.Configure(service.Settings with { OrderingMode = orderingOptions[orderingIndex] });
        var timer = service.Settings.QuestionTimeLimitSeconds; if (Forms.NumericField(theme, "Question timer (0–20s, 0 = untimed)", ref timer)) service.Configure(service.Settings with { QuestionTimeLimitSeconds = Math.Clamp(timer, 0, 20) });
        var scoring = service.Settings.DefaultScoring;
        var correct = scoring.CorrectPoints; if (Forms.NumericField(theme, "Correct points", ref correct)) service.Configure(service.Settings with { DefaultScoring = scoring with { CorrectPoints = correct } });
        var incorrect = scoring.IncorrectPoints; if (Forms.NumericField(theme, "Incorrect points", ref incorrect)) service.Configure(service.Settings with { DefaultScoring = scoring with { IncorrectPoints = incorrect } });
        var firstBonus = scoring.FirstCorrectBonus; if (Forms.NumericField(theme, "First-correct bonus", ref firstBonus)) service.Configure(service.Settings with { DefaultScoring = scoring with { FirstCorrectBonus = firstBonus } });
        var timeBonusMultiplier = scoring.TimeBonusMultiplier; if (Forms.NumericField(theme, "Time-bonus multiplier", ref timeBonusMultiplier)) service.Configure(service.Settings with { DefaultScoring = scoring with { TimeBonusMultiplier = timeBonusMultiplier } });
        UiKit.EndSectionCard();
    }

    private void DrawSetup(VenueTheme theme)
    {
        UiKit.BeginSectionCard("trivia-resume", theme, "Resume");
        if (UiKit.GhostButton(theme, "Refresh resumable games/series")) { _ = service.RefreshResumableGamesAsync(); _ = service.RefreshResumableSeriesAsync(); }
        foreach (var game in service.ResumableGames.Where(g => g.State != "finished"))
        { if (UiKit.ListRow(theme, $"{game.GameName} ({game.State})", game.VenueName, false)) _ = service.ResumeGameAsync(game.Id); }
        foreach (var series in service.ResumableSeriesList.Where(s => s.State != "finished"))
        { if (UiKit.ListRow(theme, $"Series: {series.Name}", series.State, false)) _ = service.ResumeSeriesAsync(series.Id); }
        if (service.ResumableGames.Count == 0 && service.ResumableSeriesList.Count == 0) UiKit.EmptyState(theme, "Nothing to resume", "Click refresh to check the backend for active or resumable games and series.");
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("trivia-picker", theme, "READY question sets");
        var ready = QuestionLibraryEntries().Where(e => e.Status == QuestionSetStatus.Ready).ToList();
        if (ready.Count == 0) UiKit.EmptyState(theme, "No READY question sets", "Author and save a set in Mair's Editor first — only READY sets can be attached to a game.");
        foreach (var entry in ready) if (UiKit.ListRow(theme, entry.Title, "READY", entry.Id == selectedLibrarySetId)) selectedLibrarySetId = entry.Id;
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("trivia-new-game", theme, "Standalone Game");
        Forms.TextField(theme, "Game name", ref newGameName, 128, service.Settings.DefaultGameName);
        if (UiKit.PrimaryButton(theme, "Create Game") && selectedLibrarySetId != Guid.Empty) _ = service.CreateStandaloneGameAsync(selectedLibrarySetId, string.IsNullOrWhiteSpace(newGameName) ? service.Settings.DefaultGameName : newGameName);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("trivia-new-series", theme, "Game Series (tournament-style, multiple Games, cumulative standings)");
        Forms.TextField(theme, "Series name", ref newSeriesName, 128, "Trivia Series");
        if (UiKit.PrimaryButton(theme, "Create New Series") && !string.IsNullOrWhiteSpace(newSeriesName)) _ = service.CreateSeriesAsync(newSeriesName);
        UiKit.EndSectionCard();
    }

    private IReadOnlyList<QuestionLibraryIndexEntry> QuestionLibraryEntries() => library.List();

    private void DrawLiveConsole(VenueTheme theme)
    {
        if (service.CurrentSeries is { } series) DrawSeriesConsole(theme, series);
        if (service.CurrentGame is { } game) DrawGameConsole(theme, game);
    }

    private void DrawSeriesConsole(VenueTheme theme, TriviaSeriesState series)
    {
        UiKit.BeginSectionCard("trivia-series", theme, $"Series: {series.Name} ({series.State})");
        var seriesActive = series.State == "active";
        if (seriesActive)
        {
            // The ONE persistent player-facing link for the whole Series lifetime — every Game started inside this
            // Series reuses this same code/link, so this is the only join link players ever need across the Series.
            UiKit.StatusBadge(theme, $"Series Join Code: {series.JoinCode}", ToastLevel.Information);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Copy Series Link")) ImGui.SetClipboardText(series.PlayerUrl);
        }
        ImGui.Spacing();
        UiKit.SectionHeader(theme, seriesActive ? "Series standings" : "Final series standings");
        foreach (var entry in series.Standings.OrderBy(e => e.Rank)) DrawStandingsRow(theme, entry.Rank, entry.DisplayName, entry.Score, entry.CorrectCount, entry.Answered);
        ImGui.Spacing();

        // Series Players — every active participant in the Series roster (including one who joined but hasn't
        // played a Game yet), separate from Series Standings above (which only ranks players who have scored) and
        // separate from the per-Game "Players" section in DrawGameConsole (which is Game-scoped, not Series-wide).
        UiKit.SectionHeader(theme, "Series Players");
        foreach (var participant in series.Participants)
        {
            UiKit.ListRow(theme, participant.DisplayName + (participant.Removed ? " (removed)" : ""), $"{participant.Score} pts · {participant.CorrectCount}/{participant.Answered} correct", false);
            if (seriesActive && !participant.Removed)
            {
                ImGui.SameLine();
                if (UiKit.GhostButton(theme, "Remove##series-" + participant.Id)) confirmDialog.Request("Remove from series?", $"{participant.DisplayName} will no longer be able to join future Games in this series. Historical results are preserved.", () => _ = service.RemoveFromSeriesAsync(participant.Id));
            }
        }
        ImGui.Spacing();
        if (seriesActive)
        {
            if (series.CurrentGameId is null || service.CurrentGame?.State == "finished")
            {
                Forms.TextField(theme, "Next game name", ref newGameName, 128, service.Settings.DefaultGameName);
                var ready = QuestionLibraryEntries().Where(e => e.Status == QuestionSetStatus.Ready).ToList();
                foreach (var entry in ready) if (UiKit.ListRow(theme, entry.Title, "READY", entry.Id == selectedLibrarySetId)) selectedLibrarySetId = entry.Id;
                if (UiKit.PrimaryButton(theme, "Start Next Game In Series") && selectedLibrarySetId != Guid.Empty) _ = service.StartNextGameInSeriesAsync(selectedLibrarySetId, string.IsNullOrWhiteSpace(newGameName) ? service.Settings.DefaultGameName : newGameName);
            }
            if (UiKit.DangerButton(theme, "End Series")) confirmDialog.Request("End series?", "This completes the series and crowns the Series Champion(s). This cannot be undone.", () => _ = service.EndSeriesAsync());
        }
        else
        {
            // The Series itself has ended — this is the one place a finished Series returns to setup, since
            // ReturnToSetup only clears CurrentSeries when it's actually finished (never an active one).
            UiKit.EmptyState(theme, "Series complete", "Final standings are shown above. Historical results remain on the backend.");
            if (UiKit.GhostButton(theme, "Back to Setup")) service.ReturnToSetup();
        }
        UiKit.EndSectionCard();
        ImGui.Spacing();
    }

    private void DrawGameConsole(VenueTheme theme, TriviaHostGameState game)
    {
        UiKit.BeginSectionCard("trivia-game", theme, $"Game: {game.GameName}");
        UiKit.StatusBadge(theme, game.State, ToastLevel.Information);
        if (service.CurrentSeries is null)
        {
            // Standalone Game: this Game's own join code/link IS the player-facing link — unchanged behavior.
            ImGui.SameLine(); UiKit.StatusBadge(theme, $"Join: {game.JoinCode}", ToastLevel.Information);
            ImGui.SameLine(); if (UiKit.GhostButton(theme, "Copy Link")) ImGui.SetClipboardText(game.PlayerUrl);
        }
        else
        {
            // Inside a Series, players never use a per-Game link — they use the ONE persistent Series link shown
            // in DrawSeriesConsole above. This Game's own join code is kept out of the primary UI so operators never
            // hand out the wrong (Game-scoped) link by mistake; it is shown small/marked internal for debugging only.
            ImGui.SameLine(); UiKit.StatusBadge(theme, $"Internal game code: {game.JoinCode} (players use the Series link above)", ToastLevel.Information);
        }

        if (game.State == "finished")
        {
            if (service.CurrentSeries is null)
            {
                // Standalone finished Game: this is the ONLY navigation control for this screen — Back to Setup
                // clears local context and returns to the normal setup workspace. It never calls End/Delete again,
                // never touches auth/session, and never touches the question library.
                UiKit.EmptyState(theme, "Game ended", "Final results are shown above. Historical results remain on the backend.");
                if (UiKit.GhostButton(theme, "Back to Setup")) service.ReturnToSetup();
            }
            else
            {
                // A Series exists — Series continuation (Start Next Game / End Series while active, or Back to
                // Setup once the Series itself has ended) is owned entirely by DrawSeriesConsole above, so there is
                // exactly one navigation control per state instead of two competing buttons on the same screen.
                UiKit.EmptyState(theme, "Game complete", service.CurrentSeries.State == "active" ? "Start the next game in the series above, or end the series." : "This was the final game of the series — see series results above.");
            }
            UiKit.EndSectionCard(); return;
        }

        // The only source of truth for which buttons are valid right now — mirrors the backend's own state guards
        // exactly (see TriviaActionEligibility.For), instead of duplicating "lobby or results" style checks inline.
        var eligibility = TriviaActionEligibility.For(game.State);
        var busy = service.Busy;
        ImGui.Spacing();
        if (eligibility.CanPreview) { if (UiKit.PrimaryButton(theme, "Preview") && !busy) _ = service.PreviewAsync(); ImGui.SameLine(); }
        if (eligibility.CanOpen) { if (UiKit.PrimaryButton(theme, "Open") && !busy) _ = service.OpenAsync(); ImGui.SameLine(); }
        if (eligibility.CanSkip) { if (UiKit.GhostButton(theme, "Skip") && !busy) _ = service.SkipAsync(); ImGui.SameLine(); }
        if (eligibility.CanClose) { if (UiKit.GhostButton(theme, "Close") && !busy) _ = service.CloseAsync(); ImGui.SameLine(); }
        if (eligibility.CanEndGame && UiKit.DangerButton(theme, "End Game") && !busy) confirmDialog.Request("End game?", "This ends the game for all connected players. It does not end the Series.", () => _ = service.EndGameAsync());

        if (game.State == "preview" && service.Preview is { } preview) { ImGui.Spacing(); UiKit.InfoBanner(theme, "Previewing (host only)", $"{preview.Question}\nCorrect answer: {preview.CorrectAnswer}"); }
        if (service.LastQuestionResult is { } result)
        {
            ImGui.Spacing();
            UiKit.InfoBanner(theme, "Last question result", $"Correct answer: {result.CorrectAnswer}" + (result.FirstResponder is { } first ? $"\nFirst correct: {first.DisplayName}" : "\nNo one answered correctly."));
            if (eligibility.CanRepeat)
            {
                var completedQuestionId = game.ActiveQuestionId;
                if (completedQuestionId is { } qid && UiKit.GhostButton(theme, "Repeat / Revisit this question") && !busy) _ = service.RepeatQuestionAsync(qid);
            }
        }

        ImGui.Spacing(); UiKit.SectionHeader(theme, "Game standings");
        foreach (var entry in game.Leaderboard.OrderBy(e => e.Rank)) DrawStandingsRow(theme, entry.Rank, entry.DisplayName, entry.Score, entry.CorrectCount, entry.Answered);

        ImGui.Spacing(); UiKit.SectionHeader(theme, "Players");
        foreach (var player in game.Players)
        {
            UiKit.ListRow(theme, player.DisplayName + (player.Removed ? " (removed)" : ""), $"{player.Score} pts · {player.CorrectCount}✓ {player.IncorrectCount}✗", false);
            if (!player.Removed)
            {
                ImGui.SameLine();
                if (UiKit.GhostButton(theme, "Kick##" + player.Id)) confirmDialog.Request("Kick player?", $"{player.DisplayName} will no longer be able to answer, but their earned score stays in this game's standings.", () => _ = service.KickPlayerAsync(player.Id));
                ImGui.SameLine();
                if (UiKit.GhostButton(theme, "Adjust##" + player.Id)) adjustPlayerId = player.Id;
            }
        }
        if (adjustPlayerId != Guid.Empty)
        {
            ImGui.Spacing();
            UiKit.BeginSectionCard("trivia-adjust", theme, "Manual score adjustment (reason required)");
            Forms.NumericField(theme, "Points (+/-)", ref adjustDelta);
            Forms.TextField(theme, "Reason", ref adjustReason, 256);
            if (UiKit.PrimaryButton(theme, "Apply adjustment") && adjustDelta != 0 && !string.IsNullOrWhiteSpace(adjustReason)) { _ = service.AdjustScoreAsync(adjustPlayerId, adjustDelta, adjustReason); adjustPlayerId = Guid.Empty; adjustDelta = 0; adjustReason = ""; }
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "Cancel")) { adjustPlayerId = Guid.Empty; adjustDelta = 0; adjustReason = ""; }
            UiKit.EndSectionCard();
        }
        UiKit.EndSectionCard();
    }

    /// <summary>Rank + name on the left, "correct/answered" then score right-aligned on the SAME line — UiKit.ListRow's
    /// subtitle renders as a separate line below the label, which is exactly what made the score read as an ambiguous,
    /// disconnected fragment ("#1 Kei Joi" then a lone "0" underneath). The right edge is captured before drawing
    /// anything on the row so it reflows correctly at any panel width. "Answered" is always correct+incorrect for
    /// THIS scope (Game or Series) — never a source-question-count denominator, so a late joiner is never shown as
    /// "behind". Player management controls (Kick/Adjust) intentionally stay out of this row — they belong in the
    /// Players / Series Players sections, not the standings rows.</summary>
    private static void DrawStandingsRow(VenueTheme theme, int rank, string displayName, int score, int correctCount, int answered)
    {
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var scoreText = $"{score} pts";
        var correctText = answered > 0 ? $"{correctCount}/{answered} correct  " : "";
        var scoreWidth = ImGui.CalcTextSize(scoreText).X + ImGui.CalcTextSize(correctText).X;
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted($"#{rank}  {displayName}");
        ImGui.PopStyleColor();
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), rightEdge - scoreWidth));
        if (correctText.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted(correctText);
            ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Accent));
        ImGui.TextUnformatted(scoreText);
        ImGui.PopStyleColor();
    }
}
