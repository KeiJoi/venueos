namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The exact arrival decision for a same-Data-Center World Visit — extracted as a small pure function so
/// the live-verified bug it fixes can be regression-tested without a live game/Dalamud context (the enclosing
/// polling loop, <c>ShoutRunnerAutomationService.WaitForSameDataCenterTransferAsync</c>, is otherwise untestable —
/// see <c>NEW_MODULE_GUIDE.md</c> §30's unsafe-boundary testing convention).
///
/// <b>Live-verified bug this fixes:</b> a character on Halicarnassus routed toward Cuchulainn (both Dynamis) was
/// first moved by Lifestream to a World-Visit-capable city (Ul'dah) before Lifestream had actually performed the
/// World Visit itself — an ordinary, expected intermediate step. The previous logic treated the settle-after-that-
/// intermediate-transition as if it were the transfer's own completion: it saw "not currently transitioning" +
/// "we did see a transition" + "current World is still Halicarnassus" and immediately reported
/// <c>Arrived at Halicarnassus instead of Cuchulainn</c>, faulting the whole RUN before the actual World Visit ever
/// ran. <c>ShoutRunnerAutomationService.WaitForCrossDataCenterTransferAsync</c>'s own equivalent branch already gets this right — it only
/// trusts a "wrong World" result once Lifestream itself also reports it is no longer busy, i.e. once Lifestream
/// believes its ENTIRE operation (including any intermediate city travel) is finished. This reproduces that same,
/// already-proven criterion for the same-Data-Center path instead of inventing a new one.</summary>
public static class ShoutRunnerSameDataCenterArrival
{
    public enum Decision { Continue, Success, Failed }

    /// <param name="transitioning">The character is currently mid-zone-transition (<c>BetweenAreas</c>/<c>BetweenAreas51</c>).</param>
    /// <param name="seenTransition">At least one transition has been observed since this transfer began.</param>
    /// <param name="currentWorld">The character's current World, or null/empty if unknown.</param>
    /// <param name="lifestreamBusy">Lifestream's own busy state, or null if it could not be determined right now —
    /// treated the same as "still busy" for the purpose of a wrong-World failure (never trust a wrong-World result
    /// on ambiguous information; keep waiting instead).</param>
    public static Decision Evaluate(bool isLoggedIn, bool hasLocalPlayer, bool transitioning, bool seenTransition, string? currentWorld, string targetWorld, bool? lifestreamBusy)
    {
        if (transitioning || !seenTransition || !isLoggedIn || !hasLocalPlayer) return Decision.Continue;

        var onTarget = !string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, targetWorld, StringComparison.OrdinalIgnoreCase);
        var confirmedNotBusy = lifestreamBusy == false;

        if (onTarget) return confirmedNotBusy ? Decision.Success : Decision.Continue;
        if (!string.IsNullOrEmpty(currentWorld) && confirmedNotBusy) return Decision.Failed;
        return Decision.Continue;
    }
}
