using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Plugin.ShoutRunner;

/// <summary>ShoutRunner's game-facing engine. Since 0.3.14 every World transfer is NATIVE World Visit through the shared
/// <see cref="NativeTravelService"/> (aetheryte → Visit Another World Server → Patch 7.58 Data Center dropdown → World row
/// → verified confirmation → stable arrival). This class no longer calls Lifestream in any way: no <c>/li</c>, no IPC, no
/// busy polling, no abort. It keeps its other responsibilities from the donor port, hardened as before:
/// <list type="bullet">
/// <item><see cref="EnsureReadyAsync"/>: the one shared, bounded readiness/recovery gate.</item>
/// <item><see cref="TeleportToDestinationAsync"/>: native <c>Telepo</c> teleport between destinations, never trusting the
/// bare return value as arrival.</item>
/// <item><see cref="TryGetCurrentLocationAsync"/>: the pre-shout guard's World and REAL logical Data Center
/// (<see cref="ITravelWorldCatalog"/>, from <c>World.Unknown0</c>).</item>
/// </list>
/// Lives in <c>VenueOS.Plugin</c> because of unsafe FFXIVClientStructs access and Dalamud services
/// (<c>NEW_MODULE_GUIDE.md</c> §30). <see cref="ShoutRunnerService"/> depends only on <see cref="IShoutRunnerAutomation"/>.</summary>
public sealed class ShoutRunnerAutomationService : IShoutRunnerAutomation, IDisposable
{
    private const int EscapeVirtualKey = 0x1B;

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoggedOutRecoveryGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TeleportReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TeleportTransitionTimeout = TimeSpan.FromSeconds(90);

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly NativeTravelService travel;
    private readonly ITravelWorldCatalog worlds;
    private readonly Func<IReadOnlyList<string>> routeDestinations;

    private readonly Dictionary<uint, string> aetheryteNames = new();
    private readonly Dictionary<uint, string> territoryNames = new();
    private bool teleportDataLoaded;

    /// <param name="routeDestinations">The current venue's configured destinations, used to pick the World Visit city: the
    /// next World's route then starts where the character arrives.</param>
    public ShoutRunnerAutomationService(IClientState clientState, IObjectTable objectTable, ICondition condition, IDataManager dataManager, IFramework framework, NativeTravelService travel, ITravelWorldCatalog worlds, Func<IReadOnlyList<string>> routeDestinations, IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.framework = framework;
        this.travel = travel;
        this.worlds = worlds;
        this.routeDestinations = routeDestinations;
        this.log = log;
    }

    public void Dispose() { }

    /// <summary>Nothing per-venue to reset since 0.3.14: native travel state is per operation.</summary>
    public void ResetForVenue() { }

    /// <summary>Must return quickly: cancels ShoutRunner's native travel operation (the travel service closes the World
    /// Visit menus it opened on its own loop) and makes one UI-dismissal attempt. Never touches a QA travel test.</summary>
    public void Abort()
    {
        if (travel.Status.Owner == NativeTravelOwner.ShoutRunner) travel.CancelActive("ShoutRunner stopped");
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
                // A character left logged out by any cause gets one bounded recovery attempt (familiarization report
                // §13). Since 0.3.14 there is no Lifestream to abort: the attempt dismisses travel UI only.
                if (!recoveryAttempted && DateTime.UtcNow - loggedOutSince.Value >= LoggedOutRecoveryGrace)
                {
                    recoveryAttempted = true;
                    if (travel.Status.Owner == NativeTravelOwner.ShoutRunner) travel.CancelActive("ShoutRunner readiness recovery");
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

    /// <summary>0.3.14: from game data only (logical Data Center from <c>World.Unknown0</c>, region, Home World region).</summary>
    public async Task<ShoutRunnerCrossDataCenterCheck> ClassifyTransferAsync(string targetWorld, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (current, home) = await OnFrameworkAsync(() =>
        {
            var player = objectTable.LocalPlayer;
            return player is null ? (null, null) : (worlds.ResolveById(player.CurrentWorld.RowId), worlds.ResolveById(player.HomeWorld.RowId));
        }, token).ConfigureAwait(false);
        var target = worlds.Resolve(targetWorld);
        var catalogDc = ShoutRunnerCatalog.DataCenterFor(targetWorld);
        var check = ShoutRunnerTravelClassification.Classify(current, home, target, catalogDc);
        var line = $"ShoutRunner: travel to {targetWorld} classified {check}: current {NativeWorldVisitDriver.Describe(current)}, home {NativeWorldVisitDriver.Describe(home)}, target {NativeWorldVisitDriver.Describe(target)}, catalog DC {catalogDc ?? "(none)"}.";
        if (check is ShoutRunnerCrossDataCenterCheck.SameDataCenter or ShoutRunnerCrossDataCenterCheck.CrossDataCenter) log.Information(line); else log.Warning(line);
        return check;
    }

    /// <summary>Native World Visit through <see cref="NativeTravelService"/>, mapped onto route outcomes by
    /// <see cref="ShoutRunnerNativeTravelMapping"/>. The World Visit city is the first of the route's destinations that is
    /// one of the three World Visit cities (so the next World's route can begin where the character arrives).</summary>
    public async Task<ShoutRunnerTransferOutcome> TravelToWorldAsync(string targetWorld, bool crossDataCenter, CancellationToken token)
    {
        var cities = routeDestinations().Select(WorldVisitCities.ForName).OfType<WorldVisitCity>().Distinct().ToList();
        var result = await travel.TravelAsync(new NativeTravelRequest(targetWorld, NativeTravelOwner.ShoutRunner, cities), token).ConfigureAwait(false);
        var line = $"ShoutRunner native travel #{result.OperationId} → {targetWorld} ({(crossDataCenter ? "other Data Center" : "same Data Center")}): {result.Outcome} — {result.Detail}";
        if (result.Outcome is NativeTravelOutcome.Arrived or NativeTravelOutcome.AlreadyThere) log.Information(line); else log.Warning(line + Environment.NewLine + result.Diagnostic);
        return ShoutRunnerNativeTravelMapping.ToTransferOutcome(result);
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

    /// <summary>The pre-shout guard's location: World name and its REAL logical Data Center (0.3.14: from the catalog's
    /// <c>World.Unknown0</c>, not <c>World.DataCenter</c>, which the 7.58 game data merges for every NA World).</summary>
    public Task<ShoutRunnerObservedLocation?> TryGetCurrentLocationAsync(CancellationToken token) => OnFrameworkAsync(() =>
    {
        var id = objectTable.LocalPlayer?.CurrentWorld.RowId ?? 0;
        if (id == 0) return null;
        var world = worlds.ResolveById(id);
        if (world is not null) return new ShoutRunnerObservedLocation(world.Name, world.LogicalDataCenter);
        var name = objectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText().Trim();
        return string.IsNullOrWhiteSpace(name) ? null : new ShoutRunnerObservedLocation(name, null);
    }, token);

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

    /// <summary>One framework-thread snapshot used by the readiness gate.</summary>
    private sealed record GameState(bool IsLoggedIn, bool HasLocalPlayer, bool BetweenAreas, bool BetweenAreas51, bool LoggingOut,
        bool OccupiedInCutSceneEvent, bool OccupiedInQuestEvent, bool OccupiedInEvent, bool Occupied, bool WatchingCutscene, string CurrentWorld,
        uint CurrentWorldId);

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
                    localPlayer?.CurrentWorld.RowId ?? 0));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }
}
