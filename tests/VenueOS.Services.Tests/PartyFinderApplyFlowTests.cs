using VenueOS.Modules.Operations.PartyFinder;

namespace VenueOS.Services.Tests;

/// <summary>Post-0.3.7 first-refresh investigation: covers the pure decision logic behind Party Finder's final
/// "submit the editor" stage — readiness of the Apply Changes control, confirmation that the game accepted the
/// dispatch, and the single bounded recovery. The native observations that feed it (addon/node reads) and the actual
/// <c>ReceiveEvent</c> dispatch live in <c>PartyFinderAutomationService</c> (VenueOS.Plugin, no test project) and are
/// live-QA-only; nothing here pretends to exercise FFXIV.</summary>
public sealed class PartyFinderApplyFlowTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PartyFinderSubmitObservation Ready(string label = "Apply Changes") => new(true, true, true, true, true, label);

    private static PartyFinderConfirmationObservation EditorOpen(bool buttonEnabled = true, bool popup = false) => new(true, buttonEnabled, popup);

    private static readonly PartyFinderConfirmationObservation EditorClosed = new(false, false, false);

    // ---- readiness -------------------------------------------------------------------------------------------

    [Fact] public void Every_not_ready_native_state_is_classified_distinctly()
    {
        Assert.Equal(PartyFinderSubmitReadiness.EditorMissing, PartyFinderApplyTracker.Classify(new(false, false, false, false, false, "")));
        Assert.Equal(PartyFinderSubmitReadiness.ButtonMissing, PartyFinderApplyTracker.Classify(new(true, false, false, false, false, "")));
        Assert.Equal(PartyFinderSubmitReadiness.ButtonNotVisible, PartyFinderApplyTracker.Classify(new(true, true, false, true, true, "Apply Changes")));
        Assert.Equal(PartyFinderSubmitReadiness.ButtonDisabled, PartyFinderApplyTracker.Classify(new(true, true, true, false, true, "Apply Changes")));
        Assert.Equal(PartyFinderSubmitReadiness.ButtonNotActivatable, PartyFinderApplyTracker.Classify(new(true, true, true, true, false, "Apply Changes")));
        Assert.Equal(PartyFinderSubmitReadiness.Ready, PartyFinderApplyTracker.Classify(Ready()));
    }

    [Fact] public void A_ready_control_is_dispatched_on_the_first_observation_with_no_waiting()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        var decision = tracker.EvaluateReadiness(T0, Ready());
        Assert.Equal(PartyFinderApplyAction.Dispatch, decision.Action);
        Assert.Equal(PartyFinderSubmitReadiness.Ready, decision.Readiness);
    }

    [Fact] public void A_visible_but_disabled_control_is_never_dispatched_and_readiness_is_reported_by_name()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        var disabled = new PartyFinderSubmitObservation(true, true, true, false, true, "Apply Changes");

        var decision = tracker.EvaluateReadiness(T0, disabled);
        Assert.Equal(PartyFinderApplyAction.Wait, decision.Action);
        Assert.Equal(PartyFinderSubmitReadiness.ButtonDisabled, decision.Readiness);
        Assert.Contains("disabled", decision.Reason);
    }

    [Fact] public void The_control_becoming_ready_within_the_bound_dispatches_at_that_moment()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        var disabled = new PartyFinderSubmitObservation(true, true, true, false, true, "Apply Changes");

        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateReadiness(T0, disabled).Action);
        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateReadiness(T0.AddSeconds(3), disabled).Action);
        Assert.Equal(PartyFinderApplyAction.Dispatch, tracker.EvaluateReadiness(T0.AddSeconds(4), Ready()).Action);
    }

    [Fact] public void A_control_that_never_becomes_ready_fails_at_the_bound_naming_the_last_observed_state()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        var disabled = new PartyFinderSubmitObservation(true, true, true, false, true, "Apply Changes");

        tracker.EvaluateReadiness(T0, disabled);
        var last = tracker.EvaluateReadiness(T0.AddSeconds(5), disabled);

        Assert.Equal(PartyFinderApplyAction.Fail, last.Action);
        Assert.Equal(PartyFinderSubmitReadiness.ButtonDisabled, last.Readiness);
        Assert.Contains("disabled", last.Reason);
        Assert.Equal(0, tracker.DispatchCount); // "never sent" is distinguishable from "sent and ignored"
    }

    [Fact] public void Readiness_changes_are_flagged_once_so_the_engine_logs_transitions_not_every_frame()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        var missing = new PartyFinderSubmitObservation(false, false, false, false, false, "");

        Assert.True(tracker.EvaluateReadiness(T0, missing).ReadinessChanged);
        Assert.False(tracker.EvaluateReadiness(T0.AddMilliseconds(16), missing).ReadinessChanged);
        Assert.False(tracker.EvaluateReadiness(T0.AddMilliseconds(32), missing).ReadinessChanged);
        Assert.True(tracker.EvaluateReadiness(T0.AddMilliseconds(48), Ready()).ReadinessChanged);
    }

    /// <summary>The label is diagnostic only. Its reliability has no live evidence, so it must never be able to block a
    /// healthy refresh — including a locale where the text is unfamiliar, empty, or (worst case) stale.</summary>
    [Fact] public void The_control_label_never_gates_readiness()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        Assert.Equal(PartyFinderApplyAction.Dispatch, tracker.EvaluateReadiness(T0, Ready(label: "")).Action);
        Assert.Equal(PartyFinderApplyAction.Dispatch, new PartyFinderApplyTracker(true).EvaluateReadiness(T0, Ready(label: "変更を適用")).Action);
        Assert.Equal(PartyFinderApplyAction.Dispatch, new PartyFinderApplyTracker(true).EvaluateReadiness(T0, Ready(label: "Recruit Members")).Action);
        Assert.True(PartyFinderApplyTracker.IsCreateModeLabel("  recruit members "));
        Assert.False(PartyFinderApplyTracker.IsCreateModeLabel("Apply Changes"));
    }

    // ---- confirmation ----------------------------------------------------------------------------------------

    [Fact] public void Nothing_is_confirmed_before_a_dispatch_exists()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateConfirmation(T0, EditorClosed).Action);
    }

    [Fact] public void The_editor_closing_is_the_authoritative_confirmation()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);
        Assert.Equal(PartyFinderApplyAction.Confirmed, tracker.EvaluateConfirmation(T0.AddMilliseconds(400), EditorClosed).Action);
    }

    [Fact] public void An_editor_still_open_inside_the_acceptance_window_is_waited_on_not_retried_or_failed()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);
        var decision = tracker.EvaluateConfirmation(T0.AddMilliseconds(1900), EditorOpen());
        Assert.Equal(PartyFinderApplyAction.Wait, decision.Action);
    }

    [Fact] public void An_ignored_dispatch_is_recovered_by_exactly_one_redispatch_then_confirmed()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);

        var recovery = tracker.EvaluateConfirmation(T0.AddSeconds(2), EditorOpen());
        Assert.Equal(PartyFinderApplyAction.Redispatch, recovery.Action);

        tracker.RecordDispatch(T0.AddSeconds(2));
        Assert.Equal(2, tracker.DispatchCount);
        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateConfirmation(T0.AddSeconds(3), EditorOpen()).Action);
        Assert.Equal(PartyFinderApplyAction.Confirmed, tracker.EvaluateConfirmation(T0.AddSeconds(3.2), EditorClosed).Action);
    }

    [Fact] public void Recovery_is_bounded_a_second_non_transition_fails_instead_of_dispatching_a_third_time()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);
        tracker.RecordDispatch(T0.AddSeconds(2)); // the one allowed recovery

        // Even long after the grace window, with the control still enabled, no third dispatch is authorised.
        Assert.NotEqual(PartyFinderApplyAction.Redispatch, tracker.EvaluateConfirmation(T0.AddSeconds(4.5), EditorOpen()).Action);

        var final = tracker.EvaluateConfirmation(T0.AddSeconds(7), EditorOpen());
        Assert.Equal(PartyFinderApplyAction.Fail, final.Action);
        Assert.Contains("2 dispatch", final.Reason);
    }

    [Fact] public void Recovery_requires_the_control_to_still_be_enabled()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);

        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateConfirmation(T0.AddSeconds(2.5), EditorOpen(buttonEnabled: false)).Action);
        Assert.Equal(PartyFinderApplyAction.Fail, tracker.EvaluateConfirmation(T0.AddSeconds(5), EditorOpen(buttonEnabled: false)).Action);
    }

    [Fact] public void A_pending_native_confirmation_popup_is_never_mistaken_for_an_ignored_dispatch()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: true);
        tracker.RecordDispatch(T0);

        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateConfirmation(T0.AddSeconds(3), EditorOpen(popup: true)).Action);
    }

    /// <summary>A duplicate "Recruit Members" is not provably harmless the way re-applying an identical update is, so
    /// creation never gets a recovery dispatch — it fails at the confirmation bound exactly as before.</summary>
    [Fact] public void Create_never_gets_a_recovery_dispatch()
    {
        var tracker = new PartyFinderApplyTracker(isUpdate: false);
        tracker.RecordDispatch(T0);

        Assert.Equal(PartyFinderApplyAction.Wait, tracker.EvaluateConfirmation(T0.AddSeconds(3), EditorOpen()).Action);
        var final = tracker.EvaluateConfirmation(T0.AddSeconds(5), EditorOpen());
        Assert.Equal(PartyFinderApplyAction.Fail, final.Action);
        Assert.Equal(1, tracker.DispatchCount);
    }

    [Fact] public void A_dispatched_but_never_accepted_failure_is_distinguishable_from_a_never_sent_failure()
    {
        var neverSent = new PartyFinderApplyTracker(isUpdate: true);
        var disabled = new PartyFinderSubmitObservation(true, true, true, false, true, "Apply Changes");
        neverSent.EvaluateReadiness(T0, disabled);
        var neverSentFailure = neverSent.EvaluateReadiness(T0.AddSeconds(6), disabled);

        var sentIgnored = new PartyFinderApplyTracker(isUpdate: false); // no recovery: fails after one dispatch
        Assert.Equal(PartyFinderApplyAction.Dispatch, sentIgnored.EvaluateReadiness(T0, Ready("Recruit Members")).Action);
        sentIgnored.RecordDispatch(T0);
        var sentIgnoredFailure = sentIgnored.EvaluateConfirmation(T0.AddSeconds(6), EditorOpen());

        Assert.Equal(0, neverSent.DispatchCount);
        Assert.Equal(1, sentIgnored.DispatchCount);
        Assert.Contains("disabled", neverSentFailure.Reason);
        Assert.Contains("1 dispatch", sentIgnoredFailure.Reason);
    }

    /// <summary>The first refresh and a later one make the same decisions: nothing in the decision logic is keyed off
    /// history, so any first-versus-later difference has to come from native observations (which the diagnostics now
    /// record), not from this logic.</summary>
    [Fact] public void First_and_later_refreshes_make_identical_decisions_for_identical_observations()
    {
        static List<PartyFinderApplyAction> Run()
        {
            var t = new PartyFinderApplyTracker(isUpdate: true);
            var actions = new List<PartyFinderApplyAction> { t.EvaluateReadiness(T0, Ready()).Action };
            t.RecordDispatch(T0);
            actions.Add(t.EvaluateConfirmation(T0.AddSeconds(1), EditorOpen()).Action);
            actions.Add(t.EvaluateConfirmation(T0.AddSeconds(2), EditorOpen()).Action);
            t.RecordDispatch(T0.AddSeconds(2));
            actions.Add(t.EvaluateConfirmation(T0.AddSeconds(2.5), EditorClosed).Action);
            return actions;
        }

        Assert.Equal(Run(), Run());
    }
}
