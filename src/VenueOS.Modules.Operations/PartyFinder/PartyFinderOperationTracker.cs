using System.Text;

namespace VenueOS.Modules.Operations.PartyFinder;

public enum PartyFinderOperationKind
{
    /// <summary>Recruitment is being started — no own listing exists yet.</summary>
    Create,

    /// <summary>An existing own listing is being edited/refreshed (manual Refresh, Edit / Apply Changes, or the
    /// automatic 5-minute-warning refresh). All three run the identical native chain.</summary>
    Update,

    /// <summary>End Party Finder's withdrawal chain.</summary>
    End,
}

public enum PartyFinderOperationOutcome
{
    Completed,

    /// <summary>A real automation defect — reported to Diagnostics.</summary>
    Failed,

    /// <summary>An expected precondition-not-met stop (not logged in, zoning, no listing) — never a Diagnostics
    /// failure (NEW_MODULE_GUIDE.md §24a).</summary>
    Stopped,

    /// <summary>Operator Abort, venue switch, or being superseded by End Party Finder.</summary>
    Aborted,

    /// <summary>The native task chain terminated without the engine finishing the operation itself (an ECommons
    /// <c>TaskManager</c> per-task timeout or an exception inside a step — both abort the chain silently). Without
    /// detecting this the engine's ownership flag would stay "running" forever and the 0.3.7 single-active-refresh
    /// guard would then reject every later request.</summary>
    ChainLost,
}

/// <summary>One Party Finder operation's identity and timeline. Immutable once finished (see
/// <see cref="PartyFinderOperationRecord"/>).</summary>
public sealed class PartyFinderOperation
{
    private readonly List<(string Name, DateTime AtUtc)> milestones = [];

    internal PartyFinderOperation(long id, PartyFinderOperationKind kind, string reason, DateTime startedUtc, bool isFirstUpdateForListing)
    {
        Id = id;
        Kind = kind;
        Reason = reason;
        StartedUtc = startedUtc;
        IsFirstUpdateForListing = isFirstUpdateForListing;
        Step = "queued";
    }

    public long Id { get; }
    public PartyFinderOperationKind Kind { get; }
    public string Reason { get; }
    public DateTime StartedUtc { get; }

    /// <summary>True for an <see cref="PartyFinderOperationKind.Update"/> when no update has yet completed for the
    /// current listing — i.e. the "first refresh after starting recruitment" the production soak isolated. Derived
    /// from real service state (completed-update count), not from a timer.</summary>
    public bool IsFirstUpdateForListing { get; }

    public string Step { get; internal set; }
    public IReadOnlyList<(string Name, DateTime AtUtc)> Milestones => milestones;

    internal void Mark(string name, DateTime nowUtc) => milestones.Add((name, nowUtc));
}

public sealed record PartyFinderOperationRecord(
    long Id,
    PartyFinderOperationKind Kind,
    string Reason,
    bool IsFirstUpdateForListing,
    PartyFinderOperationOutcome Outcome,
    string Detail,
    string LastStep,
    TimeSpan Elapsed,
    string Timeline);

/// <summary>Pure single-owner bookkeeping for Party Finder operations. The unsafe engine
/// (<c>PartyFinderAutomationService</c>, VenueOS.Plugin — not unit-testable) owns one instance and routes every
/// start/finish through it, so "who owns the native UI right now", "which step is it on", and "has this listing had
/// an update yet" are answered by tested code rather than by scattered field assignments. At most one operation is
/// ever current; <see cref="Begin"/> while another is current finishes the old one as
/// <see cref="PartyFinderOperationOutcome.Aborted"/> first (the engine's abort-then-replace mutual exclusion).</summary>
public sealed class PartyFinderOperationTracker
{
    private long nextId = 1;

    public PartyFinderOperation? Current { get; private set; }
    public PartyFinderOperationRecord? Last { get; private set; }

    /// <summary>Completed updates for the listing currently on screen. Reset by a completed Create, End, a "listing
    /// ended" chat notification, and a venue reset. Zero means the next Update is the first one this listing has had
    /// (as far as this plugin instance has observed).</summary>
    public int UpdatesCompletedForListing { get; private set; }

    public bool IsRunning => Current is not null;

    public PartyFinderOperation Begin(PartyFinderOperationKind kind, string reason, DateTime nowUtc)
    {
        if (Current is not null)
        {
            Finish(PartyFinderOperationOutcome.Aborted, $"superseded by a new {kind} request", nowUtc);
        }

        Current = new PartyFinderOperation(nextId++, kind, reason, nowUtc, kind == PartyFinderOperationKind.Update && UpdatesCompletedForListing == 0);
        Current.Mark("begin", nowUtc);
        return Current;
    }

    public void EnterStep(string step, DateTime nowUtc)
    {
        if (Current is null || string.Equals(Current.Step, step, StringComparison.Ordinal))
        {
            return;
        }

        Current.Step = step;
        Current.Mark(step, nowUtc);
    }

    public void Mark(string milestone, DateTime nowUtc) => Current?.Mark(milestone, nowUtc);

    /// <summary>Finishes the current operation (if any) and clears ownership. Returns the finished record, or null
    /// when nothing was running (a stray double-finish is harmless).</summary>
    public PartyFinderOperationRecord? Finish(PartyFinderOperationOutcome outcome, string detail, DateTime nowUtc)
    {
        var op = Current;
        if (op is null)
        {
            return null;
        }

        Current = null;
        op.Mark(outcome.ToString().ToLowerInvariant(), nowUtc);

        if (outcome == PartyFinderOperationOutcome.Completed)
        {
            UpdatesCompletedForListing = op.Kind switch
            {
                PartyFinderOperationKind.Update => UpdatesCompletedForListing + 1,
                _ => 0, // a fresh Create starts a new listing; End withdraws it
            };
        }

        Last = new PartyFinderOperationRecord(op.Id, op.Kind, op.Reason, op.IsFirstUpdateForListing, outcome, detail, op.Step, nowUtc - op.StartedUtc, FormatTimeline(op));
        return Last;
    }

    /// <summary>The native "your party recruitment has ended" notification, or a venue reset: the next Update is a
    /// first update again.</summary>
    public void ResetListingHistory() => UpdatesCompletedForListing = 0;

    private static string FormatTimeline(PartyFinderOperation op)
    {
        var sb = new StringBuilder();
        foreach (var (name, at) in op.Milestones)
        {
            if (sb.Length > 0)
            {
                sb.Append(" > ");
            }

            sb.Append(name).Append('@').Append((long)(at - op.StartedUtc).TotalMilliseconds).Append("ms");
        }

        return sb.ToString();
    }
}
