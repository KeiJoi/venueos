namespace VenueOS.Services;

/// <summary>Application-wide VenueOS preferences — explicitly NOT venue-scoped. Persisted independently of any
/// <c>VenueProfile</c>, so switching the active venue never reads, writes, or resets these values.
/// <see cref="ModuleEnabledOverrides"/> records only a module's explicit operator choice from Settings → Modules'
/// Toggle (keyed by <c>ModuleDescriptor.Id</c>) — a module with no entry here has never been explicitly toggled and
/// falls back to its own code-level default (<c>IVenueModule.IsEnabled</c>'s initial value), so changing that
/// code-level default later (e.g. shipping an unfinished module disabled by default) never overwrites an operator's
/// own prior choice.</summary>
public sealed record GlobalSettings(bool AutoPopOutModules = false, IReadOnlyDictionary<string, bool>? ModuleEnabledOverrides = null);

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
}

/// <summary>Where a module launched from Home should render. The single decision point every module's launch —
/// current and future — funnels through, so a newly registered module inherits pop-out support automatically
/// instead of needing a per-module branch anywhere.</summary>
public enum ModuleLaunchTarget { Embedded, Detached }
public static class ModuleLaunchRouting
{
    public static ModuleLaunchTarget Resolve(bool autoPopOutModules) => autoPopOutModules ? ModuleLaunchTarget.Detached : ModuleLaunchTarget.Embedded;
}
