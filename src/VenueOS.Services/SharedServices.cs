using System.Collections.Concurrent;
using System.Numerics;

namespace VenueOS.Services;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public interface IFrameworkDispatcher { Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken); }
public sealed class InlineFrameworkDispatcher : IFrameworkDispatcher { public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action()); }

public sealed class SchedulerService(IClock clock)
{
    private readonly List<ScheduledWork> work = [];
    public Guid Schedule(TimeSpan delay, Action action, TimeSpan? repeat = null, CancellationToken cancellationToken = default)
    { var entry = new ScheduledWork(Guid.NewGuid(), clock.UtcNow + delay, action, repeat, cancellationToken); work.Add(entry); return entry.Id; }
    public void Cancel(Guid id) => work.RemoveAll(x => x.Id == id);
    public void Tick()
    {
        foreach (var entry in work.Where(x => x.Due <= clock.UtcNow).ToArray())
        {
            if (entry.Token.IsCancellationRequested) { work.Remove(entry); continue; }
            entry.Action(); if (entry.Repeat is { } repeat) entry.Due = clock.UtcNow + repeat; else work.Remove(entry);
        }
    }
    private sealed class ScheduledWork(Guid id, DateTimeOffset due, Action action, TimeSpan? repeat, CancellationToken token) { public Guid Id { get; } = id; public DateTimeOffset Due { get; set; } = due; public Action Action { get; } = action; public TimeSpan? Repeat { get; } = repeat; public CancellationToken Token { get; } = token; }
}

/// <summary><paramref name="OnDispatched"/> is the per-command correlation hook a caller needs to know whether
/// *this specific* command actually made it to the transport successfully — the shared <see cref="ChatCommandService.Completed"/>
/// event is fire-and-forget/diagnostic-only and was never enough on its own to let a sequential sender (Greeter's
/// Line 1→2→3→4, VIP's recognition tell) safely gate its next step, or a final "greeted" flag, on real success.</summary>
public sealed record ChatCommand(string Text, CancellationToken CancellationToken = default, Action<bool, Exception?>? OnDispatched = null);
public sealed record ChatDispatchResult(ChatCommand Command, bool Success, Exception? Error = null);
public sealed class ChatCommandService(IClock clock, IFrameworkDispatcher dispatcher, Func<string, bool> execute, TimeSpan? minimumInterval = null)
{
    private readonly ConcurrentQueue<ChatCommand> queue = new(); private DateTimeOffset nextAllowed;
    private readonly TimeSpan interval = minimumInterval ?? TimeSpan.FromSeconds(1);
    public event Action<ChatDispatchResult>? Completed;
    public void Enqueue(ChatCommand command) { if (!string.IsNullOrWhiteSpace(command.Text)) queue.Enqueue(command); }
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (clock.UtcNow < nextAllowed || !queue.TryDequeue(out var command)) return;
        if (command.CancellationToken.IsCancellationRequested) { var cancelled = new OperationCanceledException(); Completed?.Invoke(new(command, false, cancelled)); command.OnDispatched?.Invoke(false, cancelled); return; }
        try
        {
            var success = await dispatcher.InvokeAsync(() => execute(command.Text), cancellationToken).ConfigureAwait(false);
            Completed?.Invoke(new(command, success));
            command.OnDispatched?.Invoke(success, success ? null : new InvalidOperationException("Chat transport reported the command was not sent."));
        }
        catch (Exception ex) { Completed?.Invoke(new(command, false, ex)); command.OnDispatched?.Invoke(false, ex); }
        finally { nextAllowed = clock.UtcNow + interval; }
    }
}

public sealed record GuestIdentity(string Name, string HomeWorld)
{
    public string NormalizedName { get; } = Normalize(Name); public string NormalizedHomeWorld { get; } = Normalize(HomeWorld);
    public string Key => $"{NormalizedName}@{NormalizedHomeWorld}";
    private static string Normalize(string value) => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
public sealed record PlayerSnapshot(string Name, string HomeWorld, ulong ObjectId, uint TerritoryId, Vector3 Position);

/// <summary>Where a distance filter's center comes from — <see cref="FollowOperator"/> re-reads the operator's live
/// position every scan (appropriate for an indoor housing instance the operator stands still-ish inside),
/// <see cref="FixedPoint"/> uses a point captured once, e.g. when a session opened (appropriate for an open-world
/// venue the operator might walk around during).</summary>
public enum PresenceAreaMode { FollowOperator, FixedPoint }

public sealed record PresencePolicy(uint? LockedTerritoryId, Vector3? Center, float? RadiusYalms, PresenceAreaMode AreaMode = PresenceAreaMode.FixedPoint)
{
    public bool Includes(PlayerSnapshot player, Vector3? operatorPosition = null)
    {
        if (LockedTerritoryId.HasValue && player.TerritoryId != LockedTerritoryId) return false;
        if (!RadiusYalms.HasValue) return true;
        var center = AreaMode == PresenceAreaMode.FollowOperator ? operatorPosition : Center;
        return center is null || Vector3.Distance(center.Value, player.Position) <= RadiusYalms.Value;
    }
}
public interface IObjectSnapshotProvider { IReadOnlyList<PlayerSnapshot> Snapshot(); }
public sealed class PresenceService(IClock clock, IObjectSnapshotProvider provider, TimeSpan? scanInterval = null)
{
    private readonly TimeSpan interval = scanInterval ?? TimeSpan.FromSeconds(1); private readonly Dictionary<string, PlayerSnapshot> current = new(StringComparer.Ordinal); private DateTimeOffset nextScan;
    private bool seedNextScan;
    /// <summary>The third parameter, <c>isSessionSeed</c>, is true only for the one scan immediately following
    /// <see cref="ResetKnownPresence"/> — i.e. everyone already standing in the venue when an opening starts/resumes,
    /// as opposed to a guest who genuinely walks in afterward. Subscribers that drive automatic greeting
    /// (<c>AttendanceService.Arrive</c>, which raises <c>AutomaticGreetingEligible</c>) must treat this population as
    /// initialization, not an arrival event — see <see cref="ResetKnownPresence"/>'s doc comment for why.</summary>
    public event Action<GuestIdentity, PlayerSnapshot, bool>? Arrived; public event Action<GuestIdentity, PlayerSnapshot>? Departed;
    public IReadOnlyCollection<PlayerSnapshot> Current => current.Values;
    public bool IsPresent(GuestIdentity guest) => current.ContainsKey(guest.Key);
    /// <summary>Forgets who's currently known to be present, with no <see cref="Departed"/> events — the next
    /// <see cref="Tick"/> then sees every currently-observed player as new-since-empty and fires <see cref="Arrived"/>
    /// for each of them, with <c>isSessionSeed: true</c>. Used by Attendance's session Start/Resume: this service
    /// scans continuously regardless of whether a session is open (VIP needs that), unlike the donor's tracker, which
    /// only ever scans while its venue is open — so the donor's "opening naturally re-discovers everyone already
    /// inside" behavior has to be produced here explicitly instead of falling out for free. Live-verified correction:
    /// "re-discovering" existing occupants must seed them into Attendance as Not Greeted, not silently run them
    /// through automatic greeting/VIP recognition — the <c>isSessionSeed</c> flag is what lets subscribers make that
    /// distinction instead of treating this batch identically to a real new arrival.</summary>
    public void ResetKnownPresence() { current.Clear(); seedNextScan = true; }
    public void Tick(PresencePolicy policy, Vector3? operatorPosition = null)
    {
        if (clock.UtcNow < nextScan) return; nextScan = clock.UtcNow + interval;
        var isSessionSeed = seedNextScan; seedNextScan = false;
        var observed = provider.Snapshot().Where(x => policy.Includes(x, operatorPosition)).GroupBy(x => new GuestIdentity(x.Name, x.HomeWorld).Key).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var pair in observed.Where(x => !current.ContainsKey(x.Key))) Arrived?.Invoke(new GuestIdentity(pair.Value.Name, pair.Value.HomeWorld), pair.Value, isSessionSeed);
        foreach (var pair in current.Where(x => !observed.ContainsKey(x.Key)).ToArray()) Departed?.Invoke(new GuestIdentity(pair.Value.Name, pair.Value.HomeWorld), pair.Value);
        current.Clear(); foreach (var pair in observed) current[pair.Key] = pair.Value;
    }
}

public sealed record Toast(string Message, ToastLevel Level, DateTimeOffset CreatedAt);
public enum ToastLevel { Information, Success, Warning, Error }
public sealed class NotificationService(IClock clock) { private readonly List<Toast> toasts = []; public IReadOnlyList<Toast> Toasts => toasts; public void Push(string message, ToastLevel level = ToastLevel.Information) => toasts.Add(new(message, level, clock.UtcNow)); }
public sealed class GameContextService { public bool IsLoggedIn { get; private set; } public uint TerritoryId { get; private set; } public void Update(bool isLoggedIn, uint territoryId) { IsLoggedIn = isLoggedIn; TerritoryId = territoryId; } }
public sealed class VenueHttpClientFactory { public HttpClient Create(Uri baseAddress, TimeSpan timeout) => new() { BaseAddress = baseAddress, Timeout = timeout }; }

/// <summary>Result of trying to read identity off whatever the operator currently has targeted — used by VIP's
/// "Use Current Target" autofill. Carries a human-readable <see cref="Error"/> instead of just failing silently,
/// so the UI can tell the operator exactly why (no target / not a player / world unavailable).</summary>
public sealed record TargetedPlayerLookup(bool Success, string Name, string HomeWorld, string? Error)
{
    public static TargetedPlayerLookup Failed(string error) => new(false, "", "", error);
    public static TargetedPlayerLookup Found(string name, string homeWorld) => new(true, name, homeWorld, null);
}
public interface ITargetedPlayerProvider { TargetedPlayerLookup GetTargetedPlayer(); }
