using System.Globalization;
using ClosedXML.Excel;

namespace VenueOS.Modules.Operations.Raffle;

/// <summary>Reconstructs the donor's three-sheet workbook (Settings, Summary, Participants), extended with a
/// "Home World" participant column so VenueOS's HomeWorld-aware identity round-trips through export/import.</summary>
public static class RaffleXlsxExporter
{
    public static void Export(LocalRaffle raffle, Stream stream)
    {
        using var workbook = new XLWorkbook();
        WriteSettingsSheet(workbook, raffle);
        WriteSummarySheet(workbook, raffle);
        WriteParticipantsSheet(workbook, raffle);
        workbook.SaveAs(stream);
    }

    private static void WriteSettingsSheet(XLWorkbook workbook, LocalRaffle raffle)
    {
        var sheet = workbook.Worksheets.Add("Settings");
        sheet.Cell(1, 1).Value = "Setting";
        sheet.Cell(1, 2).Value = "Value";

        var rows = new (string Key, object Value)[]
        {
            ("Raffle Name", raffle.Name),
            ("Created At", raffle.CreatedAt.ToLocalTime().ToString("u", CultureInfo.InvariantCulture)),
            ("Winner", raffle.WinnerName ?? string.Empty),
            ("Starting Pot", raffle.Settings.StartingPot),
            ("Ticket Cost", raffle.Settings.TicketCost),
            ("Prize %", raffle.Settings.PrizePercentage),
            ("Paid Tickets per Bonus", raffle.Settings.PaidTicketsForFree),
            ("Free Tickets per Bonus", raffle.Settings.FreeTicketsPerBlock),
        };

        for (var i = 0; i < rows.Length; i++)
        {
            var row = i + 2;
            sheet.Cell(row, 1).Value = rows[i].Key;
            switch (rows[i].Value)
            {
                case float f: sheet.Cell(row, 2).Value = f; break;
                case int n: sheet.Cell(row, 2).Value = n; break;
                default: sheet.Cell(row, 2).Value = rows[i].Value.ToString(); break;
            }
        }
    }

    private static void WriteSummarySheet(XLWorkbook workbook, LocalRaffle raffle)
    {
        var sheet = workbook.Worksheets.Add("Summary");
        string[] headers = ["Raffle Name", "Created At", "Winner", "Starting Pot", "Ticket Cost", "Prize %", "Paid Tickets", "Free Tickets", "Total Tickets", "Running Pot", "Prize Pot", "House Take"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        sheet.Cell(2, 1).Value = raffle.Name;
        sheet.Cell(2, 2).Value = raffle.CreatedAt.ToLocalTime().ToString("u", CultureInfo.InvariantCulture);
        sheet.Cell(2, 3).Value = raffle.WinnerName ?? string.Empty;
        sheet.Cell(2, 4).Value = raffle.Settings.StartingPot;
        sheet.Cell(2, 5).Value = raffle.Settings.TicketCost;
        sheet.Cell(2, 6).Value = raffle.Settings.PrizePercentage;
        sheet.Cell(2, 7).Value = raffle.TotalPaidTickets;
        sheet.Cell(2, 8).Value = raffle.TotalFreeTickets;
        sheet.Cell(2, 9).Value = raffle.TotalTickets;
        sheet.Cell(2, 10).Value = raffle.RunningPot;
        sheet.Cell(2, 11).Value = raffle.PrizePot;
        sheet.Cell(2, 12).Value = raffle.HouseTake;
    }

    private static void WriteParticipantsSheet(XLWorkbook workbook, LocalRaffle raffle)
    {
        var sheet = workbook.Worksheets.Add("Participants");
        string[] headers = ["Name", "Home World", "Paid Tickets", "Free Tickets", "Total Tickets", "Excluded"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        var ordered = raffle.Participants.OrderByDescending(p => p.PaidTickets + p.FreeTickets).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var row = i + 2;
            sheet.Cell(row, 1).Value = p.Name;
            sheet.Cell(row, 2).Value = p.HomeWorld ?? string.Empty;
            sheet.Cell(row, 3).Value = p.PaidTickets;
            sheet.Cell(row, 4).Value = p.FreeTickets;
            sheet.Cell(row, 5).Value = p.PaidTickets + p.FreeTickets;
            sheet.Cell(row, 6).Value = raffle.ExcludedParticipants.Contains(p.TicketKey) ? "Yes" : string.Empty;
        }
    }
}

public static class RaffleXlsxImporter
{
    /// <summary>Always produces a brand-new local raffle: fresh id, no backend linkage (ExternalId/HostUrl/
    /// ViewerUrl all null) even if the source file recorded one, matching donor ImportRaffle semantics. Winner
    /// history from the source file is preserved as a read-only label. Falls back to the Summary sheet (3 of the
    /// 5 settings only - PaidTicketsForFree/FreeTicketsPerBlock are not recoverable from Summary alone) when no
    /// usable Settings sheet is present, exactly like the donor. A legacy donor-era workbook with no "Home World"
    /// participant column imports every participant as a legacy Name-only entrant - HomeWorld is never invented.</summary>
    public static LocalRaffle Import(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var settingsSheet = FindSheet(workbook, "Settings");
        var summarySheet = FindSheet(workbook, "Summary");
        var participantsSheet = FindSheet(workbook, "Participants");

        var name = "Imported Raffle";
        string? winnerName = null;
        var createdAt = DateTime.UtcNow;
        var settings = new RaffleSettings();
        var settingsApplied = false;

        if (settingsSheet is not null && HasDataRows(settingsSheet, minRow: 2))
        {
            var map = ReadKeyValueSheet(settingsSheet);
            name = map.GetValueOrDefault("Raffle Name", name);
            winnerName = NullIfEmpty(map.GetValueOrDefault("Winner"));
            createdAt = ParseDate(map.GetValueOrDefault("Created At")) ?? createdAt;
            settings = new RaffleSettings(
                ParseFloat(map.GetValueOrDefault("Starting Pot")),
                ParseFloat(map.GetValueOrDefault("Ticket Cost")),
                map.TryGetValue("Prize %", out var pct) ? ParseFloat(pct, 100f) : 100f,
                ParseInt(map.GetValueOrDefault("Paid Tickets per Bonus")),
                ParseInt(map.GetValueOrDefault("Free Tickets per Bonus")));
            settingsApplied = true;
        }

        if (!settingsApplied && summarySheet is not null && HasDataRows(summarySheet, minRow: 2))
        {
            var headerMap = ReadHeaderRow(summarySheet);
            var rowValues = ReadRow(summarySheet, 2, headerMap);
            name = rowValues.GetValueOrDefault("Raffle Name", name);
            winnerName = NullIfEmpty(rowValues.GetValueOrDefault("Winner"));
            createdAt = ParseDate(rowValues.GetValueOrDefault("Created At")) ?? createdAt;
            settings = new RaffleSettings(
                ParseFloat(rowValues.GetValueOrDefault("Starting Pot")),
                ParseFloat(rowValues.GetValueOrDefault("Ticket Cost")),
                rowValues.TryGetValue("Prize %", out var pct2) ? ParseFloat(pct2, 100f) : 100f,
                0, 0);
        }

        var participants = new List<RaffleParticipant>();
        if (participantsSheet is not null)
        {
            var headerMap = ReadHeaderRow(participantsSheet);
            var lastRow = participantsSheet.LastRowUsed()?.RowNumber() ?? 1;
            for (var r = 2; r <= lastRow; r++)
            {
                var rowValues = ReadRow(participantsSheet, r, headerMap);
                var participantName = rowValues.GetValueOrDefault("Name");
                if (string.IsNullOrWhiteSpace(participantName)) continue;
                var homeWorld = NullIfEmpty(rowValues.GetValueOrDefault("Home World"));
                participants.Add(new RaffleParticipant(
                    participantName.Trim(),
                    homeWorld,
                    ParseInt(rowValues.GetValueOrDefault("Paid Tickets")),
                    ParseInt(rowValues.GetValueOrDefault("Free Tickets"))));
            }
        }

        return new LocalRaffle(Guid.NewGuid().ToString("N"), name, createdAt, settings, participants, [], null, null, null, winnerName, null, false);
    }

    private static IXLWorksheet? FindSheet(XLWorkbook workbook, string name) =>
        workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool HasDataRows(IXLWorksheet sheet, int minRow) => (sheet.LastRowUsed()?.RowNumber() ?? 0) >= minRow;

    private static Dictionary<string, string> ReadKeyValueSheet(IXLWorksheet sheet)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= lastRow; r++)
        {
            var key = sheet.Cell(r, 1).GetString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            map[key.Trim()] = sheet.Cell(r, 2).GetString();
        }
        return map;
    }

    private static Dictionary<int, string> ReadHeaderRow(IXLWorksheet sheet)
    {
        var map = new Dictionary<int, string>();
        var lastCol = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 1;
        for (var c = 1; c <= lastCol; c++)
        {
            var header = sheet.Cell(1, c).GetString();
            if (!string.IsNullOrWhiteSpace(header)) map[c] = header.Trim();
        }
        return map;
    }

    private static Dictionary<string, string> ReadRow(IXLWorksheet sheet, int row, Dictionary<int, string> headerMap)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, header) in headerMap) map[header] = sheet.Cell(row, col).GetString();
        return map;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static float ParseFloat(string? value, float fallback = 0f) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var result) ? result.ToUniversalTime() : null;
}
