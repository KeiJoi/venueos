using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Modules.Operations.Travel;

namespace VenueOS.Services.Tests;

/// <summary>Test World catalog with the logical Data Centers the installed Patch 7.58 game data reports
/// (<c>World.Unknown0</c>: Aether 4, Primal 5, Crystal 8, Dynamis 11; North America = region 2) and the real World ids for
/// the Worlds used in the owner's screenshots. Materia (region 4) is included for cross-region cases.</summary>
internal sealed class TestTravelWorldCatalog : ITravelWorldCatalog
{
    private static readonly Dictionary<string, (uint Id, uint Region)> DataCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aether"] = (4, 2), ["Primal"] = (5, 2), ["Crystal"] = (8, 2), ["Dynamis"] = (11, 2), ["Materia"] = (9, 4),
    };

    private static readonly Dictionary<string, uint> KnownIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jenova"] = 40, ["Faerie"] = 54, ["Siren"] = 57, ["Gilgamesh"] = 63, ["Midgardsormr"] = 65, ["Adamantoise"] = 73, ["Cactuar"] = 79,
        ["Sargatanas"] = 99, ["Behemoth"] = 78, ["Cuchulainn"] = 408, ["Ravana"] = 21,
    };

    public static readonly string[] MateriaWorlds = ["Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"];

    private readonly Dictionary<string, TravelWorld> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, TravelWorld> byId = [];

    public TestTravelWorldCatalog()
    {
        uint next = 1000;
        foreach (var dc in ShoutRunnerCatalog.CanonicalDataCenterOrder)
            foreach (var world in ShoutRunnerCatalog.WorldsIn(dc)) Add(world, dc, ref next);
        foreach (var world in MateriaWorlds) Add(world, "Materia", ref next);
    }

    private void Add(string world, string dc, ref uint next)
    {
        var (dcId, region) = DataCenters[dc];
        var id = KnownIds.TryGetValue(world, out var known) ? known : next++;
        var entry = new TravelWorld(id, world, dcId, dc, region);
        byName[world] = entry;
        byId[id] = entry;
    }

    public TravelWorld? Resolve(string worldName) => byName.TryGetValue(NativeTravelPolicy.CleanLabel(worldName), out var w) ? w : null;
    public TravelWorld? ResolveById(uint worldId) => byId.TryGetValue(worldId, out var w) ? w : null;
    public IReadOnlyCollection<string> LogicalDataCentersInRegion(uint regionId) => [.. byName.Values.Where(w => w.RegionId == regionId).Select(w => w.LogicalDataCenter).Distinct()];
    public TravelWorld this[string name] => byName[name];
    public IEnumerable<TravelWorld> WorldsIn(string dc) => byName.Values.Where(w => string.Equals(w.LogicalDataCenter, dc, StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A deterministic simulation of the Patch 7.58 World Visit flow, modelled on the owner's screenshots
/// (C:\FFXIVplugs\FFXIV-World-DC-Travel): the aetheryte's SelectString menu ("Aethernet.", "Visit Another World Server.",
/// …); the World Visit window with a Data Center dropdown that defaults to the CURRENT Data Center; a World list of the
/// selected Data Center's 8 Worlds in alphabetical order, in which the current World is listed but disabled; and the
/// one-line "Travel to &lt;World&gt;?" confirmation. After confirmation: queue → zone transition → arrival. Time advances
/// only through <see cref="DelayAsync"/>, so every scenario is exact and instantaneous. Fault switches inject the
/// failure modes the engine must survive. This proves the VenueOS logic, not the real game UI.</summary>
internal sealed class NativeTravelSimulator : INativeTravelGamePort
{
    public static readonly string[] AetheryteMenu = ["Aethernet.", "Visit Another World Server.", "Set Home Point.", "Register Favored Destination.", "Register Security Token Free Destination.", "Cancel."];

    public readonly TestTravelWorldCatalog Catalog;
    public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    public TravelWorld Current;
    public TravelWorld Home;
    public uint Territory = WorldVisitCities.Uldah.TerritoryId;
    public float AetheryteDistance = 3f;
    public bool InCombat, Mounted, LoggedOut;

    // UI state
    public bool MenuOpen, WindowOpen, ConfirmOpen;
    public string SelectedDc = string.Empty;
    public string[] DcOrder = ["Aether", "Crystal", "Dynamis", "Primal"];
    public string? PromptOverride;
    public uint AgentDestination;
    private string? rowsDc;              // which DC the rendered rows currently belong to
    private int staleSnapshotsLeft;
    private TravelWorld? pendingDestination;

    // Travel timeline
    private DateTimeOffset? confirmedAt;
    private DateTimeOffset? teleportAt;
    private WorldVisitCity? teleportCity;

    // Fault switches
    public int SelectWorldRowOffset;              // a misclick selects row index + offset
    public bool DropdownIgnoresSelection;         // selecting a DC has no effect
    public bool DcEventUnverified, ListEventUnverified;
    public bool DcLabelsCorrupt;
    public string[]? DcLabelsOverride;            // exact labels the dropdown reports
    public bool DropdownReportsNoSelection;       // live 7.58 (2026-11-13): selected index -1 while a page is shown
    public int SectionHiddenSnapshots;            // live 7.58: the window is ready a moment before its section shows
    public bool LegacyWindow;                     // single-Data-Center window without a selector
    public bool ArrowsReversed;                   // fault: each arrow moves the other way (wrapping)
    private int sectionHiddenLeft;
    public int StaleRowSnapshotsAfterDcChange = 1;  // rows lag the dropdown by this many snapshots
    public bool StaleRowsForever;
    public bool WindowNeverOpens;
    public bool NeverDeparts;
    public bool NeverArrives;
    public TravelWorld? ArriveAt;                 // wrong-World arrival
    public HashSet<string> DisabledWorlds = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledDcs = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HiddenWorlds = new(StringComparer.OrdinalIgnoreCase);
    public int CongestionMessagesAfterConfirm;
    public Action? BeforeExecute;                 // mutate the game between decision and action (revalidation tests)
    public Func<CancellationToken, Task>? DelayHook;

    // Observations
    public readonly List<NativeTravelAction> Executed = [];
    public readonly List<NativeTravelAction> Refused = [];
    public int CongestionMessages;
    public int SnapshotCount;
    private int operationId;

    public NativeTravelSimulator(string current, string? home = null)
    {
        Catalog = new TestTravelWorldCatalog();
        Current = Catalog[current];
        Home = Catalog[home ?? current];
    }

    public NativeTravelTexts Texts { get; } = NativeTravelTexts.English;
    public DateTimeOffset UtcNow => Now;

    public void BeginOperation(int id) { operationId = id; CongestionMessages = 0; }
    public void EndOperation(int id) { if (operationId == id) operationId = 0; }

    public Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DelayHook is not null) return DelayHook(token);
        Now += delay;
        return Task.CompletedTask;
    }

    public Task<NativeTravelSnapshot> SnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot());
    }

    public Task<bool> ExecuteAsync(NativeTravelAction action, Func<NativeTravelSnapshot, bool> revalidate, CancellationToken token)
    {
        BeforeExecute?.Invoke();
        if (!revalidate(Snapshot(peek: true))) { Refused.Add(action); return Task.FromResult(false); }
        Executed.Add(action);
        Apply(action);
        return Task.FromResult(true);
    }

    public int Count(NativeTravelActionKind kind) => Executed.Count(a => a.Kind == kind);

    /// <summary>UI captures taken, with whether the World Visit window was still open at that moment.</summary>
    public readonly List<bool> UiCaptures = [];
    public bool UiCaptureThrows;

    public Task<string> DescribeUiAsync()
    {
        if (UiCaptureThrows) throw new InvalidOperationException("addon vanished");
        UiCaptures.Add(WindowOpen);
        return Task.FromResult($"SIMULATED UI: window {(WindowOpen ? "open" : "closed")}, selected DC {SelectedDc}, executed so far {Executed.Count}");
    }

    private void Apply(NativeTravelAction a)
    {
        switch (a.Kind)
        {
            case NativeTravelActionKind.TeleportToCity:
                teleportAt = Now;
                teleportCity = WorldVisitCities.All.First(c => c.AetheryteId == a.Id);
                break;
            case NativeTravelActionKind.InteractWithAetheryte:
                MenuOpen = true;
                break;
            case NativeTravelActionKind.SelectMenuEntry:
                MenuOpen = false;
                if (!WindowNeverOpens && AetheryteMenu[a.Index] == "Visit Another World Server.")
                {
                    WindowOpen = true;
                    sectionHiddenLeft = SectionHiddenSnapshots;
                    SelectedDc = Current.LogicalDataCenter;
                    rowsDc = SelectedDc;
                }
                break;
            case NativeTravelActionKind.SelectDataCenter:
                if (DropdownIgnoresSelection) break;
                // A real arrow press moves exactly one Data Center (live QA 2026-11-13): anything else is a defect.
                if (Math.Abs(a.Index - Array.IndexOf(DcOrder, SelectedDc)) != 1)
                    throw new InvalidOperationException($"non-adjacent Data Center step from {SelectedDc} to item {a.Index}");
                var from = Array.IndexOf(DcOrder, SelectedDc);
                SelectedDc = DcOrder[ArrowsReversed ? ((from - (a.Index - from)) % DcOrder.Length + DcOrder.Length) % DcOrder.Length : a.Index];
                staleSnapshotsLeft = StaleRowSnapshotsAfterDcChange;
                if (staleSnapshotsLeft == 0 && !StaleRowsForever) rowsDc = SelectedDc;
                break;
            case NativeTravelActionKind.SelectWorld:
                var rows = Rows();
                var index = Math.Clamp(a.Index + SelectWorldRowOffset, 0, rows.Count - 1);
                pendingDestination = rows[index].World;
                AgentDestination = pendingDestination?.Id ?? 0;
                ConfirmOpen = true;
                break;
            case NativeTravelActionKind.ConfirmTravel:
                ConfirmOpen = false;
                WindowOpen = false;
                confirmedAt = Now;
                CongestionMessages += CongestionMessagesAfterConfirm;
                break;
            case NativeTravelActionKind.CloseTravelUi:
                MenuOpen = WindowOpen = ConfirmOpen = false;
                break;
        }
    }

    private List<WorldRowObservation> Rows()
    {
        var dc = rowsDc ?? SelectedDc;
        return [.. Catalog.WorldsIn(dc).Where(w => !HiddenWorlds.Contains(w.Name))
            .Select(w => new WorldRowObservation(w.Id == Current.Id ? "\uE06A " + w.Name : w.Name, w, w.Id == Current.Id || DisabledWorlds.Contains(w.Name)))];
    }

    private NativeTravelSnapshot Snapshot(bool peek = false)
    {
        if (!peek) SnapshotCount++;
        AdvanceTimeline();

        if (!peek && WindowOpen && rowsDc != SelectedDc && !StaleRowsForever)
        {
            if (staleSnapshotsLeft > 0) staleSnapshotsLeft--;
            else rowsDc = SelectedDc;
        }

        var transitioning = InTransition(out var queued);
        WorldTravelWindowObservation? window = null;
        if (WindowOpen && sectionHiddenLeft > 0)
        {
            if (!peek) sectionHiddenLeft--;
            window = new WorldTravelWindowObservation(true, false, null, null);   // live: ready before any section is shown
        }
        else if (WindowOpen && LegacyWindow)
        {
            var legacyRows = Rows().Select(r => r with { Label = NativeTravelPolicy.CleanLabel(r.Label), World = Catalog.Resolve(r.Label) }).ToList();
            window = new WorldTravelWindowObservation(true, false, null, new WorldListObservation(legacyRows, -1, false, !ListEventUnverified));
        }
        else if (WindowOpen)
        {
            var labels = DcLabelsOverride ?? (DcLabelsCorrupt ? ["Aether", "Aether", "Light", "Primal"] : DcOrder.Select(d => d == SelectedDc ? "\u25B6 " + d : d).Select(NativeTravelPolicy.CleanLabel).ToArray());
            var selector = new DataCenterSelectorObservation(labels, [.. DcOrder.Select(d => DisabledDcs.Contains(d))], DropdownReportsNoSelection ? -1 : Array.IndexOf(DcOrder, SelectedDc), false, !DcEventUnverified);
            var rows = Rows().Select(r => r with { Label = NativeTravelPolicy.CleanLabel(r.Label), World = Catalog.Resolve(r.Label) }).ToList();
            window = new WorldTravelWindowObservation(true, true, selector, new WorldListObservation(rows, -1, false, !ListEventUnverified));
        }

        var prompt = PromptOverride ?? (pendingDestination is null ? null : $"Travel to {pendingDestination.Name}?");
        return new NativeTravelSnapshot(
            !LoggedOut, !LoggedOut && !transitioning, transitioning, queued, false, Mounted, InCombat,
            LoggedOut ? null : Current, Home, Territory, !transitioning && teleportAt is null,
            WorldVisitCities.ForTerritory(Territory) is not null || AetheryteDistance < 50 ? new AetheryteObservation(42, 9, AetheryteDistance, true) : null,
            MenuOpen ? new MenuObservation(AetheryteMenu) : null,
            window,
            ConfirmOpen && prompt is not null ? new ConfirmObservation(prompt, true) : null,
            ConfirmOpen ? AgentDestination : 0,
            CongestionMessages);
    }

    private bool InTransition(out bool queued)
    {
        queued = false;
        if (teleportAt is { } t)
        {
            if (Now - t < TimeSpan.FromSeconds(3)) return true;
            Territory = teleportCity!.TerritoryId;
            AetheryteDistance = 3f;
            teleportAt = null;
        }

        if (confirmedAt is { } c && !NeverDeparts)
        {
            var elapsed = Now - c;
            if (elapsed < TimeSpan.FromSeconds(5)) { queued = true; return false; }
            if (elapsed < TimeSpan.FromSeconds(10) || NeverArrives) return true;
            Current = ArriveAt ?? pendingDestination ?? Current;
            confirmedAt = null;
            pendingDestination = null;
        }

        return false;
    }

    private void AdvanceTimeline() { }
}
