using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Services;

namespace VenueOS.Plugin.Bingo;

/// <summary>
/// The unsafe, game-version-sensitive Bingo payout (in-game trade) automation engine — the real implementation of
/// <see cref="IBingoPayoutAutomation"/>, mirroring how <c>VenueOS.Plugin.PartyFinder.PartyFinderAutomationService</c>
/// implements <c>IPartyFinderAutomation</c> (constructor-injected Dalamud services + <see cref="DiagnosticsService"/>,
/// no donor static Service locator). Unlike that class, this one is NOT declared <c>unsafe</c> at the class level —
/// its public/async methods must be able to <c>await</c>, which C# disallows inside an unsafe context; every actual
/// pointer/addon-touching helper below is instead individually marked <c>unsafe</c> and returns only plain
/// bool/string results, so no pointer type ever crosses an <c>await</c> boundary.
///
/// Gil-entry hotfix #2 (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md): live QA found the ORIGINAL gil-entry mechanism (a
/// text-based button search on the Trade addon, plus a raw <c>AtkComponentNumericInput.SetValue</c> write) never
/// actually staged anything in the real Trade window. Gil entry (<see cref="TryOpenGilInputPopup"/>/
/// <see cref="TryCommitNumericInput"/>) now ports the donor's own proven-in-production mechanism
/// (<c>ECommons.Automation.Callback.Fire</c> — the game's own native addon-callback protocol) verbatim for that one
/// narrow step, with a mandatory positive read-back (<see cref="TradeGilTextShows"/>) before ever proceeding — see
/// those methods' own doc comments. Everything else about the state machine, thread-affinity marshaling, and
/// payout-ledger authority remains this reconstruction's own; the donor's in-memory payout accounting was never
/// ported.
///
/// UNLIKE PartyFinderAutomationService, this could NOT be built against a donor implementation that already proved
/// its exact addon interaction in production — the donor Bingo plugin's own trade automation (forensic audit §18)
/// is exactly the unsafe, unverified design this reconstruction is replacing (bare <c>/trade</c>, blind
/// <c>SelectYesno</c> accept-anything, gil-delta "success" heuristic). Every place below that assumes a specific
/// addon name, node layout, or chat string is called out explicitly as <c>LIVE VERIFICATION REQUIRED</c> — this
/// class has never been exercised against a live game client in this pass, only compiled against the actual
/// referenced FFXIVClientStructs/Dalamud assemblies (so the TYPES and MEMBER NAMES used here — <c>AddonTrade</c>,
/// <c>AddonInputNumeric</c>'s <c>OkButton</c>/<c>CancelButton</c>/<c>NumericInput</c> fields,
/// <c>AtkComponentNumericInput.SetValue</c> — are real, current members, confirmed by inspecting the actual
/// referenced FFXIVClientStructs.dll's metadata in this session — but the BEHAVIORAL assumptions about what
/// clicking them does, and the exact node layout of the Trade window's own "click here to add gil"/"Ready"
/// controls, are not independently proven here).
///
/// Design (forensic audit §21's recommended safe state machine):
///  - Target identity is pinned by the CALLER (the orchestrator/operator panel resolve a Name@World BEFORE calling
///    <see cref="ExecutePayoutAttemptAsync"/> — this engine never re-derives who to trade with) and re-verified
///    against the actual current game target before any gil is entered — never a bare, target-less <c>/trade</c>.
///  - Completion is gated on the game's own "Trade complete."/"Trade canceled." system-chat messages (subscribed
///    once in the constructor), not on "the trade window closed" and not on a gil-balance delta — a timeout with
///    no chat signal returns Ambiguous, never a guessed Confirmed.
///  - No blind SelectYesno accept-anything: <see cref="TryConfirmIfExpected"/> only clicks a confirmation dialog
///    while THIS attempt is actively expecting one (a local flag set only during the brief post-Ready window).
/// </summary>
public sealed class BingoPayoutAutomationService : IBingoPayoutAutomation, IDisposable
{
    private const string ModuleId = "games.bingo";

    private const string TradeAddonName = "Trade";
    // LIVE VERIFICATION REQUIRED: "InputNumeric" is the addon name commonly used across public FFXIV plugin
    // automation for the game's shared numeric-entry popup (the same one used for gil/item-count prompts
    // throughout the game, not Bingo-specific) — a widely-relied-upon convention, not something specific to this
    // reconstruction, but not independently re-confirmed against a live client in this pass.
    private const string NumericInputAddonName = "InputNumeric";
    private const string SelectYesnoAddonName = "SelectYesno";

    // Gil-entry hotfix #2 (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md): the donor Bingo plugin (ffxivbingo4all, via its vendored
    // Dropbox/TradeTask.cs) never searches the Trade addon for a "Gil"/"Add Gil" button by text, and never writes
    // AddonInputNumeric's NumericInput component directly — both of which this engine originally did, and neither of
    // which is proven to actually propagate a value back to the live Trade window. Instead it fires the addon's own
    // native FireCallback (ECommons.Automation.Callback.Fire, a thin wrapper over AtkUnitBase.FireCallback — the
    // SAME synthetic-event mechanism the game's own UI scripting layer uses to report a button click or a completed
    // numeric entry) with a fixed callback index. This index is the donor's own proven-in-production value for
    // "open the gil input popup" on the Trade addon — LIVE VERIFICATION REQUIRED against the current game client
    // (an addon's internal callback-index protocol is defined by the game itself, not by Dalamud/ECommons, and can
    // change across game versions independent of the FFXIVClientStructs struct layout).
    private const int TradeOpenGilInputCallbackIndex = 2;
    // Bounded wait for the Trade window's own gil display to visibly reflect the amount just entered before this
    // engine trusts it — see TradeGilTextShows's doc comment for why this reads the addon's own text rather than
    // trusting the write merely because FireCallback didn't throw.
    private static readonly TimeSpan StagedGilReconcileTimeout = TimeSpan.FromSeconds(3);

    // Ready/Confirm hotfix #3 (BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md): live QA proved the Trade window's own
    // Ready/Confirm control is — like the gil control (hotfix #2) — not discoverable by rendered text
    // ("Ready to Trade"/"Confirm Trade"/"Ready" never matched anything real). The donor (Dropbox.Framework_Update)
    // never searches for it either — it accesses a fixed node index directly:
    // `(AtkComponentButton*)(addon->UldManager.NodeList[3]->GetComponent())`. Ported verbatim as the button
    // DISCOVERY mechanism only; activation still goes through this class's own already-compiled
    // <see cref="ActivateButton"/> (unchanged) rather than adopting a second, different click technique. LIVE
    // VERIFICATION REQUIRED — an addon's node layout is defined by the game client itself and can shift across
    // versions independent of this donor reference.
    private const int TradeReadyButtonNodeIndex = 3;

    // Ready/Confirm hotfix #3: the donor never confirms an arbitrary SelectYesno — GetSpecificYesno(TradeText)
    // reads the SPECIFIC dialog's own prompt text (NodeList[15]) and only proceeds if it matches the exact
    // Trade-confirmation string, itself read from the game's own localized Addon Excel sheet at row 102223
    // (Dropbox.cs's `TradeText` property). VenueOS's own pre-existing SelectYesno confirmation
    // (<see cref="TryConfirmIfExpected"/>) only gated on an internal "am I expecting one right now" flag — it never
    // checked the dialog's own content, which is weaker than the donor's proven approach and does not meet this
    // task's "never confirm an unrelated Yes/No dialog" requirement now that this stage is live-reachable for the
    // first time. Both are now required to agree before a click ever happens. LIVE VERIFICATION REQUIRED for the
    // node index and for the Excel row id remaining stable in the current game client/data.
    private const int SelectYesnoPromptTextNodeIndex = 15;
    private const uint TradeConfirmationAddonSheetRowId = 102223;

    // Donor's own literal English chat strings (Dropbox.cs:101,107 per the forensic audit's §20 study) — the game's
    // unambiguous trade-outcome signal. LIVE VERIFICATION REQUIRED across non-English game clients (forensic audit
    // §27 item 5) — only ever confirmed as literal English string comparisons by prior study, never independently
    // re-verified here.
    private const string TradeCompleteText = "Trade complete.";
    private const string TradeCanceledText = "Trade canceled.";

    private static readonly TimeSpan TradeOpenTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);
    // LIVE VERIFICATION REQUIRED: an untuned guess at how long to wait for the primary chat signal after the offer
    // is readied before giving up and reporting Ambiguous (never Confirmed) — see forensic audit §29's open product
    // question about exactly this tolerance.
    private static readonly TimeSpan CompletionSignalTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly ITargetManager targetManager;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly DiagnosticsService diagnostics;
    private readonly IFrameworkDispatcher dispatcher;

    private readonly object signalLock = new();
    private string? observedSignal;
    private volatile bool abortRequested;
    private volatile bool expectingConfirmation;
    // Set the instant the Trade addon is first observed ready/open for the CURRENT attempt, reset at the start of
    // every attempt. Lets the top-level catch (below) distinguish an engine failure that happened before any actual
    // game-state-changing step (nothing could have changed hands — always Failed) from one that happened once a
    // trade was genuinely in flight (genuinely uncertain — Ambiguous). See docs/BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md
    // §10/§11 for why this distinction matters for the payout ledger's "ambiguous" semantics.
    private volatile bool tradeWindowOpened;
    // Resolved once (static localized game data, never changes mid-session) and cached across attempts — see
    // GetTradeConfirmationPromptTextAsync.
    private string? tradeConfirmationPromptText;

    public BingoPayoutAutomationService(IClientState clientState, IChatGui chatGui, ITargetManager targetManager, IDataManager dataManager, IPluginLog log, DiagnosticsService diagnostics, IFrameworkDispatcher dispatcher)
    {
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.targetManager = targetManager;
        this.dataManager = dataManager;
        this.log = log;
        this.diagnostics = diagnostics;
        this.dispatcher = dispatcher;
        chatGui.ChatMessage += OnChatMessage;
    }

    public string Status { get; private set; } = "Idle";
    public bool IsBusy { get; private set; }

    public void Dispose() => chatGui.ChatMessage -= OnChatMessage;

    /// <summary>Stops the in-flight attempt. Best-effort attempt to close/decline any open trade window — LIVE
    /// VERIFICATION REQUIRED: the donor's own "Cancel Pay" never did this at all (forensic audit §18/§25); this is
    /// a deliberate improvement attempt using the same generic reject-button search as everywhere else in this
    /// file, not an addon-specific "decline" API (none was found on <c>AddonTrade</c> — see its own doc comment).
    /// Dispatched onto the framework thread (never called inline) for the same reason every other addon touch in
    /// this class is — <see cref="Abort"/> is public API and must not assume its caller happens to already be on
    /// the framework thread.</summary>
    public void Abort()
    {
        abortRequested = true;
        SetStatus("Aborted");
        _ = dispatcher.InvokeAsync<object?>(() =>
        {
            try { TryClickButtonByText(TradeAddonName, ["Cancel", "Decline"], []); }
            catch (Exception ex) { log.Warning(ex, "[{Module}] Best-effort trade-window close on Abort failed.", ModuleId); }
            return null;
        }, CancellationToken.None);
    }

    public async Task<BingoTradeResult> ExecutePayoutAttemptAsync(string targetNameAndWorld, int amount, CancellationToken cancellationToken)
    {
        if (IsBusy) return BingoTradeResult.Failed("An attempt is already in progress.");
        IsBusy = true;
        abortRequested = false;
        expectingConfirmation = false;
        tradeWindowOpened = false;
        lock (signalLock) observedSignal = null;

        BingoTradeResult result;
        try { result = await RunAttemptAsync(targetNameAndWorld, amount, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = BingoTradeResult.Canceled("Canceled."); }
        catch (Exception ex)
        {
            log.Error(ex, "[{Module}] Payout attempt threw an unhandled error.", ModuleId);
            diagnostics.RecordFailure($"{ModuleId}: payout attempt threw ({ex.Message})");
            // Pre-trade-open failures (e.g. this exact bug: target verification thrown off the framework thread)
            // are classified Failed, never Ambiguous — nothing that could move gil has happened yet, so there is
            // nothing genuinely uncertain to reconcile (forensic audit's "ambiguous means genuine payment
            // uncertainty" principle — see BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md §10/§11).
            result = tradeWindowOpened
                ? BingoTradeResult.Ambiguous($"Unhandled engine error: {ex.Message}")
                : BingoTradeResult.Failed($"Unhandled engine error before the trade window opened (no gil could have changed hands): {ex.Message}");
        }
        finally { IsBusy = false; expectingConfirmation = false; }

        // Unconditionally derive the terminal Status from the actual result — see BingoTradeResultStatusText's own
        // doc comment for exactly why every return path must funnel through here rather than leaving whatever
        // mid-flight SetStatus call happened to run last (the live-QA-confirmed "stuck on Verifying pinned
        // target..." defect).
        SetStatus(BingoTradeResultStatusText.Describe(result));
        return result;
    }

    private async Task<BingoTradeResult> RunAttemptAsync(string targetNameAndWorld, int amount, CancellationToken cancellationToken)
    {
        if (!await OnFrameworkThreadAsync(() => clientState.IsLoggedIn, cancellationToken).ConfigureAwait(false)) return Fail("Not logged in.");

        SetStatus($"Verifying pinned target {targetNameAndWorld}...");
        if (!await OnFrameworkThreadAsync(() => CurrentTargetMatches(targetNameAndWorld), cancellationToken).ConfigureAwait(false))
            return Fail("The currently targeted player does not match the pinned winner. Target them again before retrying — this engine deliberately never sends a target-less /trade.");

        SetStatus("Opening trade...");
        if (!await OnFrameworkThreadAsync(() => ExecuteGameCommand("/trade"), cancellationToken).ConfigureAwait(false)) return Fail("Could not send the /trade command.");
        if (!await WaitUntilAsync(() => IsAddonReady(TradeAddonName), TradeOpenTimeout, cancellationToken).ConfigureAwait(false))
            return Fail("Trade window did not open in time.");
        tradeWindowOpened = true;
        if (abortRequested) return BingoTradeResult.Canceled("Aborted before the trade partner could be verified.");

        SetStatus("Verifying trade partner matches the pinned target...");
        // LIVE VERIFICATION REQUIRED: AddonTrade (this FFXIVClientStructs version) carries no own partner-name
        // field — the safe-to-assume fallback used here is that /trade only ever opens a trade with the
        // currently-targeted player, so re-checking the CURRENT target still matches the pinned identity is the
        // best available proxy without a verified partner-name addon field. If the target changed underneath
        // us between opening the trade and this check, that is exactly the failure this guards against.
        if (!await OnFrameworkThreadAsync(() => CurrentTargetMatches(targetNameAndWorld), cancellationToken).ConfigureAwait(false))
            return Fail($"Trade partner no longer matches the pinned winner ({targetNameAndWorld}). Aborting before entering gil.");

        if (abortRequested) return BingoTradeResult.Canceled("Aborted before gil was entered.");
        SetStatus($"Entering {amount:N0} gil...");
        if (!await TryEnterGilAsync(amount, cancellationToken).ConfigureAwait(false)) return Fail("Could not enter the gil amount into the trade.");

        if (abortRequested) return BingoTradeResult.Canceled("Aborted before the offer was confirmed.");
        SetStatus("Waiting for the trade window's Ready/Confirm control...");
        if (!await WaitUntilAsync(() => IsTradeReadyButtonAvailable(), StepTimeout, cancellationToken).ConfigureAwait(false))
            return Fail("Could not find a Ready/Confirm button on the trade window.");

        // Re-check immediately before this irreversible step (BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md §27) — an abort
        // that arrived while we were waiting for the control must never be followed by activating it anyway.
        if (abortRequested) return BingoTradeResult.Canceled("Aborted before the offer was confirmed.");
        SetStatus("Confirming offer...");
        expectingConfirmation = true;
        if (!await OnFrameworkThreadAsync(() => TryActivateTradeReadyButton(), cancellationToken).ConfigureAwait(false))
            return Fail("Found the Ready/Confirm control on the trade window but could not activate it.");

        // Resolved once per attempt (cached across attempts — see GetTradeConfirmationPromptTextAsync): the exact
        // localized Trade-confirmation prompt text, used below to make sure the SelectYesno this attempt reacts to
        // is genuinely the Trade confirmation and never an unrelated Yes/No dialog that happens to appear during
        // the same window (BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md §21).
        var expectedConfirmationPrompt = await GetTradeConfirmationPromptTextAsync(cancellationToken).ConfigureAwait(false);

        SetStatus("Waiting for the game's \"Trade complete.\"/\"Trade canceled.\" chat signal...");
        var deadline = DateTime.UtcNow + CompletionSignalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (abortRequested || cancellationToken.IsCancellationRequested) return BingoTradeResult.Canceled("Aborted while awaiting the completion signal.");
            string? signal; lock (signalLock) signal = observedSignal;
            if (signal == TradeCompleteText) return BingoTradeResult.Confirmed(signal);
            if (signal == TradeCanceledText) return BingoTradeResult.Canceled(signal);
            await OnFrameworkThreadAsync(() => TryConfirmIfExpected(expectedConfirmationPrompt), cancellationToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // Ambiguous — NEVER guessed as Confirmed on a bounded timeout (forensic audit §21 principle 1 & 4).
        return BingoTradeResult.Ambiguous("Timed out waiting for the primary chat completion signal; no \"Trade complete.\"/\"Trade canceled.\" message was observed.");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Framework-thread marshaling — every one of the unsafe/addon/target-table touches below MUST run via this
    // dispatcher, never invoked directly from this async method chain. Once any step here has awaited a network
    // call or a Task.Delay, the continuation resumes on an arbitrary thread-pool thread, NOT the game's main/
    // framework thread — calling a Dalamud game-state API from there is exactly what produced the live "Not on
    // main thread!" exception this class was fixed for (see docs/BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md). Uses the
    // existing VenueOS.Services.IFrameworkDispatcher abstraction (already established by ChatCommandService) so
    // this class needs no live Dalamud IFramework in tests — only VenueOS.Plugin's production wiring supplies the
    // real dispatcher (VenueOS.Plugin.DalamudFrameworkDispatcher).
    // -----------------------------------------------------------------------------------------------------------
    private Task<T> OnFrameworkThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => dispatcher.InvokeAsync(action, cancellationToken);

    private Task OnFrameworkThreadAsync(Action action, CancellationToken cancellationToken) => dispatcher.InvokeAsync<object?>(() => { action(); return null; }, cancellationToken);

    // -----------------------------------------------------------------------------------------------------------
    // Chat-signal capture — the primary completion oracle. Subscribed once for the lifetime of this service; the
    // running attempt (if any) reads observedSignal, it is never consumed/cleared here to avoid a race between the
    // Framework thread (which raises ChatMessage) and the attempt's own polling loop.
    // -----------------------------------------------------------------------------------------------------------
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsBusy) return;
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text)) return;
        // Matched on literal text content only, not exclusively a specific XivChatType — LIVE VERIFICATION
        // REQUIRED: the exact chat-type classification these two lines use was not independently re-confirmed in
        // this pass (forensic audit §20 found them as literal string comparisons in the Dropbox reference too).
        if (text.Contains(TradeCompleteText, StringComparison.Ordinal)) lock (signalLock) observedSignal = TradeCompleteText;
        else if (text.Contains(TradeCanceledText, StringComparison.Ordinal)) lock (signalLock) observedSignal = TradeCanceledText;
    }

    private bool CurrentTargetMatches(string targetNameAndWorld) => targetManager.Target is IPlayerCharacter player && NameAndWorldMatches(player, targetNameAndWorld);

    private static bool NameAndWorldMatches(IPlayerCharacter player, string targetNameAndWorld)
    {
        var name = player.Name.TextValue?.Trim();
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText().Trim() ?? player.CurrentWorld.ValueNullable?.Name.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return false;
        return string.Equals($"{name}@{world}", targetNameAndWorld, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Never a blind "accept any SelectYesno" — requires BOTH that THIS attempt is actively expecting a
    /// post-Ready confirmation (forensic audit §18's single riskiest donor behavior, deliberately not replicated)
    /// AND, as of the Ready/Confirm hotfix (#3), that the specific SelectYesno dialog currently open is genuinely
    /// the Trade confirmation — verified by its own prompt text, never assumed merely because a SelectYesno addon
    /// happens to be open right now. If <paramref name="expectedPromptText"/> could not be resolved (see
    /// <see cref="GetTradeConfirmationPromptTextAsync"/>), this fails closed and never clicks anything — an
    /// operator can always confirm manually; this engine must never guess.</summary>
    private unsafe void TryConfirmIfExpected(string? expectedPromptText)
    {
        if (!expectingConfirmation) return;
        if (string.IsNullOrWhiteSpace(expectedPromptText)) return;
        if (!TryGetReadyAddon(SelectYesnoAddonName, out var addon)) return;
        if (!TryReadSelectYesnoPromptText(addon, out var promptText)) return;
        if (!string.Equals(promptText.Trim(), expectedPromptText.Trim(), StringComparison.Ordinal)) return;
        if (TryClickButtonByText(SelectYesnoAddonName, ["Yes"], ["No"])) expectingConfirmation = false;
    }

    /// <summary>Resolves the exact localized Trade-confirmation prompt text via the game's own Addon Excel sheet —
    /// ports ffxivbingo4all's donor-proven <c>Dropbox.TradeText</c> (<c>Svc.Data.GetExcelSheet&lt;Addon&gt;()
    /// .GetRow(102223).Text.ExtractText()</c>) verbatim, used there to pick the Trade confirmation out from any
    /// other concurrently-open SelectYesno dialog (<c>Dropbox.GetSpecificYesno</c>). Cached after the first
    /// successful resolution since this is static localized data, not live game state. Returns null (never throws)
    /// on any failure — <see cref="TryConfirmIfExpected"/> treats that as "never confirm," not "confirm anyway."
    /// </summary>
    private async Task<string?> GetTradeConfirmationPromptTextAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tradeConfirmationPromptText)) return tradeConfirmationPromptText;
        var resolved = await OnFrameworkThreadAsync(() =>
        {
            try { return dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRow(TradeConfirmationAddonSheetRowId).Text.ExtractText(); }
            catch (Exception ex) { log.Warning(ex, "[{Module}] Could not resolve the localized Trade-confirmation prompt text.", ModuleId); return null; }
        }, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resolved)) tradeConfirmationPromptText = resolved;
        return tradeConfirmationPromptText;
    }

    /// <summary>Reads a SelectYesno addon's own prompt text — ports ffxivbingo4all's donor-proven
    /// <c>Dropbox.GetSpecificYesno</c> (<c>NodeList[15]</c>) verbatim. LIVE VERIFICATION REQUIRED for the node
    /// index remaining stable in the current game client.</summary>
    private static unsafe bool TryReadSelectYesnoPromptText(AtkUnitBase* addon, out string text)
    {
        text = string.Empty;
        if (addon->UldManager.NodeList is null || SelectYesnoPromptTextNodeIndex >= addon->UldManager.NodeListCount) return false;
        var node = addon->UldManager.NodeList[SelectYesnoPromptTextNodeIndex];
        var textNode = node is null ? null : node->GetAsAtkTextNode();
        if (textNode is null) return false;
        text = ECommons.GenericHelpers.Read(textNode->NodeText).TextValue;
        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Locates the Trade window's Ready/Confirm control via a fixed node index — ports ffxivbingo4all's
    /// donor-proven <c>Dropbox.Framework_Update</c> (<c>(AtkComponentButton*)(addon->UldManager.NodeList[3]
    /// .GetComponent())</c>) verbatim, replacing a rendered-text button search that live QA proved never matches
    /// anything on the real Trade addon (BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md). Returns false (never guesses) if
    /// the node is absent or is not shaped like a button component.</summary>
    private unsafe bool TryGetTradeReadyButton(out AtkComponentButton* button)
    {
        button = null;
        if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
        if (addon->UldManager.NodeList is null || TradeReadyButtonNodeIndex >= addon->UldManager.NodeListCount) return false;
        var node = addon->UldManager.NodeList[TradeReadyButtonNodeIndex];
        if (node is null) return false;
        var component = node->GetComponent();
        if (component is null) return false;
        var candidate = (AtkComponentButton*)component;
        if (candidate->AtkComponentBase.OwnerNode is null) return false;
        button = candidate;
        return true;
    }

    /// <summary>Readiness gate for <see cref="WaitUntilAsync"/> — the control must be present AND enabled (the
    /// donor's own <c>tradeButton->IsEnabled</c> gate) before this engine will ever attempt to activate it.</summary>
    private unsafe bool IsTradeReadyButtonAvailable() => TryGetTradeReadyButton(out var button) && button->IsEnabled;

    private unsafe bool TryActivateTradeReadyButton()
    {
        if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
        if (!TryGetTradeReadyButton(out var button) || !button->IsEnabled) return false;
        return ActivateButton(addon, button);
    }

    /// <summary>Opens the shared numeric-entry popup and commits the gil amount, then positively confirms the Trade
    /// window's own display actually shows it before returning success. Gil-entry hotfix #2
    /// (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md) — see <see cref="TryOpenGilInputPopup"/>/<see cref="TryCommitNumericInput"/>
    /// for exactly what changed and why (the donor's proven FireCallback-based mechanism, not a text-button search
    /// plus a raw component-field write). Never trusts either write merely because it returned without throwing —
    /// <see cref="TradeGilTextShows"/> is the mandatory positive read-back.</summary>
    private async Task<bool> TryEnterGilAsync(int amount, CancellationToken cancellationToken)
    {
        if (!await OnFrameworkThreadAsync(() => TryOpenGilInputPopup(), cancellationToken).ConfigureAwait(false))
        {
            SetStatus("Could not open the gil-entry popup on the trade window (LIVE VERIFICATION REQUIRED for the Trade addon's callback index).");
            return false;
        }

        if (!await WaitUntilAsync(() => IsAddonReady(NumericInputAddonName), StepTimeout, cancellationToken).ConfigureAwait(false))
        {
            SetStatus("The gil-entry popup did not open in time.");
            return false;
        }

        if (!await OnFrameworkThreadAsync(() => TryCommitNumericInput(amount), cancellationToken).ConfigureAwait(false))
        {
            SetStatus("Could not confirm the amount in the gil-entry popup.");
            return false;
        }

        // Positive read-back — never advance to Trade submission on an unverified write. Bounded, since the game's
        // own UI needs at least one framework tick to reconcile the popup's confirm callback into the Trade
        // window's own display.
        if (!await WaitUntilAsync(() => TradeGilTextShows(amount), StagedGilReconcileTimeout, cancellationToken).ConfigureAwait(false))
        {
            SetStatus($"Entered {amount:N0} gil, but the trade window never visibly staged that exact amount — stopping before Trade submission.");
            return false;
        }

        return true;
    }

    // -----------------------------------------------------------------------------------------------------------
    // Every method below touches raw addon/node pointers and is individually marked unsafe — none of them contain
    // an await, so none of the async methods above ever need to be unsafe themselves. Generic addon/button
    // discovery mirrors the technique VenueOS.Plugin.PartyFinder.PartyFinderAutomationService already uses
    // (donor-proven approach: search visible, enabled buttons by readable text rather than a hard-coded node id,
    // so a differently-laid-out client/version degrades to "button not found" instead of clicking the wrong thing).
    // -----------------------------------------------------------------------------------------------------------

    private unsafe bool IsAddonReady(string name) => TryGetReadyAddon(name, out _);

    /// <summary>Fires the Trade addon's own native callback to open the shared gil-entry popup — ports
    /// ffxivbingo4all's donor-proven <c>Dropbox.TradeTask.OpenGilInput</c> (<c>Callback.Fire(addon, true, 2,
    /// Callback.ZeroAtkValue)</c>) verbatim. This replaced a text-based search for a "Gil"/"Add Gil" button
    /// (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md): the real Trade window's gil control is not a labeled button reachable
    /// that way, which is very likely why gil entry always failed to visibly stage anything. <c>Callback.Fire</c>
    /// invokes <c>AtkUnitBase.FireCallback</c> directly — the same synthetic-event path the game's own UI scripting
    /// uses to report a click — rather than simulating a node click via <see cref="ActivateButton"/>.</summary>
    private unsafe bool TryOpenGilInputPopup()
    {
        if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
        try { Callback.Fire(addon, true, TradeOpenGilInputCallbackIndex, Callback.ZeroAtkValue); return true; }
        catch (Exception ex) { log.Warning(ex, "[{Module}] Firing the Trade addon's gil-input callback failed.", ModuleId); return false; }
    }

    /// <summary>Fires the numeric-entry popup's own native callback with the chosen amount — ports
    /// ffxivbingo4all's donor-proven <c>Dropbox.TradeTask.SetNumericInput</c> (<c>Callback.Fire(addon, true, num)</c>)
    /// verbatim. This replaced a raw <c>AtkComponentNumericInput.SetValue</c> write followed by simulating a click
    /// on the addon's own <c>OkButton</c> node (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md) — writing the component's value
    /// directly does not fire the popup's own "value confirmed" event, which is very likely what the caller (the
    /// Trade window) actually needs in order to update its own gil display; a single <c>FireCallback</c> with the
    /// amount as its sole value both sets and confirms it in the one step the game itself expects.</summary>
    private unsafe bool TryCommitNumericInput(int amount)
    {
        if (!TryGetReadyAddon(NumericInputAddonName, out var addon)) return false;
        try { Callback.Fire(addon, true, amount); return true; }
        catch (Exception ex) { log.Warning(ex, "[{Module}] Firing the numeric-input popup's confirm callback failed.", ModuleId); return false; }
    }

    /// <summary>Positive read-back for the amount just entered (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md §15/§16 — never
    /// trust a write merely because it did not throw). Scans every currently visible text node reachable from the
    /// Trade addon for one whose digits, after stripping thousands separators/whitespace, equal <paramref
    /// name="amount"/> exactly. Deliberately node-index-agnostic: the donor's own working reference
    /// (<c>Dropbox.Framework_Update</c>) reads a specific fixed node (<c>NodeList[6]</c>) for the OTHER party's
    /// offered gil (used there for auto-accepting an incoming trade) — there is no equivalent donor-proven index for
    /// MY OWN staged amount, and guessing one risks silently reading the wrong side. Searching all visible text for
    /// the exact expected number avoids depending on an unverified index while still being a specific, low-collision
    /// signal (the intended chunk is always a large, deliberately-chosen gil amount, not a generic small number).
    /// LIVE VERIFICATION REQUIRED: confirms this is not a general repository-wide technique, only that no better
    /// verified alternative exists for this exact case.</summary>
    private unsafe bool TradeGilTextShows(int amount)
    {
        if (!TryGetReadyAddon(TradeAddonName, out var addon)) return false;
        var expected = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var text in CollectVisibleText(addon))
        {
            var digitsOnly = new string(text.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length > 0 && digitsOnly == expected) return true;
        }
        return false;
    }

    private unsafe bool TryClickButtonByText(string addonName, IReadOnlyList<string> desiredTexts, IReadOnlyList<string> rejectTexts)
    {
        if (!TryGetReadyAddon(addonName, out var addon)) return false;
        var buttons = CollectButtons(addon);
        var chosen = buttons.Where(b => b.Enabled).OrderByDescending(b => Score(b.Text, desiredTexts, rejectTexts)).FirstOrDefault();
        if (chosen is null || Score(chosen.Text, desiredTexts, rejectTexts) <= 0) return false;
        return ActivateButton(addon, chosen.Button);
    }

    private static int Score(string text, IReadOnlyList<string> desiredTexts, IReadOnlyList<string> rejectTexts)
    {
        if (rejectTexts.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase))) return -1000;
        for (var i = 0; i < desiredTexts.Count; i++)
        {
            if (text.Equals(desiredTexts[i], StringComparison.OrdinalIgnoreCase)) return 500 - i;
            if (text.Contains(desiredTexts[i], StringComparison.OrdinalIgnoreCase)) return 400 - i;
        }
        return 0;
    }

    private unsafe bool TryGetReadyAddon(string name, out AtkUnitBase* addon)
    {
        addon = null;
        var stage = AtkStage.Instance();
        if (stage is null || stage->RaptureAtkUnitManager is null) return false;
        foreach (var index in new[] { 1, 0, 2 })
        {
            var candidate = stage->RaptureAtkUnitManager->GetAddonByName(name, index);
            if (candidate is not null && candidate->IsReady && candidate->IsVisible) { addon = candidate; return true; }
        }
        return false;
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (await OnFrameworkThreadAsync(condition, cancellationToken).ConfigureAwait(false)) return true;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return await OnFrameworkThreadAsync(condition, cancellationToken).ConfigureAwait(false);
    }

    private unsafe List<ButtonCandidate> CollectButtons(AtkUnitBase* addon)
    {
        var buttons = new List<ButtonCandidate>();
        var seenNodes = new HashSet<nint>();
        var seenComponents = new HashSet<nint>();
        if (addon->RootNode is not null) CollectButtonsFromNode(addon->RootNode, buttons, seenNodes, seenComponents);
        if (addon->UldManager.NodeList is not null)
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node is not null) CollectButtonsFromNode(node, buttons, seenNodes, seenComponents);
            }
        return buttons;
    }

    private unsafe void CollectButtonsFromNode(AtkResNode* node, List<ButtonCandidate> buttons, HashSet<nint> seenNodes, HashSet<nint> seenComponents)
    {
        while (node is not null)
        {
            var address = (nint)node;
            if (!seenNodes.Add(address)) { node = node->NextSiblingNode; continue; }
            if (node->IsVisible())
            {
                var button = node->GetAsAtkComponentButton();
                if (button is not null && button->OwnerNode is not null) buttons.Add(new ButtonCandidate(button, GetButtonText(button), button->IsEnabled));

                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode is not null && componentNode->Component is not null)
                {
                    var componentAddress = (nint)componentNode->Component;
                    if (seenComponents.Add(componentAddress) && componentNode->Component->UldManager.NodeList is not null)
                        for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
                        {
                            var child = componentNode->Component->UldManager.NodeList[i];
                            if (child is not null) CollectButtonsFromNode(child, buttons, seenNodes, seenComponents);
                        }
                }

                if (node->ChildNode is not null) CollectButtonsFromNode(node->ChildNode, buttons, seenNodes, seenComponents);
            }
            node = node->NextSiblingNode;
        }
    }

    /// <summary>Same traversal shape as <see cref="CollectButtons"/>/<see cref="CollectButtonsFromNode"/>, collecting
    /// every visible <c>AtkTextNode</c>'s text instead of buttons — used only by <see cref="TradeGilTextShows"/>'s
    /// read-back (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md). Kept as a separate traversal rather than folded into the
    /// existing button collector to avoid changing the already-working, previously live-verified button-search path
    /// used elsewhere in this class (Ready/Confirm, SelectYesno).</summary>
    private unsafe List<string> CollectVisibleText(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        var seenNodes = new HashSet<nint>();
        var seenComponents = new HashSet<nint>();
        if (addon->RootNode is not null) CollectTextFromNode(addon->RootNode, texts, seenNodes, seenComponents);
        if (addon->UldManager.NodeList is not null)
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node is not null) CollectTextFromNode(node, texts, seenNodes, seenComponents);
            }
        return texts;
    }

    private unsafe void CollectTextFromNode(AtkResNode* node, List<string> texts, HashSet<nint> seenNodes, HashSet<nint> seenComponents)
    {
        while (node is not null)
        {
            var address = (nint)node;
            if (!seenNodes.Add(address)) { node = node->NextSiblingNode; continue; }
            if (node->IsVisible())
            {
                var textNode = node->GetAsAtkTextNode();
                if (textNode is not null)
                {
                    var text = ECommons.GenericHelpers.Read(textNode->NodeText).TextValue;
                    if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                }

                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode is not null && componentNode->Component is not null)
                {
                    var componentAddress = (nint)componentNode->Component;
                    if (seenComponents.Add(componentAddress) && componentNode->Component->UldManager.NodeList is not null)
                        for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
                        {
                            var child = componentNode->Component->UldManager.NodeList[i];
                            if (child is not null) CollectTextFromNode(child, texts, seenNodes, seenComponents);
                        }
                }

                if (node->ChildNode is not null) CollectTextFromNode(node->ChildNode, texts, seenNodes, seenComponents);
            }
            node = node->NextSiblingNode;
        }
    }

    private static unsafe string GetButtonText(AtkComponentButton* button)
    {
        var textNode = button->ButtonTextNode;
        textNode = textNode is null ? button->GetTextNodeById(2) : textNode;
        textNode = textNode is null ? button->GetTextNodeById(3) : textNode;
        return textNode is null ? string.Empty : ECommons.GenericHelpers.Read(textNode->NodeText).TextValue.Trim();
    }

    private static unsafe bool ActivateButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button is null) return false;
        var targetNode = (AtkResNode*)button->AtkComponentBase.OwnerNode;
        if (targetNode is null || targetNode->AtkEventManager.Event is null) return false;
        var evt = (AtkEvent*)targetNode->AtkEventManager.Event;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, targetNode->AtkEventManager.Event);
        return true;
    }

    private unsafe bool ExecuteGameCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            var shellModule = RaptureShellModule.Instance();
            if (uiModule is null || shellModule is null) return false;
            var utf8 = Utf8String.FromString(command);
            if (utf8 is null) return false;
            try { shellModule->ExecuteCommandInner(utf8, uiModule); return true; }
            finally { utf8->Dtor(); IMemorySpace.Free(utf8); }
        }
        catch (Exception ex) { log.Warning(ex, "[{Module}] ExecuteGameCommand failed for {Command}", ModuleId, command); return false; }
    }

    private BingoTradeResult Fail(string reason) { SetStatus(reason); log.Warning("[{Module}] {Reason}", ModuleId, reason); return BingoTradeResult.Failed(reason); }
    private void SetStatus(string status) { if (!string.Equals(Status, status, StringComparison.Ordinal)) { Status = status; log.Information("[{Module}] {Status}", ModuleId, status); } }

    private sealed unsafe class ButtonCandidate(AtkComponentButton* button, string text, bool enabled)
    {
        public AtkComponentButton* Button { get; } = button;
        public string Text { get; } = text;
        public bool Enabled { get; } = enabled;
    }
}
