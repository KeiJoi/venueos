using VenueOS.Core;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.BlockLetters;

/// <summary>Block Letters' only persistent configuration — a per-venue default destination (NEW_MODULE_GUIDE.md §9;
/// see BLOCK_LETTERS_IMPLEMENTATION.md for why the composition text itself is deliberately NOT persisted here).</summary>
public sealed record BlockLettersSettings(BlockLettersDestination DefaultDestination)
{
    public static BlockLettersSettings Default() => new(BlockLettersDestination.Chat);
}

/// <summary>State authority: this module's local per-venue config (the default destination) is the only state it
/// owns, full stop — no backend, no in-game state, no shared/other-module state (NEW_MODULE_GUIDE.md §34a). The
/// live composition text is intentionally not modeled here at all; it lives only in the operator panel's own
/// in-memory field and is never written through this service.</summary>
public sealed class BlockLettersService(VenueProfileService profiles)
{
    public const string ModuleId = "tools.blockletters";
    private const int SchemaVersion = 1;
    private Guid venueId;

    public BlockLettersSettings Settings { get; private set; } = BlockLettersSettings.Default();

    public void Load(Guid nextVenueId)
    {
        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, ModuleId, SchemaVersion, BlockLettersSettings.Default);
    }

    public void SetDefaultDestination(BlockLettersDestination destination)
    {
        if (Settings.DefaultDestination == destination) return;
        Settings = Settings with { DefaultDestination = destination };
        profiles.SaveModuleConfig(venueId, ModuleId, SchemaVersion, Settings);
    }
}

/// <summary>The <see cref="IVenueModule"/> wrapper — own file per NEW_MODULE_GUIDE.md §21/§33's convention for a
/// non-legacy module. Splits Draw()/DrawSettings() into distinct delegates from the start (§8): DrawSettings shows
/// only the persisted default destination, Draw shows the live composer.</summary>
public sealed class BlockLettersModule(BlockLettersService service, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        BlockLettersService.ModuleId,
        "Block Letters",
        "Create FFXIV block-letter text within in-game character limits.",
        "block-letters",
        DisplayOrder: 11);

    // Promoted to production (NEW_MODULE_GUIDE.md §22a) after live acceptance testing.
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
