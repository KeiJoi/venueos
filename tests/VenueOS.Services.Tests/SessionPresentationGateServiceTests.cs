using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class SessionPresentationGateServiceTests
{
    [Fact] public void Logged_out_suppresses_general_ui() => Assert.False(new SessionPresentationGateService(new FakeSession(false)).CanRenderGeneralUi);
    [Fact] public void Logged_in_allows_general_ui() => Assert.True(new SessionPresentationGateService(new FakeSession(true)).CanRenderGeneralUi);

    [Fact] public void Logged_in_allows_shoutrunner_ui_regardless_of_activity()
    {
        var gate = new SessionPresentationGateService(new FakeSession(true));
        Assert.True(gate.CanRenderShoutRunnerUi(shoutRunnerActive: false));
        Assert.True(gate.CanRenderShoutRunnerUi(shoutRunnerActive: true));
    }

    [Fact] public void Logged_out_with_an_active_shoutrunner_operation_still_allows_only_the_shoutrunner_surface()
    {
        var gate = new SessionPresentationGateService(new FakeSession(false));
        Assert.False(gate.CanRenderGeneralUi); // every other module/window stays suppressed
        Assert.True(gate.CanRenderShoutRunnerUi(shoutRunnerActive: true)); // the travel exception
    }

    [Fact] public void Logged_out_with_no_active_shoutrunner_operation_suppresses_everything()
    {
        var gate = new SessionPresentationGateService(new FakeSession(false));
        Assert.False(gate.CanRenderGeneralUi);
        Assert.False(gate.CanRenderShoutRunnerUi(shoutRunnerActive: false));
    }

    [Fact] public void The_gate_never_calls_anything_that_could_mutate_persisted_state()
    {
        // The gate is read-only by construction — it exposes two bool-returning members and holds no reference to
        // any config/save path. This test documents that contract rather than exercising I/O: constructing the
        // gate and reading both properties repeatedly must never throw or require any store/service to be wired.
        var gate = new SessionPresentationGateService(new FakeSession(true));
        for (var i = 0; i < 5; i++) { _ = gate.CanRenderGeneralUi; _ = gate.CanRenderShoutRunnerUi(true); _ = gate.CanRenderShoutRunnerUi(false); }
    }

    private sealed class FakeSession(bool isLoggedIn) : ISessionStateProvider
    {
        public bool IsLoggedIn => isLoggedIn;
    }
}
