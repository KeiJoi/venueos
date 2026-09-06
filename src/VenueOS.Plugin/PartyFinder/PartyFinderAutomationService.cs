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
    private AutomationState state = AutomationState.Idle;

    private enum AutomationState { Idle, Running, Ending }

    public PartyFinderAutomationService(IClientState clientState, IPluginLog log, DiagnosticsService diagnostics)
    {
        this.clientState = clientState;
        this.log = log;
        this.diagnostics = diagnostics;
        taskManager = CreateTaskManager();
    }

    private static TaskManager CreateTaskManager() => new(new TaskManagerConfiguration(showDebug: false, showError: true, abortOnError: true, abortOnTimeout: true, timeoutSilently: false, timeLimitMS: 15000, executeDefaultConfigurationEvents: true));

    public string Status { get; private set; } = "Idle";

    public bool IsCompatibilityVerified { get; private set; }

    public bool IsBusy => state == AutomationState.Running;

    public bool IsEnding => state == AutomationState.Ending;

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
        IsCompatibilityVerified = false;
        state = AutomationState.Idle;
        SetStatus("Idle");
    }

    public void Abort()
    {
        taskManager.Abort();
        state = AutomationState.Idle;
        SetStatus("Aborted");
    }

    public void NotifyListingEnded()
    {
        observedActiveListing = false;
        SetStatus("Party Finder listing ended.");
    }

    public void QueueCreateOrUpdate(PartyFinderPreset preset, string reason)
    {
        if (state == AutomationState.Ending)
        {
            SetStatus("Party Finder is ending — try again once it finishes.");
            return;
        }

        QueueOperation(preset, requireExistingListing: HasOwnListing, reason);
    }

    public void QueueRefresh(PartyFinderPreset preset, string reason)
    {
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
        state = AutomationState.Running;
        SetStatus($"Queued {(requireExistingListing ? "refresh" : "create/update")} from {reason}.");

        taskManager.Enqueue(() => PrepareOperation(requireExistingListing), $"prepare_{reason}");
        taskManager.Enqueue(EnsureMainAddonReady, "open_pf");
        taskManager.Enqueue(() => ApplyPresetToAgent(preset), "apply_preset");
        taskManager.Enqueue(() => ClickMainAction(requireExistingListing), "open_editor");
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        taskManager.Enqueue(() => ApplyPresetToAgent(preset), "reapply_preset_in_editor");
        taskManager.EnqueueDelay(200, false, taskManager.DefaultConfiguration);
        taskManager.Enqueue(() => SyncEditorCheckboxes(preset), "sync_editor_checkboxes");
        taskManager.EnqueueDelay(150, false, taskManager.DefaultConfiguration);
        taskManager.Enqueue(() => SubmitEditor(requireExistingListing), "submit_editor");
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        taskManager.Enqueue(ConfirmYesNoIfNeeded, "confirm_yesno");
        taskManager.Enqueue(() => VerifySubmission(requireExistingListing), "verify_submission");
        taskManager.Enqueue(ClosePartyFinderWindowIfVisible, "close_pf");
        taskManager.Enqueue(() =>
        {
            state = AutomationState.Idle;
            SetStatus("Automation sequence finished.");
            return true;
        }, "finish");
    }

    private bool PrepareOperation(bool requireExistingListing)
    {
        if (!clientState.IsLoggedIn)
        {
            SetStatus("Not logged in.");
            return true;
        }

        if (requireExistingListing && !HasOwnListing)
        {
            SetStatus("Refresh requested, but no own listing is active.");
            return true;
        }

        SetStatus("Preparing Party Finder automation.");
        return true;
    }

    private bool EnsureMainAddonReady()
    {
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
        log.Information("[{Module}] Wrote recruitment fields: limitToWorldRaw={LimitToWorld} passwordRaw={Password}", ModuleId, recruit.LimitRecruitingToWorld, recruit.Password);
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
            return onReached(detailAddon);
        }

        if (TryGetAddonByName("LookingForGroup", out var mainAddon) && mainAddon->IsReady && mainAddon->IsVisible)
        {
            if (TryClickButtonById(mainAddon, 46, out var detailsClicked))
            {
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
                    SetStatus($"Clicked active listing edit button: {editClicked}.");
                    return true;
                }

                SetStatus("Waiting for active listing Edit button to become available.");
                log.Information("[{Module}] Active listing detail buttons: {Buttons}", ModuleId, ListButtons(detailAddon));
                return false;
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
            SetStatus($"Clicked main PF action: {clickedById}.");
            return true;
        }

        var desired = requireExistingListing ? EditButtonTexts : CreateButtonTexts;
        if (TryClickButton(addon, desired, RejectPrimaryTexts, allowPrimaryFallback: !requireExistingListing, out var clicked))
        {
            SetStatus($"Clicked main PF action: {clicked}.");
            return true;
        }

        if (!requireExistingListing && TryClickButton(addon, EditButtonTexts, RejectPrimaryTexts, allowPrimaryFallback: false, out clicked))
        {
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

    private bool SubmitEditor(bool requireExistingListing)
    {
        if (TryFindVisibleEditorAddon(out var addonName, out var addon))
        {
            string[] desiredTexts = requireExistingListing
                ? ["Apply Changes", "Update", "Save"]
                : ["Recruit Members", "Register", "Create Listing"];

            if (string.Equals(addonName, "LookingForGroupCondition", StringComparison.OrdinalIgnoreCase)
                && TryClickButtonById(addon, 111, out var clickedById))
            {
                SetStatus($"Clicked submit button '{clickedById}' on {addonName}.");
                return true;
            }

            if (TryClickButton(addon, desiredTexts, RejectPrimaryTexts, allowPrimaryFallback: false, out var clicked))
            {
                SetStatus($"Clicked submit button '{clicked}' on {addonName}.");
                return true;
            }

            // Reliability hardening: the editor addon being found/ready doesn't guarantee its submit button is
            // enabled/populated on this exact tick yet — retry within a bounded window before failing.
            if (submitEditorWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
            {
                SetStatus($"Waiting for the submit button to become available on {addonName}.");
                return false;
            }

            return FailAndAbort($"Timed out waiting for a submit button on {addonName}. Visible buttons: {ListButtons(addon)}");
        }

        if (submitEditorWait.Poll(DateTime.UtcNow, UiElementReadinessTimeout))
        {
            SetStatus("Waiting for the Party Finder editor to appear.");
            return false;
        }

        return FailAndAbort("Timed out waiting for the Party Finder editor to appear after opening the recruitment screen.");
    }

    private bool ConfirmYesNoIfNeeded()
    {
        if (!TryGetAddonByName("SelectYesno", out var addon) || !ECommons.GenericHelpers.IsAddonReady(addon))
        {
            if (TryGetAddonByName("SelectOk", out addon) && ECommons.GenericHelpers.IsAddonReady(addon))
            {
                if (TryClickButton(addon, ["OK", "Ok", "Confirm", "Close"], RejectPrimaryTexts, allowPrimaryFallback: true, out var okClicked))
                {
                    SetStatus($"Confirmed popup with '{okClicked}'.");
                }
            }

            return true;
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

    private bool VerifySubmission(bool requireExistingListing)
    {
        if (submissionVerificationStartedUtc == DateTime.MinValue)
        {
            submissionVerificationStartedUtc = DateTime.UtcNow;
        }

        if (!requireExistingListing && HasOwnListing)
        {
            observedActiveListing = true;
            SetStatus("Party Finder listing is active.");
            return true;
        }

        ConfirmYesNoIfNeeded();

        if (!TryFindVisibleEditorAddon(out _, out _))
        {
            observedActiveListing = true;
            SetStatus(requireExistingListing ? "Party Finder refresh submitted." : "Party Finder listing is active.");
            return true;
        }

        if (DateTime.UtcNow - submissionVerificationStartedUtc < SubmissionVerificationTimeout)
        {
            SetStatus("Waiting for Party Finder to finish submitting.");
            return false;
        }

        return requireExistingListing
            ? FailAndAbort("Submitted Party Finder refresh, but the editor did not close.")
            : FailAndAbortCreate("Submitted Party Finder, but no active listing was detected.");
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
        state = AutomationState.Ending;
        SetStatus($"Ending Party Finder ({reason})...");

        // Step 4: no own listing at all — treat as already ended, no false failure.
        if (!HasOwnListing)
        {
            state = AutomationState.Idle;
            observedActiveListing = false;
            SetStatus("Party Finder ended — Auto Refresh disabled");
            return;
        }

        // end_open_pf / end_open_details: identical to Refresh's "open_pf" + the requireExistingListing branch of
        // ClickMainAction — the same EnsureMainAddonReady and the same NavigateToOwnListingDetail helper, so any
        // future fix to that proven route benefits both flows and can never drift apart again.
        taskManager.Enqueue(EnsureMainAddonReady, "end_open_pf");
        taskManager.Enqueue(() => NavigateToOwnListingDetail(ClickEndButton, "reach the active listing screen to end recruitment"), "end_open_details");
        taskManager.EnqueueDelay(300, false, taskManager.DefaultConfiguration);
        taskManager.Enqueue(ConfirmYesNoIfNeeded, "end_confirm_yesno");
        taskManager.Enqueue(VerifyEnded, "end_verify");
        taskManager.Enqueue(ClosePartyFinderWindowIfVisible, "end_close_pf");
        taskManager.Enqueue(() =>
        {
            state = AutomationState.Idle;
            SetStatus("Party Finder ended — Auto Refresh disabled");
            return true;
        }, "end_finish");
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
        var texts = buttons.Select(b => string.IsNullOrWhiteSpace(b.Text) ? $"<Node {b.NodeId}>" : b.Text).Distinct().ToArray();
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

    private List<ButtonCandidate> CollectButtons(AtkUnitBase* addon)
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

        log.Information("[{Module}] Scanned {Count} button candidates on {Addon}.", ModuleId, buttons.Count, addon->NameString);
        return buttons;
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

        log.Information("[{Module}] Scanned {Count} checkbox candidates on {Addon}.", ModuleId, checkboxes.Count, addon->NameString);
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
        diagnostics.RecordFailure($"{ModuleId}: {status}");
        taskManager.Abort();
        state = AutomationState.Idle;
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
