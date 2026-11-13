namespace VenueOS.Modules.Operations.Travel;

/// <summary>The game boundary for native travel. The real implementation (<c>VenueOS.Plugin.Travel.DalamudNativeTravelPort</c>)
/// does every read and every UI action on the Dalamud framework thread. Tests use a simulated World Visit window.</summary>
public interface INativeTravelGamePort
{
    /// <summary>One consistent framework-thread read of the game.</summary>
    Task<NativeTravelSnapshot> SnapshotAsync(CancellationToken token);

    /// <summary>In ONE framework callback: take a fresh snapshot, call <paramref name="revalidate"/> on it, and only if
    /// that returns true perform <paramref name="action"/>. Returns whether the action was performed.</summary>
    Task<bool> ExecuteAsync(NativeTravelAction action, Func<NativeTravelSnapshot, bool> revalidate, CancellationToken token);

    /// <summary>A non-blocking wait (Task.Delay in production; advances simulated time in tests).</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken token);

    DateTimeOffset UtcNow { get; }

    /// <summary>Game text for the menu option and the confirmation templates.</summary>
    NativeTravelTexts Texts { get; }

    /// <summary>Starts or stops counting congestion chat messages for <paramref name="operationId"/>.</summary>
    void BeginOperation(int operationId);
    void EndOperation(int operationId);

    /// <summary>A read-only text description of the travel UI (the World Visit window's nodes, components, texts and
    /// click-event owners, the dialogs and the agent). Never clicks. Captured automatically when an operation stops on
    /// an unexpected UI, before the window is closed, so a single failed live attempt documents the real structure.</summary>
    Task<string> DescribeUiAsync();
}

/// <summary>The reusable native World Visit / Data Center travel service (0.3.14), independent of ShoutRunner and of
/// Lifestream. It owns at most ONE operation per client (a second request returns <see cref="NativeTravelOutcome.Busy"/>),
/// gives every operation an id, and drives a fresh <see cref="NativeWorldVisitDriver"/> through the
/// <see cref="INativeTravelGamePort"/>: snapshot → decide → revalidate-and-act → non-blocking poll delay. A cancelled
/// token ends the loop at the next step. Before the confirmation, the UI VenueOS opened is closed; nothing runs after
/// cancellation except that close.</summary>
public sealed class NativeTravelService(INativeTravelGamePort port, ITravelWorldCatalog catalog)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>A World whose selection opens no confirmation is skipped as unavailable. When this many operations in a row
    /// end that way, the last one is reported as <see cref="NativeTravelOutcome.UnexpectedUi"/> instead: selecting Worlds
    /// may no longer work at all, and a run must not silently skip every World.</summary>
    public const int MaxConsecutiveUnacceptedSelections = 2;

    private int consecutiveUnacceptedSelections;

    private readonly object gate = new();
    private int sequence;
    private ActiveOperation? active;
    private readonly List<string> recentLog = [];
    private NativeTravelResult? lastResult;

    private sealed class ActiveOperation(int id, NativeTravelRequest request, CancellationTokenSource cts, DateTimeOffset started)
    {
        public int Id { get; } = id;
        public NativeTravelRequest Request { get; } = request;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTimeOffset Started { get; } = started;
        public NativeWorldVisitDriver? Driver { get; set; }
        public string? CancelReason { get; set; }
    }

    /// <summary>Session-only QA switch for the unverified Data Center dropdown interaction (never persisted). Off by
    /// default: a World on another logical Data Center then finishes as <see cref="NativeTravelOutcome.Unsupported"/>.</summary>
    public bool AllowDataCenterSelection { get; set; }

    public NativeTravelStatus Status
    {
        get
        {
            lock (gate)
            {
                if (active is null) return NativeTravelStatus.Idle;
                var driver = active.Driver;
                var detail = driver?.Trace.Count > 0 ? driver.Trace[^1] : "starting";
                return new(active.Id, active.Request.Owner, active.Request.TargetWorld, driver?.Phase ?? NativeTravelPhase.ResolvingDestination, detail, active.Started);
            }
        }
    }

    public bool IsBusy { get { lock (gate) return active is not null; } }

    public NativeTravelResult? LastResult { get { lock (gate) return lastResult; } }

    /// <summary>The last operations' summaries plus the live trace of the active one.</summary>
    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (gate)
            {
                var lines = new List<string>(recentLog);
                if (active?.Driver is { } d) lines.AddRange(d.Trace.Select(t => $"#{active.Id} {t}"));
                return lines;
            }
        }
    }

    /// <summary>Read-only inspection: one snapshot, no action. Used by the QA panel's Inspect button.</summary>
    public Task<NativeTravelSnapshot> InspectAsync(CancellationToken token) => port.SnapshotAsync(token);

    /// <summary>Asks the active operation (if any) to stop. Safe from any thread; the operation's own loop does the
    /// cleanup on its next step.</summary>
    public void CancelActive(string reason)
    {
        lock (gate)
        {
            if (active is null) return;
            active.CancelReason ??= reason;
            try { active.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task<NativeTravelResult> TravelAsync(NativeTravelRequest request, CancellationToken token)
    {
        ActiveOperation op;
        lock (gate)
        {
            if (active is not null)
                return new(0, NativeTravelOutcome.Busy, $"another travel operation (#{active.Id}, {active.Request.Owner} → {active.Request.TargetWorld}) is running", null, null, null, string.Empty);
            op = new ActiveOperation(++sequence, request, CancellationTokenSource.CreateLinkedTokenSource(token), port.UtcNow);
            active = op;
        }

        port.BeginOperation(op.Id);
        try
        {
            var result = await RunAsync(op).ConfigureAwait(false);
            lock (gate)
            {
                lastResult = result;
                recentLog.Add($"#{result.OperationId} {request.Owner} → {request.TargetWorld}: {result.Outcome} — {result.Detail}");
                while (recentLog.Count > 40) recentLog.RemoveAt(0);
            }
            return result;
        }
        finally
        {
            port.EndOperation(op.Id);
            lock (gate)
            {
                if (ReferenceEquals(active, op)) active = null;
                op.Cts.Dispose();
            }
        }
    }

    private async Task<NativeTravelResult> RunAsync(ActiveOperation op)
    {
        var target = catalog.Resolve(op.Request.TargetWorld);
        if (target is null)
            return new(op.Id, NativeTravelOutcome.Unsupported, $"{op.Request.TargetWorld} is not a public World in the game data", null, null, null, string.Empty);

        var regionDataCenters = RegionDataCenters(target.RegionId);
        var driver = new NativeWorldVisitDriver(target, op.Request.PreferredCities, port.Texts, regionDataCenters, AllowDataCenterSelection, port.UtcNow);
        lock (gate) op.Driver = driver;
        var token = op.Cts.Token;
        string? uiCapture = null;

        try
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    await RunFinalActionAsync(driver.Cancel(op.CancelReason ?? "cancelled", port.UtcNow), driver).ConfigureAwait(false);
                    break;
                }

                var snapshot = await port.SnapshotAsync(token).ConfigureAwait(false);
                var action = driver.Next(snapshot, port.UtcNow);
                if (action.Kind == NativeTravelActionKind.Finish) break;
                if (action.Kind != NativeTravelActionKind.Wait)
                {
                    if (action.Kind == NativeTravelActionKind.CloseTravelUi && (driver.Outcome is NativeTravelOutcome.UnexpectedUi or NativeTravelOutcome.SelectionMismatch || driver.SelectionNotAccepted))
                        uiCapture = await CaptureUiAsync().ConfigureAwait(false);
                    var performed = await port.ExecuteAsync(action, fresh => driver.Revalidate(action, fresh), token).ConfigureAwait(false);
                    driver.Executed(action, performed, port.UtcNow);
                    if (driver.Outcome is not null) break;   // the action was the final cleanup
                }

                await port.DelayAsync(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await RunFinalActionAsync(driver.Cancel(op.CancelReason ?? "cancelled", port.UtcNow), driver).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A port fault (game state read failed) ends the operation as Failed; it never propagates.
            await RunFinalActionAsync(driver.Cancel($"travel engine error: {ex.Message}", port.UtcNow), driver).ConfigureAwait(false);
            return Result(op, driver, NativeTravelOutcome.Failed, $"travel engine error: {ex.Message}", uiCapture);
        }

        var outcome = driver.Outcome ?? NativeTravelOutcome.Failed;
        var detail = driver.OutcomeDetail;
        lock (gate)
        {
            if (driver.SelectionNotAccepted)
            {
                if (++consecutiveUnacceptedSelections >= MaxConsecutiveUnacceptedSelections)
                {
                    outcome = NativeTravelOutcome.UnexpectedUi;
                    detail += $"; {consecutiveUnacceptedSelections} World selections in a row opened no confirmation, so travel stops in case selecting Worlds no longer works";
                }
            }
            else if (driver.TravelConfirmed)
            {
                consecutiveUnacceptedSelections = 0;
            }
        }

        return Result(op, driver, outcome, detail, uiCapture);
    }

    /// <summary>Maximum length of the automatic UI capture kept in a result's diagnostic.</summary>
    public const int MaxUiCaptureLength = 12_000;

    /// <summary>Best-effort, read-only: a failing capture never affects the operation.</summary>
    private async Task<string?> CaptureUiAsync()
    {
        try
        {
            var text = await port.DescribeUiAsync().ConfigureAwait(false);
            return text.Length <= MaxUiCaptureLength ? text : text[..MaxUiCaptureLength] + Environment.NewLine + "… (truncated)";
        }
        catch (Exception ex)
        {
            return $"(UI capture failed: {ex.Message})";
        }
    }

    /// <summary>The cleanup after cancellation (closing VenueOS's own menus) runs without the cancelled token, but still
    /// revalidates against a fresh snapshot.</summary>
    private async Task RunFinalActionAsync(NativeTravelAction action, NativeWorldVisitDriver driver)
    {
        if (action.Kind is NativeTravelActionKind.Finish or NativeTravelActionKind.Wait) return;
        try
        {
            var performed = await port.ExecuteAsync(action, fresh => driver.Revalidate(action, fresh), CancellationToken.None).ConfigureAwait(false);
            driver.Executed(action, performed, port.UtcNow);
        }
        catch (Exception) { /* best-effort cleanup */ }
    }

    private static NativeTravelResult Result(ActiveOperation op, NativeWorldVisitDriver driver, NativeTravelOutcome outcome, string detail, string? uiCapture) =>
        new(op.Id, outcome, detail, driver.Target, driver.Origin, driver.LastObserved,
            $"native travel #{op.Id} ({op.Request.Owner}) → {NativeWorldVisitDriver.Describe(driver.Target)}: {outcome} — {detail}{Environment.NewLine}{string.Join(Environment.NewLine, driver.Trace)}"
            + (uiCapture is null ? string.Empty : $"{Environment.NewLine}— Travel UI at the moment it stopped (read-only capture, before closing) —{Environment.NewLine}{uiCapture}"));

    private IReadOnlyCollection<string> RegionDataCenters(uint regionId) => catalog.LogicalDataCentersInRegion(regionId);
}
