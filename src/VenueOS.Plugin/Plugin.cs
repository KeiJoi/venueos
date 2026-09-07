using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.IoC;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Bindings.ImGui;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.UI;
using VenueOS.Venues;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Modules.Operations.Raffle;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Modules.Operations.Tournament;
using VenueOS.Modules.Operations.Bingo;
using VenueOS.Modules.Operations.ShoutRunner;
using VenueOS.Plugin.Shell;

namespace VenueOS.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "VenueOS";
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static Dalamud.Plugin.Services.ICondition Condition { get; set; } = null!;
    private const string CommandName = "/venueos";
    private readonly ModuleHost modules = new(); private readonly VenueProfileService venues; private readonly VenueShell shell; private readonly NotificationService notifications; private readonly SchedulerService scheduler;
    private readonly PresenceService presence; private readonly ChatCommandService chat; private readonly AttendanceModule attendance; private readonly VenueOperationsDashboard dashboard; private readonly DiagnosticsService diagnostics; private readonly SettingsScreen settingsScreen; private readonly ModuleWindowManager windowManager; private readonly GlobalSettingsService globalSettings; private readonly SqliteVenueDatabase venueDatabase; private readonly VenueOS.Plugin.PartyFinder.PartyFinderAutomationService partyFinderAutomation; private readonly VenueOS.Plugin.ShoutRunner.ShoutRunnerAutomationService shoutRunnerAutomation; private readonly VenueOS.Plugin.Bingo.BingoPayoutAutomationService bingoPayoutAutomation; private readonly Shell.VenueSwitchCoordinator switchCoordinator;
    // Live-QA correction (detached-window lifecycle): drawn directly from this class's own Draw() below, gated
    // only on bingoModule.IsEnabled — NOT nested inside VenueBingoOperatorPanel.Draw() any more, so they survive
    // the main Bingo window/tablet being closed or navigated away from. See VenueBingoOperatorPanel's doc comment.
    private readonly VenueOS.Plugin.Bingo.BingoCalledNumbersWindow bingoCalledNumbersWindow;
    private readonly VenueOS.Plugin.Bingo.BingoPlayerCardViewerWindow bingoPlayerCardViewerWindow;
    private readonly VenueOS.Plugin.Bingo.BingoCallAlertWindow bingoCallAlertWindow;
    private readonly VenueOS.Modules.Operations.Bingo.VenueBingoModule bingoModule;
    // The built-in offline manual — reads the bundled USER_MANUAL.md copy (see UserManualLoader's doc comment for
    // why that file, not this class, is the single source of truth) once at construction; a missing/unreadable file
    // renders a plain warning inside the screen itself rather than failing plugin construction.
    private readonly Shell.UserManualScreen manualScreen;
    // Live-verified correction: this used to default to true, so the very first UiBuilder.Draw call after every
    // plugin load/enable rendered the main shell immediately — there is no persisted "was it open" state anywhere
    // (this field is never read from or written to Dalamud's plugin config), so this default was the entire root
    // cause. VenueOS must load quietly; it only opens from an explicit action (/venueos, a detached window's
    // Settings gear via RequestOpenAndFocusTablet, or a future Dalamud Open Main UI hookup) — never merely because
    // the plugin finished constructing.
    private bool open; private bool focusRequested;

    public Plugin()
    {
        var clock = new SystemClock(); notifications = new NotificationService(clock); scheduler = new SchedulerService(clock); chat = new ChatCommandService(clock, new InlineFrameworkDispatcher(), ExecuteChatCommand); presence = new PresenceService(clock, new DalamudObjectSnapshotProvider(ObjectTable, ClientState)); var targetedPlayerProvider = new DalamudTargetedPlayerProvider(TargetManager);
        // Loaded exactly once and shared by both stores below — see DalamudVenueStore's doc comment for why two
        // independently-loaded copies of this same config object risked silently discarding saved venue/module data.
        var pluginConfig = PluginInterface.GetPluginConfig() as VenueOsPluginConfiguration ?? new VenueOsPluginConfiguration();
        venues = new VenueProfileService(new DalamudVenueStore(PluginInterface, pluginConfig), modules); _ = new VenueUi(venues, notifications); shell = new VenueShell(modules, venues); globalSettings = new GlobalSettingsService(new DalamudGlobalSettingsStore(PluginInterface, pluginConfig));
        venueDatabase = new SqliteVenueDatabase($"Data Source={System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "attendance.db")}");
        var addressProvider = new DalamudVenueAddressProvider(ClientState, PlayerState, DataManager, Log);
        var exportService = new AttendanceExportService(venueDatabase);
        // ECommons is a shared, process-wide framework: initialized once here, before anything that depends on it
        // (promotion.partyfinder's automation engine, via ECommons' TaskManager/NeoTaskManager) is constructed, and
        // disposed once in Dispose(). Never initialized/disposed per-venue or per-module.
        ECommonsMain.Init(PluginInterface, this);
        // The About panel (Settings -> General) reads this back via DiagnosticsService.Capture().VenueOsVersion — it
        // used to show a hard-coded "Phase 4" label that silently stopped matching the real package version. Reading
        // it from this assembly's own AssemblyVersion means it can never drift from what was actually built/shipped.
        var venueOsVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        diagnostics = new DiagnosticsService(modules, venues, clock, venueOsVersion);
        // USER_MANUAL.md is copied next to VenueOS.dll (see VenueOS.Plugin.csproj's Include+Link content item and
        // scripts/Package-Release.ps1), so it lives wherever that DLL actually is on disk. Live-verified bug:
        // typeof(Plugin).Assembly.Location is NOT reliable for a Dalamud-installed plugin — Dalamud does not
        // necessarily load the plugin assembly the way a normal LoadFrom(path) would, so that property can be
        // empty or point somewhere other than the real installed plugin folder. IDalamudPluginInterface.
        // AssemblyLocation is Dalamud's own officially-documented answer to exactly this problem (it's backed by
        // the DllFile path Dalamud itself tracked when it loaded the plugin), so it's tried first; the reflection-
        // based path is kept only as a last-resort fallback for a context where AssemblyLocation is somehow unset.
        // Both are single, specific, justified directories — never a filesystem search.
        var candidateDirectories = new List<string?> { PluginInterface.AssemblyLocation.DirectoryName };
        var reflectionDirectory = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (!string.Equals(reflectionDirectory, candidateDirectories[0], StringComparison.OrdinalIgnoreCase)) candidateDirectories.Add(reflectionDirectory);
        manualScreen = new Shell.UserManualScreen(() => VenueOS.Services.UserManualLoader.Load(candidateDirectories), diagnostics);
        // Attendance is the single authoritative source of greeted state (see AttendanceService.IsGreeted/MarkGreeted's
        // doc comments) — Greeter only queries it and reports completions back to it, never deciding or storing the
        // boolean itself. AttendanceService already takes an optional GreeterService reference (constructed second,
        // below), so the reverse direction here is wired through a captured variable assigned right after
        // AttendanceService is constructed, rather than a genuine circular constructor dependency.
        AttendanceService? attendanceServiceRef = null;
        var greeterService = new GreeterService(clock, chat, venueDatabase, guest => presence.IsPresent(guest), msg => Log.Debug($"[Greeter] {msg}"),
            isGreeted: guest => attendanceServiceRef?.IsGreeted(guest) ?? false,
            onGreetingCompleted: guest => attendanceServiceRef?.MarkGreeted(guest, true, DateTimeOffset.UtcNow));
        var attendanceService = new AttendanceService(presence, venueDatabase, clock, () => ClientState.TerritoryType, () => ObjectTable.LocalPlayer?.Position, greeterService);
        attendanceServiceRef = attendanceService;
        var vipService = new VipOrchestrationService();
        // The single greeting orchestration entry point (VIP recognition → Greeter handoff → optional VIP public
        // announcement) — used by the ONE automatic-arrival trigger below (AttendanceService.AutomaticGreetingEligible,
        // for VIP and non-VIP guests alike) and Attendance's manual "Greet" action. See GreetingCoordinator's
        // and VipOrchestrationService's type-level doc comments for exactly what this replaces and why.
        var coordinator = new GreetingCoordinator(guest => attendanceService.IsGreeted(guest), greeterService, vipService, chat, msg => Log.Debug($"[Greeting] {msg}"));
        shoutRunnerAutomation = new VenueOS.Plugin.ShoutRunner.ShoutRunnerAutomationService(PluginInterface, ClientState, ObjectTable, Condition, ChatGui, DataManager, Framework, chat, Log);
        // Crash-recovery journal: a single small JSON file directly in the plugin's own Dalamud config directory,
        // matching attendance.db's own top-level placement — see FileShoutRunnerRecoveryStore's doc comment for the
        // atomic-write guarantee (same temp-file-then-move pattern already proven by FileQuestionSetRepository).
        var shoutRunnerRecoveryStore = new FileShoutRunnerRecoveryStore(PluginInterface.ConfigDirectory.FullName);
        var shoutRunnerService = new ShoutRunnerService(shoutRunnerAutomation, chat, venues, diagnostics, clock, shoutRunnerRecoveryStore);
        var raffleService = new VenueRaffleService(new VenueRaffleClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }), venues);
        // The canonical question library is VenueOS-wide (not per-venue) and independent of either Trivia's or
        // Editor's enabled state — constructed once here, exactly like venueDatabase above.
        var questionLibrary = new FileQuestionSetRepository(System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "VenueOS", "question-sets"));
        var triviaService = new MairsTriviaService(new MairsTriviaClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }), venues, questionLibrary, diagnostics);
        var editorService = new MairsEditorService(questionLibrary, id => triviaService.ActiveSourceSetIds.Contains(id));
        var tournamentService = new TournamentControlService(new TournamentControlClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }), venues, new TournamentCalloutService(scheduler, chat));
        // Bingo: VenueOS-owned client + service (Modules.Operations/Bingo, per NEW_MODULE_GUIDE.md §21/§33) and the
        // unsafe in-game payout-trade automation engine (VenueOS.Plugin.Bingo.BingoPayoutAutomationService),
        // mirroring exactly how Party Finder's automation is wired above — see that engine's own doc comment for
        // the full list of LIVE VERIFICATION REQUIRED assumptions it makes (never exercised against a live client).
        var bingoClient = new VenueBingoClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }); var bingoService = new VenueBingoService(bingoClient, venues, diagnostics, chat); bingoService.DiagnosticEvent += msg => Log.Debug($"[Bingo] {msg}"); bingoPayoutAutomation = new VenueOS.Plugin.Bingo.BingoPayoutAutomationService(ClientState, ChatGui, TargetManager, Log, diagnostics); var bingoPayoutOrchestrator = new BingoPayoutOrchestrator(bingoPayoutAutomation, bingoClient);
        // Party Finder is VenueOS-owned end to end: no live reference to the donor project. The unsafe automation
        // engine (VenueOS.Plugin.PartyFinder.PartyFinderAutomationService) depends only on VenueOS's own Dalamud
        // service instances and DiagnosticsService — never the donor's static Service locator or PluginConfiguration.
        partyFinderAutomation = new VenueOS.Plugin.PartyFinder.PartyFinderAutomationService(ClientState, Log, diagnostics); var partyFinderService = new PartyFinderService(partyFinderAutomation, venues, clock);
        // The ONE automatic-arrival trigger — for VIP and non-VIP guests alike. A dual-path design (this event plus
        // a second, independent GreetingCoordinator subscription to PresenceService.Arrived for VIP arrivals) used
        // to exist here and was a live-verified bug this consolidation fixed: the two event chains raced, and a
        // normal guest's arrival could silently never reach TryGreet at all. This handler's only job is "is this
        // arrival even eligible to be automatically evaluated" (never "is this guest a VIP" — that decision belongs
        // entirely to GreetingCoordinator.TryGreet, see its doc comment) — the sole guard kept out here is the
        // local-player exclusion, which is Plugin.cs-specific (needs ObjectTable.LocalPlayer) and unrelated to
        // VIP/normal routing. Deliberately AttendanceService.AutomaticGreetingEligible, NOT FirstVisitTonight — see
        // AutomaticGreetingEligible's doc comment for the second live-verified bug this fixes: FirstVisitTonight's
        // underlying database "first visit tonight" flag is consumed by opening-start seeding even though the event
        // itself is suppressed for the seed, so a seeded guest's later genuine arrival could never fire it again for
        // the rest of the calendar night. AutomaticGreetingEligible is a separate, in-memory, session-scoped signal
        // immune to that.
        attendanceService.AutomaticGreetingEligible += guest =>
        {
            Log.Debug($"[Greeting] Automatic greeting eligibility: {guest.Key}");
            if (IsLocalPlayer(guest)) { Log.Debug($"[Greeting] Skipped {guest.Key}: local player is never auto-greeted."); return; }
            coordinator.TryGreet(guest, GreetingSource.AutomaticArrival);
        };
        // Donor parity: the donor's tracker only scans while its venue is open, so opening one naturally
        // rediscovers everyone already inside as a fresh arrival. This shared PresenceService scans continuously
        // (VIP needs that with no session open), so PresenceService.ResetKnownPresence reproduces that effect
        // explicitly — AttendanceService.Arrive already excludes that seeded population from both FirstVisitTonight
        // and AutomaticGreetingEligible (see PresenceService.ResetKnownPresence's doc comment), so no further reset
        // is needed here now that GreetingCoordinator has no arrival-idempotence state of its own.
        attendanceService.SessionOpened += () => presence.ResetKnownPresence();
        var attendancePanel = new AttendanceOperatorPanel(attendanceService, venues, exportService, coordinator, addressProvider, TryTargetVisitor); var greeterPanel = new GreeterOperatorPanel(greeterService, venues, attendanceService); var vipPanel = new VipOperatorPanel(vipService, venues, targetedPlayerProvider); var shoutRunnerPanel = new ShoutRunnerOperatorPanel(shoutRunnerService, venues); var rafflePanel = new RaffleOperatorPanel(raffleService, venues); var triviaPanel = new MairsTriviaOperatorPanel(triviaService, venues, questionLibrary); var editorPanel = new MairsEditorOperatorPanel(editorService, venues); var tournamentPanel = new TournamentControlOperatorPanel(tournamentService, venues); var bingoPanel = new VenueBingoOperatorPanel(bingoService, bingoPayoutOrchestrator, bingoPayoutAutomation, venues, targetedPlayerProvider, () => { RequestOpenAndFocusTablet(); shell.SelectSettings(); settingsScreen!.FocusModuleConfiguration("games.bingo"); }, () => { RequestOpenAndFocusTablet(); shell.SelectModule("games.bingo"); }); var partyFinderPanel = new PartyFinderOperatorPanel(partyFinderService, VenueOS.Plugin.PartyFinder.PartyFinderDutyLoader.Build(DataManager), venues); attendance = new AttendanceModule(attendanceService, presence, venues, attendancePanel.Draw, attendancePanel.DrawSettings); modules.Register(attendance); modules.Register(new GreeterModule(greeterService, venues, greeterPanel.Draw, greeterPanel.DrawSettings)); modules.Register(new VipModule(vipService, coordinator, venues, vipPanel.Draw, vipPanel.DrawSettings)); modules.Register(new ShoutRunnerModule(shoutRunnerService, shoutRunnerPanel.Draw, shoutRunnerPanel.DrawSettings)); modules.Register(new PartyFinderModule(partyFinderService, partyFinderPanel.Draw, partyFinderPanel.DrawSettings)); modules.Register(new VenueRaffleModule(raffleService, rafflePanel.Draw)); modules.Register(new MairsTriviaModule(triviaService, triviaPanel.Draw, triviaPanel.DrawSettings)); modules.Register(new MairsEditorModule(editorPanel.Draw)); modules.Register(new TournamentControlModule(tournamentService, tournamentPanel.Draw)); bingoModule = new VenueBingoModule(bingoService, bingoPanel.Draw, bingoPanel.DrawSettings); modules.Register(bingoModule); bingoCalledNumbersWindow = bingoPanel.CalledNumbersWindow; bingoPlayerCardViewerWindow = bingoPanel.PlayerCardViewerWindow; bingoCallAlertWindow = bingoPanel.CallAlertWindow; ChatGui.ChatMessage += message => partyFinderService.HandleChatText(message.Message.TextValue); _ = new VenueOS.Plugin.Bingo.BingoRollChatAdapter(ChatGui, ObjectTable, bingoService.HandleRollObservation, msg => { Log.Debug($"[Bingo] {msg}"); bingoService.RecordAdapterDiagnostic(msg); }, () => bingoService.IsAwaitingRoll, bingoService.DescribePendingRoll); dashboard = new VenueOperationsDashboard(venues, modules, attendanceService, greeterService, vipService, shoutRunnerService, raffleService, triviaService, tournamentService, bingoService, diagnostics); var switchCoordinator = new VenueSwitchCoordinator(venues, [new TriviaVenueSwitchGuard(triviaService)]); settingsScreen = new SettingsScreen(venues, modules, diagnostics, globalSettings, switchCoordinator); windowManager = new ModuleWindowManager(diagnostics, shell, settingsScreen, RequestOpenAndFocusTablet); this.switchCoordinator = switchCoordinator;
        modules.ModuleFailed += (message, exception) => { diagnostics.RecordFailure(exception is null ? message : $"{message} {exception.Message}"); notifications.Push(exception is null ? message : $"{message} {exception.Message}", ToastLevel.Error); }; venues.ConfigurationRecovered += warning => notifications.Push(warning, ToastLevel.Warning);
        // Applies the operator's own persisted Settings → Modules toggle over each module's code-level default —
        // a module with no override here has never been explicitly toggled, so it keeps whatever its own IsEnabled
        // initializer already set (this is also how an unfinished module can ship disabled-by-default without ever
        // touching an operator's prior explicit choice to enable it). Must run before InitializeAsync so a disabled
        // override actually skips that module's init (ModuleHost.IsolateAsync checks IsEnabled).
        foreach (var module in modules.Modules)
            if (globalSettings.GetModuleEnabledOverride(module.Descriptor.Id) is { } enabledOverride)
                module.IsEnabled = enabledOverride;
        modules.InitializeAsync().GetAwaiter().GetResult(); venues.InitializeAsync().GetAwaiter().GetResult(); PluginInterface.UiBuilder.Draw += Draw; Framework.Update += Update;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnVenueOsCommand) { HelpMessage = "Open the VenueOS tablet.", ShowInHelp = true });
    }
    public void Dispose() { CommandManager.RemoveHandler(CommandName); PluginInterface.UiBuilder.Draw -= Draw; Framework.Update -= Update; modules.DisposeAsync().AsTask().GetAwaiter().GetResult(); partyFinderAutomation.Dispose(); shoutRunnerAutomation.Dispose(); bingoPayoutAutomation.Dispose(); venueDatabase.Dispose(); ECommonsMain.Dispose(); }
    private void OnVenueOsCommand(string command, string args) => RequestOpenAndFocusTablet();
    /// <summary>Opens the main tablet if it's closed and focuses it either way — the same behavior `/venueos`
    /// provides, reused by a detached module window's Settings gear so it never opens a second tablet.</summary>
    private void RequestOpenAndFocusTablet() { open = true; focusRequested = true; }
    private void Update(IFramework framework) { presence.Tick(attendance.Policy, ObjectTable.LocalPlayer?.Position); scheduler.Tick(); chat.TickAsync().GetAwaiter().GetResult(); modules.Tick(DateTimeOffset.UtcNow); }

    /// <summary>Donor parity (<c>VenueTrackerService.OnFirstVisitTonightDetected</c>'s local-player check): the
    /// operator's own character is still tracked/counted like any other guest, but is excluded from Auto Greet so
    /// the operator never gets tell'd by their own venue.</summary>
    private static bool IsLocalPlayer(GuestIdentity guest)
    {
        var local = ObjectTable.LocalPlayer;
        if (local is null) return false;
        var localWorld = local.HomeWorld.ValueNullable?.Name.ExtractText().Trim() ?? local.CurrentWorld.ValueNullable?.Name.ExtractText().Trim();
        return string.Equals(local.Name.TextValue?.Trim(), guest.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(localWorld, guest.HomeWorld, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Donor: <c>Plugin.ExecuteChatCommand</c> — the actual game-chat transport backing every AGV message
    /// (Greeter Lines 1-4, VIP recognition, VIP public /shout or /yell), wired as <see cref="ChatCommandService"/>'s
    /// <c>execute</c> delegate. <b>Do not replace this with <c>ICommandManager.ProcessCommand</c>.</b> The donor
    /// (<c>venuestatusandgreet</c>) proved live in FFXIV that <c>ProcessCommand</c> can appear to succeed internally
    /// (no exception, a `true`-ish return) while never actually submitting a message-sending command like
    /// <c>/tell</c>/<c>/shout</c>/<c>/yell</c> to the game server — <c>ProcessCommand</c> is reserved for genuinely
    /// client-local commands only (this plugin's own <c>/target</c>, in <see cref="TryTargetVisitor"/>). The donor's
    /// fix, reproduced here exactly: prefer <see cref="Chat.ExecuteCommand"/> (ECommons — "executes command as if it
    /// was typed in the chat box"), and if that throws, fall back to calling the game's own
    /// <see cref="RaptureShellModule.ExecuteCommandInner"/> directly. Only a genuine failure of both paths returns
    /// false, which aborts the in-flight greeting/announcement rather than marking it complete.</summary>
    private unsafe bool ExecuteChatCommand(string command)
    {
        var text = command.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;

        try { Chat.ExecuteCommand(text); return true; }
        catch (Exception ex) { Log.Debug(ex, $"ECommons chat execution failed for command: {text}"); }

        try
        {
            var uiModule = UIModule.Instance();
            var shellModule = RaptureShellModule.Instance();
            if (uiModule is null || shellModule is null) { Log.Warning($"RaptureShellModule unavailable; command not sent: {text}"); return false; }
            var utf8 = Utf8String.FromString(text);
            if (utf8 is null) { Log.Warning($"Could not allocate Utf8String for command: {text}"); return false; }
            try { shellModule->ExecuteCommandInner(utf8, uiModule); return true; }
            finally { utf8->Dtor(); IMemorySpace.Free(utf8); }
        }
        catch (Exception ex) { Log.Warning(ex, $"RaptureShellModule command execution failed: {text}"); return false; }
    }

    /// <summary>Donor: <c>Plugin.TryTargetVisitor</c> — targets the live game object if the visitor is still nearby,
    /// otherwise falls back to the client-side <c>/target</c> command (never sent to the server, so it doesn't need
    /// to go through the rate-limited <see cref="ChatCommandService"/> queue).</summary>
    private static void TryTargetVisitor(GuestIdentity guest)
    {
        var live = ObjectTable.OfType<IPlayerCharacter>().FirstOrDefault(player =>
            string.Equals(player.Name.TextValue, guest.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(player.HomeWorld.ValueNullable?.Name.ExtractText() ?? player.CurrentWorld.ValueNullable?.Name.ExtractText(), guest.HomeWorld, StringComparison.OrdinalIgnoreCase));
        if (live is not null) { TargetManager.Target = live; return; }
        var safeName = guest.Name.Replace("\"", "'", StringComparison.Ordinal);
        var safeWorld = guest.HomeWorld.Replace("\"", "'", StringComparison.Ordinal);
        CommandManager.ProcessCommand($"/target \"{safeName}@{safeWorld}\"");
    }
    private void Draw()
    {
        var venueTheme = venues.Current.Theme;

        // Detached module windows are independent ImGui windows and must keep rendering even while the main
        // tablet is closed — closing the tablet is a UI action on the tablet only, not on any module.
        windowManager.DrawAll(venueTheme, modules.Modules);
        // Bingo's two auxiliary windows (Called Numbers, Player Cards) are a second tier of detached window below
        // even that — they must survive not just the tablet closing, but the main Bingo screen itself being
        // closed/hidden or navigated away from (live-QA product requirement: the host wants to free screen space
        // by closing the main Bingo window while continuing to call numbers from Called Numbers alone). Gated on
        // bingoModule.IsEnabled — matching ModuleWindowManager's own convention — so a disabled Bingo module still
        // correctly stops showing its detached UI, per the module-disable/plugin-unload lifecycle requirement.
        if (bingoModule.IsEnabled) { bingoCalledNumbersWindow.Draw(venueTheme); bingoPlayerCardViewerWindow.Draw(venueTheme); bingoCallAlertWindow.Draw(venueTheme); }
        if (!open) return;

        UiKit.PushWindowTheme(venueTheme);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(980, 650), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(900, 560), new System.Numerics.Vector2(float.MaxValue, float.MaxValue));
        if (focusRequested) { ImGui.SetNextWindowFocus(); focusRequested = false; }
        // A stable "###" id keeps this ImGui window's identity (and therefore its remembered position/size)
        // constant across venue switches even though the visible label — irrelevant with NoTitleBar, but still
        // the string ImGui uses internally — changes with the active venue's name.
        if (!ImGui.Begin($"{shell.Header}###venueos-main-tablet", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse)) { ImGui.End(); UiKit.PopWindowTheme(); return; }

        if (venueTheme.Branding.ShowVenueFrame) UiKit.DrawVenueFrame(venueTheme);

        TabletHeader.Draw(venueTheme, venues, shell, () => open = false, switchCoordinator);
        ImGui.Spacing();

        var footerReserve = venueTheme.Branding.ShowVenueOsBranding ? 22f : 0f;
        ImGui.BeginChild("content", new System.Numerics.Vector2(0, -footerReserve), false);
        if (shell.IsSettingsSelected) AppFrame.Draw(venueTheme, "gear", "Settings", "Venue management, theming and diagnostics.", () => settingsScreen.Draw(venueTheme));
        else if (shell.IsManualSelected) AppFrame.Draw(venueTheme, "book", "User Manual", "The complete VenueOS user manual, bundled offline with this release.", () => manualScreen.Draw(venueTheme));
        else if (shell.IsHome) HomeScreen.Draw(venueTheme, modules, shell, diagnostics, globalSettings, windowManager, dashboard.Draw);
        else
        {
            var selected = modules.Modules.SingleOrDefault(x => x.Descriptor.Id == shell.SelectedModuleId);
            if (selected is not null && selected.IsEnabled) AppFrame.DrawModule(venueTheme, selected, windowManager, diagnostics); else shell.SelectHome();
        }
        ImGui.EndChild();

        if (venueTheme.Branding.ShowVenueOsBranding) UiKit.DrawChassisBrand(venueTheme);

        ImGui.End(); UiKit.PopWindowTheme();
    }
}

internal sealed class DalamudObjectSnapshotProvider(IObjectTable objects, IClientState clientState) : IObjectSnapshotProvider
{
    public IReadOnlyList<PlayerSnapshot> Snapshot() => objects.OfType<IPlayerCharacter>().Select(player => new PlayerSnapshot(player.Name.TextValue, player.HomeWorld.ValueNullable?.Name.ExtractText() ?? player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "Unknown", player.GameObjectId, clientState.TerritoryType, player.Position)).ToArray();
}

/// <summary>Backs VIP's "Use Current Target" autofill via Dalamud's supported <see cref="ITargetManager"/> — never
/// unsafe/manual memory access, matching the module-development guide's requirement.</summary>
internal sealed class DalamudTargetedPlayerProvider(ITargetManager targetManager) : ITargetedPlayerProvider
{
    public TargetedPlayerLookup GetTargetedPlayer()
    {
        if (targetManager.Target is null) return TargetedPlayerLookup.Failed("No target selected.");
        if (targetManager.Target is not IPlayerCharacter player) return TargetedPlayerLookup.Failed("Target is not a player character.");
        var name = player.Name.TextValue?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return TargetedPlayerLookup.Failed("Target's name is not currently available.");
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText().Trim() ?? player.CurrentWorld.ValueNullable?.Name.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(world)) return TargetedPlayerLookup.Failed("Target's home world is not currently available.");
        return TargetedPlayerLookup.Found(name, world);
    }
}

/// <summary>Donor: <c>VenueAddressService</c>, ported directly — DC/Server/District come from safe, documented
/// Dalamud APIs (<see cref="IPlayerState"/>/<see cref="IClientState"/>/<see cref="IDataManager"/>'s Excel sheets);
/// Ward/Plot/Subdivision require FFXIVClientStructs' <c>HousingManager</c> singleton, which has no safe-API
/// equivalent as of this Dalamud SDK version. This is the donor's own already-working implementation, unmodified
/// in behavior, wrapped in the same defensive try/catch — not new unsafe code written for this pass. It was not
/// possible to verify this against a live game session in this environment; see the reconstruction notes.</summary>
internal sealed class DalamudVenueAddressProvider(IClientState clientState, IPlayerState playerState, IDataManager dataManager, IPluginLog log) : IVenueAddressProvider
{
    private readonly Dictionary<uint, string> districtNameCache = [];

    public bool TryGetCurrentAddress(out VenueAddressSnapshot snapshot)
    {
        snapshot = new VenueAddressSnapshot("", "", "", null, null, false);
        var world = playerState.CurrentWorld.ValueNullable;
        if (!world.HasValue) return false;

        var server = Clean(world.Value.Name.ExtractText());
        var dc = Clean(world.Value.DataCenter.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(dc)) return false;

        var territoryId = clientState.TerritoryType;
        var districtTerritoryId = territoryId;
        int? ward = null; int? plot = null; var subdivision = false;

        try
        {
            unsafe
            {
                var housingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                if (housingManager is not null && housingManager->IsInside())
                {
                    var original = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.GetOriginalHouseTerritoryTypeId();
                    if (original > 0 && original <= ushort.MaxValue) districtTerritoryId = (ushort)original;
                    var wardRaw = housingManager->GetCurrentWard(); if (wardRaw >= 0) ward = wardRaw + 1;
                    var plotRaw = housingManager->GetCurrentPlot(); if (plotRaw >= 0) plot = plotRaw + 1;
                    subdivision = housingManager->GetCurrentDivision() == 1;
                }
            }
        }
        catch (Exception ex) { log.Debug(ex, "Housing manager lookup failed while auto-detecting venue address."); }

        var district = ResolveDistrictName(districtTerritoryId);
        if (string.IsNullOrWhiteSpace(district)) district = ResolveDistrictName(territoryId);

        snapshot = new VenueAddressSnapshot(dc, server, string.IsNullOrWhiteSpace(district) ? "Unknown District" : district, ward, plot, subdivision);
        return true;
    }

    private string ResolveDistrictName(uint territoryTypeId)
    {
        if (territoryTypeId == 0) return string.Empty;
        if (districtNameCache.TryGetValue(territoryTypeId, out var cached)) return cached;
        var row = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(territoryTypeId);
        if (!row.HasValue) return string.Empty;
        var placeName = Clean(row.Value.PlaceName.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrWhiteSpace(placeName)) placeName = Clean(row.Value.PlaceNameZone.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrWhiteSpace(placeName)) placeName = Clean(row.Value.PlaceNameRegion.ValueNullable?.Name.ExtractText());
        districtNameCache[territoryTypeId] = placeName;
        return placeName;
    }

    private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}

public sealed class VenueOsPluginConfiguration : Dalamud.Configuration.IPluginConfiguration { public int Version { get; set; } = 1; public VenueStoreSnapshot? State { get; set; } public GlobalSettings? Global { get; set; } }

/// <summary>Both <see cref="DalamudVenueStore"/> and <see cref="DalamudGlobalSettingsStore"/> persist through the
/// same underlying Dalamud plugin-config file (<see cref="VenueOsPluginConfiguration"/>'s <c>State</c> and
/// <c>Global</c> properties respectively). They previously each called <c>pluginInterface.GetPluginConfig()</c>
/// independently and cached their own separate deserialized object. If <see cref="IDalamudPluginInterface.GetPluginConfig"/>
/// does not return the exact same cached instance on every call (deserializing fresh from the on-disk JSON each
/// time), those two independently-cached objects could drift: whichever store's <c>Write</c> ran second would
/// re-serialize its own stale copy — including whatever the OTHER store's property held at THAT store's own
/// construction time — silently discarding any edits the other store had already durably saved. Loading the config
/// exactly once, here, and handing the SAME instance to both stores removes that risk entirely: a write through
/// either store always includes the other's latest in-memory value, because they are the same object.</summary>
internal sealed class DalamudVenueStore(IDalamudPluginInterface pluginInterface, VenueOsPluginConfiguration config) : IVenueStore
{
    public VenueStoreSnapshot Read() => InMemoryVenueStore.DeepCopy(config.State ?? VenueProfileService.CreateInitialSnapshot());
    public void Write(VenueStoreSnapshot snapshot) { config.State = InMemoryVenueStore.DeepCopy(snapshot); pluginInterface.SavePluginConfig(config); }
}

/// <summary>Backs <see cref="GlobalSettingsService"/> with the same Dalamud plugin-config object venue data uses —
/// a distinct property on it (<see cref="VenueOsPluginConfiguration.Global"/>), not folded into <c>State</c>, so a
/// global preference is structurally incapable of being read/written per-venue by mistake. See
/// <see cref="DalamudVenueStore"/>'s doc comment for why this must share the exact same <paramref name="config"/>
/// instance as that store, not its own independently-loaded copy.</summary>
internal sealed class DalamudGlobalSettingsStore(IDalamudPluginInterface pluginInterface, VenueOsPluginConfiguration config) : IGlobalSettingsStore
{
    public GlobalSettings Read() => config.Global ?? new GlobalSettings();
    public void Write(GlobalSettings settings) { config.Global = settings; pluginInterface.SavePluginConfig(config); }
}
