using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Tournament;

public enum TournamentCalloutChannel { Shout, Yell }
public sealed record TournamentCalloutSettings(TournamentCalloutChannel Channel = TournamentCalloutChannel.Shout, string Line1 = "<1> versus <2> — your match is ready!", string Line2 = "", int DelaySeconds = 2);

public sealed class TournamentCalloutService(SchedulerService scheduler, ChatCommandService chat)
{
    private CancellationTokenSource cancellation = new();
    private readonly HashSet<string> active = [];
    private readonly Dictionary<string, DateTimeOffset> lastStarted = [];

    public bool Send(string matchId, TournamentCalloutSettings settings, string player1, string player2)
    {
        if (string.IsNullOrWhiteSpace(matchId) || string.IsNullOrWhiteSpace(player1) || string.IsNullOrWhiteSpace(player2) || active.Contains(matchId) || (lastStarted.TryGetValue(matchId, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(2))) return false;
        var lines = new[] { Render(settings.Line1, player1, player2), Render(settings.Line2, player1, player2) }.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 500).ToArray();
        if (lines.Length == 0) return false;
        active.Add(matchId); lastStarted[matchId] = DateTimeOffset.UtcNow;
        var command = settings.Channel == TournamentCalloutChannel.Yell ? "/yell " : "/shout ";
        chat.Enqueue(new(command + lines[0], cancellation.Token));
        if (lines.Length > 1) scheduler.Schedule(TimeSpan.FromSeconds(Math.Clamp(settings.DelaySeconds, 1, 10)), () => { chat.Enqueue(new(command + lines[1], cancellation.Token)); active.Remove(matchId); }, cancellationToken: cancellation.Token);
        else active.Remove(matchId);
        return true;
    }

    public void Stop() { cancellation.Cancel(); cancellation.Dispose(); cancellation = new(); active.Clear(); }
    private static string Render(string template, string first, string second) => new string((template ?? "").Replace("<1>", first, StringComparison.Ordinal).Replace("<2>", second, StringComparison.Ordinal).Select(x => x is '\r' or '\n' ? ' ' : x).Where(x => !char.IsControl(x)).ToArray()).Trim();
}

// Deliberately no VenueName field here — NEW_MODULE_GUIDE.md §10/§21 flags a module-local editable Venue Name as a
// known inconsistency to fix during reconstruction. The venue name sent to the backend on tournament creation is
// read fresh from VenueProfileService.Current.DisplayName at request time (see TournamentControlService.ActiveVenueDisplayName),
// matching the pattern already proven by MairsTriviaService.
public sealed record TournamentModuleSettings(TournamentConnectionSettings Connection, string DefaultGameName, string DefaultTournamentName, TournamentCalloutSettings Callouts)
{ public static TournamentModuleSettings Default() => new(new(), "", "Tournament", new()); }

public sealed record TournamentDashboard(bool IsConfigured, bool IsAuthenticated, string? TournamentName, string? State, int? Revision, string? Notice);

/// <summary>Full operator-facing bracket controller: organizer session, tournament browser, setup (players/seeding),
/// live round operation (result recording, correction with deliberate rollback), and realtime reconciliation. Every
/// mutation is a thin pass-through to <see cref="TournamentControlClient"/> — the SQLite backend remains the sole
/// source of truth (NEW_MODULE_GUIDE.md §12); this service holds only the current venue's connection/credential
/// settings and the last-fetched authoritative snapshot, never a competing notion of bracket state.</summary>
public sealed class TournamentControlService(TournamentControlClient client, VenueProfileService profiles, TournamentCalloutService callouts, DiagnosticsService diagnostics, Func<ITournamentRealtimeTransport>? transportFactory = null)
{
    private CancellationTokenSource contextCancellation = new();
    private Guid venueId;
    private readonly TournamentRealtimeClient realtime = new(transportFactory ?? (() => new WebSocketTournamentRealtimeTransport()));

    public TournamentModuleSettings Settings { get; private set; } = TournamentModuleSettings.Default();
    public TournamentControllerState? Current { get; private set; }
    public string? Notice { get; private set; }
    public List<ControllerTournament> Tournaments { get; private set; } = [];
    public string BrowserStatus { get; private set; } = "Authenticate to load tournaments.";
    public bool IsRealtimeConnected => realtime.IsConnected;
    private string ActiveVenueDisplayName => profiles.Current.DisplayName; // Decision: never a module-local Venue Name field.

    public TournamentDashboard Dashboard => new(!string.IsNullOrWhiteSpace(Settings.Connection.BaseUrl), IsSessionValid, Current?.Tournament.TournamentName, Current?.Tournament.Status, Current?.Tournament.Revision, Notice);
    private bool IsSessionValid => Settings.Connection.SessionExpiresAt > DateTimeOffset.UtcNow && !string.IsNullOrWhiteSpace(Settings.Connection.AccessToken);

    public void Load(Guid nextVenue)
    {
        contextCancellation.Cancel(); contextCancellation.Dispose(); contextCancellation = new();
        callouts.Stop(); realtime.Stop();
        venueId = nextVenue; Current = null; Notice = null; Tournaments = []; BrowserStatus = "Authenticate to load tournaments.";
        Settings = profiles.GetModuleConfig(venueId, "games.tournament", 1, TournamentModuleSettings.Default);
    }

    public void Configure(TournamentModuleSettings settings) { Settings = settings; Save(); }

    public async Task<TournamentResult<TournamentSession>> CreateOrganizerAsync()
    {
        var result = await client.CreateOrganizerAsync(Settings.Connection, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success && result.Value is { } session) { Settings = Settings with { Connection = Settings.Connection with { AccessToken = session.AccessToken, SessionExpiresAt = session.ExpiresAt } }; Save(); }
        else if (!result.Success) diagnostics.RecordFailure($"games.tournament: organizer creation failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
        return result;
    }

    public async Task<TournamentResult<TournamentSession>> AuthenticateAsync()
    {
        var result = await client.AuthenticateAsync(Settings.Connection, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success && result.Value is { } session) { Settings = Settings with { Connection = Settings.Connection with { AccessToken = session.AccessToken, SessionExpiresAt = session.ExpiresAt } }; Save(); await RefreshTournamentListAsync().ConfigureAwait(false); }
        else diagnostics.RecordFailure($"games.tournament: authentication failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
        return result;
    }

    public async Task<TournamentResult<TournamentListResponse>> RefreshTournamentListAsync(string? query = null, string? status = null)
    {
        BrowserStatus = "Loading…";
        var result = await client.ListTournamentsAsync(Settings.Connection, query, status, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success && result.Value is { } list) { Tournaments = list.Tournaments ?? []; BrowserStatus = $"Loaded {Tournaments.Count} tournament(s)."; }
        else if (result.Error?.Code == "session_expired") { ClearSession(); BrowserStatus = "Session expired. Authenticate again."; }
        else { BrowserStatus = "Could not load tournaments."; diagnostics.RecordFailure($"games.tournament: list failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})"); }
        return result;
    }

    public async Task<TournamentResult<TournamentCreateResponse>> CreateTournamentAsync(string gameName, string tournamentName, DateTimeOffset eventDate)
    {
        var request = new TournamentCreateRequest(ActiveVenueDisplayName, gameName, tournamentName, eventDate);
        var result = await client.CreateAsync(Settings.Connection, request, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success) await RefreshTournamentListAsync().ConfigureAwait(false);
        else diagnostics.RecordFailure($"games.tournament: create failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
        return result;
    }

    /// <summary>Returns to the tournament browser without touching backend state — the tournament itself is
    /// untouched; a "Back to browser" action, not a cancel/delete.</summary>
    public void CloseCurrent() { realtime.Stop(); Current = null; Notice = null; }

    public async Task<TournamentResult<TournamentControllerState>> LoadStateAsync(string id)
    {
        var result = await client.GetStateAsync(Settings.Connection, id, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success) { Current = result.Value; Notice = null; EnsureRealtimeConnection(); }
        else if (result.Error?.Code == "session_expired") ClearSession();
        else diagnostics.RecordFailure($"games.tournament: load state failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
        return result;
    }

    /// <summary>Permanently deletes an organizer-owned tournament (SETUP/COMPLETED/CANCELLED only — the backend
    /// rejects ACTIVE; <see cref="TournamentDeleteEligibility"/> mirrors that client-side so the browser never even
    /// offers Delete for one). On success the tournament is removed from <see cref="Tournaments"/> immediately (no
    /// extra round trip required), and if it was the currently open tournament, the local snapshot is cleared and
    /// the realtime subscription is stopped — matching <see cref="CloseCurrent"/> exactly, so nothing keeps trying
    /// to reconcile against a tournament that no longer exists. On failure nothing is assumed: the authoritative
    /// list is refreshed so the browser reflects reality rather than a guess, and if the deleted-but-still-loaded
    /// tournament failed to delete, its state is refetched (falling back to closing it locally only if it turns out
    /// to genuinely be gone — e.g. deleted by another controller in the interim).</summary>
    public async Task<TournamentResult<object>> DeleteTournamentAsync(string id)
    {
        var result = await client.DeleteAsync(Settings.Connection, id, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success)
        {
            Tournaments = Tournaments.Where(t => t.Id != id).ToList();
            if (Current?.Tournament.Id == id) CloseCurrent();
        }
        else if (result.Error?.Code == "session_expired") ClearSession();
        else
        {
            diagnostics.RecordFailure($"games.tournament: delete failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
            await RefreshTournamentListAsync().ConfigureAwait(false);
            if (Current?.Tournament.Id == id)
            {
                var refetched = await client.GetStateAsync(Settings.Connection, id, contextCancellation.Token).ConfigureAwait(false);
                if (refetched.Success) Current = refetched.Value; else CloseCurrent();
            }
        }
        return result;
    }

    public Task<TournamentResult<TournamentControllerState>> AddContestantAsync(string name) => WithTournament(id => client.AddContestantAsync(Settings.Connection, id, Current!.Tournament.Revision, name, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> BulkAddContestantsAsync(string bulkText) => WithTournament(id => client.BulkAddContestantsAsync(Settings.Connection, id, Current!.Tournament.Revision, bulkText, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> RenameContestantAsync(string contestantId, string name) => WithTournament(id => client.RenameContestantAsync(Settings.Connection, id, contestantId, Current!.Tournament.Revision, name, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> RemoveContestantAsync(string contestantId) => WithTournament(id => client.RemoveContestantAsync(Settings.Connection, id, contestantId, Current!.Tournament.Revision, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> ReorderAsync(IReadOnlyList<string> contestantIds) => WithTournament(id => client.ReorderAsync(Settings.Connection, id, Current!.Tournament.Revision, contestantIds, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> RandomizeAsync() => WithTournament(id => client.RandomizeAsync(Settings.Connection, id, Current!.Tournament.Revision, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> StartAsync() => WithTournament(id => client.StartAsync(Settings.Connection, id, Current!.Tournament.Revision, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> RecordWinnerAsync(string matchId, string winnerId) => WithTournament(id => client.RecordWinnerAsync(Settings.Connection, id, matchId, Current!.Tournament.Revision, winnerId, contextCancellation.Token));
    public Task<TournamentResult<TournamentControllerState>> CancelAsync() => WithTournament(id => client.CancelAsync(Settings.Connection, id, Current!.Tournament.Revision, contextCancellation.Token));

    /// <summary>Mirrors the donor backend's own correction safety gate client-side (see
    /// <see cref="TournamentCorrectionAnalysis"/>) so the operator panel can decide up front whether to show a plain
    /// confirmation or the explicit destructive-rollback confirmation described in the reconstruction brief §6. The
    /// backend independently refuses a non-rollback correction over a completed descendant regardless.</summary>
    public bool RequiresRollbackConfirmation(string matchId) => Current is not null && TournamentCorrectionAnalysis.RequiresRollbackConfirmation(Current, matchId);

    public Task<TournamentResult<TournamentControllerState>> CorrectAsync(string matchId, string winnerId, bool rollbackDownstream) => WithTournament(id => client.CorrectAsync(Settings.Connection, id, matchId, Current!.Tournament.Revision, winnerId, rollbackDownstream, contextCancellation.Token));

    public async Task<TournamentResult<TournamentControllerState>> ApplyEventAsync(TournamentEventMessage message)
    {
        var decision = TournamentEventProtocol.Inspect(message, Current?.Tournament.Revision);
        if (decision.TokenExpired) { ClearSession(); Notice = decision.Reason; return TournamentResult<TournamentControllerState>.Failed("session_expired", decision.Reason!); }
        if (decision.ShouldRefetch && Current is not null) { Notice = decision.Reason; return await LoadStateAsync(Current.Tournament.Id).ConfigureAwait(false); }
        return Current is null ? TournamentResult<TournamentControllerState>.Failed("tournament_required", "No tournament is selected.") : new(true, Current);
    }

    public bool SendMatchCallout(string matchId, string player1, string player2) => callouts.Send(matchId, Settings.Callouts, player1, player2);

    /// <summary>Drains realtime frames queued by the background socket loop and reconciles after a reconnect. Called
    /// once per framework tick (<see cref="TournamentControlModule.Tick"/>) — never from the socket's own background
    /// thread, so module state is only ever mutated from the single Dalamud framework thread, matching every other
    /// module's threading model.</summary>
    public void Tick(DateTimeOffset now)
    {
        while (realtime.TryDequeue(out var message)) ApplyEventAsync(message).GetAwaiter().GetResult();
        if (realtime.ConsumeReconnectSignal() && Current is not null) LoadStateAsync(Current.Tournament.Id).GetAwaiter().GetResult();
    }

    private void EnsureRealtimeConnection()
    {
        if (Current is null || !IsSessionValid) { realtime.Stop(); return; }
        var socketUri = TournamentEventProtocol.SocketUri(Settings.Connection.BaseUrl);
        if (socketUri is null) { realtime.Stop(); return; }
        realtime.Start(socketUri, Settings.Connection.AccessToken!, Current.Tournament.PublicCode);
    }

    private async Task<TournamentResult<TournamentControllerState>> WithTournament(Func<string, Task<TournamentResult<TournamentControllerState>>> operation)
    {
        if (Current is null) return TournamentResult<TournamentControllerState>.Failed("tournament_required", "Load a tournament first.");
        var result = await operation(Current.Tournament.Id).ConfigureAwait(false);
        return await ReconcileAsync(result).ConfigureAwait(false);
    }

    private async Task<TournamentResult<TournamentControllerState>> ReconcileAsync(TournamentResult<TournamentControllerState> result)
    {
        if (result.Success) { Current = result.Value; Notice = null; return result; }
        if (result.Error?.Code == "stale_tournament" && Current is not null)
        {
            Notice = "Tournament changed elsewhere; refreshed authoritative state. Retry deliberately if still appropriate.";
            var state = await client.GetStateAsync(Settings.Connection, Current.Tournament.Id, contextCancellation.Token).ConfigureAwait(false);
            if (state.Success) { Current = state.Value; return state with { Error = result.Error, AuthoritativeState = state.Value }; }
        }
        if (result.Error?.Code == "session_expired") ClearSession();
        else diagnostics.RecordFailure($"games.tournament: operation failed ({DiagnosticsService.Redact(result.Error?.Message ?? "unknown")})");
        return result;
    }

    private void ClearSession() { Settings = Settings with { Connection = Settings.Connection with { AccessToken = null, SessionExpiresAt = null } }; realtime.Stop(); Save(); }
    private void Save() => profiles.SaveModuleConfig(venueId, "games.tournament", 1, Settings);
}

public sealed class TournamentControlModule(TournamentControlService tournament, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("games.tournament", "Brackets", "Backend-compatible single-elimination tournament bracket operations.", "trophy", DisplayOrder: 10);
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { tournament.Load(c.VenueId); return Task.CompletedTask; }
    public void Tick(DateTimeOffset now) => tournament.Tick(now);
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() { tournament.Load(Guid.Empty); return ValueTask.CompletedTask; }
}
