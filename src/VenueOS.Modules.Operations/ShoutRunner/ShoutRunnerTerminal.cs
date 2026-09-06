namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The runtime, session-only route history — see the reconstruction brief's "RUNTIME TERMINAL"/"TERMINAL
/// RETENTION" sections. Deliberately in-memory only for this pass: it is owned by <see cref="ShoutRunnerService"/>
/// (a plain field, not persisted config), survives the module's window closing/reopening and embedded/detached
/// switches (because it's service state, not UI state), is cleared only when the active venue changes (never one
/// venue's route history bleeding into another's) or the plugin/game reloads (a fresh <see cref="ShoutRunnerService"/>
/// instance). See the familiarization report §27 for why persisting this further (per-venue-opening or historically)
/// was deliberately deferred rather than decided here.
///
/// Bounded at <see cref="MaxEntries"/> — comfortably covers a full venue night's worth of structured events (the
/// brief's own estimate: 6-8 RUNs × up to 4 Data Centers × 8 Worlds each × several destinations each, which is at
/// most a few thousand individual events even at the high end) without unbounded memory growth. The oldest entries
/// are dropped first once the cap is reached, exactly like <see cref="VenueOS.Services.DiagnosticsService"/>'s own
/// bounded error queue.</summary>
public sealed class ShoutRunnerTerminal
{
    public const int MaxEntries = 4000;

    private readonly Queue<ShoutRunnerTerminalEvent> events = new();

    public IReadOnlyList<ShoutRunnerTerminalEvent> Events => events.ToArray();

    public void Add(ShoutRunnerTerminalEvent entry)
    {
        if (events.Count >= MaxEntries) events.Dequeue();
        events.Enqueue(entry);
    }

    public void Clear() => events.Clear();
}
