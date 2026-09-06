namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>Plans one World's destination traversal as a "ping-pong" walk along the configured, ordered destination
/// list — never "always start at destination #1." The character's actual detected current location takes
/// precedence: the walk starts there and heads toward whichever end of the configured order that position implies,
/// then continues past that end and back through every remaining destination exactly once (see
/// <see cref="PlanWorldRoute"/>'s doc comment for the exact rule), so a mid-list start still visits every configured
/// destination without doubling back over ground it already covered. When the current location can't be matched to
/// any configured destination, the walk falls back to <see cref="TraversalDirection"/> — a single piece of state
/// threaded across every World in a RUN — starting a full pass in whichever direction the ping-pong was already
/// heading. This reproduces the "reverses at each World" behavior the reconstruction brief describes without ever
/// needing to remember exactly where a previous World's walk physically ended: a genuinely detected arrival at an
/// endpoint already implies the correct next direction on its own (see the worked examples below).</summary>
public static class ShoutRunnerRoutePlanner
{
    public enum TraversalDirection { Forward, Backward }

    /// <summary>Plans the ordered stops for one World, given the configured <paramref name="destinations"/> (at
    /// least one; order matters) and the character's <paramref name="currentPlaceName"/> if known (matched
    /// case-insensitively; null or unmatched is treated identically — "unknown").
    ///
    /// <b>Rule:</b> if the current location matches a configured destination at index <c>S</c>:
    /// <list type="bullet">
    /// <item>S is the first (index 0) → direction is Forward.</item>
    /// <item>S is the last index → direction is Backward.</item>
    /// <item>S is anywhere in the middle → direction is Forward (head toward the end of the configured order
    /// first — the reconstruction brief's explicit recommendation for a mid-list start).</item>
    /// </list>
    /// If the location is unknown/unmatched, the direction is whatever <paramref name="fallbackDirection"/> already
    /// holds, and <c>S</c> is index 0 for Forward or the last index for Backward ("start at the first destination in
    /// the current traversal direction" — the brief's documented fallback).
    ///
    /// The full stop order is then a rotation of every index starting at <c>S</c> in the chosen direction, wrapping
    /// through the remaining indices exactly once (so a mid-list start of [Ul'dah, <b>Gridania</b>, Limsa] produces
    /// Gridania → Limsa → Ul'dah, not just Gridania → Limsa) — this single rotation rule is what makes the same
    /// logic work uniformly for 1, 2, 3, or any larger number of configured destinations without special-casing any
    /// of them (see the class's test coverage for the 1/2/3/4-destination worked cases from the brief).
    ///
    /// Only the very first stop in the returned list can ever have <see cref="ShoutRunnerDestinationStep.RequiresTeleport"/>
    /// false, and only when it is the actual detected current location — every other stop, including the wrapped
    /// continuation past the array end, always requires a teleport, since the character is guaranteed to have moved
    /// away from it by then.
    ///
    /// <paramref name="fallbackDirection"/> is updated in place to the opposite of whichever direction was actually
    /// used this call — the direction the *next* World should fall back to if its own current location can't be
    /// determined, keeping the ping-pong continuous across an entire RUN (reset to <see cref="TraversalDirection.Forward"/>
    /// only when a new RUN starts, never per World or per Data Center).</summary>
    public static IReadOnlyList<ShoutRunnerDestinationStep> PlanWorldRoute(
        IReadOnlyList<string> destinations,
        string? currentPlaceName,
        ref TraversalDirection fallbackDirection)
    {
        if (destinations.Count == 0) return [];
        var count = destinations.Count;
        var lastIndex = count - 1;

        var matchedIndex = -1;
        if (currentPlaceName is not null)
        {
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(destinations[i], currentPlaceName, StringComparison.OrdinalIgnoreCase)) { matchedIndex = i; break; }
            }
        }

        TraversalDirection usedDirection;
        int startIndex;
        if (matchedIndex >= 0)
        {
            startIndex = matchedIndex;
            usedDirection = matchedIndex == 0 ? TraversalDirection.Forward
                : matchedIndex == lastIndex ? TraversalDirection.Backward
                : TraversalDirection.Forward;
        }
        else
        {
            usedDirection = fallbackDirection;
            startIndex = usedDirection == TraversalDirection.Forward ? 0 : lastIndex;
        }

        var steps = new List<ShoutRunnerDestinationStep>(count);
        for (var step = 0; step < count; step++)
        {
            var index = usedDirection == TraversalDirection.Forward
                ? (startIndex + step) % count
                : ((startIndex - step) % count + count) % count;
            var alreadyThere = step == 0 && matchedIndex == index;
            steps.Add(new ShoutRunnerDestinationStep(destinations[index], !alreadyThere));
        }

        fallbackDirection = usedDirection == TraversalDirection.Forward ? TraversalDirection.Backward : TraversalDirection.Forward;
        return steps;
    }
}
