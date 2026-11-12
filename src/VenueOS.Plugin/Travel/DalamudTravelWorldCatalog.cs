using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Plugin.Travel;

/// <summary>Public Worlds from the game's World sheet, with their REAL logical Data Center (0.3.14). In the installed
/// Patch 7.58 game data every North American World has <c>DataCenter = 4</c> ("Aether", the merged group) while
/// <c>Unknown0</c> holds the logical Data Center (Aether 4, Primal 5, Crystal 8, Dynamis 11); Oceania keeps both equal
/// (Materia 9). This matches two public plugins updated for 7.57/7.58 (MasterOfPuppets' WorldCatalog, SRankSentinel's
/// NativeTravel). <c>Unknown0</c> is used when non-zero, <c>DataCenter</c> otherwise; the region comes from the logical
/// Data Center's <c>WorldDCGroupType.Region</c>. Loaded lazily and retried if the sheet is not ready yet. Lumina sheet
/// reads are thread-safe, but everything here is also cheap enough for the framework thread.</summary>
public sealed class DalamudTravelWorldCatalog(IDataManager data, IPluginLog log) : ITravelWorldCatalog
{
    private readonly object gate = new();
    private Dictionary<uint, TravelWorld>? byId;
    private Dictionary<string, TravelWorld>? byName;
    private DateTime nextAttemptUtc;

    public TravelWorld? Resolve(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName)) return null;
        return EnsureLoaded() && byName!.TryGetValue(worldName.Trim(), out var world) ? world : null;
    }

    public TravelWorld? ResolveById(uint worldId) => worldId != 0 && EnsureLoaded() && byId!.TryGetValue(worldId, out var world) ? world : null;

    public IReadOnlyCollection<string> LogicalDataCentersInRegion(uint regionId) =>
        EnsureLoaded() ? byId!.Values.Where(w => w.RegionId == regionId).Select(w => w.LogicalDataCenter).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];

    public IReadOnlyList<TravelWorld> All => EnsureLoaded() ? [.. byId!.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)] : [];

    private bool EnsureLoaded()
    {
        lock (gate)
        {
            if (byId is not null) return true;
            if (DateTime.UtcNow < nextAttemptUtc) return false;
            try
            {
                var centers = data.GetExcelSheet<WorldDCGroupType>();
                var ids = new Dictionary<uint, TravelWorld>();
                var names = new Dictionary<string, TravelWorld>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in data.GetExcelSheet<World>())
                {
                    if (!row.IsPublic || row.RowId == 0) continue;
                    var logicalId = row.Unknown0 != 0 ? (uint)row.Unknown0 : row.DataCenter.RowId;
                    if (logicalId == 0 || !centers.TryGetRow(logicalId, out var dc) || dc.IsCloud) continue;
                    var name = row.Name.ExtractText().Trim();
                    var dcName = dc.Name.ExtractText().Trim();
                    if (name.Length == 0 || dcName.Length == 0) continue;
                    var world = new TravelWorld(row.RowId, name, logicalId, dcName, dc.Region.RowId);
                    ids[row.RowId] = world;
                    names.TryAdd(name, world);
                }

                if (ids.Count == 0) throw new InvalidOperationException("the World sheet produced no public Worlds");
                byId = ids;
                byName = names;
                return true;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Native travel: World catalog not ready; will retry.");
                nextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                return false;
            }
        }
    }
}
