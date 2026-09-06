namespace VenueOS.Core;

/// <summary><paramref name="DisplayOrder"/> is the one authoritative place to change a module's position on Home
/// and in Settings → Modules — both surfaces iterate <see cref="ModuleHost.Modules"/>, which is sorted by this
/// value (lower first) and falls back to <paramref name="Id"/> alphabetically to break ties, so leaving every
/// module at the default <c>0</c> reproduces today's alphabetical-by-Id order exactly. This is deliberately
/// independent of <see cref="ModuleHost"/>'s separate dependency-ordered lifecycle sequence (init/tick/dispose),
/// which must stay dependency-correct and is never reordered for display purposes.</summary>
public sealed record ModuleDescriptor(string Id, string DisplayName, string Description, string Icon, IReadOnlyList<string>? Dependencies = null, bool UnderDevelopment = false, int DisplayOrder = 0)
{
    public IReadOnlyList<string> RequiredModules { get; } = Dependencies ?? [];
}

public sealed record VenueContext(Guid VenueId, string DisplayName, object Theme);

public interface IVenueModule : IAsyncDisposable
{
    ModuleDescriptor Descriptor { get; }
    bool IsEnabled { get; set; }
    Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken);
    Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken);
    void Tick(DateTimeOffset now);
    void Draw();
    void DrawSettings();
}

public sealed class ModuleContext(Action<string, Exception?> reportFailure)
{
    public void ReportFailure(string message, Exception? exception = null) => reportFailure(message, exception);
}

public sealed class ModuleHost
{
    private readonly Dictionary<string, IVenueModule> modules = new(StringComparer.Ordinal);
    public event Action<string, Exception?>? ModuleFailed;
    public IReadOnlyList<IVenueModule> Modules => modules.Values.OrderBy(x => x.Descriptor.DisplayOrder).ThenBy(x => x.Descriptor.Id, StringComparer.Ordinal).ToArray();

    public void Register(IVenueModule module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Descriptor.Id);
        if (!modules.TryAdd(module.Descriptor.Id, module)) throw new InvalidOperationException($"Module '{module.Descriptor.Id}' is already registered.");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var module in Ordered()) await IsolateAsync(module, () => module.InitializeAsync(new ModuleContext(Report), cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Notifies every enabled module of a venue change, in dependency order, isolating each module's
    /// failure exactly like <see cref="InitializeAsync"/>/<see cref="Tick"/>/<see cref="DisposeAsync"/> already do —
    /// one incomplete module (e.g. a Party Finder integration missing a required third-party initialization step)
    /// must never prevent every other module, or the core active Venue Profile itself, from switching. A module
    /// that throws here is recorded via <see cref="ModuleFailed"/> and skipped for this notification only; it is
    /// still notified again on the next venue change and remains otherwise enabled.</summary>
    public async Task NotifyVenueChangedAsync(VenueContext context, CancellationToken cancellationToken = default)
    {
        foreach (var module in Ordered()) await IsolateAsync(module, () => module.OnVenueChangedAsync(context, cancellationToken)).ConfigureAwait(false);
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var module in Ordered().Where(x => x.IsEnabled))
            try { module.Tick(now); } catch (Exception ex) { Report($"Tick failed in {module.Descriptor.Id}.", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var module in Ordered().Reverse())
            try { await module.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Report($"Dispose failed in {module.Descriptor.Id}.", ex); }
    }

    private IEnumerable<IVenueModule> Ordered()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal); var result = new List<IVenueModule>();
        void Visit(IVenueModule module)
        {
            if (!visited.Add(module.Descriptor.Id)) return;
            if (!visiting.Add(module.Descriptor.Id)) throw new InvalidOperationException("Module dependency cycle detected.");
            foreach (var dependency in module.Descriptor.RequiredModules)
                if (!modules.TryGetValue(dependency, out var target)) throw new InvalidOperationException($"Module '{module.Descriptor.Id}' requires '{dependency}'."); else Visit(target);
            visiting.Remove(module.Descriptor.Id); result.Add(module);
        }
        foreach (var module in modules.Values.OrderBy(x => x.Descriptor.Id, StringComparer.Ordinal)) Visit(module);
        return result;
    }

    private async Task IsolateAsync(IVenueModule module, Func<Task> operation) { if (!module.IsEnabled) return; try { await operation().ConfigureAwait(false); } catch (Exception ex) { Report($"Lifecycle failed in {module.Descriptor.Id}.", ex); } }
    private void Report(string message, Exception? exception = null) => ModuleFailed?.Invoke(message, exception);
}
