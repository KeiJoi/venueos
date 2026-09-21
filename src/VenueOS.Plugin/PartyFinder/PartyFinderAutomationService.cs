using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Services;

namespace VenueOS.Plugin.PartyFinder;

/// <summary>The unsafe, game-version-sensitive Party Finder automation engine. A faithful port of the donor's
/// <c>PartyFinderAutomation</c> (venuepartyfinder, Services/PartyFinderAutomation.cs) — same addon names, same node
/// IDs/text-match fallbacks, same task-chain shape and timing constants, same compatibility safety gate — with the
/// donor's own static <c>Service</c>/<c>PluginConfiguration</c> replaced by VenueOS-owned dependencies (constructor
/// injected <see cref="IClientState"/>/<see cref="IPluginLog"/>/<see cref="DiagnosticsService"/>, and
/// <see cref="PartyFinderPreset"/> passed in per call instead of stored on a donor config object), and with the new
/// End Party Finder shutdown workflow added (VenueOS-specific — has no donor equivalent).
///
/// Lives in <c>VenueOS.Plugin</c> (not <c>VenueOS.Modules.Operations</c>) because it requires
/// unsafe FFXIVClientStructs addon access, ECommons, and Dalamud game services — exactly like the donor's own
/// unsafe/game-version-sensitive boundary, and matching how this repository's other unsafe/native code
/// (<c>DalamudVenueAddressProvider</c>'s HousingManager access) already lives in this project.</summary>
public sealed unsafe class PartyFinderAutomationService : IPartyFinderAutomation, IDisposable
{
    private static readonly string[] MainAddonNames = ["LookingForGroup"];
    private static readonly string[] DetailAddonNames = ["LookingForGroupDetail"];
    private static readonly string[] EditorAddonNames = ["LookingForGroupCondition", "LookingForGroupRecruit", "LookingForGroupSetting", "LookingForGroupInput"];
    private static readonly string[] CreateButtonTexts = ["Recruit Members", "Recruiting Members", "Create Listing", "Register"];
    private static readonly string[] EditButtonTexts = ["Edit Recruitment Details", "Edit Recruitment", "Update Details", "Edit"];
    private static readonly string[] SaveButtonTexts = ["Apply Changes", "Recruit Members", "Register", "Save", "Update"];
    private static readonly string[] RejectPrimaryTexts = ["Cancel", "Back", "Close", "Withdraw", "Leave", "No"];
    private static readonly string[] ConfirmYesTexts = ["Yes", "OK", "Confirm"];
    private static readonly string[] WorldRestrictionTexts = ["Limit recruiting to world server", "Limit recruiting to world", "World server"];
    private static readonly string[] PrivatePartyTexts = ["Form a private party", "Private party"];

    // VenueOS addition: End Party Finder's own deliberately scoped search — never shares ScoreButton's reject list,
    // which deliberately excludes exactly these words for Create/Edit/Refresh's safety. This list intentionally
    // contains no reject set of its own; it searches FOR ending recruitment, not against it. "End" is the confirmed
    // current in-game label (live-verified against the own-listing detail screen's Edit/End/Back button row); the
    // remaining entries are defensive fallbacks for a different game version/locale showing a different label.
    private static readonly string[] EndButtonTexts = ["End", "Withdraw Recruitment", "Withdraw", "End Recruitment", "Cancel Recruitment"];

    // The submit control on the Recruitment Criteria editor (LookingForGroupCondition). "Recruit Members" when creating,
    // "Apply Changes" when editing an existing listing — same native button, same id (live-verified in earlier passes).
    private const uint SubmitButtonId = 111;
    private static readonly string[] UpdateSubmitTexts = ["Apply Changes", "Update", "Save"];
    private static readonly string[] CreateSubmitTexts = ["Recruit Members", "Register", "Create Listing"];

    private const string ModuleId = "promotion.partyfinder";

    private static readonly TimeSpan MainAddonOpenTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MainAddonRetryDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SubmissionVerificationTimeout = TimeSpan.FromSeconds(5);

    // VenueOS reliability-hardening addition: a shared bounded grace window for a native UI control that should
    // already basically be available (the addon hosting it was already confirmed open a step or two earlier) but
    // whose child nodes/labels can still populate a frame — or, under load, several hundred milliseconds — late.
    // Used wherever a single-shot "if not found, fail" check has been replaced with "if not found yet, keep
    // checking, up to this bound." A fast client proceeds the instant the real condition is observed true; a slow
    // or heavily-loaded one (e.g. a second FFXIV client running on the same machine) gets up to this long before a
    // stage-specific timeout is reported. Reused across every newly-hardened wait below rather than one constant
    // per call site, since they all answer the same underlying question ("has a just-opened native control finished
    // populating yet?") at the same order of magnitude.
    private static readonly TimeSpan UiElementReadinessTimeout = TimeSpan.FromSeconds(5);

    // A short, soft grace window specifically for checkbox sync: if the editor genuinely never appears at all,
    // SubmitEditor (the very next real step, with its own full UiElementReadinessTimeout and a clear diagnostic) is
    // the authoritative failure point for that — this step only needs enough slack to catch the common "editor is
    // still finishing its open transition" case without doubling the user's wait before a real failure is reported.
    private static readonly TimeSpan CheckboxSyncSoftTimeout = TimeSpan.FromSeconds(2);

    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly DiagnosticsService diagnostics;

    private TaskManager taskManager;
    private int mainAddonOpenAttempts;
    private bool usedSlashCommandFallback;
    private bool observedActiveListing;
    private DateTime mainAddonOpenStartedUtc;
    private DateTime nextMainAddonAttemptUtc;
    private DateTime submissionVerificationStartedUtc;
    private BoundedWait endButtonWait;
    private BoundedWait detailNavigationWait;
    private BoundedWait mainActionWait;
    private BoundedWait submitEditorWait;
    private BoundedWait checkboxSyncWait;
    private BoundedWait editorReadyWait;
    private BoundedWait editButtonWait;
    private AutomationState state = AutomationState.Idle;

    // Post-0.3.7 first-refresh investigation (docs/PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md): single-owner
    // operation bookkeeping (identity, current step, milestone timeline, completed-update count) and the final-submit
    // decision logic both live in tested pure code in VenueOS.Modules.Operations; this engine only gathers native
    // observations and performs the native actions those decisions call for.
    private readonly PartyFinderOperationTracker operations = new();
    private PartyFinderApplyTracker apply = new(isUpdate: false);
    private int detailNavigationClicks;
    private string lastScanLogKey = string.Empty;

    private enum AutomationState { Idle, Running, Ending }

    public PartyFinderAutomationService(IClientState clientState, ICondition condition, IPluginLog log, DiagnosticsService diagnostics)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
        this.diagnostics = diagnostics;
        taskManager = CreateTaskManager();
    }

    private static TaskManager CreateTaskManager() => new(new TaskManagerConfiguration(showDebug: false, showError: true, abortOnError: true, abortOnTimeout: true, timeoutSilently: false, timeLimitMS: 15000, executeDefaultConfigurationEvents: true));

    public string Status { get; private set; } = "Idle";

    public bool IsCompatibilityVerified { get; private set; }

    // Both ownership flags reconcile against the TaskManager first: ECommons' TaskManager aborts its chain SILENTLY on a
    // per-task timeout or an exception inside a step, and neither path runs this class's own FailAndAbort/
    // StopGracefully. Before the 0.3.7 IsBusy guard that was benign (the next request simply aborted and restarted);
    // with the guard, a silently-dead chain would leave state == Running forever and every later manual/automatic
    // refresh would be ignored until the operator pressed Abort. Reading either flag detects that and releases
    // ownership with an attributable Diagnostics entry naming the step that died.
    public bool IsBusy
    {
        get
        {
            ReconcileLostChain();
            return state == AutomationState.Running;
        }
    }

    public bool IsEnding
    {
        get
        {
            ReconcileLostChain();
            return state == AutomationState.Ending;
        }
    }

    private void ReconcileLostChain()
    {
        if (state == AutomationState.Idle || taskManager.IsBusy)
        {
            return;
        }

        var step = operations.Current?.Step ?? "unknown";
        var message = $"Party Finder automation stopped unexpectedly during step '{step}': the task chain ended without finishing (a native step timed out or threw — see the Dalamud log). Ownership released; the next request will retry.";
        Status = message;
        log.Error("[{Module}] {Status}", ModuleId, message);
        diagnostics.RecordFailure($"{ModuleId}: {message}{DescribeCurrentOperation()}");
        EndOperation(PartyFinderOperationOutcome.ChainLost, message);
    }

    private void BeginOperation(PartyFinderOperationKind kind, string reason)
    {
        var superseded = operations.Current;
        var op = operations.Begin(kind, reason, DateTime.UtcNow);
        if (superseded is not null)
        {
            log.Information("[{Module}] op#{Id} superseded by op#{NewId}.", ModuleId, superseded.Id, op.Id);
        }

        state = kind == PartyFinderOperationKind.End ? AutomationState.Ending : AutomationState.Running;
    }

    /// <summary>Every path that releases ownership (success, failure, graceful stop, abort, lost chain) funnels through
    /// here, so there is exactly one place where <c>state</c> returns to Idle and one place that writes the operation's
    /// summary line — including the milestone timeline that shows how long each native transition took.</summary>
    private void EndOperation(PartyFinderOperationOutcome outcome, string detail)
    {
        state = AutomationState.Idle;
        var record = operations.Finish(outcome, detail, DateTime.UtcNow);
        if (record is null)
        {
            return;
        }

        log.Information("[{Module}] op#{Id} {Kind} ({Reason}) {Outcome} after {Elapsed}ms; firstUpdateForListing={First}; lastStep='{Step}'; timeline: {Timeline}",
            ModuleId, record.Id, record.Kind, record.Reason, record.Outcome, (long)record.Elapsed.TotalMilliseconds, record.IsFirstUpdateForListing, record.LastStep, record.Timeline);
    }

    private long CurrentOperationId => operations.Current?.Id ?? 0;

    private string DescribeCurrentOperation()
    {
        var op = operations.Current;
        return op is null
            ? string.Empty
            : $" [op #{op.Id} {op.Kind}{(op.IsFirstUpdateForListing ? ", first update for this listing" : string.Empty)}, step '{op.Step}', {(long)(DateTime.UtcNow - op.StartedUtc).TotalMilliseconds}ms in]";
    }

    private void EnqueueStep(string name, Func<bool> step) =>
        taskManager.Enqueue(() =>
        {
            operations.EnterStep(name, DateTime.UtcNow);
            return step();
        }, name);

    public bool HasOwnListing
    {
        get
        {
            var agent = AgentLookingForGroup.Instance();
            return (agent != null && agent->OwnListingId != 0) || observedActiveListing;
        }
    }

    public void Dispose() => taskManager.Dispose();

    /// <summary>Donor-parity: the previous VenueOS adapter recreated its whole <c>PartyFinderAutomation</c> instance
    /// on every venue switch. Reproduced here by disposing and recreating the <see cref="TaskManager"/> and resetting
    /// every per-operation runtime flag, rather than carrying any state across venues.</summary>
    public void ResetForVenue()
    {
        taskManager.Abort();
        taskManager.Dispose();
        taskManager = CreateTaskManager();
        mainAddonOpenAttempts = 0;
        usedSlashCommandFallback = false;
        observedActiveListing = false;
        mainAddonOpenStartedUtc = DateTime.MinValue;
        nextMainAddonAttemptUtc = DateTime.MinValue;
        submissionVerificationStartedUtc = DateTime.MinValue;
        endButtonWait.Reset();
        detailNavigationWait.Reset();
        mainActionWait.Reset();
        submitEditorWait.Reset();
        checkboxSyncWait.Reset();
        editorReadyWait.Reset();
        editButtonWait.Reset();
        detailNavigationClicks = 0;
        apply = new PartyFinderApplyTracker(isUpdate: false);
        IsCompatibilityVerified = false;
        EndOperation(PartyFinderOperationOutcome.Aborted, "venue reset");
        operations.ResetListingHistory();
        SetStatus("Idle");
    }

    public void Abort()
    {
        taskManager.Abort();
        EndOperation(PartyFinderOperationOutcome.Aborted, "operator abort");
        SetStatus("Aborted");
    }

    public void NotifyListingEnded()
    {
        observedActiveListing = false;
        operations.ResetListingHistory();
        SetStatus("Party Finder listing ended.");
    }

    public void QueueCreateOrUpdate(PartyFinderPreset preset, string reason)
    {
        ReconcileLostChain();
        if (state == AutomationState.Ending)
        {
            SetStatus("Party Finder is ending — try again once it finishes.");
            return;
        }

        QueueOperation(preset, requireExistingListing: HasOwnListing, reason);
    }

    public void QueueRefresh(PartyFinderPreset preset, string reason)
    {
        ReconcileLostChain();
        if (state == AutomationState.Ending)
        {
            SetStatus("Party Finder is ending — try again once it finishes.");
            return;
        }

        if (!HasOwnListing)
        {
            SetStatus("No active listing to refresh.");
            return;
        }

        QueueOperation(preset, requireExistingListing: true, reason);
    }

    private void QueueOperation(PartyFinderPreset preset, bool requireExistingListing, string reason)
    {
        taskManager.Abort();
        mainAddonOpenAttempts = 0;
        usedSlashCommandFallback = false;
        mainAddonOpenStartedUtc = DateTime.MinValue;
        nextMainAddonAttemptUtc = DateTime.MinValue;
        submissionVerificationStartedUtc = DateTime.MinValue;
        detailNavigationWait.Reset();
        mainActionWait.Reset();
        submitEditorWait.Reset();
        checkboxSyncWait.Reset();
        editorReadyWait.Reset();
        editButtonWait.Reset();
        detailNavigationClicks = 0;
        BeginOperation(requireExistingListing ? PartyFinderOperationKind.Update : PartyFinderOperationKind.Create, reason);
        apply = new PartyFinderApplyTracker(isUpdate: requireExistingListing);
        SetStatus($"Queued {(requireExistingListing ? "refresh" : "create/update")} from {reason}.");

        EnqueueStep("prepare", () => PrepareOperation(requireExistingListing));
        EnqueueStep("open_pf", EnsureMainAddonReady);
        EnqueueStep("apply_preset", () => ApplyPresetToAgent(preset));
        EnqueueStep("open_editor", () => ClickMainAction(requireExistingListing));

        // First-refresh fix (docs/PARTY_FINDER_FIRST_REFRESH_INVESTIGATION.md): every pacing delay below used to be
        // measured from the Edit/Create CLICK, on the unverified assumption that the Recruitment Criteria editor
        // exists a few hundred milliseconds later. A cold first open of that addon can take longer, in which case the
        // preset re-apply ran before the editor existed and Apply Changes was dispatched ~150ms after the editor first
        // appeared. This gate observes the editor actually being visible and ready first; the donor-parity pacing
        // that follows is now measured from that observed event.
        EnqueueStep("wait_editor_ready", WaitForEditorReady);
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        EnqueueStep("reapply_preset_in_editor", () => ApplyPresetToAgent(preset));
        taskManager.EnqueueDelay(200, false, taskManager.DefaultConfiguration);
        EnqueueStep("sync_editor_checkboxes", () => SyncEditorCheckboxes(preset));
        taskManager.EnqueueDelay(150, false, taskManager.DefaultConfiguration);
        EnqueueStep("submit_editor", () => SubmitEditor(requireExistingListing));
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        EnqueueStep("confirm_yesno", ConfirmYesNoIfNeeded);
        EnqueueStep("verify_submission", () => VerifySubmission(requireExistingListing));
        EnqueueStep("close_pf", ClosePartyFinderWindowIfVisible);
        EnqueueStep("finish", () =>
        {
            SetStatus("Automation sequence finished.");
            EndOperation(PartyFinderOperationOutcome.Completed, "sequence finished");
            return true;
        });
    }

    private bool PrepareOperation(bool requireExistingListing)
    {
        if (TryGetContextInvalidReason(out var invalidReason))
        {
            return StopGracefully($"Party Finder automation skipped — {invalidReason}. It will retry on the next request.");
        }

        if (requireExistingListing && !HasOwnListing)
        {
            return StopGracefully("Refresh requested, but no own listing is active.");
        }

        var op = operations.Current;
        if (op is not null)
        {
            // One line per operation (not per frame): which request this is, whether it is the first update this
            // listing has had, and the native addons' load state at the start. "Not loaded" for the detail/editor
            // addons means this operation will be their first open since the game last unloaded them.
            log.Information("[{Module}] op#{Id} {Kind} ({Reason}) starting; firstUpdateForListing={First}; updatesCompletedForListing={Updates}; addons: {Addons}",
                ModuleId, op.Id, op.Kind, op.Reason, op.IsFirstUpdateForListing, operations.UpdatesCompletedForListing, DescribeAddonStates());
        }

        SetStatus("Preparing Party Finder automation.");
        return true;
    }

    private string DescribeAddonStates()
    {
        var parts = new List<string>();
        foreach (var name in MainAddonNames.Concat(DetailAddonNames).Concat(EditorAddonNames))
        {
            parts.Add(TryGetAddonByName(name, out var addon)
                ? $"{name}[ready={addon->IsReady},visible={addon->IsVisible}]"
                : $"{name}[not loaded]");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Reliability hardening (production symptom: intermittent refresh failures): a character mid-zone-
    /// transition, mid-logout, or genuinely logged out cannot have its Party Finder addon meaningfully opened —
    /// retrying <see cref="EnsureMainAddonReady"/>'s normal addon-discovery loop against that state can only ever
    /// burn through the full <see cref="MainAddonOpenTimeout"/> and end in a confusing "Failed to detect a visible
    /// Party Finder window" diagnostic, even though the real cause (a loading screen that legitimately took longer
    /// than 8 seconds, or the player logging out) was never a compatibility/addon problem at all. Checked once at
    /// the start of every operation (<see cref="PrepareOperation"/>) and again on every tick of the longest-running
    /// wait (<see cref="EnsureMainAddonReady"/>, shared by Create/Edit/Refresh's "open_pf" step and End Party
    /// Finder's "end_open_pf" step) so an attempt that starts clean but the player then zones mid-wait stops
    /// promptly instead of waiting out the clock. Mirrors the same <c>ConditionFlag.BetweenAreas</c>/
    /// <c>BetweenAreas51</c>/<c>LoggingOut</c> signals <c>ShoutRunnerAutomationService</c> already uses for this
    /// exact purpose (NEW_MODULE_GUIDE.md §6's "use the current established framework-thread/game-state patterns"),
    /// not an invented signal.</summary>
    private bool TryGetContextInvalidReason(out string reason)
    {
        if (!clientState.IsLoggedIn)
        {
            reason = "not logged in";
            return true;
        }

        if (condition[ConditionFlag.LoggingOut])
        {
            reason = "logging out";
            return true;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            reason = "zoning";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool EnsureMainAddonReady()
    {
        if (TryGetContextInvalidReason(out var invalidReason))
        {
            return StopGracefully($"Party Finder automation stopped — {invalidReason}. It will retry on the next request.");
        }

        var now = DateTime.UtcNow;
        if (mainAddonOpenStartedUtc == DateTime.MinValue)
        {
            mainAddonOpenStartedUtc = now;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent != null && agent->IsAddonShown() && TryGetVisibleReadyAddon(MainAddonNames, out var addonName))
        {
            IsCompatibilityVerified = true;
            SetStatus($"{addonName} is visible and ready.");
            return true;
        }

        if (now - mainAddonOpenStartedUtc >= MainAddonOpenTimeout)
        {
            return FailAndAbort("Failed to detect a visible Party Finder window. Open Party Finder manually once, then retry.");
        }

        if (now < nextMainAddonAttemptUtc)
        {
            return false;
        }

        nextMainAddonAttemptUtc = now + MainAddonRetryDelay;

        if (agent == null)
        {
            mainAddonOpenAttempts++;
            SetStatus("Party Finder agent is not available yet.");
            return false;
        }

        mainAddonOpenAttempts++;
        log.Information("[{Module}] Requesting Party Finder window, attempt {Attempt}. shown={Shown} hidden={Hidden} ready={Ready}", ModuleId, mainAddonOpenAttempts, agent->IsAddonShown(), agent->IsAddonHidden(), agent->IsAddonReady());

        agent->ShowAddon();
        agent->FocusAddon();

        if (!usedSlashCommandFallback && mainAddonOpenAttempts >= 2)
        {
            usedSlashCommandFallback = true;
            if (ExecuteGameCommand("/partyfinder"))
            {
                SetStatus("Opening Party Finder via game command.");
                return false;
            }

            log.Warning("[{Module}] Game-command fallback failed for /partyfinder.", ModuleId);
        }

        SetStatus(usedSlashCommandFallback
            ? "Waiting for the Party Finder window to appear after /partyfinder."
            : "Opening Party Finder.");

        return false;
    }

    private bool TryGetVisibleReadyAddon(IReadOnlyList<string> names, out string foundName)
    {
        foreach (var name in names)
        {
            if (TryGetAddonByName(name, out var addon) && addon->IsReady && addon->IsVisible)
            {
                foundName = name;
                return true;
            }
        }

        foundName = string.Empty;
        return false;
    }

    private bool ApplyPresetToAgent(PartyFinderPreset preset)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            SetStatus("Could not access Party Finder agent.");
            return true;
        }

        ref var recruit = ref agent->StoredRecruitmentInfo;

        recruit.SelectedCategory = preset.Category.ToNative();
        recruit.SelectedDutyId = preset.DutyId;
        recruit.Objective = preset.Objective.ToNative();
        recruit.BeginnerFriendly = (byte)(preset.BeginnerFriendly ? 1 : 0);
        recruit.CompletionStatus = preset.CompletionStatus.ToNative();
        recruit.DutyFinderSettingFlags = preset.DutyFinderSettings.ToNative();
        recruit.LootRule = preset.LootRule.ToNative();
        recruit.Password = preset.PrivateParty ? PartyFinderPreset.ClampPassword(preset.Password) : ushort.MaxValue;
        recruit.LanguageFlags = preset.Languages.ToNative();
        recruit.NumberOfSlotsInMainParty = preset.NumberOfSlotsInMainParty;
        recruit.LimitRecruitingToWorld = (byte)(preset.LimitRecruitingToWorld ? 0 : 1);
        recruit.OnePlayerPerJob = (byte)(preset.OnePlayerPerJob ? 1 : 0);
        recruit.NumberOfGroups = preset.NumberOfGroups;
        agent->AvgItemLvEnabled = (byte)(preset.AverageItemLevelEnabled ? 1 : 0);
        agent->AvgItemLv = preset.AverageItemLevelEnabled ? PartyFinderPreset.ClampAverageItemLevel(preset.AverageItemLevel) : (ushort)0;

        for (var i = 0; i < 48; i++)
        {
            recruit.SlotFlags[i] = preset.GetSlot(i).ToNativeMask();
        }

        recruit.CommentString = PartyFinderPreset.TruncateCommentUtf8(preset.Comment);
        // The party password is deliberately not logged (only whether one is set).
        log.Information("[{Module}] Wrote recruitment fields: limitToWorldRaw={LimitToWorld} passwordSet={PasswordSet}", ModuleId, recruit.LimitRecruitingToWorld, recruit.Password != ushort.MaxValue);
        SetStatus("Applied preset to PF recruitment struct.");
        return true;
    }

    /// <summary>Shared navigation: reaches the own-active-listing detail screen (<c>LookingForGroupDetail</c>) —
    /// opening the main <c>LookingForGroup</c> addon's node-46 "Recruitment Criteria" button if the detail screen
    /// isn't visible yet — and invokes <paramref name="onReached"/> once it is, letting the caller decide what to
    /// click there. This is the exact route <see cref="ClickMainAction"/>'s <c>requireExistingListing</c> branch
    /// always used (extracted, not rewritten, so both Refresh/Edit and End Party Finder go through one proven path
    /// instead of two separately-maintained ones — see PARTY_FINDER_RECONSTRUCTION.md).</summary>
    // A pointer type can't be used as a Func<>/Action<> type argument (CS0306) — a plain unsafe delegate has no
    // such restriction.
    private unsafe delegate bool OwnListingDetailAction(AtkUnitBase* detailAddon);

    private bool NavigateToOwnListingDetail(OwnListingDetailAction onReached, string openFailureContext = "open active listing details")
    {
        if (TryGetAddonByName("LookingForGroupDetail", out var detailAddon) && detailAddon->IsReady && detailAddon->IsVisible)
        {
            if (detailNavigationClicks > 0)
            {
                // Evidence for the live log: how many times node 46 was activated before the detail screen appeared.
                // This route re-activates node 46 on every tick until the detail addon is visible (unchanged behavior);
                // a count above 1 on a cold first open would show the repeated-activation pattern directly.
                operations.Mark($"detail-visible(node46-clicks={detailNavigationClicks})", DateTime.UtcNow);
                log.Information("[{Module}] op#{Id} listing detail visible after {Clicks} node-46 activation(s).", ModuleId, CurrentOperationId, detailNavigationClicks);
                detailNavigationClicks = 0;
            }

            return onReached(detailAddon);
        }

        if (TryGetAddonByName("LookingForGroup", out var mainAddon) && mainAddon->IsReady && mainAddon->IsVisible)
        {
            if (TryClickButtonById(mainAddon, 46, out var detailsClicked))
            {
                detailNavigationClicks++;
                if (detailNavigationClicks == 1)
                {
                    operations.Mark("node46-clicked", DateTime.UtcNow);
                }

                SetStatus($"Opened active listing details: {detailsClicked}.");
                return false;
            }

            // Reliability hardening: the main addon reporting ready/visible doesn't guarantee its own Recruitment
            // Criteria button (node 46) is enabled/populated on this exact tick yet — retry within a bounded window
            // instead of failing on the first miss, the same tolerance Edit's own button search already has.
            if (detailNavigationWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
            {
                SetStatus("Waiting for the Recruitment Criteria button to become available.");
                return false;
            }

            return FailAndAbort($"Timed out waiting to {openFailureContext}: the Recruitment Criteria button did not become available. Visible buttons: {ListButtons(mainAddon)}");
        }

        if (detailNavigationWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
        {
            SetStatus("Waiting for Party Finder or active listing details window.");
            return false;
        }

        return FailAndAbort($"Timed out waiting to {openFailureContext}: neither the Party Finder window nor the active listing details window appeared.");
    }

    private bool ClickMainAction(bool requireExistingListing)
    {
        if (requireExistingListing)
        {
            return NavigateToOwnListingDetail(detailAddon =>
            {
                if (TryClickButton(detailAddon, EditButtonTexts, RejectPrimaryTexts, allowPrimaryFallback: false, out var editClicked))
                {
                    operations.Mark("edit-clicked", DateTime.UtcNow);
                    SetStatus($"Clicked active listing edit button: {editClicked}.");
                    return true;
                }

                // Previously unbounded here: with the detail screen visible but no enabled Edit button, this step
                // polled until ECommons' 15s per-task limit silently aborted the chain (no Diagnostics entry, and —
                // since 0.3.7's IsBusy guard — no way to start another refresh). Bounded like its peers (End's
                // ClickEndButton on the same screen) so a genuine miss is reported with the visible buttons.
                var firstMiss = !editButtonWait.HasStarted;
                if (editButtonWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
                {
                    SetStatus("Waiting for active listing Edit button to become available.");
                    if (firstMiss)
                    {
                        log.Information("[{Module}] Active listing detail buttons (Edit not yet found): {Buttons}", ModuleId, ListButtons(detailAddon));
                    }

                    return false;
                }

                return FailAndAbort($"Listing-detail screen became ready but the Edit button did not become available after {UiElementReadinessTimeout.TotalSeconds:0}s. Visible buttons: {ListButtons(detailAddon)}");
            });
        }

        if (!TryGetAddonByName("LookingForGroup", out var addon) || !addon->IsReady || !addon->IsVisible)
        {
            // Reliability hardening: EnsureMainAddonReady (the immediately preceding step) already confirmed this
            // same addon ready/visible — this re-check should normally pass instantly, but retry within a bounded
            // window instead of hard-failing on a single transient miss (e.g. a momentary UI state under load).
            if (mainActionWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
            {
                SetStatus("Waiting for the Party Finder window to become ready to recruit.");
                return false;
            }

            return FailAndAbort("Timed out waiting for the main Party Finder window to become ready to recruit.");
        }

        if (TryClickButtonById(addon, 46, out var clickedById))
        {
            operations.Mark("create-clicked", DateTime.UtcNow);
            SetStatus($"Clicked main PF action: {clickedById}.");
            return true;
        }

        var desired = requireExistingListing ? EditButtonTexts : CreateButtonTexts;
        if (TryClickButton(addon, desired, RejectPrimaryTexts, allowPrimaryFallback: !requireExistingListing, out var clicked))
        {
            operations.Mark("create-clicked", DateTime.UtcNow);
            SetStatus($"Clicked main PF action: {clicked}.");
            return true;
        }

        if (!requireExistingListing && TryClickButton(addon, EditButtonTexts, RejectPrimaryTexts, allowPrimaryFallback: false, out clicked))
        {
            operations.Mark("create-clicked", DateTime.UtcNow);
            SetStatus($"Clicked fallback PF action: {clicked}.");
            return true;
        }

        // Reliability hardening: the addon reporting ready/visible doesn't guarantee its Recruit Members button is
        // enabled/populated on this exact tick — retry within a bounded window before treating it as a real failure.
        if (mainActionWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
        {
            SetStatus("Waiting for the Recruit Members button to become available.");
            return false;
        }

        return FailAndAbort($"Timed out waiting for the main PF action button to become available. Visible buttons: {ListButtons(addon)}");
    }

    /// <summary>Gate between "the Edit/Create click was dispatched" and everything that touches the editor: the
    /// Recruitment Criteria editor must actually be observed visible and ready (bounded by
    /// <see cref="UiElementReadinessTimeout"/>, with a context check so a zone change stops it quietly). Identical
    /// predicate and failure text to what <see cref="SubmitEditor"/> previously applied at the very end of the chain —
    /// this only moves the observation to where the pacing that depends on it starts.</summary>
    private bool WaitForEditorReady()
    {
        if (TryGetContextInvalidReason(out var invalidReason))
        {
            return StopGracefully($"Party Finder automation stopped — {invalidReason}. It will retry on the next request.");
        }

        if (TryFindVisibleEditorAddon(out var addonName, out _))
        {
            operations.Mark($"editor-visible({addonName})", DateTime.UtcNow);
            SetStatus($"Recruitment editor {addonName} is visible and ready.");
            return true;
        }

        if (editorReadyWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
        {
            SetStatus("Waiting for the Party Finder editor to appear.");
            return false;
        }

        return FailAndAbort($"Timed out waiting for the Party Finder editor to appear after opening the recruitment screen. Addons: {DescribeAddonStates()}");
    }

    /// <summary>Final-submit stage 1 of 2: wait for an authoritative "submit control ready" observation, then dispatch
    /// exactly once. Readiness is decided by <see cref="PartyFinderApplyTracker"/> (pure, unit-tested) from what
    /// <see cref="ObserveSubmitControl"/> reads natively; every distinct not-ready state (no editor / no button /
    /// hidden / disabled / no click event / still the create-mode action) is named in the log on first observation and
    /// in the failure report if it never clears. Stage 2 (confirming the game accepted it) is
    /// <see cref="VerifySubmission"/>.</summary>
    private bool SubmitEditor(bool requireExistingListing)
    {
        if (!IsCompatibilityVerified)
        {
            return FailAndAbort("Party Finder layout has not been verified; refusing to click an unknown addon.");
        }

        var observation = ObserveSubmitControl(requireExistingListing, out var addon, out var addonName, out var button, out var mechanism);
        var decision = apply.EvaluateReadiness(DateTime.UtcNow, observation);
        if (decision.ReadinessChanged)
        {
            log.Information("[{Module}] op#{Id} submit control on {Addon}: {Readiness} ({Detail}); label='{Label}' via {Mechanism}.",
                ModuleId, CurrentOperationId, string.IsNullOrEmpty(addonName) ? "<none>" : addonName, decision.Readiness, PartyFinderApplyTracker.Describe(decision.Readiness), observation.ButtonLabel, mechanism);
        }

        switch (decision.Action)
        {
            case PartyFinderApplyAction.Dispatch:
                operations.Mark("submit-ready", DateTime.UtcNow);
                if (button == null || !DispatchApply(addon, button, addonName, mechanism, observation.ButtonLabel, recovery: false))
                {
                    return FailAndAbort($"Found the submit control on {addonName} but could not activate it.");
                }

                SetStatus($"Clicked submit button '{DisplayLabel(observation.ButtonLabel)}' on {addonName}.");
                return true;

            case PartyFinderApplyAction.Wait:
                SetStatus(decision.Readiness == PartyFinderSubmitReadiness.EditorMissing
                    ? "Waiting for the Party Finder editor to appear."
                    : $"Waiting for the submit button on {addonName}: {decision.Reason}.");
                return false;

            default:
                return decision.Readiness == PartyFinderSubmitReadiness.EditorMissing
                    ? FailAndAbort($"Timed out waiting for the Party Finder editor to appear after opening the recruitment screen. Addons: {DescribeAddonStates()}")
                    : FailAndAbort($"Timed out waiting for a usable submit button on {addonName}: {decision.Reason}. Visible buttons: {ListButtons(addon)}");
        }
    }

    private static string DisplayLabel(string label) => string.IsNullOrWhiteSpace(label) ? "<unlabelled>" : label;

    /// <summary>Reads the editor's submit control. Selection is exactly what the previous <c>SubmitEditor</c> did — the
    /// by-id control (<see cref="SubmitButtonId"/>, Condition editor only) when enabled and visible, else the best
    /// enabled text match — so which control gets activated is unchanged; what is new is that a control which is
    /// present but not usable is now reported as such (disabled / hidden / no click event / wrong-mode label) instead
    /// of being indistinguishable from "not found".</summary>
    private PartyFinderSubmitObservation ObserveSubmitControl(bool isUpdate, out AtkUnitBase* addon, out string addonName, out AtkComponentButton* button, out string mechanism)
    {
        button = null;
        mechanism = "none";
        if (!TryFindVisibleEditorAddon(out addonName, out addon))
        {
            addonName = string.Empty;
            return new(EditorVisible: false, ButtonFound: false, ButtonVisible: false, ButtonEnabled: false, ButtonActivatable: false, ButtonLabel: string.Empty);
        }

        IReadOnlyList<string> desiredTexts = isUpdate ? UpdateSubmitTexts : CreateSubmitTexts;

        AtkComponentButton* idButton = null;
        if (string.Equals(addonName, "LookingForGroupCondition", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = addon->GetComponentButtonById(SubmitButtonId);
            if (candidate != null && candidate->AtkResNode != null)
            {
                idButton = candidate;
            }
        }

        if (idButton != null && idButton->IsEnabled && idButton->AtkResNode->IsVisible())
        {
            button = idButton;
            mechanism = $"id:{SubmitButtonId}";
            return ObservationOf(button, enabled: true, visible: true);
        }

        var candidates = CollectButtons(addon, quiet: true);
        var enabled = candidates
            .Where(b => b.Enabled)
            .OrderByDescending(b => ScoreButton(b.Text, desiredTexts, RejectPrimaryTexts, false))
            .ThenByDescending(b => b.ScreenY)
            .ThenByDescending(b => b.ScreenX)
            .FirstOrDefault();
        if (enabled is not null && ScoreButton(enabled.Text, desiredTexts, RejectPrimaryTexts, false) > 0)
        {
            button = enabled.Button;
            mechanism = "text";
            return ObservationOf(button, enabled: true, visible: true);
        }

        // Nothing usable. Report the most informative unusable control so the failure names its real state.
        if (idButton != null)
        {
            mechanism = $"id:{SubmitButtonId}";
            return ObservationOf(idButton, idButton->IsEnabled, idButton->AtkResNode->IsVisible());
        }

        var present = candidates
            .OrderByDescending(b => ScoreButton(b.Text, desiredTexts, RejectPrimaryTexts, false))
            .FirstOrDefault();
        if (present is not null && ScoreButton(present.Text, desiredTexts, RejectPrimaryTexts, false) > 0)
        {
            mechanism = "text";
            return ObservationOf(present.Button, present.Enabled, visible: true);
        }

        return new(EditorVisible: true, ButtonFound: false, ButtonVisible: false, ButtonEnabled: false, ButtonActivatable: false, ButtonLabel: string.Empty);
    }

    private PartyFinderSubmitObservation ObservationOf(AtkComponentButton* button, bool enabled, bool visible)
    {
        var ownerNode = (AtkResNode*)button->AtkComponentBase.OwnerNode;
        var activatable = ownerNode != null && ownerNode->AtkEventManager.Event != null;
        return new(EditorVisible: true, ButtonFound: true, ButtonVisible: visible, ButtonEnabled: enabled, ButtonActivatable: activatable, ButtonLabel: GetButtonText(button));
    }

    /// <summary>Activates the submit control and records exactly what was sent, so a live failure can say "dispatch
    /// #N was sent to X via Y with event type/param Z" rather than leave dispatch-versus-never-sent to inference.
    /// Mechanism (unchanged): replay of the control's own registered native click event through
    /// <c>AtkUnitBase.ReceiveEvent</c> — see <see cref="ActivateButton(AtkUnitBase*, AtkComponentButton*)"/>. A true
    /// return means "dispatched", never "accepted"; acceptance is <see cref="VerifySubmission"/>'s job.</summary>
    private bool DispatchApply(AtkUnitBase* addon, AtkComponentButton* button, string addonName, string mechanism, string label, bool recovery)
    {
        var ownerNode = (AtkResNode*)button->AtkComponentBase.OwnerNode;
        var evt = ownerNode != null ? (AtkEvent*)ownerNode->AtkEventManager.Event : null;
        var eventDescription = evt != null ? $"type={evt->State.EventType}, param={evt->Param}" : "no event";

        if (!ActivateButton(addon, button))
        {
            log.Warning("[{Module}] op#{Id} submit dispatch on {Addon} FAILED: control has no activatable native event.", ModuleId, CurrentOperationId, addonName);
            return false;
        }

        if (apply.IsUpdate && PartyFinderApplyTracker.IsCreateModeLabel(label))
        {
            // Diagnostic only — never blocks (see PartyFinderApplyTracker.Classify).
            log.Warning("[{Module}] op#{Id} updating an existing listing but the submit control is labelled '{Label}' (a create-mode label).", ModuleId, CurrentOperationId, label);
        }

        var now = DateTime.UtcNow;
        apply.RecordDispatch(now);
        operations.Mark(recovery ? $"apply-redispatch#{apply.DispatchCount}" : $"apply-dispatch#{apply.DispatchCount}", now);
        log.Information("[{Module}] op#{Id} submit dispatch #{Count}{Recovery} on {Addon} via {Mechanism}: label='{Label}', ReceiveEvent({Event}).",
            ModuleId, CurrentOperationId, apply.DispatchCount, recovery ? " (recovery after observed non-transition)" : string.Empty, addonName, mechanism, DisplayLabel(label), eventDescription);
        return true;
    }

    private bool ConfirmYesNoIfNeeded()
    {
        TryHandleConfirmPopup();
        return true;
    }

    /// <summary>Clicks any native confirmation popup (SelectYesno / SelectOk) that is up. Returns whether one was
    /// present — the final-submit confirmation uses that so a pending popup is never mistaken for an ignored
    /// Apply Changes.</summary>
    private bool TryHandleConfirmPopup()
    {
        if (!TryGetAddonByName("SelectYesno", out var addon) || !ECommons.GenericHelpers.IsAddonReady(addon))
        {
            if (TryGetAddonByName("SelectOk", out addon) && ECommons.GenericHelpers.IsAddonReady(addon))
            {
                if (TryClickButton(addon, ["OK", "Ok", "Confirm", "Close"], RejectPrimaryTexts, allowPrimaryFallback: true, out var okClicked))
                {
                    SetStatus($"Confirmed popup with '{okClicked}'.");
                }

                return true;
            }

            return false;
        }

        if (TryClickButton(addon, ConfirmYesTexts, ["No"], allowPrimaryFallback: true, out var clicked))
        {
            SetStatus($"Confirmed popup with '{clicked}'.");
        }

        return true;
    }

    /// <summary>Reliability hardening: the previous single-shot check (<c>TryGetAddonByName("LookingForGroupCondition")</c>
    /// failing) could not distinguish "a different, checkbox-less editor screen is legitimately open" from "the
    /// right screen just hasn't finished opening yet" — it treated both as a silent no-op, which meant a slow client
    /// could skip syncing these two checkboxes entirely with no error at all. Now uses the same
    /// <see cref="TryFindVisibleEditorAddon"/> lookup <see cref="SubmitEditor"/> uses to tell the two apart: a
    /// different editor addon (Recruit/Setting/Input) is a real, immediate no-op (that screen has no such
    /// checkboxes); no editor addon being visible yet is retried for a short, soft grace window and then skipped —
    /// SubmitEditor (the very next real step, with its own full-length timeout) owns the authoritative failure if
    /// the editor never appears at all, so this step never doubles that wait.</summary>
    private bool SyncEditorCheckboxes(PartyFinderPreset preset)
    {
        if (TryFindVisibleEditorAddon(out var addonName, out var addon))
        {
            if (!string.Equals(addonName, "LookingForGroupCondition", StringComparison.OrdinalIgnoreCase))
            {
                return true; // this editor screen has no world-limit/private-party checkboxes to sync
            }

            var changed = false;
            changed |= TryEnsureCheckboxState(addon, WorldRestrictionTexts, preset.LimitRecruitingToWorld, out var worldMessage);
            changed |= TryEnsureCheckboxState(addon, PrivatePartyTexts, preset.PrivateParty, out var privateMessage);

            if (changed)
            {
                SetStatus(string.Join(" ", new[] { worldMessage, privateMessage }.Where(x => !string.IsNullOrWhiteSpace(x))));
            }

            return true;
        }

        if (checkboxSyncWait.Poll(DateTime.UtcNow, CheckboxSyncSoftTimeout))
        {
            SetStatus("Waiting for the recruitment editor to appear before syncing checkboxes.");
            return false;
        }

        SetStatus("Recruitment editor did not appear in time to sync checkboxes; continuing.");
        return true;
    }

    /// <summary>Final-submit stage 2 of 2: confirm the game actually accepted the dispatch. The authoritative
    /// transition is the editor closing (created listing observed, for Create). Success is never inferred from
    /// "a click was sent" or from elapsed time. If the editor is still open after the acceptance grace, the control is
    /// still enabled and no native popup is pending, <see cref="PartyFinderApplyTracker"/> authorises exactly one
    /// bounded re-dispatch (updates only — see its class doc for why that is safe); otherwise the failure names the
    /// exact observed state and how many dispatches were sent.</summary>
    private bool VerifySubmission(bool requireExistingListing)
    {
        if (!requireExistingListing && HasOwnListing)
        {
            observedActiveListing = true;
            operations.Mark("listing-observed", DateTime.UtcNow);
            SetStatus("Party Finder listing is active.");
            return true;
        }

        var popupPending = TryHandleConfirmPopup();
        var observation = ObserveSubmitControl(requireExistingListing, out var addon, out var addonName, out var button, out var mechanism);
        var decision = apply.EvaluateConfirmation(DateTime.UtcNow, new PartyFinderConfirmationObservation(observation.EditorVisible, observation.ButtonFound && observation.ButtonEnabled && observation.ButtonActivatable, popupPending));

        switch (decision.Action)
        {
            case PartyFinderApplyAction.Confirmed:
                observedActiveListing = true;
                operations.Mark("editor-closed", DateTime.UtcNow);
                log.Information("[{Module}] op#{Id} submit confirmed: editor closed after {Count} dispatch(es).", ModuleId, CurrentOperationId, apply.DispatchCount);
                SetStatus(requireExistingListing ? "Party Finder refresh submitted." : "Party Finder listing is active.");
                return true;

            case PartyFinderApplyAction.Redispatch:
                log.Warning("[{Module}] op#{Id} {Reason}; re-dispatching (dispatch {Next} of at most {Max}).", ModuleId, CurrentOperationId, decision.Reason, apply.DispatchCount + 1, PartyFinderApplyTimings.Default.MaxDispatches);
                if (button == null || !DispatchApply(addon, button, addonName, mechanism, observation.ButtonLabel, recovery: true))
                {
                    return FailAndAbort($"Apply Changes was not accepted and could not be re-dispatched ({decision.Reason}).");
                }

                SetStatus("Apply Changes had no effect yet; re-sent once.");
                return false;

            case PartyFinderApplyAction.Fail:
                return requireExistingListing
                    ? FailAndAbort($"Submitted Party Finder refresh, but the editor did not close. {decision.Reason}. Dispatches sent: {apply.DispatchCount}.")
                    : FailAndAbortCreate($"Submitted Party Finder, but no active listing was detected. {decision.Reason}. Dispatches sent: {apply.DispatchCount}.");

            default:
                SetStatus("Waiting for Party Finder to finish submitting.");
                return false;
        }
    }

    private bool ClosePartyFinderWindowIfVisible()
    {
        if (!TryAnyPartyFinderAddonVisible())
        {
            return true;
        }

        if (ExecuteGameCommand("/partyfinder"))
        {
            SetStatus("Closed Party Finder window.");
            return true;
        }

        return FailAndAbort("Failed to close Party Finder window.");
    }

    private bool TryAnyPartyFinderAddonVisible()
    {
        if (TryGetVisibleReadyAddon(MainAddonNames, out _))
        {
            return true;
        }

        if (TryGetVisibleReadyAddon(DetailAddonNames, out _))
        {
            return true;
        }

        return TryFindVisibleEditorAddon(out _, out _);
    }

    private bool TryFindVisibleEditorAddon(out string addonName, out AtkUnitBase* addon)
    {
        foreach (var name in EditorAddonNames)
        {
            if (TryGetAddonByName(name, out addon) && addon->IsReady && addon->IsVisible)
            {
                addonName = name;
                return true;
            }
        }

        addonName = string.Empty;
        addon = null;
        return false;
    }

    // -----------------------------------------------------------------------------------------------------------
    // End Party Finder — VenueOS addition, no donor equivalent. Reuses the exact same proven navigation as normal
    // Refresh/Edit (EnsureMainAddonReady, then NavigateToOwnListingDetail's node-46 route to LookingForGroupDetail)
    // and only diverges once that screen is reached — searching for "End" with its OWN deliberately scoped lookup
    // (EndButtonTexts) rather than any change to ScoreButton's normal reject list, which must keep excluding
    // "End"/"Withdraw"/"Leave" for Create/Edit/Refresh exactly as the donor does.
    //
    // A previous version of this method reset only mainAddonOpenStartedUtc/nextMainAddonAttemptUtc/
    // submissionVerificationStartedUtc before starting, but not mainAddonOpenAttempts/usedSlashCommandFallback —
    // unlike QueueOperation, which resets all five. If a prior Create/Edit/Refresh chain had already consumed the
    // one-time "/partyfinder" slash-command fallback (needed whenever ShowAddon/FocusAddon alone doesn't reopen an
    // already-closed Party Finder window — the normal case once a listing exists), End's own EnsureMainAddonReady
    // call would see usedSlashCommandFallback already true, never send /partyfinder, and time out after 8 seconds
    // with "Failed to detect a visible Party Finder window" even though the window was genuinely just closed and
    // reachable. Fixed by resetting the same five fields QueueOperation does.
    //
    // A second, distinct bug caused the live-reported "Cannot Find End button" failure on the FIRST attempt whenever
    // VenueOS had to navigate to the listing-detail screen itself (as opposed to it already being open): once
    // NavigateToOwnListingDetail's target addon (LookingForGroupDetail) first reports IsReady/IsVisible, that only
    // means the addon instance exists and is on-screen — its button/label nodes can still be unpopulated for a
    // frame or more after that. Refresh/Edit's own onReached callback already tolerated this by returning false
    // (not found *yet*, retry me) when its button search came up empty, so ClickMainAction naturally re-scans on
    // the next tick until the label is actually populated. ClickEndButton instead called FailAndAbort — a hard,
    // immediate, non-retrying failure — the instant its first single-shot scan (taken on the very frame the detail
    // screen became ready) didn't find "End" yet. Manually pre-opening the detail screen before clicking End Party
    // Finder masked this, since by then the screen had already been populated for many frames. Fixed by giving
    // ClickEndButton the same retry-until-found behavior as Edit, bounded by an explicit timeout
    // (UiElementReadinessTimeout) so a genuinely missing/relabeled button still fails safely instead of hanging.
    // -----------------------------------------------------------------------------------------------------------

    public void EndPartyFinder(string reason)
    {
        ReconcileLostChain();
        if (state == AutomationState.Ending)
        {
            SetStatus("Party Finder is already ending.");
            return;
        }

        // Step 2 of the End sequence: abort any in-flight Create/Edit/Refresh chain first, so a queued automatic
        // refresh can never resume after this begins (Auto Refresh itself is already disabled by the caller,
        // PartyFinderService.EndPartyFinder, before this method is ever invoked).
        taskManager.Abort();
        mainAddonOpenAttempts = 0;
        usedSlashCommandFallback = false;
        mainAddonOpenStartedUtc = DateTime.MinValue;
        nextMainAddonAttemptUtc = DateTime.MinValue;
        submissionVerificationStartedUtc = DateTime.MinValue;
        endButtonWait.Reset();
        detailNavigationWait.Reset();
        detailNavigationClicks = 0;
        BeginOperation(PartyFinderOperationKind.End, reason);
        SetStatus($"Ending Party Finder ({reason})...");

        // Step 4: no own listing at all — treat as already ended, no false failure.
        if (!HasOwnListing)
        {
            observedActiveListing = false;
            SetStatus("Party Finder ended — Auto Refresh disabled");
            EndOperation(PartyFinderOperationOutcome.Completed, "no own listing to end");
            return;
        }

        // end_open_pf / end_open_details: identical to Refresh's "open_pf" + the requireExistingListing branch of
        // ClickMainAction — the same EnsureMainAddonReady and the same NavigateToOwnListingDetail helper, so any
        // future fix to that proven route benefits both flows and can never drift apart again.
        EnqueueStep("end_open_pf", EnsureMainAddonReady);
        EnqueueStep("end_open_details", () => NavigateToOwnListingDetail(ClickEndButton, "reach the active listing screen to end recruitment"));
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        EnqueueStep("end_confirm_yesno", ConfirmYesNoIfNeeded);
        EnqueueStep("end_verify", VerifyEnded);
        EnqueueStep("end_close_pf", ClosePartyFinderWindowIfVisible);
        EnqueueStep("end_finish", () =>
        {
            SetStatus("Party Finder ended — Auto Refresh disabled");
            EndOperation(PartyFinderOperationOutcome.Completed, "listing withdrawn");
            return true;
        });
    }

    /// <summary>Step 6/7: End Party Finder's own scoped button search on the reached <c>LookingForGroupDetail</c>
    /// screen — deliberately does not reuse <see cref="ScoreButton"/>'s reject list (which excludes "End"/
    /// "Withdraw"/"Leave" for normal automation's safety) and never runs against any other/unverified addon.
    ///
    /// The detail addon reporting IsReady/IsVisible only means the screen exists and is on-screen — its button/label
    /// nodes can still be unpopulated for a frame or more afterward. Mirrors how Edit's own search (inside
    /// <see cref="ClickMainAction"/>) already tolerates this: not finding the button yet is "keep polling" (return
    /// false), not a hard failure, bounded by <see cref="UiElementReadinessTimeout"/> so a genuinely
    /// missing/relabeled button still fails safely instead of hanging.</summary>
    private bool ClickEndButton(AtkUnitBase* detailAddon)
    {
        if (!IsCompatibilityVerified)
        {
            return FailAndAbort("Party Finder layout has not been verified; refusing to click an unknown addon to end recruitment.");
        }

        var buttons = CollectButtons(detailAddon);
        var chosen = buttons
            .Where(b => b.Enabled)
            .OrderByDescending(b => ScoreText(b.Text, EndButtonTexts))
            .ThenByDescending(b => b.ScreenY)
            .ThenByDescending(b => b.ScreenX)
            .FirstOrDefault();

        if (chosen is null || ScoreText(chosen.Text, EndButtonTexts) <= 0)
        {
            if (endButtonWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
            {
                SetStatus("Waiting for the End button to become available on the active listing screen.");
                log.Information("[{Module}] Active listing detail buttons (End not yet found): {Buttons}", ModuleId, ListButtons(detailAddon));
                return false;
            }

            return FailAndAbort($"Listing-detail screen became ready but the End button did not appear after {UiElementReadinessTimeout.TotalSeconds:0}s. Visible buttons: {ListButtons(detailAddon)}");
        }

        var label = string.IsNullOrWhiteSpace(chosen.Text) ? $"Node {chosen.NodeId}" : chosen.Text;
        if (!ActivateButton(detailAddon, chosen))
        {
            return FailAndAbort($"Found '{label}' but could not activate it to end recruitment.");
        }

        SetStatus($"Clicked '{label}' to end recruitment.");
        return true;
    }

    /// <summary>Step 9: never consider ending successful merely because a button was clicked — poll the
    /// authoritative native listing state with the same bounded timeout normal automation uses for submission
    /// verification, allowing the native UI to finish its own transition first (step 8/9 of the required order:
    /// confirm any popup, then wait, never close the window before this confirms).</summary>
    private bool VerifyEnded()
    {
        if (submissionVerificationStartedUtc == DateTime.MinValue)
        {
            submissionVerificationStartedUtc = DateTime.UtcNow;
        }

        ConfirmYesNoIfNeeded();

        var agent = AgentLookingForGroup.Instance();
        if (agent == null || agent->OwnListingId == 0)
        {
            observedActiveListing = false;
            SetStatus("Recruitment withdrawn.");
            return true;
        }

        if (DateTime.UtcNow - submissionVerificationStartedUtc < SubmissionVerificationTimeout)
        {
            SetStatus("Waiting to confirm recruitment ended.");
            return false;
        }

        return FailAndAbort("Recruitment-end verification timed out. Auto Refresh remains disabled.");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Shared addon/button/checkbox discovery — ported verbatim from the donor.
    // -----------------------------------------------------------------------------------------------------------

    private bool TryClickButton(AtkUnitBase* addon, IReadOnlyList<string> desiredTexts, IReadOnlyList<string> rejectTexts, bool allowPrimaryFallback, out string clickedLabel)
    {
        if (!IsCompatibilityVerified)
        {
            clickedLabel = string.Empty;
            return FailAndAbort("Party Finder layout has not been verified; refusing to click an unknown addon.");
        }

        var buttons = CollectButtons(addon);

        var chosen = buttons
            .Where(b => b.Enabled)
            .OrderByDescending(b => ScoreButton(b.Text, desiredTexts, rejectTexts, allowPrimaryFallback))
            .ThenByDescending(b => b.ScreenY)
            .ThenByDescending(b => b.ScreenX)
            .FirstOrDefault();

        if (chosen is null || ScoreButton(chosen.Text, desiredTexts, rejectTexts, allowPrimaryFallback) <= 0)
        {
            clickedLabel = string.Empty;
            return false;
        }

        clickedLabel = string.IsNullOrWhiteSpace(chosen.Text) ? $"Node {chosen.NodeId}" : chosen.Text;
        return ActivateButton(addon, chosen);
    }

    private bool TryClickButtonById(AtkUnitBase* addon, uint buttonId, out string clickedLabel)
    {
        var button = addon->GetComponentButtonById(buttonId);
        if (button == null || !button->IsEnabled || button->AtkResNode == null || !button->AtkResNode->IsVisible())
        {
            clickedLabel = string.Empty;
            return false;
        }

        clickedLabel = string.IsNullOrWhiteSpace(GetButtonText(button))
            ? $"Button {buttonId}"
            : GetButtonText(button);
        return ActivateButton(addon, button);
    }

    private int ScoreButton(string text, IReadOnlyList<string> desiredTexts, IReadOnlyList<string> rejectTexts, bool allowPrimaryFallback)
    {
        if (rejectTexts.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase)))
        {
            return -1000;
        }

        for (var i = 0; i < desiredTexts.Count; i++)
        {
            if (text.Equals(desiredTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                return 500 - i;
            }

            if (text.Contains(desiredTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                return 400 - i;
            }
        }

        if (!allowPrimaryFallback)
        {
            return 0;
        }

        return string.IsNullOrWhiteSpace(text) ? 25 : 100;
    }

    private string ListButtons(AtkUnitBase* addon)
    {
        var buttons = CollectButtons(addon);
        var texts = buttons.Select(b => (string.IsNullOrWhiteSpace(b.Text) ? $"<Node {b.NodeId}>" : b.Text) + (b.Enabled ? string.Empty : " [disabled]")).Distinct().ToArray();
        return texts.Length == 0 ? "none" : string.Join(", ", texts);
    }

    private string ListCheckboxes(AtkUnitBase* addon)
    {
        var checkboxes = CollectCheckboxes(addon);
        var texts = checkboxes
            .Select(c =>
            {
                var stateLabel = c.Checked ? "checked" : "unchecked";
                return string.IsNullOrWhiteSpace(c.Text) ? $"<Node {c.NodeId}:{stateLabel}>" : $"{c.Text}:{stateLabel}";
            })
            .Distinct()
            .ToArray();
        return texts.Length == 0 ? "none" : string.Join(", ", texts);
    }

    private List<ButtonCandidate> CollectButtons(AtkUnitBase* addon, bool quiet = false)
    {
        var buttons = new List<ButtonCandidate>();
        var seenNodes = new HashSet<nint>();
        var seenComponents = new HashSet<nint>();

        if (addon->RootNode != null)
        {
            CollectButtonsFromNode(addon->RootNode, buttons, seenNodes, seenComponents);
        }

        if (addon->UldManager.NodeList != null)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node != null)
                {
                    CollectButtonsFromNode(node, buttons, seenNodes, seenComponents);
                }
            }
        }

        LogScan("button", buttons.Count, addon->NameString, quiet);
        return buttons;
    }

    /// <summary>Scan results are polled every framework tick while a wait is in progress; logging each one flooded the
    /// Dalamud log (one line per frame per wait). Now logs only when the (kind, addon, count) result changes.</summary>
    private void LogScan(string kind, int count, string addonName, bool quiet)
    {
        var key = $"{kind}:{addonName}:{count}";
        if (quiet || string.Equals(key, lastScanLogKey, StringComparison.Ordinal))
        {
            return;
        }

        lastScanLogKey = key;
        log.Information("[{Module}] Scanned {Count} {Kind} candidates on {Addon}.", ModuleId, count, kind, addonName);
    }

    private List<CheckboxCandidate> CollectCheckboxes(AtkUnitBase* addon)
    {
        var checkboxes = new List<CheckboxCandidate>();
        var seenNodes = new HashSet<nint>();
        var seenComponents = new HashSet<nint>();

        if (addon->RootNode != null)
        {
            CollectCheckboxesFromNode(addon->RootNode, checkboxes, seenNodes, seenComponents);
        }

        if (addon->UldManager.NodeList != null)
        {
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node != null)
                {
                    CollectCheckboxesFromNode(node, checkboxes, seenNodes, seenComponents);
                }
            }
        }

        LogScan("checkbox", checkboxes.Count, addon->NameString, quiet: false);
        return checkboxes;
    }

    private void CollectButtonsFromNode(AtkResNode* node, List<ButtonCandidate> buttons, HashSet<nint> seenNodes, HashSet<nint> seenComponents)
    {
        while (node != null)
        {
            var nodeAddress = (nint)node;
            if (!seenNodes.Add(nodeAddress))
            {
                node = node->NextSiblingNode;
                continue;
            }

            if (node->IsVisible())
            {
                var button = node->GetAsAtkComponentButton();
                if (button != null && button->OwnerNode != null)
                {
                    buttons.Add(new ButtonCandidate(button, GetButtonText(button), button->IsEnabled, node->NodeId, node->ScreenX, node->ScreenY));
                }

                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode != null && componentNode->Component != null)
                {
                    var componentAddress = (nint)componentNode->Component;
                    if (seenComponents.Add(componentAddress) && componentNode->Component->UldManager.NodeList != null)
                    {
                        for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
                        {
                            var child = componentNode->Component->UldManager.NodeList[i];
                            if (child != null)
                            {
                                CollectButtonsFromNode(child, buttons, seenNodes, seenComponents);
                            }
                        }
                    }
                }

                if (node->ChildNode != null)
                {
                    CollectButtonsFromNode(node->ChildNode, buttons, seenNodes, seenComponents);
                }
            }

            node = node->NextSiblingNode;
        }
    }

    private void CollectCheckboxesFromNode(AtkResNode* node, List<CheckboxCandidate> checkboxes, HashSet<nint> seenNodes, HashSet<nint> seenComponents)
    {
        while (node != null)
        {
            var nodeAddress = (nint)node;
            if (!seenNodes.Add(nodeAddress))
            {
                node = node->NextSiblingNode;
                continue;
            }

            if (node->IsVisible())
            {
                var checkBox = node->GetAsAtkComponentCheckBox();
                if (checkBox != null && checkBox->OwnerNode != null)
                {
                    checkboxes.Add(new CheckboxCandidate(checkBox, GetCheckboxText(checkBox), checkBox->IsEnabled, checkBox->IsChecked, node->NodeId, node->ScreenX, node->ScreenY));
                }

                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode != null && componentNode->Component != null)
                {
                    var componentAddress = (nint)componentNode->Component;
                    if (seenComponents.Add(componentAddress) && componentNode->Component->UldManager.NodeList != null)
                    {
                        for (var i = 0; i < componentNode->Component->UldManager.NodeListCount; i++)
                        {
                            var child = componentNode->Component->UldManager.NodeList[i];
                            if (child != null)
                            {
                                CollectCheckboxesFromNode(child, checkboxes, seenNodes, seenComponents);
                            }
                        }
                    }
                }

                if (node->ChildNode != null)
                {
                    CollectCheckboxesFromNode(node->ChildNode, checkboxes, seenNodes, seenComponents);
                }
            }

            node = node->NextSiblingNode;
        }
    }

    private string GetButtonText(AtkComponentButton* button)
    {
        AtkTextNode* textNode = button->ButtonTextNode;
        textNode = textNode == null ? button->GetTextNodeById(2) : textNode;
        textNode = textNode == null ? button->GetTextNodeById(3) : textNode;
        return textNode == null ? string.Empty : ECommons.GenericHelpers.Read(textNode->NodeText).TextValue.Trim();
    }

    private string GetCheckboxText(AtkComponentCheckBox* checkbox)
    {
        AtkTextNode* textNode = checkbox->ButtonTextNode;
        textNode = textNode == null ? checkbox->GetTextNodeById(2) : textNode;
        textNode = textNode == null ? checkbox->GetTextNodeById(3) : textNode;
        return textNode == null ? string.Empty : ECommons.GenericHelpers.Read(textNode->NodeText).TextValue.Trim();
    }

    private bool TryEnsureCheckboxState(AtkUnitBase* addon, IReadOnlyList<string> desiredTexts, bool desiredState, out string message)
    {
        var checkboxes = CollectCheckboxes(addon);
        var matching = checkboxes
            .Where(c => c.Enabled && ScoreText(c.Text, desiredTexts) > 0)
            .OrderBy(c => c.ScreenY)
            .ThenBy(c => c.ScreenX)
            .ToArray();

        if (matching.Length > 0)
        {
            var summary = string.Join("; ", matching.Select(c =>
            {
                var text = string.IsNullOrWhiteSpace(c.Text) ? $"Node {c.NodeId}" : c.Text;
                return $"{text} [id={c.NodeId}, checked={c.Checked}, x={c.ScreenX:0}, y={c.ScreenY:0}]";
            }));
            log.Information("[{Module}] Matching checkboxes for '{Label}': {Summary}", ModuleId, desiredTexts[0], summary);
        }

        var chosen = checkboxes
            .Where(c => c.Enabled)
            .OrderByDescending(c => ScoreText(c.Text, desiredTexts))
            .ThenBy(c => c.ScreenY)
            .ThenBy(c => c.ScreenX)
            .FirstOrDefault();

        if (chosen is null || ScoreText(chosen.Text, desiredTexts) <= 0)
        {
            message = $"Could not find checkbox for '{desiredTexts[0]}'. Visible checkboxes: {ListCheckboxes(addon)}";
            log.Warning("[{Module}] {Message}", ModuleId, message);
            return false;
        }

        if (chosen.Checked == desiredState)
        {
            message = string.Empty;
            return false;
        }

        var label = string.IsNullOrWhiteSpace(chosen.Text) ? $"Node {chosen.NodeId}" : chosen.Text;
        if (!ActivateCheckbox(addon, chosen.CheckBox, desiredState))
        {
            message = $"Failed to toggle checkbox '{label}'.";
            log.Warning("[{Module}] {Message}", ModuleId, message);
            return false;
        }

        message = $"Set '{label}' to {(desiredState ? "checked" : "unchecked")}.";
        return true;
    }

    private int ScoreText(string text, IReadOnlyList<string> desiredTexts)
    {
        for (var i = 0; i < desiredTexts.Count; i++)
        {
            if (text.Equals(desiredTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                return 500 - i;
            }

            if (text.Contains(desiredTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                return 400 - i;
            }
        }

        return 0;
    }

    private bool ActivateButton(AtkUnitBase* addon, ButtonCandidate candidate) => ActivateButton(addon, candidate.Button);

    private bool ActivateButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null)
        {
            return false;
        }

        var targetNode = (AtkResNode*)button->AtkComponentBase.OwnerNode;
        if (targetNode == null || targetNode->AtkEventManager.Event == null)
        {
            return false;
        }

        var evt = (AtkEvent*)targetNode->AtkEventManager.Event;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, targetNode->AtkEventManager.Event);
        return true;
    }

    private bool ActivateCheckbox(AtkUnitBase* addon, AtkComponentCheckBox* checkbox, bool desiredState)
    {
        if (checkbox == null || checkbox->OwnerNode == null || checkbox->OwnerNode->AtkResNode.AtkEventManager.Event == null)
        {
            return false;
        }

        var evt = (AtkEvent*)checkbox->OwnerNode->AtkResNode.AtkEventManager.Event;
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
        checkbox->SetChecked(desiredState);
        return true;
    }

    private void SetStatus(string status)
    {
        if (string.Equals(Status, status, StringComparison.Ordinal))
        {
            return;
        }

        Status = status;
        log.Information("[{Module}] {Status}", ModuleId, status);
    }

    private bool FailAndAbort(string status)
    {
        Status = status;
        log.Error("[{Module}] {Status}", ModuleId, status);
        // The operation suffix (id, kind, first-update flag, step, elapsed) makes the Diagnostics entry attributable
        // to the exact step that failed instead of a bare "Party Finder failed"-shaped message.
        diagnostics.RecordFailure($"{ModuleId}: {status}{DescribeCurrentOperation()}");
        taskManager.Abort();
        EndOperation(PartyFinderOperationOutcome.Failed, status);
        return true;
    }

    /// <summary>Like <see cref="FailAndAbort"/> — stops the in-flight chain and returns automation to
    /// <see cref="AutomationState.Idle"/> — but for an expected, non-alarming reason (context temporarily invalid,
    /// no active listing to refresh) rather than a real automation defect. Deliberately never calls
    /// <see cref="DiagnosticsService.RecordFailure"/>: NEW_MODULE_GUIDE.md §24a is explicit that an expected
    /// cancellation/precondition-not-met outcome must not be routed through Diagnostics as if it were a scary
    /// operator-facing error. The next normal trigger (the native 5-minute warning, or an explicit operator click)
    /// retries cleanly — nothing here poisons future attempts.</summary>
    private bool StopGracefully(string status)
    {
        Status = status;
        log.Information("[{Module}] {Status}", ModuleId, status);
        taskManager.Abort();
        EndOperation(PartyFinderOperationOutcome.Stopped, status);
        return true;
    }

    private bool FailAndAbortCreate(string status)
    {
        observedActiveListing = false;
        return FailAndAbort(status);
    }

    private bool TryGetAddonByName(string name, out AtkUnitBase* addon)
    {
        addon = null;
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
        {
            return false;
        }

        foreach (var index in new[] { 1, 0, 2 })
        {
            addon = stage->RaptureAtkUnitManager->GetAddonByName(name, index);
            if (addon != null)
            {
                return true;
            }
        }

        return false;
    }

    private bool ExecuteGameCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            var shellModule = RaptureShellModule.Instance();
            if (uiModule == null || shellModule == null)
            {
                return false;
            }

            var utf8 = Utf8String.FromString(command);
            if (utf8 == null)
            {
                return false;
            }

            try
            {
                shellModule->ExecuteCommandInner(utf8, uiModule);
                return true;
            }
            finally
            {
                utf8->Dtor();
                IMemorySpace.Free(utf8);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[{Module}] ExecuteGameCommand failed for {Command}", ModuleId, command);
            return false;
        }
    }

    private sealed class ButtonCandidate(AtkComponentButton* button, string text, bool enabled, uint nodeId, float screenX, float screenY)
    {
        public AtkComponentButton* Button { get; } = button;
        public string Text { get; } = text;
        public bool Enabled { get; } = enabled;
        public uint NodeId { get; } = nodeId;
        public float ScreenX { get; } = screenX;
        public float ScreenY { get; } = screenY;
    }

    private sealed class CheckboxCandidate(AtkComponentCheckBox* checkBox, string text, bool enabled, bool checkedState, uint nodeId, float screenX, float screenY)
    {
        public AtkComponentCheckBox* CheckBox { get; } = checkBox;
        public string Text { get; } = text;
        public bool Enabled { get; } = enabled;
        public bool Checked { get; } = checkedState;
        public uint NodeId { get; } = nodeId;
        public float ScreenX { get; } = screenX;
        public float ScreenY { get; } = screenY;
    }
}
