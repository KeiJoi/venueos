using VenueOS.Core;
using VenueOS.Venues;

namespace VenueOS.Services;

public sealed record DiagnosticEntry(string Message, DateTimeOffset At);
public sealed record DiagnosticSnapshot(string VenueOsVersion, string ActiveVenueId, string ActiveVenueName, IReadOnlyList<string> EnabledModules, IReadOnlyDictionary<string, string> ProtocolPins, IReadOnlyList<DiagnosticEntry> RecentErrors, IReadOnlyList<string> RecoveryWarnings);

public sealed class DiagnosticsService(ModuleHost modules, VenueProfileService venues, IClock clock, string venueOsVersion = "dev")
{
    private readonly Queue<DiagnosticEntry> errors = new();
    public void RecordFailure(string message) { if (errors.Count >= 20) errors.Dequeue(); errors.Enqueue(new(Redact(message), clock.UtcNow)); }
    public void Clear() => errors.Clear();
    // venueOsVersion is supplied by the composition root from the actual running assembly version (Plugin.cs) — never
    // a hand-maintained label here. It used to be the hard-coded literal "Phase 4", which silently drifted from every
    // real release version (0.1.0, then 0.2.0); the "dev" default only applies to callers (mostly tests) that never
    // wire a real version through.
    public DiagnosticSnapshot Capture() => new(venueOsVersion, venues.Current.Id.ToString("N"), venues.Current.DisplayName, modules.Modules.Where(x => x.IsEnabled).Select(x => x.Descriptor.Id).ToArray(), new Dictionary<string, string> { ["games.raffle"] = "111b3a9", ["games.trivia"] = "6ba4855", ["games.tournament"] = "c29e984", ["games.bingo"] = "015d5d6" }, errors.ToArray(), venues.RecoveryWarnings.Select(Redact).ToArray());
    // "/host/" and "/view/" cover Raffle's capability token, which — unlike every other marker here — is carried as
    // a bare URL PATH SEGMENT (/host/{raffleId}/{hostToken}, /view/{raffleId}/{viewerToken}), not a `key=value`
    // pair. A `key=value`-shaped marker list alone does not catch that shape (confirmed gap, docs/RAFFLE_FORENSIC_
    // AUDIT.md §15/§23) — if a raw HostUrl/ViewerUrl (or the exception message of a failed HTTP request against
    // one) ever reaches RecordFailure, this is what keeps the token out of Settings → Diagnostics.
    public static string Redact(string message)
    {
        foreach (var marker in new[] { "token=", "accessToken", "refreshToken", "AdminKey", "RoomKey", "password", "/host/", "/view/" })
        {
            var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (index >= 0) return message[..(index + marker.Length)] + " [redacted]";
        }
        return message;
    }
}
