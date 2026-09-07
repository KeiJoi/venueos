namespace VenueOS.Modules.Operations.Bingo;

// Byte-for-byte port of backend/lib/cardgen.js (itself a verbatim transcription of backend/public/app.js's
// hashSeed/mulberry32/shuffle/generateColumn/generateCard/cardHasBingo — see docs/BINGO_V2_PROTOCOL.md §9 and
// §24/§12 of the forensic audit: card generation is a Category A "must preserve exactly" contract). Do NOT change
// the bit operations here without updating cardgen.js and app.js in lockstep — a one-bit difference produces a
// completely different, mismatched card layout between host and player views.
//
// Known vector (verified against backend/lib/cardgen.js after the live-QA Card 1 fix below): seed "seedC",
// cardIndex 0 -> [[9,28,35,47,75],[13,24,44,49,73],[1,25,"free",54,64],[11,26,42,53,61],[5,30,34,56,67]]
// (row-major, [2][2] free). See BingoCardGeneratorTests for the exact assertion and further golden vectors.

/// <summary>One cell of a generated Bingo card: either a called-able number 1-75, or the free center space.</summary>
public readonly struct BingoCardCell(int? number)
{
    public int? Number { get; } = number;
    public bool IsFree => Number is null;
    public static readonly BingoCardCell Free = new(null);
    public override string ToString() => IsFree ? "free" : Number!.Value.ToString();
}

public static class BingoCardGenerator
{
    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>FNV-1a-style 32-bit hash over UTF-16 code units — matches JS's `seed.charCodeAt(i)` exactly for the
    /// BMP characters every real seed string uses.</summary>
    public static uint HashSeed(string seed)
    {
        var h = FnvOffsetBasis;
        unchecked { foreach (var c in seed) { h ^= c; h *= FnvPrime; } }
        return h;
    }

    /// <summary>Mulberry32 PRNG, returning a stateful `next()` delegate exactly like the JS closure — successive
    /// calls share and advance the same 32-bit state `t`.</summary>
    public static Func<double> Mulberry32(uint seed)
    {
        var t = seed;
        return () =>
        {
            unchecked
            {
                t += 0x6D2B79F5u;
                var x = t ^ (t >> 15);
                var r = x * (t | 1u);
                var y = r ^ (r >> 7);
                r ^= r + y * (r | 61u);
                var z = r ^ (r >> 14);
                return z / 4294967296.0;
            }
        };
    }

    public static List<int> Shuffle(IReadOnlyList<int> values, Func<double> rng)
    {
        var copy = new List<int>(values);
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(rng() * (i + 1));
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    public static List<int> GenerateColumn(Func<double> rng, int start, int end)
    {
        var numbers = new List<int>();
        for (var i = start; i <= end; i++) numbers.Add(i);
        return Shuffle(numbers, rng).Take(5).ToList();
    }

    /// <summary>grid[row][col]; grid[2][2] is always the free space.</summary>
    public static BingoCardCell[][] GenerateCard(string seed)
    {
        var rng = Mulberry32(HashSeed(seed));
        // Column order (and therefore RNG draw order) matters: B, I, N, G, O, drawn from the SAME rng instance
        // sequentially — never reset between columns.
        var columns = new[]
        {
            GenerateColumn(rng, 1, 15),
            GenerateColumn(rng, 16, 30),
            GenerateColumn(rng, 31, 45),
            GenerateColumn(rng, 46, 60),
            GenerateColumn(rng, 61, 75),
        };

        var grid = new BingoCardCell[5][];
        for (var row = 0; row < 5; row++)
        {
            grid[row] = new BingoCardCell[5];
            for (var col = 0; col < 5; col++)
                grid[row][col] = row == 2 && col == 2 ? BingoCardCell.Free : new BingoCardCell(columns[col][row]);
        }
        return grid;
    }

    /// <summary>Live-QA correction (Card 1 mismatch against the player browser): this previously special-cased
    /// <paramref name="cardIndex"/> 0 to use the bare <paramref name="masterSeed"/> with no suffix — matching
    /// neither authoritative implementation. The browser client (app.js's <c>createCard(index)</c>, the ONLY thing
    /// that actually renders a player's cards) does <c>const seed = `${masterSeed}_${index}`;</c> UNCONDITIONALLY,
    /// and the donor plugin's own <c>GenerateCardGrid(masterSeed, cardIndex)</c> does the identical
    /// <c>$"{masterSeed}_{cardIndex}"</c> with no special case either — neither has ever treated index 0
    /// differently. This method's bare-seed special case for index 0 was therefore this port's own bug: live QA
    /// found Card 1 (index 0) mismatching the player's browser cell-for-cell while every other card (index 1-15)
    /// matched exactly, which is exactly what this specific bug predicts. The backend's own
    /// <c>cardgen.js</c>/<c>generateCardForIndex</c> had the identical bug and has been fixed identically — see
    /// its doc comment. Every index, including 0, now uses the same unconditional
    /// <c>"{masterSeed}_{cardIndex}"</c> rule.</summary>
    public static BingoCardCell[][] GenerateCardForIndex(string masterSeed, int cardIndex) =>
        GenerateCard($"{masterSeed}_{cardIndex}");

    public static List<int> CardNumbers(BingoCardCell[][] grid)
    {
        var numbers = new List<int>();
        foreach (var row in grid) foreach (var cell in row) if (!cell.IsFree) numbers.Add(cell.Number!.Value);
        return numbers;
    }

    /// <summary>Matches app.js's cardHasBingo / cardgen.js's cardHasBingo rule shape exactly: Four Corners, Blackout,
    /// Two Lines (>=2 of {row, column, either diagonal}), else Single Line (>=1).</summary>
    public static bool CardHasBingo(BingoCardCell[][] grid, IReadOnlySet<int> daubedNumbers, string? gameType)
    {
        bool IsDaubed(BingoCardCell cell) => cell.IsFree || daubedNumbers.Contains(cell.Number!.Value);
        var lower = (gameType ?? "").Trim().ToLowerInvariant();

        if (lower == "four corners") return IsDaubed(grid[0][0]) && IsDaubed(grid[0][4]) && IsDaubed(grid[4][0]) && IsDaubed(grid[4][4]);

        if (lower == "blackout")
        {
            for (var row = 0; row < 5; row++) for (var col = 0; col < 5; col++) if (!IsDaubed(grid[row][col])) return false;
            return true;
        }

        var lines = 0;
        for (var row = 0; row < 5; row++)
        {
            var complete = true;
            for (var col = 0; col < 5; col++) if (!IsDaubed(grid[row][col])) { complete = false; break; }
            if (complete) lines++;
        }
        for (var col = 0; col < 5; col++)
        {
            var complete = true;
            for (var row = 0; row < 5; row++) if (!IsDaubed(grid[row][col])) { complete = false; break; }
            if (complete) lines++;
        }
        var diag = true;
        for (var i = 0; i < 5; i++) if (!IsDaubed(grid[i][i])) { diag = false; break; }
        if (diag) lines++;
        var anti = true;
        for (var i = 0; i < 5; i++) if (!IsDaubed(grid[i][4 - i])) { anti = false; break; }
        if (anti) lines++;

        return lower == "two lines" ? lines >= 2 : lines >= 1;
    }

    /// <summary>Live-QA addition (host Card Viewer visual states): which specific cells belong to a CURRENTLY
    /// COMPLETE pattern, for highlighting — e.g. Card Viewer wants to show a "BINGO" badge/highlight on a card that
    /// has actually won, not just render daubs. This is deliberately a SEPARATE method from <see cref="CardHasBingo"/>
    /// (which is a live, tested, "must preserve exactly" contract — see this file's header) rather than a
    /// modification of it, even though the underlying geometry (rows/cols/diagonals/corners/blackout) is identical;
    /// this method returns every cell belonging to ANY complete line/group once the pattern's threshold is met (not
    /// necessarily the minimal winning set) — sufficient for "which cells to highlight," not used for validation.
    /// Returns an empty set if the card does not currently satisfy <paramref name="gameType"/>'s pattern at
    /// all.</summary>
    public static HashSet<(int Row, int Col)> WinningCells(BingoCardCell[][] grid, IReadOnlySet<int> daubedNumbers, string? gameType)
    {
        bool IsDaubed(BingoCardCell cell) => cell.IsFree || daubedNumbers.Contains(cell.Number!.Value);
        var lower = (gameType ?? "").Trim().ToLowerInvariant();
        var result = new HashSet<(int, int)>();

        if (lower == "four corners")
        {
            if (IsDaubed(grid[0][0]) && IsDaubed(grid[0][4]) && IsDaubed(grid[4][0]) && IsDaubed(grid[4][4]))
                foreach (var cell in new[] { (0, 0), (0, 4), (4, 0), (4, 4) }) result.Add(cell);
            return result;
        }

        if (lower == "blackout")
        {
            for (var row = 0; row < 5; row++) for (var col = 0; col < 5; col++) if (!IsDaubed(grid[row][col])) return result; // incomplete — empty set
            for (var row = 0; row < 5; row++) for (var col = 0; col < 5; col++) result.Add((row, col));
            return result;
        }

        var needed = lower == "two lines" ? 2 : 1;
        var lines = new List<(int Row, int Col)[]>();
        for (var row = 0; row < 5; row++) lines.Add(Enumerable.Range(0, 5).Select(col => (row, col)).ToArray());
        for (var col = 0; col < 5; col++) lines.Add(Enumerable.Range(0, 5).Select(row => (row, col)).ToArray());
        lines.Add([(0, 0), (1, 1), (2, 2), (3, 3), (4, 4)]);
        lines.Add([(0, 4), (1, 3), (2, 2), (3, 1), (4, 0)]);

        var completeLines = lines.Where(line => line.All(cell => IsDaubed(grid[cell.Row][cell.Col]))).ToList();
        if (completeLines.Count < needed) return result; // threshold not met — empty set, even if some individual lines are complete
        foreach (var line in completeLines) foreach (var cell in line) result.Add(cell);
        return result;
    }
}
