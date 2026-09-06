namespace VenueOS.Services;

/// <summary>Donor: <c>VenueAddressSnapshot</c> — a read-only snapshot of where the operator currently is, used to
/// auto-fill Attendance's "In-Game Address" Settings field. Purely descriptive: unlike territory lock/radius, it is
/// never used in any presence-filtering calculation.</summary>
public sealed record VenueAddressSnapshot(string DataCenter, string Server, string District, int? Ward, int? Plot, bool IsSubdivision)
{
    public bool IsHousingArea => Ward.HasValue && Plot.HasValue && !string.IsNullOrWhiteSpace(District);

    public string ToAddressString() =>
        $"{DataCenter} | {Server} | {District} | Ward {(Ward?.ToString() ?? "?")} | Plot {(Plot?.ToString() ?? "?")} ({(IsSubdivision ? "Subdivision" : "Main Division")})";
}

/// <summary>Donor: <c>VenueAddressService</c>. Abstracted the same way <see cref="ITargetedPlayerProvider"/> wraps
/// target-manager access, so the Dalamud/housing-manager specifics stay in <c>Plugin.cs</c> and everything else is testable.</summary>
public interface IVenueAddressProvider
{
    bool TryGetCurrentAddress(out VenueAddressSnapshot snapshot);
}
