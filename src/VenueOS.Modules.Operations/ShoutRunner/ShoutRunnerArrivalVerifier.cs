namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>One polled observation of the character and Lifestream during a World transfer. Every field is sampled
/// on the Dalamud framework thread in a single pass (<c>ShoutRunnerAutomationService.GetGameStateAsync</c>), so the
/// World and Lifestream's busy state describe the same frame.</summary>
/// <param name="PrerequisiteSeen">The transfer-type-specific proof that travel actually began: a zone transition
/// (same Data Center) or a logout (cross Data Center).</param>
/// <param name="LifestreamBusy">Lifestream's <c>IsBusy</c> IPC result, or null when it could not be read.</param>
public readonly record struct ShoutRunnerArrivalSample(
    bool IsLoggedIn,
    bool HasLocalPlayer,
    bool Transitioning,
    bool PrerequisiteSeen,
    string? CurrentWorld,
    bool? LifestreamBusy);

/// <summary>The final arrival decision for one Lifestream World transfer (0.3.12). It replaces the "decide on the first
/// qualifying sample" rule both transfer loops used before, for the wrong-World incident in
/// <c>docs/SHOUTRUNNER_LIFESTREAM_COMPATIBILITY_AUDIT.md</c>.
///
/// <para>A sample is <b>settled</b> when <see cref="ShoutRunnerSameDataCenterArrival.Evaluate"/> would already have
/// accepted it: logged in, local player present, not mid-transition, the prerequisite seen, a known World, and
/// Lifestream positively reporting <i>not</i> busy. The verifier only returns a verdict once the SAME settled
/// conclusion (same World, same right/wrong outcome) has held continuously for a bounded window:
/// <see cref="ArrivalSettleWindow"/> for the intended World, <see cref="WrongWorldSettleWindow"/> for any other World.
/// Any unsettled sample in between (a new transition, Lifestream busy again, an unreadable busy state, a different
/// World) restarts the window. So:</para>
/// <list type="bullet">
/// <item>A wrong World is still never accepted as success (the safety check is unchanged), but one momentary "idle"
/// reading, such as a gap between two chained Lifestream tasks or its own default "retry failed World Visit"
/// behavior, can no longer fault the whole run while Lifestream is still correcting course.</item>
/// <item>No wait is unbounded: the enclosing transfer loop's own timeout still applies, and the windows are a few
/// seconds of continuous stability, not a fixed delay before every decision.</item>
/// </list>
/// <para>It also records a compact history (Worlds observed in order, number of transitions, whether Lifestream was
/// ever seen busy) for diagnostics. That history is what tells "Lifestream went straight to the wrong World" apart from
/// "it reached the target and then left" and from "it never started".</para></summary>
public sealed class ShoutRunnerArrivalVerifier
{
    public enum Verdict { Continue, Arrived, WrongWorld }

    /// <summary>How long the character must remain settled on the intended World before arrival is accepted.</summary>
    public static readonly TimeSpan ArrivalSettleWindow = TimeSpan.FromSeconds(1);

    /// <summary>How long the character must remain settled on a different World, with Lifestream idle, before the
    /// transfer is declared a genuine wrong-World arrival.</summary>
    public static readonly TimeSpan WrongWorldSettleWindow = TimeSpan.FromSeconds(3);

    private readonly List<string> worldsObserved = [];
    private string? candidateWorld;
    private ShoutRunnerSameDataCenterArrival.Decision candidateDecision;
    private DateTimeOffset candidateSince;
    private bool lastTransitioning;

    public ShoutRunnerArrivalVerifier(string targetWorld, string? originWorld)
    {
        TargetWorld = targetWorld;
        OriginWorld = string.IsNullOrWhiteSpace(originWorld) ? null : originWorld;
        if (OriginWorld is not null) worldsObserved.Add(OriginWorld);
    }

    public string TargetWorld { get; }
    public string? OriginWorld { get; }
    public IReadOnlyList<string> WorldsObserved => worldsObserved;
    public int TransitionsObserved { get; private set; }
    public bool LifestreamSeenBusy { get; private set; }
    public bool? LastLifestreamBusy { get; private set; }
    public int SamplesObserved { get; private set; }

    /// <summary>True when the World the verdict was reached on is the World the transfer started from, meaning the
    /// character never actually left. Only meaningful after <see cref="Verdict.WrongWorld"/>.</summary>
    public bool EndedOnOriginWorld => OriginWorld is not null && candidateWorld is not null && string.Equals(candidateWorld, OriginWorld, StringComparison.OrdinalIgnoreCase);

    /// <summary>The World the most recent verdict (or settled candidate) refers to.</summary>
    public string? SettledWorld => candidateWorld;

    public Verdict Observe(ShoutRunnerArrivalSample sample, DateTimeOffset now)
    {
        SamplesObserved++;
        if (sample.Transitioning && !lastTransitioning) TransitionsObserved++;
        lastTransitioning = sample.Transitioning;
        if (sample.LifestreamBusy == true) LifestreamSeenBusy = true;
        LastLifestreamBusy = sample.LifestreamBusy;
        if (!string.IsNullOrWhiteSpace(sample.CurrentWorld) && !sample.Transitioning
            && (worldsObserved.Count == 0 || !string.Equals(worldsObserved[^1], sample.CurrentWorld, StringComparison.OrdinalIgnoreCase)))
            worldsObserved.Add(sample.CurrentWorld!);

        var decision = ShoutRunnerSameDataCenterArrival.Evaluate(sample.IsLoggedIn, sample.HasLocalPlayer, sample.Transitioning, sample.PrerequisiteSeen, sample.CurrentWorld, TargetWorld, sample.LifestreamBusy);
        if (decision == ShoutRunnerSameDataCenterArrival.Decision.Continue)
        {
            candidateWorld = null;
            return Verdict.Continue;
        }

        if (candidateWorld is null || candidateDecision != decision || !string.Equals(candidateWorld, sample.CurrentWorld, StringComparison.OrdinalIgnoreCase))
        {
            candidateWorld = sample.CurrentWorld;
            candidateDecision = decision;
            candidateSince = now;
        }

        var window = decision == ShoutRunnerSameDataCenterArrival.Decision.Success ? ArrivalSettleWindow : WrongWorldSettleWindow;
        if (now - candidateSince < window) return Verdict.Continue;
        return decision == ShoutRunnerSameDataCenterArrival.Decision.Success ? Verdict.Arrived : Verdict.WrongWorld;
    }

    /// <summary>The operator-facing terminal text for a confirmed wrong-World arrival. Keeps the established
    /// "Arrived at X instead of Y" wording and adds the one fact that tells the two cases apart: the character went to
    /// another World, or never left the World it started on.</summary>
    public string DescribeWrongWorld(string? observedWorld) => EndedOnOriginWorld
        ? $"Travel to {TargetWorld} ended back on {OriginWorld}: Lifestream finished without completing the World Visit."
        : $"Arrived at {observedWorld} instead of {TargetWorld} (Lifestream finished travel on a different World; details in Diagnostics).";

    /// <summary>Compact, operator-shareable history for Diagnostics, with no character names.</summary>
    public string DescribeHistory() =>
        $"Worlds observed: {(worldsObserved.Count == 0 ? "(none)" : string.Join(" → ", worldsObserved))}; transitions: {TransitionsObserved}; " +
        $"Lifestream seen busy: {(LifestreamSeenBusy ? "yes" : "no")}; last Lifestream busy: {FormatBusy(LastLifestreamBusy)}; samples: {SamplesObserved}";

    private static string FormatBusy(bool? busy) => busy switch { true => "busy", false => "idle", null => "unknown" };
}
