namespace VenueOS.Modules.Operations.PartyFinder;

public sealed record PartyFinderDutyEntry(ushort Id, string Name);

/// <summary>Donor: <c>PartyFinderAutomation.GetDutyName</c>/<c>GetFilteredDuties</c> — pure name lookup/substring
/// filter over the duty list. Kept independent of how the list is built (the donor builds it once from
/// <c>IDataManager.GetExcelSheet&lt;ContentFinderCondition&gt;()</c> in its constructor — a live Dalamud/Lumina
/// concern that belongs in <c>VenueOS.Plugin</c>, not here) so the search/lookup logic itself is unit-testable.</summary>
public sealed class PartyFinderDutyCatalog(IReadOnlyList<PartyFinderDutyEntry> duties)
{
    public string GetName(ushort dutyId) => dutyId == 0
        ? "None"
        : duties.FirstOrDefault(d => d.Id == dutyId)?.Name ?? $"Unknown Duty ({dutyId})";

    public IEnumerable<PartyFinderDutyEntry> Filter(string filter) => string.IsNullOrWhiteSpace(filter)
        ? duties
        : duties.Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
