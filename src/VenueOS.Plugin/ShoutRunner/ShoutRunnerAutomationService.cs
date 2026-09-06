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

    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitCrossDc;
    private readonly ICallGateSubscriber<object?> lifestreamAbort;

    private readonly Dictionary<uint, string> aetheryteNames = new();
    private readonly Dictionary<uint, string> territoryNames = new();
    private bool teleportDataLoaded;

    private readonly object transferMonitorLock = new();
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

        // The donor's LifestreamIpc also wrapped "Lifestream.ChangeWorld" and "Lifestream.CanVisitSameDC" — both
        // confirmed dead code (never called anywhere in the donor's own MacroRunner, familiarization report §6) and
        // deliberately not reproduced here.
        lifestreamIsBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamCanVisitCrossDc = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        lifestreamAbort = pluginInterface.GetIpcSubscriber<object?>("Lifestream.Abort");

        chatGui.ChatMessageUnhandled += OnChatMessageUnhandled;
    }

    public void Dispose() => chatGui.ChatMessageUnhandled -= OnChatMessageUnhandled;

    public void ResetForVenue()
    {
        lock (transferMonitorLock)
        {
            monitoredTransferWorld = null;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    /// <summary>Must return quickly (see the interface's doc comment) — a single quick IPC call plus a single quick
    /// framework-thread-marshaled UI-dismissal attempt, fired without being awaited. The robust, multi-attempt
    /// dismissal + recovery verification lives in <see cref="EnsureReadyAsync"/>/<see cref="TravelToWorldAsync"/>,
    /// which the orchestration layer does properly await.</summary>
    public void Abort()
    {
        TryLifestreamAbort();
        _ = framework.RunOnFrameworkThread(DismissTransferUiOnceUnsafe);
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
                    TryLifestreamAbort();
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

    public Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var isCrossDc = lifestreamCanVisitCrossDc.InvokeFunc(targetWorld);
            return Task.FromResult(isCrossDc ? ShoutRunnerCrossDataCenterCheck.CrossDataCenter : ShoutRunnerCrossDataCenterCheck.SameDataCenter);
        }
        catch (IpcNotReadyError)
        {
            return Task.FromResult(ShoutRunnerCrossDataCenterCheck.Unknown);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ShoutRunner: Lifestream.CanVisitCrossDC failed.");
            return Task.FromResult(ShoutRunnerCrossDataCenterCheck.Unknown);
        }
    }

    public async Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token)
    {
        if (!await WaitForLifestreamReadyAsync(token).ConfigureAwait(false))
            return ShoutRunnerTransferOutcome.Failed("Lifestream is unavailable or busy.");

        BeginTransferMonitoring(targetWorld);
        try
        {
            if (!await IssueChatCommandAsync($"/li {targetWorld}", token).ConfigureAwait(false))
                return ShoutRunnerTransferOutcome.Failed("Could not send the Lifestream travel command.");

            // Donor-proven pacing delay before polling begins (familiarization report §6) — never the sole evidence
            // a transition occurred; both wait methods below drive off observed game/Lifestream state.
            await Task.Delay(1000, token).ConfigureAwait(false);

            var outcome = crossDataCenter
                ? await WaitForCrossDataCenterTransferAsync(targetWorld, token).ConfigureAwait(false)
                : await WaitForSameDataCenterTransferAsync(targetWorld, token).ConfigureAwait(false);

            if (outcome.Result == ShoutRunnerTransferResult.WorldCongestedSkip)
            {
                TryLifestreamAbort();
                await DismissTransferUiAsync(token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            else if (outcome.Result == ShoutRunnerTransferResult.Failed)
            {
                TryLifestreamAbort();
                await DismissTransferUiAsync(token).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            EndTransferMonitoring();
        }
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

    private async Task<bool> WaitForLifestreamReadyAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + LifestreamBusyBeforeStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!TryLifestreamIsBusy(out var busy)) return false;
            if (!busy) return true;
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<ShoutRunnerTransferOutcome> WaitForSameDataCenterTransferAsync(string targetWorld, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + SameDataCenterTimeout;
        var startDeadline = DateTime.UtcNow + SameDataCenterStartWindow;
        var seenTransition = false;

        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer()) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.");
            if (TryLifestreamIsBusy(out var busy) && busy) break;
            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (state.BetweenAreas || state.BetweenAreas51) { seenTransition = true; break; }
            if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase)) return ShoutRunnerTransferOutcome.Success();
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer()) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.");

            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            var transitioning = state.BetweenAreas || state.BetweenAreas51;
            if (transitioning) seenTransition = true;

            if (!transitioning && seenTransition && state.IsLoggedIn && state.HasLocalPlayer)
            {
                if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryLifestreamIsBusy(out var busy) && !busy) return ShoutRunnerTransferOutcome.Success();
                }
                else if (!string.IsNullOrEmpty(state.CurrentWorld))
                {
                    return ShoutRunnerTransferOutcome.Failed($"Arrived at {state.CurrentWorld} instead of {targetWorld}.");
                }
            }

            await Task.Delay(500, token).ConfigureAwait(false);
        }

        return ShoutRunnerTransferOutcome.Failed($"Same-Data-Center transfer to {targetWorld} timed out after {SameDataCenterTimeout.TotalMinutes:0} minutes.");
    }

    /// <summary>The entire-Data-Center-unavailable path (familiarization report §12) — reproduced with the "stuck at
    /// the login screen" idle-while-logged-out detector, but this version performs the full recovery sequence
    /// (abort, dismiss, confirm playable) itself before ever reporting a skip, satisfying the hard route invariant
    /// that nothing continues while the character is still at character-select/login limbo.</summary>
    private async Task<ShoutRunnerTransferOutcome> WaitForCrossDataCenterTransferAsync(string targetWorld, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + CrossDataCenterTimeout;
        var startDeadline = DateTime.UtcNow + CrossDataCenterStartWindow;
        var seenLogout = false;
        DateTime? loggedOutWhileIdleSince = null;

        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer()) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.");
            if (TryLifestreamIsBusy(out var busy) && busy) break;
            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (!state.IsLoggedIn) { seenLogout = true; break; }
            if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase)) return ShoutRunnerTransferOutcome.Success();
            await Task.Delay(500, token).ConfigureAwait(false);
        }

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (ShouldSkipCurrentTransfer()) return ShoutRunnerTransferOutcome.WorldCongested("Congestion reported twice.");

            var state = await GetGameStateAsync(token).ConfigureAwait(false);
            if (!state.IsLoggedIn)
            {
                seenLogout = true;
                if (TryLifestreamIsBusy(out var busy) && !busy)
                {
                    loggedOutWhileIdleSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - loggedOutWhileIdleSince.Value >= LoggedOutIdleStuckThreshold)
                    {
                        TryLifestreamAbort();
                        await DismissTransferUiAsync(token).ConfigureAwait(false);
                        var recovered = await WaitForRecoveryAsync(token).ConfigureAwait(false);
                        return recovered
                            ? ShoutRunnerTransferOutcome.DataCenterUnavailable($"Transfer to {targetWorld} stopped at the login screen; the destination data center is likely congested.")
                            : ShoutRunnerTransferOutcome.Failed($"Transfer to {targetWorld} stopped at the login screen and the character could not be recovered to a playable state.");
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
            if (seenLogout && state.HasLocalPlayer)
            {
                if (string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryLifestreamIsBusy(out var busy) && !busy) return ShoutRunnerTransferOutcome.Success();
                }
                else if (!string.IsNullOrEmpty(state.CurrentWorld) && TryLifestreamIsBusy(out var busyNow) && !busyNow)
                {
                    return ShoutRunnerTransferOutcome.Failed($"Arrived at {state.CurrentWorld} instead of {targetWorld}.");
                }
            }
            else if (!seenLogout && state.HasLocalPlayer && string.Equals(state.CurrentWorld, targetWorld, StringComparison.OrdinalIgnoreCase))
            {
                return ShoutRunnerTransferOutcome.Success();
            }

            await Task.Delay(1000, token).ConfigureAwait(false);
        }

        return ShoutRunnerTransferOutcome.Failed($"Cross-Data-Center transfer to {targetWorld} timed out after {CrossDataCenterTimeout.TotalMinutes:0} minutes.");
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

    private void BeginTransferMonitoring(string targetWorld)
    {
        lock (transferMonitorLock)
        {
            monitoredTransferWorld = targetWorld;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    private void EndTransferMonitoring()
    {
        lock (transferMonitorLock)
        {
            monitoredTransferWorld = null;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    private bool ShouldSkipCurrentTransfer()
    {
        lock (transferMonitorLock) return monitoredTransferShouldSkip;
    }

    private void OnChatMessageUnhandled(IChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text) || !IsCongestedTransferMessage(text)) return;

        lock (transferMonitorLock)
        {
            if (monitoredTransferWorld is null || monitoredTransferShouldSkip) return;
            monitoredTransferCongestionCount++;
            if (monitoredTransferCongestionCount >= 2)
            {
                monitoredTransferShouldSkip = true;
                TryLifestreamAbort();
            }
        }
    }

    private static bool IsCongestedTransferMessage(string text) => CongestionMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // ----- Lifestream IPC -----

    private bool TryLifestreamIsBusy(out bool busy)
    {
        try { busy = lifestreamIsBusy.InvokeFunc(); return true; }
        catch (IpcNotReadyError) { busy = false; return false; }
        catch (Exception ex) { log.Debug(ex, "ShoutRunner: Lifestream.IsBusy failed."); busy = false; return false; }
    }

    private void TryLifestreamAbort()
    {
        try { lifestreamAbort.InvokeAction(); }
        catch { /* best-effort */ }
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

            WindowsKeypress.SendKeypress(EscapeVirtualKey);
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

    private sealed record GameState(bool IsLoggedIn, bool HasLocalPlayer, bool BetweenAreas, bool BetweenAreas51, bool LoggingOut,
        bool OccupiedInCutSceneEvent, bool OccupiedInQuestEvent, bool OccupiedInEvent, bool Occupied, bool WatchingCutscene, string CurrentWorld);

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
                    localPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }
}
