using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.Travel;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin.Travel;

/// <summary>Native Travel QA (0.3.14): a diagnostic tool for the owner's live tests, shown collapsed at the bottom of
/// Settings → Modules → ShoutRunner. It drives <see cref="NativeTravelService"/> directly, so it:
/// <list type="bullet">
/// <item>never sends <c>/shout</c> or <c>/yell</c> (it has no chat dependency at all), never runs a route, and never
/// reads or writes ShoutRunner's settings or recovery checkpoint;</item>
/// <item>refuses to start while ShoutRunner is running or while any travel operation owns the service;</item>
/// <item>offers a read-only <b>Inspect</b> that dumps the World Visit window, dialogs and agent without clicking
/// anything;</item>
/// <item>owns the session-only switch for the not-yet-verified Data Center dropdown interaction.</item>
/// </list>
/// Its operation is cancelled on plugin unload by <c>Plugin.Dispose</c>.</summary>
public sealed class NativeTravelQaPanel(NativeTravelService travel, DalamudNativeTravelPort port, DalamudTravelWorldCatalog catalog, Func<bool> shoutRunnerActive, VenueProfileService venues)
{
    private int dataCenterIndex;
    private int worldIndex;
    private int cityIndex;
    private string rawInspection = string.Empty;
    private Task<string>? inspectTask;
    private Task<NativeTravelSnapshot>? liveTask;
    private NativeTravelSnapshot? live;
    private DateTime nextLiveReadUtc;
    private string message = string.Empty;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Native Travel QA (diagnostic)##venueos-native-travel-qa")) return;

        UiKit.InfoBanner(theme, "Testing tool", "Tests VenueOS's own World Visit and Data Center travel without any announcement. It never sends /shout or /yell, never runs a route, and never touches ShoutRunner's progress. Travel happens only when you press Start Travel Test.");
        RefreshLive();
        DrawLocation(theme);
        DrawStatus(theme);
        ImGui.Separator();
        DrawDestinationPicker(theme);
        ImGui.Separator();
        DrawDiagnostics(theme);
    }

    private void RefreshLive()
    {
        if (liveTask is { IsCompleted: true })
        {
            if (liveTask.IsCompletedSuccessfully) live = liveTask.Result;
            liveTask = null;
        }

        if (liveTask is null && DateTime.UtcNow >= nextLiveReadUtc)
        {
            nextLiveReadUtc = DateTime.UtcNow.AddSeconds(1);
            liveTask = travel.InspectAsync(CancellationToken.None);
        }
    }

    private void DrawLocation(VenueTheme theme)
    {
        var current = live?.CurrentWorld;
        ImGui.TextUnformatted($"Current World: {current?.Name ?? "(unknown)"}   Data Center: {current?.LogicalDataCenter ?? "(unknown)"}   Home: {live?.HomeWorld?.Name ?? "(unknown)"} [{live?.HomeWorld?.LogicalDataCenter ?? "?"}]");
        var city = live is null ? null : WorldVisitCities.ForTerritory(live.TerritoryId);
        ImGui.TextUnformatted($"Location: {(city?.Name ?? $"territory {live?.TerritoryId ?? 0} (not a World Visit city)")}   Nearest aetheryte: {(live?.NearestAetheryte is { } a ? $"{a.Distance:0.0}y (base id {a.BaseId})" : "none")}");
        ImGui.TextUnformatted($"World Visit window: {(live?.Window is { } w ? $"open, Data Center selector {(w.RegionalSelectorVisible ? "visible" : "hidden")}" : "closed")}   Confirmation: {(live?.Confirm is { } c ? $"\"{c.Prompt.Replace('\n', ' ')}\"" : "none")}");
    }

    private void DrawStatus(VenueTheme theme)
    {
        var status = travel.Status;
        ImGui.TextUnformatted(status.IsActive
            ? $"Travel operation #{status.OperationId} ({status.Owner} → {status.TargetWorld}): {status.Phase}"
            : "Travel: idle");
        if (status.IsActive) ImGui.TextWrapped(status.Detail);
        if (travel.LastResult is { } last) ImGui.TextWrapped($"Last result: #{last.OperationId} {last.Outcome}: {last.Detail}");
        if (message.Length > 0) UiKit.WarningState(theme, message);
    }

    private void DrawDestinationPicker(VenueTheme theme)
    {
        var region = live?.CurrentWorld?.RegionId ?? 2;
        var worlds = catalog.All.Where(w => w.RegionId == region).ToList();
        var dataCenters = worlds.Select(w => w.LogicalDataCenter).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (dataCenters.Count == 0) { UiKit.WarningState(theme, "The game's World list is not available yet."); return; }

        dataCenterIndex = Math.Clamp(dataCenterIndex, 0, dataCenters.Count - 1);
        Forms.ComboField(theme, "Destination Data Center", dataCenters, ref dataCenterIndex);
        var dcWorlds = worlds.Where(w => string.Equals(w.LogicalDataCenter, dataCenters[dataCenterIndex], StringComparison.OrdinalIgnoreCase)).Select(w => w.Name).ToList();
        worldIndex = Math.Clamp(worldIndex, 0, Math.Max(0, dcWorlds.Count - 1));
        Forms.ComboField(theme, "Destination World", dcWorlds, ref worldIndex);
        var cities = WorldVisitCities.All.Select(c => c.Name).ToList();
        cityIndex = Math.Clamp(cityIndex, 0, cities.Count - 1);
        Forms.ComboField(theme, "World Visit city (if a teleport is needed)", cities, ref cityIndex);

        var allow = travel.AllowDataCenterSelection;
        if (UiKit.Toggle(theme, "Allow selecting another Data Center in the World Visit window (QA, this session only)", ref allow)) travel.AllowDataCenterSelection = allow;
        ImGui.TextWrapped("The Data Center dropdown interaction is based on public 7.58 sources and has not been verified on your client. With this off, a World on another Data Center stops before anything is clicked.");

        var blocked = shoutRunnerActive() ? "ShoutRunner is running — stop it first." : travel.IsBusy ? "A travel operation is already running." : null;
        if (blocked is not null) ImGui.TextDisabled(blocked);
        if (UiKit.PrimaryButton(theme, "Start Travel Test") && dcWorlds.Count > 0)
        {
            if (blocked is not null) { message = blocked; }
            else
            {
                message = string.Empty;
                var request = new NativeTravelRequest(dcWorlds[worldIndex], NativeTravelOwner.QaTest, [WorldVisitCities.All[cityIndex]]);
                _ = travel.TravelAsync(request, CancellationToken.None);
            }
        }

        ImGui.SameLine();
        if (UiKit.DangerButton(theme, "Cancel Travel Test"))
        {
            if (travel.Status.Owner == NativeTravelOwner.QaTest) travel.CancelActive("cancelled from Native Travel QA");
            else message = travel.IsBusy ? "The running travel belongs to ShoutRunner; use ShoutRunner's Stop." : "No travel test is running.";
        }
    }

    private void DrawDiagnostics(VenueTheme theme)
    {
        if (inspectTask is { IsCompleted: true })
        {
            rawInspection = inspectTask.IsCompletedSuccessfully ? inspectTask.Result : $"Inspect failed: {inspectTask.Exception?.GetBaseException().Message}";
            inspectTask = null;
        }

        if (UiKit.GhostButton(theme, "Inspect (read-only)") && inspectTask is null) inspectTask = port.DescribeRawAsync();
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Copy Diagnostics")) ImGui.SetClipboardText(BuildDiagnostics());
        ImGui.TextDisabled("Inspect reads the World Visit window, dialogs and game state without clicking anything. Open the World Visit window by hand first to capture it.");

        if (ImGui.BeginChild("##native-travel-qa-log", new System.Numerics.Vector2(0, 260), true))
        {
            if (rawInspection.Length > 0) { ImGui.TextUnformatted("— Inspect —"); ImGui.TextUnformatted(rawInspection); }
            ImGui.TextUnformatted("— Travel log —");
            foreach (var line in travel.RecentLog) ImGui.TextWrapped(line);
            if (travel.LastResult is { } last) { ImGui.TextUnformatted("— Last result trace —"); ImGui.TextUnformatted(last.Diagnostic); }
        }

        ImGui.EndChild();
    }

    private string BuildDiagnostics()
    {
        var status = travel.Status;
        return string.Join(Environment.NewLine, new[]
        {
            $"VenueOS Native Travel QA — {DateTime.UtcNow:u}",
            $"Data Center selection allowed: {travel.AllowDataCenterSelection}",
            $"Status: #{status.OperationId} {status.Owner} → {status.TargetWorld} {status.Phase}: {status.Detail}",
            "— Inspect —", rawInspection,
            "— Travel log —", string.Join(Environment.NewLine, travel.RecentLog),
            "— Last result —", travel.LastResult?.Diagnostic ?? "(none)",
        });
    }
}
