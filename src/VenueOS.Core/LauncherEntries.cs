namespace VenueOS.Core;

/// <summary>A plain id + enabled snapshot of one registered module — deliberately not a live <c>IVenueModule</c>/
/// <c>ModuleDescriptor</c> reference, since <c>VenueOS.Core</c> cannot depend on anything above it. The
/// <c>VenueOS.Plugin.Shell</c> rendering layer projects the real <c>ModuleHost.Modules</c> list into this shape
/// every frame before calling <see cref="LauncherEntries"/>.</summary>
public sealed record LauncherModuleInfo(string Id, bool IsEnabled);

/// <summary>Pure ordering/filtering logic for the Module Launcher's button list and its Settings page's module list
/// — ImGui-free and free of any <c>GlobalSettingsService</c>/<c>ModuleHost</c> reference, so it's directly unit
/// testable. Both callers hand in plain snapshots (module ids already in <c>ModuleHost.Modules</c>'s canonical
/// display order, plus the persisted preference dictionaries) rather than live service references.</summary>
public static class LauncherEntries
{
    /// <summary>The complete, stable ordering of every id in <paramref name="allModuleIds"/>: ids explicitly present
    /// in <paramref name="moduleOrder"/> come first, in that order, followed by any remaining id from
    /// <paramref name="allModuleIds"/> (already in <c>ModuleHost.Modules</c> display order) that
    /// <paramref name="moduleOrder"/> doesn't mention yet — including a brand-new module with no recorded
    /// preference, which lands at its natural display-order position rather than at the end. An id in
    /// <paramref name="moduleOrder"/> that no longer exists in <paramref name="allModuleIds"/> (a stale/removed
    /// module id) is silently skipped, never surfaced — the "never crash, never corrupt the rest of the config"
    /// contract for unknown ids, applied to ordering.</summary>
    public static IReadOnlyList<string> FullOrder(IReadOnlyList<string> allModuleIds, IReadOnlyList<string>? moduleOrder)
    {
        var known = new HashSet<string>(allModuleIds, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        if (moduleOrder is not null)
            foreach (var id in moduleOrder)
                if (known.Contains(id) && placed.Add(id)) result.Add(id);

        foreach (var id in allModuleIds)
            if (placed.Add(id)) result.Add(id);

        return result;
    }

    /// <summary>The launcher's actual visible button list: <see cref="FullOrder"/>, filtered down to enabled
    /// modules whose explicit <paramref name="showOnLauncher"/> preference isn't <c>false</c> — absence of an entry
    /// means shown. This is the launcher's revised, default-on/opt-out first-run behavior: a fresh configuration
    /// shows every eligible module immediately, and only an explicit operator choice ever removes one. A disabled
    /// module is never included regardless of its <c>ShowOnLauncher</c> value, matching the precedent that
    /// <c>HomeScreen.DrawGrid</c> already fully omits disabled modules' tiles.</summary>
    public static IReadOnlyList<string> Eligible(IReadOnlyList<LauncherModuleInfo> modules, IReadOnlyDictionary<string, bool>? showOnLauncher, IReadOnlyList<string>? moduleOrder)
    {
        var enabledIds = new HashSet<string>(modules.Where(m => m.IsEnabled).Select(m => m.Id), StringComparer.Ordinal);
        var allIds = modules.Select(m => m.Id).ToArray();

        return FullOrder(allIds, moduleOrder)
            .Where(id => enabledIds.Contains(id))
            .Where(id => showOnLauncher is null || !showOnLauncher.TryGetValue(id, out var shown) || shown)
            .ToArray();
    }
}
