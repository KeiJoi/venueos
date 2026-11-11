namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>One Data Center determined unavailable/congested for the CURRENT interrupted run — see the crash-recovery
/// brief's "RUN-SCOPED DATA CENTER SKIPS". Never edits the operator's saved route; exists purely so Resume does not
/// re-attempt a Data Center the run already gave up on, and so the operator can see why.</summary>
public sealed record ShoutRunnerRecoverySkippedDataCenter(string DataCenter, string Reason, DateTimeOffset AtUtc);

/// <summary>The durable, crash-recoverable checkpoint for the ONE currently in-progress ShoutRunner run — not a
/// history log (see <see cref="ShoutRunnerTerminal"/> for that) and not permanent (deleted on successful completion
/// or explicit Stop; see <see cref="ShoutRunnerService"/>'s recovery members for exactly when).
///
/// <see cref="Route"/> is the SAME frozen <see cref="ShoutRunnerRunConfig"/> snapshot <see cref="ShoutRunnerService.Start"/>
/// already takes at RUN start — persisting it here means Resume always continues the exact interrupted route (Data
/// Centers, destinations, delay) even if the operator edits Settings after a crash, per the brief's "RUN
/// CONFIGURATION SNAPSHOT"/"ROUTE CONFIGURATION CHANGED AFTER CRASH". The live Shout Message is deliberately NOT
/// duplicated here — it is already safely stored in normal per-venue <c>ShoutRunnerSettings</c> and, exactly like a
/// non-crashed run, is read fresh before each unsent shout (see <see cref="ShoutRunnerService.BeginShout"/>'s doc
/// comment) — Resume does not change that existing, intentional behavior.
///
/// <see cref="CurrentDataCenter"/>/<see cref="CurrentWorld"/> identify where in <see cref="Route"/>'s frozen order to
/// resume — by name against a fixed, versioned catalog/snapshot, never a raw index, so a lookup can never silently
/// desynchronize. Everything before them in that same frozen order is, by construction, already behind the run's
/// cursor and therefore never revisited on Resume, whether it finished normally or was skipped
/// (<see cref="SkippedDataCenters"/> is retained purely for display/audit, not for that guarantee).
///
/// <see cref="CompletedDestinationsInCurrentWorld"/>/<see cref="CurrentWorldPlan"/> exist so Resume never has to
/// re-detect the character's location or re-run the ping-pong planner for a World that was already mid-traversal —
/// it simply replays the exact plan that was already computed before the crash, skipping (by name, see
/// <see cref="ShoutRunnerResumePlan"/>)
/// whatever is already in <see cref="CompletedDestinationsInCurrentWorld"/>. A World with zero completed
/// destinations yet is instead resumed exactly like a fresh entry (nothing to preserve) — see
/// <see cref="ShoutRunnerService.Resume"/>.</summary>
public sealed record ShoutRunnerRecoveryJournal(
    int SchemaVersion,
    Guid VenueId,
    string VenueDisplayNameSnapshot,
    Guid RunId,
    int RunNumber,
    DateTimeOffset RunStartedAtUtc,
    ShoutRunnerRunConfig Route,
    string? CurrentDataCenter,
    string? CurrentWorld,
    List<string> CompletedDestinationsInCurrentWorld,
    List<ShoutRunnerDestinationStep>? CurrentWorldPlan,
    ShoutRunnerRoutePlanner.TraversalDirection FallbackDirection,
    List<ShoutRunnerRecoverySkippedDataCenter> SkippedDataCenters,
    DateTimeOffset LastCheckpointAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Read-only, UI-facing view of a recovered journal — everything the Resume summary needs to show the
/// operator "what will happen" before they click Resume, without exposing the raw journal shape to the panel.</summary>
public sealed record ShoutRunnerRecoverySummary(
    Guid VenueId,
    string VenueDisplayName,
    int RunNumber,
    string? DataCenter,
    string? World,
    string? LastCompletedDestination,
    string? NextDestination,
    IReadOnlyList<ShoutRunnerRecoverySkippedDataCenter> SkippedDataCenters,
    DateTimeOffset LastCheckpointAtUtc);

public enum ShoutRunnerResumeResult
{
    Resumed,
    NoRecoveryAvailable,
    AlreadyRunning,
    DifferentVenue,
    RecoveryCorrupt,
}

/// <summary>The crash-safe durability boundary for the one active-run recovery journal — deliberately a thin,
/// swappable interface (matching <c>IShoutRunnerAutomation</c>'s precedent) purely so <see cref="ShoutRunnerService"/>'s
/// own orchestration tests can use an in-memory fake instead of touching real disk; see
/// <see cref="FileShoutRunnerRecoveryStore"/> for the real, separately-tested implementation and its atomic-write
/// guarantee.</summary>
public interface IShoutRunnerRecoveryStore
{
    /// <summary><paramref name="corrupt"/> is true only when a journal file exists but could not be safely loaded
    /// (malformed JSON, unsupported schema version, or internally inconsistent state) — distinct from "no file at
    /// all," which returns null with <paramref name="corrupt"/> false. Must never throw.</summary>
    ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt);

    void Save(ShoutRunnerRecoveryJournal journal);

    /// <summary>Removes the journal if present. Never throws — a failure to delete a stale file is not worth
    /// crashing the plugin over.</summary>
    void Delete();
}

/// <summary>Which steps of an interrupted World still have to run on Resume (0.3.12, D7). Before 0.3.12 Resume restarted
/// the saved plan at index <c>CompletedDestinationsInCurrentWorld.Count</c>. That index is only right when every
/// earlier step succeeded. A failed step (a teleport failure, a failed shout) is not counted as completed, so the
/// count fell behind the real position: plan [A, B, C] with A failed and B sent resumed at index 1 and re-announced
/// at B, while A, never sent, was not retried. The journal's own contract (see
/// <see cref="ShoutRunnerRecoveryJournal"/>) is "replay the plan, skipping whatever is already completed"; this is
/// that contract, by name.</summary>
public static class ShoutRunnerResumePlan
{
    /// <summary>The plan's steps, in plan order, minus one occurrence per completed destination (case-insensitive;
    /// multiset semantics, so a destination listed twice is only skipped as often as it was completed). Every
    /// returned step requires a teleport: after an interruption and the World re-entry that follows it, the character
    /// cannot be assumed to still be standing at any destination.</summary>
    public static IReadOnlyList<ShoutRunnerDestinationStep> RemainingSteps(IReadOnlyList<ShoutRunnerDestinationStep> plan, IReadOnlyList<string> completedDestinations)
    {
        var outstanding = new List<string>(completedDestinations);
        var remaining = new List<ShoutRunnerDestinationStep>(plan.Count);
        foreach (var step in plan)
        {
            var completedIndex = outstanding.FindIndex(d => string.Equals(d, step.Destination, StringComparison.OrdinalIgnoreCase));
            if (completedIndex >= 0) { outstanding.RemoveAt(completedIndex); continue; }
            remaining.Add(step with { RequiresTeleport = true });
        }

        return remaining;
    }
}
