namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The single source of truth for North America Data Center → World membership and display order,
/// replacing the donor's two independently-maintained, textually-duplicate copies
/// (<c>MacroRunner.worldToDataCenter</c>/<c>worldVisitOrder</c>, consulted at execution time, and
/// <c>MainWindow.NorthAmericaWorlds</c>, consulted only by the donor's UI) — see the ShoutRunner familiarization
/// report §10. Data Center order is fixed and alphabetical (Aether, Crystal, Dynamis, Primal — a deliberate product
/// decision, not the donor's Aether/Primal/Crystal/Dynamis grouping order) and is never user-reorderable; only which
/// Data Centers participate in a route is configurable (<see cref="ShoutRunnerSettings.SelectedDataCenters"/>).
/// World membership per Data Center is the donor's own verified mapping, re-sorted alphabetically within each Data
/// Center (the donor's Dynamis list in particular was not alphabetical — Halicarnassus, Cuchulainn, Golem, Kraken,
/// Maduin, Marilith, Rafflesia, Seraph — this catalog corrects that ordering without changing membership).</summary>
public static class ShoutRunnerCatalog
{
    /// <summary>The fixed, non-reorderable display/traversal order for Data Centers. A route only ever visits a
    /// subset of these (<see cref="ShoutRunnerSettings.SelectedDataCenters"/>), always filtered from this exact
    /// order — e.g. selecting Crystal and Primal always routes Crystal before Primal, never the reverse.</summary>
    public static IReadOnlyList<string> CanonicalDataCenterOrder { get; } = ["Aether", "Crystal", "Dynamis", "Primal"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> WorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Aether"] = ["Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren"],
        ["Crystal"] = ["Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera"],
        ["Dynamis"] = ["Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"],
        ["Primal"] = ["Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros"],
    };

    private static readonly IReadOnlyDictionary<string, string> DataCenterByWorldLookup = WorldsByDataCenter
        .SelectMany(pair => pair.Value.Select(world => (World: world, DataCenter: pair.Key)))
        .ToDictionary(x => x.World, x => x.DataCenter, StringComparer.OrdinalIgnoreCase);

    /// <summary>True only for one of the four known, currently-supported canonical Data Center names.</summary>
    public static bool IsKnownDataCenter(string dataCenter) => WorldsByDataCenter.ContainsKey(dataCenter);

    /// <summary>The Worlds belonging to <paramref name="dataCenter"/>, already in fixed alphabetical order. Empty
    /// for an unrecognized Data Center name rather than throwing — callers filter against
    /// <see cref="CanonicalDataCenterOrder"/>/<see cref="IsKnownDataCenter"/> first in normal use.</summary>
    public static IReadOnlyList<string> WorldsIn(string dataCenter) =>
        WorldsByDataCenter.TryGetValue(dataCenter, out var worlds) ? worlds : [];

    /// <summary>The single Data Center a World belongs to, or null if the World is not part of this catalog (e.g. a
    /// non-NA World, or a name that didn't match any known World).</summary>
    public static string? DataCenterFor(string world) => DataCenterByWorldLookup.TryGetValue(world, out var dc) ? dc : null;

    /// <summary>Filters <paramref name="selected"/> down to known Data Centers and returns them in
    /// <see cref="CanonicalDataCenterOrder"/> — the one place route planning should ever derive "which Data Centers,
    /// in what order" from a user's selection, so canonical ordering can never be bypassed by construction order,
    /// dictionary enumeration, or a stale/duplicated selection list.</summary>
    public static IReadOnlyList<string> OrderSelectedDataCenters(IEnumerable<string> selected)
    {
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return [.. CanonicalDataCenterOrder.Where(set.Contains)];
    }
}
