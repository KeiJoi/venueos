namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>Why the editor's submit control (Apply Changes when updating, Recruit Members when creating) is or is not
/// ready to be activated right now. Every value except <see cref="Ready"/> is a distinct, observable native state —
/// the point of the enum is that a failure report can name exactly which one held, instead of "Party Finder
/// failed".</summary>
public enum PartyFinderSubmitReadiness
{
    /// <summary>No editor addon (Recruitment Criteria) is loaded, ready and visible.</summary>
    EditorMissing,

    /// <summary>The editor is open but no submit control was found on it.</summary>
    ButtonMissing,

    /// <summary>The submit control exists but its node is not visible.</summary>
    ButtonNotVisible,

    /// <summary>The submit control is present and visible but the game reports it disabled.</summary>
    ButtonDisabled,

    /// <summary>The submit control has no native click event registered, so it cannot be activated.</summary>
    ButtonNotActivatable,

    Ready,
}

/// <summary>What the engine observed about the editor's submit control on this tick. Gathered by the unsafe engine
/// (addon/node reads); interpreted here so the decision logic is testable without a game.</summary>
public readonly record struct PartyFinderSubmitObservation(
    bool EditorVisible,
    bool ButtonFound,
    bool ButtonVisible,
    bool ButtonEnabled,
    bool ButtonActivatable,
    string ButtonLabel);

/// <summary>What the engine observed after a submit dispatch.</summary>
public readonly record struct PartyFinderConfirmationObservation(bool EditorVisible, bool ButtonEnabled, bool PopupPending);

public enum PartyFinderApplyAction
{
    Wait,
    Dispatch,

    /// <summary>The previous dispatch produced no observed transition; activate the (still enabled) control once
    /// more. Only ever returned for updates, and at most <see cref="PartyFinderApplyTimings.MaxDispatches"/> total.</summary>
    Redispatch,

    /// <summary>The authoritative transition was observed: the editor closed.</summary>
    Confirmed,

    Fail,
}

public readonly record struct PartyFinderApplyDecision(PartyFinderApplyAction Action, PartyFinderSubmitReadiness Readiness, bool ReadinessChanged, string Reason);

public sealed record PartyFinderApplyTimings(TimeSpan ReadinessTimeout, TimeSpan AcceptanceGrace, TimeSpan ConfirmationTimeout, int MaxDispatches)
{
    /// <summary><para><see cref="ReadinessTimeout"/> matches the engine's shared native-control readiness bound
    /// (<c>UiElementReadinessTimeout</c>). <see cref="ConfirmationTimeout"/> matches the previous
    /// <c>SubmissionVerificationTimeout</c>, now measured from the last dispatch rather than from the first verify
    /// tick.</para>
    /// <para><see cref="AcceptanceGrace"/> is not a "wait and hope" sleep: it is the observation window after a
    /// dispatch in which the only acceptable outcome is the editor closing. It exists because non-transition can only
    /// be observed as the absence of a change over time; recovery fires only if, after it, the editor is still open,
    /// the control is still enabled and no native confirmation popup is pending.</para></summary>
    public static readonly PartyFinderApplyTimings Default = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), MaxDispatches: 2);
}

/// <summary>Pure decision logic for the final "submit the editor" stage of a Party Finder Create/Update chain:
/// (1) is the submit control genuinely ready, (2) has a dispatch produced the authoritative native transition, and
/// (3) if not, is one bounded re-dispatch justified. One instance per operation.
///
/// Why recovery exists at all, and why it is safe: activating the control replays its registered native click event
/// synthetically (<c>AtkUnitBase.ReceiveEvent</c>), which the game can silently ignore — dispatch is not acceptance.
/// Applying an update is idempotent (the identical preset is re-submitted by every refresh, manual or automatic),
/// so a second dispatch after <em>observed</em> non-transition cannot corrupt state. It is never used for Create
/// (a duplicate "Recruit Members" is not provably harmless), never blind (it requires the editor still visible, the
/// control still enabled and no popup pending) and never unbounded (<see cref="PartyFinderApplyTimings.MaxDispatches"/>).</summary>
public sealed class PartyFinderApplyTracker(bool isUpdate, PartyFinderApplyTimings? timings = null)
{
    private static readonly string[] CreateModeLabels = ["Recruit Members", "Recruiting Members", "Register", "Create Listing"];

    private readonly PartyFinderApplyTimings config = timings ?? PartyFinderApplyTimings.Default;
    private DateTime readinessStartedUtc;
    private DateTime lastDispatchUtc;

    public bool IsUpdate { get; } = isUpdate;
    public int DispatchCount { get; private set; }
    public PartyFinderSubmitReadiness? LastReadiness { get; private set; }

    public static PartyFinderSubmitReadiness Classify(in PartyFinderSubmitObservation observation)
    {
        if (!observation.EditorVisible) return PartyFinderSubmitReadiness.EditorMissing;
        if (!observation.ButtonFound) return PartyFinderSubmitReadiness.ButtonMissing;
        if (!observation.ButtonVisible) return PartyFinderSubmitReadiness.ButtonNotVisible;
        if (!observation.ButtonEnabled) return PartyFinderSubmitReadiness.ButtonDisabled;
        if (!observation.ButtonActivatable) return PartyFinderSubmitReadiness.ButtonNotActivatable;

        // The control's label is deliberately NOT part of readiness. Label text is read from the button's text node,
        // whose reliability across game versions/locales has no live evidence behind it; gating on it could block a
        // healthy refresh. It is recorded in the dispatch log (and flagged via IsCreateModeLabel when updating) so a
        // live failure shows what the control said, without the label ever being able to stop an update.
        return PartyFinderSubmitReadiness.Ready;
    }

    public static bool IsCreateModeLabel(string label)
    {
        var trimmed = label.Trim();
        return CreateModeLabels.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Called every tick until the submit control is ready. Returns <see cref="PartyFinderApplyAction.Dispatch"/>
    /// the first tick it is, <see cref="PartyFinderApplyAction.Wait"/> while inside the readiness bound, and
    /// <see cref="PartyFinderApplyAction.Fail"/> once the bound is exceeded (with the exact last-observed state).</summary>
    public PartyFinderApplyDecision EvaluateReadiness(DateTime nowUtc, in PartyFinderSubmitObservation observation)
    {
        if (readinessStartedUtc == default)
        {
            readinessStartedUtc = nowUtc;
        }

        var readiness = Classify(observation);
        var changed = LastReadiness != readiness;
        LastReadiness = readiness;

        if (readiness == PartyFinderSubmitReadiness.Ready)
        {
            return new(PartyFinderApplyAction.Dispatch, readiness, changed, "submit control ready");
        }

        if (nowUtc - readinessStartedUtc < config.ReadinessTimeout)
        {
            return new(PartyFinderApplyAction.Wait, readiness, changed, Describe(readiness));
        }

        return new(PartyFinderApplyAction.Fail, readiness, changed, $"{Describe(readiness)} for {config.ReadinessTimeout.TotalSeconds:0}s");
    }

    /// <summary>Records that the engine activated the control (whether first attempt or recovery).</summary>
    public void RecordDispatch(DateTime nowUtc)
    {
        DispatchCount++;
        lastDispatchUtc = nowUtc;
    }

    /// <summary>Called every tick after a dispatch until confirmed or failed.</summary>
    public PartyFinderApplyDecision EvaluateConfirmation(DateTime nowUtc, in PartyFinderConfirmationObservation observation)
    {
        var readiness = LastReadiness ?? PartyFinderSubmitReadiness.Ready;
        if (DispatchCount == 0)
        {
            return new(PartyFinderApplyAction.Wait, readiness, false, "no dispatch has been made yet");
        }

        if (!observation.EditorVisible)
        {
            return new(PartyFinderApplyAction.Confirmed, readiness, false, "editor closed");
        }

        if (observation.PopupPending)
        {
            return new(PartyFinderApplyAction.Wait, readiness, false, "a native confirmation popup is being handled");
        }

        var sinceDispatch = nowUtc - lastDispatchUtc;
        if (IsUpdate && observation.ButtonEnabled && DispatchCount < config.MaxDispatches && sinceDispatch >= config.AcceptanceGrace)
        {
            return new(PartyFinderApplyAction.Redispatch, readiness, false, $"editor still open {sinceDispatch.TotalSeconds:0.0}s after dispatch {DispatchCount}, control still enabled");
        }

        if (sinceDispatch >= config.ConfirmationTimeout)
        {
            return new(PartyFinderApplyAction.Fail, readiness, false, $"editor still open {sinceDispatch.TotalSeconds:0.0}s after {DispatchCount} dispatch(es); control enabled={observation.ButtonEnabled}");
        }

        return new(PartyFinderApplyAction.Wait, readiness, false, "waiting for the editor to close");
    }

    public static string Describe(PartyFinderSubmitReadiness readiness) => readiness switch
    {
        PartyFinderSubmitReadiness.EditorMissing => "the Recruitment Criteria editor is not visible",
        PartyFinderSubmitReadiness.ButtonMissing => "no submit control was found on the editor",
        PartyFinderSubmitReadiness.ButtonNotVisible => "the submit control exists but is not visible",
        PartyFinderSubmitReadiness.ButtonDisabled => "the submit control is present but disabled",
        PartyFinderSubmitReadiness.ButtonNotActivatable => "the submit control has no native click event registered",
        _ => "submit control ready",
    };
}
