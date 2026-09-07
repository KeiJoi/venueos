namespace VenueOS.Modules.Operations.Bingo;

/// <summary>Pure, Dalamud-free layout math for the host Card Viewer's responsive tiled grid (live-QA correction —
/// the previous one-card-per-row layout wasted horizontal space and forced excessive scrolling for a 10-16 card
/// player). Kept separate from <c>VenueOS.Plugin.Bingo.BingoPlayerCardViewerWindow</c> (which has a Dalamud
/// dependency no test project references) purely so this arithmetic can be unit-tested directly, matching this
/// codebase's established "pure logic in Modules.Operations, ImGui rendering in Plugin" boundary
/// (<see cref="BingoRollCorrelator"/>/<see cref="BingoRollSenderIdentity"/> use the identical split for the same
/// reason).</summary>
public static class BingoCardViewerLayout
{
    /// <summary>A card's own rendered footprint is fixed (5 cells x ~52px + inter-cell spacing ~276px) plus a
    /// safety margin for the "Card N" label/divider and inter-card gutter — cards are never shrunk to force an
    /// extra column onto a row (explicit product requirement), so this is the one number that determines how many
    /// whole cards fit, not a per-card size that flexes to fill leftover width.</summary>
    public const float DefaultMinCardWidth = 300f;

    /// <summary>Horizontal gap reserved between adjacent card tiles in the same row.</summary>
    public const float DefaultSpacing = 16f;

    /// <summary>How many whole, full-size card tiles fit side by side in <paramref name="availableWidth"/>. Never
    /// returns fewer than 1 (a single card always gets its own row even in an unreasonably narrow window) and
    /// never shrinks <paramref name="minCardWidth"/> to force in an extra column — any leftover width on a row
    /// simply goes unused as margin.</summary>
    public static int CalculateColumnCount(float availableWidth, float minCardWidth = DefaultMinCardWidth, float spacing = DefaultSpacing)
    {
        if (minCardWidth <= 0) return 1;
        if (availableWidth < minCardWidth) return 1;
        var columns = (int)((availableWidth + spacing) / (minCardWidth + spacing));
        return Math.Max(1, columns);
    }

    /// <summary>How many rows are needed to lay out <paramref name="totalCards"/> across <paramref name="columns"/>
    /// per row.</summary>
    public static int CalculateRowCount(int totalCards, int columns) =>
        columns <= 0 || totalCards <= 0 ? 0 : (int)Math.Ceiling(totalCards / (double)columns);
}
