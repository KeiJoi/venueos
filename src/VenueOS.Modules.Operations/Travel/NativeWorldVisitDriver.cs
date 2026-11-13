using VenueOS.Modules.Operations.ShoutRunner;

namespace VenueOS.Modules.Operations.Travel;

/// <summary>The pure decision engine for ONE native World Visit (0.3.14): aetheryte → "Visit Another World Server" →
/// logical Data Center dropdown → World row → verified confirmation → departure → stable arrival. It reads only
/// <see cref="NativeTravelSnapshot"/>s and returns one <see cref="NativeTravelAction"/> at a time. It never touches
/// the game, which is what lets the whole flow be tested against a simulated World Visit window.
///
/// <para><b>Safety invariants</b> (each covered by tests):</para>
/// <list type="bullet">
/// <item>No acting step is chosen from positions alone. The Data Center item is chosen by its label, and the World
/// row by the World its label resolves to in game data. Every row in the list must belong to the target logical Data
/// Center (a stale list from the previous dropdown page is never used). The port revalidates the exact index + label/id
/// immediately before acting (<see cref="Revalidate"/>).</item>
/// <item>Travel is confirmed only when the Yes/No prompt's first line is exactly the game's "Travel to &lt;World&gt;?"
/// template for the target World, AND the game's own destination field (AgentWorldTravel.DestinationWorldId) is
/// either unset or the target's id. Anything else closes the UI with <see cref="NativeTravelOutcome.SelectionMismatch"/>.</item>
/// <item>One World selection and one confirmation per operation: no duplicate travel request, no retry after an
/// unknown destination.</item>
/// <item>Every wait is bounded, and arrival needs the 0.3.12 stable observation (<see cref="ShoutRunnerArrivalVerifier"/>,
/// shared here) on the target World and logical Data Center.</item>
/// <item>No logout, character select or Home World detour exists in this flow at all.</item>
/// </list>
///
/// <para><b>Data Center selection is gated</b> (<paramref name="allowDataCenterSelection"/>): the dropdown interaction
/// follows the public SRankSentinel 7.58 implementation and SimpleTweaks' layout, but has not been verified on this
/// client. Until the owner's live QA verifies it, a World on another logical Data Center finishes as
/// <see cref="NativeTravelOutcome.Unsupported"/> with an explicit reason unless the QA switch enables it.</para></summary>
public sealed class NativeWorldVisitDriver
{
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(7);
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ListSettleTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DepartureTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ArrivalTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InteractRetryInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DataCenterReselectInterval = TimeSpan.FromSeconds(2);

    /// <summary>Horizontal distance within which the aetheryte is interacted with directly. Teleport arrival puts the
    /// character beside the city aetheryte. Unverified live: the QA panel shows the measured distance.</summary>
    public const float AetheryteInteractRange = 8f;

    public const int MaxInteractAttempts = 2;
    public const int MaxDataCenterSelectAttempts = 2;
    public const int MaxTeleportAttempts = 1;

    private readonly TravelWorld target;
    private readonly IReadOnlyList<WorldVisitCity> preferredCities;
    private readonly NativeTravelTexts texts;
    private readonly IReadOnlyCollection<string> regionDataCenters;
    private readonly bool allowDataCenterSelection;
    private readonly DateTimeOffset startedAt;
    private readonly List<string> trace = [];

    private DateTimeOffset phaseEnteredAt;
    private DateTimeOffset? teleportedAt, lastInteractAt, menuSelectedAt, lastDataCenterSelectAt, worldSelectedAt, confirmedAt;
    private int teleportAttempts, interactAttempts, dataCenterSelectAttempts;
    private bool leftoverUiClosed, departureSeen, windowSeen, openedUi;
    private ShoutRunnerArrivalVerifier? arrival;

    public NativeWorldVisitDriver(TravelWorld target, IReadOnlyList<WorldVisitCity> preferredCities, NativeTravelTexts texts, IReadOnlyCollection<string> regionDataCenters, bool allowDataCenterSelection, DateTimeOffset now)
    {
        this.target = target;
        this.preferredCities = preferredCities;
        this.texts = texts;
        this.regionDataCenters = regionDataCenters;
        this.allowDataCenterSelection = allowDataCenterSelection;
        startedAt = now;
        phaseEnteredAt = now;
        Phase = NativeTravelPhase.ResolvingDestination;
        Note(now, $"requested {Describe(target)}; Data Center selection {(allowDataCenterSelection ? "ENABLED (QA)" : "disabled (awaiting live verification)")}");
    }

    public NativeTravelPhase Phase { get; private set; }
    public NativeTravelOutcome? Outcome { get; private set; }
    public string OutcomeDetail { get; private set; } = string.Empty;
    public TravelWorld Target => target;
    public TravelWorld? Origin { get; private set; }
    public TravelWorld? LastObserved { get; private set; }
    public IReadOnlyList<string> Trace => trace;
    public bool TravelConfirmed => confirmedAt is not null;

    /// <summary>Decides the next step from the latest snapshot.</summary>
    public NativeTravelAction Next(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (Outcome is not null) return Finished();
        if (s.CurrentWorld is not null) LastObserved = s.CurrentWorld;

        if (now - startedAt >= OverallTimeout) return Fail(now, NativeTravelOutcome.TimedOut, $"travel did not finish within {OverallTimeout.TotalMinutes:0} minutes", closeUi: !TravelConfirmed);
        if (s.CongestionMessages >= 2) return Fail(now, NativeTravelOutcome.Congested, $"{target.Name} reported congestion twice", closeUi: !TravelConfirmed);

        switch (Phase)
        {
            case NativeTravelPhase.ResolvingDestination:
                if (!s.IsLoggedIn || !s.HasLocalPlayer || s.CurrentWorld is null)
                    return Elapsed(now) >= StepTimeout ? Fail(now, NativeTravelOutcome.NotReady, "the character's current World could not be read") : NativeTravelAction.Wait("waiting for the character");
                Origin = s.CurrentWorld;
                Enter(now, NativeTravelPhase.CheckingCapability, $"starting on {Describe(s.CurrentWorld)}; home {Describe(s.HomeWorld)}");
                goto case NativeTravelPhase.CheckingCapability;

            case NativeTravelPhase.CheckingCapability:
                if (s.CurrentWorld!.Id == target.Id) return Succeed(now, NativeTravelOutcome.AlreadyThere, $"already on {target.Name}");
                if (!NativeTravelPolicy.CanVisit(s.CurrentWorld, s.HomeWorld, target))
                    return Fail(now, NativeTravelOutcome.Unsupported, $"{target.Name} is not reachable by World Visit from {s.CurrentWorld.Name} (different region, or the character is outside its Home World's region)");
                if (s.CurrentWorld.LogicalDataCenterId != target.LogicalDataCenterId && !allowDataCenterSelection)
                    return Fail(now, NativeTravelOutcome.Unsupported, $"{target.Name} is on {target.LogicalDataCenter}; selecting another Data Center in the World Visit window is awaiting live verification (enable it in Native Travel QA)");
                Enter(now, NativeTravelPhase.PreparingAetheryte, "capability confirmed");
                goto case NativeTravelPhase.PreparingAetheryte;

            case NativeTravelPhase.PreparingAetheryte:
                return PrepareAetheryte(s, now);

            case NativeTravelPhase.OpeningWorldVisit:
                return OpenWorldVisit(s, now);

            case NativeTravelPhase.SelectingDataCenter:
                return SelectDataCenter(s, now);

            case NativeTravelPhase.SelectingWorld:
                return SelectWorld(s, now);

            case NativeTravelPhase.VerifyingSelection:
                return VerifySelection(s, now);

            case NativeTravelPhase.ConfirmingTravel:
                // Reached only if the confirm click was not performed (revalidation failed); re-verify from scratch.
                Enter(now, NativeTravelPhase.VerifyingSelection, "confirmation not performed; re-verifying");
                return VerifySelection(s, now);

            case NativeTravelPhase.WaitingForDeparture:
            case NativeTravelPhase.TravelInProgress:
            case NativeTravelPhase.WaitingForStableArrival:
                return ObserveTravel(s, now);
        }

        return Fail(now, NativeTravelOutcome.Failed, $"unexpected phase {Phase}");
    }

    /// <summary>Called by the port in the SAME framework callback as the action, against a fresh snapshot. False means
    /// the game changed since the decision; the port then does nothing.</summary>
    public bool Revalidate(NativeTravelAction a, NativeTravelSnapshot s) => a.Kind switch
    {
        NativeTravelActionKind.TeleportToCity => !s.BetweenAreas && !s.TravelQueued && s.TeleportReady && s.Window is null && s.Confirm is null,
        NativeTravelActionKind.InteractWithAetheryte => s.NearestAetheryte is { Targetable: true } n && n.ObjectId == a.ObjectId && n.Distance <= AetheryteInteractRange
            && s.Menu is null && s.Window is null && s.Confirm is null && !s.BetweenAreas && !s.Occupied && !s.Mounted && !s.InCombat,
        NativeTravelActionKind.SelectMenuEntry => s.Menu is { } m && a.Index >= 0 && a.Index < m.Entries.Count && m.Entries[a.Index] == a.Label
            && NativeTravelPolicy.IsWorldVisitMenuEntry(m.Entries[a.Index], texts.WorldVisitOption),
        NativeTravelActionKind.SelectDataCenter => allowDataCenterSelection && s.Window is { Ready: true, RegionalSelectorVisible: true, DataCenters: { } d } && s.Confirm is null
            && d is { Pending: false, EventVerified: true } && a.Index >= 0 && a.Index < d.Labels.Count && string.Equals(d.Labels[a.Index], a.Label, StringComparison.OrdinalIgnoreCase)
            && !d.Disabled[a.Index] && string.Equals(a.Label, target.LogicalDataCenter, StringComparison.OrdinalIgnoreCase),
        NativeTravelActionKind.SelectWorld => s.Window is { Ready: true, Worlds: { } w } win && s.Confirm is null && w is { Pending: false, EventVerified: true }
            && a.Index >= 0 && a.Index < w.Rows.Count && w.Rows[a.Index].World?.Id == target.Id && a.Id == target.Id && !w.Rows[a.Index].Disabled
            && (!win.RegionalSelectorVisible || SelectedDataCenter(win) is { } dc && string.Equals(dc, target.LogicalDataCenter, StringComparison.OrdinalIgnoreCase)),
        NativeTravelActionKind.ConfirmTravel => s.Confirm is { YesEnabled: true } c && NativeTravelPolicy.IsConfirmationFor(c.Prompt, target.Name, texts.ConfirmationTemplates)
            && (s.AgentDestinationWorldId == 0 || s.AgentDestinationWorldId == target.Id) && !TravelConfirmed,
        _ => true,
    };

    /// <summary>Records what the port actually did.</summary>
    public void Executed(NativeTravelAction a, bool performed, DateTimeOffset now)
    {
        Note(now, $"{(performed ? "did" : "SKIPPED (revalidation failed)")} {a.Kind}: {a.Detail}");
        if (!performed) return;
        switch (a.Kind)
        {
            case NativeTravelActionKind.TeleportToCity: teleportAttempts++; teleportedAt = now; break;
            case NativeTravelActionKind.InteractWithAetheryte: interactAttempts++; lastInteractAt = now; openedUi = true; break;
            case NativeTravelActionKind.SelectMenuEntry: menuSelectedAt = now; break;
            case NativeTravelActionKind.SelectDataCenter: dataCenterSelectAttempts++; lastDataCenterSelectAt = now; break;
            case NativeTravelActionKind.SelectWorld: worldSelectedAt = now; Enter(now, NativeTravelPhase.VerifyingSelection, $"selected {target.Name}; waiting for the confirmation"); break;
            case NativeTravelActionKind.ConfirmTravel: confirmedAt = now; Enter(now, NativeTravelPhase.WaitingForDeparture, $"confirmed travel to {target.Name}"); break;
        }
    }

    /// <summary>Stop/cancel. Before the confirmation the World Visit UI is closed; after it the game may still complete
    /// the transfer (the World Visit guide: once the queue window appears "the transfer cannot be canceled").</summary>
    public NativeTravelAction Cancel(string reason, DateTimeOffset now)
    {
        if (Outcome is not null) return Finished();
        return Fail(now, NativeTravelOutcome.Cancelled,
            TravelConfirmed ? $"{reason}; travel to {target.Name} was already confirmed, so the game may still complete it" : reason,
            closeUi: !TravelConfirmed);
    }

    // ----- phases -----

    private NativeTravelAction PrepareAetheryte(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.BetweenAreas || s.TravelQueued)
            return teleportedAt is { } t && now - t >= TeleportTimeout ? Fail(now, NativeTravelOutcome.NotReady, "the teleport did not finish") : NativeTravelAction.Wait("teleport in progress");
        if (s.InCombat) return Fail(now, NativeTravelOutcome.NotReady, "the character is in combat");

        // A menu left open by someone else is closed once, never driven.
        if ((s.Menu is not null || s.Window is not null || s.Confirm is not null) && interactAttempts == 0)
        {
            if (leftoverUiClosed) return Elapsed(now) >= StepTimeout ? Fail(now, NativeTravelOutcome.UnexpectedUi, "a travel menu that VenueOS did not open stayed open") : NativeTravelAction.Wait("waiting for the leftover menu to close");
            leftoverUiClosed = true;
            return new(NativeTravelActionKind.CloseTravelUi, "close a menu VenueOS did not open");
        }

        var city = WorldVisitCities.ForTerritory(s.TerritoryId);
        if (city is not null && s.NearestAetheryte is { Targetable: true } aetheryte && aetheryte.Distance <= AetheryteInteractRange)
        {
            if (s.Mounted) return Fail(now, NativeTravelOutcome.NotReady, "the character is mounted at the aetheryte");
            if (s.Occupied) return Elapsed(now) >= StepTimeout ? Fail(now, NativeTravelOutcome.NotReady, "the character stayed occupied") : NativeTravelAction.Wait("waiting for the character to be free");
            Enter(now, NativeTravelPhase.OpeningWorldVisit, $"at the {city.Name} aetheryte ({aetheryte.Distance:0.0}y, object base id {aetheryte.BaseId})");
            return OpenWorldVisit(s, now);
        }

        if (teleportAttempts >= MaxTeleportAttempts)
            return Fail(now, NativeTravelOutcome.NotReady, city is null
                ? $"after teleporting, the character is not in a World Visit city (territory {s.TerritoryId})"
                : $"after teleporting, no aetheryte is within {AetheryteInteractRange:0}y (nearest {s.NearestAetheryte?.Distance.ToString("0.0") ?? "none"})");
        if (!s.TeleportReady)
            return Elapsed(now) >= StepTimeout ? Fail(now, NativeTravelOutcome.NotReady, "Teleport is not available right now") : NativeTravelAction.Wait("waiting for Teleport to be usable");

        var destination = city ?? preferredCities.FirstOrDefault() ?? WorldVisitCities.LimsaLominsa;
        return new(NativeTravelActionKind.TeleportToCity, $"teleport to {destination.Name} for World Visit", Id: destination.AetheryteId, Label: destination.Name);
    }

    private NativeTravelAction OpenWorldVisit(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.Window is not null)
        {
            windowSeen = true;
            Enter(now, NativeTravelPhase.SelectingDataCenter, "World Visit window open");
            return SelectDataCenter(s, now);
        }

        if (s.Menu is { } menu)
        {
            var index = -1;
            for (var i = 0; i < menu.Entries.Count; i++)
                if (NativeTravelPolicy.IsWorldVisitMenuEntry(menu.Entries[i], texts.WorldVisitOption)) { index = i; break; }
            if (index < 0) return Fail(now, NativeTravelOutcome.UnexpectedUi, $"the aetheryte menu has no \"{texts.WorldVisitOption}\" option (entries: {string.Join(" | ", menu.Entries)})");
            if (menuSelectedAt is null) return new(NativeTravelActionKind.SelectMenuEntry, $"choose \"{menu.Entries[index]}\"", index, menu.Entries[index]);
            return now - menuSelectedAt.Value >= StepTimeout ? Fail(now, NativeTravelOutcome.UnexpectedUi, "the World Visit window did not open") : NativeTravelAction.Wait("waiting for the World Visit window");
        }

        if (menuSelectedAt is { } selected)
            return now - selected >= StepTimeout ? Fail(now, NativeTravelOutcome.UnexpectedUi, "the World Visit window did not open") : NativeTravelAction.Wait("waiting for the World Visit window");
        if (lastInteractAt is { } last && now - last < InteractRetryInterval) return NativeTravelAction.Wait("waiting for the aetheryte menu");
        if (interactAttempts >= MaxInteractAttempts) return Fail(now, NativeTravelOutcome.UnexpectedUi, "the aetheryte menu did not open");
        if (s.NearestAetheryte is not { Targetable: true } aetheryte || aetheryte.Distance > AetheryteInteractRange)
            return Fail(now, NativeTravelOutcome.NotReady, "the aetheryte is no longer within reach");
        return new(NativeTravelActionKind.InteractWithAetheryte, $"interact with the aetheryte ({aetheryte.Distance:0.0}y)", ObjectId: aetheryte.ObjectId, Id: aetheryte.BaseId);
    }

    private NativeTravelAction SelectDataCenter(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.Window is not { } window) return WindowLost(now);
        if (!window.Ready) return Elapsed(now) >= StepTimeout ? Fail(now, NativeTravelOutcome.UnexpectedUi, "the World Visit window never became ready") : NativeTravelAction.Wait("waiting for the World Visit window");

        if (!window.RegionalSelectorVisible)
        {
            if (target.LogicalDataCenterId != Origin!.LogicalDataCenterId)
                return Fail(now, NativeTravelOutcome.Unsupported, $"the World Visit window shows no Data Center selector, so {target.LogicalDataCenter} cannot be chosen");
            Enter(now, NativeTravelPhase.SelectingWorld, "legacy single-Data-Center window");
            return SelectWorld(s, now);
        }

        if (window.DataCenters is not { } dcs || dcs.Pending || !dcs.EventVerified || !SelectorLooksValid(dcs, out var why))
            return Elapsed(now) >= ListSettleTimeout
                ? Fail(now, NativeTravelOutcome.UnexpectedUi, $"the Data Center selector could not be verified ({(window.DataCenters is null ? "it is shown, but not readable as a dropdown list" : !window.DataCenters.EventVerified ? "its click event is not registered to the dropdown" : window.DataCenters.Pending ? "a change is still pending" : DescribeInvalid(window.DataCenters))}); nothing was clicked")
                : NativeTravelAction.Wait("waiting for a verifiable Data Center selector");

        if (string.Equals(SelectedDataCenter(window), target.LogicalDataCenter, StringComparison.OrdinalIgnoreCase))
        {
            Enter(now, NativeTravelPhase.SelectingWorld, $"{target.LogicalDataCenter} is selected");
            return SelectWorld(s, now);
        }

        var index = IndexOf(dcs.Labels, target.LogicalDataCenter);
        if (index < 0) return Fail(now, NativeTravelOutcome.DataCenterUnavailable, $"{target.LogicalDataCenter} is not offered by the Data Center selector ({string.Join(", ", dcs.Labels)})");
        if (dcs.Disabled[index]) return Fail(now, NativeTravelOutcome.DataCenterUnavailable, $"{target.LogicalDataCenter} is disabled in the Data Center selector");
        if (!allowDataCenterSelection) return Fail(now, NativeTravelOutcome.Unsupported, "Data Center selection is awaiting live verification");
        if (lastDataCenterSelectAt is { } last && now - last < DataCenterReselectInterval) return NativeTravelAction.Wait($"waiting for the {target.LogicalDataCenter} page");
        if (dataCenterSelectAttempts >= MaxDataCenterSelectAttempts)
            return Fail(now, NativeTravelOutcome.UnexpectedUi, $"selecting {target.LogicalDataCenter} in the dropdown did not take effect (still {SelectedDataCenter(window) ?? "none"})");
        return new(NativeTravelActionKind.SelectDataCenter, $"select Data Center {target.LogicalDataCenter} (item {index})", index, dcs.Labels[index]);
    }

    private NativeTravelAction SelectWorld(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.Window is not { } window) return WindowLost(now);
        if (window.RegionalSelectorVisible && !string.Equals(SelectedDataCenter(window), target.LogicalDataCenter, StringComparison.OrdinalIgnoreCase))
        {
            Enter(now, NativeTravelPhase.SelectingDataCenter, $"the selector shows {SelectedDataCenter(window) ?? "none"}, not {target.LogicalDataCenter}");
            return SelectDataCenter(s, now);
        }

        if (window.Worlds is not { } list || list.Pending || !list.EventVerified || list.Rows.Count == 0)
            return Elapsed(now) >= ListSettleTimeout
                ? Fail(now, NativeTravelOutcome.UnexpectedUi, $"the World list could not be verified ({(window.Worlds is null ? "missing" : !window.Worlds.EventVerified ? "its click event is not registered to the window" : window.Worlds.Pending ? "still updating" : "empty")}); nothing was clicked")
                : NativeTravelAction.Wait("waiting for a verifiable World list");

        // The dropdown can switch before the rows are rebuilt. Every row must belong to the target Data Center.
        var foreign = list.Rows.FirstOrDefault(r => r.World is null || r.World.LogicalDataCenterId != target.LogicalDataCenterId);
        if (foreign is not null)
            return Elapsed(now) >= ListSettleTimeout
                ? Fail(now, NativeTravelOutcome.UnexpectedUi, $"the World list does not belong to {target.LogicalDataCenter} (row \"{foreign.Label}\" resolves to {Describe(foreign.World)}); nothing was clicked")
                : NativeTravelAction.Wait($"waiting for the {target.LogicalDataCenter} World list");

        var matches = Enumerable.Range(0, list.Rows.Count).Where(i => list.Rows[i].World!.Id == target.Id).ToList();
        if (matches.Count == 0) return Fail(now, NativeTravelOutcome.WorldUnavailable, $"{target.Name} is not listed on the {target.LogicalDataCenter} page ({string.Join(", ", list.Rows.Select(r => r.Label))})");
        if (matches.Count > 1) return Fail(now, NativeTravelOutcome.UnexpectedUi, $"{target.Name} appears {matches.Count} times in the World list");
        var row = matches[0];
        if (list.Rows[row].Disabled) return Fail(now, NativeTravelOutcome.WorldUnavailable, $"{target.Name} is listed but cannot be selected (congested or unavailable)");
        if (worldSelectedAt is not null) return NativeTravelAction.Wait("waiting for the confirmation");
        return new(NativeTravelActionKind.SelectWorld, $"select World {target.Name} (row {row}, \"{list.Rows[row].Label}\")", row, list.Rows[row].Label, target.Id);
    }

    private NativeTravelAction VerifySelection(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.Confirm is { } confirm)
        {
            var promptOk = NativeTravelPolicy.IsConfirmationFor(confirm.Prompt, target.Name, texts.ConfirmationTemplates);
            var agentOk = s.AgentDestinationWorldId == 0 || s.AgentDestinationWorldId == target.Id;
            if (!promptOk || !agentOk)
                return Fail(now, NativeTravelOutcome.SelectionMismatch,
                    $"the confirmation does not name {target.Name} (prompt \"{FirstLine(confirm.Prompt)}\", game destination id {s.AgentDestinationWorldId}); NOT confirmed");
            if (!confirm.YesEnabled)
                return now - (worldSelectedAt ?? phaseEnteredAt) >= StepTimeout ? Fail(now, NativeTravelOutcome.UnexpectedUi, "the confirmation's Yes button stayed disabled") : NativeTravelAction.Wait("waiting for Yes to be enabled");
            Enter(now, NativeTravelPhase.ConfirmingTravel, $"confirmation verified: \"{FirstLine(confirm.Prompt)}\" (game destination id {s.AgentDestinationWorldId})");
            return new(NativeTravelActionKind.ConfirmTravel, $"confirm travel to {target.Name}", Label: target.Name, Id: target.Id);
        }

        if (s.Window is null && !s.TravelQueued) return WindowLost(now);
        return now - (worldSelectedAt ?? phaseEnteredAt) >= StepTimeout
            ? Fail(now, NativeTravelOutcome.UnexpectedUi, "no confirmation appeared after selecting the World")
            : NativeTravelAction.Wait("waiting for the confirmation");
    }

    private NativeTravelAction ObserveTravel(NativeTravelSnapshot s, DateTimeOffset now)
    {
        if (s.TravelQueued || s.BetweenAreas)
        {
            if (!departureSeen) Note(now, s.TravelQueued ? "the game queued the World Visit" : "zone transition started");
            departureSeen = true;
            if (Phase != NativeTravelPhase.TravelInProgress) Enter(now, NativeTravelPhase.TravelInProgress, "travel in progress");
        }

        if (!departureSeen)
            return now - confirmedAt!.Value >= DepartureTimeout
                ? Fail(now, NativeTravelOutcome.NotStarted, $"the game did not start travel to {target.Name} within {DepartureTimeout.TotalSeconds:0}s of confirming")
                : NativeTravelAction.Wait("waiting for the transfer to start");

        if (now - confirmedAt!.Value >= ArrivalTimeout)
            return Fail(now, NativeTravelOutcome.TimedOut, $"arrival on {target.Name} was not confirmed within {ArrivalTimeout.TotalMinutes:0} minutes");

        arrival ??= new ShoutRunnerArrivalVerifier(target.Name, Origin?.Name, target.LogicalDataCenter);
        var verdict = arrival.Observe(new ShoutRunnerArrivalSample(s.IsLoggedIn, s.HasLocalPlayer, s.BetweenAreas, true, s.CurrentWorld?.Name, s.TravelQueued, s.CurrentWorld?.LogicalDataCenter), now);
        if (verdict == ShoutRunnerArrivalVerifier.Verdict.Continue)
        {
            if (!s.BetweenAreas && !s.TravelQueued && Phase == NativeTravelPhase.TravelInProgress) Enter(now, NativeTravelPhase.WaitingForStableArrival, $"settled on {Describe(s.CurrentWorld)}");
            return NativeTravelAction.Wait("waiting for a stable arrival");
        }

        if (verdict == ShoutRunnerArrivalVerifier.Verdict.Arrived && s.CurrentWorld?.Id == target.Id)
            return Succeed(now, NativeTravelOutcome.Arrived, $"arrived on {Describe(s.CurrentWorld)} ({arrival.DescribeHistory()})");
        return Fail(now, NativeTravelOutcome.WrongDestination, $"{arrival.DescribeWrongWorld(s.CurrentWorld?.Name)} [{arrival.DescribeHistory()}]");
    }

    // ----- helpers -----

    private bool SelectorLooksValid(DataCenterSelectorObservation dcs, out string why)
    {
        why = string.Empty;
        if (dcs.Labels.Count is < 1 or > 8 || dcs.Disabled.Count != dcs.Labels.Count) { why = $"{dcs.Labels.Count} items"; return false; }
        if (dcs.Labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != dcs.Labels.Count) { why = "duplicate items"; return false; }
        var unknown = dcs.Labels.FirstOrDefault(l => !regionDataCenters.Contains(l, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null) { why = $"item \"{unknown}\" is not a Data Center of this region"; return false; }
        return true;
    }

    private string DescribeInvalid(DataCenterSelectorObservation dcs) => SelectorLooksValid(dcs, out var why) ? "valid" : why;

    private static string? SelectedDataCenter(WorldTravelWindowObservation window) =>
        window.DataCenters is { } d && d.SelectedIndex >= 0 && d.SelectedIndex < d.Labels.Count ? d.Labels[d.SelectedIndex] : null;

    private static int IndexOf(IReadOnlyList<string> labels, string value)
    {
        for (var i = 0; i < labels.Count; i++) if (string.Equals(labels[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private NativeTravelAction WindowLost(DateTimeOffset now) =>
        Fail(now, NativeTravelOutcome.UnexpectedUi, windowSeen ? "the World Visit window closed before travel was confirmed" : "the World Visit window is not open");

    private TimeSpan Elapsed(DateTimeOffset now) => now - phaseEnteredAt;

    private void Enter(DateTimeOffset now, NativeTravelPhase phase, string detail)
    {
        Phase = phase;
        phaseEnteredAt = now;
        Note(now, $"→ {phase}: {detail}");
    }

    private NativeTravelAction Succeed(DateTimeOffset now, NativeTravelOutcome outcome, string detail)
    {
        Outcome = outcome;
        OutcomeDetail = detail;
        Phase = NativeTravelPhase.Completed;
        Note(now, $"FINISHED {outcome}: {detail}");
        return Finished();
    }

    private NativeTravelAction Fail(DateTimeOffset now, NativeTravelOutcome outcome, string detail, bool closeUi = true)
    {
        Outcome = outcome;
        OutcomeDetail = detail;
        Phase = outcome switch
        {
            NativeTravelOutcome.Cancelled => NativeTravelPhase.Cancelled,
            NativeTravelOutcome.Congested => NativeTravelPhase.Congested,
            NativeTravelOutcome.TimedOut => NativeTravelPhase.TimedOut,
            NativeTravelOutcome.WrongDestination => NativeTravelPhase.WrongDestination,
            NativeTravelOutcome.UnexpectedUi or NativeTravelOutcome.SelectionMismatch => NativeTravelPhase.UnexpectedUi,
            NativeTravelOutcome.WorldUnavailable or NativeTravelOutcome.DataCenterUnavailable or NativeTravelOutcome.Unsupported => NativeTravelPhase.Unavailable,
            NativeTravelOutcome.NotStarted => NativeTravelPhase.Interrupted,
            _ => NativeTravelPhase.Failed,
        };
        Note(now, $"FINISHED {outcome}: {detail}");
        // Before confirmation, the menus THIS operation opened are closed so the character is left free. A failure before
        // VenueOS opened anything closes nothing (a menu the player opened is theirs). After confirmation the game owns
        // the transfer and nothing is touched.
        return closeUi && openedUi && !TravelConfirmed ? new(NativeTravelActionKind.CloseTravelUi, $"close travel menus ({outcome})") : Finished();
    }

    private NativeTravelAction Finished() => new(NativeTravelActionKind.Finish, $"{Outcome}: {OutcomeDetail}");

    private void Note(DateTimeOffset now, string text)
    {
        if (trace.Count < 200) trace.Add($"+{(now - startedAt).TotalSeconds,6:0.0}s {text}");
    }

    private static string FirstLine(string text) => text.Replace("\r", string.Empty).Split('\n', 2)[0].Trim();

    public static string Describe(TravelWorld? world) => world is null ? "(unknown)" : $"{world.Name} [{world.LogicalDataCenter}] (World id {world.Id}, DC id {world.LogicalDataCenterId}, region {world.RegionId})";
}
