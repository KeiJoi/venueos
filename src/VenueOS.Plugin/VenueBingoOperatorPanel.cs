using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Plugin.Bingo;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>Bingo's operational (<see cref="Draw"/>) and persistent-configuration (<see cref="DrawSettings"/>) UI —
/// the reconstruction of the previously minimal scaffold (see BINGO_FORENSIC_AUDIT.md / BINGO_V2_PROTOCOL.md).
///
/// <b>Detached-window lifecycle correction (live-QA pass)</b>: the two auxiliary floating windows
/// (<see cref="BingoCalledNumbersWindow"/>, <see cref="BingoPlayerCardViewerWindow"/>) used to be drawn from
/// INSIDE this class's own <see cref="Draw"/> — which sounds independent, but <see cref="Draw"/> itself is only
/// ever invoked while the operator is actively viewing the Bingo module (the embedded tablet screen navigated to
/// Bingo, or Bingo popped out via <c>ModuleWindowManager</c>). Closing/hiding the main Bingo window, or simply
/// navigating the tablet to a different module tab, meant <see cref="Draw"/> stopped being called that frame —
/// silently taking both auxiliary windows down with it even though their own <c>IsOpen</c> flags were still true,
/// which contradicted the product requirement that Called Numbers must be able to outlive the main window. Fixed
/// by exposing <see cref="CalledNumbersWindow"/>/<see cref="PlayerCardViewerWindow"/> so <c>Plugin.cs</c>'s
/// top-level <c>Draw</c> can render them unconditionally every frame (gated only on the Bingo module's own
/// <c>IsEnabled</c>, so a disabled module still stops showing its detached UI) — this class's own
/// <see cref="Draw"/> no longer draws them itself, to avoid rendering the same ImGui window twice in one frame.
///
/// Payout automation (<see cref="payoutOrchestrator"/>/<see cref="payoutAutomation"/>) is deliberately NOT reachable
/// without the operator explicitly checking "I understand this requires live testing" every session (never
/// persisted) — this reconstruction's engine has never been exercised against a live game client (see
/// BingoPayoutAutomationService's doc comment for the full list of LIVE VERIFICATION REQUIRED assumptions).</summary>
internal sealed class VenueBingoOperatorPanel
{
    private readonly VenueBingoService service;
    private readonly BingoPayoutOrchestrator payoutOrchestrator;
    private readonly IBingoPayoutAutomation payoutAutomation;
    private readonly VenueProfileService venues;
    private readonly ITargetedPlayerProvider targetProvider;
    private readonly ConfirmDialog confirmDialog = new();
    private readonly BingoCalledNumbersWindow calledNumbersWindow;
    private readonly BingoPlayerCardViewerWindow playerCardViewerWindow;
    private readonly BingoCallAlertWindow callAlertWindow;

    /// <summary>Exposed (live-QA correction — detached-window lifecycle) so <c>Plugin.Draw</c> can render these
    /// three auxiliary windows unconditionally every frame, independent of whether this panel's own <see cref="Draw"/>
    /// is currently being invoked at all (embedded tablet closed/navigated away, or the Bingo module not popped
    /// out). See <c>Plugin.cs</c>'s <c>Draw</c> method and this class's own <see cref="Draw"/> doc comment for
    /// exactly why <see cref="Draw"/> itself no longer draws them.</summary>
    public BingoCalledNumbersWindow CalledNumbersWindow => calledNumbersWindow;
    public BingoPlayerCardViewerWindow PlayerCardViewerWindow => playerCardViewerWindow;
    public BingoCallAlertWindow CallAlertWindow => callAlertWindow;

    // ---- panel-local UI state (never persisted) ----
    private string newPlayerName = "";
    private string newPlayerHomeWorld = ""; // captured for display only — the legacy/v2 player record has no world field, see the "Use Current Target" wiring below
    private string? newPlayerTargetError;
    private int newPlayerPaid = 1;
    private int newPlayerComp;
    private string observeRoomCode = "";
    private List<BingoRoomSummary>? roomList;
    private bool roomListLoading;
    // Room Key buffered edit (see BingoRoomKeyEdit) — never saved/confirmed per keystroke.
    private string roomKeyEditBuffer = "";
    private string? roomKeyBufferSyncedFrom;
    // Player browser-link cache, keyed by seed — panel-local only, matching how newPlayerName/roomList etc. are
    // already handled (NEW_MODULE_GUIDE.md's operational state stays in-memory, never GetModuleConfig/SaveModuleConfig).
    // Link stability rule: a cached link is reused as long as the player's current total card count still matches
    // Count (the count baked into that generated link) — the browser client only ever REJECTS a count increase past
    // what the link's payload allows, it never re-checks downward, so a stale-but-not-shrunk link stays valid and a
    // fresh one is generated automatically only when the count has actually grown since generation.
    private readonly Dictionary<string, (string Url, int Count)> playerLinkCache = new();
    // Product correction (live-QA): a player's link is now ensured automatically (Add Player, +/-Paid/+/-Comp, and
    // once per player on room resume/reload) instead of only ever being created the first time the operator
    // presses Copy Link. playerLinkEnsureAttempted bounds the automatic (roster-scan-driven) attempts to AT MOST
    // ONCE per seed per panel lifetime (i.e. until the next plugin reload) — a failure is surfaced via
    // playerLinkErrors and left for the operator to retry explicitly (Copy Link/New Link), never auto-retried every
    // frame, so a persistent backend/network problem can't turn into an endless request loop. Neither dictionary is
    // ever cleared on Leave Game/Resume: seeds are GUIDs (collision-free across rooms/venues), so entries from a
    // previous room are simply harmless, unused memoization, not stale state that could apply to the wrong player.
    private readonly HashSet<string> playerLinkEnsureAttempted = new();
    private readonly Dictionary<string, string> playerLinkErrors = new();
    private bool payoutLiveTestingAcknowledged;
    private string payoutTargetNameAndWorld = "";
    private BingoPayoutRunResult? lastPayoutRun;
    private bool payoutRunInFlight;
    private string? payoutRunError;

    public VenueBingoOperatorPanel(VenueBingoService service, BingoPayoutOrchestrator payoutOrchestrator, IBingoPayoutAutomation payoutAutomation, VenueProfileService venues, ITargetedPlayerProvider targetProvider, Action onOpenBingoSettings, Action onOpenBingo)
    {
        this.service = service;
        this.payoutOrchestrator = payoutOrchestrator;
        this.payoutAutomation = payoutAutomation;
        this.venues = venues;
        this.targetProvider = targetProvider;
        calledNumbersWindow = new BingoCalledNumbersWindow(service, onOpenBingoSettings);
        playerCardViewerWindow = new BingoPlayerCardViewerWindow(service, onOpenBingoSettings);
        // Live-QA addition: "View Cards" from the alert reuses THIS SAME card-viewer instance (never a second
        // implementation) and kicks one immediate poll, matching the roster/payout section's own View Cards
        // buttons exactly (see DrawRosterSection/DrawPayoutSection).
        callAlertWindow = new BingoCallAlertWindow(service, seed => { playerCardViewerWindow.ShowPlayer(seed); _ = service.PollAsync(); }, onOpenBingo);
    }

    public void Draw()
    {
        var theme = venues.Current.Theme;
        confirmDialog.Draw(theme);
        // The two auxiliary windows are NO LONGER drawn here — see this class's doc comment and
        // CalledNumbersWindow/PlayerCardViewerWindow's doc comments for why: Plugin.cs's top-level Draw now
        // renders them unconditionally every frame instead, so they survive this panel's own Draw not running at
        // all (main Bingo window closed/hidden, or the tablet navigated to a different module).

        DrawStatusSection(theme);
        ImGui.Spacing();
        DrawLifecycleSection(theme);
        ImGui.Spacing();
        DrawNumberCallingSection(theme);
        ImGui.Spacing();
        DrawRosterSection(theme);
        ImGui.Spacing();
        DrawPayoutSection(theme);
    }

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        var defaults = service.Defaults;
        var locked = service.ActiveGame.Lifecycle == "Active";

        UiKit.BeginSectionCard("bingo-connection", theme, "Connection");
        // Product decision: every host-side Bingo credential below is a plain, readable, selectable/copyable text
        // field — no password masking. The operator needs to read, copy, and hand these to another host (e.g. for
        // Room Key host handoff); masking a value the operator is expected to recover/communicate defeats that.
        // This is a UI-presentation decision only — DiagnosticsService.Redact / BingoConnectionSettings.WithoutSecrets
        // still strip these from logs/diagnostics/exceptions exactly as before ("visible in Settings" is not "safe
        // to log").
        var serverUrl = defaults.Connection.ServerUrl;
        if (Forms.TextField(theme, "Server URL", ref serverUrl, 256, "https://")) Save(defaults with { Connection = defaults.Connection with { ServerUrl = serverUrl } });

        ImGui.Spacing();
        ImGui.TextWrapped("Room Key is this venue's persistent host credential — every game this venue creates reuses it, and it's what lets a second host discover/resume this venue's rooms below. It is NOT generated per game; a game created with no Room Key configured is refused.");
        // Buffered edit, not per-keystroke save/confirm (BingoRoomKeyEdit's doc comment explains why this matters):
        // the field only ever writes to roomKeyEditBuffer while typing. roomKeyBufferSyncedFrom tracks the persisted
        // value as of the last resync so a venue switch (or a just-completed Save/Generate) refreshes the buffer,
        // while an in-progress edit is never clobbered by a redraw — the persisted value simply hasn't changed yet.
        roomKeyEditBuffer = BingoRoomKeyEdit.ResyncBuffer(roomKeyEditBuffer, roomKeyBufferSyncedFrom, defaults.Connection.RoomKey);
        roomKeyBufferSyncedFrom = defaults.Connection.RoomKey;
        Forms.TextField(theme, "Room key", ref roomKeyEditBuffer, 256);
        var roomKeyDirty = BingoRoomKeyEdit.IsDirty(defaults.Connection.RoomKey, roomKeyEditBuffer);
        ImGui.BeginDisabled(!roomKeyDirty);
        if (UiKit.PrimaryButton(theme, "Save Room Key")) CommitRoomKey(defaults, roomKeyEditBuffer);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Generate Random Room Key"))
        {
            // Generated exactly once per click, only ever inside the (possibly deferred, confirm-gated) action —
            // never regenerated while a confirmation popup is simply being redrawn.
            if (string.IsNullOrWhiteSpace(defaults.Connection.RoomKey))
                service.GenerateAndSaveRoomKey();
            else
                confirmDialog.Request("Replace this venue's Room Key?", "This venue already has a configured Room Key. Generating a new one will make existing rooms created under the old key no longer appear in this venue's room list/resume workflow. Only do this if you mean to stop using the old key.", () => service.GenerateAndSaveRoomKey());
        }
        UiKit.Tooltip("For initial venue setup. This saves a new PERSISTENT venue Room Key — it does not create a per-game key.");

        var adminKey = defaults.Connection.AdminKey ?? "";
        if (Forms.TextField(theme, "Admin key", ref adminKey, 256)) Save(defaults with { Connection = defaults.Connection with { AdminKey = string.IsNullOrWhiteSpace(adminKey) ? null : adminKey } });
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("bingo-pricing", theme, "Default Pricing & Rules");
        if (locked) UiKit.WarningState(theme, "A game is currently Active — cost, starting pot and prize percentage are LOCKED for it. Editing them here only changes the default used for the NEXT game you create.");
        ImGui.Spacing();
        ImGui.BeginDisabled(locked);
        var cost = defaults.CostPerCard;
        if (Forms.NumericField(theme, "Cost per card", ref cost, 1, 0, 1_000_000)) Save(defaults with { CostPerCard = cost });
        var startingPot = defaults.StartingPot;
        if (Forms.NumericField(theme, "Starting pot", ref startingPot, 1, 0, 100_000_000)) Save(defaults with { StartingPot = startingPot });
        var percentage = (int)defaults.PrizePercentage;
        if (Forms.NumericField(theme, "Prize percentage", ref percentage, 1, 0, 100)) Save(defaults with { PrizePercentage = percentage });
        ImGui.EndDisabled();
        var letters = defaults.Letters;
        if (Forms.TextField(theme, "Letters (5 characters)", ref letters, 5)) Save(defaults with { Letters = letters });
        var gameTypes = new[] { "Single Line", "Two Lines", "Four Corners", "Blackout" };
        var gameTypeIndex = Array.IndexOf(gameTypes, defaults.GameType); if (gameTypeIndex < 0) gameTypeIndex = 0;
        if (Forms.ComboField(theme, "Game type", gameTypes, ref gameTypeIndex)) Save(defaults with { GameType = gameTypes[gameTypeIndex] });
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("bingo-progressive", theme, "Progressive Defaults");
        var progressiveEnabled = defaults.Progressive.Enabled;
        if (UiKit.Toggle(theme, "Progressive game", ref progressiveEnabled)) Save(defaults with { Progressive = defaults.Progressive with { Enabled = progressiveEnabled } });
        ImGui.BeginDisabled(!progressiveEnabled);
        var p1 = (int)defaults.Progressive.PhaseOneSplit; if (Forms.NumericField(theme, "Phase 1 split %", ref p1, 1, 0, 100)) Save(defaults with { Progressive = defaults.Progressive with { PhaseOneSplit = p1 } });
        var p2 = (int)defaults.Progressive.PhaseTwoSplit; if (Forms.NumericField(theme, "Phase 2 split %", ref p2, 1, 0, 100)) Save(defaults with { Progressive = defaults.Progressive with { PhaseTwoSplit = p2 } });
        var p3 = (int)defaults.Progressive.PhaseThreeSplit; if (Forms.NumericField(theme, "Phase 3 split %", ref p3, 1, 0, 100)) Save(defaults with { Progressive = defaults.Progressive with { PhaseThreeSplit = p3 } });
        ImGui.EndDisabled();
        UiKit.Tooltip("Progressive phase locking is not yet wired to the backend's opaque progressive state — these are stored so your intent survives across sessions.");
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("bingo-webdisplay", theme, "Web Display Colors");
        ImGui.TextWrapped("Sent to the backend for the browser player view. VenueOS's own card viewer also reads these — no image assets are used.");
        ImGui.Spacing();
        ColorField(theme, "Background", defaults.Colors.Bg, v => Save(defaults with { Colors = defaults.Colors with { Bg = v } }));
        ColorField(theme, "Card", defaults.Colors.Card, v => Save(defaults with { Colors = defaults.Colors with { Card = v } }));
        ColorField(theme, "Header", defaults.Colors.Header, v => Save(defaults with { Colors = defaults.Colors with { Header = v } }));
        ColorField(theme, "Text", defaults.Colors.Text, v => Save(defaults with { Colors = defaults.Colors with { Text = v } }));
        ColorField(theme, "Daub", defaults.Colors.Daub, v => Save(defaults with { Colors = defaults.Colors with { Daub = v } }));
        ColorField(theme, "Ball", defaults.Colors.Ball, v => Save(defaults with { Colors = defaults.Colors with { Ball = v } }));
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.InfoBanner(theme, "Roll Command & Announcement", "Selected live from the main Bingo screen's Number Calling section — they're used every roll, so they live there. Whatever you pick is saved as your default for next time.");
    }

    // Deliberately saves the RAW typed text here, not a normalized value — this field is live/per-keystroke (not
    // buffered), and normalizing every keystroke would reset the field to empty mid-typing the instant a partial
    // value (e.g. 4 hex digits so far) fails to parse as a complete color, since the displayed `value` comes back
    // from persisted state on the very next frame. The actual fix for the "Copy Link 400 invalid color value" bug
    // is in VenueBingoService, which normalizes via BingoColorNormalization at the point colors are actually sent —
    // that also means a value already persisted (from before this fix) with a trailing alpha component still works
    // without the operator needing to re-type it here.
    private static void ColorField(VenueTheme theme, string label, string? value, Action<string?> onChange)
    {
        var text = value ?? "";
        if (Forms.TextField(theme, label, ref text, 9, "#RRGGBB")) onChange(string.IsNullOrWhiteSpace(text) ? null : text);
    }

    /// <summary>The explicit "Save Room Key" commit — called once per button click, never per keystroke. Confirms
    /// exactly once only when replacing an already-configured key (see BingoRoomKeyEdit.RequiresConfirmation);
    /// a first-time save or a no-op save (button is disabled for the latter anyway) never shows the dialog.</summary>
    private void CommitRoomKey(VenueBingoDefaults defaults, string editBuffer)
    {
        var candidate = BingoRoomKeyEdit.Normalize(editBuffer);
        if (BingoRoomKeyEdit.RequiresConfirmation(defaults.Connection.RoomKey, editBuffer))
            confirmDialog.Request("Change this venue's Room Key?", "Rooms created with the previous Room Key will no longer appear in this venue's room list/resume workflow (they still exist on the backend under their old key). Only do this if you mean to stop using the old key.", () => Save(defaults with { Connection = defaults.Connection with { RoomKey = candidate } }));
        else
            Save(defaults with { Connection = defaults.Connection with { RoomKey = candidate } });
    }

    private void DrawStatusSection(VenueTheme theme)
    {
        UiKit.BeginSectionCard("bingo-status", theme, "Bingo");
        var dashboard = service.Dashboard;
        UiKit.ConnectionBadge(theme, dashboard.IsConnected ? "Connected" : "Not connected", dashboard.IsConnected);
        ImGui.SameLine();
        var lifecycle = service.ActiveGame.Lifecycle ?? "No game";
        UiKit.StatusBadge(theme, lifecycle, lifecycle switch { "Active" => ToastLevel.Success, "Draft" => ToastLevel.Information, "Closed" => ToastLevel.Warning, "Legacy" => ToastLevel.Information, _ => ToastLevel.Warning });

        var pot = service.ActiveGame.Pot;
        if (pot is not null)
        {
            ImGui.Spacing();
            UiKit.StatCard(theme, "grid", pot.PaidCards.ToString(), "Paid cards", theme.Tokens.Accent); ImGui.SameLine();
            UiKit.StatCard(theme, "grid", pot.CompCards.ToString(), "Comp cards", theme.Tokens.TextSecondary); ImGui.SameLine();
            UiKit.StatCard(theme, "grid", pot.TotalCards.ToString(), "Total cards", theme.Tokens.TextPrimary);
            UiKit.StatCard(theme, "star", pot.CurrentPot.ToString("N0"), "Current pot", theme.Tokens.Accent); ImGui.SameLine();
            UiKit.StatCard(theme, "trophy", pot.PrizePool.ToString("N0"), "Prize pool", theme.Tokens.Success);
        }

        if (!string.IsNullOrWhiteSpace(service.Status)) { ImGui.Spacing(); UiKit.WarningState(theme, service.Status); }
        UiKit.EndSectionCard();
    }

    private void DrawLifecycleSection(VenueTheme theme)
    {
        UiKit.BeginSectionCard("bingo-lifecycle", theme, "Game");
        var lifecycle = service.ActiveGame.Lifecycle;
        var canCreate = lifecycle is null or "Closed";
        var canStart = lifecycle == "Draft";
        var canLeave = !string.IsNullOrWhiteSpace(service.ActiveGame.RoomCode);

        // Game Type near Create Game (live-QA correction): previously only reachable from Settings, buried away
        // from where the host actually needs to change it most — right before creating the next game. This is the
        // SAME persistent per-venue default Settings' own Game Type control edits (VenueBingoDefaults.GameType via
        // the identical Save/SaveDefaults path) — never a second, independent Game Type state that could silently
        // disagree with Settings'. Editable ONLY while no game exists yet (canCreate): once Created/Started, the
        // active game's own locked snapshot (below) is authoritative and this default no longer applies to it —
        // changing the default here or in Settings can never retroactively alter an in-progress game's rules.
        if (canCreate)
        {
            var gameTypes = new[] { "Single Line", "Two Lines", "Four Corners", "Blackout" };
            var gameTypeIndex = Array.IndexOf(gameTypes, service.Defaults.GameType);
            if (gameTypeIndex < 0) gameTypeIndex = 0;
            if (Forms.ComboField(theme, "Game Type (for the next game)", gameTypes, ref gameTypeIndex)) Save(service.Defaults with { GameType = gameTypes[gameTypeIndex] });
        }
        else
        {
            ImGui.TextUnformatted($"Game Type: {service.ActiveGame.Snapshot?.GameType ?? service.Defaults.GameType}");
            UiKit.Tooltip("Locked for this game once created — Leave/Close Room and create a new game to change it.");
        }
        ImGui.Spacing();

        ImGui.BeginDisabled(!canCreate);
        if (UiKit.PrimaryButton(theme, "Create Game")) _ = service.CreateGameAsync();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!canStart);
        if (UiKit.PrimaryButton(theme, "Start Game")) _ = service.StartGameAsync();
        ImGui.EndDisabled();
        ImGui.SameLine();
        // "Leave Game" (product correction): purely local detach, never a backend request — see
        // VenueBingoService.LeaveGame's doc comment. This is deliberately NOT behind a confirmation dialog, since
        // it is fully reversible via Resume below and nothing is lost or changed on the backend by pressing it.
        ImGui.BeginDisabled(!canLeave);
        if (UiKit.GhostButton(theme, "Leave Game")) service.LeaveGame();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Refresh")) _ = service.PollAsync();

        ImGui.Spacing();
        if (ImGui.TreeNode("This venue's rooms (host handoff / resume)"))
        {
            var hasRoomKey = !string.IsNullOrWhiteSpace(service.Defaults.Connection.RoomKey);
            if (!hasRoomKey)
            {
                UiKit.WarningState(theme, "Configure this venue's Room Key in Settings -> Modules -> Bingo to list or resume its rooms.");
            }
            else
            {
                ImGui.TextWrapped("Every room this venue's Room Key was used to create (by this host or any other, including a prior session or a crashed host) — pick one to resume from wherever the backend says it currently stands.");
                ImGui.BeginDisabled(roomListLoading);
                if (UiKit.GhostButton(theme, "Refresh room list")) _ = RefreshRoomListAsync();
                ImGui.EndDisabled();
                if (roomList is not null)
                {
                    if (roomList.Count == 0) UiKit.EmptyState(theme, "No rooms found", "This venue's Room Key has no rooms on this backend yet.");
                    foreach (var room in roomList)
                    {
                        ImGui.Spacing();
                        var updated = room.UpdatedAt is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("g") : "unknown";
                        var subtitle = $"{room.GameType} - {room.CalledNumbersCount} called - updated {updated}";
                        UiKit.ListRow(theme, room.RoomCode, subtitle, false);
                        ImGui.SameLine();
                        if (UiKit.GhostButton(theme, $"Resume##{room.RoomCode}"))
                        { service.ObserveExistingRoom(room.RoomCode, service.Defaults.Connection.RoomKey!); _ = service.PollAsync(); }

                        // Active-room protection (product correction): the room currently being operated cannot be
                        // deleted from this list — Leave Game (above) is the one safe way to stop operating it
                        // first. This avoids offering a destructive delete for the very room a live session is
                        // currently reading from, and guarantees local ActiveGame state is never ripped out from
                        // under a live session by a click in an unrelated list.
                        var isActiveRoom = string.Equals(service.ActiveGame.RoomCode, room.RoomCode, StringComparison.Ordinal) && service.ActiveGame.Lifecycle is not (null or "Closed");
                        ImGui.SameLine();
                        ImGui.BeginDisabled(isActiveRoom);
                        if (UiKit.DangerButton(theme, $"Close Room##{room.RoomCode}"))
                            confirmDialog.Request("Close Bingo Room?", $"Room: {room.RoomCode}. This permanently removes this room and its persisted game state from the Bingo server. It cannot be resumed afterward.", () => _ = DeleteRoomAndRefreshAsync(room.RoomCode));
                        ImGui.EndDisabled();
                        if (isActiveRoom) UiKit.Tooltip("Leave the active game before closing this room.");
                    }
                }
                ImGui.Spacing();
                UiKit.Divider(theme);
                ImGui.TextWrapped("Know a room code that isn't listed yet (e.g. from another host, just created)? Resume it directly — it always uses this venue's configured Room Key, never a retyped one.");
                Forms.TextField(theme, "Room code", ref observeRoomCode, 64);
                if (UiKit.PrimaryButton(theme, "Resume by code") && !string.IsNullOrWhiteSpace(observeRoomCode))
                { service.ObserveExistingRoom(observeRoomCode, service.Defaults.Connection.RoomKey!); _ = service.PollAsync(); }
            }
            ImGui.TreePop();
        }
        UiKit.EndSectionCard();
    }

    private async Task RefreshRoomListAsync()
    {
        roomListLoading = true;
        roomList = await service.ListVenueRoomsAsync();
        roomListLoading = false;
    }

    /// <summary>Only refreshes the list on a successful delete — service.Status/Diagnostics already report a
    /// failure via the status section, and the room legitimately still exists on the backend in that case, so
    /// leaving it listed is correct, not stale.</summary>
    private async Task DeleteRoomAndRefreshAsync(string roomCode)
    {
        if (await service.DeleteRoomAsync(roomCode)) await RefreshRoomListAsync();
    }

    private void DrawNumberCallingSection(VenueTheme theme)
    {
        UiKit.BeginSectionCard("bingo-calling", theme, "Number Calling");
        var defaults = service.Defaults;
        var rollOptions = new[] { "Random (/random 75)", "Dice (/dice 75)" };
        var rollIndex = defaults.RollCommand == BingoRollCommand.Dice ? 1 : 0;
        if (Forms.ComboField(theme, "Roll command", rollOptions, ref rollIndex)) service.SaveDefaults(defaults with { RollCommand = rollIndex == 1 ? BingoRollCommand.Dice : BingoRollCommand.Random });
        if (rollIndex == 1) UiKit.WarningState(theme, "/dice 75's exact party-chat result format and whether it requires being in a party have not been verified in-game for Bingo — a roll may simply time out with no result rather than being called. It never falls back to /random automatically; switch modes yourself if needed. Only YOUR OWN roll can ever be accepted, even if another party member also uses /dice 75.");

        var announceOptions = new[] { "Shout", "Yell", "Party", "None" };
        var announceIndex = (int)defaults.AnnounceChannel;
        if (Forms.ComboField(theme, "Announce channel", announceOptions, ref announceIndex)) service.SaveDefaults(defaults with { AnnounceChannel = (BingoAnnounceChannel)announceIndex });

        ImGui.Spacing();
        var canCall = service.ActiveGame.Lifecycle is "Draft" or "Active" or "Legacy";
        ImGui.BeginDisabled(!canCall);
        if (UiKit.PrimaryButton(theme, "Roll & Call")) service.RollAndCall();
        ImGui.EndDisabled();
        ImGui.SameLine();
        UiKit.StatusBadge(theme, $"{service.ActiveGame.CalledNumbers.Count} called", ToastLevel.Information);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, calledNumbersWindow.IsOpen ? "Hide Called Numbers" : "Show Called Numbers")) calledNumbersWindow.IsOpen = !calledNumbersWindow.IsOpen;

        // Live diagnostic trace (product correction: a prior live /random failure gave the operator nothing but
        // "0 called" to go on). Shows exactly where the pipeline stops — chat event received, parser match,
        // local-host check, correlator accept/reject reason, backend accept/reject, announcement sent/failed —
        // directly in this panel, without needing to find/read Dalamud's own log file. Bounded to the last 40
        // entries (VenueBingoService.MaxRecentRollDiagnostics) and only populated while a roll is actually
        // outstanding, never a running log of all chat traffic.
        ImGui.Spacing();
        if (ImGui.TreeNode("Roll diagnostics (for troubleshooting /random or /dice not registering)"))
        {
            var trace = service.RecentRollDiagnostics;
            if (trace.Count == 0) UiKit.EmptyState(theme, "No roll activity yet", "Press Roll & Call to see the live event trace here.");
            else
            {
                ImGui.BeginChild("bingo-roll-diagnostics", new Vector2(0, 160), true);
                foreach (var line in trace) ImGui.TextWrapped(line);
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1f); // auto-follow the latest line
                ImGui.EndChild();
            }
            ImGui.TreePop();
        }
        UiKit.EndSectionCard();
    }

    private void DrawRosterSection(VenueTheme theme)
    {
        UiKit.BeginSectionCard("bingo-roster", theme, $"Players ({service.ActiveGame.Players.Count})");
        var canEdit = service.ActiveGame.Lifecycle is not null;

        if (service.ActiveGame.Players.Count == 0) UiKit.EmptyState(theme, "No players yet", "Add a player below.");
        else foreach (var (seed, player) in service.ActiveGame.Players.ToArray()) DrawPlayerRow(theme, seed, player, canEdit);

        // Room resume / plugin reload (product correction): a player already visible in the roster whose link has
        // never been ensured THIS panel lifetime gets one automatic attempt so the URL becomes visible without the
        // operator having to press Copy Link on every player after every resume — bounded to once per seed by
        // playerLinkEnsureAttempted (see its doc comment). Skipped entirely once a current-or-over-provisioned link
        // is already cached, so this is a no-op scan on every frame after the first successful attempt.
        if (canEdit) foreach (var (seed, player) in service.ActiveGame.Players)
        {
            var total = (player.PaidCount ?? player.Count) + (player.CompCount ?? 0);
            if (total <= 0 || playerLinkEnsureAttempted.Contains(seed)) continue;
            if (playerLinkCache.TryGetValue(seed, out var cached) && cached.Count >= total) continue;
            _ = EnsurePlayerLinkAsync(seed, player.Name, total);
        }

        ImGui.Spacing();
        UiKit.SectionHeader(theme, "New Player");
        ImGui.BeginDisabled(!canEdit);
        DrawNewPlayerNameRow(theme);
        if (!string.IsNullOrWhiteSpace(newPlayerHomeWorld)) ImGui.TextDisabled($"@ {newPlayerHomeWorld}");
        if (!string.IsNullOrWhiteSpace(newPlayerTargetError)) UiKit.WarningState(theme, newPlayerTargetError);
        Forms.NumericField(theme, "Paid cards", ref newPlayerPaid, 1, 0, 16);
        Forms.NumericField(theme, "Comp cards", ref newPlayerComp, 1, 0, 16);
        if (UiKit.PrimaryButton(theme, "Add Player") && !string.IsNullOrWhiteSpace(newPlayerName))
        {
            var seed = Guid.NewGuid().ToString("N");
            var name = newPlayerName.Trim();
            _ = AddPlayerAsync(seed, name, newPlayerPaid, newPlayerComp);
            newPlayerName = ""; newPlayerHomeWorld = ""; newPlayerTargetError = null; newPlayerPaid = 1; newPlayerComp = 0;
        }
        ImGui.EndDisabled();
        UiKit.EndSectionCard();
    }

    /// <summary>Add Player's full transaction (product correction): backend card allocation and short-link
    /// generation are related but deliberately NOT one fragile all-or-nothing transaction. If <see cref="VenueBingoService.GrantCardsAsync"/>
    /// fails, nothing else happens (no player was created — service.Status already carries why). If it succeeds but
    /// the link generation that follows fails, the player and their authoritative card allocation are still fully
    /// visible and correct; only the link shows an error, retryable via Copy Link/New Link — see
    /// <see cref="EnsurePlayerLinkAsync"/>.</summary>
    private async Task AddPlayerAsync(string seed, string name, int paidCount, int compCount)
    {
        if (!await service.GrantCardsAsync(seed, name, paidCount, compCount).ConfigureAwait(false)) return;
        await EnsurePlayerLinkAsync(seed, name, paidCount + compCount).ConfigureAwait(false);
    }

    /// <summary>+/-Paid/+/-Comp's full transaction — same "card allocation succeeds independently of link ensure"
    /// contract as <see cref="AddPlayerAsync"/>. A card-count change that pushes the player's total PAST what their
    /// currently cached/backend-known link covers automatically ensures an updated current link (Custom Letters-
    /// style correction: the operator should never see a blank URL after clicking +Paid until they separately
    /// remember to press Copy Link).</summary>
    private async Task AdjustCardsAsync(string seed, string name, int paidCount, int compCount)
    {
        if (!await service.GrantCardsAsync(seed, name, paidCount, compCount).ConfigureAwait(false)) return;
        await EnsurePlayerLinkAsync(seed, name, paidCount + compCount).ConfigureAwait(false);
    }

    /// <summary>The single "make sure this player's currently-visible link is right" operation — used by
    /// <see cref="AddPlayerAsync"/>, <see cref="AdjustCardsAsync"/>, the room-resume roster scan in
    /// <see cref="DrawRosterSection"/>, and <see cref="CopyPlayerLinkAsync"/>'s no-cache fallback. Skips the backend
    /// entirely when the cache already has a current-or-over-provisioned link (no wasted call). On failure, records
    /// a per-seed status in <see cref="playerLinkErrors"/> instead of throwing/rolling anything back — the player
    /// and their cards are never affected by a link failure.</summary>
    private async Task EnsurePlayerLinkAsync(string seed, string name, int totalCardCount)
    {
        playerLinkEnsureAttempted.Add(seed);
        if (playerLinkCache.TryGetValue(seed, out var cached) && cached.Count >= totalCardCount) { playerLinkErrors.Remove(seed); return; }
        var result = await service.EnsureCurrentPlayerLinkAsync(seed, name, totalCardCount).ConfigureAwait(false);
        if (result is null) { playerLinkErrors[seed] = service.Status ?? "Link generation failed."; return; }
        playerLinkErrors.Remove(seed);
        playerLinkCache[seed] = (result.Value.Url, result.Value.Count);
    }

    /// <summary>Compact per-player row (live-QA density correction — matches the donor's operational density, using
    /// VenueOS's own components): name + Paid/Comp/Total on one reflowing line, then a wrapping action-button row.
    /// Neither line ever clips off-window at a narrow width (NEW_MODULE_GUIDE.md §35).</summary>
    private void DrawPlayerRow(VenueTheme theme, string seed, BingoPlayer player, bool canEdit)
    {
        var paid = player.PaidCount ?? player.Count;
        var comp = player.CompCount ?? 0;
        var total = paid + comp;
        ImGui.PushID(seed);

        // Balls-to-Bingo (live-QA addition): backend-authoritative — the fewest additional CALLED numbers (never
        // daub state; see VenueBingoService.EnsureCurrentPlayerLinkAsync's sibling ballsToBingo doc trail in
        // docs/BINGO_V2_PROTOCOL.md) that could complete a valid pattern on ANY of this player's cards, under the
        // active game type. Shown immediately after the name, e.g. "Kei Joi (1)"; omitted entirely for a Legacy
        // room (no backend equivalent exists there) or a seed the backend hasn't computed a value for yet.
        var displayName = service.ActiveGame.BallsToBingo.TryGetValue(seed, out var ballsToBingo) ? $"{player.Name} ({ballsToBingo})" : player.Name;

        var availWidth = ImGui.GetContentRegionAvail().X;
        var statsText = $"Paid {paid} · Comp {comp} · Total {total}";
        var fitsOnOneLine = ImGui.CalcTextSize(displayName).X + ImGui.CalcTextSize(statsText).X + ImGui.GetStyle().ItemSpacing.X * 2 <= availWidth;
        ImGui.TextUnformatted(displayName);
        if (fitsOnOneLine) ImGui.SameLine(availWidth - ImGui.CalcTextSize(statsText).X); // reserved right edge — reflows to its own line instead of clipping if it doesn't fit
        ImGui.TextDisabled(statsText);

        // Visible short URL (live-QA request): the host commonly pastes this into an FFXIV tell and wants to
        // visually verify it before pressing Send, not just trust that Copy Link silently worked. Reads from the
        // SAME playerLinkCache every ensure/Copy Link path writes to — never a separate fetch here, never triggers
        // its own backend call. Now populated automatically (Add Player / card-count changes / room-resume roster
        // scan — see DrawRosterSection/EnsurePlayerLinkAsync), so it is normally already visible on first render,
        // not only after the operator manually presses Copy Link. >= (not ==): a link generated for a HIGHER count
        // than the player's current total is still perfectly valid to show/copy (a card-count DECREASE keeps its
        // existing link — the browser only ever rejects a count INCREASE past what a link's payload allows).
        if (playerLinkCache.TryGetValue(seed, out var cachedLink) && cachedLink.Count >= total) DrawPlayerLinkText(theme, seed, cachedLink.Url);
        else if (playerLinkErrors.TryGetValue(seed, out var linkError)) UiKit.WarningState(theme, $"Link: {linkError}");

        var rightEdge = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;
        ImGui.BeginDisabled(!canEdit);
        if (WrapButton(theme, "+ Paid", new Vector2(64, 0), rightEdge, ref first)) _ = AdjustCardsAsync(seed, player.Name, paid + 1, comp);
        if (WrapButton(theme, "- Paid", new Vector2(64, 0), rightEdge, ref first) && paid > 0) _ = AdjustCardsAsync(seed, player.Name, paid - 1, comp);
        if (WrapButton(theme, "+ Comp", new Vector2(64, 0), rightEdge, ref first)) _ = AdjustCardsAsync(seed, player.Name, paid, comp + 1);
        if (WrapButton(theme, "- Comp", new Vector2(64, 0), rightEdge, ref first) && comp > 0) _ = AdjustCardsAsync(seed, player.Name, paid, comp - 1);
        ImGui.EndDisabled();
        if (WrapButton(theme, "Copy Link", new Vector2(84, 0), rightEdge, ref first)) _ = CopyPlayerLinkAsync(seed, player.Name, total, false);
        if (WrapButton(theme, "New Link", new Vector2(80, 0), rightEdge, ref first)) _ = CopyPlayerLinkAsync(seed, player.Name, total, true);
        UiKit.Tooltip("Copies a fresh link and replaces this row's cached one. The OLD link keeps working (short links are never deleted server-side) — it just keeps showing the card count from when IT was generated.");
        // Live-QA correction: kick one immediate poll alongside opening the viewer, so the host checking a claim
        // doesn't wait up to ~5s for the existing Tick() poll cadence to bring in the player's latest daubs.
        if (WrapButton(theme, "View Cards", new Vector2(90, 0), rightEdge, ref first)) { playerCardViewerWindow.ShowPlayer(seed); _ = service.PollAsync(); }

        ImGui.PopID();
        UiKit.Divider(theme);
    }

    /// <summary>Draws a ghost button as part of a horizontally-wrapping row — SameLine's onto the previous button if
    /// there's room, otherwise starts a fresh line, instead of letting a long button row clip off-window at a narrow
    /// width (NEW_MODULE_GUIDE.md §35). <paramref name="rightEdge"/> is the row's fixed screen-space right edge,
    /// captured once before the first button in the row.</summary>
    private static bool WrapButton(VenueTheme theme, string label, Vector2 size, float rightEdge, ref bool first)
    {
        if (!first && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + size.X < rightEdge) ImGui.SameLine();
        first = false;
        return UiKit.GhostButton(theme, label, size);
    }

    /// <summary>Renders a player's short URL as selectable/readable text on its own wrapped line — a read-only
    /// full-width <c>InputText</c> (same "no-label, full-width" trick as <see cref="Forms.TextField"/>'s internal
    /// <c>ImGui.SetNextItemWidth(-1)</c>, but with <see cref="ImGuiInputTextFlags.ReadOnly"/> so it can never be
    /// edited) rather than a plain wrapped <c>TextWrapped</c>: an ImGui InputText can be click-selected and
    /// Ctrl+C'd on its own, which a bare text label cannot, and a single-line field never forces this row wider
    /// than the panel the way an unbroken URL string inside a fixed-width text run could. The buffer is rebuilt
    /// from <paramref name="url"/> every frame — safe because ReadOnly means ImGui never mutates it back.</summary>
    private static void DrawPlayerLinkText(VenueTheme theme, string seed, string url)
    {
        ImGui.PushID($"link-{seed}");
        ImGui.SetNextItemWidth(-1);
        Forms.PushFieldStyle(theme);
        var buffer = url;
        ImGui.InputText("##playerlink", ref buffer, Math.Max(url.Length + 1, 1), ImGuiInputTextFlags.ReadOnly);
        Forms.PopFieldStyle();
        ImGui.PopID();
    }

    /// <summary>Copy Link (product correction): now that a player's link is normally already ensured/visible
    /// automatically (see <see cref="EnsurePlayerLinkAsync"/>), this normally does nothing but copy the
    /// already-cached, already-current URL to the clipboard (<c>ImGui.SetClipboardText</c> — the same mechanism
    /// <c>MairsTriviaOperatorPanel</c>'s "Copy Link"/"Copy Series Link" already use). It only falls back to actually
    /// generating one when no valid current link is cached at all — a legacy/resumed room, a temporary earlier
    /// failure, or local cache loss — via the same <see cref="EnsurePlayerLinkAsync"/> path the automatic flows use
    /// (backend lookup first, so this recovery path can never mint a needless duplicate code either). "New Link"
    /// (<paramref name="forceRegenerate"/>) bypasses all of that and always mints a brand-new code, unconditionally —
    /// the OLD link keeps working (short links are never deleted server-side), it just keeps showing the card count
    /// from when IT was generated.</summary>
    private async Task CopyPlayerLinkAsync(string seed, string name, int totalCardCount, bool forceRegenerate)
    {
        if (forceRegenerate)
        {
            var freshUrl = await service.GetOrCreatePlayerLinkAsync(seed, name, totalCardCount).ConfigureAwait(false);
            if (freshUrl is null) { playerLinkErrors[seed] = service.Status ?? "Link generation failed."; return; }
            playerLinkErrors.Remove(seed);
            playerLinkCache[seed] = (freshUrl, totalCardCount);
            ImGui.SetClipboardText(freshUrl);
            return;
        }

        // >= (not ==): a cached link generated for a HIGHER count than the player's current total is still valid —
        // see DrawPlayerRow's matching comment. Only actually missing/under-provisioned reaches EnsurePlayerLinkAsync.
        if (!(playerLinkCache.TryGetValue(seed, out var cached) && cached.Count >= totalCardCount))
            await EnsurePlayerLinkAsync(seed, name, totalCardCount).ConfigureAwait(false);

        if (playerLinkCache.TryGetValue(seed, out var current) && current.Count >= totalCardCount) ImGui.SetClipboardText(current.Url);
        // else: playerLinkErrors[seed] already carries the failure reason (also shown inline under the player row)
    }

    /// <summary>Compact inline field (<see cref="Forms.PushFieldStyle"/>/<see cref="Forms.PopFieldStyle"/> are
    /// exposed exactly for this "same-line list-row edit" case — see their doc comment) instead of the fully labeled
    /// <see cref="Forms.TextField"/>. Root cause of "Use Current Target" being effectively unreachable in the
    /// previous layout: <c>Forms.TextField</c> always claims the FULL row width internally
    /// (<c>ImGui.SetNextItemWidth(-1)</c>) and draws label-then-field as two lines, so chaining a bare
    /// <c>ImGui.SameLine()</c> after it — exactly what the previous code did — placed the button past the field's
    /// full-width right edge, off past the visible content region at any normal window width (NEW_MODULE_GUIDE.md
    /// §35 explicitly warns against this exact pattern). The button was structurally present, reachable in code, and
    /// not disabled — it was simply drawn off-window. Reserving the button's width FIRST and sizing the field to
    /// what's left fixes this.</summary>
    private void DrawNewPlayerNameRow(VenueTheme theme)
    {
        Forms.FieldLabel(theme, "New player name");
        const string useTargetLabel = "Use Current Target";
        var useTargetWidth = ImGui.CalcTextSize(useTargetLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        var fieldWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - useTargetWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(fieldWidth);
        Forms.PushFieldStyle(theme);
        ImGui.InputTextWithHint("##newPlayerName", "Player name", ref newPlayerName, 64);
        Forms.PopFieldStyle();
        ImGui.SameLine();
        // Reads the current target ONLY — never creates the player, never issues/charges cards, never touches the
        // backend by itself. Reuses the exact same ITargetedPlayerProvider the payout section's own "Use Current
        // Target" already relies on (no target / non-player-character target both fail cleanly with a status
        // message rather than throwing).
        if (UiKit.GhostButton(theme, useTargetLabel))
        {
            var lookup = targetProvider.GetTargetedPlayer();
            if (lookup.Success) { newPlayerName = lookup.Name; newPlayerHomeWorld = lookup.HomeWorld; newPlayerTargetError = null; }
            else { newPlayerHomeWorld = ""; newPlayerTargetError = lookup.Error; }
        }
    }

    private void DrawPayoutSection(VenueTheme theme)
    {
        UiKit.BeginSectionCard("bingo-payouts", theme, "Payout Ledger");
        ImGui.TextWrapped("Automatic in-game trade payout has never been verified against a live client — see Settings → Diagnostics for engine errors. An ambiguous outcome is NOT the same as unpaid: it always requires manual reconciliation below, never an automatic retry.");
        ImGui.Spacing();

        DrawCallerStatus(theme);
        ImGui.Spacing();
        UiKit.StatCard(theme, "trophy", service.ActiveGame.CurrentPrizePool.ToString("N0"), "Current prize pool", theme.Tokens.Success);
        ImGui.SameLine();
        UiKit.StatCard(theme, "star", service.ActiveGame.SplitAmount.ToString("N0"), "Per-caller split", theme.Tokens.Accent);
        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Sync Payouts")) _ = service.SyncPayoutsAsync();
        UiKit.Tooltip("Creates/updates a payout obligation for every current Bingo caller at the backend-computed split amount. Safe to click repeatedly — this is the ONLY way an obligation gets created; there is no manual winner selection.");

        ImGui.Spacing();
        UiKit.Divider(theme);

        var payouts = service.ActiveGame.Payouts;
        if (payouts.Count == 0) UiKit.EmptyState(theme, "No payout obligations yet", "Click Sync Payouts above once a player has called Bingo.");
        else
            foreach (var obligation in payouts)
            {
                ImGui.PushID(obligation.PayoutId);
                UiKit.StatusBadge(theme, obligation.Status, obligation.Status == "paid" ? ToastLevel.Success : ToastLevel.Information);
                ImGui.SameLine();
                ImGui.TextUnformatted($"{obligation.WinnerName} — owed {obligation.TotalOwed:N0}, paid {obligation.ConfirmedPaid:N0}, outstanding {obligation.Outstanding:N0}");

                foreach (var attempt in obligation.Attempts ?? [])
                {
                    var level = attempt.Status switch { "confirmed" => ToastLevel.Success, "ambiguous" => ToastLevel.Warning, "failed" or "canceled" => ToastLevel.Error, _ => ToastLevel.Information };
                    ImGui.Indent();
                    UiKit.StatusBadge(theme, $"{attempt.Status} · {attempt.Amount:N0}", level);
                    if (attempt.Status is "ambiguous" or "pending")
                    {
                        ImGui.SameLine();
                        if (UiKit.GhostButton(theme, "Mark Paid##" + attempt.AttemptId)) confirmDialog.Request("Mark this attempt as actually paid?", "Only do this if you have independently confirmed (e.g. via /tell or your own gil log) that the recipient received this exact amount.", () => _ = service.ReconcileAttemptAsync(obligation.PayoutId, attempt.AttemptId, true, "Manually reconciled from operator panel."));
                        ImGui.SameLine();
                        if (UiKit.GhostButton(theme, "Mark Not Paid##" + attempt.AttemptId)) confirmDialog.Request("Mark this attempt as not paid?", "This leaves the amount outstanding so a new attempt can be created.", () => _ = service.ReconcileAttemptAsync(obligation.PayoutId, attempt.AttemptId, false, "Manually reconciled from operator panel."));
                    }
                    ImGui.Unindent();
                }

                if (obligation.Status == "open") DrawAttemptPayoutControls(theme, obligation);
                ImGui.PopID();
                UiKit.Divider(theme);
            }

        UiKit.EndSectionCard();
    }

    /// <summary>Backend-authoritative caller status (product correction — replaces the old manual "Create Payout
    /// Obligation" Winner dropdown + typed total-owed field entirely): the backend already determines who validly
    /// called Bingo, so the host never manually picks a winner from the roster. Callers are shown chronologically,
    /// oldest first, exactly as the backend returns them — an earlier caller is never hidden once a later one
    /// appears. A defensive DistinctBy-style collapse already happened service-side
    /// (<see cref="VenueBingoService"/>'s own de-dup), so this only ever renders what's already unique.</summary>
    private void DrawCallerStatus(VenueTheme theme)
    {
        var callers = service.ActiveGame.BingoCallers;
        if (callers.Count == 0) { UiKit.EmptyState(theme, "No Bingo callers yet", "Click Sync Payouts below once a player has called Bingo."); return; }
        if (callers.Count == 1) { ImGui.TextWrapped($"Last Bingo: {callers[0].Name} @ {FormatCallerTimestamp(callers[0].Timestamp)}."); return; }

        UiKit.SectionHeader(theme, "Bingo Callers");
        foreach (var caller in callers)
        {
            ImGui.PushID(caller.Seed);
            ImGui.TextUnformatted($"{caller.Name} — {FormatCallerTimestamp(caller.Timestamp)}");
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "View Cards")) { playerCardViewerWindow.ShowPlayer(caller.Seed); _ = service.PollAsync(); }
            ImGui.PopID();
        }
    }

    private static string FormatCallerTimestamp(long unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime.ToString("g");

    private void DrawAttemptPayoutControls(VenueTheme theme, BingoPayoutObligation obligation)
    {
        ImGui.Spacing();
        UiKit.Toggle(theme, "I understand this requires live testing", ref payoutLiveTestingAcknowledged);
        ImGui.BeginDisabled(!payoutLiveTestingAcknowledged || payoutRunInFlight);
        DrawPayoutTargetRow(theme);

        var canAttempt = !string.IsNullOrWhiteSpace(payoutTargetNameAndWorld);
        if (UiKit.PrimaryButton(theme, "Attempt Payout") && canAttempt) _ = AttemptPayoutAsync(obligation, payoutTargetNameAndWorld.Trim());
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Abort")) payoutOrchestrator.Abort();

        UiKit.StatusBadge(theme, payoutRunInFlight ? $"Running: {payoutOrchestrator.Stage}" : "Idle", payoutRunInFlight ? ToastLevel.Information : ToastLevel.Success);
        ImGui.SameLine();
        UiKit.StatusBadge(theme, payoutAutomation.Status, ToastLevel.Information);
        if (lastPayoutRun is not null) ImGui.TextWrapped($"Last attempt: {(lastPayoutRun.FullyPaid ? "fully paid" : lastPayoutRun.FinalStage.ToString())}{(payoutRunError is null ? "" : $" — {payoutRunError}")}");
    }

    /// <summary>Same compact-field-plus-reserved-button fix as <see cref="DrawNewPlayerNameRow"/> — this row had the
    /// identical <c>Forms.TextField</c>-then-bare-<c>SameLine()</c> defect (found while fixing the roster section's
    /// copy of the same bug; this one was assumed correct by the task brief as the reuse precedent, but reading it
    /// line by line showed it wasn't). "Use Current Target" here still only ever fills the target text field — it
    /// never touches the backend or starts a payout attempt by itself.</summary>
    private void DrawPayoutTargetRow(VenueTheme theme)
    {
        Forms.FieldLabel(theme, "Target (Name@World)");
        const string useTargetLabel = "Use Current Target";
        var useTargetWidth = ImGui.CalcTextSize(useTargetLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        var fieldWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - useTargetWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(fieldWidth);
        Forms.PushFieldStyle(theme);
        ImGui.InputText("##payoutTarget", ref payoutTargetNameAndWorld, 128);
        Forms.PopFieldStyle();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, useTargetLabel))
        {
            var lookup = targetProvider.GetTargetedPlayer();
            if (lookup.Success) payoutTargetNameAndWorld = $"{lookup.Name}@{lookup.HomeWorld}";
        }
    }

    private async Task AttemptPayoutAsync(BingoPayoutObligation obligation, string targetNameAndWorld)
    {
        payoutRunInFlight = true; payoutRunError = null;
        var connection = service.Defaults.Connection with { RoomKey = service.ActiveGame.RoomKey };
        try { lastPayoutRun = await payoutOrchestrator.RunAsync(connection, service.ActiveGame.RoomCode, obligation, targetNameAndWorld, 1_000_000, default).ConfigureAwait(false); payoutRunError = lastPayoutRun.Error; }
        finally { payoutRunInFlight = false; _ = service.PollAsync(); }
    }

    private void Save(VenueBingoDefaults defaults) => service.SaveDefaults(defaults);
}
