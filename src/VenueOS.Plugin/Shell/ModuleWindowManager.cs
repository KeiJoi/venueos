using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.UI;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Owns which modules currently have an independent, detached operator window open, and how it's drawn.
/// The main tablet stays the launcher; detaching/re-embedding a module is a pure UI choice that never touches module
/// lifecycle, and every detached window renders the exact same <c>IVenueModule.Draw()</c> the embedded
/// <see cref="AppFrame"/> does — there is only ever one module UI implementation, wrapped by two different chrome
/// layers. Chrome itself is <see cref="ModuleWindowHeader"/>, shared by every module rather than reimplemented per
/// window.
///
/// Module Launcher & Window Management pass: this class is now layer 2 of a two-layer split. Layer 1,
/// <see cref="VenueOS.Core.ModuleWindowManager"/> (same class name, deliberately, in a different namespace — see
/// its own doc comment), is the pure Expanded/Collapsed/Hidden state machine; this class wraps one instance of it
/// with the actual <c>ImGui.Begin</c>/<c>End</c> calls and <see cref="GlobalSettingsService"/> persistence, while
/// keeping every call site in <c>Plugin.cs</c>/<c>HomeScreen</c>/<c>ModuleLauncherWindow</c> unchanged
/// (<c>windowManager.Open(id)</c> etc.). A Hidden module is skipped with no <c>ImGui.Begin</c> call at all — this is
/// presentation-only and never touches module operational state (Party Finder, ShoutRunner, Trivia, etc. keep
/// running while hidden); see <c>docs/MODULE_LAUNCHER_WINDOW_MANAGEMENT.md</c>'s operational-separation section.
///
/// Live QA follow-up: this class also now owns the main tablet's own Collapse/Expand state, through a second,
/// separate <see cref="VenueOS.Core.ModuleWindowManager"/> instance under a reserved key — see the "Main tablet
/// Collapse/Expand" region below for exactly why a second instance (not the same instance used for modules) is
/// used, and why that's still reusing the same machinery rather than duplicated logic.</summary>
internal sealed class ModuleWindowManager(DiagnosticsService diagnostics, VenueShell shell, SettingsScreen settingsScreen, Action requestOpenAndFocusTablet, GlobalSettingsService globalSettings)
{
    private const float CollapsedWidth = 320f;
    private const float CollapsedHeight = ModuleWindowHeader.HeaderHeight + 12f;
    // Live QA follow-up — main tablet Collapse/Expand reuses the exact same compact-header geometry every module's
    // own Collapsed state already uses; exposed publicly since Plugin.cs (a different namespace) needs them too.
    public const float TabletCollapsedWidth = CollapsedWidth;
    public const float TabletCollapsedHeight = CollapsedHeight;

    private readonly VenueOS.Core.ModuleWindowManager core = new();
    // A module id whose window was Collapsed on the previous frame — used only to detect the single frame a
    // Collapsed window transitions to Expanded, so the transition-frame one-shot repositioning (see DrawWindow)
    // never fires on any other frame.
    private readonly HashSet<string> collapsedLastFrame = new(StringComparer.Ordinal);
    // A module id whose most recent Open call actually applied a persisted preference (a fresh open, not a refocus
    // of an already-open window) — consumed on the very next DrawWindow call for that id to force its remembered
    // position/size once, then never again until the next fresh Open.
    private readonly HashSet<string> pendingForcedGeometry = new(StringComparer.Ordinal);

    public bool IsOpen(string moduleId) => core.IsOpen(moduleId);
    public ModulePresentationState? GetState(string moduleId) => core.GetState(moduleId);
    public void RequestFocus(string moduleId) => core.RequestFocus(moduleId);

    /// <summary>Opens the module's detached window, or focuses it if already open — never a second instance. A
    /// fresh open (not a refocus) seeds from the module's persisted <see cref="ModuleWindowPreference"/> if one
    /// exists, so a module that was collapsed before the last reload reopens collapsed, at its last position/size —
    /// this is the "next explicit open seeds from the persisted preference" resolution to the "no window may
    /// auto-open on load" rule (NEW_MODULE_GUIDE.md §7): nothing here runs automatically on plugin construction,
    /// only from an actual Open call.</summary>
    public void Open(string moduleId)
    {
        var preference = globalSettings.GetModuleWindowPreference(moduleId);
        var wasFresh = core.Open(moduleId, preference);
        if (wasFresh && preference is not null) pendingForcedGeometry.Add(moduleId);
    }

    public void Restore(string moduleId)
    {
        core.Restore(moduleId);
        PersistTransition(moduleId);
    }

    /// <summary><paramref name="canRenderModule"/> is the 0.3.0 character-session presentation gate
    /// (<c>Plugin.cs</c>'s composed policy, ultimately <c>SessionPresentationGateService</c>) — a module whose
    /// window is open but not currently permitted to render (e.g. genuinely logged out, with no ShoutRunner
    /// exception applying to this module) is simply skipped for THIS frame; it stays tracked and resumes rendering
    /// the moment the gate allows it again. This is presentation-only — it must never evict the module, which would
    /// be indistinguishable from the operator explicitly closing the window.
    ///
    /// Disabled-module eviction keeps today's rule (a disabled module's detached window is force-closed every frame
    /// it's disabled) but now also preserves its remembered geometry/state for next time, via the same
    /// <see cref="VenueOS.Core.ModuleWindowManager.Evict"/> + persist path an explicit Close uses.</summary>
    public void DrawAll(VenueTheme theme, IReadOnlyList<IVenueModule> modules, Func<string, bool> canRenderModule)
    {
        foreach (var moduleId in core.OpenModuleIds)
        {
            var module = modules.FirstOrDefault(x => x.Descriptor.Id == moduleId);
            if (module is null || !module.IsEnabled) { EvictAndForget(moduleId); continue; }
            if (!canRenderModule(moduleId)) continue;
            DrawWindow(theme, module);
        }
    }

    /// <summary>Flushes every currently-open module's live (continuously in-memory-updated) geometry to persistent
    /// storage — called once from <c>Plugin.Dispose()</c> so a module that was merely dragged/resized this session
    /// (never explicitly Closed/Collapsed/Expanded) still remembers its true last position/size across
    /// <c>/xlreload</c> or a plugin disable, rather than only whatever was last written at an explicit state
    /// transition.</summary>
    public void PersistAllOpenGeometry()
    {
        foreach (var moduleId in core.OpenModuleIds) PersistTransition(moduleId);
    }

    // ---- Main tablet Collapse/Expand (live-QA follow-up) ---------------------------------------------------------
    //
    // Reserved key for the main VenueOS tablet's own presentation state (Plugin.cs's own ImGui.Begin — not a
    // module). Reuses the exact same pure state-machine CLASS (VenueOS.Core.ModuleWindowManager) and the exact same
    // persisted GlobalSettings.ModuleWindowPreferences dictionary every module's detached window already goes
    // through (GlobalSettingsService.GetModuleWindowPreference/SetModuleWindowPreference, keyed by this string
    // exactly like a module id). Real module ids are dotted, lowercase, namespaced (e.g. "core.attendance") and
    // never leading-underscore, so this key can never collide with one.
    //
    // Deliberately tracked through its OWN separate VenueOS.Core.ModuleWindowManager instance below, NOT the `core`
    // field above: `core.OpenModuleIds` is iterated every frame by DrawAll/PersistAllOpenGeometry and cross-checked
    // against the live `modules.Modules` list — an id with no matching module is force-evicted (DrawAll's
    // `module is null` branch a few lines up). Seeding the tablet into that same set would therefore have DrawAll
    // evict it on literally the very next frame. A second instance of the identical pure class sidesteps that
    // without touching DrawAll or the Launcher at all — this is not duplicated state-machine logic (it's the same
    // class, reused), and it keeps the tablet structurally invisible to ModuleHost/LauncherEntries/the Launcher UI,
    // which only ever iterate `core`/`modules.Modules`, never this field.
    public const string TabletKey = "__venueos.tablet__";
    private readonly VenueOS.Core.ModuleWindowManager tabletCore = new();
    // Mirrors collapsedLastFrame/pendingForcedGeometry above, scoped to the one tablet entry (no HashSet needed).
    private bool tabletCollapsedLastFrame;
    private bool tabletPendingForcedGeometry;

    /// <summary>Seeds the tablet's tracked entry once from any persisted preference — call once, from Plugin's
    /// constructor. Never auto-opens/shows anything on its own: the tablet's own <c>open</c> bool (entirely
    /// separate, unchanged by this pass) remains the sole gate on whether it's ever actually drawn; this only
    /// ensures Collapse/Expand/geometry have a tracked entry to live in from the first frame it might be shown.
    /// Idempotent — a second call is a no-op, matching <see cref="Open"/>'s own idempotent-open contract.</summary>
    public void EnsureTabletTracked()
    {
        if (tabletCore.IsOpen(TabletKey)) return;
        var preference = globalSettings.GetModuleWindowPreference(TabletKey);
        var wasFresh = tabletCore.Open(TabletKey, preference);
        if (wasFresh && preference is not null) tabletPendingForcedGeometry = true;
    }

    /// <summary>Defaults to Expanded if, somehow, called before <see cref="EnsureTabletTracked"/> — never null, so
    /// <c>Plugin.Draw</c> doesn't need to null-check every frame the way module code does.</summary>
    public ModulePresentationState TabletState => tabletCore.GetState(TabletKey) ?? ModulePresentationState.Expanded;

    public ModuleWindowPreference? GetTabletSnapshot() => tabletCore.GetSnapshot(TabletKey);

    public void CollapseTablet() { tabletCore.Collapse(TabletKey); PersistTabletGeometry(); }
    public void ExpandTablet() { tabletCore.Expand(TabletKey); PersistTabletGeometry(); }

    /// <summary>Every frame while Expanded — mirrors <c>core.ReportPosition</c>/<c>ReportExpandedSize</c>'s own
    /// continuous reporting in <see cref="DrawWindow"/>.</summary>
    public void ReportTabletPosition(float x, float y) => tabletCore.ReportPosition(TabletKey, x, y);
    public void ReportTabletExpandedSize(float width, float height) => tabletCore.ReportExpandedSize(TabletKey, width, height);

    /// <summary>Call once from <c>Plugin.Dispose()</c> alongside <see cref="PersistAllOpenGeometry"/>, so a tablet
    /// that was only ever dragged/resized this session (never explicitly Collapsed/Expanded) still remembers its
    /// true last geometry across <c>/xlreload</c> — the exact same reasoning <see cref="PersistAllOpenGeometry"/>
    /// documents for modules.</summary>
    public void PersistTabletGeometry()
    {
        var snapshot = tabletCore.GetSnapshot(TabletKey);
        if (snapshot is not null) globalSettings.SetModuleWindowPreference(TabletKey, snapshot);
    }

    /// <summary>Mirrors <see cref="DrawWindow"/>'s own <c>wasCollapsedLastFrame</c>/<c>justExpanded</c>/
    /// <c>forceGeometryThisFrame</c> bookkeeping exactly, just for the one tablet entry instead of a per-module
    /// HashSet. Call once per frame from <c>Plugin.Draw</c>, before deciding whether to force position/size this
    /// frame — the result tells the caller whether this is the single frame a Collapsed tablet transitioned to
    /// Expanded (or a fresh construction seeded from a persisted Collapsed preference), during which position + the
    /// remembered expanded size should be forced once via <c>ImGuiCond.Always</c>.</summary>
    public bool ConsumeTabletForcedGeometryFrame()
    {
        var state = TabletState;
        var wasCollapsedLastFrame = tabletCollapsedLastFrame;
        tabletCollapsedLastFrame = state == ModulePresentationState.Collapsed;
        var justExpanded = wasCollapsedLastFrame && state == ModulePresentationState.Expanded;
        var force = justExpanded || tabletPendingForcedGeometry;
        tabletPendingForcedGeometry = false;
        return force;
    }

    private void EvictAndForget(string moduleId)
    {
        var evicted = core.Evict(moduleId);
        if (evicted is not null) globalSettings.SetModuleWindowPreference(moduleId, evicted);
        collapsedLastFrame.Remove(moduleId);
        pendingForcedGeometry.Remove(moduleId);
    }

    private void PersistTransition(string moduleId)
    {
        var snapshot = core.GetSnapshot(moduleId);
        if (snapshot is not null) globalSettings.SetModuleWindowPreference(moduleId, snapshot);
    }

    private void DrawWindow(VenueTheme theme, IVenueModule module)
    {
        var moduleId = module.Descriptor.Id;
        var state = core.GetState(moduleId);
        if (state is null || state == ModulePresentationState.Hidden) return; // Hidden: no ImGui.Begin at all.
        var snapshot = core.GetSnapshot(moduleId)!;

        var wasCollapsedLastFrame = collapsedLastFrame.Contains(moduleId);
        if (state == ModulePresentationState.Collapsed) collapsedLastFrame.Add(moduleId); else collapsedLastFrame.Remove(moduleId);
        var justExpanded = wasCollapsedLastFrame && state == ModulePresentationState.Expanded;
        var forceGeometryThisFrame = justExpanded || pendingForcedGeometry.Remove(moduleId);

        if (core.ConsumeFocusRequest(moduleId)) ImGui.SetNextWindowFocus();

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (state == ModulePresentationState.Collapsed)
        {
            // Fixed compact header height, forced every frame — same technique Macro's HUD hotbars use to force
            // their own size every frame — plus NoResize so native resize grips can't fight it. Position is left
            // alone (still draggable via the header's own drag handle) except on the one frame a fresh Open seeded
            // it from a persisted Collapsed preference.
            flags |= ImGuiWindowFlags.NoResize;
            if (forceGeometryThisFrame) ImGui.SetNextWindowPos(new Vector2(snapshot.PosX, snapshot.PosY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(CollapsedWidth, CollapsedHeight), ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(new Vector2(420, 320), new Vector2(float.MaxValue, float.MaxValue));
            if (forceGeometryThisFrame)
            {
                // The single frame a Collapsed window transitions to Expanded (or a fresh Open seeds from a
                // persisted Expanded preference): force position + the remembered expanded size once, then let
                // ImGui resume normal free resize/move for every subsequent frame.
                ImGui.SetNextWindowPos(new Vector2(snapshot.PosX, snapshot.PosY), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(snapshot.Width, snapshot.Height), ImGuiCond.Always);
            }
            else
            {
                ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
            }
        }

        UiKit.PushWindowTheme(theme);
        // No visible title text — the header below is the only chrome — so the window name is a pure stable ID.
        // It never embeds the module/venue display name, so a rename or venue switch can't reset remembered
        // position/size the way the main tablet's id had to be fixed to avoid in Phase 3.
        var closeRequested = false; var collapseToggleRequested = false; var minimizeRequested = false;
        if (ImGui.Begin($"###venueos-detached-{moduleId}", flags))
        {
            var currentPos = ImGui.GetWindowPos();
            core.ReportPosition(moduleId, currentPos.X, currentPos.Y); // updated every frame regardless of state

            ModuleWindowHeader.Draw(theme, module.Descriptor.Icon, module.Descriptor.DisplayName, moduleId,
                onSettings: () => { requestOpenAndFocusTablet(); shell.SelectSettings(); settingsScreen.FocusModuleConfiguration(moduleId); },
                onClose: () => closeRequested = true,
                collapsed: state == ModulePresentationState.Collapsed,
                onToggleCollapse: () => collapseToggleRequested = true,
                onMinimize: () => minimizeRequested = true);

            if (state == ModulePresentationState.Expanded)
            {
                var windowSize = ImGui.GetWindowSize();
                core.ReportExpandedSize(moduleId, windowSize.X, windowSize.Y);

                ImGui.BeginChild("detached-content", ImGui.GetContentRegionAvail(), false);
                UiKit.SafeDraw(theme, diagnostics, moduleId, module.Draw);
                ImGui.EndChild();
            }
            // Collapsed: header only, drawn above — deliberately no module content and no AppFrame wrapping.
        }
        ImGui.End();
        UiKit.PopWindowTheme();

        // Close vs Minimize (a genuinely new, additional control, not a redefinition of Close): Close evicts the
        // module from the tracked set entirely (today's exact behavior) — Minimize keeps it tracked as Hidden so
        // the Launcher can restore it and its remembered position/size stays live.
        if (closeRequested) EvictAndForget(moduleId);
        if (collapseToggleRequested) { if (state == ModulePresentationState.Collapsed) core.Expand(moduleId); else core.Collapse(moduleId); PersistTransition(moduleId); }
        if (minimizeRequested) core.Hide(moduleId);
    }
}
