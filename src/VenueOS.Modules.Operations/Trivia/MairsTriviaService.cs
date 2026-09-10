using VenueOS.Core;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Trivia;

public sealed record MairsTriviaSettings(TriviaConnectionSettings Connection, string DefaultGameName, TriviaScoringRequest DefaultScoring, int QuestionTimeLimitSeconds = 0, string OrderingMode = "inOrder", Guid? SelectedQuestionSetId = null)
{
    // Decision 8 defaults: 100 correct / 0 incorrect / 50 first-correct; TriviaScoringRequest's own default supplies the 5x time-bonus multiplier.
    public static MairsTriviaSettings Default() => new(new(), "Trivia Night", new(100, 0, 50));
}

public sealed record MairsTriviaDashboard(bool IsConfigured, bool IsAuthenticated, string? GameState, Guid? ActiveQuestionId, string? GameName, bool Busy, string? LastError);

/// <summary>
/// Pure state-eligibility view over the authoritative backend game state. Computed once here and used identically by
/// the operator panel and by tests, so "which live actions are valid right now" is never duplicated as inline ImGui
/// conditionals (which cannot be unit-tested). Mirrors the backend's own guards exactly — e.g. End Game is allowed
/// from "preview" (only "question_open" blocks it server-side), which the previous inline panel logic missed.
/// </summary>
public readonly record struct TriviaActionEligibility(bool CanPreview, bool CanOpen, bool CanSkip, bool CanClose, bool CanRepeat, bool CanEndGame)
{
    public static TriviaActionEligibility For(string? gameState) => gameState switch
    {
        "lobby" => new(CanPreview: true, CanOpen: false, CanSkip: false, CanClose: false, CanRepeat: false, CanEndGame: true),
        "preview" => new(CanPreview: false, CanOpen: true, CanSkip: true, CanClose: false, CanRepeat: false, CanEndGame: true),
        "question_open" => new(CanPreview: false, CanOpen: false, CanSkip: false, CanClose: true, CanRepeat: false, CanEndGame: false),
        "results" => new(CanPreview: true, CanOpen: false, CanSkip: false, CanClose: false, CanRepeat: true, CanEndGame: true),
        _ => default, // "finished" or unknown/null — no live actions
    };
}

/// <summary>
/// Live Trivia orchestration: HTTP session, active Game, active Series, host action results. Every async operation
/// captures (venue, generation) before awaiting and discards its result if either changed while in flight — this is
/// what prevents a response from a previous venue/session from ever mutating current UI state, and what makes
/// venue-switch/module-disable safe without a manual "is this stale?" check at every call site.
/// </summary>
public sealed class MairsTriviaService(MairsTriviaClient client, VenueProfileService profiles, IQuestionSetRepository library, DiagnosticsService diagnostics)
{
    private CancellationTokenSource contextCancellation = new();
    private Guid venueId;
    private long generation;

    public MairsTriviaSettings Settings { get; private set; } = MairsTriviaSettings.Default();
    public TriviaHostGameState? CurrentGame { get; private set; }
    public TriviaSeriesState? CurrentSeries { get; private set; }
    public TriviaQuestion? Preview { get; private set; }
    public TriviaQuestionResult? LastQuestionResult { get; private set; }
    public bool Busy { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<TriviaGameSummary> ResumableGames { get; private set; } = [];
    public IReadOnlyList<TriviaSeriesSummary> ResumableSeriesList { get; private set; } = [];

    public MairsTriviaDashboard Dashboard => new(!string.IsNullOrWhiteSpace(Settings.Connection.BaseUrl), !string.IsNullOrWhiteSpace(Settings.Connection.AccessToken), CurrentGame?.State, CurrentGame?.ActiveQuestionId, CurrentGame?.GameName, Busy, LastError);

    /// <summary>Best-effort set of canonical set UUIDs attached to any non-finished game this host can currently see — the "in use" predicate Mair's Editor's delete confirmation checks against. Refreshed by <see cref="RefreshResumableGamesAsync"/>.</summary>
    public IReadOnlySet<Guid> ActiveSourceSetIds => new HashSet<Guid>(
        ResumableGames.Where(g => g.State != "finished").SelectMany(g => g.AttachedSourceSetIds)
        .Concat(CurrentGame is { State: not "finished" } cg ? cg.AttachedSourceSetIds : []));

    public void Load(Guid nextVenueId)
    {
        contextCancellation.Cancel(); contextCancellation.Dispose(); contextCancellation = new();
        Interlocked.Increment(ref generation);
        venueId = nextVenueId;
        CurrentGame = null; CurrentSeries = null; Preview = null; LastQuestionResult = null; Busy = false; LastError = null; ResumableGames = []; ResumableSeriesList = []; mutationInFlight = false; nextPoll = DateTimeOffset.MinValue; polling = false;
        Settings = profiles.GetModuleConfig(venueId, "games.trivia", 1, MairsTriviaSettings.Default);
        // Auto-connect: a stored refresh token means the operator already signed in before — silently renew the
        // access token now instead of requiring a manual "Sign In" click every plugin/venue load. This is the actual
        // reconnect behavior; without it, "connected" was only ever the appearance of a possibly-expired 15-minute
        // access token still being present in settings.
        if (!string.IsNullOrWhiteSpace(Settings.Connection.RefreshToken)) _ = RefreshAsync();
    }

    /// <summary>Stops local polling/requests only. The backend Game/Series is untouched and remains resumable — this must never send an End Game/Series call.</summary>
    public void Stop() { contextCancellation.Cancel(); Interlocked.Increment(ref generation); }

    /// <summary>Called when the module transitions from disabled back to enabled (see <see cref="MairsTriviaModule.IsEnabled"/>'s
    /// setter). <c>ModuleHost</c> skips <c>OnVenueChangedAsync</c>/<c>Tick</c> entirely for a disabled module (see
    /// <c>NEW_MODULE_GUIDE.md</c> §22), so <see cref="Load"/> — the only place <see cref="Settings"/>/the session
    /// token are re-synced to the active venue — is never called for any venue switch(es) that happen while Trivia
    /// is disabled. Without this, re-enabling resumed with whichever venue's connection/token this singleton
    /// service last loaded, potentially reused against a different now-active venue — a confirmed contributing
    /// cause of Trivia's intermittent "expired token" defect, distinct from (and in addition to) the refresh-race
    /// fixed in <see cref="RefreshAsync"/>. Always reloads for whichever venue is actually active right now.</summary>
    public void ReloadForCurrentVenue() => Load(profiles.Current.Id);

    // ---------------------------------------------------------------- polling ---
    private DateTimeOffset nextPoll;
    private bool polling;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2); // matches the donor's own 2s host-poll cadence; frequent enough for operator feedback without excessive Render traffic.

    /// <summary>Called every framework update for enabled modules (see <c>ModuleHost.Tick</c>/<c>Plugin.Update</c>) —
    /// never from Draw(), so this keeps running while the module is embedded, detached, or its window is closed, and
    /// stops automatically the moment the module is disabled (ModuleHost skips Tick for disabled modules). Follows
    /// the same self-gated pattern as VenueBingoService.Tick rather than a SchedulerService job: no separate job
    /// handle to track/cancel, and it already shares this exact call site.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (polling || mutationInFlight || now < nextPoll) return;
        // A finished Game is immutable — it never needs routine 2s polling merely to keep displaying its final
        // result, and doing so forever was the exact live bug: Busy toggled true/false on every poll, which is what
        // read as a perpetually flashing "Working" badge. A Series, however, can genuinely still be active after one
        // of its Games finishes (waiting for Start Next Game), so the two are tracked independently.
        var gameNeedsSync = CurrentGame is { State: not "finished" };
        var seriesNeedsSync = CurrentSeries is { State: not "finished" };
        if (!gameNeedsSync && !seriesNeedsSync) return;
        nextPoll = now + PollInterval;
        polling = true;
        _ = PollAsync().ContinueWith(_ => polling = false, TaskScheduler.Default);
    }

    /// <summary>One authoritative-state refresh pass: current game (players/standings/state/active question) and,
    /// independently, the Series projection used by the between-games console. Both go through the same
    /// generation-guarded, non-exclusive RunAsync path as every other read, so a stale response from a since-changed
    /// venue/session can never land, and this never contends with an in-flight host mutation. A finished Game/Series
    /// is skipped — it's immutable, so there is nothing authoritative left to re-read.</summary>
    public async Task PollAsync()
    {
        if (CurrentGame is { State: not "finished" }) await RefreshCurrentGameAsync().ConfigureAwait(false);
        if (CurrentSeries is { State: not "finished" }) await RefreshCurrentSeriesAsync().ConfigureAwait(false);
    }

    /// <summary>Local operator-navigation only — leaves a completed Game's results view. Never calls the backend
    /// (no End/Delete), never touches auth/session, never touches the question library, and never abandons a Series
    /// that is still active (Series continuation stays owned by Start Next Game / End Series). Bumps the generation
    /// so any poll or mutation still in flight for the game/series being left can never resurrect it once it
    /// completes — see IsCurrent, the same guard that already protects every other stale-response case.</summary>
    public void ReturnToSetup()
    {
        Interlocked.Increment(ref generation);
        CurrentGame = null;
        Preview = null;
        LastQuestionResult = null;
        if (CurrentSeries is { State: "finished" }) CurrentSeries = null; // only ever clears a Series that has itself already ended
    }

    public void Configure(MairsTriviaSettings settings) { Settings = settings; Save(); }
    private void Save() => profiles.SaveModuleConfig(venueId, "games.trivia", 1, Settings);
    private string ActiveVenueDisplayName => profiles.Current.DisplayName; // Decision: never a module-local Venue Name field.

    private bool IsCurrent(long capturedGeneration, Guid capturedVenue) => capturedGeneration == generation && capturedVenue == venueId;

    private bool mutationInFlight;

    /// <summary>Auth-failure codes the backend returns for an expired/invalid/revoked/missing bearer token — see
    /// TriviaService.parseAccess/authenticate. Deliberately NOT "invalid_login"/"invalid_server_access": those are
    /// literal credential rejections during an explicit sign-in attempt, not "my session expired mid-use", and must
    /// never trigger an automatic recovery+retry loop.</summary>
    private static readonly HashSet<string> AuthExpiredCodes = ["expired_token", "invalid_token", "revoked_token", "missing_token"];
    private Task<bool>? authRecovery;

    /// <summary>
    /// <paramref name="exclusiveMutation"/> (default true) makes this call a no-op — returning null without ever
    /// reaching the network — while another exclusive call is still in flight. This is what makes one operator click
    /// produce exactly one backend mutation: a second click on the same or a different action button while the first
    /// is still resolving is dropped here, not sent. Pass false only for genuinely independent read-only calls that
    /// are meant to run concurrently (the Resume screen's "refresh games" + "refresh series" pair) — never for
    /// anything that changes backend game/series state.
    ///
    /// <paramref name="showBusy"/> is a DELIBERATELY SEPARATE concept from <paramref name="exclusiveMutation"/>: it
    /// controls only the user-facing "Working…" badge (<see cref="Busy"/>), never mutation safety. Routine background
    /// polling (<see cref="RefreshCurrentGameAsync"/>/<see cref="RefreshCurrentSeriesAsync"/>, used by both
    /// <see cref="PollAsync"/> and the post-mutation authoritative resync) passes false here so a silent 2-second
    /// background sync never flashes "Working" — that flashing (toggling on/off every poll, forever, even after a
    /// Game finished) was the exact live bug. An explicit operator action (Preview, Open, Create Game, the manual
    /// "Refresh resumable games" button, etc.) always leaves this at its default of true.
    ///
    /// <paramref name="allowAuthRecovery"/> (default true): on an auth-expired failure, transparently attempts
    /// session recovery (refresh, falling back to a full login if stored credentials exist) and retries this SAME
    /// call exactly once before giving up — the operator must never see "Access token has expired" if that recovery
    /// succeeds. Recovery itself is single-flight (<see cref="EnsureAuthenticatedAsync"/>): if several requests hit
    /// an expired token around the same time (exactly the situation 2-second polling creates), only one refresh/login
    /// attempt is made and every caller awaits that same attempt, never a pile of concurrent refresh-token races.
    /// </summary>
    private async Task<T?> RunAsync<T>(Func<CancellationToken, Task<TriviaResult<T>>> call, string operationLabel, bool exclusiveMutation = true, bool showBusy = true, bool allowAuthRecovery = true) where T : class
    {
        if (exclusiveMutation && mutationInFlight) return null;
        var token = contextCancellation.Token; var capturedGeneration = generation; var capturedVenue = venueId;
        if (exclusiveMutation) mutationInFlight = true;
        if (showBusy) Busy = true;
        try
        {
            var result = await call(token).ConfigureAwait(false);
            if (!IsCurrent(capturedGeneration, capturedVenue)) return null; // stale: venue/session changed while this was in flight
            if (result.Success && result.Value is not null) { LastError = null; return result.Value; }
            if (allowAuthRecovery && result.Error is { } authError && AuthExpiredCodes.Contains(authError.Code))
            {
                var recovered = await EnsureAuthenticatedAsync(token).ConfigureAwait(false);
                if (!IsCurrent(capturedGeneration, capturedVenue)) return null; // venue/session moved on while recovery was in flight
                if (recovered)
                {
                    var retry = await call(token).ConfigureAwait(false);
                    if (!IsCurrent(capturedGeneration, capturedVenue)) return null;
                    if (retry.Success && retry.Value is not null) { LastError = null; return retry.Value; }
                    LastError = "Session expired and automatic sign-in failed.";
                    diagnostics.RecordFailure($"games.trivia: {operationLabel} failed after auth recovery ({DiagnosticsService.Redact(retry.Error?.Message ?? "unknown")})");
                    return null;
                }
                LastError = "Session expired and automatic sign-in failed.";
                diagnostics.RecordFailure($"games.trivia: {operationLabel} failed — session expired and automatic recovery failed");
                return null;
            }
            LastError = result.Error?.Message ?? "Request failed.";
            diagnostics.RecordFailure($"games.trivia: {operationLabel} failed ({DiagnosticsService.Redact(LastError)})");
            return null;
        }
        catch (OperationCanceledException) { return null; }
        finally { if (showBusy && IsCurrent(capturedGeneration, capturedVenue)) Busy = false; if (exclusiveMutation) mutationInFlight = false; }
    }

    /// <summary>Single-flight wrapper: if a recovery attempt is already running, every concurrent caller awaits that
    /// SAME task instead of starting its own — this is what prevents refresh-token rotation races and duplicate
    /// login attempts when several polls/mutations discover an expired token at nearly the same moment.</summary>
    private Task<bool> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        var existing = authRecovery;
        if (existing is not null && !existing.IsCompleted) return existing;
        var recovery = RecoverSessionAsync(generation, venueId, ct);
        authRecovery = recovery;
        return recovery;
    }

    /// <summary>Refresh first; if there is no refresh token or refresh itself fails, fall back to a full login using
    /// the persisted (visible, per product decision) Username/Password/ServerAccessPassword — never an infinite
    /// loop, since each of these is attempted at most once per recovery. Bypasses RunAsync entirely (calls the
    /// client directly) so this can never recursively trigger its own auth-recovery branch.</summary>
    private async Task<bool> RecoverSessionAsync(long capturedGeneration, Guid capturedVenue, CancellationToken ct)
    {
        // No explicit "clear authRecovery when done" step is needed: EnsureAuthenticatedAsync's own
        // `!existing.IsCompleted` check already treats a finished task as stale and starts a fresh one next time.
        if (!string.IsNullOrWhiteSpace(Settings.Connection.RefreshToken))
        {
            var refreshed = await client.RefreshAsync(Settings.Connection, ct).ConfigureAwait(false);
            if (!IsCurrent(capturedGeneration, capturedVenue)) return false;
            if (refreshed.Success && refreshed.Value is not null)
            {
                Configure(Settings with { Connection = Settings.Connection with { AccessToken = refreshed.Value.AccessToken, RefreshToken = refreshed.Value.RefreshToken } });
                return true;
            }
        }
        if (!string.IsNullOrWhiteSpace(Settings.Connection.Username) && !string.IsNullOrWhiteSpace(Settings.Connection.Password))
        {
            var loggedIn = await client.LoginAsync(Settings.Connection, Settings.Connection.Username!, Settings.Connection.Password!, ct).ConfigureAwait(false);
            if (!IsCurrent(capturedGeneration, capturedVenue)) return false;
            if (loggedIn.Success && loggedIn.Value is not null)
            {
                Configure(Settings with { Connection = Settings.Connection with { Username = loggedIn.Value.User.Username, AccessToken = loggedIn.Value.AccessToken, RefreshToken = loggedIn.Value.RefreshToken } });
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ auth ---
    public async Task<bool> RegisterAsync(string username, string password)
    {
        var value = await RunAsync(ct => client.RegisterAsync(Settings.Connection, username, password, ct), "register").ConfigureAwait(false);
        if (value is null) return false;
        // Password is persisted here too (not just via the Settings live-binding) so any future non-panel caller of
        // Register/Login still leaves a retrievable credential behind — never cleared on success, by product decision.
        Configure(Settings with { Connection = Settings.Connection with { Username = value.User.Username, Password = password, AccessToken = value.AccessToken, RefreshToken = value.RefreshToken } });
        return true;
    }
    public async Task<bool> LoginAsync(string username, string password)
    {
        var value = await RunAsync(ct => client.LoginAsync(Settings.Connection, username, password, ct), "sign in").ConfigureAwait(false);
        if (value is null) return false;
        Configure(Settings with { Connection = Settings.Connection with { Username = value.User.Username, Password = password, AccessToken = value.AccessToken, RefreshToken = value.RefreshToken } });
        return true;
    }
    /// <summary>Both the load-time auto-connect (<see cref="Load"/>) and the operator's manual "Refresh session"
    /// button call this SAME method, which routes through the single-flight <see cref="EnsureAuthenticatedAsync"/>
    /// path — the identical one the reactive 401-recovery branch of <see cref="RunAsync{T}"/> uses. This is a
    /// deliberate fix: this method used to call <c>client.RefreshAsync</c> directly, bypassing that single-flight
    /// guard entirely. The backend ROTATES the refresh token on every use (it invalidates the previous one), so two
    /// independent refresh calls racing each other — e.g. a venue-load auto-refresh overlapping a background poll's
    /// reactive 401 recovery, or the manual button clicked while a poll-triggered recovery is already in flight —
    /// could each send the same now-superseded refresh token, and whichever <c>Configure(...)</c> call landed second
    /// would silently discard the other's (possibly newer) token pair. This was the confirmed root cause of Mair's
    /// Trivia's intermittent "expired token" defect. Coalescing every refresh entry point onto one shared
    /// in-flight task (via <see cref="EnsureAuthenticatedAsync"/>'s existing <c>authRecovery</c> field) makes at
    /// most one <c>/v1/auth/refresh</c>/login call possible at a time, with every caller awaiting that same
    /// result — and <see cref="RecoverSessionAsync"/>'s existing <c>IsCurrent</c> check still prevents a
    /// venue-switch-stale response from ever landing.</summary>
    public async Task<bool> RefreshAsync()
    {
        var capturedGeneration = generation; var capturedVenue = venueId;
        Busy = true;
        try
        {
            var recovered = await EnsureAuthenticatedAsync(contextCancellation.Token).ConfigureAwait(false);
            return IsCurrent(capturedGeneration, capturedVenue) && recovered;
        }
        catch (OperationCanceledException) { return false; }
        finally { if (IsCurrent(capturedGeneration, capturedVenue)) Busy = false; }
    }
    /// <summary>Ends the session only — Password/ServerAccessPassword are durable connection configuration the operator
    /// explicitly wants to keep (Decision: readable, persistent credentials), never cleared by signing out.</summary>
    public async Task LogoutAsync()
    {
        await RunAsync(ct => client.LogoutAsync(Settings.Connection, ct), "sign out").ConfigureAwait(false);
        Configure(Settings with { Connection = Settings.Connection.WithoutSessionTokens() });
    }

    // -------------------------------------------------------------- resume ---
    public async Task RefreshResumableGamesAsync()
    {
        // Non-exclusive: a pure read that must be able to run alongside its series counterpart below, and must never block a live mutation.
        var value = await RunAsync(ct => client.GetGamesAsync(Settings.Connection, ct), "list games", exclusiveMutation: false).ConfigureAwait(false);
        if (value is not null) ResumableGames = value;
    }
    public async Task RefreshResumableSeriesAsync()
    {
        var value = await RunAsync(ct => client.GetSeriesListAsync(Settings.Connection, ct), "list series", exclusiveMutation: false).ConfigureAwait(false);
        if (value is not null) ResumableSeriesList = value;
    }
    /// <summary>Re-reads authoritative host state for the current game. Required after any action whose own response
    /// does not itself carry full host state (Preview/Repeat return only the previewed question) — VenueOS must
    /// never fabricate the resulting state locally.</summary>
    private async Task RefreshCurrentGameAsync()
    {
        if (CurrentGame is null) return;
        var value = await RunAsync(ct => client.GetGameAsync(Settings.Connection, CurrentGame.Id, ct), "refresh game state", exclusiveMutation: false, showBusy: false).ConfigureAwait(false);
        if (value is not null) CurrentGame = value;
    }
    /// <summary>Same recovery read as <see cref="RefreshCurrentGameAsync"/>, but for the stale-rejection recovery
    /// path: the refresh's own outcome (success or failure) must never overwrite the genuine mutation error the
    /// operator needs to see (e.g. "A question cannot be previewed now.") with null or an unrelated refresh error.</summary>
    private async Task RecoverCurrentGameAsync()
    {
        var operatorFacingError = LastError;
        await RefreshCurrentGameAsync().ConfigureAwait(false);
        LastError = operatorFacingError;
    }
    private async Task RecoverCurrentSeriesAsync()
    {
        var operatorFacingError = LastError;
        await RefreshCurrentSeriesAsync().ConfigureAwait(false);
        LastError = operatorFacingError;
    }
    private async Task RefreshCurrentSeriesAsync()
    {
        if (CurrentSeries is null) return;
        var value = await RunAsync(ct => client.GetSeriesAsync(Settings.Connection, CurrentSeries.Id, ct), "refresh series state", exclusiveMutation: false, showBusy: false).ConfigureAwait(false);
        if (value is not null) CurrentSeries = value;
    }
    public async Task<bool> ResumeGameAsync(Guid gameId)
    {
        var value = await RunAsync(ct => client.GetGameAsync(Settings.Connection, gameId, ct), "resume game").ConfigureAwait(false);
        if (value is null) return false;
        CurrentGame = value; return true;
    }
    public async Task<bool> ResumeSeriesAsync(Guid seriesId)
    {
        var value = await RunAsync(ct => client.GetSeriesAsync(Settings.Connection, seriesId, ct), "resume series").ConfigureAwait(false);
        if (value is null) return false;
        CurrentSeries = value;
        if (value.CurrentGameId is { } gameId) await ResumeGameAsync(gameId).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------- game setup ---
    public async Task<bool> CreateStandaloneGameAsync(Guid librarySetId, string gameName)
    {
        var set = library.Read(librarySetId);
        if (set is null || !set.IsValidHostSet()) { LastError = "The selected question set is not READY."; return false; }
        var request = new TriviaCreateGameRequest(ActiveVenueDisplayName, gameName, set, Settings.OrderingMode, Settings.DefaultScoring, Settings.QuestionTimeLimitSeconds, false);
        var value = await RunAsync(ct => client.CreateGameAsync(Settings.Connection, request, ct), "create game").ConfigureAwait(false);
        if (value is null) return false;
        CurrentGame = value; CurrentSeries = null; Preview = null; LastQuestionResult = null;
        return true;
    }

    public async Task<bool> CreateSeriesAsync(string name)
    {
        var value = await RunAsync(ct => client.CreateSeriesAsync(Settings.Connection, name, ct), "create series").ConfigureAwait(false);
        if (value is null) return false;
        CurrentSeries = value; CurrentGame = null; Preview = null; LastQuestionResult = null;
        return true;
    }

    public async Task<bool> StartNextGameInSeriesAsync(Guid librarySetId, string gameName)
    {
        if (CurrentSeries is null) { LastError = "No active series."; return false; }
        var set = library.Read(librarySetId);
        if (set is null || !set.IsValidHostSet()) { LastError = "The selected question set is not READY."; return false; }
        var request = new TriviaStartNextGameRequest(ActiveVenueDisplayName, gameName, set, Settings.OrderingMode, Settings.DefaultScoring, Settings.QuestionTimeLimitSeconds);
        var value = await RunAsync(ct => client.StartNextGameInSeriesAsync(Settings.Connection, CurrentSeries.Id, request, ct), "start next game").ConfigureAwait(false);
        if (value is null) return false;
        CurrentGame = value; Preview = null; LastQuestionResult = null;
        return true;
    }

    /// <summary>Attach-then-select as one host-facing operation: attach is idempotent by source-set UUID, so if select fails after a successful attach (e.g. a question was already open), retrying this call is always safe rather than leaving a falsely-successful partial state.</summary>
    public async Task<bool> SwitchQuestionSetAsync(Guid librarySetId)
    {
        if (CurrentGame is null) { LastError = "No active game."; return false; }
        var set = library.Read(librarySetId);
        if (set is null || !set.IsValidHostSet()) { LastError = "The selected question set is not READY."; return false; }
        var added = await RunAsync(ct => client.AddQuestionSetAsync(Settings.Connection, CurrentGame.Id, new(set, Settings.OrderingMode), ct), "attach question set").ConfigureAwait(false);
        if (added is null) return false;
        var value = await RunAsync(ct => client.SelectQuestionSetAsync(Settings.Connection, CurrentGame.Id, added.GameSetId, ct), "select question set").ConfigureAwait(false);
        if (value is null) return false; // attach succeeded, select failed — surfaced via LastError; retry is safe (attach will report reused:true).
        CurrentGame = value; Settings = Settings with { SelectedQuestionSetId = librarySetId }; Save();
        return true;
    }

    // ----------------------------------------------------------- live game ---
    public async Task<bool> PreviewAsync()
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.PreviewAsync(Settings.Connection, CurrentGame.Id, ct), "preview").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; } // a rejection (e.g. "no unused questions remain") may mean local state was stale — resync rather than stay stuck.
        Preview = value; LastQuestionResult = null;
        // The preview endpoint intentionally returns only the previewed question, not host state — without this,
        // CurrentGame.State silently stays "lobby" after a successful preview, which is the exact live bug this
        // fixes: the panel kept offering "Preview" (the lobby action) instead of "Open"/"Skip", and a second click
        // on the only visible button hit the backend while it was already in "preview", producing the observed
        // "A question cannot be previewed now." error on an otherwise-successful preview.
        await RefreshCurrentGameAsync().ConfigureAwait(false);
        return true;
    }
    public async Task<bool> RepeatQuestionAsync(Guid questionId)
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.RepeatQuestionAsync(Settings.Connection, CurrentGame.Id, questionId, ct), "repeat question").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; }
        Preview = value; LastQuestionResult = null;
        await RefreshCurrentGameAsync().ConfigureAwait(false); // same fix as PreviewAsync — repeat also returns only the question, not host state.
        return true;
    }
    public async Task<bool> OpenAsync()
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.OpenAsync(Settings.Connection, CurrentGame.Id, ct), "open question").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; }
        CurrentGame = value; Preview = null;
        return true;
    }
    public async Task<bool> SkipAsync()
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.SkipAsync(Settings.Connection, CurrentGame.Id, ct), "skip question").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; }
        CurrentGame = value; Preview = null;
        return true;
    }
    public async Task<bool> CloseAsync()
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.CloseAsync(Settings.Connection, CurrentGame.Id, ct), "close question").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; } // e.g. a stale "question_open" belief hitting "no open question exists" self-heals here instead of sticking.
        CurrentGame = value.Game; LastQuestionResult = value.Result;
        return true;
    }
    public async Task<TriviaGameCompleteResult?> EndGameAsync()
    {
        if (CurrentGame is null) return null;
        var value = await RunAsync(ct => client.EndAsync(Settings.Connection, CurrentGame.Id, ct), "end game").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return null; }
        CurrentGame = CurrentGame with { State = "finished" };
        return value;
    }
    public async Task<TriviaSeriesCompleteResult?> EndSeriesAsync()
    {
        if (CurrentSeries is null) return null;
        var value = await RunAsync(ct => client.EndSeriesAsync(Settings.Connection, CurrentSeries.Id, ct), "end series").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentSeriesAsync().ConfigureAwait(false); return null; }
        CurrentSeries = CurrentSeries with { State = "finished" };
        return value;
    }

    // ------------------------------------------------------- player management ---
    public async Task<bool> KickPlayerAsync(Guid playerId)
    {
        if (CurrentGame is null) return false;
        var value = await RunAsync(ct => client.KickPlayerAsync(Settings.Connection, CurrentGame.Id, playerId, ct), "kick player").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; }
        CurrentGame = value; return true;
    }
    public async Task<bool> RemoveFromSeriesAsync(Guid participantId)
    {
        if (CurrentSeries is null) return false;
        var value = await RunAsync(ct => client.RemoveFromSeriesAsync(Settings.Connection, CurrentSeries.Id, participantId, ct), "remove series participant").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentSeriesAsync().ConfigureAwait(false); return false; }
        CurrentSeries = value; return true;
    }
    /// <summary>Reason is required — an unexplained score change is never persisted (the backend rejects a blank reason).</summary>
    public async Task<bool> AdjustScoreAsync(Guid playerId, int delta, string reason)
    {
        if (CurrentGame is null || string.IsNullOrWhiteSpace(reason)) return false;
        var value = await RunAsync(ct => client.AdjustScoreAsync(Settings.Connection, CurrentGame.Id, playerId, new(delta, reason), ct), "adjust score").ConfigureAwait(false);
        if (value is null) { await RecoverCurrentGameAsync().ConfigureAwait(false); return false; }
        CurrentGame = value; return true;
    }
}

public sealed class MairsTriviaModule(MairsTriviaService trivia, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    private bool isEnabled = true;

    public ModuleDescriptor Descriptor { get; } = new("games.trivia", "Mair's Trivia", "Live host trivia operation.", "circle-question", DisplayOrder: 6);

    /// <summary>Custom setter (matching <c>ShoutRunnerModule.IsEnabled</c>'s precedent) — see
    /// <see cref="MairsTriviaService.ReloadForCurrentVenue"/>'s doc comment for why re-enabling must force a fresh
    /// load rather than resuming from whatever venue this singleton service last had loaded while disabled.</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value) return;
            isEnabled = value;
            if (value) trivia.ReloadForCurrentVenue();
        }
    }

    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { trivia.Load(c.VenueId); return Task.CompletedTask; }
    public void Tick(DateTimeOffset now) => trivia.Tick(now);
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() { trivia.Stop(); return ValueTask.CompletedTask; }
}
