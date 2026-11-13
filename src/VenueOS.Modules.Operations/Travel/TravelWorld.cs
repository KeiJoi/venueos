namespace VenueOS.Modules.Operations.Travel;

/// <summary>A public World as the game's own data describes it after Patch 7.58 (0.3.14). <paramref name="LogicalDataCenterId"/>
/// is the REAL logical Data Center (Aether, Crystal, Dynamis, Primal), read from <c>World.Unknown0</c>: in the 7.58 game
/// data every North American World's <c>World.DataCenter</c> column holds the merged group (4, "Aether"), so that column
/// can no longer tell Data Centers apart (verified by reading this PC's installed 7.58 game data; see
/// <c>docs/SHOUTRUNNER_NATIVE_TRAVEL_ARCHITECTURE.md</c> §2). <paramref name="RegionId"/> is the physical region
/// (<c>WorldDCGroupType.Region</c>: 1 JP, 2 NA, 3 EU, 4 OC).</summary>
public sealed record TravelWorld(uint Id, string Name, uint LogicalDataCenterId, string LogicalDataCenter, uint RegionId);

/// <summary>The game's World list, resolved from authoritative game data (the real implementation reads the Lumina World
/// sheet in <c>VenueOS.Plugin</c>). This layer never touches Lumina directly.</summary>
public interface ITravelWorldCatalog
{
    TravelWorld? Resolve(string worldName);
    TravelWorld? ResolveById(uint worldId);

    /// <summary>The logical Data Center names of a region, used to verify the dropdown's items are real Data Centers
    /// before anything is clicked.</summary>
    IReadOnlyCollection<string> LogicalDataCentersInRegion(uint regionId);
}

/// <summary>One of the three main-city aetherytes that offer "Visit Another World Server" (World Visit guide: "only
/// available from the central aetheryte, not Aethernet shards"). Ids and territories verified against the installed 7.58
/// Aetheryte sheet.</summary>
public sealed record WorldVisitCity(uint AetheryteId, uint TerritoryId, string Name);

public static class WorldVisitCities
{
    public static readonly WorldVisitCity LimsaLominsa = new(8, 129, "Limsa Lominsa Lower Decks");
    public static readonly WorldVisitCity Gridania = new(2, 132, "New Gridania");
    public static readonly WorldVisitCity Uldah = new(9, 130, "Ul'dah - Steps of Nald");

    public static IReadOnlyList<WorldVisitCity> All { get; } = [LimsaLominsa, Gridania, Uldah];

    public static WorldVisitCity? ForTerritory(uint territoryId) => All.FirstOrDefault(c => c.TerritoryId == territoryId);

    public static WorldVisitCity? ForName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(c => string.Equals(NormalizeName(c.Name), NormalizeName(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The World Visit cities among a ShoutRunner route's destinations, in route order, without duplicates. The
    /// first one is where travel teleports when the character is not already at a World Visit aetheryte (owner
    /// requirement, 2026-11-13), so the next World's stops begin where the character arrives.</summary>
    public static IReadOnlyList<WorldVisitCity> FromRouteDestinations(IEnumerable<string?> destinations) =>
        [.. destinations.Select(ForName).OfType<WorldVisitCity>().Distinct()];

    /// <summary>Dash variants (game text uses an en dash in "Ul'dah – Steps of Nald") and whitespace runs compare equal.</summary>
    private static string NormalizeName(string name) =>
        string.Join(' ', name.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2212', '-').Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Where native travel can take the character, under the Patch 7.58 rules (official NA/EU topic): World Visit
/// covers every logical Data Center of the character's region, with no Home World detour. Data Center Travel (character
/// select) is only for another region, and native travel deliberately does not implement it.</summary>
public static class NativeTravelPolicy
{
    /// <summary>True when the target is reachable by World Visit: a public World of a known global region (1–4), the same
    /// region the character is in, and the same region as the character's Home World. A character visiting another region
    /// through Data Center Travel cannot World Visit back into its own region.</summary>
    public static bool CanVisit(TravelWorld? current, TravelWorld? home, TravelWorld? target) =>
        current is not null && home is not null && target is not null &&
        current.RegionId is >= 1 and <= 4 && current.RegionId == home.RegionId && current.RegionId == target.RegionId;

    /// <summary>The confirmation templates as the game words them (Addon rows 12519 and 12624: "Travel to &lt;World&gt;?",
    /// the second followed by a line break and a notice). Each entry is the literal text before and after the World name.
    /// The real implementation reads them from the Addon sheet; this English pair is the fallback when that fails.</summary>
    public static readonly IReadOnlyList<(string Prefix, string Suffix)> EnglishConfirmationTemplates = [("Travel to ", "?")];

    /// <summary>True only when the FIRST line of the Yes/No prompt is exactly a confirmation template with
    /// <paramref name="destination"/> as the World. "Travel to Jenova?" never confirms Cactuar. Whitespace is normalized;
    /// nothing else is fuzzy.</summary>
    public static bool IsConfirmationFor(string? prompt, string destination, IReadOnlyList<(string Prefix, string Suffix)> templates)
    {
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(destination)) return false;
        var firstLine = Normalize(prompt.Replace("\r", string.Empty).Split('\n', 2)[0]);
        foreach (var (prefix, suffix) in templates.Count > 0 ? templates : EnglishConfirmationTemplates)
            if (string.Equals(firstLine, Normalize(prefix + destination.Trim() + suffix), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when a SelectString entry is the aetheryte's World Visit option (transport/Aetheryte row 3, " Visit
    /// Another World Server."; the rendered entry may carry a leading icon).</summary>
    public static bool IsWorldVisitMenuEntry(string? entry, string optionText) =>
        !string.IsNullOrWhiteSpace(entry) && !string.IsNullOrWhiteSpace(optionText) &&
        Normalize(entry).Contains(Normalize(optionText), StringComparison.OrdinalIgnoreCase);

    /// <summary>The English fallback for <see cref="IsWorldVisitMenuEntry"/>'s option text.</summary>
    public const string EnglishWorldVisitOption = "Visit Another World Server.";

    /// <summary>A UI label reduced to its name: leading icons, arrows and private-use glyphs are dropped (the owner's
    /// 7.58 screenshots show "▶ Dynamis" on the dropdown and a home icon before the current World's row) and whitespace
    /// is normalized. Only leading/trailing decoration is removed; the name itself must still match exactly.</summary>
    public static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        var text = label.Trim();
        var start = 0;
        while (start < text.Length && !char.IsLetterOrDigit(text[start])) start++;
        var end = text.Length;
        while (end > start && !char.IsLetterOrDigit(text[end - 1]) && text[end - 1] is not ('.' or ')' or '\'')) end--;
        return Normalize(text[start..end]);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
