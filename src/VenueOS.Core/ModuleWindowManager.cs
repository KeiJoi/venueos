namespace VenueOS.Core;

/// <summary>Presentation state of a module's <b>detached</b> window only — embedded rendering has no equivalent
/// concept (switching away from an embedded module is already Home navigation). A module id absent from
/// <see cref="ModuleWindowManager"/>'s tracked set means "closed", matching the original
/// <c>HashSet&lt;string&gt;</c> semantics exactly — there is no separate "IsClosed" bool.</summary>
public enum ModulePresentationState { Expanded, Collapsed, Hidden }

/// <summary>A module's remembered detached-window geometry/state — what gets persisted to
/// <c>GlobalSettings.ModuleWindowPreferences</c> and what seeds the next <see cref="ModuleWindowManager.Open"/>.
/// <see cref="LastState"/> is always <see cref="ModulePresentationState.Expanded"/> or
/// <see cref="ModulePresentationState.Collapsed"/>, never <see cref="ModulePresentationState.Hidden"/> — Hidden is
/// not a persisted "opening default" (opening a module implies wanting to see it), so
/// <see cref="ModuleWindowManager.GetSnapshot"/>/<see cref="ModuleWindowManager.Evict"/> report whatever the module
/// was showing right before it was hidden instead.</summary>
public sealed record ModuleWindowPreference(ModulePresentationState LastState, float PosX, float PosY, float Width, float Height);

/// <summary>Pure (ImGui-free) state machine for every module's detached-window presentation — Expanded, Collapsed,
/// or Hidden — plus remembered position/size and pending focus requests. This is layer 1 of the two-layer split
/// described in the Module Launcher / Window Management plan: this class owns state only and has zero dependency on
/// ImGui, <c>IVenueModule</c>, or any VenueOS service, which is what makes it directly unit-testable and what proves
/// Collapse/Hide/Restore/Close can never reach into module operational state (Party Finder, ShoutRunner, Trivia,
/// etc. keep running while hidden — presentation and operation are structurally separate, not just separate by
/// convention). Layer 2 is <c>VenueOS.Plugin.Shell.ModuleWindowManager</c> — same class name, different namespace,
/// deliberately, per the plan's "keep the familiar name/call sites" instruction — which wraps an instance of this
/// class with the actual <c>ImGui.Begin</c>/<c>End</c> calls and <c>GlobalSettingsService</c> persistence.</summary>
public sealed class ModuleWindowManager
{
    private sealed class Entry
    {
        public ModulePresentationState State = ModulePresentationState.Expanded;
        public ModulePresentationState? PreHideState;
        public float PosX, PosY;
        public float ExpandedWidth = 560, ExpandedHeight = 480;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> focusRequested = new(StringComparer.Ordinal);

    public bool IsOpen(string moduleId) => entries.ContainsKey(moduleId);
    public ModulePresentationState? GetState(string moduleId) => entries.TryGetValue(moduleId, out var e) ? e.State : null;

    /// <summary>Opens the module's detached window, or focuses it if already open — never a second instance (today's
    /// exact <c>Open</c> behavior, unchanged). Returns <c>true</c> only when this call actually created a new
    /// tracked entry (a "fresh" open) — the caller uses this to know whether <paramref name="preference"/> was
    /// actually applied, since a refocus of an already-open window must never silently reposition/resize it.
    /// <paramref name="preference"/>, when supplied on a fresh open, seeds the initial state/position/size instead
    /// of the hardcoded default — this is how "a module that was collapsed before reload reopens collapsed, at its
    /// last position/size" is satisfied without ever auto-opening anything on its own (reload itself never calls
    /// this; only an explicit tile click, pop-out button, or Launcher click does).</summary>
    public bool Open(string moduleId, ModuleWindowPreference? preference = null)
    {
        if (entries.ContainsKey(moduleId)) { focusRequested.Add(moduleId); return false; }
        var entry = new Entry();
        if (preference is not null)
        {
            entry.State = preference.LastState == ModulePresentationState.Collapsed ? ModulePresentationState.Collapsed : ModulePresentationState.Expanded;
            entry.PosX = preference.PosX;
            entry.PosY = preference.PosY;
            if (preference.Width > 0) entry.ExpandedWidth = preference.Width;
            if (preference.Height > 0) entry.ExpandedHeight = preference.Height;
        }
        entries[moduleId] = entry;
        focusRequested.Add(moduleId);
        return true;
    }

    /// <summary>Stops tracking the module entirely — today's exact Close behavior, unchanged. Used for both an
    /// explicit user Close <b>and</b> disabled-module eviction (the caller decides which); the returned snapshot (or
    /// <c>null</c> if the module wasn't tracked) is what the caller should persist as the module's remembered
    /// preference for its next Open, so neither path ever silently discards where the window was.</summary>
    public ModuleWindowPreference? Evict(string moduleId)
    {
        if (!entries.TryGetValue(moduleId, out var entry)) return null;
        var snapshot = BuildSnapshot(entry);
        entries.Remove(moduleId);
        focusRequested.Remove(moduleId);
        return snapshot;
    }

    public void Collapse(string moduleId) { if (entries.TryGetValue(moduleId, out var e) && e.State != ModulePresentationState.Hidden) e.State = ModulePresentationState.Collapsed; }
    public void Expand(string moduleId) { if (entries.TryGetValue(moduleId, out var e) && e.State != ModulePresentationState.Hidden) e.State = ModulePresentationState.Expanded; }

    /// <summary>Keeps the module tracked (unlike Close/Evict) with <c>State = Hidden</c>, remembering whichever of
    /// Expanded/Collapsed it was showing so <see cref="Restore"/> can return to exactly that — preserving the
    /// user's deliberate collapsed state rather than always popping back to Expanded.</summary>
    public void Hide(string moduleId)
    {
        if (!entries.TryGetValue(moduleId, out var e) || e.State == ModulePresentationState.Hidden) return;
        e.PreHideState = e.State;
        e.State = ModulePresentationState.Hidden;
    }

    public void Restore(string moduleId)
    {
        if (!entries.TryGetValue(moduleId, out var e)) return;
        e.State = e.PreHideState ?? ModulePresentationState.Expanded;
        e.PreHideState = null;
        focusRequested.Add(moduleId);
    }

    public void RequestFocus(string moduleId) { if (entries.ContainsKey(moduleId)) focusRequested.Add(moduleId); }
    public bool ConsumeFocusRequest(string moduleId) => focusRequested.Remove(moduleId);

    /// <summary>Last known on-screen position — updated every frame regardless of state, fed from
    /// <c>ImGui.GetWindowPos()</c> by the rendering layer.</summary>
    public void ReportPosition(string moduleId, float x, float y) { if (entries.TryGetValue(moduleId, out var e)) { e.PosX = x; e.PosY = y; } }

    /// <summary>Remembered expanded size — updated only while <c>State == Expanded</c>, so collapsing never
    /// overwrites it with the compact header's own fixed size.</summary>
    public void ReportExpandedSize(string moduleId, float width, float height) { if (entries.TryGetValue(moduleId, out var e) && e.State == ModulePresentationState.Expanded) { e.ExpandedWidth = width; e.ExpandedHeight = height; } }

    public ModuleWindowPreference? GetSnapshot(string moduleId) => entries.TryGetValue(moduleId, out var e) ? BuildSnapshot(e) : null;

    public IReadOnlyList<string> OpenModuleIds => entries.Keys.ToArray();

    private static ModuleWindowPreference BuildSnapshot(Entry e) =>
        new(e.State == ModulePresentationState.Hidden ? (e.PreHideState ?? ModulePresentationState.Expanded) : e.State, e.PosX, e.PosY, e.ExpandedWidth, e.ExpandedHeight);
}
