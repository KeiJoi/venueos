using VenueOS.Core;
using VenueOS.Venues;

namespace VenueOS.Services;

public sealed record DiagnosticEntry(string Message, DateTimeOffset At);
public sealed record DiagnosticSnapshot(string VenueOsVersion, string ActiveVenueId, string ActiveVenueName, IReadOnlyList<string> EnabledModules, IReadOnlyDictionary<string, string> ProtocolPins, IReadOnlyList<DiagnosticEntry> RecentErrors, IReadOnlyList<string> RecoveryWarnings);

public sealed class DiagnosticsService(ModuleHost modules, VenueProfileService venues, IClock clock)
{
    private readonly Queue<DiagnosticEntry> errors = new();
    public void RecordFailure(string message) { if (errors.Count >= 20) errors.Dequeue(); errors.Enqueue(new(Redact(message), clock.UtcNow)); }
    public void Clear() => errors.Clear();
    public DiagnosticSnapshot Capture() => new("Phase 4", venues.Current.Id.ToString("N"), venues.Current.DisplayName, modules.Modules.Where(x => x.IsEnabled).Select(x => x.Descriptor.Id).ToArray(), new Dictionary<string, string> { ["games.raffle"] = "111b3a9", ["games.trivia"] = "6ba4855", ["games.tournament"] = "c29e984", ["games.bingo"] = "015d5d6" }, errors.ToArray(), venues.RecoveryWarnings.Select(Redact).ToArray());
    public static string Redact(string message)
    {
        foreach (var marker in new[] { "token=", "accessToken", "refreshToken", "AdminKey", "RoomKey", "password" })
        {
            var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (index >= 0) return message[..(index + marker.Length)] + " [redacted]";
        }
        return message;
    }
}
