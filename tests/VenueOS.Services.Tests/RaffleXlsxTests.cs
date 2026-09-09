using ClosedXML.Excel;
using VenueOS.Modules.Operations.Raffle;

namespace VenueOS.Services.Tests;

public sealed class RaffleXlsxTests
{
    [Fact]
    public void Export_then_import_round_trips_settings_and_homeworld_qualified_participants()
    {
        var settings = new RaffleSettings(StartingPot: 50, TicketCost: 5, PrizePercentage: 80, PaidTicketsForFree: 5, FreeTicketsPerBlock: 1);
        var original = new LocalRaffle(
            "local-id",
            "Weekend Giveaway",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            settings,
            [new("Ada", "Balmung", 5, 1), new("Ada", "Gilgamesh", 2, 0), new("LegacyName", null, 1, 0)],
            ["LegacyName"],
            WinnerName: "Ada@Balmung");

        using var stream = new MemoryStream();
        RaffleXlsxExporter.Export(original, stream);
        stream.Position = 0;

        var imported = RaffleXlsxImporter.Import(stream);

        Assert.NotEqual(original.Id, imported.Id); // always a fresh local identity
        Assert.Null(imported.ExternalId);
        Assert.Null(imported.HostUrl);
        Assert.Null(imported.ViewerUrl);
        Assert.Equal("Weekend Giveaway", imported.Name);
        Assert.Equal("Ada@Balmung", imported.WinnerName);
        Assert.Equal(settings, imported.Settings);

        Assert.Equal(3, imported.Participants.Count);
        Assert.Contains(imported.Participants, p => p.Name == "Ada" && p.HomeWorld == "Balmung" && p.PaidTickets == 5 && p.FreeTickets == 1);
        Assert.Contains(imported.Participants, p => p.Name == "Ada" && p.HomeWorld == "Gilgamesh" && p.PaidTickets == 2);
        Assert.Contains(imported.Participants, p => p.Name == "LegacyName" && p.HomeWorld == null && p.PaidTickets == 1);
    }

    [Fact]
    public void A_legacy_donor_style_workbook_with_no_settings_sheet_or_homeworld_column_falls_back_to_summary_and_never_invents_a_world()
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add("Summary");
        string[] headers = ["Raffle Name", "Created At", "Winner", "Starting Pot", "Ticket Cost", "Prize %", "Paid Tickets", "Free Tickets", "Total Tickets", "Running Pot", "Prize Pot", "House Take"];
        for (var c = 0; c < headers.Length; c++) summary.Cell(1, c + 1).Value = headers[c];
        summary.Cell(2, 1).Value = "Legacy Raffle";
        summary.Cell(2, 2).Value = "2025-01-01 00:00:00Z";
        summary.Cell(2, 3).Value = "OldWinner";
        summary.Cell(2, 4).Value = 10;
        summary.Cell(2, 5).Value = 2;
        summary.Cell(2, 6).Value = 100;

        var participants = workbook.Worksheets.Add("Participants");
        participants.Cell(1, 1).Value = "Name";
        participants.Cell(1, 2).Value = "Paid Tickets";
        participants.Cell(1, 3).Value = "Free Tickets";
        participants.Cell(2, 1).Value = "OldWinner";
        participants.Cell(2, 2).Value = 3;
        participants.Cell(2, 3).Value = 0;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var imported = RaffleXlsxImporter.Import(stream);

        Assert.Equal("Legacy Raffle", imported.Name);
        Assert.Equal("OldWinner", imported.WinnerName);
        Assert.Equal(10, imported.Settings.StartingPot);
        Assert.Equal(2, imported.Settings.TicketCost);
        // Not recoverable from a Summary-only sheet, matching donor behavior - never invented as nonzero.
        Assert.Equal(0, imported.Settings.PaidTicketsForFree);
        Assert.Equal(0, imported.Settings.FreeTicketsPerBlock);

        var participant = Assert.Single(imported.Participants);
        Assert.Equal("OldWinner", participant.Name);
        Assert.Null(participant.HomeWorld); // never invented
        Assert.Equal("OldWinner", participant.TicketKey);
    }

    [Fact]
    public void Excluded_participants_are_marked_in_the_exported_workbook()
    {
        var raffle = new LocalRaffle("id", "R", DateTime.UtcNow, new(), [new("Ada", "Balmung", 1, 0)], ["Ada@Balmung"]);
        using var stream = new MemoryStream();
        RaffleXlsxExporter.Export(raffle, stream);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var participants = workbook.Worksheet("Participants");
        Assert.Equal("Yes", participants.Cell(2, 6).GetString());
    }
}
