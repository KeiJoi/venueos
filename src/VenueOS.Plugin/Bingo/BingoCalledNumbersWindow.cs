using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Bingo;

/// <summary>An additional, independently toggleable window owned by <see cref="VenueBingoOperatorPanel"/> — NOT
/// registered with <c>ModuleWindowManager</c> (that's the one-window-per-module-id system for the module's own
/// embedded/detached content; this is a second, auxiliary window the module chooses to also offer, per
/// NEW_MODULE_GUIDE.md §19's "there is exactly one module content implementation" rule not applying to a module's
/// own extra floating windows). Reuses <see cref="ModuleWindowHeader"/>/<see cref="UiKit.PushWindowTheme"/> for
/// chrome consistency. Driven entirely by <see cref="VenueBingoService"/>'s already-polled
/// <see cref="VenueBingoActiveGame.CalledNumbers"/> — no independent polling or game model of its own. Closing this
/// window only flips <see cref="IsOpen"/>; it never touches game state.
///
/// <b>Live-QA fixes (this pass)</b>: (1) the board previously early-returned to an empty state whenever zero
/// numbers had been called, instead of rendering the fixed 1-75 board in its uncalled state — fixed below, the
/// board is now always drawn. (2) Added a Roll & Call control so the host can operate primarily from this window
/// without reopening the full Bingo panel — it calls <see cref="VenueBingoService.RollAndCall"/> directly, the
/// EXACT SAME production method the main operator panel's own Roll & Call button calls
/// (<c>VenueBingoOperatorPanel.DrawNumberCallingSection</c>) — there is only ever one Roll & Call
/// implementation; this window does not duplicate any part of the `/random 75`/`/dice 75`/AwaitingRoll/
/// correlation/backend-submission/announcement pipeline. See <c>VenueOS.Plugin.Plugin.Draw</c> for how this
/// window (and the Card Viewer) now keep rendering independently of the main Bingo window/tablet being open —
/// this class itself is unchanged in that respect; only where its <c>Draw</c> is called from changed.</summary>
internal sealed class BingoCalledNumbersWindow(VenueBingoService service, Action onOpenBingoSettings)
{
    public bool IsOpen;

    public void Draw(VenueTheme theme)
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(420, 620), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340, 420), new Vector2(float.MaxValue, float.MaxValue));
        UiKit.PushWindowTheme(theme);
        var closeRequested = false;
        if (ImGui.Begin("###venueos-bingo-called-numbers", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            ModuleWindowHeader.Draw(theme, "grid", "Bingo — Called Numbers", onOpenBingoSettings, () => closeRequested = true);
            ImGui.BeginChild("bingo-called-content", ImGui.GetContentRegionAvail(), false);
            DrawContent(theme);
            ImGui.EndChild();
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        if (closeRequested) IsOpen = false;
    }

    private void DrawContent(VenueTheme theme)
    {
        var called = service.ActiveGame.CalledNumbers;
        // Custom Letters correction: the backend-authoritative LOCKED game snapshot's letters, never the venue's
        // current (possibly since-edited) Settings default — matches Announce's own source of truth.
        var letters = string.IsNullOrEmpty(service.ActiveGame.Snapshot?.Letters) ? "BINGO" : service.ActiveGame.Snapshot!.Letters;

        DrawRollControl(theme, called.Count);
        ImGui.Spacing();
        UiKit.Divider(theme);
        ImGui.Spacing();

        if (called.Count > 0)
        {
            UiKit.SectionHeader(theme, "Current Ball");
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Accent));
            ImGui.SetWindowFontScale(2.4f);
            ImGui.TextUnformatted(VenueBingoService.FormatBallLabel(called[^1], letters));
            ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        UiKit.SectionHeader(theme, $"Board ({called.Count} of 75 called)");

        // The complete 1-75 board is ALWAYS rendered, even at zero called numbers (live-QA fix — this used to
        // early-return to an empty state instead). It is a fixed board whose cells only ever change APPEARANCE
        // when called; a ball's label always exists, called or not.
        var calledSet = new HashSet<int>(called);
        for (var col = 0; col < 5; col++)
        {
            if (col > 0) ImGui.SameLine(0, 14);
            ImGui.BeginGroup();
            var letter = col < letters.Length ? letters[col] : "BINGO"[col];
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
            ImGui.TextUnformatted(letter.ToString());
            ImGui.PopStyleColor();
            UiKit.Divider(theme);
            for (var row = 0; row < 15; row++)
            {
                var number = col * 15 + row + 1;
                var wasCalled = calledSet.Contains(number);
                ImGui.PushStyleColor(ImGuiCol.Text, wasCalled ? UiKit.Color(theme.Tokens.Success) : UiKit.Color(theme.Tokens.TextSecondary));
                ImGui.TextUnformatted(wasCalled ? $"[{number,2}]" : $" {number,2} ");
                ImGui.PopStyleColor();
            }
            ImGui.EndGroup();
        }
    }

    /// <summary>Roll & Call, callable directly from this detached window. Enablement mirrors the main panel's own
    /// gate (<c>Lifecycle is "Draft" or "Active" or "Legacy"</c>) plus two additional guards the product
    /// requirement explicitly asked for here: never enabled while a roll is already outstanding
    /// (<see cref="VenueBingoService.IsAwaitingRoll"/> — prevents overlapping roll attempts), and never enabled
    /// once all 75 numbers have been called.</summary>
    private void DrawRollControl(VenueTheme theme, int calledCount)
    {
        var lifecycle = service.ActiveGame.Lifecycle;
        var gameType = service.ActiveGame.Snapshot?.GameType ?? service.Defaults.GameType;
        var canCall = lifecycle is "Draft" or "Active" or "Legacy" && !service.IsAwaitingRoll && calledCount < 75;

        ImGui.TextUnformatted($"Game Type: {gameType}");
        ImGui.SameLine();
        UiKit.StatusBadge(theme, $"{calledCount} / 75 called", ToastLevel.Information);

        var statusLabel = lifecycle is null
            ? "No active game"
            : service.IsAwaitingRoll
                ? $"Awaiting {(service.PendingRollMode == BingoRollMode.Dice ? "/dice 75" : "/random 75")}..."
                : calledCount >= 75 ? "All 75 numbers called" : "Ready";
        ImGui.TextDisabled(statusLabel);

        ImGui.BeginDisabled(!canCall);
        if (UiKit.PrimaryButton(theme, "Roll & Call")) service.RollAndCall();
        ImGui.EndDisabled();
    }
}
