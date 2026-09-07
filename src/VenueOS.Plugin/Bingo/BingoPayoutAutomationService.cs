using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
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
    private readonly IPluginLog log;
    private readonly DiagnosticsService diagnostics;

    private readonly object signalLock = new();
    private string? observedSignal;
    private volatile bool abortRequested;
    private volatile bool expectingConfirmation;

    public BingoPayoutAutomationService(IClientState clientState, IChatGui chatGui, ITargetManager targetManager, IPluginLog log, DiagnosticsService diagnostics)
    {
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.targetManager = targetManager;
        this.log = log;
        this.diagnostics = diagnostics;
        chatGui.ChatMessage += OnChatMessage;
    }

    public string Status { get; private set; } = "Idle";
    public bool IsBusy { get; private set; }

    public void Dispose() => chatGui.ChatMessage -= OnChatMessage;

    /// <summary>Stops the in-flight attempt. Best-effort attempt to close/decline any open trade window — LIVE
    /// VERIFICATION REQUIRED: the donor's own "Cancel Pay" never did this at all (forensic audit §18/§25); this is
    /// a deliberate improvement attempt using the same generic reject-button search as everywhere else in this
    /// file, not an addon-specific "decline" API (none was found on <c>AddonTrade</c> — see its own doc comment).
    /// </summary>
    public void Abort()
    {
        abortRequested = true;
        SetStatus("Aborted");
        try { TryClickButtonByText(TradeAddonName, ["Cancel", "Decline"], []); }
        catch (Exception ex) { log.Warning(ex, "[{Module}] Best-effort trade-window close on Abort failed.", ModuleId); }
    }

    public async Task<BingoTradeResult> ExecutePayoutAttemptAsync(string targetNameAndWorld, int amount, CancellationToken cancellationToken)
    {
        if (IsBusy) return BingoTradeResult.Failed("An attempt is already in progress.");
        IsBusy = true;
        abortRequested = false;
        expectingConfirmation = false;
        lock (signalLock) observedSignal = null;

        try
        {
            if (!clientState.IsLoggedIn) return Fail("Not logged in.");

            SetStatus($"Verifying pinned target {targetNameAndWorld}...");
            if (!CurrentTargetMatches(targetNameAndWorld))
                return Fail("The currently targeted player does not match the pinned winner. Target them again before retrying — this engine deliberately never sends a target-less /trade.");

            SetStatus("Opening trade...");
            if (!ExecuteGameCommand("/trade")) return Fail("Could not send the /trade command.");
            if (!await WaitUntilAsync(() => IsAddonReady(TradeAddonName), TradeOpenTimeout, cancellationToken).ConfigureAwait(false))
                return Fail("Trade window did not open in time.");
            if (abortRequested) return BingoTradeResult.Canceled("Aborted before the trade partner could be verified.");

            SetStatus("Verifying trade partner matches the pinned target...");
            // LIVE VERIFICATION REQUIRED: AddonTrade (this FFXIVClientStructs version) carries no own partner-name
            // field — the safe-to-assume fallback used here is that /trade only ever opens a trade with the
            // currently-targeted player, so re-checking the CURRENT target still matches the pinned identity is the
            // best available proxy without a verified partner-name addon field. If the target changed underneath
            // us between opening the trade and this check, that is exactly the failure this guards against.
            if (!CurrentTargetMatches(targetNameAndWorld))
                return Fail($"Trade partner no longer matches the pinned winner ({targetNameAndWorld}). Aborting before entering gil.");

            if (abortRequested) return BingoTradeResult.Canceled("Aborted before gil was entered.");
            SetStatus($"Entering {amount:N0} gil...");
            if (!await TryEnterGilAsync(amount, cancellationToken).ConfigureAwait(false)) return Fail("Could not enter the gil amount into the trade.");

            if (abortRequested) return BingoTradeResult.Canceled("Aborted before the offer was confirmed.");
            SetStatus("Confirming offer...");
            expectingConfirmation = true;
            if (!TryClickButtonByText(TradeAddonName, ["Ready to Trade", "Confirm Trade", "Ready"], ["Cancel", "Decline"]))
                return Fail("Could not find a Ready/Confirm button on the trade window.");

            SetStatus("Waiting for the game's \"Trade complete.\"/\"Trade canceled.\" chat signal...");
            var deadline = DateTime.UtcNow + CompletionSignalTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (abortRequested || cancellationToken.IsCancellationRequested) return BingoTradeResult.Canceled("Aborted while awaiting the completion signal.");
                string? signal; lock (signalLock) signal = observedSignal;
                if (signal == TradeCompleteText) return BingoTradeResult.Confirmed(signal);
                if (signal == TradeCanceledText) return BingoTradeResult.Canceled(signal);
                TryConfirmIfExpected();
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            // Ambiguous — NEVER guessed as Confirmed on a bounded timeout (forensic audit §21 principle 1 & 4).
            return BingoTradeResult.Ambiguous("Timed out waiting for the primary chat completion signal; no \"Trade complete.\"/\"Trade canceled.\" message was observed.");
        }
        catch (OperationCanceledException) { return BingoTradeResult.Canceled("Canceled."); }
        catch (Exception ex)
        {
            log.Error(ex, "[{Module}] Payout attempt threw an unhandled error.", ModuleId);
            diagnostics.RecordFailure($"{ModuleId}: payout attempt threw ({ex.Message})");
            return BingoTradeResult.Ambiguous($"Unhandled engine error: {ex.Message}");
        }
        finally { IsBusy = false; expectingConfirmation = false; }
    }

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

    /// <summary>Never a blind "accept any SelectYesno" — only clicks Yes while THIS attempt is actively expecting a
    /// post-Ready confirmation (forensic audit §18's single riskiest donor behavior, deliberately not replicated).
    /// </summary>
    private void TryConfirmIfExpected()
    {
        if (!expectingConfirmation) return;
        if (TryClickButtonByText(SelectYesnoAddonName, ["Yes"], ["No"])) expectingConfirmation = false;
    }

    /// <summary>Opens the shared numeric-entry popup and sets the gil amount. LIVE VERIFICATION REQUIRED: exactly
    /// how the Trade window's own "add gil" control is triggered (there is no dedicated field for it on
    /// <c>AddonTrade</c> in this FFXIVClientStructs version — it is a plain <see cref="AtkUnitBase"/>-derived addon
    /// with no Bingo/trade-specific members beyond the base UI plumbing) has not been confirmed against a live
    /// client. This implementation searches for a button literally labeled "Gil"/"Add Gil" first; if neither is
    /// found, it fails safely (returns false, surfaced as a Failed outcome) rather than guessing a hard-coded node
    /// id.</summary>
    private async Task<bool> TryEnterGilAsync(int amount, CancellationToken cancellationToken)
    {
        if (!TryClickButtonByText(TradeAddonName, ["Gil", "Add Gil"], []))
        {
            SetStatus("Could not find a \"Gil\" control on the trade window (LIVE VERIFICATION REQUIRED for the exact node).");
            return false;
        }

        if (!await WaitUntilAsync(() => IsAddonReady(NumericInputAddonName), StepTimeout, cancellationToken).ConfigureAwait(false)) return false;
        return TrySetNumericInputAndConfirm(amount);
    }

    // -----------------------------------------------------------------------------------------------------------
    // Every method below touches raw addon/node pointers and is individually marked unsafe — none of them contain
    // an await, so none of the async methods above ever need to be unsafe themselves. Generic addon/button
    // discovery mirrors the technique VenueOS.Plugin.PartyFinder.PartyFinderAutomationService already uses
    // (donor-proven approach: search visible, enabled buttons by readable text rather than a hard-coded node id,
    // so a differently-laid-out client/version degrades to "button not found" instead of clicking the wrong thing).
    // -----------------------------------------------------------------------------------------------------------

    private unsafe bool IsAddonReady(string name) => TryGetReadyAddon(name, out _);

    private unsafe bool TrySetNumericInputAndConfirm(int amount)
    {
        if (!TryGetReadyAddon(NumericInputAddonName, out var addon)) return false;
        var numericAddon = (AddonInputNumeric*)addon;
        if (numericAddon->NumericInput is null) return false;
        numericAddon->NumericInput->SetValue(amount);
        if (numericAddon->OkButton is null) return false;
        return ActivateButton((AtkUnitBase*)numericAddon, numericAddon->OkButton);
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (condition()) return true;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return condition();
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
