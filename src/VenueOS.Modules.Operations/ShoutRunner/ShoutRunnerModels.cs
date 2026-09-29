using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>Persistent per-venue ShoutRunner configuration — the route policy (Settings → Modules → ShoutRunner)
/// plus the live Shout Message, which the module's own operational screen edits directly (no separate "Apply"
/// step, no generated-action copy — see the familiarization report §5/§17). Everything here is per-venue: despite
/// earlier discussion floating a "global route policy," <c>NEW_MODULE_GUIDE.md</c> §12 reserves global scope for
/// application behavior independent of the active venue (its only current example is Auto Pop-Out), and different
/// venues plausibly do want different Data Centers/destinations/timing — so this stays fully per-venue, consistent
/// with every other reconstructed module. See <c>SHOUTRUNNER_RECONSTRUCTION.md</c> §16/§17/Q1 for the full
/// reasoning this deliberately departs from the earlier "global module settings" framing.
///
/// <para><see cref="ShoutMessage"/> is Shout Line 1 (the JSON property keeps its original name so every existing
/// saved configuration loads unchanged) and <see cref="ShoutMessageLine2"/> is the optional second line added in
/// 0.3.8 — an additive, defaulted property, so an old payload without it deserializes to an empty Line 2 with no
/// schema-version bump. <see cref="Destinations"/> is the one and only route-destination source: the three default
/// city entries are simply this list's seeded initial contents, never a separate hard-coded set, so any entry the
/// operator adds is routed exactly like a default one.</para></summary>
public sealed record ShoutRunnerSettings(
    string ShoutMessage,
    bool RepeatEnabled,
    int IntervalHours,
    int IntervalMinutes,
    int IntervalSeconds,
    int DelayBetweenActionsSeconds,
    List<string> SelectedDataCenters,
    List<string> Destinations,
    string ShoutMessageLine2 = "")
{
    /// <summary>Donor default was 30 minutes (<c>Configuration.IntervalMinutes = 30</c>); the VenueOS redesign uses
    /// a 1-hour default per explicit product direction. Data Centers/Destinations intentionally start unselected —
    /// forcing an explicit choice on first configuration rather than silently routing across every Data Center or a
    /// guessed destination list the first time the module runs (see <see cref="MinimumIntervalSeconds"/> and
    /// <see cref="ShoutRunnerService.Start"/>'s validation, which refuses to start with either empty).</summary>
    public static ShoutRunnerSettings Default() => new(
        ShoutMessage: string.Empty,
        RepeatEnabled: true,
        IntervalHours: 1,
        IntervalMinutes: 0,
        IntervalSeconds: 0,
        DelayBetweenActionsSeconds: 2,
        SelectedDataCenters: [],
        Destinations: ["Ul'dah - Steps of Nald", "New Gridania", "Limsa Lominsa Lower Decks"],
        ShoutMessageLine2: string.Empty);

    /// <summary>A zero interval is forbidden — the donor allowed it and could hot-loop the entire route back-to-back
    /// with no throttle (familiarization report §15). One minute is the floor; unlike the donor, the total is also
    /// capped well below <see cref="TimeSpan"/>'s range so no combination of the three fields can overflow
    /// constructing it.</summary>
    public const int MinimumIntervalSeconds = 60;
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(30);

    public TimeSpan GetInterval()
    {
        var hours = Math.Clamp(IntervalHours, 0, 999);
        var minutes = Math.Clamp(IntervalMinutes, 0, 59);
        var seconds = Math.Clamp(IntervalSeconds, 0, 59);
        var interval = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        if (interval < TimeSpan.FromSeconds(MinimumIntervalSeconds)) interval = TimeSpan.FromSeconds(MinimumIntervalSeconds);
        return interval > MaximumInterval ? MaximumInterval : interval;
    }

    public int ClampedDelaySeconds() => Math.Clamp(DelayBetweenActionsSeconds, 0, 120);
}

/// <summary>A frozen snapshot of route policy taken at RUN start (familiarization report §"RUN CONFIG SNAPSHOT VS
/// LIVE EDITS") — a RUN always finishes using the Data Centers/Destinations/delay it started with, even if the
/// operator edits Settings while it's mid-flight; those edits apply starting with the next RUN. The Shout Message is
/// deliberately not part of this snapshot — it is read fresh from <see cref="ShoutRunnerSettings"/> immediately
/// before each unsent shout, so a small wording correction mid-route takes effect without restarting.</summary>
public sealed record ShoutRunnerRunConfig(IReadOnlyList<string> DataCentersInOrder, IReadOnlyList<string> Destinations, int DelayBetweenActionsSeconds);

public enum ShoutRunnerState
{
    Stopped,
    Starting,
    RecoveringTravel,
    TravelingWorld,
    Teleporting,
    SendingShout,
    /// <summary>Line 1 of a two-line shout was confirmed dispatched; Line 2 is held until
    /// <see cref="ShoutRunnerShoutLines.InterLineDelay"/> has elapsed since that confirmation. Never entered for a
    /// one-line shout.</summary>
    WaitingForLine2,
    WaitingActionDelay,
    WaitingRepeat,
    Stopping,
    Faulted,
}

public enum ShoutRunnerStartResult
{
    Started,
    AlreadyRunning,
    ShoutMessageRequired,
    NoDataCenterSelected,
    NoDestinationConfigured,
    /// <summary>Shout Line 1, with its <c>/shout </c> prefix, exceeds <see cref="BlockLettersLimits.ChatBytes"/>.</summary>
    ShoutMessageTooLong,
    /// <summary>Shout Line 2 (non-blank), with its <c>/shout </c> prefix, exceeds <see cref="BlockLettersLimits.ChatBytes"/>.</summary>
    ShoutMessageLine2TooLong,
}

/// <summary>The outcome of <see cref="ShoutRunnerService.AddDestination"/> — the operator-facing "Add Aetheryte"
/// action. Only <see cref="Added"/> changes anything; every other value leaves the saved list untouched and is
/// something the UI can explain rather than silently ignoring the click.</summary>
public enum ShoutRunnerAddDestinationResult
{
    Added,
    /// <summary>Empty or whitespace-only entry.</summary>
    Blank,
    /// <summary>Longer than <see cref="ShoutRunnerService.MaxDestinationNameLength"/>.</summary>
    TooLong,
    /// <summary>Already in the destination list (case-insensitive) — a duplicate would only shout twice at one place.</summary>
    Duplicate,
    /// <summary>Not a known Aetheryte name in the game data (only ever returned when an
    /// <see cref="IShoutRunnerAetheryteCatalog"/> with data is available).</summary>
    UnknownAetheryte,
}

/// <summary>The authoritative list of valid Aetheryte teleport-destination names, sourced from the game's own
/// Aetheryte data by the real implementation in <c>VenueOS.Plugin</c> (this layer never touches Lumina/Dalamud — the
/// same boundary <see cref="IShoutRunnerAutomation"/> draws). <see cref="ShoutRunnerService"/> only uses it to
/// validate and canonicalize an entry as it is ADDED; an already-saved destination is never re-validated against
/// it, so a configuration written before this catalog existed always keeps working exactly as before.</summary>
public interface IShoutRunnerAetheryteCatalog
{
    /// <summary>Every known Aetheryte name, alphabetical and distinct. Empty when the game data isn't available
    /// (early plugin startup, a sheet-load failure) — <see cref="ShoutRunnerService.AddDestination"/> then falls
    /// back to accepting any well-formed name rather than making Add impossible.</summary>
    IReadOnlyList<string> Names { get; }
}

/// <summary>Everything about one outgoing <c>/shout</c> line — its normalization, the exact command text, and its
/// UTF-8 byte count measured the same way the Shouts module measures a line (<see cref="BlockTextLength"/> over the
/// FULL command including its <c>/shout </c> prefix, against <see cref="BlockLettersLimits.ChatBytes"/>, since that
/// limit models FFXIV's chat input buffer holding the whole typed command). Line 1 and Line 2 both go through this
/// one implementation, so they can never be validated differently.</summary>
public static class ShoutRunnerShoutLines
{
    public const string CommandPrefix = "/shout ";

    /// <summary>Trimmed text; a whitespace-only line normalizes to empty, which means "no line."</summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Trim();

    public static bool IsBlank(string? text) => Normalize(text).Length == 0;

    public static string BuildCommand(string? text) => CommandPrefix + Normalize(text);

    public static int CountBytes(string? text) => BlockTextLength.CountBytes(BuildCommand(text));

    /// <summary>False for a blank line (nothing to send, so nothing to limit).</summary>
    public static bool ExceedsLimit(string? text) => !IsBlank(text) && CountBytes(text) > BlockLettersLimits.ChatBytes;

    /// <summary>The minimum gap between Line 1's CONFIRMED dispatch (its
    /// <see cref="VenueOS.Services.ChatCommand.OnDispatched"/> callback reporting success) and Line 2 becoming eligible to be handed to the chat queue. Live FFXIV testing
    /// showed the shared chat service's 1 s minimum interval alone is not enough — the game silently dropped a second
    /// <c>/shout</c> sent ~1 s after the first.</summary>
    public static readonly TimeSpan InterLineDelay = TimeSpan.FromSeconds(2);

    /// <summary>True once at least <see cref="InterLineDelay"/> has elapsed since <paramref name="line1ConfirmedAt"/>.</summary>
    public static bool IsLine2Eligible(DateTimeOffset line1ConfirmedAt, DateTimeOffset now) => now - line1ConfirmedAt >= InterLineDelay;
}

/// <summary>One planned stop within a single World's destination traversal — see
/// <see cref="ShoutRunnerRoutePlanner"/> for how <see cref="RequiresTeleport"/> is decided (false only for the very
/// first stop, and only when the character's detected current location already matches it).</summary>
public sealed record ShoutRunnerDestinationStep(string Destination, bool RequiresTeleport);

public enum ShoutRunnerCrossDataCenterCheck { SameDataCenter, CrossDataCenter, Unknown }

public enum ShoutRunnerTransferResult { Success, WorldCongestedSkip, DataCenterUnavailableSkip, Failed }

public sealed record ShoutRunnerTransferOutcome(ShoutRunnerTransferResult Result, string? Detail = null)
{
    public static ShoutRunnerTransferOutcome Success() => new(ShoutRunnerTransferResult.Success);
    public static ShoutRunnerTransferOutcome WorldCongested(string detail) => new(ShoutRunnerTransferResult.WorldCongestedSkip, detail);
    public static ShoutRunnerTransferOutcome DataCenterUnavailable(string detail) => new(ShoutRunnerTransferResult.DataCenterUnavailableSkip, detail);
    public static ShoutRunnerTransferOutcome Failed(string detail) => new(ShoutRunnerTransferResult.Failed, detail);
}

public enum ShoutRunnerTeleportResult { Success, Failed }

public sealed record ShoutRunnerTeleportOutcome(ShoutRunnerTeleportResult Result, string? Detail = null)
{
    public static ShoutRunnerTeleportOutcome Success() => new(ShoutRunnerTeleportResult.Success);
    public static ShoutRunnerTeleportOutcome Failed(string detail) => new(ShoutRunnerTeleportResult.Failed, detail);
}

/// <summary>The result of the one shared readiness/recovery gate every live action (a Lifestream transfer, a
/// teleport, a shout) is required to pass through first — see the familiarization report §13's "confirmed hang" and
/// the reconstruction brief's "HARD ROUTE INVARIANT." <see cref="Ready"/> false means recovery was attempted and
/// failed within its bounded timeout, which ends the entire automation (not just the current World/Data Center) —
/// this must never be a silent infinite wait like the donor's <c>WaitUntilChatReadyAsync</c>.</summary>
public sealed record ShoutRunnerReadinessOutcome(bool Ready, bool RecoveryAttempted, string? Detail = null)
{
    public static ShoutRunnerReadinessOutcome ReadyNow() => new(true, false);
    public static ShoutRunnerReadinessOutcome RecoveredThenReady(string detail) => new(true, true, detail);
    public static ShoutRunnerReadinessOutcome RecoveryFailed(string detail) => new(false, true, detail);
}

public enum ShoutRunnerEventSeverity { Success, Failure, InProgress, Warning }

/// <summary>One structured, timestamped entry in the runtime terminal — see <see cref="ShoutRunnerTerminal"/> for
/// retention and <c>ShoutRunnerOperatorPanel</c> for how <see cref="Severity"/> maps to theme colors (never a
/// hard-coded color). <see cref="DataCenter"/>/<see cref="World"/>/<see cref="Destination"/> are the event's
/// position in the RUN → Data Center → World → Destination hierarchy the terminal renders, left null at whatever
/// level doesn't apply (a RUN-level event has none of the three; a Data-Center-level event has only
/// <see cref="DataCenter"/>).</summary>
public sealed record ShoutRunnerTerminalEvent(
    DateTimeOffset At,
    int RunNumber,
    ShoutRunnerEventSeverity Severity,
    string Text,
    string? DataCenter = null,
    string? World = null,
    string? Destination = null);
