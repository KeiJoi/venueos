using VenueOS.Core;

namespace VenueOS.Modules.Operations.PartyFinder;

/// <summary>The <see cref="IVenueModule"/> wrapper — own file per NEW_MODULE_GUIDE.md §21/§33's convention for a
/// non-legacy module. Splits Draw()/DrawSettings() into distinct delegates (fixing the documented gap in
/// NEW_MODULE_GUIDE.md §8 where every existing module currently mirrors the two).</summary>
public sealed class PartyFinderModule(PartyFinderService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(PartyFinderService.ModuleId, "Party Finder", "Venue-scoped Party Finder recruitment automation.", "search", DisplayOrder: 5);
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken)
    {
        service.Load(context.VenueId);
        return Task.CompletedTask;
    }

    public void Tick(DateTimeOffset now)
    {
    }

    public void Draw() => draw?.Invoke();

    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();

    /// <summary>Donor-parity note: disabling the module aborts its own in-flight automation only — it must never be
    /// treated as equivalent to withdrawing an existing native listing. End Party Finder is the explicit operation
    /// for that.</summary>
    public ValueTask DisposeAsync()
    {
        service.Abort();
        return ValueTask.CompletedTask;
    }
}
