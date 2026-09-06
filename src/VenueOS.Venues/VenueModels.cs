using VenueOS.Core;

namespace VenueOS.Venues;

public sealed record VenueBranding(string? LogoPath = null, string? BackgroundImagePath = null, float BackgroundOpacity = 0.25f, bool ShowVenueFrame = true, bool ShowVenueOsBranding = true);
public sealed record ThemeMetrics(float Rounding = 6, float Padding = 10, float Spacing = 8, float Density = 1);
public sealed record VenueTheme(string BuiltInThemeId, ThemeTokens Tokens, ThemeMetrics Metrics, VenueBranding Branding)
{
    public static VenueTheme Dark { get; } = BuiltInThemes.Dark;
}
public sealed record ThemeTokens(string Primary, string Accent, string Background, string Surface, string RaisedSurface, string Border, string TextPrimary, string TextSecondary, string Success, string Warning, string Error, string Disabled, string Selected);
public sealed record VenueProfile(Guid Id, string DisplayName, VenueTheme Theme);
public sealed record VenueModuleConfigKey(Guid VenueId, string ModuleId, int SchemaVersion)
{
    public override string ToString() => $"{VenueId:N}:{ModuleId}:{SchemaVersion}";
}
/// <summary>A module's persisted config, stored as raw JSON <em>text</em> rather than a live
/// <see cref="System.Text.Json.JsonElement"/>. This is a deliberate, confirmed-necessary choice: <c>ModulePayload</c>
/// is a field inside <see cref="VenueOS.Plugin.VenueOsPluginConfiguration"/>, which Dalamud persists via
/// <c>IDalamudPluginInterface.SavePluginConfig</c>/<c>GetPluginConfig</c> — a Newtonsoft.Json-based round trip, not
/// <see cref="System.Text.Json"/>. Newtonsoft has no special handling for a raw <c>JsonElement</c> struct and
/// silently reflects over its public members instead of its actual content, which serializes only the
/// <c>ValueKind</c> enum and discards every real field — confirmed directly against a live installation's on-disk
/// <c>VenueOS.json</c>, where every module's stored payload had degraded to <c>{"ValueKind": 0}</c> (or 1, with no
/// underlying data either) after nothing more than an ordinary save/reload. A plain <see cref="string"/> round-trips
/// through any serializer correctly, so it's the storage type here; <see cref="System.Text.Json.JsonSerializer"/> is
/// used explicitly at the read/write boundary in <see cref="VenueOS.Venues.VenueProfileService"/> instead.</summary>
public sealed record ModulePayload(int SchemaVersion, string Json);
public sealed record VenueStoreSnapshot(int SchemaVersion, Guid ActiveVenueId, List<VenueProfile> Venues, Dictionary<string, ModulePayload> ModulePayloads);
public sealed record VenueOperationResult(bool Success, string? Error = null);

public static class BuiltInThemes
{
    public static VenueTheme Dark { get; } = Create("dark", "#8B5CF6", "#22D3EE", "#101217", "#191D26", "#242A35", "#3B4354", "#F7F7FA", "#A9B1C3", "#5EEAD4", "#FBBF24", "#FB7185", "#6B7280", "#382C55");
    public static VenueTheme Light { get; } = Create("light", "#6D28D9", "#0891B2", "#F5F7FB", "#FFFFFF", "#E8EDF5", "#CBD5E1", "#111827", "#475569", "#047857", "#B45309", "#BE123C", "#94A3B8", "#EDE9FE");
    public static VenueTheme Neon { get; } = Create("neon", "#E879F9", "#22D3EE", "#090611", "#150B24", "#23123B", "#5B2A86", "#FDF4FF", "#D8B4FE", "#34D399", "#FDE047", "#FB7185", "#7C3AED", "#4C1D95");
    public static VenueTheme Midnight { get; } = Create("midnight", "#60A5FA", "#38BDF8", "#07111F", "#0D1B2A", "#13263A", "#23415C", "#E0F2FE", "#94A3B8", "#2DD4BF", "#F59E0B", "#F43F5E", "#64748B", "#123A5A");
    public static IReadOnlyDictionary<string, VenueTheme> All { get; } = new Dictionary<string, VenueTheme>(StringComparer.OrdinalIgnoreCase) { ["dark"] = Dark, ["light"] = Light, ["neon"] = Neon, ["midnight"] = Midnight };
    public static VenueTheme Get(string id) => All.TryGetValue(id, out var theme) ? theme : Dark;
    private static VenueTheme Create(string id, string primary, string accent, string background, string surface, string raised, string border, string text, string secondary, string success, string warning, string error, string disabled, string selected) => new(id, new(primary, accent, background, surface, raised, border, text, secondary, success, warning, error, disabled, selected), new ThemeMetrics(), new VenueBranding());
}

public interface IVenueStore
{
    VenueStoreSnapshot Read();
    void Write(VenueStoreSnapshot snapshot);
}

public sealed class InMemoryVenueStore : IVenueStore
{
    private VenueStoreSnapshot snapshot;
    public InMemoryVenueStore(VenueStoreSnapshot? initial = null) => snapshot = DeepCopy(initial ?? VenueProfileService.CreateInitialSnapshot());
    public VenueStoreSnapshot Read() => DeepCopy(snapshot);
    public void Write(VenueStoreSnapshot value) => snapshot = DeepCopy(value);
    /// <summary>Structural clone. <see cref="ModulePayload"/> is immutable and string-backed (see its own doc
    /// comment for why), so copying the dictionary is already a full deep copy — no JSON reparsing needed.</summary>
    public static VenueStoreSnapshot DeepCopy(VenueStoreSnapshot value)
    {
        var venues = value.Venues.Select(venue => venue with
        {
            Theme = venue.Theme with { Tokens = venue.Theme.Tokens with { }, Metrics = venue.Theme.Metrics with { }, Branding = venue.Theme.Branding with { } },
        }).ToList();
        var payloads = new Dictionary<string, ModulePayload>(value.ModulePayloads, StringComparer.Ordinal);
        return new VenueStoreSnapshot(value.SchemaVersion, value.ActiveVenueId, venues, payloads);
    }
}
