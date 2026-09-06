using VenueOS.Core;

namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>The <see cref="IVenueModule"/> wrapper — own file/folder per <c>NEW_MODULE_GUIDE.md</c> §21/§33, and its
/// own <c>Draw()</c>/<c>DrawSettings()</c> delegates (the split most existing modules still don't do — see §8's
/// documented gap — but Party Finder and this module both do from the start).
///
/// Keeps the existing <c>communication.announcements</c> module ID and "ShoutRunner" display name — see
/// <see cref="ShoutRunnerService.SchemaVersion"/>'s doc comment for exactly what that does and doesn't carry over
/// from the previous Announcements implementation that lived under this same ID.</summary>
public sealed class ShoutRunnerModule(ShoutRunnerService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    private bool isEnabled = true;

    public ModuleDescriptor Descriptor { get; } = new(ShoutRunnerService.ModuleId, "ShoutRunner", "Data Center shout route automation.", "megaphone");

    /// <summary>A custom setter, not a plain auto-property — disabling the module must behave like a strong Stop
    /// (reconstruction brief "MODULE DISABLE"), and <c>IVenueModule</c>/<c>ModuleHost</c> have no separate
    /// "on-disabled" lifecycle hook to intercept that transition otherwise (disabling only ever excludes a module
    /// from <c>ModuleHost.Tick</c> — see <c>NEW_MODULE_GUIDE.md</c> §22 — which would otherwise leave any in-flight
    /// automation <see cref="Task"/> completely unobserved/undriven rather than actually stopped).</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value) return;
            isEnabled = value;
            if (!value) service.HardStop();
        }
    }

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken)
    {
        service.Load(context.VenueId);
        return Task.CompletedTask;
    }

    public void Tick(DateTimeOffset now) => service.Tick(now);

    public void Draw() => draw?.Invoke();

    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();

    /// <summary>Bounded, non-blocking teardown — see <see cref="ShoutRunnerService.HardStop"/>. Plugin unload must
    /// never hang waiting for ShoutRunner.</summary>
    public ValueTask DisposeAsync()
    {
        service.HardStop();
        return ValueTask.CompletedTask;
    }
}
