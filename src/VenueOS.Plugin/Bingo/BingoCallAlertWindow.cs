using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Bingo;

/// <summary>Live-QA addition: a host-attention layer over the backend's already-authoritative Bingo caller
/// history — a real Bingo call was previously easy to miss (only a small line inside the Payout Ledger). This
/// window automatically appears when <see cref="VenueBingoService.PendingAlertCallers"/> is non-empty and
/// disappears the instant it's empty again — there is deliberately NO separate <c>IsOpen</c> bool that could fall
/// out of sync with that authoritative list; visibility is a pure function of backend-derived state, exactly like
/// this class's sibling detached windows (<see cref="BingoCalledNumbersWindow"/>, <see cref="BingoPlayerCardViewerWindow"/>)
/// are pure functions of <see cref="VenueBingoService.ActiveGame"/>.
///
/// This NEVER invents a second winner state: a caller only ever appears here because the backend's own
/// <see cref="VenueBingoActiveGame.BingoCallers"/> already accepted them (see
/// <see cref="VenueBingoService.DetectNewBingoCallers"/> — the alert layer only tracks which of those accepted
/// occurrences the local host has already been shown). "Dismiss" is a LOCAL acknowledgment only
/// (<see cref="VenueBingoService.DismissBingoAlert"/>/<see cref="VenueBingoService.DismissAllBingoAlerts"/>) — it
/// never touches the backend, the caller history, payouts, daubs, called numbers, or the room itself.
///
/// "View Cards" reuses the EXISTING <see cref="BingoPlayerCardViewerWindow"/> (the same responsive tiled viewer
/// this pass rebuilt) rather than a second card-viewing implementation. "Payout Details" is the documented
/// smaller-scope fallback (docs/BINGO_V2_PROTOCOL.md's Bingo Call Alert section): a compact, read-only inline
/// summary sourced directly from <see cref="VenueBingoActiveGame.Payouts"/>/<c>SplitAmount</c> (never computed
/// here — this widget performs no payout arithmetic of its own), plus an "Open Bingo" button that navigates the
/// host to the full operational screen (where the durable Payout Ledger already lives) — focusing a subsection of
/// that screen directly was judged too invasive for this pass.
///
/// Drawn independently every frame by <c>Plugin.Draw</c> — see that method and <see cref="VenueBingoOperatorPanel"/>'s
/// doc comment for why: closing/hiding the main Bingo window (or having only Called Numbers open) must never
/// suppress this alert.</summary>
internal sealed class BingoCallAlertWindow(VenueBingoService service, Action<string> onViewCards, Action onOpenBingo)
{
    public void Draw(VenueTheme theme)
    {
        var pending = service.PendingAlertCallers;
        if (pending.Count == 0) return;

        ImGui.SetNextWindowSize(new Vector2(420, 360), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340, 260), new Vector2(float.MaxValue, float.MaxValue));
        UiKit.PushWindowTheme(theme);
        if (ImGui.Begin("###venueos-bingo-call-alert", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            // The header's own close button dismisses EVERY pending caller — there is no other way to make this
            // window go away, since its visibility is driven entirely by PendingAlertCallers being non-empty.
            ModuleWindowHeader.Draw(theme, "star", "Bingo Call Alert", onOpenBingo, service.DismissAllBingoAlerts);
            ImGui.BeginChild("bingo-call-alert-content", ImGui.GetContentRegionAvail(), false);
            DrawContent(theme, pending);
            ImGui.EndChild();
        }
        ImGui.End();
        UiKit.PopWindowTheme();
    }

    private void DrawContent(VenueTheme theme, IReadOnlyList<BingoCaller> pending)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
        ImGui.SetWindowFontScale(1.6f);
        ImGui.TextUnformatted("BINGO CALLED!");
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        ImGui.Spacing();
        UiKit.Divider(theme);
        ImGui.Spacing();

        // Backend-authoritative payout obligations, looked up by seed — displayed read-only; never computed here.
        var payoutsBySeed = service.ActiveGame.Payouts.ToDictionary(p => p.WinnerSeed, p => p);

        foreach (var caller in pending.ToArray()) // ToArray: Dismiss below mutates the live list mid-iteration
        {
            ImGui.PushID(caller.Seed + "|" + caller.Phase);
            ImGui.TextUnformatted(caller.Name);
            ImGui.SameLine();
            ImGui.TextDisabled(FormatTimestamp(caller.Timestamp));

            if (payoutsBySeed.TryGetValue(caller.Seed, out var payout))
                ImGui.TextDisabled($"Owed {payout.TotalOwed} · Paid {payout.ConfirmedPaid} · Outstanding {payout.Outstanding} · {payout.Status}");
            else
                ImGui.TextDisabled("No payout obligation recorded yet — press Payout Details / Open Bingo to sync.");

            if (UiKit.PrimaryButton(theme, $"View Cards##{caller.Seed}")) onViewCards(caller.Seed);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, $"Payout Details##{caller.Seed}")) onOpenBingo();
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, $"Dismiss##{caller.Seed}")) service.DismissBingoAlert(caller.Seed, caller.Phase);

            ImGui.Spacing();
            UiKit.Divider(theme);
            ImGui.Spacing();
            ImGui.PopID();
        }
    }

    private static string FormatTimestamp(long unixMillis) => DateTimeOffset.FromUnixTimeMilliseconds(unixMillis).LocalDateTime.ToString("h:mm:ss tt");
}
