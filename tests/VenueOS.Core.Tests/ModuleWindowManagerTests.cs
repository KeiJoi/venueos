using VenueOS.Core;

namespace VenueOS.Core.Tests;

public sealed class ModuleWindowManagerTests
{
    // ---- Default / closed state --------------------------------------------------------------------------------

    [Fact] public void An_unknown_module_id_is_closed_by_default() { var manager = new ModuleWindowManager(); Assert.False(manager.IsOpen("games.raffle")); }
    [Fact] public void An_unknown_module_id_has_no_state() { var manager = new ModuleWindowManager(); Assert.Null(manager.GetState("games.raffle")); }
    [Fact] public void An_unknown_module_id_has_no_snapshot() { var manager = new ModuleWindowManager(); Assert.Null(manager.GetSnapshot("games.raffle")); }

    // ---- Open ---------------------------------------------------------------------------------------------------

    [Fact] public void Open_on_a_closed_module_tracks_it_as_expanded_with_no_preference()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        Assert.True(manager.IsOpen("games.raffle"));
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState("games.raffle"));
    }

    [Fact] public void Open_with_a_preference_seeds_state_and_geometry()
    {
        var manager = new ModuleWindowManager();
        var preference = new ModuleWindowPreference(ModulePresentationState.Collapsed, 100, 200, 640, 512);
        manager.Open("games.raffle", preference);
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState("games.raffle"));
        var snapshot = manager.GetSnapshot("games.raffle")!;
        Assert.Equal(100, snapshot.PosX);
        Assert.Equal(200, snapshot.PosY);
        Assert.Equal(640, snapshot.Width);
        Assert.Equal(512, snapshot.Height);
    }

    [Fact] public void Repeated_open_is_idempotent_and_never_creates_a_second_tracked_entry()
    {
        var manager = new ModuleWindowManager();
        Assert.True(manager.Open("games.raffle"));
        Assert.False(manager.Open("games.raffle")); // second call: already tracked, not a fresh open
        Assert.Single(manager.OpenModuleIds);
    }

    [Fact] public void Reopening_an_already_open_module_does_not_apply_a_preference_seeded_after_the_first_open()
    {
        // A refocus must never silently reposition/resize a window the operator may have already moved.
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ReportPosition("games.raffle", 555, 666);
        manager.Open("games.raffle", new ModuleWindowPreference(ModulePresentationState.Collapsed, 1, 1, 1, 1));
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState("games.raffle"));
        Assert.Equal(555, manager.GetSnapshot("games.raffle")!.PosX);
    }

    // ---- Collapse / Expand ---------------------------------------------------------------------------------------

    [Fact] public void Collapse_sets_state_to_collapsed()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState("games.raffle"));
    }

    [Fact] public void Expand_sets_state_back_to_expanded()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Expand("games.raffle");
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState("games.raffle"));
    }

    [Fact] public void Collapse_and_expand_on_an_unknown_module_id_are_safely_ignored()
    {
        var manager = new ModuleWindowManager();
        manager.Collapse("games.raffle");
        manager.Expand("games.raffle");
        Assert.Null(manager.GetState("games.raffle"));
    }

    [Fact] public void Expanded_width_and_height_are_preserved_across_a_collapse_then_expand()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ReportExpandedSize("games.raffle", 700, 550);
        manager.Collapse("games.raffle");
        // While collapsed, reported "expanded size" calls must be ignored — a collapsed window's own fixed compact
        // size must never overwrite the remembered expanded size.
        manager.ReportExpandedSize("games.raffle", 50, 50);
        manager.Expand("games.raffle");
        var snapshot = manager.GetSnapshot("games.raffle")!;
        Assert.Equal(700, snapshot.Width);
        Assert.Equal(550, snapshot.Height);
    }

    [Fact] public void Moving_a_collapsed_window_then_expanding_uses_the_new_position_with_the_old_expanded_size()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ReportPosition("games.raffle", 10, 10);
        manager.ReportExpandedSize("games.raffle", 700, 550);
        manager.Collapse("games.raffle");
        manager.ReportPosition("games.raffle", 300, 400); // position updates every frame regardless of state
        manager.Expand("games.raffle");

        var snapshot = manager.GetSnapshot("games.raffle")!;
        Assert.Equal(300, snapshot.PosX);
        Assert.Equal(400, snapshot.PosY);
        Assert.Equal(700, snapshot.Width);
        Assert.Equal(550, snapshot.Height);
    }

    // ---- Hide / Restore -------------------------------------------------------------------------------------------

    [Fact] public void Hide_from_expanded_keeps_the_module_tracked_as_hidden()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Hide("games.raffle");
        Assert.True(manager.IsOpen("games.raffle"));
        Assert.Equal(ModulePresentationState.Hidden, manager.GetState("games.raffle"));
    }

    [Fact] public void Restore_from_hidden_after_expanded_returns_to_expanded()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Hide("games.raffle");
        manager.Restore("games.raffle");
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState("games.raffle"));
    }

    [Fact] public void Hide_from_collapsed_then_restore_returns_to_collapsed_not_expanded()
    {
        // Preserves the user's deliberate collapsed state rather than always popping back to Expanded.
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        Assert.Equal(ModulePresentationState.Hidden, manager.GetState("games.raffle"));
        manager.Restore("games.raffle");
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState("games.raffle"));
    }

    [Fact] public void A_snapshot_taken_while_hidden_reports_the_pre_hide_state_not_hidden()
    {
        // Hidden is never a persisted "opening default" — the caller persisting this snapshot must see whichever of
        // Expanded/Collapsed the module actually was.
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetSnapshot("games.raffle")!.LastState);
    }

    [Fact] public void Hide_and_restore_on_an_unknown_module_id_are_safely_ignored()
    {
        var manager = new ModuleWindowManager();
        manager.Hide("games.raffle");
        manager.Restore("games.raffle");
        Assert.Null(manager.GetState("games.raffle"));
    }

    [Fact] public void Hiding_an_already_hidden_module_does_not_overwrite_its_remembered_pre_hide_state()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        manager.Hide("games.raffle"); // second Hide call: must be a no-op, not re-capture Hidden as the pre-hide state
        manager.Restore("games.raffle");
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState("games.raffle"));
    }

    // ---- Evict (Close / disabled-module eviction) ------------------------------------------------------------------

    [Fact] public void Evict_stops_tracking_the_module()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Evict("games.raffle");
        Assert.False(manager.IsOpen("games.raffle"));
    }

    [Fact] public void Evict_returns_null_for_a_module_that_was_never_tracked()
    {
        var manager = new ModuleWindowManager();
        Assert.Null(manager.Evict("games.raffle"));
    }

    [Fact] public void Evict_preserves_the_remembered_geometry_in_its_returned_snapshot()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ReportPosition("games.raffle", 42, 84);
        manager.ReportExpandedSize("games.raffle", 720, 480);
        var snapshot = manager.Evict("games.raffle")!;
        Assert.Equal(42, snapshot.PosX);
        Assert.Equal(84, snapshot.PosY);
        Assert.Equal(720, snapshot.Width);
        Assert.Equal(480, snapshot.Height);
        Assert.Equal(ModulePresentationState.Expanded, snapshot.LastState);
    }

    [Fact] public void Evicting_a_hidden_module_reports_its_pre_hide_state_in_the_snapshot()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        var snapshot = manager.Evict("games.raffle")!;
        Assert.Equal(ModulePresentationState.Collapsed, snapshot.LastState);
    }

    [Fact] public void Reopening_after_evict_starts_fresh_unless_a_preference_is_supplied()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ReportPosition("games.raffle", 999, 999);
        manager.Evict("games.raffle");
        manager.Open("games.raffle");
        Assert.Equal(0, manager.GetSnapshot("games.raffle")!.PosX);
    }

    // ---- Focus requests -------------------------------------------------------------------------------------------

    [Fact] public void Open_requests_focus()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        Assert.True(manager.ConsumeFocusRequest("games.raffle"));
    }

    [Fact] public void Consuming_a_focus_request_clears_it()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ConsumeFocusRequest("games.raffle");
        Assert.False(manager.ConsumeFocusRequest("games.raffle"));
    }

    [Fact] public void Request_focus_on_an_unopened_module_does_nothing()
    {
        var manager = new ModuleWindowManager();
        manager.RequestFocus("games.raffle");
        Assert.False(manager.ConsumeFocusRequest("games.raffle"));
    }

    [Fact] public void Restore_also_requests_focus()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.ConsumeFocusRequest("games.raffle");
        manager.Hide("games.raffle");
        manager.Restore("games.raffle");
        Assert.True(manager.ConsumeFocusRequest("games.raffle"));
    }

    // ---- Multiple modules never interfere -----------------------------------------------------------------------

    [Fact] public void Two_different_modules_track_independent_state()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Open("games.bingo");
        manager.Collapse("games.raffle");
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState("games.raffle"));
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState("games.bingo"));
    }

    [Fact] public void Evicting_one_module_does_not_affect_another()
    {
        var manager = new ModuleWindowManager();
        manager.Open("games.raffle");
        manager.Open("games.bingo");
        manager.Evict("games.raffle");
        Assert.False(manager.IsOpen("games.raffle"));
        Assert.True(manager.IsOpen("games.bingo"));
    }

    // ---- Operational separation proof ------------------------------------------------------------------------------

    [Fact] public void The_manager_takes_no_dependency_on_any_module_or_service_by_construction()
    {
        // Proves the state machine is structurally incapable of reaching into module operational state — it has no
        // constructor parameter to hold such a reference in the first place.
        var constructor = Assert.Single(typeof(ModuleWindowManager).GetConstructors());
        Assert.Empty(constructor.GetParameters());
    }

    [Fact] public void Collapse_hide_restore_and_evict_touch_only_this_modules_own_tracked_entry()
    {
        // A broader containment proof than the per-operation tests above: run every presentation transition on one
        // module and confirm a second, untouched module's state/geometry never moves as a side effect.
        var manager = new ModuleWindowManager();
        manager.Open("games.bingo");
        manager.ReportPosition("games.bingo", 11, 22);
        manager.ReportExpandedSize("games.bingo", 640, 480);
        var baseline = manager.GetSnapshot("games.bingo");

        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        manager.Restore("games.raffle");
        manager.Evict("games.raffle");
        manager.Open("games.raffle");

        Assert.Equal(baseline, manager.GetSnapshot("games.bingo"));
        Assert.True(manager.IsOpen("games.bingo"));
    }

    // ---- Reserved-key reuse (Live QA follow-up: the main tablet's own Collapse/Expand) -----------------------------
    //
    // Shell.ModuleWindowManager tracks the main VenueOS tablet's own Collapse/Expand/geometry through exactly this
    // same class, under a reserved key ("__venueos.tablet__") that can never collide with a real module id (real ids
    // are dotted/lowercase/namespaced, e.g. "core.attendance" — never leading-underscore). These tests exercise the
    // pure state machine the exact way the tablet uses it: opened once and never evicted, geometry/state managed
    // exactly like a module's, with the same guarantees.

    private const string TabletKey = "__venueos.tablet__";

    [Fact] public void A_reserved_key_behaves_exactly_like_any_other_module_id()
    {
        var manager = new ModuleWindowManager();
        manager.Open(TabletKey);
        Assert.True(manager.IsOpen(TabletKey));
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState(TabletKey));
    }

    [Fact] public void Reserved_key_collapse_then_expand_round_trips_state()
    {
        var manager = new ModuleWindowManager();
        manager.Open(TabletKey);
        manager.Collapse(TabletKey);
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState(TabletKey));
        manager.Expand(TabletKey);
        Assert.Equal(ModulePresentationState.Expanded, manager.GetState(TabletKey));
    }

    [Fact] public void Reserved_key_expanded_geometry_is_retained_across_a_collapse_expand_round_trip()
    {
        var manager = new ModuleWindowManager();
        manager.Open(TabletKey);
        manager.ReportExpandedSize(TabletKey, 980, 650);
        manager.Collapse(TabletKey);
        manager.Expand(TabletKey);
        var snapshot = manager.GetSnapshot(TabletKey)!;
        Assert.Equal(980, snapshot.Width);
        Assert.Equal(650, snapshot.Height);
    }

    [Fact] public void Moving_the_reserved_key_while_collapsed_then_expanding_uses_the_new_position_with_the_old_expanded_size()
    {
        // Mirrors Moving_a_collapsed_window_then_expanding_uses_the_new_position_with_the_old_expanded_size above —
        // this is the exact "drag the collapsed tablet header, then expand" live-QA sequence.
        var manager = new ModuleWindowManager();
        manager.Open(TabletKey);
        manager.ReportPosition(TabletKey, 24, 24);
        manager.ReportExpandedSize(TabletKey, 980, 650);
        manager.Collapse(TabletKey);
        manager.ReportPosition(TabletKey, 400, 300); // dragging the collapsed header updates position every frame
        manager.Expand(TabletKey);

        var snapshot = manager.GetSnapshot(TabletKey)!;
        Assert.Equal(400, snapshot.PosX);
        Assert.Equal(300, snapshot.PosY);
        Assert.Equal(980, snapshot.Width);
        Assert.Equal(650, snapshot.Height);
    }

    [Fact] public void Reserved_key_is_never_evicted_across_repeated_opens_the_way_the_tablet_is_never_closed()
    {
        // The tablet always exists — EnsureTabletTracked's own Open call is idempotent, never followed by an Evict.
        var manager = new ModuleWindowManager();
        Assert.True(manager.Open(TabletKey));
        manager.Collapse(TabletKey);
        Assert.False(manager.Open(TabletKey)); // idempotent refocus, not a fresh open — collapsed state survives
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState(TabletKey));
    }

    [Fact] public void The_reserved_key_preference_round_trips_through_open_exactly_like_a_modules_own_preference()
    {
        var manager = new ModuleWindowManager();
        var preference = new ModuleWindowPreference(ModulePresentationState.Collapsed, 111, 222, 980, 650);
        manager.Open(TabletKey, preference);
        Assert.Equal(ModulePresentationState.Collapsed, manager.GetState(TabletKey));
        var snapshot = manager.GetSnapshot(TabletKey)!;
        Assert.Equal(111, snapshot.PosX);
        Assert.Equal(222, snapshot.PosY);
        Assert.Equal(980, snapshot.Width);
        Assert.Equal(650, snapshot.Height);
    }

    [Fact] public void The_reserved_key_and_a_real_module_id_never_interfere_with_each_other()
    {
        // By construction/an explicit assertion: the reserved key is tracked in the exact same class as a real
        // module, but transitions on one must never move the other, and nothing here ever reaches into
        // ModuleHost/IVenueModule/any service — this class has zero such dependency (see
        // The_manager_takes_no_dependency_on_any_module_or_service_by_construction above), so this is a pure
        // same-instance isolation check, not an operational-separation check on top of it.
        var manager = new ModuleWindowManager();
        manager.Open(TabletKey);
        manager.ReportPosition(TabletKey, 5, 5);
        manager.ReportExpandedSize(TabletKey, 980, 650);
        var tabletBaseline = manager.GetSnapshot(TabletKey);

        manager.Open("games.raffle");
        manager.Collapse("games.raffle");
        manager.Hide("games.raffle");
        manager.Restore("games.raffle");
        manager.Evict("games.raffle");

        Assert.Equal(tabletBaseline, manager.GetSnapshot(TabletKey));
        Assert.True(manager.IsOpen(TabletKey));
        Assert.False(manager.IsOpen("games.raffle"));
    }
}
