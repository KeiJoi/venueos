namespace VenueOS.Modules.Operations.Travel;

// ----- What one framework-thread read of the game shows (0.3.14) -----
//
// Every field is read in ONE framework callback by the plugin-layer port, so the pieces describe the same frame. The
// pure NativeWorldVisitDriver decides only from these records, which is what makes it testable with a simulated game.

/// <summary>The nearest aetheryte object: <paramref name="BaseId"/> is its Aetheryte sheet row (8/2/9 for the three
/// World Visit cities); <paramref name="Distance"/> is horizontal.</summary>
public sealed record AetheryteObservation(ulong ObjectId, uint BaseId, float Distance, bool Targetable);

/// <summary>An open SelectString menu (the aetheryte's options), entry text as rendered.</summary>
public sealed record MenuObservation(IReadOnlyList<string> Entries);

/// <summary>One row of the World list: its rendered label, the World it resolves to in game data (null when the label is
/// not exactly one known World), and whether the game shows it disabled (congested or unavailable).</summary>
public sealed record WorldRowObservation(string Label, TravelWorld? World, bool Disabled);

/// <summary>The Data Center dropdown (WorldTravelSelect node 2): its items' labels, which are disabled, which is
/// selected, whether a change is still pending, and whether its item-click event is registered to the dropdown itself
/// (<paramref name="EventVerified"/>, the precondition for dispatching a selection).</summary>
public sealed record DataCenterSelectorObservation(IReadOnlyList<string> Labels, IReadOnlyList<bool> Disabled, int SelectedIndex, bool Pending, bool EventVerified);

/// <summary>The World list (regional node 7, or legacy node 20).</summary>
public sealed record WorldListObservation(IReadOnlyList<WorldRowObservation> Rows, int SelectedIndex, bool Pending, bool EventVerified);

/// <summary>The WorldTravelSelect window. <paramref name="RegionalSelectorVisible"/> is the Patch 7.57/7.58 layout with
/// the Data Center dropdown (node 2 visible).</summary>
public sealed record WorldTravelWindowObservation(bool Ready, bool RegionalSelectorVisible, DataCenterSelectorObservation? DataCenters, WorldListObservation? Worlds);

/// <summary>An open SelectYesno dialog.</summary>
public sealed record ConfirmObservation(string Prompt, bool YesEnabled);

/// <param name="TravelQueued">WaitingToVisitOtherWorld or ReadyingVisitOtherWorld: the game is queuing or preparing a
/// World Visit.</param>
/// <param name="Occupied">In an event, cutscene or other state that blocks starting an interaction.</param>
/// <param name="AgentDestinationWorldId">AgentWorldTravel.DestinationWorldId (0 when unset): the game's own record of
/// the selected destination.</param>
/// <param name="CongestionMessages">Congestion chat messages seen since the current operation began.</param>
public sealed record NativeTravelSnapshot(
    bool IsLoggedIn,
    bool HasLocalPlayer,
    bool BetweenAreas,
    bool TravelQueued,
    bool Occupied,
    bool Mounted,
    bool InCombat,
    TravelWorld? CurrentWorld,
    TravelWorld? HomeWorld,
    uint TerritoryId,
    bool TeleportReady,
    AetheryteObservation? NearestAetheryte,
    MenuObservation? Menu,
    WorldTravelWindowObservation? Window,
    ConfirmObservation? Confirm,
    uint AgentDestinationWorldId,
    int CongestionMessages);

// ----- What the driver asks the port to do -----

public enum NativeTravelActionKind { Wait, TeleportToCity, InteractWithAetheryte, SelectMenuEntry, SelectDataCenter, SelectWorld, ConfirmTravel, CloseTravelUi, Finish }

/// <summary>One step for the port. Every acting step names exactly what it expects to act on (index AND label or id),
/// and the port re-reads the game and revalidates with <see cref="NativeWorldVisitDriver.Revalidate"/> in the same
/// framework callback immediately before acting.</summary>
public sealed record NativeTravelAction(NativeTravelActionKind Kind, string Detail, int Index = -1, string? Label = null, uint Id = 0, ulong ObjectId = 0)
{
    public static NativeTravelAction Wait(string why) => new(NativeTravelActionKind.Wait, why);
}

// ----- Phases and outcomes -----

public enum NativeTravelPhase
{
    Idle,
    ResolvingDestination,
    CheckingCapability,
    PreparingAetheryte,
    OpeningWorldVisit,
    SelectingDataCenter,
    SelectingWorld,
    VerifyingSelection,
    ConfirmingTravel,
    WaitingForDeparture,
    TravelInProgress,
    WaitingForStableArrival,
    Completed,
    Cancelled,
    Unavailable,
    Congested,
    TimedOut,
    WrongDestination,
    UnexpectedUi,
    Interrupted,
    Failed,
}

public enum NativeTravelOutcome
{
    /// <summary>On the requested World and logical Data Center, settled.</summary>
    Arrived,
    /// <summary>Already on the requested World; nothing was done.</summary>
    AlreadyThere,
    /// <summary>The World is listed but not selectable, or not listed (congested or unavailable). World-level.</summary>
    WorldUnavailable,
    /// <summary>The target logical Data Center is disabled or missing in the dropdown. Data-Center-level.</summary>
    DataCenterUnavailable,
    /// <summary>Two congestion messages for this operation. World-level.</summary>
    Congested,
    /// <summary>Travel finished, settled, somewhere else (including back on the starting World).</summary>
    WrongDestination,
    /// <summary>Confirmed, but the game never started the transfer.</summary>
    NotStarted,
    /// <summary>The requested combination is not something native World Visit supports.</summary>
    Unsupported,
    /// <summary>The character could not be brought to a state where travel can begin.</summary>
    NotReady,
    /// <summary>The window, list, dropdown or dialog did not look as verified; nothing speculative was clicked.</summary>
    UnexpectedUi,
    /// <summary>The confirmation dialog or the game's destination field named a different World. Never confirmed.</summary>
    SelectionMismatch,
    TimedOut,
    Cancelled,
    /// <summary>Another travel operation owns the service.</summary>
    Busy,
    Failed,
}

/// <param name="Diagnostic">The operation's full step trace, for Diagnostics, /xllog and the QA panel's Copy.</param>
public sealed record NativeTravelResult(int OperationId, NativeTravelOutcome Outcome, string Detail, TravelWorld? Target, TravelWorld? Origin, TravelWorld? Observed, string Diagnostic);

/// <summary>Who asked for travel, so the QA tool and ShoutRunner can never run at the same time unnoticed.</summary>
public enum NativeTravelOwner { ShoutRunner, QaTest }

/// <param name="PreferredCities">Where to perform the World Visit when the character is not already at a World Visit
/// aetheryte, in order of preference. ShoutRunner passes its route's city destinations, so the next World's route can
/// begin where the character arrives.</param>
public sealed record NativeTravelRequest(string TargetWorld, NativeTravelOwner Owner, IReadOnlyList<WorldVisitCity> PreferredCities);

/// <summary>The game text the driver matches against, read from game data by the port (English fallbacks otherwise).</summary>
public sealed record NativeTravelTexts(string WorldVisitOption, IReadOnlyList<(string Prefix, string Suffix)> ConfirmationTemplates)
{
    public static NativeTravelTexts English { get; } = new(NativeTravelPolicy.EnglishWorldVisitOption, NativeTravelPolicy.EnglishConfirmationTemplates);
}

/// <summary>Live status for the QA panel and diagnostics.</summary>
public sealed record NativeTravelStatus(int OperationId, NativeTravelOwner? Owner, string? TargetWorld, NativeTravelPhase Phase, string Detail, DateTimeOffset? StartedAtUtc)
{
    public static NativeTravelStatus Idle { get; } = new(0, null, null, NativeTravelPhase.Idle, "Idle", null);
    public bool IsActive => Owner is not null;
}
