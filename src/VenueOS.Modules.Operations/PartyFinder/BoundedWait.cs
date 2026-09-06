namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>Pure bounded-wait tracker for a native-UI readiness poll. The actual game/UI state being waited for is
/// always checked by the caller (see <c>PartyFinderAutomationService</c> in <c>VenueOS.Plugin</c>) — this struct
/// only answers "how long has this particular wait been running, and have I exceeded my allotted grace window,"
/// which keeps that timing/retry decision unit-testable without any FFXIVClientStructs/Dalamud dependency.
///
/// A caller owns one instance per distinct wait (typically a field reset at the start of each new operation, the
/// same convention <c>PartyFinderAutomationService</c> already used for its raw <see cref="DateTime"/> timer
/// fields). Call <see cref="Poll"/> once per tick from inside the readiness check; call <see cref="Reset"/> whenever
/// a fresh operation begins so the next wait starts its own clean window.</summary>
public struct BoundedWait
{
    private DateTime startedUtc;

    /// <summary>Starts the timer on first call. Returns true while still within <paramref name="timeout"/> of that
    /// first call (keep polling), false once it has elapsed (the caller should report a bounded, stage-specific
    /// failure rather than waiting indefinitely).</summary>
    public bool Poll(DateTime nowUtc, TimeSpan timeout)
    {
        if (startedUtc == default)
        {
            startedUtc = nowUtc;
        }

        return nowUtc - startedUtc < timeout;
    }

    /// <summary>Clears the tracker so the next <see cref="Poll"/> call starts an entirely fresh window — call this
    /// whenever a new operation begins (a fresh Create/Refresh/End Party Finder request), never mid-wait.</summary>
    public void Reset() => startedUtc = default;

    public readonly bool HasStarted => startedUtc != default;
}
