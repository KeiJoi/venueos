using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.PartyFinder;

namespace VenueOS.Plugin.PartyFinder;

/// <summary>Donor: <c>PartyFinderAutomation.BuildDutyCache</c> — the live Lumina/<see cref="IDataManager"/> lookup
/// that <see cref="PartyFinderDutyCatalog"/> (pure logic) is built from. Lives here because it needs a real Dalamud
/// data-manager context; separated so the search/lookup logic itself stays independently testable.</summary>
internal static class PartyFinderDutyLoader
{
    public static PartyFinderDutyCatalog Build(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
        {
            return new PartyFinderDutyCatalog([]);
        }

        var duties = sheet
            .Where(row => row.RowId <= ushort.MaxValue)
            .Select(row => new PartyFinderDutyEntry((ushort)row.RowId, row.Name.ToString().Trim()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PartyFinderDutyCatalog(duties);
    }
}
