using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Services;

namespace VenueOS.Plugin.ShoutRunner;

/// <summary>The unsafe, game-version-sensitive travel engine — a hardened port/adaptation of the donor's
/// <c>MacroRunner</c> (ShoutRunner familiarization report §3/§22), reduced to exactly the primitives
/// <see cref="IShoutRunnerAutomation"/> needs and with every confirmed defect fixed rather than reproduced:
/// <list type="bullet">
/// <item>No unbounded wait anywhere (the donor's <c>WaitUntilChatReadyAsync</c> was a bare <c>while(true)</c> —
/// familiarization report §13). Every wait loop here has a fixed deadline.</item>
/// <item><see cref="EnsureReadyAsync"/> is the one shared readiness/recovery gate every live action goes through —
/// generalizing the donor's DC-transfer-only "stuck at login screen" detector so a character that ends up logged
/// out from <i>any</i> cause gets the same bounded recovery attempt, not just mid-DC-transfer.</item>
/// <item><see cref="ClassifyTransferAsync"/> reports <see cref="ShoutRunnerCrossDataCenterCheck.Unknown"/> on IPC
/// failure instead of silently defaulting to same-Data-Center (familiarization report §9).</item>
/// <item><see cref="TeleportToDestinationAsync"/> never trusts <c>Telepo.Teleport(...)</c>'s bare return value as
/// proof of arrival, and retries loading the Aetheryte data sheet on failure instead of permanently giving up after
/// one failed attempt (familiarization report §8).</item>
/// <item><c>/li</c> and <c>/shout</c> both go through the shared <see cref="ChatCommandService"/> — no private
/// command queue (familiarization report §7/§14).</item>
/// </list>
/// Lives in <c>VenueOS.Plugin</c>, not <c>VenueOS.Modules.Operations</c>, for the same reason
/// <c>PartyFinderAutomationService</c> does — unsafe FFXIVClientStructs access, ECommons, and Dalamud game services
/// (<c>NEW_MODULE_GUIDE.md</c> §30). <see cref="ShoutRunnerService"/> and its tests depend only on
/// <see cref="IShoutRunnerAutomation"/> and never need a live game context.</summary>
public sealed class ShoutRunnerAutomationService : IShoutRunnerAutomation, IDisposable
{
    private static readonly string[] CongestionMarkers =
    [
        "This World is experiencing congestion. Character movement is limited at this time",
        "currently congested",
        "destination world is currently congested",
        "please wait until the world has become less congested",
        "world is currently full",
        "please wait until an opening is available and try again",
    ];

    private const int EscapeVirtualKey = 0x1B;

    // Familiarization report §16's timeout matrix, ported with the same order-of-magnitude values (the donor's
    // actual verified constants) except where explicitly hardened — see the type-level remark and each field's own
    // comment for what changed and why.
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoggedOutRecoveryGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LifestreamBusyBeforeStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SameDataCenterStartWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SameDataCenterTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CrossDataCenterStartWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CrossDataCenterTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoggedOutIdleStuckThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeleportReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeleportTransitionTimeout = TimeSpan.FromSeconds(90);

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly ChatCommandService chat;
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;

    // Every Lifestream IPC call is made ON THE FRAMEWORK THREAD (0.3.12). Lifestream's IsBusy reads its ECommons
    // TaskManager's plain List/field state, which Lifestream mutates every framework tick; before 0.3.12 the transfer
    // wait loops called it from thread-pool continuations (after ConfigureAwait(false)), i.e. concurrently with that
    // mutation. See docs/SHOUTRUNNER_LIFESTREAM_COMPATIBILITY_AUDIT.md §5 (D1).
    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitSameDc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitCrossDc;
    private readonly ICallGateSubscriber<object?> lifestreamAbort;

    private readonly Dictionary<uint, string> aetheryteNames = new();
    private readonly Dictionary<uint, string> territoryNames = new();
    private bool teleportDataLoaded;

    // Congestion monitoring is owned by exactly one transfer operation at a time, identified by its operation id, so a
    // cancelled transfer's late `finally` can never clear or disable the monitor of a newer transfer (0.3.12, D6).
    private readonly object transferMonitorLock = new();
    private int transferOperationSequence;
    private int monitoredTransferOperationId;
    private string? monitoredTransferWorld;
    private int monitoredTransferCongestionCount;
    private bool monitoredTransferShouldSkip;

    public ShoutRunnerAutomationService(IDalamudPluginInterface pluginInterface, IClientState clientState, IObjectTable objectTable, ICondition condition, IChatGui chatGui, IDataManager dataManager, IFramework framework, ChatCommandService chat, IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.chatGui = chatGui;
        this.dataManager = dataManager;
        this.framework = framework;
        this.chat = chat;
        this.log = log;
        this.pluginInterface = pluginInterface;

        // The donor's LifestreamIpc also wrapped "Lifestream.ChangeWorld" and "Lifestream.CanVisitSameDC" — both
        // confirmed dead code (never called anywhere in the donor's own MacroRunner, familiarization report §6) and
        // deliberately not reproduced here.
        lifestreamIsBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        // 0.3.12: CanVisitSameDC is now consulted too. It was previously dismissed as donor dead code, but it is needed
        // to tell "same Data Center" apart from "Lifestream cannot reach this World" (ShoutRunnerTransferClassification).
        lifestreamCanVisitSameDc = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        lifestreamCanVisitCrossDc = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        lifestreamAbort = pluginInterface.GetIpcSubscriber<object?>("Lifestream.Abort");

        chatGui.ChatMessageUnhandled += OnChatMessageUnhandled;
    }

    public void Dispose() => chatGui.ChatMessageUnhandled -= OnChatMessageUnhandled;

    public void ResetForVenue()
    {
        lock (transferMonitorLock)
        {
            monitoredTransferOperationId = 0;
            monitoredTransferWorld = null;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    /// <summary>Must return quickly (see the interface's doc comment): one framework-thread-marshaled Lifestream abort
    /// plus one UI-dismissal attempt, fired without being awaited. Called from the UI (Stop) and from module teardown,
    /// both already on the framework thread, where <c>RunOnFrameworkThread</c> runs inline, so the abort still
    /// happens before this returns. The robust, multi-attempt dismissal and recovery verification lives in
    /// <see cref="EnsureReadyAsync"/>/<see cref="TravelToWorldAsync"/>, which the orchestration layer awaits.</summary>
    public void Abort()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            TryLifestreamAbortOnFrameworkThread();
            DismissTransferUiOnceUnsafe();
        });
    }

    public async Task<ShoutRunnerReadinessOutcome> EnsureReadyAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + ReadinessTimeout;
        DateTime? loggedOutSince = null;
        var recoveryAttempted = false;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await GetGameStateAsync(token).ConfigureAwait(false);

            if (!state.IsLoggedIn || !state.HasLocalPlayer)
            {
                loggedOutSince ??= DateTime.UtcNow;
                // Familiarization report §13: the donor's chat-readiness wait had no equivalent to its own DC-transfer
                // waiter's "stuck at login screen" detector, so a character left logged out by any other cause than a
                // DC transfer in progress could hang the entire automation forever. This generalizes that recovery
                // attempt to every readiness check, not just the one call site the donor happened to guard.
                if (!recoveryAttempted && DateTime.UtcNow - loggedOutSince.Value >= LoggedOutRecoveryGrace)
                {
                    recoveryAttempted = true;
                    await AbortLifestreamAsync().ConfigureAwait(false);
                    await DismissTransferUiAsync(token).ConfigureAwait(false);
                }

                await Task.Delay(500, token).ConfigureAwait(false);
                continue;
            }

            loggedOutSince = null;
            var blocked = state.BetweenAreas || state.BetweenAreas51 || state.LoggingOut || state.OccupiedInCutSceneEvent
                || state.OccupiedInQuestEvent || state.OccupiedInEvent || state.Occupied || state.WatchingCutscene;
            if (!blocked)
            {
                return recoveryAttempted
                    ? ShoutRunnerReadinessOutcome.RecoveredThenReady("Character is logged in and controllable.")
                    : ShoutRunnerReadinessOutcome.ReadyNow();
            }

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        return ShoutRunnerReadinessOutcome.RecoveryFailed($"Character was not confirmed logged in and controllable within {ReadinessTimeout.TotalSeconds:0}s.");
    }

    /// <summary>0.3.13 (Patch 7.58): the travel method is decided from GAME DATA (logical Data Center and region of the
    /// current and target World), and Lifestream's reachability lists only confirm it. See
    /// <see cref="ShoutRunnerTravelClassification.Classify"/>.</summary>
    public async Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (sameDc, crossDc, current, target) = await OnFrameworkAsync(() => (
            TryLifestreamQuery(lifestreamCanVisitSameDc, targetWorld, "CanVisitSameDC"),
            TryLifestreamQuery(lifestreamCanVisitCrossDc, targetWorld, "CanVisitCrossDC"),
            CurrentPlacementOnFrameworkThread(),
            ResolvePlacement(targetWorld)), token).ConfigureAwait(false);
        var catalogDc = ShoutRunnerCatalog.DataCenterFor(targetWorld);
        var check = ShoutRunnerTravelClassification.Classify(sameDc, crossDc, current, target, catalogDc);
        var line = $"ShoutRunner: travel to {targetWorld} classified {check}: current {DescribePlacement(current)}, target {DescribePlacement(target)}, catalog DC {catalogDc ?? "(none)"}, Lifestream {LifestreamVersion()} CanVisitSameDC={FormatBool(sameDc)} CanVisitCrossDC={FormatBool(crossDc)}.";
        if (check is ShoutRunnerCrossDataCenterCheck.SameDataCenter) log.Information(line); else log.Warning(line);
        return check;
    }

    private static string DescribePlacement(ShoutRunnerWorldPlacement? p) =>
        p is null ? "(unknown)" : $"{p.World} (World id {p.WorldId}, DC {p.DataCenter} id {p.DataCenterId}, region {p.RegionId})";

    /// <summary>The installed Lifestream's version, for diagnostics only. Never used to enable a capability.</summary>
    private string LifestreamVersion()
    {
        try
        {
            var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase));
            return plugin is null ? "not installed" : $"{plugin.Version}{(plugin.IsLoaded ? string.Empty : " (not loaded)")}";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ShoutRunner: could not read the installed Lifestream version.");
            return "unknown";
        }
    }

    /// <summary>One Lifestream World transfer, owned end to end by a fresh operation id (<c>#n</c> in every log and
    /// diagnostic line). Arrival is decided ONLY from the local player's own current World, sampled on the framework
    /// thread together with Lifestream's busy state, and confirmed stable by <see cref="ShoutRunnerArrivalVerifier"/>.
    /// The <c>/li</c> command being executed is never treated as proof of anything.</summary>
    public async Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token)
    {
        var operationId = Interlocked.Increment(ref transferOperationSequence);
        var kind = crossDataCenter ? "Data Center Travel (cross-region)" : "World Visit (same logical DC)";
        var target = await OnFrameworkAsync(() => ResolvePlacement(targetWorld), token).ConfigureAwait(false);
        var initialState = await GetGameStateAsync(token).ConfigureAwait(false);
        var context = new TransferContext(operationId, kind, targetWorld, target, initialState, DateTime.UtcNow, LifestreamVersion());

        if (target is null)
            return Finish(context, ShoutRunnerTransferOutcome.Failed($"{targetWorld} is not a World in the game data.", context.Describe(initialState, null, "requested World not found in the World sheet")));

        // 0.3.13 travel-time guard (Patch 7.58). The method is re-derived from where the character is RIGHT NOW. A /li to
        // another logical DC of the same region would make current Lifestream use its obsolete Data Center Travel (Home
        // World detour, logout, character select), which the game no longer allows inside a region. A /li to another
        // region for a "same DC" request would also log out. Either way no command is issued.
        var current = initialState.Placement;
        if (current is not null && initialState.IsLoggedIn && initialState.HasLocalPlayer)
        {
            if (ShoutRunnerTravelClassification.WouldUseObsoleteDataCenterTravel(current, target))
                return Finish(context, ShoutRunnerTransferOutcome.Failed(
                    $"Refused travel to {targetWorld}: it is on another Data Center ({target.DataCenter}), which now needs the in-game Patch 7.58 World Visit; the installed Lifestream would log out instead.",
                    context.Describe(initialState, null, "travel-time guard: other logical DC in the same region; no command issued")));
            var crossRegion = target.RegionId != current.RegionId;
            if (crossRegion != crossDataCenter)
                return Finish(context, ShoutRunnerTransferOutcome.Failed(
                    $"Refused travel to {targetWorld}: the character's location changed since the route checked how to get there.",
                    context.Describe(initialState, null, $"travel-time guard: requested {kind} but the target is {(crossRegion ? "in another region" : "in the same region")}; no command issued")));
        }

        // Case 1 ("already on target World"): checked before ever touching Lifestream, so a World the character is
        // already standing on never issues an unnecessary /li command.
        if (initialState.IsLoggedIn && initialState.HasLocalPlayer && string.Equals(initialState.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase))
            return Finish(context, ShoutRunnerTransferOutcome.Success(context.Describe(initialState, null, "already on the requested World; no travel issued")));

        if (!await WaitForLifestreamReadyAsync(token).ConfigureAwait(false))
            return Finish(context, ShoutRunnerTransferOutcome.Failed("Lifestream is unavailable or busy.", context.Describe(initialState, null, "Lifestream IsBusy unreadable or busy for 30 s before the request")));

        log.Information($"ShoutRunner transfer #{operationId} ({kind}): {initialState.CurrentWorld} ({initialState.CurrentWorldId}) [{initialState.CurrentDataCenter}] → {targetWorld} ({target.WorldId}, {target.DataCenter}); issuing /li {targetWorld}.");
        BeginTransferMonitoring(operationId, targetWorld);
        try
        {
            if (!await IssueChatCommandAsync($"/li {targetWorld}", token).ConfigureAwait(false))
                return Finish(context, ShoutRunnerTransferOutcome.Failed("Could not send the Lifestream travel command.", context.Describe(initialState, null, "chat transport rejected /li")));

            // Donor-proven pacing delay before polling begins (familiarization report §6) — never the sole evidence
            // a transition occurred; both wait methods below drive off observed game/Lifestream state.
            await Task.Delay(1000, token).ConfigureAwait(false);

            var outcome = crossDataCenter
                ? await WaitForCrossDataCenterTransferAsync(context, operationId, token).ConfigureAwait(false)
                : await WaitForSameDataCenterTransferAsync(context, operationId, token).ConfigureAwait(false);

            if (outcome.Result == ShoutRunnerTransferResult.WorldCongestedSkip)
            {
                await AbortLifestreamAsync().ConfigureAwait(false);
                await DismissTransferUiAsync(token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            else if (outcome.Result == ShoutRunnerTransferResult.Failed)
            {
                await AbortLifestreamAsync().ConfigureAwait(false);
                await DismissTransferUiAsync(token).ConfigureAwait(false);
            }

            return Finish(context, outcome);
        }
        finally
        {
            EndTransferMonitoring(operationId);
        }
    }

    private ShoutRunnerTransferOutcome Finish(TransferContext context, ShoutRunnerTransferOutcome outcome)
    {
        var line = $"ShoutRunner transfer #{context.OperationId} {outcome.Result}: {outcome.Detail ?? "arrived"} | {outcome.Diagnostic}";
        if (outcome.Result == ShoutRunnerTransferResult.Success) log.Information(line); else log.Warning(line);
        return outcome;
    }

    public async Task<string?> TryGetCurrentPlaceNameAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var row = dataManager.GetExcelSheet<TerritoryType>()?.GetRow(clientState.TerritoryType);
                var placeName = row?.PlaceName.ValueNullable?.Name.ExtractText().Trim();
                tcs.TrySetResult(string.IsNullOrWhiteSpace(placeName) ? null : placeName);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "ShoutRunner: current-location lookup failed.");
                tcs.TrySetResult(null);
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<ShoutRunnerTeleportOutcome> TeleportToDestinationAsync(string destinationName, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TeleportReadinessTimeout;
        TeleportPrepareResult prepared;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            prepared = await PrepareTeleportAsync(destinationName, token).ConfigureAwait(false);
            if (!prepared.Found)
                return ShoutRunnerTeleportOutcome.Failed($"No attuned Aetheryte matched \"{destinationName}\" (or the match was ambiguous).");
            if (prepared.ActionStatus == 0) break;
            // Familiarization report §8: the donor checked readiness exactly once and failed instantly. This waits,
            // bounded, for the Teleport action to become usable instead.
            if (DateTime.UtcNow >= deadline)
                return ShoutRunnerTeleportOutcome.Failed($"Teleport action was not ready within {TeleportReadinessTimeout.TotalSeconds:0}s (status {prepared.ActionStatus}).");
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        if (!await InvokeTeleportAsync(prepared.Info, token).ConfigureAwait(false))
            return ShoutRunnerTeleportOutcome.Failed($"Teleport call failed for {prepared.MatchedName}.");

        // Familiarization report §8: the donor trusted Teleport(...)'s bare `true` return as proof of arrival. This
        // waits, bounded, for an actual observed transition before declaring success.
        var confirmed = await WaitForTeleportTransitionAsync(token).ConfigureAwait(false);
        return confirmed
            ? ShoutRunnerTeleportOutcome.Success()
            : ShoutRunnerTeleportOutcome.Failed($"Teleport to {prepared.MatchedName} did not complete within {TeleportTransitionTimeout.TotalSeconds:0}s.");
    }

    public Task<ShoutRunnerObservedLocation?> TryGetCurrentLocationAsync(CancellationToken token) => OnFrameworkAsync(() =>
    {
        var world = objectTable.LocalPlayer?.CurrentWorld.ValueNullable;
        var name = world?.Name.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var dc = world!.Value.DataCenter.ValueNullable?.Name.ExtractText().Trim();
        return new ShoutRunnerObservedLocation(name, string.IsNullOrWhiteSpace(dc) ? null : dc);
    }, token);

    private async Task<bool> WaitForLifestreamReadyAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + LifestreamBusyBeforeStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (state.LifestreamBusy is null) return false;
            if (state.LifestreamBusy == false) return true;
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Same-Data-Center World Visit. Phase 1 (start window) requires evidence that Lifestream ACCEPTED the
    /// request: it reported busy, or a zone transition began. If neither happens, the transfer fails fast with an
    /// explicit "not accepted" reason instead of waiting out the full timeout (0.3.12, D4). Lifestream refuses
    /// several requests with only a toast and no state change: "Another task is in progress", "Already in this world",
    /// a configured Travel Block. Phase 2 hands every sample to <see cref="ShoutRunnerArrivalVerifier"/>, which keeps the
    /// live-verified Halicarnassus fix (<see cref="ShoutRunnerSameDataCenterArrival"/>: an intermediate city settle
    /// while Lifestream is still busy is never a verdict) and adds the stability window for both final verdicts.</summary>
    private async Task<ShoutRunnerTransferOutcome> WaitForSameDataCenterTransferAsync(TransferContext context, int operationId, CancellationToken token)
    {
        var targetWorld = context.TargetWorld;
        var verifier = new ShoutRunnerArrivalVerifier(targetWorld, context.Initial.CurrentWorld, context.Target?.DataCenter);
        var deadline = DateTime.UtcNow + SameDataCenterTimeout;
        var startDeadline = DateTime.UtcNow + SameDataCenterStartWindow;
        var seenTransition = false;
        var accepted = false;
        var state = context.Initial;

        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer(operationId)) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.", context.Describe(state, verifier, "two congestion messages"));
            state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (state.BetweenAreas || state.BetweenAreas51) seenTransition = true;
            verifier.Observe(Sample(state, seenTransition), DateTimeOffset.UtcNow);
            if (state.LifestreamBusy == true || seenTransition) { accepted = true; break; }
            if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase)) return ShoutRunnerTransferOutcome.Success(context.Describe(state, verifier, "on the requested World during the start window"));
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        if (!accepted)
            return ShoutRunnerTransferOutcome.Failed(
                $"Lifestream did not start travel to {targetWorld} within {SameDataCenterStartWindow.TotalSeconds:0}s (request not accepted; check Lifestream's on-screen notifications).",
                context.Describe(state, verifier, "no Lifestream busy state and no zone transition after /li"));

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer(operationId)) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.", context.Describe(state, verifier, "two congestion messages"));

            state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (state.BetweenAreas || state.BetweenAreas51) seenTransition = true;
            var verdict = verifier.Observe(Sample(state, seenTransition), DateTimeOffset.UtcNow);
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.Arrived) return ShoutRunnerTransferOutcome.Success(context.Describe(state, verifier, "arrival verified"));
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.WrongWorld) return ShoutRunnerTransferOutcome.WrongDestination(verifier.DescribeWrongWorld(state.CurrentWorld), context.Describe(state, verifier, "wrong World or Data Center confirmed stable with Lifestream idle"));

            await Task.Delay(500, token).ConfigureAwait(false);
        }

        return ShoutRunnerTransferOutcome.Failed($"Same-Data-Center transfer to {targetWorld} timed out after {SameDataCenterTimeout.TotalMinutes:0} minutes.", context.Describe(state, verifier, "overall timeout"));
    }

    /// <summary>0.3.13: used ONLY for cross-REGION Data Center Travel (e.g. Oceania to NA), which the game still does
    /// through logout and character select after Patch 7.58. Travel between logical DCs of one region never reaches
    /// this method: <see cref="TravelToWorldAsync"/>'s travel-time guard refuses it first.
    ///
    /// The entire-Data-Center-unavailable path (familiarization report §12) — reproduced with the "stuck at
    /// the login screen" idle-while-logged-out detector, but this version performs the full recovery sequence
    /// (abort, dismiss, confirm playable) itself before ever reporting a skip, satisfying the hard route invariant
    /// that nothing continues while the character is still at character-select/login limbo. 0.3.12: same acceptance
    /// fail-fast and stable-verdict rules as the same-Data-Center path. Lifestream refuses a Data Center transfer
    /// outright, with only a notification, when "Allow Data Center travel" is off in its settings.</summary>
    private async Task<ShoutRunnerTransferOutcome> WaitForCrossDataCenterTransferAsync(TransferContext context, int operationId, CancellationToken token)
    {
        var targetWorld = context.TargetWorld;
        var verifier = new ShoutRunnerArrivalVerifier(targetWorld, context.Initial.CurrentWorld, context.Target?.DataCenter);
        var deadline = DateTime.UtcNow + CrossDataCenterTimeout;
        var startDeadline = DateTime.UtcNow + CrossDataCenterStartWindow;
        var seenLogout = false;
        var accepted = false;
        DateTime? loggedOutWhileIdleSince = null;
        var state = context.Initial;

        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer(operationId)) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.", context.Describe(state, verifier, "two congestion messages"));
            state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (!state.IsLoggedIn) seenLogout = true;
            verifier.Observe(Sample(state, seenLogout), DateTimeOffset.UtcNow);
            if (state.LifestreamBusy == true || seenLogout) { accepted = true; break; }
            if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase)) return ShoutRunnerTransferOutcome.Success(context.Describe(state, verifier, "on the requested World during the start window"));
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        if (!accepted)
            return ShoutRunnerTransferOutcome.Failed(
                $"Lifestream did not start Data Center travel to {targetWorld} within {CrossDataCenterStartWindow.TotalSeconds:0}s (request not accepted; check that Data Center travel is allowed in Lifestream's settings).",
                context.Describe(state, verifier, "no Lifestream busy state and no logout after /li"));

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer(operationId)) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.", context.Describe(state, verifier, "two congestion messages"));

            state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (!state.IsLoggedIn) seenLogout = true;
            var verdict = verifier.Observe(Sample(state, seenLogout), DateTimeOffset.UtcNow);

            if (!state.IsLoggedIn)
            {
                if (state.LifestreamBusy == false)
                {
                    loggedOutWhileIdleSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - loggedOutWhileIdleSince.Value >= LoggedOutIdleStuckThreshold)
                    {
                        await AbortLifestreamAsync().ConfigureAwait(false);
                        await DismissTransferUiAsync(token).ConfigureAwait(false);
                        var recovered = await WaitForRecoveryAsync(token).ConfigureAwait(false);
                        return recovered
                            ? ShoutRunnerTransferOutcome.DataCenterUnavailable($"Transfer to {targetWorld} stopped at the login screen; the destination data center is likely congested.", context.Describe(state, verifier, "logged out with Lifestream idle; recovered"))
                            : ShoutRunnerTransferOutcome.Failed($"Transfer to {targetWorld} stopped at the login screen and the character could not be recovered to a playable state.", context.Describe(state, verifier, "logged out with Lifestream idle; recovery failed"));
                    }
                }
                else
                {
                    loggedOutWhileIdleSince = null;
                }

                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }

            loggedOutWhileIdleSince = null;
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.Arrived) return ShoutRunnerTransferOutcome.Success(context.Describe(state, verifier, "arrival verified"));
            if (verdict == ShoutRunnerArrivalVerifier.Verdict.WrongWorld) return ShoutRunnerTransferOutcome.WrongDestination(verifier.DescribeWrongWorld(state.CurrentWorld), context.Describe(state, verifier, "wrong World or Data Center confirmed stable with Lifestream idle"));
            if (!seenLogout && state.HasLocalPlayer && string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase))
                return ShoutRunnerTransferOutcome.Success(context.Describe(state, verifier, "on the requested World without a logout"));

            await Task.Delay(1000, token).ConfigureAwait(false);
        }

        return ShoutRunnerTransferOutcome.Failed($"Cross-Data-Center transfer to {targetWorld} timed out after {CrossDataCenterTimeout.TotalMinutes:0} minutes.", context.Describe(state, verifier, "overall timeout"));
    }

    private static ShoutRunnerArrivalSample Sample(GameState state, bool prerequisiteSeen) =>
        new(state.IsLoggedIn, state.HasLocalPlayer, state.BetweenAreas || state.BetweenAreas51, prerequisiteSeen, state.CurrentWorld, state.LifestreamBusy, state.CurrentDataCenter);

    private static string FormatBool(bool? value) => value switch { true => "true", false => "false", null => "unavailable" };

    /// <summary>Everything about one transfer that Diagnostics needs to separate "VenueOS asked for the wrong World",
    /// "VenueOS misread where it is" and "the travel itself went elsewhere". It carries no character names: only World
    /// names and ids, Data Centers, and whether the observed World is the character's home World.</summary>
    private sealed record TransferContext(int OperationId, string Kind, string TargetWorld, ShoutRunnerWorldPlacement? Target, GameState Initial, DateTime StartedAtUtc, string LifestreamVersion)
    {
        public string Describe(GameState observed, ShoutRunnerArrivalVerifier? verifier, string note) =>
            $"transfer #{OperationId} ({Kind}, Lifestream {LifestreamVersion}); requested {TargetWorld} (World id {Target?.WorldId.ToString() ?? "unknown"}, DC {Target?.DataCenter ?? "(unknown)"} id {Target?.DataCenterId.ToString() ?? "unknown"}); " +
            $"started on {Display(Initial.CurrentWorld)} (id {Initial.CurrentWorldId}, DC {Display(Initial.CurrentDataCenter)}); " +
            $"observed {Display(observed.CurrentWorld)} (id {observed.CurrentWorldId}, DC {Display(observed.CurrentDataCenter)}, home World: {(observed.OnHomeWorld ? "yes" : "no")}, logged in: {(observed.IsLoggedIn ? "yes" : "no")}); " +
            (verifier is null ? string.Empty : verifier.DescribeHistory() + "; ") +
            $"elapsed {(DateTime.UtcNow - StartedAtUtc).TotalSeconds:0.0}s; {note}";

        private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
    }

    private async Task<bool> WaitForRecoveryAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + ReadinessTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (state.IsLoggedIn && state.HasLocalPlayer && !state.BetweenAreas && !state.BetweenAreas51 && !state.LoggingOut) return true;
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        return false;
    }

    private Task<bool> IssueChatCommandAsync(string command, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.Enqueue(new(command, token, (success, _) => tcs.TrySetResult(success)));
        return tcs.Task;
    }

    // ----- Congestion detection (familiarization report §11) -----

    private void BeginTransferMonitoring(int operationId, string targetWorld)
    {
        lock (transferMonitorLock)
        {
            monitoredTransferOperationId = operationId;
            monitoredTransferWorld = targetWorld;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    /// <summary>Only the operation that owns the monitor may clear it — a stale, cancelled transfer finishing late
    /// must not disable congestion detection for the newer transfer that replaced it.</summary>
    private void EndTransferMonitoring(int operationId)
    {
        lock (transferMonitorLock)
        {
            if (monitoredTransferOperationId != operationId) return;
            monitoredTransferOperationId = 0;
            monitoredTransferWorld = null;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    private bool ShouldSkipCurrentTransfer(int operationId)
    {
        lock (transferMonitorLock) return monitoredTransferOperationId == operationId && monitoredTransferShouldSkip;
    }

    /// <summary>Raised by Dalamud on the framework thread, so the Lifestream abort below is already thread-correct.</summary>
    private void OnChatMessageUnhandled(IChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text) || !IsCongestedTransferMessage(text)) return;

        lock (transferMonitorLock)
        {
            if (monitoredTransferWorld is null || monitoredTransferShouldSkip) return;
            monitoredTransferCongestionCount++;
            log.Information($"ShoutRunner transfer #{monitoredTransferOperationId}: congestion message {monitoredTransferCongestionCount}/2 for {monitoredTransferWorld}.");
            if (monitoredTransferCongestionCount >= 2)
            {
                monitoredTransferShouldSkip = true;
                TryLifestreamAbortOnFrameworkThread();
            }
        }
    }

    private static bool IsCongestedTransferMessage(string text) => CongestionMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // ----- Lifestream IPC (framework thread only) -----

    /// <summary>Lifestream.IsBusy, or null if the IPC is not available. Must be called on the framework thread.</summary>
    private bool? TryLifestreamIsBusyOnFrameworkThread()
    {
        try { return lifestreamIsBusy.InvokeFunc(); }
        catch (IpcNotReadyError) { return null; }
        catch (Exception ex) { log.Debug(ex, "ShoutRunner: Lifestream.IsBusy failed."); return null; }
    }

    /// <summary>A Lifestream world query, or null if the IPC is not available. Must be called on the framework thread.</summary>
    private bool? TryLifestreamQuery(ICallGateSubscriber<string, bool> subscriber, string world, string name)
    {
        try { return subscriber.InvokeFunc(world); }
        catch (IpcNotReadyError) { return null; }
        catch (Exception ex) { log.Debug(ex, $"ShoutRunner: Lifestream.{name} failed."); return null; }
    }

    private void TryLifestreamAbortOnFrameworkThread()
    {
        try { lifestreamAbort.InvokeAction(); }
        catch { /* best-effort */ }
    }

    private async Task AbortLifestreamAsync()
    {
        try { await framework.RunOnFrameworkThread(TryLifestreamAbortOnFrameworkThread).ConfigureAwait(false); }
        catch (Exception ex) { log.Debug(ex, "ShoutRunner: Lifestream abort could not be marshaled to the framework thread."); }
    }

    /// <summary>A public World's placement (World id, logical DC, region) from the World sheet, or null if no such World
    /// exists. Framework thread.</summary>
    private ShoutRunnerWorldPlacement? ResolvePlacement(string worldName)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<World>();
            foreach (var row in sheet)
            {
                if (row.IsPublic && string.Equals(row.Name.ExtractText(), worldName, StringComparison.OrdinalIgnoreCase)) return PlacementOf(row);
            }
        }
        catch (Exception ex) { log.Debug(ex, "ShoutRunner: World sheet lookup failed."); }
        return null;
    }

    private static ShoutRunnerWorldPlacement? PlacementOf(World row)
    {
        var dc = row.DataCenter.ValueNullable;
        if (dc is null) return null;
        return new ShoutRunnerWorldPlacement(row.Name.ExtractText(), row.RowId, dc.Value.Name.ExtractText(), row.DataCenter.RowId, dc.Value.Region.RowId);
    }

    /// <summary>The local player's current World placement, or null. Framework thread.</summary>
    private ShoutRunnerWorldPlacement? CurrentPlacementOnFrameworkThread()
    {
        var world = objectTable.LocalPlayer?.CurrentWorld.ValueNullable;
        return world is null ? null : PlacementOf(world.Value);
    }

    /// <summary>Runs <paramref name="func"/> on the framework thread and observes <paramref name="token"/> while waiting.</summary>
    private async Task<T> OnFrameworkAsync<T>(Func<T> func, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));
        _ = framework.RunOnFrameworkThread(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    // ----- Transfer/travel UI dismissal -----

    private async Task DismissTransferUiAsync(CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            token.ThrowIfCancellationRequested();
            await framework.RunOnFrameworkThread(DismissTransferUiOnceUnsafe).ConfigureAwait(false);
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private unsafe void DismissTransferUiOnceUnsafe()
    {
        try
        {
            var agent = AgentWorldTravel.Instance();
            if (agent != null && (agent->IsAddonShown() || agent->IsAgentActive() || agent->IsAddonReady()))
            {
                agent->HideAddon();
                agent->Hide();
            }

            // Escape is a real key event as far as the game is concerned; with the character in the world and nothing
            // to cancel, its native meaning is "open the System Menu" (ShoutRunnerEscapePolicy). Only send it when the
            // character is not in the world, which is the only situation this dismissal exists for.
            if (ShoutRunnerEscapePolicy.ShouldSendEscape(clientState.IsLoggedIn, objectTable.LocalPlayer != null))
                WindowsKeypress.SendKeypress(EscapeVirtualKey);
            else
                log.Debug("ShoutRunner: transfer UI dismissal skipped the Escape keypress — the character is in the world.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ShoutRunner: transfer UI dismissal failed.");
        }
    }

    // ----- Native teleport -----

    private readonly record struct TeleportPrepareResult(bool Found, TeleportInfo Info, string MatchedName, uint ActionStatus);

    private async Task<TeleportPrepareResult> PrepareTeleportAsync(string destination, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<TeleportPrepareResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                unsafe
                {
                    EnsureTeleportDataLoaded();
                    if (!TryFindTeleportInfo(destination, out var info, out var name)) { tcs.TrySetResult(new(false, default, destination, uint.MaxValue)); return; }
                    // targetId: no specific target is required for the Teleport action (action id 5); 0xE0000000 is the
                    // conventional "no target" sentinel used elsewhere against this same native API. REQUIRES LIVE
                    // FFXIV VERIFICATION — this exact 6-parameter overload did not exist in the donor's older
                    // FFXIVClientStructs version (it called a 2-parameter overload that no longer exists in this SDK).
                    var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 5, 0xE0000000, false, false, null);
                    tcs.TrySetResult(new(true, info, name, status));
                }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<bool> InvokeTeleportAsync(TeleportInfo info, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                unsafe
                {
                    if (Control.GetLocalPlayer() == null) { tcs.TrySetResult(false); return; }
                    tcs.TrySetResult(Telepo.Instance()->Teleport(info.AetheryteId, info.SubIndex));
                }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<bool> WaitForTeleportTransitionAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TeleportTransitionTimeout;
        var seenTransition = false;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            var transitioning = state.BetweenAreas || state.BetweenAreas51;
            if (transitioning) seenTransition = true;
            if (seenTransition && !transitioning && state.IsLoggedIn && state.HasLocalPlayer) return true;
            await Task.Delay(200, token).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Familiarization report §8's donor defect: <c>teleportDataLoaded</c> was set unconditionally before
    /// the load attempt, so a failed load (Excel sheet briefly unavailable, e.g. very early in plugin startup) was
    /// never retried for the rest of the session. Here the flag is only ever set on confirmed success.</summary>
    private void EnsureTeleportDataLoaded()
    {
        if (teleportDataLoaded) return;
        try
        {
            var sheet = dataManager.GetExcelSheet<Aetheryte>();
            if (sheet == null) return;

            foreach (var row in sheet)
            {
                var placeName = row.PlaceName.ValueNullable?.Name.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(placeName)) aetheryteNames[row.RowId] = placeName;

                if (row.IsAetheryte)
                {
                    var territoryName = row.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText().Trim();
                    if (!string.IsNullOrWhiteSpace(territoryName)) territoryNames[row.RowId] = territoryName;
                }
            }

            teleportDataLoaded = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ShoutRunner: failed to load teleport Aetheryte data; will retry.");
            aetheryteNames.Clear();
            territoryNames.Clear();
        }
    }

    /// <summary>The donor's exact three-pass matching (familiarization report §8), preserved: exact PlaceName, then
    /// exact Territory name, then a unique-substring match — refusing to guess when more than one candidate
    /// matches. Must run on the framework thread (accesses <see cref="Telepo"/> unsafely).</summary>
    private unsafe bool TryFindTeleportInfo(string destination, out TeleportInfo info, out string matchedName)
    {
        info = default;
        matchedName = destination;
        var dest = destination.Trim();
        if (dest.Length == 0) return false;

        var tp = Telepo.Instance();
        if (tp == null) return false;
        tp->UpdateAetheryteList();
        var count = tp->TeleportList.LongCount;
        if (count <= 0) return false;

        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            if (aetheryteNames.TryGetValue(entry.AetheryteId, out var placeName) && string.Equals(placeName, dest, StringComparison.OrdinalIgnoreCase))
            { info = entry; matchedName = placeName; return true; }
        }

        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            if (territoryNames.TryGetValue(entry.AetheryteId, out var territoryName) && string.Equals(territoryName, dest, StringComparison.OrdinalIgnoreCase))
            { info = entry; matchedName = aetheryteNames.TryGetValue(entry.AetheryteId, out var placeName2) ? placeName2 : territoryName; return true; }
        }

        TeleportInfo? match = null;
        string? matchName = null;
        var matches = 0;
        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            var placeName = aetheryteNames.TryGetValue(entry.AetheryteId, out var p) ? p : string.Empty;
            var territoryName = territoryNames.TryGetValue(entry.AetheryteId, out var t) ? t : string.Empty;

            if (!string.IsNullOrEmpty(placeName) && (placeName.Contains(dest, StringComparison.OrdinalIgnoreCase) || dest.Contains(placeName, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
                if (matches == 1) { match = entry; matchName = placeName; }
                continue;
            }

            if (!string.IsNullOrEmpty(territoryName) && (territoryName.Contains(dest, StringComparison.OrdinalIgnoreCase) || dest.Contains(territoryName, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
                if (matches == 1) { match = entry; matchName = !string.IsNullOrEmpty(placeName) ? placeName : territoryName; }
            }
        }

        if (matches == 1 && match.HasValue && matchName != null) { info = match.Value; matchedName = matchName; return true; }
        return false;
    }

    // ----- Game state -----

    /// <summary>One framework-thread snapshot. <see cref="LifestreamBusy"/> is read in the SAME framework callback as
    /// the World, so the two always describe the same frame (0.3.12).</summary>
    private sealed record GameState(bool IsLoggedIn, bool HasLocalPlayer, bool BetweenAreas, bool BetweenAreas51, bool LoggingOut,
        bool OccupiedInCutSceneEvent, bool OccupiedInQuestEvent, bool OccupiedInEvent, bool Occupied, bool WatchingCutscene, string CurrentWorld,
        uint CurrentWorldId, string CurrentDataCenter, bool OnHomeWorld, bool? LifestreamBusy, ShoutRunnerWorldPlacement? Placement);

    private async Task<GameState> GetGameStateAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<GameState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var localPlayer = objectTable.LocalPlayer;
                tcs.TrySetResult(new GameState(
                    clientState.IsLoggedIn,
                    localPlayer != null,
                    condition[ConditionFlag.BetweenAreas],
                    condition[ConditionFlag.BetweenAreas51],
                    condition[ConditionFlag.LoggingOut],
                    condition[ConditionFlag.OccupiedInCutSceneEvent],
                    condition[ConditionFlag.OccupiedInQuestEvent],
                    condition[ConditionFlag.OccupiedInEvent],
                    condition[ConditionFlag.Occupied],
                    condition[ConditionFlag.WatchingCutscene],
                    localPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    localPlayer?.CurrentWorld.RowId ?? 0,
                    localPlayer?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    localPlayer != null && localPlayer.CurrentWorld.RowId != 0 && localPlayer.CurrentWorld.RowId == localPlayer.HomeWorld.RowId,
                    TryLifestreamIsBusyOnFrameworkThread(),
                    localPlayer?.CurrentWorld.ValueNullable is { } currentWorld ? PlacementOf(currentWorld) : null));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }
}
