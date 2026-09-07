using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Bingo;

/// <summary>A second auxiliary window owned by <see cref="VenueBingoOperatorPanel"/> (see
/// <see cref="BingoCalledNumbersWindow"/>'s doc comment for why this isn't a <c>ModuleWindowManager</c> entry).
///
/// <b>Live-QA redesign (this pass)</b>: the previous version showed exactly one card at a time behind a
/// zero-based "Card #" -/+ selector (which could even display "0", confusing for a host used to "Card 1"). The
/// donor's own workflow — and the explicit product requirement — is ALL of a player's cards on one scrollable
/// page, numbered 1..N (never 0), so a host checking a Bingo claim can scan every card at once instead of
/// stepping through them. The index selector is gone entirely.
///
/// <b>Live-QA daub-sync fix (this pass)</b>: the previous version rendered a card's raw contents only — it never
/// read daub state at all, so a player's browser daubs never appeared here no matter what the backend had
/// persisted. Root cause: <see cref="VenueBingoActiveGame"/> had no <c>Daubs</c> field at all (the wire response
/// carried one, but it was deserialized into an unused, untyped <c>Dictionary&lt;string, object&gt;</c> and never
/// mapped into any service state) — the daub state stopped propagating at the very first step, before it ever
/// reached this window's model. Fixed by <see cref="VenueBingoActiveGame.Daubs"/>/<see cref="VenueBingoService.GetDaubedNumbers"/>;
/// this window now reads that live, backend-authoritative state every frame (via <see cref="service"/>) exactly
/// like it already read <c>CalledNumbers</c>/<c>Snapshot</c> — no independent local daub truth is introduced here.
/// Because it reads <see cref="VenueBingoService.ActiveGame"/> fresh every draw, it updates automatically on the
/// existing ~5s <see cref="VenueBingoService.Tick"/> poll cadence while left open; no separate polling loop was
/// added. <see cref="ShowPlayer"/>'s caller (<c>VenueBingoOperatorPanel</c>) also kicks one immediate
/// <c>PollAsync</c> so opening the viewer to check a claim doesn't wait up to 5s for fresh daub state.
///
/// Card numbers/contents come from <see cref="BingoCardGenerator"/> (the same deterministic algorithm the player
/// browser and backend use); called numbers and game type come from <see cref="VenueBingoActiveGame.Snapshot"/>/
/// <c>CalledNumbers</c> (backend-authoritative); daubs come from <see cref="VenueBingoActiveGame.Daubs"/>
/// (backend-authoritative). Per the reconstruction brief, VenueOS does not reproduce/download custom image
/// assets, so falling back to the configured color scheme for ImGui rendering is the correct, by-construction
/// behavior, not a missing feature. Closing this window never affects game state.</summary>
internal sealed class BingoPlayerCardViewerWindow(VenueBingoService service, Action onOpenBingoSettings)
{
    public bool IsOpen;
    private string? selectedSeed;

    /// <summary>Preselects a player (called from the roster's/payout section's "View Cards" button) and opens the
    /// window.</summary>
    public void ShowPlayer(string seed) { selectedSeed = seed; IsOpen = true; }

    public void Draw(VenueTheme theme)
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(480, 640), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 420), new Vector2(float.MaxValue, float.MaxValue));
        UiKit.PushWindowTheme(theme);
        var closeRequested = false;
        if (ImGui.Begin("###venueos-bingo-card-viewer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            ModuleWindowHeader.Draw(theme, "grid", "Bingo — Player Cards", onOpenBingoSettings, () => closeRequested = true);
            ImGui.BeginChild("bingo-card-viewer-content", ImGui.GetContentRegionAvail(), false);
            DrawContent(theme);
            ImGui.EndChild();
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        if (closeRequested) IsOpen = false;
    }

    private void DrawContent(VenueTheme theme)
    {
        var players = service.ActiveGame.Players;
        if (players.Count == 0) { UiKit.EmptyState(theme, "No players yet", "Grant cards to a player from the main Bingo screen."); return; }

        var seeds = players.Keys.ToArray();
        var index = selectedSeed is null ? 0 : Array.IndexOf(seeds, selectedSeed);
        if (index < 0) index = 0;
        var names = seeds.Select(s => $"{players[s].Name} ({s[..Math.Min(6, s.Length)]})").ToArray();
        if (Forms.ComboField(theme, "Player", names, ref index)) selectedSeed = seeds[index];
        selectedSeed ??= seeds[index];

        var player = players[selectedSeed];
        var paid = player.PaidCount ?? player.Count;
        var comp = player.CompCount ?? 0;
        var totalCards = Math.Max(1, paid + comp);
        var gameType = service.ActiveGame.Snapshot?.GameType ?? "Single Line";
        ImGui.TextUnformatted(player.Name);
        ImGui.TextDisabled($"Paid {paid} · Comp {comp} · Total {totalCards}  ·  {gameType}");

        ImGui.Spacing();
        UiKit.Divider(theme);
        ImGui.Spacing();

        var calledSet = new HashSet<int>(service.ActiveGame.CalledNumbers);
        var colors = service.ActiveGame.Snapshot?.Colors ?? new BingoColors();
        var letters = string.IsNullOrEmpty(service.ActiveGame.Snapshot?.Letters) ? "BINGO" : service.ActiveGame.Snapshot!.Letters;

        // ALL cards, tiled across available width and wrapping onto additional rows as needed (live-QA correction
        // — the previous one-card-per-row layout forced excessive scrolling for a 10-16 card player). No index
        // selector, no previous/next, never "Card 0" — human numbering starts at 1 (cardIndex 0 is displayed as
        // "Card 1"). Column count comes from the pure, unit-tested BingoCardViewerLayout — cards are never shrunk
        // to force an extra column onto a row; a narrow window simply gets fewer, full-size columns.
        ImGui.BeginChild("bingo-all-cards", ImGui.GetContentRegionAvail(), false);
        var columns = BingoCardViewerLayout.CalculateColumnCount(ImGui.GetContentRegionAvail().X);
        for (var cardIndex = 0; cardIndex < totalCards; cardIndex++)
        {
            var grid = BingoCardGenerator.GenerateCardForIndex(selectedSeed, cardIndex);
            var daubedSet = new HashSet<int>(service.GetDaubedNumbers(selectedSeed, cardIndex));
            var winningCells = BingoCardGenerator.WinningCells(grid, daubedSet, gameType);

            if (cardIndex % columns != 0) ImGui.SameLine();
            ImGui.PushID(cardIndex);
            ImGui.BeginGroup();
            if (winningCells.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
                ImGui.TextUnformatted($"Card {cardIndex + 1} — BINGO!");
                ImGui.PopStyleColor();
            }
            else ImGui.TextUnformatted($"Card {cardIndex + 1}");
            DrawCard(theme, grid, letters, colors, calledSet, daubedSet, winningCells);
            ImGui.EndGroup();
            ImGui.PopID();

            // End-of-row spacing: only after the LAST tile in a completed row (or the final card), so the
            // vertical gap between rows doesn't also appear between horizontally-adjacent tiles.
            var isLastInRow = cardIndex % columns == columns - 1;
            var isLastCard = cardIndex == totalCards - 1;
            if (isLastInRow || isLastCard) { ImGui.Spacing(); UiKit.Divider(theme); ImGui.Spacing(); }
        }
        ImGui.EndChild();
    }

    /// <summary>Renders one 5x5 card with four visually distinct cell states — ordinary uncalled, called-but-not-
    /// daubed, player-daubed, and free — plus a winning-cell highlight that overrides daubed styling when this
    /// card currently satisfies the active game type's pattern. Called vs. daubed are deliberately never
    /// conflated: a host verifying a claim needs to see both "this number was called" AND "the player actually
    /// marked it" as separate facts.</summary>
    private static void DrawCard(VenueTheme theme, BingoCardCell[][] grid, string letters, BingoColors colors, HashSet<int> calledSet, HashSet<int> daubedSet, HashSet<(int Row, int Col)> winningCells)
    {
        var headerHex = string.IsNullOrEmpty(colors.Header) ? theme.Tokens.Accent : colors.Header;
        var textHex = string.IsNullOrEmpty(colors.Text) ? theme.Tokens.TextPrimary : colors.Text;
        var cardHex = string.IsNullOrEmpty(colors.Card) ? theme.Tokens.RaisedSurface : colors.Card;
        var cellSize = new Vector2(52, 44);

        for (var col = 0; col < 5; col++)
        {
            if (col > 0) ImGui.SameLine(0, 4);
            var letter = col < letters.Length ? letters[col] : "BINGO"[col];
            ImGui.PushStyleColor(ImGuiCol.Button, UiKit.Color(headerHex));
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
            ImGui.Button($"{letter}##header{col}", cellSize);
            ImGui.PopStyleColor(2);
        }

        for (var row = 0; row < 5; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                if (col > 0) ImGui.SameLine(0, 4);
                var cell = grid[row][col];
                var isWinning = winningCells.Contains((row, col));
                var isDaubed = !cell.IsFree && daubedSet.Contains(cell.Number!.Value);
                var isCalledOnly = !cell.IsFree && !isDaubed && calledSet.Contains(cell.Number!.Value);

                var backgroundHex = isWinning ? theme.Tokens.Accent
                    : cell.IsFree ? theme.Tokens.Selected
                    : isDaubed ? theme.Tokens.Success
                    : isCalledOnly ? theme.Tokens.Warning
                    : cardHex;
                var label = cell.IsFree ? "FREE" : cell.Number!.Value.ToString();

                ImGui.PushStyleColor(ImGuiCol.Button, UiKit.Color(backgroundHex));
                ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(textHex));
                ImGui.Button($"{label}##r{row}c{col}", cellSize);
                ImGui.PopStyleColor(2);
                if (isCalledOnly) UiKit.Tooltip("Called, but the player has not daubed this cell yet.");
                else if (isDaubed && !isWinning) UiKit.Tooltip("Daubed by the player.");
            }
        }
    }
}
