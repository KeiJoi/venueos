using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Bingo;

/// <summary>Which command the operator uses to roll the next ball. Persisted per-venue (Settings), not re-selected
/// every session. `/dice`'s exact chat-result text has never been exercised for Bingo by the donor or this codebase
/// — see <see cref="VenueBingoService.HandleChatText"/>'s doc comment: NEEDS LIVE VERIFICATION.</summary>
public enum BingoRollCommand { Random, Dice }

/// <summary>Where the called ball is announced. `None` deliberately skips any outgoing chat command.</summary>
public enum BingoAnnounceChannel { Shout, Yell, Party, None }

/// <summary>Progressive-phase split defaults. Wiring these into the backend's opaque `progressive` JSON blob is
/// deferred — the pre-existing scaffold already never populated <see cref="BingoProgressive"/> (it always sent
/// `null`, see the old <c>VenueBingoService.SyncAsync</c>), so this is not a regression; these fields exist so a
/// venue's progressive-game intent survives across sessions even though this pass doesn't wire the exact nested
/// backend shape for it.</summary>
public sealed record BingoProgressiveDefaults(bool Enabled = false, double PhaseOneSplit = 100, double PhaseTwoSplit = 0, double PhaseThreeSplit = 0);

/// <summary>The venue's persistent, reusable Bingo defaults — Settings → Modules → Bingo (NEW_MODULE_GUIDE.md §9).
/// Editing one of these fields only ever updates this default; it must never retroactively rewrite an already-locked
/// active game's settings (see <see cref="VenueBingoActiveGame.Snapshot"/>).</summary>
public sealed record VenueBingoDefaults(BingoConnectionSettings Connection, int CostPerCard, int StartingPot, double PrizePercentage, string Letters, string GameType, BingoProgressiveDefaults Progressive, BingoColors Colors, BingoRollCommand RollCommand, BingoAnnounceChannel AnnounceChannel)
{
    public static VenueBingoDefaults Default() => new(new(), 0, 0, 100, "BINGO", "Single Line", new(), new(), BingoRollCommand.Random, BingoAnnounceChannel.Shout);
}

/// <summary>The settings snapshot actually sent when a game was created — independent of later default edits, since
/// cost/pot/percentage/letters/colors are locked (backend-enforced, docs/BINGO_V2_PROTOCOL.md §5) once a v2 room
/// transitions Draft -> Active.</summary>
public sealed record VenueBingoGameSnapshot(int CostPerCard, int StartingPot, double PrizePercentage, string GameType, string Letters, BingoColors Colors);

/// <summary>Live, in-memory game state — NOT persisted the same way <see cref="VenueBingoDefaults"/> is (it is
/// rebuilt from the backend on every <see cref="VenueBingoService.PollAsync"/>, and reset to <see cref="Empty"/> on
/// every venue switch). <see cref="Lifecycle"/> is null when there is no active/observed game at all.
/// <see cref="BingoCallers"/>/<see cref="CurrentPrizePool"/>/<see cref="SplitAmount"/> are additive, backend-driven
/// payout state (product correction — the host never manually picks a winner); they default to empty/zero for a
/// Legacy-lifecycle room, which predates the caller/split concept entirely (see
/// <see cref="VenueBingoService.ApplyLegacySnapshot"/>) — an acceptable limitation for legacy rooms, not a bug.
/// <see cref="Daubs"/> (live-QA correction — host Card Viewer sync) is backend-authoritative per-seed-per-card
/// daubed-ball-number state, keyed exactly as the wire shape is (seed -> card index AS STRING -> daubed numbers);
/// mapped for BOTH v2 and Legacy rooms (the backend returns it on both), unlike the v2-only caller/split fields.
/// <see cref="BallsToBingo"/> is v2-only (server.js's computeBallsToBingo has no legacy equivalent), defaulting to
/// empty for a Legacy room exactly like <see cref="BingoCallers"/> does.</summary>
public sealed record VenueBingoActiveGame(string RoomCode, string RoomKey, string? Lifecycle, VenueBingoGameSnapshot? Snapshot, BingoPot? Pot, Dictionary<string, BingoPlayer> Players, List<int> CalledNumbers, List<BingoPayoutObligation> Payouts, List<BingoCaller> BingoCallers, int CurrentPrizePool, int SplitAmount, Dictionary<string, Dictionary<string, List<int>>> Daubs, Dictionary<string, int> BallsToBingo)
{
    public static VenueBingoActiveGame Empty() => new("", "", null, null, null, new(), new(), new(), new(), 0, 0, new(), new());
}

public sealed record BingoDashboard(bool IsConfigured, bool IsConnected, bool IsGameActive, int PlayerCount, int NumbersCalled, string? RoomCode, string? Status);

/// <summary>
/// Live Bingo host orchestration: per-venue defaults, the active/observed game's authoritative state (mirrored from
/// the backend, never recomputed locally — docs/BINGO_V2_PROTOCOL.md §4's "backend is authoritative" rule), and the
/// number-calling/card-grant/payout-obligation actions a host can take. Supports BOTH a VenueOS-created v2 game and
/// resuming/observing a legacy-style room the standalone plugin created (<see cref="PollAsync"/> falls back to the
/// legacy room-state read when <see cref="VenueBingoActiveGame.Lifecycle"/> is unknown).
/// </summary>
public sealed class VenueBingoService(VenueBingoClient client, VenueProfileService profiles, DiagnosticsService diagnostics, ChatCommandService chat)
{
    public const string ModuleId = "games.bingo";

    private static readonly TimeSpan RollTimeout = TimeSpan.FromSeconds(10);

    private CancellationTokenSource contextCancellation = new();
    private Guid venueId;
    private DateTimeOffset nextPoll;
    private bool polling;
    private BingoPendingRoll? pendingRoll;

    public VenueBingoDefaults Defaults { get; private set; } = VenueBingoDefaults.Default();
    public VenueBingoActiveGame ActiveGame { get; private set; } = VenueBingoActiveGame.Empty();
    public string? Status { get; private set; }

    /// <summary>Fires a short, secret-free string at each meaningful roll/call/announcement transition — accepted
    /// only by whoever wants to bridge it into Dalamud's own log (Plugin.cs wires this to <c>IPluginLog.Debug</c>),
    /// never the user-facing Settings → Diagnostics panel (that stays reserved for actual failures via
    /// <see cref="DiagnosticsService.RecordFailure"/>). This is deliberately event-transition-only, never per-frame
    /// — see the completion report's "live diagnostic support" section for why this exists. Every raised message is
    /// also appended to <see cref="RecentRollDiagnostics"/> so the operator can see the exact same trace directly in
    /// the Bingo panel, without needing to find/read Dalamud's own log file — this was added specifically because a
    /// prior live test failed with only "0 called" to go on, giving no way to tell whether the failure was at the
    /// chat event, the parser, the correlator, the backend, or the announcement.</summary>
    public event Action<string>? DiagnosticEvent;

    private readonly Queue<string> recentRollDiagnostics = new();
    private const int MaxRecentRollDiagnostics = 40;

    /// <summary>The most recent roll-pipeline diagnostic lines (oldest first), capped at
    /// <see cref="MaxRecentRollDiagnostics"/> — read-only, per-session (not persisted). Cleared on venue switch
    /// (<see cref="Load"/>) so a resumed/new venue starts with a clean trace.</summary>
    public IReadOnlyList<string> RecentRollDiagnostics => recentRollDiagnostics.ToArray();

    private void RaiseDiagnostic(string message)
    {
        recentRollDiagnostics.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} {message}");
        while (recentRollDiagnostics.Count > MaxRecentRollDiagnostics) recentRollDiagnostics.Dequeue();
        DiagnosticEvent?.Invoke(message);
    }

    /// <summary>Wired from <c>VenueOS.Plugin.Bingo.BingoRollChatAdapter</c> — lets the adapter (which sees the raw
    /// chat event, LogKind, Sender/Message text, PlayerPayload presence, and its own parse/local-host decisions
    /// BEFORE this service ever sees a normalized <see cref="BingoRollObservation"/>) contribute to the same
    /// diagnostic trace. This is the piece that makes the trace cover the FULL pipeline (chat event → parser →
    /// correlator → backend → announcement), not just the half this service owns.</summary>
    public void RecordAdapterDiagnostic(string message) => RaiseDiagnostic(message);

    /// <summary>True while a roll is outstanding — the adapter uses this to bound its own diagnostic logging to the
    /// actual AwaitingRoll window (per the product requirement: do not log every chat message forever) rather than
    /// logging unconditionally for the plugin's entire lifetime.</summary>
    public bool IsAwaitingRoll => pendingRoll is { Consumed: false };

    public BingoRollMode? PendingRollMode => pendingRoll is { Consumed: false } p ? p.Mode : null;

    /// <summary>Backend-authoritative daubed ball numbers for one seed's card (live-QA correction — host Card
    /// Viewer synchronization). Reads straight from <see cref="VenueBingoActiveGame.Daubs"/>, which is refreshed on
    /// every <see cref="PollAsync"/>/<see cref="Tick"/> poll like every other authoritative field — there is no
    /// independent local daub truth anywhere in this service. Returns an empty list (never throws) for a seed/card
    /// with no daub state yet.</summary>
    public IReadOnlyList<int> GetDaubedNumbers(string seed, int cardIndex) =>
        ActiveGame.Daubs.TryGetValue(seed, out var byCard) && byCard.TryGetValue(cardIndex.ToString(), out var numbers) ? numbers : [];

    // --- Bingo Call Alert (live-QA addition): a host-attention layer over the backend's already-authoritative
    // BingoCallers list — this never introduces a second winner state, it only tracks which authoritative caller
    // OCCURRENCES the local host has already been alerted about. Keyed by (Seed, Phase) — the backend's own
    // stable identity for one caller occurrence (a seed can only appear once per phase, and its first-recorded
    // timestamp never changes — "first timestamp wins") — deliberately NOT display name, which could collide or
    // change. ---
    private bool callerAlertBaselineEstablished;
    private readonly HashSet<string> acknowledgedCallerKeys = new(StringComparer.Ordinal);
    private readonly List<BingoCaller> pendingAlertCallers = new();

    /// <summary>Authoritative Bingo callers the local host has not yet dismissed — drives
    /// <c>VenueOS.Plugin.Bingo.BingoCallAlertWindow</c>'s visibility directly (that window shows nothing at all
    /// when this is empty; there is no separate "is the alert open" flag to fall out of sync with this list).
    /// Populated only by <see cref="ApplySnapshot"/> detecting a caller key that was not already
    /// acknowledged/baselined — see this class's "Bingo Call Alert" region doc comment for the exact baseline and
    /// key semantics.</summary>
    public IReadOnlyList<BingoCaller> PendingAlertCallers => pendingAlertCallers;

    private static string CallerAlertKey(BingoCaller caller) => $"{caller.Seed}|{caller.Phase}";

    /// <summary>Resets alert tracking — called whenever this service attaches to a (possibly different, possibly
    /// the same) room: <see cref="Load"/> (venue switch), <see cref="ObserveExistingRoom"/> (resume/host-handoff),
    /// and <see cref="CreateGameAsync"/> (brand new room). The very NEXT snapshot received after this becomes the
    /// baseline — every caller already present in it is marked acknowledged WITHOUT alerting (avoids popping stale
    /// alerts for every historical caller when resuming a room that already had some), and only a caller key that
    /// appears in a LATER snapshot and was not part of that baseline ever triggers an alert.</summary>
    private void ResetCallerAlertTracking()
    {
        callerAlertBaselineEstablished = false;
        acknowledgedCallerKeys.Clear();
        pendingAlertCallers.Clear();
    }

    /// <summary>Diffs a freshly-received authoritative caller list against what this host has already
    /// baselined/acknowledged, adding any genuinely new occurrence to <see cref="pendingAlertCallers"/>. Called
    /// from every <see cref="ApplySnapshot"/> (the only place <c>BingoCallers</c> is refreshed) — deliberately
    /// NOT dependent on comparing against the immediately-previous poll's list, so a missed poll or dropped event
    /// can never suppress an alert: whenever the NEXT successful snapshot arrives, it is still compared against
    /// the full cumulative acknowledged set, not just "what changed since last time."</summary>
    private void DetectNewBingoCallers(IReadOnlyList<BingoCaller> latestCallers)
    {
        if (!callerAlertBaselineEstablished)
        {
            foreach (var caller in latestCallers) acknowledgedCallerKeys.Add(CallerAlertKey(caller));
            callerAlertBaselineEstablished = true;
            return;
        }
        foreach (var caller in latestCallers)
            if (acknowledgedCallerKeys.Add(CallerAlertKey(caller))) pendingAlertCallers.Add(caller);
    }

    /// <summary>Local host acknowledgment ONLY (product correction — must never be confused with backend state):
    /// removes one caller from <see cref="PendingAlertCallers"/>. Never calls the backend, never mutates
    /// <see cref="VenueBingoActiveGame.BingoCallers"/>/Payouts/CalledNumbers/Daubs, never Leaves the game or closes
    /// the room — the backend's caller history and payout ledger are completely unaffected by dismissal.</summary>
    public void DismissBingoAlert(string seed, int? phase) => pendingAlertCallers.RemoveAll(c => c.Seed == seed && c.Phase == phase);

    /// <summary>Acknowledges every currently pending caller at once (the alert window's own close button) — same
    /// local-only contract as <see cref="DismissBingoAlert"/>.</summary>
    public void DismissAllBingoAlerts() => pendingAlertCallers.Clear();

    /// <summary>Live-diagnostics helper (product correction — the operator's next test must show exactly which
    /// pipeline stage a chat event was evaluated against): a short, secret-free description of the current pending
    /// roll, if any, for the adapter to log alongside every raw chat fact it records. Read BEFORE parsing/evaluating
    /// a candidate message, so a diagnostic trace can answer "was a roll actually pending at all when this event
    /// arrived" independent of whatever the correlator later decides.</summary>
    public string DescribePendingRoll() => pendingRoll switch
    {
        null => "pending=no",
        { Consumed: true } p => $"pending=yes(consumed) mode={p.Mode} attempt={p.AttemptId} deadline={p.Deadline:HH:mm:ss.fff}",
        { } p => $"pending=yes mode={p.Mode} attempt={p.AttemptId} deadline={p.Deadline:HH:mm:ss.fff}",
    };

    public BingoDashboard Dashboard => new(
        !string.IsNullOrWhiteSpace(Defaults.Connection.ServerUrl),
        !string.IsNullOrWhiteSpace(ActiveGame.RoomCode) && ActiveGame.Lifecycle is not null,
        ActiveGame.Lifecycle == "Active",
        ActiveGame.Players.Count,
        ActiveGame.CalledNumbers.Count,
        string.IsNullOrWhiteSpace(ActiveGame.RoomCode) ? null : ActiveGame.RoomCode,
        Status);

    /// <summary>Called on every venue activation (including the first one at startup) — resets in-memory active-game
    /// state and reloads this venue's persistent defaults. Never carries an active game across a venue switch.</summary>
    public void Load(Guid nextVenue)
    {
        contextCancellation.Cancel(); contextCancellation.Dispose(); contextCancellation = new();
        venueId = nextVenue; polling = false; nextPoll = default; Status = null;
        ActiveGame = VenueBingoActiveGame.Empty();
        Defaults = profiles.GetModuleConfig(venueId, ModuleId, 1, VenueBingoDefaults.Default);
        recentRollDiagnostics.Clear();
        ResetCallerAlertTracking();
    }

    /// <summary>Persists a new set of venue defaults. Deliberately the ONLY method that writes
    /// <see cref="VenueBingoDefaults"/> to storage — <see cref="CreateGameAsync"/> reads defaults but never calls
    /// this, so a one-off game-creation value can never silently become the new persistent default.</summary>
    public void SaveDefaults(VenueBingoDefaults defaults) { Defaults = defaults; profiles.SaveModuleConfig(venueId, ModuleId, 1, Defaults); }

    /// <summary>Creates a new v2 Draft room from the current defaults, using the active Venue Profile's display name
    /// as the event/venue label — never a Bingo-local "Venue Name" setting (NEW_MODULE_GUIDE.md §10). Fails safe: on
    /// any error (including a 409 if the generated room code somehow collides) this surfaces via
    /// <see cref="DiagnosticsService"/> and returns false; it never retries with different values.
    ///
    /// Room Key semantics (product correction, see git history around this comment): Room Key is a PERSISTENT
    /// per-venue host credential and room-grouping key — the same value the venue's other games already use and the
    /// value <see cref="ListVenueRoomsAsync"/> lists rooms by — never a value minted fresh per game. A new game gets
    /// a brand-new <c>RoomCode</c> (that part IS meant to be unique per game) but always inherits the venue's
    /// currently-configured <see cref="VenueBingoDefaults.Connection"/>'s <c>RoomKey</c> unchanged. If none is
    /// configured yet, this fails with a clear message rather than silently minting a throwaway key that would make
    /// the game invisible to the venue's own room listing/resume workflow (host handoff depends on every game a venue
    /// creates sharing one persistent key — see <see cref="GenerateAndSaveRoomKey"/> for the one-time setup path).</summary>
    public async Task<bool> CreateGameAsync()
    {
        if (!string.IsNullOrWhiteSpace(ActiveGame.RoomCode) && ActiveGame.Lifecycle is not (null or "Closed"))
        { Status = "A game is already active for this venue. Close it before creating a new one."; return false; }

        if (string.IsNullOrWhiteSpace(Defaults.Connection.RoomKey))
        { Status = "This venue has no Room Key configured yet. Set one in Settings -> Modules -> Bingo before creating a game."; return false; }

        ResetCallerAlertTracking(); // a brand new room starts with no callers at all — the baseline is simply empty
        var roomCode = Guid.NewGuid().ToString("N");
        var roomKey = Defaults.Connection.RoomKey!;
        var venueName = profiles.Current.DisplayName;
        var colors = NormalizedColors();
        var request = new BingoV2CreateRoomRequest(roomCode, roomKey, venueName, Defaults.CostPerCard, Defaults.StartingPot, Defaults.PrizePercentage, Defaults.GameType, null, Defaults.Letters, colors.Bg, colors.Card, colors.Header, colors.Text, colors.Daub, colors.Ball, Guid.NewGuid().ToString());
        var result = await client.CreateRoomV2Async(Defaults.Connection with { RoomKey = roomKey }, request, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("create game", result.Error); return false; }

        ApplySnapshot(roomKey, result.Value);
        Status = $"Game created for {venueName} (Draft).";
        return true;
    }

    /// <summary>One-time (or deliberate rotation) venue setup: mints a new Room Key and PERSISTS it as this venue's
    /// default via <see cref="SaveDefaults"/> — this is never a per-game action. The caller (Settings UI) must warn
    /// the operator before calling this when a Room Key is already configured, since every room created under the
    /// old key becomes unreachable via <see cref="ListVenueRoomsAsync"/> once the default changes (the rooms
    /// themselves are untouched server-side, but the venue's own listing/resume workflow keys off the current
    /// default) — this method itself never rotates silently, it only ever does exactly what it's called to do.</summary>
    public string GenerateAndSaveRoomKey()
    {
        var newKey = Guid.NewGuid().ToString("N");
        SaveDefaults(Defaults with { Connection = Defaults.Connection with { RoomKey = newKey } });
        return newKey;
    }

    /// <summary>Lists every room associated with this venue's persistent Room Key (legacy <c>GET /api/rooms</c>,
    /// filtered server-side by <c>room_key</c> — unaffected by whether a given room was created by the standalone
    /// plugin, a prior VenueOS session, or this one, since they all write into the same backend table under the same
    /// key). This is the discovery step of host handoff: a second host configured with the same venue's Room Key
    /// calls this to find the room code, then <see cref="ObserveExistingRoom"/> to resume it.</summary>
    public async Task<List<BingoRoomSummary>?> ListVenueRoomsAsync()
    {
        if (string.IsNullOrWhiteSpace(Defaults.Connection.RoomKey)) { Status = "This venue has no Room Key configured yet."; return null; }
        var result = await client.ListRoomsAsync(Defaults.Connection, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("list rooms", result.Error); return null; }
        return result.Value.Rooms;
    }

    /// <summary>Explicitly and permanently removes a room from the backend — the legacy, keyed
    /// <c>POST /api/rooms/close</c> route (the same one the standalone plugin's "Server Rooms -> Close" already
    /// uses), authorized by this venue's persistent Room Key, distinct from <see cref="CloseGameAsync"/>'s v2
    /// soft-close (which stops accepting new activity but keeps the room's state). This is what lets an operator
    /// clean up old rooms on demand instead of waiting for the backend's automatic multi-day retention sweep.
    /// Does NOT touch the room currently being operated (see the operator panel's active-room guard) — if it ever
    /// is called for the active room's code, local state is cleared on success so VenueOS never keeps operating
    /// against a room the backend no longer has.</summary>
    public async Task<bool> DeleteRoomAsync(string roomCode)
    {
        if (string.IsNullOrWhiteSpace(Defaults.Connection.RoomKey)) { Status = "This venue has no Room Key configured yet."; return false; }
        var result = await client.CloseRoomAsync(Defaults.Connection, roomCode, admin: false, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success) { Fail("close room", result.Error); return false; }
        if (string.Equals(ActiveGame.RoomCode, roomCode, StringComparison.Ordinal)) ActiveGame = VenueBingoActiveGame.Empty();
        Status = $"Room {roomCode} closed.";
        return true;
    }

    /// <summary>Locks economics (Draft -> Active). Idempotent server-side. Never blindly retries with different
    /// values on failure — a 409 (e.g. a stale client racing a legacy room) is surfaced as-is.</summary>
    public async Task<bool> StartGameAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No game to start."; return false; }
        var result = await client.StartRoomV2Async(ConnectionForActiveGame(), ActiveGame.RoomCode, new(Guid.NewGuid().ToString()), contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("start game", result.Error); return false; }
        ApplySnapshot(ActiveGame.RoomKey, result.Value);
        Status = "Game started — cost, starting pot and prize percentage are now locked.";
        return true;
    }

    /// <summary>Backend v2 soft-close — permanently ends this room's ability to accept further calls/claims (does
    /// NOT delete it; that is <see cref="DeleteRoomAsync"/>). Product correction: this must never be wired to a
    /// primary-controls action an operator could mistake for "step away for now" — live QA showed pressing what
    /// looked like a safe pause irreversibly ended the room's ability to keep calling numbers, which read to the
    /// operator as the room having been destructively closed even though the row itself was still on the backend.
    /// The primary controls' "step away" action is <see cref="LeaveGame"/>, which makes no backend request at all.
    /// Nothing in the operator panel currently calls this method; it is kept because the underlying backend
    /// capability (formally ending a room's lifecycle without deleting its data) is legitimate and still exercised
    /// by <c>VenueBingoServiceTests</c>.</summary>
    public async Task<bool> CloseGameAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No game to close."; return false; }
        var result = await client.CloseRoomV2Async(ConnectionForActiveGame(), ActiveGame.RoomCode, new(Guid.NewGuid().ToString()), contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("close game", result.Error); return false; }
        ApplySnapshot(ActiveGame.RoomKey, result.Value);
        Status = "Game closed.";
        return true;
    }

    /// <summary>"Leave Game" — the primary controls' non-destructive step-away action (product correction for the
    /// live "Close Game" bug; see <see cref="CloseGameAsync"/>'s doc comment for the exact symptom this replaces).
    /// Makes NO backend request whatsoever: the room and everything the backend holds for it — players, cards,
    /// daubs, called numbers, caller history, payout state — are left completely untouched. Only this client's own
    /// local <see cref="ActiveGame"/> is cleared, which also naturally stops <see cref="Tick"/>'s room-specific
    /// polling (gated on <c>ActiveGame.RoomCode</c> being non-blank) and abandons any outstanding roll wait so a
    /// late-arriving result can never be submitted against a room this client is no longer observing. The room
    /// keeps appearing under "This venue's rooms" and can be fully resumed later via
    /// <see cref="ObserveExistingRoom"/>.</summary>
    public void LeaveGame()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game to leave."; return; }
        var roomCode = ActiveGame.RoomCode;
        pendingRoll = null;
        ActiveGame = VenueBingoActiveGame.Empty();
        Status = $"Left {roomCode} — the room is untouched on the backend and can be resumed anytime from This venue's rooms.";
    }

    /// <summary>Grants/sets a seed's paid and comp card counts. One call == one fresh idempotency key — the caller
    /// (operator panel) must invoke this once per logical click, never reuse a key across retries of the SAME
    /// logical action from a UI loop.</summary>
    public async Task<bool> GrantCardsAsync(string seed, string? name, int paidCount, int compCount)
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return false; }
        if (string.IsNullOrWhiteSpace(seed)) { Status = "A seed is required to grant cards."; return false; }
        var request = new BingoGrantCardsRequest(seed.Trim(), name, null, paidCount, compCount, Guid.NewGuid().ToString());
        var result = await client.GrantCardsAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, request, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("grant cards", result.Error); return false; }
        ApplySnapshot(ActiveGame.RoomKey, result.Value);
        Status = $"Updated cards for {name ?? seed}.";
        return true;
    }

    /// <summary>Backend-driven payout sync (product correction, docs/BINGO_V2_PROTOCOL.md's payouts/sync section) —
    /// the ONLY way a payout obligation is created in normal operation. Creates/updates an obligation for every
    /// backend-computed eligible Bingo caller (<see cref="VenueBingoActiveGame.BingoCallers"/>) at the backend-
    /// computed split (<see cref="VenueBingoActiveGame.SplitAmount"/>) — the host never manually selects a winner or
    /// types a total owed. Safe to call repeatedly: at most one obligation is ever auto-created per caller per room,
    /// frozen permanently the instant any payment against it is confirmed (server.js's own doc comment on the
    /// route).</summary>
    public async Task<bool> SyncPayoutsAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return false; }
        var result = await client.SyncPayoutsAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, new(Guid.NewGuid().ToString()), contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("sync payouts", result.Error); return false; }
        ApplySnapshot(ActiveGame.RoomKey, result.Value);
        Status = "Payouts synced.";
        return true;
    }

    /// <summary>Gets (or creates) a browser link showing a player ALL of their cards — paid and comp both, since the
    /// paid/comp accounting distinction is invisible to the player by design. Wraps the admin-key-protected
    /// POST /api/links (<see cref="VenueBingoClient.CreateBrowserLinkAsync"/>), which was previously never called
    /// anywhere in this codebase. Caching/staleness policy (regenerate only when the player's total card count has
    /// grown past what an existing cached link was generated for) is deliberately the CALLER's responsibility — this
    /// method is a pure, stateless, per-call wrapper, matching every other single-purpose method on this
    /// class.</summary>
    public async Task<string?> GetOrCreatePlayerLinkAsync(string seed, string playerName, int totalCardCount)
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return null; }
        if (string.IsNullOrWhiteSpace(Defaults.Connection.AdminKey)) { Status = "Configure this venue's Admin Key in Settings to generate player links."; return null; }
        var colors = NormalizedColors();
        var request = new BingoLinkRequest(seed.Trim(), Math.Max(1, totalCardCount), Defaults.Letters, playerName, profiles.Current.DisplayName, ActiveGame.RoomCode, ActiveGame.Snapshot?.GameType ?? Defaults.GameType, colors.Bg, colors.Card, colors.Header, colors.Text, colors.Daub, colors.Ball, Defaults.Connection.ServerUrl);
        var result = await client.CreateBrowserLinkAsync(ConnectionForActiveGame(), request, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("create player link", result.Error); return null; }
        return BuildBrowserUrl(result.Value.Code);
    }

    private string BuildBrowserUrl(string code)
    {
        var baseUrl = string.IsNullOrWhiteSpace(Defaults.Connection.BrowserUrl) ? Defaults.Connection.ServerUrl : Defaults.Connection.BrowserUrl;
        return baseUrl.TrimEnd('/') + "/l/" + code;
    }

    /// <summary>Product correction (live-QA): the operator should never have to press Copy Link once merely to
    /// generate a player's link — it should already be current and visible the moment a player is added or their
    /// card count changes, and it should survive a Leave Game/Resume/plugin reload without minting a fresh code
    /// every time. This is the single "make sure this player's CURRENTLY VALID link is known" operation every
    /// caller (Add Player, +/-Paid/+/-Comp, room resume) should use instead of unconditionally calling
    /// <see cref="GetOrCreatePlayerLinkAsync"/>: it first asks the backend (additive <c>GET /api/links/lookup</c>,
    /// docs/BINGO_V2_PROTOCOL.md §6) whether a link already exists for this room+seed, and only creates a brand-new
    /// one if none exists yet OR the existing one's baked-in card count is too low for the player's CURRENT
    /// authoritative total (the same "only regenerate on a count INCREASE" rule <c>GetOrCreatePlayerLinkAsync</c>'s
    /// callers already followed locally, now also checked against whatever the backend itself remembers — which is
    /// what makes this safe to call after a local cache loss without churning out duplicate short codes). A lookup
    /// failure (network hiccup, etc.) is never fatal — it just falls through to creating fresh, matching this
    /// method's overall "never fail merely because rediscovery didn't work" contract.</summary>
    public async Task<(string Url, int Count)?> EnsureCurrentPlayerLinkAsync(string seed, string playerName, int totalCardCount)
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return null; }
        if (string.IsNullOrWhiteSpace(Defaults.Connection.AdminKey)) { Status = "Configure this venue's Admin Key in Settings to generate player links."; return null; }

        var lookup = await client.FindPlayerLinkAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, seed.Trim(), contextCancellation.Token).ConfigureAwait(false);
        if (lookup.Success && lookup.Value?.Code is { } existingCode && lookup.Value.Count is int existingCount && existingCount >= totalCardCount)
            return (BuildBrowserUrl(existingCode), existingCount);

        var url = await GetOrCreatePlayerLinkAsync(seed, playerName, totalCardCount).ConfigureAwait(false);
        return url is null ? null : (url, totalCardCount);
    }

    /// <summary>Refreshes <see cref="ActiveGame"/> from the backend's authoritative snapshot. Tries the v2 snapshot
    /// endpoint first — it works for a Legacy-lifecycle room too (<c>buildRoomSnapshot</c> loads by room code
    /// regardless of how the room was created), so once <see cref="VenueBingoActiveGame.Lifecycle"/> has been
    /// established by any prior successful poll, every subsequent poll uses v2 only. The legacy
    /// <c>GET /api/room-state</c> fallback is only attempted while <c>Lifecycle</c> is still null/unknown (the very
    /// first poll after <see cref="ObserveExistingRoom"/>, or against an older backend deployment with no v2 routes
    /// at all) — this is what lets VenueOS observe/resume a room the standalone plugin created.</summary>
    public async Task<bool> PollAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) return false;

        var v2 = await client.GetRoomV2Async(ConnectionForActiveGame(), ActiveGame.RoomCode, contextCancellation.Token).ConfigureAwait(false);
        if (v2.Success && v2.Value is not null) { ApplySnapshot(ActiveGame.RoomKey, v2.Value); Status = "Connected"; return true; }
        if (ActiveGame.Lifecycle is not null) { Fail("refresh room", v2.Error); return false; }

        var legacy = await client.GetRoomStateAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, contextCancellation.Token).ConfigureAwait(false);
        if (!legacy.Success || legacy.Value is null) { Fail("refresh room", legacy.Error ?? v2.Error); return false; }
        ApplyLegacySnapshot(legacy.Value);
        Status = "Connected (legacy room).";
        return true;
    }

    /// <summary>Still just the existing legacy endpoint — the protocol doc deliberately adds no v2 number-call
    /// route (docs/BINGO_V2_PROTOCOL.md §10).</summary>
    public async Task<bool> CallNumberAsync(int number)
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return false; }
        var result = await client.CallNumberAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, number, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("call number", result.Error); return false; }
        ActiveGame = ActiveGame with { CalledNumbers = result.Value.CalledNumbers };
        Status = $"Called {number}.";
        return true;
    }

    /// <summary>Points the service at an existing room code/key without creating one — the "resume a room the
    /// standalone plugin (or a previous VenueOS session) already created" path. The next <see cref="PollAsync"/>
    /// (or the regular <see cref="Tick"/> poll) fills in the rest.</summary>
    public void ObserveExistingRoom(string roomCode, string roomKey)
    {
        ActiveGame = VenueBingoActiveGame.Empty() with { RoomCode = roomCode.Trim(), RoomKey = roomKey.Trim() };
        // Bingo Call Alert baseline (live-QA addition): attaching/resuming establishes a fresh baseline from the
        // NEXT snapshot — see ResetCallerAlertTracking's doc comment for why this avoids popping stale alerts for
        // every historical caller already in the room.
        ResetCallerAlertTracking();
    }

    /// <summary>Deliberately NOT a manual numeric-entry field — the reconstruction brief requires that removal to
    /// stay removed (an anti-cheat fix in the donor's own history). Sends exactly <c>/random 75</c> or
    /// <c>/dice 75</c> (never a bare <c>/dice</c> — both must explicitly request the Bingo 1-75 range) via
    /// <see cref="ChatCommandService.Enqueue"/> (never a direct chat/ICommandManager call — NEW_MODULE_GUIDE.md
    /// §27/§29), and opens a bounded <see cref="BingoPendingRoll"/> window that only the LOCAL HOST's own matching
    /// result (see <see cref="HandleRollObservation"/>/<see cref="BingoRollCorrelator"/>) can satisfy — another
    /// party member rolling the same command during this window must never be consumed. Never calls a number that
    /// wasn't confidently correlated; a bounded timeout (<see cref="RollTimeout"/>, checked in <see cref="Tick"/>)
    /// reports "no result" rather than guessing, and never automatically retries the command.</summary>
    public void RollAndCall()
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) { Status = "No active game."; return; }
        if (pendingRoll is { Consumed: false }) { Status = "Already waiting for a roll result."; return; }
        var mode = Defaults.RollCommand == BingoRollCommand.Dice ? BingoRollMode.Dice : BingoRollMode.Random;
        pendingRoll = new BingoPendingRoll(Guid.NewGuid(), mode, DateTimeOffset.UtcNow + RollTimeout);
        var command = mode == BingoRollMode.Dice ? "/dice 75" : "/random 75";
        Status = $"Rolling ({command})...";
        RaiseDiagnostic($"Roll requested: {mode}, attempt {pendingRoll.AttemptId}");
        chat.Enqueue(new ChatCommand(command));
    }

    /// <summary>Wired from <c>VenueOS.Plugin.Bingo.BingoRollChatAdapter</c> (the Dalamud-aware adapter that resolves
    /// LogKind/sender identity/text-parsing into this Dalamud-free observation — see its doc comment for exactly
    /// what's live-verified vs. best-effort, especially for <c>/dice 75</c>). Ignores everything that doesn't satisfy
    /// <see cref="BingoRollCorrelator.Evaluate"/> — most chat messages are not roll results at all, and this is
    /// deliberately silent for those; only a genuinely accepted local-host result submits to the backend, unless
    /// <see cref="BingoAnnounceChannel.None"/> is selected in which case no announcement chat command is sent
    /// either. Returns a <see cref="Task"/> (the real Dalamud chat callback that invokes this fires it without
    /// awaiting, exactly like every other fire-and-forget UI action in this class) purely so a duplicate/near-
    /// simultaneous delivery of the same message can be deterministically tested — <see cref="pendingRoll"/> is
    /// marked consumed synchronously, before the backend call, so two overlapping calls to this method can never
    /// both submit.</summary>
    public Task HandleRollObservation(BingoRollObservation observation)
    {
        var consumedBefore = pendingRoll?.Consumed ?? false;
        var evaluation = BingoRollCorrelator.Evaluate(pendingRoll, observation, DateTimeOffset.UtcNow);
        if (!evaluation.Accepted)
        {
            // Only logged when there WAS a candidate-shaped message to evaluate (the adapter only calls this for
            // text that already matched the roll regex) — never a per-frame/per-message flood. Candidate number and
            // consumed-before state are included so a rejection is fully diagnosable without cross-referencing the
            // adapter's own preceding log line.
            RaiseDiagnostic($"Correlator: REJECTED — reason={evaluation.RejectReason}, candidate={observation.ParsedNumber?.ToString() ?? "null"}, mode={observation.Mode}, localHost={observation.IsFromLocalHost}, consumedBefore={consumedBefore}. Backend callback invoked: no");
            return Task.CompletedTask;
        }
        pendingRoll!.MarkConsumed(); // exactly-once: a later matching message (duplicate delivery, coincidence) can never re-trigger this action
        RaiseDiagnostic($"Correlator: ACCEPTED — number={evaluation.Number}, consumedBefore={consumedBefore}, consumedAfter=true. Backend callback invoked: yes");
        return SubmitRolledNumberAsync(evaluation.Number);
    }

    private async Task SubmitRolledNumberAsync(int number)
    {
        var roomCode = ActiveGame.RoomCode;
        RaiseDiagnostic($"Backend POST: room={roomCode}, number={number}, endpoint=/api/call-number — attempting");
        var result = await client.CallNumberAsync(ConnectionForActiveGame(), roomCode, number, contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success || result.Value is null) { Fail("call number", result.Error); RaiseDiagnostic($"Backend POST: FAILED — {result.Error}. State refresh: skipped (no announcement will be sent)"); return; }
        ActiveGame = ActiveGame with { CalledNumbers = result.Value.CalledNumbers };
        RaiseDiagnostic($"Backend POST: accepted, added={result.Value.Added}. State refresh: calledNumbers.Count={ActiveGame.CalledNumbers.Count}");
        // Duplicate calls are rejected with a visible warning, not silently re-rolled — matches the donor's own
        // "DUPLICATE ROLL! Reroll." behavior (forensic audit §15), just surfaced via Status instead of chat text.
        if (!result.Value.Added) { Status = $"{number} was already called — no duplicate added."; RaiseDiagnostic("Announcement: skipped (duplicate, not a fresh call)"); return; }
        Status = $"Called {number}.";
        Announce(number);
    }

    private void Announce(int number)
    {
        if (Defaults.AnnounceChannel == BingoAnnounceChannel.None) { RaiseDiagnostic("Announcement: attempted=no, channel=None"); return; }
        var prefix = Defaults.AnnounceChannel switch { BingoAnnounceChannel.Shout => "/shout", BingoAnnounceChannel.Yell => "/yell", BingoAnnounceChannel.Party => "/p", _ => (string?)null };
        if (prefix is null) { RaiseDiagnostic($"Announcement: attempted=no, unmapped channel {Defaults.AnnounceChannel}"); return; }
        // Backend-authoritative letters (the locked game snapshot), never the venue's current Settings default —
        // Custom Letters correction: an operator editing the Settings default mid-game must never retroactively
        // change how an already-locked game's balls are labeled.
        var letters = ActiveGame.Snapshot?.Letters ?? Defaults.Letters;
        var text = $"{prefix} {FormatBallLabel(number, letters)}";
        RaiseDiagnostic($"Announcement: attempted=yes, channel={Defaults.AnnounceChannel}, command=\"{text}\"");
        chat.Enqueue(new ChatCommand(text, OnDispatched: (success, ex) =>
            RaiseDiagnostic(success ? $"Announcement: sent ({prefix})" : $"Announcement: failed ({prefix}) — {ex?.Message}")));
    }

    public static string FormatBallLabel(int number, string letters)
    {
        var column = Math.Clamp((number - 1) / 15, 0, 4);
        var letter = !string.IsNullOrEmpty(letters) && column < letters.Length ? letters[column] : "BINGO"[column];
        return $"{letter}-{number}";
    }

    /// <summary>Manual override for an attempt the operator has independently confirmed was (or was not) actually
    /// paid — e.g. after reconciling an <c>Ambiguous</c> attempt by hand. Never called automatically; the operator
    /// panel gates this behind a <c>ConfirmDialog</c> (NEW_MODULE_GUIDE.md §37).</summary>
    public async Task<bool> ReconcileAttemptAsync(string payoutId, string attemptId, bool actuallyPaid, string note)
    {
        if (string.IsNullOrWhiteSpace(ActiveGame.RoomCode)) return false;
        var result = await client.TransitionPayoutAttemptAsync(ConnectionForActiveGame(), ActiveGame.RoomCode, payoutId, attemptId, new(actuallyPaid ? "confirmed" : "failed", note, Guid.NewGuid().ToString()), contextCancellation.Token).ConfigureAwait(false);
        if (!result.Success) { Fail("reconcile payout attempt", result.Error); return false; }
        Status = actuallyPaid ? "Attempt manually reconciled as paid." : "Attempt manually marked as not paid.";
        _ = PollAsync();
        return true;
    }

    public void Tick(DateTimeOffset now)
    {
        // No number is ever invented on timeout, no backend call is made, and the command is never auto-resent —
        // the operator sees a clear status and may explicitly press Roll & Call again.
        if (pendingRoll is { Consumed: false } pending && now > pending.Deadline)
        { RaiseDiagnostic($"Roll timed out: attempt {pending.AttemptId}, mode {pending.Mode}"); pendingRoll = null; Status = "Roll timed out — no valid result observed from your character. Try again."; }
        if (polling || string.IsNullOrWhiteSpace(ActiveGame.RoomCode) || now < nextPoll) return;
        nextPoll = now.AddSeconds(5);
        polling = true;
        _ = PollAsync().ContinueWith(_ => polling = false, TaskScheduler.Default);
    }

    private BingoConnectionSettings ConnectionForActiveGame() => Defaults.Connection with { RoomKey = ActiveGame.RoomKey };

    /// <summary>Normalizes every configured appearance color into the backend's exact accepted "RRGGBB"/"RGB" hex
    /// format (see <see cref="BingoColorNormalization"/> for why this exists — the live "Copy Link" 400 "invalid
    /// color value" root cause). Applied at the point colors are actually sent (here) rather than at Settings
    /// save-time, so a value already persisted before this fix works without the operator re-entering it, and so
    /// the live-editing Settings field is never fighting a per-keystroke normalization reset.</summary>
    private BingoColors NormalizedColors() => new(
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Bg),
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Card),
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Header),
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Text),
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Daub),
        BingoColorNormalization.ToBackendHex(Defaults.Colors.Ball));

    private void ApplySnapshot(string roomKey, BingoV2RoomState state)
    {
        var callers = DeduplicateCallers(state.BingoCallers);
        ActiveGame = new VenueBingoActiveGame(
            state.RoomCode, roomKey, state.Lifecycle,
            ActiveGame.Snapshot ?? new VenueBingoGameSnapshot(state.CostPerCard, state.StartingPot, state.PrizePercentage, state.GameType, state.Letters ?? Defaults.Letters, state.Colors ?? Defaults.Colors),
            state.Pot, state.Players ?? [], state.CalledNumbers ?? [], state.Payouts ?? [],
            callers, state.CurrentPrizePool, state.SplitAmount,
            state.Daubs ?? [], state.BallsToBingo ?? []);
        DetectNewBingoCallers(callers);
    }

    // Defensive belt-and-suspenders only — the backend already de-duplicates bingoCallers by (seed, phase) at write
    // time (docs/BINGO_V2_PROTOCOL.md), so this should normally be a no-op. First-wins, chronological order (as
    // returned by the backend) is preserved — never re-sorted.
    private static List<BingoCaller> DeduplicateCallers(List<BingoCaller>? callers)
    {
        if (callers is null || callers.Count == 0) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BingoCaller>(callers.Count);
        foreach (var caller in callers) if (seen.Add(caller.Seed)) result.Add(caller);
        return result;
    }

    /// <summary>A legacy-lifecycle room predates the backend-caller/split concept entirely (docs/BINGO_V2_PROTOCOL.md
    /// — only a v2-created room's snapshot carries <c>bingoCallers</c>/<c>currentPrizePool</c>/<c>splitAmount</c>),
    /// so those fields are explicitly reset to empty/zero here rather than left stale from a prior v2 poll — payout
    /// stays a display-only "no callers yet" state for a legacy room, which is an acceptable limitation, not a
    /// bug.</summary>
    private void ApplyLegacySnapshot(BingoRoomState state) => ActiveGame = ActiveGame with
    {
        Lifecycle = state.Lifecycle ?? "Legacy",
        Snapshot = ActiveGame.Snapshot ?? new VenueBingoGameSnapshot(state.CostPerCard, state.StartingPot, state.PrizePercentage, state.GameType, state.Letters ?? Defaults.Letters, state.Colors ?? Defaults.Colors),
        Pot = state.Pot,
        Players = state.Players ?? [],
        CalledNumbers = state.CalledNumbers ?? [],
        BingoCallers = [],
        CurrentPrizePool = 0,
        SplitAmount = 0,
        // Daubs IS mapped for a legacy room (the backend returns session.daubs on this route too) — unlike the
        // caller/split fields above, per-seed daub state predates the v2 concept entirely and is not v2-specific.
        Daubs = state.Daubs ?? [],
        BallsToBingo = [], // v2-only calculation; a legacy room has no computeBallsToBingo equivalent
    };

    private void Fail(string operation, string? error)
    {
        var message = error ?? "unknown error";
        Status = message;
        diagnostics.RecordFailure($"{ModuleId}: {operation} failed ({DiagnosticsService.Redact(message)})");
    }
}

public sealed class VenueBingoModule(VenueBingoService bingo, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("games.bingo", "Bingo", "Backend-compatible Bingo host operations: cards, calling, and payout tracking.", "grid", DisplayOrder: 8);
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { bingo.Load(c.VenueId); return Task.CompletedTask; }
    public void Tick(DateTimeOffset now) => bingo.Tick(now);
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() { bingo.Load(Guid.Empty); return ValueTask.CompletedTask; }
}
