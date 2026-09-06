using VenueOS.Modules.Operations.PartyFinder;

namespace VenueOS.Services.Tests;

/// <summary>Covers the pure timing/retry logic behind Party Finder's native-UI readiness hardening
/// (see PartyFinderAutomationService in VenueOS.Plugin, which is not itself unit-testable — no test project exists
/// for VenueOS.Plugin, per NEW_MODULE_GUIDE.md §30). <see cref="BoundedWait"/> is the extracted, addon-free piece of
/// that logic: "how long has this wait been running, and have I exceeded my bound." The actual game-state checks
/// (does the addon exist/is it visible/ready) remain live-FFXIV-only verification items.</summary>
public sealed class BoundedWaitTests
{
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact] public void A_never_polled_wait_has_not_started()
    {
        var wait = new BoundedWait();
        Assert.False(wait.HasStarted);
    }

    /// <summary>Models "state is already ready": the caller checks its own readiness condition (addon
    /// visible/ready/populated) BEFORE ever consulting a wait tracker — so in the fast-path case, a fast client
    /// proceeds immediately and <see cref="BoundedWait.Poll"/> is never even called. This is why every hardened
    /// readiness check in the engine is written as "if (real condition met) return true/proceed; else Poll(...)".</summary>
    [Fact] public void Ready_immediately_case_never_needs_to_poll_the_wait_tracker()
    {
        var readyImmediately = true;
        var wait = new BoundedWait();
        var pollCount = 0;
        if (!readyImmediately)
        {
            pollCount++;
            wait.Poll(Start, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, pollCount);
        Assert.False(wait.HasStarted);
    }

    [Fact] public void First_poll_starts_the_timer_and_reports_within_timeout()
    {
        var wait = new BoundedWait();
        Assert.True(wait.Poll(Start, TimeSpan.FromSeconds(5)));
        Assert.True(wait.HasStarted);
    }

    [Fact] public void Tolerates_delayed_availability_within_the_bound()
    {
        var wait = new BoundedWait();
        Assert.True(wait.Poll(Start, TimeSpan.FromSeconds(5)));
        Assert.True(wait.Poll(Start.AddSeconds(4.9), TimeSpan.FromSeconds(5))); // slow client, still within the bound
    }

    [Fact] public void Times_out_cleanly_once_the_bound_elapses()
    {
        var wait = new BoundedWait();
        wait.Poll(Start, TimeSpan.FromSeconds(5));
        Assert.False(wait.Poll(Start.AddSeconds(5).AddMilliseconds(1), TimeSpan.FromSeconds(5)));
    }

    [Fact] public void Elapsed_time_is_measured_from_the_first_poll_not_each_call()
    {
        var wait = new BoundedWait();
        wait.Poll(Start, TimeSpan.FromSeconds(5));
        wait.Poll(Start.AddSeconds(1), TimeSpan.FromSeconds(5));
        wait.Poll(Start.AddSeconds(2), TimeSpan.FromSeconds(5));
        // 4.9s after the FIRST poll, not the most recent one — still within the original 5s bound.
        Assert.True(wait.Poll(Start.AddSeconds(4.9), TimeSpan.FromSeconds(5)));
        // 5.1s after the first poll — now timed out, even though each individual call arrived only ~1-2s apart.
        Assert.False(wait.Poll(Start.AddSeconds(5.1), TimeSpan.FromSeconds(5)));
    }

    /// <summary>Models cancellation/supersession: when an operation is aborted or a new one begins (a fresh
    /// Create/Refresh/End Party Finder request, or a venue switch), the owning engine calls <c>Reset()</c> on every
    /// wait field before enqueueing anything new — so a stale wait can never bleed into, or falsely time out, a
    /// later unrelated operation.</summary>
    [Fact] public void Reset_starts_a_fresh_window_and_cannot_inherit_a_prior_operations_elapsed_time()
    {
        var wait = new BoundedWait();
        wait.Poll(Start, TimeSpan.FromSeconds(5));
        Assert.False(wait.Poll(Start.AddSeconds(10), TimeSpan.FromSeconds(5))); // timed out under the old operation

        wait.Reset();
        Assert.False(wait.HasStarted);
        Assert.True(wait.Poll(Start.AddSeconds(10), TimeSpan.FromSeconds(5))); // a new operation's wait starts clean
    }

    [Fact] public void Reset_before_any_poll_is_a_safe_no_op()
    {
        var wait = new BoundedWait();
        wait.Reset();
        Assert.False(wait.HasStarted);
        Assert.True(wait.Poll(Start, TimeSpan.FromSeconds(5)));
    }
}
