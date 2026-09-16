using VenueOS.Core;

namespace VenueOS.Services;

/// <summary>Application-wide VenueOS preferences — explicitly NOT venue-scoped. Persisted independently of any
/// <c>VenueProfile</c>, so switching the active venue never reads, writes, or resets these values.
/// <see cref="ModuleEnabledOverrides"/> records only a module's explicit operator choice from Settings → Modules'
/// Toggle (keyed by <c>ModuleDescriptor.Id</c>) — a module with no entry here has never been explicitly toggled and
/// falls back to its own code-level default (<c>IVenueModule.IsEnabled</c>'s initial value), so changing that
/// code-level default later (e.g. shipping an unfinished module disabled by default) never overwrites an operator's
/// own prior choice. <see cref="Launcher"/>/<see cref="ModuleWindowPreferences"/> are global for the same reason
/// <see cref="AutoPopOutModules"/> is (Module Launcher & Window Management pass): the launcher and a module's
/// remembered detached-window geometry are preferences about how the VenueOS application itself behaves, not venue
/// data — see <c>GlobalSettingsService</c>'s doc comment and NEW_MODULE_GUIDE.md §12.</summary>
public sealed record GlobalSettings(
    bool AutoPopOutModules = false,
    IReadOnlyDictionary<string, bool>? ModuleEnabledOverrides = null,
    LauncherSettings? Launcher = null,
    IReadOnlyDictionary<string, ModuleWindowPreference>? ModuleWindowPreferences = null);

/// <summary>Persisted Module Launcher configuration — a small, always-available hotbar independent of the main
/// tablet (see <c>VenueOS.Plugin.Shell.ModuleLauncherWindow</c>). <see cref="ShowOnLauncher"/> is default-on/
/// opt-out: a module id with no explicit entry is treated as shown, provided it's enabled — so a fresh launcher
/// configuration shows every eligible module immediately, and only an explicit operator choice ever hides one (that
/// choice then survives a disable/re-enable or a reload, exactly like <see cref="GlobalSettings.ModuleEnabledOverrides"/>
/// does). <see cref="Width"/>/<see cref="Height"/> and <see cref="Scale"/> are independent controls — resizing the
/// window never changes <see cref="Scale"/>, and changing <see cref="Scale"/> never changes the window's pixel
/// dimensions. <see cref="ButtonsPerRow"/> is authoritative and independent of both: resizing changes available
/// pixel space only, never the column count (see <c>LauncherLayout</c>).</summary>
public sealed record LauncherSettings(
    bool Enabled = true,
    float PositionX = 24, float PositionY = 24,
    float Width = 360, float Height = 72,
    float Scale = 1.0f,
    int ButtonsPerRow = 6,
    bool Locked = true,
    IReadOnlyList<string>? ModuleOrder = null,
    IReadOnlyDictionary<string, bool>? ShowOnLauncher = null);

public interface IGlobalSettingsStore
{
    GlobalSettings Read();
    void Write(GlobalSettings settings);
}

public sealed class GlobalSettingsService(IGlobalSettingsStore store)
{
    private GlobalSettings settings = store.Read();
    public bool AutoPopOutModules => settings.AutoPopOutModules;
    public void SetAutoPopOutModules(bool value)
    {
        if (value == settings.AutoPopOutModules) return;
        settings = settings with { AutoPopOutModules = value };
        store.Write(settings);
    }

    /// <summary>The operator's explicit enabled/disabled choice for a module, or null if they've never touched its
    /// Toggle — callers should fall back to the module's own code-level default in that case, never to a hard-coded
    /// value here.</summary>
    public bool? GetModuleEnabledOverride(string moduleId) =>
        settings.ModuleEnabledOverrides is { } overrides && overrides.TryGetValue(moduleId, out var value) ? value : null;

    public void SetModuleEnabled(string moduleId, bool enabled)
    {
        var overrides = new Dictionary<string, bool>(settings.ModuleEnabledOverrides ?? new Dictionary<string, bool>()) { [moduleId] = enabled };
        settings = settings with { ModuleEnabledOverrides = overrides };
        store.Write(settings);
    }

    // ---- Module Launcher --------------------------------------------------------------------------------------

    public LauncherSettings Launcher => settings.Launcher ?? new LauncherSettings();

    /// <summary>The one write path every Launcher control (the launcher window's own lock toggle and free-resize,
    /// and every control on <c>LauncherSettingsPage</c>) funnels through — all changes take effect immediately since
    /// the launcher reads this live every frame, with no <c>/xlreload</c> required.</summary>
    public void SetLauncher(LauncherSettings launcher)
    {
        if (launcher == settings.Launcher) return;
        settings = settings with { Launcher = launcher };
        store.Write(settings);
    }

    /// <summary>Resets only the launcher's on-screen geometry (position, size, density) — never
    /// <see cref="LauncherSettings.ButtonsPerRow"/>, <see cref="LauncherSettings.Locked"/>,
    /// <see cref="LauncherSettings.ModuleOrder"/>,
    /// <see cref="LauncherSettings.ShowOnLauncher"/>, or <see cref="LauncherSettings.Enabled"/>, which are behavior
    /// preferences, not screen placement. The caller supplies the actual target values (the launcher window computes
    /// them from <c>ImGui.GetMainViewport()</c> at click time; the <c>/venueos launcher</c> command's own
    /// offscreen-safe fallback uses <see cref="LauncherSettings"/>'s own record defaults) — this method only owns
    /// which fields are touched, not what "sensible defaults" means for a given viewport.</summary>
    public void ResetLauncherPosition(float positionX, float positionY, float width, float height, float scale) =>
        SetLauncher(Launcher with { PositionX = positionX, PositionY = positionY, Width = width, Height = height, Scale = scale });

    /// <summary>Shown on the launcher unless the operator explicitly hid this module — absence of an entry means
    /// shown, matching <see cref="LauncherSettings.ShowOnLauncher"/>'s default-on/opt-out contract. Enabled state is
    /// not checked here (a caller building the actual button list also filters by <c>IVenueModule.IsEnabled</c> —
    /// see <c>LauncherEntries.Eligible</c>); this only reports the operator's own explicit preference.</summary>
    public bool IsShownOnLauncher(string moduleId) =>
        Launcher.ShowOnLauncher is not { } show || !show.TryGetValue(moduleId, out var shown) || shown;

    public void SetShownOnLauncher(string moduleId, bool shown)
    {
        var launcher = Launcher;
        var show = new Dictionary<string, bool>(launcher.ShowOnLauncher ?? new Dictionary<string, bool>(), StringComparer.Ordinal) { [moduleId] = shown };
        SetLauncher(launcher with { ShowOnLauncher = show });
    }

    public void SetLauncherModuleOrder(IReadOnlyList<string> order) => SetLauncher(Launcher with { ModuleOrder = order });

    // ---- Per-module detached-window preference -----------------------------------------------------------------

    /// <summary>The module's remembered detached-window state/geometry from its last Close/Collapse/Expand/disabled
    /// eviction, or null if it has never been detached (or its preference predates this pass). Consulted only by an
    /// explicit Open — never auto-applied on plugin load, which would violate the hard "no VenueOS window may
    /// auto-open on load" rule (NEW_MODULE_GUIDE.md §7).</summary>
    public ModuleWindowPreference? GetModuleWindowPreference(string moduleId) =>
        settings.ModuleWindowPreferences is { } prefs && prefs.TryGetValue(moduleId, out var pref) ? pref : null;

    public void SetModuleWindowPreference(string moduleId, ModuleWindowPreference preference)
    {
        var prefs = new Dictionary<string, ModuleWindowPreference>(settings.ModuleWindowPreferences ?? new Dictionary<string, ModuleWindowPreference>(), StringComparer.Ordinal) { [moduleId] = preference };
        settings = settings with { ModuleWindowPreferences = prefs };
        store.Write(settings);
    }
}

/// <summary>Where a module launched from Home should render. The single decision point every module's launch —
/// current and future — funnels through, so a newly registered module inherits pop-out support automatically
/// instead of needing a per-module branch anywhere.</summary>
public enum ModuleLaunchTarget { Embedded, Detached }
public static class ModuleLaunchRouting
{
    public static ModuleLaunchTarget Resolve(bool autoPopOutModules) => autoPopOutModules ? ModuleLaunchTarget.Detached : ModuleLaunchTarget.Embedded;
}
