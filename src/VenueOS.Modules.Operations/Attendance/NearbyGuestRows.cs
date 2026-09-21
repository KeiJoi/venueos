using VenueOS.Services;

namespace VenueOS.Modules.Operations.Attendance;

/// <summary>One display row of Attendance's "Guests Nearby" list. The name and home world travel together in a
/// single record, and <see cref="Text"/> is the one string the operator panel draws for the guest — the world can
/// therefore never be rendered separately from (or displaced relative to) the name it belongs to.</summary>
public sealed record NearbyGuestRow(string Name, string HomeWorld, bool Greeted)
{
    /// <summary>"Name — HomeWorld", with " · Greeted" appended for a greeted guest. A missing world (never expected
    /// from the object table, but not impossible) renders as just the name rather than a dangling separator.</summary>
    public string Text => Compose(Name, HomeWorld, Greeted);

    public static string Compose(string name, string? homeWorld, bool greeted)
    {
        var label = string.IsNullOrWhiteSpace(homeWorld) ? name : $"{name} — {homeWorld}";
        return greeted ? $"{label} · Greeted" : label;
    }
}

/// <summary>Filtering, ordering and row construction for Attendance's "Guests Nearby" list, kept below the ImGui
/// boundary so it is testable. Filter and order semantics are exactly what the panel used inline before the layout
/// correction: a case-insensitive name-contains search, ordered by name.</summary>
public static class NearbyGuestRows
{
    public static IReadOnlyList<NearbyGuestRow> Build(IEnumerable<PlayerSnapshot> guests, string? search, Func<GuestIdentity, bool> isGreeted) =>
        guests
            .Where(g => string.IsNullOrWhiteSpace(search) || g.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Name)
            .Select(g => new NearbyGuestRow(g.Name, g.HomeWorld, isGreeted(new GuestIdentity(g.Name, g.HomeWorld))))
            .ToArray();
}
