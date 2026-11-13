using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.Travel;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace VenueOS.Plugin.Travel;

/// <summary>The unsafe, game-version-sensitive half of native travel (0.3.14). Every read and every action runs on the
/// Dalamud framework thread. Nothing here decides anything: <see cref="NativeWorldVisitDriver"/> decides, and this class
/// reads the game into a <see cref="NativeTravelSnapshot"/> and performs one revalidated action at a time.
///
/// <para><b>WorldTravelSelect layout (Patch 7.58, VERIFIED by the owner's live Inspect on 2026-11-13).</b> Node 2 is a
/// plain container (type 1, no component) for the regional section, visible when travel to other logical Data Centers
/// is offered. Inside it: node 7, the World list (<see cref="AtkComponentList"/>) for the selected Data Center (labels
/// and its window-owned click event verified live); node 4, the Data Center <see cref="AtkComponentDropDownList"/>;
/// nodes 5 and 6, the two arrow Buttons beside it. Node 20 (TreeList, hidden on 7.58) is the legacy single-Data-Center
/// list. Public sources (SimpleTweaks ImprovedWorldVisit, SRankSentinel NativeTravel.WorldVisit) treated node 2 as the
/// dropdown; that read was refused safely live and corrected here. The dropdown's item labels and its list's click
/// ownership remain to be verified live: the driver gates on both. Every
/// component is type-checked; World rows are identified by the World their label names, never by position alone; a
/// selection is dispatched only when the list's item-click event is registered to its expected owner
/// (<see cref="HasOwnedClick"/>); and the confirmation is gated by the driver on the exact prompt text and
/// <c>AgentWorldTravel.DestinationWorldId</c>. A wrong click therefore can stall a travel, but cannot confirm a wrong
/// destination. No raw callback index is ever used for the Data Center or the World row.</para></summary>
public sealed unsafe class DalamudNativeTravelPort : INativeTravelGamePort, IDisposable
{
    private const uint RegionalSectionNodeId = 2;     // container; visible ⇔ other Data Centers are offered
    private const uint DataCenterSelectorNodeId = 4;  // the DropDownList inside it (live Inspect, 2026-11-13)
    private const uint RegionalWorldListNodeId = 7;
    private const uint LegacyWorldListNodeId = 20;
    private const uint ConfirmTemplateRowA = 12519;   // "Travel to <World>?"
    private const uint ConfirmTemplateRowB = 12624;   // "Travel to <World>?<br>(party notice)"

    private static readonly string[] CongestionMarkers =
    [
        "This World is experiencing congestion. Character movement is limited at this time",
        "currently congested",
        "destination world is currently congested",
        "please wait until the world has become less congested",
        "world is currently full",
        "please wait until an opening is available and try again",
    ];

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly IChatGui chatGui;
    private readonly IDataManager data;
    private readonly ITravelWorldCatalog catalog;
    private readonly IPluginLog log;
    private readonly object congestionGate = new();
    private int congestionOperationId;
    private int congestionCount;
    private NativeTravelTexts? texts;
    private HashSet<string>? knownDataCenterNames;

    private HashSet<string> KnownDataCenterNames
    {
        get
        {
            if (knownDataCenterNames is { Count: > 0 }) return knownDataCenterNames;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint region = 1; region <= 4; region++) names.UnionWith(catalog.LogicalDataCentersInRegion(region));
            return knownDataCenterNames = names;
        }
    }

    public DalamudNativeTravelPort(IFramework framework, IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui, ITargetManager targetManager, IChatGui chatGui, IDataManager data, ITravelWorldCatalog catalog, IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.chatGui = chatGui;
        this.data = data;
        this.catalog = catalog;
        this.log = log;
        chatGui.ChatMessageUnhandled += OnChatMessageUnhandled;
    }

    public void Dispose() => chatGui.ChatMessageUnhandled -= OnChatMessageUnhandled;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    public NativeTravelTexts Texts => texts ??= LoadTexts();

    public void BeginOperation(int operationId)
    {
        lock (congestionGate) { congestionOperationId = operationId; congestionCount = 0; }
    }

    public void EndOperation(int operationId)
    {
        lock (congestionGate) if (congestionOperationId == operationId) { congestionOperationId = 0; congestionCount = 0; }
    }

    public Task<NativeTravelSnapshot> SnapshotAsync(CancellationToken token) => framework.RunOnFrameworkThread(ReadSnapshot);

    public Task<bool> ExecuteAsync(NativeTravelAction action, Func<NativeTravelSnapshot, bool> revalidate, CancellationToken token) =>
        framework.RunOnFrameworkThread(() =>
        {
            var fresh = ReadSnapshot();
            if (!revalidate(fresh)) return false;
            try { return Perform(action); }
            catch (Exception ex) { log.Warning(ex, $"Native travel: {action.Kind} failed."); return false; }
        });

    /// <summary>Read-only raw dump of the World Visit window, its dialogs and the agent, for live QA (the "Inspect"
    /// button). Reads only; never clicks.</summary>
    public Task<string> DescribeRawAsync() => framework.RunOnFrameworkThread(DescribeRaw);

    /// <inheritdoc />
    public Task<string> DescribeUiAsync() => DescribeRawAsync();

    /// <summary>The plain text of an encoded SeString (macros such as icons removed).</summary>
    private static string SeText(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? string.Empty : new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(bytes).ExtractText().Trim();

    // ----- reading -----

    private NativeTravelSnapshot ReadSnapshot()
    {
        var player = objectTable.LocalPlayer;
        TravelWorld? current = null, home = null;
        if (player is not null)
        {
            current = catalog.ResolveById(player.CurrentWorld.RowId);
            home = catalog.ResolveById(player.HomeWorld.RowId);
        }

        int congestion;
        lock (congestionGate) congestion = congestionCount;

        return new NativeTravelSnapshot(
            clientState.IsLoggedIn,
            player is not null,
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            condition[ConditionFlag.WaitingToVisitOtherWorld] || condition[ConditionFlag.ReadyingVisitOtherWorld],
            condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInCutSceneEvent]
                || condition[ConditionFlag.Occupied] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.Casting],
            condition[ConditionFlag.Mounted],
            condition[ConditionFlag.InCombat],
            current,
            home,
            clientState.TerritoryType,
            TeleportReady(),
            NearestAetheryte(),
            ReadMenu(),
            ReadWindow(),
            ReadConfirm(),
            AgentDestinationWorldId(),
            congestion);
    }

    private static bool TeleportReady()
    {
        var actions = ActionManager.Instance();
        return actions is not null && actions->GetActionStatus(ActionType.Action, 5, 0xE0000000, false, false, null) == 0;
    }

    private AetheryteObservation? NearestAetheryte()
    {
        var player = objectTable.LocalPlayer;
        if (player is null) return null;
        AetheryteObservation? best = null;
        foreach (var o in objectTable)
        {
            if (o is null || o.ObjectKind != ObjectKind.Aetheryte) continue;
            var dx = o.Position.X - player.Position.X;
            var dz = o.Position.Z - player.Position.Z;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (best is null || distance < best.Distance) best = new AetheryteObservation(o.GameObjectId, o.BaseId, distance, o.IsTargetable);
        }

        return best;
    }

    private MenuObservation? ReadMenu()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString");
        if (addon is null || !IsReady((AtkUnitBase*)addon)) return null;
        var menu = &addon->PopupMenu.PopupMenu;
        if (menu->EntryNames is null || menu->EntryCount <= 0 || menu->EntryCount > 32) return null;
        var entries = new string[menu->EntryCount];
        // Parsed as SeString: entry names carry macro payloads (icons), whose raw bytes otherwise leak into the text.
        for (var i = 0; i < entries.Length; i++) entries[i] = SeText(menu->EntryNames[i].AsSpan());
        return new MenuObservation(entries);
    }

    private ConfirmObservation? ReadConfirm()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->PromptText is null) return null;
        return new ConfirmObservation(addon->PromptText->NodeText.ToString(), addon->YesButton is not null && addon->YesButton->IsEnabled);
    }

    private static uint AgentDestinationWorldId()
    {
        var agent = AgentWorldTravel.Instance();
        return agent is null ? 0u : (uint)agent->DestinationWorldId;
    }

    private WorldTravelWindowObservation? ReadWindow()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (addon is null || !addon->IsVisible) return null;
        if (!addon->IsReady) return new WorldTravelWindowObservation(false, false, null, null);

        var section = addon->GetNodeById(RegionalSectionNodeId);
        var regional = section is not null && section->IsVisible();
        var selectorNode = addon->GetNodeById(DataCenterSelectorNodeId);
        DataCenterSelectorObservation? selector = null;
        if (regional && selectorNode is not null && selectorNode->IsVisible())
        {
            var dropdown = selectorNode->GetAsAtkComponentDropdownList();
            if (dropdown is not null && (AtkResNode*)dropdown->OwnerNode == selectorNode && dropdown->List is not null)
            {
                var list = dropdown->List;
                var count = list->GetItemCount();
                if (count is >= 0 and <= 16)
                {
                    var labels = new string[count];
                    var disabled = new bool[count];
                    for (var i = 0; i < count; i++) { labels[i] = ReadItemText(list, i); disabled[i] = list->GetItemDisabledState(i); }
                    selector = new DataCenterSelectorObservation(labels, disabled, dropdown->GetSelectedItemIndex(), dropdown->OpenStateChangePending, HasOwnedClick(list, (AtkEventListener*)dropdown));
                }
            }
        }

        var worldList = addon->GetComponentListById(regional ? RegionalWorldListNodeId : LegacyWorldListNodeId);
        WorldListObservation? worlds = null;
        if (worldList is not null && worldList->OwnerNode is not null && worldList->OwnerNode->IsVisible())
        {
            var count = worldList->GetItemCount();
            if (count is >= 0 and <= 32)
            {
                var rows = new WorldRowObservation[count];
                for (var i = 0; i < count; i++)
                {
                    var label = ReadItemText(worldList, i);
                    rows[i] = new WorldRowObservation(label, catalog.Resolve(label), worldList->GetItemDisabledState(i));
                }

                worlds = new WorldListObservation(rows, worldList->SelectedItemIndex, worldList->IsUpdatePending, HasOwnedClick(worldList, (AtkEventListener*)addon));
            }
        }

        return new WorldTravelWindowObservation(true, regional, selector, worlds);
    }

    /// <summary>A list item's label, or, when the list renders its text through a row renderer instead, the single
    /// visible text node that names exactly one World (several different World names → empty, i.e. unresolved).</summary>
    private string ReadItemText(AtkComponentList* list, int index)
    {
        var label = list->GetItemLabel(index);
        if (label.HasValue)
        {
            var text = NativeTravelPolicy.CleanLabel(label.ToString());
            if (text.Length > 0) return text;
        }

        var renderer = list->GetItemRenderer(index);
        if (renderer is null || renderer->UldManager.NodeList is null || renderer->UldManager.NodeListCount > 128) return string.Empty;
        string? found = null;
        for (var i = 0; i < renderer->UldManager.NodeListCount; i++)
        {
            var node = renderer->UldManager.NodeList[i];
            if (node is null || !node->IsVisible()) continue;
            var textNode = node->GetAsAtkTextNode();
            if (textNode is null) continue;
            var text = NativeTravelPolicy.CleanLabel(textNode->NodeText.ToString());
            if (text.Length == 0) continue;
            if (catalog.Resolve(text) is null && !KnownDataCenterNames.Contains(text)) continue;
            if (found is not null && !string.Equals(found, text, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            found = text;
        }

        return found ?? string.Empty;
    }

    /// <summary>True when exactly one ListItemClick event is registered on the list's owner node, and its listener is
    /// <paramref name="listener"/>: the component the game itself expects to receive the click.</summary>
    private static bool HasOwnedClick(AtkComponentList* list, AtkEventListener* listener)
    {
        if (list is null || list->OwnerNode is null) return false;
        var entry = list->OwnerNode->AtkEventManager.Event;
        var matches = 0;
        for (var i = 0; entry is not null && i < 32; i++, entry = entry->NextEvent)
        {
            if (entry->State.EventType != AtkEventType.ListItemClick) continue;
            if (entry->Listener != listener || ++matches > 1) return false;
        }

        return entry is null && matches == 1;
    }

    private static bool IsReady(AtkUnitBase* addon) => addon is not null && addon->IsReady && addon->IsVisible;

    // ----- acting (framework thread, after revalidation) -----

    private bool Perform(NativeTravelAction action)
    {
        switch (action.Kind)
        {
            case NativeTravelActionKind.TeleportToCity:
            {
                var telepo = Telepo.Instance();
                if (telepo is null) return false;
                telepo->UpdateAetheryteList();
                var known = false;
                foreach (var entry in telepo->TeleportList) if (entry.AetheryteId == action.Id && entry.SubIndex == 0) { known = true; break; }
                return known && telepo->Teleport(action.Id, 0);
            }
            case NativeTravelActionKind.InteractWithAetheryte:
            {
                var target = objectTable.FirstOrDefault(o => o is not null && o.GameObjectId == action.ObjectId && o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable);
                var system = TargetSystem.Instance();
                if (target is null || system is null) return false;
                targetManager.Target = target;
                system->InteractWithObject((CSGameObject*)target.Address, false);
                return true;
            }
            case NativeTravelActionKind.SelectMenuEntry:
            {
                var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString");
                if (addon is null || !IsReady((AtkUnitBase*)addon)) return false;
                FireCallback((AtkUnitBase*)addon, action.Index);
                return true;
            }
            case NativeTravelActionKind.SelectDataCenter:
            {
                var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
                if (!IsReady(addon)) return false;
                var section = addon->GetNodeById(RegionalSectionNodeId);
                var node = addon->GetNodeById(DataCenterSelectorNodeId);
                if (section is null || !section->IsVisible() || node is null || !node->IsVisible()) return false;
                var dropdown = node->GetAsAtkComponentDropdownList();
                if (dropdown is null || dropdown->List is null || (AtkResNode*)dropdown->OwnerNode != node) return false;
                var list = dropdown->List;
                if (!string.Equals(ReadItemText(list, action.Index), action.Label, StringComparison.OrdinalIgnoreCase) || !HasOwnedClick(list, (AtkEventListener*)dropdown)) return false;
                dropdown->SelectItem(action.Index);
                if (dropdown->GetSelectedItemIndex() != action.Index) return false;
                list->DispatchItemEvent(action.Index, AtkEventType.ListItemClick);
                return true;
            }
            case NativeTravelActionKind.SelectWorld:
            {
                var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
                if (!IsReady(addon)) return false;
                var section = addon->GetNodeById(RegionalSectionNodeId);
                var regional = section is not null && section->IsVisible();
                var list = addon->GetComponentListById(regional ? RegionalWorldListNodeId : LegacyWorldListNodeId);
                if (list is null || catalog.Resolve(ReadItemText(list, action.Index))?.Id != action.Id || !HasOwnedClick(list, (AtkEventListener*)addon)) return false;
                list->SelectItem(action.Index, false);
                if (list->SelectedItemIndex != action.Index) return false;
                list->DispatchItemEvent(action.Index, AtkEventType.ListItemClick);
                return true;
            }
            case NativeTravelActionKind.ConfirmTravel:
            {
                var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
                if (addon is null || !IsReady((AtkUnitBase*)addon)) return false;
                FireCallback((AtkUnitBase*)addon, 0);   // Yes
                return true;
            }
            case NativeTravelActionKind.CloseTravelUi:
                CloseIfOpen("SelectYesno");
                CloseIfOpen("WorldTravelSelect");
                CloseIfOpen("SelectString");
                return true;
            default:
                return true;
        }
    }

    private void CloseIfOpen(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        if (addon is not null && addon->IsVisible) addon->Close(true);
    }

    private static void FireCallback(AtkUnitBase* addon, params int[] arguments)
    {
        var values = stackalloc AtkValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) { values[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int; values[i].Int = arguments[i]; }
        addon->FireCallback((uint)arguments.Length, values, true);
    }

    // ----- texts and congestion -----

    private NativeTravelTexts LoadTexts()
    {
        var option = NativeTravelPolicy.EnglishWorldVisitOption;
        var templates = new List<(string, string)>();
        try
        {
            var sheet = data.Excel.GetSheet<RawRow>(null, "transport/Aetheryte");
            if (sheet.TryGetRow(3, out var row))
            {
                string? last = null;
                for (var c = 0; c < sheet.Columns.Count; c++)
                    if (sheet.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String) last = row.ReadStringColumn(c).ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(last)) option = last;
            }

            var addonSheet = data.GetExcelSheet<Addon>();
            foreach (var id in new[] { ConfirmTemplateRowA, ConfirmTemplateRowB })
                if (addonSheet.TryGetRow(id, out var addonRow) && SplitTemplate(addonRow.Text) is { } template) templates.Add(template);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Native travel: could not read World Visit texts from game data; using English.");
        }

        if (templates.Count == 0) templates.AddRange(NativeTravelPolicy.EnglishConfirmationTemplates);
        log.Information($"Native travel texts: option \"{option}\"; confirmation templates {string.Join(" / ", templates.Select(t => $"\"{t.Item1}<World>{t.Item2}\""))}.");
        return new NativeTravelTexts(option, templates);
    }

    /// <summary>Literal text before the first macro (the World placeholder) and between it and the next macro.</summary>
    private static (string, string)? SplitTemplate(Lumina.Text.ReadOnly.ReadOnlySeString text)
    {
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var stage = 0;
        foreach (var payload in text)
        {
            var isText = payload.Type == Lumina.Text.ReadOnly.ReadOnlySePayloadType.Text;
            if (stage == 0) { if (isText) prefix.Append(Encoding.UTF8.GetString(payload.Body.Span)); else stage = 1; }
            else if (stage == 1) { if (isText) suffix.Append(Encoding.UTF8.GetString(payload.Body.Span)); else break; }
        }

        return stage == 1 && prefix.Length > 0 ? (prefix.ToString(), suffix.ToString()) : null;
    }

    private void OnChatMessageUnhandled(IChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text) || !CongestionMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return;
        lock (congestionGate)
        {
            if (congestionOperationId == 0) return;
            congestionCount++;
            log.Information($"Native travel #{congestionOperationId}: congestion message {congestionCount}.");
        }
    }

    // ----- raw inspection (read-only) -----

    private string DescribeRaw()
    {
        var sb = new StringBuilder();
        var player = objectTable.LocalPlayer;
        sb.AppendLine($"Territory {clientState.TerritoryType}; current World id {player?.CurrentWorld.RowId ?? 0} → {NativeWorldVisitDriver.Describe(player is null ? null : catalog.ResolveById(player.CurrentWorld.RowId))}; home {NativeWorldVisitDriver.Describe(player is null ? null : catalog.ResolveById(player.HomeWorld.RowId))}");
        sb.AppendLine($"Conditions: BetweenAreas={condition[ConditionFlag.BetweenAreas]} WaitingToVisitOtherWorld={condition[ConditionFlag.WaitingToVisitOtherWorld]} ReadyingVisitOtherWorld={condition[ConditionFlag.ReadyingVisitOtherWorld]} OccupiedInEvent={condition[ConditionFlag.OccupiedInEvent]} Mounted={condition[ConditionFlag.Mounted]} TeleportReady={TeleportReady()}");
        var a = NearestAetheryte();
        sb.AppendLine(a is null ? "Nearest aetheryte: none" : $"Nearest aetheryte: base id {a.BaseId}, {a.Distance:0.0}y, targetable {a.Targetable}");
        var agent = AgentWorldTravel.Instance();
        if (agent is not null) sb.AppendLine($"AgentWorldTravel: DestinationWorldId={agent->DestinationWorldId} DestinationWorldName=\"{agent->DestinationWorldName}\" CurrentWorldName=\"{agent->CurrentWorldName}\"");
        var menu = ReadMenu();
        sb.AppendLine(menu is null ? "SelectString: closed" : $"SelectString: {string.Join(" | ", menu.Entries.Select((e, i) => $"[{i}] {e}"))}");
        var confirm = ReadConfirm();
        sb.AppendLine(confirm is null ? "SelectYesno: closed" : $"SelectYesno: \"{confirm.Prompt.Replace("\n", "⏎")}\" yesEnabled={confirm.YesEnabled}");

        var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (addon is null) { sb.AppendLine("WorldTravelSelect: closed"); return sb.ToString(); }
        sb.AppendLine($"WorldTravelSelect: ready {addon->IsReady} visible {addon->IsVisible}; nodes {addon->UldManager.NodeListCount}; AtkValues {addon->AtkValuesCount}");
        sb.AppendLine("  Node tree (path = parent component node ids; text = visible text; events = registered event types → listener):");
        var budget = 400;
        DumpNodes(sb, &addon->UldManager, addon, null, "", 0, ref budget);
        if (budget <= 0) sb.AppendLine("  … node dump truncated");
        DiagnoseSelectorNode(sb, addon);

        var window = ReadWindow();
        if (window?.DataCenters is { } dcs)
            sb.AppendLine($"  DC selector (node {DataCenterSelectorNodeId}): selected {dcs.SelectedIndex}, pending {dcs.Pending}, click event owned {dcs.EventVerified}; items {string.Join(" | ", dcs.Labels.Select((l, i) => $"[{i}] {l}{(dcs.Disabled[i] ? " (disabled)" : string.Empty)}"))}");
        else sb.AppendLine($"  DC selector (node {DataCenterSelectorNodeId}): {(window?.RegionalSelectorVisible == true ? "visible but not readable as a dropdown" : "not visible")}");
        if (window?.Worlds is { } wl)
            sb.AppendLine($"  World list (node {(window.RegionalSelectorVisible ? RegionalWorldListNodeId : LegacyWorldListNodeId)}): selected {wl.SelectedIndex}, pending {wl.Pending}, click event owned {wl.EventVerified}; rows {string.Join(" | ", wl.Rows.Select((r, i) => $"[{i}] \"{r.Label}\" → {(r.World is null ? "?" : $"{r.World.Name}/{r.World.LogicalDataCenter}")}{(r.Disabled ? " (disabled)" : string.Empty)}"))}");
        else sb.AppendLine("  World list: not readable");
        for (var i = 0; i < addon->AtkValuesCount && i < 160; i++)
        {
            var v = addon->AtkValues[i];
            var shown = v.Type switch
            {
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => v.Int.ToString(),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => v.UInt.ToString(),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool => v.Byte != 0 ? "true" : "false",
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString => v.String.Value is null ? "(null)" : $"\"{v.String}\"",
                _ => "…",
            };
            sb.AppendLine($"  AtkValue[{i}] {v.Type} {shown}");
        }

        return sb.ToString();
    }

    /// <summary>Read-only recursive dump of a node list and the node lists of its components (depth-limited).</summary>
    private static void DumpNodes(StringBuilder sb, AtkUldManager* uld, AtkUnitBase* addon, AtkComponentBase* owner, string path, int depth, ref int budget)
    {
        if (uld is null || uld->NodeList is null || uld->NodeListCount > 512) return;
        for (var i = 0; i < uld->NodeListCount && budget > 0; i++)
        {
            var node = uld->NodeList[i];
            if (node is null) continue;
            budget--;
            sb.Append($"  {new string(' ', depth * 2)}{path}{node->NodeId} type {(int)node->Type} {(node->IsVisible() ? "visible" : "hidden")}");
            var text = node->GetAsAtkTextNode();
            if (text is not null)
            {
                var value = SeText(text->NodeText.AsSpan());
                if (value.Length > 0) sb.Append($" text \"{(value.Length > 60 ? value[..60] + "…" : value).Replace("\n", "⏎").Replace("\r", string.Empty)}\"");
            }

            var component = node->GetComponent();
            if (component is not null)
            {
                var type = component->GetComponentType();
                sb.Append($" component {type}");
                if (type is ComponentType.List or ComponentType.TreeList)
                {
                    var list = (AtkComponentList*)component;
                    sb.Append($" items {list->GetItemCount()} selected {list->SelectedItemIndex}");
                }
                else if (type == ComponentType.DropDownList)
                {
                    var dd = (AtkComponentDropDownList*)component;
                    sb.Append($" selected {dd->GetSelectedItemIndex()} list {(dd->List is null ? "null" : $"items {dd->List->GetItemCount()}")} ownerMatches {(AtkResNode*)dd->OwnerNode == node}");
                }
            }

            AppendEvents(sb, node, addon, owner, component);
            sb.AppendLine();
            if (component is not null && depth < 3)
                DumpNodes(sb, &component->UldManager, addon, component, $"{path}{node->NodeId}/", depth + 1, ref budget);
        }
    }

    private static void AppendEvents(StringBuilder sb, AtkResNode* node, AtkUnitBase* addon, AtkComponentBase* owner, AtkComponentBase* self)
    {
        var entry = node->AtkEventManager.Event;
        if (entry is null) return;
        var parts = new List<string>();
        for (var i = 0; entry is not null && i < 16; i++, entry = entry->NextEvent)
        {
            var listener = entry->Listener;
            var who = listener == (AtkEventListener*)addon ? "window"
                : self is not null && listener == (AtkEventListener*)self ? "this component"
                : owner is not null && listener == (AtkEventListener*)owner ? "parent component"
                : "other";
            parts.Add($"{entry->State.EventType}(param {entry->Param})→{who}");
        }

        sb.Append($" events [{string.Join(", ", parts)}]");
    }

    /// <summary>Why node <see cref="DataCenterSelectorNodeId"/> is or isn't readable as a Data Center dropdown.</summary>
    private static void DiagnoseSelectorNode(StringBuilder sb, AtkUnitBase* addon)
    {
        var section = addon->GetNodeById(RegionalSectionNodeId);
        sb.AppendLine($"  Node {RegionalSectionNodeId} (regional section): {(section is null ? "no such node" : section->IsVisible() ? "visible" : "hidden")}");
        var node = addon->GetNodeById(DataCenterSelectorNodeId);
        if (node is null) { sb.AppendLine($"  Node {DataCenterSelectorNodeId} diagnosis: no such node"); return; }
        var component = node->GetComponent();
        var type = component is null ? "none (not a component node)" : component->GetComponentType().ToString();
        var dropdown = node->GetAsAtkComponentDropdownList();
        var reason = !node->IsVisible() ? "hidden"
            : component is null ? "not a component"
            : dropdown is null ? $"its component is {type}, not DropDownList"
            : (AtkResNode*)dropdown->OwnerNode != node ? "the dropdown's owner node is a different node"
            : dropdown->List is null ? "the dropdown has no list"
            : "readable";
        sb.AppendLine($"  Node {DataCenterSelectorNodeId} diagnosis: type {(int)node->Type}, {(node->IsVisible() ? "visible" : "hidden")}, component {type} → {reason}");
    }
}
